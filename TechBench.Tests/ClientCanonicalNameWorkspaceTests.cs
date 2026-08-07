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
        Assert.Contains("Create live client", xaml, StringComparison.Ordinal);
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
