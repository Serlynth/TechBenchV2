using System.Text;

namespace TechBench.Models;

public sealed record ClientInfoResourceFieldDefinition(
    string FieldKey,
    string FieldLabel,
    string ValueType,
    int SortOrder,
    bool ShowInGrid = true,
    bool ShowInCompact = true);

public static class ClientInfoResourceFieldDefinitions
{
    private static readonly IReadOnlyDictionary<string, ClientInfoResourceFieldDefinition[]>
        EditorDefinitions = new Dictionary<string, ClientInfoResourceFieldDefinition[]>(
            StringComparer.OrdinalIgnoreCase)
        {
            [ClientInfoResourceCategories.ServersInfrastructure] =
            [
                new("primary_ip", "Primary IP", "IpAddress", 10),
                new("management_ip", "Management IP", "IpAddress", 20),
                new("role_purpose", "Role / Purpose", "Text", 30),
                new("operating_system", "Operating System", "Text", 40),
                new("manufacturer_model", "Manufacturer / Model", "Text", 50, ShowInGrid: false),
                new("serial_number", "Serial Number", "Text", 60, ShowInGrid: false),
                new(
                    "additional_ips_subnet",
                    "Additional IPs / Subnet",
                    "Text",
                    70,
                    ShowInGrid: false,
                    ShowInCompact: false)
            ],
            [ClientInfoResourceCategories.ConnectionInternet] =
            [
                new("public_wan_ip", "Public / WAN IP", "IpAddress", 10),
                new("ssl_vpn_port", "SSL VPN Port", "Text", 20),
                new("gateway", "Gateway", "IpAddress", 30),
                new("subnet_cidr", "Subnet / CIDR", "Text", 40),
                new("circuit_id", "Circuit ID", "Text", 50, ShowInGrid: false),
                new("device_model", "Device Model", "Text", 60, ShowInGrid: false),
                new("serial_number", "Serial Number", "Text", 70, ShowInGrid: false),
                new("firmware_version", "Firmware Version", "Text", 80, ShowInGrid: false),
                new("isp_provider", "ISP / Provider", "Text", 90, ShowInGrid: false),
                new("support_phone", "Support Phone", "Phone", 100, ShowInGrid: false)
            ],
            [ClientInfoResourceCategories.Wifi] =
            [
                new("management_ip", "Management IP", "IpAddress", 10),
                new("ssid", "SSID", "Text", 20),
                new("vlan", "VLAN", "Text", 30),
                new("wireless_security", "Security", "Text", 40, ShowInGrid: false),
                new("controller_name", "Controller", "Text", 50, ShowInGrid: false),
                new("guest_ssid", "Guest SSID", "Text", 60, ShowInGrid: false),
                new("coverage_notes", "Coverage / Location Notes", "Text", 70, ShowInGrid: false, ShowInCompact: false)
            ],
            [ClientInfoResourceCategories.ApplicationsCloud] =
            [
                new("tenant_instance", "Tenant / Instance", "Text", 10),
                new("hosting_type", "Hosting Type", "Text", 20),
                new("primary_ip", "Primary IP", "IpAddress", 30),
                new("admin_portal", "Admin Portal", "Url", 40, ShowInGrid: false),
                new("version", "Version", "Text", 50, ShowInGrid: false),
                new("support_contact", "Support Contact", "Text", 60, ShowInGrid: false),
                new("renewal_date", "Renewal Date", "Date", 70, ShowInGrid: false)
            ],
            [ClientInfoResourceCategories.DomainsEmail] =
            [
                new("domain_name", "Domain Name", "Text", 10),
                new("registrar", "Registrar", "Text", 20),
                new("dns_provider", "DNS Provider", "Text", 30),
                new("mail_provider", "Mail Provider", "Text", 40, ShowInGrid: false),
                new("tenant_name", "Tenant Name", "Text", 50, ShowInGrid: false),
                new("expiration_date", "Expiration Date", "Date", 60, ShowInGrid: false)
            ],
            [ClientInfoResourceCategories.BackupSecurity] =
            [
                new("product_service", "Product / Service", "Text", 10),
                new("protected_scope", "Protected Scope", "Text", 20),
                new("console_url", "Console URL", "Url", 30),
                new("retention", "Retention", "Text", 40, ShowInGrid: false),
                new("backup_schedule", "Backup Schedule", "Text", 50, ShowInGrid: false),
                new("last_restore_test", "Last Restore Test", "Date", 60, ShowInGrid: false),
                new("renewal_date", "Renewal Date", "Date", 70, ShowInGrid: false)
            ],
            [ClientInfoResourceCategories.VendorsServices] =
            [
                new("account_number", "Account Number", "Text", 10),
                new("primary_contact", "Primary Contact", "Text", 20),
                new("support_phone", "Support Phone", "Phone", 30),
                new("support_email", "Support Email", "Email", 40, ShowInGrid: false),
                new("portal_url", "Portal URL", "Url", 50, ShowInGrid: false),
                new("contract_expiration", "Contract Expiration", "Date", 60, ShowInGrid: false)
            ]
        };

    // These definitions are the stable workbook/table contract. Keep their order
    // unchanged so existing migration workbooks continue to round-trip correctly.
    private static readonly IReadOnlyDictionary<string, ClientInfoResourceFieldDefinition[]>
        Definitions = new Dictionary<string, ClientInfoResourceFieldDefinition[]>(
            StringComparer.OrdinalIgnoreCase)
        {
            [ClientInfoResourceCategories.ServersInfrastructure] =
            [
                new("primary_ip", "Primary IP", "IpAddress", 10),
                new("management_ip", "Management IP", "IpAddress", 20),
                new("additional_ips_subnet", "Additional IPs / Subnet", "Text", 30, ShowInGrid: false)
            ],
            [ClientInfoResourceCategories.ConnectionInternet] =
            [
                new("public_wan_ip", "Public / WAN IP", "IpAddress", 10),
                new("gateway", "Gateway", "IpAddress", 20),
                new("subnet_cidr", "Subnet / CIDR", "Text", 30),
                new("circuit_id", "Circuit ID", "Text", 40, ShowInGrid: false)
            ],
            [ClientInfoResourceCategories.Wifi] =
            [
                new("management_ip", "Management IP", "IpAddress", 10),
                new("ssid", "SSID", "Text", 20),
                new("vlan", "VLAN", "Text", 30),
                new("wireless_security", "Security", "Text", 40, ShowInGrid: false)
            ],
            [ClientInfoResourceCategories.ApplicationsCloud] =
            [
                new("tenant_instance", "Tenant / Instance", "Text", 10),
                new("hosting_type", "Hosting Type", "Text", 20),
                new("primary_ip", "Primary IP", "IpAddress", 30)
            ]
        };

    private static readonly IReadOnlyDictionary<string, string[]> TypeOptions =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [ClientInfoResourceCategories.ServersInfrastructure] =
                ["Physical Server", "Virtual Server", "Hyper-V Host", "VMware Host", "NAS / Storage", "Switch", "UPS", "Network Appliance", "Other"],
            [ClientInfoResourceCategories.ConnectionInternet] =
                ["WatchGuard Firewall", "Firewall", "Router / Gateway", "Internet Circuit", "VPN", "Public IP", "Modem", "Other"],
            [ClientInfoResourceCategories.Wifi] =
                ["Wireless Network / SSID", "Access Point", "Wireless Controller", "Guest Network", "Other"],
            [ClientInfoResourceCategories.ApplicationsCloud] =
                ["Microsoft 365 Tenant", "Line-of-Business Application", "Cloud Service", "Hosted Application", "License", "Other"],
            [ClientInfoResourceCategories.DomainsEmail] =
                ["Domain", "DNS Hosting", "Email Tenant", "Registrar", "SSL Certificate", "Other"],
            [ClientInfoResourceCategories.BackupSecurity] =
                ["Veeam Backup", "Backup Appliance", "Cloud Backup", "Antivirus / EDR", "MFA", "Spam Filtering", "Security Service", "Other"],
            [ClientInfoResourceCategories.VendorsServices] =
                ["Internet Provider", "Phone / VoIP Provider", "Copier Provider", "Software Vendor", "Support Contract", "Managed Service", "Other"],
            [ClientInfoResourceCategories.NeedsSorting] = ["Other", "Unknown"]
        };

    public static IReadOnlyList<ClientInfoResourceFieldDefinition> ForCategory(
        string? category) =>
        category is not null && Definitions.TryGetValue(category, out var fields)
            ? fields
            : [];

    public static IReadOnlyList<ClientInfoResourceFieldDefinition> ForEditorCategory(
        string? category) =>
        category is not null && EditorDefinitions.TryGetValue(category, out var fields)
            ? fields
            : [];

    public static IReadOnlyList<string> TypeOptionsForCategory(string? category) =>
        category is not null && TypeOptions.TryGetValue(category, out var options)
            ? options
            : ["Other"];

    public static string EditorDescriptionForCategory(string? category) =>
        $"This record belongs to {NormalizeLabel(category)}. Use Move if it belongs in a different section.";

    private static string NormalizeLabel(string? category) =>
        string.IsNullOrWhiteSpace(category) ? ClientInfoResourceCategories.NeedsSorting : category.Trim();

    public static string AddressLabelForCategory(string? category) =>
        category switch
        {
            ClientInfoResourceCategories.ServersInfrastructure => "Hostname / URL",
            ClientInfoResourceCategories.ConnectionInternet => "Hostname / URL",
            ClientInfoResourceCategories.Wifi => "Controller / URL",
            ClientInfoResourceCategories.ApplicationsCloud => "URL",
            ClientInfoResourceCategories.DomainsEmail => "Domain / URL",
            ClientInfoResourceCategories.BackupSecurity => "Console / Portal URL",
            ClientInfoResourceCategories.VendorsServices => "Website / Portal URL",
            _ => "Address / URL"
        };

    public static bool IsStandardField(string? category, string? fieldKey) =>
        ForEditorCategory(category).Any(field => string.Equals(
            field.FieldKey,
            fieldKey,
            StringComparison.OrdinalIgnoreCase));

    public static string CustomFieldKey(string label)
    {
        var builder = new StringBuilder("custom.");
        var previousWasSeparator = false;
        foreach (var character in label.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && builder.Length > 7)
            {
                builder.Append('_');
                previousWasSeparator = true;
            }
        }

        var key = builder.ToString().TrimEnd('_');
        return key.Length > 7 ? key[..Math.Min(key.Length, 120)] : "custom.field";
    }
}
