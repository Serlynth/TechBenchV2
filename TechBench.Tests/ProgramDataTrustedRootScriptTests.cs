namespace TechBench.Tests;

public sealed class ProgramDataTrustedRootScriptTests
{
    [Theory]
    [InlineData("Install-TechBenchSyncService.ps1")]
    [InlineData("TechBench-ServerManager.ps1")]
    [InlineData("Start-TechBenchServerManager.ps1")]
    [InlineData("Uninstall-TechBenchSyncService.ps1")]
    public void PrivilegedScriptsRejectEqualAncestorAndDescendantTrees(string scriptName)
    {
        var source = ReadScript(scriptName);
        var helper = SliceFunction(source, "Assert-PathTreesDoNotOverlap");

        Assert.Contains("[IO.Path]::GetFullPath($FirstPath)", helper, StringComparison.Ordinal);
        Assert.Contains("Replace($alternateSeparator, $separator)", helper, StringComparison.Ordinal);
        Assert.Contains("$firstPrefix = $firstCanonical + $separator", helper, StringComparison.Ordinal);
        Assert.Contains("$secondPrefix = $secondCanonical + $separator", helper, StringComparison.Ordinal);
        Assert.Contains("$firstCanonical.Equals($secondCanonical", helper, StringComparison.Ordinal);
        Assert.Contains("$firstCanonical.StartsWith($secondPrefix", helper, StringComparison.Ordinal);
        Assert.Contains("$secondCanonical.StartsWith($firstPrefix", helper, StringComparison.Ordinal);
        Assert.Contains("must not be equal or contain one another", helper, StringComparison.Ordinal);
        Assert.Contains(
            "-FirstName 'InstallDirectory' -SecondName 'ManagerDirectory'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-FirstName 'DataDirectory' -SecondName 'ManagerDataDirectory'",
            source,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Install-TechBenchSyncService.ps1")]
    [InlineData("TechBench-ServerManager.ps1")]
    [InlineData("Start-TechBenchServerManager.ps1")]
    [InlineData("Uninstall-TechBenchSyncService.ps1")]
    public void PrivilegedScriptsInspectEveryExistingPathComponent(string scriptName)
    {
        var source = ReadScript(scriptName);
        var helper = SliceFunction(source, "Assert-NoReparsePointInPath");

        Assert.Contains("Get-Item -LiteralPath $rootPath -Force -ErrorAction Stop", helper, StringComparison.Ordinal);
        Assert.Contains("$trustedRootItem.PSIsContainer", helper, StringComparison.Ordinal);
        Assert.Contains("$trustedRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint", helper, StringComparison.Ordinal);
        Assert.Contains("for ($index = 0; $index -lt $segments.Length; $index++)", helper, StringComparison.Ordinal);
        Assert.Contains("Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop", helper, StringComparison.Ordinal);
        Assert.Contains("Refusing to follow a reparse-point path component", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerProtectsAnchorBeforeUsingServiceOrManagerChildren()
    {
        var source = ReadScript("Install-TechBenchSyncService.ps1");
        var anchor = SliceFunction(source, "Initialize-ProtectedProgramDataAnchor");
        var secretAcl = SliceFunction(source, "Set-SecretDirectoryAcl");
        var secretFileAcl = SliceFunction(source, "Assert-TrustedSecretFileAcl");
        var stage = SliceFunction(source, "New-VerifiedAdministratorInstallStage");
        var installBodyStart = source.IndexOf(
            "if ($PSCmdlet.ShouldProcess($DisplayName", StringComparison.Ordinal);
        var installBody = source[installBodyStart..];

        Assert.Contains("SetAccessRuleProtection($true, $false)", source, StringComparison.Ordinal);
        Assert.Contains("$security.SetOwner($administrators)", source, StringComparison.Ordinal);
        Assert.Contains("'S-1-5-18'", source, StringComparison.Ordinal);
        Assert.Contains("'S-1-5-32-544'", source, StringComparison.Ordinal);
        Assert.Contains("'S-1-5-32-545'", source, StringComparison.Ordinal);
        Assert.Contains("ReadAndExecute, Synchronize", source, StringComparison.Ordinal);
        Assert.Contains("Assert-LegacyTechBenchAnchorCanMigrate", anchor, StringComparison.Ordinal);
        Assert.Contains("Automatic legacy-alpha ACL migration was refused", anchor, StringComparison.Ordinal);
        Assert.Contains("New-ProtectedDirectoryAtomically", anchor, StringComparison.Ordinal);
        Assert.Contains("[IO.Directory]::Move($temporaryPath, $Path)", source, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparsePointsInDirectoryTree", secretAcl, StringComparison.Ordinal);
        Assert.Contains("$allowedSecretNames = @('whd.secret', 'sage.secret')", secretAcl, StringComparison.Ordinal);
        Assert.Contains("Assert-TrustedSecretFileAcl -Path $entry.FullName", secretAcl, StringComparison.Ordinal);
        Assert.Contains("Rights = [Security.AccessControl.FileSystemRights]'ReadAndExecute, Synchronize'", secretAcl, StringComparison.Ordinal);
        Assert.DoesNotContain("Rights = [Security.AccessControl.FileSystemRights]::Modify", secretAcl, StringComparison.Ordinal);
        Assert.DoesNotContain("whd.secret.new", secretAcl, StringComparison.Ordinal);
        Assert.Contains("$item.PSObject.Properties['LinkType']", secretFileAcl, StringComparison.Ordinal);
        Assert.Contains("$allowedSidValues -notcontains $owner", secretFileAcl, StringComparison.Ordinal);
        var installerOutsiderCheck = secretFileAcl[..secretFileAcl.IndexOf(
            "if ($RequireServiceReadOnly)", StringComparison.Ordinal)];
        Assert.Contains(
            "$allowedSidValues -notcontains $_.IdentityReference.Value",
            installerOutsiderCheck,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$_.FileSystemRights -band",
            installerOutsiderCheck,
            StringComparison.Ordinal);
        Assert.Contains("Remove it and reprovision the credential", secretFileAcl, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireServiceReadOnly", secretFileAcl, StringComparison.Ordinal);
        Assert.Contains("normalized credential file ACL still permits inheritance", secretFileAcl, StringComparison.Ordinal);
        Assert.Contains("normalized credential file still grants write access", secretFileAcl, StringComparison.Ordinal);
        Assert.Contains("-AllowedWriteSidValues $privilegedWriteSids", secretAcl, StringComparison.Ordinal);
        Assert.Contains("-RequireServiceReadOnly", secretAcl, StringComparison.Ordinal);
        var secretNormalizationStart = secretAcl.IndexOf(
            "foreach ($secretName in @('whd.secret', 'sage.secret'))",
            StringComparison.Ordinal);
        var secretSetAcl = secretAcl.IndexOf(
            "Set-Acl -LiteralPath $secretPath",
            secretNormalizationStart,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-RequireServiceReadOnly",
            secretAcl[secretNormalizationStart..secretSetAcl],
            StringComparison.Ordinal);
        Assert.Contains(
            "-RequireServiceReadOnly",
            secretAcl[secretSetAcl..],
            StringComparison.Ordinal);
        Assert.True(
            secretAcl.IndexOf("foreach ($entry in @(Get-ChildItem", StringComparison.Ordinal) <
            secretAcl.IndexOf("Set-Acl -LiteralPath $Path", StringComparison.Ordinal));
        Assert.Contains("Initialize-ProtectedProgramDataAnchor", stage, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparsePointsInDirectoryTree -Path $managerDataRoot", stage, StringComparison.Ordinal);
        Assert.True(
            installBody.IndexOf("Initialize-ProtectedProgramDataAnchor", StringComparison.Ordinal) <
            installBody.IndexOf("Set-SecretDirectoryAcl", StringComparison.Ordinal));
        Assert.True(
            installBody.IndexOf("Set-SecretDirectoryAcl", StringComparison.Ordinal) <
            installBody.IndexOf("New-VerifiedAdministratorInstallStage", StringComparison.Ordinal));
    }

    [Fact]
    public void ManagerSecuresAndScansItsDataRootBeforeAdoptingContents()
    {
        var source = ReadScript("TechBench-ServerManager.ps1");
        var initialize = SliceFunction(source, "Initialize-AdminManagerDataRoot");
        var anchor = SliceFunction(source, "Initialize-ProtectedProgramDataAnchor");
        var legacy = SliceFunction(source, "Assert-LegacyTechBenchAnchorCanMigrate");
        var protectLegacy = SliceFunction(source, "Protect-LegacyServiceDataDirectory");
        var secretFile = SliceFunction(source, "Assert-TrustedManagerSecretFile");
        var repair = SliceFunction(source, "Repair-ManagerLaunchIntegration");
        var lifetimeLock = source.IndexOf(
            "$script:ManagerLifetimeLock = Open-ManagerLifetimeLock",
            StringComparison.Ordinal);
        var serializedNormalization = source.IndexOf(
            "Protect-LegacyServiceDataDirectory",
            lifetimeLock,
            StringComparison.Ordinal);
        var launchRepair = source.IndexOf(
            "Repair-ManagerLaunchIntegration",
            serializedNormalization,
            StringComparison.Ordinal);

        Assert.Contains("Initialize-ProtectedProgramDataAnchor", initialize, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparsePointInPath", initialize, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparsePointsInDirectoryTree", initialize, StringComparison.Ordinal);
        Assert.Contains("Assert-TrustedDirectoryAcl", initialize, StringComparison.Ordinal);
        Assert.Contains("New-ProtectedDirectoryAtomically", initialize, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Acl -LiteralPath $rootPath", initialize, StringComparison.Ordinal);
        Assert.DoesNotContain("Protect-LegacyServiceDataDirectory", anchor, StringComparison.Ordinal);
        Assert.Contains("Resolve-InstalledServiceAccountSidValue", legacy, StringComparison.Ordinal);
        Assert.Contains("Assert-LegacyServiceDataContents", legacy, StringComparison.Ordinal);
        Assert.Contains("Rights = [Security.AccessControl.FileSystemRights]'ReadAndExecute, Synchronize'", protectLegacy, StringComparison.Ordinal);
        Assert.Contains("-AllowedWriteSidValues $legacyAllowedWriteSids", protectLegacy, StringComparison.Ordinal);
        Assert.Contains("-Description 'existing TechBench service data directory'", protectLegacy, StringComparison.Ordinal);
        Assert.True(
            protectLegacy.IndexOf("Assert-TrustedDirectoryAcl", StringComparison.Ordinal) <
            protectLegacy.IndexOf("Assert-LegacyServiceDataContents", StringComparison.Ordinal));
        Assert.Contains("-RequireServiceReadOnly", protectLegacy, StringComparison.Ordinal);
        Assert.Contains("normalized credential file ACL still permits inheritance", secretFile, StringComparison.Ordinal);
        var managerOutsiderCheck = secretFile[..secretFile.IndexOf(
            "if ($RequireServiceReadOnly)", StringComparison.Ordinal)];
        Assert.Contains(
            "$allowedSidValues -notcontains $_.IdentityReference.Value",
            managerOutsiderCheck,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$_.FileSystemRights -band",
            managerOutsiderCheck,
            StringComparison.Ordinal);
        Assert.True(lifetimeLock >= 0);
        Assert.True(serializedNormalization > lifetimeLock);
        Assert.True(launchRepair > serializedNormalization);
        Assert.True(
            repair.IndexOf("Initialize-AdminManagerDataRoot", StringComparison.Ordinal) <
            repair.IndexOf("pending-update.json", StringComparison.Ordinal));
        Assert.Contains("-AllowLeaf", repair, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallerChecksTrustedAncestorsAndTreesBeforeRemoval()
    {
        var source = ReadScript("Uninstall-TechBenchSyncService.ps1");
        var removeTree = SliceFunction(source, "Remove-SafeDirectoryTree");

        Assert.Contains("[string]$TrustedRoot", removeTree, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparsePointInPath -Path $target -TrustedRoot $TrustedRoot", removeTree, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparsePointsInDirectoryTree -Path $target", removeTree, StringComparison.Ordinal);
        Assert.Contains("-Path $filePath -TrustedRoot $TrustedRoot -AllowLeaf", removeTree, StringComparison.Ordinal);
        Assert.Contains("-Path $directoryPath -TrustedRoot $TrustedRoot", removeTree, StringComparison.Ordinal);
        Assert.Contains("-Path $dataPath -TrustedRoot $programDataRootPath", source, StringComparison.Ordinal);
        Assert.Contains("-Path $managerDataPath -TrustedRoot $programDataRootPath", source, StringComparison.Ordinal);
        Assert.Contains("-Path $installPath -TrustedRoot $programFilesRootPath", source, StringComparison.Ordinal);
        Assert.Contains("-Path $managerPath -TrustedRoot $programFilesRootPath", source, StringComparison.Ordinal);
        Assert.Contains("-TrustedRoot $programDataRootPath", source, StringComparison.Ordinal);
        Assert.Contains("-TrustedRoot $programFilesRootPath", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerAndManagerCloseProgramFilesAclBoundary()
    {
        var installer = ReadScript("Install-TechBenchSyncService.ps1");
        var installManager = SliceFunction(installer, "Install-ServerManagerShortcut");
        var installCompanion = SliceFunction(installer, "Set-ManagerCompanionFileAcl");
        var manager = ReadScript("TechBench-ServerManager.ps1");
        var deploymentPath = SliceFunction(manager, "Assert-ServiceDeploymentPath");
        var managerDirectoryAcl = SliceFunction(manager, "Set-ManagerInstallDirectoryAcl");
        var managerCompanionAcl = SliceFunction(manager, "Set-ManagerCompanionFileAcl");

        Assert.Contains("$programFilesRootPath", installer, StringComparison.Ordinal);
        Assert.Contains("-Path $installPath -TrustedRoot $programFilesRootPath", installer, StringComparison.Ordinal);
        Assert.Contains("-Path $managerPath -TrustedRoot $programFilesRootPath", installer, StringComparison.Ordinal);
        Assert.Contains("$managerSecurity.SetOwner($administrators)", installManager, StringComparison.Ordinal);
        Assert.Contains("Assert-TrustedDirectoryAcl", installManager, StringComparison.Ordinal);
        Assert.Contains("Set-ManagerCompanionFileAcl -Path $destinationFile", installManager, StringComparison.Ordinal);
        Assert.Contains("[Security.AccessControl.FileSecurity]::new()", installCompanion, StringComparison.Ordinal);
        Assert.Contains("$security.SetOwner($administrators)", installCompanion, StringComparison.Ordinal);
        Assert.Contains("Assert-RegularManagerCompanionFile", installCompanion, StringComparison.Ordinal);
        Assert.Contains("Assert-TrustedDirectoryAcl", installCompanion, StringComparison.Ordinal);

        Assert.Contains("Assert-NoReparsePointInPath", deploymentPath, StringComparison.Ordinal);
        Assert.Contains("$security.SetOwner($administrators)", managerDirectoryAcl, StringComparison.Ordinal);
        Assert.Contains("Assert-TrustedDirectoryAcl", managerDirectoryAcl, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparsePointsInDirectoryTree", managerDirectoryAcl, StringComparison.Ordinal);
        Assert.Contains("[Security.AccessControl.FileSecurity]::new()", managerCompanionAcl, StringComparison.Ordinal);
        Assert.Contains("$security.SetOwner($administrators)", managerCompanionAcl, StringComparison.Ordinal);
        Assert.Contains("Set-ManagerCompanionFileAcl -Path $managerFile.Stage", manager, StringComparison.Ordinal);
        Assert.Contains("Set-ManagerCompanionFileAcl -Path $managerFile.Target", manager, StringComparison.Ordinal);
        Assert.Contains("Set-ManagerCompanionFileAcl -Path $targetPath", manager, StringComparison.Ordinal);
        Assert.Contains("Set-ManagerCompanionFileAcl -Path $TargetPath", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void LauncherChecksManagerDirectoryAndScriptAclBeforeElevation()
    {
        var source = ReadScript("Start-TechBenchServerManager.ps1");
        var aclCheck = SliceFunction(source, "Test-DirectoryHasProtectedAdminAcl");
        var scriptCheck = SliceFunction(source, "Test-TrustedManagerScriptFile");
        var elevation = source.IndexOf("Start-CorrectProcess -PowerShellPath", StringComparison.Ordinal);
        var directoryCheck = source.IndexOf(
            "if (-not (Test-DirectoryHasProtectedAdminAcl",
            StringComparison.Ordinal);
        var fileCheck = source.IndexOf(
            "if (-not (Test-TrustedManagerScriptFile",
            StringComparison.Ordinal);

        Assert.Contains("[Security.AccessControl.FileSystemRights]::WriteData", aclCheck, StringComparison.Ordinal);
        Assert.DoesNotContain("[Security.AccessControl.FileSystemRights]::FullControl -bor", aclCheck, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparsePointInPath", scriptCheck, StringComparison.Ordinal);
        Assert.Contains("$item.PSObject.Properties['LinkType']", scriptCheck, StringComparison.Ordinal);
        Assert.Contains("Test-DirectoryHasProtectedAdminAcl -Path $Path", scriptCheck, StringComparison.Ordinal);
        Assert.True(directoryCheck >= 0);
        Assert.True(fileCheck > directoryCheck);
        Assert.True(elevation > fileCheck);
    }

    [Fact]
    public void LauncherNeverCreatesProgramDataAndChecksEveryLogFallback()
    {
        var source = ReadScript("Start-TechBenchServerManager.ps1");
        var writer = SliceFunction(source, "Write-StartupFailure");
        var trustedLog = SliceFunction(source, "Test-TrustedManagerLogDirectory");

        Assert.Contains("Test-DirectoryHasProtectedAdminAcl -Path $anchorPath", trustedLog, StringComparison.Ordinal);
        Assert.Contains("Test-DirectoryHasProtectedAdminAcl -Path $Path", trustedLog, StringComparison.Ordinal);
        Assert.Contains("Create = $false", writer, StringComparison.Ordinal);
        Assert.Contains("Create = $true", writer, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparsePointInPath", writer, StringComparison.Ordinal);
        Assert.Contains("-AllowLeaf", writer, StringComparison.Ordinal);
        Assert.Contains("if ([bool]$candidate.Create)", writer, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "New-Item -ItemType Directory -Path $script:ManagerDataDirectory",
            writer,
            StringComparison.Ordinal);
    }

    private static string SliceFunction(string source, string functionName)
    {
        var start = source.IndexOf($"function {functionName}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find function {functionName}.");
        var next = source.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
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
