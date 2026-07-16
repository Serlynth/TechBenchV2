using System.Net;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Http.Json;
using System.Globalization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TechBench.Models;

namespace TechBench.Providers;

public sealed class WhdRestClient
{
    private const int PageSize = 100;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, WhdAuthParameters> _authenticationCache = new(StringComparer.Ordinal);

    public WhdRestClient() : this(new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(20)
    })
    {
    }

    internal WhdRestClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<WhdSyncResult> TestConnectionAsync(WhdConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        var validationError = Validate(settings);
        if (validationError is not null)
        {
            return WhdSyncResult.Failed(validationError);
        }

        try
        {
            var auth = await ResolveAuthenticationAsync(settings, cancellationToken);
            var tickets = await GetTicketsPageAsync(settings, auth, page: 1, limit: 1, cancellationToken);
            return WhdSyncResult.Succeeded(
                $"Connected to Web Help Desk as {settings.Username}. Ticket filter returned {tickets.Count} sample item(s).",
                tickets);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            return WhdSyncResult.Failed($"Web Help Desk test failed: {ex.Message}");
        }
    }

    public async Task<WhdSyncResult> GetMyTicketsAsync(WhdConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        var validationError = Validate(settings);
        if (validationError is not null)
        {
            return WhdSyncResult.Failed(validationError);
        }

        try
        {
            var auth = await ResolveAuthenticationAsync(settings, cancellationToken);
            var tickets = new List<WhdSyncedTicket>();
            var seenExternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pageSignatures = new HashSet<string>(StringComparer.Ordinal);
            var isComplete = false;

            for (var page = 1; page <= 100; page++)
            {
                var batch = await GetTicketsPageAsync(settings, auth, page, PageSize, cancellationToken);
                if (batch.Count == 0)
                {
                    isComplete = true;
                    break;
                }

                var signature = BuildPageSignature(batch.Select(static ticket => ticket.ExternalId));
                if (!pageSignatures.Add(signature))
                {
                    break;
                }

                var addedCount = 0;
                foreach (var ticket in batch)
                {
                    if (seenExternalIds.Add(ticket.ExternalId))
                    {
                        tickets.Add(ticket);
                        addedCount++;
                    }
                }

                if (addedCount == 0)
                {
                    break;
                }

                if (batch.Count < PageSize)
                {
                    isComplete = true;
                    break;
                }
            }

            var openTicketCount = tickets.Count(static ticket => !ticket.IsClosed);
            var closedTicketCount = tickets.Count - openTicketCount;
            return WhdSyncResult.Succeeded(
                $"Synced {openTicketCount} non-closed assigned Web Help Desk ticket(s) for {settings.Username}"
                + (closedTicketCount > 0 ? $" and updated {closedTicketCount} closed ticket(s)." : ".")
                + (isComplete ? string.Empty : " Paging stopped because WHD repeated a page; missing-ticket reconciliation was skipped."),
                tickets,
                isComplete);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            return WhdSyncResult.Failed($"Web Help Desk sync failed: {ex.Message}");
        }
    }

    public async Task<WhdTicketLookupResult> GetTicketAsync(
        WhdConnectionSettings settings,
        int ticketId,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(settings);
        if (validationError is not null)
        {
            return WhdTicketLookupResult.Failed(validationError);
        }

        if (ticketId <= 0)
        {
            return WhdTicketLookupResult.Failed("Enter a valid numeric Web Help Desk ticket number.");
        }

        try
        {
            var auth = await ResolveAuthenticationAsync(settings, cancellationToken);
            var requestUri = BuildRequestUri(settings.BaseUrl, $"Tickets/{ticketId}", auth, new Dictionary<string, string>
            {
                ["style"] = "long"
            });
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var details = string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase : content.Trim();
                return response.StatusCode switch
                {
                    HttpStatusCode.Forbidden => WhdTicketLookupResult.Failed(
                        $"Web Help Desk denied access to ticket #{ticketId}. The current technician may not have access to that ticket's tech group."),
                    HttpStatusCode.NotFound => WhdTicketLookupResult.Failed(
                        $"Web Help Desk ticket #{ticketId} was not found."),
                    _ => WhdTicketLookupResult.Failed(
                        $"Web Help Desk could not check ticket #{ticketId}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {details}")
                };
            }

            using var document = JsonDocument.Parse(content);
            var ticket = ParseTickets(document.RootElement).SingleOrDefault();
            return ticket is null
                ? WhdTicketLookupResult.Failed($"Web Help Desk returned no details for ticket #{ticketId}.")
                : WhdTicketLookupResult.Succeeded(ticket);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            return WhdTicketLookupResult.Failed($"Web Help Desk could not check ticket #{ticketId}: {ex.Message}");
        }
    }

    public async Task<WhdStatusTypeSyncResult> GetStatusTypesAsync(
        WhdConnectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(settings);
        if (validationError is not null)
        {
            return WhdStatusTypeSyncResult.Failed(validationError);
        }

        try
        {
            var auth = await ResolveAuthenticationAsync(settings, cancellationToken);
            var statusTypes = await GetStatusTypesListAsync(settings, auth, cancellationToken);
            return WhdStatusTypeSyncResult.Succeeded(
                $"Synced {statusTypes.Count} Web Help Desk status type(s).",
                statusTypes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            return WhdStatusTypeSyncResult.Failed($"Web Help Desk status sync failed: {ex.Message}");
        }
    }

    public async Task<WhdClientSyncResult> GetClientsAsync(
        WhdConnectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(settings);
        if (validationError is not null)
        {
            return WhdClientSyncResult.Failed(validationError);
        }

        try
        {
            var auth = await ResolveAuthenticationAsync(settings, cancellationToken);
            var clients = new List<WhdSyncedClient>();
            var seenExternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pageSignatures = new HashSet<string>(StringComparer.Ordinal);
            var isComplete = false;

            for (var page = 1; page <= 200; page++)
            {
                var batch = await GetLocationsPageAsync(settings, auth, page, PageSize, cancellationToken);
                if (batch.Count == 0)
                {
                    isComplete = true;
                    break;
                }

                var signature = BuildPageSignature(batch.Select(static client => client.ExternalId));
                if (!pageSignatures.Add(signature))
                {
                    break;
                }

                var addedCount = 0;
                foreach (var client in batch)
                {
                    if (client.IsActive && seenExternalIds.Add(client.ExternalId))
                    {
                        clients.Add(client);
                        addedCount++;
                    }
                }

                if (addedCount == 0 && batch.All(static client => client.IsActive))
                {
                    break;
                }

                if (batch.Count < PageSize)
                {
                    isComplete = true;
                    break;
                }
            }

            return WhdClientSyncResult.Succeeded(
                $"Synced {clients.Count} active Web Help Desk location(s)."
                + (isComplete ? string.Empty : " Paging stopped because WHD repeated a page; stale-client reconciliation was skipped."),
                clients,
                isComplete);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            return WhdClientSyncResult.Failed($"Web Help Desk client sync failed: {ex.Message}");
        }
    }

    public async Task<PostingResult> PostTicketNoteAsync(
        WhdConnectionSettings settings,
        int ticketId,
        string noteText,
        int durationMinutes,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(settings);
        if (validationError is not null)
        {
            return PostingResult.Failed(validationError);
        }

        if (ticketId <= 0)
        {
            return PostingResult.Failed("Select a synced Web Help Desk ticket before posting.");
        }

        if (string.IsNullOrWhiteSpace(noteText))
        {
            return PostingResult.Failed("Enter a Sage/WHD Note before posting to Web Help Desk.");
        }

        if (durationMinutes <= 0)
        {
            return PostingResult.Failed("Enter a positive duration before posting to Web Help Desk.");
        }

        var payload = BuildTicketNotePayload(ticketId, noteText, durationMinutes);

        var postStarted = false;
        try
        {
            var auth = await ResolveAuthenticationAsync(settings, cancellationToken);
            var requestUri = BuildRequestUri(settings.BaseUrl, "TechNotes", auth, new Dictionary<string, string>());
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload))
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            postStarted = true;
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var message = string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase : content.Trim();
                return PostingResult.Failed(
                    $"Web Help Desk post failed using {auth.DisplayName}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. WHD response: {message}",
                    payload);
            }

            var externalReference = TryReadResponseId(content);
            if (string.IsNullOrWhiteSpace(externalReference))
            {
                externalReference = await FindPostedTicketNoteAsync(
                    settings,
                    auth,
                    ticketId,
                    noteText,
                    durationMinutes,
                    cancellationToken);
            }

            return string.IsNullOrWhiteSpace(externalReference)
                ? PostingResult.Uncertain(
                    "Web Help Desk accepted the note, but TechBench could not read back a TechNote ID. The entry remains WHD pending to avoid a false posted status.",
                    payload)
                : PostingResult.Succeeded("Posted and verified the Sage/WHD Note in Web Help Desk.", payload, externalReference);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return postStarted
                ? PostingResult.Uncertain(
                    $"Web Help Desk did not return a confirmable result after the note request began: {ex.Message} Verify WHD before retrying.",
                    payload)
                : PostingResult.Failed($"Web Help Desk post failed: {ex.Message}", payload);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException)
        {
            return PostingResult.Failed($"Web Help Desk post failed: {ex.Message}", payload);
        }
    }

    public async Task<WhdTechNoteLookupResult> GetTechNoteAsync(
        WhdConnectionSettings settings,
        int ticketId,
        int techNoteId,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(settings);
        if (validationError is not null)
        {
            return WhdTechNoteLookupResult.Failed(validationError);
        }

        if (ticketId <= 0 || techNoteId <= 0)
        {
            return WhdTechNoteLookupResult.Failed("A valid WHD ticket and TechNote ID are required for synchronization.");
        }

        try
        {
            var auth = await ResolveAuthenticationAsync(settings, cancellationToken);
            var pageSignatures = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 1; page <= 100; page++)
            {
                var requestUri = BuildRequestUri(settings.BaseUrl, "TicketNotes", auth, new Dictionary<string, string>
                {
                    ["jobTicketId"] = ticketId.ToString(CultureInfo.InvariantCulture),
                    ["style"] = "long",
                    ["limit"] = PageSize.ToString(CultureInfo.InvariantCulture),
                    ["page"] = page.ToString(CultureInfo.InvariantCulture)
                });

                using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var details = string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase : content.Trim();
                    return response.StatusCode switch
                    {
                        HttpStatusCode.Forbidden => WhdTechNoteLookupResult.Failed(
                            $"Web Help Desk denied access to TechNote #{techNoteId}. The current technician may not have access to the ticket's tech group."),
                        HttpStatusCode.NotFound => WhdTechNoteLookupResult.Failed(
                            $"Web Help Desk ticket #{ticketId} or TechNote #{techNoteId} was not found."),
                        _ => WhdTechNoteLookupResult.Failed(
                            $"Web Help Desk could not read TechNote #{techNoteId}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {details}")
                    };
                }

                using var document = JsonDocument.Parse(content);
                var records = EnumerateRecords(document.RootElement).ToArray();
                foreach (var noteElement in records)
                {
                    if (ReadInt(noteElement, "id") != techNoteId)
                    {
                        continue;
                    }

                    return WhdTechNoteLookupResult.Succeeded(
                        techNoteId,
                        ReadString(noteElement, "noteText") ?? string.Empty,
                        ReadDurationMinutes(noteElement));
                }

                if (records.Length < PageSize)
                {
                    break;
                }

                var signature = BuildPageSignature(records.Select(note => ReadString(note, "id") ?? string.Empty));
                if (!pageSignatures.Add(signature))
                {
                    break;
                }
            }

            return WhdTechNoteLookupResult.Failed(
                $"Web Help Desk ticket #{ticketId} is available, but TechNote #{techNoteId} was not found.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            return WhdTechNoteLookupResult.Failed($"Web Help Desk could not read TechNote #{techNoteId}: {ex.Message}");
        }
    }

    public async Task<PostingResult> UpdateTechNoteAsync(
        WhdConnectionSettings settings,
        int ticketId,
        int techNoteId,
        string noteText,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(settings);
        if (validationError is not null)
        {
            return PostingResult.Failed(validationError);
        }

        if (ticketId <= 0 || techNoteId <= 0)
        {
            return PostingResult.Failed("A valid WHD ticket and TechNote ID are required for synchronization.");
        }

        if (string.IsNullOrWhiteSpace(noteText))
        {
            return PostingResult.Failed("Enter a Sage/WHD Note before updating Web Help Desk.");
        }

        var payload = JsonSerializer.Serialize(
            new { noteText = noteText.Trim() },
            new JsonSerializerOptions { WriteIndented = true });
        var updateStarted = false;

        try
        {
            var auth = await ResolveAuthenticationAsync(settings, cancellationToken);
            var requestUri = BuildRequestUri(settings.BaseUrl, $"TechNotes/{techNoteId}", auth, new Dictionary<string, string>());
            using var request = new HttpRequestMessage(HttpMethod.Put, requestUri)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload))
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            updateStarted = true;
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var details = string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase : content.Trim();
                return PostingResult.Failed(
                    $"Web Help Desk did not update TechNote #{techNoteId}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {details}",
                    payload);
            }

            WhdTechNoteLookupResult? verification = null;
            foreach (var delay in new[]
                     {
                         TimeSpan.FromMilliseconds(150),
                         TimeSpan.FromMilliseconds(500),
                         TimeSpan.FromSeconds(1)
                     })
            {
                await Task.Delay(delay, cancellationToken);
                verification = await GetTechNoteAsync(settings, ticketId, techNoteId, cancellationToken);
                if (verification.Success
                    && NormalizeNote(verification.NoteText).Equals(NormalizeNote(noteText), StringComparison.Ordinal))
                {
                    return PostingResult.Succeeded(
                        $"Updated and verified Web Help Desk TechNote #{techNoteId}.",
                        payload,
                        $"WHD-TECHNOTE-{techNoteId}");
                }
            }

            var verificationMessage = verification?.Success == true
                ? "WHD returned different note text during verification."
                : verification?.Message ?? "WHD did not return the TechNote during verification.";
            return PostingResult.Uncertain(
                $"Web Help Desk accepted the update to TechNote #{techNoteId}, but TechBench could not verify it. {verificationMessage}",
                payload,
                $"WHD-TECHNOTE-{techNoteId}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return updateStarted
                ? PostingResult.Uncertain(
                    $"The update to Web Help Desk TechNote #{techNoteId} began but could not be verified: {ex.Message}",
                    payload,
                    $"WHD-TECHNOTE-{techNoteId}")
                : PostingResult.Failed($"Web Help Desk TechNote update failed: {ex.Message}", payload);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException)
        {
            return PostingResult.Failed($"Web Help Desk TechNote update failed: {ex.Message}", payload);
        }
    }

    public async Task<PostingResult> UpdateTicketStatusAsync(
        WhdConnectionSettings settings,
        int ticketId,
        int statusTypeId,
        string statusName,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(settings);
        if (validationError is not null)
        {
            return PostingResult.Failed(validationError);
        }

        if (ticketId <= 0)
        {
            return PostingResult.Failed("Select a synced Web Help Desk ticket before changing status.");
        }

        if (statusTypeId <= 0)
        {
            return PostingResult.Failed("Select a synced Web Help Desk status before changing status.");
        }

        var payload = BuildTicketStatusPayload(statusTypeId);

        try
        {
            var auth = await ResolveAuthenticationAsync(settings, cancellationToken);
            var requestUri = BuildRequestUri(settings.BaseUrl, $"Tickets/{ticketId}", auth, new Dictionary<string, string>());
            using var request = new HttpRequestMessage(HttpMethod.Put, requestUri)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload))
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var message = string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase : content.Trim();
                return PostingResult.Failed(
                    $"Web Help Desk status update failed using {auth.DisplayName}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. WHD response: {message}",
                    payload);
            }

            return PostingResult.Succeeded($"Changed Web Help Desk ticket status to {statusName}.", payload, markPosted: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            return PostingResult.Failed($"Web Help Desk status update failed: {ex.Message}", payload);
        }
    }

    private async Task<WhdAuthParameters> ResolveAuthenticationAsync(WhdConnectionSettings settings, CancellationToken cancellationToken)
    {
        var explicitAuthentication = GetExplicitAuthentication(settings);
        if (explicitAuthentication is not null)
        {
            return explicitAuthentication;
        }

        var cacheKey = BuildAuthenticationCacheKey(settings);
        if (_authenticationCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var candidates = new[]
        {
            WhdAuthParameters.UsernamePassword(settings.Username, settings.Secret),
            WhdAuthParameters.ApplicationApiKey(settings.Username, settings.Secret),
            WhdAuthParameters.TechApiKey(settings.Secret)
        };

        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                await GetTicketsPageAsync(settings, candidate, page: 1, limit: 1, cancellationToken);
                _authenticationCache[cacheKey] = candidate;
                return candidate;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                failures.Add(candidate.DisplayName);
            }
        }

        throw new InvalidOperationException(
            failures.Count == 0
                ? "No supported authentication method succeeded."
                : $"Authentication failed using {string.Join(", ", failures)}.");
    }

    private async Task<IReadOnlyList<WhdSyncedTicket>> GetTicketsPageAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        int page,
        int limit,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(settings.BaseUrl, "Tickets/mine", auth, new Dictionary<string, string>
        {
            ["style"] = "long",
            ["limit"] = limit.ToString(),
            ["page"] = page.ToString()
        });

        using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase : content.Trim();
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} from Web Help Desk: {message}",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(content);
        return ParseTickets(document.RootElement);
    }

    private async Task<IReadOnlyList<WhdSyncedClient>> GetLocationsPageAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        int page,
        int limit,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(settings.BaseUrl, "Locations", auth, new Dictionary<string, string>
        {
            ["style"] = "long",
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            ["page"] = page.ToString(CultureInfo.InvariantCulture)
        });

        using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase : content.Trim();
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} from Web Help Desk locations: {message}",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(content);
        return ParseLocations(document.RootElement);
    }

    private async Task<IReadOnlyList<WhdStatusType>> GetStatusTypesListAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(settings.BaseUrl, "StatusTypes", auth, new Dictionary<string, string>
        {
            ["style"] = "short"
        });

        using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase : content.Trim();
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} from Web Help Desk status types: {message}",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(content);
        return ParseStatusTypes(document.RootElement);
    }

    private async Task<string?> FindPostedTicketNoteAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        int ticketId,
        string expectedNote,
        int expectedDurationMinutes,
        CancellationToken cancellationToken)
    {
        var delays = new[]
        {
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1)
        };

        foreach (var delay in delays)
        {
            await Task.Delay(delay, cancellationToken);
            var requestUri = BuildRequestUri(settings.BaseUrl, "TicketNotes", auth, new Dictionary<string, string>
            {
                ["jobTicketId"] = ticketId.ToString(CultureInfo.InvariantCulture),
                ["style"] = "long",
                ["limit"] = PageSize.ToString(CultureInfo.InvariantCulture),
                ["page"] = "1"
            });

            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            using var document = JsonDocument.Parse(content);
            foreach (var noteElement in EnumerateRecords(document.RootElement))
            {
                var id = ReadString(noteElement, "id");
                var noteText = ReadString(noteElement, "noteText");
                var duration = ReadDurationMinutes(noteElement);
                if (!string.IsNullOrWhiteSpace(id)
                    && NormalizeNote(noteText).Equals(NormalizeNote(expectedNote), StringComparison.Ordinal)
                    && duration == expectedDurationMinutes)
                {
                    return $"WHD-TECHNOTE-{id.Trim()}";
                }
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateRecords(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                yield return element;
            }

            yield break;
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("records", out var records)
            && records.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in records.EnumerateArray())
            {
                yield return element;
            }

            yield break;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
        }
    }

    private static int? ReadDurationMinutes(JsonElement element)
    {
        foreach (var propertyName in new[] { "workTime", "durationMinutes", "minutes" })
        {
            var value = ReadString(element, propertyName);
            if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var duration))
            {
                return decimal.ToInt32(decimal.Round(duration, 0, MidpointRounding.AwayFromZero));
            }
        }

        return null;
    }

    private static string NormalizeNote(string? value) =>
        (value ?? string.Empty).ReplaceLineEndings("\n").Trim();

    private static string BuildTicketNotePayload(int ticketId, string noteText, int durationMinutes)
    {
        var payload = new
        {
            noteText = noteText.Trim(),
            jobticket = new
            {
                type = "JobTicket",
                id = ticketId
            },
            workTime = durationMinutes.ToString(CultureInfo.InvariantCulture),
            isHidden = true,
            isSolution = false,
            emailClient = false,
            emailCc = false,
            emailBcc = false
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildTicketStatusPayload(int statusTypeId)
    {
        var payload = new
        {
            statustype = new
            {
                type = "StatusType",
                id = statusTypeId
            },
            sendEmail = false,
            emailClient = false,
            emailTech = false,
            emailCc = false,
            emailBcc = false
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static IReadOnlyList<WhdSyncedTicket> ParseTickets(JsonElement root)
    {
        var tickets = new List<WhdSyncedTicket>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var ticketElement in root.EnumerateArray())
            {
                AddTicket(tickets, ticketElement);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
        {
            foreach (var ticketElement in records.EnumerateArray())
            {
                AddTicket(tickets, ticketElement);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            AddTicket(tickets, root);
        }

        return tickets;
    }

    private static IReadOnlyList<WhdStatusType> ParseStatusTypes(JsonElement root)
    {
        var statusTypes = new List<WhdStatusType>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var statusTypeElement in root.EnumerateArray())
            {
                AddStatusType(statusTypes, statusTypeElement);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
        {
            foreach (var statusTypeElement in records.EnumerateArray())
            {
                AddStatusType(statusTypes, statusTypeElement);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            AddStatusType(statusTypes, root);
        }

        return statusTypes
            .GroupBy(static statusType => statusType.Id)
            .Select(static group => group.First())
            .OrderBy(static statusType => statusType.IsClosed)
            .ThenBy(static statusType => statusType.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<WhdSyncedClient> ParseLocations(JsonElement root)
    {
        var locations = new List<WhdSyncedClient>();
        foreach (var locationElement in EnumerateRecords(root))
        {
            AddLocation(locations, locationElement);
        }

        return locations;
    }

    private static void AddLocation(List<WhdSyncedClient> locations, JsonElement locationElement)
    {
        if (locationElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var id = ReadString(locationElement, "id");
        var locationName = ReadString(locationElement, "locationName")
            ?? ReadString(locationElement, "displayName")
            ?? ReadString(locationElement, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(locationName))
        {
            return;
        }

        var isActive = !ReadBoolean(locationElement, "deleted")
            && !ReadBoolean(locationElement, "inactive")
            && !ReadBoolean(locationElement, "isInactive");
        var trimmedName = locationName.Trim();
        locations.Add(new WhdSyncedClient
        {
            ExternalId = $"WHD-LOCATION-{id.Trim()}",
            Name = trimmedName,
            LocationName = trimmedName,
            ContactName = null,
            IsActive = isActive
        });
    }

    private static void AddStatusType(List<WhdStatusType> statusTypes, JsonElement statusTypeElement)
    {
        if (statusTypeElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var id = ReadInt(statusTypeElement, "id");
        if (!id.HasValue || id.Value <= 0)
        {
            return;
        }

        var name = ReadString(statusTypeElement, "statusTypeName")
            ?? ReadString(statusTypeElement, "name")
            ?? ReadString(statusTypeElement, "displayName")
            ?? $"Status {id.Value}";

        statusTypes.Add(new WhdStatusType
        {
            Id = id.Value,
            Name = name,
            IsClosed = IsClosedStatus(name)
        });
    }

    private static void AddTicket(List<WhdSyncedTicket> tickets, JsonElement ticketElement)
    {
        if (ticketElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var id = ReadString(ticketElement, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var status = ReadNestedString(ticketElement, "statustype", "statusTypeName")
            ?? ReadNestedString(ticketElement, "statusType", "statusTypeName")
            ?? ReadNestedString(ticketElement, "status", "statusTypeName")
            ?? ReadString(ticketElement, "statusTypeName")
            ?? ReadString(ticketElement, "status")
            ?? "Open";
        var statusTypeId = ReadInt(ticketElement, "statusTypeId")
            ?? ReadNestedInt(ticketElement, "statustype", "id")
            ?? ReadNestedInt(ticketElement, "statusType", "id")
            ?? ReadNestedInt(ticketElement, "status", "id");

        var isClosed = IsClosedStatus(status) || ReadBoolean(ticketElement, "deleted");

        tickets.Add(new WhdSyncedTicket
        {
            ExternalId = FormatWhdId(id),
            TicketNumber = FormatWhdId(id),
            Subject = ReadString(ticketElement, "subject") ?? "Web Help Desk ticket",
            Status = status,
            StatusTypeId = statusTypeId,
            IsClosed = isClosed,
            Client = ReadClient(ticketElement)
        });
    }

    private static WhdSyncedClient ReadClient(JsonElement ticketElement)
    {
        var clientElement = TryGetObject(ticketElement, "clientReporter")
            ?? TryGetObject(ticketElement, "client")
            ?? TryGetObject(ticketElement, "clientTech");

        if (clientElement is null)
        {
            return new WhdSyncedClient
            {
                ExternalId = "WHD-UNKNOWN",
                Name = "Unknown WHD Client"
            };
        }

        var element = clientElement.Value;
        var id = ReadString(element, "id")
            ?? ReadString(element, "username")
            ?? ReadString(element, "email")
            ?? ReadString(element, "displayName")
            ?? "UNKNOWN";

        var clientName = ReadString(ticketElement, "displayClient")
            ?? ReadString(element, "displayName")
            ?? ReadString(element, "fullName")
            ?? BuildName(element)
            ?? ReadString(element, "clientName")
            ?? ReadString(element, "username")
            ?? ReadString(element, "email")
            ?? "Unknown WHD Client";

        var locationName = ReadLocationName(ticketElement);
        var name = BuildClientDisplayName(locationName, clientName);

        return new WhdSyncedClient
        {
            ExternalId = FormatWhdId(id),
            Name = name,
            LocationName = string.IsNullOrWhiteSpace(locationName) ? null : locationName.Trim(),
            ContactName = string.IsNullOrWhiteSpace(clientName) ? null : clientName.Trim()
        };
    }

    private static string BuildClientDisplayName(string? locationName, string clientName)
    {
        var trimmedClient = clientName.Trim();
        var trimmedLocation = locationName?.Trim();

        if (string.IsNullOrWhiteSpace(trimmedLocation)
            || trimmedClient.Contains(trimmedLocation, StringComparison.OrdinalIgnoreCase))
        {
            return trimmedClient;
        }

        return $"{trimmedLocation} - {trimmedClient}";
    }

    private static string? ReadLocationName(JsonElement ticketElement)
    {
        var locationElement = TryGetObject(ticketElement, "location");
        if (locationElement.HasValue)
        {
            var location = locationElement.Value;
            return ReadString(location, "locationName")
                ?? ReadString(location, "displayName")
                ?? ReadString(location, "name")
                ?? ReadString(location, "clientName");
        }

        return ReadString(ticketElement, "locationName")
            ?? ReadString(ticketElement, "locationDisplayName");
    }

    private static JsonElement? TryGetObject(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    private static string? BuildName(JsonElement element)
    {
        var firstName = ReadString(element, "firstName");
        var lastName = ReadString(element, "lastName");
        var name = string.Join(" ", new[] { firstName, lastName }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string? ReadNestedString(JsonElement element, string objectName, string propertyName)
    {
        var nested = TryGetObject(element, objectName);
        return nested.HasValue ? ReadString(nested.Value, propertyName) : null;
    }

    private static int? ReadNestedInt(JsonElement element, string objectName, string propertyName)
    {
        var nested = TryGetObject(element, objectName);
        return nested.HasValue ? ReadInt(nested.Value, propertyName) : null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var boolean) && boolean,
            _ => false
        };
    }

    private static bool IsClosedStatus(string status)
    {
        return status.Trim().Equals("Closed", StringComparison.OrdinalIgnoreCase)
            || status.Contains("closed", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadResponseId(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var id = ReadString(document.RootElement, "id");
            return string.IsNullOrWhiteSpace(id) ? null : $"WHD-TECHNOTE-{id}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Uri BuildRequestUri(
        string baseUrl,
        string resource,
        WhdAuthParameters auth,
        IDictionary<string, string> additionalQuery)
    {
        var root = BuildApiRoot(baseUrl);
        var uriBuilder = new UriBuilder(new Uri($"{root.ToString().TrimEnd('/')}/{resource.TrimStart('/')}"));
        var query = new List<string>();

        foreach (var parameter in additionalQuery)
        {
            query.Add($"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}");
        }

        foreach (var parameter in auth.ToQueryParameters())
        {
            query.Add($"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}");
        }

        uriBuilder.Query = string.Join("&", query);
        return uriBuilder.Uri;
    }

    private static Uri BuildApiRoot(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var input) || string.IsNullOrWhiteSpace(input.Scheme))
        {
            throw new UriFormatException("Enter a full WHD HTTPS URL, including https://.");
        }

        if (!input.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new UriFormatException("Web Help Desk connections must use HTTPS so credentials are encrypted in transit.");
        }

        var path = input.AbsolutePath.TrimEnd('/');
        string apiPath;
        if (path.EndsWith("/ra", StringComparison.OrdinalIgnoreCase))
        {
            apiPath = path;
        }
        else if (path.EndsWith("/helpdesk", StringComparison.OrdinalIgnoreCase))
        {
            apiPath = $"{path}/WebObjects/Helpdesk.woa/ra";
        }
        else if (path.Contains("/WebObjects/", StringComparison.OrdinalIgnoreCase))
        {
            apiPath = $"{path}/ra";
        }
        else if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            apiPath = "/helpdesk/WebObjects/Helpdesk.woa/ra";
        }
        else
        {
            apiPath = $"{path}/helpdesk/WebObjects/Helpdesk.woa/ra";
        }

        var builder = new UriBuilder(input)
        {
            Path = apiPath,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri;
    }

    private static string? Validate(WhdConnectionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return "Enter the Web Help Desk base URL.";
        }

        if (!Uri.TryCreate(settings.BaseUrl.Trim(), UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "Enter an HTTPS Web Help Desk URL. HTTP is not allowed because WHD credentials are sent with API requests.";
        }

        if (settings.AuthenticationMode != WhdAuthenticationMode.TechnicianApiKey
            && string.IsNullOrWhiteSpace(settings.Username))
        {
            return "Enter the Web Help Desk username.";
        }

        if (string.IsNullOrWhiteSpace(settings.Secret))
        {
            return "Enter the Web Help Desk API key, token, or password.";
        }

        return null;
    }

    private static WhdAuthParameters? GetExplicitAuthentication(WhdConnectionSettings settings)
    {
        return settings.AuthenticationMode switch
        {
            WhdAuthenticationMode.UsernamePassword => WhdAuthParameters.UsernamePassword(settings.Username, settings.Secret),
            WhdAuthenticationMode.ApplicationApiKey => WhdAuthParameters.ApplicationApiKey(settings.Username, settings.Secret),
            WhdAuthenticationMode.TechnicianApiKey => WhdAuthParameters.TechApiKey(settings.Secret),
            _ => null
        };
    }

    private static string BuildAuthenticationCacheKey(WhdConnectionSettings settings)
    {
        var secretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.Secret)));
        return $"{settings.BaseUrl.Trim().TrimEnd('/')}|{settings.Username.Trim()}|{settings.AuthenticationMode}|{secretHash}";
    }

    private static string BuildPageSignature(IEnumerable<string> identifiers)
    {
        var joined = string.Join('\n', identifiers.Select(static identifier => identifier.Trim()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    private static string FormatWhdId(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("WHD-", StringComparison.OrdinalIgnoreCase) ? trimmed : $"WHD-{trimmed}";
    }

    private sealed class WhdAuthParameters
    {
        private readonly IReadOnlyDictionary<string, string> _parameters;

        private WhdAuthParameters(string displayName, IReadOnlyDictionary<string, string> parameters)
        {
            DisplayName = displayName;
            _parameters = parameters;
        }

        public string DisplayName { get; }

        public static WhdAuthParameters ApplicationApiKey(string username, string apiKey) => new(
            "username + application API key",
            new Dictionary<string, string>
            {
                ["username"] = username,
                ["apiKey"] = apiKey
            });

        public static WhdAuthParameters TechApiKey(string apiKey) => new(
            "tech API key",
            new Dictionary<string, string>
            {
                ["apiKey"] = apiKey
            });

        public static WhdAuthParameters UsernamePassword(string username, string password) => new(
            "username + password",
            new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = password
            });

        public IEnumerable<KeyValuePair<string, string>> ToQueryParameters() => _parameters;
    }
}
