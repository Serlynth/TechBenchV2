using TechBench.Models;

namespace TechBench.Services;

public static class ModuleBranding
{
    public static BenchModule Resolve(string? value)
    {
        return Enum.TryParse<BenchModule>(value, ignoreCase: true, out var module)
            ? module
            : BenchModule.TechBench;
    }

    public static string LogoSource(BenchModule module) => module switch
    {
        BenchModule.SalesBench =>
            "/TechBenchV2;component/Assets/csri-salesbench-logo.png",
        BenchModule.AdminBench =>
            "/TechBenchV2;component/Assets/csri-adminbench-logo.png",
        _ => "/TechBenchV2;component/Assets/csri-techbench-logo.png"
    };
}
