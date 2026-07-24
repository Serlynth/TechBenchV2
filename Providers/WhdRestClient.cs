using System.Net;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Http.Json;
using System.Globalization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TechBench.Models;

namespace TechBench.Providers;

public sealed class WhdRestClient
{
    private const string ConfiguredOrganizationAccountExternalId = "WHD-CONFIGURED-ORGANIZATION-ACCOUNT";
    private const int PageSize = 100;
    private const int MaximumPageCount = 10_000;
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PostReconciliationWindow = TimeSpan.FromSeconds(20);
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, WhdAuthParameters> _authenticationCache = new(StringComparer.Ordinal);

    public WhdRestClient() : this(new HttpClient
    {
        Timeout = DefaultRequestTimeout
    })
    {
    }

    public WhdRestClient(HttpClient httpClient)
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
            await ResolveAuthenticationAsync(settings, cancellationToken, requireFreshProbe: true);
            return WhdSyncResult.Succeeded(
                $"Web Help Desk accepted the personal credentials for {settings.Username}. No tickets were downloaded or synchronized.",
                Array.Empty<WhdSyncedTicket>());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            return WhdSyncResult.Failed($"Web Help Desk test failed: {ex.Message}");
        }
    }

    public Task<WhdSyncResult> GetOrganizationTicketsAsync(
        WhdConnectionSettings settings,
        CancellationToken cancellationToken = default) =>
        GetOrganizationTicketsCoreAsync(settings, null, cancellationToken);

    public Task<WhdSyncResult> GetOrganizationTicketsChangedSinceAsync(
        WhdConnectionSettings settings,
        DateTimeOffset changedSinceUtc,
        CancellationToken cancellationToken = default) =>
        GetOrganizationTicketsCoreAsync(settings, changedSinceUtc, cancellationToken);

    private async Task<WhdSyncResult> GetOrganizationTicketsCoreAsync(
        WhdConnectionSettings settings,
        DateTimeOffset? changedSinceUtc,
        CancellationToken cancellationToken)
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

            for (var page = 1; page <= MaximumPageCount; page++)
            {
                var batch = await GetOrganizationTicketsPageAsync(
                    settings,
                    auth,
                    changedSinceUtc,
                    page,
                    PageSize,
                    cancellationToken);
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
                $"Read {openTicketCount} non-closed organization Web Help Desk ticket(s)"
                + (closedTicketCount > 0 ? $" and updated {closedTicketCount} closed ticket(s)." : ".")
                + (changedSinceUtc.HasValue ? $" Changes since {changedSinceUtc.Value:O} were requested." : string.Empty)
                + (isComplete ? string.Empty : " Paging stopped because WHD repeated a page; returned tickets were still updated."),
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

            for (var page = 1; page <= MaximumPageCount; page++)
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

    public async Task<WhdTechnicianSyncResult> GetTechniciansAsync(
        WhdConnectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(settings);
        if (validationError is not null)
        {
            return WhdTechnicianSyncResult.Failed(validationError);
        }

        try
        {
            var auth = await ResolveAuthenticationAsync(settings, cancellationToken);
            var technicians = new List<WhdSyncedTechnician>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            var isComplete = false;

            for (var page = 1; page <= MaximumPageCount; page++)
            {
                var batch = await GetTechniciansPageAsync(settings, auth, page, PageSize, cancellationToken);
                if (batch.Count == 0)
                {
                    isComplete = true;
                    break;
                }

                var signature = BuildPageSignature(batch.Select(static item => item.ExternalId));
                if (!signatures.Add(signature))
                {
                    break;
                }

                foreach (var technician in batch)
                {
                    if (seen.Add(technician.ExternalId))
                    {
                        technicians.Add(technician);
                    }
                }

                if (batch.Count < PageSize)
                {
                    isComplete = true;
                    break;
                }
            }

            // WHD 12.x can omit the authenticated administrator from the
            // Techs collection. The documented currentTech resource requires
            // a temporary session key, even when the other API calls use an
            // application API key. Create that short-lived session only when
            // the configured organization account is absent from the list.
            if (!technicians.Any(technician =>
                    string.Equals(
                        technician.Username,
                        settings.Username,
                        StringComparison.OrdinalIgnoreCase)))
            {
                var currentTechnician = await TryGetAuthenticatedTechnicianAsync(
                    settings,
                    auth,
                    cancellationToken).ConfigureAwait(false);
                if (currentTechnician is not null && seen.Add(currentTechnician.ExternalId))
                {
                    technicians.Add(currentTechnician);
                }
            }

            // An application API key authenticates the integration, not a
            // technician session. WHD 12.x therefore omits its built-in
            // Helpdesk Manager account from both /Techs and currentTech even
            // though the configured username is active and valid. Preserve a
            // stable organization-account mapping choice when WHD exposes no
            // numeric technician identity. This is intentionally distinct
            // from a normal WHD-TECH-* assignment and is used to identify the
            // shared organization account in TechBench administration.
            if (!technicians.Any(technician =>
                    string.Equals(
                        technician.Username,
                        settings.Username,
                        StringComparison.OrdinalIgnoreCase)))
            {
                technicians.Add(new WhdSyncedTechnician
                {
                    ExternalId = ConfiguredOrganizationAccountExternalId,
                    DisplayName = "Helpdesk Manager (organization-wide account)",
                    Username = settings.Username.Trim(),
                    IsActive = true
                });
            }

            return WhdTechnicianSyncResult.Succeeded(
                $"Read {technicians.Count} Web Help Desk technician(s).",
                technicians,
                isComplete);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            return WhdTechnicianSyncResult.Failed($"Web Help Desk technician sync failed: {ex.Message}");
        }
    }

    public async Task<WhdTechnicianGroupSyncResult> GetTechnicianGroupsAsync(
        WhdConnectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(settings);
        if (validationError is not null)
        {
            return WhdTechnicianGroupSyncResult.Failed(validationError);
        }

        try
        {
            var auth = await ResolveAuthenticationAsync(settings, cancellationToken);
            try
            {
                return await GetTechnicianGroupsFromEndpointAsync(
                    settings,
                    auth,
                    cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is
                HttpStatusCode.NotFound or
                HttpStatusCode.BadRequest or
                HttpStatusCode.MethodNotAllowed)
            {
                // TechGroups is available on some WHD releases but is not a
                // documented endpoint on all supported versions. Long Tech
                // records commonly carry their group membership, so use that
                // representation when the dedicated resource is unavailable.
                return await GetTechnicianGroupsFromTechRecordsAsync(
                    settings,
                    auth,
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            return WhdTechnicianGroupSyncResult.Failed($"Web Help Desk technician-group sync failed: {ex.Message}");
        }
    }

    public async Task<PostingResult> PostTicketNoteAsync(
        WhdConnectionSettings settings,
        int ticketId,
        string noteText,
        int durationMinutes,
        DateTime noteDateUtc,
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

        var payload = BuildTicketNotePayload(ticketId, noteText, durationMinutes, noteDateUtc);

        var postStarted = false;
        WhdAuthParameters? auth = null;
        try
        {
            auth = await ResolveAuthenticationAsync(settings, cancellationToken);
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
            if (postStarted
                && auth is not null
                && !cancellationToken.IsCancellationRequested)
            {
                var reconciledReference = await TryReconcileUnconfirmedTicketNoteAsync(
                    settings,
                    auth,
                    ticketId,
                    noteText,
                    durationMinutes,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(reconciledReference))
                {
                    return PostingResult.Succeeded(
                        "Web Help Desk did not return a usable POST response, but TechBench found and verified the saved Sage/WHD Note.",
                        payload,
                        reconciledReference);
                }
            }

            return postStarted
                ? PostingResult.Uncertain(
                    $"Web Help Desk did not return a confirmable result after the note request began, and TechBench could not find the exact note during automatic verification: {ex.Message} Verify WHD before retrying.",
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
            for (var page = 1; page <= MaximumPageCount; page++)
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
        DateTime? noteDateUtc = null,
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
            new
            {
                noteText = noteText.Trim(),
                date = noteDateUtc.HasValue
                    ? FormatWhdDate(noteDateUtc.Value)
                    : null
            },
            new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
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

    private async Task<WhdAuthParameters> ResolveAuthenticationAsync(
        WhdConnectionSettings settings,
        CancellationToken cancellationToken,
        bool requireFreshProbe = false)
    {
        var explicitAuthentication = GetExplicitAuthentication(settings);
        if (explicitAuthentication is not null)
        {
            if (requireFreshProbe)
            {
                await ProbeAuthenticationAsync(settings, explicitAuthentication, cancellationToken);
            }

            return explicitAuthentication;
        }

        var cacheKey = BuildAuthenticationCacheKey(settings);
        if (_authenticationCache.TryGetValue(cacheKey, out var cached))
        {
            if (requireFreshProbe)
            {
                await ProbeAuthenticationAsync(settings, cached, cancellationToken);
            }

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
                await ProbeAuthenticationAsync(settings, candidate, cancellationToken);
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

    private async Task ProbeAuthenticationAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(
            settings.BaseUrl,
            "Session",
            auth,
            new Dictionary<string, string>());
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase : content.Trim();
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} from Web Help Desk authentication: {message}",
                null,
                response.StatusCode);
        }

        string? sessionKey;
        try
        {
            using var document = JsonDocument.Parse(content);
            sessionKey = ReadString(document.RootElement, "sessionKey");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Web Help Desk authenticated the request but did not return a valid Session response.");
        }

        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            throw new InvalidOperationException("Web Help Desk authenticated the request but did not return a temporary session key.");
        }

        await TryTerminateProbeSessionAsync(settings, sessionKey, cancellationToken);
    }

    private async Task TryTerminateProbeSessionAsync(
        WhdConnectionSettings settings,
        string sessionKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var requestUri = BuildRequestUri(
                settings.BaseUrl,
                "Session",
                WhdAuthParameters.SessionKey(sessionKey),
                new Dictionary<string, string>());
            using var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            // Authentication already succeeded; WHD will expire an unclosed probe session.
        }
    }

    private Task<IReadOnlyList<WhdSyncedTicket>> GetOrganizationTicketsPageAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        DateTimeOffset? changedSinceUtc,
        int page,
        int limit,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(settings.BaseUrl, "Tickets", auth, new Dictionary<string, string>
        {
            ["qualifier"] = BuildOrganizationTicketQualifier(changedSinceUtc),
            ["style"] = "long",
            ["withUTC"] = "true",
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            ["page"] = page.ToString(CultureInfo.InvariantCulture)
        });

        return GetTicketsPageAsync(requestUri, cancellationToken);
    }

    private static string BuildOrganizationTicketQualifier(DateTimeOffset? changedSinceUtc)
    {
        const string includeExplicitDeletionState =
            "((deleted = null) or (deleted = 0) or (deleted = 1))";
        if (!changedSinceUtc.HasValue)
        {
            return includeExplicitDeletionState;
        }

        // WHD qualifiers use EOQualifier syntax. Always format the server cursor
        // in invariant UTC and let URI construction escape it as one value.
        var timestamp = changedSinceUtc.Value.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture);
        return $"({includeExplicitDeletionState} and (lastUpdated >= '{timestamp}'))";
    }

    private async Task<IReadOnlyList<WhdSyncedTicket>> GetTicketsPageAsync(
        Uri requestUri,
        CancellationToken cancellationToken)
    {
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

    private async Task<IReadOnlyList<WhdSyncedTechnician>> GetTechniciansPageAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        int page,
        int limit,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(settings.BaseUrl, "Techs", auth, new Dictionary<string, string>
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
                $"HTTP {(int)response.StatusCode} from Web Help Desk technicians: {message}",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(content);
        return ParseTechnicians(document.RootElement);
    }

    private async Task<WhdSyncedTechnician?> TryGetAuthenticatedTechnicianAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        CancellationToken cancellationToken)
    {
        string? sessionKey = null;
        try
        {
            var sessionUri = BuildRequestUri(
                settings.BaseUrl,
                "Session",
                auth,
                new Dictionary<string, string>());
            using var sessionResponse = await _httpClient.GetAsync(sessionUri, cancellationToken);
            if (!sessionResponse.IsSuccessStatusCode)
            {
                return await TryGetConfiguredTechnicianDirectlyAsync(
                    settings,
                    auth,
                    cancellationToken).ConfigureAwait(false);
            }

            var sessionContent = await sessionResponse.Content.ReadAsStringAsync(cancellationToken);
            using var sessionDocument = JsonDocument.Parse(sessionContent);
            sessionKey = ReadSessionString(
                sessionDocument.RootElement,
                "sessionKey",
                "key");
            var instanceId = NormalizeSessionInstanceId(ReadSessionString(
                sessionDocument.RootElement,
                "instanceId"));

            var embeddedTechnician = ParseSessionTechnician(
                sessionDocument.RootElement,
                settings.Username);
            if (embeddedTechnician is not null)
            {
                return embeddedTechnician;
            }

            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                return await TryGetConfiguredTechnicianDirectlyAsync(
                    settings,
                    auth,
                    cancellationToken).ConfigureAwait(false);
            }

            var sessionAuthentication = WhdAuthParameters.SessionKey(sessionKey);
            var currentTechnicianId = ReadSessionString(
                sessionDocument.RootElement,
                "currentTechId",
                "techId",
                "technicianId");
            WhdSyncedTechnician? currentTechnician = null;
            if (!string.IsNullOrWhiteSpace(currentTechnicianId))
            {
                currentTechnician = await TryGetTechnicianAsync(
                    settings,
                    sessionAuthentication,
                    $"Techs/{currentTechnicianId.Trim()}",
                    cancellationToken,
                    instanceId).ConfigureAwait(false);
                if (currentTechnician is not null)
                {
                    return currentTechnician;
                }
            }

            currentTechnician = await TryGetTechnicianAsync(
                settings,
                sessionAuthentication,
                "Techs/currentTech",
                cancellationToken,
                instanceId).ConfigureAwait(false);
            if (currentTechnician is not null)
            {
                return currentTechnician;
            }

            // Some WHD builds return currentTechId in the authenticated
            // Session but deny both technician-detail routes for an Admin
            // account. The session ID is still the authoritative technician
            // identity used by ticket assignments, so retain it with the
            // configured organization username instead of silently dropping
            // the active account from the mapping roster.
            return string.IsNullOrWhiteSpace(currentTechnicianId)
                ? await TryGetConfiguredTechnicianDirectlyAsync(
                    settings,
                    auth,
                    cancellationToken).ConfigureAwait(false)
                : new WhdSyncedTechnician
                {
                    ExternalId = FormatWhdTechnicianId(currentTechnicianId),
                    DisplayName = settings.Username.Trim(),
                    Username = settings.Username.Trim(),
                    IsActive = true
                };
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(sessionKey))
            {
                await TryTerminateProbeSessionAsync(
                    settings,
                    sessionKey,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<WhdSyncedTechnician?> TryGetConfiguredTechnicianDirectlyAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        CancellationToken cancellationToken)
    {
        var currentTechnician = await TryGetTechnicianAsync(
            settings,
            auth,
            "Techs/currentTech",
            cancellationToken).ConfigureAwait(false);
        if (currentTechnician is not null)
        {
            return currentTechnician;
        }

        // WHD 12.x installations differ on whether the single-Tech resource
        // accepts a login name as its path identifier. It is safe to probe:
        // unsupported builds return a normal 400/404 and the ticket snapshot
        // still recovers assigned administrators omitted from the Techs list.
        return await TryGetTechnicianAsync(
            settings,
            auth,
            $"Techs/{Uri.EscapeDataString(settings.Username.Trim())}",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WhdSyncedTechnician?> TryGetTechnicianAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        string resource,
        CancellationToken cancellationToken,
        string? instanceId = null)
    {
        var requestUri = BuildRequestUri(settings.BaseUrl, resource, auth, new Dictionary<string, string>
        {
            ["style"] = "long"
        }, instanceId);
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(content);
        return ParseTechnicians(document.RootElement).FirstOrDefault();
    }

    private async Task<IReadOnlyList<WhdSyncedTechnicianGroup>> GetTechnicianGroupsPageAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        int page,
        int limit,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(settings.BaseUrl, "TechGroups", auth, new Dictionary<string, string>
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
                $"HTTP {(int)response.StatusCode} from Web Help Desk technician groups: {message}",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(content);
        return ParseTechnicianGroups(document.RootElement);
    }

    private async Task<WhdTechnicianGroupSyncResult> GetTechnicianGroupsFromEndpointAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, WhdSyncedTechnicianGroup>(StringComparer.OrdinalIgnoreCase);
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var isComplete = false;
        for (var page = 1; page <= MaximumPageCount; page++)
        {
            var batch = await GetTechnicianGroupsPageAsync(
                settings,
                auth,
                page,
                PageSize,
                cancellationToken);
            if (batch.Count == 0)
            {
                isComplete = true;
                break;
            }

            var signature = BuildPageSignature(batch.Select(static item =>
                item.ExternalId + ":" + string.Join(",", item.TechnicianExternalIds)));
            if (!signatures.Add(signature))
            {
                break;
            }

            MergeTechnicianGroups(groups, batch);
            if (batch.Count < PageSize)
            {
                isComplete = true;
                break;
            }
        }

        return WhdTechnicianGroupSyncResult.Succeeded(
            $"Read {groups.Count} Web Help Desk technician group(s).",
            groups.Values.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            isComplete);
    }

    private async Task<WhdTechnicianGroupSyncResult> GetTechnicianGroupsFromTechRecordsAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, WhdSyncedTechnicianGroup>(StringComparer.OrdinalIgnoreCase);
        var pageSignatures = new HashSet<string>(StringComparer.Ordinal);
        var isComplete = false;
        for (var page = 1; page <= MaximumPageCount; page++)
        {
            var requestUri = BuildRequestUri(settings.BaseUrl, "Techs", auth, new Dictionary<string, string>
            {
                ["style"] = "long",
                ["limit"] = PageSize.ToString(CultureInfo.InvariantCulture),
                ["page"] = page.ToString(CultureInfo.InvariantCulture)
            });
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"HTTP {(int)response.StatusCode} from Web Help Desk technician membership data: "
                    + (string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase : content.Trim()),
                    null,
                    response.StatusCode);
            }

            using var document = JsonDocument.Parse(content);
            var records = EnumerateRecords(document.RootElement).ToList();
            if (records.Count == 0)
            {
                isComplete = true;
                break;
            }

            var signature = BuildPageSignature(records.Select(static item =>
                ReadStringAny(item, "id", "techId", "technicianId") ?? string.Empty));
            if (!pageSignatures.Add(signature))
            {
                break;
            }

            MergeTechnicianGroups(groups, ParseTechnicianGroupsFromTechRecords(records));
            if (records.Count < PageSize)
            {
                isComplete = true;
                break;
            }
        }

        return WhdTechnicianGroupSyncResult.Succeeded(
            $"Read {groups.Count} Web Help Desk technician group(s) from technician membership data.",
            groups.Values.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            isComplete);
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

    private async Task<string?> TryReconcileUnconfirmedTicketNoteAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        int ticketId,
        string expectedNote,
        int expectedDurationMinutes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var verificationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            verificationTimeout.CancelAfter(PostReconciliationWindow);
            return await FindPostedTicketNoteAsync(
                settings,
                auth,
                ticketId,
                expectedNote,
                expectedDurationMinutes,
                verificationTimeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                       or TaskCanceledException
                                       or JsonException
                                       or InvalidOperationException
                                       or UriFormatException)
        {
            return null;
        }
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

    private static string BuildTicketNotePayload(
        int ticketId,
        string noteText,
        int durationMinutes,
        DateTime noteDateUtc)
    {
        var payload = new
        {
            noteText = noteText.Trim(),
            date = FormatWhdDate(noteDateUtc),
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

    private static string FormatWhdDate(DateTime noteDateUtc) =>
        noteDateUtc
            .ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

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

    private static IReadOnlyList<WhdSyncedTechnician> ParseTechnicians(JsonElement root)
    {
        var technicians = new List<WhdSyncedTechnician>();
        foreach (var element in EnumerateRecords(root))
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadStringAny(element, "id", "techId", "technicianId");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var username = ReadStringAny(element, "username", "userName", "loginName");
            var email = ReadStringAny(element, "email", "emailAddress");
            var displayName = ReadStringAny(element, "displayName", "fullName", "name")
                ?? BuildName(element)
                ?? username
                ?? email
                ?? $"Technician {id}";
            technicians.Add(new WhdSyncedTechnician
            {
                ExternalId = FormatWhdTechnicianId(id),
                DisplayName = displayName.Trim(),
                Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
                Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
                IsActive = !ReadBooleanAny(element, "deleted", "inactive", "isInactive", "disabled")
            });
        }

        return technicians;
    }

    private static IReadOnlyList<WhdSyncedTechnicianGroup> ParseTechnicianGroups(JsonElement root)
    {
        var groups = new List<WhdSyncedTechnicianGroup>();
        foreach (var element in EnumerateRecords(root))
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadStringAny(element, "id", "techGroupId", "groupId");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var name = ReadStringAny(element, "techGroupName", "groupName", "displayName", "name")
                ?? $"Technician group {id}";
            groups.Add(new WhdSyncedTechnicianGroup
            {
                ExternalId = FormatWhdGroupId(id),
                Name = name.Trim(),
                IsActive = !ReadBooleanAny(element, "deleted", "inactive", "isInactive", "disabled"),
                TechnicianExternalIds = ReadTechnicianMemberIds(element)
            });
        }

        return groups;
    }

    private static IReadOnlyList<WhdSyncedTechnicianGroup> ParseTechnicianGroupsFromTechRecords(
        IReadOnlyList<JsonElement> technicians)
    {
        var groups = new Dictionary<string, (string Name, bool IsActive, HashSet<string> Members)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var technician in technicians)
        {
            var technicianId = ReadStringAny(technician, "id", "techId", "technicianId");
            if (string.IsNullOrWhiteSpace(technicianId))
            {
                continue;
            }

            var formattedTechnicianId = FormatWhdTechnicianId(technicianId);
            foreach (var groupElement in EnumerateGroupObjects(technician))
            {
                var groupId = ReadStringAny(groupElement, "id", "techGroupId", "groupId");
                if (string.IsNullOrWhiteSpace(groupId))
                {
                    continue;
                }

                var formattedGroupId = FormatWhdGroupId(groupId);
                var groupName = ReadStringAny(
                        groupElement,
                        "techGroupName",
                        "groupName",
                        "displayName",
                        "name")
                    ?? $"Technician group {groupId}";
                if (!groups.TryGetValue(formattedGroupId, out var group))
                {
                    group = (
                        groupName.Trim(),
                        !ReadBooleanAny(groupElement, "deleted", "inactive", "isInactive", "disabled"),
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }

                group.Members.Add(formattedTechnicianId);
                groups[formattedGroupId] = group;
            }
        }

        return groups.Select(static pair => new WhdSyncedTechnicianGroup
        {
            ExternalId = pair.Key,
            Name = pair.Value.Name,
            IsActive = pair.Value.IsActive,
            TechnicianExternalIds = pair.Value.Members.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToList()
        }).ToList();
    }

    private static IEnumerable<JsonElement> EnumerateGroupObjects(JsonElement technician)
    {
        foreach (var propertyName in new[] { "techGroups", "technicianGroups", "groups" })
        {
            if (!technician.TryGetProperty(propertyName, out var groups)
                || groups.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind == JsonValueKind.Object)
                {
                    yield return group;
                }
            }
        }

        if (technician.TryGetProperty("techGroupLevels", out var levels)
            && levels.ValueKind == JsonValueKind.Array)
        {
            foreach (var level in levels.EnumerateArray())
            {
                var group = TryGetObject(level, "techGroup") ?? TryGetObject(level, "group");
                if (group.HasValue)
                {
                    yield return group.Value;
                }
            }
        }
    }

    private static void MergeTechnicianGroups(
        IDictionary<string, WhdSyncedTechnicianGroup> target,
        IEnumerable<WhdSyncedTechnicianGroup> source)
    {
        foreach (var group in source)
        {
            if (!target.TryGetValue(group.ExternalId, out var existing))
            {
                target[group.ExternalId] = group;
                continue;
            }

            target[group.ExternalId] = new WhdSyncedTechnicianGroup
            {
                ExternalId = existing.ExternalId,
                Name = string.IsNullOrWhiteSpace(group.Name) ? existing.Name : group.Name,
                IsActive = existing.IsActive || group.IsActive,
                TechnicianExternalIds = existing.TechnicianExternalIds
                    .Concat(group.TechnicianExternalIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }
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
            ExternalId = FormatWhdLocationId(id),
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
            IsClosed = ReadBooleanAny(
                    statusTypeElement,
                    "closed",
                    "isClosed",
                    "terminal",
                    "isTerminal")
                || IsClosedStatus(name)
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

        var isDeleted = ReadBooleanAny(ticketElement, "deleted", "isDeleted");
        var isClosed = ReadBooleanAny(
                ticketElement,
                "closed",
                "isClosed",
                "canceled",
                "cancelled",
                "isCanceled",
                "isCancelled")
            || IsClosedStatus(status)
            || isDeleted;
        var assignedTechnician = TryGetObjectAny(
            ticketElement,
            "assignedTech",
            "assignedTechnician",
            "tech",
            "technician",
            "techAssigned");
        var assignedGroup = TryGetObjectAny(
            ticketElement,
            "techGroup",
            "assignedTechGroup",
            "assignedGroup",
            "group")
            ?? TryGetNestedObject(ticketElement, "requestType", "techGroup");
        var assignedTechnicianId = assignedTechnician.HasValue
            ? ReadStringAny(assignedTechnician.Value, "id", "techId", "technicianId")
            : ReadStringAny(ticketElement, "assignedTechId", "assignedTechnicianId", "techId");
        var assignedGroupId = assignedGroup.HasValue
            ? ReadStringAny(assignedGroup.Value, "id", "techGroupId", "groupId")
            : ReadStringAny(ticketElement, "techGroupId", "assignedGroupId");

        tickets.Add(new WhdSyncedTicket
        {
            ExternalId = FormatWhdId(id),
            TicketNumber = FormatWhdId(id),
            Subject = ReadString(ticketElement, "subject") ?? "Web Help Desk ticket",
            Status = status,
            StatusTypeId = statusTypeId,
            IsClosed = isClosed,
            IsDeleted = isDeleted,
            LastUpdatedUtc = ReadDateTimeOffsetAny(
                ticketElement,
                "lastUpdatedUtc",
                "lastUpdated",
                "lastUpdatedDate",
                "updatedAt",
                "dateModified",
                "modified"),
            AssignedTechnicianExternalId = string.IsNullOrWhiteSpace(assignedTechnicianId)
                ? null
                : FormatWhdTechnicianId(assignedTechnicianId),
            AssignedTechnicianName = assignedTechnician.HasValue
                ? ReadStringAny(assignedTechnician.Value, "displayName", "fullName", "name", "username")
                : ReadStringAny(ticketElement, "assignedTechName", "assignedTechnicianName"),
            AssignedGroupExternalId = string.IsNullOrWhiteSpace(assignedGroupId)
                ? null
                : FormatWhdGroupId(assignedGroupId),
            AssignedGroupName = assignedGroup.HasValue
                ? ReadStringAny(assignedGroup.Value, "techGroupName", "groupName", "displayName", "name")
                : ReadStringAny(ticketElement, "techGroupName", "assignedGroupName"),
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
        var clientId = ReadString(element, "id")
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

        var locationElement = TryGetObject(ticketElement, "location");
        var locationId = locationElement.HasValue
            ? ReadStringAny(locationElement.Value, "id", "locationId")
            : ReadStringAny(ticketElement, "locationId");
        var locationName = ReadLocationName(ticketElement);
        var hasLocationIdentity = !string.IsNullOrWhiteSpace(locationId);
        var name = hasLocationIdentity && !string.IsNullOrWhiteSpace(locationName)
            ? locationName.Trim()
            : BuildClientDisplayName(locationName, clientName);

        return new WhdSyncedClient
        {
            // Location is TechBench's customer boundary. Using the reporter's
            // WHD client ID here would split one customer into a client row per
            // contact and bypass the shared WHD-to-Sage customer mapping.
            ExternalId = hasLocationIdentity
                ? FormatWhdLocationId(locationId!)
                : FormatWhdId(clientId),
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

    private static JsonElement? TryGetObjectAny(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = TryGetObject(element, propertyName);
            if (value.HasValue)
            {
                return value;
            }
        }

        return null;
    }

    private static JsonElement? TryGetNestedObject(
        JsonElement element,
        string objectName,
        string nestedObjectName)
    {
        var parent = TryGetObject(element, objectName);
        return parent.HasValue ? TryGetObject(parent.Value, nestedObjectName) : null;
    }

    private static string? ReadStringAny(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = ReadString(element, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ReadSessionString(JsonElement root, params string[] propertyNames)
    {
        var direct = ReadStringAny(root, propertyNames);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        foreach (var containerName in new[]
                 {
                     "session",
                     "currentTech",
                     "currentTechnician",
                     "tech",
                     "technician",
                     "user"
                 })
        {
            var container = TryGetObject(root, containerName);
            if (!container.HasValue)
            {
                continue;
            }

            var nested = ReadStringAny(container.Value, propertyNames);
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }

        return null;
    }

    private static WhdSyncedTechnician? ParseSessionTechnician(
        JsonElement root,
        string configuredUsername)
    {
        foreach (var containerName in new[]
                 {
                     "currentTech",
                     "currentTechnician",
                     "tech",
                     "technician"
                 })
        {
            var container = TryGetObject(root, containerName);
            if (!container.HasValue)
            {
                var session = TryGetObject(root, "session");
                container = session.HasValue
                    ? TryGetObject(session.Value, containerName)
                    : null;
            }

            if (!container.HasValue)
            {
                continue;
            }

            var technician = ParseTechnicians(container.Value).FirstOrDefault();
            if (technician is not null)
            {
                return technician;
            }

            var id = ReadStringAny(container.Value, "id", "currentTechId", "techId", "technicianId");
            if (!string.IsNullOrWhiteSpace(id))
            {
                return new WhdSyncedTechnician
                {
                    ExternalId = FormatWhdTechnicianId(id),
                    DisplayName = BuildName(container.Value)
                        ?? ReadStringAny(container.Value, "displayName", "fullName", "name")
                        ?? configuredUsername.Trim(),
                    Username = ReadStringAny(container.Value, "username", "userName", "loginName")
                        ?? configuredUsername.Trim(),
                    Email = ReadStringAny(container.Value, "email", "emailAddress"),
                    IsActive = !ReadBooleanAny(
                        container.Value,
                        "deleted",
                        "inactive",
                        "isInactive",
                        "disabled")
                };
            }
        }

        return null;
    }

    private static bool ReadBooleanAny(JsonElement element, params string[] propertyNames) =>
        propertyNames.Any(propertyName => ReadBoolean(element, propertyName));

    private static DateTimeOffset? ReadDateTimeOffsetAny(
        JsonElement element,
        params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var epoch))
            {
                try
                {
                    return epoch > 10_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                        : DateTimeOffset.FromUnixTimeSeconds(epoch);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Continue through aliases when a WHD extension returns an invalid value.
                }
            }

            var text = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var dotNetJsonStart = text.IndexOf("/Date(", StringComparison.OrdinalIgnoreCase);
            if (dotNetJsonStart >= 0)
            {
                var digits = new string(text.Skip(dotNetJsonStart + 6)
                    .TakeWhile(character => char.IsDigit(character) || character == '-')
                    .ToArray());
                if (long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
                {
                    try
                    {
                        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // Fall through to the normal date parser.
                    }
                }
            }

            if (DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ReadTechnicianMemberIds(JsonElement groupElement)
    {
        foreach (var propertyName in new[] { "techs", "technicians", "members", "techMembers" })
        {
            if (!groupElement.TryGetProperty(propertyName, out var members)
                || members.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in members.EnumerateArray())
            {
                var id = member.ValueKind switch
                {
                    JsonValueKind.Object => ReadStringAny(member, "id", "techId", "technicianId"),
                    JsonValueKind.String => member.GetString(),
                    JsonValueKind.Number => member.ToString(),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(id))
                {
                    identifiers.Add(FormatWhdTechnicianId(id));
                }
            }

            return identifiers.OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        return Array.Empty<string>();
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
            || status.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || status.Trim().Equals("Canceled", StringComparison.OrdinalIgnoreCase)
            || status.Trim().Equals("Cancelled", StringComparison.OrdinalIgnoreCase);
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
        IDictionary<string, string> additionalQuery,
        string? instanceId = null)
    {
        var root = BuildApiRoot(baseUrl, instanceId);
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

    private static Uri BuildApiRoot(string baseUrl, string? instanceId = null)
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

        if (!string.IsNullOrWhiteSpace(instanceId) &&
            apiPath.EndsWith("/ra", StringComparison.OrdinalIgnoreCase))
        {
            apiPath = $"{apiPath[..^3]}/{instanceId}/ra";
        }

        var builder = new UriBuilder(input)
        {
            Path = apiPath,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri;
    }

    private static string? NormalizeSessionInstanceId(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var instanceId)
        && instanceId >= 0
            ? instanceId.ToString(CultureInfo.InvariantCulture)
            : null;

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

    private static string FormatWhdLocationId(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("WHD-LOCATION-", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"WHD-LOCATION-{trimmed}";
    }

    private static string FormatWhdTechnicianId(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("WHD-TECH-", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"WHD-TECH-{trimmed}";
    }

    private static string FormatWhdGroupId(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("WHD-GROUP-", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"WHD-GROUP-{trimmed}";
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

        public static WhdAuthParameters SessionKey(string sessionKey) => new(
            "temporary session key",
            new Dictionary<string, string>
            {
                ["sessionKey"] = sessionKey
            });

        public IEnumerable<KeyValuePair<string, string>> ToQueryParameters() => _parameters;
    }
}
