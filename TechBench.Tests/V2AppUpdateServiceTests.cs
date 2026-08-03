using TechBench.Services;

namespace TechBench.Tests;

public sealed class V2AppUpdateServiceTests
{
    [Fact]
    public void UsesIndependentV2ReleaseRepository()
    {
        Assert.Equal(
            "https://github.com/Serlynth/TechBenchV2-Releases",
            V2AppUpdateService.ReleaseRepositoryUrl);
        Assert.DoesNotContain(
            "/TechBench-Releases",
            V2AppUpdateService.ReleaseRepositoryUrl,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("v2", V2AppUpdateService.StableReleaseChannel);
        Assert.Equal(
            "inventory-beta",
            V2AppUpdateService.InventoryBetaReleaseChannel);
        Assert.Equal(
            "client-info-beta",
            V2AppUpdateService.ClientInfoBetaReleaseChannel);
        Assert.False(V2AppUpdateService.InventoryBetaAvailable);
        Assert.True(V2AppUpdateService.ClientInfoBetaAvailable);
        Assert.Equal("v2", V2AppUpdateService.CompiledReleaseChannel);
    }

    [Fact]
    public void RetiredInventoryBetaPreferencesResolveToStable()
    {
        Assert.Equal(
            V2AppUpdateService.StableReleaseChannel,
            V2AppUpdateService.ResolveReleaseChannel(
                V2AppUpdateService.InventoryBetaReleaseChannel));
        Assert.Equal(
            V2AppUpdateService.StableReleaseChannel,
            V2AppUpdateService.ResolveReleaseChannel(
                V2AppUpdateService.StableReleaseChannel));
        Assert.Equal(
            V2AppUpdateService.StableReleaseChannel,
            V2AppUpdateService.ResolveReleaseChannel(
                "invalid",
                V2AppUpdateService.StableReleaseChannel));
        Assert.Equal(
            V2AppUpdateService.StableReleaseChannel,
            V2AppUpdateService.ResolveReleaseChannel(
                null,
                V2AppUpdateService.InventoryBetaReleaseChannel));
        Assert.Equal(
            V2AppUpdateService.ClientInfoBetaReleaseChannel,
            V2AppUpdateService.ResolveReleaseChannel(
                V2AppUpdateService.ClientInfoBetaReleaseChannel));
        Assert.Equal(
            V2AppUpdateService.ClientInfoBetaReleaseChannel,
            V2AppUpdateService.ResolveReleaseChannel(
                null,
                V2AppUpdateService.ClientInfoBetaReleaseChannel));
    }

    [Fact]
    public void PublisherSupportsAnIsolatedInventoryBetaChannel()
    {
        var source = File.ReadAllText(
            RepositoryFile(@"scripts\Publish-TechBenchRelease.ps1"));

        Assert.Contains(
            "[ValidateSet('v2', 'inventory-beta', 'client-info-beta')]",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "'TechBenchInventoryBetaSetup.exe'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "'TechBenchClientInfoBetaSetup.exe'",
            source,
            StringComparison.Ordinal);
        Assert.Contains("$packId = 'CSRI.TechBenchV2'", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CSRI.TechBenchV2.ClientInfoBeta",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"-p:TechBenchReleaseChannel=$releaseChannel\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains("'download', 'github'", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$SkipTests", source, StringComparison.Ordinal);
        Assert.Contains("if (-not $SkipTests)", source, StringComparison.Ordinal);
        Assert.Contains(
            "function New-AnnotatedClientReleaseTag",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "git/matching-refs/tags/$Tag",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "git/ref/tags/$Tag",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "--raw-field \"tagger[date]=$tagDate\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "New-AnnotatedClientReleaseTag `",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-Tag \"v$Version\"",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if ($releaseChannel -eq 'client-info-beta')",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectCompilesInventoryBetaWithItsOwnUpdateChannel()
    {
        var project = File.ReadAllText(RepositoryFile("TechBench.csproj"));
        var service = File.ReadAllText(
            RepositoryFile(@"Services\V2AppUpdateService.cs"));

        Assert.Contains(
            "<TechBenchReleaseChannel Condition=\"'$(TechBenchReleaseChannel)' == ''\">v2</TechBenchReleaseChannel>",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "TECHBENCH_INVENTORY_BETA",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "TECHBENCH_CLIENT_INFO_BETA",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "public const string CompiledReleaseChannel = ClientInfoBetaReleaseChannel;",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "public const string CompiledReleaseChannel = InventoryBetaReleaseChannel;",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "AllowVersionDowngrade = true",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "public const bool InventoryBetaAvailable = false;",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "public const bool ClientInfoBetaAvailable = true;",
            service,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StableWorkspaceOffersTheClientInfoBetaChannel()
    {
        var mainWindow = File.ReadAllText(RepositoryFile("MainWindow.xaml"));
        var connectionWindow = File.ReadAllText(
            RepositoryFile("DatabaseConnectionWindow.xaml"));
        var clientInfoWorkspace = File.ReadAllText(
            RepositoryFile(@"ViewModels\MainWindowViewModel.ClientInfoBeta.cs"));

        Assert.Contains(
            "Use Client Info Beta update channel",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Check the Client Info Beta channel instead of Stable",
            connectionWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "#if TECHBENCH_CLIENT_INFO_BETA",
            clientInfoWorkspace,
            StringComparison.Ordinal);
        Assert.Contains(
            "public bool IsClientInfoBetaBuild => false;",
            clientInfoWorkspace,
            StringComparison.Ordinal);
    }

    private static string RepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "TechBenchV2.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, relativePath);
    }
}
