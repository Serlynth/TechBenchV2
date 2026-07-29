using System.Xml.Linq;

namespace TechBench.Tests;

public sealed class BenchModuleShellTests
{
    [Fact]
    public void SidebarKeepsModuleNavigationSeparateAndMovesAdminToolsToAdminBench()
    {
        var navigation = ReadRepositoryFile(
            Path.Combine("Controls", "WorkspaceNavigation.xaml"));
        var viewModel = ReadRepositoryFile(
            Path.Combine("ViewModels", "MainWindowViewModel.cs"));
        var project = ReadRepositoryFile("TechBench.csproj");

        Assert.Contains(
            "Source=\"{Binding ModuleLogoSource}\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "Width=\"{Binding ModuleLogoDisplayWidth}\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "ModuleLogoDisplayWidth => 252",
            viewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ModuleLogoOffset",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "Value=\"5,3\"",
            navigation,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Text=\"{Binding ModuleBrandName}\"",
            navigation,
            StringComparison.Ordinal);
        foreach (var asset in new[]
                 {
                     "csri-techbench-logo.png",
                     "csri-salesbench-logo.png",
                     "csri-adminbench-logo.png"
                 })
        {
            Assert.Contains(asset, viewModel, StringComparison.Ordinal);
            Assert.Contains(asset, project, StringComparison.Ordinal);
        }
        Assert.Contains(
            "Visibility=\"{Binding CanAccessBenchModules",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter=\"TechBench\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter=\"SalesBench\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter=\"AdminBench\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"TechBenchNavigation\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"SalesBenchNavigation\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"AdminBenchNavigation\"",
            navigation,
            StringComparison.Ordinal);

        var document = XDocument.Parse(navigation);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var techBenchSidebar = FindNamedElement(
            document,
            xaml,
            "TechBenchNavigation");
        var salesBenchSidebar = FindNamedElement(
            document,
            xaml,
            "SalesBenchNavigation");
        var adminBenchSidebar = FindNamedElement(
            document,
            xaml,
            "AdminBenchNavigation");

        Assert.DoesNotContain(
            techBenchSidebar.Descendants(),
            element => HasButtonContent(element, "Client Matching")
                || HasButtonContent(element, "Admin Center"));
        Assert.DoesNotContain(
            salesBenchSidebar.Descendants(),
            element => element.Name.LocalName == "Button");
        Assert.Contains(
            adminBenchSidebar.Descendants(),
            element => HasButtonContent(element, "Client Matching"));
        Assert.Contains(
            adminBenchSidebar.Descendants(),
            element => HasButtonContent(element, "Admin Center"));
    }

    [Fact]
    public void AdminBenchUsesTheWorkspaceWhileSalesBenchKeepsTheEmptyShell()
    {
        var mainWindow = ReadRepositoryFile("MainWindow.xaml");
        var header = ReadRepositoryFile(
            Path.Combine("Controls", "WorkspaceHeader.xaml"));

        Assert.Contains(
            "Visibility=\"{Binding HasModuleWorkspace, Converter={StaticResource BooleanToVisibilityConverter}}\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding ShowsEmptyModuleShell, Converter={StaticResource BooleanToVisibilityConverter}}\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "public bool HasModuleWorkspace => IsTechBenchModule || IsAdminBenchModule;",
            ReadRepositoryFile(Path.Combine("ViewModels", "MainWindowViewModel.cs")),
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding WorkspaceHeaderEyebrow}\"",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding WorkspaceHeaderTitle}\"",
            header,
            StringComparison.Ordinal);
    }

    private static XElement FindNamedElement(
        XDocument document,
        XNamespace xaml,
        string name) =>
        Assert.Single(
            document.Descendants(),
            element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                name,
                StringComparison.Ordinal));

    private static bool HasButtonContent(XElement element, string content) =>
        element.Name.LocalName == "Button"
        && string.Equals(
            (string?)element.Attribute("Content"),
            content,
            StringComparison.Ordinal);

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "TechBenchV2.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }
}
