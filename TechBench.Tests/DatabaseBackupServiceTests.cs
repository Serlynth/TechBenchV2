using Microsoft.Data.Sqlite;
using TechBench.Data;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class DatabaseBackupServiceTests
{
    [Fact]
    public void CreatesVerifiedBackupContainingCommittedData()
    {
        WithDatabase((repository, factory, directory) =>
        {
            var entry = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 14),
                ManualClientName = "Backup Test",
                DurationMinutes = 30,
                Note = "Committed before backup"
            };
            repository.SaveWorkEntry(entry);

            var service = new DatabaseBackupService(factory, () => new DateTime(2026, 7, 14, 1, 30, 0));
            var result = service.CreateBackup();

            Assert.True(result.Succeeded, result.Message);
            Assert.True(result.Created);
            Assert.NotNull(result.BackupPath);
            Assert.True(File.Exists(result.BackupPath));
            Assert.Equal(Path.Combine(directory, "Backups"), service.BackupDirectory);

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = result.BackupPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Note FROM WorkEntries WHERE Id = $id";
            command.Parameters.AddWithValue("$id", entry.Id);
            Assert.Equal(entry.Note, command.ExecuteScalar());
        });
    }

    [Fact]
    public void DailyBackupDoesNotDuplicateAnExistingBackupForTheDate()
    {
        WithDatabase((_, factory, _) =>
        {
            var now = new DateTime(2026, 7, 14, 2, 0, 0);
            var service = new DatabaseBackupService(factory, () => now);

            var first = service.CreateDailyBackupIfDue();
            var second = service.CreateDailyBackupIfDue();

            Assert.True(first.Succeeded, first.Message);
            Assert.True(first.Created);
            Assert.True(second.Succeeded, second.Message);
            Assert.False(second.Created);
            Assert.Single(Directory.EnumerateFiles(service.BackupDirectory, "techbench-*.db"));
        });
    }

    [Fact]
    public void BackupRetentionKeepsTheNewestFourteenCopies()
    {
        WithDatabase((_, factory, _) =>
        {
            var now = new DateTime(2026, 7, 14, 3, 0, 0);
            var service = new DatabaseBackupService(factory, () => now);

            for (var index = 0; index < 16; index++)
            {
                var result = service.CreateBackup();
                Assert.True(result.Succeeded, result.Message);
                now = now.AddSeconds(1);
            }

            Assert.Equal(14, Directory.EnumerateFiles(service.BackupDirectory, "techbench-*.db").Count());
        });
    }

    private static void WithDatabase(Action<TechBenchRepository, SqliteConnectionFactory, string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TechBenchBackupTests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(directory, "techbench.db");
        try
        {
            var factory = new SqliteConnectionFactory(databasePath);
            var repository = new TechBenchRepository(factory);
            repository.Initialize();
            action(repository, factory, directory);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
