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
        Assert.Equal("v2", V2AppUpdateService.CompiledReleaseChannel);
    }

    [Fact]
    public void ResolvesStableOrInventoryBetaAtRuntime()
    {
        Assert.Equal(
            V2AppUpdateService.InventoryBetaReleaseChannel,
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
    }

    [Fact]
    public void PublisherSupportsAnIsolatedInventoryBetaChannel()
    {
        var source = File.ReadAllText(
            RepositoryFile(@"scripts\Publish-TechBenchRelease.ps1"));

        Assert.Contains(
            "[ValidateSet('v2', 'inventory-beta')]",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "'TechBenchInventoryBetaSetup.exe'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"-p:TechBenchReleaseChannel=$releaseChannel\"",
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
            "public const string CompiledReleaseChannel = InventoryBetaReleaseChannel;",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "AllowVersionDowngrade = true",
            service,
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
