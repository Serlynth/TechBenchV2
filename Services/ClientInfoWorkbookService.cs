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
    public const string TemplateVersion = "TB-CI-3";
    public const string PreviousTemplateVersion = "TB-CI-2";
    public const string LegacyTemplateVersion = "TB-CI-1";

    private static readonly string[] ReviewStatuses =
    [
        "Unverified", "Verified", "AcceptedUnverified", "NeedsReview", "Rejected"
    ];

    public void CreateTemplate(string path, int clientId, string clientName)
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
                ["What to do", "Copy the useful, cleaned information from the client's current workbook into the matching category tabs. Leave tabs that do not apply blank."],
                ["Review each row", "Choose Verified, Keep as-is, Needs review, or Do not import. A workbook cannot be approved while a populated row is blank or still Needs review."],
                ["Passwords", "Put passwords and other secrets only on the Passwords tab. They are encrypted when imported and are never written to import logs."],
                ["Template Version", TemplateVersion],
                ["Workbook ID", Guid.NewGuid().ToString("D")],
                ["Internal Client ID", clientId.ToString(CultureInfo.InvariantCulture)],
                ["Client Name", clientName],
                ["Summary", ""],
                ["Review Status", "Verified"],
                ["Important", "Do not change the internal client ID or reuse this workbook for another client."]
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
            "People",
            [
                ["Name", "Role/Department", "Email", "Phone", "Mobile Phone",
                 "Location", "Contact Type", "Is Primary", "Review Status"]
            ],
            columnWidths: [26, 24, 32, 18, 18, 22, 20, 12, 22]);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Servers & Infrastructure",
            [
                ["Type", "Name", "Provider", "Address/URL", "Location", "Status",
                 "Notes", "Review Status"]
            ],
            columnWidths: [22, 28, 24, 34, 22, 18, 40, 22]);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Network & Internet",
            [
                ["Type", "Name", "Provider", "Address/URL", "Location", "Status",
                 "Notes", "Review Status"]
            ],
            columnWidths: [22, 28, 24, 34, 22, 18, 40, 22]);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Applications & Cloud",
            [
                ["Type", "Name", "Provider", "Address/URL", "Location", "Status",
                 "Notes", "Review Status"]
            ],
            columnWidths: [22, 28, 24, 34, 22, 18, 40, 22]);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Domains & Email",
            [
                ["Type", "Name", "Provider", "Address/URL", "Location", "Status",
                 "Notes", "Review Status"]
            ],
            columnWidths: [22, 28, 24, 34, 22, 18, 40, 22]);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Backup & Security",
            [
                ["Type", "Name", "Provider", "Address/URL", "Location", "Status",
                 "Notes", "Review Status"]
            ],
            columnWidths: [22, 28, 24, 34, 22, 18, 40, 22]);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Vendors & Services",
            [
                ["Type", "Name", "Provider", "Address/URL", "Location", "Status",
                 "Notes", "Review Status"]
            ],
            columnWidths: [22, 28, 24, 34, 22, 18, 40, 22]);
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
                 "Related System", "Notes", "Secret Type", "Secret Label",
                 "Review Status"]
            ],
            columnWidths: [26, 20, 26, 30, 34, 26, 40, 18, 20, 22]);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Other Info",
            [
                ["Section", "Item", "Value / Notes", "Review Status"]
            ],
            columnWidths: [22, 28, 55, 22]);
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
                PreviousTemplateVersion,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                version,
                LegacyTemplateVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Template version '{version}' is not supported. Expected {TemplateVersion}, {PreviousTemplateVersion}, or {LegacyTemplateVersion}.");
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
                StringComparison.OrdinalIgnoreCase))
        {
            var locationKeys = ParseSimpleLocations(
                GetSheet(tables, "Locations"),
                records);
            ParseSimplePeople(
                GetSheet(tables, "People"),
                records,
                locationKeys);
            var resourceKeys = new Dictionary<string, string?>(
                StringComparer.OrdinalIgnoreCase);
            ParseSimpleResources(
                GetSheet(tables, "Servers & Infrastructure"),
                records,
                locationKeys,
                resourceKeys,
                ClientInfoResourceCategories.ServersInfrastructure,
                "Servers & Infrastructure",
                "infrastructure");
            ParseSimpleResources(
                GetSheet(tables, "Network & Internet"),
                records,
                locationKeys,
                resourceKeys,
                ClientInfoResourceCategories.NetworkInternet,
                "Network & Internet",
                "network");
            ParseSimpleResources(
                GetSheet(tables, "Applications & Cloud"),
                records,
                locationKeys,
                resourceKeys,
                ClientInfoResourceCategories.ApplicationsCloud,
                "Applications & Cloud",
                "application");
            ParseSimpleResources(
                GetSheet(tables, "Domains & Email"),
                records,
                locationKeys,
                resourceKeys,
                ClientInfoResourceCategories.DomainsEmail,
                "Domains & Email",
                "domain");
            ParseSimpleResources(
                GetSheet(tables, "Backup & Security"),
                records,
                locationKeys,
                resourceKeys,
                ClientInfoResourceCategories.BackupSecurity,
                "Backup & Security",
                "security");
            ParseSimpleResources(
                GetSheet(tables, "Vendors & Services"),
                records,
                locationKeys,
                resourceKeys,
                ClientInfoResourceCategories.VendorsServices,
                "Vendors & Services",
                "vendor");
            ParseSimpleEquipment(GetSheet(tables, "Equipment"), records);
            ParseSimpleCredentials(
                GetSheet(tables, "Passwords"),
                records,
                secrets,
                resourceKeys);
            ParseSimpleFacts(GetSheet(tables, "Other Info"), records);
        }
        else if (string.Equals(
                     version,
                     PreviousTemplateVersion,
                     StringComparison.OrdinalIgnoreCase))
        {
            var locationKeys = ParseSimpleLocations(
                GetSheet(tables, "Locations"),
                records);
            ParseSimplePeople(
                GetSheet(tables, "People"),
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
                resourceKeys);
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

    private static void ParseSimplePeople(
        IReadOnlyList<string[]> rows,
        ICollection<ClientInfoImportRecord> records,
        IReadOnlyDictionary<string, string?> locationKeys)
    {
        foreach (var row in DataRows(rows))
        {
            var name = Value(row.Values, row.Headers, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var locationName = Value(row.Values, row.Headers, "Location");
            records.Add(new ClientInfoImportRecord(
                "Person",
                LocalKey(string.Empty, "person", row.RowNumber),
                ResolveFriendlyLookup(
                    locationKeys,
                    locationName,
                    "location",
                    "People",
                    row.RowNumber),
                JsonSerializer.Serialize(new
                {
                    displayName = name,
                    roleDepartment = Value(row.Values, row.Headers, "Role/Department"),
                    email = Value(row.Values, row.Headers, "Email"),
                    phone = Value(row.Values, row.Headers, "Phone"),
                    mobilePhone = Value(row.Values, row.Headers, "Mobile Phone"),
                    contactType = Value(row.Values, row.Headers, "Contact Type"),
                    isPrimary = ParseBoolean(Value(row.Values, row.Headers, "Is Primary")),
                    isActive = true
                }),
                "People",
                row.RowNumber,
                NormalizeReviewStatus(Value(row.Values, row.Headers, "Review Status"))));
        }
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
        string keyPrefix)
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
                    addressOrUrl = Value(row.Values, row.Headers, "Address/URL"),
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
                NormalizeReviewStatus(Value(row.Values, row.Headers, "Review Status"))));
        }
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
        IReadOnlyDictionary<string, string?> resourceKeys)
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
                    personKey = string.Empty,
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

    private static void AddSheet(
        WorkbookPart workbookPart,
        Sheets sheets,
        ref uint sheetId,
        string name,
        IReadOnlyList<string[]> rows,
        int headerRow = 1,
        IReadOnlyList<double>? columnWidths = null,
        bool formatFirstRowAsTitle = false)
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
                            ? 38D
                            : formatFirstRowAsTitle && rowIndex == 2
                                ? 52D
                                : formatFirstRowAsTitle && rowIndex == 3
                                    ? 42D
                        : 24D,
                CustomHeight = true
            };
            for (var columnIndex = 0;
                 columnIndex < rows[rowIndex].Length;
                 columnIndex++)
            {
                var styleIndex = rowIndex + 1 == headerRow
                    ? 1U
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
        string values)
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
            ShowErrorMessage = true,
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
