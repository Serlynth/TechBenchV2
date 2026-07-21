using TechBench.ServerManager;
using TechBench.SyncService;
using Microsoft.Extensions.Options;
using System.Text;

namespace TechBench.Tests;

public sealed class CompiledServerManagerTests
{
    [Fact]
    public void ProjectBuildsAsElevatedWindowsExecutable()
    {
        var project = ReadRepositoryFile("TechBench.ServerManager", "TechBench.ServerManager.csproj");
        var manifest = ReadRepositoryFile("TechBench.ServerManager", "app.manifest");
        Assert.Contains("<OutputType>WinExe</OutputType>", project, StringComparison.Ordinal);
        Assert.Contains("<UseWindowsForms>true</UseWindowsForms>", project, StringComparison.Ordinal);
        Assert.Contains("<RuntimeIdentifier>win-x64</RuntimeIdentifier>", project, StringComparison.Ordinal);
        Assert.Contains("level=\"requireAdministrator\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void CompiledManagerOwnsServiceSecretsSqlTrayAndUpdater()
    {
        var form = ReadRepositoryFile("TechBench.ServerManager", "ServerManagerForm.cs");
        var sql = ReadRepositoryFile("TechBench.ServerManager", "SqlAdminRepository.cs");
        var directory = ReadRepositoryFile("TechBench.ServerManager", "ActiveDirectoryUserProvider.cs");
        var updater = ReadRepositoryFile("TechBench.ServerManager", "ReleaseUpdater.cs");
        var installer = ReadRepositoryFile("TechBench.ServerManager", "PackageInstaller.cs");

        Assert.Contains("NotifyIcon", form, StringComparison.Ordinal);
        Assert.Contains("WindowState == FormWindowState.Minimized", form, StringComparison.Ordinal);
        Assert.Contains("ServicePassword", form, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ProtectedSecretStore.Whd", form, StringComparison.Ordinal);
        Assert.Contains("DataGridView", form, StringComparison.Ordinal);
        Assert.Contains("Save all mappings", form, StringComparison.Ordinal);
        Assert.Contains("Save settings + mappings", form, StringComparison.Ordinal);
        Assert.Contains("CommitEdit(DataGridViewDataErrorContexts.Commit)", form, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(form, "SavePendingMappingsAsync("));
        Assert.Contains("TechBench_Users", directory, StringComparison.Ordinal);
        Assert.Contains("TechBench_Admins", directory, StringComparison.Ordinal);
        Assert.Contains("GetMembers(recursive: true)", directory, StringComparison.Ordinal);
        Assert.Contains("SaveMappings", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Sage.ActivityItemId", form + sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Activity Item ID", form, StringComparison.Ordinal);
        Assert.Contains("tb_app.AdminSaveOrganizationSetting", sql, StringComparison.Ordinal);
        Assert.Contains("IntegratedSecurity = true", sql, StringComparison.Ordinal);
        Assert.Contains("PackageManifest.LoadAndVerify", updater, StringComparison.Ordinal);
        Assert.Contains("VerifyChecksum", updater, StringComparison.Ordinal);
        Assert.Contains("TryRollbackDirectory(paths.ServiceDirectory, serviceBackup", installer, StringComparison.Ordinal);
        Assert.Contains("ShortcutManager.Create(paths)", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("powershell", form, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", updater, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectoryUsersMergeWithSavedMappingsWithoutDuplicateRows()
    {
        var directoryUsers = new[]
        {
            new DirectoryUser("CSRI\\alice", "Alice Admin", true),
            new DirectoryUser("CSRI\\bob", "Bob User", false)
        };
        var savedMappings = new[]
        {
            new UserMapping("csri\\ALICE", "Old Alice", false, "WHD-TECH-1"),
            new UserMapping("CSRI\\retired", "Retired User", false, "WHD-TECH-2")
        };

        var merged = ActiveDirectoryUserProvider.MergeMappings(directoryUsers, savedMappings);

        Assert.Collection(
            merged,
            alice =>
            {
                Assert.Equal("CSRI\\alice", alice.LoginName);
                Assert.Equal("Alice Admin", alice.DisplayName);
                Assert.True(alice.IsAdmin);
                Assert.Equal("WHD-TECH-1", alice.TechnicianExternalId);
            },
            bob =>
            {
                Assert.Equal("CSRI\\bob", bob.LoginName);
                Assert.False(bob.IsAdmin);
                Assert.Empty(bob.TechnicianExternalId);
            });
    }

    [Theory]
    [InlineData("2.0.0-alpha.9", "2.0.0-alpha.14", -1)]
    [InlineData("2.0.0-alpha.14", "2.0.0-alpha.9", 1)]
    [InlineData("2.0.0-alpha.14", "2.0.0", -1)]
    [InlineData("2.0.1", "2.0.0", 1)]
    public void SemanticVersionsAreOrderedCorrectly(string left, string right, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(SemanticVersion.Compare(left, right)));
    }

    [Fact]
    public void SelfUpdateHelperDoesNotInheritTheInstalledManagerWorkingDirectory()
    {
        var packageDirectory = Path.Combine(Path.GetTempPath(), "TechBenchUpdate", Guid.NewGuid().ToString("N"));
        var startInfo = ReleaseUpdater.CreateInstallerStartInfo(packageDirectory, 1234);

        Assert.Equal(Path.GetFullPath(packageDirectory), startInfo.WorkingDirectory);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(packageDirectory), "server-manager", "TechBench.ServerManager.exe"),
            startInfo.FileName);
        Assert.Contains("--manager-pid 1234", startInfo.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void SelfUpdateStopsBeforeChangingFilesWhenManagerDoesNotExit()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PackageInstaller.WaitForManagerExit(Environment.ProcessId, TimeSpan.Zero));

        Assert.Contains("no installed files were changed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirstInstallScriptVerifiesPayloadAndCreatesDirectShortcut()
    {
        var script = ReadRepositoryFile("scripts", "Install-TechBenchServerManager.ps1");
        Assert.Contains("Get-FileHash", script, StringComparison.Ordinal);
        Assert.Contains("server-manager", script, StringComparison.Ordinal);
        Assert.Contains("TechBench.ServerManager.exe", script, StringComparison.Ordinal);
        Assert.Contains("$shortcut.TargetPath", script, StringComparison.Ordinal);
        Assert.Contains("S-1-5-32-545", script, StringComparison.Ordinal);
        Assert.DoesNotContain("wscript.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell.exe", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompiledManagerWritesSecretsInTheExistingServiceFormat()
    {
        var root = Path.Combine(Path.GetTempPath(), "TechBenchManagerTests", Guid.NewGuid().ToString("N"));
        var paths = TestPaths(root);
        try
        {
            Directory.CreateDirectory(paths.DataDirectory);
            ProtectedSecretStore.Whd(paths).Write("whd-test-value");
            ProtectedSecretStore.Sage(paths).Write("sage-test-value");
            var whd = new WhdSecretStore(Options.Create(new SyncServiceOptions { SecretPath = paths.WhdSecretPath }));
            var sage = new SageSecretStore(Options.Create(new SyncServiceOptions { SageSecretPath = paths.SageSecretPath }));
            Assert.Equal("whd-test-value", whd.Read());
            Assert.Equal("sage-test-value", sage.Read());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SqlConnectionEditorPreservesUnrelatedServiceSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "TechBenchManagerTests", Guid.NewGuid().ToString("N"));
        var paths = TestPaths(root);
        try
        {
            Directory.CreateDirectory(paths.ServiceDirectory);
            File.WriteAllText(paths.ConfigurationPath, """
                { "TechBenchSync": { "SqlServer": "old", "Database": "oldDb", "TrustServerCertificate": false, "PollSeconds": 20 }, "Logging": { "LogLevel": { "Default": "Information" } } }
                """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            paths.SaveConfiguration(new("CSRI-SQL.CSRI.local", "TechBench", true));
            var saved = paths.ReadConfiguration();
            Assert.Equal("CSRI-SQL.CSRI.local", saved.SqlServer);
            Assert.Equal("TechBench", saved.Database);
            Assert.True(saved.TrustServerCertificate);
            var text = File.ReadAllText(paths.ConfigurationPath);
            Assert.Contains("\"PollSeconds\": 20", text, StringComparison.Ordinal);
            Assert.Contains("\"Logging\"", text, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AllManagerJsonReadersAcceptWindowsPowerShellUtf8BomFiles()
    {
        var paths = ReadRepositoryFile("TechBench.ServerManager", "AppPaths.cs");
        var installer = ReadRepositoryFile("TechBench.ServerManager", "PackageInstaller.cs");
        var updater = ReadRepositoryFile("TechBench.ServerManager", "ReleaseUpdater.cs");
        Assert.Contains("JsonDocument.Parse(File.ReadAllText(ConfigurationPath))", paths, StringComparison.Ordinal);
        Assert.Contains("JsonNode.Parse(File.ReadAllText(ConfigurationPath))", paths, StringComparison.Ordinal);
        Assert.Contains("JsonNode.Parse(File.ReadAllText(manifestPath))", installer, StringComparison.Ordinal);
        Assert.Contains("Deserialize<PackageManifest>(File.ReadAllText(manifestPath)", updater, StringComparison.Ordinal);
        Assert.DoesNotContain("Parse(File.ReadAllBytes", paths + installer + updater, StringComparison.Ordinal);
    }

    private static AppPaths TestPaths(string root) => new(
        "TechBenchTestService",
        Path.Combine(root, "service"),
        Path.Combine(root, "data"),
        Path.Combine(root, "manager"),
        Path.Combine(root, "manager-data"),
        Path.Combine(root, "shortcut.lnk"));

    private static string ReadRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the TechBenchV2 repository root.");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }
}
