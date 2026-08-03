namespace TechBench.Tests;

using System.Text.RegularExpressions;

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

    [Fact]
    public void InlineEditorStaticResourcesAreRegisteredBeforeItsDeferredTemplate()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        const string templateMarker = "<DataTemplate x:Key=\"InlineWorkEntryEditorTemplate\">";
        var templateStart = xaml.IndexOf(templateMarker, StringComparison.Ordinal);
        var templateEnd = xaml.IndexOf("</DataTemplate>", templateStart, StringComparison.Ordinal);

        Assert.True(templateStart >= 0, "The inline editor template was not found.");
        Assert.True(templateEnd > templateStart, "The inline editor template is incomplete.");

        var template = xaml[templateStart..templateEnd];
        var referencedKeys = Regex.Matches(template, @"\{StaticResource\s+([^,}\s]+)")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);

        foreach (var key in referencedKeys)
        {
            var localDeclaration = xaml.IndexOf($"x:Key=\"{key}\"", StringComparison.Ordinal);
            if (localDeclaration >= 0)
            {
                Assert.True(
                    localDeclaration < templateStart,
                    $"StaticResource '{key}' must be registered before InlineWorkEntryEditorTemplate is created.");
            }
        }
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
