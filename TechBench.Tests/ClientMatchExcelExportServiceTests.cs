using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ExcelDataReader;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class ClientMatchExcelExportServiceTests
{
    [Fact]
    public void BuildsWorkbookWithAuditAndCategoryWorksheets()
    {
        var clients = TestClients();

        var workbook = ClientMatchExcelExportService.BuildWorkbook(
            clients,
            new DateTimeOffset(2026, 7, 30, 14, 30, 0, TimeSpan.FromHours(-4)));

        using var stream = new MemoryStream(workbook);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
        Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
        Assert.NotNull(archive.GetEntry("xl/styles.xml"));
        Assert.Equal(6, archive.Entries.Count(entry =>
            entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)));

        var workbookXml = ReadXml(archive, "xl/workbook.xml");
        XNamespace spreadsheet =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sheetNames = workbookXml
            .Descendants(spreadsheet + "sheet")
            .Select(element => element.Attribute("name")?.Value ?? string.Empty)
            .ToArray();
        Assert.Equal(
            ["Summary", "Matched", "WHD Only", "Sage Only", "Manual Other", "All Clients"],
            sheetNames);
    }

    [Fact]
    public void CategoryWorksheetsContainOnlyTheirClientGroups()
    {
        var workbook = ClientMatchExcelExportService.BuildWorkbook(TestClients());

        using var stream = new MemoryStream(workbook);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var matched = ReadXml(archive, "xl/worksheets/sheet2.xml").ToString();
        var whdOnly = ReadXml(archive, "xl/worksheets/sheet3.xml").ToString();
        var sageOnly = ReadXml(archive, "xl/worksheets/sheet4.xml").ToString();
        var allClients = ReadXml(archive, "xl/worksheets/sheet6.xml").ToString();

        Assert.Contains("Marrone &amp; O'Rourke", matched, StringComparison.Ordinal);
        Assert.DoesNotContain("WHD Only Client", matched, StringComparison.Ordinal);
        Assert.Contains("WHD Only Client", whdOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("Sage Only Client", whdOnly, StringComparison.Ordinal);
        Assert.Contains("Sage Only Client", sageOnly, StringComparison.Ordinal);
        Assert.Contains("Manual Client", allClients, StringComparison.Ordinal);
    }

    [Fact]
    public void CategorizesClientsByTheirAuthoritativeSourceState()
    {
        var clients = TestClients();

        Assert.Equal(
            ClientMatchExportCategory.Matched,
            ClientMatchExcelExportService.GetCategory(clients[0]));
        Assert.Equal(
            ClientMatchExportCategory.WhdOnly,
            ClientMatchExcelExportService.GetCategory(clients[1]));
        Assert.Equal(
            ClientMatchExportCategory.SageOnly,
            ClientMatchExcelExportService.GetCategory(clients[2]));
        Assert.Equal(
            ClientMatchExportCategory.ManualOrOther,
            ClientMatchExcelExportService.GetCategory(clients[3]));
    }

    [Fact]
    public void GeneratedWorkbookCanBeReadByAnExcelWorkbookParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var workbook = ClientMatchExcelExportService.BuildWorkbook(TestClients());

        using var stream = new MemoryStream(workbook);
        using var reader = ExcelReaderFactory.CreateOpenXmlReader(stream);
        var sheetNames = new List<string>();
        do
        {
            sheetNames.Add(reader.Name);
            Assert.True(reader.Read());
        }
        while (reader.NextResult());

        Assert.Equal(
            ["Summary", "Matched", "WHD Only", "Sage Only", "Manual Other", "All Clients"],
            sheetNames);
    }

    private static XDocument ReadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path)
            ?? throw new InvalidOperationException($"Missing workbook part {path}.");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static List<Client> TestClients() =>
    [
        new()
        {
            Id = 1,
            Name = "Marrone & O'Rourke",
            Source = "Both",
            IsActive = true,
            ExternalId = "WHD-LOCATION-463",
            WhdLocationName = "Marrone & O'Rourke LLP",
            SageCustomerId = "69832",
            SageCustomerName = "Marrone & O'Rourke",
            MatchStatus = "Matched"
        },
        new()
        {
            Id = 2,
            Name = "WHD Only Client",
            Source = "WHD",
            IsActive = true,
            ExternalId = "WHD-LOCATION-2",
            WhdLocationName = "WHD Only Client"
        },
        new()
        {
            Id = 3,
            Name = "Sage Only Client",
            Source = "Sage",
            IsActive = true,
            SageCustomerId = "300",
            SageCustomerName = "Sage Only Client"
        },
        new()
        {
            Id = 4,
            Name = "Manual Client",
            Source = "Manual",
            IsActive = false
        }
    ];
}
