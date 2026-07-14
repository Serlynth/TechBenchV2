using TechBench.Models;

namespace TechBench.Providers;

public interface IClientProvider
{
    string SourceName { get; }
    Task<IReadOnlyList<Client>> SearchClientsAsync(string? searchTerm, CancellationToken cancellationToken = default);
}
