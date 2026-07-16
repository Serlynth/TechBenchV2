using Microsoft.Data.Sqlite;
using System.IO;
using TechBench.Data;

namespace TechBench.Services;

public sealed record DatabaseBackupResult(
    bool Succeeded,
    bool Created,
    string Message,
    string? BackupPath = null,
    DateTime? CreatedAt = null);

public sealed record DatabaseIntegrityResult(bool IsHealthy, string Message);

public sealed class DatabaseBackupService
{
    private const int RetainedBackupCount = 14;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly Func<DateTime> _clock;

    public DatabaseBackupService(SqliteConnectionFactory connectionFactory)
        : this(connectionFactory, () => DateTime.Now)
    {
    }

    internal DatabaseBackupService(SqliteConnectionFactory connectionFactory, Func<DateTime> clock)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public string BackupDirectory => Path.Combine(
        Path.GetDirectoryName(_connectionFactory.DatabasePath)!,
        "Backups");
    public DatabaseBackupResult? LastBackupResult { get; private set; }
    public DatabaseIntegrityResult? LastIntegrityResult { get; private set; }

    public DatabaseBackupResult CreateDailyBackupIfDue()
    {
        if (!File.Exists(_connectionFactory.DatabasePath))
        {
            LastBackupResult = new DatabaseBackupResult(
                Succeeded: true,
                Created: false,
                Message: "The local database will be backed up automatically after it has been created.");
            return LastBackupResult;
        }

        var now = _clock();
        string? existing;
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            existing = Directory
                .EnumerateFiles(BackupDirectory, $"techbench-{now:yyyyMMdd}-*.db")
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastBackupResult = new DatabaseBackupResult(
                Succeeded: false,
                Created: false,
                Message: $"Automatic database backup could not run: {ex.Message}");
            return LastBackupResult;
        }

        if (existing is not null)
        {
            var verification = CheckIntegrity(existing);
            if (verification.IsHealthy)
            {
                LastBackupResult = new DatabaseBackupResult(
                    Succeeded: true,
                    Created: false,
                    Message: "Today's automatic database backup already exists and is valid.",
                    BackupPath: existing,
                    CreatedAt: File.GetLastWriteTime(existing));
                return LastBackupResult;
            }

            TryDeleteIncompleteBackup(existing);
        }

        return CreateBackup("Automatic daily backup");
    }

    public DatabaseBackupResult CreateBackup(string reason = "Manual backup")
    {
        if (!File.Exists(_connectionFactory.DatabasePath))
        {
            LastBackupResult = new DatabaseBackupResult(
                Succeeded: false,
                Created: false,
                Message: "The local database does not exist yet, so there is nothing to back up.");
            return LastBackupResult;
        }

        var now = _clock();
        string? backupPath = null;

        try
        {
            Directory.CreateDirectory(BackupDirectory);
            backupPath = GetAvailableBackupPath(now);
            using (var source = _connectionFactory.CreateConnection())
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString()))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }

            var verification = CheckIntegrity(backupPath);
            if (!verification.IsHealthy)
            {
                File.Delete(backupPath);
                LastBackupResult = new DatabaseBackupResult(
                    Succeeded: false,
                    Created: false,
                    Message: $"Database backup verification failed: {verification.Message}");
                return LastBackupResult;
            }

            PruneOldBackups();
            LastBackupResult = new DatabaseBackupResult(
                Succeeded: true,
                Created: true,
                Message: $"{reason} created and verified.",
                BackupPath: backupPath,
                CreatedAt: now);
            return LastBackupResult;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            if (backupPath is not null)
            {
                TryDeleteIncompleteBackup(backupPath);
            }
            LastBackupResult = new DatabaseBackupResult(
                Succeeded: false,
                Created: false,
                Message: $"Database backup failed: {ex.Message}");
            return LastBackupResult;
        }
    }

    public DatabaseIntegrityResult CheckIntegrity()
    {
        if (!File.Exists(_connectionFactory.DatabasePath))
        {
            LastIntegrityResult = new DatabaseIntegrityResult(
                IsHealthy: true,
                Message: "The local database has not been created yet.");
            return LastIntegrityResult;
        }

        LastIntegrityResult = CheckIntegrity(_connectionFactory.DatabasePath);
        return LastIntegrityResult;
    }

    public FileInfo? GetLatestBackup()
    {
        try
        {
            if (!Directory.Exists(BackupDirectory))
            {
                return null;
            }

            return Directory
                .EnumerateFiles(BackupDirectory, "techbench-*.db")
                .Select(static path => new FileInfo(path))
                .OrderByDescending(static file => file.LastWriteTime)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DatabaseIntegrityResult CheckIntegrity(string databasePath)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            using var reader = command.ExecuteReader();
            var results = new List<string>();
            while (reader.Read())
            {
                results.Add(reader.GetString(0));
            }

            return results.Count == 1 && results[0].Equals("ok", StringComparison.OrdinalIgnoreCase)
                ? new DatabaseIntegrityResult(true, "Database integrity check passed.")
                : new DatabaseIntegrityResult(
                    false,
                    results.Count == 0
                        ? "Database integrity check returned no result."
                        : $"Database integrity check failed: {string.Join("; ", results)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new DatabaseIntegrityResult(false, $"Database integrity check failed: {ex.Message}");
        }
    }

    private string GetAvailableBackupPath(DateTime timestamp)
    {
        var stem = $"techbench-{timestamp:yyyyMMdd-HHmmss-fff}";
        var path = Path.Combine(BackupDirectory, $"{stem}.db");
        var suffix = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(BackupDirectory, $"{stem}-{suffix++}.db");
        }

        return path;
    }

    private void PruneOldBackups()
    {
        var staleBackups = Directory
            .EnumerateFiles(BackupDirectory, "techbench-*.db")
            .Select(static path => new FileInfo(path))
            .OrderByDescending(static file => file.LastWriteTime)
            .Skip(RetainedBackupCount)
            .ToList();

        foreach (var staleBackup in staleBackups)
        {
            try
            {
                staleBackup.Delete();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TryDeleteIncompleteBackup(string backupPath)
    {
        try
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
