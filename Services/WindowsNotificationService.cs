using System.Drawing;
using TechBench.Models;

namespace TechBench.Services;

public sealed class WindowsNotificationService : IUserNotificationService, IDisposable
{
    private readonly Icon? _applicationIcon;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private bool _disposed;

    public WindowsNotificationService()
    {
        _applicationIcon = TryGetApplicationIcon();
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _applicationIcon ?? SystemIcons.Information,
            Text = "TechBench",
            Visible = true
        };
    }

    public void ShowNewWhdTickets(IReadOnlyList<WhdSyncedTicket> tickets)
    {
        if (_disposed || tickets.Count == 0)
        {
            return;
        }

        try
        {
            var title = tickets.Count == 1
                ? "New WHD ticket assigned"
                : $"{tickets.Count} new WHD tickets assigned";
            var lines = tickets
                .Take(3)
                .Select(static ticket => string.IsNullOrWhiteSpace(ticket.Subject)
                    ? ticket.TicketNumber
                    : $"{ticket.TicketNumber} - {ticket.Subject}");
            var text = string.Join(Environment.NewLine, lines);
            if (tickets.Count > 3)
            {
                text = $"{text}{Environment.NewLine}+ {tickets.Count - 3} more";
            }

            ShowBalloon(title, text);
        }
        catch (InvalidOperationException)
        {
            // Notifications are helpful, but they should never interrupt sync.
        }
    }

    public void ShowUpdateAvailable(string version)
    {
        if (_disposed || string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        try
        {
            ShowBalloon(
                $"TechBench {version} is available",
                "Open TechBench to download and install the update.");
        }
        catch (InvalidOperationException)
        {
            // Update checks must remain non-disruptive when Windows cannot show a balloon.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _applicationIcon?.Dispose();
    }

    private static Icon? TryGetApplicationIcon()
    {
        var executablePath = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(executablePath)
            ? null
            : Icon.ExtractAssociatedIcon(executablePath);
    }

    private void ShowBalloon(string title, string text)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = Truncate(text, 240);
        _notifyIcon.ShowBalloonTip(8000);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : $"{value[..Math.Max(0, maxLength - 3)]}...";
    }
}
