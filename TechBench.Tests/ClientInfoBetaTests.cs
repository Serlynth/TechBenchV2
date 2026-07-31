using DocumentFormat.OpenXml.Packaging;
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
            Assert.Empty(package.Secrets);

            using var workbook = SpreadsheetDocument.Open(path, false);
            var names = workbook.WorkbookPart!.Workbook.Sheets!
                .Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
                .Select(sheet => sheet.Name?.Value ?? string.Empty)
                .ToArray();
            Assert.Equal(
                [
                    "Client Info",
                    "People & Locations",
                    "Systems & Services",
                    "Equipment",
                    "Credentials",
                    "Other Info & Notes"
                ],
                names);
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

        foreach (var tab in new[]
                 {
                     "Overview",
                     "People &amp; Locations",
                     "Systems &amp; Services",
                     "Credentials",
                     "Other Info &amp; Notes",
                     "Migration"
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
        Assert.Contains("new ClientInfoWindow", mainWindow, StringComparison.Ordinal);
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
            "Content=\"Client Info (Beta)\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter=\"Client Database\"",
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
            "Content=\"Open Client Info\"",
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
}
