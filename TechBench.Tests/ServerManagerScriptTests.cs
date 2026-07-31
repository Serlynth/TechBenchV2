namespace TechBench.Tests;

public sealed class ServerManagerScriptTests
{
    [Fact]
    public void ManagerElevatesAndProvidesExpectedServiceControls()
    {
        var source = ReadScript("TechBench-ServerManager.ps1");

        Assert.Contains("Test-IsAdministrator", source, StringComparison.Ordinal);
        Assert.Contains("Start-Process -FilePath $powershell -Verb RunAs", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-ServiceControl", source, StringComparison.Ordinal);
        Assert.Contains("'Start', 'Stop', 'Restart'", source, StringComparison.Ordinal);
        Assert.Contains("Install / Apply password", source, StringComparison.Ordinal);
        Assert.Contains("Show service password", source, StringComparison.Ordinal);
        Assert.Contains("$schemaVersion -lt 13 -or $schemaVersion -gt 14", source, StringComparison.Ordinal);
        Assert.Contains("database schemas 13 through 14", source, StringComparison.Ordinal);
        Assert.DoesNotContain("database schema 8", source, StringComparison.Ordinal);
        Assert.Contains("ShowWhdSecretCheckBox", source, StringComparison.Ordinal);
        Assert.Contains("ShowSageSecretCheckBox", source, StringComparison.Ordinal);
        Assert.Contains("requires the complete extracted TechBench service release package", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$sourceDirectory = if", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagerKeepsSecretsOutOfArgumentsAndSharedConfiguration()
    {
        var source = ReadScript("TechBench-ServerManager.ps1");

        Assert.Contains("New-SecureStringFromBox", source, StringComparison.Ordinal);
        Assert.Contains("[PSCredential]::new($account, $securePassword)", source, StringComparison.Ordinal);
        Assert.Contains("$arguments[$parameterName] = $secureSecret", source, StringComparison.Ordinal);
        Assert.Contains("Set-TechBenchSyncCredential.ps1", source, StringComparison.Ordinal);
        Assert.Contains("Set-TechBenchSageSyncCredential.ps1", source, StringComparison.Ordinal);
        Assert.Contains(
            "Shared WHD/Sage settings are managed on the right. Secrets remain machine-protected on this server.",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[string]$Password", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password=", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoutineUpdaterVerifiesAndRollsBackWithoutRecreatingService()
    {
        var source = ReadScript("TechBench-ServerManager.ps1");
        var updateStart = source.IndexOf(
            "function Download-AndInstallServiceUpdate",
            StringComparison.Ordinal);
        var nextFunction = source.IndexOf("function New-Label", updateStart, StringComparison.Ordinal);
        var updateFunction = source[updateStart..nextFunction];

        Assert.Contains("Assert-ApprovedReleaseAssetUrl", source, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -LiteralPath $zipPath -Algorithm SHA256", source, StringComparison.Ordinal);
        Assert.Contains("Get-SafeArchiveDestination", source, StringComparison.Ordinal);
        Assert.Contains("Assert-ServicePackageManifest", source, StringComparison.Ordinal);
        Assert.Contains("Assert-RequiredDatabaseSchema", source, StringComparison.Ordinal);
        Assert.Contains("RequiredDatabaseSchemaVersion", source, StringComparison.Ordinal);
        Assert.Contains("PackageFormatVersion", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-BoundedDownload", source, StringComparison.Ordinal);
        Assert.Contains("Initialize-AdminUpdateDirectory", source, StringComparison.Ordinal);
        Assert.Contains("MaximumArchiveEntries", source, StringComparison.Ordinal);
        Assert.Contains("duplicate Windows paths", source, StringComparison.Ordinal);
        Assert.Contains("Install-VerifiedServicePayload", updateFunction, StringComparison.Ordinal);
        Assert.Contains("Move-Item -LiteralPath $backupPath -Destination $installPath", source, StringComparison.Ordinal);
        Assert.Contains("The previous service files were restored successfully.", source, StringComparison.Ordinal);
        Assert.Contains("not digitally signed", source, StringComparison.Ordinal);
        Assert.Contains("Wait-ForStableRunningService", source, StringComparison.Ordinal);
        Assert.Contains("Repair-InterruptedUpdate", source, StringComparison.Ordinal);
        Assert.Contains("$journal.Phase = 'ManagerSwapPrepared'", source, StringComparison.Ordinal);
        Assert.Contains("complete Server Manager companion set", source, StringComparison.Ordinal);
        Assert.Contains("both its rollback and staged copies are missing", source, StringComparison.Ordinal);
        Assert.Contains("$managerFile.Stage, $managerFile.Target, $managerFile.Backup", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("$journal.Phase = 'ManagerSwapPrepared'", StringComparison.Ordinal) <
            source.IndexOf("$managerFile.Stage, $managerFile.Target, $managerFile.Backup", StringComparison.Ordinal));
        Assert.Contains("$manifest.SageOdbcWorkerRuntime -cne 'win-x86'", source, StringComparison.Ordinal);
        Assert.Contains("$manifest.SelfContained -isnot [bool]", source, StringComparison.Ordinal);
        Assert.Contains("The Sage ODBC worker executable version does not match release", source, StringComparison.Ordinal);
        Assert.Contains("The Sage ODBC worker executable is not x86", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Install-OrUpdateService", updateFunction, StringComparison.Ordinal);
        Assert.DoesNotContain("ServicePasswordBox.Text", updateFunction, StringComparison.Ordinal);
        Assert.DoesNotContain("--check-whd-secret", updateFunction, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseDiscoveryEnumeratesGithubJsonArraysOnWindowsPowerShell51()
    {
        var source = ReadScript("TechBench-ServerManager.ps1");

        Assert.Contains("$releaseResponse = Invoke-RestMethod", source, StringComparison.Ordinal);
        Assert.Contains("$releases = @($releaseResponse)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$releases = @(Invoke-RestMethod", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PublisherAndInstallerIntegrateManagerWithSchemaManifest()
    {
        var publisher = ReadScript("Publish-TechBenchServer.ps1");
        var installer = ReadScript("Install-TechBenchSyncService.ps1");
        var uninstaller = ReadScript("Uninstall-TechBenchSyncService.ps1");

        Assert.Contains("[int]$RequiredDatabaseSchemaVersion = 15", publisher, StringComparison.Ordinal);
        Assert.Contains(
            "[ValidatePattern('^\\d+\\.\\d+\\.\\d+$')]",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains("if ($versionIsPrerelease) { continue }", ReadScript("TechBench-ServerManager.ps1"), StringComparison.Ordinal);
        Assert.Contains("'TechBench-ServerManager.ps1'", publisher, StringComparison.Ordinal);
        Assert.Contains(
            "RequiredDatabaseSchemaVersion = $RequiredDatabaseSchemaVersion",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains("PackageFormatVersion = 1", publisher, StringComparison.Ordinal);
        Assert.Contains("Install-ServerManagerShortcut", installer, StringComparison.Ordinal);
        Assert.Contains("TechBench Server Manager", installer, StringComparison.Ordinal);
        Assert.Contains("TechBench Server Manager.lnk", installer, StringComparison.Ordinal);
        Assert.Contains(
            "'sage-odbc-worker\\TechBench.SageOdbcWorker.runtimeconfig.json'",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "'sage-odbc-worker\\TechBench.SageOdbcWorker.deps.json'",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "Update-InstalledPackageManifestConfigurationEntry -PackageDirectory $installPath",
            installer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$_.Name -ne 'package-manifest.json'",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "Update-InstalledPackageManifestConfigurationEntry -PackageDirectory $stagePath",
            ReadScript("TechBench-ServerManager.ps1"),
            StringComparison.Ordinal);
        Assert.Contains("TechBench Server Manager.lnk", uninstaller, StringComparison.Ordinal);

        var normalizedInstaller = installer.Replace("\r\n", "\n", StringComparison.Ordinal);
        var addTypeHereStringEnd = normalizedInstaller.IndexOf("'@\n    }", StringComparison.Ordinal);
        var shortcutFunction = normalizedInstaller.IndexOf(
            "function Install-ServerManagerShortcut",
            StringComparison.Ordinal);
        Assert.True(addTypeHereStringEnd >= 0);
        Assert.True(shortcutFunction > addTypeHereStringEnd);
    }

    [Fact]
    public void UninstallerValidatesServiceNameAndSafelyRemovesManagerState()
    {
        var source = ReadScript("Uninstall-TechBenchSyncService.ps1");

        Assert.Contains("[ValidatePattern('^[A-Za-z0-9_.-]+$')]", source, StringComparison.Ordinal);
        Assert.Contains("CSRI\\TechBench Server Manager", source, StringComparison.Ordinal);
        Assert.Contains("function Remove-SafeDirectoryTree", source, StringComparison.Ordinal);
        Assert.Contains("$target.StartsWith($rootPrefix", source, StringComparison.Ordinal);
        Assert.Contains("directory tree containing a reparse point", source, StringComparison.Ordinal);
        Assert.Contains("-not $KeepBinaries -and (Test-Path -LiteralPath $managerDataPath)", source, StringComparison.Ordinal);
        Assert.Contains("Never trust paths recorded", source, StringComparison.Ordinal);
        Assert.Contains("-KeepBinaries preserved", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConvertFrom-Json", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $managerDataPath -Recurse", source, StringComparison.Ordinal);
    }

    private static string ReadScript(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", name);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the TechBenchV2 repository root.");
    }
}
