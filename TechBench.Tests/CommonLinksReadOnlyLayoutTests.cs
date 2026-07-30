using TechBench.ViewModels;

namespace TechBench.Tests;

public sealed class CommonLinksReadOnlyLayoutTests
{
    [Fact]
    public void CommonLinksWorkspaceIsAReadOnlyLauncher()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var start = xaml.IndexOf("ConverterParameter=Common Links", StringComparison.Ordinal);
        var end = xaml.IndexOf("ConverterParameter=Search", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var commonLinksWorkspace = xaml[start..end];

        Assert.Contains("OpenCommonLinkCommand", commonLinksWorkspace, StringComparison.Ordinal);
        Assert.DoesNotContain("Add Shared Link", commonLinksWorkspace, StringComparison.Ordinal);
        Assert.DoesNotContain("EditCommonLinkCommand", commonLinksWorkspace, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteCommonLinkCommand", commonLinksWorkspace, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveCommonLinkCommand", commonLinksWorkspace, StringComparison.Ordinal);
        Assert.DoesNotContain("Link Details", commonLinksWorkspace, StringComparison.Ordinal);

        Assert.Null(typeof(MainWindowViewModel).GetProperty("NewCommonLinkCommand"));
        Assert.Null(typeof(MainWindowViewModel).GetProperty("EditCommonLinkCommand"));
        Assert.Null(typeof(MainWindowViewModel).GetProperty("SaveCommonLinkCommand"));
        Assert.Null(typeof(MainWindowViewModel).GetProperty("DeleteCommonLinkCommand"));
    }

    [Fact]
    public void CommonLinkGroupHeadingsAreVisuallyProminent()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var start = xaml.IndexOf("ConverterParameter=Common Links", StringComparison.Ordinal);
        var end = xaml.IndexOf("ConverterParameter=Search", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var commonLinksWorkspace = xaml[start..end];

        Assert.Contains("BorderThickness=\"4,0,0,0\"", commonLinksWorkspace, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{DynamicResource PrimaryTextBrush}\"", commonLinksWorkspace, StringComparison.Ordinal);
        Assert.Contains("FontSize=\"15\"", commonLinksWorkspace, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TechBenchV2.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }
}
