using System.Net;
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
            ClientInfoResourceCategories.Backup =>
                BuildBackup(resources, credentials),
            ClientInfoResourceCategories.Security =>
                BuildSecurity(resources, credentials),
            ClientInfoResourceCategories.VendorsServices =>
                BuildVendors(resources, credentials),
            _ => BuildNeedsSorting(resources)
        };

        return sections.Where(section => section.Fields.Count > 0).ToArray();
    }

    public static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildSelected(
        string category,
        ClientInfoResource resource,
        IReadOnlyList<ClientInfoCredential> credentials)
    {
        var title = category switch
        {
            ClientInfoResourceCategories.ServersInfrastructure =>
                MatchesTerms(resource, "ilo", "idrac")
                    ? "ILO"
                    : MatchesTerms(resource, "ups")
                        ? "UPS"
                        : "Core infrastructure",
            ClientInfoResourceCategories.ConnectionInternet =>
                IsInternetCircuit(resource)
                    ? "Internet & circuits"
                    : "Connection",
            ClientInfoResourceCategories.Wifi => "WiFi",
            ClientInfoResourceCategories.ApplicationsCloud =>
                IsRemoteAccessResource(resource)
                    ? "Remote Access"
                    : IsMicrosoft365Resource(resource)
                        ? "Microsoft 365"
                        : "Other applications & cloud",
            ClientInfoResourceCategories.DomainsEmail =>
                IsEmailSecurityResource(resource)
                    ? "Email Security"
                    : "Domain & AD",
            ClientInfoResourceCategories.Backup =>
                IsFireDrillGroup(resource, "Veeam")
                    ? "Veeam"
                    : "Other backup",
            ClientInfoResourceCategories.Security =>
                IsFireDrillGroup(resource, "ESET")
                    ? "ESET"
                    : "Other security",
            ClientInfoResourceCategories.VendorsServices => "Vendors & services",
            _ => "Sorting queue"
        };

        return Build(category, [resource], credentials)
            .Where(section => string.Equals(
                section.Title,
                title,
                StringComparison.OrdinalIgnoreCase))
            .Select(section => IncludeEverySelectedCredential(
                section,
                credentials))
            .ToArray();
    }

    private static ClientInfoCategoryOverviewSection IncludeEverySelectedCredential(
        ClientInfoCategoryOverviewSection section,
        IReadOnlyList<ClientInfoCredential> credentials)
    {
        var fields = section.Fields
            .Where(field => !string.Equals(
                field.Label,
                "More access",
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        var representedCredentialIds = fields
            .SelectMany(field => field.CredentialIds)
            .ToHashSet();
        foreach (var credential in credentials
                     .Where(credential => credential.IsActive)
                     .Where(credential => !representedCredentialIds.Contains(
                         credential.CredentialId))
                     .OrderBy(credential => credential.Name,
                         StringComparer.OrdinalIgnoreCase))
        {
            fields.Add(AccessField(credential));
            representedCredentialIds.Add(credential.CredentialId);
        }

        return section with { Fields = fields };
    }

    public static ClientInfoCategoryOverviewSection BuildCloudAccounts(
        IReadOnlyList<ClientInfoCredential> credentials) =>
        Section(
            "Cloud Accounts",
            "Administrative usernames and passwords for Barracuda, ESET, and Microsoft 365.",
            RequiredCredentialField(
                "Barracuda",
                MatchingCredentials(credentials, ["barracuda"])),
            RequiredCredentialField(
                "ESET",
                MatchingCredentials(credentials, ["eset"])),
            RequiredCredentialField(
                "Microsoft 365",
                MatchingCredentials(
                    credentials,
                    ["microsoft 365", "office 365", "m365"])));

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

        var sections = new List<ClientInfoCategoryOverviewSection>
        {
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
                    coreCredentials))
        };
        sections.AddRange(BuildIloSections(iloResources, iloCredentials));
        sections.Add(
            Section("UPS", "Location, model, network address, and administrator access.",
                RequiredField("Location", Coalesce(
                    "Location",
                    Aggregate("Location", upsResources.Select(resource => resource.LocationName)),
                    Values("Location", upsResources, "location", "site", "room"))),
                RequiredField("Model", Coalesce(
                    "Model",
                    Values("Model", upsResources, "manufacturer_model", "ups model", "model"),
                    Aggregate("Model", upsResources.Select(resource => resource.TypeLabel)))),
                RequiredField("IP", Values(
                    "IP",
                    upsResources,
                    "management_ip",
                    "ups ip",
                    "ip address",
                    "primary_ip")),
                RequiredCredentialUsernameField("Username", upsCredentials),
                RequiredCredentialPasswordField("Password", upsCredentials)));
        return sections;
    }

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildIloSections(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials)
    {
        if (resources.Count == 0)
        {
            return credentials
                .Where(credential => credential.IsActive)
                .OrderBy(credential => credential.Name, StringComparer.OrdinalIgnoreCase)
                .Select(credential => Section(
                    "ILO",
                    string.Empty,
                    RequiredField("Host name", Field("Host name", credential.Name)),
                    RequiredField("Host IP", Field("Host IP", NetworkHost(credential.LoginUrl))),
                    RequiredCredentialUsernameField("Username", [credential]),
                    RequiredCredentialPasswordField("Password", [credential])))
                .ToArray();
        }

        return resources
            .OrderBy(resource => resource.Name, StringComparer.OrdinalIgnoreCase)
            .Select(resource =>
            {
                var access = MatchingIloCredentials(resource, resources, credentials);
                return Section(
                    "ILO",
                    string.Empty,
                    RequiredField("Host name", Coalesce(
                        "Host name",
                        Field("Host name", FindValue(
                            resource,
                            ["host_name", "host name", "hostname", "server_name", "server hostname", "server name"])),
                        Field("Host name", resource.Name))),
                    RequiredField("Host IP", Coalesce(
                        "Host IP",
                        Field("Host IP", FindValue(
                            resource,
                            ["management_ip", "ilo ip", "host ip", "ip address", "primary_ip"])),
                        Field("Host IP", NetworkHost(resource.AddressOrUrl)))),
                    RequiredCredentialUsernameField("Username", access),
                    RequiredCredentialPasswordField("Password", access));
            })
            .ToArray();
    }

    private static ClientInfoCredential[] MatchingIloCredentials(
        ClientInfoResource resource,
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials)
    {
        var linked = credentials
            .Where(credential => credential.IsActive)
            .Where(credential => credential.ResourceId == resource.ResourceId)
            .ToArray();
        if (linked.Length > 0)
        {
            return linked;
        }

        var normalizedName = Normalize(resource.Name);
        var host = NetworkHost(resource.AddressOrUrl);
        var named = credentials
            .Where(credential => credential.IsActive)
            .Where(credential =>
                (normalizedName.Length > 0
                 && Normalize(CredentialMetadata(credential)).Contains(
                     normalizedName,
                     StringComparison.Ordinal))
                || (host.Length > 0
                    && string.Equals(
                        NetworkHost(credential.LoginUrl),
                        host,
                        StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (named.Length > 0)
        {
            return named;
        }

        return resources.Count == 1
            ? credentials.Where(credential => credential.IsActive).ToArray()
            : [];
    }

    private static string NetworkHost(string? address)
    {
        var value = address?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var candidate = value.Contains("://", StringComparison.Ordinal)
            ? value
            : $"https://{value}";
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            ? uri.Host
            : value;
    }

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildConnections(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials)
    {
        var ispResources = resources
            .Where(IsInternetCircuit)
            .ToArray();
        var ispResourceIds = ispResources
            .Select(resource => resource.ResourceId)
            .ToHashSet();
        var ispCredentials = credentials
            .Where(credential =>
                credential.ResourceId.HasValue
                    ? ispResourceIds.Contains(credential.ResourceId.Value)
                    : MatchesTerms(
                        credential,
                        "internet provider",
                        "isp portal",
                        "comcast",
                        "verizon"))
            .ToArray();
        var connectionResources = resources.Except(ispResources).ToArray();
        var connectionCredentials = credentials.Except(ispCredentials).ToArray();
        var status = MatchingCredentials(connectionCredentials, ["status"]);
        var admin = MatchingCredentials(
            connectionCredentials,
            ["watchguard admin", "firebox admin", "firewall admin", "admin password", "admin"],
            ["status", "firebox db", "firebox database", "csriadmin", "authpoint", "ssl vpn", "sslvpn", "cloud", "ad auth", "active directory"]);
        var fireboxDatabase = MatchingCredentials(
            connectionCredentials,
            ["firebox db", "firebox database", "firebox-db"]);
        var csriAdminAuthPoint = connectionCredentials
            .Where(credential => credential.IsActive)
            .Where(credential =>
                ContainsCredentialTerm(credential, "csriadmin")
                || (Normalize(credential.Username).Contains("csriadmin", StringComparison.Ordinal)
                    && ContainsCredentialTerm(credential, "authpoint")))
            .ToArray();
        var authPoint = MatchingCredentials(
            connectionCredentials,
            ["authpoint user", "authpoint"],
            ["csriadmin"]);
        var sslVpn = MatchingCredentials(
            connectionCredentials,
            ["ssl vpn", "sslvpn"]);
        var watchGuardCloud = MatchingCredentials(
            connectionCredentials,
            ["watchguard cloud", "cloud user", "cloud password"]);
        var watchGuardAd = MatchingCredentials(
            connectionCredentials,
            ["watchguard ad auth", "ad auth", "ad user", "ad password", "active directory auth"],
            ["authpoint"]);

        var sections = new List<ClientInfoCategoryOverviewSection>();
        if (connectionResources.Length > 0 || resources.Count == 0)
        {
            sections.Add(Section("Connection", "The connection details technicians use most often.",
                RequiredField("External IP", Values("External IP", connectionResources, "public_wan_ip", "external ip", "firebox ip", "wan ip", "public ip")),
                RequiredField("SSL VPN port", Values("SSL VPN port", connectionResources, "ssl_vpn_port", "ssl vpn port", "sslvpn port", "vpn port")),
                RequiredField("Model", Values("Model", connectionResources, "device_model", "model", "firebox model", "firewall model")),
                RequiredCredentialField("Status password", status),
                RequiredCredentialField("Admin password", admin),
                fireboxDatabase.Length > 0
                    ? RequiredCredentialField("Firebox-DB\\csri", fireboxDatabase)
                    : RequiredCredentialField("CSRIAdmin AuthPoint", csriAdminAuthPoint),
                RequiredCredentialField("AuthPoint user", authPoint),
                RequiredCredentialField("SSL VPN password", sslVpn),
                RequiredCredentialField("WatchGuard Cloud user / password", watchGuardCloud),
                RequiredCredentialField("WatchGuard AD auth user / password", watchGuardAd)));
        }

        if (ispResources.Length > 0)
        {
            sections.Add(Section(
                "Internet & circuits",
                string.Empty,
                RequiredField("Provider", Coalesce(
                    "Provider",
                    Providers("Provider", ispResources),
                    Values("Provider", ispResources, "isp_provider", "isp", "carrier"))),
                RequiredField("Circuit ID", Values(
                    "Circuit ID",
                    ispResources,
                    "circuit_id",
                    "account number",
                    "circuit number")),
                RequiredField("Account number", Values(
                    "Account number",
                    ispResources,
                    "account_number",
                    "customer number",
                    "billing account")),
                RequiredField("Service type", Values(
                    "Service type",
                    ispResources,
                    "service_type",
                    "service plan",
                    "circuit type")),
                RequiredField("Bandwidth", Values(
                    "Bandwidth",
                    ispResources,
                    "bandwidth",
                    "speed",
                    "service speed")),
                RequiredField("Public IP", Values(
                    "Public IP",
                    ispResources,
                    "public_wan_ip",
                    "public ip",
                    "static ip")),
                RequiredField("Gateway", Values("Gateway", ispResources, "gateway")),
                RequiredField("Subnet / CIDR", Values(
                    "Subnet / CIDR",
                    ispResources,
                    "subnet_cidr",
                    "subnet",
                    "cidr")),
                IpAssignment(ispResources),
                StaticIpCount(ispResources),
                StaticIpDetails(ispResources),
                RequiredField("Support contact", Values(
                    "Support contact",
                    ispResources,
                    "support_contact",
                    "support name",
                    "account representative")),
                RequiredField("Support phone", Values(
                    "Support phone",
                    ispResources,
                    "support_phone",
                    "support number")),
                RequiredField("Location", Aggregate(
                    "Location",
                    ispResources.Select(resource => resource.LocationName))),
                RequiredField("Status", Aggregate(
                    "Status",
                    ispResources.Select(resource => resource.Status))),
                RequiredCredentialField(
                    "Account username / password",
                    ispCredentials)));
        }

        return sections;
    }

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildWifi(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials)
    {
        var activeCredentials = credentials
            .Where(credential => credential.IsActive)
            .ToArray();
        var admin = MatchingCredentials(
            activeCredentials,
            ["wireless admin", "wifi admin", "controller", "management", "central", "unifi", "meraki"]);
        if (admin.Length == 0)
        {
            admin = activeCredentials
                .Where(credential =>
                    !string.IsNullOrWhiteSpace(credential.Username)
                    && !string.IsNullOrWhiteSpace(credential.LoginUrl)
                    && !IsWifiPasswordCredential(credential))
                .ToArray();
        }

        var fields = new List<ClientInfoOverviewField?>
        {
            RequiredField("Type", Aggregate("Type", resources.Select(resource => resource.TypeLabel))),
            RequiredField("Management URL", Aggregate(
                "Management URL",
                resources.Select(resource => FindValue(
                        resource,
                        ["management_url", "management url", "controller url"]))
                    .Concat(resources.Select(resource => resource.AddressOrUrl))
                    .Concat(admin.Select(credential => credential.LoginUrl)))),
            RequiredCredentialField("Admin username / password", admin)
        };
        var ssidFields = BuildSsidFields(
            resources,
            activeCredentials,
            admin.Select(credential => credential.CredentialId).ToHashSet());
        fields.AddRange(ssidFields.Count > 0
            ? ssidFields
            : [new ClientInfoOverviewField("SSID / password", "Not entered")]);

        return
        [
            Section(
                "WiFi",
                "Wireless management access plus every configured SSID and password.",
                fields.ToArray())
        ];
    }

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildApplications(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials)
    {
        var remoteResources = resources.Where(IsRemoteAccessResource).ToArray();
        var microsoftResources = resources
            .Except(remoteResources)
            .Where(IsMicrosoft365Resource)
            .ToArray();
        var remoteCredentials = credentials
            .Where(credential => IsFireDrillGroup(credential, "Remote Access"))
            .ToArray();
        var microsoftCredentials = credentials
            .Except(remoteCredentials)
            .Where(credential => IsFireDrillGroup(credential, "Microsoft 365"))
            .ToArray();
        var otherResources = resources.Except(microsoftResources).Except(remoteResources).ToArray();
        var otherCredentials = credentials.Except(microsoftCredentials).Except(remoteCredentials).ToArray();

        return
        [
            Section("Microsoft 365", "Tenant and administrative access.",
                WithAccess(
                    [
                        Values("Tenant / instance", microsoftResources, "tenant_instance", "tenant name", "tenant id", "onmicrosoft")
                    ],
                    microsoftCredentials)),
            Section("Remote Access", "RustDesk, ScreenConnect, ConnectWise, Splashtop, and TeamViewer references.",
                WithAccess(
                    [
                        Names("Services", remoteResources),
                        Providers("Provider", remoteResources),
                        Addresses("Portal / URL", remoteResources),
                        Values("Tenant / instance", remoteResources, "tenant_instance", "instance", "site name"),
                        Values("Version", remoteResources, "version")
                    ],
                    remoteCredentials)),
            Section("Other applications & cloud", "Important canonical application records outside the named FireDrill groups.",
                WithAccess(
                    [
                        Names("Services", otherResources),
                        Providers("Provider", otherResources),
                        Values("Tenant / instance", otherResources, "tenant_instance", "instance"),
                        Values("Version / plan", otherResources, "version", "plan"),
                        Values("Renewal", otherResources, "renewal_date", "renewal")
                    ],
                    otherCredentials))
        ];
    }

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildDomains(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials)
    {
        var emailSecurityResources = resources
            .Where(IsEmailSecurityResource)
            .ToArray();
        var emailSecurityResourceIds = emailSecurityResources
            .Select(resource => resource.ResourceId)
            .ToHashSet();
        var emailSecurityCredentials = credentials
            .Where(credential =>
                credential.ResourceId.HasValue
                    ? emailSecurityResourceIds.Contains(credential.ResourceId.Value)
                    : MatchesTerms(
                        credential,
                        "barracuda",
                        "email security",
                        "spam filter"))
            .ToArray();
        var domainResources = resources.Except(emailSecurityResources).ToArray();
        var activeDirectoryResources = domainResources.Where(resource =>
                MatchesTerms(resource, "active directory", "domain controller", "directory service")
                || !string.IsNullOrWhiteSpace(FindValue(
                    resource,
                    ["ad_domain", "active_directory_domain", "local_domain"])))
            .ToArray();
        var emailDomainResources = domainResources.Except(activeDirectoryResources).ToArray();
        var domainAdministrators = MatchingCredentials(
            credentials.Except(emailSecurityCredentials).ToArray(),
            ["domain admin", "active directory admin", "ad admin"]);

        var sections = new List<ClientInfoCategoryOverviewSection>
        {
            Section("Domain & AD", "The domain names and administrative access technicians use most often.",
                RequiredField("AD domain", Coalesce(
                    "AD domain",
                    Values("AD domain", domainResources, "ad_domain", "active_directory_domain", "local_domain"),
                    Values("AD domain", activeDirectoryResources, "domain_name", "domain"))),
                RequiredField("Email domain", Coalesce(
                    "Email domain",
                    Values("Email domain", domainResources, "email_domain", "primary_email_domain", "mail_domain"),
                    Values("Email domain", emailDomainResources, "domain_name", "domain"))),
                RequiredCredentialField("Domain admin username / password", domainAdministrators))
        };
        if (emailSecurityResources.Length > 0)
        {
            sections.Add(Section(
                "Email Security",
                string.Empty,
                RequiredField("Provider", Providers("Provider", emailSecurityResources)),
                RequiredField("Protected domain", Values(
                    "Protected domain",
                    emailSecurityResources,
                    "domain_name",
                    "email domain")),
                RequiredField("Service", Values(
                    "Service",
                    emailSecurityResources,
                    "product_service",
                    "mail_provider",
                    "service")),
                RequiredField("Protected mailboxes", Values(
                    "Protected mailboxes",
                    emailSecurityResources,
                    "tenant_name",
                    "protected_scope",
                    "coverage")),
                RequiredField("Message retention", Values(
                    "Message retention",
                    emailSecurityResources,
                    "retention",
                    "message retention")),
                RequiredField("Filtering / monitoring", Values(
                    "Filtering / monitoring",
                    emailSecurityResources,
                    "backup_schedule",
                    "policy",
                    "monitoring")),
                RequiredField("Renewal", Values(
                    "Renewal",
                    emailSecurityResources,
                    "expiration_date",
                    "renewal_date")),
                RequiredCredentialField(
                    "Admin username / password",
                    emailSecurityCredentials)));
        }

        return sections;
    }

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildBackup(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials) =>
        BuildProtection(
            resources,
            credentials,
            ["Veeam"],
            "Other backup");

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildSecurity(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials) =>
        BuildProtection(
            resources,
            credentials,
            ["ESET"],
            "Other security");

    private static IReadOnlyList<ClientInfoCategoryOverviewSection> BuildProtection(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials,
        IReadOnlyList<string> groups,
        string otherTitle)
    {
        var sections = groups.Select(group => ProtectionSection(
                group,
                resources.Where(resource => IsFireDrillGroup(resource, group)).ToArray(),
                credentials.Where(credential => IsFireDrillGroup(credential, group)).ToArray()))
            .ToList();
        var groupedResources = resources.Where(resource => groups.Any(group => IsFireDrillGroup(resource, group))).ToArray();
        var groupedCredentials = credentials.Where(credential => groups.Any(group => IsFireDrillGroup(credential, group))).ToArray();
        sections.Add(ProtectionSection(
            otherTitle,
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
                        ? "Email security scope, console, retention, and access."
                        : title == "Other backup"
                            ? "Important backup records outside the named FireDrill groups."
                            : "Important security records outside the named FireDrill groups.",
            WithAccess(
                [
                    Values("Product / service", resources, "product_service", "product", "service"),
                    Names("Systems", resources),
                    Values(
                        title is "Veeam" or "Other backup"
                            ? "Protected scope"
                            : "Protected scope / coverage",
                        resources,
                        "protected_scope", "protected devices", "backup scope", "coverage"),
                    Coalesce("Console / portal",
                        Values("Console / portal", resources, "console_url", "portal url", "management url"),
                        Addresses("Console / portal", resources)),
                    Values(
                        title is "Veeam" or "Other backup"
                            ? "Schedule"
                            : "Policy / monitoring",
                        resources,
                        "backup_schedule", "backup time", "schedule", "policy", "monitoring"),
                    Values("Retention", resources, "retention", "retention period"),
                    Values(
                        title is "Veeam" or "Other backup"
                            ? "Last restore test"
                            : "Last review / test",
                        resources,
                        "last_restore_test", "restore test", "last test", "last review"),
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

    private static ClientInfoCredential[] MatchingCredentials(
        IEnumerable<ClientInfoCredential> credentials,
        IReadOnlyList<string> includedTerms,
        IReadOnlyList<string>? excludedTerms = null) =>
        credentials
            .Where(credential => credential.IsActive)
            .Where(credential => includedTerms.Any(term =>
                ContainsCredentialTerm(credential, term)))
            .Where(credential => excludedTerms is null || excludedTerms.All(term =>
                !ContainsCredentialTerm(credential, term)))
            .OrderBy(credential => credential.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool ContainsCredentialTerm(
        ClientInfoCredential credential,
        string term) =>
        Normalize(CredentialMetadata(credential)).Contains(
            Normalize(term),
            StringComparison.Ordinal);

    private static string CredentialMetadata(ClientInfoCredential credential) =>
        string.Join(
            " ",
            new[] { credential.Name, credential.Category }
                .Concat(credential.Secrets.SelectMany(secret =>
                    new[] { secret.SecretType, secret.SecretLabel })));

    private static ClientInfoOverviewField? CredentialField(
        string label,
        IEnumerable<ClientInfoCredential> credentials)
    {
        var matches = credentials
            .Where(credential => credential.IsActive)
            .OrderBy(credential => credential.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matches.Length == 0)
        {
            return null;
        }

        var usernames = matches
            .Select(credential => credential.Username?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var secrets = matches
            .SelectMany(credential => credential.Secrets)
            .Where(secret => secret.IsCurrent)
            .GroupBy(secret =>
                $"{secret.CredentialId}:{secret.SecretId}:{secret.SecretType}:{secret.SecretLabel}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var value = usernames.Length > 0
            ? string.Join(Environment.NewLine, usernames)
            : secrets.Length > 0
                ? "Password available"
                : "Configured";
        return new ClientInfoOverviewField(
            label,
            value,
            secrets,
            matches.Select(credential => credential.CredentialId).ToArray());
    }

    private static ClientInfoOverviewField RequiredCredentialField(
        string label,
        IEnumerable<ClientInfoCredential> credentials) =>
        CredentialField(label, credentials)
        ?? new ClientInfoOverviewField(label, "Not entered");

    private static ClientInfoOverviewField RequiredCredentialUsernameField(
        string label,
        IEnumerable<ClientInfoCredential> credentials) =>
        RequiredField(
            label,
            Aggregate(
                label,
                credentials
                    .Where(credential => credential.IsActive)
                    .Select(credential => credential.Username)));

    private static ClientInfoOverviewField RequiredCredentialPasswordField(
        string label,
        IEnumerable<ClientInfoCredential> credentials)
    {
        var secrets = credentials
            .Where(credential => credential.IsActive)
            .SelectMany(credential => credential.Secrets)
            .Where(secret => secret.IsCurrent)
            .GroupBy(secret =>
                $"{secret.CredentialId}:{secret.SecretId}:{secret.SecretType}:{secret.SecretLabel}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        return new ClientInfoOverviewField(
            label,
            secrets.Length > 0 ? "Password available" : "Not entered",
            secrets,
            credentials
                .Where(credential => credential.IsActive)
                .Select(credential => credential.CredentialId)
                .Distinct()
                .ToArray());
    }

    private static ClientInfoOverviewField RequiredField(
        string label,
        ClientInfoOverviewField? field) =>
        field ?? new ClientInfoOverviewField(label, "Not entered");

    private static IReadOnlyList<ClientInfoOverviewField?> BuildSsidFields(
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoCredential> credentials,
        IReadOnlySet<long> adminCredentialIds)
    {
        var entries = resources
            .SelectMany(resource => resource.Fields
                .Where(field =>
                    Normalize(field.FieldKey).Contains("ssid", StringComparison.Ordinal)
                    || Normalize(field.FieldLabel).Contains("ssid", StringComparison.Ordinal))
                .Where(field => !string.IsNullOrWhiteSpace(field.ValueText))
                .Select(field => new
                {
                    resource.ResourceId,
                    Label = string.IsNullOrWhiteSpace(field.FieldLabel)
                        ? "SSID"
                        : field.FieldLabel.Trim(),
                    Value = field.ValueText.Trim()
                }))
            .GroupBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Value = group.Key,
                Label = group.First().Label,
                ResourceIds = group.Select(entry => entry.ResourceId).ToHashSet()
            })
            .ToArray();

        return entries.Select(entry =>
        {
            var normalizedSsid = Normalize(entry.Value);
            var candidates = credentials
                .Where(credential => credential.IsActive)
                .Where(credential => !adminCredentialIds.Contains(credential.CredentialId))
                .ToArray();
            var exactMatches = candidates
                .Where(credential =>
                    normalizedSsid.Length > 0
                    && (CredentialMetadata(credential).Contains(
                        entry.Value,
                        StringComparison.OrdinalIgnoreCase)
                    || Normalize(CredentialMetadata(credential)).Contains(
                        normalizedSsid,
                        StringComparison.Ordinal)))
                .ToArray();
            var isGuest = Normalize(entry.Label).Contains("guest", StringComparison.Ordinal);
            var linkedMatches = candidates
                .Where(credential =>
                    credential.ResourceId.HasValue
                    && entry.ResourceIds.Contains(credential.ResourceId.Value)
                    && IsWifiPasswordCredential(credential))
                .Where(credential =>
                    isGuest == ContainsCredentialTerm(credential, "guest"))
                .ToArray();
            var matches = exactMatches.Length > 0
                ? exactMatches
                : linkedMatches;
            var secrets = matches
                .SelectMany(credential => credential.Secrets)
                .Where(secret => secret.IsCurrent)
                .GroupBy(secret =>
                    $"{secret.CredentialId}:{secret.SecretId}:{secret.SecretType}:{secret.SecretLabel}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var value = secrets.Length > 0
                ? entry.Value
                : $"{entry.Value}{Environment.NewLine}Password not entered";
            return new ClientInfoOverviewField(
                entry.Label,
                value,
                secrets,
                matches.Select(credential => credential.CredentialId).ToArray());
        }).Cast<ClientInfoOverviewField?>().ToArray();
    }

    private static bool IsWifiPasswordCredential(ClientInfoCredential credential) =>
        new[] { "ssid", "wireless", "wifi", "guest", "network password" }
            .Any(term => ContainsCredentialTerm(credential, term));

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
            credential.Secrets.Where(secret => secret.IsCurrent).ToArray(),
            [credential.CredentialId]);
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

    private static bool IsInternetCircuit(ClientInfoResource resource)
    {
        var type = Normalize(resource.TypeLabel);
        return type.Contains("internetcircuit", StringComparison.Ordinal)
            || type.Contains("internetprovider", StringComparison.Ordinal)
            || type.Contains("broadband", StringComparison.Ordinal)
            || type.Contains("modem", StringComparison.Ordinal)
            || type.Equals("isp", StringComparison.Ordinal);
    }

    private static ClientInfoOverviewField? IpAssignment(
        IReadOnlyList<ClientInfoResource> resources)
    {
        var hasBlockData = resources.Any(HasStaticBlockData);
        var values = resources.Select(resource =>
        {
            var assignment = FindValue(
                resource,
                ["ip_assignment_type", "ip assignment", "static allocation"]);
            if (string.IsNullOrWhiteSpace(assignment))
            {
                return HasStaticBlockData(resource) ? "Static block" : string.Empty;
            }

            return assignment.Contains("single", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : assignment;
        });
        var field = Aggregate(
            "IP assignment",
            values,
            resources.Select(resource => resource.Name));
        return field ?? (hasBlockData
            ? new ClientInfoOverviewField("IP assignment", "Static block")
            : null);
    }

    private static ClientInfoOverviewField? StaticIpCount(
        IReadOnlyList<ClientInfoResource> resources)
    {
        var blockResources = resources.Where(HasStaticBlockData).ToArray();
        if (blockResources.Length == 0)
        {
            return null;
        }

        return Aggregate(
                "Usable static IPs",
                blockResources.Select(ResolveStaticIpCount),
                blockResources.Select(resource => resource.Name))
            ?? new ClientInfoOverviewField("Usable static IPs", "Not entered");
    }

    private static string ResolveStaticIpCount(ClientInfoResource resource)
    {
        var enteredCount = FindValue(
            resource,
            ["usable_static_ip_count", "usable static ips", "static count"]);
        if (!string.IsNullOrWhiteSpace(enteredCount))
        {
            return enteredCount.Trim();
        }

        var addresses = ParseStaticIpAddresses(FindValue(
            resource,
            ["static_ip_addresses", "static ip list", "static ips"]));
        if (addresses.Count > 0)
        {
            var validAddressCount = addresses
                .Select(address => IPAddress.TryParse(address, out var parsed)
                    ? parsed.ToString()
                    : null)
                .Where(address => address is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            return validAddressCount > 0
                ? validAddressCount.ToString()
                : string.Empty;
        }

        var rangeStart = FindValue(
            resource,
            ["static_ip_range_start", "first usable static ip", "static range start"]);
        var rangeEnd = FindValue(
            resource,
            ["static_ip_range_end", "last usable static ip", "static range end"]);
        return TryCountIpv4Range(rangeStart, rangeEnd, out var rangeCount)
            ? rangeCount.ToString()
            : string.Empty;
    }

    private static ClientInfoOverviewField? StaticIpDetails(
        IReadOnlyList<ClientInfoResource> resources)
    {
        var blockResources = resources.Where(HasStaticBlockData).ToArray();
        if (blockResources.Length == 0)
        {
            return null;
        }

        return Aggregate(
                "Static IPs / range",
                blockResources.Select(ResolveStaticIpDetails),
                blockResources.Select(resource => resource.Name))
            ?? new ClientInfoOverviewField("Static IPs / range", "Not entered");
    }

    private static string ResolveStaticIpDetails(ClientInfoResource resource)
    {
        var addresses = ParseStaticIpAddresses(FindValue(
            resource,
            ["static_ip_addresses", "static ip list", "static ips"]));
        if (addresses.Count > 0)
        {
            return string.Join(Environment.NewLine, addresses);
        }

        var rangeStart = FindValue(
            resource,
            ["static_ip_range_start", "first usable static ip", "static range start"])
            .Trim();
        var rangeEnd = FindValue(
            resource,
            ["static_ip_range_end", "last usable static ip", "static range end"])
            .Trim();
        return (rangeStart, rangeEnd) switch
        {
            ({ Length: > 0 } start, { Length: > 0 } end) => $"{start} – {end}",
            ({ Length: > 0 } start, _) => start,
            (_, { Length: > 0 } end) => end,
            _ => string.Empty
        };
    }

    private static bool HasStaticBlockData(ClientInfoResource resource)
    {
        var assignment = FindValue(
            resource,
            ["ip_assignment_type", "ip assignment", "static allocation"]);
        if (!string.IsNullOrWhiteSpace(assignment))
        {
            return assignment.Contains("block", StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(FindValue(
                resource,
                ["usable_static_ip_count", "usable static ips", "static count"]))
            || !string.IsNullOrWhiteSpace(FindValue(
                resource,
                ["static_ip_addresses", "static ip list", "static ips"]))
            || !string.IsNullOrWhiteSpace(FindValue(
                resource,
                ["static_ip_range_start", "first usable static ip", "static range start"]))
            || !string.IsNullOrWhiteSpace(FindValue(
                resource,
                ["static_ip_range_end", "last usable static ip", "static range end"]));
    }

    private static IReadOnlyList<string> ParseStaticIpAddresses(string value) =>
        value.Split(
                ['\r', '\n', ',', ';'],
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Where(address => address.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool TryCountIpv4Range(
        string rangeStart,
        string rangeEnd,
        out long count)
    {
        count = 0;
        if (!IPAddress.TryParse(rangeStart.Trim(), out var start)
            || !IPAddress.TryParse(rangeEnd.Trim(), out var end))
        {
            return false;
        }

        var startBytes = start.GetAddressBytes();
        var endBytes = end.GetAddressBytes();
        if (startBytes.Length != 4 || endBytes.Length != 4)
        {
            return false;
        }

        var startValue = ToIpv4Number(startBytes);
        var endValue = ToIpv4Number(endBytes);
        if (endValue < startValue)
        {
            return false;
        }

        count = endValue - startValue + 1;
        return true;
    }

    private static long ToIpv4Number(IReadOnlyList<byte> bytes) =>
        ((long)bytes[0] << 24)
        | ((long)bytes[1] << 16)
        | ((long)bytes[2] << 8)
        | bytes[3];

    private static bool IsEmailSecurityResource(ClientInfoResource resource)
    {
        var identity = $"{resource.Name} {resource.TypeLabel} {resource.Provider}";
        return new[]
        {
            "email security",
            "mail security",
            "spam filter",
            "spam filtering",
            "email gateway",
            "mail gateway"
        }.Any(term => identity.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRemoteAccessResource(ClientInfoResource resource)
    {
        var identity = $"{resource.Name} {resource.TypeLabel} {resource.Provider} {resource.AddressOrUrl}";
        return new[]
        {
            "remote access",
            "rustdesk",
            "screenconnect",
            "connectwise control",
            "splashtop",
            "teamviewer"
        }.Any(term => identity.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMicrosoft365Resource(ClientInfoResource resource)
    {
        var identity = $"{resource.Name} {resource.TypeLabel} {resource.Provider} {resource.AddressOrUrl} "
            + FindValue(resource, ["tenant_instance", "tenant name", "tenant id"]);
        return new[]
        {
            "microsoft 365",
            "office 365",
            "m365",
            "onmicrosoft.com",
            "admin.microsoft.com"
        }.Any(term => identity.Contains(term, StringComparison.OrdinalIgnoreCase));
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
