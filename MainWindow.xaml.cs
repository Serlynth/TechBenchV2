using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TechBench.Data;
using TechBench.Models;
using TechBench.Providers;
using TechBench.Services;
using TechBench.ViewModels;

namespace TechBench;

public partial class MainWindow : Window
{
    private readonly WindowsNotificationService _notificationService;
    private readonly LocalPreferences _localPreferences;
    private MarkdownEditorWindow? _markdownEditorWindow;

    public MainWindow(
        SqlServerConnectionFactory connectionFactory,
        CurrentUserContext currentUser)
    {
        InitializeComponent();
        _localPreferences = LocalPreferenceStore.LoadOrCreate();
        ApplyWindowPreferences();
        EditorClientComboBox.AddHandler(
            System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(EditorClientComboBox_TextChanged));

        var repository = new SqlServerTechBenchRepository(
            connectionFactory,
            _localPreferences.DeviceId);
        repository.Initialize();
        var whdRestClient = new WhdRestClient();
        var sageOdbcClient = new SageOdbcProcessClient();
        _notificationService = new WindowsNotificationService();

        var viewModel = new MainWindowViewModel(
            repository,
            new SqlServerClientProvider(repository),
            new SqlServerTicketProvider(repository),
            new WhdRestPoster(whdRestClient),
            new SageNativeUiPoster(new SageNativeUiAutomation(), sageOdbcClient),
            whdRestClient,
            sageOdbcClient,
            new AppDialogService(),
            _notificationService,
            new WindowsCredentialStore(LocalUserDataPath.ResolveCredentialScope(
                currentUser.DatabaseInstanceId,
                currentUser.UserSid)),
            currentUser,
            _localPreferences,
            new V2AppUpdateService(),
            () => System.Windows.Application.Current.Shutdown());

        DataContext = viewModel;
        viewModel.StatusMessage =
            $"Connected to {connectionFactory.Options.Server}/{connectionFactory.Options.Database} "
            + $"as {currentUser.DisplayName}. Server-backed workspace ready.";
        if (!string.IsNullOrWhiteSpace(App.UpdateCompletionVersion))
        {
            viewModel.Updates.MarkUpdateCompleted(App.UpdateCompletionVersion);
        }

        viewModel.Updates.StartAutomaticChecks();
        WhdApiTokenPasswordBox.Password = viewModel.WhdApiToken;
        SagePasswordBox.Password = viewModel.SagePassword;
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveWindowPreferences();
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _notificationService.Dispose();
        base.OnClosed(e);
    }

    private void ApplyWindowPreferences()
    {
        if (_localPreferences.WindowWidth is >= 640)
        {
            Width = _localPreferences.WindowWidth.Value;
        }

        if (_localPreferences.WindowHeight is >= 480)
        {
            Height = _localPreferences.WindowHeight.Value;
        }

        if (_localPreferences.WindowLeft is double left
            && _localPreferences.WindowTop is double top
            && double.IsFinite(left)
            && double.IsFinite(top))
        {
            Left = left;
            Top = top;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        WindowState = _localPreferences.WindowState.Equals(
            "Maximized",
            StringComparison.OrdinalIgnoreCase)
            ? WindowState.Maximized
            : WindowState.Normal;
    }

    private void SaveWindowPreferences()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _localPreferences.WindowLeft = bounds.Left;
        _localPreferences.WindowTop = bounds.Top;
        _localPreferences.WindowWidth = bounds.Width;
        _localPreferences.WindowHeight = bounds.Height;
        _localPreferences.WindowState =
            WindowState == WindowState.Maximized ? "Maximized" : "Normal";
        try
        {
            LocalPreferenceStore.Save(_localPreferences);
        }
        catch
        {
            // Window shutdown should not be blocked by preference persistence.
        }
    }

    private void EditorClientComboBoxItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ComboBoxItem { DataContext: Client client }
            || DataContext is not MainWindowViewModel viewModel
            || !viewModel.SelectEditorClientCommand.CanExecute(client))
        {
            return;
        }

        viewModel.SelectEditorClientCommand.Execute(client);
        e.Handled = true;
    }

    private void WhdApiTokenPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.WhdApiToken = passwordBox.Password;
        }
    }

    private void SagePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SagePassword = passwordBox.Password;
        }
    }

    private void NewEntryButton_Click(object sender, RoutedEventArgs e)
    {
        FocusEditorClient();
    }

    private void EditorClientComboBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.TextBox editableTextBox || !editableTextBox.IsKeyboardFocused)
        {
            return;
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.IsEditorClientDropDownOpen = true;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (editableTextBox.IsKeyboardFocused
                && editableTextBox.SelectionStart == 0
                && editableTextBox.SelectionLength > 0)
            {
                editableTextBox.SelectionStart = editableTextBox.Text.Length;
                editableTextBox.SelectionLength = 0;
            }
        });
    }

    private void MoreActionsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenButtonContextMenu(sender);
    }

    private void PostActionsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenButtonContextMenu(sender);
    }

    private void OpenInternalMarkdownEditor_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (_markdownEditorWindow is { IsVisible: true } existingWindow)
        {
            if (existingWindow.WindowState == WindowState.Minimized)
            {
                existingWindow.WindowState = WindowState.Normal;
            }

            existingWindow.Activate();
            return;
        }

        var window = new MarkdownEditorWindow(viewModel)
        {
            Owner = this
        };
        _markdownEditorWindow = window;
        window.Closed += (_, _) => _markdownEditorWindow = null;
        window.Show();
    }

    private void OpenHistoryMarkdownViewer_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { SelectedEntry.InternalNote: { } markdown })
        {
            return;
        }

        var window = new MarkdownEditorWindow(markdown, isReadOnly: true)
        {
            Owner = this
        };
        window.Show();
    }

    private static void OpenButtonContextMenu(object sender)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } contextMenu } button)
        {
            contextMenu.PlacementTarget = button;
            contextMenu.IsOpen = true;
        }
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.N
            && Keyboard.Modifiers == ModifierKeys.Control
            && DataContext is MainWindowViewModel viewModel
            && viewModel.NewEntryCommand.CanExecute(null))
        {
            viewModel.NewEntryCommand.Execute(null);
            FocusEditorClient();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void FocusEditorClient()
    {
        Dispatcher.BeginInvoke(() =>
        {
            EditorClientComboBox.Focus();
            Keyboard.Focus(EditorClientComboBox);
        });
    }
}
