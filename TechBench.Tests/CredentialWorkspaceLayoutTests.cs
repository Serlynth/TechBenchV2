namespace TechBench.Tests;

public sealed class CredentialWorkspaceLayoutTests
{
    [Fact]
    public void SidebarUsesFullClientCredentialsLabel()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");

        Assert.Contains(
            "Content=\"Client Credentials\" Command=\"{Binding NavigateCommand}\" CommandParameter=\"Client Credentials\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Content=\"Credentials\" Command=\"{Binding NavigateCommand}\" CommandParameter=\"Client Credentials\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResultCardsExposeOnlyClientName()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var start = xaml.IndexOf("<ListBox ItemsSource=\"{Binding FireDrillCredentials}\"", StringComparison.Ordinal);
        var end = xaml.IndexOf("</ListBox>", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var list = xaml[start..end];

        Assert.Contains("{Binding ClientName}", list, StringComparison.Ordinal);
        Assert.DoesNotContain("FireboxIp", list, StringComparison.Ordinal);
        Assert.DoesNotContain("Status", list, StringComparison.Ordinal);
        Assert.DoesNotContain("LastSyncedLabel", list, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailPanelShowsMaskedFieldsBeforeReveal()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var viewModel = ReadRepositoryFile(Path.Combine("ViewModels", "MainWindowViewModel.FireDrill.cs"));

        Assert.Contains("ItemsSource=\"{Binding FireDrillCredentialFields}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding HasSelectedFireDrillCredential", xaml, StringComparison.Ordinal);
        Assert.Contains("AddFireDrillFields(_ => \"***\")", viewModel, StringComparison.Ordinal);
        Assert.Contains("PopulateRevealedFireDrillFields", viewModel, StringComparison.Ordinal);
        Assert.Contains("\"Firebox IP\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("\"SSL VPN Password\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("\"AD Password\"", viewModel, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TechBenchV2.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }
}
