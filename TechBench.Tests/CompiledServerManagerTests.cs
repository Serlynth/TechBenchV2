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
        Assert.Contains("BuildManagerTabs", form, StringComparison.Ordinal);
        Assert.Contains("BuildStackedTab(\"Service\"", form, StringComparison.Ordinal);
        Assert.Contains("BuildStackedTab(\"SQL Server\"", form, StringComparison.Ordinal);
        Assert.Contains("new TabPage(\"Web Help Desk\")", form, StringComparison.Ordinal);
        Assert.Contains("BuildStackedTab(\"Sage 50\"", form, StringComparison.Ordinal);
        Assert.Contains("BuildStackedTab(\"Updates\"", form, StringComparison.Ordinal);
        Assert.Contains("Connection & Sync", form, StringComparison.Ordinal);
        Assert.Contains("User Mappings", form, StringComparison.Ordinal);
        Assert.Contains("Activity (newest first)", form, StringComparison.Ordinal);
        Assert.Contains("_log.SelectedText = entry", form, StringComparison.Ordinal);
        Assert.DoesNotContain("_log.AppendText", form, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildServiceColumn", form, StringComparison.Ordinal);
        Assert.Contains("ServicePassword", form, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ProtectedSecretStore.Whd", form, StringComparison.Ordinal);
        Assert.Contains("DataGridView", form, StringComparison.Ordinal);
        Assert.Contains("Save all mappings", form, StringComparison.Ordinal);
        Assert.Contains("Sync WHD technicians", form, StringComparison.Ordinal);
        Assert.Contains("MonitorWhdTechnicianSyncAsync", form, StringComparison.Ordinal);
        Assert.Contains("Save settings + mappings", form, StringComparison.Ordinal);
        Assert.Contains("CommitEdit(DataGridViewDataErrorContexts.Commit)", form, StringComparison.Ordinal);
        Assert.Contains("MonitorFireDrillSyncAsync", form, StringComparison.Ordinal);
        Assert.Contains("The request is still queued", form, StringComparison.Ordinal);
        Assert.Contains("LoadFireDrillStatus", sql, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(form, "SavePendingMappingsAsync("));
        Assert.Contains("TechBench_Users", directory, StringComparison.Ordinal);
        Assert.Contains("TechBench_Admins", directory, StringComparison.Ordinal);
        Assert.Contains("GetMembers(recursive: true)", directory, StringComparison.Ordinal);
        Assert.Contains("user.EmailAddress", directory, StringComparison.Ordinal);
        Assert.Contains("user.UserPrincipalName", directory, StringComparison.Ordinal);
        Assert.Contains("Refresh from Active Directory", form, StringComparison.Ordinal);
        Assert.Contains("AD email / AuthPoint identity", form, StringComparison.Ordinal);
        Assert.DoesNotContain("Save AuthPoint mappings", form, StringComparison.Ordinal);
        Assert.Contains("SaveMappings", sql, StringComparison.Ordinal);
        Assert.Contains("ReconcileAuthorizedUsers", sql, StringComparison.Ordinal);
        Assert.Contains("AdminReconcileWhdAuthorizedUsers", sql, StringComparison.Ordinal);
        Assert.Contains("ReconcileAuthorizedUsers(directoryUsers)", form, StringComparison.Ordinal);
        Assert.Contains("authorized-user reconciliation failed", form, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("windowsSid = user.WindowsSidHex", sql, StringComparison.Ordinal);
        Assert.Contains("ToSqlSidHex(user.Sid)", directory, StringComparison.Ordinal);
        Assert.Contains("MinimumSupportedSchemaVersion = 13", sql, StringComparison.Ordinal);
        Assert.Contains("if (isPrerelease) continue;", updater, StringComparison.Ordinal);
        Assert.Contains("NormalizeServerReleaseTag", updater, StringComparison.Ordinal);
        Assert.Contains("server-v", updater, StringComparison.Ordinal);
        Assert.DoesNotContain("!current.IsPrerelease", updater, StringComparison.Ordinal);
        Assert.Contains("MaximumSupportedSchemaVersion = 15", sql, StringComparison.Ordinal);
        Assert.Contains("supports database schemas", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("requires database schema 8", sql, StringComparison.Ordinal);
        Assert.Contains("RequestWhdSync(\"Full\")", sql, StringComparison.Ordinal);
        Assert.Contains("RequestWhdTechnicianSync", sql, StringComparison.Ordinal);
        Assert.Contains("RequestWhdSync(\"Technicians\")", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Sage.ActivityItemId", form + sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Activity Item ID", form, StringComparison.Ordinal);
        Assert.Contains("tb_app.AdminSaveOrganizationSetting", sql, StringComparison.Ordinal);
        Assert.Contains("IntegratedSecurity = true", sql, StringComparison.Ordinal);
        Assert.Contains("PackageManifest.LoadAndVerify", updater, StringComparison.Ordinal);
        Assert.Contains("VerifyChecksum", updater, StringComparison.Ordinal);
        Assert.Contains("TryRollbackDirectory(paths.ServiceDirectory, serviceBackup", installer, StringComparison.Ordinal);
        Assert.Contains("UpdateCacheCleanup.CleanupFailedOperation", updater, StringComparison.Ordinal);
        Assert.Contains("CleanupUpdateCacheAsync", form, StringComparison.Ordinal);
        Assert.Contains("ShortcutManager.Create(paths)", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("powershell", form, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", updater, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("server-v0.6.6", "0.6.6")]
    [InlineData("v0.6.5", "0.6.5")]
    [InlineData("0.6.5", null)]
    public void ServerReleaseTagsAreIndependentFromClientChannels(string tag, string? expected)
    {
        Assert.Equal(expected, ReleaseUpdater.NormalizeServerReleaseTag(tag));
    }

    [Fact]
    public void DirectoryUsersMergeWithSavedMappingsWithoutDuplicateRows()
    {
        var directoryUsers = new[]
        {
            new DirectoryUser(
                "CSRI\\alice",
                "Alice Admin",
                true,
                AuthPointLogin: "alice@csri-qt.com"),
            new DirectoryUser(
                "CSRI\\bob",
                "Bob User",
                false,
                AuthPointLogin: "bob@csri-qt.com")
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
                Assert.Equal("alice@csri-qt.com", alice.AuthPointLogin);
                Assert.True(alice.AuthPointEnabled);
            },
            bob =>
            {
                Assert.Equal("CSRI\\bob", bob.LoginName);
                Assert.False(bob.IsAdmin);
                Assert.Empty(bob.TechnicianExternalId);
                Assert.Equal("bob@csri-qt.com", bob.AuthPointLogin);
                Assert.True(bob.AuthPointEnabled);
            });
    }

    [Fact]
    public void DirectoryEmailDrivesAuthPointIdentityAndUpnIsTheFallback()
    {
        Assert.Equal(
            "alice@csri-qt.com",
            ActiveDirectoryUserProvider.ResolveAuthPointLogin(
                " alice@csri-qt.com ",
                "alice@CSRI.local"));
        Assert.Equal(
            "bob@CSRI.local",
            ActiveDirectoryUserProvider.ResolveAuthPointLogin(
                null,
                " bob@CSRI.local "));
        Assert.Empty(ActiveDirectoryUserProvider.ResolveAuthPointLogin(null, null));
    }

    [Fact]
    public void DirectoryAuthPointSyncWritesOnlyChangedIdentities()
    {
        var rowVersion = new byte[] { 0, 0, 0, 0, 0, 0, 0, 7 };
        var directoryUsers = new[]
        {
            new DirectoryUser(
                "CSRI\\alice",
                "Alice",
                false,
                AuthPointLogin: "alice@csri-qt.com"),
            new DirectoryUser(
                "CSRI\\bob",
                "Bob",
                false,
                AuthPointLogin: "new-bob@csri-qt.com"),
            new DirectoryUser(
                "CSRI\\carol",
                "Carol",
                false,
                AuthPointLogin: "carol@csri-qt.com")
        };
        var savedMappings = new[]
        {
            new UserMapping(
                "CSRI\\alice",
                "Alice",
                false,
                string.Empty,
                "alice@csri-qt.com",
                true,
                rowVersion),
            new UserMapping(
                "CSRI\\bob",
                "Bob",
                false,
                string.Empty,
                "old-bob@csri-qt.com",
                true,
                rowVersion)
        };

        var assignments = ActiveDirectoryUserProvider.BuildAuthPointSyncAssignments(
            directoryUsers,
            savedMappings);

        Assert.Collection(
            assignments,
            bob =>
            {
                Assert.Equal("CSRI\\bob", bob.LoginName);
                Assert.Equal("new-bob@csri-qt.com", bob.AuthPointLogin);
                Assert.True(bob.IsEnabled);
                Assert.Equal(rowVersion, bob.ExpectedRowVersion);
            },
            carol =>
            {
                Assert.Equal("CSRI\\carol", carol.LoginName);
                Assert.Equal("carol@csri-qt.com", carol.AuthPointLogin);
                Assert.True(carol.IsEnabled);
                Assert.Null(carol.ExpectedRowVersion);
            });
    }

    [Fact]
    public void MappedDirectoryNamesReplaceOnlyPlaceholderWhdTechnicianLabels()
    {
        var technicians = new[]
        {
            new Technician(string.Empty, "No WHD technician (remove mapping)"),
            new Technician("WHD-TECH-6", "WHD-TECH-6"),
            new Technician("WHD-TECH-12", "Ken Allen"),
            new Technician(
                "WHD-CONFIGURED-ORGANIZATION-ACCOUNT",
                "Helpdesk Manager (whdmgr, organization-wide account)",
                "whdmgr")
        };
        var mappings = new[]
        {
            new UserMapping("CSRI\\cgoemans", "Craig Goemans", false, "WHD-TECH-6"),
            new UserMapping("CSRI\\kallen", "Kenneth Allen", true, "WHD-TECH-12"),
            new UserMapping(
                "CSRI\\dhallen",
                "David H. Allen",
                true,
                "WHD-CONFIGURED-ORGANIZATION-ACCOUNT")
        };

        var restored = ActiveDirectoryUserProvider.RestoreMappedTechnicianLabels(
            technicians,
            mappings);

        Assert.Equal("Craig Goemans", restored.Single(item => item.ExternalId == "WHD-TECH-6").Label);
        Assert.Equal("Ken Allen", restored.Single(item => item.ExternalId == "WHD-TECH-12").Label);
        Assert.Equal(
            "Helpdesk Manager (whdmgr, organization-wide account)",
            restored.Single(item => item.ExternalId == "WHD-CONFIGURED-ORGANIZATION-ACCOUNT").Label);
        Assert.Equal("No WHD technician (remove mapping)", restored[0].Label);
    }

    [Theory]
    [InlineData("2.0.0-alpha.9", "2.0.0-alpha.14", -1)]
    [InlineData("2.0.0-alpha.14", "2.0.0-alpha.9", 1)]
    [InlineData("2.0.0-alpha.14", "2.0.0", -1)]
    [InlineData("2.0.1", "2.0.0", 1)]
    [InlineData("5.0.1", "2.0.0-alpha.25", 1)]
    [InlineData("5.0.2", "5.0.1", 1)]
    public void SemanticVersionsAreOrderedCorrectly(string left, string right, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(SemanticVersion.Compare(left, right)));
    }

    [Theory]
    [InlineData("0.5.1", "5.0.3", 1)]
    [InlineData("0.5.1", "2.0.0-alpha.25", 1)]
    [InlineData("0.5.2", "0.5.1", 1)]
    [InlineData("5.0.3", "0.5.1", -1)]
    public void CorrectedReleaseLineSupersedesMistakenVersionNumbers(string left, string right, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(SemanticVersion.CompareForUpdate(left, right)));
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
    public void CompletedServerDownloadsAreRemovedWithoutDeletingRecoveryArtifacts()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "TechBench-Update-Cleanup-Test-" + Guid.NewGuid().ToString("N"));
        var paths = TestPaths(root);
        var updateOperation = Path.Combine(paths.ManagerDataDirectory, "updates", "download-1");
        var setupOperation = Path.Combine(paths.ManagerDataDirectory, "setup", "extract-1");
        var recoveryOperation = Path.Combine(paths.ManagerDataDirectory, "install-recovery");
        try
        {
            Directory.CreateDirectory(updateOperation);
            Directory.CreateDirectory(setupOperation);
            Directory.CreateDirectory(recoveryOperation);
            File.WriteAllBytes(Path.Combine(updateOperation, "package.zip"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(setupOperation, "payload.bin"), new byte[2048]);
            File.WriteAllText(Path.Combine(recoveryOperation, "keep.txt"), "rollback");

            var result = UpdateCacheCleanup.CleanupNow(paths);

            Assert.Equal(2, result.RemovedOperations);
            Assert.Equal(6144, result.ReclaimedBytes);
            Assert.False(Directory.Exists(updateOperation));
            Assert.False(Directory.Exists(setupOperation));
            Assert.True(File.Exists(Path.Combine(recoveryOperation, "keep.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
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
