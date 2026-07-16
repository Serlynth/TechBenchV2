using Microsoft.Data.Sqlite;
using System.IO;

namespace TechBench.Data;

public sealed class SqliteConnectionFactory
{
    private string _databasePath;

    public SqliteConnectionFactory()
    {
#if VISUAL_QA
        var visualQaDirectory = Path.Combine(Path.GetTempPath(), "TechBenchV2-VisualQA");
        Directory.CreateDirectory(visualQaDirectory);
        _databasePath = Path.Combine(visualQaDirectory, $"techbench-{Environment.ProcessId}.db");
#else
        var overridePath = Environment.GetEnvironmentVariable("TECHBENCH_V2_LOCAL_DATABASE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var fullOverridePath = Path.GetFullPath(overridePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullOverridePath)!);
            _databasePath = fullOverridePath;
            return;
        }

        _databasePath = DatabaseLocationConfig.ResolveDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
#endif
    }

    internal SqliteConnectionFactory(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _databasePath = fullPath;
    }

    public string DatabasePath => _databasePath;

    internal void UseDatabasePath(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _databasePath = fullPath;
    }

    public SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            ForeignKeys = true
        };

        return new SqliteConnection(builder.ToString());
    }
}
