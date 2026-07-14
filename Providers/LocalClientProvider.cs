using TechBench.Data;
using TechBench.Models;

namespace TechBench.Providers;

public sealed class LocalClientProvider : IClientProvider
{
    private readonly TechBenchRepository _repository;

    public LocalClientProvider(TechBenchRepository repository)
    {
        _repository = repository;
    }

    public string SourceName => "Local synced client table";

    public Task<IReadOnlyList<Client>> SearchClientsAsync(string? searchTerm, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_repository.GetClients(searchTerm: searchTerm));
    }
}
