using System.IO;
using System.IO.Compression;
using System.Text;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class EquipmentBuildSheetImporterTests
{
    [Fact]
    public void PcConfigurationSheetMapsOnlyOperationalInventoryFields()
    {
        IReadOnlyList<string>[] rows =
        [
            ["PC Configuration Sheet", "", "", "", "", "", "", ""],
            ["Customer - Marrone & O'Rourke", "", "", "", "", "", "", ""],
            ["Machine - D4EB2UT - Elitebook 6", "", "S/N:", "5CD6231QNN", "", "", "", ""],
            ["Machine Name: MO-2026-JS", "", "", "", "", "", "", ""],
            ["End User- Jacqueline L. Summers", "", "", "", "All Components Available (Y/N)", "", "", "yes"],
            ["Email Address: jacquie@marroneorourke.com", "", "", "", "", "", "", ""],
            ["Build Date: 7/13/26", "", "", "", "Is there any image of this machine (Y/N)", "", "", "no"],
            ["Delivery Date: Week of 7/27/26", "", "", "", "", "", "", ""]
        ];

        var import = EquipmentBuildSheetImporter.ParseRows(
            rows,
            "MO-2026-JS.xlsx");

        Assert.Equal("Marrone & O'Rourke", import.Customer);
        Assert.Equal("D4EB2UT - Elitebook 6", import.Machine);
        Assert.Equal("D4EB2UT", import.PartNumber);
        Assert.Equal("Elitebook 6", import.Model);
        Assert.Equal("Laptop", import.DeviceType);
        Assert.Equal("MO-2026-JS", import.MachineName);
        Assert.Equal("5CD6231QNN", import.SerialNumber);
        Assert.Equal("Jacqueline L. Summers", import.EndUser);
        Assert.Equal("jacquie@marroneorourke.com", import.EmailAddress);
        Assert.Equal("MO-2026-JS.xlsx", import.SourceFileName);
    }

    [Fact]
    public void XlsxReaderHandlesMergedTemplateLabels()
    {
        var fileName = Path.Combine(
            Path.GetTempPath(),
            $"techbench-build-sheet-{Guid.NewGuid():N}.xlsx");
        try
        {
            WriteSampleWorkbook(fileName);

            var import = new EquipmentBuildSheetImporter().Read(fileName);

            Assert.Equal("Marrone & O'Rourke", import.Customer);
            Assert.Equal("MO-2026-JS", import.MachineName);
            Assert.Equal("5CD6231QNN", import.SerialNumber);
            Assert.Equal("D4EB2UT", import.PartNumber);
            Assert.Equal("Elitebook 6", import.Model);
        }
        finally
        {
            if (File.Exists(fileName))
            {
                File.Delete(fileName);
            }
        }
    }

    [Fact]
    public void ParserFindsValuesInLaterCellsInsteadOfFixedCoordinates()
    {
        IReadOnlyList<string>[] rows =
        [
            ["Customer:", "", "", "Acme Services"],
            ["Machine", "", "OptiPlex 7020"],
            ["Serial Number", "SERIAL-42"],
            ["Machine Name", "", "", "", "ACME-FRONT-01"],
            ["End User", "", "Dana Brooks"],
            ["Email Address", "dana@example.test"]
        ];

        var import = EquipmentBuildSheetImporter.ParseRows(rows);

        Assert.Equal("Acme Services", import.Customer);
        Assert.Equal("OptiPlex 7020", import.Model);
        Assert.Equal("Desktop", import.DeviceType);
        Assert.Equal("SERIAL-42", import.SerialNumber);
        Assert.Equal("ACME-FRONT-01", import.MachineName);
        Assert.Equal("Dana Brooks", import.EndUser);
        Assert.Equal("dana@example.test", import.EmailAddress);
    }

    [Fact]
    public void ClientAndUserMatchingStaysInsideTheImportedCustomer()
    {
        var selectedClient = new InventoryClient
        {
            ClientId = 7,
            Name = "Marrone and O'Rourke, Inc."
        };
        selectedClient.Users.Add(new InventoryClientUser
        {
            ClientUserId = 71,
            ClientId = 7,
            DisplayName = "Jacqueline Summers",
            Email = "jacquie@marroneorourke.com"
        });
        var otherClient = new InventoryClient
        {
            ClientId = 8,
            Name = "Another Client"
        };
        otherClient.Users.Add(new InventoryClientUser
        {
            ClientUserId = 81,
            ClientId = 8,
            DisplayName = "Jacqueline Summers",
            Email = "jacquie@marroneorourke.com"
        });
        var import = new EquipmentBuildSheetImport(
            "Marrone & O'Rourke",
            "D4EB2UT - Elitebook 6",
            "MO-2026-JS",
            "5CD6231QNN",
            "Jacqueline L. Summers",
            "jacquie@marroneorourke.com",
            "D4EB2UT",
            "Elitebook 6",
            "Laptop",
            "build-sheet.xlsx");

        var client = EquipmentBuildSheetImporter.FindClient(
            import.Customer,
            [otherClient, selectedClient]);
        var user = EquipmentBuildSheetImporter.FindClientUser(import, client);

        Assert.Same(selectedClient, client);
        Assert.Equal(71, user?.ClientUserId);
    }

    [Fact]
    public void ImportedEndUserFindsTheClosestActiveUserInsideMatchedClient()
    {
        IReadOnlyList<string>[] rows =
        [
            ["Customer - Marrone & O'Rourke"],
            ["Machine Name: MO-2026-LM"],
            ["End User- Licia Marrone"],
            ["Email Address: licia@marroneorourke.com"]
        ];
        var import = EquipmentBuildSheetImporter.ParseRows(rows);
        var client = new InventoryClient
        {
            ClientId = 7,
            Name = "Teeters Harvey Marrone & O'Rourke LLP"
        };
        client.Users.Add(new InventoryClientUser
        {
            ClientUserId = 70,
            ClientId = 7,
            DisplayName = "Licia Marrone",
            IsActive = false
        });
        client.Users.Add(new InventoryClientUser
        {
            ClientUserId = 71,
            ClientId = 7,
            DisplayName = "Licia A. Marrone"
        });
        client.Users.Add(new InventoryClientUser
        {
            ClientUserId = 72,
            ClientId = 7,
            DisplayName = "Linda Marrone"
        });

        var match = EquipmentBuildSheetImporter.FindClientUser(
            import,
            client);

        Assert.Equal(71, match?.ClientUserId);
    }

    [Fact]
    public void EquallyPlausibleEndUsersRemainUnmatched()
    {
        var import = new EquipmentBuildSheetImport(
            "Marrone & O'Rourke",
            "D4EB2UT - Elitebook 6",
            "MO-2026-LM",
            "5CD6231QB3",
            "Licia Marrone",
            string.Empty,
            "D4EB2UT",
            "Elitebook 6",
            "Laptop",
            "build-sheet.xlsx");
        var client = new InventoryClient
        {
            ClientId = 7,
            Name = "Teeters Harvey Marrone & O'Rourke LLP"
        };
        client.Users.Add(new InventoryClientUser
        {
            ClientUserId = 71,
            ClientId = 7,
            DisplayName = "Licia A. Marrone"
        });
        client.Users.Add(new InventoryClientUser
        {
            ClientUserId = 72,
            ClientId = 7,
            DisplayName = "Licia B. Marrone"
        });

        var match = EquipmentBuildSheetImporter.FindClientUser(
            import,
            client);

        Assert.Null(match);
    }

    [Fact]
    public void CustomerMatchIgnoresConnectorsAndPrefersTheClosestClientName()
    {
        var directClient = new InventoryClient
        {
            ClientId = 7,
            Name = "Marrone O'Rourke"
        };
        var expandedClient = new InventoryClient
        {
            ClientId = 8,
            Name = "Teeters Harvey Marrone & O'Rourke LLP"
        };

        var match = EquipmentBuildSheetImporter.FindClient(
            "Marrone and O'Rourke",
            [expandedClient, directClient]);

        Assert.Same(directClient, match);
    }

    [Fact]
    public void CustomerMatchUsesConfidentSharedWordsInLongerClientName()
    {
        var expectedClient = new InventoryClient
        {
            ClientId = 8,
            Name = "Teeters Harvey Marrone & O'Rourke LLP"
        };
        var unrelatedClient = new InventoryClient
        {
            ClientId = 9,
            Name = "Teeters Harvey Smith & Jones LLP"
        };

        var match = EquipmentBuildSheetImporter.FindClient(
            "Marrone & O'Rourke",
            [unrelatedClient, expectedClient]);

        Assert.Same(expectedClient, match);
    }

    [Fact]
    public void BuildSheetImportNeverAddsUnmatchedValuesToEquipmentNotes()
    {
        var source = ReadRepositoryFile(
            "ViewModels/MainWindowViewModel.Equipment.cs");
        var normalizedSource = source.Replace("\r\n", "\n");

        Assert.Contains("EquipmentNotes = string.Empty;", source);
        Assert.DoesNotContain("Build sheet customer:", source);
        Assert.DoesNotContain("Build sheet end user:", source);
        Assert.Contains(
            "EquipmentClientUser = clientUser;\n        EquipmentLocationName = string.Empty;",
            normalizedSource);
    }

    [Fact]
    public void ConflictingLabeledValuesAreRejected()
    {
        IReadOnlyList<string>[] rows =
        [
            ["Machine Name: PC-ONE"],
            ["Machine Name: PC-TWO"]
        ];

        var exception = Assert.Throws<InvalidDataException>(
            () => EquipmentBuildSheetImporter.ParseRows(rows));

        Assert.Contains("Machine Name", exception.Message);
    }

    [Fact]
    public void InventoryNavigationSeparatesRegistryAndEquipmentBoard()
    {
        var mainWindowXaml = ReadRepositoryFile("MainWindow.xaml");
        var equipmentViewModel = ReadRepositoryFile(
            "ViewModels/MainWindowViewModel.Equipment.cs");

        Assert.Contains("Content=\"INVENTORY\"", mainWindowXaml);
        Assert.Contains("CommandParameter=\"Inventory\"", mainWindowXaml);
        Assert.Contains("Style=\"{StaticResource NavInventoryStyle}\"", mainWindowXaml);
        Assert.Contains("Content=\"Equipment Board\"", mainWindowXaml);
        Assert.Contains("CommandParameter=\"Equipment Board\"", mainWindowXaml);
        Assert.Contains("ConverterParameter=Inventory", mainWindowXaml);
        Assert.Contains("ConverterParameter=Equipment Board", mainWindowXaml);
        Assert.Contains("Text=\"All Equipment\"", mainWindowXaml);
        Assert.Contains(
            "ItemsSource=\"{Binding InventoryEquipmentItems}\"",
            mainWindowXaml);
        Assert.Contains("InventoryEquipmentSearchText", mainWindowXaml);
        Assert.Contains("InventoryStockOnly", mainWindowXaml);
        Assert.Contains("x:Name=\"EquipmentQuickViewPanel\"", mainWindowXaml);
        Assert.Contains(
            "Visibility=\"{Binding IsEquipmentQuickViewVisible",
            mainWindowXaml);
        Assert.DoesNotContain(
            "CurrentSection = \"Equipment Board\";",
            equipmentViewModel);
        Assert.Contains(
            "without leaving {CurrentSection}",
            equipmentViewModel);
        Assert.Contains(
            "RebuildInventoryEquipmentRegistry(equipment);",
            equipmentViewModel);
        Assert.Contains(
            "EquipmentInventoryFilter.Matches(",
            equipmentViewModel);
        Assert.Contains("Content=\"Import build sheet\"", mainWindowXaml);
        Assert.Contains(
            "Command=\"{Binding ImportEquipmentBuildSheetCommand}\"",
            mainWindowXaml);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(
                   directory.FullName,
                   "TechBenchV2.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }

    private static void WriteSampleWorkbook(string fileName)
    {
        using var archive = ZipFile.Open(fileName, ZipArchiveMode.Create);
        WriteArchiveEntry(
            archive,
            "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        WriteArchiveEntry(
            archive,
            "_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteArchiveEntry(
            archive,
            "xl/workbook.xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="PC Configuration" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        WriteArchiveEntry(
            archive,
            "xl/_rels/workbook.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        WriteArchiveEntry(
            archive,
            "xl/worksheets/sheet1.xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <dimension ref="A1:H8"/>
              <sheetData>
                <row r="2"><c r="A2" t="inlineStr"><is><t>Customer - Marrone &amp; O'Rourke</t></is></c></row>
                <row r="3"><c r="A3" t="inlineStr"><is><t>Machine - D4EB2UT - Elitebook 6</t></is></c><c r="C3" t="inlineStr"><is><t>S/N:</t></is></c><c r="D3" t="inlineStr"><is><t>5CD6231QNN</t></is></c></row>
                <row r="4"><c r="A4" t="inlineStr"><is><t>Machine Name: MO-2026-JS</t></is></c></row>
                <row r="5"><c r="A5" t="inlineStr"><is><t>End User- Jacqueline L. Summers</t></is></c></row>
                <row r="6"><c r="A6" t="inlineStr"><is><t>Email Address: jacquie@marroneorourke.com</t></is></c></row>
              </sheetData>
              <mergeCells count="4">
                <mergeCell ref="A2:B2"/>
                <mergeCell ref="A3:B3"/>
                <mergeCell ref="A4:B4"/>
                <mergeCell ref="A5:B5"/>
              </mergeCells>
            </worksheet>
            """);
    }

    private static void WriteArchiveEntry(
        ZipArchive archive,
        string name,
        string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(
            entry.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
