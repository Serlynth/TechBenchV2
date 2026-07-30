using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TechBench.Models;

namespace TechBench.Services;

/// <summary>
/// Extracts importable, user-owned history from a selected TechBench V1
/// database. The reader never initializes, migrates, or writes the source.
/// </summary>
public sealed class V1DatabaseImportReader
{
    private const int BusyTimeoutMilliseconds = 5000;
    private const int MaxManualClientLength = 240;
    private const int MaxTicketTextLength = 120;
    private const int MaxTagsLength = 1000;
    private const int MaxClientNameLength = 240;
    private const int MaxClientSourceLength = 40;
    private const int MaxClientExternalIdLength = 500;
    private const int MaxSageCustomerIdLength = 120;
    private const int MaxTicketNumberLength = 120;
    private const int MaxTicketSubjectLength = 500;
    private const int MaxTicketStatusLength = 160;
    private const int MaxTicketSourceLength = 40;
    private const int MaxTicketExternalIdLength = 240;
    private const int MaxPostingDestinationLength = 40;
    private const int MaxPostingExternalReferenceLength = 500;

    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    private static readonly string[] SharedExcludedTables =
    [
        "Clients",
        "Tickets",
        "TicketStatusOptions",
        "ClientAliases",
        "Templates",
        "CommonLinks"
    ];

    private static readonly string[] OtherExcludedTables =
    [
        "Settings",
        "PostingAttempts",
        "EditorDrafts"
    ];

    private readonly Func<string, CancellationToken, Task>? _beforeFinalHash;

    public V1DatabaseImportReader()
    {
    }

    internal V1DatabaseImportReader(Func<string, CancellationToken, Task> beforeFinalHash)
    {
        _beforeFinalHash = beforeFinalHash
            ?? throw new ArgumentNullException(nameof(beforeFinalHash));
    }

    public V1DatabaseImportPackage Read(string sourcePath) =>
        ReadAsync(sourcePath).GetAwaiter().GetResult();

    public async Task<V1DatabaseImportPackage> ReadAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new V1DatabaseImportException("Select a TechBench V1 SQLite .db file.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new V1DatabaseImportException("The selected V1 database path is invalid.", innerException: ex);
        }

        if (!Path.GetExtension(fullPath).Equals(".db", StringComparison.OrdinalIgnoreCase))
        {
            throw new V1DatabaseImportException("Select a TechBench V1 SQLite file with a .db extension.");
        }

        if (!File.Exists(fullPath))
        {
            throw new V1DatabaseImportException($"The selected V1 database does not exist: {fullPath}");
        }

        EnsureNoActiveSqliteSidecars(fullPath);
        await ValidateSqliteHeaderAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var initialHash = await ComputeFileHashAsync(fullPath, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<V1WorkEntryImportRow> workEntries;
        IReadOnlyList<V1WorkEntryLinkImportRow> links;
        IReadOnlyList<V1PostingLogImportRow> postingLogs;
        IReadOnlyDictionary<string, int> excludedItemCounts;
        int excludedSharedItemCount;
        bool hasEditorDraft;

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = BusyTimeoutMilliseconds / 1000
            }.ToString();

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureReadOnlyConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

            // Microsoft.Data.Sqlite's asynchronous transaction overload starts an
            // immediate transaction. SQLite treats that as a prospective write,
            // which query_only correctly rejects. A deferred transaction acquires
            // a read snapshot only when the first validation query runs.
            await using var transaction = connection.BeginTransaction(deferred: true);

            await VerifyQuickCheckAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var schema = await ReadAndValidateSchemaAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            workEntries = await ReadWorkEntriesAsync(connection, transaction, schema, cancellationToken)
                .ConfigureAwait(false);
            var legacyWorkEntryIds = workEntries
                .Select(static row => row.LegacyId)
                .ToHashSet();
            links = await ReadLinksAsync(
                    connection,
                    transaction,
                    schema,
                    legacyWorkEntryIds,
                    cancellationToken)
                .ConfigureAwait(false);
            postingLogs = await ReadPostingLogsAsync(
                    connection,
                    transaction,
                    schema,
                    legacyWorkEntryIds,
                    cancellationToken)
                .ConfigureAwait(false);

            excludedItemCounts = await ReadExcludedCountsAsync(
                    connection,
                    transaction,
                    schema,
                    cancellationToken)
                .ConfigureAwait(false);
            excludedSharedItemCount = checked(SharedExcludedTables.Sum(
                table => excludedItemCounts.GetValueOrDefault(table)));
            hasEditorDraft = excludedItemCounts.GetValueOrDefault("EditorDrafts") > 0;

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (V1DatabaseImportException)
        {
            throw;
        }
        catch (SqliteException ex)
        {
            throw new V1DatabaseImportException(
                $"The selected V1 database could not be read safely. Close TechBench V1 or select a verified backup copy. {ex.Message}",
                innerException: ex);
        }

        if (_beforeFinalHash is not null)
        {
            await _beforeFinalHash(fullPath, cancellationToken).ConfigureAwait(false);
        }

        EnsureNoActiveSqliteSidecars(fullPath);
        var finalHash = await ComputeFileHashAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (!initialHash.Equals(finalHash, StringComparison.Ordinal))
        {
            throw new V1DatabaseImportException(
                "The selected V1 database changed while it was being read. Close TechBench V1 and retry using a verified backup copy.");
        }

        return new V1DatabaseImportPackage
        {
            SourcePath = fullPath,
            FileName = Path.GetFileName(fullPath),
            FileHash = initialHash,
            WorkEntries = workEntries,
            Links = links,
            PostingLogs = postingLogs,
            HasEditorDraft = hasEditorDraft,
            ExcludedSharedItemCount = excludedSharedItemCount,
            ExcludedItemCounts = excludedItemCounts
        };
    }

    private static async Task ConfigureReadOnlyConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
                connection,
                transaction: null,
                $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};",
                cancellationToken)
            .ConfigureAwait(false);
        await ExecuteNonQueryAsync(
                connection,
                transaction: null,
                "PRAGMA query_only = ON;",
                cancellationToken)
            .ConfigureAwait(false);
        await ExecuteNonQueryAsync(
                connection,
                transaction: null,
                "PRAGMA trusted_schema = OFF;",
                cancellationToken)
            .ConfigureAwait(false);

        var queryOnly = await ExecuteScalarAsync(
                connection,
                transaction: null,
                "PRAGMA query_only;",
                cancellationToken)
            .ConfigureAwait(false);
        var trustedSchema = await ExecuteScalarAsync(
                connection,
                transaction: null,
                "PRAGMA trusted_schema;",
                cancellationToken)
            .ConfigureAwait(false);
        if (Convert.ToInt64(queryOnly, CultureInfo.InvariantCulture) != 1
            || Convert.ToInt64(trustedSchema, CultureInfo.InvariantCulture) != 0)
        {
            throw new V1DatabaseImportException(
                "The SQLite connection could not be placed into the required read-only security mode.");
        }
    }

    private static async Task VerifyQuickCheckAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA quick_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(reader.IsDBNull(0) ? "(no result)" : reader.GetString(0));
        }

        if (results.Count != 1 || !results[0].Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            var detail = results.Count == 0 ? "no result" : string.Join("; ", results);
            throw new V1DatabaseImportException(
                $"SQLite quick_check failed for the selected V1 database: {detail}");
        }
    }

    private static async Task<V1Schema> ReadAndValidateSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    tables.Add(reader.GetString(0));
                }
            }
        }

        RequireTable(tables, "Clients");
        RequireTable(tables, "Tickets");
        RequireTable(tables, "WorkEntries");

        var columns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            if (!KnownTable(table))
            {
                continue;
            }

            columns[table] = await ReadTableColumnsAsync(
                    connection,
                    transaction,
                    table,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        RequireColumns(columns, "Clients", "Id", "Name", "Source", "ExternalId");
        RequireColumns(
            columns,
            "Tickets",
            "Id",
            "TicketNumber",
            "ClientId",
            "Subject",
            "Status",
            "Source",
            "ExternalId",
            "IsClosed");
        RequireColumns(
            columns,
            "WorkEntries",
            "Id",
            "WorkDate",
            "ClientId",
            "TicketId",
            "TicketNumberText",
            "StartTime",
            "EndTime",
            "DurationMinutes",
            "Billable",
            "Note",
            "InternalNote",
            "WhdPosted",
            "WhdPostedAt",
            "SagePosted",
            "SagePostedAt",
            "PostingStatus",
            "LastError",
            "CreatedAt",
            "UpdatedAt");

        if (tables.Contains("WorkEntryLinks"))
        {
            RequireColumns(
                columns,
                "WorkEntryLinks",
                "Id",
                "SourceWorkEntryId",
                "TargetWorkEntryId",
                "LinkType",
                "CreatedAt");
        }

        if (tables.Contains("PostingLogs"))
        {
            RequireColumns(
                columns,
                "PostingLogs",
                "Id",
                "WorkEntryId",
                "Destination",
                "Payload",
                "Success",
                "Message",
                "CreatedAt");
        }

        return new V1Schema(tables, columns);
    }

    private static async Task<HashSet<string>> ReadTableColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private static async Task<IReadOnlyList<V1WorkEntryImportRow>> ReadWorkEntriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        V1Schema schema,
        CancellationToken cancellationToken)
    {
        var workColumns = schema.Columns["WorkEntries"];
        var clientColumns = schema.Columns["Clients"];
        var ticketColumns = schema.Columns["Tickets"];

        var fields = new[]
        {
            Select("w", "Id", "NULL", "LegacyId", workColumns),
            Select("w", "WorkDate", "NULL", "WorkDate", workColumns),
            Select("w", "ClientId", "NULL", "WorkEntryClientId", workColumns),
            Select("w", "ManualClientName", "NULL", "ManualClientName", workColumns),
            Select("w", "TicketId", "NULL", "LegacyTicketId", workColumns),
            Select("w", "TicketNumberText", "NULL", "TicketNumberText", workColumns),
            Select("w", "HasTimeRange", "1", "HasTimeRange", workColumns),
            Select("w", "StartTime", "NULL", "StartTime", workColumns),
            Select("w", "EndTime", "NULL", "EndTime", workColumns),
            Select("w", "DurationMinutes", "NULL", "DurationMinutes", workColumns),
            Select("w", "Billable", "NULL", "Billable", workColumns),
            Select("w", "Note", "NULL", "Note", workColumns),
            Select("w", "InternalNote", "NULL", "InternalNote", workColumns),
            Select("w", "IncludePersonalNoteInWhd", "0", "IncludePersonalNoteInWhd", workColumns),
            Select("w", "Tags", "''", "Tags", workColumns),
            Select("w", "FollowUpState", "'None'", "FollowUpState", workColumns),
            Select("w", "FollowUpDueDate", "NULL", "FollowUpDueDate", workColumns),
            Select("w", "WhdPosted", "NULL", "WhdPosted", workColumns),
            Select("w", "WhdPostedAt", "NULL", "WhdPostedAt", workColumns),
            Select("w", "SagePosted", "NULL", "SagePosted", workColumns),
            Select("w", "SagePostedAt", "NULL", "SagePostedAt", workColumns),
            Select("w", "SageTicketNumber", "NULL", "SageTicketNumber", workColumns),
            Select("w", "PostingStatus", "NULL", "PostingStatus", workColumns),
            Select("w", "LastError", "NULL", "LastError", workColumns),
            Select("w", "CreatedAt", "NULL", "CreatedAt", workColumns),
            Select("w", "UpdatedAt", "NULL", "UpdatedAt", workColumns),
            "c.Id AS JoinedWorkEntryClientId",
            "t.Id AS JoinedLegacyTicketId",
            "t.ClientId AS LegacyTicketClientId",
            "tc.Id AS JoinedTicketClientId",
            PreferredSelect("c", "tc", "Name", "NULL", "LegacyClientName", clientColumns),
            PreferredSelect("c", "tc", "Source", "NULL", "LegacyClientSource", clientColumns),
            PreferredSelect("c", "tc", "ExternalId", "NULL", "LegacyClientExternalId", clientColumns),
            PreferredSelect("c", "tc", "WhdLocationName", "NULL", "LegacyClientWhdLocationName", clientColumns),
            PreferredSelect("c", "tc", "SageCustomerId", "NULL", "LegacyClientSageCustomerId", clientColumns),
            PreferredSelect("c", "tc", "SageCustomerName", "NULL", "LegacyClientSageCustomerName", clientColumns),
            "tc.Name AS LegacyTicketClientName",
            Select("t", "TicketNumber", "NULL", "LegacyTicketNumber", ticketColumns),
            Select("t", "Subject", "NULL", "LegacyTicketSubject", ticketColumns),
            Select("t", "Status", "NULL", "LegacyTicketStatus", ticketColumns),
            Select("t", "Source", "NULL", "LegacyTicketSource", ticketColumns),
            Select("t", "ExternalId", "NULL", "LegacyTicketExternalId", ticketColumns),
            Select("t", "WhdStatusTypeId", "NULL", "LegacyTicketWhdStatusTypeId", ticketColumns)
        };

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT
                {string.Join(",\n                ", fields)}
            FROM WorkEntries AS w
            LEFT JOIN Clients AS c ON c.Id = w.ClientId
            LEFT JOIN Tickets AS t ON t.Id = w.TicketId
            LEFT JOIN Clients AS tc ON tc.Id = t.ClientId
            ORDER BY w.Id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<V1WorkEntryImportRow>();
        var ids = new HashSet<long>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var legacyId = ReadRequiredInt64(reader, "LegacyId", "WorkEntries", legacyId: null);
            if (legacyId <= 0)
            {
                throw RowError("WorkEntries", legacyId, "Id must be a positive integer.");
            }

            if (!ids.Add(legacyId))
            {
                throw RowError("WorkEntries", legacyId, "Id is duplicated.");
            }

            var workEntryClientId = ReadNullableInt64(reader, "WorkEntryClientId", "WorkEntries", legacyId);
            var joinedWorkEntryClientId = ReadNullableInt64(reader, "JoinedWorkEntryClientId", "WorkEntries", legacyId);
            var legacyTicketId = ReadNullableInt64(reader, "LegacyTicketId", "WorkEntries", legacyId);
            var joinedLegacyTicketId = ReadNullableInt64(reader, "JoinedLegacyTicketId", "WorkEntries", legacyId);
            var legacyTicketClientId = ReadNullableInt64(reader, "LegacyTicketClientId", "WorkEntries", legacyId);
            var joinedTicketClientId = ReadNullableInt64(reader, "JoinedTicketClientId", "WorkEntries", legacyId);

            ValidateReference(workEntryClientId, joinedWorkEntryClientId, "client", "WorkEntries", legacyId);
            ValidateReference(legacyTicketId, joinedLegacyTicketId, "ticket", "WorkEntries", legacyId);
            if (legacyTicketId.HasValue)
            {
                ValidateReference(
                    legacyTicketClientId,
                    joinedTicketClientId,
                    "ticket client",
                    "WorkEntries",
                    legacyId);
            }

            var manualClientName = ReadNullableText(reader, "ManualClientName", "WorkEntries", legacyId);
            var ticketNumberText = ReadNullableText(reader, "TicketNumberText", "WorkEntries", legacyId);
            var tags = ReadRequiredText(reader, "Tags", "WorkEntries", legacyId);
            var sageTicketNumber = ReadNullableText(reader, "SageTicketNumber", "WorkEntries", legacyId);
            ValidateOptionalLength(manualClientName, MaxManualClientLength, "ManualClientName", "WorkEntries", legacyId);
            ValidateOptionalLength(ticketNumberText, MaxTicketTextLength, "TicketNumberText", "WorkEntries", legacyId);
            ValidateLength(tags, MaxTagsLength, "Tags", "WorkEntries", legacyId);
            ValidateOptionalLength(sageTicketNumber, MaxTicketTextLength, "SageTicketNumber", "WorkEntries", legacyId);

            var legacyClientId = workEntryClientId ?? legacyTicketClientId;
            var legacyClientName = ReadNullableText(reader, "LegacyClientName", "WorkEntries", legacyId);
            var legacyClientSource = ReadNullableText(reader, "LegacyClientSource", "WorkEntries", legacyId);
            var legacyClientExternalId = ReadNullableText(reader, "LegacyClientExternalId", "WorkEntries", legacyId);
            var legacyClientWhdLocationName = ReadNullableText(reader, "LegacyClientWhdLocationName", "WorkEntries", legacyId);
            var legacyClientSageCustomerId = ReadNullableText(reader, "LegacyClientSageCustomerId", "WorkEntries", legacyId);
            var legacyClientSageCustomerName = ReadNullableText(reader, "LegacyClientSageCustomerName", "WorkEntries", legacyId);
            ValidateIdentityClient(
                legacyClientId,
                legacyClientName,
                legacyClientSource,
                legacyClientExternalId,
                legacyClientWhdLocationName,
                legacyClientSageCustomerId,
                legacyClientSageCustomerName,
                legacyId);

            if (!legacyClientId.HasValue && string.IsNullOrWhiteSpace(manualClientName))
            {
                throw RowError(
                    "WorkEntries",
                    legacyId,
                    "A client reference or ManualClientName is required.");
            }

            var legacyTicketClientName = ReadNullableText(reader, "LegacyTicketClientName", "WorkEntries", legacyId);
            var legacyTicketNumber = ReadNullableText(reader, "LegacyTicketNumber", "WorkEntries", legacyId);
            var legacyTicketSubject = ReadNullableText(reader, "LegacyTicketSubject", "WorkEntries", legacyId);
            var legacyTicketStatus = ReadNullableText(reader, "LegacyTicketStatus", "WorkEntries", legacyId);
            var legacyTicketSource = ReadNullableText(reader, "LegacyTicketSource", "WorkEntries", legacyId);
            var legacyTicketExternalId = ReadNullableText(reader, "LegacyTicketExternalId", "WorkEntries", legacyId);
            var legacyTicketWhdStatusTypeId = ReadNullableInt32(
                reader,
                "LegacyTicketWhdStatusTypeId",
                "WorkEntries",
                legacyId);
            if (legacyTicketWhdStatusTypeId is <= 0)
            {
                throw RowError(
                    "WorkEntries",
                    legacyId,
                    "LegacyTicketWhdStatusTypeId must be positive when present.");
            }
            ValidateIdentityTicket(
                legacyTicketId,
                legacyTicketNumber,
                legacyTicketSubject,
                legacyTicketStatus,
                legacyTicketSource,
                legacyTicketExternalId,
                legacyId);

            var durationMinutes = ReadRequiredInt32(reader, "DurationMinutes", "WorkEntries", legacyId);
            if (durationMinutes is < 0 or > 1440)
            {
                throw RowError(
                    "WorkEntries",
                    legacyId,
                    $"DurationMinutes must be between 0 and 1440; found {durationMinutes}.");
            }

            var workDate = ParseDate(
                ReadRequiredText(reader, "WorkDate", "WorkEntries", legacyId),
                "WorkDate",
                "WorkEntries",
                legacyId);
            var startTime = ParseTime(
                ReadRequiredText(reader, "StartTime", "WorkEntries", legacyId),
                "StartTime",
                "WorkEntries",
                legacyId);
            var endTime = ParseTime(
                ReadRequiredText(reader, "EndTime", "WorkEntries", legacyId),
                "EndTime",
                "WorkEntries",
                legacyId);
            var followUpState = ParseEnum<FollowUpState>(
                ReadRequiredText(reader, "FollowUpState", "WorkEntries", legacyId),
                "FollowUpState",
                "WorkEntries",
                legacyId);
            var postingStatus = ParseEnum<PostingStatus>(
                ReadRequiredText(reader, "PostingStatus", "WorkEntries", legacyId),
                "PostingStatus",
                "WorkEntries",
                legacyId);

            var entry = new WorkEntry
            {
                WorkDate = workDate,
                ClientId = null,
                ManualClientName = manualClientName,
                TicketId = null,
                TicketNumberText = ticketNumberText,
                HasTimeRange = ReadRequiredBoolean(reader, "HasTimeRange", "WorkEntries", legacyId),
                StartTime = startTime,
                EndTime = endTime,
                DurationMinutes = durationMinutes,
                Billable = ReadRequiredBoolean(reader, "Billable", "WorkEntries", legacyId),
                Note = ReadRequiredText(reader, "Note", "WorkEntries", legacyId),
                InternalNote = ReadNullableText(reader, "InternalNote", "WorkEntries", legacyId),
                IncludePersonalNoteInWhd = ReadRequiredBoolean(
                    reader,
                    "IncludePersonalNoteInWhd",
                    "WorkEntries",
                    legacyId),
                Tags = tags,
                FollowUpState = followUpState,
                FollowUpDueDate = ParseNullableDate(
                    ReadNullableText(reader, "FollowUpDueDate", "WorkEntries", legacyId),
                    "FollowUpDueDate",
                    "WorkEntries",
                    legacyId),
                WhdPosted = ReadRequiredBoolean(reader, "WhdPosted", "WorkEntries", legacyId),
                WhdPostedAt = ParseNullableUtcDateTime(
                    ReadNullableText(reader, "WhdPostedAt", "WorkEntries", legacyId),
                    "WhdPostedAt",
                    "WorkEntries",
                    legacyId),
                SagePosted = ReadRequiredBoolean(reader, "SagePosted", "WorkEntries", legacyId),
                SagePostedAt = ParseNullableUtcDateTime(
                    ReadNullableText(reader, "SagePostedAt", "WorkEntries", legacyId),
                    "SagePostedAt",
                    "WorkEntries",
                    legacyId),
                SageTicketNumber = sageTicketNumber,
                PostingStatus = postingStatus,
                LastError = ReadNullableText(reader, "LastError", "WorkEntries", legacyId),
                CreatedAt = ParseUtcDateTime(
                    ReadRequiredText(reader, "CreatedAt", "WorkEntries", legacyId),
                    "CreatedAt",
                    "WorkEntries",
                    legacyId),
                UpdatedAt = ParseUtcDateTime(
                    ReadRequiredText(reader, "UpdatedAt", "WorkEntries", legacyId),
                    "UpdatedAt",
                    "WorkEntries",
                    legacyId),
                ClientName = legacyClientName ?? manualClientName ?? string.Empty,
                TicketNumber = legacyTicketNumber,
                TicketSubject = legacyTicketSubject
            };

            var row = new V1WorkEntryImportRow
            {
                LegacyId = legacyId,
                WorkEntry = entry,
                LegacyClientId = legacyClientId,
                LegacyClientName = legacyClientName,
                LegacyClientSource = legacyClientSource,
                LegacyClientExternalId = legacyClientExternalId,
                LegacyClientWhdLocationName = legacyClientWhdLocationName,
                LegacyClientSageCustomerId = legacyClientSageCustomerId,
                LegacyClientSageCustomerName = legacyClientSageCustomerName,
                LegacyTicketId = legacyTicketId,
                LegacyTicketClientId = legacyTicketClientId,
                LegacyTicketClientName = legacyTicketClientName,
                LegacyTicketNumber = legacyTicketNumber,
                LegacyTicketSubject = legacyTicketSubject,
                LegacyTicketStatus = legacyTicketStatus,
                LegacyTicketSource = legacyTicketSource,
                LegacyTicketExternalId = legacyTicketExternalId,
                LegacyTicketWhdStatusTypeId = legacyTicketWhdStatusTypeId,
                ContentHash = ComputeContentHash(
                    legacyId,
                    workDate,
                    workEntryClientId,
                    manualClientName,
                    legacyTicketId,
                    ticketNumberText,
                    entry.HasTimeRange,
                    startTime,
                    endTime,
                    durationMinutes,
                    entry.Billable,
                    entry.Note,
                    entry.InternalNote,
                    entry.IncludePersonalNoteInWhd,
                    tags,
                    followUpState,
                    entry.FollowUpDueDate,
                    entry.WhdPosted,
                    entry.WhdPostedAt,
                    entry.SagePosted,
                    entry.SagePostedAt,
                    sageTicketNumber,
                    postingStatus,
                    entry.LastError,
                    entry.CreatedAt,
                    entry.UpdatedAt,
                    legacyClientId,
                    legacyClientSource,
                    legacyClientExternalId,
                    legacyClientSageCustomerId,
                    legacyTicketNumber,
                    legacyTicketSource,
                    legacyTicketExternalId)
            };
            result.Add(row);
        }

        return result;
    }

    private static async Task<IReadOnlyList<V1WorkEntryLinkImportRow>> ReadLinksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        V1Schema schema,
        IReadOnlySet<long> legacyWorkEntryIds,
        CancellationToken cancellationToken)
    {
        if (!schema.Tables.Contains("WorkEntryLinks"))
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Id, SourceWorkEntryId, TargetWorkEntryId, LinkType, CreatedAt
            FROM WorkEntryLinks
            ORDER BY Id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<V1WorkEntryLinkImportRow>();
        var ids = new HashSet<long>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var legacyId = ReadRequiredInt64(reader, "Id", "WorkEntryLinks", legacyId: null);
            if (legacyId <= 0 || !ids.Add(legacyId))
            {
                throw RowError("WorkEntryLinks", legacyId, "Id must be positive and unique.");
            }

            var sourceId = ReadRequiredInt64(reader, "SourceWorkEntryId", "WorkEntryLinks", legacyId);
            var targetId = ReadRequiredInt64(reader, "TargetWorkEntryId", "WorkEntryLinks", legacyId);
            if (sourceId <= 0 || targetId <= 0 || sourceId == targetId)
            {
                throw RowError(
                    "WorkEntryLinks",
                    legacyId,
                    "SourceWorkEntryId and TargetWorkEntryId must be distinct positive IDs.");
            }

            if (!legacyWorkEntryIds.Contains(sourceId) || !legacyWorkEntryIds.Contains(targetId))
            {
                throw RowError(
                    "WorkEntryLinks",
                    legacyId,
                    "The link references a work entry that is not present in the selected database.");
            }

            var linkType = ParseEnum<WorkEntryLinkType>(
                ReadRequiredText(reader, "LinkType", "WorkEntryLinks", legacyId),
                "LinkType",
                "WorkEntryLinks",
                legacyId);
            var createdAtUtc = ParseUtcDateTime(
                ReadRequiredText(reader, "CreatedAt", "WorkEntryLinks", legacyId),
                "CreatedAt",
                "WorkEntryLinks",
                legacyId);
            result.Add(new V1WorkEntryLinkImportRow
            {
                LegacyId = legacyId,
                SourceLegacyWorkEntryId = sourceId,
                TargetLegacyWorkEntryId = targetId,
                LinkType = linkType,
                CreatedAtUtc = createdAtUtc,
                ContentHash = ComputeContentHash(
                    legacyId,
                    sourceId,
                    targetId,
                    linkType,
                    createdAtUtc)
            });
        }

        return result;
    }

    private static async Task<IReadOnlyList<V1PostingLogImportRow>> ReadPostingLogsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        V1Schema schema,
        IReadOnlySet<long> legacyWorkEntryIds,
        CancellationToken cancellationToken)
    {
        if (!schema.Tables.Contains("PostingLogs"))
        {
            return [];
        }

        var postingColumns = schema.Columns["PostingLogs"];
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT Id,
                   WorkEntryId,
                   Destination,
                   Payload,
                   Success,
                   Message,
                   {ColumnOrDefault(postingColumns, "ExternalReference", "NULL")} AS ExternalReference,
                   CreatedAt
            FROM PostingLogs
            ORDER BY Id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<V1PostingLogImportRow>();
        var ids = new HashSet<long>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var legacyId = ReadRequiredInt64(reader, "Id", "PostingLogs", legacyId: null);
            if (legacyId <= 0 || !ids.Add(legacyId))
            {
                throw RowError("PostingLogs", legacyId, "Id must be positive and unique.");
            }

            var workEntryId = ReadRequiredInt64(reader, "WorkEntryId", "PostingLogs", legacyId);
            if (!legacyWorkEntryIds.Contains(workEntryId))
            {
                throw RowError(
                    "PostingLogs",
                    legacyId,
                    $"WorkEntryId {workEntryId} is not present in the selected database.");
            }

            var rawDestination = ReadRequiredText(reader, "Destination", "PostingLogs", legacyId);
            ValidateLength(
                rawDestination,
                MaxPostingDestinationLength,
                "Destination",
                "PostingLogs",
                legacyId);
            var destination = rawDestination.Equals("WHD", StringComparison.OrdinalIgnoreCase)
                ? "WHD"
                : rawDestination.Equals("Sage", StringComparison.OrdinalIgnoreCase)
                    ? "Sage"
                    : throw RowError(
                        "PostingLogs",
                        legacyId,
                        $"Destination '{rawDestination}' is not a supported value (WHD or Sage).");
            var payload = ReadRequiredText(reader, "Payload", "PostingLogs", legacyId);
            var success = ReadRequiredBoolean(reader, "Success", "PostingLogs", legacyId);
            var message = ReadRequiredText(reader, "Message", "PostingLogs", legacyId);
            var externalReference = ReadNullableText(reader, "ExternalReference", "PostingLogs", legacyId);
            ValidateOptionalLength(
                externalReference,
                MaxPostingExternalReferenceLength,
                "ExternalReference",
                "PostingLogs",
                legacyId);
            var createdAtUtc = ParseUtcDateTime(
                ReadRequiredText(reader, "CreatedAt", "PostingLogs", legacyId),
                "CreatedAt",
                "PostingLogs",
                legacyId);
            result.Add(new V1PostingLogImportRow
            {
                LegacyId = legacyId,
                LegacyWorkEntryId = workEntryId,
                Destination = destination,
                Payload = payload,
                Success = success,
                Message = message,
                ExternalReference = externalReference,
                CreatedAtUtc = createdAtUtc,
                ContentHash = ComputeContentHash(
                    legacyId,
                    workEntryId,
                    destination,
                    payload,
                    success,
                    message,
                    externalReference,
                    createdAtUtc)
            });
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<string, int>> ReadExcludedCountsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        V1Schema schema,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in SharedExcludedTables.Concat(OtherExcludedTables))
        {
            if (!schema.Tables.Contains(table))
            {
                continue;
            }

            var value = await ExecuteScalarAsync(
                    connection,
                    transaction,
                    $"SELECT COUNT(*) FROM {QuoteIdentifier(table)};",
                    cancellationToken)
                .ConfigureAwait(false);
            var count = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (count < 0 || count > int.MaxValue)
            {
                throw new V1DatabaseImportException(
                    $"Excluded table '{table}' contains an unsupported row count: {count}.",
                    table);
            }

            counts[table] = (int)count;
        }

        return counts;
    }

    private static bool KnownTable(string table) =>
        table.Equals("Clients", StringComparison.OrdinalIgnoreCase)
        || table.Equals("Tickets", StringComparison.OrdinalIgnoreCase)
        || table.Equals("WorkEntries", StringComparison.OrdinalIgnoreCase)
        || table.Equals("WorkEntryLinks", StringComparison.OrdinalIgnoreCase)
        || table.Equals("PostingLogs", StringComparison.OrdinalIgnoreCase)
        || SharedExcludedTables.Contains(table, StringComparer.OrdinalIgnoreCase)
        || OtherExcludedTables.Contains(table, StringComparer.OrdinalIgnoreCase);

    private static void RequireTable(IReadOnlySet<string> tables, string table)
    {
        if (!tables.Contains(table))
        {
            throw new V1DatabaseImportException(
                $"The selected file is not a supported TechBench V1 database: required table '{table}' is missing.",
                table);
        }
    }

    private static void RequireColumns(
        IReadOnlyDictionary<string, HashSet<string>> columns,
        string table,
        params string[] requiredColumns)
    {
        if (!columns.TryGetValue(table, out var actualColumns))
        {
            throw new V1DatabaseImportException(
                $"The selected V1 database schema could not be inspected for table '{table}'.",
                table);
        }

        var missing = requiredColumns
            .Where(column => !actualColumns.Contains(column))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new V1DatabaseImportException(
                $"The selected V1 database table '{table}' is missing required column(s): {string.Join(", ", missing)}.",
                table);
        }
    }

    private static void ValidateReference(
        long? referencedId,
        long? joinedId,
        string description,
        string table,
        long legacyId)
    {
        if (referencedId is <= 0)
        {
            throw RowError(table, legacyId, $"The {description} ID must be positive when present.");
        }

        if (referencedId.HasValue && joinedId != referencedId)
        {
            throw RowError(
                table,
                legacyId,
                $"The referenced {description} row {referencedId.Value} is missing.");
        }
    }

    private static void ValidateIdentityClient(
        long? clientId,
        string? name,
        string? source,
        string? externalId,
        string? whdLocationName,
        string? sageCustomerId,
        string? sageCustomerName,
        long workEntryId)
    {
        if (!clientId.HasValue)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw RowError("WorkEntries", workEntryId, "The joined client name is empty.");
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw RowError("WorkEntries", workEntryId, "The joined client source is empty.");
        }

        ValidateLength(name, MaxClientNameLength, "LegacyClientName", "WorkEntries", workEntryId);
        ValidateOptionalLength(source, MaxClientSourceLength, "LegacyClientSource", "WorkEntries", workEntryId);
        ValidateOptionalLength(externalId, MaxClientExternalIdLength, "LegacyClientExternalId", "WorkEntries", workEntryId);
        ValidateOptionalLength(whdLocationName, MaxClientNameLength, "LegacyClientWhdLocationName", "WorkEntries", workEntryId);
        ValidateOptionalLength(sageCustomerId, MaxSageCustomerIdLength, "LegacyClientSageCustomerId", "WorkEntries", workEntryId);
        ValidateOptionalLength(sageCustomerName, MaxClientNameLength, "LegacyClientSageCustomerName", "WorkEntries", workEntryId);
    }

    private static void ValidateIdentityTicket(
        long? ticketId,
        string? ticketNumber,
        string? subject,
        string? status,
        string? source,
        string? externalId,
        long workEntryId)
    {
        if (!ticketId.HasValue)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ticketNumber))
        {
            throw RowError("WorkEntries", workEntryId, "The joined ticket number is empty.");
        }

        if (string.IsNullOrWhiteSpace(status) || string.IsNullOrWhiteSpace(source))
        {
            throw RowError("WorkEntries", workEntryId, "The joined ticket status and source are required.");
        }

        ValidateLength(ticketNumber, MaxTicketNumberLength, "LegacyTicketNumber", "WorkEntries", workEntryId);
        ValidateOptionalLength(subject, MaxTicketSubjectLength, "LegacyTicketSubject", "WorkEntries", workEntryId);
        ValidateOptionalLength(status, MaxTicketStatusLength, "LegacyTicketStatus", "WorkEntries", workEntryId);
        ValidateOptionalLength(source, MaxTicketSourceLength, "LegacyTicketSource", "WorkEntries", workEntryId);
        ValidateOptionalLength(externalId, MaxTicketExternalIdLength, "LegacyTicketExternalId", "WorkEntries", workEntryId);
    }

    private static void ValidateLength(
        string value,
        int maximum,
        string column,
        string table,
        long legacyId)
    {
        if (value.Length > maximum)
        {
            throw RowError(
                table,
                legacyId,
                $"{column} is {value.Length} characters; the maximum is {maximum}. The value was not truncated.");
        }
    }

    private static void ValidateOptionalLength(
        string? value,
        int maximum,
        string column,
        string table,
        long legacyId)
    {
        if (value is not null)
        {
            ValidateLength(value, maximum, column, table, legacyId);
        }
    }

    private static T ParseEnum<T>(
        string value,
        string column,
        string table,
        long legacyId)
        where T : struct, Enum
    {
        if (!Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            throw RowError(
                table,
                legacyId,
                $"{column} has unsupported value '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<T>())}.");
        }

        return parsed;
    }

    private static DateTime ParseDate(
        string value,
        string column,
        string table,
        long legacyId)
    {
        if (!DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            throw RowError(table, legacyId, $"{column} is not a valid date: '{value}'.");
        }

        return DateTime.SpecifyKind(parsed.Date, DateTimeKind.Unspecified);
    }

    private static DateTime? ParseNullableDate(
        string? value,
        string column,
        string table,
        long legacyId) =>
        value is null ? null : ParseDate(value, column, table, legacyId);

    private static TimeSpan ParseTime(
        string value,
        string column,
        string table,
        long legacyId)
    {
        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            || parsed < TimeSpan.Zero
            || parsed >= TimeSpan.FromDays(1))
        {
            throw RowError(table, legacyId, $"{column} is not a valid time of day: '{value}'.");
        }

        return parsed;
    }

    private static DateTime ParseUtcDateTime(
        string value,
        string column,
        string table,
        long legacyId)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            throw RowError(table, legacyId, $"{column} is not a valid timestamp: '{value}'.");
        }

        return parsed.UtcDateTime;
    }

    private static DateTime? ParseNullableUtcDateTime(
        string? value,
        string column,
        string table,
        long legacyId) =>
        value is null ? null : ParseUtcDateTime(value, column, table, legacyId);

    private static long ReadRequiredInt64(
        SqliteDataReader reader,
        string column,
        string table,
        long? legacyId)
    {
        var value = ReadValue(reader, column);
        return value switch
        {
            long number => number,
            int number => number,
            _ => throw RowError(table, legacyId, $"{column} must be an integer.")
        };
    }

    private static long? ReadNullableInt64(
        SqliteDataReader reader,
        string column,
        string table,
        long legacyId)
    {
        var value = ReadValue(reader, column);
        return value switch
        {
            DBNull => null,
            long number => number,
            int number => number,
            _ => throw RowError(table, legacyId, $"{column} must be an integer or null.")
        };
    }

    private static int ReadRequiredInt32(
        SqliteDataReader reader,
        string column,
        string table,
        long legacyId)
    {
        var value = ReadRequiredInt64(reader, column, table, legacyId);
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw RowError(table, legacyId, $"{column} is outside the supported integer range.");
        }

        return (int)value;
    }

    private static int? ReadNullableInt32(
        SqliteDataReader reader,
        string column,
        string table,
        long legacyId)
    {
        var value = ReadNullableInt64(reader, column, table, legacyId);
        if (!value.HasValue)
        {
            return null;
        }

        if (value.Value is < int.MinValue or > int.MaxValue)
        {
            throw RowError(table, legacyId, $"{column} is outside the supported integer range.");
        }

        return (int)value.Value;
    }

    private static bool ReadRequiredBoolean(
        SqliteDataReader reader,
        string column,
        string table,
        long legacyId)
    {
        var value = ReadRequiredInt64(reader, column, table, legacyId);
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw RowError(table, legacyId, $"{column} must be 0 or 1; found {value}.")
        };
    }

    private static string ReadRequiredText(
        SqliteDataReader reader,
        string column,
        string table,
        long legacyId)
    {
        var value = ReadValue(reader, column);
        return value is string text
            ? text
            : throw RowError(table, legacyId, $"{column} must be text and cannot be null.");
    }

    private static string? ReadNullableText(
        SqliteDataReader reader,
        string column,
        string table,
        long legacyId)
    {
        var value = ReadValue(reader, column);
        return value switch
        {
            DBNull => null,
            string text => text,
            _ => throw RowError(table, legacyId, $"{column} must be text or null.")
        };
    }

    private static object ReadValue(SqliteDataReader reader, string column) =>
        reader.GetValue(reader.GetOrdinal(column));

    private static string Select(
        string alias,
        string column,
        string fallback,
        string resultAlias,
        IReadOnlySet<string> availableColumns) =>
        $"{(availableColumns.Contains(column) ? $"{alias}.{QuoteIdentifier(column)}" : fallback)} AS {QuoteIdentifier(resultAlias)}";

    private static string PreferredSelect(
        string firstAlias,
        string secondAlias,
        string column,
        string fallback,
        string resultAlias,
        IReadOnlySet<string> availableColumns) =>
        availableColumns.Contains(column)
            ? $"CASE WHEN {firstAlias}.Id IS NOT NULL THEN {firstAlias}.{QuoteIdentifier(column)} ELSE {secondAlias}.{QuoteIdentifier(column)} END AS {QuoteIdentifier(resultAlias)}"
            : $"{fallback} AS {QuoteIdentifier(resultAlias)}";

    private static string ColumnOrDefault(
        IReadOnlySet<string> availableColumns,
        string column,
        string fallback) =>
        availableColumns.Contains(column) ? QuoteIdentifier(column) : fallback;

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> ExecuteScalarAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateSqliteHeaderAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length < SqliteHeader.Length)
            {
                throw new V1DatabaseImportException(
                    "The selected file is too small to be a SQLite database.");
            }

            var header = new byte[SqliteHeader.Length];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan().SequenceEqual(SqliteHeader))
            {
                throw new V1DatabaseImportException(
                    "The selected file is not a SQLite database (the SQLite format header is missing).");
            }
        }
        catch (V1DatabaseImportException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new V1DatabaseImportException(
                "The selected V1 database could not be opened for reading. Close TechBench V1 or select a verified backup copy.",
                innerException: ex);
        }
    }

    private static async Task<string> ComputeFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new V1DatabaseImportException(
                "The selected V1 database could not be hashed. Close TechBench V1 or select a verified backup copy.",
                innerException: ex);
        }
    }

    private static void EnsureNoActiveSqliteSidecars(string path)
    {
        var sidecars = new[] { $"{path}-wal", $"{path}-journal", $"{path}-shm" }
            .Where(File.Exists)
            .Select(Path.GetFileName)
            .ToArray();
        if (sidecars.Length > 0)
        {
            throw new V1DatabaseImportException(
                $"The selected database has active SQLite sidecar file(s): {string.Join(", ", sidecars)}. Close TechBench V1 and select a verified backup copy.");
        }
    }

    private static string ComputeContentHash(params object?[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            if (value is null)
            {
                hash.AppendData([0]);
                continue;
            }

            hash.AppendData([1]);
            var normalized = value switch
            {
                DateTime dateTime => dateTime.Kind == DateTimeKind.Unspecified
                    ? dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
                bool boolean => boolean ? "1" : "0",
                Enum enumeration => enumeration.ToString(),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty
            };
            var bytes = Encoding.UTF8.GetBytes(normalized);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static V1DatabaseImportException RowError(
        string table,
        long? legacyId,
        string message) =>
        new(
            legacyId.HasValue
                ? $"{table} row {legacyId.Value}: {message}"
                : $"{table}: {message}",
            table,
            legacyId);

    private sealed record V1Schema(
        HashSet<string> Tables,
        Dictionary<string, HashSet<string>> Columns);
}

public sealed class V1DatabaseImportException : Exception
{
    public V1DatabaseImportException(
        string message,
        string? tableName = null,
        long? legacyId = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        TableName = tableName;
        LegacyId = legacyId;
    }

    public string? TableName { get; }
    public long? LegacyId { get; }
}
