using TechBench.Models;
using TechBench.Services;

namespace TechBench.Providers;

public sealed class WhdRestPoster : IWorkEntryPoster
{
    private readonly WhdRestClient _whdRestClient;

    public WhdRestPoster(WhdRestClient whdRestClient)
    {
        _whdRestClient = whdRestClient;
    }

    public string DestinationName => "Web Help Desk";

    public Task<PostingResult> PostAsync(
        WorkEntry entry,
        Client client,
        Ticket? ticket,
        IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ticket is null || !TryResolveWhdTicketId(ticket, out var whdTicketId))
        {
            return Task.FromResult(PostingResult.Failed("Select a synced Web Help Desk ticket before posting."));
        }

        if (string.IsNullOrWhiteSpace(entry.Note))
        {
            return Task.FromResult(PostingResult.Failed("Enter a Sage/WHD Note before posting to Web Help Desk."));
        }

        if (entry.DurationMinutes <= 0)
        {
            return Task.FromResult(PostingResult.Failed("Enter a positive duration before posting to Web Help Desk."));
        }

        var whdSettings = BuildPersonalWhdConnectionSettings(settings);

        return _whdRestClient.PostTicketNoteAsync(
            whdSettings,
            whdTicketId,
            WhdNoteTextFormatter.BuildWhdNoteText(entry),
            entry.DurationMinutes,
            GetWhdNoteTimestampUtc(entry),
            cancellationToken);
    }

    internal static DateTime GetWhdNoteTimestampUtc(WorkEntry entry)
    {
        var localTimestamp = DateTime.SpecifyKind(
            entry.WorkDate.Date
            + (entry.HasTimeRange ? entry.StartTime : TimeSpan.FromHours(12)),
            DateTimeKind.Unspecified);
        var offset = TimeZoneInfo.Local.GetUtcOffset(localTimestamp);
        return new DateTimeOffset(localTimestamp, offset).UtcDateTime;
    }

    internal static WhdConnectionSettings BuildPersonalWhdConnectionSettings(
        IReadOnlyDictionary<string, string> settings) =>
        BuildPersonalWhdConnectionSettings(
            settings.GetValueOrDefault("Whd.BaseUrl", string.Empty),
            settings.GetValueOrDefault("Whd.Username", string.Empty),
            settings.GetValueOrDefault("Whd.ApiToken", string.Empty));

    internal static WhdConnectionSettings BuildPersonalWhdConnectionSettings(
        string baseUrl,
        string username,
        string secret) => new()
        {
            BaseUrl = baseUrl.Trim(),
            Username = username.Trim(),
            Secret = secret,
            // The organization-wide mode belongs to the server sync identity.
            // Workstation reads and writes must detect the signed-in user's
            // independently stored password or API token instead.
            AuthenticationMode = WhdAuthenticationMode.Auto
        };

    private static bool TryResolveWhdTicketId(Ticket ticket, out int whdTicketId)
    {
        var candidates = new[]
        {
            ticket.ExternalId,
            ticket.TicketNumber
        };

        foreach (var candidate in candidates)
        {
            var normalized = candidate?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (normalized.StartsWith("WHD-", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[4..];
            }

            if (int.TryParse(normalized, out whdTicketId) && whdTicketId > 0)
            {
                return true;
            }
        }

        whdTicketId = 0;
        return false;
    }
}
