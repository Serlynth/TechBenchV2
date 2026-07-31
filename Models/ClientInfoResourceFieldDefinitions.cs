using System.Text;

namespace TechBench.Models;

public sealed record ClientInfoResourceFieldDefinition(
    string FieldKey,
    string FieldLabel,
    string ValueType,
    int SortOrder,
    bool ShowInGrid = true);

public static class ClientInfoResourceFieldDefinitions
{
    private static readonly IReadOnlyDictionary<string, ClientInfoResourceFieldDefinition[]>
        Definitions = new Dictionary<string, ClientInfoResourceFieldDefinition[]>(
            StringComparer.OrdinalIgnoreCase)
        {
            [ClientInfoResourceCategories.ServersInfrastructure] =
            [
                new("primary_ip", "Primary IP", "IpAddress", 10),
                new("management_ip", "Management IP", "IpAddress", 20),
                new(
                    "additional_ips_subnet",
                    "Additional IPs / Subnet",
                    "Text",
                    30,
                    ShowInGrid: false)
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

    public static IReadOnlyList<ClientInfoResourceFieldDefinition> ForCategory(
        string? category) =>
        category is not null && Definitions.TryGetValue(category, out var fields)
            ? fields
            : [];

    public static string AddressLabelForCategory(string? category) =>
        category switch
        {
            ClientInfoResourceCategories.ServersInfrastructure => "Hostname / URL",
            ClientInfoResourceCategories.ConnectionInternet => "Hostname / URL",
            ClientInfoResourceCategories.Wifi => "Controller / URL",
            ClientInfoResourceCategories.ApplicationsCloud => "URL",
            _ => "Address / URL"
        };

    public static bool IsStandardField(string? category, string? fieldKey) =>
        ForCategory(category).Any(field => string.Equals(
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
