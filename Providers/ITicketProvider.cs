using TechBench.Models;

namespace TechBench.Providers;

public interface ITicketProvider
{
    string SourceName { get; }
    Task<IReadOnlyList<Ticket>> SearchTicketsAsync(int clientId, string? searchTerm, CancellationToken cancellationToken = default);
}
