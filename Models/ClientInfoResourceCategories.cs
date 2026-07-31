namespace TechBench.Models;

public static class ClientInfoResourceCategories
{
    public const string ServersInfrastructure = "Servers & Infrastructure";
    public const string NetworkInternet = "Network & Internet";
    public const string ApplicationsCloud = "Applications & Cloud";
    public const string DomainsEmail = "Domains & Email";
    public const string BackupSecurity = "Backup & Security";
    public const string VendorsServices = "Vendors & Services";
    public const string NeedsSorting = "Needs Sorting";

    public static readonly string[] All =
    [
        ServersInfrastructure,
        NetworkInternet,
        ApplicationsCloud,
        DomainsEmail,
        BackupSecurity,
        VendorsServices,
        NeedsSorting
    ];

    public static string Encode(string category, string type)
    {
        var normalizedCategory = NormalizeCategory(category);
        var normalizedType = GetTypeLabel(type).Trim();
        if (normalizedCategory == NeedsSorting)
        {
            return string.IsNullOrWhiteSpace(normalizedType)
                ? "Other"
                : normalizedType;
        }

        return string.IsNullOrWhiteSpace(normalizedType)
            || string.Equals(
                normalizedType,
                normalizedCategory,
                StringComparison.OrdinalIgnoreCase)
            ? normalizedCategory
            : $"{normalizedCategory} / {normalizedType}";
    }

    public static string Classify(string? resourceType)
    {
        var value = resourceType?.Trim() ?? string.Empty;
        foreach (var category in All.Where(
                     category => category != NeedsSorting))
        {
            if (string.Equals(value, category, StringComparison.OrdinalIgnoreCase)
                || value.StartsWith(
                    $"{category} / ",
                    StringComparison.OrdinalIgnoreCase))
            {
                return category;
            }
        }

        var lower = value.ToLowerInvariant();
        if (ContainsAny(
                lower,
                "antivirus", "anti-virus", "edr", "endpoint", "security",
                "backup", "mfa", "multi-factor", "spam", "filter",
                "defender", "sentinel", "crowdstrike", "webroot",
                "sophos", "malwarebytes"))
        {
            return BackupSecurity;
        }

        if (ContainsAny(
                lower,
                "domain", "dns", "registrar", "email", "exchange",
                "mailbox", "mail tenant"))
        {
            return DomainsEmail;
        }

        if (ContainsAny(
                lower,
                "firewall", "router", "switch", "wireless", "wi-fi",
                "wifi", "access point", "internet", "isp", "circuit",
                "vlan", "vpn", "public ip", "modem", "gateway",
                "network"))
        {
            return NetworkInternet;
        }

        if (ContainsAny(
                lower,
                "server", "virtual machine", "vm", "hypervisor", "vmware",
                "hyper-v", "storage", "nas", "san", "active directory",
                "directory service", "infrastructure"))
        {
            return ServersInfrastructure;
        }

        if (ContainsAny(
                lower,
                "vendor", "contract", "provider", "copier", "phone",
                "voip", "telecom", "support agreement", "managed service"))
        {
            return VendorsServices;
        }

        if (ContainsAny(
                lower,
                "application", "app", "saas", "cloud", "software",
                "license", "licensing", "microsoft 365", "office 365",
                "m365", "azure", "aws", "quickbooks", "sage"))
        {
            return ApplicationsCloud;
        }

        return NeedsSorting;
    }

    public static string GetTypeLabel(string? resourceType)
    {
        var value = resourceType?.Trim() ?? string.Empty;
        foreach (var category in All.Where(
                     category => category != NeedsSorting))
        {
            var prefix = $"{category} / ";
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return value[prefix.Length..].Trim();
            }
        }

        return value;
    }

    public static string NormalizeCategory(string? category) =>
        All.FirstOrDefault(item => string.Equals(
            item,
            category?.Trim(),
            StringComparison.OrdinalIgnoreCase))
        ?? NeedsSorting;

    private static bool ContainsAny(
        string value,
        params string[] candidates) =>
        candidates.Any(value.Contains);
}
