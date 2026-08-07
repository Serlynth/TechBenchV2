using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ExcelDataReader;
using TechBench.Models;

namespace TechBench.Services;

public sealed class ClientInfoWorkbookService
{
    public const string TemplateVersion = "TB-CI-11";
    public const string FireDrillTransferTemplateVersion = "TB-CI-10";
    public const string PrimaryAccessTemplateVersion = "TB-CI-9";
    public const string InlineCredentialsTemplateVersion = "TB-CI-8";
    public const string PreviousTemplateVersion = "TB-CI-7";
    public const string ResourceFieldsTemplateVersion = "TB-CI-6";
    public const string WifiTemplateVersion = "TB-CI-5";
    public const string ConnectionTemplateVersion = "TB-CI-4";
    public const string CategorizedTemplateVersion = "TB-CI-3";
    public const string FriendlyTemplateVersion = "TB-CI-2";
    public const string LegacyTemplateVersion = "TB-CI-1";

    private static readonly string[] ReviewStatuses =
    [
        "Unverified", "Verified", "AcceptedUnverified", "NeedsReview", "Rejected"
    ];

    private static readonly string[] LocationTypeOptions =
    [
        "Headquarters", "Office", "Branch Office", "Warehouse", "Remote Site", "Other"
    ];

    private static readonly string[] ContactTypeOptions =
    [
        "Primary Contact", "Site Contact", "Technical Contact", "Billing Contact", "End User", "Other"
    ];

    private static readonly string[] EquipmentTypeOptions =
    [
        "Desktop", "Laptop", "Server", "Switch", "Firewall", "Access Point", "Printer", "UPS", "Phone", "Other"
    ];

    private static readonly string[] SecretTypeOptions =
    [
        "Password", "API Key", "Token", "PIN", "Recovery Code", "Other"
    ];

    private static readonly string[] LegacyFireDrillHeaders =
    [
        "Firebox IP", "Status", "Admin", "csriadmin",
        "*if enabled -Firebox-DB\\csri", "Authpoint User", "sslvpnpassword",
        "AD Auth User", "AD Password", "RustPW"
    ];

    public void CreateTemplate(
        string path,
        int clientId,
        string clientName,
        IReadOnlyList<string>? fireDrillFieldLabels = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);

        using var document = SpreadsheetDocument.Create(
            path,
            SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = CreateStylesheet();
        stylesPart.Stylesheet.Save();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        uint sheetId = 1;

        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Start Here",
            [
                ["TechBench Client Info Migration Workbook", ""],
                ["What to do", "Copy cleaned information into the matching category tabs. The FireDrill tab uses this client's current FireDrill column names; copy existing FireDrill values there instead of renaming them or entering the same value twice. Use the category-specific tabs for new information and object-linked logins. You may add optional columns whose headings begin with 'Custom:' for unusual client-specific details."],
                ["Review each row", "Choose Verified, Keep as-is, Needs review, or Do not import. A workbook cannot be approved while a populated row is blank or still Needs review."],
                ["Passwords", "Enter the primary username and password beside each application, security product, server, service, or other system. Firewall rows provide separate Status and Admin logins. For Microsoft 365 users, choose whether the AD login is reused; when it is not, enter the separate Microsoft 365 username and password. Use Passwords for additional logins or credentials that are not tied to one row. Imported credentials stay linked to their item and also appear in the master Passwords section. All secrets are encrypted when imported and are never written to import logs."],
                ["Template Version", TemplateVersion],
                ["Workbook ID", Guid.NewGuid().ToString("D")],
                ["Internal Client ID", clientId.ToString(CultureInfo.InvariantCulture)],
                ["Client Name", clientName],
                ["Summary", ""],
                ["Review Status", "Verified"],
                ["Important", "Do not change the internal client ID or reuse this workbook for another client."],
                ["Workbook security", "Passwords are visible as plain text in this workbook until import. Keep the file secured and remove the completed copy after Client Information is promoted."],
                ["Required fields", "Amber headers are required for every row you use: Name (or Item on Other Info) and Review Status. Other fields are optional. If Microsoft 365 does not reuse AD, enter its separate username and password."]
            ],
            headerRow: 0,
            columnWidths: [28, 78],
            formatFirstRowAsTitle: true);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Locations",
            [
                ["Name", "Location Type", "Address 1", "Address 2", "City",
                 "State/Province", "Postal Code", "Main Phone", "Time Zone ID",
                 "Is Primary", "Review Status"]
            ],
            columnWidths: [26, 18, 28, 22, 18, 16, 16, 18, 18, 12, 22]);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Users",
            [
                ["Name", "Role/Department", "AD Username", "AD Password", "Email",
                 "Has Microsoft 365", "Microsoft 365 License",
                 "Microsoft 365 Uses AD Login", "Microsoft 365 Username",
                 "Microsoft 365 Password", "PC Name",
                 "Phone", "Mobile Phone", "Location", "Contact Type",
                 "Is Primary", "Review Status"]
            ],
            columnWidths: [26, 24, 24, 28, 32, 18, 26, 24, 30, 30, 20, 18, 18, 22, 20, 12, 22]);
        var fireDrillHeaders = BuildFireDrillHeaders(fireDrillFieldLabels);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "FireDrill",
            [fireDrillHeaders],
            columnWidths: fireDrillHeaders
                .Select(header => header.Equals(
                    "Review Status",
                    StringComparison.OrdinalIgnoreCase)
                    ? 22D
                    : 30D)
                .ToArray());
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Servers & Infrastructure",
            [ResourceHeaders(ClientInfoResourceCategories.ServersInfrastructure)],
            columnWidths: ResourceColumnWidths(
                ClientInfoResourceCategories.ServersInfrastructure),
            resourceCategory: ClientInfoResourceCategories.ServersInfrastructure);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Connection & Internet",
            [ResourceHeaders(ClientInfoResourceCategories.ConnectionInternet)],
            columnWidths: ResourceColumnWidths(
                ClientInfoResourceCategories.ConnectionInternet),
            resourceCategory: ClientInfoResourceCategories.ConnectionInternet);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Wi-Fi",
            [ResourceHeaders(ClientInfoResourceCategories.Wifi)],
            columnWidths: ResourceColumnWidths(ClientInfoResourceCategories.Wifi),
            resourceCategory: ClientInfoResourceCategories.Wifi);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Applications & Cloud",
            [ResourceHeaders(ClientInfoResourceCategories.ApplicationsCloud)],
            columnWidths: ResourceColumnWidths(
                ClientInfoResourceCategories.ApplicationsCloud),
            resourceCategory: ClientInfoResourceCategories.ApplicationsCloud);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Domains & Email",
            [ResourceHeaders(ClientInfoResourceCategories.DomainsEmail)],
            columnWidths: ResourceColumnWidths(
                ClientInfoResourceCategories.DomainsEmail),
            resourceCategory: ClientInfoResourceCategories.DomainsEmail);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Backup",
            [ResourceHeaders(ClientInfoResourceCategories.Backup)],
            columnWidths: ResourceColumnWidths(ClientInfoResourceCategories.Backup),
            resourceCategory: ClientInfoResourceCategories.Backup);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Security",
            [ResourceHeaders(ClientInfoResourceCategories.Security)],
            columnWidths: ResourceColumnWidths(ClientInfoResourceCategories.Security),
            resourceCategory: ClientInfoResourceCategories.Security);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Vendors & Services",
            [ResourceHeaders(ClientInfoResourceCategories.VendorsServices)],
            columnWidths: ResourceColumnWidths(
                ClientInfoResourceCategories.VendorsServices),
            resourceCategory: ClientInfoResourceCategories.VendorsServices);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Equipment",
            [
                ["Device Type", "Name", "Manufacturer", "Model", "Serial Number",
                 "Part Number", "Asset Tag", "IP Address", "Location", "Notes",
                 "Review Status"]
            ],
            columnWidths: [20, 28, 22, 22, 22, 22, 18, 18, 22, 40, 22]);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Passwords",
            [
                ["Name", "Category", "Username", "Password / Secret", "Login URL",
                 "Related System", "Related User", "Notes", "Secret Type", "Secret Label",
                 "Review Status"]
            ],
            columnWidths: [26, 20, 26, 30, 34, 26, 26, 40, 18, 20, 22]);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Other Info",
            [
                ["Section", "Item", "Value / Notes", "Review Status"]
            ],
            columnWidths: [22, 28, 55, 22]);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Needs Sorting",
            [ResourceHeaders(ClientInfoResourceCategories.NeedsSorting)],
            columnWidths: ResourceColumnWidths(
                ClientInfoResourceCategories.NeedsSorting),
            resourceCategory: ClientInfoResourceCategories.NeedsSorting);
        workbookPart.Workbook.Save();
    }

    public ClientInfoWorkbookPackage Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var tables = ReadTables(path);
        var metadataSheet = tables.ContainsKey("Start Here")
            ? "Start Here"
            : "Client Info";
        var info = ReadKeyValues(RequireSheet(tables, metadataSheet));
        var version = GetRequired(info, "Template Version");
        if (!string.Equals(version, TemplateVersion, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                version,
                FireDrillTransferTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                version,
                PrimaryAccessTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                version,
                InlineCredentialsTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                version,
                PreviousTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                version,
                ResourceFieldsTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                version,
                WifiTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                version,
                ConnectionTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                version,
                CategorizedTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                version,
                FriendlyTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                version,
                LegacyTemplateVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Template version '{version}' is not supported. Expected {TemplateVersion}, {FireDrillTransferTemplateVersion}, {PrimaryAccessTemplateVersion}, {InlineCredentialsTemplateVersion}, {PreviousTemplateVersion}, {ResourceFieldsTemplateVersion}, {WifiTemplateVersion}, {ConnectionTemplateVersion}, {CategorizedTemplateVersion}, {FriendlyTemplateVersion}, or {LegacyTemplateVersion}.");
        }

        if (!Guid.TryParse(GetRequired(info, "Workbook ID"), out var workbookId))
        {
            throw new InvalidDataException("Workbook ID must be a valid GUID.");
        }

        if (!int.TryParse(
                GetRequired(info, "Internal Client ID"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var clientId)
            || clientId <= 0)
        {
            throw new InvalidDataException(
                "Internal Client ID must be a positive TechBench client ID.");
        }

        var clientName = GetRequired(info, "Client Name");
        var records = new List<ClientInfoImportRecord>();
        var secrets = new List<ClientInfoImportSecret>();
        var summary = Get(info, "Summary");
        records.Add(new ClientInfoImportRecord(
            "Profile",
            "profile",
            null,
            JsonSerializer.Serialize(new { summary }),
            metadataSheet,
            metadataSheet.Equals("Start Here", StringComparison.OrdinalIgnoreCase)
                ? 9
                : 6,
            NormalizeReviewStatus(Get(info, "Review Status"))));

        if (string.Equals(
                version,
                TemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                version,
                FireDrillTransferTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                version,
                PrimaryAccessTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                version,
                InlineCredentialsTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                version,
                PreviousTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                version,
                ResourceFieldsTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                version,
                WifiTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                version,
                ConnectionTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                version,
                CategorizedTemplateVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            var hasInlineCredentials = string.Equals(
                version,
                TemplateVersion,
                StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    version,
                    FireDrillTransferTemplateVersion,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    version,
                    PrimaryAccessTemplateVersion,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    version,
                    InlineCredentialsTemplateVersion,
                    StringComparison.OrdinalIgnoreCase);
            var hasSeparateMicrosoft365Credentials = string.Equals(
                version,
                TemplateVersion,
                StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    version,
                    FireDrillTransferTemplateVersion,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    version,
                    PrimaryAccessTemplateVersion,
                    StringComparison.OrdinalIgnoreCase);
            var hasSplitProtectionSheets = hasInlineCredentials;
            var hasWifiSheet = string.Equals(
                    version,
                    TemplateVersion,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    version,
                    FireDrillTransferTemplateVersion,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    version,
                    PrimaryAccessTemplateVersion,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    version,
                    InlineCredentialsTemplateVersion,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    version,
                    PreviousTemplateVersion,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    version,
                    ResourceFieldsTemplateVersion,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    version,
                    WifiTemplateVersion,
                    StringComparison.OrdinalIgnoreCase);
            var connectionSheetName = string.Equals(
                version,
                CategorizedTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
                ? "Network & Internet"
                : "Connection & Internet";
            var locationKeys = ParseSimpleLocations(
                GetSheet(tables, "Locations"),
                records);
            var userKeys = ParseSimplePeople(
                GetUsersSheet(tables),
                records,
                locationKeys,
                hasInlineCredentials ? secrets : null,
                hasSeparateMicrosoft365Credentials);
            var resourceKeys = new Dictionary<string, string?>(
                StringComparer.OrdinalIgnoreCase);
            ParseSimpleResources(
                GetSheet(tables, "Servers & Infrastructure"),
                records,
                locationKeys,
                resourceKeys,
                ClientInfoResourceCategories.ServersInfrastructure,
                "Servers & Infrastructure",
                "infrastructure",
                hasInlineCredentials ? secrets : null);
            ParseSimpleResources(
                GetSheet(tables, connectionSheetName),
                records,
                locationKeys,
                resourceKeys,
                ClientInfoResourceCategories.ConnectionInternet,
                connectionSheetName,
                "network",
                hasInlineCredentials ? secrets : null);
            if (hasWifiSheet)
            {
                ParseSimpleResources(
                    GetSheet(tables, "Wi-Fi"),
                    records,
                    locationKeys,
                    resourceKeys,
                    ClientInfoResourceCategories.Wifi,
                    "Wi-Fi",
                    "wifi",
                    hasInlineCredentials ? secrets : null);
            }
            ParseSimpleResources(
                GetSheet(tables, "Applications & Cloud"),
                records,
                locationKeys,
                resourceKeys,
                ClientInfoResourceCategories.ApplicationsCloud,
                "Applications & Cloud",
                "application",
                hasInlineCredentials ? secrets : null);
            ParseSimpleResources(
                GetSheet(tables, "Domains & Email"),
                records,
                locationKeys,
                resourceKeys,
                ClientInfoResourceCategories.DomainsEmail,
                "Domains & Email",
                "domain",
                hasInlineCredentials ? secrets : null);
            if (hasSplitProtectionSheets)
            {
                ParseSimpleResources(
                    GetSheet(tables, "Backup"),
                    records,
                    locationKeys,
                    resourceKeys,
                    ClientInfoResourceCategories.Backup,
                    "Backup",
                    "backup",
                    secrets);
                ParseSimpleResources(
                    GetSheet(tables, "Security"),
                    records,
                    locationKeys,
                    resourceKeys,
                    ClientInfoResourceCategories.Security,
                    "Security",
                    "security",
                    secrets);
            }
            else
            {
                ParseSimpleResources(
                    GetSheet(tables, "Backup & Security"),
                    records,
                    locationKeys,
                    resourceKeys,
                    ClientInfoResourceCategories.LegacyBackupSecurity,
                    "Backup & Security",
                    "security");
            }
            ParseSimpleResources(
                GetSheet(tables, "Vendors & Services"),
                records,
                locationKeys,
                resourceKeys,
                ClientInfoResourceCategories.VendorsServices,
                "Vendors & Services",
                "vendor",
                hasInlineCredentials ? secrets : null);
            if (hasInlineCredentials)
            {
                ParseSimpleResources(
                    GetSheet(tables, "Needs Sorting"),
                    records,
                    locationKeys,
                    resourceKeys,
                    ClientInfoResourceCategories.NeedsSorting,
                    "Needs Sorting",
                    "needs-sorting",
                    secrets);
            }
            ParseSimpleEquipment(GetSheet(tables, "Equipment"), records);
            ParseSimpleCredentials(
                GetSheet(tables, "Passwords"),
                records,
                secrets,
                resourceKeys,
                userKeys);
            ParseSimpleFacts(GetSheet(tables, "Other Info"), records);
            if (string.Equals(
                    version,
                    TemplateVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                ParseFireDrillCredentials(
                    GetSheet(tables, "FireDrill"),
                    records,
                    secrets);
            }
        }
        else if (string.Equals(
                     version,
                     FriendlyTemplateVersion,
                     StringComparison.OrdinalIgnoreCase))
        {
            var locationKeys = ParseSimpleLocations(
                GetSheet(tables, "Locations"),
                records);
            var userKeys = ParseSimplePeople(
                GetUsersSheet(tables),
                records,
                locationKeys);
            var resourceKeys = ParseSimpleResources(
                GetSheet(tables, "Systems & Services"),
                records,
                locationKeys);
            ParseSimpleEquipment(GetSheet(tables, "Equipment"), records);
            ParseSimpleCredentials(
                GetSheet(tables, "Passwords"),
                records,
                secrets,
                resourceKeys,
                userKeys);
            ParseSimpleFacts(GetSheet(tables, "Other Info"), records);
        }
        else
        {
            ParsePeopleAndLocations(GetSheet(tables, "People & Locations"), records);
            ParseResources(GetSheet(tables, "Systems & Services"), records);
            ParseEquipment(GetSheet(tables, "Equipment"), records);
            ParseCredentials(GetSheet(tables, "Credentials"), records, secrets);
            ParseFacts(GetSheet(tables, "Other Info & Notes"), records);
        }

        using var hashStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        var contentHash = SHA256.HashData(hashStream);
        return new ClientInfoWorkbookPackage
        {
            TemplateVersion = version,
            WorkbookId = workbookId,
            ClientId = clientId,
            ClientName = clientName,
            SourcePath = path,
            SourceModifiedAtUtc = File.GetLastWriteTimeUtc(path),
            ContentSha256 = contentHash,
            Records = records,
            Secrets = secrets
        };
    }

    private static Dictionary<string, List<string[]>> ReadTables(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var result = new Dictionary<string, List<string[]>>(
            StringComparer.OrdinalIgnoreCase);
        do
        {
            var rows = new List<string[]>();
            while (reader.Read())
            {
                var values = new string[reader.FieldCount];
                for (var column = 0; column < values.Length; column++)
                {
                    values[column] = Format(reader.GetValue(column));
                }

                rows.Add(values);
            }

            result[reader.Name] = rows;
        }
        while (reader.NextResult());
        return result;
    }

    private static IReadOnlyDictionary<string, string?> ParseSimpleLocations(
        IReadOnlyList<string[]> rows,
        ICollection<ClientInfoImportRecord> records)
    {
        var keys = new Dictionary<string, string?>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in DataRows(rows))
        {
            var name = Value(row.Values, row.Headers, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var localKey = LocalKey(string.Empty, "location", row.RowNumber);
            AddFriendlyLookup(keys, name, localKey);
            records.Add(new ClientInfoImportRecord(
                "Location",
                localKey,
                null,
                JsonSerializer.Serialize(new
                {
                    name,
                    locationType = Value(row.Values, row.Headers, "Location Type"),
                    address1 = Value(row.Values, row.Headers, "Address 1"),
                    address2 = Value(row.Values, row.Headers, "Address 2"),
                    city = Value(row.Values, row.Headers, "City"),
                    stateProvince = Value(row.Values, row.Headers, "State/Province"),
                    postalCode = Value(row.Values, row.Headers, "Postal Code"),
                    mainPhone = Value(row.Values, row.Headers, "Main Phone"),
                    timeZoneId = Value(row.Values, row.Headers, "Time Zone ID"),
                    isPrimary = ParseBoolean(Value(row.Values, row.Headers, "Is Primary")),
                    isActive = true
                }),
                "Locations",
                row.RowNumber,
                NormalizeReviewStatus(Value(row.Values, row.Headers, "Review Status"))));
        }

        return keys;
    }

    private static IReadOnlyDictionary<string, string?> ParseSimplePeople(
        IReadOnlyList<string[]> rows,
        ICollection<ClientInfoImportRecord> records,
        IReadOnlyDictionary<string, string?> locationKeys,
        ICollection<ClientInfoImportSecret>? secrets = null,
        bool parseSeparateMicrosoft365Credentials = false)
    {
        var keys = new Dictionary<string, string?>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in DataRows(rows))
        {
            var name = Value(row.Values, row.Headers, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var localKey = LocalKey(string.Empty, "person", row.RowNumber);
            AddFriendlyLookup(keys, name, localKey);
            var locationName = Value(row.Values, row.Headers, "Location");
            var reviewStatus = NormalizeReviewStatus(
                Value(row.Values, row.Headers, "Review Status"));
            records.Add(new ClientInfoImportRecord(
                "Person",
                localKey,
                ResolveFriendlyLookup(
                    locationKeys,
                    locationName,
                    "location",
                    "Users",
                    row.RowNumber),
                JsonSerializer.Serialize(new
                {
                    displayName = name,
                    roleDepartment = Value(row.Values, row.Headers, "Role/Department"),
                    adUsername = Value(row.Values, row.Headers, "AD Username"),
                    email = Value(row.Values, row.Headers, "Email"),
                    hasMicrosoft365 = ParseBoolean(Value(
                        row.Values,
                        row.Headers,
                        "Has Microsoft 365")),
                    microsoft365License = Value(
                        row.Values,
                        row.Headers,
                        "Microsoft 365 License"),
                    pcName = Value(row.Values, row.Headers, "PC Name"),
                    phone = Value(row.Values, row.Headers, "Phone"),
                    mobilePhone = Value(row.Values, row.Headers, "Mobile Phone"),
                    contactType = Value(row.Values, row.Headers, "Contact Type"),
                    isPrimary = ParseBoolean(Value(row.Values, row.Headers, "Is Primary")),
                    isActive = true
                }),
                "Users",
                row.RowNumber,
                reviewStatus));
            if (secrets is not null)
            {
                ParseInlineUserCredential(
                    row.Values,
                    row.Headers,
                    row.RowNumber,
                    name,
                    localKey,
                    reviewStatus,
                    records,
                    secrets);
                if (parseSeparateMicrosoft365Credentials)
                {
                    ParseInlineMicrosoft365Credential(
                        row.Values,
                        row.Headers,
                        row.RowNumber,
                        name,
                        localKey,
                        reviewStatus,
                        records,
                        secrets);
                }
            }
        }

        return keys;
    }

    private static void ParseInlineUserCredential(
        string[] values,
        string[] headers,
        int rowNumber,
        string userName,
        string personLocalKey,
        string reviewStatus,
        ICollection<ClientInfoImportRecord> records,
        ICollection<ClientInfoImportSecret> secrets)
    {
        var username = Value(values, headers, "AD Username");
        var password = Value(
            values,
            headers,
            "AD Password",
            preserveWhitespace: true);
        if (string.IsNullOrWhiteSpace(username)
            && string.IsNullOrEmpty(password))
        {
            return;
        }

        var credentialLocalKey = $"user-ad-credential-{rowNumber}";
        records.Add(new ClientInfoImportRecord(
            "Credential",
            credentialLocalKey,
            null,
            JsonSerializer.Serialize(new
            {
                resourceKey = (string?)null,
                personKey = personLocalKey,
                name = $"{userName} AD account",
                category = "Active Directory User",
                username,
                loginUrl = "",
                notes = "Active Directory sign-in for this client user."
            }),
            "Users",
            rowNumber,
            reviewStatus));
        if (!string.IsNullOrEmpty(password))
        {
            secrets.Add(new ClientInfoImportSecret(
                credentialLocalKey,
                "Password",
                "AD password",
                password));
        }
    }

    private static void ParseInlineMicrosoft365Credential(
        string[] values,
        string[] headers,
        int rowNumber,
        string userName,
        string personLocalKey,
        string reviewStatus,
        ICollection<ClientInfoImportRecord> records,
        ICollection<ClientInfoImportSecret> secrets)
    {
        var hasMicrosoft365 = ParseBoolean(Value(
            values,
            headers,
            "Has Microsoft 365"));
        var usesAdLogin = ParseBoolean(
            Value(values, headers, "Microsoft 365 Uses AD Login"),
            fallback: true);
        if (!hasMicrosoft365 || usesAdLogin)
        {
            return;
        }

        var username = Value(values, headers, "Microsoft 365 Username");
        if (string.IsNullOrWhiteSpace(username))
        {
            username = Value(values, headers, "Email");
        }

        var password = Value(
            values,
            headers,
            "Microsoft 365 Password",
            preserveWhitespace: true);
        var credentialLocalKey = $"user-m365-credential-{rowNumber}";
        records.Add(new ClientInfoImportRecord(
            "Credential",
            credentialLocalKey,
            null,
            JsonSerializer.Serialize(new
            {
                resourceKey = (string?)null,
                personKey = personLocalKey,
                name = $"{userName} Microsoft 365 account",
                category = "Microsoft 365 User",
                username,
                loginUrl = "https://www.office.com",
                notes = "Separate Microsoft 365 sign-in for this client user."
            }),
            "Users",
            rowNumber,
            reviewStatus));
        if (!string.IsNullOrEmpty(password))
        {
            secrets.Add(new ClientInfoImportSecret(
                credentialLocalKey,
                "Password",
                "Microsoft 365 password",
                password));
        }
    }

    private static IReadOnlyList<string[]> GetUsersSheet(
        IReadOnlyDictionary<string, List<string[]>> tables)
    {
        var users = GetSheet(tables, "Users");
        return users.Count > 0 ? users : GetSheet(tables, "People");
    }

    private static IReadOnlyDictionary<string, string?> ParseSimpleResources(
        IReadOnlyList<string[]> rows,
        ICollection<ClientInfoImportRecord> records,
        IReadOnlyDictionary<string, string?> locationKeys)
    {
        var keys = new Dictionary<string, string?>(
            StringComparer.OrdinalIgnoreCase);
        ParseSimpleResources(
            rows,
            records,
            locationKeys,
            keys,
            category: null,
            sourceSheet: "Systems & Services",
            keyPrefix: "resource");
        return keys;
    }

    private static void ParseSimpleResources(
        IReadOnlyList<string[]> rows,
        ICollection<ClientInfoImportRecord> records,
        IReadOnlyDictionary<string, string?> locationKeys,
        IDictionary<string, string?> keys,
        string? category,
        string sourceSheet,
        string keyPrefix,
        ICollection<ClientInfoImportSecret>? secrets = null)
    {
        foreach (var row in DataRows(rows))
        {
            var name = Value(row.Values, row.Headers, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var localKey = LocalKey(string.Empty, keyPrefix, row.RowNumber);
            AddFriendlyLookup(keys, name, localKey);
            var locationName = Value(row.Values, row.Headers, "Location");
            var type = Value(row.Values, row.Headers, "Type");
            var reviewStatus = NormalizeReviewStatus(
                Value(row.Values, row.Headers, "Review Status"));
            var addressLabel = ClientInfoResourceFieldDefinitions
                .AddressLabelForCategory(category);
            var addressOrUrl = ValueAny(
                row.Values,
                row.Headers,
                addressLabel,
                "Address/URL",
                "Address / URL",
                "Hostname / URL",
                "Controller / URL",
                "URL");
            records.Add(new ClientInfoImportRecord(
                "Resource",
                localKey,
                null,
                JsonSerializer.Serialize(new
                {
                    locationKey = ResolveFriendlyLookup(
                        locationKeys,
                        locationName,
                        "location",
                        sourceSheet,
                        row.RowNumber),
                    resourceType = category is null
                        ? type
                        : ClientInfoResourceCategories.Encode(category, type),
                    name,
                    provider = Value(row.Values, row.Headers, "Provider"),
                    addressOrUrl,
                    status = Value(row.Values, row.Headers, "Status"),
                    notes = Value(
                        row.Values,
                        row.Headers,
                        "Notes",
                        preserveWhitespace: true),
                    isActive = true
                }),
                sourceSheet,
                row.RowNumber,
                reviewStatus));
            if (secrets is not null)
            {
                ParseInlineResourceCredentials(
                    row.Values,
                    row.Headers,
                    row.RowNumber,
                    name,
                    localKey,
                    category ?? ClientInfoResourceCategories.NeedsSorting,
                    addressOrUrl,
                    sourceSheet,
                    keyPrefix,
                    reviewStatus,
                    records,
                    secrets);
            }
            ParseSimpleResourceFields(
                row.Values,
                row.Headers,
                row.RowNumber,
                records,
                category,
                sourceSheet,
                keyPrefix,
                localKey,
                reviewStatus);
        }
    }

    private static void ParseInlineResourceCredentials(
        string[] values,
        string[] headers,
        int rowNumber,
        string resourceName,
        string resourceLocalKey,
        string category,
        string loginUrl,
        string sourceSheet,
        string keyPrefix,
        string reviewStatus,
        ICollection<ClientInfoImportRecord> records,
        ICollection<ClientInfoImportSecret> secrets)
    {
        if (category.Equals(
                ClientInfoResourceCategories.ConnectionInternet,
                StringComparison.OrdinalIgnoreCase))
        {
            ParseInlineResourceCredential(
                values,
                headers,
                rowNumber,
                resourceName,
                resourceLocalKey,
                category,
                loginUrl,
                sourceSheet,
                keyPrefix,
                reviewStatus,
                "Status Username",
                "Status Password",
                "",
                $"{resourceName} status",
                "status",
                records,
                secrets);
            ParseInlineResourceCredential(
                values,
                headers,
                rowNumber,
                resourceName,
                resourceLocalKey,
                category,
                loginUrl,
                sourceSheet,
                keyPrefix,
                reviewStatus,
                "Admin Username",
                "Admin Password",
                "",
                $"{resourceName} admin",
                "admin",
                records,
                secrets);
        }

        ParseInlineResourceCredential(
            values,
            headers,
            rowNumber,
            resourceName,
            resourceLocalKey,
            category,
            loginUrl,
            sourceSheet,
            keyPrefix,
            reviewStatus,
            "Username",
            headers.Contains("Password", StringComparer.OrdinalIgnoreCase)
                ? "Password"
                : "Password / Secret",
            Value(values, headers, "Login Name"),
            $"{resourceName} login",
            "primary",
            records,
            secrets);
    }

    private static void ParseInlineResourceCredential(
        string[] values,
        string[] headers,
        int rowNumber,
        string resourceName,
        string resourceLocalKey,
        string category,
        string loginUrl,
        string sourceSheet,
        string keyPrefix,
        string reviewStatus,
        string usernameHeader,
        string passwordHeader,
        string credentialName,
        string defaultCredentialName,
        string credentialKeySuffix,
        ICollection<ClientInfoImportRecord> records,
        ICollection<ClientInfoImportSecret> secrets)
    {
        var username = Value(values, headers, usernameHeader);
        var password = Value(
            values,
            headers,
            passwordHeader,
            preserveWhitespace: true);
        if (string.IsNullOrWhiteSpace(credentialName)
            && string.IsNullOrWhiteSpace(username)
            && string.IsNullOrEmpty(password))
        {
            return;
        }

        var credentialLocalKey =
            $"{keyPrefix}-credential-{credentialKeySuffix}-{rowNumber}";
        records.Add(new ClientInfoImportRecord(
            "Credential",
            credentialLocalKey,
            null,
            JsonSerializer.Serialize(new
            {
                resourceKey = resourceLocalKey,
                personKey = (string?)null,
                name = Default(credentialName, defaultCredentialName),
                category,
                username,
                loginUrl,
                notes = ""
            }),
            sourceSheet,
            rowNumber,
            reviewStatus));
        if (!string.IsNullOrEmpty(password))
        {
            secrets.Add(new ClientInfoImportSecret(
                credentialLocalKey,
                "Password",
                "Password",
                password));
        }
    }

    private static void ParseSimpleResourceFields(
        string[] values,
        string[] headers,
        int rowNumber,
        ICollection<ClientInfoImportRecord> records,
        string? category,
        string sourceSheet,
        string keyPrefix,
        string resourceLocalKey,
        string reviewStatus)
    {
        foreach (var definition in ClientInfoResourceFieldDefinitions
                     .ForEditorCategory(category))
        {
            var value = Value(values, headers, definition.FieldLabel);
            if (!string.IsNullOrWhiteSpace(value))
            {
                AddResourceFieldRecord(
                    records,
                    sourceSheet,
                    rowNumber,
                    keyPrefix,
                    resourceLocalKey,
                    definition.FieldKey,
                    definition.FieldLabel,
                    value,
                    definition.ValueType,
                    definition.SortOrder,
                    reviewStatus);
            }
        }

        var customKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < headers.Length && index < values.Length; index++)
        {
            var header = headers[index].Trim();
            if (!header.StartsWith("Custom:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var label = header["Custom:".Length..].Trim();
            var value = values[index].Trim();
            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var fieldKey = ClientInfoResourceFieldDefinitions.CustomFieldKey(label);
            if (!customKeys.Add(fieldKey))
            {
                throw new InvalidDataException(
                    $"{sourceSheet} has duplicate custom field columns named '{label}'.");
            }

            AddResourceFieldRecord(
                records,
                sourceSheet,
                rowNumber,
                keyPrefix,
                resourceLocalKey,
                fieldKey,
                label,
                value,
                "Text",
                100 + index,
                reviewStatus);
        }
    }

    private static void AddResourceFieldRecord(
        ICollection<ClientInfoImportRecord> records,
        string sourceSheet,
        int rowNumber,
        string keyPrefix,
        string resourceLocalKey,
        string fieldKey,
        string fieldLabel,
        string valueText,
        string valueType,
        int sortOrder,
        string reviewStatus)
    {
        var identity = Encoding.UTF8.GetBytes(
            $"{keyPrefix}|{rowNumber}|{fieldKey}");
        var suffix = Convert.ToHexString(SHA256.HashData(identity))
            .ToLowerInvariant()[..12];
        records.Add(new ClientInfoImportRecord(
            "ResourceField",
            $"{keyPrefix}-field-{rowNumber}-{suffix}",
            resourceLocalKey,
            JsonSerializer.Serialize(new
            {
                fieldKey,
                fieldLabel,
                valueText,
                valueType,
                sortOrder
            }),
            sourceSheet,
            rowNumber,
            reviewStatus));
    }

    private static void ParseSimpleEquipment(
        IReadOnlyList<string[]> rows,
        ICollection<ClientInfoImportRecord> records)
    {
        foreach (var row in DataRows(rows))
        {
            var name = Value(row.Values, row.Headers, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            records.Add(new ClientInfoImportRecord(
                "Equipment",
                LocalKey(string.Empty, "equipment", row.RowNumber),
                null,
                JsonSerializer.Serialize(new
                {
                    deviceType = Value(row.Values, row.Headers, "Device Type"),
                    name,
                    manufacturer = Value(row.Values, row.Headers, "Manufacturer"),
                    model = Value(row.Values, row.Headers, "Model"),
                    serialNumber = Value(row.Values, row.Headers, "Serial Number"),
                    partNumber = Value(row.Values, row.Headers, "Part Number"),
                    assetTag = Value(row.Values, row.Headers, "Asset Tag"),
                    ipAddress = Value(row.Values, row.Headers, "IP Address"),
                    locationName = Value(row.Values, row.Headers, "Location"),
                    notes = Value(
                        row.Values,
                        row.Headers,
                        "Notes",
                        preserveWhitespace: true)
                }),
                "Equipment",
                row.RowNumber,
                NormalizeReviewStatus(Value(row.Values, row.Headers, "Review Status"))));
        }
    }

    private static void ParseSimpleCredentials(
        IReadOnlyList<string[]> rows,
        ICollection<ClientInfoImportRecord> records,
        ICollection<ClientInfoImportSecret> secrets,
        IReadOnlyDictionary<string, string?> resourceKeys,
        IReadOnlyDictionary<string, string?> userKeys)
    {
        foreach (var row in DataRows(rows))
        {
            var name = Value(row.Values, row.Headers, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var localKey = LocalKey(string.Empty, "credential", row.RowNumber);
            var relatedSystem = Value(row.Values, row.Headers, "Related System");
            var relatedUser = Value(row.Values, row.Headers, "Related User");
            records.Add(new ClientInfoImportRecord(
                "Credential",
                localKey,
                null,
                JsonSerializer.Serialize(new
                {
                    resourceKey = ResolveFriendlyLookup(
                        resourceKeys,
                        relatedSystem,
                        "system or service",
                        "Passwords",
                        row.RowNumber),
                    personKey = ResolveFriendlyLookup(
                        userKeys,
                        relatedUser,
                        "user",
                        "Passwords",
                        row.RowNumber),
                    name,
                    category = Value(row.Values, row.Headers, "Category"),
                    username = Value(row.Values, row.Headers, "Username"),
                    loginUrl = Value(row.Values, row.Headers, "Login URL"),
                    notes = Value(
                        row.Values,
                        row.Headers,
                        "Notes",
                        preserveWhitespace: true)
                }),
                "Passwords",
                row.RowNumber,
                NormalizeReviewStatus(Value(row.Values, row.Headers, "Review Status"))));

            var secretValue = Value(
                row.Values,
                row.Headers,
                "Password / Secret",
                preserveWhitespace: true);
            if (!string.IsNullOrEmpty(secretValue))
            {
                secrets.Add(new ClientInfoImportSecret(
                    localKey,
                    Default(Value(row.Values, row.Headers, "Secret Type"), "Password"),
                    Default(Value(row.Values, row.Headers, "Secret Label"), "Password"),
                    secretValue));
            }
        }
    }

    private static void ParseFireDrillCredentials(
        IReadOnlyList<string[]> rows,
        ICollection<ClientInfoImportRecord> records,
        ICollection<ClientInfoImportSecret> secrets)
    {
        foreach (var row in DataRows(rows))
        {
            var reviewStatus = NormalizeReviewStatus(
                Value(row.Values, row.Headers, "Review Status"));
            for (var index = 0;
                 index < row.Headers.Length && index < row.Values.Length;
                 index++)
            {
                var label = row.Headers[index].Trim();
                var value = row.Values[index];
                if (string.IsNullOrWhiteSpace(label)
                    || label.Equals(
                        "Review Status",
                        StringComparison.OrdinalIgnoreCase)
                    || label.Equals("Client", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var localKey = $"firedrill-credential-{row.RowNumber}-{index + 1}";
                records.Add(new ClientInfoImportRecord(
                    "Credential",
                    localKey,
                    null,
                    JsonSerializer.Serialize(new
                    {
                        resourceKey = (string?)null,
                        personKey = (string?)null,
                        name = label,
                        category = ClassifyFireDrillField(label),
                        username = "",
                        loginUrl = "",
                        notes = "Imported from the matching FireDrill column."
                    }),
                    "FireDrill",
                    row.RowNumber,
                    reviewStatus));
                secrets.Add(new ClientInfoImportSecret(
                    localKey,
                    "Password",
                    label,
                    value));
            }
        }
    }

    private static string ClassifyFireDrillField(string label)
    {
        var normalized = label.Trim().ToLowerInvariant();
        if (normalized is "status" or "admin" or "csriadmin"
            || normalized.Contains("watchguard", StringComparison.Ordinal)
            || normalized.Contains("firebox", StringComparison.Ordinal)
            || normalized.Contains("authpoint", StringComparison.Ordinal)
            || normalized.Contains("sslvpn", StringComparison.Ordinal)
            || normalized.Contains("ssl vpn", StringComparison.Ordinal))
        {
            return ClientInfoResourceCategories.ConnectionInternet;
        }

        if (normalized.Contains("wireless", StringComparison.Ordinal)
            || normalized.Contains("wifi", StringComparison.Ordinal)
            || normalized.Contains("wi-fi", StringComparison.Ordinal))
        {
            return ClientInfoResourceCategories.Wifi;
        }

        if (normalized.Contains("microsoft 365", StringComparison.Ordinal)
            || normalized.Contains("office 365", StringComparison.Ordinal)
            || normalized.Contains("m365", StringComparison.Ordinal)
            || normalized.Contains("o365", StringComparison.Ordinal)
            || normalized.Contains("rustpw", StringComparison.Ordinal)
            || normalized.Contains("rust pw", StringComparison.Ordinal)
            || normalized.Contains("rustdesk", StringComparison.Ordinal)
            || normalized.Contains("screenconnect", StringComparison.Ordinal)
            || normalized.Contains("connectwise", StringComparison.Ordinal))
        {
            return ClientInfoResourceCategories.ApplicationsCloud;
        }

        if (normalized.Contains("barracuda", StringComparison.Ordinal)
            || normalized.Contains("ad auth", StringComparison.Ordinal)
            || normalized.Contains("ad password", StringComparison.Ordinal)
            || normalized.Contains("active directory", StringComparison.Ordinal)
            || normalized.Contains("domain", StringComparison.Ordinal))
        {
            return ClientInfoResourceCategories.DomainsEmail;
        }

        if (normalized.Contains("veeam", StringComparison.Ordinal)
            || normalized.Contains("backup", StringComparison.Ordinal))
        {
            return ClientInfoResourceCategories.Backup;
        }

        if (normalized.Contains("eset", StringComparison.Ordinal)
            || normalized.Contains("antivirus", StringComparison.Ordinal)
            || normalized.Contains("edr", StringComparison.Ordinal))
        {
            return ClientInfoResourceCategories.Security;
        }

        if (normalized.Contains("ilo", StringComparison.Ordinal)
            || normalized.Contains("ups", StringComparison.Ordinal)
            || normalized.Contains("server", StringComparison.Ordinal)
            || normalized.Contains("switch", StringComparison.Ordinal))
        {
            return ClientInfoResourceCategories.ServersInfrastructure;
        }

        return ClientInfoResourceCategories.NeedsSorting;
    }

    private static void ParseSimpleFacts(
        IReadOnlyList<string[]> rows,
        ICollection<ClientInfoImportRecord> records)
    {
        foreach (var row in DataRows(rows))
        {
            var item = Value(row.Values, row.Headers, "Item");
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            records.Add(new ClientInfoImportRecord(
                "Fact",
                LocalKey(string.Empty, "fact", row.RowNumber),
                null,
                JsonSerializer.Serialize(new
                {
                    sectionName = Default(
                        Value(row.Values, row.Headers, "Section"),
                        "Other"),
                    fieldLabel = item,
                    valueText = Value(
                        row.Values,
                        row.Headers,
                        "Value / Notes",
                        preserveWhitespace: true),
                    valueType = "Text",
                    sortOrder = row.RowNumber,
                    isActive = true
                }),
                "Other Info",
                row.RowNumber,
                NormalizeReviewStatus(Value(row.Values, row.Headers, "Review Status"))));
        }
    }

    private static void AddFriendlyLookup(
        IDictionary<string, string?> lookup,
        string displayName,
        string localKey)
    {
        var key = displayName.Trim();
        if (lookup.ContainsKey(key))
        {
            lookup[key] = null;
            return;
        }

        lookup[key] = localKey;
    }

    private static string? ResolveFriendlyLookup(
        IReadOnlyDictionary<string, string?> lookup,
        string displayName,
        string recordType,
        string sheetName,
        int rowNumber)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        if (!lookup.TryGetValue(displayName.Trim(), out var localKey))
        {
            throw new InvalidDataException(
                $"{sheetName} row {rowNumber} refers to {recordType} '{displayName}', but no matching row was found in the workbook.");
        }

        return localKey ?? throw new InvalidDataException(
            $"{sheetName} row {rowNumber} refers to {recordType} '{displayName}', but that name appears more than once in the workbook. Make the names unique before importing.");
    }

    private static void ParsePeopleAndLocations(
        IReadOnlyList<string[]> rows,
        ICollection<ClientInfoImportRecord> records)
    {
        foreach (var row in DataRows(rows))
        {
            var type = Value(row.Values, row.Headers, "Record Type");
            if (string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            var isPerson = type.Equals(
                "Person",
                StringComparison.OrdinalIgnoreCase);
            var localKey = LocalKey(
                Value(row.Values, row.Headers, "Local Key"),
                isPerson ? "person" : "location",
                row.RowNumber);
            var review = NormalizeReviewStatus(
                Value(row.Values, row.Headers, "Review Status"));
            if (type.Equals("Location", StringComparison.OrdinalIgnoreCase))
            {
                records.Add(new ClientInfoImportRecord(
                    "Location",
                    localKey,
                    null,
                    JsonSerializer.Serialize(new
                    {
                        name = Value(row.Values, row.Headers, "Name"),
                        locationType = Value(row.Values, row.Headers, "Location Type"),
                        address1 = Value(row.Values, row.Headers, "Address 1"),
                        address2 = Value(row.Values, row.Headers, "Address 2"),
                        city = Value(row.Values, row.Headers, "City"),
                        stateProvince = Value(row.Values, row.Headers, "State/Province"),
                        postalCode = Value(row.Values, row.Headers, "Postal Code"),
                        mainPhone = Value(row.Values, row.Headers, "Main Phone"),
                        timeZoneId = Value(row.Values, row.Headers, "Time Zone ID"),
                        isPrimary = ParseBoolean(Value(row.Values, row.Headers, "Is Primary")),
                        isActive = ParseBoolean(Value(row.Values, row.Headers, "Is Active"), true)
                    }),
                    "People & Locations",
                    row.RowNumber,
                    review));
            }
            else if (isPerson)
            {
                records.Add(new ClientInfoImportRecord(
                    "Person",
                    localKey,
                    NullIfBlank(Value(
                        row.Values,
                        row.Headers,
                        "Location Local Key")),
                    JsonSerializer.Serialize(new
                    {
                        displayName = Value(row.Values, row.Headers, "Name"),
                        roleDepartment = Value(row.Values, row.Headers, "Role/Department"),
                        email = Value(row.Values, row.Headers, "Email"),
                        phone = Value(row.Values, row.Headers, "Phone"),
                        mobilePhone = Value(row.Values, row.Headers, "Mobile Phone"),
                        contactType = Value(row.Values, row.Headers, "Contact Type"),
                        isPrimary = ParseBoolean(Value(row.Values, row.Headers, "Is Primary")),
                        isActive = ParseBoolean(Value(row.Values, row.Headers, "Is Active"), true)
                    }),
                    "People & Locations",
                    row.RowNumber,
                    review));
            }
            else
            {
                throw new InvalidDataException(
                    $"People & Locations row {row.RowNumber} must use Record Type Location or Person.");
            }
        }
    }

    private static void ParseResources(
        IReadOnlyList<string[]> rows,
        ICollection<ClientInfoImportRecord> records)
    {
        foreach (var row in DataRows(rows))
        {
            var name = Value(row.Values, row.Headers, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            records.Add(new ClientInfoImportRecord(
                "Resource",
                LocalKey(
                    Value(row.Values, row.Headers, "Local Key"),
                    "resource",
                    row.RowNumber),
                NullIfBlank(Value(row.Values, row.Headers, "Parent Local Key")),
                JsonSerializer.Serialize(new
                {
                    locationKey = Value(row.Values, row.Headers, "Location Local Key"),
                    resourceType = Value(row.Values, row.Headers, "Type"),
                    name,
                    provider = Value(row.Values, row.Headers, "Provider"),
                    addressOrUrl = Value(row.Values, row.Headers, "Address/URL"),
                    status = Value(row.Values, row.Headers, "Status"),
                    notes = Value(row.Values, row.Headers, "Notes"),
                    isActive = ParseBoolean(Value(row.Values, row.Headers, "Is Active"), true)
                }),
                "Systems & Services",
                row.RowNumber,
                NormalizeReviewStatus(Value(row.Values, row.Headers, "Review Status"))));
        }
    }

    private static void ParseEquipment(
        IReadOnlyList<string[]> rows,
        ICollection<ClientInfoImportRecord> records)
    {
        foreach (var row in DataRows(rows))
        {
            var name = Value(row.Values, row.Headers, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            records.Add(new ClientInfoImportRecord(
                "Equipment",
                LocalKey(
                    Value(row.Values, row.Headers, "Local Key"),
                    "equipment",
                    row.RowNumber),
                NullIfBlank(Value(row.Values, row.Headers, "Location Local Key")),
                JsonSerializer.Serialize(new
                {
                    deviceType = Value(row.Values, row.Headers, "Device Type"),
                    name,
                    manufacturer = Value(row.Values, row.Headers, "Manufacturer"),
                    model = Value(row.Values, row.Headers, "Model"),
                    serialNumber = Value(row.Values, row.Headers, "Serial Number"),
                    partNumber = Value(row.Values, row.Headers, "Part Number"),
                    assetTag = Value(row.Values, row.Headers, "Asset Tag"),
                    ipAddress = Value(row.Values, row.Headers, "IP Address"),
                    locationName = Value(row.Values, row.Headers, "Location Name"),
                    notes = Value(row.Values, row.Headers, "Notes")
                }),
                "Equipment",
                row.RowNumber,
                NormalizeReviewStatus(Value(row.Values, row.Headers, "Review Status"))));
        }
    }

    private static void ParseCredentials(
        IReadOnlyList<string[]> rows,
        ICollection<ClientInfoImportRecord> records,
        ICollection<ClientInfoImportSecret> secrets)
    {
        foreach (var row in DataRows(rows))
        {
            var name = Value(row.Values, row.Headers, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var localKey = LocalKey(
                Value(row.Values, row.Headers, "Local Key"),
                "credential",
                row.RowNumber);
            records.Add(new ClientInfoImportRecord(
                "Credential",
                localKey,
                null,
                JsonSerializer.Serialize(new
                {
                    resourceKey = Value(row.Values, row.Headers, "Resource Local Key"),
                    personKey = Value(row.Values, row.Headers, "Person Local Key"),
                    name,
                    category = Value(row.Values, row.Headers, "Category"),
                    username = Value(row.Values, row.Headers, "Username"),
                    loginUrl = Value(row.Values, row.Headers, "Login URL"),
                    notes = Value(row.Values, row.Headers, "Notes")
                }),
                "Credentials",
                row.RowNumber,
                NormalizeReviewStatus(Value(row.Values, row.Headers, "Review Status"))));

            var secretValue = Value(
                row.Values,
                row.Headers,
                "Password / Secret",
                preserveWhitespace: true);
            if (!string.IsNullOrEmpty(secretValue))
            {
                secrets.Add(new ClientInfoImportSecret(
                    localKey,
                    Default(
                        Value(row.Values, row.Headers, "Secret Type"),
                        "Password"),
                    Default(
                        Value(row.Values, row.Headers, "Secret Label"),
                        "Password"),
                    secretValue));
            }
        }
    }

    private static void ParseFacts(
        IReadOnlyList<string[]> rows,
        ICollection<ClientInfoImportRecord> records)
    {
        foreach (var row in DataRows(rows))
        {
            var fieldLabel = Value(row.Values, row.Headers, "Field Label");
            if (string.IsNullOrWhiteSpace(fieldLabel))
            {
                continue;
            }

            records.Add(new ClientInfoImportRecord(
                "Fact",
                LocalKey(
                    Value(row.Values, row.Headers, "Local Key"),
                    "fact",
                    row.RowNumber),
                null,
                JsonSerializer.Serialize(new
                {
                    sectionName = Default(
                        Value(row.Values, row.Headers, "Section"),
                        "Other"),
                    fieldLabel,
                    valueText = Value(
                        row.Values,
                        row.Headers,
                        "Value",
                        preserveWhitespace: true),
                    valueType = Default(
                        Value(row.Values, row.Headers, "Value Type"),
                        "Text"),
                    sortOrder = ParseInteger(
                        Value(row.Values, row.Headers, "Sort Order")),
                    isActive = ParseBoolean(
                        Value(row.Values, row.Headers, "Is Active"),
                        true)
                }),
                "Other Info & Notes",
                row.RowNumber,
                NormalizeReviewStatus(Value(row.Values, row.Headers, "Review Status"))));
        }
    }

    private static IEnumerable<(string[] Headers, string[] Values, int RowNumber)>
        DataRows(IReadOnlyList<string[]> rows)
    {
        if (rows.Count == 0)
        {
            yield break;
        }

        var headers = rows[0];
        for (var index = 1; index < rows.Count; index++)
        {
            if (rows[index].All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            yield return (headers, rows[index], index + 1);
        }
    }

    private static IReadOnlyList<string[]> RequireSheet(
        IReadOnlyDictionary<string, List<string[]>> tables,
        string name) =>
        tables.TryGetValue(name, out var rows)
            ? rows
            : throw new InvalidDataException(
                $"The workbook is missing the required '{name}' tab.");

    private static IReadOnlyList<string[]> GetSheet(
        IReadOnlyDictionary<string, List<string[]>> tables,
        string name) =>
        tables.TryGetValue(name, out var rows) ? rows : [];

    private static Dictionary<string, string> ReadKeyValues(
        IReadOnlyList<string[]> rows)
    {
        var values = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.Length < 2 || string.IsNullOrWhiteSpace(row[0]))
            {
                continue;
            }

            values[row[0].Trim()] = row[1].Trim();
        }

        return values;
    }

    private static string Value(
        IReadOnlyList<string> values,
        IReadOnlyList<string> headers,
        string header,
        bool preserveWhitespace = false)
    {
        var index = -1;
        for (var position = 0; position < headers.Count; position++)
        {
            if (string.Equals(
                    headers[position].Trim(),
                    header,
                    StringComparison.OrdinalIgnoreCase))
            {
                index = position;
                break;
            }
        }

        if (index < 0 || index >= values.Count)
        {
            return string.Empty;
        }

        return preserveWhitespace ? values[index] : values[index].Trim();
    }

    private static string ValueAny(
        IReadOnlyList<string> values,
        IReadOnlyList<string> headers,
        params string[] candidates)
    {
        foreach (var candidate in candidates.Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            var value = Value(values, headers, candidate);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string GetRequired(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        var value = Get(values, key);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException(
                $"Client Info is missing '{key}'.")
            : value;
    }

    private static string Get(
        IReadOnlyDictionary<string, string> values,
        string key) =>
        values.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

    private static string NormalizeReviewStatus(string value)
    {
        var trimmed = value.Trim();
        var friendlyValue = trimmed.ToUpperInvariant() switch
        {
            "KEEP AS-IS" or "KEEP AS IS" => "AcceptedUnverified",
            "NEEDS REVIEW" => "NeedsReview",
            "DO NOT IMPORT" => "Rejected",
            "NOT REVIEWED" => "Unverified",
            _ => trimmed
        };

        return ReviewStatuses.FirstOrDefault(status => string.Equals(
            status,
            friendlyValue,
            StringComparison.OrdinalIgnoreCase))
            ?? "Unverified";
    }

    private static string LocalKey(string value, string prefix, int rowNumber) =>
        string.IsNullOrWhiteSpace(value)
            ? $"{prefix}-{rowNumber}"
            : value.Trim();

    private static string Default(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ParseBoolean(string value, bool fallback = false) =>
        value.Trim().ToUpperInvariant() switch
        {
            "TRUE" or "YES" or "Y" or "1" => true,
            "FALSE" or "NO" or "N" or "0" => false,
            _ => fallback
        };

    private static int ParseInteger(string value) =>
        int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : 0;

    private static string Format(object? value) =>
        value switch
        {
            null => string.Empty,
            DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(
                null,
                CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };

    private static string[] ResourceHeaders(string category) =>
        [
            "Type",
            "Name",
            "Provider",
            ClientInfoResourceFieldDefinitions.AddressLabelForCategory(category),
            .. ResourceAccessHeaders(category),
            .. ClientInfoResourceFieldDefinitions.ForEditorCategory(category)
                .Select(field => field.FieldLabel),
            "Location",
            "Status",
            "Notes",
            "Review Status"
        ];

    private static string[] BuildFireDrillHeaders(
        IReadOnlyList<string>? fieldLabels)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headers = (fieldLabels is { Count: > 0 }
                ? fieldLabels
                : LegacyFireDrillHeaders)
            .Select(label => label?.Trim() ?? string.Empty)
            .Where(label => !string.IsNullOrWhiteSpace(label)
                            && !label.Equals(
                                "Client",
                                StringComparison.OrdinalIgnoreCase)
                            && !label.Equals(
                                "Review Status",
                                StringComparison.OrdinalIgnoreCase)
                            && unique.Add(label))
            .ToList();
        headers.Add("Review Status");
        return headers.ToArray();
    }

    private static string[] ResourceAccessHeaders(string category) =>
        category.Equals(
            ClientInfoResourceCategories.ConnectionInternet,
            StringComparison.OrdinalIgnoreCase)
            ?
            [
                "Status Username",
                "Status Password",
                "Admin Username",
                "Admin Password"
            ]
            : ["Login Name", "Username", "Password"];

    private static double[] ResourceColumnWidths(string category) =>
        [
            22,
            28,
            24,
            34,
            .. ResourceAccessHeaders(category)
                .Select(header => header.Contains(
                    "Password",
                    StringComparison.OrdinalIgnoreCase)
                    ? 30D
                    : 26D),
            .. ClientInfoResourceFieldDefinitions.ForEditorCategory(category)
                .Select(field => field.ValueType == "IpAddress" ? 20D : 22D),
            22,
            18,
            40,
            22
        ];

    private static void AddSheet(
        WorkbookPart workbookPart,
        Sheets sheets,
        ref uint sheetId,
        string name,
        IReadOnlyList<string[]> rows,
        int headerRow = 1,
        IReadOnlyList<double>? columnWidths = null,
        bool formatFirstRowAsTitle = false,
        string? resourceCategory = null)
    {
        var part = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        var worksheet = new Worksheet();
        var view = new SheetView
        {
            WorkbookViewId = 0U,
            ShowGridLines = false
        };
        if (headerRow > 0)
        {
            view.Append(new Pane
            {
                VerticalSplit = headerRow,
                TopLeftCell = $"A{headerRow + 1}",
                ActivePane = PaneValues.BottomLeft,
                State = PaneStateValues.Frozen
            });
        }
        worksheet.Append(new SheetViews(view));

        var columns = new Columns();
        var widestRow = rows.Count == 0
            ? 1
            : rows.Max(row => row.Length);
        for (var index = 0; index < widestRow; index++)
        {
            columns.Append(new Column
            {
                Min = (uint)(index + 1),
                Max = (uint)(index + 1),
                Width = columnWidths is not null && index < columnWidths.Count
                    ? columnWidths[index]
                    : 22,
                CustomWidth = true
            });
        }
        worksheet.Append(columns);
        worksheet.Append(sheetData);

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = new Row
            {
                RowIndex = (uint)(rowIndex + 1),
                Height = rowIndex + 1 == headerRow
                    ? 28D
                    : formatFirstRowAsTitle && rowIndex == 0
                        ? 34D
                        : formatFirstRowAsTitle && rowIndex == 1
                            ? 58D
                            : formatFirstRowAsTitle && rowIndex == 2
                                ? 52D
                                : formatFirstRowAsTitle && rowIndex == 3
                                    ? 120D
                                    : formatFirstRowAsTitle && rowIndex is 10 or 11
                                        ? 42D
                                        : formatFirstRowAsTitle && rowIndex == 12
                                            ? 58D
                                        : 24D,
                CustomHeight = true
            };
            for (var columnIndex = 0;
                 columnIndex < rows[rowIndex].Length;
                 columnIndex++)
            {
                var header = rows[rowIndex][columnIndex];
                var styleIndex = rowIndex + 1 == headerRow
                    ? IsRequiredHeader(name, header) ? 4U : 1U
                    : formatFirstRowAsTitle && rowIndex == 0
                        ? 2U
                        : headerRow == 0 && columnIndex == 0
                            ? 3U
                            : 0U;
                row.Append(new Cell
                {
                    CellReference =
                        $"{ColumnName(columnIndex + 1)}{rowIndex + 1}",
                    DataType = CellValues.InlineString,
                    StyleIndex = styleIndex,
                    InlineString = new InlineString(
                        new Text(rows[rowIndex][columnIndex])
                        {
                            Space = SpaceProcessingModeValues.Preserve
                        })
                });
            }

            sheetData.Append(row);
        }

        if (formatFirstRowAsTitle && widestRow > 1)
        {
            worksheet.Append(new MergeCells(
                new MergeCell
                {
                    Reference = $"A1:{ColumnName(widestRow)}1"
                }));
        }

        if (headerRow > 0 && rows.Count > 0)
        {
            worksheet.Append(new AutoFilter
            {
                Reference =
                    $"A{headerRow}:{ColumnName(rows[0].Length)}{headerRow}"
            });
            AppendListValidation(
                worksheet,
                rows[0],
                "Review Status",
                "Verified,Keep as-is,Needs review,Do not import");
            AppendListValidation(
                worksheet,
                rows[0],
                "Is Primary",
                "Yes,No");
            AppendListValidation(
                worksheet,
                rows[0],
                "Has Microsoft 365",
                "Yes,No");
            AppendListValidation(
                worksheet,
                rows[0],
                "Microsoft 365 License",
                string.Join(',', Microsoft365LicenseCatalog.All));
            AppendListValidation(
                worksheet,
                rows[0],
                "Microsoft 365 Uses AD Login",
                "Yes,No");
            AppendListValidation(
                worksheet,
                rows[0],
                "Location Type",
                string.Join(',', LocationTypeOptions));
            AppendListValidation(
                worksheet,
                rows[0],
                "Contact Type",
                string.Join(',', ContactTypeOptions));
            AppendListValidation(
                worksheet,
                rows[0],
                "Device Type",
                string.Join(',', EquipmentTypeOptions));
            AppendListValidation(
                worksheet,
                rows[0],
                "Secret Type",
                string.Join(',', SecretTypeOptions));
            if (!string.IsNullOrWhiteSpace(resourceCategory))
            {
                AppendListValidation(
                    worksheet,
                    rows[0],
                    "Type",
                    string.Join(',', ClientInfoResourceFieldDefinitions
                        .TypeOptionsForCategory(resourceCategory)));
                foreach (var definition in ClientInfoResourceFieldDefinitions
                             .ForEditorCategory(resourceCategory)
                             .Where(definition => definition.Options is { Count: > 0 }))
                {
                    AppendListValidation(
                        worksheet,
                        rows[0],
                        definition.FieldLabel,
                        string.Join(',', definition.Options!),
                        definition.AllowCustomValue);
                }
            }
        }

        part.Worksheet = worksheet;
        part.Worksheet.Save();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(part),
            SheetId = sheetId++,
            Name = name
        });
    }

    private static void AppendListValidation(
        Worksheet worksheet,
        IReadOnlyList<string> headers,
        string header,
        string values,
        bool allowCustomValue = false)
    {
        var index = -1;
        for (var position = 0; position < headers.Count; position++)
        {
            if (string.Equals(
                    headers[position],
                    header,
                    StringComparison.OrdinalIgnoreCase))
            {
                index = position;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        var validations = worksheet.Elements<DataValidations>().FirstOrDefault();
        if (validations is null)
        {
            validations = new DataValidations();
            worksheet.Append(validations);
        }

        var column = ColumnName(index + 1);
        validations.Append(new DataValidation
        {
            Type = DataValidationValues.List,
            AllowBlank = true,
            ShowErrorMessage = !allowCustomValue,
            ErrorTitle = "Choose a listed value",
            Error = $"Use one of: {values}",
            SequenceOfReferences = new ListValue<StringValue>
            {
                InnerText = $"{column}2:{column}500"
            },
            Formula1 = new Formula1($"\"{values}\"")
        });
        validations.Count = (uint)validations.ChildElements.Count;
    }

    private static bool IsRequiredHeader(string sheetName, string header) =>
        string.Equals(header, "Review Status", StringComparison.OrdinalIgnoreCase)
        || string.Equals(header, "Name", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(
                sheetName,
                "Other Info",
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(header, "Item", StringComparison.OrdinalIgnoreCase));

    private static Stylesheet CreateStylesheet() => new(
        new Fonts(
            new DocumentFormat.OpenXml.Spreadsheet.Font(
                new FontName { Val = "Aptos" },
                new FontSize { Val = 11D }),
            new DocumentFormat.OpenXml.Spreadsheet.Font(
                new Bold(),
                new FontName { Val = "Aptos" },
                new FontSize { Val = 11D },
                new DocumentFormat.OpenXml.Spreadsheet.Color
                {
                    Rgb = "FFFFFFFF"
                }),
            new DocumentFormat.OpenXml.Spreadsheet.Font(
                new Bold(),
                new FontName { Val = "Aptos Display" },
                new FontSize { Val = 18D },
                new DocumentFormat.OpenXml.Spreadsheet.Color
                {
                    Rgb = "FFFFFFFF"
                }),
            new DocumentFormat.OpenXml.Spreadsheet.Font(
                new Bold(),
                new FontName { Val = "Aptos" },
                new FontSize { Val = 11D },
                new DocumentFormat.OpenXml.Spreadsheet.Color
                {
                    Rgb = "FF17324D"
                })),
        new Fills(
            new Fill(new PatternFill
            {
                PatternType = PatternValues.None
            }),
            new Fill(new PatternFill
            {
                PatternType = PatternValues.Gray125
            }),
            new Fill(new PatternFill(
                new ForegroundColor { Rgb = "FF1F4E78" })
            {
                PatternType = PatternValues.Solid
            }),
            new Fill(new PatternFill(
                new ForegroundColor { Rgb = "FFDCE6F1" })
            {
                PatternType = PatternValues.Solid
            }),
            new Fill(new PatternFill(
                new ForegroundColor { Rgb = "FFFFC857" })
            {
                PatternType = PatternValues.Solid
            })),
        new Borders(
            new Border(),
            new Border(
                new BottomBorder
                {
                    Style = BorderStyleValues.Thin,
                    Color = new DocumentFormat.OpenXml.Spreadsheet.Color
                    {
                        Rgb = "FFB8C8D8"
                    }
                })),
        new CellFormats(
            new CellFormat
            {
                Alignment = new Alignment
                {
                    Horizontal = HorizontalAlignmentValues.Left,
                    Vertical = VerticalAlignmentValues.Top,
                    WrapText = true
                },
                ApplyAlignment = true
            },
            new CellFormat
            {
                FontId = 1,
                FillId = 2,
                BorderId = 1,
                ApplyFont = true,
                ApplyFill = true,
                ApplyBorder = true,
                Alignment = new Alignment
                {
                    Horizontal = HorizontalAlignmentValues.Left,
                    Vertical = VerticalAlignmentValues.Center,
                    WrapText = true
                },
                ApplyAlignment = true
            },
            new CellFormat
            {
                FontId = 2,
                FillId = 2,
                ApplyFont = true,
                ApplyFill = true,
                Alignment = new Alignment
                {
                    Horizontal = HorizontalAlignmentValues.Left,
                    Vertical = VerticalAlignmentValues.Center
                },
                ApplyAlignment = true
            },
            new CellFormat
            {
                FontId = 3,
                FillId = 3,
                BorderId = 1,
                ApplyFont = true,
                ApplyFill = true,
                ApplyBorder = true,
                Alignment = new Alignment
                {
                    Horizontal = HorizontalAlignmentValues.Left,
                    Vertical = VerticalAlignmentValues.Top,
                    WrapText = true
                },
                ApplyAlignment = true
            },
            new CellFormat
            {
                FontId = 3,
                FillId = 4,
                BorderId = 1,
                ApplyFont = true,
                ApplyFill = true,
                ApplyBorder = true,
                Alignment = new Alignment
                {
                    Horizontal = HorizontalAlignmentValues.Left,
                    Vertical = VerticalAlignmentValues.Center,
                    WrapText = true
                },
                ApplyAlignment = true
            }));

    private static string ColumnName(int column)
    {
        var result = string.Empty;
        while (column > 0)
        {
            column--;
            result = (char)('A' + column % 26) + result;
            column /= 26;
        }

        return result;
    }
}
