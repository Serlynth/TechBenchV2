using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using TechBench.Models;

namespace TechBench.Services;

public enum ClientMatchExportCategory
{
    Matched,
    WhdOnly,
    SageOnly,
    ManualOrOther
}

public static class ClientMatchExcelExportService
{
    private const string SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OfficeRelationshipNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";

    private static readonly string[] ClientHeaders =
    [
        "TechBench client name",
        "Category",
        "Source",
        "Active",
        "Match status",
        "WHD ID",
        "WHD location",
        "WHD contact",
        "WHD email",
        "WHD phone",
        "WHD address",
        "Sage customer ID",
        "Sage customer name",
        "Sage contact",
        "Sage phone",
        "Last synced"
    ];

    public static byte[] BuildWorkbook(
        IEnumerable<Client> clients,
        DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(clients);

        var allClients = clients
            .OrderBy(GetCategorySortOrder)
            .ThenBy(static client => client.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static client => client.Id)
            .ToList();
        var sheets = new List<WorkbookSheet>
        {
            BuildSummarySheet(allClients, generatedAt ?? DateTimeOffset.Now),
            BuildClientSheet(
                "Matched",
                allClients.Where(client =>
                    GetCategory(client) == ClientMatchExportCategory.Matched)),
            BuildClientSheet(
                "WHD Only",
                allClients.Where(client =>
                    GetCategory(client) == ClientMatchExportCategory.WhdOnly)),
            BuildClientSheet(
                "Sage Only",
                allClients.Where(client =>
                    GetCategory(client) == ClientMatchExportCategory.SageOnly)),
            BuildClientSheet(
                "Manual Other",
                allClients.Where(client =>
                    GetCategory(client) == ClientMatchExportCategory.ManualOrOther)),
            BuildClientSheet("All Clients", allClients)
        };

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteContentTypes(archive, sheets.Count);
            WriteRootRelationships(archive);
            WriteWorkbook(archive, sheets);
            WriteWorkbookRelationships(archive, sheets.Count);
            WriteStyles(archive);
            for (var index = 0; index < sheets.Count; index++)
            {
                WriteWorksheet(archive, index + 1, sheets[index]);
            }
        }

        return output.ToArray();
    }

    public static ClientMatchExportCategory GetCategory(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (client.Source.Equals("Both", StringComparison.OrdinalIgnoreCase))
        {
            return ClientMatchExportCategory.Matched;
        }

        if (client.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase))
        {
            return ClientMatchExportCategory.WhdOnly;
        }

        if (client.Source.Equals("Sage", StringComparison.OrdinalIgnoreCase))
        {
            return ClientMatchExportCategory.SageOnly;
        }

        return ClientMatchExportCategory.ManualOrOther;
    }

    public static string GetCategoryLabel(Client client) =>
        GetCategory(client) switch
        {
            ClientMatchExportCategory.Matched => "Matched",
            ClientMatchExportCategory.WhdOnly => "WHD only",
            ClientMatchExportCategory.SageOnly => "Sage only",
            _ => "Manual / other"
        };

    private static int GetCategorySortOrder(Client client) =>
        GetCategory(client) switch
        {
            ClientMatchExportCategory.Matched => 0,
            ClientMatchExportCategory.WhdOnly => 1,
            ClientMatchExportCategory.SageOnly => 2,
            _ => 3
        };

    private static WorkbookSheet BuildSummarySheet(
        IReadOnlyList<Client> clients,
        DateTimeOffset generatedAt)
    {
        var rows = new List<WorkbookRow>
        {
            new(["TechBench client-match audit"], 2),
            new(
                [$"Generated {generatedAt:yyyy-MM-dd h:mm tt zzz}"],
                0),
            new([], 0),
            new(["Category", "Active", "Inactive", "Total"], 1)
        };

        AddSummaryRow(rows, "Matched", clients.Where(client =>
            GetCategory(client) == ClientMatchExportCategory.Matched));
        AddSummaryRow(rows, "WHD only", clients.Where(client =>
            GetCategory(client) == ClientMatchExportCategory.WhdOnly));
        AddSummaryRow(rows, "Sage only", clients.Where(client =>
            GetCategory(client) == ClientMatchExportCategory.SageOnly));
        AddSummaryRow(rows, "Manual / other", clients.Where(client =>
            GetCategory(client) == ClientMatchExportCategory.ManualOrOther));
        AddSummaryRow(rows, "All clients", clients);

        return new WorkbookSheet(
            "Summary",
            rows,
            [30d, 12d, 12d, 12d],
            FreezeRow: 4,
            AutoFilterRow: 4);
    }

    private static void AddSummaryRow(
        ICollection<WorkbookRow> rows,
        string label,
        IEnumerable<Client> clients)
    {
        var categoryClients = clients.ToList();
        rows.Add(new WorkbookRow(
            [
                label,
                categoryClients.Count(static client => client.IsActive)
                    .ToString(CultureInfo.InvariantCulture),
                categoryClients.Count(static client => !client.IsActive)
                    .ToString(CultureInfo.InvariantCulture),
                categoryClients.Count.ToString(CultureInfo.InvariantCulture)
            ],
            0,
            NumericColumns: [1, 2, 3]));
    }

    private static WorkbookSheet BuildClientSheet(
        string name,
        IEnumerable<Client> clients)
    {
        var rows = new List<WorkbookRow>
        {
            new(ClientHeaders, 1)
        };
        rows.AddRange(
            clients
                .OrderBy(static client => client.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static client => client.Id)
                .Select(client => new WorkbookRow(
                    [
                        client.Name,
                        GetCategoryLabel(client),
                        client.SourceLabel,
                        client.IsActive ? "Yes" : "No",
                        client.MatchStatusLabel,
                        ResolveWhdExternalId(client),
                        client.WhdLocationName ?? string.Empty,
                        client.WhdContactName ?? string.Empty,
                        client.WhdContactEmail ?? string.Empty,
                        client.WhdPhone ?? string.Empty,
                        client.WhdAddress ?? string.Empty,
                        client.SageCustomerId ?? string.Empty,
                        client.SageCustomerName ?? string.Empty,
                        client.SageContactName ?? string.Empty,
                        client.SageTelephone ?? string.Empty,
                        client.LastSyncedAt?.ToString(
                            "yyyy-MM-dd HH:mm",
                            CultureInfo.InvariantCulture) ?? string.Empty
                    ],
                    0)));

        return new WorkbookSheet(
            name,
            rows,
            [34d, 16d, 12d, 10d, 16d, 22d, 32d, 26d, 30d, 18d, 42d, 20d, 34d, 26d, 18d, 20d],
            FreezeRow: 1,
            AutoFilterRow: 1);
    }

    private static string ResolveWhdExternalId(Client client)
    {
        if (client.Source.Equals("Sage", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return client.ExternalId ?? string.Empty;
    }

    private static void WriteContentTypes(ZipArchive archive, int worksheetCount)
    {
        WriteXmlEntry(archive, "[Content_Types].xml", writer =>
        {
            writer.WriteStartElement("Types", ContentTypesNamespace);
            WriteContentTypeDefault(
                writer,
                "rels",
                "application/vnd.openxmlformats-package.relationships+xml");
            WriteContentTypeDefault(writer, "xml", "application/xml");
            WriteContentTypeOverride(
                writer,
                "/xl/workbook.xml",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            WriteContentTypeOverride(
                writer,
                "/xl/styles.xml",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            for (var index = 1; index <= worksheetCount; index++)
            {
                WriteContentTypeOverride(
                    writer,
                    $"/xl/worksheets/sheet{index}.xml",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            }

            writer.WriteEndElement();
        });
    }

    private static void WriteContentTypeDefault(
        XmlWriter writer,
        string extension,
        string contentType)
    {
        writer.WriteStartElement("Default", ContentTypesNamespace);
        writer.WriteAttributeString("Extension", extension);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void WriteContentTypeOverride(
        XmlWriter writer,
        string partName,
        string contentType)
    {
        writer.WriteStartElement("Override", ContentTypesNamespace);
        writer.WriteAttributeString("PartName", partName);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void WriteRootRelationships(ZipArchive archive)
    {
        WriteXmlEntry(archive, "_rels/.rels", writer =>
        {
            writer.WriteStartElement("Relationships", PackageRelationshipNamespace);
            WriteRelationship(
                writer,
                "rId1",
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument",
                "xl/workbook.xml");
            writer.WriteEndElement();
        });
    }

    private static void WriteWorkbook(
        ZipArchive archive,
        IReadOnlyList<WorkbookSheet> sheets)
    {
        WriteXmlEntry(archive, "xl/workbook.xml", writer =>
        {
            writer.WriteStartElement("workbook", SpreadsheetNamespace);
            writer.WriteAttributeString(
                "xmlns",
                "r",
                null,
                OfficeRelationshipNamespace);
            writer.WriteStartElement("sheets", SpreadsheetNamespace);
            for (var index = 0; index < sheets.Count; index++)
            {
                writer.WriteStartElement("sheet", SpreadsheetNamespace);
                writer.WriteAttributeString("name", sheets[index].Name);
                writer.WriteAttributeString(
                    "sheetId",
                    (index + 1).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString(
                    "r",
                    "id",
                    OfficeRelationshipNamespace,
                    $"rId{index + 1}");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        });
    }

    private static void WriteWorkbookRelationships(
        ZipArchive archive,
        int worksheetCount)
    {
        WriteXmlEntry(archive, "xl/_rels/workbook.xml.rels", writer =>
        {
            writer.WriteStartElement("Relationships", PackageRelationshipNamespace);
            for (var index = 1; index <= worksheetCount; index++)
            {
                WriteRelationship(
                    writer,
                    $"rId{index}",
                    "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet",
                    $"worksheets/sheet{index}.xml");
            }

            WriteRelationship(
                writer,
                $"rId{worksheetCount + 1}",
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles",
                "styles.xml");
            writer.WriteEndElement();
        });
    }

    private static void WriteRelationship(
        XmlWriter writer,
        string id,
        string type,
        string target)
    {
        writer.WriteStartElement("Relationship", PackageRelationshipNamespace);
        writer.WriteAttributeString("Id", id);
        writer.WriteAttributeString("Type", type);
        writer.WriteAttributeString("Target", target);
        writer.WriteEndElement();
    }

    private static void WriteStyles(ZipArchive archive)
    {
        WriteXmlEntry(archive, "xl/styles.xml", writer =>
        {
            writer.WriteStartElement("styleSheet", SpreadsheetNamespace);

            writer.WriteStartElement("fonts", SpreadsheetNamespace);
            writer.WriteAttributeString("count", "3");
            WriteFont(writer, bold: false, white: false, size: 11);
            WriteFont(writer, bold: true, white: true, size: 11);
            WriteFont(writer, bold: true, white: false, size: 16);
            writer.WriteEndElement();

            writer.WriteStartElement("fills", SpreadsheetNamespace);
            writer.WriteAttributeString("count", "3");
            WritePatternFill(writer, "none", null);
            WritePatternFill(writer, "gray125", null);
            WritePatternFill(writer, "solid", "FF1F4E78");
            writer.WriteEndElement();

            writer.WriteStartElement("borders", SpreadsheetNamespace);
            writer.WriteAttributeString("count", "1");
            writer.WriteStartElement("border", SpreadsheetNamespace);
            writer.WriteElementString("left", SpreadsheetNamespace, string.Empty);
            writer.WriteElementString("right", SpreadsheetNamespace, string.Empty);
            writer.WriteElementString("top", SpreadsheetNamespace, string.Empty);
            writer.WriteElementString("bottom", SpreadsheetNamespace, string.Empty);
            writer.WriteElementString("diagonal", SpreadsheetNamespace, string.Empty);
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteStartElement("cellStyleXfs", SpreadsheetNamespace);
            writer.WriteAttributeString("count", "1");
            WriteXf(writer, 0, 0, applyAlignment: false);
            writer.WriteEndElement();

            writer.WriteStartElement("cellXfs", SpreadsheetNamespace);
            writer.WriteAttributeString("count", "3");
            WriteXf(writer, 0, 0, applyAlignment: false);
            WriteXf(writer, 1, 2, applyAlignment: true);
            WriteXf(writer, 2, 0, applyAlignment: false);
            writer.WriteEndElement();

            writer.WriteStartElement("cellStyles", SpreadsheetNamespace);
            writer.WriteAttributeString("count", "1");
            writer.WriteStartElement("cellStyle", SpreadsheetNamespace);
            writer.WriteAttributeString("name", "Normal");
            writer.WriteAttributeString("xfId", "0");
            writer.WriteAttributeString("builtinId", "0");
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteEndElement();
        });
    }

    private static void WriteFont(
        XmlWriter writer,
        bool bold,
        bool white,
        int size)
    {
        writer.WriteStartElement("font", SpreadsheetNamespace);
        if (bold)
        {
            writer.WriteElementString("b", SpreadsheetNamespace, string.Empty);
        }

        writer.WriteStartElement("sz", SpreadsheetNamespace);
        writer.WriteAttributeString("val", size.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteStartElement("color", SpreadsheetNamespace);
        writer.WriteAttributeString("rgb", white ? "FFFFFFFF" : "FF000000");
        writer.WriteEndElement();
        writer.WriteStartElement("name", SpreadsheetNamespace);
        writer.WriteAttributeString("val", "Calibri");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WritePatternFill(
        XmlWriter writer,
        string patternType,
        string? foregroundColor)
    {
        writer.WriteStartElement("fill", SpreadsheetNamespace);
        writer.WriteStartElement("patternFill", SpreadsheetNamespace);
        writer.WriteAttributeString("patternType", patternType);
        if (foregroundColor is not null)
        {
            writer.WriteStartElement("fgColor", SpreadsheetNamespace);
            writer.WriteAttributeString("rgb", foregroundColor);
            writer.WriteEndElement();
            writer.WriteStartElement("bgColor", SpreadsheetNamespace);
            writer.WriteAttributeString("indexed", "64");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteXf(
        XmlWriter writer,
        int fontId,
        int fillId,
        bool applyAlignment)
    {
        writer.WriteStartElement("xf", SpreadsheetNamespace);
        writer.WriteAttributeString("numFmtId", "0");
        writer.WriteAttributeString(
            "fontId",
            fontId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString(
            "fillId",
            fillId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("borderId", "0");
        writer.WriteAttributeString("xfId", "0");
        if (fontId != 0)
        {
            writer.WriteAttributeString("applyFont", "1");
        }

        if (fillId != 0)
        {
            writer.WriteAttributeString("applyFill", "1");
        }

        if (applyAlignment)
        {
            writer.WriteAttributeString("applyAlignment", "1");
            writer.WriteStartElement("alignment", SpreadsheetNamespace);
            writer.WriteAttributeString("horizontal", "center");
            writer.WriteAttributeString("vertical", "center");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteWorksheet(
        ZipArchive archive,
        int index,
        WorkbookSheet sheet)
    {
        WriteXmlEntry(archive, $"xl/worksheets/sheet{index}.xml", writer =>
        {
            writer.WriteStartElement("worksheet", SpreadsheetNamespace);
            var rowCount = Math.Max(sheet.Rows.Count, 1);
            var columnCount = Math.Max(
                sheet.Rows.Select(static row => row.Values.Count).DefaultIfEmpty(1).Max(),
                1);
            writer.WriteStartElement("dimension", SpreadsheetNamespace);
            writer.WriteAttributeString(
                "ref",
                $"A1:{GetColumnName(columnCount)}{rowCount}");
            writer.WriteEndElement();

            writer.WriteStartElement("sheetViews", SpreadsheetNamespace);
            writer.WriteStartElement("sheetView", SpreadsheetNamespace);
            writer.WriteAttributeString("workbookViewId", "0");
            if (sheet.FreezeRow > 0)
            {
                writer.WriteStartElement("pane", SpreadsheetNamespace);
                writer.WriteAttributeString(
                    "ySplit",
                    sheet.FreezeRow.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("topLeftCell", $"A{sheet.FreezeRow + 1}");
                writer.WriteAttributeString("activePane", "bottomLeft");
                writer.WriteAttributeString("state", "frozen");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteStartElement("sheetFormatPr", SpreadsheetNamespace);
            writer.WriteAttributeString("defaultRowHeight", "15");
            writer.WriteEndElement();

            writer.WriteStartElement("cols", SpreadsheetNamespace);
            for (var column = 0; column < sheet.ColumnWidths.Count; column++)
            {
                writer.WriteStartElement("col", SpreadsheetNamespace);
                writer.WriteAttributeString(
                    "min",
                    (column + 1).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString(
                    "max",
                    (column + 1).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString(
                    "width",
                    sheet.ColumnWidths[column].ToString(
                        "0.##",
                        CultureInfo.InvariantCulture));
                writer.WriteAttributeString("customWidth", "1");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();

            writer.WriteStartElement("sheetData", SpreadsheetNamespace);
            for (var rowIndex = 0; rowIndex < sheet.Rows.Count; rowIndex++)
            {
                var row = sheet.Rows[rowIndex];
                writer.WriteStartElement("row", SpreadsheetNamespace);
                writer.WriteAttributeString(
                    "r",
                    (rowIndex + 1).ToString(CultureInfo.InvariantCulture));
                for (var columnIndex = 0;
                     columnIndex < row.Values.Count;
                     columnIndex++)
                {
                    WriteCell(
                        writer,
                        rowIndex + 1,
                        columnIndex + 1,
                        row.Values[columnIndex],
                        row.StyleIndex,
                        row.NumericColumns?.Contains(columnIndex) == true);
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();

            if (sheet.AutoFilterRow > 0
                && sheet.Rows.Count >= sheet.AutoFilterRow)
            {
                writer.WriteStartElement("autoFilter", SpreadsheetNamespace);
                writer.WriteAttributeString(
                    "ref",
                    $"A{sheet.AutoFilterRow}:{GetColumnName(columnCount)}{rowCount}");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        });
    }

    private static void WriteCell(
        XmlWriter writer,
        int row,
        int column,
        string value,
        int styleIndex,
        bool numeric)
    {
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", $"{GetColumnName(column)}{row}");
        if (styleIndex > 0)
        {
            writer.WriteAttributeString(
                "s",
                styleIndex.ToString(CultureInfo.InvariantCulture));
        }

        if (numeric)
        {
            writer.WriteAttributeString("t", "n");
            writer.WriteElementString("v", SpreadsheetNamespace, value);
        }
        else
        {
            writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is", SpreadsheetNamespace);
            writer.WriteStartElement("t", SpreadsheetNamespace);
            writer.WriteString(NormalizeCellText(value));
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static string NormalizeCellText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, 32767));
        foreach (var character in value)
        {
            if (builder.Length >= 32767)
            {
                break;
            }

            if (XmlConvert.IsXmlChar(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string GetColumnName(int oneBasedColumn)
    {
        var result = new StringBuilder();
        var value = oneBasedColumn;
        while (value > 0)
        {
            value--;
            result.Insert(0, (char)('A' + (value % 26)));
            value /= 26;
        }

        return result.ToString();
    }

    private static void WriteXmlEntry(
        ZipArchive archive,
        string path,
        Action<XmlWriter> write)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(
            stream,
            new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = true,
                CloseOutput = false
            });
        writer.WriteStartDocument();
        write(writer);
        writer.WriteEndDocument();
    }

    private sealed record WorkbookSheet(
        string Name,
        IReadOnlyList<WorkbookRow> Rows,
        IReadOnlyList<double> ColumnWidths,
        int FreezeRow,
        int AutoFilterRow);

    private sealed record WorkbookRow(
        IReadOnlyList<string> Values,
        int StyleIndex,
        IReadOnlyCollection<int>? NumericColumns = null);
}
