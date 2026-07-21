using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TechBench.ServerManager;

internal sealed record AppPaths(
    string ServiceName,
    string ServiceDirectory,
    string DataDirectory,
    string ManagerDirectory,
    string ManagerDataDirectory,
    string ShortcutPath)
{
    public const string DefaultServiceName = "TechBenchWhdSync";

    public static AppPaths Installed => new(
        DefaultServiceName,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CSRI", "TechBench Sync Service"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CSRI", "TechBench Sync Service"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CSRI", "TechBench Server Manager"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CSRI", "TechBench Server Manager"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "CSRI", "TechBench Server Manager.lnk"));

    public string ServiceExecutable => Path.Combine(ServiceDirectory, "TechBench.SyncService.exe");
    public string ConfigurationPath => Path.Combine(ServiceDirectory, "appsettings.json");
    public string WhdSecretPath => Path.Combine(DataDirectory, "whd.secret");
    public string SageSecretPath => Path.Combine(DataDirectory, "sage.secret");
    public string ManagerExecutable => Path.Combine(ManagerDirectory, "TechBench.ServerManager.exe");

    public ServiceConfiguration ReadConfiguration()
    {
        if (!File.Exists(ConfigurationPath))
        {
            throw new FileNotFoundException("The installed service configuration was not found.", ConfigurationPath);
        }

        // Windows PowerShell 5.1 writes UTF-8 files with a BOM. Read as text so
        // .NET removes that encoding marker before the JSON parser sees it.
        using var document = JsonDocument.Parse(File.ReadAllText(ConfigurationPath));
        if (!document.RootElement.TryGetProperty("TechBenchSync", out var section))
        {
            throw new InvalidDataException("appsettings.json does not contain TechBenchSync.");
        }

        var server = GetRequiredString(section, "SqlServer");
        var database = GetRequiredString(section, "Database");
        var trust = section.TryGetProperty("TrustServerCertificate", out var trustValue)
            && trustValue.ValueKind is JsonValueKind.True;
        return new ServiceConfiguration(server, database, trust);
    }

    public void SaveConfiguration(ServiceConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.SqlServer) || string.IsNullOrWhiteSpace(configuration.Database))
            throw new ArgumentException("SQL Server and database are required.");
        var root = JsonNode.Parse(File.ReadAllText(ConfigurationPath))?.AsObject()
            ?? throw new InvalidDataException("appsettings.json is invalid.");
        var section = root["TechBenchSync"]?.AsObject()
            ?? throw new InvalidDataException("appsettings.json does not contain TechBenchSync.");
        section["SqlServer"] = configuration.SqlServer.Trim();
        section["Database"] = configuration.Database.Trim();
        section["TrustServerCertificate"] = configuration.TrustServerCertificate;
        var temporary = ConfigurationPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, ConfigurationPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string CurrentVersion
    {
        get
        {
            var path = File.Exists(ServiceExecutable) ? ServiceExecutable : Assembly.GetExecutingAssembly().Location;
            var value = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion;
            return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Split('+', 2)[0];
        }
    }

    private static string GetRequiredString(JsonElement section, string name)
    {
        if (!section.TryGetProperty(name, out var value) || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"TechBenchSync.{name} is blank.");
        }
        return value.GetString()!;
    }
}

internal sealed record ServiceConfiguration(string SqlServer, string Database, bool TrustServerCertificate);
