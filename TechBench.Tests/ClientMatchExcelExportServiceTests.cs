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
        Assert.Equal(7, archive.Entries.Count(entry =>
            entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)));

        var workbookXml = ReadXml(archive, "xl/workbook.xml");
        XNamespace spreadsheet =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sheetNames = workbookXml
            .Descendants(spreadsheet + "sheet")
            .Select(element => element.Attribute("name")?.Value ?? string.Empty)
            .ToArray();
        Assert.Equal(
            ["Summary", "Fully Linked", "TB WHD", "TB Sage", "TB Only", "Source Only", "All Clients"],
            sheetNames);
    }

    [Fact]
    public void CategoryWorksheetsContainOnlyTheirClientGroups()
    {
        var workbook = ClientMatchExcelExportService.BuildWorkbook(TestClients());

        using var stream = new MemoryStream(workbook);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var fullyLinked = ReadXml(archive, "xl/worksheets/sheet2.xml").ToString();
        var techBenchWhd = ReadXml(archive, "xl/worksheets/sheet3.xml").ToString();
        var techBenchSage = ReadXml(archive, "xl/worksheets/sheet4.xml").ToString();
        var sourceOnly = ReadXml(archive, "xl/worksheets/sheet6.xml").ToString();
        var allClients = ReadXml(archive, "xl/worksheets/sheet7.xml").ToString();

        Assert.Contains("Marrone &amp; O'Rourke", fullyLinked, StringComparison.Ordinal);
        Assert.DoesNotContain("TB WHD Client", fullyLinked, StringComparison.Ordinal);
        Assert.Contains("TB WHD Client", techBenchWhd, StringComparison.Ordinal);
        Assert.DoesNotContain("TB Sage Client", techBenchWhd, StringComparison.Ordinal);
        Assert.Contains("TB Sage Client", techBenchSage, StringComparison.Ordinal);
        Assert.Contains("WHD Source Record", sourceOnly, StringComparison.Ordinal);
        Assert.Contains("Manual Client", allClients, StringComparison.Ordinal);
    }

    [Fact]
    public void CategorizesClientsByTheirAuthoritativeSourceState()
    {
        var clients = TestClients();

        Assert.Equal(
            ClientMatchExportCategory.FullyLinked,
            ClientMatchExcelExportService.GetCategory(clients[0]));
        Assert.Equal(
            ClientMatchExportCategory.TechBenchWhd,
            ClientMatchExcelExportService.GetCategory(clients[1]));
        Assert.Equal(
            ClientMatchExportCategory.TechBenchSage,
            ClientMatchExcelExportService.GetCategory(clients[2]));
        Assert.Equal(
            ClientMatchExportCategory.TechBenchOnly,
            ClientMatchExcelExportService.GetCategory(clients[3]));
        Assert.Equal(
            ClientMatchExportCategory.SourceOnly,
            ClientMatchExcelExportService.GetCategory(clients[4]));
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
            ["Summary", "Fully Linked", "TB WHD", "TB Sage", "TB Only", "Source Only", "All Clients"],
            sheetNames);
    }

    [Fact]
    public void SummaryCountsMatchedClientsInBothSourceImportTotals()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var workbook = ClientMatchExcelExportService.BuildWorkbook(TestClients());

        using var stream = new MemoryStream(workbook);
        using var reader = ExcelReaderFactory.CreateOpenXmlReader(stream);
        var summaryTotals = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var label = reader.GetValue(0)?.ToString();
            if (!string.IsNullOrWhiteSpace(label)
                && !label.Equals("Category", StringComparison.Ordinal)
                && reader.GetValue(1) is not null)
            {
                summaryTotals[label] = Convert.ToInt32(reader.GetValue(1));
            }
        }

        Assert.Equal(3, summaryTotals["WHD identities (all link states)"]);
        Assert.Equal(2, summaryTotals["Sage identities (all link states)"]);
        Assert.Equal(3, summaryTotals["Live TechBench clients"]);
        Assert.Equal(1, summaryTotals["Fully linked"]);
        Assert.Equal(1, summaryTotals["TB + WHD"]);
        Assert.Equal(1, summaryTotals["TB + Sage"]);
        Assert.Equal(0, summaryTotals["TB only"]);
        Assert.Equal(1, summaryTotals["Source only / needs review"]);
    }

    [Fact]
    public void SummaryOmitsInactiveCountsWhileDetailSheetsRetainInactiveRows()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var workbook = ClientMatchExcelExportService.BuildWorkbook(TestClients());

        using var stream = new MemoryStream(workbook);
        using var reader = ExcelReaderFactory.CreateOpenXmlReader(stream);
        var summaryRows = new List<string>();
        while (reader.Read())
        {
            summaryRows.Add(
                string.Join(
                    "|",
                    Enumerable.Range(0, reader.FieldCount)
                        .Select(index => reader.GetValue(index)?.ToString() ?? string.Empty)));
        }

        Assert.Contains("Category|Current total", summaryRows);
        Assert.DoesNotContain(
            summaryRows,
            row => row.Contains("Inactive", StringComparison.OrdinalIgnoreCase)
                && !row.Contains(
                    "Inactive records remain on the detail tabs",
                    StringComparison.Ordinal));

        while (reader.NextResult())
        {
            if (!reader.Name.Equals("All Clients", StringComparison.Ordinal))
            {
                continue;
            }

            var foundInactiveClient = false;
            while (reader.Read())
            {
                foundInactiveClient |=
                    reader.GetValue(1)?.ToString() == "Manual Client"
                    && reader.GetValue(6)?.ToString() == "No";
            }

            Assert.True(foundInactiveClient);
        }
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
            MatchStatus = "Matched",
            IsClientInfoLive = true,
            HasWhdIdentity = true,
            HasSageIdentity = true,
            ClientInfoReviewStatus = "Verified"
        },
        new()
        {
            Id = 2,
            Name = "TB WHD Client",
            Source = "WHD",
            IsActive = true,
            ExternalId = "WHD-LOCATION-2",
            WhdLocationName = "TB WHD Client",
            IsClientInfoLive = true,
            HasWhdIdentity = true,
            ClientInfoReviewStatus = "Verified"
        },
        new()
        {
            Id = 3,
            Name = "TB Sage Client",
            Source = "Sage",
            IsActive = true,
            SageCustomerId = "300",
            SageCustomerName = "TB Sage Client",
            IsClientInfoLive = true,
            HasSageIdentity = true,
            ClientInfoReviewStatus = "Verified"
        },
        new()
        {
            Id = 4,
            Name = "Manual Client",
            Source = "Manual",
            IsActive = false,
            IsClientInfoLive = true,
            ClientInfoReviewStatus = "Verified"
        },
        new()
        {
            Id = 5,
            Name = "WHD Source Record",
            Source = "WHD",
            IsActive = true,
            ExternalId = "WHD-LOCATION-5",
            WhdLocationName = "WHD Source Record",
            HasWhdIdentity = true
        }
    ];
}
