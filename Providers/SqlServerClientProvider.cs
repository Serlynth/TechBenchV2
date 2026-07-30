using TechBench.Data;
using TechBench.Models;

namespace TechBench.Providers;

/// <summary>
/// Adapts the authoritative SQL repository to the client-search provider
/// contract while preserving repository row-version tracking.
/// </summary>
public sealed class SqlServerClientProvider : IClientProvider
{
    private readonly SqlServerTechBenchRepository _repository;

    public SqlServerClientProvider(SqlServerTechBenchRepository repository)
    {
        _repository = repository
            ?? throw new ArgumentNullException(nameof(repository));
    }

    public string SourceName => "TechBench V2 SQL Server";

    public Task<IReadOnlyList<Client>> SearchClientsAsync(
        string? searchTerm,
        CancellationToken cancellationToken = default) =>
        _repository.GetClientsAsync(
            includeInactive: false,
            searchTerm,
            cancellationToken);
}
