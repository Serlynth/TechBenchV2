using System.Text;

namespace TechBench.Models;

/// <summary>
/// Builds a bounded category dashboard from canonical Client Information
/// records. FireDrill-style labels are recognized as aliases, but no FireDrill
/// repository or workbook data is read here.
/// </summary>
public static class ClientInfoCategoryOverviewBuilder
{
    private const int MaximumValuesPerField = 3;

    public static IReadOnlyList<ClientInfoCategoryOverviewSection> Build(
        string category,
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials)
    {
        var sections = category switch
        {
            ClientInfoResourceCategories.ServersInfrastructure =>
                BuildServers(resources, credentials),
            ClientInfoResourceCategories.ConnectionInternet =>
                BuildConnection(resources, credentials),
            ClientInfoResourceCategories.Wifi =>
                BuildWifi(resources, credentials),
            ClientInfoResourceCategories.ApplicationsCloud =>
                BuildApplications(resources, credentials),
            ClientInfoResourceCategories.DomainsEmail =>
                BuildDomains(resources, credentials),
            ClientInfoResourceCategories.BackupSecurity =>
                BuildProtection(resources, credentials),
            ClientInfoResourceCategories.VendorsServices =>
                BuildVendors(resources, credentials),
            _ => BuildNeedsSorting(resources)
        };

        return sections.Where(section => section.Fields.Count > 0).ToArray();
    }

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildServers(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials) =>
    [
        Section("Core infrastructure", "The systems and addressing most useful during support.",
            Names("Key systems", resources),
            Values("Primary IP", resources, "primary_ip", "server ip", "host ip"),
            Values("Role / purpose", resources, "role_purpose", "server role", "purpose"),
            Values("Operating system", resources, "operating_system", "server os", "os version")),
        Section("Management & power", "Out-of-band management, hardware, and power details.",
            Values("Management IP", resources, "management_ip", "ilo ip", "idrac ip", "ups ip"),
            Addresses("Management URL", resources, "ilo", "idrac", "ups", "management"),
            Values("Manufacturer / model", resources, "manufacturer_model", "server model", "ilo host", "ups model"),
            Values("Network / rack", resources, "additional_ips_subnet", "subnet", "rack", "runtime")),
        AccessSection(credentials)
    ];

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildConnection(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials) =>
    [
        Section("Firewall & internet", "Primary edge device, provider, and support details.",
            Names("Primary systems", resources),
            Providers("Provider", resources),
            Addresses("Management URL", resources),
            Values("Device model", resources, "device_model", "firewall model", "router model"),
            Values("Firmware", resources, "firmware_version", "fireware", "firmware"),
            Values("ISP", resources, "isp_provider", "internet provider", "isp")),
        Section("WAN addressing", "The circuit and public addressing needed for troubleshooting.",
            Values("Public / WAN IP", resources, "public_wan_ip", "wan ip", "external ip", "public ip"),
            Values("Gateway", resources, "gateway", "default gateway"),
            Values("Subnet / CIDR", resources, "subnet_cidr", "subnet", "cidr"),
            Values("Circuit ID", resources, "circuit_id", "circuit", "account number"),
            Values("Support phone", resources, "support_phone", "isp phone")),
        AccessSection(credentials)
    ];

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildWifi(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials) =>
    [
        Section("Wireless overview", "Controller and network names technicians need at a glance.",
            Names("Wireless systems", resources),
            Values("Controller", resources, "controller_name", "wireless controller", "controller"),
            Addresses("Controller / URL", resources),
            Values("Management IP", resources, "management_ip", "controller ip", "wireless ip"),
            Values("Staff SSID", resources, "ssid", "staff ssid", "corporate ssid", "wifi name"),
            Values("Guest SSID", resources, "guest_ssid", "guest wifi", "guest network"),
            Values("Security", resources, "wireless_security", "wifi security", "encryption"),
            Values("VLAN", resources, "vlan", "wifi vlan")),
        AccessSection(credentials)
    ];

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildApplications(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials) =>
    [
        Section("Critical applications & cloud", "Tenant, portal, support, and renewal information.",
            Names("Services", resources),
            Values("Tenant / instance", resources, "tenant_instance", "tenant id", "tenant name", "company id"),
            Values("Hosting", resources, "hosting_type", "hosted", "hosting"),
            Values("Admin portal", resources, "admin_portal", "portal url", "console url"),
            Values("Version / plan", resources, "version", "plan", "subscription"),
            Values("Support", resources, "support_contact", "support phone", "support email"),
            Values("Renewal", resources, "renewal_date", "renewal", "expiration")),
        AccessSection(credentials)
    ];

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildDomains(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials) =>
    [
        Section("Domains & email", "Core domain, DNS, mail, and tenant information.",
            Values("Domain", resources, "domain_name", "ad domain", "domain"),
            Values("Registrar", resources, "registrar", "domain registrar"),
            Values("DNS provider", resources, "dns_provider", "name servers", "dns"),
            Values("Mail provider", resources, "mail_provider", "email provider", "mail host"),
            Values("Tenant", resources, "tenant_name", "tenant id", "onmicrosoft"),
            Values("Expiration", resources, "expiration_date", "domain expiration", "expires"),
            Addresses("Domain / admin URL", resources)),
        AccessSection(credentials)
    ];

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildProtection(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials) =>
    [
        Section("Backup & security", "Protection products, scope, schedules, and recovery readiness.",
            Values("Products / services", resources, "product_service", "backup product", "antivirus", "security product"),
            Values("Protected scope", resources, "protected_scope", "protected devices", "backup scope"),
            Values("Console", resources, "console_url", "portal url", "management url"),
            Values("Schedule", resources, "backup_schedule", "backup time", "schedule"),
            Values("Retention", resources, "retention", "retention period"),
            Values("Last restore test", resources, "last_restore_test", "restore test", "last test"),
            Values("Renewal", resources, "renewal_date", "renewal", "expiration")),
        AccessSection(credentials)
    ];

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildVendors(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials) =>
    [
        Section("Support & service", "The account and contact details needed to reach a provider.",
            Names("Services", resources),
            Providers("Vendor", resources),
            Values("Account number", resources, "account_number", "customer number", "account id"),
            Values("Primary contact", resources, "primary_contact", "account manager", "contact"),
            Values("Support phone", resources, "support_phone", "phone"),
            Values("Support email", resources, "support_email", "email"),
            Values("Portal", resources, "portal_url", "support portal", "website"),
            Values("Contract expiration", resources, "contract_expiration", "renewal", "expiration")),
        AccessSection(credentials)
    ];

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildNeedsSorting(
        IReadOnlyList<ClientInfoResource> resources)
    {
        if (resources.Count == 0)
        {
            return [];
        }

        var activeCount = resources.Count(resource => resource.IsActive);
        return
        [
            Section("Sorting queue", "A short queue summary; use the full list below to classify each record.",
                Field("Records waiting", resources.Count.ToString()),
                Names("Examples", resources),
                Aggregate("Types", resources.Select(resource => resource.TypeLabel)),
                Aggregate("Providers", resources.Select(resource => resource.Provider)),
                Field("Active records", activeCount.ToString()))
        ];
    }

    private static ClientInfoCategoryOverviewSection AccessSection(
        IReadOnlyList<ClientInfoCredential> credentials)
    {
        var secretCount = credentials.Sum(credential => credential.SecretCount);
        return Section("Protected access", "References only; secret values stay in the audited Passwords workflow.",
            Aggregate("Access records", credentials.Select(credential => credential.Name)),
            Aggregate("Usernames", credentials.Select(credential => credential.Username), credentials.Select(credential => credential.Name)),
            Aggregate("Login URLs", credentials.Select(credential => credential.LoginUrl), credentials.Select(credential => credential.Name)),
            Field("Protected values", secretCount == 1
                ? "1 stored secret"
                : secretCount > 1 ? $"{secretCount} stored secrets" : string.Empty));
    }

    private static ClientInfoCategoryOverviewSection Section(
        string title,
        string description,
        params ClientInfoOverviewField?[] fields) =>
        new(title, description, fields.Where(field => field is not null).Cast<ClientInfoOverviewField>().ToArray());

    private static ClientInfoOverviewField? Names(
        string label,
        IReadOnlyList<ClientInfoResource> resources) =>
        Aggregate(label, resources.Select(resource => resource.Name));

    private static ClientInfoOverviewField? Providers(
        string label,
        IReadOnlyList<ClientInfoResource> resources) =>
        Aggregate(label, resources.Select(resource => resource.Provider), resources.Select(resource => resource.Name));

    private static ClientInfoOverviewField? Addresses(
        string label,
        IReadOnlyList<ClientInfoResource> resources,
        params string[] preferredTerms)
    {
        IReadOnlyList<ClientInfoResource> selected = preferredTerms.Length == 0
            ? resources
            : resources.Where(resource => preferredTerms.Any(term => ResourceContains(resource, term))).ToArray();
        if (selected.Count == 0)
        {
            selected = resources;
        }

        return Aggregate(label, selected.Select(resource => resource.AddressOrUrl), selected.Select(resource => resource.Name));
    }

    private static ClientInfoOverviewField? Values(
        string label,
        IReadOnlyList<ClientInfoResource> resources,
        params string[] aliases) =>
        Aggregate(label, resources.Select(resource => FindValue(resource, aliases)), resources.Select(resource => resource.Name));

    private static string FindValue(
        ClientInfoResource resource,
        IReadOnlyList<string> aliases)
    {
        var normalizedAliases = aliases.Select(Normalize).Where(value => value.Length > 0).ToArray();
        var exact = resource.Fields.FirstOrDefault(field => normalizedAliases.Any(alias =>
            Normalize(field.FieldKey) == alias || Normalize(field.FieldLabel) == alias));
        if (exact is not null)
        {
            return exact.ValueText;
        }

        return resource.Fields.FirstOrDefault(field => aliases.Any(alias =>
            MatchesAlias(field.FieldKey, alias)
            || MatchesAlias(field.FieldLabel, alias)))?.ValueText ?? string.Empty;
    }

    private static ClientInfoOverviewField? Aggregate(
        string label,
        IEnumerable<string?> values,
        IEnumerable<string?>? labelSources = null)
    {
        var valueArray = values.ToArray();
        var sourceArray = labelSources?.ToArray();
        var candidates = valueArray
            .Select((value, index) => new
            {
                Value = value?.Trim() ?? string.Empty,
                Source = sourceArray is not null && index < sourceArray.Length
                    ? sourceArray[index]?.Trim() ?? string.Empty
                    : string.Empty
            })
            .Where(item => item.Value.Length > 0)
            .GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var includeSource = candidates.Length > 1 && candidates.Any(item => item.Source.Length > 0);
        var lines = candidates.Take(MaximumValuesPerField)
            .Select(item => includeSource && item.Source.Length > 0
                ? $"{item.Source}: {item.Value}"
                : item.Value)
            .ToList();
        if (candidates.Length > MaximumValuesPerField)
        {
            lines.Add($"+{candidates.Length - MaximumValuesPerField} more in full list");
        }

        return new ClientInfoOverviewField(label, string.Join(Environment.NewLine, lines));
    }

    private static ClientInfoOverviewField? Field(string label, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new(label, value.Trim());

    private static bool ResourceContains(ClientInfoResource resource, string term) =>
        Normalize($"{resource.Name} {resource.TypeLabel} {resource.Provider}")
            .Contains(Normalize(term), StringComparison.Ordinal);

    private static bool MatchesAlias(string? fieldName, string? alias)
    {
        var normalizedField = Normalize(fieldName);
        var normalizedAlias = Normalize(alias);
        if (normalizedField.Contains(normalizedAlias, StringComparison.Ordinal))
        {
            return true;
        }

        var tokens = (alias ?? string.Empty)
            .Split([' ', '_', '-', '/', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(token => token.Length > 1)
            .ToArray();
        return tokens.Length > 1
               && tokens.All(token => normalizedField.Contains(token, StringComparison.Ordinal));
    }

    private static string Normalize(string? value)
    {
        var builder = new StringBuilder();
        foreach (var character in value ?? string.Empty)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}
