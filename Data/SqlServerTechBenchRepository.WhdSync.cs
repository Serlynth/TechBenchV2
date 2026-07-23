using Microsoft.Data.SqlClient;
using TechBench.Models;

namespace TechBench.Data;

public sealed partial class SqlServerTechBenchRepository
{
    public WhdSyncServiceStatus GetWhdSyncStatus() =>
        GetWhdSyncStatusAsync().GetAwaiter().GetResult();

    public Task<WhdSyncServiceStatus> GetWhdSyncStatusAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.GetWhdSyncStatus,
            null,
            async (reader, token) =>
            {
                var latestRequest = new WhdSyncServiceStatus();
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                }
                else
                {
                    latestRequest = ReadWhdSyncServiceStatus(reader);
                }

                if (!await reader.NextResultAsync(token).ConfigureAwait(false)
                    || !await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return latestRequest;
                }

                var lastError = GetString(reader, "LastError");
                return new WhdSyncServiceStatus
                {
                    Health = string.IsNullOrWhiteSpace(lastError)
                        ? latestRequest.Health
                        : "Error",
                    Message = string.IsNullOrWhiteSpace(lastError)
                        ? latestRequest.Message
                        : lastError,
                    IsRunning = latestRequest.IsRunning,
                    QueueDepth = latestRequest.QueueDepth,
                    LastRunAt = GetNullableDateTime(reader, "LastAttemptAtUtc")
                        ?? latestRequest.LastRunAt,
                    LastSuccessfulRunAt = GetNullableDateTime(reader, "LastSuccessfulAtUtc")
                };
            },
            cancellationToken);

    public WhdSyncRequestResult RequestWhdSync() =>
        RequestWhdSyncAsync().GetAwaiter().GetResult();

    public Task<WhdSyncRequestResult> RequestWhdSyncAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.RequestWhdSync,
            command =>
            {
                AddRequiredText(command, "@RequestType", 40, "Full");
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            async (reader, token) =>
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return new WhdSyncRequestResult
                    {
                        Accepted = true,
                        Message = "The server sync request was queued."
                    };
                }

                var status = GetString(reader, "Status");
                return new WhdSyncRequestResult
                {
                    Accepted = status.Equals("Queued", StringComparison.OrdinalIgnoreCase)
                        || status.Equals("AlreadyQueued", StringComparison.OrdinalIgnoreCase),
                    Message = status.Equals("AlreadyQueued", StringComparison.OrdinalIgnoreCase)
                        ? "A server synchronization request is already queued or running."
                        : "The server sync request was queued.",
                    QueueDepth = 1
                };
            },
            cancellationToken);

    public SageSyncServiceStatus GetSageSyncStatus() =>
        GetSageSyncStatusAsync().GetAwaiter().GetResult();

    public Task<SageSyncServiceStatus> GetSageSyncStatusAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.GetSageSyncStatus,
            null,
            async (reader, token) =>
            {
                var latestRequest = new SageSyncServiceStatus();
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    latestRequest = ReadSageSyncServiceStatus(reader);
                }

                if (!await reader.NextResultAsync(token).ConfigureAwait(false)
                    || !await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return latestRequest;
                }

                var lastError = GetString(reader, "LastError");
                return new SageSyncServiceStatus
                {
                    LatestRequestId = latestRequest.LatestRequestId,
                    ConfirmedRequestId = latestRequest.ConfirmedRequestId,
                    Health = string.IsNullOrWhiteSpace(lastError)
                        ? latestRequest.Health
                        : "Error",
                    Message = string.IsNullOrWhiteSpace(lastError)
                        ? latestRequest.Message
                        : lastError,
                    IsRunning = latestRequest.IsRunning,
                    QueueDepth = latestRequest.QueueDepth,
                    LastRunAt = GetNullableDateTime(reader, "LastAttemptAtUtc")
                        ?? latestRequest.LastRunAt,
                    LastSuccessfulRunAt = GetNullableDateTime(reader, "LastSuccessfulAtUtc")
                        ?? latestRequest.LastSuccessfulRunAt,
                    LastReadCount = latestRequest.LastReadCount,
                    LastSavedCount = latestRequest.LastSavedCount,
                    LastStaleCount = latestRequest.LastStaleCount,
                    ExistingCount = latestRequest.ExistingCount,
                    AllowLargeRemoval = latestRequest.AllowLargeRemoval,
                    RequiresLargeRemovalConfirmation = latestRequest.RequiresLargeRemovalConfirmation
                };
            },
            cancellationToken);

    public SageSyncRequestResult RequestSageSync(
        bool allowLargeRemoval = false,
        Guid? confirmedRequestId = null) =>
        RequestSageSyncAsync(allowLargeRemoval, confirmedRequestId).GetAwaiter().GetResult();

    public Task<SageSyncRequestResult> RequestSageSyncAsync(
        bool allowLargeRemoval = false,
        Guid? confirmedRequestId = null,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.RequestSageSync,
            command =>
            {
                AddGuid(command, "@RequestId", Guid.NewGuid());
                AddBit(command, "@AllowLargeRemoval", allowLargeRemoval);
                AddGuid(command, "@ConfirmedRequestId", confirmedRequestId);
            },
            async (reader, token) =>
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return new SageSyncRequestResult
                    {
                        Accepted = true,
                        Message = allowLargeRemoval
                            ? "The confirmed server Sage customer synchronization request was queued."
                            : "The server Sage customer synchronization request was queued.",
                        AllowLargeRemoval = allowLargeRemoval,
                        ConfirmedRequestId = confirmedRequestId
                    };
                }

                var status = GetString(reader, "Status");
                var alreadyQueued = status.Equals(
                    "AlreadyQueued",
                    StringComparison.OrdinalIgnoreCase);
                var effectiveAllowLargeRemoval = GetBoolean(
                    reader,
                    "AllowLargeRemoval",
                    false);
                var approvalNotQueued = alreadyQueued
                    && allowLargeRemoval
                    && !effectiveAllowLargeRemoval;
                return new SageSyncRequestResult
                {
                    RequestId = GetNullableGuid(reader, "RequestId"),
                    Accepted = status.Equals("Queued", StringComparison.OrdinalIgnoreCase)
                        || (alreadyQueued && !approvalNotQueued),
                    Message = alreadyQueued
                        ? approvalNotQueued
                            ? "Another server Sage customer synchronization is already queued or running; the large-removal approval was not queued."
                            : "A server Sage customer synchronization is already queued or running."
                        : effectiveAllowLargeRemoval
                            ? "The confirmed server Sage customer synchronization request was queued."
                            : "The server Sage customer synchronization request was queued.",
                    QueueDepth = GetInt32(reader, "QueueDepth", 1),
                    AllowLargeRemoval = effectiveAllowLargeRemoval,
                    ConfirmedRequestId = GetNullableGuid(reader, "ConfirmedRequestId")
                };
            },
            cancellationToken);

    public IReadOnlyList<WhdUserMapping> GetWhdUserMappings() =>
        GetWhdUserMappingsAsync().GetAwaiter().GetResult();

    public Task<IReadOnlyList<WhdUserMapping>> GetWhdUserMappingsAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.GetWhdUserMappings,
            null,
            (reader, token) => ReadListAsync(reader, token, ReadWhdUserMapping),
            cancellationToken);

    public IReadOnlyList<WhdTechnician> GetWhdTechnicians() =>
        GetWhdTechniciansAsync().GetAwaiter().GetResult();

    public Task<IReadOnlyList<WhdTechnician>> GetWhdTechniciansAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.GetWhdTechnicians,
            null,
            (reader, token) => ReadListAsync(reader, token, ReadWhdTechnician),
            cancellationToken);

    public WhdUserMapping SaveWhdUserMapping(WhdUserMapping mapping) =>
        SaveWhdUserMappingAsync(mapping).GetAwaiter().GetResult();

    public async Task<WhdUserMapping> SaveWhdUserMappingAsync(
        WhdUserMapping mapping,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return await QueryAsync(
                Procedures.SaveWhdUserMapping,
                command =>
                {
                    AddRequiredText(command, "@WindowsLoginName", 256, mapping.LoginName);
                    AddText(
                        command,
                        "@TechnicianExternalId",
                        120,
                        mapping.WhdTechnicianExternalId);
                },
                async (reader, token) =>
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        return mapping;
                    }

                    return ReadWhdUserMapping(reader);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static WhdSyncServiceStatus ReadWhdSyncServiceStatus(SqlDataReader reader) => new()
    {
        Health = GetString(reader, "Status", "Unknown"),
        Message = GetString(reader, "Message"),
        IsRunning = GetString(reader, "Status").Equals("Running", StringComparison.OrdinalIgnoreCase),
        QueueDepth = GetInt32(
            reader,
            "QueueDepth",
            GetString(reader, "Status").Equals("Queued", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
        LastRunAt = GetNullableDateTime(reader, "RequestedAtUtc"),
        LastSuccessfulRunAt = GetNullableDateTime(reader, "CompletedAtUtc")
    };

    private static SageSyncServiceStatus ReadSageSyncServiceStatus(SqlDataReader reader)
    {
        var status = GetString(reader, "Status", "Idle");
        return new SageSyncServiceStatus
        {
            LatestRequestId = GetNullableGuid(reader, "RequestId"),
            ConfirmedRequestId = GetNullableGuid(reader, "ConfirmedRequestId"),
            Health = status,
            Message = GetString(reader, "Message"),
            IsRunning = status.Equals("Running", StringComparison.OrdinalIgnoreCase),
            QueueDepth = GetInt32(
                reader,
                "QueueDepth",
                status.Equals("Queued", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
            LastRunAt = GetNullableDateTime(reader, "RequestedAtUtc"),
            LastSuccessfulRunAt = status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
                ? GetNullableDateTime(reader, "CompletedAtUtc")
                : null,
            LastReadCount = GetNullableInt32(reader, "ReadCount"),
            LastSavedCount = GetNullableInt32(reader, "SavedCount"),
            LastStaleCount = GetNullableInt32(reader, "StaleCount"),
            ExistingCount = GetNullableInt32(reader, "ExistingCount"),
            AllowLargeRemoval = GetBoolean(reader, "AllowLargeRemoval", false),
            RequiresLargeRemovalConfirmation = GetBoolean(
                reader,
                "RequiresLargeRemovalConfirmation",
                false)
        };
    }

    private static WhdUserMapping ReadWhdUserMapping(SqlDataReader reader) => new()
    {
        Id = GetInt32(reader, "Id"),
        UserSid = GetString(reader, "UserSid"),
        LoginName = GetString(reader, "LoginName", GetString(reader, "UserName")),
        DisplayName = GetString(reader, "DisplayName"),
        WhdTechnicianExternalId = GetNullableString(reader, "TechnicianExternalId"),
        WhdTechnicianName = GetString(reader, "TechnicianDisplayName", GetString(reader, "TechnicianName"))
    };

    private static WhdTechnician ReadWhdTechnician(SqlDataReader reader) => new()
    {
        ExternalId = GetString(reader, "ExternalId", GetString(reader, "TechnicianExternalId")),
        Name = GetString(reader, "DisplayName", GetString(reader, "Name")),
        Username = GetString(reader, "Username", GetString(reader, "Email")),
        IsActive = GetBoolean(reader, "IsActive", true)
    };
}
