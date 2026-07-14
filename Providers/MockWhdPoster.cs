using System.Text.Json;
using TechBench.Models;

namespace TechBench.Providers;

public sealed class MockWhdPoster : IWorkEntryPoster
{
    public string DestinationName => "Web Help Desk";

    public Task<PostingResult> PostAsync(
        WorkEntry entry,
        Client client,
        Ticket? ticket,
        IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mockMode = settings.TryGetValue("Whd.MockMode", out var enabled)
            && bool.TryParse(enabled, out var isEnabled)
            && isEnabled;

        var payload = JsonSerializer.Serialize(new
        {
            Destination = "SolarWinds Web Help Desk",
            entry.Id,
            WorkDate = entry.WorkDate.ToString("yyyy-MM-dd"),
            Client = client.Name,
            Ticket = ticket?.TicketNumber ?? entry.TicketNumberText ?? "No Ticket Selected",
            entry.DurationMinutes,
            entry.Billable,
            entry.Note,
            entry.InternalNote
        }, new JsonSerializerOptions { WriteIndented = true });

        if (!mockMode)
        {
            return Task.FromResult(PostingResult.Failed(
                "Web Help Desk mock mode is disabled and the real REST poster is not configured yet.",
                payload));
        }

        return Task.FromResult(PostingResult.Succeeded(
            "Mock Web Help Desk post recorded locally; the entry remains WHD pending.",
            payload,
            $"MOCK-WHD-{entry.Id}-{DateTime.Now:yyyyMMddHHmmss}",
            markPosted: false));
    }
}
