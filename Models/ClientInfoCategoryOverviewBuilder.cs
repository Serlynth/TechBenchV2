using System.Text;
using TechBench.Services;

namespace TechBench.Models;

/// <summary>
/// Builds a bounded category dashboard from canonical Client Information
/// records. The named sections reuse FireDrill's real grouping rules, but no
/// FireDrill repository or workbook data is read here.
/// </summary>
public static class ClientInfoCategoryOverviewBuilder
{
    private const int MaximumValuesPerField = 3;
    private const int MaximumAccessRecordsPerSection = 2;

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
                BuildConnections(resources, credentials),
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
        IReadOnlyList<ClientInfoCredential> credentials)
    {
        var iloResources = resources.Where(resource => MatchesTerms(resource, "ilo", "idrac")).ToArray();
        var iloCredentials = credentials.Where(credential => MatchesTerms(credential, "ilo", "idrac")).ToArray();
        var upsResources = resources.Where(resource => MatchesTerms(resource, "ups")).ToArray();
        var upsCredentials = credentials.Where(credential => MatchesTerms(credential, "ups")).ToArray();
        var coreResources = resources.Except(iloResources).Except(upsResources).ToArray();
        var coreCredentials = credentials.Except(iloCredentials).Except(upsCredentials).ToArray();

        return
        [
            Section("Core infrastructure", "Servers, hosts, storage, switches, and their most useful support details.",
                WithAccess(
                    [
                        Names("Key systems", coreResources),
                        Values("Primary IP", coreResources, "primary_ip", "server ip", "host ip"),
                        Values("Management IP", coreResources, "management_ip", "management address"),
                        Values("Role / purpose", coreResources, "role_purpose", "server role", "purpose"),
                        Values("Operating system", coreResources, "operating_system", "server os", "os version"),
                        Values("Manufacturer / model", coreResources, "manufacturer_model", "server model", "hardware model")
                    ],
                    coreCredentials)),
            Section("ILO / iDRAC", "Out-of-band server management information from the canonical infrastructure records.",
                WithAccess(
                    [
                        Names("Managed hosts", iloResources),
                        Values("Management IP", iloResources, "management_ip", "ilo ip", "idrac ip"),
                        Addresses("Management URL", iloResources),
                        Values("Manufacturer / model", iloResources, "manufacturer_model", "server model", "ilo host", "idrac host"),
                        Values("Serial number", iloResources, "serial_number", "serial")
                    ],
                    iloCredentials)),
            Section("UPS", "Power protection and network-management information.",
                WithAccess(
                    [
                        Names("UPS systems", upsResources),
                        Values("Management IP", upsResources, "management_ip", "ups ip"),
                        Addresses("Management URL", upsResources),
                        Values("Manufacturer / model", upsResources, "manufacturer_model", "ups model"),
                        Values("Rack / runtime", upsResources, "additional_ips_subnet", "rack", "runtime")
                    ],
                    upsCredentials))
        ];
    }

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildConnections(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials)
    {
        var watchGuardResources = resources.Where(resource => IsFireDrillGroup(resource, "WatchGuard")).ToArray();
        var watchGuardCredentials = credentials.Where(credential => IsFireDrillGroup(credential, "WatchGuard")).ToArray();
        var otherResources = resources.Except(watchGuardResources).ToArray();
        var otherCredentials = credentials.Except(watchGuardCredentials).ToArray();

        return
        [
            Section("WatchGuard", "Firebox, AuthPoint, SSL VPN, and administrative access references.",
                WithAccess(
                    [
                        Names("Firewalls", watchGuardResources),
                        Coalesce("Firebox IP",
                            Values("Firebox IP", watchGuardResources, "firebox ip", "management_ip", "device ip"),
                            Addresses("Firebox IP", watchGuardResources)),
                        Aggregate("Status", watchGuardResources.Select(resource => resource.Status), watchGuardResources.Select(resource => resource.Name)),
                        Values("Device model", watchGuardResources, "device_model", "firebox model", "firewall model"),
                        Values("Firmware", watchGuardResources, "firmware_version", "fireware", "firmware"),
                        Values("Public / WAN IP", watchGuardResources, "public_wan_ip", "wan ip", "public ip"),
                        Values("Gateway", watchGuardResources, "gateway", "default gateway"),
                        Values("Subnet / CIDR", watchGuardResources, "subnet_cidr", "subnet", "cidr")
                    ],
                    watchGuardCredentials)),
            Section("Internet & circuits", "Provider, circuit, addressing, and support information outside WatchGuard.",
                WithAccess(
                    [
                        Names("Connections", otherResources),
                        Providers("Provider", otherResources),
                        Values("Public / WAN IP", otherResources, "public_wan_ip", "wan ip", "public ip"),
                        Values("Gateway", otherResources, "gateway", "default gateway"),
                        Values("Subnet / CIDR", otherResources, "subnet_cidr", "subnet", "cidr"),
                        Values("Circuit ID", otherResources, "circuit_id", "circuit", "account number"),
                        Values("Support phone", otherResources, "support_phone", "isp phone")
                    ],
                    otherCredentials))
        ];
    }

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildWifi(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials) =>
    [
        Section("WiFi", "The Wireless fields from FireDrill, plus canonical controller and network details.",
            WithAccess(
                [
                    Names("Wireless systems", resources),
                    Values("Controller", resources, "controller_name", "wireless controller", "controller"),
                    Addresses("Controller / URL", resources),
                    Values("Management IP", resources, "management_ip", "controller ip", "wireless ip"),
                    Values("Staff SSID", resources, "ssid", "wireless ssid", "staff ssid", "corporate ssid"),
                    Values("Guest SSID", resources, "guest_ssid", "wireless guest", "guest wifi"),
                    Values("Security", resources, "wireless_security", "wifi security", "encryption"),
                    Values("VLAN", resources, "vlan", "wifi vlan")
                ],
                credentials))
    ];

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildApplications(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials)
    {
        var microsoftResources = resources.Where(resource => IsFireDrillGroup(resource, "Microsoft 365")).ToArray();
        var microsoftCredentials = credentials.Where(credential => IsFireDrillGroup(credential, "Microsoft 365")).ToArray();
        var remoteResources = resources.Where(resource => IsFireDrillGroup(resource, "Remote Access")).ToArray();
        var remoteCredentials = credentials.Where(credential => IsFireDrillGroup(credential, "Remote Access")).ToArray();
        var otherResources = resources.Except(microsoftResources).Except(remoteResources).ToArray();
        var otherCredentials = credentials.Except(microsoftCredentials).Except(remoteCredentials).ToArray();

        return
        [
            Section("Microsoft 365", "Tenant and administrative access.",
                WithAccess(
                    [
                        Values("Tenant / instance", microsoftResources, "tenant_instance", "tenant name", "tenant id", "onmicrosoft"),
                        Coalesce("Admin portal",
                            Values("Admin portal", microsoftResources, "admin_portal", "portal url", "console url"),
                            Addresses("Admin portal", microsoftResources))
                    ],
                    microsoftCredentials)),
            Section("Remote Access", "RustDesk, ScreenConnect, ConnectWise, Splashtop, and TeamViewer references.",
                WithAccess(
                    [
                        Names("Services", remoteResources),
                        Providers("Provider", remoteResources),
                        Addresses("Portal / URL", remoteResources),
                        Values("Tenant / instance", remoteResources, "tenant_instance", "instance", "site name"),
                        Values("Version", remoteResources, "version"),
                        Values("Support", remoteResources, "support_contact", "support")
                    ],
                    remoteCredentials)),
            Section("Other applications & cloud", "Important canonical application records outside the named FireDrill groups.",
                WithAccess(
                    [
                        Names("Services", otherResources),
                        Providers("Provider", otherResources),
                        Values("Tenant / instance", otherResources, "tenant_instance", "instance"),
                        Values("Admin portal", otherResources, "admin_portal", "portal url"),
                        Values("Version / plan", otherResources, "version", "plan"),
                        Values("Renewal", otherResources, "renewal_date", "renewal")
                    ],
                    otherCredentials))
        ];
    }

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildDomains(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials) =>
    [
        Section("Domain & AD", "Domain controllers and administrative access.",
            WithAccess(
                [
                    Values("Domain", resources, "domain_name", "local domain", "ad domain", "domain"),
                    Values("Domain controllers", resources, "domain_controller", "domain controller", "dc name", "dc ip"),
                    Addresses("Domain / admin URL", resources)
                ],
                credentials))
    ];

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildProtection(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials)
    {
        var groups = new[] { "Veeam", "ESET", "Barracuda" };
        var sections = groups.Select(group => ProtectionSection(
                group,
                resources.Where(resource => IsFireDrillGroup(resource, group)).ToArray(),
                credentials.Where(credential => IsFireDrillGroup(credential, group)).ToArray()))
            .ToList();
        var groupedResources = resources.Where(resource => groups.Any(group => IsFireDrillGroup(resource, group))).ToArray();
        var groupedCredentials = credentials.Where(credential => groups.Any(group => IsFireDrillGroup(credential, group))).ToArray();
        sections.Add(ProtectionSection(
            "Other backup & security",
            resources.Except(groupedResources).ToArray(),
            credentials.Except(groupedCredentials).ToArray()));
        return sections;
    }

    private static ClientInfoCategoryOverviewSection ProtectionSection(
        string title,
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials) =>
        Section(title, title == "Veeam"
                ? "Backup scope, console, schedule, retention, restore testing, and access."
                : title == "ESET"
                    ? "Endpoint protection scope, console, renewal, and access."
                    : title == "Barracuda"
                        ? "Email security or backup scope, console, retention, and access."
                        : "Important protection records outside the named FireDrill groups.",
            WithAccess(
                [
                    Values("Product / service", resources, "product_service", "product", "service"),
                    Names("Systems", resources),
                    Values("Protected scope", resources, "protected_scope", "protected devices", "backup scope"),
                    Coalesce("Console / portal",
                        Values("Console / portal", resources, "console_url", "portal url", "management url"),
                        Addresses("Console / portal", resources)),
                    Values("Schedule", resources, "backup_schedule", "backup time", "schedule"),
                    Values("Retention", resources, "retention", "retention period"),
                    Values("Last restore test", resources, "last_restore_test", "restore test", "last test"),
                    Values("Renewal", resources, "renewal_date", "renewal", "expiration")
                ],
                credentials));

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildVendors(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials) =>
    [
        Section("Vendors & services", "Account and contact information needed to reach providers.",
            WithAccess(
                [
                    Names("Services", resources),
                    Providers("Vendor", resources),
                    Values("Account number", resources, "account_number", "customer number", "account id"),
                    Values("Primary contact", resources, "primary_contact", "account manager", "contact"),
                    Values("Support phone", resources, "support_phone", "phone"),
                    Values("Support email", resources, "support_email", "email"),
                    Coalesce("Portal", Values("Portal", resources, "portal_url", "support portal"), Addresses("Portal", resources)),
                    Values("Contract expiration", resources, "contract_expiration", "renewal", "expiration")
                ],
                credentials))
    ];

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildNeedsSorting(
        IReadOnlyList<ClientInfoResource> resources) =>
        resources.Count == 0
            ? []
            :
            [
                Section("Sorting queue", "A short summary; use the full list below to classify each record.",
                    Field("Records waiting", resources.Count.ToString()),
                    Names("Examples", resources),
                    Aggregate("Types", resources.Select(resource => resource.TypeLabel)),
                    Aggregate("Providers", resources.Select(resource => resource.Provider)))
            ];

    private static ClientInfoOverviewField?[] WithAccess(
        IEnumerable<ClientInfoOverviewField?> fields,
        IReadOnlyList<ClientInfoCredential> credentials)
    {
        var accessRecords = credentials
            .Where(credential => credential.IsActive)
            .OrderBy(credential => credential.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var visibleAccessRecords = accessRecords
            .Take(MaximumAccessRecordsPerSection)
            .Select(AccessField)
            .Cast<ClientInfoOverviewField?>();
        var remainingCount = accessRecords.Length - MaximumAccessRecordsPerSection;

        return fields
            .Concat(visibleAccessRecords)
            .Append(remainingCount > 0
                ? Field("More access", $"+{remainingCount} more in Passwords")
                : null)
            .ToArray();
    }

    private static ClientInfoOverviewField AccessField(ClientInfoCredential credential)
    {
        var details = new[] { credential.Username, credential.LoginUrl }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var value = details.Length > 0
            ? string.Join(Environment.NewLine, details)
            : credential.Secrets.Count > 0 ? "Password available" : "Access record";
        return new ClientInfoOverviewField(
            string.IsNullOrWhiteSpace(credential.Name) ? "Access" : credential.Name.Trim(),
            value,
            credential.Secrets.Where(secret => secret.IsCurrent).ToArray());
    }

    private static ClientInfoCategoryOverviewSection Section(
        string title,
        string description,
        params ClientInfoOverviewField?[] fields) =>
        new(title, description, fields.Where(field => field is not null).Cast<ClientInfoOverviewField>().ToArray());

    private static ClientInfoOverviewField? Names(string label, IReadOnlyList<ClientInfoResource> resources) =>
        Aggregate(label, resources.Select(resource => resource.Name));

    private static ClientInfoOverviewField? Providers(string label, IReadOnlyList<ClientInfoResource> resources) =>
        Aggregate(label, resources.Select(resource => resource.Provider), resources.Select(resource => resource.Name));

    private static ClientInfoOverviewField? Addresses(string label, IReadOnlyList<ClientInfoResource> resources) =>
        Aggregate(label, resources.Select(resource => resource.AddressOrUrl), resources.Select(resource => resource.Name));

    private static ClientInfoOverviewField? Values(
        string label,
        IReadOnlyList<ClientInfoResource> resources,
        params string[] aliases) =>
        Aggregate(label, resources.Select(resource => FindValue(resource, aliases)), resources.Select(resource => resource.Name));

    private static ClientInfoOverviewField? Coalesce(
        string label,
        params ClientInfoOverviewField?[] candidates)
    {
        var value = candidates.FirstOrDefault(candidate => candidate is not null)?.Value;
        return Field(label, value);
    }

    private static string FindValue(ClientInfoResource resource, IReadOnlyList<string> aliases)
    {
        var normalizedAliases = aliases.Select(Normalize).Where(value => value.Length > 0).ToArray();
        var exact = resource.Fields.FirstOrDefault(field => normalizedAliases.Any(alias =>
            Normalize(field.FieldKey) == alias || Normalize(field.FieldLabel) == alias));
        if (exact is not null)
        {
            return exact.ValueText;
        }

        return resource.Fields.FirstOrDefault(field => aliases.Any(alias =>
            MatchesAlias(field.FieldKey, alias) || MatchesAlias(field.FieldLabel, alias)))?.ValueText ?? string.Empty;
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

    private static bool IsFireDrillGroup(ClientInfoResource resource, string groupName) =>
        ResolveFireDrillGroup(Searchable(resource)).Equals(groupName, StringComparison.OrdinalIgnoreCase);

    private static bool IsFireDrillGroup(ClientInfoCredential credential, string groupName) =>
        ResolveFireDrillGroup(Searchable(credential)).Equals(groupName, StringComparison.OrdinalIgnoreCase);

    private static string ResolveFireDrillGroup(string searchable)
    {
        var group = CredentialFieldGrouper.Group(
        [
            new FireDrillCredentialField
            {
                Label = searchable,
                FieldName = searchable,
                Value = string.Empty
            }
        ]);
        return group.Count == 0 ? "Other" : group[0].Name;
    }

    private static bool MatchesTerms(ClientInfoResource resource, params string[] terms) =>
        terms.Any(term => Searchable(resource).Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesTerms(ClientInfoCredential credential, params string[] terms) =>
        terms.Any(term => Searchable(credential).Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string Searchable(ClientInfoResource resource) =>
        string.Join(" ",
            new[] { resource.Name, resource.TypeLabel, resource.Provider, resource.AddressOrUrl }
                .Concat(resource.Fields.SelectMany(field => new[] { field.FieldKey, field.FieldLabel, field.ValueText })));

    private static string Searchable(ClientInfoCredential credential) =>
        $"{credential.Name} {credential.Category} {credential.Username} {credential.LoginUrl}";

    private static bool MatchesAlias(string? fieldName, string? alias)
    {
        var normalizedField = Normalize(fieldName);
        var normalizedAlias = Normalize(alias);
        if (normalizedAlias.Length > 0 && normalizedField.Contains(normalizedAlias, StringComparison.Ordinal))
        {
            return true;
        }

        var tokens = (alias ?? string.Empty)
            .Split([' ', '_', '-', '/', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(token => token.Length > 1)
            .ToArray();
        return tokens.Length > 1 && tokens.All(token => normalizedField.Contains(token, StringComparison.Ordinal));
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
