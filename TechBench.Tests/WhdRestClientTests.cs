using System.Net;
using System.Text;
using System.Text.Json;
using TechBench.Models;
using TechBench.Providers;

namespace TechBench.Tests;

public sealed class WhdRestClientTests
{
    [Fact]
    public void DefaultClientAllowsSlowWhdResponses()
    {
        Assert.Equal(TimeSpan.FromSeconds(90), WhdRestClient.DefaultRequestTimeout);
    }

    [Fact]
    public async Task PersonalConnectionTestUsesOnlyTheAuthenticationSessionResource()
    {
        var handler = new RecordingHandler(request =>
            request.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, "{\"sessionKey\":\"temporary-key\",\"currentTechId\":7,\"instanceId\":-1}")
                : Json(HttpStatusCode.OK, "{}"));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.TestConnectionAsync(new WhdConnectionSettings
        {
            BaseUrl = "https://whd.example.test",
            Username = "technician",
            Secret = "secret"
        });

        Assert.True(result.Success, result.Message);
        Assert.Empty(result.Tickets);
        Assert.Contains("No tickets were downloaded or synchronized", result.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestCount);
        Assert.All(handler.Requests, request => Assert.EndsWith("/Session", request.Uri?.AbsolutePath, StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request =>
            request.Uri?.AbsolutePath.Contains("/Tickets", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task AutoAuthenticationBeforePostingNeverReadsTickets()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                return Json(HttpStatusCode.OK, "{}");
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/Session", StringComparison.Ordinal) == true)
            {
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                return query.Contains("password=", StringComparison.Ordinal)
                    ? Json(HttpStatusCode.Unauthorized, "Authentication required.")
                    : Json(HttpStatusCode.OK, "{\"sessionKey\":\"temporary-key\",\"currentTechId\":7,\"instanceId\":-1}");
            }

            return Json(HttpStatusCode.OK, "{\"id\":987}");
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.PostTicketNoteAsync(
            new WhdConnectionSettings
            {
                BaseUrl = "https://whd.example.test",
                Username = "technician",
                Secret = "application-key"
            },
            101,
            "Investigated the issue.",
            15,
            new DateTime(2026, 7, 20, 13, 30, 0, DateTimeKind.Utc));

        Assert.True(result.Success, result.Message);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.DoesNotContain(handler.Requests, request =>
            request.Uri?.AbsolutePath.Contains("/Tickets", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal(2, handler.Requests.Count(request => request.Method == HttpMethod.Get));

        var postedRequest = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        using var payload = JsonDocument.Parse(postedRequest.Body);
        Assert.Equal("2026-07-20T13:30:00Z", payload.RootElement.GetProperty("date").GetString());
    }

    [Fact]
    public async Task OrganizationSyncRetainsExplicitlyClosedOrDeletedTicketsReturnedByWhd()
    {
        const string responseJson = """
            [
              {
                "id": 101,
                "subject": "Open ticket",
                "statustype": { "id": 1, "statusTypeName": "Open" },
                "clientReporter": { "id": 10, "displayName": "Open Client" }
              },
              {
                "id": 102,
                "subject": "Closed ticket",
                "statustype": { "id": 2, "statusTypeName": "Closed" },
                "clientReporter": { "id": 11, "displayName": "Closed Client" }
              },
              {
                "id": 103,
                "subject": "Deleted ticket",
                "deleted": 1,
                "statustype": { "id": 1, "statusTypeName": "Open" },
                "clientReporter": { "id": 12, "displayName": "Deleted Client" }
              }
            ]
            """;

        using var httpClient = new HttpClient(new JsonResponseHandler(responseJson));
        var client = new WhdRestClient(httpClient);

        var result = await client.GetOrganizationTicketsAsync(new WhdConnectionSettings
        {
            BaseUrl = "https://whd.example.test",
            Username = "technician",
            Secret = "secret",
            AuthenticationMode = WhdAuthenticationMode.ApplicationApiKey
        });

        Assert.True(result.Success);
        Assert.Equal(3, result.Tickets.Count);
        Assert.Contains(result.Tickets, ticket => ticket.ExternalId == "WHD-101" && !ticket.IsClosed);
        Assert.Contains(result.Tickets, ticket => ticket.ExternalId == "WHD-102" && ticket.IsClosed);
        Assert.Contains(result.Tickets, ticket => ticket.ExternalId == "WHD-103" && ticket.IsClosed);
        Assert.Contains("organization", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assigned", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OrganizationSyncUsesTicketsResourceAndAllTicketQualifierOnEveryPage()
    {
        var firstPage = JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(id => new
        {
            id,
            subject = $"Ticket {id}",
            statustype = new { id = 1, statusTypeName = "Open" },
            clientReporter = new { id, displayName = $"Client {id}" }
        }));
        var handler = new RecordingHandler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri?.Query ?? string.Empty);
            return Json(HttpStatusCode.OK, query.Contains("page=1", StringComparison.Ordinal)
                ? firstPage
                : "[]");
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetOrganizationTicketsAsync(ExplicitSettings());

        Assert.True(result.Success, result.Message);
        Assert.True(result.IsComplete);
        Assert.Equal(100, result.Tickets.Count);
        Assert.Equal(2, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            Assert.EndsWith("/Tickets", request.Uri?.AbsolutePath, StringComparison.Ordinal);
            Assert.DoesNotContain("/Tickets/mine", request.Uri?.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "qualifier=((deleted = null) or (deleted = 0) or (deleted = 1))",
                Uri.UnescapeDataString(request.Uri?.Query ?? string.Empty),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RejectsHttpBeforeSendingCredentials()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "[]"));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetOrganizationTicketsAsync(new WhdConnectionSettings
        {
            BaseUrl = "http://whd.example.test",
            Username = "technician",
            Secret = "secret"
        });

        Assert.False(result.Success);
        Assert.Contains("HTTPS", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ReadsBackTechNoteIdWhenPostResponseHasNoId()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json(HttpStatusCode.OK, "{}");
            }

            return Json(HttpStatusCode.OK, """
                [{"id":987,"noteText":"Investigated the issue.","workTime":"15"}]
                """);
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.PostTicketNoteAsync(
            ExplicitSettings(),
            101,
            "Investigated the issue.",
            15,
            new DateTime(2026, 7, 20, 13, 30, 0, DateTimeKind.Utc));

        Assert.True(result.Success);
        Assert.True(result.MarkPosted);
        Assert.Equal("WHD-TECHNOTE-987", result.ExternalReference);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task VerifiesExactNoteAfterPostResponseTimesOut()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                throw new TaskCanceledException("Simulated WHD response timeout.");
            }

            return Json(HttpStatusCode.OK, """
                [{"id":988,"noteText":"Investigated the timeout.","workTime":"15"}]
                """);
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.PostTicketNoteAsync(
            ExplicitSettings(),
            101,
            "Investigated the timeout.",
            15,
            new DateTime(2026, 7, 20, 13, 30, 0, DateTimeKind.Utc));

        Assert.True(result.Success, result.Message);
        Assert.True(result.MarkPosted);
        Assert.False(result.OutcomeUncertain);
        Assert.Equal("WHD-TECHNOTE-988", result.ExternalReference);
        Assert.Contains("did not return", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task KeepsPostUncertainWhenTimeoutReadbackCannotFindExactNote()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                throw new TaskCanceledException("Simulated WHD response timeout.");
            }

            return Json(HttpStatusCode.OK, "[]");
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.PostTicketNoteAsync(
            ExplicitSettings(),
            101,
            "Investigated the timeout.",
            15,
            new DateTime(2026, 7, 20, 13, 30, 0, DateTimeKind.Utc));

        Assert.False(result.Success);
        Assert.False(result.MarkPosted);
        Assert.True(result.OutcomeUncertain);
        Assert.Null(result.ExternalReference);
        Assert.Contains("could not find the exact note", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, handler.RequestCount);
    }

    [Fact]
    public async Task DoesNotMarkPostedWhenSuccessfulResponseCannotBeVerified()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Post
            ? Json(HttpStatusCode.OK, "{}")
            : Json(HttpStatusCode.OK, "[]"));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.PostTicketNoteAsync(
            ExplicitSettings(),
            101,
            "Investigated the issue.",
            15,
            new DateTime(2026, 7, 20, 13, 30, 0, DateTimeKind.Utc));

        Assert.False(result.Success);
        Assert.False(result.MarkPosted);
        Assert.True(result.OutcomeUncertain);
        Assert.Null(result.ExternalReference);
    }

    [Fact]
    public async Task GetsTheExactTrackedTechNoteFromTheTicket()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """
            [
              {"id":986,"noteText":"Older note.","workTime":"10"},
              {"id":987,"noteText":"Current note.","workTime":"15"}
            ]
            """));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetTechNoteAsync(ExplicitSettings(), 101, 987);

        Assert.True(result.Success, result.Message);
        Assert.Equal(987, result.TechNoteId);
        Assert.Equal("Current note.", result.NoteText);
        Assert.Equal(15, result.DurationMinutes);
        Assert.Contains("/TicketNotes", handler.LastRequestUri?.AbsolutePath);
        Assert.Contains("jobTicketId=101", handler.LastRequestUri?.Query);
    }

    [Fact]
    public async Task UpdatesOnlyTheExactTechNoteTextAndVerifiesIt()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Put
            ? Json(HttpStatusCode.OK, "{}")
            : Json(HttpStatusCode.OK, """
                [{"id":987,"noteText":"Updated work note.","workTime":"15"}]
                """));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.UpdateTechNoteAsync(
            ExplicitSettings(),
            101,
            987,
            "Updated work note.",
            new DateTime(2026, 7, 20, 13, 30, 0, DateTimeKind.Utc));

        Assert.True(result.Success, result.Message);
        Assert.Equal("WHD-TECHNOTE-987", result.ExternalReference);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.EndsWith("/TechNotes/987", handler.Requests[0].Uri?.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);

        using var payload = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("Updated work note.", payload.RootElement.GetProperty("noteText").GetString());
        Assert.Equal("2026-07-20T13:30:00Z", payload.RootElement.GetProperty("date").GetString());
        Assert.False(payload.RootElement.TryGetProperty("jobticket", out _));
        Assert.False(payload.RootElement.TryGetProperty("workTime", out _));
    }

    [Fact]
    public async Task TechNoteUpdateFailureNeverFallsBackToPostingANewNote()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.MethodNotAllowed, "PUT is disabled"));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.UpdateTechNoteAsync(
            ExplicitSettings(),
            101,
            987,
            "Updated work note.");

        Assert.False(result.Success);
        Assert.False(result.OutcomeUncertain);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task StopsWhenWhdRepeatsAFullPage()
    {
        var response = JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(id => new
        {
            id,
            subject = $"Ticket {id}",
            statustype = new { id = 1, statusTypeName = "Open" },
            clientReporter = new { id, displayName = $"Client {id}" }
        }));
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, response));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetOrganizationTicketsAsync(ExplicitSettings());

        Assert.True(result.Success);
        Assert.False(result.IsComplete);
        Assert.Equal(100, result.Tickets.Count);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task SyncsWhdLocationsAsCustomerCompanies()
    {
        const string response = """
            [
              {"id": 12, "locationName": "Friends Central School", "isInactive": false},
              {"id": 13, "locationName": "Old Location", "isInactive": true}
            ]
            """;
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, response));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetClientsAsync(ExplicitSettings());

        Assert.True(result.Success, result.Message);
        var location = Assert.Single(result.Clients);
        Assert.Equal("WHD-LOCATION-12", location.ExternalId);
        Assert.Equal("Friends Central School", location.Name);
        Assert.Equal("Friends Central School", location.LocationName);
        Assert.Null(location.ContactName);
        Assert.Contains("/Locations", handler.Requests[0].Uri?.AbsolutePath);
    }

    [Fact]
    public async Task AutoAuthenticationUsesPermissionLightProbeOnlyOncePerConnection()
    {
        const string response = "[{\"id\":1,\"subject\":\"One\",\"clientReporter\":{\"id\":1,\"displayName\":\"Client\"}}]";
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/Session", StringComparison.Ordinal) == true)
            {
                return request.Method == HttpMethod.Get
                    ? Json(HttpStatusCode.OK, "{\"sessionKey\":\"temporary-key\",\"currentTechId\":7,\"instanceId\":-1}")
                    : Json(HttpStatusCode.OK, "{}");
            }

            return Json(HttpStatusCode.OK, response);
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);
        var settings = new WhdConnectionSettings
        {
            BaseUrl = "https://whd.example.test",
            Username = "technician",
            Secret = "secret"
        };

        Assert.True((await client.GetOrganizationTicketsAsync(settings)).Success);
        Assert.True((await client.GetOrganizationTicketsAsync(settings)).Success);
        Assert.Equal(4, handler.RequestCount);
        Assert.EndsWith("/Session", handler.Requests[0].Uri?.AbsolutePath, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "qualifier=",
            Uri.UnescapeDataString(handler.Requests[0].Uri?.Query ?? string.Empty),
            StringComparison.OrdinalIgnoreCase);
        Assert.All(
            handler.Requests.Skip(2),
            request =>
            {
                Assert.EndsWith("/Tickets", request.Uri?.AbsolutePath, StringComparison.Ordinal);
                Assert.Contains(
                    "qualifier=((deleted = null) or (deleted = 0) or (deleted = 1))",
                    Uri.UnescapeDataString(request.Uri?.Query ?? string.Empty),
                    StringComparison.Ordinal);
            });
        Assert.DoesNotContain(handler.Requests, request =>
            request.Uri?.AbsolutePath.EndsWith("/Tickets/mine", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task GetsAnAuthorizedTicketByNumber()
    {
        const string response = """
            {
              "id": 456,
              "subject": "Former employee ticket",
              "statustype": { "id": 3, "statusTypeName": "Closed" },
              "clientReporter": { "id": 11, "displayName": "Contoso" }
            }
            """;
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, response));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetTicketAsync(ExplicitSettings(), 456);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.Ticket);
        Assert.Equal("WHD-456", result.Ticket.ExternalId);
        Assert.Equal("Former employee ticket", result.Ticket.Subject);
        Assert.Equal("Closed", result.Ticket.Status);
        Assert.True(result.Ticket.IsClosed);
        Assert.Equal("Contoso", result.Ticket.Client.Name);
        Assert.Contains("/Tickets/456", handler.LastRequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task ExplainsTechGroupPermissionDenialForDirectTicketLookup()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Forbidden, "{}"));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetTicketAsync(ExplicitSettings(), 456);

        Assert.False(result.Success);
        Assert.Contains("tech group", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static WhdConnectionSettings ExplicitSettings() => new()
    {
        BaseUrl = "https://whd.example.test",
        Username = "technician",
        Secret = "secret",
        AuthenticationMode = WhdAuthenticationMode.ApplicationApiKey
    };

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string content) => new(statusCode)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class JsonResponseHandler(string responseJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri, body));
            return responseFactory(request);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? Uri, string Body);
}
