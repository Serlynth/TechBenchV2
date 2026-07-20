using System.Text.Json;

namespace TechBench.SyncService;

public sealed class SageCustomerSyncEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SyncSqlRepository _repository;
    private readonly ISageOdbcWorkerProcessClient _odbcWorker;
    private readonly SageSecretStore _secretStore;

    public SageCustomerSyncEngine(
        SyncSqlRepository repository,
        ISageOdbcWorkerProcessClient odbcWorker,
        SageSecretStore secretStore)
    {
        _repository = repository;
        _odbcWorker = odbcWorker;
        _secretStore = secretStore;
    }

    public async Task<SageSyncExecutionResult> ExecuteAsync(
        SageSyncWork work,
        Guid workerId,
        CancellationToken cancellationToken)
    {
        var configuration = await _repository
            .GetSageConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!configuration.IsConfigured)
        {
            throw new InvalidOperationException(
                "The shared Sage customer-sync DSN and username must be configured by a TechBench Admin.");
        }

        var customers = await _odbcWorker.ReadCustomersAsync(
                configuration.Dsn,
                configuration.Username,
                _secretStore.Read(),
                cancellationToken)
            .ConfigureAwait(false);

        if (customers.Count == 0)
        {
            throw new InvalidOperationException(
                "Sage ODBC returned no active customers. TechBench refused to replace the shared customer snapshot with an empty result.");
        }

        var syncedAt = DateTimeOffset.UtcNow;
        var counts = await _repository.ApplySageCustomersAsync(
                work,
                workerId,
                JsonSerializer.Serialize(customers, JsonOptions),
                syncedAt,
                cancellationToken)
            .ConfigureAwait(false);
        if (counts.RequiresLargeRemovalConfirmation)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(counts.Message)
                    ? "The Sage snapshot would remove an unusually large number of customers. No data was changed; a TechBench Admin must explicitly confirm the removal."
                    : counts.Message);
        }

        var message = $"Synchronized {counts.SavedCount} active Sage customer(s) from server DSN '{configuration.Dsn}'.";
        if (counts.StaleCount > 0)
        {
            message += $" Removed or deactivated {counts.StaleCount} stale Sage customer(s).";
        }

        if (counts.MatchedCount > 0)
        {
            message += $" {counts.MatchedCount} customer record(s) are matched to WHD.";
        }

        return new SageSyncExecutionResult(counts, message);
    }
}
