using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TechBench.ServerManager;

internal static class PackageInstaller
{
    public static int Apply(string packageDirectory, int managerProcessId)
    {
        var paths = AppPaths.Installed;
        var logPath = Path.Combine(paths.ManagerDataDirectory, "update.log");
        SecureDirectory.EnsureAdministratorsOnly(paths.ManagerDataDirectory);
        void Log(string message) => File.AppendAllText(logPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");

        try
        {
            WaitForManagerExit(managerProcessId, TimeSpan.FromSeconds(30));
            var manifest = PackageManifest.LoadAndVerify(packageDirectory);
            if (!InstalledPackageDeclaresRequiredSchema(paths, manifest.RequiredDatabaseSchemaVersion))
            {
                new SqlAdminRepository(paths).VerifyRequiredSchema(manifest.RequiredDatabaseSchemaVersion);
            }

            var operation = Path.Combine(paths.ManagerDataDirectory, "install-" + Guid.NewGuid().ToString("N"));
            var serviceStage = Path.Combine(operation, "service-stage");
            var managerStage = Path.Combine(operation, "manager-stage");
            var serviceBackup = Path.Combine(operation, "service-backup");
            var managerBackup = Path.Combine(operation, "manager-backup");
            Directory.CreateDirectory(operation);
            CopyDirectory(packageDirectory, serviceStage, _ => true);
            CopyDirectory(Path.Combine(packageDirectory, "server-manager"), managerStage, _ => true);
            if (File.Exists(paths.ConfigurationPath))
            {
                File.Copy(paths.ConfigurationPath, Path.Combine(serviceStage, "appsettings.json"), true);
                UpdateConfigurationManifestEntry(serviceStage);
            }

            var service = new WindowsServiceManager(paths);
            var installedService = service.GetDetails();
            if (!installedService.Installed)
                throw new InvalidOperationException("The TechBench Sync Service is not installed. Use TechBench Server Setup for a first installation.");
            Log($"Installing TechBench {manifest.Version}.");
            try { service.Stop(); } catch (InvalidOperationException) { }
            var serviceMoved = false;
            var managerMoved = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(paths.ServiceDirectory)!);
                if (Directory.Exists(paths.ServiceDirectory)) { MoveDirectoryWithRetries(paths.ServiceDirectory, serviceBackup); serviceMoved = true; }
                if (Directory.Exists(paths.ManagerDirectory)) { MoveDirectoryWithRetries(paths.ManagerDirectory, managerBackup); managerMoved = true; }
                MoveDirectoryWithRetries(serviceStage, paths.ServiceDirectory);
                MoveDirectoryWithRetries(managerStage, paths.ManagerDirectory);
                SecureDirectory.GrantReadAndExecute(paths.ServiceDirectory, installedService.Account);
                SecureDirectory.GrantBuiltInUsersReadAndExecute(paths.ManagerDirectory);
                ShortcutManager.Create(paths);
                service.Start();
                Log("Update completed and the service started.");
            }
            catch (Exception updateException)
            {
                try { service.Stop(); } catch { }
                var rollbackErrors = new List<Exception> { updateException };
                TryRollbackDirectory(paths.ServiceDirectory, serviceBackup, serviceMoved, rollbackErrors);
                TryRollbackDirectory(paths.ManagerDirectory, managerBackup, managerMoved, rollbackErrors);
                try { if (Directory.Exists(paths.ManagerDirectory)) ShortcutManager.Create(paths); } catch (Exception ex) { rollbackErrors.Add(ex); }
                try { service.Start(); } catch (Exception ex) { rollbackErrors.Add(ex); }
                if (rollbackErrors.Count > 1)
                    throw new AggregateException("The update failed and one or more rollback actions also failed. Backup files were retained for recovery.", rollbackErrors);
                throw;
            }

            TryDelete(serviceBackup);
            TryDelete(managerBackup);
            TryDelete(operation);
            var manager = new ProcessStartInfo { FileName = paths.ManagerExecutable, UseShellExecute = true };
            Process.Start(manager);
            return 0;
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex);
            MessageBox.Show($"TechBench could not install the update. The previous installation was restored when possible.\n\n{ex.Message}\n\nDetails: {logPath}",
                "TechBench Server Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    internal static bool InstalledPackageDeclaresRequiredSchema(AppPaths paths, int requiredVersion)
    {
        try
        {
            var manifestPath = Path.Combine(paths.ServiceDirectory, "package-manifest.json");
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            return root.TryGetProperty("Product", out var product) &&
                   product.GetString() == "TechBench Sync Service" &&
                   root.TryGetProperty("PackageFormatVersion", out var format) &&
                   format.GetInt32() == 1 &&
                   root.TryGetProperty("RequiredDatabaseSchemaVersion", out var schema) &&
                   schema.GetInt32() == requiredVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static void WaitForManagerExit(int managerProcessId, TimeSpan timeout)
    {
        if (managerProcessId <= 0) return;
        try
        {
            using var manager = Process.GetProcessById(managerProcessId);
            if (!manager.WaitForExit((int)timeout.TotalMilliseconds))
                throw new InvalidOperationException(
                    "The running TechBench Server Manager did not close, so no installed files were changed. " +
                    "Exit it from the notification area and try the update again.");
        }
        catch (ArgumentException)
        {
            // The process already exited before the helper obtained its handle.
        }
    }

    private static void CopyDirectory(string source, string destination, Func<string, bool> include)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            if (include(relative)) Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (!include(relative)) continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static void UpdateConfigurationManifestEntry(string serviceStage)
    {
        var manifestPath = Path.Combine(serviceStage, "package-manifest.json");
        var configurationPath = Path.Combine(serviceStage, "appsettings.json");
        var root = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidDataException("The installed package manifest is invalid.");
        var files = root["Files"]?.AsArray() ?? root["files"]?.AsArray()
            ?? throw new InvalidDataException("The package manifest has no files collection.");
        var entry = files.Select(static node => node?.AsObject()).FirstOrDefault(item =>
            item is not null && string.Equals(item["Path"]?.GetValue<string>() ?? item["path"]?.GetValue<string>(), "appsettings.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The package manifest has no appsettings.json entry.");
        var lengthName = entry.ContainsKey("Length") ? "Length" : "length";
        var hashName = entry.ContainsKey("Sha256") ? "Sha256" : "sha256";
        entry[lengthName] = new FileInfo(configurationPath).Length;
        entry[hashName] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(configurationPath)));
        File.WriteAllText(manifestPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private static void MoveDirectoryWithRetries(string source, string destination)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try { Directory.Move(source, destination); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(250 * (attempt + 1));
            }
        }
        throw last ?? new IOException($"Unable to move '{source}' to '{destination}'.");
    }

    private static void TryRollbackDirectory(string installedPath, string backupPath, bool hadBackup, ICollection<Exception> errors)
    {
        try
        {
            DeleteWithRetries(installedPath);
            if (hadBackup && Directory.Exists(backupPath)) Directory.Move(backupPath, installedPath);
        }
        catch (Exception ex) { errors.Add(ex); }
    }

    private static void DeleteWithRetries(string path)
    {
        if (!Directory.Exists(path)) return;
        Exception? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try { Directory.Delete(path, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(250 * (attempt + 1));
            }
        }
        throw last ?? new IOException($"Unable to remove '{path}' during rollback.");
    }
}
