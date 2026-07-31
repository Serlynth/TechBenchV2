using System.Data;

namespace TechBench.SyncService;

public sealed partial class SyncSqlRepository
{
    public async Task<AuthPointApiConfiguration> GetAuthPointConfigurationAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(
            connection,
            "[tb_service].[GetAuthPointMfaConfiguration]");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "SQL Server returned no WatchGuard AuthPoint configuration.");
        }

        return new AuthPointApiConfiguration(
            GetBoolean(reader, "Enabled", false),
            GetString(reader, "BaseApiUrl"),
            GetString(reader, "AccountId"),
            GetString(reader, "ResourceId"),
            GetString(reader, "AccessId"));
    }

    public async Task<AuthPointMfaWork?> ClaimAuthPointMfaChallengeAsync(
        Guid workerId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(
            connection,
            "[tb_service].[ClaimAuthPointMfaChallenge]");
        Add(command, "@WorkerId", SqlDbType.UniqueIdentifier, workerId);
        Add(command, "@LeaseSeconds", SqlDbType.Int, 180);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AuthPointMfaWork(
            GetGuid(reader, "ChallengeId"),
            GetGuid(reader, "LeaseId"),
            GetString(reader, "ProviderLogin"),
            GetString(reader, "ClientMachine"),
            GetString(reader, "ActionScope"),
            GetInt64(reader, "SecretId"),
            GetDateTimeOffset(reader, "ExpiresAtUtc"));
    }

    public async Task CompleteAuthPointMfaChallengeAsync(
        AuthPointMfaWork work,
        Guid workerId,
        AuthPointMfaResult result,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(
            connection,
            "[tb_service].[CompleteAuthPointMfaChallenge]");
        Add(command, "@ChallengeId", SqlDbType.UniqueIdentifier, work.ChallengeId);
        Add(command, "@WorkerId", SqlDbType.UniqueIdentifier, workerId);
        Add(command, "@LeaseId", SqlDbType.UniqueIdentifier, work.LeaseId);
        Add(command, "@Result", SqlDbType.NVarChar, result.Kind.ToString(), 16);
        Add(command, "@OutcomeCode", SqlDbType.NVarChar, Truncate(result.Code, 80), 80);
        Add(command, "@OutcomeMessage", SqlDbType.NVarChar, Truncate(result.Message, 500), 500);
        Add(
            command,
            "@ProviderTransactionId",
            SqlDbType.NVarChar,
            Truncate(result.TransactionId, 120),
            120);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
