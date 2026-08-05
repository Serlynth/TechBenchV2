using System.Threading;
using System.Windows.Input;
using TechBench.Services;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace TechBench.Tests;

public sealed class TextBoxClipboardCommandServiceTests
{
    [Fact]
    public void CopyWritesTheSelectedTextWithoutChangingTheEditor()
    {
        RunOnSta(() =>
        {
            var textBox = new WpfTextBox { Text = "alpha beta" };
            textBox.Select(6, 4);
            string? copiedText = null;

            var handled = TextBoxClipboardCommandService.TryExecuteAsync(
                    textBox,
                    ApplicationCommands.Copy,
                    value =>
                    {
                        copiedText = value;
                        return Task.FromResult(true);
                    })
                .GetAwaiter()
                .GetResult();

            Assert.True(handled);
            Assert.Equal("beta", copiedText);
            Assert.Equal("alpha beta", textBox.Text);
            Assert.Equal("beta", textBox.SelectedText);
        });
    }

    [Fact]
    public void CutOnlyRemovesTheSelectionAfterClipboardSuccess()
    {
        RunOnSta(() =>
        {
            var textBox = new WpfTextBox { Text = "alpha beta" };
            textBox.Select(6, 4);

            var handled = TextBoxClipboardCommandService.TryExecuteAsync(
                    textBox,
                    ApplicationCommands.Cut,
                    _ => Task.FromResult(true))
                .GetAwaiter()
                .GetResult();

            Assert.True(handled);
            Assert.Equal("alpha ", textBox.Text);
            Assert.Equal(6, textBox.CaretIndex);
        });
    }

    [Fact]
    public void FailedCutLeavesTheSelectedTextInPlace()
    {
        RunOnSta(() =>
        {
            var textBox = new WpfTextBox { Text = "alpha beta" };
            textBox.Select(6, 4);

            var handled = TextBoxClipboardCommandService.TryExecuteAsync(
                    textBox,
                    ApplicationCommands.Cut,
                    _ => Task.FromResult(false))
                .GetAwaiter()
                .GetResult();

            Assert.False(handled);
            Assert.Equal("alpha beta", textBox.Text);
            Assert.Equal("beta", textBox.SelectedText);
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw failure;
        }
    }
}
