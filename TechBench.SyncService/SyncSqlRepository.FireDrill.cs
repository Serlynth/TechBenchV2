using System.Data;

namespace TechBench.SyncService;

public sealed partial class SyncSqlRepository
{
    public async Task<FireDrillSyncConfiguration> GetFireDrillConfigurationAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, "[tb_service].[GetFireDrillSyncConfiguration]");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("SQL Server returned no FireDrill synchronization configuration.");
        return new FireDrillSyncConfiguration(
            GetString(reader, "SourcePath"),
            GetBoolean(reader, "DailySyncEnabled", true),
            GetString(reader, "DailySyncTime", "04:00"));
    }

    public async Task<FireDrillSyncWork?> ClaimFireDrillWorkAsync(Guid workerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, "[tb_service].[ClaimFireDrillSyncWork]");
        Add(command, "@WorkerId", SqlDbType.UniqueIdentifier, workerId);
        Add(command, "@LeaseSeconds", SqlDbType.Int, _options.EffectiveLeaseSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new FireDrillSyncWork(
            GetGuid(reader, "WorkId"), GetGuid(reader, "LeaseId"), GetDateTimeOffset(reader, "LeaseExpiresUtc"));
    }

    public async Task RenewFireDrillLeaseAsync(FireDrillSyncWork work, Guid workerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, "[tb_service].[RenewFireDrillSyncLease]");
        AddFireDrillIdentity(command, work, workerId);
        Add(command, "@LeaseSeconds", SqlDbType.Int, _options.EffectiveLeaseSeconds);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FireDrillSyncCounts> ApplyFireDrillSnapshotAsync(
        FireDrillSyncWork work, Guid workerId, string rowsJson,
        DateTimeOffset sourceModifiedAtUtc, DateTimeOffset syncedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, "[tb_service].[ApplyFireDrillCredentialSnapshot]");
        AddFireDrillIdentity(command, work, workerId);
        Add(command, "@RowsJson", SqlDbType.NVarChar, rowsJson, -1);
        Add(command, "@SourceModifiedAtUtc", SqlDbType.DateTime2, sourceModifiedAtUtc.UtcDateTime);
        Add(command, "@SyncedAtUtc", SqlDbType.DateTime2, syncedAtUtc.UtcDateTime);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("SQL Server returned no result for the FireDrill snapshot.");
        return new FireDrillSyncCounts(
            GetInt32(reader, "ReadCount", 0), GetInt32(reader, "SavedCount", 0), GetInt32(reader, "StaleCount", 0));
    }

    public async Task CompleteFireDrillWorkAsync(
        FireDrillSyncWork work, Guid workerId, bool succeeded, string? message,
        DateTimeOffset? sourceModifiedAtUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, "[tb_service].[CompleteFireDrillSyncWork]");
        AddFireDrillIdentity(command, work, workerId);
        Add(command, "@Succeeded", SqlDbType.Bit, succeeded);
        Add(command, "@Message", SqlDbType.NVarChar, Truncate(message, 2000), 2000);
        Add(command, "@SourceModifiedAtUtc", SqlDbType.DateTime2, sourceModifiedAtUtc?.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddFireDrillIdentity(Microsoft.Data.SqlClient.SqlCommand command, FireDrillSyncWork work, Guid workerId)
    {
        Add(command, "@RequestId", SqlDbType.UniqueIdentifier, work.WorkId);
        Add(command, "@LeaseId", SqlDbType.UniqueIdentifier, work.LeaseId);
        Add(command, "@WorkerId", SqlDbType.UniqueIdentifier, workerId);
    }
}
