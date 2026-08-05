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
        Assert.Equal(TimeSpan.FromSeconds(20), WhdRestClient.OptionalClientDetailTimeout);
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
    public async Task TechNoteImageUploadUsesTemporarySessionCookiesAndMultipartFileUpload()
    {
        var imagePath = Path.Combine(
            Path.GetTempPath(),
            $"techbench-whd-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4e, 0x47]);
        try
        {
            var handler = new RecordingHandler(request =>
            {
                if (request.Method == HttpMethod.Get
                    && request.RequestUri?.AbsolutePath.EndsWith("/Session", StringComparison.Ordinal) == true)
                {
                    var response = Json(
                        HttpStatusCode.OK,
                        "{\"sessionKey\":\"attachment-session\",\"currentTechId\":7,\"instanceId\":-1}");
                    response.Headers.TryAddWithoutValidation(
                        "Set-Cookie",
                        "JSESSIONID=java-session; Path=/helpdesk; Secure; HttpOnly");
                    return response;
                }

                if (request.Method == HttpMethod.Delete)
                {
                    return Json(HttpStatusCode.OK, "{}");
                }

                return Json(HttpStatusCode.OK, "{\"id\":11}");
            });
            using var httpClient = new HttpClient(handler);
            var client = new WhdRestClient(httpClient);

            var result = await client.UploadTechNoteImagesAsync(
                ExplicitSettings(),
                987,
                [imagePath]);

            Assert.True(result.Success, result.Message);
            Assert.Equal([imagePath], result.UploadedFilePaths);
            Assert.Empty(result.Failures);
            Assert.Equal(3, handler.RequestCount);

            var upload = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
            Assert.Equal("/helpdesk/attachment/upload", upload.Uri?.AbsolutePath);
            var query = Uri.UnescapeDataString(upload.Uri?.Query ?? string.Empty);
            Assert.Contains("type=techNote", query, StringComparison.Ordinal);
            Assert.Contains("entityId=987", query, StringComparison.Ordinal);
            Assert.Contains("returnFields=id,uploadDate", query, StringComparison.Ordinal);
            Assert.Contains("JSESSIONID=java-session", upload.CookieHeader, StringComparison.Ordinal);
            Assert.Contains("wosid=attachment-session", upload.CookieHeader, StringComparison.Ordinal);
            Assert.DoesNotContain("sessionKey=", query, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("multipart/form-data", upload.ContentType, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("name=fileUpload", upload.Body, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(imagePath), upload.Body, StringComparison.Ordinal);

            var closeSession = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete);
            Assert.Contains("sessionKey=attachment-session", closeSession.Uri?.Query, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task TechNoteImageUploadContinuesWhenRedirectCookiesAreHiddenFromTheFinalSessionResponse()
    {
        var imagePath = Path.Combine(
            Path.GetTempPath(),
            $"techbench-whd-redirect-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(imagePath, [0xff, 0xd8, 0xff, 0xd9]);
        try
        {
            var cookieContainer = new CookieContainer();
            var uploadUri = new Uri("https://whd.example.test/helpdesk/attachment/upload");
            cookieContainer.Add(
                uploadUri,
                new Cookie("JSESSIONID", "redirect-java", "/helpdesk/"));
            cookieContainer.Add(
                uploadUri,
                new Cookie("wosid", "redirect-webobjects", "/helpdesk/"));
            string? cookiesDuringUpload = null;
            var handler = new RecordingHandler(request =>
            {
                if (request.Method == HttpMethod.Get
                    && request.RequestUri?.AbsolutePath.EndsWith("/Session", StringComparison.Ordinal) == true)
                {
                    // HttpClientHandler retains cookies set by an earlier redirect, but those
                    // Set-Cookie headers are not repeated on the final Session response.
                    return Json(
                        HttpStatusCode.OK,
                        "{\"sessionKey\":\"redirect-session\",\"currentTechId\":7,\"instanceId\":-1}");
                }

                if (request.Method == HttpMethod.Delete)
                {
                    return Json(HttpStatusCode.OK, "{}");
                }

                cookiesDuringUpload = cookieContainer.GetCookieHeader(uploadUri);
                return Json(HttpStatusCode.OK, "{\"id\":12}");
            });
            using var httpClient = new HttpClient(handler);
            var client = new WhdRestClient(httpClient, cookieContainer);

            var result = await client.UploadTechNoteImagesAsync(
                ExplicitSettings(),
                987,
                [imagePath]);

            Assert.True(result.Success, result.Message);
            Assert.Equal([imagePath], result.UploadedFilePaths);
            var upload = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
            Assert.Empty(upload.CookieHeader);
            Assert.DoesNotContain(
                "sessionKey=",
                Uri.UnescapeDataString(upload.Uri?.Query ?? string.Empty),
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("JSESSIONID=redirect-java", cookiesDuringUpload, StringComparison.Ordinal);
            Assert.Contains("wosid=redirect-session", cookiesDuringUpload, StringComparison.Ordinal);
            Assert.DoesNotContain("redirect-webobjects", cookiesDuringUpload, StringComparison.Ordinal);
            var retainedCookies = cookieContainer.GetCookieHeader(uploadUri);
            Assert.Contains("JSESSIONID=redirect-java", retainedCookies, StringComparison.Ordinal);
            Assert.DoesNotContain("wosid=", retainedCookies, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("redirect-webobjects", retainedCookies, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task TechNoteImageUploadFailsClosedWhenWhdDoesNotProvideJavaSessionCookie()
    {
        var imagePath = Path.Combine(
            Path.GetTempPath(),
            $"techbench-whd-auth-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4e, 0x47]);
        try
        {
            var handler = new RecordingHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    var response = Json(
                        HttpStatusCode.OK,
                        "{\"sessionKey\":\"temporary-session\",\"currentTechId\":7,\"instanceId\":-1}");
                    response.Headers.TryAddWithoutValidation(
                        "Set-Cookie",
                        "proxy-affinity=do-not-disclose; Path=/; Secure; HttpOnly");
                    return response;
                }

                return Json(HttpStatusCode.OK, "{}");
            });
            using var httpClient = new HttpClient(handler);
            var client = new WhdRestClient(httpClient);

            var result = await client.UploadTechNoteImagesAsync(
                ExplicitSettings(),
                987,
                [imagePath]);

            Assert.False(result.Success);
            var failure = Assert.Single(result.Failures);
            Assert.Equal(imagePath, failure.FilePath);
            Assert.Contains("JSESSIONID", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("do-not-disclose", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
            Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task TechNoteImageUploadDoesNotRehomeFinalResponseCookieInAutomaticMode()
    {
        var imagePath = Path.Combine(
            Path.GetTempPath(),
            $"techbench-whd-fresh-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4e, 0x47]);
        try
        {
            var cookieContainer = new CookieContainer();
            var uploadUri = new Uri("https://whd.example.test/helpdesk/attachment/upload");
            cookieContainer.Add(uploadUri, new Cookie("JSESSIONID", "origin-scoped-java", "/helpdesk/"));
            cookieContainer.Add(uploadUri, new Cookie("wosid", "old-webobjects", "/helpdesk/"));
            string? cookiesDuringUpload = null;
            var handler = new RecordingHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    var response = Json(
                        HttpStatusCode.OK,
                        "{\"sessionKey\":\"current-rest-session\",\"currentTechId\":7,\"instanceId\":-1}");
                    response.Headers.TryAddWithoutValidation(
                        "Set-Cookie",
                        "JSESSIONID=untrusted-final-response; Path=/; Secure; HttpOnly");
                    return response;
                }

                if (request.Method == HttpMethod.Post)
                {
                    cookiesDuringUpload = cookieContainer.GetCookieHeader(uploadUri);
                }

                return Json(HttpStatusCode.OK, request.Method == HttpMethod.Post ? "{\"id\":13}" : "{}");
            });
            using var httpClient = new HttpClient(handler);
            var client = new WhdRestClient(httpClient, cookieContainer);

            var result = await client.UploadTechNoteImagesAsync(
                ExplicitSettings(),
                987,
                [imagePath]);

            Assert.True(result.Success, result.Message);
            Assert.Contains("JSESSIONID=origin-scoped-java", cookiesDuringUpload, StringComparison.Ordinal);
            Assert.Contains("wosid=current-rest-session", cookiesDuringUpload, StringComparison.Ordinal);
            Assert.DoesNotContain("untrusted-final-response", cookiesDuringUpload, StringComparison.Ordinal);
            Assert.DoesNotContain("old-webobjects", cookiesDuringUpload, StringComparison.Ordinal);
            var retainedCookies = cookieContainer.GetCookieHeader(uploadUri);
            Assert.Contains("JSESSIONID=origin-scoped-java", retainedCookies, StringComparison.Ordinal);
            Assert.DoesNotContain("wosid=", retainedCookies, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("old-webobjects", retainedCookies, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task TechNoteImageUploadBuildsWebObjectsCookieFromCurrentRestSession()
    {
        var imagePath = Path.Combine(
            Path.GetTempPath(),
            $"techbench-whd-no-wosid-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(imagePath, [0xff, 0xd8, 0xff, 0xd9]);
        try
        {
            var handler = new RecordingHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    var response = Json(
                        HttpStatusCode.OK,
                        "{\"sessionKey\":\"temporary-session\",\"currentTechId\":7,\"instanceId\":-1}");
                    response.Headers.TryAddWithoutValidation(
                        "Set-Cookie",
                        "JSESSIONID=java-only; Path=/helpdesk; Secure; HttpOnly");
                    return response;
                }

                return Json(
                    HttpStatusCode.OK,
                    request.Method == HttpMethod.Post ? "{\"id\":14}" : "{}");
            });
            using var httpClient = new HttpClient(handler);
            var client = new WhdRestClient(httpClient);

            var result = await client.UploadTechNoteImagesAsync(
                ExplicitSettings(),
                987,
                [imagePath]);

            Assert.True(result.Success, result.Message);
            var upload = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
            Assert.Contains("JSESSIONID=java-only", upload.CookieHeader, StringComparison.Ordinal);
            Assert.Contains("wosid=temporary-session", upload.CookieHeader, StringComparison.Ordinal);
            Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task TechNoteImageUploadRejectsRedirectedHtmlSuccessResponse()
    {
        var imagePath = Path.Combine(
            Path.GetTempPath(),
            $"techbench-whd-login-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4e, 0x47]);
        try
        {
            var handler = new RecordingHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    var response = Json(
                        HttpStatusCode.OK,
                        "{\"sessionKey\":\"redirected-session\",\"currentTechId\":7,\"instanceId\":-1}");
                    response.Headers.TryAddWithoutValidation(
                        "Set-Cookie",
                        "JSESSIONID=redirected-java; Path=/helpdesk; Secure; HttpOnly");
                    return response;
                }

                if (request.Method == HttpMethod.Delete)
                {
                    return Json(HttpStatusCode.OK, "{}");
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = new HttpRequestMessage(
                        HttpMethod.Get,
                        "https://whd.example.test/helpdesk/login"),
                    Content = new StringContent("<html>Sign in</html>", Encoding.UTF8, "text/html")
                };
            });
            using var httpClient = new HttpClient(handler);
            var client = new WhdRestClient(httpClient);

            var result = await client.UploadTechNoteImagesAsync(
                ExplicitSettings(),
                987,
                [imagePath]);

            Assert.False(result.Success);
            Assert.Empty(result.UploadedFilePaths);
            Assert.Contains(
                "redirected",
                Assert.Single(result.Failures).Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task TechNoteImageUploadRequiresConfirmedAttachmentId()
    {
        var imagePath = Path.Combine(
            Path.GetTempPath(),
            $"techbench-whd-unconfirmed-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(imagePath, [0xff, 0xd8, 0xff, 0xd9]);
        try
        {
            var handler = new RecordingHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    var response = Json(
                        HttpStatusCode.OK,
                        "{\"sessionKey\":\"unconfirmed-session\",\"currentTechId\":7,\"instanceId\":-1}");
                    response.Headers.TryAddWithoutValidation(
                        "Set-Cookie",
                        "JSESSIONID=unconfirmed-java; Path=/helpdesk; Secure; HttpOnly");
                    return response;
                }

                return Json(HttpStatusCode.OK, "{}");
            });
            using var httpClient = new HttpClient(handler);
            var client = new WhdRestClient(httpClient);

            var result = await client.UploadTechNoteImagesAsync(
                ExplicitSettings(),
                987,
                [imagePath]);

            Assert.False(result.Success);
            Assert.Contains(
                "attachment ID",
                Assert.Single(result.Failures).Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task TechNoteImageUploadCleanupTimeoutDoesNotReplaceSuccessfulResult()
    {
        var imagePath = Path.Combine(
            Path.GetTempPath(),
            $"techbench-whd-cleanup-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4e, 0x47]);
        try
        {
            var handler = new RecordingHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    var response = Json(
                        HttpStatusCode.OK,
                        "{\"sessionKey\":\"cleanup-session\",\"currentTechId\":7,\"instanceId\":-1}");
                    response.Headers.TryAddWithoutValidation(
                        "Set-Cookie",
                        "JSESSIONID=cleanup-java; Path=/helpdesk; Secure; HttpOnly");
                    return response;
                }

                if (request.Method == HttpMethod.Delete)
                {
                    throw new TaskCanceledException("Cleanup timed out.");
                }

                return Json(HttpStatusCode.OK, "{\"id\":15}");
            });
            using var httpClient = new HttpClient(handler);
            var client = new WhdRestClient(httpClient);

            var result = await client.UploadTechNoteImagesAsync(
                ExplicitSettings(),
                987,
                [imagePath]);

            Assert.True(result.Success, result.Message);
            Assert.Equal([imagePath], result.UploadedFilePaths);
            Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task TechNoteImageUploadRedactsSessionValuesFromWhdFailureDetails()
    {
        const string javaSessionId = "java-session-do-not-log";
        const string restSessionKey = "rest-session-do-not-log";
        var imagePath = Path.Combine(
            Path.GetTempPath(),
            $"techbench-whd-redact-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(imagePath, [0xff, 0xd8, 0xff, 0xd9]);
        try
        {
            var handler = new RecordingHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    var response = Json(
                        HttpStatusCode.OK,
                        $"{{\"sessionKey\":\"{restSessionKey}\",\"currentTechId\":7,\"instanceId\":-1}}");
                    response.Headers.TryAddWithoutValidation(
                        "Set-Cookie",
                        $"JSESSIONID={javaSessionId}; Path=/helpdesk; Secure; HttpOnly");
                    return response;
                }

                if (request.Method == HttpMethod.Delete)
                {
                    return Json(HttpStatusCode.OK, "{}");
                }

                return Json(
                    HttpStatusCode.BadRequest,
                    $"Rejected JSESSIONID={javaSessionId}; wosid={restSessionKey}.");
            });
            using var httpClient = new HttpClient(handler);
            var client = new WhdRestClient(httpClient);

            var result = await client.UploadTechNoteImagesAsync(
                ExplicitSettings(),
                987,
                [imagePath]);

            Assert.False(result.Success);
            var message = Assert.Single(result.Failures).Message;
            Assert.Contains("[redacted]", message, StringComparison.Ordinal);
            Assert.DoesNotContain(javaSessionId, message, StringComparison.Ordinal);
            Assert.DoesNotContain(restSessionKey, message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task TechNoteImageUploadRejectsNonImagesBeforeCallingWhd()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.UploadTechNoteImagesAsync(
            ExplicitSettings(),
            987,
            ["not-an-image.txt"]);

        Assert.False(result.Success);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("not-an-image.txt", failure.FilePath);
        Assert.Contains("supported image type", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
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
    public async Task OrganizationSyncExplainsTicketRequestTimeout()
    {
        var handler = new RecordingHandler(_ =>
            throw new TaskCanceledException("The operation was canceled."));
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
        var client = new WhdRestClient(httpClient);

        var result = await client.GetOrganizationTicketsAsync(ExplicitSettings());

        Assert.False(result.Success);
        Assert.Contains("timed out after 90 seconds", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ticket data", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("operation was canceled", result.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task TicketNotesRouteNotFoundIsNotConclusiveProofThatTheExactNoteIsMissing()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.NotFound, "Route not found."));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetTechNoteAsync(ExplicitSettings(), 101, 987);

        Assert.False(result.Success);
        Assert.False(result.IsNotFound);
        Assert.Contains("could not conclusively verify", result.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task TechNoteUpdateVerificationAcceptsWhdHtmlEquivalentText()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Put
            ? Json(HttpStatusCode.OK, "{}")
            : Json(HttpStatusCode.OK, """
                [{"id":987,"noteText":"<p>Line one<br>Line two &amp; more</p>","workTime":"15"}]
                """));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.UpdateTechNoteAsync(
            ExplicitSettings(),
            101,
            987,
            "Line one\nLine two & more");

        Assert.True(result.Success, result.Message);
        Assert.Equal("WHD-TECHNOTE-987", result.ExternalReference);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task DeletesOnlyTheExactTrackedTechNoteAndVerifiesItIsGone()
    {
        var lookupCount = 0;
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                return Json(HttpStatusCode.OK, "{}");
            }

            lookupCount++;
            return lookupCount == 1
                ? Json(HttpStatusCode.OK, """
                    [{"id":987,"noteText":"Existing note.","workTime":"15"}]
                    """)
                : Json(HttpStatusCode.OK, "[]");
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.DeleteTechNoteAsync(ExplicitSettings(), 101, 987);

        Assert.True(result.Success, result.Message);
        Assert.False(result.MarkPosted);
        Assert.Equal("WHD-TECHNOTE-987", result.ExternalReference);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.EndsWith("/TechNotes/987", handler.Requests[1].Uri?.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Requests[2].Method);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task TechNoteDeleteFailureDoesNotReportSuccessOrRetryAsAnotherMethod()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Delete
            ? Json(HttpStatusCode.Forbidden, "Deletion is not permitted.")
            : Json(HttpStatusCode.OK, """
                [{"id":987,"noteText":"Existing note.","workTime":"15"}]
                """));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.DeleteTechNoteAsync(ExplicitSettings(), 101, 987);

        Assert.False(result.Success);
        Assert.False(result.OutcomeUncertain);
        Assert.False(result.MarkPosted);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task DeleteRouteNotFoundDoesNotDeleteLocalStateWithoutMissingNoteVerification()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Delete
            ? Json(HttpStatusCode.NotFound, "The delete route was not found.")
            : Json(HttpStatusCode.OK, """
                [{"id":987,"noteText":"Existing note.","workTime":"15"}]
                """));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));

        var result = await client.DeleteTechNoteAsync(
            ExplicitSettings(),
            101,
            987,
            cancellation.Token);

        Assert.False(result.Success);
        Assert.True(result.OutcomeUncertain);
        Assert.True(handler.Requests.Count >= 3);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.Contains(
            handler.Requests.Skip(2),
            request => request.Method == HttpMethod.Get);
    }

    [Theory]
    [InlineData("Line one\nLine two & more", "<p>Line one<br>Line two &amp; more</p>")]
    [InlineData("Line one\r\nLine two", "Line one\nLine two")]
    [InlineData("A non-breaking space", "A non-breaking\u00a0space")]
    public void NormalizesWhdNoteRepresentationsForVerification(string local, string whd)
    {
        Assert.Equal(
            WhdRestClient.NormalizeNoteForComparison(local),
            WhdRestClient.NormalizeNoteForComparison(whd));
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
        const string locationResponse = """
            [
              {
                "id": 12,
                "locationName": "Friends Central School",
                "address": "1101 City Avenue",
                "city": "Wynnewood",
                "state": "PA",
                "postalCode": "19096",
                "phone": "610-555-0100",
                "isInactive": false
              },
              {"id": 13, "locationName": "Old Location", "isInactive": true}
            ]
            """;
        const string clientResponse = """
            [
              {
                "id": 72,
                "firstName": "Alex",
                "lastName": "Morgan",
                "email": "alex@example.test",
                "phone": "610-555-0123",
                "isAdmin": true,
                "location": {"id": 12}
              },
              {
                "id": 73,
                "firstName": "Secondary",
                "lastName": "Contact",
                "email": "secondary@example.test",
                "location": {"id": 12}
              }
            ]
            """;
        var handler = new RecordingHandler(request => Json(
            HttpStatusCode.OK,
            request.RequestUri?.AbsolutePath.EndsWith("/Clients", StringComparison.Ordinal) == true
                ? clientResponse
                : locationResponse));
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetClientsAsync(ExplicitSettings());

        Assert.True(result.Success, result.Message);
        var location = Assert.Single(result.Clients);
        Assert.Equal("WHD-LOCATION-12", location.ExternalId);
        Assert.Equal("Friends Central School", location.Name);
        Assert.Equal("Friends Central School", location.LocationName);
        Assert.Equal("Alex Morgan", location.ContactName);
        Assert.Equal("alex@example.test", location.ContactEmail);
        Assert.Equal("610-555-0123", location.Phone);
        Assert.Equal("1101 City Avenue, Wynnewood, PA 19096", location.Address);
        Assert.Contains("/Locations", handler.Requests[0].Uri?.AbsolutePath);
        Assert.Contains("/Clients", handler.Requests[1].Uri?.AbsolutePath);
    }

    [Fact]
    public async Task FullClientSyncLoadsDetailedWhdContactInformation()
    {
        const string locationResponse = """
            [
              {
                "id": 12,
                "locationName": "Holy Ghost Prep",
                "address": "Fallback location address",
                "city": "Bensalem",
                "state": "PA",
                "postalCode": "19020"
              }
            ]
            """;
        const string clientListResponse = """
            [
              {
                "id": 72,
                "firstName": "Mike",
                "lastName": "Jacobs",
                "isAdmin": true,
                "location": {"id": 12}
              }
            ]
            """;
        const string clientDetailResponse = """
            {
              "id": 72,
              "firstName": "Mike",
              "lastName": "Jacobs",
              "email": "technology@holyghostprep.org",
              "secondaryEmail": "mjacobs@holyghostprep.org",
              "phone": "(215) 639-2102",
              "phone2": "(610) 613-1882",
              "address": "2429 Bristol Pike",
              "city": "Bensalem",
              "state": "PA",
              "zip": "19020",
              "location": {"id": 12}
            }
            """;
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            return Json(
                HttpStatusCode.OK,
                path.EndsWith("/Clients/72", StringComparison.Ordinal)
                    ? clientDetailResponse
                    : path.EndsWith("/Clients", StringComparison.Ordinal)
                        ? clientListResponse
                        : locationResponse);
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetClientsAsync(ExplicitSettings());

        Assert.True(result.Success, result.Message);
        var location = Assert.Single(result.Clients);
        Assert.Equal("Mike Jacobs", location.ContactName);
        Assert.Equal(
            "technology@holyghostprep.org / mjacobs@holyghostprep.org",
            location.ContactEmail);
        Assert.Equal("(215) 639-2102 / (610) 613-1882", location.Phone);
        Assert.Equal("2429 Bristol Pike, Bensalem, PA 19020", location.Address);
        Assert.Contains(
            handler.Requests,
            request => request.Uri?.AbsolutePath.EndsWith("/Clients/72", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task FullClientSyncRetainsListContactWhenWhdRejectsLegacyClientDetails()
    {
        const string locationResponse = """
            [
              {
                "id": 22,
                "locationName": "Problem School"
              }
            ]
            """;
        const string clientListResponse = """
            [
              {
                "id": 486,
                "firstName": "Legacy",
                "lastName": "Contact",
                "location": {"id": 22}
              }
            ]
            """;
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            return path.EndsWith("/Clients/486", StringComparison.Ordinal)
                ? Json(
                    HttpStatusCode.BadRequest,
                    """{"message":"The provider e-mail address does not meet RFC 5322."}""")
                : Json(
                    HttpStatusCode.OK,
                    path.EndsWith("/Clients", StringComparison.Ordinal)
                        ? clientListResponse
                        : locationResponse);
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetClientsAsync(ExplicitSettings());

        Assert.True(result.Success, result.Message);
        var location = Assert.Single(result.Clients);
        Assert.Equal("Legacy Contact", location.ContactName);
        Assert.True(string.IsNullOrWhiteSpace(location.ContactEmail));
        Assert.Contains("486", result.Message, StringComparison.Ordinal);
        Assert.Contains(
            "list data was retained",
            result.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FullClientSyncStillFailsWhenClientDetailServiceIsUnavailable()
    {
        const string locationResponse =
            """[{"id":22,"locationName":"Problem School"}]""";
        const string clientListResponse =
            """[{"id":486,"firstName":"Legacy","lastName":"Contact","location":{"id":22}}]""";
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            return path.EndsWith("/Clients/486", StringComparison.Ordinal)
                ? Json(HttpStatusCode.InternalServerError, """{"message":"Unavailable"}""")
                : Json(
                    HttpStatusCode.OK,
                    path.EndsWith("/Clients", StringComparison.Ordinal)
                        ? clientListResponse
                        : locationResponse);
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetClientsAsync(ExplicitSettings());

        Assert.False(result.Success);
        Assert.Contains("client 486", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HTTP 500", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullClientSyncRetainsListContactWhenOptionalClientDetailTimesOut()
    {
        const string locationResponse =
            """[{"id":22,"locationName":"Slow School"}]""";
        const string clientListResponse =
            """[{"id":486,"firstName":"Slow","lastName":"Contact","location":{"id":22}}]""";
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/Clients/486", StringComparison.Ordinal))
            {
                throw new TaskCanceledException("Simulated optional detail timeout.");
            }

            return Json(
                HttpStatusCode.OK,
                path.EndsWith("/Clients", StringComparison.Ordinal)
                    ? clientListResponse
                    : locationResponse);
        });
        using var httpClient = new HttpClient(handler);
        var client = new WhdRestClient(httpClient);

        var result = await client.GetClientsAsync(ExplicitSettings());

        Assert.True(result.Success, result.Message);
        var location = Assert.Single(result.Clients);
        Assert.Equal("Slow Contact", location.ContactName);
        Assert.Contains("486", result.Message, StringComparison.Ordinal);
        Assert.Contains(
            "list data was retained",
            result.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FullClientSyncExplainsTimeoutWhenRequiredListDataTimesOut()
    {
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/Clients", StringComparison.Ordinal))
            {
                throw new TaskCanceledException("The operation was canceled.");
            }

            return Json(
                HttpStatusCode.OK,
                """[{"id":22,"locationName":"Slow School"}]""");
        });
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
        var client = new WhdRestClient(httpClient);

        var result = await client.GetClientsAsync(ExplicitSettings());

        Assert.False(result.Success);
        Assert.Contains("timed out after 90 seconds", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("required list data", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("operation was canceled", result.Message, StringComparison.OrdinalIgnoreCase);
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
            var cookieHeader = request.Headers.TryGetValues("Cookie", out var cookies)
                ? string.Join("; ", cookies)
                : string.Empty;
            var contentType = request.Content?.Headers.ContentType?.ToString() ?? string.Empty;
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                body,
                cookieHeader,
                contentType));
            var response = responseFactory(request);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri? Uri,
        string Body,
        string CookieHeader,
        string ContentType);
}
