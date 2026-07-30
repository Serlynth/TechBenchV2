using TechBench.Data;
using TechBench.Models;

namespace TechBench.Providers;

public sealed class SqlServerTicketProvider : ITicketProvider
{
    private readonly SqlServerTechBenchRepository _repository;

    public SqlServerTicketProvider(SqlServerTechBenchRepository repository)
    {
        _repository = repository
            ?? throw new ArgumentNullException(nameof(repository));
    }

    public string SourceName => "TechBench V2 SQL Server";

    public Task<IReadOnlyList<Ticket>> SearchTicketsAsync(
        int clientId,
        string? searchTerm,
        CancellationToken cancellationToken = default) =>
        _repository.GetTicketsAsync(
            clientId,
            searchTerm,
            includeClosed: false,
            cancellationToken);
}
