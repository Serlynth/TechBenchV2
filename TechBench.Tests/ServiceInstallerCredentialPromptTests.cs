namespace TechBench.Tests;

public sealed class ServiceInstallerCredentialPromptTests
{
    [Fact]
    public void InstallerProvidesRevealablePasswordDialogWithoutChangingCredentialContract()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "scripts",
            "Install-TechBenchSyncService.ps1"));

        Assert.Contains("[PSCredential]$Credential", source, StringComparison.Ordinal);
        Assert.Contains("Show-ServiceAccountCredentialDialog", source, StringComparison.Ordinal);
        Assert.Contains("Show password while I verify it", source, StringComparison.Ordinal);
        Assert.Contains("$passwordBox.UseSystemPasswordChar = $true", source, StringComparison.Ordinal);
        Assert.Contains(
            "$passwordBox.UseSystemPasswordChar = -not $showPasswordCheckBox.Checked",
            source,
            StringComparison.Ordinal);
        Assert.Contains("$passwordBox.ShortcutsEnabled = $false", source, StringComparison.Ordinal);
        Assert.Contains(
            "ConvertTo-SecureString -String $passwordBox.Text -AsPlainText -Force",
            source,
            StringComparison.Ordinal);
        Assert.Contains("$passwordBox.Clear()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerRetainsProtectedFallbackAndDoesNotAcceptAPlaintextPasswordParameter()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "scripts",
            "Install-TechBenchSyncService.ps1"));

        Assert.Contains("Get-Credential -UserName $AccountName", source, StringComparison.Ordinal);
        Assert.Contains("Supply a PSCredential with -Credential", source, StringComparison.Ordinal);
        Assert.Contains(
            "catch [OperationCanceledException] {\n        throw\n    } catch {",
            source.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.DoesNotContain("[string]$Password", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Write-Host $password", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Write-Output $password", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhatIfReachesShouldProcessBeforeInteractiveCredentialPrompt()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "scripts",
            "Install-TechBenchSyncService.ps1"));
        var shouldProcess = source.IndexOf(
            "if ($PSCmdlet.ShouldProcess($DisplayName",
            StringComparison.Ordinal);
        var prompt = source.LastIndexOf(
            "$Credential = Read-ServiceAccountCredential -AccountName $ServiceAccount",
            StringComparison.Ordinal);

        Assert.True(shouldProcess >= 0);
        Assert.True(prompt > shouldProcess);
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidateParts = new[] { directory.FullName }.Concat(relativeParts).ToArray();
            var candidate = Path.Combine(candidateParts);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the TechBenchV2 repository root.");
    }
}
