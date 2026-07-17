using Microsoft.Data.SqlClient;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TechBench.Models;

namespace TechBench.Data;

public sealed partial class SqlServerTechBenchRepository
{
    public V1ImportReferenceResolution ResolveV1ImportReferences(
        V1DatabaseImportPackage package) =>
        ResolveV1ImportReferencesAsync(package).GetAwaiter().GetResult();

    public async Task<V1ImportReferenceResolution> ResolveV1ImportReferencesAsync(
        V1DatabaseImportPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        var cache = new Dictionary<V1ReferenceCacheKey, V1ResolvedReference>();
        var matchedClients = 0;
        var unmatchedClients = 0;
        var matchedTickets = 0;
        var unmatchedTickets = 0;

        foreach (var row in package.WorkEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = BuildReferenceCacheKey(row);
            if (!cache.TryGetValue(key, out var reference))
            {
                reference = await ResolveV1ReferenceAsync(row, cancellationToken)
                    .ConfigureAwait(false);
                cache[key] = reference;
            }

            ApplyResolvedReference(row, reference);
            if (reference.ClientId.HasValue)
            {
                matchedClients++;
            }
            else
            {
                unmatchedClients++;
            }

            if (IsTicketReferenceRequested(row))
            {
                if (reference.TicketId.HasValue)
                {
                    matchedTickets++;
                }
                else
                {
                    unmatchedTickets++;
                }
            }
        }

        return new V1ImportReferenceResolution(
            matchedClients,
            unmatchedClients,
            matchedTickets,
            unmatchedTickets);
    }

    public V1DatabaseImportResult ImportV1Database(V1DatabaseImportPackage package) =>
        ImportV1DatabaseAsync(package).GetAwaiter().GetResult();

    public void AbandonV1Import() =>
        AbandonV1ImportAsync().GetAwaiter().GetResult();

    public async Task AbandonV1ImportAsync(
        CancellationToken cancellationToken = default)
    {
        await QueryAsync(
                Procedures.AbandonTechBenchV1Import,
                command =>
                {
                    AddGuid(command, "@BatchId", null);
                    AddMaxText(
                        command,
                        "@Message",
                        "Abandoned by the user before starting a different TechBench V1 import.");
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                async (reader, token) =>
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(
                            $"{Procedures.AbandonTechBenchV1Import} did not return the abandoned import batch.");
                    }

                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<V1DatabaseImportResult> ImportV1DatabaseAsync(
        V1DatabaseImportPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(package.FileName))
        {
            throw new ArgumentException("The V1 database file name is required.", nameof(package));
        }

        ValidateSha256(package.FileHash, nameof(package.FileHash));
        var totalRead = checked(
            package.WorkEntries.Count
            + package.Links.Count
            + package.PostingLogs.Count);
        var begin = await BeginV1ImportAsync(
                package.FileName,
                package.FileHash,
                totalRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (begin.AlreadyImported)
        {
            return new V1DatabaseImportResult(
                begin.BatchId,
                WorkEntriesImported: 0,
                WorkEntriesSkipped: package.WorkEntries.Count,
                LinksImported: 0,
                LinksSkipped: package.Links.Count,
                PostingLogsImported: 0,
                PostingLogsSkipped: package.PostingLogs.Count,
                ConflictCount: 0,
                ConflictMessages: []);
        }

        var workImported = 0;
        var workSkipped = 0;
        var linksImported = 0;
        var linksSkipped = 0;
        var logsImported = 0;
        var logsSkipped = 0;
        var conflicts = new List<string>();
        var conflictedWorkEntryIds = new HashSet<long>();
        var batchFinalized = false;

        try
        {
            foreach (var row in package.WorkEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = await ImportV1WorkEntryAsync(
                        begin.BatchId,
                        row,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (CountOutcome(
                    outcome,
                    "work entry",
                    row.LegacyId,
                    ref workImported,
                    ref workSkipped,
                    conflicts))
                {
                    conflictedWorkEntryIds.Add(row.LegacyId);
                }
            }

            foreach (var row in package.Links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (conflictedWorkEntryIds.Contains(row.SourceLegacyWorkEntryId)
                    || conflictedWorkEntryIds.Contains(row.TargetLegacyWorkEntryId))
                {
                    conflicts.Add(
                        $"V1 work-entry link #{row.LegacyId}: a referenced work entry conflicted and the link was not imported");
                    continue;
                }

                var outcome = await ImportV1WorkEntryLinkAsync(
                        begin.BatchId,
                        row,
                        cancellationToken)
                    .ConfigureAwait(false);
                CountOutcome(
                    outcome,
                    "work-entry link",
                    row.LegacyId,
                    ref linksImported,
                    ref linksSkipped,
                    conflicts);
            }

            foreach (var row in package.PostingLogs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (conflictedWorkEntryIds.Contains(row.LegacyWorkEntryId))
                {
                    conflicts.Add(
                        $"V1 posting log #{row.LegacyId}: its work entry conflicted and the posting record was not imported");
                    continue;
                }

                var outcome = await ImportV1PostingLogAsync(
                        begin.BatchId,
                        row,
                        cancellationToken)
                    .ConfigureAwait(false);
                CountOutcome(
                    outcome,
                    "posting log",
                    row.LegacyId,
                    ref logsImported,
                    ref logsSkipped,
                    conflicts);
            }

            await CompleteV1ImportAsync(
                    begin.BatchId,
                    succeeded: true,
                    totalRead,
                    workImported + linksImported + logsImported,
                    workSkipped + linksSkipped + logsSkipped,
                    conflicts.Count,
                    errorCount: 0,
                    conflicts.Count == 0
                        ? "TechBench V1 personal data import completed."
                        : $"Import completed with {conflicts.Count} unchanged server-side conflict(s).",
                    cancellationToken)
                .ConfigureAwait(false);
            batchFinalized = true;

            return new V1DatabaseImportResult(
                begin.BatchId,
                workImported,
                workSkipped,
                linksImported,
                linksSkipped,
                logsImported,
                logsSkipped,
                conflicts.Count,
                conflicts);
        }
        catch (Exception ex)
        {
            if (!batchFinalized)
            {
                var resolvedCount = workImported + workSkipped
                    + linksImported + linksSkipped
                    + logsImported + logsSkipped
                    + conflicts.Count;
                var errorCount = resolvedCount < totalRead ? 1 : 0;
                try
                {
                    await CompleteV1ImportAsync(
                            begin.BatchId,
                            succeeded: false,
                            totalRead,
                            workImported + linksImported + logsImported,
                            workSkipped + linksSkipped + logsSkipped,
                            conflicts.Count,
                            errorCount,
                            ex.Message,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original import error. A same-file retry can
                    // resume a Started batch because every item is idempotent.
                }
            }

            throw;
        }
    }

    private async Task<V1ResolvedReference> ResolveV1ReferenceAsync(
        V1WorkEntryImportRow row,
        CancellationToken cancellationToken)
    {
        var rawClientSource = NormalizeReference(row.LegacyClientSource);
        var clientExternalId = rawClientSource is null
            ? null
            : NormalizeReference(row.LegacyClientExternalId);
        var clientSource = clientExternalId is null
            ? null
            : rawClientSource;
        var sageCustomerId = NormalizeReference(row.LegacyClientSageCustomerId);
        var rawTicketSource = NormalizeReference(row.LegacyTicketSource);
        var ticketExternalId = rawTicketSource is null
            ? null
            : NormalizeReference(row.LegacyTicketExternalId);
        var ticketSource = ticketExternalId is null
            ? null
            : rawTicketSource;
        var ticketNumber = ResolveLegacyTicketNumber(row);

        if (clientExternalId is not null || sageCustomerId is not null)
        {
            var identityResult = await QueryV1ReferenceAsync(
                    clientSource,
                    clientExternalId,
                    sageCustomerId,
                    clientName: null,
                    ticketSource,
                    ticketExternalId,
                    ticketNumber,
                    cancellationToken)
                .ConfigureAwait(false);
            if (identityResult.ClientMatched)
            {
                return identityResult;
            }

            if (identityResult.ClientResolutionStatus.Equals(
                    "Ambiguous",
                    StringComparison.OrdinalIgnoreCase)
                || identityResult.ClientResolutionStatus.Equals(
                    "Conflict",
                    StringComparison.OrdinalIgnoreCase)
                || identityResult.ClientResolutionStatus.Equals(
                    "InvalidInput",
                    StringComparison.OrdinalIgnoreCase))
            {
                return V1ResolvedReference.Unresolved(
                    identityResult.ClientResolutionStatus);
            }
        }

        V1ResolvedReference? matched = null;
        var blockedByAmbiguity = false;
        foreach (var clientName in GetLegacyClientNames(row))
        {
            var nameResult = await QueryV1ReferenceAsync(
                    clientSourceSystem: null,
                    clientExternalId: null,
                    sageCustomerId: null,
                    clientName,
                    ticketSource,
                    ticketExternalId,
                    ticketNumber,
                    cancellationToken)
                .ConfigureAwait(false);
            if (nameResult.ClientMatched)
            {
                if (matched is not null
                    && matched.ClientId != nameResult.ClientId)
                {
                    return V1ResolvedReference.Unresolved("Conflict");
                }

                matched ??= nameResult;
                continue;
            }

            if (nameResult.ClientResolutionStatus.Equals(
                    "Ambiguous",
                    StringComparison.OrdinalIgnoreCase)
                || nameResult.ClientResolutionStatus.Equals(
                    "Conflict",
                    StringComparison.OrdinalIgnoreCase)
                || nameResult.ClientResolutionStatus.Equals(
                    "InvalidInput",
                    StringComparison.OrdinalIgnoreCase))
            {
                blockedByAmbiguity = true;
            }
        }

        return matched is not null && !blockedByAmbiguity
            ? matched
            : V1ResolvedReference.Unresolved(
                blockedByAmbiguity ? "Ambiguous" : "NotFound");
    }

    private Task<V1ResolvedReference> QueryV1ReferenceAsync(
        string? clientSourceSystem,
        string? clientExternalId,
        string? sageCustomerId,
        string? clientName,
        string? ticketSourceSystem,
        string? ticketExternalId,
        string? ticketNumber,
        CancellationToken cancellationToken) =>
        QueryAsync(
            Procedures.ResolveTechBenchV1Reference,
            command =>
            {
                AddText(command, "@ClientSourceSystem", 40, clientSourceSystem);
                AddText(command, "@ClientExternalId", 500, clientExternalId);
                AddText(command, "@SageCustomerId", 120, sageCustomerId);
                AddText(command, "@ClientName", 240, clientName);
                AddText(command, "@TicketSourceSystem", 40, ticketSourceSystem);
                AddText(command, "@TicketExternalId", 240, ticketExternalId);
                AddText(command, "@TicketNumber", 120, ticketNumber);
            },
            async (reader, token) =>
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"{Procedures.ResolveTechBenchV1Reference} did not return a reference resolution.");
                }

                var clientStatus = GetString(reader, "ClientResolutionStatus");
                var ticketStatus = GetString(reader, "TicketResolutionStatus");
                return new V1ResolvedReference(
                    clientStatus,
                    clientStatus.Equals("Matched", StringComparison.OrdinalIgnoreCase)
                        ? GetNullableInt32(reader, "ClientId")
                        : null,
                    GetNullableString(reader, "ClientMatchMethod"),
                    ticketStatus,
                    ticketStatus.Equals("Matched", StringComparison.OrdinalIgnoreCase)
                        ? GetNullableInt32(reader, "TicketId")
                        : null,
                    GetNullableString(reader, "TicketMatchMethod"));
            },
            cancellationToken);

    private static void ApplyResolvedReference(
        V1WorkEntryImportRow row,
        V1ResolvedReference reference)
    {
        var entry = row.WorkEntry
            ?? throw new InvalidOperationException(
                $"V1 work entry {row.LegacyId} did not contain work-entry data.");
        var clientName = ResolveLegacyClientName(row);
        var ticketNumber = ResolveLegacyTicketNumber(row);

        row.ResolvedClientId = reference.ClientId;
        row.ResolvedTicketId = reference.ClientId.HasValue
            ? reference.TicketId
            : null;
        entry.Id = 0;
        entry.RowVersion = null;
        entry.PersonalNoteRowVersion = null;
        entry.ClientId = row.ResolvedClientId;
        entry.ClientName = clientName;
        entry.ManualClientName = row.ResolvedClientId.HasValue
            ? null
            : clientName;
        entry.TicketId = row.ResolvedTicketId;
        entry.TicketNumber = ticketNumber;
        entry.TicketSubject = row.LegacyTicketSubject;
        entry.TicketNumberText = row.ResolvedTicketId.HasValue
            ? null
            : ticketNumber;
    }

    private static V1ReferenceCacheKey BuildReferenceCacheKey(
        V1WorkEntryImportRow row) =>
        new(
            NormalizeReference(row.LegacyClientSource),
            NormalizeReference(row.LegacyClientExternalId),
            NormalizeReference(row.LegacyClientSageCustomerId),
            NormalizeReference(row.LegacyClientName),
            NormalizeReference(row.LegacyClientWhdLocationName),
            NormalizeReference(row.LegacyClientSageCustomerName),
            NormalizeReference(row.LegacyTicketClientName),
            NormalizeReference(row.WorkEntry.ManualClientName),
            NormalizeReference(row.WorkEntry.ClientName),
            NormalizeReference(row.LegacyTicketSource),
            NormalizeReference(row.LegacyTicketExternalId),
            ResolveLegacyTicketNumber(row));

    private static IReadOnlyList<string> GetLegacyClientNames(
        V1WorkEntryImportRow row) =>
        new[]
        {
            row.LegacyClientName,
            row.LegacyClientWhdLocationName,
            row.LegacyClientSageCustomerName,
            row.LegacyTicketClientName,
            row.WorkEntry.ManualClientName,
            row.WorkEntry.ClientName
        }
        .Select(NormalizeReference)
        .Where(static value => value is not null)
        .Select(static value => value!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string ResolveLegacyClientName(V1WorkEntryImportRow row) =>
        GetLegacyClientNames(row).FirstOrDefault()
        ?? $"V1 client {row.LegacyClientId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";

    private static string? ResolveLegacyTicketNumber(V1WorkEntryImportRow row) =>
        NormalizeReference(row.LegacyTicketNumber)
        ?? NormalizeReference(row.WorkEntry.TicketNumberText)
        ?? NormalizeReference(row.WorkEntry.TicketNumber);

    private static bool IsTicketReferenceRequested(V1WorkEntryImportRow row) =>
        row.LegacyTicketId.HasValue
        || NormalizeReference(row.LegacyTicketExternalId) is not null
        || ResolveLegacyTicketNumber(row) is not null;

    private static string? NormalizeReference(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<V1ImportBeginState> BeginV1ImportAsync(
        string fileName,
        string fileHash,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        try
        {
            return await QueryAsync(
                    Procedures.BeginTechBenchV1Import,
                    command =>
                    {
                        AddRequiredText(command, "@FileName", 500, fileName);
                        AddRequiredText(command, "@FileHash", 64, fileHash.ToUpperInvariant());
                        AddInt(command, "@ExpectedCount", expectedCount);
                        AddGuid(command, "@DeviceId", DeviceId);
                        AddGuid(command, "@RequestId", Guid.NewGuid());
                    },
                    async (reader, token) =>
                    {
                        if (!await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            throw new InvalidOperationException(
                                $"{Procedures.BeginTechBenchV1Import} did not return an import batch.");
                        }

                        var batchValue = GetValue(reader, "BatchId")
                            ?? throw new InvalidOperationException("The V1 import batch ID was missing.");
                        return new V1ImportBeginState(
                            batchValue is Guid batchId
                                ? batchId
                                : Guid.Parse(Convert.ToString(batchValue)!),
                            GetBoolean(reader, "AlreadyImported"),
                            GetBoolean(reader, "Resumed"));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number == 51602)
        {
            throw new V1ImportInProgressException(
                "A different TechBench V1 import for this Windows user is still incomplete.",
                ex);
        }
    }

    private Task<V1ImportItemOutcome> ImportV1WorkEntryAsync(
        Guid batchId,
        V1WorkEntryImportRow row,
        CancellationToken cancellationToken)
    {
        ValidateSha256(row.ContentHash, $"work entry {row.LegacyId} content hash");
        var entry = row.WorkEntry
            ?? throw new InvalidOperationException(
                $"V1 work entry {row.LegacyId} did not contain work-entry data.");
        var effectiveContentHash = BuildEffectiveWorkEntryHash(row, entry);
        return QueryImportOutcomeAsync(
            Procedures.ImportTechBenchV1WorkEntry,
            command =>
            {
                AddGuid(command, "@BatchId", batchId);
                AddBigInt(command, "@LegacyId", row.LegacyId);
                AddRequiredText(command, "@ContentHash", 64, effectiveContentHash);
                AddDate(command, "@WorkDate", entry.WorkDate);
                AddInt(command, "@ClientId", row.ResolvedClientId);
                AddText(command, "@ManualClientName", 240, entry.ManualClientName);
                AddInt(command, "@TicketId", row.ResolvedTicketId);
                AddText(command, "@TicketNumberText", 120, entry.TicketNumberText);
                AddBit(command, "@HasTimeRange", entry.HasTimeRange);
                AddTime(command, "@StartTime", entry.StartTime);
                AddTime(command, "@EndTime", entry.EndTime);
                AddInt(command, "@DurationMinutes", entry.DurationMinutes);
                AddBit(command, "@Billable", entry.Billable);
                AddMaxText(command, "@Note", entry.Note);
                AddMaxText(command, "@PersonalNote", entry.InternalNote);
                AddBit(command, "@IncludePersonalNoteInWhd", entry.IncludePersonalNoteInWhd);
                AddRequiredText(command, "@Tags", 1000, entry.Tags);
                AddRequiredText(command, "@FollowUpState", 30, entry.FollowUpState.ToString());
                AddDate(command, "@FollowUpDueDate", entry.FollowUpDueDate);
                AddBit(command, "@WhdPosted", entry.WhdPosted);
                AddDateTime(command, "@WhdPostedAtUtc", entry.WhdPostedAt);
                AddBit(command, "@SagePosted", entry.SagePosted);
                AddDateTime(command, "@SagePostedAtUtc", entry.SagePostedAt);
                AddText(command, "@SageTicketNumber", 120, entry.SageTicketNumber);
                AddRequiredText(command, "@LegacyPostingStatus", 40, entry.PostingStatus.ToString());
                AddMaxText(command, "@LastError", entry.LastError);
                AddDateTime(command, "@CreatedAtUtc", entry.CreatedAt);
                AddDateTime(command, "@UpdatedAtUtc", entry.UpdatedAt);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            cancellationToken);
    }

    private Task<V1ImportItemOutcome> ImportV1WorkEntryLinkAsync(
        Guid batchId,
        V1WorkEntryLinkImportRow row,
        CancellationToken cancellationToken)
    {
        ValidateSha256(row.ContentHash, $"work-entry link {row.LegacyId} content hash");
        return QueryImportOutcomeAsync(
            Procedures.ImportTechBenchV1WorkEntryLink,
            command =>
            {
                AddGuid(command, "@BatchId", batchId);
                AddBigInt(command, "@LegacyId", row.LegacyId);
                AddRequiredText(command, "@ContentHash", 64, row.ContentHash.ToUpperInvariant());
                AddBigInt(command, "@LegacySourceWorkEntryId", row.SourceLegacyWorkEntryId);
                AddBigInt(command, "@LegacyTargetWorkEntryId", row.TargetLegacyWorkEntryId);
                AddRequiredText(command, "@LinkType", 30, row.LinkType.ToString());
                AddDateTime(command, "@CreatedAtUtc", row.CreatedAtUtc);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            cancellationToken);
    }

    private Task<V1ImportItemOutcome> ImportV1PostingLogAsync(
        Guid batchId,
        V1PostingLogImportRow row,
        CancellationToken cancellationToken)
    {
        ValidateSha256(row.ContentHash, $"posting log {row.LegacyId} content hash");
        return QueryImportOutcomeAsync(
            Procedures.ImportTechBenchV1PostingLog,
            command =>
            {
                AddGuid(command, "@BatchId", batchId);
                AddBigInt(command, "@LegacyId", row.LegacyId);
                AddRequiredText(command, "@ContentHash", 64, row.ContentHash.ToUpperInvariant());
                AddBigInt(command, "@LegacyWorkEntryId", row.LegacyWorkEntryId);
                AddRequiredText(command, "@Destination", 40, row.Destination);
                AddMaxText(command, "@Payload", row.Payload);
                AddBit(command, "@Success", row.Success);
                AddMaxText(command, "@Message", row.Message);
                AddText(command, "@ExternalReference", 500, row.ExternalReference);
                AddDateTime(command, "@CreatedAtUtc", row.CreatedAtUtc);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            cancellationToken);
    }

    private Task<V1ImportItemOutcome> QueryImportOutcomeAsync(
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
                    throw new InvalidOperationException(
                        $"{procedure} did not return an item outcome.");
                }

                return new V1ImportItemOutcome(
                    GetString(reader, "Outcome"),
                    GetValue(reader, "NewEntityId") is { } entityId
                        ? Convert.ToInt64(entityId)
                        : null,
                    GetNullableString(reader, "Message"));
            },
            cancellationToken);

    private Task CompleteV1ImportAsync(
        Guid batchId,
        bool succeeded,
        int readCount,
        int importedCount,
        int skippedCount,
        int conflictCount,
        int errorCount,
        string? message,
        CancellationToken cancellationToken) =>
        QueryAsync(
            Procedures.CompleteTechBenchV1Import,
            command =>
            {
                AddGuid(command, "@BatchId", batchId);
                AddBit(command, "@Succeeded", succeeded);
                AddInt(command, "@ReadCount", readCount);
                AddInt(command, "@ImportedCount", importedCount);
                AddInt(command, "@SkippedCount", skippedCount);
                AddInt(command, "@ConflictCount", conflictCount);
                AddInt(command, "@ErrorCount", errorCount);
                AddMaxText(command, "@Message", message);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            async (reader, token) =>
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"{Procedures.CompleteTechBenchV1Import} did not finalize the import batch.");
                }

                return true;
            },
            cancellationToken);

    private static bool CountOutcome(
        V1ImportItemOutcome outcome,
        string entityLabel,
        long legacyId,
        ref int imported,
        ref int skipped,
        ICollection<string> conflicts)
    {
        if (outcome.Imported)
        {
            imported++;
            return false;
        }

        if (outcome.Skipped)
        {
            skipped++;
            return false;
        }

        if (outcome.Conflict)
        {
            conflicts.Add(
                $"V1 {entityLabel} #{legacyId}: {outcome.Message ?? "the source differs from its prior import"}");
            return true;
        }

        throw new InvalidOperationException(
            $"The server returned an unknown V1 import outcome '{outcome.Outcome}' for {entityLabel} #{legacyId}.");
    }

    private static string BuildEffectiveWorkEntryHash(
        V1WorkEntryImportRow row,
        WorkEntry entry)
    {
        var values = new[]
        {
            "TechBenchV1WorkEntryResolvedV1",
            row.ContentHash.ToUpperInvariant(),
            row.ResolvedClientId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            row.ResolvedTicketId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            entry.ManualClientName?.Trim() ?? string.Empty,
            entry.TicketNumberText?.Trim() ?? string.Empty
        };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void ValidateSha256(string? value, string parameterName)
    {
        if (value is null
            || value.Length != 64
            || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "A 64-character hexadecimal SHA-256 value is required.",
                parameterName);
        }
    }

    private sealed record V1ImportBeginState(
        Guid BatchId,
        bool AlreadyImported,
        bool Resumed);

    private sealed record V1ResolvedReference(
        string ClientResolutionStatus,
        int? ClientId,
        string? ClientMatchMethod,
        string TicketResolutionStatus,
        int? TicketId,
        string? TicketMatchMethod)
    {
        public bool ClientMatched =>
            ClientId.HasValue
            && ClientResolutionStatus.Equals(
                "Matched",
                StringComparison.OrdinalIgnoreCase);

        public static V1ResolvedReference Unresolved(string status) =>
            new(status, null, null, "NotResolved", null, null);
    }

    private sealed record V1ReferenceCacheKey(
        string? ClientSourceSystem,
        string? ClientExternalId,
        string? SageCustomerId,
        string? ClientName,
        string? ClientWhdLocationName,
        string? ClientSageCustomerName,
        string? TicketClientName,
        string? ManualClientName,
        string? WorkEntryClientName,
        string? TicketSourceSystem,
        string? TicketExternalId,
        string? TicketNumber);
}
