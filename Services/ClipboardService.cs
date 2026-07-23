using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace TechBench.Services;

internal static class ClipboardService
{
    private static readonly Task<Dispatcher> ClipboardDispatcher = StartClipboardDispatcher();

    public static async Task<bool> TrySetTextAsync(string value)
    {
        var dispatcher = await ClipboardDispatcher.ConfigureAwait(false);
        var operation = dispatcher.InvokeAsync(
            () => TrySetText(value),
            DispatcherPriority.Normal);
        return await operation.Task.ConfigureAwait(false);
    }

    private static Task<Dispatcher> StartClipboardDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            ready.TrySetResult(dispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "TechBench Clipboard"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    private static bool TrySetText(string value)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                // Keep the dedicated STA dispatcher alive as the clipboard owner.
                // Avoid persistent clipboard flushing, which can block for seconds
                // while RDP or third-party clipboard managers synchronize data.
                System.Windows.Clipboard.SetDataObject(value, copy: false);
                return true;
            }
            catch (ExternalException) when (attempt < 5)
            {
                Thread.Sleep(40);
            }
            catch (ExternalException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
        return false;
    }
}
