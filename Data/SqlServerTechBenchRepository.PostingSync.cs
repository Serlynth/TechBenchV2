using Microsoft.Data.SqlClient;
using TechBench.Models;

namespace TechBench.Data;

public sealed partial class SqlServerTechBenchRepository
{
    public void AddPostingLog(PostingLog log) =>
        AddPostingLogAsync(log).GetAwaiter().GetResult();

    public async Task AddPostingLogAsync(
        PostingLog log,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        var saved = await QueryAsync(
                Procedures.AddPostingLog,
                command =>
                {
                    AddBigInt(command, "@WorkEntryId", log.WorkEntryId);
                    AddRequiredText(command, "@Destination", 40, log.Destination);
                    AddMaxText(command, "@Payload", log.Payload);
                    AddBit(command, "@Success", log.Success);
                    AddMaxText(command, "@Message", log.Message);
                    AddText(
                        command,
                        "@ExternalReference",
                        500,
                        log.ExternalReference);
                    AddDateTime(command, "@CreatedAtUtc", log.CreatedAt);
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                (reader, token) => ReadSingleAsync(reader, token, ReadPostingLog),
                cancellationToken)
            .ConfigureAwait(false);
        if (saved is not null)
        {
            CopyPostingLog(saved, log);
        }
    }

    public PostingLog? GetLatestVerifiedWhdPostingLog(int workEntryId) =>
        GetLatestVerifiedWhdPostingLogAsync(workEntryId).GetAwaiter().GetResult();

    public Task<PostingLog?> GetLatestVerifiedWhdPostingLogAsync(
        int workEntryId,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.GetLatestVerifiedWhdPostingLog,
            command => AddBigInt(command, "@WorkEntryId", workEntryId),
            (reader, token) => ReadSingleAsync(reader, token, ReadPostingLog),
            cancellationToken);

    public PostingAttemptStartResult TryBeginPostingAttempt(
        int workEntryId,
        string destination,
        string attemptKey,
        string payloadHash) =>
        TryBeginPostingAttemptAsync(
                workEntryId,
                destination,
                attemptKey,
                payloadHash)
            .GetAwaiter()
            .GetResult();

    public async Task<PostingAttemptStartResult> TryBeginPostingAttemptAsync(
        int workEntryId,
        string destination,
        string attemptKey,
        string payloadHash,
        CancellationToken cancellationToken = default)
    {
        var started = await QueryAsync(
                Procedures.BeginPostingAttempt,
                command =>
                {
                    AddBigInt(command, "@WorkEntryId", workEntryId);
                    AddRequiredText(command, "@Destination", 40, destination);
                    AddRequiredText(command, "@AttemptKey", 120, attemptKey);
                    AddRequiredText(command, "@PayloadHash", 64, payloadHash);
                    AddGuid(command, "@DeviceId", DeviceId);
                    AddInt(command, "@LeaseSeconds", PostingLeaseSeconds);
                },
                async (reader, token) =>
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        return false;
                    }

                    return GetBoolean(reader, "Started", true);
                },
                cancellationToken)
            .ConfigureAwait(false);

        var active = await GetOutstandingPostingAttemptAsync(
                workEntryId,
                destination,
                cancellationToken)
            .ConfigureAwait(false);
        return started
            ? new PostingAttemptStartResult(true, active, null)
            : new PostingAttemptStartResult(false, null, active);
    }

    public PostingAttempt? GetOutstandingPostingAttempt(
        int workEntryId,
        string destination) =>
        GetOutstandingPostingAttemptAsync(workEntryId, destination)
            .GetAwaiter()
            .GetResult();

    public Task<PostingAttempt?> GetOutstandingPostingAttemptAsync(
        int workEntryId,
        string destination,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.GetOutstandingPostingAttempt,
            command =>
            {
                AddBigInt(command, "@WorkEntryId", workEntryId);
                AddRequiredText(command, "@Destination", 40, destination);
            },
            (reader, token) => ReadSingleAsync(reader, token, ReadPostingAttempt),
            cancellationToken);

    public void CompletePostingAttempt(
        int attemptId,
        PostingAttemptStatus status,
        string message,
        string? externalReference = null,
        bool markPosted = true) =>
        CompletePostingAttemptAsync(
                attemptId,
                status,
                message,
                externalReference,
                markPosted)
            .GetAwaiter()
            .GetResult();

    public async Task CompletePostingAttemptAsync(
        int attemptId,
        PostingAttemptStatus status,
        string message,
        string? externalReference = null,
        bool markPosted = true,
        CancellationToken cancellationToken = default)
    {
        if (status == PostingAttemptStatus.Started)
        {
            throw new ArgumentException(
                "A completed posting attempt cannot remain Started.",
                nameof(status));
        }

        await ExecuteNonQueryAsync(
                Procedures.CompletePostingAttempt,
                command =>
                {
                    AddBigInt(command, "@AttemptId", attemptId);
                    AddRequiredText(command, "@Status", 40, status.ToString());
                    AddMaxText(command, "@Message", message);
                    AddText(
                        command,
                        "@ExternalReference",
                        500,
                        externalReference);
                    AddBit(command, "@MarkPosted", markPosted);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public int ResolveOutstandingPostingAttempts(
        int workEntryId,
        string destination,
        string message,
        string? externalReference = null) =>
        ResolveOutstandingPostingAttemptsAsync(
                workEntryId,
                destination,
                message,
                externalReference)
            .GetAwaiter()
            .GetResult();

    public Task<int> ResolveOutstandingPostingAttemptsAsync(
        int workEntryId,
        string destination,
        string message,
        string? externalReference = null,
        CancellationToken cancellationToken = default) =>
        ReadAffectedCountAsync(
            Procedures.ResolveOutstandingPostingAttempts,
            command =>
            {
                AddBigInt(command, "@WorkEntryId", workEntryId);
                AddRequiredText(command, "@Destination", 40, destination);
                AddMaxText(command, "@Message", message);
                AddText(command, "@ExternalReference", 500, externalReference);
            },
            cancellationToken);

    public int AbandonOutstandingPostingAttempts(
        int workEntryId,
        string destination,
        string message) =>
        AbandonOutstandingPostingAttemptsAsync(workEntryId, destination, message)
            .GetAwaiter()
            .GetResult();

    public Task<int> AbandonOutstandingPostingAttemptsAsync(
        int workEntryId,
        string destination,
        string message,
        CancellationToken cancellationToken = default) =>
        ReadAffectedCountAsync(
            Procedures.AbandonOutstandingPostingAttempts,
            command =>
            {
                AddBigInt(command, "@WorkEntryId", workEntryId);
                AddRequiredText(command, "@Destination", 40, destination);
                AddMaxText(command, "@Message", message);
            },
            cancellationToken);

    public void MarkWorkEntryPosted(
        int workEntryId,
        string destination,
        string message,
        string? externalReference = null) =>
        MarkWorkEntryPostedAsync(
                workEntryId,
                destination,
                message,
                externalReference)
            .GetAwaiter()
            .GetResult();

    public async Task MarkWorkEntryPostedAsync(
        int workEntryId,
        string destination,
        string message,
        string? externalReference = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteNonQueryAsync(
                Procedures.MarkWorkEntryPosted,
                command =>
                {
                    AddInt(command, "@WorkEntryId", workEntryId);
                    AddRequiredText(command, "@Destination", 40, destination);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        GetTrackedRowVersion("WorkEntry", workEntryId));
                    AddMaxText(command, "@Message", message);
                    AddText(
                        command,
                        "@ExternalReference",
                        500,
                        externalReference);
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                cancellationToken)
            .ConfigureAwait(false);
        _rowVersions.TryRemove(
            BuildRowVersionKey("WorkEntry", workEntryId),
            out _);
    }

    public bool HasSuccessfulSageDraftLog(int workEntryId) =>
        HasSuccessfulSageDraftLogAsync(workEntryId).GetAwaiter().GetResult();

    public Task<bool> HasSuccessfulSageDraftLogAsync(
        int workEntryId,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.HasSuccessfulSageDraftLog,
            command => AddBigInt(command, "@WorkEntryId", workEntryId),
            async (reader, token) =>
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return false;
                }

                return GetBoolean(
                    reader,
                    "HasSuccessfulLog",
                    GetBoolean(reader, "Exists"));
            },
            cancellationToken);

    public IReadOnlyList<PostingLog> GetPostingLogs(
        string? destination = null,
        bool? success = null,
        string? keyword = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int limit = 250) =>
        GetPostingLogsAsync(
                destination,
                success,
                keyword,
                startDate,
                endDate,
                limit)
            .GetAwaiter()
            .GetResult();

    public Task<IReadOnlyList<PostingLog>> GetPostingLogsAsync(
        string? destination = null,
        bool? success = null,
        string? keyword = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int limit = 250,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.GetPostingLogs,
            command =>
            {
                AddText(command, "@Destination", 40, destination);
                AddNullableBit(command, "@Success", success);
                AddText(command, "@Keyword", 240, keyword);
                AddDate(command, "@StartDate", startDate);
                AddDate(command, "@EndDate", endDate);
                AddInt(command, "@Limit", Math.Clamp(limit, 1, 1000));
            },
            (reader, token) => ReadListAsync(reader, token, ReadPostingLog),
            cancellationToken);

    public void SynchronizeWhdTickets(
        IReadOnlyList<WhdSyncedTicket> whdTickets,
        DateTime syncedAt,
        bool reconcileMissing) =>
        SynchronizeWhdTicketsAsync(whdTickets, syncedAt, reconcileMissing)
            .GetAwaiter()
            .GetResult();

    public async Task SynchronizeWhdTicketsAsync(
        IReadOnlyList<WhdSyncedTicket> whdTickets,
        DateTime syncedAt,
        bool reconcileMissing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(whdTickets);
        await ApplySyncSnapshotAsync(
                "WHD-Tickets",
                Procedures.ApplyWhdTicketSnapshot,
                whdTickets,
                syncedAt,
                reconcileMissing,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public int SynchronizeWhdClients(
        IReadOnlyList<WhdSyncedClient> whdClients,
        DateTime syncedAt,
        bool reconcileMissing = false) =>
        SynchronizeWhdClientsAsync(whdClients, syncedAt, reconcileMissing)
            .GetAwaiter()
            .GetResult();

    public async Task<int> SynchronizeWhdClientsAsync(
        IReadOnlyList<WhdSyncedClient> whdClients,
        DateTime syncedAt,
        bool reconcileMissing = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(whdClients);
        var result = await ApplySyncSnapshotAsync(
                "WHD-Clients",
                Procedures.ApplyWhdClientSnapshot,
                whdClients,
                syncedAt,
                reconcileMissing,
                cancellationToken)
            .ConfigureAwait(false);
        return result.MatchedCount;
    }

    public async Task ApplyTicketStatusSnapshotAsync(
        IReadOnlyList<WhdStatusType> statuses,
        DateTime syncedAt,
        bool reconcileMissing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        await ApplySyncSnapshotAsync(
                "WHD-TicketStatuses",
                Procedures.ApplyTicketStatusSnapshot,
                statuses,
                syncedAt,
                reconcileMissing,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SyncSnapshotResult> ApplySyncSnapshotAsync<T>(
        string source,
        string applyProcedure,
        IReadOnlyList<T> snapshot,
        DateTime syncedAt,
        bool reconcileMissing,
        CancellationToken cancellationToken)
    {
        var leaseId = await AcquireSyncLeaseAsync(source, cancellationToken)
            .ConfigureAwait(false);
        Guid? runId = null;
        try
        {
            runId = await BeginSyncRunAsync(source, leaseId, cancellationToken)
                .ConfigureAwait(false);
            var result = await QueryAsync(
                    applyProcedure,
                    command =>
                    {
                        command.CommandTimeout = Math.Max(
                            command.CommandTimeout,
                            300);
                        AddGuid(command, "@RunId", runId);
                        AddMaxText(command, "@SnapshotJson", SerializePayload(snapshot));
                        AddDateTime(command, "@SyncedAtUtc", syncedAt);
                        AddBit(command, "@ReconcileMissing", reconcileMissing);
                    },
                    async (reader, token) =>
                    {
                        if (!await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            return new SyncSnapshotResult(snapshot.Count, 0, 0);
                        }

                        return new SyncSnapshotResult(
                            GetInt32(
                                reader,
                                "SavedCount",
                                GetInt32(reader, "AppliedCount", snapshot.Count)),
                            GetInt32(reader, "StaleCount"),
                            GetInt32(reader, "MatchedCount"));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await CompleteSyncRunAsync(
                    runId.Value,
                    succeeded: true,
                    snapshot.Count,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            if (runId.HasValue)
            {
                try
                {
                    await CompleteSyncRunAsync(
                            runId.Value,
                            succeeded: false,
                            0,
                            ex.Message,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original synchronization failure.
                }
            }

            throw;
        }
        finally
        {
            try
            {
                await ReleaseSyncLeaseAsync(leaseId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The lease has a server-side expiry and can be recovered.
            }
        }
    }

    private Task<Guid> AcquireSyncLeaseAsync(
        string source,
        CancellationToken cancellationToken) =>
        ReadGuidAsync(
            Procedures.AcquireSyncLease,
            command =>
            {
                AddRequiredText(command, "@Source", 120, source);
                AddInt(command, "@LeaseSeconds", 300);
                AddGuid(command, "@DeviceId", DeviceId);
            },
            "LeaseId",
            cancellationToken);

    private Task<Guid> BeginSyncRunAsync(
        string source,
        Guid leaseId,
        CancellationToken cancellationToken) =>
        ReadGuidAsync(
            Procedures.BeginSyncRun,
            command =>
            {
                AddRequiredText(command, "@Source", 120, source);
                AddGuid(command, "@LeaseId", leaseId);
                AddGuid(command, "@DeviceId", DeviceId);
            },
            "RunId",
            cancellationToken);

    private async Task CompleteSyncRunAsync(
        Guid runId,
        bool succeeded,
        int itemCount,
        string? message,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
                Procedures.CompleteSyncRun,
                command =>
                {
                    AddGuid(command, "@RunId", runId);
                    AddBit(command, "@Succeeded", succeeded);
                    AddInt(command, "@ItemCount", itemCount);
                    AddMaxText(command, "@Message", message);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ReleaseSyncLeaseAsync(
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
                Procedures.ReleaseSyncLease,
                command =>
                {
                    AddGuid(command, "@LeaseId", leaseId);
                    AddGuid(command, "@DeviceId", DeviceId);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<Guid> ReadGuidAsync(
        string procedure,
        Action<SqlCommand> configure,
        string columnName,
        CancellationToken cancellationToken) =>
        QueryAsync(
            procedure,
            configure,
            async (reader, token) =>
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"{procedure} did not return {columnName}.");
                }

                var value = GetValue(reader, columnName);
                return value is Guid guid
                    ? guid
                    : Guid.Parse(Convert.ToString(value)!);
            },
            cancellationToken);

    private Task<int> ReadAffectedCountAsync(
        string procedure,
        Action<SqlCommand> configure,
        CancellationToken cancellationToken) =>
        QueryAsync(
            procedure,
            configure,
            async (reader, token) =>
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return 0;
                }

                return GetInt32(reader, "AffectedCount");
            },
            cancellationToken);

    private static PostingLog ReadPostingLog(SqlDataReader reader) =>
        new()
        {
            Id = GetInt32(reader, "Id"),
            WorkEntryId = GetInt32(reader, "WorkEntryId"),
            Destination = GetString(reader, "Destination"),
            Payload = GetString(reader, "Payload"),
            Success = GetBoolean(reader, "Success"),
            Message = GetString(reader, "Message"),
            ExternalReference = GetNullableString(reader, "ExternalReference"),
            CreatedAt = GetDateTime(reader, "CreatedAt", DateTime.Now)
        };

    private static PostingAttempt ReadPostingAttempt(SqlDataReader reader) =>
        new()
        {
            Id = GetInt32(reader, "Id", GetInt32(reader, "AttemptId")),
            WorkEntryId = GetInt32(reader, "WorkEntryId"),
            Destination = GetString(reader, "Destination"),
            AttemptKey = GetString(reader, "AttemptKey"),
            PayloadHash = GetString(reader, "PayloadHash"),
            Status = GetEnum(
                reader,
                "Status",
                PostingAttemptStatus.Unknown),
            Message = GetString(reader, "Message"),
            ExternalReference = GetNullableString(reader, "ExternalReference"),
            StartedAt = GetDateTime(reader, "StartedAt", DateTime.Now),
            CompletedAt = GetNullableDateTime(reader, "CompletedAt")
        };

    private static void CopyPostingLog(PostingLog source, PostingLog target)
    {
        target.Id = source.Id;
        target.WorkEntryId = source.WorkEntryId;
        target.Destination = source.Destination;
        target.Payload = source.Payload;
        target.Success = source.Success;
        target.Message = source.Message;
        target.ExternalReference = source.ExternalReference;
        target.CreatedAt = source.CreatedAt;
    }

    private sealed record SyncSnapshotResult(
        int SavedCount,
        int StaleCount,
        int MatchedCount);
}
