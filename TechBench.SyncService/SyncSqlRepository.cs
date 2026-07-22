using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TechBench.Models;

namespace TechBench.SyncService;

public sealed class SyncSqlRepository
{
    private readonly SyncServiceOptions _options;
    private readonly string _connectionString;

    public SyncSqlRepository(IOptions<SyncServiceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.SqlServer)
            || string.IsNullOrWhiteSpace(_options.Database))
        {
            throw new InvalidOperationException("TechBenchSync SqlServer and Database are required.");
        }

        _connectionString = new SqlConnectionStringBuilder
        {
            DataSource = _options.SqlServer.Trim(),
            InitialCatalog = _options.Database.Trim(),
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = _options.TrustServerCertificate,
            MultipleActiveResultSets = false,
            ConnectTimeout = 15,
            ApplicationName = "TechBench V2 Sync Service"
        }.ConnectionString;
    }

    public async Task<WhdServiceConfiguration> GetConfigurationAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, "[tb_service].[GetWhdSyncConfiguration]");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("SQL Server returned no WHD synchronization configuration.");
        }

        var authenticationText = GetString(reader, "AuthenticationMode", "Auto");
        if (!Enum.TryParse<WhdAuthenticationMode>(authenticationText, ignoreCase: true, out var authenticationMode))
        {
            authenticationMode = WhdAuthenticationMode.Auto;
        }

        return new WhdServiceConfiguration(
            GetString(reader, "BaseUrl"),
            GetString(reader, "Username"),
            authenticationMode,
            GetBoolean(reader, "AutoSyncEnabled", true),
            Math.Clamp(GetInt32(reader, "AutoSyncMinutes", 5), 1, 1440),
            ParseCursor(GetString(reader, "CursorValue")));
    }

    public async Task<SageSyncConfiguration> GetSageConfigurationAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, "[tb_service].[GetSageSyncConfiguration]");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("SQL Server returned no Sage synchronization configuration.");
        }

        return new SageSyncConfiguration(
            GetString(reader, "Dsn"),
            GetString(reader, "Username"));
    }

    public async Task<WhdSyncWork?> ClaimWorkAsync(
        Guid workerId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, "[tb_service].[ClaimWhdSyncWork]");
        Add(command, "@WorkerId", SqlDbType.UniqueIdentifier, workerId);
        Add(command, "@LeaseSeconds", SqlDbType.Int, _options.EffectiveLeaseSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var cursor = ParseCursor(GetString(reader, "CursorValue"));
        var requestType = GetString(reader, "RequestType", "Incremental");
        return new WhdSyncWork(
            GetGuid(reader, "WorkId"),
            GetGuid(reader, "LeaseId"),
            GetString(reader, "WorkType"),
            requestType.Equals("Full", StringComparison.OrdinalIgnoreCase) || cursor is null,
            cursor,
            GetNullableGuid(reader, "RequestId"),
            GetDateTimeOffset(reader, "ExpiresAtUtc"));
    }

    public async Task<SageSyncWork?> ClaimSageWorkAsync(
        Guid workerId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, "[tb_service].[ClaimSageSyncWork]");
        Add(command, "@WorkerId", SqlDbType.UniqueIdentifier, workerId);
        Add(command, "@LeaseSeconds", SqlDbType.Int, _options.EffectiveLeaseSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SageSyncWork(
            GetGuid(reader, "WorkId"),
            GetGuid(reader, "LeaseId"),
            GetDateTimeOffset(reader, "ExpiresAtUtc"),
            GetBoolean(reader, "AllowLargeRemoval", false));
    }

    public async Task RenewLeaseAsync(
        WhdSyncWork work,
        Guid workerId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, "[tb_service].[RenewWhdSyncLease]");
        AddWorkIdentity(command, work, workerId);
        Add(command, "@LeaseSeconds", SqlDbType.Int, _options.EffectiveLeaseSeconds);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RenewSageLeaseAsync(
        SageSyncWork work,
        Guid workerId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, "[tb_service].[RenewSageSyncLease]");
        AddSageWorkIdentity(command, work, workerId);
        Add(command, "@LeaseSeconds", SqlDbType.Int, _options.EffectiveLeaseSeconds);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyClientsAsync(
        WhdSyncWork work,
        Guid workerId,
        string json,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken)
    {
        await ApplyJsonAsync(
                "[tb_service].[ApplyWhdClientSnapshot]",
                work,
                workerId,
                json,
                syncedAt,
                cancellationToken)
            .ConfigureAwait(false);
        await ReconcileAutomaticClientMatchesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ApplyTicketsAsync(
        WhdSyncWork work,
        Guid workerId,
        string json,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken) =>
        ApplyJsonAsync("[tb_service].[ApplyWhdTicketBatch]", work, workerId, json, syncedAt, cancellationToken);

    public Task ApplyStatusesAsync(
        WhdSyncWork work,
        Guid workerId,
        string json,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken) =>
        ApplyJsonAsync("[tb_service].[ApplyWhdTicketStatusSnapshot]", work, workerId, json, syncedAt, cancellationToken);

    public Task ApplyTechniciansAsync(
        WhdSyncWork work,
        Guid workerId,
        string json,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken) =>
        ApplyJsonAsync("[tb_service].[ApplyWhdTechnicianSnapshot]", work, workerId, json, syncedAt, cancellationToken);

    public Task ApplyGroupsAsync(
        WhdSyncWork work,
        Guid workerId,
        string json,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken) =>
        ApplyJsonAsync("[tb_service].[ApplyWhdTechGroupSnapshot]", work, workerId, json, syncedAt, cancellationToken);

    public async Task<SageSyncCounts> ApplySageCustomersAsync(
        SageSyncWork work,
        Guid workerId,
        string json,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, "[tb_service].[ApplySageCustomerSnapshot]");
        AddSageWorkIdentity(command, work, workerId);
        Add(command, "@Json", SqlDbType.NVarChar, json, -1);
        Add(command, "@SyncedAtUtc", SqlDbType.DateTime2, syncedAt.UtcDateTime);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "SQL Server returned no result for the Sage customer snapshot.");
        }

        var counts = new SageSyncCounts(
            GetInt32(reader, "ReadCount", 0),
            GetInt32(reader, "SavedCount", 0),
            GetInt32(reader, "StaleCount", 0),
            GetInt32(reader, "MatchedCount", 0),
            GetInt32(reader, "ExistingCount", 0),
            GetBoolean(reader, "RequiresLargeRemovalConfirmation", false),
            GetString(reader, "Message"));
        await reader.DisposeAsync().ConfigureAwait(false);

        if (!counts.RequiresLargeRemovalConfirmation)
        {
            var automaticMatches = await ReconcileAutomaticClientMatchesAsync(cancellationToken)
                .ConfigureAwait(false);
            counts = counts with { MatchedCount = counts.MatchedCount + automaticMatches };
        }

        return counts;
    }

    internal async Task<int> ReconcileAutomaticClientMatchesAsync(
        CancellationToken cancellationToken)
    {
        var candidates = await GetAutomaticClientMatchCandidatesAsync(cancellationToken)
            .ConfigureAwait(false);
        var matches = ServerAutomaticClientMatcher.FindSafeAutomaticMatches(candidates);
        var appliedCount = 0;

        foreach (var matchGroup in matches
                     .GroupBy(static match => match.SageClient.Id)
                     .OrderBy(static group => group.Key))
        {
            var orderedMatches = matchGroup
                .OrderBy(static match => match.WhdClient.Id)
                .ToList();
            var primary = orderedMatches[0];
            var primaryApplied = await ApplyAutomaticClientMatchAsync(
                    primary,
                    cancellationToken)
                .ConfigureAwait(false);
            appliedCount += primaryApplied;
            if (primaryApplied == 0
                || orderedMatches.Count == 1
                || string.IsNullOrWhiteSpace(primary.SageClient.SageCustomerId))
            {
                continue;
            }

            foreach (var additionalMatch in orderedMatches.Skip(1))
            {
                appliedCount += await ApplyAutomaticWhdFamilyMemberAsync(
                        primary.WhdClient.Id,
                        primary.SageClient.SageCustomerId,
                        additionalMatch,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return appliedCount;
    }

    private async Task<int> ApplyAutomaticClientMatchAsync(
        ServerAutomaticClientMatch match,
        CancellationToken cancellationToken)
    {
        if (match.WhdClient.RowVersion is not { Length: 8 } whdRowVersion
            || match.SageClient.RowVersion is not { Length: 8 } sageRowVersion)
        {
            return 0;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, "[tb_service].[ApplyAutomaticClientMatch]");
        Add(command, "@WhdClientId", SqlDbType.Int, match.WhdClient.Id);
        Add(command, "@SageClientId", SqlDbType.Int, match.SageClient.Id);
        Add(command, "@ExpectedWhdRowVersion", SqlDbType.Binary, whdRowVersion, 8);
        Add(command, "@ExpectedSageRowVersion", SqlDbType.Binary, sageRowVersion, 8);
        AddMatchScore(command, match.Score);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private async Task<int> ApplyAutomaticWhdFamilyMemberAsync(
        int targetClientId,
        string expectedSageCustomerId,
        ServerAutomaticClientMatch match,
        CancellationToken cancellationToken)
    {
        if (match.WhdClient.RowVersion is not { Length: 8 } whdRowVersion)
        {
            return 0;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(
            connection,
            "[tb_service].[ApplyAutomaticWhdFamilyMember]");
        Add(command, "@TargetClientId", SqlDbType.Int, targetClientId);
        Add(command, "@SourceWhdClientId", SqlDbType.Int, match.WhdClient.Id);
        Add(command, "@ExpectedSourceWhdRowVersion", SqlDbType.Binary, whdRowVersion, 8);
        Add(command, "@ExpectedSageCustomerId", SqlDbType.NVarChar, expectedSageCustomerId, 120);
        AddMatchScore(command, match.Score);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static void AddMatchScore(SqlCommand command, double score)
    {
        Add(
            command,
            "@MatchScore",
            SqlDbType.Decimal,
            Convert.ToDecimal(score, CultureInfo.InvariantCulture));
        command.Parameters["@MatchScore"].Precision = 6;
        command.Parameters["@MatchScore"].Scale = 5;
    }

    private async Task<IReadOnlyList<AutomaticClientMatchCandidate>> GetAutomaticClientMatchCandidatesAsync(
        CancellationToken cancellationToken)
    {
        var candidates = new List<AutomaticClientMatchCandidate>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, "[tb_service].[GetAutomaticClientMatchCandidates]");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new AutomaticClientMatchCandidate(
                GetInt32(reader, "Id", 0),
                GetString(reader, "Name"),
                GetString(reader, "Source"),
                GetNullableString(reader, "ExternalId"),
                GetBoolean(reader, "IsActive", true),
                GetNullableString(reader, "WhdLocationName"),
                GetNullableString(reader, "SageCustomerId"),
                GetNullableString(reader, "SageCustomerName"),
                GetBytes(reader, "RowVersion")));
        }

        return candidates;
    }

    public async Task CompleteWorkAsync(
        WhdSyncWork work,
        Guid workerId,
        bool succeeded,
        DateTimeOffset? nextCursorUtc,
        string? message,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, "[tb_service].[CompleteWhdSyncWork]");
        AddWorkIdentity(command, work, workerId);
        Add(command, "@Succeeded", SqlDbType.Bit, succeeded);
        Add(
            command,
            "@CursorValue",
            SqlDbType.NVarChar,
            nextCursorUtc?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            400);
        Add(command, "@Message", SqlDbType.NVarChar, Truncate(message, 2000), 2000);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteSageWorkAsync(
        SageSyncWork work,
        Guid workerId,
        bool succeeded,
        string? message,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, "[tb_service].[CompleteSageSyncWork]");
        AddSageWorkIdentity(command, work, workerId);
        Add(command, "@Succeeded", SqlDbType.Bit, succeeded);
        Add(command, "@Message", SqlDbType.NVarChar, Truncate(message, 2000), 2000);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyJsonAsync(
        string procedure,
        WhdSyncWork work,
        Guid workerId,
        string json,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, procedure);
        AddWorkIdentity(command, work, workerId);
        Add(command, "@Json", SqlDbType.NVarChar, json, -1);
        Add(command, "@SyncedAtUtc", SqlDbType.DateTime2, syncedAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private SqlCommand CreateCommand(SqlConnection connection, string procedure) => new(procedure, connection)
    {
        CommandType = CommandType.StoredProcedure,
        CommandTimeout = _options.EffectiveCommandTimeoutSeconds
    };

    private static void AddWorkIdentity(SqlCommand command, WhdSyncWork work, Guid workerId)
    {
        Add(command, "@WorkId", SqlDbType.UniqueIdentifier, work.WorkId);
        Add(command, "@LeaseId", SqlDbType.UniqueIdentifier, work.LeaseId);
        Add(command, "@WorkerId", SqlDbType.UniqueIdentifier, workerId);
    }

    private static void AddSageWorkIdentity(SqlCommand command, SageSyncWork work, Guid workerId)
    {
        Add(command, "@WorkId", SqlDbType.UniqueIdentifier, work.WorkId);
        Add(command, "@LeaseId", SqlDbType.UniqueIdentifier, work.LeaseId);
        Add(command, "@WorkerId", SqlDbType.UniqueIdentifier, workerId);
    }

    private static void Add(
        SqlCommand command,
        string name,
        SqlDbType type,
        object? value,
        int? size = null)
    {
        var parameter = command.Parameters.Add(name, type);
        if (size.HasValue)
        {
            parameter.Size = size.Value;
        }

        parameter.Value = value ?? DBNull.Value;
    }

    private static int FindOrdinal(SqlDataReader reader, string name)
    {
        for (var index = 0; index < reader.FieldCount; index++)
        {
            if (reader.GetName(index).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string GetString(SqlDataReader reader, string name, string fallback = "")
    {
        var ordinal = FindOrdinal(reader, name);
        return ordinal < 0 || reader.IsDBNull(ordinal)
            ? fallback
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? fallback;
    }

    private static string? GetNullableString(SqlDataReader reader, string name)
    {
        var ordinal = FindOrdinal(reader, name);
        return ordinal < 0 || reader.IsDBNull(ordinal)
            ? null
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static byte[]? GetBytes(SqlDataReader reader, string name)
    {
        var ordinal = FindOrdinal(reader, name);
        return ordinal < 0 || reader.IsDBNull(ordinal)
            ? null
            : (byte[])reader.GetValue(ordinal);
    }

    private static bool GetBoolean(SqlDataReader reader, string name, bool fallback)
    {
        var ordinal = FindOrdinal(reader, name);
        return ordinal < 0 || reader.IsDBNull(ordinal)
            ? fallback
            : Convert.ToBoolean(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static int GetInt32(SqlDataReader reader, string name, int fallback)
    {
        var ordinal = FindOrdinal(reader, name);
        return ordinal < 0 || reader.IsDBNull(ordinal)
            ? fallback
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static Guid GetGuid(SqlDataReader reader, string name)
    {
        var ordinal = FindOrdinal(reader, name);
        return ordinal < 0 || reader.IsDBNull(ordinal)
            ? Guid.Empty
            : reader.GetGuid(ordinal);
    }

    private static Guid? GetNullableGuid(SqlDataReader reader, string name)
    {
        var value = GetGuid(reader, name);
        return value == Guid.Empty ? null : value;
    }

    private static DateTimeOffset GetDateTimeOffset(SqlDataReader reader, string name)
    {
        var ordinal = FindOrdinal(reader, name);
        if (ordinal < 0 || reader.IsDBNull(ordinal))
        {
            return DateTimeOffset.MinValue;
        }

        var value = reader.GetDateTime(ordinal);
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static DateTimeOffset? ParseCursor(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var cursor)
            ? cursor
            : null;

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maxLength
                ? value
                : value[..maxLength];
}
