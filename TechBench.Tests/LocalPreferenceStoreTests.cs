using System.Text.Json;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class LocalPreferenceStoreTests
{
    [Fact]
    public void CreatesAndPersistsStableDevicePreferences()
    {
        var (directory, path) = CreateTestPath();
        try
        {
            var created = LocalPreferenceStore.LoadOrCreate(path);
            var deviceId = created.DeviceId;
            Assert.NotEqual(Guid.Empty, deviceId);

            created.Theme = "Light";
            created.WindowLeft = 120;
            created.WindowTop = 80;
            created.WindowWidth = 1440;
            created.WindowHeight = 900;
            created.WindowState = "Maximized";
            created.RefreshIntervalMinutes = 15;
            created.MicrosoftAdminOpenInChromeIncognito = true;
            created.LastUpdateCheckAtUtc = new DateTime(
                2026,
                7,
                16,
                18,
                30,
                0,
                DateTimeKind.Utc);
            created.SkippedUpdateVersion = "2.0.0-alpha.2";
            created.UpdateChannel =
                V2AppUpdateService.InventoryBetaReleaseChannel;
            created.EquipmentDetailsPanelVisible = false;
            created.EquipmentTechnicianOrder =
            [
                "CSRI\\rskoog",
                "CSRI\\kallen"
            ];

            LocalPreferenceStore.Save(created, path);
            var loaded = LocalPreferenceStore.LoadOrCreate(path);

            Assert.Equal(deviceId, loaded.DeviceId);
            Assert.Equal("Light", loaded.Theme);
            Assert.Equal(120, loaded.WindowLeft);
            Assert.Equal(80, loaded.WindowTop);
            Assert.Equal(1440, loaded.WindowWidth);
            Assert.Equal(900, loaded.WindowHeight);
            Assert.Equal("Maximized", loaded.WindowState);
            Assert.Equal(15, loaded.RefreshIntervalMinutes);
            Assert.True(loaded.MicrosoftAdminOpenInChromeIncognito);
            Assert.Equal(created.LastUpdateCheckAtUtc, loaded.LastUpdateCheckAtUtc);
            Assert.Equal("2.0.0-alpha.2", loaded.SkippedUpdateVersion);
            Assert.Equal(
                V2AppUpdateService.InventoryBetaReleaseChannel,
                loaded.UpdateChannel);
            Assert.False(loaded.EquipmentDetailsPanelVisible);
            Assert.Equal(
                ["CSRI\\rskoog", "CSRI\\kallen"],
                loaded.EquipmentTechnicianOrder);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void NormalizesValuesAndPersistsOnlyAllowedPreferenceFields()
    {
        var (directory, path) = CreateTestPath();
        try
        {
            LocalPreferenceStore.Save(new LocalPreferences
            {
                Theme = "unexpected",
                WindowState = "Fullscreen",
                RefreshIntervalMinutes = -10,
                SkippedUpdateVersion = "   ",
                UpdateChannel = "unexpected",
                EquipmentTechnicianOrder =
                [
                    " CSRI\\rskoog ",
                    "",
                    "csri\\RSKOOG",
                    "CSRI\\kallen"
                ]
            }, path);

            var loaded = LocalPreferenceStore.LoadOrCreate(path);
            Assert.Equal("Dark", loaded.Theme);
            Assert.Equal("Normal", loaded.WindowState);
            Assert.Equal(1, loaded.RefreshIntervalMinutes);
            Assert.Null(loaded.SkippedUpdateVersion);
            Assert.Equal(string.Empty, loaded.UpdateChannel);
            Assert.Equal(
                ["CSRI\\rskoog", "CSRI\\kallen"],
                loaded.EquipmentTechnicianOrder);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var propertyNames = document.RootElement
                .EnumerateObject()
                .Select(static property => property.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var allowed = typeof(LocalPreferences)
                .GetProperties()
                .Select(static property => property.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.True(allowed.SetEquals(propertyNames));
            Assert.DoesNotContain(
                propertyNames,
                static name => name.Contains(
                    "AutoSync",
                    StringComparison.OrdinalIgnoreCase));

            Assert.DoesNotContain(
                propertyNames,
                static name => name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Credential", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("WorkEntry", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Client", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Ticket", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Note", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void AtomicSaveLeavesNoTemporaryFiles()
    {
        var (directory, path) = CreateTestPath();
        try
        {
            LocalPreferenceStore.Save(
                new LocalPreferences { Theme = "Dark" },
                path);
            LocalPreferenceStore.Save(
                new LocalPreferences { Theme = "Light" },
                path);

            Assert.True(File.Exists(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
            Assert.Equal("Light", LocalPreferenceStore.LoadOrCreate(path).Theme);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void CorruptJsonIsReportedAndPreserved()
    {
        var (directory, path) = CreateTestPath();
        try
        {
            Directory.CreateDirectory(directory);
            const string corruptJson = "{ this is not valid json";
            File.WriteAllText(path, corruptJson);

            var exception = Assert.Throws<InvalidOperationException>(
                () => LocalPreferenceStore.LoadOrCreate(path));

            Assert.Contains("preference file is invalid", exception.Message);
            Assert.Equal(corruptJson, File.ReadAllText(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ConcurrentUpdatesDoNotLoseChanges()
    {
        var (directory, path) = CreateTestPath();
        try
        {
            LocalPreferenceStore.Save(
                new LocalPreferences { RefreshIntervalMinutes = 5 },
                path);

            var updates = Enumerable.Range(0, 40)
                .Select(_ => Task.Run(() =>
                    LocalPreferenceStore.Update(
                        preferences =>
                        {
                            preferences.RefreshIntervalMinutes++;
                            return preferences;
                        },
                        path)));

            await Task.WhenAll(updates);

            Assert.Equal(
                45,
                LocalPreferenceStore.LoadOrCreate(path).RefreshIntervalMinutes);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void EmptyDeviceIdIsRepairedAndPersisted()
    {
        var (directory, path) = CreateTestPath();
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                path,
                """
                {
                  "deviceId": "00000000-0000-0000-0000-000000000000",
                  "theme": "Dark"
                }
                """);

            var repaired = LocalPreferenceStore.LoadOrCreate(path);
            var reloaded = LocalPreferenceStore.LoadOrCreate(path);

            Assert.NotEqual(Guid.Empty, repaired.DeviceId);
            Assert.Equal(repaired.DeviceId, reloaded.DeviceId);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static (string Directory, string Path) CreateTestPath()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"TechBenchPreferenceTests-{Guid.NewGuid():N}");
        return (directory, System.IO.Path.Combine(directory, "preferences.json"));
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
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
