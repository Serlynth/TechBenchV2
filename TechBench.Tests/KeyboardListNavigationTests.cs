using TechBench.Services;

namespace TechBench.Tests;

public sealed class KeyboardListNavigationTests
{
    [Fact]
    public void NewEntryClientSelectorUsesHighlightThenEnterKeyboardNavigation()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var codeBehind = ReadRepositoryFile("MainWindow.xaml.cs");
        var comboBoxCode = ReadRepositoryFile("Controls/EditorClientComboBox.cs");
        var viewModel = ReadRepositoryFile(
            "ViewModels/MainWindowViewModel.cs");

        Assert.Contains(
            "<controls:EditorClientComboBox",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "KeyboardListNavigation.GetNextIndex",
            comboBoxCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "protected override void OnPreviewKeyDown",
            comboBoxCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "Do not call the ComboBox base implementation for these keys",
            comboBoxCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetCurrentValue(IsDropDownOpenProperty, true)",
            comboBoxCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "viewModel.SelectEditorClientCommand.Execute(client)",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "_editorClientSearchTimer.Start()",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "ClientSearchMatcher.Matches(",
            viewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SearchClientsAsync(EditorClientFilterText)",
            viewModel,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4, -1, true, 0)]
    [InlineData(4, -1, false, 3)]
    [InlineData(4, 0, true, 1)]
    [InlineData(4, 2, false, 1)]
    [InlineData(4, 3, true, 3)]
    [InlineData(4, 0, false, 0)]
    [InlineData(0, -1, true, -1)]
    public void ComputesTheNextAndPreviousVisibleResult(
        int itemCount,
        int currentIndex,
        bool moveDown,
        int expected)
    {
        Assert.Equal(
            expected,
            KeyboardListNavigation.GetNextIndex(
                itemCount,
                currentIndex,
                moveDown));
    }

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
