using System.Text.Json;
using TechBench.Models;

namespace TechBench.Providers;

public sealed class MockSagePoster : IWorkEntryPoster
{
    public string DestinationName => "Sage 50";

    public Task<PostingResult> PostAsync(
        WorkEntry entry,
        Client client,
        Ticket? ticket,
        IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mockMode = settings.TryGetValue("Sage.MockMode", out var enabled)
            && bool.TryParse(enabled, out var isEnabled)
            && isEnabled;

        var payload = JsonSerializer.Serialize(new
        {
            Destination = "Sage 50 Accounting",
            entry.Id,
            WorkDate = entry.WorkDate.ToString("yyyy-MM-dd"),
            Customer = client.Name,
            Ticket = ticket?.TicketNumber ?? entry.TicketNumberText,
            Minutes = entry.DurationMinutes,
            Hours = Math.Round(entry.DurationMinutes / 60m, 2),
            entry.Billable,
            Description = entry.Note
        }, new JsonSerializerOptions { WriteIndented = true });

        if (entry.DurationMinutes <= 0)
        {
            return Task.FromResult(PostingResult.Failed("Sage posting requires a positive duration.", payload));
        }

        if (!mockMode)
        {
            return Task.FromResult(PostingResult.Failed(
                "Sage mock mode is disabled. Use the native Sage poster for live tickets.",
                payload));
        }

        return Task.FromResult(PostingResult.Succeeded(
            "Mock Sage post recorded locally; the entry remains Sage pending.",
            payload,
            $"MOCK-SAGE-{entry.Id}-{DateTime.Now:yyyyMMddHHmmss}",
            markPosted: false));
    }
}
