namespace TechBench.Tests;

public sealed class ServerManagerUninstallLifecycleScriptTests
{
    [Fact]
    public void UninstallRefusesPendingUpdateBeforeAnyMutation()
    {
        var script = ReadScript();
        var journalCheck = script.IndexOf(
            "$pendingUpdateJournalPath = Join-Path $managerDataPath 'pending-update.json'",
            StringComparison.Ordinal);
        var refusal = script.IndexOf(
            "An interrupted TechBench service update is still pending",
            journalCheck,
            StringComparison.Ordinal);
        var lockAcquisition = script.IndexOf(
            "$managerLifetimeLock = Open-UninstallManagerLifetimeLock",
            refusal,
            StringComparison.Ordinal);
        var serviceStop = script.IndexOf(
            "Stop-Service -Name $ServiceName",
            lockAcquisition,
            StringComparison.Ordinal);

        Assert.True(journalCheck >= 0);
        Assert.True(refusal > journalCheck);
        Assert.True(lockAcquisition > refusal);
        Assert.True(serviceStop > lockAcquisition);
        Assert.Contains("-AllowLeaf", script[journalCheck..refusal], StringComparison.Ordinal);
        Assert.Contains(
            "Open TechBench Server Manager and let it complete or restore that update",
            script[journalCheck..lockAcquisition],
            StringComparison.Ordinal);
        Assert.Contains("No service or files were changed.", script[journalCheck..lockAcquisition], StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallExclusivelyLocksManagerBeforeAnyServiceMutation()
    {
        var script = ReadScript();
        var lockFunction = script.IndexOf(
            "function Open-UninstallManagerLifetimeLock",
            StringComparison.Ordinal);
        var lockAcquisition = script.IndexOf(
            "$managerLifetimeLock = Open-UninstallManagerLifetimeLock",
            lockFunction,
            StringComparison.Ordinal);
        var serviceStop = script.IndexOf(
            "Stop-Service -Name $ServiceName",
            lockAcquisition,
            StringComparison.Ordinal);
        var serviceDelete = script.IndexOf(
            "$output = & $scExecutable delete $ServiceName",
            lockAcquisition,
            StringComparison.Ordinal);

        Assert.True(lockFunction >= 0);
        Assert.True(lockAcquisition > lockFunction);
        Assert.True(serviceStop > lockAcquisition);
        Assert.True(serviceDelete > lockAcquisition);
        Assert.Contains(
            "$scExecutable = Join-Path ([Environment]::SystemDirectory) 'sc.exe'",
            script[lockAcquisition..serviceDelete],
            StringComparison.Ordinal);
        Assert.Contains("server-manager.lock", script[lockFunction..lockAcquisition], StringComparison.Ordinal);
        Assert.Contains("[IO.FileMode]::OpenOrCreate", script[lockFunction..lockAcquisition], StringComparison.Ordinal);
        Assert.Contains("[IO.FileShare]::None", script[lockFunction..lockAcquisition], StringComparison.Ordinal);
        Assert.Contains("win32Error -in @(32, 33)", script[lockFunction..lockAcquisition], StringComparison.Ordinal);
        Assert.Contains(
            "Exit it from the notification area icon before uninstalling. No service or files were changed.",
            script[lockFunction..lockAcquisition],
            StringComparison.Ordinal);
        Assert.Contains("-AllowLeaf", script[lockFunction..lockAcquisition], StringComparison.Ordinal);
        Assert.Contains("if (-not $WhatIfPreference)", script[..serviceStop], StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallLeavesWorkingDirectoryAndHoldsLockThroughBinaryRemoval()
    {
        var script = ReadScript();
        var lockAcquisition = script.IndexOf(
            "$managerLifetimeLock = Open-UninstallManagerLifetimeLock",
            StringComparison.Ordinal);
        var systemRootLocation = script.IndexOf(
            "Set-Location -LiteralPath ([Environment]::SystemDirectory)",
            lockAcquisition,
            StringComparison.Ordinal);
        var serviceBinaryRemoval = script.IndexOf(
            "Remove-SafeDirectoryTree -Path $installPath",
            systemRootLocation,
            StringComparison.Ordinal);
        var managerBinaryRemoval = script.IndexOf(
            "Remove-SafeDirectoryTree -Path $managerPath",
            serviceBinaryRemoval,
            StringComparison.Ordinal);
        var lockDispose = script.IndexOf(
            "$managerLifetimeLock.Dispose()",
            managerBinaryRemoval,
            StringComparison.Ordinal);
        var managerStateRemoval = script.IndexOf(
            "Remove-SafeDirectoryTree -Path $managerDataPath",
            lockDispose,
            StringComparison.Ordinal);

        Assert.True(systemRootLocation > lockAcquisition);
        Assert.True(serviceBinaryRemoval > systemRootLocation);
        Assert.True(managerBinaryRemoval > serviceBinaryRemoval);
        Assert.True(lockDispose > managerBinaryRemoval);
        Assert.True(managerStateRemoval > lockDispose);
        Assert.Equal(
            managerStateRemoval,
            script.IndexOf("Remove-SafeDirectoryTree -Path $managerDataPath", StringComparison.Ordinal));
        Assert.Contains(
            "Set-Location -LiteralPath $originalLocationPath",
            script[managerStateRemoval..],
            StringComparison.Ordinal);
    }

    private static string ReadScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "scripts",
                "Uninstall-TechBenchSyncService.ps1");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the TechBenchV2 repository root.");
    }
}
