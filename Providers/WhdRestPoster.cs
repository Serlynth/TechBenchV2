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
            return Task.FromResult(PostingResult.Failed("Enter a work note before posting to Web Help Desk."));
        }

        if (entry.DurationMinutes <= 0)
        {
            return Task.FromResult(PostingResult.Failed("Enter a positive duration before posting to Web Help Desk."));
        }

        var whdSettings = new WhdConnectionSettings
        {
            BaseUrl = settings.GetValueOrDefault("Whd.BaseUrl", string.Empty),
            Username = settings.GetValueOrDefault("Whd.Username", string.Empty),
            Secret = settings.GetValueOrDefault("Whd.ApiToken", string.Empty),
            AuthenticationMode = Enum.TryParse<WhdAuthenticationMode>(
                settings.GetValueOrDefault("Whd.AuthenticationMode", string.Empty),
                ignoreCase: true,
                out var authenticationMode)
                ? authenticationMode
                : WhdAuthenticationMode.Auto
        };

        return _whdRestClient.PostTicketNoteAsync(
            whdSettings,
            whdTicketId,
            WhdNoteTextFormatter.BuildWhdNoteText(entry),
            entry.DurationMinutes,
            cancellationToken);
    }

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
