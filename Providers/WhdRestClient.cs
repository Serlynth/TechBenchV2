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
using System.Text.RegularExpressions;
using TechBench.Models;

namespace TechBench.Providers;

public sealed class WhdRestClient
{
    private const string ConfiguredOrganizationAccountExternalId = "WHD-CONFIGURED-ORGANIZATION-ACCOUNT";
    private const int PageSize = 100;
    private const int MaximumPageCount = 10_000;
    private const int MaximumConcurrentClientDetailRequests = 6;
    private const int MaximumConcurrentTechnicianDetailRequests = 6;
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan OptionalClientDetailTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PostReconciliationWindow = TimeSpan.FromSeconds(20);
    private readonly HttpClient _httpClient;
    private readonly CookieContainer? _cookieContainer;
    private readonly SemaphoreSlim _attachmentUploadGate = new(1, 1);
    private readonly ConcurrentDictionary<string, WhdAuthParameters> _authenticationCache = new(StringComparer.Ordinal);

    public WhdRestClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        _cookieContainer = handler.CookieContainer;
        _httpClient = new HttpClient(handler)
        {
            Timeout = DefaultRequestTimeout
        };
    }

    public WhdRestClient(HttpClient httpClient) : this(httpClient, null)
    {
    }

    internal WhdRestClient(HttpClient httpClient, CookieContainer? cookieContainer)
    {
        _httpClient = httpClient;
        _cookieContainer = cookieContainer;
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
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WhdSyncResult.Failed(
                $"Web Help Desk sync timed out after {_httpClient.Timeout.TotalSeconds:0} seconds while reading ticket data.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or UriFormatException)
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

            var contactResult = await GetBestLocationContactsAsync(
                settings,
                auth,
                cancellationToken);
            clients = clients
                .Select(location => EnrichLocation(location, contactResult.Contacts))
                .ToList();

            return WhdClientSyncResult.Succeeded(
                $"Synced {clients.Count} active Web Help Desk location(s)."
                + $" Added contact details for {clients.Count(client => !string.IsNullOrWhiteSpace(client.ContactName))} location(s)."
                + (contactResult.UnavailableClientIds.Count == 0
                    ? string.Empty
                    : $" WHD could not return optional details for client(s) {string.Join(", ", contactResult.UnavailableClientIds)}; list data was retained.")
                + (isComplete ? string.Empty : " Paging stopped because WHD repeated a page; stale-client reconciliation was skipped."),
                clients,
                isComplete);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WhdClientSyncResult.Failed(
                $"Web Help Desk client sync timed out after {_httpClient.Timeout.TotalSeconds:0} seconds while reading required list data.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or UriFormatException)
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

            technicians = (await EnrichTechnicianDetailsAsync(
                    settings,
                    auth,
                    technicians,
                    cancellationToken)
                .ConfigureAwait(false))
                .ToList();

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
                    DisplayName = $"Helpdesk Manager ({settings.Username.Trim()}, organization-wide account)",
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

    public async Task<WhdAttachmentUploadResult> UploadTechNoteImagesAsync(
        WhdConnectionSettings settings,
        int techNoteId,
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(settings);
        if (validationError is not null)
        {
            return WhdAttachmentUploadResult.Failed(validationError, filePaths);
        }

        if (techNoteId <= 0)
        {
            return WhdAttachmentUploadResult.Failed(
                "A verified WHD TechNote ID is required before images can be attached.",
                filePaths);
        }

        var requestedPaths = filePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requestedPaths.Length == 0)
        {
            return WhdAttachmentUploadResult.Failed("Select at least one image to attach.", []);
        }

        var uploadPaths = new List<string>();
        var failures = new List<WhdAttachmentUploadFailure>();
        foreach (var filePath in requestedPaths)
        {
            if (!WhdImageAttachmentPolicy.IsSupported(filePath))
            {
                failures.Add(new WhdAttachmentUploadFailure(
                    filePath,
                    "The selected file is not a supported image type."));
            }
            else if (!File.Exists(filePath))
            {
                failures.Add(new WhdAttachmentUploadFailure(
                    filePath,
                    "The selected image is no longer available."));
            }
            else
            {
                uploadPaths.Add(filePath);
            }
        }

        var uploadedPaths = new List<string>();
        if (uploadPaths.Count > 0)
        {
            await _attachmentUploadGate.WaitAsync(cancellationToken);
            try
            {
                WhdUploadSession? session = null;
                try
                {
                    var auth = await ResolveAuthenticationAsync(settings, cancellationToken);
                    session = await CreateUploadSessionAsync(settings, auth, cancellationToken);
                    foreach (var filePath in uploadPaths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var upload = await UploadTechNoteImageAsync(
                            settings,
                            session,
                            techNoteId,
                            filePath,
                            cancellationToken);
                        if (upload.Success)
                        {
                            uploadedPaths.Add(filePath);
                        }
                        else
                        {
                            failures.Add(new WhdAttachmentUploadFailure(filePath, upload.Message));
                        }
                    }
                }
                catch (Exception ex) when (
                    ex is HttpRequestException
                        or TaskCanceledException
                        or JsonException
                        or InvalidOperationException
                        or UriFormatException
                        or IOException
                        or UnauthorizedAccessException)
                {
                    foreach (var filePath in uploadPaths.Except(uploadedPaths, StringComparer.OrdinalIgnoreCase))
                    {
                        if (failures.All(failure =>
                                !failure.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
                        {
                            failures.Add(new WhdAttachmentUploadFailure(filePath, ex.Message));
                        }
                    }
                }
                finally
                {
                    if (session is not null)
                    {
                        try
                        {
                            await TryTerminateProbeSessionAsync(
                                settings,
                                session.SessionKey,
                                CancellationToken.None);
                        }
                        finally
                        {
                            ClearUploadSessionCookie(settings, session);
                        }
                    }
                }
            }
            finally
            {
                _attachmentUploadGate.Release();
            }
        }

        return WhdAttachmentUploadResult.Create(techNoteId, uploadedPaths, failures);
    }

    private async Task<WhdUploadSession> CreateUploadSessionAsync(
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
            var details = string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase : content.Trim();
            throw new HttpRequestException(
                $"WHD could not create the attachment session: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {details}",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(content);
        var sessionKey = ReadSessionString(document.RootElement, "sessionKey", "key");
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            throw new InvalidOperationException(
                "WHD authenticated the attachment request but did not return a temporary REST session key.");
        }

        var uploadUri = BuildAttachmentUploadUri(settings.BaseUrl, "techNote", 1);
        if (_cookieContainer is not null)
        {
            var retainedJavaSessionCookie = FindCookie(_cookieContainer, uploadUri, "JSESSIONID");
            if (retainedJavaSessionCookie is null)
            {
                await TryTerminateProbeSessionAsync(settings, sessionKey, CancellationToken.None);
                throw new InvalidOperationException(
                    "WHD created the attachment session but did not retain the JSESSIONID cookie required for attachment uploads.");
            }

            SetUploadSessionCookie(
                _cookieContainer,
                uploadUri,
                retainedJavaSessionCookie,
                sessionKey);
            return new WhdUploadSession(
                sessionKey,
                string.Empty,
                UsesAutomaticCookies: true,
                retainedJavaSessionCookie.Value);
        }

        var responseCookieHeader = BuildSessionCookieHeader(response);
        var javaSessionCookie = FindCookie(responseCookieHeader, "JSESSIONID");
        if (javaSessionCookie is null)
        {
            await TryTerminateProbeSessionAsync(settings, sessionKey, CancellationToken.None);
            throw new InvalidOperationException(
                "WHD created the attachment session but did not provide the JSESSIONID cookie required for attachment uploads.");
        }

        var javaSession = javaSessionCookie.Value;
        var cookieHeader = string.Join(
            "; ",
            $"{javaSession.Key}={javaSession.Value}",
            $"wosid={sessionKey}");
        return new WhdUploadSession(
            sessionKey,
            cookieHeader,
            UsesAutomaticCookies: false,
            javaSession.Value);
    }

    private async Task<WhdSingleAttachmentUploadResult> UploadTechNoteImageAsync(
        WhdConnectionSettings settings,
        WhdUploadSession session,
        int techNoteId,
        string filePath,
        CancellationToken cancellationToken)
    {
        var currentUpload = await UploadTechNoteImagePartAsync(
            settings,
            session,
            techNoteId,
            filePath,
            "file",
            cancellationToken);
        if (currentUpload.Success
            || !currentUpload.MissingRequiredPartName.Equals(
                "fileUpload",
                StringComparison.OrdinalIgnoreCase))
        {
            return currentUpload;
        }

        // Current WHD builds require the part name "file", while the older WHD
        // API guide documents "fileUpload". Retry the legacy name only when
        // WHD explicitly says that exact part was absent, so an uncertain
        // response can never cause the same image to be uploaded twice.
        return await UploadTechNoteImagePartAsync(
            settings,
            session,
            techNoteId,
            filePath,
            "fileUpload",
            cancellationToken);
    }

    private async Task<WhdSingleAttachmentUploadResult> UploadTechNoteImagePartAsync(
        WhdConnectionSettings settings,
        WhdUploadSession session,
        int techNoteId,
        string filePath,
        string multipartPartName,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildAttachmentUploadUri(settings.BaseUrl, "techNote", techNoteId);
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        using var multipart = new MultipartFormDataContent();
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            WhdImageAttachmentPolicy.GetMediaType(filePath));
        multipart.Add(fileContent, multipartPartName, Path.GetFileName(filePath));

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = multipart
        };
        if (!session.UsesAutomaticCookies && !string.IsNullOrWhiteSpace(session.CookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
        }
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            if (response.RequestMessage?.Method != HttpMethod.Post
                || !IsExpectedAttachmentResponseUri(requestUri, response.RequestMessage.RequestUri))
            {
                return WhdSingleAttachmentUploadResult.Failed(
                    "WHD redirected the attachment request away from the upload endpoint. The image was not marked uploaded.");
            }

            if (!TryReadAttachmentId(content, out _))
            {
                return WhdSingleAttachmentUploadResult.Failed(
                    "WHD returned a successful HTTP status but did not confirm an attachment ID. The image was not marked uploaded.");
            }

            return WhdSingleAttachmentUploadResult.Succeeded();
        }

        var details = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "WHD returned an attachment authentication error."
            : string.IsNullOrWhiteSpace(content)
                ? response.ReasonPhrase ?? "No response details were returned."
                : SanitizeAttachmentResponseDetails(content.Trim(), session);
        if (details.Length > 500)
        {
            details = details[..500];
        }

        var authenticationHint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? " WHD rejected the documented JSESSIONID and wosid attachment session cookies."
                : string.Empty;
        return WhdSingleAttachmentUploadResult.Failed(
            $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {details}{authenticationHint}",
            TryReadMissingRequiredPartName(content));
    }

    private static string TryReadMissingRequiredPartName(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var details = content;
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("reason", out var reason)
                && reason.ValueKind == JsonValueKind.String)
            {
                details = reason.GetString() ?? content;
            }
        }
        catch (JsonException)
        {
            // Some WHD versions return this diagnostic as plain text.
        }

        const string prefix = "Required request part '";
        const string suffix = "' is not present";
        var prefixIndex = details.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (prefixIndex < 0)
        {
            return string.Empty;
        }

        var partStart = prefixIndex + prefix.Length;
        var suffixIndex = details.IndexOf(suffix, partStart, StringComparison.OrdinalIgnoreCase);
        return suffixIndex <= partStart
            ? string.Empty
            : details[partStart..suffixIndex].Trim();
    }

    private static bool IsExpectedAttachmentResponseUri(Uri requestUri, Uri? responseUri)
    {
        if (responseUri is null)
        {
            return false;
        }

        return requestUri.Scheme.Equals(responseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && requestUri.Host.Equals(responseUri.Host, StringComparison.OrdinalIgnoreCase)
            && requestUri.Port == responseUri.Port
            && requestUri.AbsolutePath.Equals(responseUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadAttachmentId(string content, out int attachmentId)
    {
        attachmentId = 0;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            attachmentId = ReadInt(document.RootElement, "id") ?? 0;
            return attachmentId > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string SanitizeAttachmentResponseDetails(
        string details,
        WhdUploadSession session)
    {
        var sanitized = details;
        foreach (var sensitiveValue in new[] { session.SessionKey, session.JavaSessionId })
        {
            if (string.IsNullOrWhiteSpace(sensitiveValue) || sensitiveValue.Length < 8)
            {
                continue;
            }

            sanitized = sanitized.Replace(sensitiveValue, "[redacted]", StringComparison.Ordinal);
            var escapedValue = Uri.EscapeDataString(sensitiveValue);
            if (!escapedValue.Equals(sensitiveValue, StringComparison.Ordinal))
            {
                sanitized = sanitized.Replace(escapedValue, "[redacted]", StringComparison.OrdinalIgnoreCase);
            }
        }

        return sanitized;
    }

    private static string BuildSessionCookieHeader(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            return string.Empty;
        }

        return string.Join(
            "; ",
            setCookieHeaders
                .Select(static header => header.Split(';', 2)[0].Trim())
                .Where(static cookie => !string.IsNullOrWhiteSpace(cookie))
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static KeyValuePair<string, string>? FindCookie(
        string cookieHeader,
        string cookieName)
    {
        foreach (var segment in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var name = segment[..separatorIndex].Trim();
            if (!name.Equals(cookieName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = segment[(separatorIndex + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return new KeyValuePair<string, string>(name, value);
            }
        }

        return null;
    }

    private static Cookie? FindCookie(
        CookieContainer? cookieContainer,
        Uri requestUri,
        string cookieName)
    {
        if (cookieContainer is null)
        {
            return null;
        }

        foreach (Cookie cookie in cookieContainer.GetCookies(requestUri))
        {
            if (cookie.Name.Equals(cookieName, StringComparison.OrdinalIgnoreCase)
                && !cookie.Expired
                && !string.IsNullOrWhiteSpace(cookie.Value))
            {
                return cookie;
            }
        }

        return null;
    }

    private static void SetUploadSessionCookie(
        CookieContainer cookieContainer,
        Uri uploadUri,
        Cookie javaSessionCookie,
        string sessionKey)
    {
        foreach (Cookie cookie in cookieContainer.GetCookies(uploadUri))
        {
            if (cookie.Name.Equals("wosid", StringComparison.OrdinalIgnoreCase))
            {
                cookie.Expired = true;
            }
        }

        var cookiePath = string.IsNullOrWhiteSpace(javaSessionCookie.Path)
            ? GetAttachmentApplicationPath(uploadUri)
            : javaSessionCookie.Path;
        cookieContainer.Add(
            uploadUri,
            new Cookie("wosid", sessionKey, cookiePath)
            {
                Secure = uploadUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase),
                HttpOnly = true
            });
    }

    private void ClearUploadSessionCookie(
        WhdConnectionSettings settings,
        WhdUploadSession session)
    {
        if (!session.UsesAutomaticCookies || _cookieContainer is null)
        {
            return;
        }

        try
        {
            var uploadUri = BuildAttachmentUploadUri(settings.BaseUrl, "techNote", 1);
            foreach (Cookie cookie in _cookieContainer.GetCookies(uploadUri))
            {
                if (cookie.Name.Equals("wosid", StringComparison.OrdinalIgnoreCase)
                    && cookie.Value.Equals(session.SessionKey, StringComparison.Ordinal))
                {
                    cookie.Expired = true;
                }
            }
        }
        catch (CookieException)
        {
            // Session termination is best-effort; stale upload cookies must not hide the upload result.
        }
        catch (UriFormatException)
        {
            // The connection URL was validated before upload; cleanup remains best-effort.
        }
    }

    private static string GetAttachmentApplicationPath(Uri uploadUri)
    {
        const string attachmentSuffix = "/attachment/upload";
        return uploadUri.AbsolutePath.EndsWith(attachmentSuffix, StringComparison.OrdinalIgnoreCase)
            ? uploadUri.AbsolutePath[..^attachmentSuffix.Length]
            : "/";
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A cleanup timeout must not replace the result of the operation that created the session.
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

        return GetTicketsPageAsync(
            requestUri,
            settings.Username,
            cancellationToken);
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
        string configuredOrganizationUsername,
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
        return ParseTickets(
            document.RootElement,
            configuredOrganizationUsername);
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

    private async Task<WhdLocationContactResult> GetBestLocationContactsAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        CancellationToken cancellationToken)
    {
        var contacts = new List<WhdLocationContact>();
        var pageSignatures = new HashSet<string>(StringComparer.Ordinal);

        for (var page = 1; page <= MaximumPageCount; page++)
        {
            var batch = await GetClientContactsPageAsync(
                settings,
                auth,
                page,
                PageSize,
                cancellationToken);
            if (batch.Count == 0)
                break;

            var signature = BuildPageSignature(
                batch.Select(contact => $"{contact.LocationExternalId}:{contact.ExternalId}"));
            if (!pageSignatures.Add(signature))
                break;

            contacts.AddRange(batch.Where(static contact => contact.IsActive));
            if (batch.Count < PageSize)
                break;
        }

        var selectedContacts = contacts
            .GroupBy(contact => contact.LocationExternalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(static contact => contact.IsPrimary)
                    .ThenByDescending(static contact => contact.CompletenessScore)
                    .ThenBy(static contact => contact.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static contact => contact.ExternalId, StringComparer.OrdinalIgnoreCase)
                    .First(),
                StringComparer.OrdinalIgnoreCase);

        using var detailGate = new SemaphoreSlim(MaximumConcurrentClientDetailRequests);
        var detailTasks = selectedContacts
            .Select(async pair =>
            {
                var contact = pair.Value;
                if (contact.HasCompleteDetails)
                {
                    return new WhdLocationContactEnrichment(
                        pair.Key,
                        contact,
                        DetailUnavailable: false);
                }

                await detailGate.WaitAsync(cancellationToken);
                try
                {
                    var detailResult = await GetClientContactDetailsAsync(
                        settings,
                        auth,
                        contact,
                        cancellationToken);
                    return new WhdLocationContactEnrichment(
                        pair.Key,
                        detailResult.Contact,
                        detailResult.DetailUnavailable);
                }
                finally
                {
                    detailGate.Release();
                }
            })
            .ToArray();

        var enrichments = await Task.WhenAll(detailTasks);
        return new WhdLocationContactResult(
            enrichments.ToDictionary(
                static result => result.LocationExternalId,
                static result => result.Contact,
                StringComparer.OrdinalIgnoreCase),
            enrichments
                .Where(static result => result.DetailUnavailable)
                .Select(static result => result.Contact.ExternalId)
                .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private async Task<WhdLocationContactDetailResult> GetClientContactDetailsAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        WhdLocationContact listContact,
        CancellationToken cancellationToken)
    {
        var encodedClientId = Uri.EscapeDataString(listContact.ExternalId);
        var requestUri = BuildRequestUri(
            settings.BaseUrl,
            $"Clients/{encodedClientId}",
            auth,
            new Dictionary<string, string>());
        using var detailTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        detailTimeout.CancelAfter(OptionalClientDetailTimeout);
        try
        {
            using var response = await _httpClient.GetAsync(requestUri, detailTimeout.Token);
            var content = await response.Content.ReadAsStringAsync(detailTimeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new WhdLocationContactDetailResult(
                    listContact,
                    DetailUnavailable: false);
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                // WHD can retain legacy client records whose provider email no
                // longer passes its current RFC validation. The Clients list still
                // contains enough information to associate the contact with its
                // location, so keep that data instead of failing the full snapshot.
                return new WhdLocationContactDetailResult(
                    listContact,
                    DetailUnavailable: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase : content.Trim();
                throw new HttpRequestException(
                    $"HTTP {(int)response.StatusCode} from Web Help Desk client {listContact.ExternalId}: {message}",
                    null,
                    response.StatusCode);
            }

            using var document = JsonDocument.Parse(content);
            var detailed = ParseClientContacts(document.RootElement).FirstOrDefault();
            if (detailed is null)
            {
                return new WhdLocationContactDetailResult(
                    listContact,
                    DetailUnavailable: false);
            }

            return new WhdLocationContactDetailResult(
                MergeContactDetails(listContact, detailed),
                DetailUnavailable: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Client detail is optional enrichment. A slow legacy record must
            // not cancel the complete location snapshot.
            return new WhdLocationContactDetailResult(
                listContact,
                DetailUnavailable: true);
        }
    }

    private async Task<IReadOnlyList<WhdLocationContact>> GetClientContactsPageAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        int page,
        int limit,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(settings.BaseUrl, "Clients", auth, new Dictionary<string, string>
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
                $"HTTP {(int)response.StatusCode} from Web Help Desk clients: {message}",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(content);
        return ParseClientContacts(document.RootElement);
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

    private async Task<IReadOnlyList<WhdSyncedTechnician>> EnrichTechnicianDetailsAsync(
        WhdConnectionSettings settings,
        WhdAuthParameters auth,
        IReadOnlyList<WhdSyncedTechnician> technicians,
        CancellationToken cancellationToken)
    {
        using var detailGate = new SemaphoreSlim(MaximumConcurrentTechnicianDetailRequests);
        var detailTasks = technicians.Select(async technician =>
        {
            if (!IsPlaceholderTechnicianName(technician))
            {
                return technician;
            }

            var rawId = GetRawTechnicianId(technician.ExternalId);
            if (string.IsNullOrWhiteSpace(rawId))
            {
                return technician;
            }

            await detailGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var detailed = await TryGetTechnicianAsync(
                        settings,
                        auth,
                        $"Techs/{Uri.EscapeDataString(rawId)}",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (detailed is null
                    || !string.Equals(
                        detailed.ExternalId,
                        technician.ExternalId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return technician;
                }

                return new WhdSyncedTechnician
                {
                    ExternalId = technician.ExternalId,
                    DisplayName = IsPlaceholderTechnicianName(detailed)
                        ? technician.DisplayName
                        : detailed.DisplayName,
                    Username = detailed.Username ?? technician.Username,
                    Email = detailed.Email ?? technician.Email,
                    IsActive = detailed.IsActive
                };
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                    or JsonException
                    or InvalidOperationException
                    or UriFormatException
                || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // A weak list result is still usable for mapping by ID. Do
                // not fail the complete technician snapshot when one optional
                // detail resource is unavailable.
                return technician;
            }
            finally
            {
                detailGate.Release();
            }
        });

        return await Task.WhenAll(detailTasks).ConfigureAwait(false);
    }

    private static bool IsPlaceholderTechnicianName(WhdSyncedTechnician technician)
    {
        var displayName = technician.DisplayName?.Trim();
        var rawId = GetRawTechnicianId(technician.ExternalId);
        return string.IsNullOrWhiteSpace(displayName)
            || displayName.Equals(technician.ExternalId, StringComparison.OrdinalIgnoreCase)
            || displayName.Equals(rawId, StringComparison.OrdinalIgnoreCase)
            || displayName.Equals($"Technician {rawId}", StringComparison.OrdinalIgnoreCase)
            || displayName.StartsWith("WHD-TECH-", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRawTechnicianId(string externalId)
    {
        const string prefix = "WHD-TECH-";
        var trimmed = externalId.Trim();
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
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
                if (!TryReadTicketNoteText(noteElement, out var noteText))
                {
                    continue;
                }

                var duration = ReadDurationMinutes(noteElement);
                if (!string.IsNullOrWhiteSpace(id)
                    && NormalizeNoteForComparison(noteText).Equals(
                        NormalizeNoteForComparison(expectedNote),
                        StringComparison.Ordinal)
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

    private static bool TryReadTicketNoteText(JsonElement element, out string noteText)
    {
        var primaryText = ReadString(element, "noteText");
        var mobileText = ReadString(element, "mobileNoteText");

        if (!string.IsNullOrEmpty(primaryText))
        {
            noteText = primaryText;
            return true;
        }

        if (!string.IsNullOrEmpty(mobileText))
        {
            noteText = mobileText;
            return true;
        }

        if (primaryText is not null || mobileText is not null)
        {
            noteText = primaryText ?? mobileText ?? string.Empty;
            return true;
        }

        noteText = string.Empty;
        return false;
    }

    public static string NormalizeNoteForComparison(string? value)
    {
        var normalized = WebUtility.HtmlDecode(value ?? string.Empty)
            .Replace('\u00a0', ' ')
            .ReplaceLineEndings("\n");
        normalized = Regex.Replace(
            normalized,
            @"<br\s*/?>",
            "\n",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"</p>\s*<p(?:\s[^>]*)?>",
            "\n\n",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"^\s*<p(?:\s[^>]*)?>|</p>\s*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return string.Join(
                "\n",
                normalized.Split('\n').Select(static line => line.TrimEnd()))
            .Trim();
    }

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

    private static IReadOnlyList<WhdSyncedTicket> ParseTickets(
        JsonElement root,
        string? configuredOrganizationUsername = null)
    {
        var tickets = new List<WhdSyncedTicket>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var ticketElement in root.EnumerateArray())
            {
                AddTicket(tickets, ticketElement, configuredOrganizationUsername);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
        {
            foreach (var ticketElement in records.EnumerateArray())
            {
                AddTicket(tickets, ticketElement, configuredOrganizationUsername);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            AddTicket(tickets, root, configuredOrganizationUsername);
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

    private static IReadOnlyList<WhdLocationContact> ParseClientContacts(JsonElement root)
    {
        var contacts = new List<WhdLocationContact>();
        foreach (var element in EnumerateRecords(root))
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            var location = TryGetObject(element, "location")
                ?? TryGetObject(element, "defaultLocation");
            var locationId = location.HasValue
                ? ReadStringAny(location.Value, "id", "locationId")
                : ReadStringAny(element, "locationId", "defaultLocationId");
            var externalId = ReadStringAny(element, "id", "clientId", "username", "email");
            if (string.IsNullOrWhiteSpace(locationId) || string.IsNullOrWhiteSpace(externalId))
                continue;

            var name = ReadStringAny(element, "displayName", "fullName", "name")
                ?? BuildName(element)
                ?? ReadStringAny(element, "username", "email")
                ?? $"WHD client {externalId}";
            contacts.Add(new WhdLocationContact(
                FormatWhdLocationId(locationId),
                externalId.Trim(),
                name.Trim(),
                JoinDistinctValues(
                    ReadStringAny(element, "email", "emailAddress"),
                    ReadStringAny(element, "secondaryEmail", "secondaryEmailAddress", "email2")),
                JoinDistinctValues(
                    ReadStringAny(element, "phone", "phone1", "phoneNumber"),
                    ReadStringAny(element, "phone2", "secondaryPhone", "alternatePhone")),
                FormatAddress(element),
                !ReadBooleanAny(element, "deleted", "inactive", "isInactive", "disabled"),
                ReadBooleanAny(
                    element,
                    "isPrimary",
                    "primary",
                    "isPrimaryContact",
                    "primaryContact",
                    "isAdmin",
                    "admin")));
        }

        return contacts;
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
            var displayName = ReadStringAny(element, "displayName", "fullName")
                ?? BuildName(element)
                ?? ReadStringAny(element, "name")
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
            Phone = TrimToNull(ReadStringAny(locationElement, "phone", "phone1", "phoneNumber")),
            Address = FormatAddress(locationElement),
            IsActive = isActive
        });
    }

    private static WhdSyncedClient EnrichLocation(
        WhdSyncedClient location,
        IReadOnlyDictionary<string, WhdLocationContact> contactsByLocation)
    {
        if (!contactsByLocation.TryGetValue(location.ExternalId, out var contact))
            return location;

        return new WhdSyncedClient
        {
            ExternalId = location.ExternalId,
            Name = location.Name,
            LocationName = location.LocationName,
            ContactName = contact.Name,
            ContactEmail = contact.Email,
            Phone = contact.Phone ?? location.Phone,
            Address = contact.Address ?? location.Address,
            IsActive = location.IsActive
        };
    }

    private static WhdLocationContact MergeContactDetails(
        WhdLocationContact listContact,
        WhdLocationContact detailedContact) =>
        new(
            listContact.LocationExternalId,
            listContact.ExternalId,
            detailedContact.Name,
            detailedContact.Email ?? listContact.Email,
            detailedContact.Phone ?? listContact.Phone,
            detailedContact.Address ?? listContact.Address,
            listContact.IsActive && detailedContact.IsActive,
            listContact.IsPrimary || detailedContact.IsPrimary);

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

    private static void AddTicket(
        List<WhdSyncedTicket> tickets,
        JsonElement ticketElement,
        string? configuredOrganizationUsername)
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
            AssignedTechnicianExternalId = ResolveAssignedTechnicianExternalId(
                assignedTechnicianId,
                assignedTechnician,
                ticketElement,
                configuredOrganizationUsername),
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

    private static string? ResolveAssignedTechnicianExternalId(
        string? assignedTechnicianId,
        JsonElement? assignedTechnician,
        JsonElement ticketElement,
        string? configuredOrganizationUsername)
    {
        if (string.IsNullOrWhiteSpace(assignedTechnicianId))
        {
            return null;
        }

        if (IsConfiguredOrganizationAccountAssignment(
                assignedTechnician,
                ticketElement,
                configuredOrganizationUsername))
        {
            return ConfiguredOrganizationAccountExternalId;
        }

        return FormatWhdTechnicianId(assignedTechnicianId);
    }

    private static bool IsConfiguredOrganizationAccountAssignment(
        JsonElement? assignedTechnician,
        JsonElement ticketElement,
        string? configuredOrganizationUsername)
    {
        if (string.IsNullOrWhiteSpace(configuredOrganizationUsername))
        {
            return false;
        }

        var assignedUsername = assignedTechnician.HasValue
            ? ReadStringAny(
                assignedTechnician.Value,
                "username",
                "userName",
                "loginName",
                "login")
            : null;
        assignedUsername ??= ReadStringAny(
            ticketElement,
            "assignedTechUsername",
            "assignedTechnicianUsername",
            "techUsername");
        if (string.Equals(
                assignedUsername?.Trim(),
                configuredOrganizationUsername.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // WHD 12.x omits its built-in Helpdesk Manager account from /Techs
        // when an application API key is used. Ticket payloads still identify
        // that account as "H. Manager", but may omit its whdmgr login name.
        // Recognize those equivalent labels so ticket ownership uses the same
        // stable mapping identity exposed by GetTechniciansAsync.
        var assignedName = assignedTechnician.HasValue
            ? ReadStringAny(
                assignedTechnician.Value,
                "displayName",
                "fullName",
                "name")
            : ReadStringAny(
                ticketElement,
                "assignedTechName",
                "assignedTechnicianName");
        return IsHelpdeskManagerUsername(configuredOrganizationUsername)
            && IsHelpdeskManagerDisplayName(assignedName);
    }

    private static bool IsHelpdeskManagerUsername(string value)
    {
        var normalized = NormalizeIdentityLabel(value);
        return normalized is "whdmgr" or "helpdeskmanager";
    }

    private static bool IsHelpdeskManagerDisplayName(string? value)
    {
        var normalized = NormalizeIdentityLabel(value);
        return normalized is "hmanager" or "helpdeskmanager" or "whdmanager";
    }

    private static string NormalizeIdentityLabel(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());

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
            ContactName = string.IsNullOrWhiteSpace(clientName) ? null : clientName.Trim(),
            ContactEmail = JoinDistinctValues(
                ReadStringAny(element, "email", "emailAddress"),
                ReadStringAny(element, "secondaryEmail", "secondaryEmailAddress", "email2")),
            Phone = JoinDistinctValues(
                    ReadStringAny(element, "phone", "phone1", "phoneNumber"),
                    ReadStringAny(element, "phone2", "secondaryPhone", "alternatePhone"))
                ?? (locationElement.HasValue
                    ? JoinDistinctValues(
                        ReadStringAny(locationElement.Value, "phone", "phone1", "phoneNumber"),
                        ReadStringAny(locationElement.Value, "phone2", "secondaryPhone", "alternatePhone"))
                    : null),
            Address = FormatAddress(element)
                ?? (locationElement.HasValue ? FormatAddress(locationElement.Value) : null)
        };
    }

    private static string? FormatAddress(JsonElement element)
    {
        var street = TrimToNull(ReadStringAny(element, "address", "address1", "street"));
        var city = TrimToNull(ReadStringAny(element, "city"));
        var state = TrimToNull(ReadStringAny(element, "state", "province"));
        var postalCode = TrimToNull(ReadStringAny(element, "postalCode", "zip", "zipCode"));
        var country = TrimToNull(ReadStringAny(element, "country"));

        var cityLine = string.Join(
            ", ",
            new[] { city, state }.Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrWhiteSpace(postalCode))
            cityLine = string.IsNullOrWhiteSpace(cityLine) ? postalCode : $"{cityLine} {postalCode}";

        var parts = new[] { street, TrimToNull(cityLine), country }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return parts.Length == 0 ? null : string.Join(", ", parts);
    }

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? JoinDistinctValues(params string?[] values)
    {
        var distinct = values
            .Select(TrimToNull)
            .Where(static value => value is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return distinct.Length == 0 ? null : string.Join(" / ", distinct);
    }

    private sealed record WhdLocationContact(
        string LocationExternalId,
        string ExternalId,
        string Name,
        string? Email,
        string? Phone,
        string? Address,
        bool IsActive,
        bool IsPrimary)
    {
        public bool HasCompleteDetails =>
            !string.IsNullOrWhiteSpace(Email)
            && !string.IsNullOrWhiteSpace(Phone)
            && !string.IsNullOrWhiteSpace(Address);

        public int CompletenessScore =>
            (string.IsNullOrWhiteSpace(Name) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(Email) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(Phone) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(Address) ? 0 : 1);
    }

    private sealed record WhdLocationContactDetailResult(
        WhdLocationContact Contact,
        bool DetailUnavailable);

    private sealed record WhdLocationContactEnrichment(
        string LocationExternalId,
        WhdLocationContact Contact,
        bool DetailUnavailable);

    private sealed record WhdLocationContactResult(
        IReadOnlyDictionary<string, WhdLocationContact> Contacts,
        IReadOnlyList<string> UnavailableClientIds);

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

    private static Uri BuildAttachmentUploadUri(
        string baseUrl,
        string entityType,
        int entityId)
    {
        var apiRoot = BuildApiRoot(baseUrl);
        var apiPath = apiRoot.AbsolutePath;
        var webObjectsIndex = apiPath.IndexOf("/WebObjects/", StringComparison.OrdinalIgnoreCase);
        var applicationRoot = webObjectsIndex >= 0
            ? apiPath[..webObjectsIndex]
            : apiPath.EndsWith("/ra", StringComparison.OrdinalIgnoreCase)
                ? apiPath[..^3]
                : apiPath;
        var builder = new UriBuilder(apiRoot)
        {
            Path = $"{applicationRoot.TrimEnd('/')}/attachment/upload",
            Query = string.Join(
                "&",
                $"type={Uri.EscapeDataString(entityType)}",
                $"entityId={entityId.ToString(CultureInfo.InvariantCulture)}",
                "returnFields=id%2CuploadDate"),
            Fragment = string.Empty
        };

        return builder.Uri;
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

    private sealed record WhdUploadSession(
        string SessionKey,
        string CookieHeader,
        bool UsesAutomaticCookies,
        string JavaSessionId);

    private sealed record WhdSingleAttachmentUploadResult(
        bool Success,
        string Message,
        string MissingRequiredPartName)
    {
        public static WhdSingleAttachmentUploadResult Succeeded() =>
            new(true, string.Empty, string.Empty);

        public static WhdSingleAttachmentUploadResult Failed(
            string message,
            string missingRequiredPartName = "") =>
            new(false, message, missingRequiredPartName);
    }
}

public sealed record WhdAttachmentUploadFailure(string FilePath, string Message)
{
    public string FileName => Path.GetFileName(FilePath);
}

public sealed class WhdAttachmentUploadResult
{
    public bool Success => Failures.Count == 0 && UploadedFilePaths.Count > 0;
    public IReadOnlyList<string> UploadedFilePaths { get; init; } = [];
    public IReadOnlyList<WhdAttachmentUploadFailure> Failures { get; init; } = [];
    public string Message { get; init; } = string.Empty;

    public static WhdAttachmentUploadResult Failed(
        string message,
        IEnumerable<string> filePaths) => new()
        {
            Failures = filePaths
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(path => new WhdAttachmentUploadFailure(path, message))
                .ToArray(),
            Message = message
        };

    public static WhdAttachmentUploadResult Create(
        int techNoteId,
        IReadOnlyList<string> uploadedFilePaths,
        IReadOnlyList<WhdAttachmentUploadFailure> failures)
    {
        var successMessage = uploadedFilePaths.Count switch
        {
            0 => string.Empty,
            1 => $"Attached 1 image to WHD TechNote #{techNoteId}.",
            _ => $"Attached {uploadedFilePaths.Count} images to WHD TechNote #{techNoteId}."
        };
        if (failures.Count == 0)
        {
            return new WhdAttachmentUploadResult
            {
                UploadedFilePaths = uploadedFilePaths.ToArray(),
                Message = successMessage
            };
        }

        var failureText = string.Join(
            "; ",
            failures.Select(failure => $"{failure.FileName}: {failure.Message}"));
        return new WhdAttachmentUploadResult
        {
            UploadedFilePaths = uploadedFilePaths.ToArray(),
            Failures = failures.ToArray(),
            Message = string.IsNullOrWhiteSpace(successMessage)
                ? $"WHD image upload failed. {failureText}"
                : $"{successMessage} {failures.Count} image upload(s) failed: {failureText}"
        };
    }
}
