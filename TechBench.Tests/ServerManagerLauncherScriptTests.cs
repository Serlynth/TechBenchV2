namespace TechBench.Tests;

public sealed class ServerManagerLauncherScriptTests
{
    [Fact]
    public void StartMenuUsesConsolelessBootstrapWithFixedArguments()
    {
        var installer = ReadScript("Install-TechBenchSyncService.ps1");

        Assert.Contains("[Environment]::SystemDirectory", installer, StringComparison.Ordinal);
        Assert.Contains("'wscript.exe'", installer, StringComparison.Ordinal);
        Assert.Contains("Start-TechBenchServerManager.vbs", installer, StringComparison.Ordinal);
        Assert.Contains("Start-TechBenchServerManager.ps1", installer, StringComparison.Ordinal);
        Assert.Contains("csri-techbench-icon.ico", installer, StringComparison.Ordinal);
        Assert.Contains("$ServiceName", installer, StringComparison.Ordinal);
        Assert.Contains("$InstalledDirectory", installer, StringComparison.Ordinal);
        Assert.Contains("$DataDirectory", installer, StringComparison.Ordinal);
        Assert.Contains("$ManagerDirectory", installer, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "System32\\WindowsPowerShell\\v1.0\\powershell.exe'\r\n        $shortcut.Arguments",
            installer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BootstrapElevatesIntoHidden64BitStaPowerShellAndReportsFailures()
    {
        var launcher = ReadScript("Start-TechBenchServerManager.ps1");
        var vbs = ReadScript("Start-TechBenchServerManager.vbs");

        Assert.Contains("-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass", launcher, StringComparison.Ordinal);
        Assert.Contains("'System32'", launcher, StringComparison.Ordinal);
        Assert.Contains("'Sysnative'", launcher, StringComparison.Ordinal);
        Assert.Contains("[Environment]::SystemDirectory", launcher, StringComparison.Ordinal);
        Assert.Contains("$startArguments.Verb = 'RunAs'", launcher, StringComparison.Ordinal);
        Assert.Contains("[Threading.ApartmentState]::STA", launcher, StringComparison.Ordinal);
        Assert.Contains("Write-StartupFailure", launcher, StringComparison.Ordinal);
        Assert.Contains("startup-errors.log", launcher, StringComparison.Ordinal);
        Assert.Contains("Show-StartupFailure", launcher, StringComparison.Ordinal);
        Assert.Contains("Windows.Forms.MessageBox", launcher, StringComparison.Ordinal);
        Assert.Contains("Start-Process @startArguments | Out-Null", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("Wait = $true", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("PassThru = $true", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("$env:SystemRoot", launcher, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("shell.Run(command, 0, False)", vbs, StringComparison.Ordinal);
        Assert.Contains("fileSystem.GetSpecialFolder(1)", vbs, StringComparison.Ordinal);
        Assert.Contains("WScript.Arguments", vbs, StringComparison.Ordinal);
        Assert.Contains("arguments.Count <> 4", vbs, StringComparison.Ordinal);
        Assert.Contains("-STA -WindowStyle Hidden", vbs, StringComparison.Ordinal);
        Assert.DoesNotContain("shell.Run(command, 0, True)", vbs, StringComparison.Ordinal);
        Assert.DoesNotContain("%SystemRoot%", vbs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublishedPackageIncludesEveryManagerCompanion()
    {
        var publisher = ReadScript("Publish-TechBenchServer.ps1");
        var installer = ReadScript("Install-TechBenchSyncService.ps1");
        var manager = ReadScript("TechBench-ServerManager.ps1");

        foreach (var name in new[]
        {
            "TechBench-ServerManager.ps1",
            "Start-TechBenchServerManager.ps1",
            "Start-TechBenchServerManager.vbs",
            "csri-techbench-icon.ico"
        })
        {
            Assert.Contains(name, publisher, StringComparison.Ordinal);
            Assert.Contains(name, installer, StringComparison.Ordinal);
            Assert.Contains(name, manager, StringComparison.Ordinal);
        }

        Assert.Contains("Assets\\csri-techbench-icon.ico", publisher, StringComparison.Ordinal);
    }

    [Fact]
    public void RoutineUpdateTransactionsAllManagerCompanionsAndSupportsOldJournal()
    {
        var manager = ReadScript("TechBench-ServerManager.ps1");

        Assert.Contains("JournalFormatVersion = 2", manager, StringComparison.Ordinal);
        Assert.Contains("ManagerFiles = $managerFiles", manager, StringComparison.Ordinal);
        Assert.Contains("Get-ValidatedJournalManagerFiles", manager, StringComparison.Ordinal);
        Assert.Contains("if ($formatVersion -eq 1)", manager, StringComparison.Ordinal);
        Assert.Contains("foreach ($managerFile in $managerFiles)", manager, StringComparison.Ordinal);
        Assert.Contains("$managerFile.Stage, $managerFile.Target, $managerFile.Backup", manager, StringComparison.Ordinal);
        Assert.Contains("Restore-ManagerFileFromBackup", manager, StringComparison.Ordinal);
        Assert.Contains("staged Server Manager companion failed", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedRollbackRetainsEveryArtifactNeededByNextLaunchRecovery()
    {
        var manager = ReadScript("TechBench-ServerManager.ps1");
        var installStart = manager.IndexOf(
            "function Install-VerifiedServicePayload",
            StringComparison.Ordinal);
        var installEnd = manager.IndexOf(
            "function Assert-RequiredDatabaseSchema",
            installStart,
            StringComparison.Ordinal);
        var install = manager[installStart..installEnd];

        Assert.Contains("Copy-Item -LiteralPath $BackupPath", manager, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Move-Item -LiteralPath $managerFile.Backup",
            manager,
            StringComparison.Ordinal);
        Assert.Contains(
            "Move-Item -LiteralPath $managerFile.Target",
            manager,
            StringComparison.Ordinal);
        Assert.Contains("-Destination $managerFile.Stage", manager, StringComparison.Ordinal);
        Assert.Contains("$journal.Phase = 'RolledBack'", install, StringComparison.Ordinal);
        Assert.Contains("$cleanupIsSafe = -not $journalWritten", install, StringComparison.Ordinal);
        Assert.Contains("$updateSucceeded -or $rollbackSucceeded", install, StringComparison.Ordinal);
        Assert.Contains(
            "the protected update journal and remaining artifacts were retained",
            install,
            StringComparison.Ordinal);

        var finallyStart = install.LastIndexOf("} finally {", StringComparison.Ordinal);
        var cleanupCall = install.IndexOf(
            "Remove-UpdateRecoveryArtifacts",
            finallyStart,
            StringComparison.Ordinal);
        Assert.True(finallyStart >= 0);
        Assert.True(cleanupCall > finallyStart);
        Assert.DoesNotContain(
            "Remove-Item -LiteralPath $managerFile.Stage",
            install[finallyStart..],
            StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalJournalPhasesMakeCleanupRestartable()
    {
        var manager = ReadScript("TechBench-ServerManager.ps1");
        var repairStart = manager.IndexOf(
            "function Repair-InterruptedUpdate",
            StringComparison.Ordinal);
        var repairEnd = manager.IndexOf(
            "function Assert-ServicePackageManifest",
            repairStart,
            StringComparison.Ordinal);
        var repair = manager[repairStart..repairEnd];

        Assert.Contains("'Committed', 'RolledBack'", repair, StringComparison.Ordinal);
        Assert.Contains(
            "$phase -notin @('Committed', 'RolledBack')",
            repair,
            StringComparison.Ordinal);
        Assert.Contains("Remove-UpdateRecoveryArtifacts", repair, StringComparison.Ordinal);
        Assert.True(
            repair.IndexOf("Remove-UpdateRecoveryArtifacts", StringComparison.Ordinal) <
            repair.IndexOf("Remove-UpdateJournal", StringComparison.Ordinal));
        Assert.Contains("Write-UpdateJournal -State $journal", repair, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateJournalIsDurablyFlushedBeforeAtomicReplacement()
    {
        var manager = ReadScript("TechBench-ServerManager.ps1");
        var writerStart = manager.IndexOf("function Write-UpdateJournal", StringComparison.Ordinal);
        var writerEnd = manager.IndexOf("function Remove-UpdateJournal", writerStart, StringComparison.Ordinal);
        var writer = manager[writerStart..writerEnd];

        var createNew = writer.IndexOf("[IO.FileMode]::CreateNew", StringComparison.Ordinal);
        var writeThrough = writer.IndexOf("[IO.FileOptions]::WriteThrough", StringComparison.Ordinal);
        var durableFlush = writer.IndexOf("$journalStream.Flush($true)", StringComparison.Ordinal);
        var atomicReplace = writer.IndexOf("[IO.File]::Replace($temporaryPath, $journalPath, $null)", StringComparison.Ordinal);

        Assert.True(createNew >= 0);
        Assert.True(writeThrough > createNew);
        Assert.True(durableFlush > writeThrough);
        Assert.True(atomicReplace > durableFlush);
        Assert.DoesNotContain("Set-Content -LiteralPath $temporaryPath", writer, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryInfersManagerSwapFromArtifactsWhenJournalPhaseIsStale()
    {
        var manager = ReadScript("TechBench-ServerManager.ps1");
        var repairStart = manager.IndexOf("function Repair-InterruptedUpdate", StringComparison.Ordinal);
        var repairEnd = manager.IndexOf("function Assert-ServicePackageManifest", repairStart, StringComparison.Ordinal);
        var repair = manager[repairStart..repairEnd];

        Assert.Contains("$managerSwapArtifactEvidence = $false", repair, StringComparison.Ordinal);
        Assert.Contains("if ($backupExists -or", repair, StringComparison.Ordinal);
        Assert.Contains("-not $managerFile.HadExisting -and -not $stageExists -and $targetExists", repair, StringComparison.Ordinal);
        Assert.Contains("$managerStateNeedsClassification", repair, StringComparison.Ordinal);
        Assert.Contains("$managerSwapArtifactEvidence -or", repair, StringComparison.Ordinal);
        Assert.Contains("$managerSwapStarted =", repair, StringComparison.Ordinal);
        Assert.True(
            repair.IndexOf("$managerSwapArtifactEvidence = $false", StringComparison.Ordinal) <
            repair.IndexOf("Restore-ManagerCompanionState", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyV1ConsumedRollbackIsAcceptedOnlyAfterManifestProof()
    {
        var manager = ReadScript("TechBench-ServerManager.ps1");
        var repairStart = manager.IndexOf(
            "function Repair-InterruptedUpdate",
            StringComparison.Ordinal);
        var repairEnd = manager.IndexOf(
            "function Assert-ServicePackageManifest",
            repairStart,
            StringComparison.Ordinal);
        var repair = manager[repairStart..repairEnd];
        var serviceRestore = repair.IndexOf(
            "Move-Item -LiteralPath $backupPath -Destination $script:InstallDirectory",
            StringComparison.Ordinal);
        var manifestProof = repair.IndexOf(
            "Test-ManagerFileMatchesInstalledPackageManifest",
            StringComparison.Ordinal);

        Assert.Contains("JournalFormatVersion -eq 1", repair, StringComparison.Ordinal);
        Assert.Contains("$legacyManagerNeedsManifestProof = $true", repair, StringComparison.Ordinal);
        Assert.True(serviceRestore >= 0);
        Assert.True(manifestProof > serviceRestore);
        Assert.Contains("$managerSwapStarted = $false", repair, StringComparison.Ordinal);
        Assert.Contains("restored service package manifest", repair, StringComparison.Ordinal);

        var proofStart = manager.IndexOf(
            "function Test-ManagerFileMatchesInstalledPackageManifest",
            StringComparison.Ordinal);
        var proofEnd = manager.IndexOf(
            "function Restore-ManagerFileFromBackup",
            proofStart,
            StringComparison.Ordinal);
        var proof = manager[proofStart..proofEnd];
        Assert.Contains("package-manifest.json", proof, StringComparison.Ordinal);
        Assert.Contains("$ManagerFile.Target", proof, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", proof, StringComparison.Ordinal);
        Assert.Contains("$packagedPath", proof, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstLaunchAfterOldUpdaterSelfHealsLauncherIconAndShortcut()
    {
        var manager = ReadScript("TechBench-ServerManager.ps1");
        var repairDefinition = manager.IndexOf(
            "function Repair-ManagerLaunchIntegration",
            StringComparison.Ordinal);
        var repairCall = manager.IndexOf(
            "    Repair-ManagerLaunchIntegration",
            repairDefinition + "function Repair-ManagerLaunchIntegration".Length,
            StringComparison.Ordinal);
        var formCreation = manager.IndexOf(
            "$script:MainForm = [Windows.Forms.Form]::new()",
            StringComparison.Ordinal);

        Assert.True(repairDefinition >= 0);
        Assert.True(repairCall > repairDefinition);
        Assert.True(repairCall < formCreation);
        Assert.Contains("$script:InstallDirectory $fileName", manager, StringComparison.Ordinal);
        Assert.Contains("failed manifest verification", manager, StringComparison.Ordinal);
        Assert.Contains("pending-update.json", manager[repairDefinition..repairCall], StringComparison.Ordinal);
        Assert.Contains("TechBench Server Manager.lnk", manager, StringComparison.Ordinal);
        Assert.Contains("[Environment]::SystemDirectory", manager, StringComparison.Ordinal);
        Assert.Contains("'wscript.exe'", manager, StringComparison.Ordinal);
        Assert.Contains("The Start Menu launcher could not be repaired", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagerLifetimeLockSerializesAllSessionsAndSurvivesTrayLifetime()
    {
        var manager = ReadScript("TechBench-ServerManager.ps1");
        var lockFunction = manager.IndexOf(
            "function Open-ManagerLifetimeLock",
            StringComparison.Ordinal);
        var lockAcquisition = manager.IndexOf(
            "$script:ManagerLifetimeLock = Open-ManagerLifetimeLock",
            lockFunction,
            StringComparison.Ordinal);
        var launchRepair = manager.IndexOf(
            "Repair-ManagerLaunchIntegration",
            lockAcquisition,
            StringComparison.Ordinal);
        var messageLoop = manager.IndexOf(
            "[Windows.Forms.Application]::Run($script:MainForm)",
            lockAcquisition,
            StringComparison.Ordinal);
        var lockDispose = manager.IndexOf(
            "$script:ManagerLifetimeLock.Dispose()",
            messageLoop,
            StringComparison.Ordinal);

        Assert.True(lockFunction >= 0);
        Assert.True(lockAcquisition > lockFunction);
        Assert.True(launchRepair > lockAcquisition);
        Assert.True(messageLoop > launchRepair);
        Assert.True(lockDispose > messageLoop);
        Assert.Contains("Initialize-AdminManagerDataRoot", manager[lockFunction..lockAcquisition], StringComparison.Ordinal);
        Assert.Contains("server-manager.lock", manager[lockFunction..lockAcquisition], StringComparison.Ordinal);
        Assert.Contains("-AllowLeaf", manager[lockFunction..lockAcquisition], StringComparison.Ordinal);
        Assert.Contains("[IO.FileShare]::None", manager[lockFunction..lockAcquisition], StringComparison.Ordinal);
        Assert.Contains("win32Error -in @(32, 33)", manager[lockFunction..lockAcquisition], StringComparison.Ordinal);
        Assert.Contains(
            "already running. Use its notification area icon",
            manager[lockFunction..lockAcquisition],
            StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $lockPath", manager, StringComparison.Ordinal);
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
