using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TechBench.Services;

public static class WindowActivationService
{
    private const int ShowNormal = 1;
    private const int ShowMaximized = 3;

    public static void BringToForeground(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.ShowActivated = true;
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            () =>
            {
                if (!window.IsVisible)
                {
                    return;
                }

                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                var handle = new WindowInteropHelper(window).Handle;
                if (handle != IntPtr.Zero)
                {
                    ShowWindow(
                        handle,
                        window.WindowState == WindowState.Maximized
                            ? ShowMaximized
                            : ShowNormal);
                    SetForegroundWindow(handle);
                }

                window.Activate();
                window.Topmost = true;
                window.Topmost = false;
                window.Focus();
            });
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);
}
