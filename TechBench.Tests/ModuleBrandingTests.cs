using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class ModuleBrandingTests
{
    [Theory]
    [InlineData("TechBench", BenchModule.TechBench)]
    [InlineData("salesbench", BenchModule.SalesBench)]
    [InlineData("AdminBench", BenchModule.AdminBench)]
    [InlineData("unexpected", BenchModule.TechBench)]
    [InlineData(null, BenchModule.TechBench)]
    public void Resolve_NormalizesSavedModule(
        string? value,
        BenchModule expected)
    {
        Assert.Equal(expected, ModuleBranding.Resolve(value));
    }

    [Theory]
    [InlineData(BenchModule.TechBench, "csri-techbench-logo.png")]
    [InlineData(BenchModule.SalesBench, "csri-salesbench-logo.png")]
    [InlineData(BenchModule.AdminBench, "csri-adminbench-logo.png")]
    public void LogoSource_UsesMatchingModuleAsset(
        BenchModule module,
        string expectedAsset)
    {
        Assert.EndsWith(
            expectedAsset,
            ModuleBranding.LogoSource(module),
            StringComparison.Ordinal);
    }
}
