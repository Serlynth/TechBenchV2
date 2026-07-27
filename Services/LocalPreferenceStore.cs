using System.IO;
using System.Text.Json;

namespace TechBench.Services;

/// <summary>
/// Stores workstation-only preferences. No business or operational records
/// belong in this file; those are owned by SQL Server.
/// </summary>
public static class LocalPreferenceStore
{
    private const string FileName = "preferences.json";
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    public static string PreferenceDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TechBenchV2");

    public static string PreferenceFilePath =>
        Path.Combine(PreferenceDirectory, FileName);

    public static LocalPreferences LoadOrCreate()
        => LoadOrCreate(PreferenceFilePath);

    internal static LocalPreferences LoadOrCreate(string preferenceFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferenceFilePath);
        lock (SyncRoot)
        {
            if (!File.Exists(preferenceFilePath))
            {
                var created = new LocalPreferences();
                SaveCore(created, preferenceFilePath);
                return created;
            }

            try
            {
                var json = File.ReadAllText(preferenceFilePath);
                var preferences = JsonSerializer.Deserialize<LocalPreferences>(
                        json,
                        JsonOptions)
                    ?? throw new InvalidOperationException(
                        "The local preference file is empty.");
                if (preferences.DeviceId == Guid.Empty)
                {
                    preferences.DeviceId = Guid.NewGuid();
                    SaveCore(preferences, preferenceFilePath);
                }

                return preferences.Normalize();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"The local TechBench preference file is invalid: {preferenceFilePath}",
                    ex);
            }
        }
    }

    public static void Save(LocalPreferences preferences)
        => Save(preferences, PreferenceFilePath);

    internal static void Save(
        LocalPreferences preferences,
        string preferenceFilePath)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentException.ThrowIfNullOrWhiteSpace(preferenceFilePath);
        lock (SyncRoot)
        {
            SaveCore(preferences.Normalize(), preferenceFilePath);
        }
    }

    public static LocalPreferences Update(
        Func<LocalPreferences, LocalPreferences> update)
        => Update(update, PreferenceFilePath);

    internal static LocalPreferences Update(
        Func<LocalPreferences, LocalPreferences> update,
        string preferenceFilePath)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(preferenceFilePath);
        lock (SyncRoot)
        {
            var current = LoadOrCreate(preferenceFilePath);
            var updated = update(current)
                ?? throw new InvalidOperationException(
                    "The local preference update returned no value.");
            SaveCore(updated.Normalize(), preferenceFilePath);
            return updated;
        }
    }

    private static void SaveCore(
        LocalPreferences preferences,
        string preferenceFilePath)
    {
        var directory = Path.GetDirectoryName(preferenceFilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException(
                "The preference file must include a directory.",
                nameof(preferenceFilePath));
        }

        Directory.CreateDirectory(directory);
        var temporaryPath =
            $"{preferenceFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(preferences, JsonOptions));
            File.Move(temporaryPath, preferenceFilePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Preserve the original save exception.
            }
        }
    }
}

public sealed class LocalPreferences
{
    public Guid DeviceId { get; set; } = Guid.NewGuid();

    public string Theme { get; set; } = "Dark";

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public string WindowState { get; set; } = "Normal";

    public int RefreshIntervalMinutes { get; set; } = 5;

    public DateTime? LastUpdateCheckAtUtc { get; set; }

    public string? SkippedUpdateVersion { get; set; }

    public string UpdateChannel { get; set; } = string.Empty;

    public bool MicrosoftAdminOpenInChromeIncognito { get; set; }

    internal LocalPreferences Normalize()
    {
        if (DeviceId == Guid.Empty)
        {
            DeviceId = Guid.NewGuid();
        }

        Theme = Theme.Equals("Light", StringComparison.OrdinalIgnoreCase)
            ? "Light"
            : "Dark";
        WindowState = WindowState is "Maximized" or "Minimized"
            ? WindowState
            : "Normal";
        RefreshIntervalMinutes = Math.Clamp(RefreshIntervalMinutes, 1, 120);
        SkippedUpdateVersion = string.IsNullOrWhiteSpace(SkippedUpdateVersion)
            ? null
            : SkippedUpdateVersion.Trim();
        UpdateChannel = UpdateChannel?.Trim() switch
        {
            V2AppUpdateService.StableReleaseChannel =>
                V2AppUpdateService.StableReleaseChannel,
            V2AppUpdateService.InventoryBetaReleaseChannel =>
                V2AppUpdateService.InventoryBetaReleaseChannel,
            _ => string.Empty
        };
        return this;
    }
}
