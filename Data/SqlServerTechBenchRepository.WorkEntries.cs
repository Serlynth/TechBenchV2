using Microsoft.Data.SqlClient;
using TechBench.Models;

namespace TechBench.Data;

public sealed partial class SqlServerTechBenchRepository
{
    public IReadOnlyList<WorkEntry> GetWorkEntries(WorkEntryQuery query) =>
        GetWorkEntriesAsync(query).GetAwaiter().GetResult();

    public Task<IReadOnlyList<WorkEntry>> GetWorkEntriesAsync(
        WorkEntryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return QueryAsync(
            Procedures.SearchWorkEntries,
            command =>
            {
                AddDate(command, "@StartDate", query.StartDate);
                AddDate(command, "@EndDate", query.EndDate);
                AddInt(command, "@ClientId", query.ClientId);
                AddInt(command, "@TicketId", query.TicketId);
                AddInt(command, "@ExcludeId", query.ExcludeId);
                AddText(command, "@TicketText", 120, query.TicketText);
                AddText(command, "@PostingStatus", 40, query.PostingStatus?.ToString());
                AddText(command, "@Keyword", 240, query.Keyword);
                AddText(command, "@Tags", 500, query.Tags);
                AddText(command, "@FollowUpState", 30, query.FollowUpState?.ToString());
                AddBit(command, "@OpenFollowUpsOnly", query.OpenFollowUpsOnly);
                AddBit(command, "@PendingWhdOnly", query.PendingWhdOnly);
                AddBit(command, "@PendingSageOnly", query.PendingSageOnly);
                AddBit(command, "@PendingAnyOnly", query.PendingAnyOnly);
                AddBit(command, "@IncludeAllUsers", false);
                AddInt(
                    command,
                    "@Limit",
                    Math.Clamp(query.MaxResults ?? 500, 1, 500));
            },
            (reader, token) => ReadListAsync(reader, token, ReadWorkEntry),
            cancellationToken);
    }

    public IReadOnlyList<string> GetDistinctTags() =>
        GetDistinctTagsAsync().GetAwaiter().GetResult();

    public Task<IReadOnlyList<string>> GetDistinctTagsAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.GetDistinctTags,
            command => AddBit(command, "@IncludeAllUsers", false),
            async (reader, token) =>
            {
                var tags = new List<string>();
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var tag = GetString(reader, "Tag", GetString(reader, "Tags"));
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        tags.Add(tag);
                    }
                }

                return (IReadOnlyList<string>)tags;
            },
            cancellationToken);

    public WorkEntry? GetWorkEntry(int id) =>
        GetWorkEntryAsync(id).GetAwaiter().GetResult();

    public Task<WorkEntry?> GetWorkEntryAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            return Task.FromResult<WorkEntry?>(null);
        }

        return QueryAsync(
            Procedures.GetWorkEntry,
            command =>
            {
                AddInt(command, "@Id", id);
                AddBit(command, "@IncludeAllUsers", false);
            },
            (reader, token) => ReadSingleAsync(reader, token, ReadWorkEntry),
            cancellationToken);
    }

    public int SaveWorkEntry(WorkEntry entry) =>
        SaveWorkEntryAsync(entry).GetAwaiter().GetResult();

    public async Task<int> SaveWorkEntryAsync(
        WorkEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var saved = await QueryAsync(
                Procedures.SaveWorkEntry,
                command => AddWorkEntryParameters(command, entry),
                (reader, token) => ReadSingleAsync(reader, token, ReadWorkEntry),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{Procedures.SaveWorkEntry} did not return the saved work entry.");
        CopyWorkEntry(saved, entry);
        return entry.Id;
    }

    public int ImportWorkEntries(
        IEnumerable<WorkEntry> entries,
        IReadOnlyDictionary<string, int>? clientAliases = null) =>
        ImportWorkEntriesAsync(entries, clientAliases).GetAwaiter().GetResult();

    public async Task<int> ImportWorkEntriesAsync(
        IEnumerable<WorkEntry> entries,
        IReadOnlyDictionary<string, int>? clientAliases = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var materialized = entries.ToList();
        var batchId = await BeginImportBatchAsync(
                "WorklogCsv",
                materialized.Count,
                cancellationToken)
            .ConfigureAwait(false);
        var savedCount = 0;
        try
        {
            foreach (var entry in materialized)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SaveWorkEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                savedCount++;
            }

            if (clientAliases is not null)
            {
                foreach (var (alias, clientId) in clientAliases)
                {
                    if (string.IsNullOrWhiteSpace(alias) || clientId <= 0)
                    {
                        continue;
                    }

                    await SaveClientAliasAsync(
                            alias,
                            clientId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await ExecuteNonQueryAsync(
                            Procedures.AddImportLegacyMapping,
                            command =>
                            {
                                AddGuid(command, "@BatchId", batchId);
                                AddRequiredText(command, "@LegacyValue", 240, alias);
                                AddRequiredText(command, "@EntityType", 80, "Client");
                                AddBigInt(command, "@EntityId", clientId);
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            await CompleteImportBatchAsync(
                    batchId,
                    succeeded: true,
                    savedCount,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            return savedCount;
        }
        catch (Exception ex)
        {
            try
            {
                await CompleteImportBatchAsync(
                        batchId,
                        succeeded: false,
                        savedCount,
                        ex.Message,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original import failure.
            }

            throw;
        }
    }

    public void DeleteWorkEntry(int id) =>
        DeleteWorkEntryAsync(id).GetAwaiter().GetResult();

    public async Task DeleteWorkEntryAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await ExecuteNonQueryAsync(
                Procedures.DeleteWorkEntry,
                command =>
                {
                    AddInt(command, "@Id", id);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        GetTrackedRowVersion("WorkEntry", id));
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                cancellationToken)
            .ConfigureAwait(false);
        _rowVersions.TryRemove(BuildRowVersionKey("WorkEntry", id), out _);
        _rowVersions.TryRemove(BuildRowVersionKey("PersonalNote", id), out _);
    }

    public IReadOnlyList<WorkEntryLink> GetWorkEntryLinks(int workEntryId) =>
        GetWorkEntryLinksAsync(workEntryId).GetAwaiter().GetResult();

    public async Task<IReadOnlyList<WorkEntryLink>> GetWorkEntryLinksAsync(
        int workEntryId,
        CancellationToken cancellationToken = default)
    {
        if (workEntryId <= 0)
        {
            return [];
        }

        var seeds = await QueryAsync(
                Procedures.GetWorkEntryLinks,
                command => AddBigInt(command, "@WorkEntryId", workEntryId),
                (reader, token) => ReadListAsync(reader, token, row =>
                {
                    var sourceId = GetInt32(row, "SourceWorkEntryId");
                    var targetId = GetInt32(row, "TargetWorkEntryId");
                    var linkId = GetInt32(row, "LinkId", GetInt32(row, "Id"));
                    TrackRowVersion("WorkEntryLink", linkId, row);
                    return new WorkEntryLinkSeed(
                        linkId,
                        sourceId,
                        targetId,
                        GetEnum(row, "LinkType", WorkEntryLinkType.Related),
                        GetDateTime(row, "CreatedAt", DateTime.MinValue),
                        sourceId == workEntryId ? targetId : sourceId);
                }),
                cancellationToken)
            .ConfigureAwait(false);

        var links = new List<WorkEntryLink>(seeds.Count);
        foreach (var seed in seeds)
        {
            var related = await GetWorkEntryAsync(seed.RelatedWorkEntryId, cancellationToken)
                .ConfigureAwait(false);
            if (related is null)
            {
                continue;
            }

            links.Add(new WorkEntryLink
            {
                Id = seed.Id,
                SourceWorkEntryId = seed.SourceWorkEntryId,
                TargetWorkEntryId = seed.TargetWorkEntryId,
                CurrentWorkEntryId = workEntryId,
                LinkType = seed.LinkType,
                CreatedAt = seed.CreatedAt,
                RelatedEntry = related
            });
        }

        return links;
    }

    public int SaveWorkEntryLink(
        int sourceWorkEntryId,
        int targetWorkEntryId,
        WorkEntryLinkType linkType) =>
        SaveWorkEntryLinkAsync(sourceWorkEntryId, targetWorkEntryId, linkType)
            .GetAwaiter()
            .GetResult();

    public Task<int> SaveWorkEntryLinkAsync(
        int sourceWorkEntryId,
        int targetWorkEntryId,
        WorkEntryLinkType linkType,
        CancellationToken cancellationToken = default)
    {
        if (sourceWorkEntryId <= 0 || targetWorkEntryId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceWorkEntryId),
                "Both linked notes must already be saved.");
        }

        if (sourceWorkEntryId == targetWorkEntryId)
        {
            throw new InvalidOperationException("A note cannot be linked to itself.");
        }

        return QueryAsync(
            Procedures.SaveWorkEntryLink,
            command =>
            {
                AddInt(command, "@SourceWorkEntryId", sourceWorkEntryId);
                AddInt(command, "@TargetWorkEntryId", targetWorkEntryId);
                AddRequiredText(command, "@LinkType", 40, linkType.ToString());
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            async (reader, token) =>
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"{Procedures.SaveWorkEntryLink} did not return a link.");
                }

                var id = GetInt32(reader, "LinkId", GetInt32(reader, "Id"));
                TrackRowVersion("WorkEntryLink", id, reader);
                return id;
            },
            cancellationToken);
    }

    public void DeleteWorkEntryLink(int linkId) =>
        DeleteWorkEntryLinkAsync(linkId).GetAwaiter().GetResult();

    public async Task DeleteWorkEntryLinkAsync(
        int linkId,
        CancellationToken cancellationToken = default)
    {
        await ExecuteNonQueryAsync(
                Procedures.DeleteWorkEntryLink,
                command =>
                {
                    AddInt(command, "@Id", linkId);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        GetTrackedRowVersion("WorkEntryLink", linkId));
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                cancellationToken)
            .ConfigureAwait(false);
        _rowVersions.TryRemove(
            BuildRowVersionKey("WorkEntryLink", linkId),
            out _);
    }

    private WorkEntry ReadWorkEntry(SqlDataReader reader)
    {
        var entry = new WorkEntry
        {
            Id = GetInt32(reader, "Id"),
            WorkDate = GetDate(reader, "WorkDate", DateTime.Today),
            ClientId = GetNullableInt32(reader, "ClientId"),
            ManualClientName = GetNullableString(reader, "ManualClientName"),
            TicketId = GetNullableInt32(reader, "TicketId"),
            TicketNumberText = GetNullableString(reader, "TicketNumberText"),
            HasTimeRange = GetBoolean(reader, "HasTimeRange", true),
            StartTime = GetTimeSpan(reader, "StartTime"),
            EndTime = GetTimeSpan(reader, "EndTime"),
            DurationMinutes = GetInt32(reader, "DurationMinutes"),
            Billable = GetBoolean(reader, "Billable", true),
            Note = GetString(reader, "Note"),
            InternalNote = GetNullableString(reader, "PersonalNote")
                ?? GetNullableString(reader, "InternalNote"),
            IncludePersonalNoteInWhd =
                GetBoolean(reader, "IncludePersonalNoteInWhd"),
            Tags = GetString(reader, "Tags"),
            FollowUpState = GetEnum(reader, "FollowUpState", FollowUpState.None),
            FollowUpDueDate = GetNullableDateTime(reader, "FollowUpDueDate")?.Date,
            WhdPosted = GetBoolean(reader, "WhdPosted"),
            WhdPostedAt = GetNullableDateTime(reader, "WhdPostedAt")
                ?? GetNullableDateTime(reader, "WhdPostedAtUtc"),
            SagePosted = GetBoolean(reader, "SagePosted"),
            SagePostedAt = GetNullableDateTime(reader, "SagePostedAt")
                ?? GetNullableDateTime(reader, "SagePostedAtUtc"),
            SageTicketNumber = GetNullableString(reader, "SageTicketNumber"),
            PostingStatus = GetEnum(reader, "PostingStatus", PostingStatus.Draft),
            LastError = GetNullableString(reader, "LastError"),
            CreatedAt = GetDateTime(reader, "CreatedAt", DateTime.Now),
            UpdatedAt = GetDateTime(reader, "UpdatedAt", DateTime.Now),
            ClientName = GetString(reader, "ClientName"),
            TicketNumber = GetNullableString(reader, "TicketNumber"),
            TicketSubject = GetNullableString(reader, "TicketSubject"),
            SearchSnippet = GetNullableString(reader, "SearchSnippet")
        };
        TrackRowVersion("WorkEntry", entry.Id, reader);
        entry.RowVersion = GetBytes(reader, "RowVersion");
        var personalNoteVersion = GetBytes(reader, "PersonalNoteRowVersion");
        entry.PersonalNoteRowVersion = personalNoteVersion;
        if (personalNoteVersion is { Length: > 0 })
        {
            _rowVersions[BuildRowVersionKey("PersonalNote", entry.Id)] =
                personalNoteVersion;
        }

        return entry;
    }

    private void AddWorkEntryParameters(SqlCommand command, WorkEntry entry)
    {
        AddBigInt(command, "@Id", entry.Id > 0 ? entry.Id : null);
        AddDate(command, "@WorkDate", entry.WorkDate);
        AddInt(command, "@ClientId", entry.ClientId);
        AddText(command, "@ManualClientName", 240, entry.ManualClientName);
        AddInt(command, "@TicketId", entry.TicketId);
        AddText(command, "@TicketNumberText", 120, entry.TicketNumberText);
        AddBit(command, "@HasTimeRange", entry.HasTimeRange);
        AddTime(command, "@StartTime", entry.StartTime);
        AddTime(command, "@EndTime", entry.EndTime);
        AddInt(command, "@DurationMinutes", entry.DurationMinutes);
        AddBit(command, "@Billable", entry.Billable);
        AddMaxText(command, "@Note", entry.Note);
        AddMaxText(command, "@PersonalNote", entry.InternalNote);
        AddBit(
            command,
            "@IncludePersonalNoteInWhd",
            entry.IncludePersonalNoteInWhd);
        AddText(command, "@Tags", 1000, entry.Tags);
        AddRequiredText(command, "@FollowUpState", 30, entry.FollowUpState.ToString());
        AddDate(command, "@FollowUpDueDate", entry.FollowUpDueDate);
        var editablePostingStatus = entry.PostingStatus == PostingStatus.Draft
            ? PostingStatus.Draft
            : PostingStatus.Ready;
        AddRequiredText(
            command,
            "@PostingStatus",
            40,
            editablePostingStatus.ToString());
        AddMaxText(command, "@LastError", entry.LastError);
        AddBinary(
            command,
            "@ExpectedRowVersion",
            8,
            entry.RowVersion
            ?? GetTrackedRowVersion("WorkEntry", entry.Id));
        AddBinary(
            command,
            "@ExpectedPersonalNoteRowVersion",
            8,
            entry.PersonalNoteRowVersion
            ?? GetTrackedRowVersion("PersonalNote", entry.Id));
        AddGuid(command, "@RequestId", Guid.NewGuid());
    }

    private async Task<Guid> BeginImportBatchAsync(
        string source,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        return await QueryAsync(
                Procedures.BeginImportBatch,
                command =>
                {
                    AddRequiredText(command, "@Source", 120, source);
                    AddInt(command, "@ExpectedCount", expectedCount);
                    AddGuid(command, "@DeviceId", DeviceId);
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                async (reader, token) =>
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(
                            $"{Procedures.BeginImportBatch} did not return a batch ID.");
                    }

                    var value = GetValue(reader, "BatchId")
                        ?? GetValue(reader, "ImportBatchId");
                    return value is Guid id
                        ? id
                        : Guid.Parse(Convert.ToString(value)!);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task CompleteImportBatchAsync(
        Guid batchId,
        bool succeeded,
        int importedCount,
        string? message,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
                Procedures.CompleteImportBatch,
                command =>
                {
                    AddGuid(command, "@BatchId", batchId);
                    AddBit(command, "@Succeeded", succeeded);
                    AddInt(command, "@ImportedCount", importedCount);
                    AddMaxText(command, "@Message", message);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void CopyWorkEntry(WorkEntry source, WorkEntry target)
    {
        target.Id = source.Id;
        target.WorkDate = source.WorkDate;
        target.ClientId = source.ClientId;
        target.ManualClientName = source.ManualClientName;
        target.TicketId = source.TicketId;
        target.TicketNumberText = source.TicketNumberText;
        target.HasTimeRange = source.HasTimeRange;
        target.StartTime = source.StartTime;
        target.EndTime = source.EndTime;
        target.DurationMinutes = source.DurationMinutes;
        target.Billable = source.Billable;
        target.Note = source.Note;
        target.InternalNote = source.InternalNote;
        target.IncludePersonalNoteInWhd = source.IncludePersonalNoteInWhd;
        target.Tags = source.Tags;
        target.FollowUpState = source.FollowUpState;
        target.FollowUpDueDate = source.FollowUpDueDate;
        target.WhdPosted = source.WhdPosted;
        target.WhdPostedAt = source.WhdPostedAt;
        target.SagePosted = source.SagePosted;
        target.SagePostedAt = source.SagePostedAt;
        target.SageTicketNumber = source.SageTicketNumber;
        target.PostingStatus = source.PostingStatus;
        target.LastError = source.LastError;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
        target.ClientName = source.ClientName;
        target.TicketNumber = source.TicketNumber;
        target.TicketSubject = source.TicketSubject;
        target.SearchSnippet = source.SearchSnippet;
        target.RowVersion = source.RowVersion;
        target.PersonalNoteRowVersion = source.PersonalNoteRowVersion;
    }

    private sealed record WorkEntryLinkSeed(
        int Id,
        int SourceWorkEntryId,
        int TargetWorkEntryId,
        WorkEntryLinkType LinkType,
        DateTime CreatedAt,
        int RelatedWorkEntryId);
}
