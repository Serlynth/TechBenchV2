using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using TechBench.Data;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class ClientInfoBetaTests
{
    [Fact]
    public void GeneratedWorkbookRoundTripsTheInternalClientIdentity()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Acme Client Info.xlsx");
            var service = new ClientInfoWorkbookService();

            service.CreateTemplate(path, 477, "Acme Legal");
            var package = service.Read(path);

            Assert.Equal(ClientInfoWorkbookService.TemplateVersion, package.TemplateVersion);
            Assert.Equal(477, package.ClientId);
            Assert.Equal("Acme Legal", package.ClientName);
            Assert.NotEqual(Guid.Empty, package.WorkbookId);
            Assert.Equal(32, package.ContentSha256.Length);
            var profile = Assert.Single(package.Records);
            Assert.Equal("Profile", profile.RecordType);
            Assert.Equal("Verified", profile.ReviewStatus);
            Assert.Empty(package.Secrets);

            using var workbook = SpreadsheetDocument.Open(path, false);
            var names = workbook.WorkbookPart!.Workbook.Sheets!
                .Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
                .Select(sheet => sheet.Name?.Value ?? string.Empty)
                .ToArray();
            Assert.Equal(
                [
                    "Start Here",
                    "Locations",
                    "People",
                    "Systems & Services",
                    "Equipment",
                    "Passwords",
                    "Other Info"
                ],
                names);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FriendlyWorkbookRowsMapToCanonicalRecordsWithoutTechnicalKeys()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Friendly Migration.xlsx");
            var service = new ClientInfoWorkbookService();
            service.CreateTemplate(path, 477, "Acme Legal");

            AppendRow(
                path,
                "Locations",
                "Main Office", "Office", "100 Main Street", "", "Malvern",
                "PA", "19355", "610-555-0100", "Eastern Standard Time",
                "Yes", "Verified");
            AppendRow(
                path,
                "People",
                "Jamie Rivera", "Office Manager", "jamie@example.test",
                "610-555-0101", "", "Main Office", "Primary", "Yes",
                "Verified");
            AppendRow(
                path,
                "Systems & Services",
                "Firewall", "WatchGuard", "WatchGuard", "https://firewall.test",
                "Main Office", "Active", "Primary firewall", "Verified");
            AppendRow(
                path,
                "Equipment",
                "Desktop", "Reception PC", "Dell", "OptiPlex", "SN-100", "",
                "TB-100", "10.0.0.10", "Main Office", "Front desk", "Verified");
            AppendRow(
                path,
                "Passwords",
                "Firewall Admin", "Firewall", "admin", "P@ss word",
                "https://firewall.test", "WatchGuard", "Emergency admin",
                "Password", "Admin password", "Verified");
            AppendRow(
                path,
                "Other Info",
                "Operations", "After-hours procedure", "Call the office manager",
                "Keep as-is");

            var package = service.Read(path);

            Assert.Equal(7, package.Records.Count);
            Assert.Contains(package.Records, record => record.RecordType == "Profile");
            var location = Assert.Single(
                package.Records,
                record => record.RecordType == "Location");
            var person = Assert.Single(
                package.Records,
                record => record.RecordType == "Person");
            var resource = Assert.Single(
                package.Records,
                record => record.RecordType == "Resource");
            var credential = Assert.Single(
                package.Records,
                record => record.RecordType == "Credential");
            Assert.Single(
                package.Records,
                record => record.RecordType == "Equipment");
            var fact = Assert.Single(
                package.Records,
                record => record.RecordType == "Fact");
            Assert.Equal("AcceptedUnverified", fact.ReviewStatus);
            Assert.Equal(location.LocalKey, person.ParentLocalKey);
            Assert.Contains(location.LocalKey, resource.PayloadJson, StringComparison.Ordinal);
            Assert.Contains(resource.LocalKey, credential.PayloadJson, StringComparison.Ordinal);
            var secret = Assert.Single(package.Secrets);
            Assert.Equal(credential.LocalKey, secret.CredentialLocalKey);
            Assert.Equal("P@ss word", secret.SecretValue);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BetaSchemaIsAdditiveAndKeepsStableClientsAtSchemaFifteen()
    {
        var schema = Read("database", "sqlserver2016", "36-V0015-ClientInfoBetaSchema.sql");
        var procedures = Read(
            "database",
            "sqlserver2016",
            "61-V0015-ClientInfoBetaProcedures.sql");
        var imports = Read(
            "database",
            "sqlserver2016",
            "62-V0015-ClientInfoBetaImportProcedures.sql");
        var verifier = Read(
            "database",
            "sqlserver2016",
            "106-V0015-ClientInfoBetaVerify.sql");

        Assert.Contains(
            "SqlServer2016.ClientInfoBeta.0015",
            schema,
            StringComparison.Ordinal);
        Assert.Contains(
            "N'SqlServer2016.ClientInfoBeta.0015', 15",
            schema,
            StringComparison.Ordinal);
        Assert.Contains(
            "CONVERT(int, 15) AS [SchemaVersion]",
            procedures,
            StringComparison.Ordinal);
        Assert.Contains(
            "CONVERT(bit, 1) AS [ClientInfoBetaAvailable]",
            procedures,
            StringComparison.Ordinal);
        Assert.Equal(15, SqlServerConnectionFactory.SupportedSchemaVersion);
        Assert.Contains(
            "CompareClientInfoImportToFireDrill",
            imports,
            StringComparison.Ordinal);
        Assert.Contains(
            "HASHBYTES(",
            imports,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SELECT secret.[ValueEncrypted]",
            imports,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "CHARINDEX(N'CONVERT(int, 15) AS [SchemaVersion]', @Capabilities) = 0",
            verifier,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "@Capabilities NOT LIKE",
            verifier,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryClientMergePathReparentsCanonicalClientInfo()
    {
        var manual = Read(
            "database",
            "sqlserver2016",
            "42-V0002-SharedProcedures.sql");
        var automatic = Read(
            "database",
            "sqlserver2016",
            "49-V0007-ServerOwnedSageAndAdminPreviewProcedures.sql");

        Assert.Contains(
            "EXEC [tb_client].[ReparentClientGraph]",
            manual,
            StringComparison.Ordinal);
        Assert.True(
            automatic.Split(
                "EXEC [tb_client].[ReparentClientGraph]",
                StringSplitOptions.None).Length - 1 >= 2);
    }

    [Fact]
    public void BetaWindowUsesSmallRecordEditorsAndAnExplicitMigrationGate()
    {
        var xaml = Read("ClientInfoBetaWindow.xaml");
        var viewModel = Read(
            "ViewModels",
            "ClientInfoBetaViewModel.cs");
        var mainWindow = Read("MainWindow.xaml.cs");
        var importWindow = Read("ClientInfoImportWindow.xaml");

        foreach (var tab in new[]
                 {
                     "Overview",
                     "People &amp; Locations",
                     "Systems &amp; Services",
                     "Credentials",
                     "Other Info &amp; Notes"
                 })
        {
            Assert.Contains($"Header=\"{tab}\"", xaml, StringComparison.Ordinal);
        }

        Assert.Contains("SaveClientInfoLocation", viewModel, StringComparison.Ordinal);
        Assert.Contains("SaveClientInfoPerson", viewModel, StringComparison.Ordinal);
        Assert.Contains("SaveClientInfoResource", viewModel, StringComparison.Ordinal);
        Assert.Contains("SaveClientInfoCredential", viewModel, StringComparison.Ordinal);
        Assert.Contains("CompareClientInfoImportToFireDrill", viewModel, StringComparison.Ordinal);
        Assert.Contains("Another editor saved first", viewModel, StringComparison.Ordinal);
        Assert.Contains("new ClientInfoBetaWindow", mainWindow, StringComparison.Ordinal);
        Assert.Contains("new ClientInfoImportWindow", mainWindow, StringComparison.Ordinal);
        Assert.Contains("new ClientInfoWindow", mainWindow, StringComparison.Ordinal);
        Assert.Contains(
            "Header=\"Migration\" Visibility=\"Collapsed\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"Create Migration Workbook\"",
            importWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"Import Completed Workbook\"",
            importWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"Add to Client Information\"",
            importWindow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalClientInfoHasItsOwnWorkspaceAndDoesNotReplaceFireDrill()
    {
        var navigation = Read("Controls", "WorkspaceNavigation.xaml");
        var mainWindowXaml = Read("MainWindow.xaml");
        var mainWindowCode = Read("MainWindow.xaml.cs");
        var workspaceViewModel = Read(
            "ViewModels",
            "MainWindowViewModel.ClientInfoBeta.cs");

        Assert.Contains("Header=\"CLIENTS\"", navigation, StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"Client Information\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"Workbook Imports\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter=\"Client Database\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter=\"Workbook Imports\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding IsClientInfoBetaBuild",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "ConverterParameter=Client Database",
            mainWindowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding ClientInfoClients}\"",
            mainWindowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"View / Edit\"",
            mainWindowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "MouseDoubleClick=\"ClientInfoClientListBox_MouseDoubleClick\"",
            mainWindowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "DataContext = viewModel.CreateClientInfoProfile(summary)",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "#if TECHBENCH_CLIENT_INFO_BETA",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateCanonicalClientInfoProfile(\n        ClientInfoClientSummary summary)",
            workspaceViewModel.Replace("\r\n", "\n"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FireDrillCredentialSummary",
            workspaceViewModel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BetaWindowOwnsReadableThemeAwareTabAndGridTemplates()
    {
        var xaml = Read("ClientInfoBetaWindow.xaml");

        Assert.Contains(
            "<Style TargetType=\"TabItem\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<ControlTemplate TargetType=\"TabControl\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "TextElement.Foreground=\"{TemplateBinding Foreground}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Style TargetType=\"DataGridColumnHeader\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<ControlTemplate TargetType=\"DataGridColumnHeader\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Value=\"{DynamicResource ControlAltBackgroundBrush}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Value=\"{DynamicResource PrimaryTextBrush}\"",
            xaml,
            StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(
            new[] { RepositoryRoot() }.Concat(parts).ToArray()));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "TechBenchV2.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static void AppendRow(
        string path,
        string sheetName,
        params string[] values)
    {
        using var workbook = SpreadsheetDocument.Open(path, true);
        var sheet = workbook.WorkbookPart!.Workbook.Sheets!
            .Elements<Sheet>()
            .Single(item => string.Equals(
                item.Name?.Value,
                sheetName,
                StringComparison.Ordinal));
        var part = (WorksheetPart)workbook.WorkbookPart.GetPartById(sheet.Id!);
        var sheetData = part.Worksheet.GetFirstChild<SheetData>()!;
        var rowIndex = (uint)(sheetData.Elements<Row>()
            .Select(row => row.RowIndex?.Value ?? 0U)
            .DefaultIfEmpty(0U)
            .Max() + 1U);
        var row = new Row { RowIndex = rowIndex };
        for (var index = 0; index < values.Length; index++)
        {
            row.Append(new Cell
            {
                CellReference = $"{TestColumnName(index + 1)}{rowIndex}",
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(values[index]))
            });
        }

        sheetData.Append(row);
        part.Worksheet.Save();
    }

    private static string TestColumnName(int column)
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
