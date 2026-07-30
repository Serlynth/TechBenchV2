using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class V1DatabaseImportReaderTests
{
    [Fact]
    public async Task ReadsCompleteHistoryWithIdentityMetadataAndExcludedCountsWithoutChangingSource()
    {
        using var database = TestDatabase.CreateModern();
        var hashBefore = await HashAsync(database.Path);

        var package = await new V1DatabaseImportReader().ReadAsync(database.Path);

        Assert.Equal(System.IO.Path.GetFullPath(database.Path), package.SourcePath);
        Assert.Equal(System.IO.Path.GetFileName(database.Path), package.FileName);
        Assert.Equal(hashBefore, package.FileHash);
        Assert.Equal(hashBefore, await HashAsync(database.Path));
        Assert.Equal(2, package.WorkEntries.Count);
        Assert.True(package.HasEditorDraft);
        Assert.Equal(6, package.ExcludedSharedItemCount);
        Assert.Equal(1, package.ExcludedItemCounts["Settings"]);
        Assert.Equal(1, package.ExcludedItemCounts["PostingAttempts"]);

        var row = package.WorkEntries.Single(item => item.LegacyId == 101);
        Assert.Equal(0, row.WorkEntry.Id);
        Assert.Equal("Acme School", row.LegacyClientName);
        Assert.Equal("WHD", row.LegacyClientSource);
        Assert.Equal("WHD-LOCATION-44", row.LegacyClientExternalId);
        Assert.Equal("30462", row.LegacyClientSageCustomerId);
        Assert.Equal("WHD-9001", row.LegacyTicketNumber);
        Assert.Equal(7, row.LegacyTicketWhdStatusTypeId);
        Assert.True(row.WorkEntry.WhdPosted);
        Assert.True(row.WorkEntry.SagePosted);
        Assert.Equal(PostingStatus.PostedToBoth, row.WorkEntry.PostingStatus);
        Assert.Equal(DateTimeKind.Utc, row.WorkEntry.CreatedAt.Kind);
        Assert.Equal(new DateTime(2026, 7, 17, 13, 0, 0, DateTimeKind.Utc), row.WorkEntry.CreatedAt);
        Assert.Equal(64, row.ContentHash.Length);
        Assert.Null(row.ResolvedClientId);
        Assert.Null(row.ResolvedTicketId);

        var link = Assert.Single(package.Links);
        Assert.Equal(501, link.LegacyId);
        Assert.Equal(101, link.SourceLegacyWorkEntryId);
        Assert.Equal(102, link.TargetLegacyWorkEntryId);
        Assert.Equal(WorkEntryLinkType.FollowUpTo, link.LinkType);
        Assert.Equal(DateTimeKind.Utc, link.CreatedAtUtc.Kind);
        Assert.Equal(64, link.ContentHash.Length);

        var log = Assert.Single(package.PostingLogs);
        Assert.Equal(701, log.LegacyId);
        Assert.Equal(101, log.LegacyWorkEntryId);
        Assert.Equal("WHD", log.Destination);
        Assert.True(log.Success);
        Assert.Equal("WHD-TECHNOTE-88", log.ExternalReference);
        Assert.Equal(DateTimeKind.Utc, log.CreatedAtUtc.Kind);
        Assert.Equal(64, log.ContentHash.Length);

        var secondRead = await new V1DatabaseImportReader().ReadAsync(database.Path);
        Assert.Equal(row.ContentHash, secondRead.WorkEntries.Single(item => item.LegacyId == 101).ContentHash);
        Assert.Equal(link.ContentHash, Assert.Single(secondRead.Links).ContentHash);
        Assert.Equal(log.ContentHash, Assert.Single(secondRead.PostingLogs).ContentHash);
    }

    [Fact]
    public async Task AppliesDocumentedDefaultsForOlderMissingColumnsAndTables()
    {
        using var database = TestDatabase.CreateLegacy();

        var package = await new V1DatabaseImportReader().ReadAsync(database.Path);

        var row = Assert.Single(package.WorkEntries);
        Assert.True(row.WorkEntry.HasTimeRange);
        Assert.False(row.WorkEntry.IncludePersonalNoteInWhd);
        Assert.Equal(string.Empty, row.WorkEntry.Tags);
        Assert.Equal(FollowUpState.None, row.WorkEntry.FollowUpState);
        Assert.Null(row.WorkEntry.FollowUpDueDate);
        Assert.Null(row.WorkEntry.SageTicketNumber);
        Assert.Null(row.LegacyTicketWhdStatusTypeId);
        Assert.Empty(package.Links);
        var log = Assert.Single(package.PostingLogs);
        Assert.Null(log.ExternalReference);
        Assert.False(package.HasEditorDraft);
        Assert.DoesNotContain("EditorDrafts", package.ExcludedItemCounts.Keys);
    }

    [Fact]
    public async Task WorkDateChangeProducesANewWorkEntryContentHash()
    {
        using var database = TestDatabase.CreateLegacy();
        var original = await new V1DatabaseImportReader().ReadAsync(database.Path);
        var originalRow = Assert.Single(original.WorkEntries);

        database.Execute(
            "UPDATE WorkEntries SET WorkDate = '2026-07-02' WHERE Id = 1;");
        var changed = await new V1DatabaseImportReader().ReadAsync(database.Path);
        var changedRow = Assert.Single(changed.WorkEntries);

        Assert.Equal(new DateTime(2026, 7, 2), changedRow.WorkEntry.WorkDate);
        Assert.NotEqual(originalRow.ContentHash, changedRow.ContentHash);
    }

    [Fact]
    public async Task RejectsInvalidDurationWithClearTableAndRowId()
    {
        using var database = TestDatabase.CreateLegacy();
        database.Execute("UPDATE WorkEntries SET DurationMinutes = 1441 WHERE Id = 1;");

        var error = await Assert.ThrowsAsync<V1DatabaseImportException>(
            () => new V1DatabaseImportReader().ReadAsync(database.Path));

        Assert.Equal("WorkEntries", error.TableName);
        Assert.Equal(1, error.LegacyId);
        Assert.Contains("between 0 and 1440", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsInvalidEnumWithClearTableAndRowId()
    {
        using var database = TestDatabase.CreateModern();
        database.Execute("UPDATE WorkEntries SET FollowUpState = 'Someday' WHERE Id = 101;");

        var error = await Assert.ThrowsAsync<V1DatabaseImportException>(
            () => new V1DatabaseImportReader().ReadAsync(database.Path));

        Assert.Equal("WorkEntries", error.TableName);
        Assert.Equal(101, error.LegacyId);
        Assert.Contains("FollowUpState", error.Message, StringComparison.Ordinal);
        Assert.Contains("Someday", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsOversizeTextWithoutTruncatingIt()
    {
        using var database = TestDatabase.CreateModern();
        database.Execute(
            "UPDATE WorkEntries SET Tags = $tags WHERE Id = 101;",
            ("$tags", new string('x', 1001)));

        var error = await Assert.ThrowsAsync<V1DatabaseImportException>(
            () => new V1DatabaseImportReader().ReadAsync(database.Path));

        Assert.Equal("WorkEntries", error.TableName);
        Assert.Equal(101, error.LegacyId);
        Assert.Contains("1001", error.Message, StringComparison.Ordinal);
        Assert.Contains("not truncated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsClientSourceLongerThanTheSqlResolverContract()
    {
        using var database = TestDatabase.CreateModern();
        database.Execute(
            "UPDATE Clients SET Source = $source WHERE Id = 11;",
            ("$source", new string('S', 41)));

        var error = await Assert.ThrowsAsync<V1DatabaseImportException>(
            () => new V1DatabaseImportReader().ReadAsync(database.Path));

        Assert.Equal("WorkEntries", error.TableName);
        Assert.Equal(101, error.LegacyId);
        Assert.Contains("LegacyClientSource", error.Message, StringComparison.Ordinal);
        Assert.Contains("41", error.Message, StringComparison.Ordinal);
        Assert.Contains("not truncated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsDatabaseChangedAfterRead()
    {
        using var database = TestDatabase.CreateLegacy();
        var reader = new V1DatabaseImportReader(
            (path, cancellationToken) =>
                File.AppendAllTextAsync(path, "changed", cancellationToken));

        var error = await Assert.ThrowsAsync<V1DatabaseImportException>(
            () => reader.ReadAsync(database.Path));

        Assert.Contains("changed while it was being read", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsActiveSqliteSidecarInsteadOfReadingALiveSource()
    {
        using var database = TestDatabase.CreateLegacy();
        await File.WriteAllTextAsync($"{database.Path}-wal", "active");

        var error = await Assert.ThrowsAsync<V1DatabaseImportException>(
            () => new V1DatabaseImportReader().ReadAsync(database.Path));

        Assert.Contains("sidecar", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verified backup", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string _directory;

        private TestDatabase(string directory, string path)
        {
            _directory = directory;
            Path = path;
        }

        public string Path { get; }

        public static TestDatabase CreateModern()
        {
            var database = Create();
            database.Execute(
                """
                CREATE TABLE Clients (
                    Id INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Source TEXT NOT NULL,
                    ExternalId TEXT NULL,
                    WhdLocationName TEXT NULL,
                    SageCustomerId TEXT NULL,
                    SageCustomerName TEXT NULL
                );
                CREATE TABLE Tickets (
                    Id INTEGER PRIMARY KEY,
                    TicketNumber TEXT NOT NULL,
                    ClientId INTEGER NOT NULL,
                    Subject TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    Source TEXT NOT NULL,
                    ExternalId TEXT NULL,
                    WhdStatusTypeId INTEGER NULL,
                    IsClosed INTEGER NOT NULL
                );
                CREATE TABLE WorkEntries (
                    Id INTEGER PRIMARY KEY,
                    WorkDate TEXT NOT NULL,
                    ClientId INTEGER NULL,
                    ManualClientName TEXT NULL,
                    TicketId INTEGER NULL,
                    TicketNumberText TEXT NULL,
                    HasTimeRange INTEGER NOT NULL,
                    StartTime TEXT NOT NULL,
                    EndTime TEXT NOT NULL,
                    DurationMinutes INTEGER NOT NULL,
                    Billable INTEGER NOT NULL,
                    Note TEXT NOT NULL,
                    InternalNote TEXT NULL,
                    IncludePersonalNoteInWhd INTEGER NOT NULL,
                    Tags TEXT NOT NULL,
                    FollowUpState TEXT NOT NULL,
                    FollowUpDueDate TEXT NULL,
                    WhdPosted INTEGER NOT NULL,
                    WhdPostedAt TEXT NULL,
                    SagePosted INTEGER NOT NULL,
                    SagePostedAt TEXT NULL,
                    SageTicketNumber TEXT NULL,
                    PostingStatus TEXT NOT NULL,
                    LastError TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE TABLE WorkEntryLinks (
                    Id INTEGER PRIMARY KEY,
                    SourceWorkEntryId INTEGER NOT NULL,
                    TargetWorkEntryId INTEGER NOT NULL,
                    LinkType TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );
                CREATE TABLE PostingLogs (
                    Id INTEGER PRIMARY KEY,
                    WorkEntryId INTEGER NOT NULL,
                    Destination TEXT NOT NULL,
                    Payload TEXT NOT NULL,
                    Success INTEGER NOT NULL,
                    Message TEXT NOT NULL,
                    ExternalReference TEXT NULL,
                    CreatedAt TEXT NOT NULL
                );
                CREATE TABLE TicketStatusOptions (Id INTEGER PRIMARY KEY);
                CREATE TABLE ClientAliases (Alias TEXT PRIMARY KEY, ClientId INTEGER NOT NULL);
                CREATE TABLE Templates (Id INTEGER PRIMARY KEY);
                CREATE TABLE CommonLinks (Id INTEGER PRIMARY KEY);
                CREATE TABLE Settings (Id INTEGER PRIMARY KEY);
                CREATE TABLE PostingAttempts (Id INTEGER PRIMARY KEY);
                CREATE TABLE EditorDrafts (Id INTEGER PRIMARY KEY, Payload TEXT NOT NULL);

                INSERT INTO Clients
                    (Id, Name, Source, ExternalId, WhdLocationName, SageCustomerId, SageCustomerName)
                VALUES
                    (11, 'Acme School', 'WHD', 'WHD-LOCATION-44', 'Acme School', '30462', 'ACME SCHOOL');
                INSERT INTO Tickets
                    (Id, TicketNumber, ClientId, Subject, Status, Source, ExternalId, WhdStatusTypeId, IsClosed)
                VALUES
                    (21, 'WHD-9001', 11, 'Server issue', 'Closed', 'WHD', 'WHD-9001', 7, 1);
                INSERT INTO WorkEntries
                    (Id, WorkDate, ClientId, ManualClientName, TicketId, TicketNumberText, HasTimeRange,
                     StartTime, EndTime, DurationMinutes, Billable, Note, InternalNote,
                     IncludePersonalNoteInWhd, Tags, FollowUpState, FollowUpDueDate,
                     WhdPosted, WhdPostedAt, SagePosted, SagePostedAt, SageTicketNumber,
                     PostingStatus, LastError, CreatedAt, UpdatedAt)
                VALUES
                    (101, '2026-07-17', 11, NULL, 21, NULL, 1,
                     '09:00', '10:00', 60, 1, 'Resolved server issue.', 'Private context.',
                     1, 'server,urgent', 'Completed', '2026-07-18',
                     1, '2026-07-17T10:01:00.0000000-04:00', 1, '2026-07-17T10:02:00.0000000-04:00', '12345',
                     'PostedToBoth', NULL, '2026-07-17T09:00:00.0000000-04:00', '2026-07-17T10:02:00.0000000-04:00'),
                    (102, '2026-07-18', NULL, 'Walk-in customer', NULL, 'MANUAL-1', 0,
                     '00:00', '00:00', 30, 0, 'Prepared follow-up.', NULL,
                     0, '', 'FollowUp', NULL,
                     0, NULL, 0, NULL, NULL,
                     'Ready', NULL, '2026-07-18T09:00:00.0000000-04:00', '2026-07-18T09:30:00.0000000-04:00');
                INSERT INTO WorkEntryLinks
                    (Id, SourceWorkEntryId, TargetWorkEntryId, LinkType, CreatedAt)
                VALUES
                    (501, 101, 102, 'FollowUpTo', '2026-07-18T09:00:00.0000000-04:00');
                INSERT INTO PostingLogs
                    (Id, WorkEntryId, Destination, Payload, Success, Message, ExternalReference, CreatedAt)
                VALUES
                    (701, 101, 'WHD', '{"note":"Resolved"}', 1, 'Posted.', 'WHD-TECHNOTE-88', '2026-07-17T10:01:00.0000000-04:00');
                INSERT INTO TicketStatusOptions (Id) VALUES (1);
                INSERT INTO ClientAliases (Alias, ClientId) VALUES ('ACME', 11);
                INSERT INTO Templates (Id) VALUES (1);
                INSERT INTO CommonLinks (Id) VALUES (1);
                INSERT INTO Settings (Id) VALUES (1);
                INSERT INTO PostingAttempts (Id) VALUES (1);
                INSERT INTO EditorDrafts (Id, Payload) VALUES (1, '{}');
                """);
            return database;
        }

        public static TestDatabase CreateLegacy()
        {
            var database = Create();
            database.Execute(
                """
                CREATE TABLE Clients (
                    Id INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Source TEXT NOT NULL,
                    ExternalId TEXT NULL
                );
                CREATE TABLE Tickets (
                    Id INTEGER PRIMARY KEY,
                    TicketNumber TEXT NOT NULL,
                    ClientId INTEGER NOT NULL,
                    Subject TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    Source TEXT NOT NULL,
                    ExternalId TEXT NULL,
                    IsClosed INTEGER NOT NULL
                );
                CREATE TABLE WorkEntries (
                    Id INTEGER PRIMARY KEY,
                    WorkDate TEXT NOT NULL,
                    ClientId INTEGER NOT NULL,
                    TicketId INTEGER NULL,
                    TicketNumberText TEXT NULL,
                    StartTime TEXT NOT NULL,
                    EndTime TEXT NOT NULL,
                    DurationMinutes INTEGER NOT NULL,
                    Billable INTEGER NOT NULL,
                    Note TEXT NOT NULL,
                    InternalNote TEXT NULL,
                    WhdPosted INTEGER NOT NULL,
                    WhdPostedAt TEXT NULL,
                    SagePosted INTEGER NOT NULL,
                    SagePostedAt TEXT NULL,
                    PostingStatus TEXT NOT NULL,
                    LastError TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE TABLE PostingLogs (
                    Id INTEGER PRIMARY KEY,
                    WorkEntryId INTEGER NOT NULL,
                    Destination TEXT NOT NULL,
                    Payload TEXT NOT NULL,
                    Success INTEGER NOT NULL,
                    Message TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );
                INSERT INTO Clients (Id, Name, Source, ExternalId)
                    VALUES (1, 'Legacy Client', 'WHD', 'WHD-CLIENT-1');
                INSERT INTO Tickets
                    (Id, TicketNumber, ClientId, Subject, Status, Source, ExternalId, IsClosed)
                    VALUES (2, 'WHD-2', 1, 'Legacy ticket', 'Open', 'WHD', 'WHD-2', 0);
                INSERT INTO WorkEntries
                    (Id, WorkDate, ClientId, TicketId, TicketNumberText, StartTime, EndTime,
                     DurationMinutes, Billable, Note, InternalNote, WhdPosted, WhdPostedAt,
                     SagePosted, SagePostedAt, PostingStatus, LastError, CreatedAt, UpdatedAt)
                    VALUES
                    (1, '2026-07-01', 1, 2, NULL, '08:00', '08:30',
                     30, 1, 'Legacy note.', NULL, 0, NULL,
                     0, NULL, 'Ready', NULL, '2026-07-01T08:00:00.0000000-04:00', '2026-07-01T08:30:00.0000000-04:00');
                INSERT INTO PostingLogs
                    (Id, WorkEntryId, Destination, Payload, Success, Message, CreatedAt)
                    VALUES (1, 1, 'Sage', '{}', 0, 'Not posted.', '2026-07-01T08:31:00.0000000-04:00');
                """);
            return database;
        }

        public void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private static TestDatabase Create()
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"TechBenchV1ReaderTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            return new TestDatabase(directory, System.IO.Path.Combine(directory, "techbench.db"));
        }
    }
}
