using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.Data;

/// <summary>
/// Stored-procedure-only SQL Server implementation of the V1-derived
/// repository contract. Synchronous methods are compatibility shims for the
/// current WPF view model; every operation has an asynchronous implementation
/// that owns its connection, honors cancellation, and applies the configured
/// command timeout.
/// </summary>
public sealed partial class SqlServerTechBenchRepository : ITechBenchRepository
{
    private const string UserSettingScope = "User";
    internal const string OrganizationScope = "Organization";
    private const int PostingLeaseSeconds = 1800;

    private static readonly JsonSerializerOptions PayloadJsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly SqlServerConnectionFactory _connectionFactory;
    private readonly ConcurrentDictionary<string, byte[]> _rowVersions =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _clientAliasIds =
        new(StringComparer.OrdinalIgnoreCase);
    private byte[]? _editorDraftRowVersion;
    private bool _fullTextSearchAvailable;
    private bool _equipmentBoardAvailable;
    private bool _clientInfoBetaAvailable;

    public SqlServerTechBenchRepository(
        SqlServerConnectionFactory connectionFactory,
        Guid? deviceId = null)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
        DeviceId = deviceId ?? LocalPreferenceStore.LoadOrCreate().DeviceId;
    }

    public Guid DeviceId { get; }

    // Retained for drop-in compatibility with the existing UI label.
    public string DatabasePath =>
        $"SQL Server: {_connectionFactory.Options.Server}/{_connectionFactory.Options.Database}";

    public bool FullTextSearchAvailable => _fullTextSearchAvailable;
    public bool EquipmentBoardAvailable => _equipmentBoardAvailable;
    public bool ClientInfoBetaAvailable => _clientInfoBetaAvailable;

    public static class Procedures
    {
        public const string SearchClients = "[tb_app].[SearchClients]";
        public const string GetClient = "[tb_app].[GetClient]";
        public const string SaveClient = "[tb_app].[AdminSaveClient]";
        public const string SearchTickets = "[tb_app].[SearchTickets]";
        public const string GetTicket = "[tb_app].[GetTicket]";
        public const string SaveTicket = "[tb_app].[SaveTicket]";
        public const string GetTicketStatusOptions = "[tb_app].[GetTicketStatusOptions]";
        public const string UpsertTicketStatusOption = "[tb_app].[SyncUpsertTicketStatusOption]";
        public const string UpsertTicket = "[tb_app].[SyncUpsertTicket]";
        public const string UpsertClient = "[tb_app].[SyncUpsertClient]";
        public const string UpsertSageCustomer = "[tb_app].[SyncUpsertSageCustomer]";
        public const string RemoveStaleSageCustomers =
            "[tb_app].[SyncRemoveStaleSageCustomers]";
        public const string MergeClients = "[tb_app].[AdminMergeClients]";
        public const string ReconcileClientMatches = "[tb_app].[ReconcileClientMatches]";
        public const string SearchWorkEntries = "[tb_app].[SearchWorkEntries]";
        public const string GetRepositoryCapabilities =
            "[tb_app].[GetRepositoryCapabilities]";
        public const string EnsureWorkspaceDefaults =
            "[tb_app].[EnsureWorkspaceDefaults]";
        public const string GetWorkEntry = "[tb_app].[GetWorkEntry]";
        public const string GetDistinctTags = "[tb_app].[GetDistinctTags]";
        public const string GetOrganizationTags =
            "[tb_app].[AdminGetOrganizationTags]";
        public const string SaveOrganizationTag =
            "[tb_app].[AdminSaveOrganizationTag]";
        public const string DeleteOrganizationTag =
            "[tb_app].[AdminDeleteOrganizationTag]";
        public const string SaveWorkEntry = "[tb_app].[SaveWorkEntry]";
        public const string DeleteWorkEntry = "[tb_app].[DeleteWorkEntry]";
        public const string GetWorkEntryLinks = "[tb_app].[GetWorkEntryLinks]";
        public const string SaveWorkEntryLink = "[tb_app].[SaveWorkEntryLink]";
        public const string DeleteWorkEntryLink = "[tb_app].[DeleteWorkEntryLink]";
        public const string GetTemplates = "[tb_app].[GetTemplates]";
        public const string SaveTemplate = "[tb_app].[SaveTemplate]";
        public const string DeleteTemplate = "[tb_app].[DeleteTemplate]";
        public const string GetEditorDraft = "[tb_app].[GetEditorDraft]";
        public const string SaveEditorDraft = "[tb_app].[SaveEditorDraft]";
        public const string DeleteEditorDraft = "[tb_app].[DeleteEditorDraft]";
        public const string GetClientAliases = "[tb_app].[GetClientAliases]";
        public const string SaveClientAlias = "[tb_app].[SaveClientAlias]";
        public const string DeleteClientAlias = "[tb_app].[DeleteClientAlias]";
        public const string GetCommonLinks = "[tb_app].[GetCommonLinks]";
        public const string SearchFireDrillCredentials = "[tb_app].[SearchFireDrillCredentials]";
        public const string RevealFireDrillCredential = "[tb_app].[RevealFireDrillCredential]";
        public const string SearchClientInfoClients = "[tb_app].[SearchClientInfoClients]";
        public const string GetClientInfoSnapshot = "[tb_app].[GetClientInfoSnapshot]";
        public const string GetClientAttachmentStorageConfiguration =
            "[tb_app].[GetClientAttachmentStorageConfiguration]";
        public const string GetClientInfoAttachments =
            "[tb_app].[GetClientInfoAttachments]";
        public const string SaveClientInfoAttachment =
            "[tb_app].[SaveClientInfoAttachment]";
        public const string SetClientInfoAttachmentEquipmentLink =
            "[tb_app].[SetClientInfoAttachmentEquipmentLink]";
        public const string SetClientInfoAttachmentArchived =
            "[tb_app].[SetClientInfoAttachmentArchived]";
        public const string SaveClientInfoProfile = "[tb_app].[SaveClientInfoProfile]";
        public const string SaveClientInfoLocation = "[tb_app].[SaveClientInfoLocation]";
        public const string SaveClientInfoPerson = "[tb_app].[SaveClientInfoPerson]";
        public const string SaveClientInfoResource = "[tb_app].[SaveClientInfoResource]";
        public const string SaveClientInfoResourceField =
            "[tb_app].[SaveClientInfoResourceField]";
        public const string DeleteClientInfoResourceField =
            "[tb_app].[DeleteClientInfoResourceField]";
        public const string SaveClientInfoFact = "[tb_app].[SaveClientInfoFact]";
        public const string SaveClientCredential = "[tb_app].[SaveClientCredential]";
        public const string SetClientCredentialSecret =
            "[tb_app].[SetClientCredentialSecret]";
        public const string RevealClientCredentialSecret =
            "[tb_app].[RevealClientCredentialSecret]";
        public const string BeginClientSecretMfaChallenge =
            "[tb_app].[BeginClientSecretMfaChallenge]";
        public const string GetClientSecretMfaChallenge =
            "[tb_app].[GetClientSecretMfaChallenge]";
        public const string CancelClientSecretMfaChallenge =
            "[tb_app].[CancelClientSecretMfaChallenge]";
        public const string BeginClientInfoImport = "[tb_app].[BeginClientInfoImport]";
        public const string StageClientInfoRecord = "[tb_app].[StageClientInfoRecord]";
        public const string StageClientInfoSecret = "[tb_app].[StageClientInfoSecret]";
        public const string ValidateClientInfoImport =
            "[tb_app].[ValidateClientInfoImport]";
        public const string CompareClientInfoImportToFireDrill =
            "[tb_app].[CompareClientInfoImportToFireDrill]";
        public const string GetClientInfoImportBatch =
            "[tb_app].[GetClientInfoImportBatch]";
        public const string ApproveClientInfoImport =
            "[tb_app].[ApproveClientInfoImport]";
        public const string PromoteClientInfoImport =
            "[tb_app].[PromoteClientInfoImport]";
        public const string SearchClientUsers = "[tb_app].[SearchClientUsers]";
        public const string RevealClientUser = "[tb_app].[RevealClientUser]";
        public const string GetCredentialsSyncStatus = "[tb_app].[GetFireDrillSyncStatus]";
        public const string RequestCredentialsSync = "[tb_app].[AdminRequestFireDrillSync]";
        public const string SaveCommonLink = "[tb_app].[SaveCommonLink]";
        public const string DeleteCommonLink = "[tb_app].[DeleteCommonLink]";
        public const string GetSettings = "[tb_app].[GetSettings]";
        public const string SaveSetting = "[tb_app].[SaveUserSetting]";
        public const string DeleteSetting = "[tb_app].[DeleteUserSetting]";
        public const string SaveOrganizationSetting =
            "[tb_app].[AdminSaveOrganizationSetting]";
        public const string DeleteOrganizationSetting =
            "[tb_app].[AdminDeleteOrganizationSetting]";
        public const string GetWhdSyncStatus = "[tb_app].[GetWhdSyncStatus]";
        public const string RequestWhdSync = "[tb_app].[AdminRequestWhdSync]";
        public const string GetSageSyncStatus = "[tb_app].[GetSageSyncStatus]";
        public const string RequestSageSync = "[tb_app].[AdminRequestSageSync]";
        public const string HeartbeatClientSession = "[tb_app].[HeartbeatClientSession]";
        public const string GetActiveClientSessions = "[tb_app].[AdminGetActiveClientSessions]";
        public const string GetRecentClientSessionResponses =
            "[tb_app].[AdminGetRecentClientSessionResponses]";
        public const string QueueClientSessionCommand = "[tb_app].[AdminQueueClientSessionCommand]";
        public const string AcknowledgeClientSessionCommand =
            "[tb_app].[AcknowledgeClientSessionCommand]";
        public const string CloseClientSession = "[tb_app].[CloseClientSession]";
        public const string GetWhdUserMappings = "[tb_app].[AdminGetWhdUserMappings]";
        public const string GetWhdTechnicians = "[tb_app].[AdminGetWhdTechnicians]";
        public const string SaveWhdUserMapping = "[tb_app].[AdminSaveWhdUserMapping]";
        public const string GetEquipmentBoard = "[tb_app].[AdminGetEquipmentBoard]";
        public const string GetEquipmentInventory = "[tb_app].[GetEquipmentInventory]";
        public const string GetInventoryClients = "[tb_app].[AdminGetInventoryClients]";
        public const string GetEquipmentAssignmentHistory =
            "[tb_app].[AdminGetEquipmentAssignmentHistory]";
        public const string SaveEquipment = "[tb_app].[AdminSaveEquipment]";
        public const string MoveEquipment = "[tb_app].[AdminMoveEquipment]";
        public const string ArchiveEquipment = "[tb_app].[AdminArchiveEquipment]";
        public const string AddPostingLog = "[tb_app].[AddPostingLog]";
        public const string GetLatestVerifiedWhdPostingLog =
            "[tb_app].[GetLatestVerifiedWhdPostingLog]";
        public const string BeginPostingAttempt = "[tb_app].[BeginPostingAttempt]";
        public const string GetOutstandingPostingAttempt =
            "[tb_app].[GetOutstandingPostingAttempt]";
        public const string CompletePostingAttempt = "[tb_app].[CompletePostingAttempt]";
        public const string ResolveOutstandingPostingAttempts =
            "[tb_app].[ResolveOutstandingPostingAttempts]";
        public const string AbandonOutstandingPostingAttempts =
            "[tb_app].[AbandonOutstandingPostingAttempts]";
        public const string MarkWorkEntryPosted =
            "[tb_app].[MarkWorkEntryPosted]";
        public const string HasSuccessfulSageDraftLog =
            "[tb_app].[HasSuccessfulSageDraftLog]";
        public const string GetPostingLogs = "[tb_app].[GetPostingLogs]";
        public const string AcquireSyncLease = "[tb_app].[AcquireSyncLease]";
        public const string ReleaseSyncLease = "[tb_app].[ReleaseSyncLease]";
        public const string BeginSyncRun = "[tb_app].[BeginSyncRun]";
        public const string CompleteSyncRun = "[tb_app].[CompleteSyncRun]";
        public const string ApplyWhdClientSnapshot = "[tb_app].[SyncApplyClientSnapshot]";
        public const string ApplyWhdTicketSnapshot = "[tb_app].[SyncApplyTicketSnapshot]";
        public const string ApplyTicketStatusSnapshot =
            "[tb_app].[SyncApplyTicketStatusSnapshot]";
        public const string BeginImportBatch = "[tb_app].[BeginImportBatch]";
        public const string AddImportLegacyMapping = "[tb_app].[AddImportLegacyMapping]";
        public const string CompleteImportBatch = "[tb_app].[CompleteImportBatch]";
        public const string BeginTechBenchV1Import =
            "[tb_app].[BeginTechBenchV1Import]";
        public const string ResolveTechBenchV1Reference =
            "[tb_app].[ResolveTechBenchV1Reference]";
        public const string ImportTechBenchV1WorkEntry =
            "[tb_app].[ImportTechBenchV1WorkEntry]";
        public const string ImportTechBenchV1WorkEntryLink =
            "[tb_app].[ImportTechBenchV1WorkEntryLink]";
        public const string ImportTechBenchV1PostingLog =
            "[tb_app].[ImportTechBenchV1PostingLog]";
        public const string CompleteTechBenchV1Import =
            "[tb_app].[CompleteTechBenchV1Import]";
        public const string AbandonTechBenchV1Import =
            "[tb_app].[AbandonTechBenchV1Import]";
    }

    public void Initialize() =>
        InitializeAsync().GetAwaiter().GetResult();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var currentUser = await _connectionFactory
            .GetCurrentUserContextAsync(cancellationToken)
            .ConfigureAwait(false);
        if (currentUser.CanManageSharedConfiguration)
        {
            await ExecuteNonQueryAsync(
                    Procedures.EnsureWorkspaceDefaults,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        try
        {
            var capabilities = await QueryAsync(
                    Procedures.GetRepositoryCapabilities,
                    null,
                    async (reader, token) =>
                    {
                        if (!await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            return (
                                FullText: false,
                                Equipment: false,
                                ClientInfo: false);
                        }

                        return (
                            FullText: GetBoolean(reader, "FullTextSearchAvailable"),
                            Equipment: GetBoolean(reader, "EquipmentBoardAvailable"),
                            ClientInfo: GetBoolean(reader, "ClientInfoBetaAvailable"));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            _fullTextSearchAvailable = capabilities.FullText;
            _equipmentBoardAvailable = capabilities.Equipment;
            _clientInfoBetaAvailable = capabilities.ClientInfo;
        }
        catch (SqlException ex) when (ex.Number == 2812)
        {
            // Older deployment packages do not expose the capability procedure.
            _fullTextSearchAvailable = false;
            _equipmentBoardAvailable = false;
            _clientInfoBetaAvailable = false;
        }
    }

    // The server is already authoritative; there is deliberately no local cache write.
    public void SynchronizeServerClientCache(IReadOnlyList<Client> clients) =>
        ArgumentNullException.ThrowIfNull(clients);

    private async Task<T> QueryAsync<T>(
        string procedure,
        Action<SqlCommand>? configure,
        Func<SqlDataReader, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var command = CreateCommand(connection, procedure);
        configure?.Invoke(command);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await read(reader, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ExecuteNonQueryAsync(
        string procedure,
        Action<SqlCommand>? configure,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var command = CreateCommand(connection, procedure);
        configure?.Invoke(command);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqlCommand CreateCommand(SqlConnection connection, string procedure)
    {
        var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = procedure;
        command.CommandTimeout = _connectionFactory.Options.CommandTimeoutSeconds;
        return command;
    }

    private static async Task<IReadOnlyList<T>> ReadListAsync<T>(
        SqlDataReader reader,
        CancellationToken cancellationToken,
        Func<SqlDataReader, T> map)
    {
        var values = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(map(reader));
        }

        return values;
    }

    private static async Task<T?> ReadSingleAsync<T>(
        SqlDataReader reader,
        CancellationToken cancellationToken,
        Func<SqlDataReader, T> map)
        where T : class
    {
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? map(reader)
            : null;
    }

    private static void AddInt(SqlCommand command, string name, int? value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Int)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });

    private static void AddBigInt(SqlCommand command, string name, long? value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.BigInt)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });

    private static void AddBit(SqlCommand command, string name, bool value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Bit) { Value = value });

    private static void AddNullableBit(SqlCommand command, string name, bool? value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Bit)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });

    private static void AddText(
        SqlCommand command,
        string name,
        int size,
        string? value,
        bool trim = true)
    {
        var normalized = trim ? value?.Trim() : value;
        command.Parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, size)
        {
            Value = string.IsNullOrEmpty(normalized) ? DBNull.Value : normalized
        });
    }

    private static void AddRequiredText(
        SqlCommand command,
        string name,
        int size,
        string value,
        bool trim = true)
    {
        ArgumentNullException.ThrowIfNull(value);
        command.Parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, size)
        {
            Value = trim ? value.Trim() : value
        });
    }

    private static void AddMaxText(
        SqlCommand command,
        string name,
        string? value,
        bool trim = false)
    {
        var normalized = trim ? value?.Trim() : value;
        command.Parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, -1)
        {
            Value = normalized is null ? DBNull.Value : normalized
        });
    }

    private static void AddDate(SqlCommand command, string name, DateTime? value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Date)
        {
            Value = value.HasValue ? value.Value.Date : DBNull.Value
        });

    private static void AddDateTime(SqlCommand command, string name, DateTime? value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.DateTime2)
        {
            Value = value.HasValue ? ToUtc(value.Value) : DBNull.Value
        });

    private static void AddTime(SqlCommand command, string name, TimeSpan value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Time) { Value = value });

    private static void AddGuid(SqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.UniqueIdentifier)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });

    private static void AddBinary(
        SqlCommand command,
        string name,
        int size,
        byte[]? value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Binary, size)
        {
            Value = value is { Length: > 0 } ? value : DBNull.Value
        });

    private static DateTime ToUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
        };

    private static DateTime ToLocal(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Local => value,
            DateTimeKind.Utc => value.ToLocalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime()
        };

    private static string SerializePayload<T>(T value) =>
        JsonSerializer.Serialize(value, PayloadJsonOptions);

    private void TrackRowVersion(string entityType, long id, SqlDataReader reader)
    {
        var rowVersion = GetBytes(reader, "RowVersion");
        if (rowVersion is { Length: > 0 })
        {
            _rowVersions[BuildRowVersionKey(entityType, id)] = rowVersion;
        }
    }

    private byte[]? GetTrackedRowVersion(string entityType, long id) =>
        id > 0
            && _rowVersions.TryGetValue(BuildRowVersionKey(entityType, id), out var value)
                ? value
                : null;

    private static string BuildRowVersionKey(string entityType, long id) =>
        $"{entityType}:{id.ToString(CultureInfo.InvariantCulture)}";

    private static int GetOrdinal(SqlDataReader reader, string columnName)
    {
        for (var index = 0; index < reader.FieldCount; index++)
        {
            if (reader.GetName(index).Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static object? GetValue(SqlDataReader reader, string columnName)
    {
        var ordinal = GetOrdinal(reader, columnName);
        return ordinal < 0 || reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
    }

    private static string GetString(
        SqlDataReader reader,
        string columnName,
        string fallback = "") =>
        Convert.ToString(GetValue(reader, columnName), CultureInfo.InvariantCulture)
        ?? fallback;

    private static string? GetNullableString(SqlDataReader reader, string columnName) =>
        GetValue(reader, columnName) is { } value
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;

    private static int GetInt32(
        SqlDataReader reader,
        string columnName,
        int fallback = 0) =>
        GetValue(reader, columnName) is { } value
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : fallback;

    private static int? GetNullableInt32(SqlDataReader reader, string columnName) =>
        GetValue(reader, columnName) is { } value
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : null;

    private static Guid? GetNullableGuid(SqlDataReader reader, string columnName) =>
        GetValue(reader, columnName) is Guid value ? value : null;

    private static long GetInt64(
        SqlDataReader reader,
        string columnName,
        long fallback = 0) =>
        GetValue(reader, columnName) is { } value
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : fallback;

    private static long? GetNullableInt64(SqlDataReader reader, string columnName) =>
        GetValue(reader, columnName) is { } value
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : null;

    private static bool GetBoolean(
        SqlDataReader reader,
        string columnName,
        bool fallback = false) =>
        GetValue(reader, columnName) is { } value
            ? Convert.ToBoolean(value, CultureInfo.InvariantCulture)
            : fallback;

    private static DateTime GetDateTime(
        SqlDataReader reader,
        string columnName,
        DateTime fallback = default)
    {
        return GetValue(reader, columnName) switch
        {
            DateTimeOffset value => value.LocalDateTime,
            DateTime value => ToLocal(value),
            { } value => ToLocal(Convert.ToDateTime(value, CultureInfo.InvariantCulture)),
            _ => fallback
        };
    }

    private static DateTime? GetNullableDateTime(SqlDataReader reader, string columnName)
    {
        return GetValue(reader, columnName) switch
        {
            DateTimeOffset value => value.LocalDateTime,
            DateTime value => ToLocal(value),
            { } value => ToLocal(Convert.ToDateTime(value, CultureInfo.InvariantCulture)),
            _ => null
        };
    }

    private static DateTime GetDate(
        SqlDataReader reader,
        string columnName,
        DateTime fallback = default)
    {
        return GetValue(reader, columnName) switch
        {
            DateTime value => value.Date,
            { } value => Convert.ToDateTime(value, CultureInfo.InvariantCulture).Date,
            _ => fallback.Date
        };
    }

    private static TimeSpan GetTimeSpan(
        SqlDataReader reader,
        string columnName,
        TimeSpan fallback = default)
    {
        return GetValue(reader, columnName) switch
        {
            TimeSpan value => value,
            DateTime value => value.TimeOfDay,
            string value when TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
                => parsed,
            _ => fallback
        };
    }

    private static byte[]? GetBytes(SqlDataReader reader, string columnName) =>
        GetValue(reader, columnName) as byte[];

    private static TEnum GetEnum<TEnum>(
        SqlDataReader reader,
        string columnName,
        TEnum fallback)
        where TEnum : struct, Enum
    {
        var value = GetValue(reader, columnName);
        if (value is null)
        {
            return fallback;
        }

        if (value is string text
            && Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsedText))
        {
            return parsedText;
        }

        try
        {
            var numeric = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return Enum.IsDefined(typeof(TEnum), numeric)
                ? (TEnum)Enum.ToObject(typeof(TEnum), numeric)
                : fallback;
        }
        catch (Exception) when (value is IConvertible)
        {
            return fallback;
        }
    }

}
