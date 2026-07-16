using System.IO;
using Microsoft.Data.Sqlite;
using TechBench.Data;

namespace TechBench.Services;

public sealed record DatabaseLocationResult(
    bool Succeeded,
    string Message,
    string? PreviousPath = null,
    string? CurrentPath = null);

public sealed class DatabaseLocationService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly Action<string> _saveConfiguredPath;

    public DatabaseLocationService(SqliteConnectionFactory connectionFactory)
        : this(connectionFactory, DatabaseLocationConfig.SaveDatabasePath)
    {
    }

    internal DatabaseLocationService(
        SqliteConnectionFactory connectionFactory,
        Action<string> saveConfiguredPath)
    {
        _connectionFactory = connectionFactory;
        _saveConfiguredPath = saveConfiguredPath;
    }

    public DatabaseLocationResult MoveDatabase(string targetPath)
    {
        var previousPath = _connectionFactory.DatabasePath;
        var normalizedTarget = NormalizeDatabasePath(targetPath);
        if (PathsEqual(previousPath, normalizedTarget))
        {
            return new DatabaseLocationResult(
                true,
                "The database is already stored at that location.",
                previousPath,
                normalizedTarget);
        }

        if (File.Exists(normalizedTarget))
        {
            return new DatabaseLocationResult(
                false,
                "A database already exists at the selected path. Use \"Use Existing Database\" instead so it is verified before TechBench switches to it.",
                previousPath,
                normalizedTarget);
        }

        var targetDirectory = Path.GetDirectoryName(normalizedTarget)!;
        Directory.CreateDirectory(targetDirectory);
        var temporaryTarget = $"{normalizedTarget}.{Guid.NewGuid():N}.moving";

        try
        {
            if (File.Exists(previousPath))
            {
                using (var source = _connectionFactory.CreateConnection())
                using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = temporaryTarget,
                    ForeignKeys = true,
                    Pooling = false
                }.ToString()))
                {
                    source.Open();
                    destination.Open();
                    source.BackupDatabase(destination);
                }

                var integrity = CheckIntegrity(temporaryTarget);
                if (!integrity.IsHealthy)
                {
                    return new DatabaseLocationResult(false, integrity.Message, previousPath, normalizedTarget);
                }

                File.Move(temporaryTarget, normalizedTarget);
            }

            _saveConfiguredPath(normalizedTarget);
            _connectionFactory.UseDatabasePath(normalizedTarget);
            return new DatabaseLocationResult(
                true,
                $"TechBench now uses {normalizedTarget}. The previous database was retained at {previousPath} for rollback.",
                previousPath,
                normalizedTarget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new DatabaseLocationResult(
                false,
                $"The database could not be moved: {ex.Message}",
                previousPath,
                normalizedTarget);
        }
        finally
        {
            TryDelete(temporaryTarget);
        }
    }

    public DatabaseLocationResult UseExistingDatabase(string databasePath)
    {
        var previousPath = _connectionFactory.DatabasePath;
        var normalizedPath = NormalizeDatabasePath(databasePath);
        if (!File.Exists(normalizedPath))
        {
            return new DatabaseLocationResult(
                false,
                "The selected database file does not exist.",
                previousPath,
                normalizedPath);
        }

        var integrity = ValidateExistingDatabase(normalizedPath);
        if (!integrity.IsHealthy)
        {
            return new DatabaseLocationResult(false, integrity.Message, previousPath, normalizedPath);
        }

        try
        {
            _saveConfiguredPath(normalizedPath);
            _connectionFactory.UseDatabasePath(normalizedPath);
            return new DatabaseLocationResult(
                true,
                $"TechBench now uses {normalizedPath}.",
                previousPath,
                normalizedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new DatabaseLocationResult(
                false,
                $"TechBench could not switch to the selected database: {ex.Message}",
                previousPath,
                normalizedPath);
        }
    }

    public static DatabaseIntegrityResult ValidateExistingDatabase(string databasePath)
    {
        var integrity = CheckIntegrity(databasePath);
        if (!integrity.IsHealthy)
        {
            return integrity;
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                ForeignKeys = true,
                Pooling = false
            }.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT name
                FROM sqlite_master
                WHERE type = 'table'
                  AND name IN ('Clients', 'WorkEntries', 'Settings')
                """;

            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }

            var requiredTables = new[] { "Clients", "WorkEntries", "Settings" };
            var missingTables = requiredTables.Where(table => !tables.Contains(table)).ToArray();
            return missingTables.Length == 0
                ? new DatabaseIntegrityResult(true, "TechBench database validation passed.")
                : new DatabaseIntegrityResult(
                    false,
                    $"The selected file is a valid SQLite database, but it is not a TechBench database. Missing: {string.Join(", ", missingTables)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new DatabaseIntegrityResult(false, $"TechBench database validation failed: {ex.Message}");
        }
    }

    public static DatabaseIntegrityResult CheckIntegrity(string databasePath)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                ForeignKeys = true,
                Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            var results = new List<string>();
            using var reader = command.ExecuteReader();
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

    private static string NormalizeDatabasePath(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath.Trim());
        return string.IsNullOrWhiteSpace(Path.GetExtension(fullPath))
            ? $"{fullPath}.db"
            : fullPath;
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A temporary file can be removed manually if Windows still has it locked.
        }
    }
}
