using System.Windows.Input;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace TechBench.Services;

internal static class TextBoxClipboardCommandService
{
    public static bool Handles(ICommand command) =>
        ReferenceEquals(command, ApplicationCommands.Copy)
        || ReferenceEquals(command, ApplicationCommands.Cut);

    public static async Task<bool> TryExecuteAsync(
        WpfTextBox textBox,
        ICommand command,
        Func<string, Task<bool>>? setClipboardTextAsync = null)
    {
        ArgumentNullException.ThrowIfNull(textBox);

        if (!Handles(command) || textBox.SelectionLength <= 0)
        {
            return false;
        }

        var isCut = ReferenceEquals(command, ApplicationCommands.Cut);
        if (isCut && textBox.IsReadOnly)
        {
            return false;
        }

        var selectionStart = textBox.SelectionStart;
        var selectionLength = textBox.SelectionLength;
        var selectedText = textBox.SelectedText;
        var clipboardWriter = setClipboardTextAsync ?? ClipboardService.TrySetTextAsync;

        bool copied;
        try
        {
            copied = await clipboardWriter(selectedText);
        }
        catch
        {
            copied = false;
        }

        if (!copied)
        {
            return false;
        }

        if (isCut
            && textBox.SelectionStart == selectionStart
            && textBox.SelectionLength == selectionLength
            && string.Equals(textBox.SelectedText, selectedText, StringComparison.Ordinal))
        {
            textBox.SelectedText = string.Empty;
            textBox.CaretIndex = selectionStart;
        }

        return true;
    }
}
