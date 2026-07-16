using Microsoft.Data.Sqlite;
using TechBench.Data;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class DatabaseLocationServiceTests
{
    [Fact]
    public void MovesDatabaseWithIntegrityVerificationAndRetainsSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TechBenchLocationTests-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(directory, "source.db");
        var targetPath = Path.Combine(directory, "cloud", "techbench.db");
        string? configuredPath = null;

        try
        {
            var factory = new SqliteConnectionFactory(sourcePath);
            var repository = new TechBenchRepository(factory);
            repository.Initialize();
            repository.SaveWorkEntry(new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 16),
                ManualClientName = "CSRI",
                DurationMinutes = 15,
                Note = "Verified database move."
            });
            var service = new DatabaseLocationService(factory, path => configuredPath = path);

            var result = service.MoveDatabase(targetPath);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(Path.GetFullPath(targetPath), factory.DatabasePath);
            Assert.Equal(Path.GetFullPath(targetPath), configuredPath);
            Assert.True(File.Exists(sourcePath));
            Assert.True(File.Exists(targetPath));
            Assert.Single(repository.GetWorkEntries(new WorkEntryQuery { Keyword = "database move" }));
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

    [Fact]
    public void RejectsValidSqliteFileThatIsNotATechBenchDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TechBenchLocationTests-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(directory, "source.db");
        var unrelatedPath = Path.Combine(directory, "unrelated.db");
        string? configuredPath = null;

        try
        {
            Directory.CreateDirectory(directory);
            var factory = new SqliteConnectionFactory(sourcePath);
            var repository = new TechBenchRepository(factory);
            repository.Initialize();

            using (var unrelated = new SqliteConnection($"Data Source={unrelatedPath};Pooling=False"))
            {
                unrelated.Open();
                using var command = unrelated.CreateCommand();
                command.CommandText = "CREATE TABLE OtherData (Id INTEGER PRIMARY KEY)";
                command.ExecuteNonQuery();
            }

            var service = new DatabaseLocationService(factory, path => configuredPath = path);

            var result = service.UseExistingDatabase(unrelatedPath);

            Assert.False(result.Succeeded);
            Assert.Contains("not a TechBench database", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Path.GetFullPath(sourcePath), factory.DatabasePath);
            Assert.Null(configuredPath);
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
