namespace TechBench.Models;

public static class ClientInfoResourceCategories
{
    public const string ServersInfrastructure = "Servers & Infrastructure";
    public const string ConnectionInternet = "Connection & Internet";
    public const string Wifi = "Wi-Fi";
    public const string LegacyNetworkInternet = "Network & Internet";
    public const string ApplicationsCloud = "Applications & Cloud";
    public const string DomainsEmail = "Domains & Email";
    public const string Backup = "Backup";
    public const string Security = "Security";
    public const string LegacyBackupSecurity = "Backup & Security";
    public const string VendorsServices = "Vendors & Services";
    public const string NeedsSorting = "Needs Sorting";

    public static readonly string[] All =
    [
        ServersInfrastructure,
        ConnectionInternet,
        Wifi,
        ApplicationsCloud,
        DomainsEmail,
        Backup,
        Security,
        VendorsServices,
        NeedsSorting
    ];

    public static string Encode(string category, string type)
    {
        var normalizedType = GetTypeLabel(type).Trim();
        var normalizedCategory = string.Equals(
            category?.Trim(),
            LegacyBackupSecurity,
            StringComparison.OrdinalIgnoreCase)
            ? ClassifyProtection(normalizedType.ToLowerInvariant())
                ?? NeedsSorting
            : NormalizeCategory(category);
        if (ContainsAny(
                normalizedType.ToLowerInvariant(),
                "switch",
                "switching",
                "network appliance"))
        {
            normalizedCategory = ServersInfrastructure;
        }
        else if (IsWifi(normalizedType.ToLowerInvariant()))
        {
            normalizedCategory = Wifi;
        }
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
        var lower = value.ToLowerInvariant();
        if (ContainsAny(
                lower,
                "switch",
                "switching",
                "network appliance"))
        {
            return ServersInfrastructure;
        }

        if (IsWifi(lower))
        {
            return Wifi;
        }

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

        var legacyNetworkPrefix = $"{LegacyNetworkInternet} / ";
        if (value.StartsWith(
                legacyNetworkPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionInternet;
        }

        if (string.Equals(
                value,
                LegacyNetworkInternet,
                StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionInternet;
        }

        var legacyProtectionPrefix = $"{LegacyBackupSecurity} / ";
        if (value.StartsWith(
                legacyProtectionPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return ClassifyProtection(
                    value[legacyProtectionPrefix.Length..]
                        .Trim()
                        .ToLowerInvariant())
                ?? NeedsSorting;
        }

        if (string.Equals(
                value,
                LegacyBackupSecurity,
                StringComparison.OrdinalIgnoreCase))
        {
            return NeedsSorting;
        }

        if (ClassifyProtection(lower) is { } protectionCategory)
        {
            return protectionCategory;
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
                "firewall", "router", "internet", "isp", "circuit",
                "vlan", "vpn", "public ip", "modem", "gateway",
                "network"))
        {
            return ConnectionInternet;
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

        var legacyNetworkPrefix = $"{LegacyNetworkInternet} / ";
        if (value.StartsWith(
                legacyNetworkPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return value[legacyNetworkPrefix.Length..].Trim();
        }

        var legacyProtectionPrefix = $"{LegacyBackupSecurity} / ";
        if (value.StartsWith(
                legacyProtectionPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return value[legacyProtectionPrefix.Length..].Trim();
        }

        return value;
    }

    public static string NormalizeCategory(string? category) =>
        All.FirstOrDefault(item => string.Equals(
            item,
            category?.Trim(),
            StringComparison.OrdinalIgnoreCase))
        ?? NeedsSorting;

    public static string ClassifyCredential(ClientInfoCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var searchable = $"{credential.Category} {credential.Name}";
        var lower = searchable.ToLowerInvariant();
        var protectionLower = searchable.Replace(
                LegacyBackupSecurity,
                string.Empty,
                StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();
        if (ContainsAny(lower, "rustdesk", "screenconnect", "connectwise", "splashtop", "teamviewer", "remote access"))
        {
            return ApplicationsCloud;
        }

        if (ContainsAny(lower, "watchguard", "firebox", "firewall", "vpn"))
        {
            return ConnectionInternet;
        }

        if (ClassifyProtection(protectionLower) is { } protectionCategory)
        {
            return protectionCategory;
        }

        if (ContainsAny(lower, "active directory", "domain admin", "domain", "dns", "registrar"))
        {
            return DomainsEmail;
        }

        if (ContainsAny(lower, "ilo", "idrac", "ups", "server", "hyper-v", "vmware", "switch"))
        {
            return ServersInfrastructure;
        }

        return Classify(protectionLower);
    }

    private static bool ContainsAny(
        string value,
        params string[] candidates) =>
        candidates.Any(value.Contains);

    private static string? ClassifyProtection(string value)
    {
        if (ContainsAny(
                value,
                "backup", "restore", "replication", "disaster recovery",
                "bdr", "veeam", "acronis", "backblaze", "carbonite",
                "cove backup", "datto", "nakivo", "rubrik", "cohesity"))
        {
            return Backup;
        }

        if (ContainsAny(
                value,
                "antivirus", "anti-virus", "edr", "endpoint", "security",
                "mfa", "multi-factor", "spam", "filter", "defender",
                "sentinel", "crowdstrike", "webroot", "sophos",
                "malwarebytes", "eset", "barracuda", "siem", "soc",
                "phishing", "authpoint"))
        {
            return Security;
        }

        return null;
    }

    private static bool IsWifi(string value) =>
        ContainsAny(
            value,
            "wi-fi",
            "wifi",
            "wireless",
            "access point",
            "wlan",
            "ssid");
}
