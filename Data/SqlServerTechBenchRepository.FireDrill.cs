using System.Text.Json;
using TechBench.Models;

namespace TechBench.Data;

public sealed partial class SqlServerTechBenchRepository
{
    private static readonly JsonSerializerOptions FireDrillJsonOptions =
        new(JsonSerializerDefaults.Web);

    public IReadOnlyList<FireDrillCredentialSummary> SearchFireDrillCredentials(string? searchTerm = null) =>
        QueryAsync(
            Procedures.SearchFireDrillCredentials,
            command =>
            {
                AddText(command, "@Search", 240, searchTerm);
                AddInt(command, "@Limit", 500);
            },
            (reader, token) => ReadListAsync(reader, token, row => new FireDrillCredentialSummary(
                GetInt64(row, "CredentialId"),
                GetString(row, "ClientName"),
                GetString(row, "FireboxIp"),
                GetString(row, "Status"),
                GetDateTime(row, "LastSyncedAtUtc", DateTime.MinValue),
                ParseFireDrillFields(GetString(row, "FieldsJson")))),
            CancellationToken.None).GetAwaiter().GetResult();

    public FireDrillCredential? RevealFireDrillCredential(long credentialId)
    {
        if (credentialId <= 0) return null;
        return QueryAsync(
            Procedures.RevealFireDrillCredential,
            command => AddBigInt(command, "@CredentialId", credentialId),
            (reader, token) => ReadSingleAsync(reader, token, row => new FireDrillCredential(
                GetInt64(row, "CredentialId"),
                GetString(row, "ClientName"),
                GetString(row, "FireboxIp"),
                GetString(row, "Status"),
                GetDateTime(row, "LastSyncedAtUtc", DateTime.MinValue),
                ParseFireDrillFields(GetString(row, "FieldsJson")))),
            CancellationToken.None).GetAwaiter().GetResult();
    }

    public CredentialsSyncServiceStatus GetCredentialsSyncStatus() =>
        GetCredentialsSyncStatusAsync().GetAwaiter().GetResult();

    public Task<CredentialsSyncServiceStatus> GetCredentialsSyncStatusAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.GetCredentialsSyncStatus,
            null,
            async (reader, token) =>
            {
                var latestRequest = new CredentialsSyncServiceStatus();
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var status = GetString(reader, "Status", "NeverRun");
                    latestRequest = new CredentialsSyncServiceStatus
                    {
                        LatestRequestId = GetNullableGuid(reader, "RequestId"),
                        Health = status,
                        Message = GetString(reader, "Message"),
                        IsRunning = status.Equals(
                            "Running",
                            StringComparison.OrdinalIgnoreCase),
                        QueueDepth = GetInt32(
                            reader,
                            "QueueDepth",
                            status.Equals("Queued", StringComparison.OrdinalIgnoreCase)
                                ? 1
                                : 0),
                        LastRunAt = GetNullableDateTime(reader, "RequestedAtUtc"),
                        LastSuccessfulRunAt = status.Equals(
                            "Completed",
                            StringComparison.OrdinalIgnoreCase)
                                ? GetNullableDateTime(reader, "CompletedAtUtc")
                                : null,
                        LastReadCount = GetNullableInt32(reader, "ReadCount"),
                        LastSavedCount = GetNullableInt32(reader, "SavedCount"),
                        LastStaleCount = GetNullableInt32(reader, "StaleCount")
                    };
                }

                if (!await reader.NextResultAsync(token).ConfigureAwait(false)
                    || !await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return latestRequest;
                }

                var lastError = GetString(reader, "LastError");
                return new CredentialsSyncServiceStatus
                {
                    LatestRequestId = latestRequest.LatestRequestId,
                    Health = string.IsNullOrWhiteSpace(lastError)
                        || latestRequest.IsActive
                            ? latestRequest.Health
                            : "Failed",
                    Message = string.IsNullOrWhiteSpace(lastError)
                        || latestRequest.IsActive
                            ? latestRequest.Message
                            : lastError,
                    IsRunning = latestRequest.IsRunning,
                    QueueDepth = latestRequest.QueueDepth,
                    LastRunAt = GetNullableDateTime(reader, "LastAttemptAtUtc")
                        ?? latestRequest.LastRunAt,
                    LastSuccessfulRunAt = GetNullableDateTime(
                        reader,
                        "LastSuccessfulAtUtc")
                        ?? latestRequest.LastSuccessfulRunAt,
                    LastReadCount = latestRequest.LastReadCount,
                    LastSavedCount = latestRequest.LastSavedCount,
                    LastStaleCount = latestRequest.LastStaleCount
                };
            },
            cancellationToken);

    public CredentialsSyncRequestResult RequestCredentialsSync() =>
        RequestCredentialsSyncAsync().GetAwaiter().GetResult();

    public Task<CredentialsSyncRequestResult> RequestCredentialsSyncAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.RequestCredentialsSync,
            command => AddGuid(command, "@RequestId", Guid.NewGuid()),
            async (reader, token) =>
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return new CredentialsSyncRequestResult
                    {
                        Accepted = true,
                        Message = "The server Credentials synchronization was queued.",
                        QueueDepth = 1
                    };
                }

                var status = GetString(reader, "Status");
                var alreadyQueued = status.Equals(
                    "AlreadyQueued",
                    StringComparison.OrdinalIgnoreCase);
                return new CredentialsSyncRequestResult
                {
                    RequestId = GetNullableGuid(reader, "RequestId"),
                    Accepted = status.Equals("Queued", StringComparison.OrdinalIgnoreCase)
                        || alreadyQueued,
                    Message = alreadyQueued
                        ? "A server Credentials synchronization is already queued or running."
                        : "The server Credentials synchronization was queued.",
                    QueueDepth = 1
                };
            },
            cancellationToken);

    private static IReadOnlyList<FireDrillCredentialField> ParseFireDrillFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<List<FireDrillCredentialField>>(json, FireDrillJsonOptions)?
            .OrderBy(field => field.SortOrder)
            .ThenBy(field => field.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }
}
