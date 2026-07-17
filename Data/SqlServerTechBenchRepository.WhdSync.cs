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
            command => AddGuid(command, "@RequestId", Guid.NewGuid()),
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
