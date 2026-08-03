namespace TechBench.Tests;

public sealed class WorklogInlineEditorTests
{
    [Fact]
    public void WeekAndHistoryHostTheEditorWithoutForcingTodayNavigation()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var viewModel = ReadRepositoryFile("ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("x:Key=\"InlineWorkEntryEditorTemplate\"", xaml, StringComparison.Ordinal);
        Assert.Equal(
            3,
            xaml.Split("InlineWorkEntryEditorTemplate", StringSplitOptions.None).Length - 1);
        Assert.Contains("ConverterParameter=This Week", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=History", xaml, StringComparison.Ordinal);
        Assert.Contains("Editing {savedEntry.ClientDisplay} here", viewModel, StringComparison.Ordinal);
        Assert.Contains("var editInline = CurrentSection is \"This Week\" or \"History\";", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingKeepsTheEntryAndTicketOpenForPosting()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var viewModel = ReadRepositoryFile("ViewModels", "MainWindowViewModel.cs");
        var saveStart = viewModel.IndexOf(
            "private async Task SaveEntryAsync",
            StringComparison.Ordinal);
        var saveEnd = viewModel.IndexOf(
            "private WorkEntry? SaveEditor",
            saveStart,
            StringComparison.Ordinal);
        var saveMethod = viewModel[saveStart..saveEnd];

        Assert.DoesNotContain("NewEntry();", saveMethod, StringComparison.Ordinal);
        Assert.Contains("remains open for posting or further edits", saveMethod, StringComparison.Ordinal);
        Assert.Contains("The saved ticket stays selected for posting", xaml, StringComparison.Ordinal);
        Assert.Contains("Save and keep this entry open (Ctrl+S)", xaml, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var candidate = Path.Combine([current, .. parts]);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
