using System.Windows;
using System.Windows.Input;

namespace TechBench;

public enum AppDialogKind
{
    Confirmation,
    Information,
    Error,
    Prompt
}

public partial class AppDialogWindow : Window
{
    private readonly bool _isConfirmation;
    private readonly bool _isPrompt;

    private AppDialogWindow(
        string title,
        string message,
        AppDialogKind kind,
        string primaryText,
        string? secondaryText,
        string? initialValue = null)
    {
        DialogTitle = string.IsNullOrWhiteSpace(title) ? "TechBench" : title.Trim();
        DialogMessage = message ?? string.Empty;
        _isConfirmation = kind == AppDialogKind.Confirmation;
        _isPrompt = kind == AppDialogKind.Prompt;

        InitializeComponent();
        DataContext = this;
        InputBox.Text = initialValue ?? string.Empty;
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

    public static string? Prompt(
        string title,
        string message,
        string initialValue = "",
        Window? owner = null,
        string confirmText = "OK",
        string cancelText = "Cancel")
    {
        var dialog = new AppDialogWindow(
            title,
            message,
            AppDialogKind.Prompt,
            confirmText,
            cancelText,
            initialValue);
        ConfigureOwner(dialog, owner);
        return dialog.ShowDialog() == true ? dialog.InputBox.Text.Trim() : null;
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
        ConfigureOwner(dialog, owner);
        return dialog.ShowDialog() == true;
    }

    private static void ConfigureOwner(AppDialogWindow dialog, Window? owner)
    {
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
        var hasSecondaryAction = _isConfirmation || _isPrompt;
        SecondaryButton.Visibility = hasSecondaryAction ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.IsDefault = !_isConfirmation;
        SecondaryButton.IsDefault = _isConfirmation;
        SecondaryButton.IsCancel = hasSecondaryAction;
        InputBox.Visibility = _isPrompt ? Visibility.Visible : Visibility.Collapsed;

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
            AppDialogKind.Prompt => "#",
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
                if (_isPrompt)
                {
                    InputBox.Focus();
                    InputBox.SelectAll();
                    Keyboard.Focus(InputBox);
                    return;
                }

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
