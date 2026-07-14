using TechBench.Models;

namespace TechBench.Providers;

public interface IWorkEntryPoster
{
    string DestinationName { get; }

    Task<PostingResult> PostAsync(
        WorkEntry entry,
        Client client,
        Ticket? ticket,
        IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken = default);
}
