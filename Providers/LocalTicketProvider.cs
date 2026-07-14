using TechBench.Data;
using TechBench.Models;

namespace TechBench.Providers;

public sealed class LocalTicketProvider : ITicketProvider
{
    private readonly TechBenchRepository _repository;

    public LocalTicketProvider(TechBenchRepository repository)
    {
        _repository = repository;
    }

    public string SourceName => "Manual/local tickets";

    public Task<IReadOnlyList<Ticket>> SearchTicketsAsync(int clientId, string? searchTerm, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_repository.GetTickets(clientId, searchTerm));
    }
}
