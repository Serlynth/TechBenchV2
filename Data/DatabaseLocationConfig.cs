using System.IO;

namespace TechBench.Data;

public static class DatabaseLocationConfig
{
    private const string ConfigurationFileName = "database-location.txt";

    public static string ConfigurationDirectory
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "TechBench");
        }
    }

    public static string ConfigurationFilePath =>
        Path.Combine(ConfigurationDirectory, ConfigurationFileName);

    public static string DefaultDatabasePath =>
        Path.Combine(ConfigurationDirectory, "techbench.db");

    public static bool HasConfiguredLocation => File.Exists(ConfigurationFilePath);

    public static bool ShouldOfferInitialLocationChoice =>
        !HasConfiguredLocation && !File.Exists(DefaultDatabasePath);

    public static string ResolveDatabasePath()
    {
        if (File.Exists(ConfigurationFilePath))
        {
            var configuredPath = File.ReadAllText(ConfigurationFilePath).Trim();
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return Path.GetFullPath(configuredPath);
            }
        }

        return DefaultDatabasePath;
    }

    public static void SaveDatabasePath(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(ConfigurationDirectory);
        var temporaryPath = $"{ConfigurationFilePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, fullPath);
        File.Move(temporaryPath, ConfigurationFilePath, overwrite: true);
    }
}
