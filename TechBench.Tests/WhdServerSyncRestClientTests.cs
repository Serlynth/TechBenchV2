using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TechBench.Models;
using TechBench.Providers;

namespace TechBench.Tests;

public sealed class WhdServerSyncRestClientTests
{
    [Fact]
    public async Task DeltaSyncUsesUtcQualifierOnEveryPageAndParsesServerOwnedFields()
    {
        var firstPage = JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(id => new
        {
            id,
            subject = $"Ticket {id}",
            statustype = new { id = 1, statusTypeName = "Open" },
            clientReporter = new { id, displayName = $"Client {id}" }
        }));
        const string secondPage = """
            [
              {
                "id": 101,
                "subject": "Server-side assignment test",
                "deleted": 0,
                "lastUpdatedUtc": "2026-07-17T11:22:33.444Z",
                "statustype": { "id": 4, "statusTypeName": "In Progress" },
                "assignedTech": { "id": 72, "displayName": "Ada Admin" },
                "requestType": {
                  "techGroup": { "id": 9, "techGroupName": "Infrastructure" }
                },
                "clientReporter": { "id": 300, "displayName": "Sam User" },
                "location": { "id": 44, "locationName": "Main Office" }
              }
            ]
            """;

        var handler = new RecordingHandler(request =>
            Json(HttpStatusCode.OK, ReadQueryParameter(request.RequestUri, "page") == "1"
                ? firstPage
                : secondPage));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);
        var changedSince = new DateTimeOffset(2026, 7, 17, 12, 34, 56, 789, TimeSpan.FromHours(2));

        var result = await client.GetOrganizationTicketsChangedSinceAsync(
            ExplicitSettings(),
            changedSince);

        Assert.True(result.Success, result.Message);
        Assert.True(result.IsComplete);
        Assert.Equal(101, result.Tickets.Count);
        Assert.Equal(2, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            Assert.Equal("true", ReadQueryParameter(request.Uri, "withUTC"));
            Assert.Equal("long", ReadQueryParameter(request.Uri, "style"));
            Assert.Equal(
                "(((deleted = null) or (deleted = 0) or (deleted = 1)) and "
                + "(lastUpdated >= '2026-07-17T10:34:56.789Z'))",
                ReadQueryParameter(request.Uri, "qualifier"));
        }

        var parsed = Assert.Single(result.Tickets, ticket => ticket.ExternalId == "WHD-101");
        Assert.Equal(4, parsed.StatusTypeId);
        Assert.False(parsed.IsClosed);
        Assert.False(parsed.IsDeleted);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-17T11:22:33.444Z", CultureInfo.InvariantCulture),
            parsed.LastUpdatedUtc);
        Assert.Equal("WHD-TECH-72", parsed.AssignedTechnicianExternalId);
        Assert.Equal("Ada Admin", parsed.AssignedTechnicianName);
        Assert.Equal("WHD-GROUP-9", parsed.AssignedGroupExternalId);
        Assert.Equal("Infrastructure", parsed.AssignedGroupName);
        Assert.Equal("WHD-LOCATION-44", parsed.Client.ExternalId);
        Assert.Equal("Main Office", parsed.Client.Name);
        Assert.Equal("Main Office", parsed.Client.LocationName);
        Assert.Equal("Sam User", parsed.Client.ContactName);
    }

    [Fact]
    public async Task OrganizationPagingContinuesPastOneHundredPages()
    {
        var handler = new RecordingHandler(request =>
        {
            var page = int.Parse(
                ReadQueryParameter(request.RequestUri, "page") ?? "0",
                CultureInfo.InvariantCulture);
            if (page > 101)
            {
                return Json(HttpStatusCode.OK, "[]");
            }

            var pageJson = JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(offset => new
            {
                id = ((page - 1) * 100) + offset,
                subject = $"Ticket {page}-{offset}",
                statustype = new { id = 1, statusTypeName = "Open" },
                clientReporter = new { id = offset, displayName = $"Client {offset}" }
            }));
            return Json(HttpStatusCode.OK, pageJson);
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetOrganizationTicketsAsync(ExplicitSettings());

        Assert.True(result.Success, result.Message);
        Assert.True(result.IsComplete);
        Assert.Equal(10_100, result.Tickets.Count);
        Assert.Equal(102, handler.Requests.Count);
        Assert.Equal("101", ReadQueryParameter(handler.Requests[100].Uri, "page"));
        Assert.Equal("102", ReadQueryParameter(handler.Requests[101].Uri, "page"));
    }

    [Fact]
    public async Task TechnicianSyncParsesMappingIdentityAndInactiveState()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """
            [
              {
                "id": 7,
                "displayName": "Ada Admin",
                "username": "aadmin",
                "email": "ada@example.test",
                "isInactive": false
              },
              {
                "techId": 8,
                "firstName": "Grace",
                "lastName": "Hopper",
                "loginName": "ghopper",
                "emailAddress": "grace@example.test",
                "disabled": 1
              }
            ]
            """));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetTechniciansAsync(ExplicitSettings("aadmin"));

        Assert.True(result.Success, result.Message);
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Technicians.Count);
        var active = Assert.Single(result.Technicians, technician => technician.ExternalId == "WHD-TECH-7");
        Assert.Equal("Ada Admin", active.DisplayName);
        Assert.Equal("aadmin", active.Username);
        Assert.Equal("ada@example.test", active.Email);
        Assert.True(active.IsActive);
        var inactive = Assert.Single(result.Technicians, technician => technician.ExternalId == "WHD-TECH-8");
        Assert.Equal("Grace Hopper", inactive.DisplayName);
        Assert.Equal("ghopper", inactive.Username);
        Assert.Equal("grace@example.test", inactive.Email);
        Assert.False(inactive.IsActive);
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/Techs", request.Uri?.AbsolutePath);
        Assert.Equal("long", ReadQueryParameter(request.Uri, "style"));
        Assert.Equal("100", ReadQueryParameter(request.Uri, "limit"));
        Assert.Equal("1", ReadQueryParameter(request.Uri, "page"));
    }

    [Fact]
    public async Task TechnicianSyncReplacesInternalListNameWithDetailedTechnicianName()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/Techs/6", StringComparison.Ordinal) == true)
            {
                return Json(HttpStatusCode.OK, """
                    {
                      "id": 6,
                      "name": "WHD-TECH-6",
                      "firstName": "Craig",
                      "lastName": "Goemans",
                      "username": "cgoemans",
                      "email": "craig@example.test",
                      "activeAccount": true
                    }
                    """);
            }

            return Json(HttpStatusCode.OK, """
                [
                  {
                    "id": 6,
                    "name": "WHD-TECH-6",
                    "activeAccount": true
                  }
                ]
                """);
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetTechniciansAsync(ExplicitSettings("cgoemans"));

        Assert.True(result.Success, result.Message);
        Assert.True(result.IsComplete);
        var technician = Assert.Single(result.Technicians);
        Assert.Equal("WHD-TECH-6", technician.ExternalId);
        Assert.Equal("Craig Goemans", technician.DisplayName);
        Assert.Equal("cgoemans", technician.Username);
        Assert.Equal("craig@example.test", technician.Email);
        Assert.Collection(
            handler.Requests,
            request => Assert.EndsWith("/Techs", request.Uri?.AbsolutePath),
            request => Assert.EndsWith("/Techs/6", request.Uri?.AbsolutePath));
    }

    [Fact]
    public async Task TechnicianSyncUsesTemporarySessionToIncludeAdministratorOmittedByTechList()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Delete
                && request.RequestUri?.AbsolutePath.EndsWith("/Session", StringComparison.Ordinal) == true)
            {
                return Json(HttpStatusCode.OK, "{}");
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/Session", StringComparison.Ordinal) == true)
            {
                return Json(HttpStatusCode.OK, """
                    {
                      "type": "Session",
                      "sessionKey": "temporary-session",
                      "currentTechId": 99,
                      "instanceId": -1
                    }
                    """);
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/Techs/99", StringComparison.Ordinal) == true)
            {
                return Json(HttpStatusCode.OK, """
                    {
                      "id": 99,
                      "firstName": "Helpdesk",
                      "lastName": "Manager",
                      "username": "WHDmgr",
                      "email": "whdmgr@example.test",
                      "activeAccount": true
                    }
                    """);
            }

            return Json(HttpStatusCode.OK, """
                [
                  {
                    "id": 7,
                    "displayName": "Ada Admin",
                    "username": "aadmin",
                    "isInactive": false
                  }
                ]
                """);
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetTechniciansAsync(ExplicitSettings("WHDmgr"));

        Assert.True(result.Success, result.Message);
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Technicians.Count);
        var manager = Assert.Single(
            result.Technicians,
            technician => technician.ExternalId == "WHD-TECH-99");
        Assert.Equal("Helpdesk Manager", manager.DisplayName);
        Assert.Equal("WHDmgr", manager.Username);
        Assert.True(manager.IsActive);
        Assert.Collection(
            handler.Requests,
            listRequest => Assert.EndsWith("/Techs", listRequest.Uri?.AbsolutePath),
            sessionRequest =>
            {
                Assert.EndsWith("/Session", sessionRequest.Uri?.AbsolutePath);
                Assert.Equal("WHDmgr", ReadQueryParameter(sessionRequest.Uri, "username"));
                Assert.Equal("test-secret", ReadQueryParameter(sessionRequest.Uri, "apiKey"));
            },
            technicianRequest =>
            {
                Assert.EndsWith("/Techs/99", technicianRequest.Uri?.AbsolutePath);
                Assert.Equal("temporary-session", ReadQueryParameter(technicianRequest.Uri, "sessionKey"));
            },
            deleteRequest =>
            {
                Assert.Equal(HttpMethod.Delete, deleteRequest.Method);
                Assert.EndsWith("/Session", deleteRequest.Uri?.AbsolutePath);
                Assert.Equal("temporary-session", ReadQueryParameter(deleteRequest.Uri, "sessionKey"));
            });
    }

    [Fact]
    public async Task TechnicianSyncRetainsSessionTechnicianIdWhenDetailRoutesAreUnavailable()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                return Json(HttpStatusCode.OK, "{}");
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/Session", StringComparison.Ordinal) == true)
            {
                return Json(HttpStatusCode.OK, """
                    {
                      "type": "Session",
                      "sessionKey": "temporary-session",
                      "currentTechId": 99,
                      "instanceId": -1
                    }
                    """);
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/Techs/99", StringComparison.Ordinal) == true
                || request.RequestUri?.AbsolutePath.EndsWith("/Techs/currentTech", StringComparison.Ordinal) == true)
            {
                return Json(HttpStatusCode.Forbidden, """{"message":"Not permitted"}""");
            }

            return Json(HttpStatusCode.OK, """
                [
                  {
                    "id": 7,
                    "displayName": "Ada Admin",
                    "username": "aadmin",
                    "isInactive": false
                  }
                ]
                """);
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetTechniciansAsync(ExplicitSettings("WHDmgr"));

        Assert.True(result.Success, result.Message);
        var manager = Assert.Single(
            result.Technicians,
            technician => technician.ExternalId == "WHD-TECH-99");
        Assert.Equal("WHDmgr", manager.DisplayName);
        Assert.Equal("WHDmgr", manager.Username);
        Assert.True(manager.IsActive);
        Assert.Contains(
            handler.Requests,
            request => request.Uri?.AbsolutePath.EndsWith("/Techs/currentTech", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task TechnicianSyncReadsNestedCurrentTechnicianFromSession()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                return Json(HttpStatusCode.OK, "{}");
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/Session", StringComparison.Ordinal) == true)
            {
                return Json(HttpStatusCode.OK, """
                    {
                      "session": {
                        "sessionKey": "nested-session",
                        "currentTechnician": {
                          "technicianId": 99,
                          "firstName": "Helpdesk",
                          "lastName": "Manager",
                          "userName": "WHDmgr",
                          "emailAddress": "whdmgr@example.test",
                          "activeAccount": true
                        }
                      }
                    }
                    """);
            }

            return Json(HttpStatusCode.OK, """
                [
                  {
                    "id": 7,
                    "displayName": "Ada Admin",
                    "username": "aadmin",
                    "isInactive": false
                  }
                ]
                """);
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetTechniciansAsync(ExplicitSettings("WHDmgr"));

        Assert.True(result.Success, result.Message);
        var manager = Assert.Single(
            result.Technicians,
            technician => technician.ExternalId == "WHD-TECH-99");
        Assert.Equal("Helpdesk Manager", manager.DisplayName);
        Assert.Equal("WHDmgr", manager.Username);
        Assert.Equal("whdmgr@example.test", manager.Email);
        Assert.True(manager.IsActive);
        Assert.DoesNotContain(
            handler.Requests,
            request => request.Uri?.AbsolutePath.Contains("/Techs/currentTech", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task TechnicianSyncUsesDirectCurrentTechnicianWhenApplicationSessionIsUnavailable()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/Session", StringComparison.Ordinal) == true)
            {
                return Json(HttpStatusCode.Unauthorized, """{"message":"Session requires a technician key"}""");
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/Techs/currentTech", StringComparison.Ordinal) == true)
            {
                return Json(HttpStatusCode.OK, """
                    {
                      "id": 99,
                      "firstName": "Helpdesk",
                      "lastName": "Manager",
                      "username": "WHDmgr",
                      "activeAccount": true
                    }
                    """);
            }

            return Json(HttpStatusCode.OK, """
                [
                  {
                    "id": 7,
                    "displayName": "Ada Admin",
                    "username": "aadmin",
                    "isInactive": false
                  }
                ]
                """);
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetTechniciansAsync(ExplicitSettings("WHDmgr"));

        Assert.True(result.Success, result.Message);
        var manager = Assert.Single(
            result.Technicians,
            technician => technician.ExternalId == "WHD-TECH-99");
        Assert.Equal("Helpdesk Manager", manager.DisplayName);
        Assert.Equal("WHDmgr", manager.Username);
        Assert.Contains(
            handler.Requests,
            request => request.Uri?.AbsolutePath.EndsWith("/Techs/currentTech", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task TechnicianSyncUsesSessionInstanceForCurrentAdministratorLookup()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                return Json(HttpStatusCode.OK, "{}");
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/Session", StringComparison.Ordinal) == true)
            {
                return Json(HttpStatusCode.OK, """
                    {
                      "type": "Session",
                      "sessionKey": "instance-session",
                      "instanceId": 4
                    }
                    """);
            }

            if (request.RequestUri?.AbsolutePath.EndsWith(
                    "/Helpdesk.woa/4/ra/Techs/currentTech",
                    StringComparison.Ordinal) == true)
            {
                return Json(HttpStatusCode.OK, """
                    {
                      "id": 99,
                      "firstName": "Helpdesk",
                      "lastName": "Manager",
                      "username": "WHDMgr",
                      "activeAccount": true
                    }
                    """);
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/Techs", StringComparison.Ordinal) == true)
            {
                return Json(HttpStatusCode.OK, """
                    [
                      {
                        "id": 7,
                        "displayName": "Ada Admin",
                        "username": "aadmin",
                        "isInactive": false
                      }
                    ]
                    """);
            }

            return Json(HttpStatusCode.NotFound, "{}");
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetTechniciansAsync(ExplicitSettings("WHDMgr"));

        Assert.True(result.Success, result.Message);
        var manager = Assert.Single(
            result.Technicians,
            technician => technician.ExternalId == "WHD-TECH-99");
        Assert.Equal("Helpdesk Manager", manager.DisplayName);
        Assert.Equal("WHDMgr", manager.Username);
        Assert.Contains(
            handler.Requests,
            request => request.Uri?.AbsolutePath.EndsWith(
                "/Helpdesk.woa/4/ra/Techs/currentTech",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task TechnicianSyncRepresentsConfiguredOrganizationAccountWhenWhdOmitsItsIdentity()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                return Json(HttpStatusCode.OK, "{}");
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/Techs", StringComparison.Ordinal) == true)
            {
                return Json(HttpStatusCode.OK, """
                    [
                      {
                        "id": 7,
                        "displayName": "Ada Admin",
                        "username": "aadmin",
                        "isInactive": false
                      }
                    ]
                    """);
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/Session", StringComparison.Ordinal) == true)
            {
                return Json(HttpStatusCode.OK, """
                    {
                      "type": "Session",
                      "sessionKey": "application-session",
                      "instanceId": -1
                    }
                    """);
            }

            return Json(HttpStatusCode.NotFound, "{}");
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetTechniciansAsync(ExplicitSettings("WHDMgr"));

        Assert.True(result.Success, result.Message);
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Technicians.Count);
        var manager = Assert.Single(
            result.Technicians,
            technician => technician.ExternalId == "WHD-CONFIGURED-ORGANIZATION-ACCOUNT");
        Assert.Equal("Helpdesk Manager (WHDMgr, organization-wide account)", manager.DisplayName);
        Assert.Equal("WHDMgr", manager.Username);
        Assert.True(manager.IsActive);
    }

    [Fact]
    public async Task OrganizationTicketSyncMapsHiddenHelpdeskManagerAssignmentsToConfiguredAccount()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """
            [
              {
                "id": 31698,
                "subject": "Manager ticket",
                "statustype": { "id": 2, "statusTypeName": "In Progress" },
                "assignedTech": {
                  "id": 99,
                  "displayName": "H. Manager"
                },
                "clientReporter": {
                  "id": 15,
                  "displayName": "Test Client"
                }
              }
            ]
            """));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetOrganizationTicketsAsync(ExplicitSettings("whdmgr"));

        Assert.True(result.Success, result.Message);
        var ticket = Assert.Single(result.Tickets);
        Assert.Equal("H. Manager", ticket.AssignedTechnicianName);
        Assert.Equal(
            "WHD-CONFIGURED-ORGANIZATION-ACCOUNT",
            ticket.AssignedTechnicianExternalId);
    }

    [Fact]
    public async Task OrganizationTicketSyncMapsConfiguredUsernameEvenWhenDisplayNameDiffers()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """
            [
              {
                "id": 31701,
                "subject": "Manager ticket",
                "statustype": { "id": 2, "statusTypeName": "Assigned" },
                "assignedTech": {
                  "id": 99,
                  "displayName": "Built-in Administrator",
                  "username": "WHDMgr"
                },
                "clientReporter": {
                  "id": 16,
                  "displayName": "Second Test Client"
                }
              }
            ]
            """));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetOrganizationTicketsAsync(ExplicitSettings("whdmgr"));

        Assert.True(result.Success, result.Message);
        var ticket = Assert.Single(result.Tickets);
        Assert.Equal(
            "WHD-CONFIGURED-ORGANIZATION-ACCOUNT",
            ticket.AssignedTechnicianExternalId);
    }

    [Fact]
    public async Task TechnicianGroupSyncFallsBackToTechnicianMembershipData()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/TechGroups", StringComparison.Ordinal) == true)
            {
                return Json(HttpStatusCode.NotFound, "not supported");
            }

            return Json(HttpStatusCode.OK, """
                [
                  {
                    "id": 7,
                    "displayName": "First Tech",
                    "techGroups": [
                      { "id": 20, "techGroupName": "Desktop" }
                    ]
                  },
                  {
                    "id": 8,
                    "displayName": "Second Tech",
                    "techGroupLevels": [
                      { "techGroup": { "id": 20, "techGroupName": "Desktop" } },
                      { "techGroup": { "id": 21, "techGroupName": "Servers" } }
                    ]
                  }
                ]
                """);
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetTechnicianGroupsAsync(ExplicitSettings());

        Assert.True(result.Success, result.Message);
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Groups.Count);
        var desktop = Assert.Single(result.Groups, group => group.ExternalId == "WHD-GROUP-20");
        Assert.Equal(new[] { "WHD-TECH-7", "WHD-TECH-8" }, desktop.TechnicianExternalIds);
        var servers = Assert.Single(result.Groups, group => group.ExternalId == "WHD-GROUP-21");
        Assert.Equal(new[] { "WHD-TECH-8" }, servers.TechnicianExternalIds);
        Assert.Collection(
            handler.Requests,
            request => Assert.EndsWith("/TechGroups", request.Uri?.AbsolutePath),
            request => Assert.EndsWith("/Techs", request.Uri?.AbsolutePath));
    }

    private static WhdConnectionSettings ExplicitSettings(string username = "service-technician") => new()
    {
        BaseUrl = "https://whd.example.test",
        Username = username,
        Secret = "test-secret",
        AuthenticationMode = WhdAuthenticationMode.ApplicationApiKey
    };

    private static string? ReadQueryParameter(Uri? uri, string name)
    {
        if (uri is null)
        {
            return null;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return parts.Length == 2
                    ? Uri.UnescapeDataString(parts[1].Replace('+', ' '))
                    : string.Empty;
            }
        }

        return null;
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string content) => new(statusCode)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri));
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? Uri);
}
