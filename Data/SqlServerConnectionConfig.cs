using System.IO;
using System.Text.Json;

namespace TechBench.Data;

public static class SqlServerConnectionConfig
{
    public const string ServerEnvironmentVariable = "TECHBENCH_V2_SQL_SERVER";
    public const string DatabaseEnvironmentVariable = "TECHBENCH_V2_SQL_DATABASE";
    public const string TrustCertificateEnvironmentVariable =
        "TECHBENCH_V2_SQL_TRUST_SERVER_CERTIFICATE";

    private const string ConfigurationFileName = "sql-server.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string ConfigurationDirectory
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "TechBenchV2");
        }
    }

    public static string ConfigurationFilePath =>
        Path.Combine(ConfigurationDirectory, ConfigurationFileName);

    public static SqlServerConnectionOptions? Resolve()
    {
        SqlServerConnectionOptions? savedOptions = null;
        if (File.Exists(ConfigurationFilePath))
        {
            var json = File.ReadAllText(ConfigurationFilePath);
            savedOptions = JsonSerializer.Deserialize<SqlServerConnectionOptions>(json, JsonOptions)
                ?? throw new InvalidOperationException(
                    "The saved SQL Server configuration is empty or invalid.");
        }

        var configuredServer = Environment.GetEnvironmentVariable(ServerEnvironmentVariable);
        var configuredDatabase = Environment.GetEnvironmentVariable(DatabaseEnvironmentVariable);
        var configuredTrust = Environment.GetEnvironmentVariable(TrustCertificateEnvironmentVariable);

        var server = FirstConfiguredValue(configuredServer, savedOptions?.Server);
        var database = FirstConfiguredValue(configuredDatabase, savedOptions?.Database);
        var hasAnyConfiguration = !string.IsNullOrWhiteSpace(server)
            || !string.IsNullOrWhiteSpace(database)
            || savedOptions is not null;
        if (!hasAnyConfiguration)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
        {
            throw new InvalidOperationException(
                "Both the SQL Server name and TechBench database name must be configured.");
        }

        var trustServerCertificate = string.IsNullOrWhiteSpace(configuredTrust)
            ? savedOptions?.TrustServerCertificate ?? false
            : ParseBoolean(configuredTrust, TrustCertificateEnvironmentVariable);

        return new SqlServerConnectionOptions(
            server,
            database,
            trustServerCertificate)
        {
            ConnectTimeoutSeconds = savedOptions?.ConnectTimeoutSeconds
                ?? SqlServerConnectionOptions.DefaultConnectTimeoutSeconds,
            CommandTimeoutSeconds = savedOptions?.CommandTimeoutSeconds
                ?? SqlServerConnectionOptions.DefaultCommandTimeoutSeconds
        }.NormalizeAndValidate();
    }

    public static void Save(SqlServerConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalized = options.NormalizeAndValidate();

        Directory.CreateDirectory(ConfigurationDirectory);
        var temporaryPath = $"{ConfigurationFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temporaryPath, ConfigurationFilePath, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static string? FirstConfiguredValue(string? primary, string? fallback) =>
        !string.IsNullOrWhiteSpace(primary) ? primary.Trim() : fallback?.Trim();

    private static bool ParseBoolean(string value, string variableName)
    {
        var normalized = value.Trim();
        if (bool.TryParse(normalized, out var parsed))
        {
            return parsed;
        }

        return normalized.ToUpperInvariant() switch
        {
            "1" or "YES" or "Y" or "ON" => true,
            "0" or "NO" or "N" or "OFF" => false,
            _ => throw new InvalidOperationException(
                $"{variableName} must be true or false.")
        };
    }

    private static void TryDeleteTemporaryFile(string path)
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
            // A failed atomic save surfaces from the original file operation.
        }
    }
}
