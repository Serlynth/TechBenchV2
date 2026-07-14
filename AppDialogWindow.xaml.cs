using System.Windows;
using System.Windows.Input;

namespace TechBench;

public enum AppDialogKind
{
    Confirmation,
    Information,
    Error
}

public partial class AppDialogWindow : Window
{
    private readonly bool _isConfirmation;

    private AppDialogWindow(
        string title,
        string message,
        AppDialogKind kind,
        string primaryText,
        string? secondaryText)
    {
        DialogTitle = string.IsNullOrWhiteSpace(title) ? "TechBench" : title.Trim();
        DialogMessage = message ?? string.Empty;
        _isConfirmation = kind == AppDialogKind.Confirmation;

        InitializeComponent();
        DataContext = this;
        ConfigureAppearance(kind, primaryText, secondaryText);
        ContentRendered += HandleContentRendered;
    }

    public string DialogTitle { get; }
    public string DialogMessage { get; }

    public static bool Confirm(
        string title,
        string message,
        Window? owner = null,
        string confirmText = "Yes",
        string cancelText = "No")
    {
        return ShowCore(
            title,
            message,
            AppDialogKind.Confirmation,
            owner,
            confirmText,
            cancelText);
    }

    public static void Info(string title, string message, Window? owner = null)
    {
        ShowCore(title, message, AppDialogKind.Information, owner, "OK", secondaryText: null);
    }

    public static void Error(string title, string message, Window? owner = null)
    {
        ShowCore(title, message, AppDialogKind.Error, owner, "OK", secondaryText: null);
    }

    private static bool ShowCore(
        string title,
        string message,
        AppDialogKind kind,
        Window? owner,
        string primaryText,
        string? secondaryText)
    {
        var dialog = new AppDialogWindow(title, message, kind, primaryText, secondaryText);
        var resolvedOwner = ResolveOwner(owner);
        if (resolvedOwner is not null)
        {
            dialog.Owner = resolvedOwner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true;
    }

    private static Window? ResolveOwner(Window? requestedOwner)
    {
        if (requestedOwner is { IsVisible: true })
        {
            return requestedOwner;
        }

        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return null;
        }

        return application.Windows
            .OfType<Window>()
            .FirstOrDefault(static window => window is not AppDialogWindow && window.IsActive && window.IsVisible)
            ?? (application.MainWindow is { IsVisible: true } mainWindow ? mainWindow : null);
    }

    private void ConfigureAppearance(
        AppDialogKind kind,
        string primaryText,
        string? secondaryText)
    {
        PrimaryButton.Content = string.IsNullOrWhiteSpace(primaryText) ? "OK" : primaryText;
        if (primaryText.Equals("Delete", StringComparison.OrdinalIgnoreCase)
            || primaryText.Equals("Discard", StringComparison.OrdinalIgnoreCase))
        {
            PrimaryButton.Style = (Style)FindResource("DangerButtonStyle");
        }

        SecondaryButton.Content = string.IsNullOrWhiteSpace(secondaryText) ? "Cancel" : secondaryText;
        SecondaryButton.Visibility = _isConfirmation ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.IsDefault = !_isConfirmation;
        SecondaryButton.IsDefault = _isConfirmation;
        SecondaryButton.IsCancel = _isConfirmation;

        var accentKey = kind switch
        {
            AppDialogKind.Confirmation => "WarningBrush",
            AppDialogKind.Error => "DangerBrush",
            _ => "AccentBrush"
        };
        var accent = (System.Windows.Media.Brush)FindResource(accentKey);
        AccentStrip.Background = accent;
        IconBadge.BorderBrush = accent;
        IconGlyph.Foreground = accent;
        IconGlyph.Text = kind switch
        {
            AppDialogKind.Information => "i",
            AppDialogKind.Error => "\u00D7",
            _ => "!"
        };
    }

    private void HandleContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= HandleContentRendered;
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                var target = _isConfirmation ? SecondaryButton : PrimaryButton;
                FocusManager.SetFocusedElement(this, target);
                target.Focus();
                Keyboard.Focus(target);
            }));
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
