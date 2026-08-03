namespace TechBench.Models;

public sealed record ClientInfoDemoSnapshotData(
    ClientInfoClientSummary Summary,
    ClientInfoSnapshot Snapshot,
    IReadOnlyList<EquipmentItem> Equipment);

public static class ClientInfoDemoData
{
    public const int ClientId = -1000;

    public static ClientInfoClientSummary Summary { get; } = new()
    {
        ClientId = ClientId,
        ClientName = "Demo Client",
        IsActive = true,
        ReviewStatus = "Example",
        CutoverState = "Local sample",
        IsLive = false,
        LocationCount = 2,
        PersonCount = 3,
        ResourceCount = 8,
        CredentialCount = 4,
        IsDemo = true
    };

    public static ClientInfoDemoSnapshotData Create()
    {
        var updated = new DateTime(2026, 8, 1, 14, 30, 0, DateTimeKind.Utc);
        var locations = new ClientInfoLocation[]
        {
            new()
            {
                LocationId = -1, ClientId = ClientId, LocalKey = "demo-main",
                Name = "Main Office", LocationType = "Office", Address1 = "123 Sample Street",
                City = "Philadelphia", StateProvince = "PA", PostalCode = "19103",
                MainPhone = "215-555-0100", TimeZoneId = "Eastern Standard Time",
                IsPrimary = true, IsActive = true, ReviewStatus = "Verified", UpdatedAtUtc = updated
            },
            new()
            {
                LocationId = -2, ClientId = ClientId, LocalKey = "demo-warehouse",
                Name = "Warehouse", LocationType = "Warehouse", Address1 = "450 Example Road",
                City = "King of Prussia", StateProvince = "PA", PostalCode = "19406",
                MainPhone = "610-555-0142", TimeZoneId = "Eastern Standard Time",
                IsActive = true, ReviewStatus = "Verified", UpdatedAtUtc = updated
            }
        };
        var people = new ClientInfoPerson[]
        {
            new()
            {
                PersonId = -1, ClientId = ClientId, LocationId = -1, LocationName = "Main Office",
                LocalKey = "demo-alex", DisplayName = "Alex Morgan", RoleDepartment = "Office Manager",
                Email = "alex.morgan@example.test", Phone = "215-555-0100 x101",
                MobilePhone = "215-555-0191", ContactType = "Primary Contact", IsPrimary = true,
                IsActive = true, ReviewStatus = "Verified", UpdatedAtUtc = updated
            },
            new()
            {
                PersonId = -2, ClientId = ClientId, LocationId = -1, LocationName = "Main Office",
                LocalKey = "demo-jordan", DisplayName = "Jordan Lee", RoleDepartment = "Accounting",
                Email = "jordan.lee@example.test", Phone = "215-555-0100 x114",
                MobilePhone = "215-555-0184", ContactType = "End User", IsActive = true,
                ReviewStatus = "Verified", UpdatedAtUtc = updated
            },
            new()
            {
                PersonId = -3, ClientId = ClientId, LocationId = -2, LocationName = "Warehouse",
                LocalKey = "demo-taylor", DisplayName = "Taylor Rivera", RoleDepartment = "Operations",
                Email = "taylor.rivera@example.test", Phone = "610-555-0142 x202",
                MobilePhone = "610-555-0177", ContactType = "Site Contact", IsActive = true,
                ReviewStatus = "Verified", UpdatedAtUtc = updated
            }
        };

        var resources = new[]
        {
            Resource(-101, ClientInfoResourceCategories.ServersInfrastructure, "Primary Hyper-V Host", "Hyper-V Host", "Dell", "hv-demo-01", "Main Office", -1,
                new Dictionary<string, string> { ["primary_ip"]="10.20.0.10", ["management_ip"]="10.20.0.11", ["role_purpose"]="Virtualization host", ["operating_system"]="Windows Server 2025", ["manufacturer_model"]="Dell PowerEdge R760", ["serial_number"]="DEMO-R760-01", ["additional_ips_subnet"]="10.20.0.0/24" }),
            Resource(-102, ClientInfoResourceCategories.ConnectionInternet, "Main WatchGuard", "WatchGuard Firewall", "WatchGuard", "https://10.20.0.1", "Main Office", -1,
                new Dictionary<string, string> { ["public_wan_ip"]="203.0.113.24", ["gateway"]="203.0.113.1", ["subnet_cidr"]="203.0.113.24/29", ["circuit_id"]="FIBER-DEMO-4821", ["device_model"]="Firebox M390", ["serial_number"]="WG-DEMO-390", ["firmware_version"]="Fireware 12.10.4", ["isp_provider"]="Example Fiber", ["support_phone"]="800-555-0140" }),
            Resource(-103, ClientInfoResourceCategories.Wifi, "Corporate Wireless", "Wireless Network / SSID", "Aruba", "https://aruba-central.example.test", "Main Office", -1,
                new Dictionary<string, string> { ["management_ip"]="10.20.0.20", ["ssid"]="DemoClient-Staff", ["vlan"]="20", ["wireless_security"]="WPA3 Enterprise", ["controller_name"]="Aruba Central", ["guest_ssid"]="DemoClient-Guest", ["coverage_notes"]="Office and conference rooms; warehouse uses a separate AP group." }),
            Resource(-104, ClientInfoResourceCategories.ApplicationsCloud, "Microsoft 365", "Microsoft 365 Tenant", "Microsoft", "https://admin.microsoft.com", "Main Office", -1,
                new Dictionary<string, string> { ["tenant_instance"]="democlient.onmicrosoft.com", ["hosting_type"]="Cloud / SaaS", ["primary_ip"]="198.51.100.12", ["admin_portal"]="https://admin.microsoft.com", ["version"]="Microsoft 365 Business Premium", ["support_contact"]="Cloud Services Team · 800-555-0115", ["renewal_date"]="2027-01-15" }),
            Resource(-105, ClientInfoResourceCategories.DomainsEmail, "Primary Domain", "Domain", "Example Registrar", "https://example.test", "Main Office", -1,
                new Dictionary<string, string> { ["domain_name"]="example.test", ["registrar"]="Example Registrar", ["dns_provider"]="Cloudflare", ["mail_provider"]="Microsoft 365", ["tenant_name"]="democlient.onmicrosoft.com", ["expiration_date"]="2027-06-30" }),
            Resource(-106, ClientInfoResourceCategories.BackupSecurity, "Veeam Backup", "Veeam Backup", "Veeam", "https://backup-console.example.test", "Main Office", -1,
                new Dictionary<string, string> { ["product_service"]="Veeam Backup & Replication", ["protected_scope"]="All servers and Microsoft 365", ["console_url"]="https://backup-console.example.test", ["retention"]="30 daily · 12 monthly", ["backup_schedule"]="Nightly at 10:00 PM", ["last_restore_test"]="2026-07-15", ["renewal_date"]="2027-03-01" }),
            Resource(-107, ClientInfoResourceCategories.VendorsServices, "Example Fiber Support", "Internet Provider", "Example Fiber", "https://support.example.test", "Main Office", -1,
                new Dictionary<string, string> { ["account_number"]="ACCT-DEMO-1048", ["primary_contact"]="Business Support", ["support_phone"]="800-555-0140", ["support_email"]="support@example.test", ["portal_url"]="https://support.example.test", ["contract_expiration"]="2028-01-31" }),
            Resource(-108, ClientInfoResourceCategories.NeedsSorting, "Legacy Monitoring Note", "Unknown", "Legacy Vendor", "https://monitoring.example.test", "Warehouse", -2, new Dictionary<string, string>())
        };

        var credentials = new ClientInfoCredential[]
        {
            Credential(-201, -102, "WatchGuard Admin", "Firewall", "demo-admin", "https://10.20.0.1", updated),
            Credential(-202, -103, "Aruba Central", "Wi-Fi", "network-admin@example.test", "https://aruba-central.example.test", updated),
            Credential(-203, -104, "Microsoft 365 Global Admin", "Cloud", "m365-admin@example.test", "https://admin.microsoft.com", updated),
            Credential(-204, -106, "Veeam Console", "Backup", "backup-admin", "https://backup-console.example.test", updated)
        };
        var facts = new ClientInfoFact[]
        {
            Fact(-301, "Business Hours", "Monday-Friday, 8:00 AM-5:00 PM", 10, updated),
            Fact(-302, "After-hours Contact", "Alex Morgan · 215-555-0191", 20, updated),
            Fact(-303, "Internet Failover", "5G modem in the main network closet", 30, updated),
            Fact(-304, "Special Instructions", "Call the office manager before restarting production systems.", 40, updated)
        };
        var equipment = new EquipmentItem[]
        {
            new() { EquipmentId=-401, AssetTag="DEMO-1001", DeviceType="Laptop", Name="Alex's Laptop", SerialNumber="DEMO-LT-1001", Manufacturer="Dell", Model="Latitude 7450", IpAddress="10.20.20.31", AnyDeskNumber="123 456 789", ClientId=ClientId, ClientName="Demo Client", ClientUserDisplayName="Alex Morgan", LocationName="Main Office", WorkflowStage=EquipmentWorkflowStages.Deployed, CreatedAtUtc=updated, UpdatedAtUtc=updated },
            new() { EquipmentId=-402, AssetTag="DEMO-1002", DeviceType="Desktop", Name="Accounting Workstation", SerialNumber="DEMO-PC-1002", Manufacturer="HP", Model="EliteDesk 800", IpAddress="10.20.20.42", AnyDeskNumber="987 654 321", ClientId=ClientId, ClientName="Demo Client", ClientUserDisplayName="Jordan Lee", LocationName="Main Office", WorkflowStage=EquipmentWorkflowStages.Deployed, CreatedAtUtc=updated, UpdatedAtUtc=updated },
            new() { EquipmentId=-403, AssetTag="DEMO-1003", DeviceType="Printer", Name="Warehouse MFP", SerialNumber="DEMO-MFP-1003", Manufacturer="Brother", Model="MFC-L8900CDW", IpAddress="10.20.30.15", ClientId=ClientId, ClientName="Demo Client", ClientUserDisplayName="Taylor Rivera", LocationName="Warehouse", WorkflowStage=EquipmentWorkflowStages.Deployed, CreatedAtUtc=updated, UpdatedAtUtc=updated }
        };

        return new ClientInfoDemoSnapshotData(
            Summary,
            new ClientInfoSnapshot
            {
                Profile = new ClientInfoProfile
                {
                    ClientId = ClientId, ClientName = "Demo Client", IsActive = true,
                    WhdContactName = "Alex Morgan", WhdContactEmail = "alex.morgan@example.test",
                    WhdPhone = "215-555-0100 x101",
                    WhdAddress = "123 Sample Street, Philadelphia, PA 19103",
                    Summary = "Fictional example client showing how a complete Client Information profile can look. All names, addresses, credentials, and systems on this page are sample data.",
                    ReviewStatus = "Verified", CutoverState = "Local sample", UpdatedAtUtc = updated
                },
                Locations = locations,
                People = people,
                Resources = resources,
                Credentials = credentials,
                Facts = facts
            },
            equipment);
    }

    private static ClientInfoResource Resource(long id, string category, string name, string type, string provider, string address, string location, long locationId, IReadOnlyDictionary<string, string> values)
    {
        var fields = ClientInfoResourceFieldDefinitions.ForEditorCategory(category)
            .Select((definition, index) => new ClientInfoResourceField
            {
                ResourceFieldId = id * 100 - index,
                ResourceId = id,
                FieldKey = definition.FieldKey,
                FieldLabel = definition.FieldLabel,
                ValueText = values.TryGetValue(definition.FieldKey, out var value) ? value : $"Example {definition.FieldLabel}",
                ValueType = definition.ValueType,
                SortOrder = definition.SortOrder
            })
            .ToArray();
        return new ClientInfoResource
        {
            ResourceId = id, ClientId = ClientId, LocationId = locationId, LocationName = location,
            LocalKey = $"demo-resource-{Math.Abs(id)}", ResourceType = ClientInfoResourceCategories.Encode(category, type),
            Name = name, Provider = provider, AddressOrUrl = address, Status = "Operational",
            Notes = "Fictional example record for demonstration only.", ReviewStatus = "Verified",
            IsActive = true, Fields = fields
        };
    }

    private static ClientInfoCredential Credential(long id, long resourceId, string name, string category, string username, string url, DateTime updated) => new()
    {
        CredentialId = id, ClientId = ClientId, ResourceId = resourceId,
        LocalKey = $"demo-credential-{Math.Abs(id)}", Name = name, Category = category,
        Username = username, LoginUrl = url, Notes = "Demo credential metadata; no real secret is stored.",
        ReviewStatus = "Verified", IsActive = true, UpdatedAtUtc = updated, SecretCount = 1,
        Secrets = [new ClientInfoSecretSummary { SecretId = id * 10, CredentialId = id, SecretType = "Password", SecretLabel = "Password", IsCurrent = true, UpdatedAtUtc = updated }]
    };

    private static ClientInfoFact Fact(long id, string label, string value, int order, DateTime updated) => new()
    {
        FactId = id, ClientId = ClientId, LocalKey = $"demo-fact-{Math.Abs(id)}",
        SectionName = "Other Information", FieldLabel = label, ValueText = value,
        ValueType = "Text", ReviewStatus = "Verified", SortOrder = order,
        IsActive = true, UpdatedAtUtc = updated
    };
}
