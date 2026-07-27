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
}
