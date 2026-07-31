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
    public const string TemplateVersion = "TB-CI-1";

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
            "Client Info",
            [
                ["TechBench Client Info Migration Workbook", ""],
                ["Template Version", TemplateVersion],
                ["Workbook ID", Guid.NewGuid().ToString("D")],
                ["Internal Client ID", clientId.ToString(CultureInfo.InvariantCulture)],
                ["Client Name", clientName],
                ["Summary", ""],
                ["Review Status", "Unverified"],
                ["Instructions", "Keep the internal client ID unchanged. Use one row per record on the remaining tabs. Passwords belong only in the Credentials tab."]
            ],
            headerRow: 0);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "People & Locations",
            [
                ["Record Type", "Local Key", "Name", "Location Type", "Address 1",
                 "Address 2", "City", "State/Province", "Postal Code", "Main Phone",
                 "Time Zone ID", "Location Local Key", "Role/Department", "Email",
                 "Phone", "Mobile Phone", "Contact Type", "Is Primary", "Is Active",
                 "Review Status"]
            ]);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Systems & Services",
            [
                ["Local Key", "Parent Local Key", "Location Local Key", "Type", "Name",
                 "Provider", "Address/URL", "Status", "Notes", "Is Active",
                 "Review Status"]
            ]);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Equipment",
            [
                ["Local Key", "Location Local Key", "Device Type", "Name", "Manufacturer",
                 "Model", "Serial Number", "Part Number", "Asset Tag", "IP Address",
                 "Location Name", "Notes", "Review Status"]
            ]);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Credentials",
            [
                ["Local Key", "Resource Local Key", "Person Local Key", "Name", "Category",
                 "Username", "Login URL", "Notes", "Secret Type", "Secret Label",
                 "Password / Secret", "Review Status"]
            ]);
        AddSheet(
            workbookPart,
            sheets,
            ref sheetId,
            "Other Info & Notes",
            [
                ["Local Key", "Section", "Field Label", "Value", "Value Type",
                 "Sort Order", "Is Active", "Review Status"]
            ]);
        workbookPart.Workbook.Save();
    }

    public ClientInfoWorkbookPackage Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var tables = ReadTables(path);
        var info = ReadKeyValues(RequireSheet(tables, "Client Info"));
        var version = GetRequired(info, "Template Version");
        if (!string.Equals(version, TemplateVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Template version '{version}' is not supported. Expected {TemplateVersion}.");
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
            "Client Info",
            6,
            NormalizeReviewStatus(Get(info, "Review Status"))));

        ParsePeopleAndLocations(GetSheet(tables, "People & Locations"), records);
        ParseResources(GetSheet(tables, "Systems & Services"), records);
        ParseEquipment(GetSheet(tables, "Equipment"), records);
        ParseCredentials(GetSheet(tables, "Credentials"), records, secrets);
        ParseFacts(GetSheet(tables, "Other Info & Notes"), records);

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

    private static string NormalizeReviewStatus(string value) =>
        ReviewStatuses.FirstOrDefault(status => string.Equals(
            status,
            value.Trim(),
            StringComparison.OrdinalIgnoreCase))
        ?? "Unverified";

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
        int headerRow = 1)
    {
        var part = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        var worksheet = new Worksheet();
        if (headerRow > 0)
        {
            worksheet.Append(new SheetViews(
                new SheetView(
                    new Pane
                    {
                        VerticalSplit = headerRow,
                        TopLeftCell = $"A{headerRow + 1}",
                        ActivePane = PaneValues.BottomLeft,
                        State = PaneStateValues.Frozen
                    })
                {
                    WorkbookViewId = 0U
                }));
        }

        worksheet.Append(new Columns(
            new Column
            {
                Min = 1,
                Max = 30,
                Width = 22,
                CustomWidth = true
            }));
        worksheet.Append(sheetData);

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = new Row { RowIndex = (uint)(rowIndex + 1) };
            for (var columnIndex = 0;
                 columnIndex < rows[rowIndex].Length;
                 columnIndex++)
            {
                row.Append(new Cell
                {
                    CellReference =
                        $"{ColumnName(columnIndex + 1)}{rowIndex + 1}",
                    DataType = CellValues.InlineString,
                    StyleIndex = rowIndex + 1 == headerRow ? 1U : 0U,
                    InlineString = new InlineString(
                        new Text(rows[rowIndex][columnIndex])
                        {
                            Space = SpaceProcessingModeValues.Preserve
                        })
                });
            }

            sheetData.Append(row);
        }

        if (headerRow > 0 && rows.Count > 0)
        {
            worksheet.Append(new AutoFilter
            {
                Reference =
                    $"A{headerRow}:{ColumnName(rows[0].Length)}{headerRow}"
            });
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

    private static Stylesheet CreateStylesheet() => new(
        new Fonts(
            new DocumentFormat.OpenXml.Spreadsheet.Font(),
            new DocumentFormat.OpenXml.Spreadsheet.Font(
                new Bold(),
                new DocumentFormat.OpenXml.Spreadsheet.Color
                {
                    Rgb = "FFFFFFFF"
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
            })),
        new Borders(new Border()),
        new CellFormats(
            new CellFormat(),
            new CellFormat
            {
                FontId = 1,
                FillId = 2,
                ApplyFont = true,
                ApplyFill = true
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
