using TechBench.Services;

namespace TechBench.Tests;

public sealed class KeyboardListNavigationTests
{
    [Fact]
    public void NewEntryClientSelectorUsesHighlightThenEnterKeyboardNavigation()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var codeBehind = ReadRepositoryFile("MainWindow.xaml.cs");

        Assert.Contains(
            "PreviewKeyDown=\"EditorClientComboBox_PreviewKeyDown\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "KeyboardListNavigation.GetNextIndex",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "HighlightEditorClientOption(comboBox, nextIndex)",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetCurrentValue(WpfComboBox.IsDropDownOpenProperty, true)",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Keyboard.Focus(container)",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "viewModel.SelectEditorClientCommand.Execute(client)",
            codeBehind,
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
