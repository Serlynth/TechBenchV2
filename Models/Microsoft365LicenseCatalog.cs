namespace TechBench.Models;

public static class Microsoft365LicenseCatalog
{
    public const string BusinessBasic = "Microsoft 365 Business Basic";
    public const string BusinessStandard = "Microsoft 365 Business Standard";
    public const string BusinessPremium = "Microsoft 365 Business Premium";
    public const string ExchangeOnlinePlan1 = "Exchange Online Plan 1";
    public const string ExchangeOnlinePlan2 = "Exchange Online Plan 2";

    public static readonly string[] All =
    [
        BusinessBasic,
        BusinessStandard,
        BusinessPremium,
        ExchangeOnlinePlan1,
        ExchangeOnlinePlan2
    ];

    public static string Normalize(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.ToUpperInvariant() switch
        {
            "BUSINESS BASIC" => BusinessBasic,
            "BUSINESS STANDARD" => BusinessStandard,
            "BUSINESS PREMIUM" => BusinessPremium,
            "EXCHANGE ONLINE P1" or "EXCHANGE ONLINE PLAN 1" =>
                ExchangeOnlinePlan1,
            "EXCHANGE ONLINE P2" or "EXCHANGE ONLINE PLAN 2" =>
                ExchangeOnlinePlan2,
            _ => All.FirstOrDefault(item => string.Equals(
                    item,
                    trimmed,
                    StringComparison.OrdinalIgnoreCase))
                ?? string.Empty
        };
    }
}
