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
        Assert.Contains("_repository.SaveClient(client)", viewModel, StringComparison.Ordinal);
        Assert.Contains("WHD and Sage links were preserved", viewModel, StringComparison.Ordinal);
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
