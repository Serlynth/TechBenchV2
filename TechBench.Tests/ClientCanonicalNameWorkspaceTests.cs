namespace TechBench.Tests;

public sealed class ClientCanonicalNameWorkspaceTests
{
    [Fact]
    public void ClientMatchWorkspaceExposesEditableCanonicalNameControls()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        var viewModel = File.ReadAllText(
            Path.Combine(root, "ViewModels", "MainWindowViewModel.ClientManagement.cs"));

        Assert.Contains("Text=\"TechBench client name\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ClientNameEditText", xaml, StringComparison.Ordinal);
        Assert.Contains("UseSuggestedClientNameCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("SaveClientNameCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ExportClientMatchWorkbookCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("_repository.SaveClient(client)", viewModel, StringComparison.Ordinal);
        Assert.Contains(
            "ClientMatchExcelExportService.BuildWorkbook",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains("WHD and Sage links were preserved", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientMatchWorkspaceLinksExternalSourcesIntoALiveCanonicalClient()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        var viewModel = File.ReadAllText(
            Path.Combine(root, "ViewModels", "MainWindowViewModel.cs"));

        Assert.Contains("Canonical TechBench client", xaml, StringComparison.Ordinal);
        Assert.Contains("CanonicalClientCandidates", xaml, StringComparison.Ordinal);
        Assert.Contains("WhdMatchCandidates", xaml, StringComparison.Ordinal);
        Assert.Contains("LinkClientSourcesCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Link selected sources", xaml, StringComparison.Ordinal);
        Assert.Contains("CreateCanonicalClientFromSourcesCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Match WHD + Sage", xaml, StringComparison.Ordinal);
        Assert.Contains("Workbook import is required before it becomes Live", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("combine the selected WHD-only and Sage-only records into one live client", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("InternalIdLabel", xaml, StringComparison.Ordinal);
        Assert.Contains("CanonicalLinkStatusLabel", xaml, StringComparison.Ordinal);

        Assert.Contains("client.IsClientInfoLive", viewModel, StringComparison.Ordinal);
        Assert.Contains("client.IsExternalSourceLinkEligible", viewModel, StringComparison.Ordinal);
        Assert.Contains("client.HasWhdIdentity", viewModel, StringComparison.Ordinal);
        Assert.Contains("client.HasSageIdentity", viewModel, StringComparison.Ordinal);
        Assert.Contains(
            "Finish or discard the client workbook migration first.",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains("var whdCandidates = Clients", viewModel, StringComparison.Ordinal);
        Assert.Contains("var sageCandidates = Clients", viewModel, StringComparison.Ordinal);
        Assert.Contains(
            "_repository.LinkClientSources(",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "_repository.MergeClientRecords(",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains("SelectedCanonicalMatchClient = null;", viewModel, StringComparison.Ordinal);
        Assert.Contains("It remains Needs review", viewModel, StringComparison.Ordinal);
        Assert.Contains("use Workbook Imports to promote it to Live", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Created live TechBench client", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlClientMappingCarriesClientInfoWorkspaceEligibilityState()
    {
        var root = FindRepositoryRoot();
        var repository = File.ReadAllText(Path.Combine(
            root,
            "Data",
            "SqlServerTechBenchRepository.ClientsTickets.cs"));

        Assert.Contains(
            "client.HasClientInfoWorkspace = GetBoolean(",
            repository,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"HasClientInfoWorkspace\"",
            repository,
            StringComparison.Ordinal);
        Assert.Contains(
            "target.HasClientInfoWorkspace = source.HasClientInfoWorkspace;",
            repository,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "TechBench.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the TechBench repository root.");
    }
}
