using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class V1DatabaseImportReadOnlyTests
{
    [Fact]
    public async Task ReadsAFileSystemReadOnlyDatabaseWithoutChangingOrCreatingSidecars()
    {
        using var database = MinimalV1Database.Create();
        var hashBefore = await HashAsync(database.Path);
        File.SetAttributes(database.Path, File.GetAttributes(database.Path) | FileAttributes.ReadOnly);

        var package = await new V1DatabaseImportReader().ReadAsync(database.Path);

        Assert.Single(package.WorkEntries);
        Assert.Equal(hashBefore, package.FileHash);
        Assert.Equal(hashBefore, await HashAsync(database.Path));
        AssertNoSidecars(database.Path);
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-journal")]
    [InlineData("-shm")]
    public async Task RejectsEveryActiveSqliteSidecar(string suffix)
    {
        using var database = MinimalV1Database.Create();
        await File.WriteAllTextAsync(database.Path + suffix, "active");

        var error = await Assert.ThrowsAsync<V1DatabaseImportException>(
            () => new V1DatabaseImportReader().ReadAsync(database.Path));

        Assert.Contains(suffix, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verified backup", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsASidecarThatAppearsWhileTheSnapshotIsBeingRead()
    {
        using var database = MinimalV1Database.Create();
        var reader = new V1DatabaseImportReader(
            (path, cancellationToken) =>
                File.WriteAllTextAsync(path + "-journal", "active", cancellationToken));

        var error = await Assert.ThrowsAsync<V1DatabaseImportException>(
            () => reader.ReadAsync(database.Path));

        Assert.Contains("-journal", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sidecar", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoSidecars(string path)
    {
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-journal"));
        Assert.False(File.Exists(path + "-shm"));
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private sealed class MinimalV1Database : IDisposable
    {
        private readonly string _directory;

        private MinimalV1Database(string directory, string path)
        {
            _directory = directory;
            Path = path;
        }

        public string Path { get; }

        public static MinimalV1Database Create()
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"TechBenchV1ReadOnlyTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "techbench.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
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
                INSERT INTO Clients (Id, Name, Source, ExternalId)
                    VALUES (1, 'Legacy Client', 'WHD', 'LOCATION-1');
                INSERT INTO Tickets
                    (Id, TicketNumber, ClientId, Subject, Status, Source, ExternalId, IsClosed)
                    VALUES (2, 'WHD-2', 1, 'Legacy ticket', 'Open', 'WHD', '2', 0);
                INSERT INTO WorkEntries
                    (Id, WorkDate, ClientId, TicketId, TicketNumberText, StartTime, EndTime,
                     DurationMinutes, Billable, Note, InternalNote, WhdPosted, WhdPostedAt,
                     SagePosted, SagePostedAt, PostingStatus, LastError, CreatedAt, UpdatedAt)
                    VALUES
                    (1, '2026-07-01', 1, 2, NULL, '08:00', '08:30',
                     30, 1, 'Legacy note.', NULL, 0, NULL,
                     0, NULL, 'Ready', NULL,
                     '2026-07-01T08:00:00.0000000-04:00',
                     '2026-07-01T08:30:00.0000000-04:00');
                """;
            command.ExecuteNonQuery();
            return new MinimalV1Database(directory, path);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(Path))
            {
                File.SetAttributes(Path, FileAttributes.Normal);
            }

            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
