using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TechBench.Controls;
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
    private DispatcherTimer? _previewExpiryTimer;
    private DateTime _previewExpiryDeadlineUtc;
    private bool _previewExpiryHandled;
    private MarkdownEditorWindow? _markdownEditorWindow;
    private System.Windows.Point _equipmentDragStartPoint;
    private EquipmentItem? _pendingEquipmentDragItem;
    private bool _equipmentDragStarted;
    private EquipmentDragPreview? _equipmentDragPreview;
    private GridLength _expandedEquipmentDeploymentHeight =
        new(230, GridUnitType.Pixel);

    private void EquipmentDeploymentExpander_Collapsed(
        object sender,
        RoutedEventArgs e)
    {
        if (EquipmentDeploymentRow is null
            || EquipmentDeploymentSplitter is null)
        {
            return;
        }

        if (EquipmentDeploymentRow.ActualHeight > 90)
        {
            _expandedEquipmentDeploymentHeight =
                new GridLength(
                    EquipmentDeploymentRow.ActualHeight,
                    GridUnitType.Pixel);
        }

        EquipmentDeploymentSplitter.Visibility = Visibility.Collapsed;
        EquipmentDeploymentRow.Height = GridLength.Auto;
    }

    private void EquipmentDeploymentExpander_Expanded(
        object sender,
        RoutedEventArgs e)
    {
        if (EquipmentDeploymentRow is null
            || EquipmentDeploymentSplitter is null)
        {
            return;
        }

        EquipmentDeploymentRow.Height = _expandedEquipmentDeploymentHeight;
        EquipmentDeploymentSplitter.Visibility = Visibility.Visible;
    }

    private void EquipmentLaneListBox_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _equipmentDragStartPoint = e.GetPosition(null);
        _equipmentDragStarted = false;
        _pendingEquipmentDragItem =
            FindVisualAncestor<ListBoxItem>(
                e.OriginalSource as DependencyObject)?.DataContext as EquipmentItem;
        if (_pendingEquipmentDragItem is not null
            && sender is System.Windows.Controls.ListBox listBox)
        {
            listBox.CaptureMouse();
        }
    }

    private void EquipmentLaneListBox_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox listBox
            && listBox.IsMouseCaptured)
        {
            listBox.ReleaseMouseCapture();
        }

        if (!_equipmentDragStarted
            && _pendingEquipmentDragItem is { } equipment
            && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SelectedEquipment = equipment;
        }

        _pendingEquipmentDragItem = null;
        _equipmentDragStarted = false;
    }

    private void EquipmentLaneListBox_PreviewMouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || sender is not System.Windows.Controls.ListBox listBox)
        {
            return;
        }

        var currentPosition = e.GetPosition(null);
        if (Math.Abs(currentPosition.X - _equipmentDragStartPoint.X)
                < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPosition.Y - _equipmentDragStartPoint.Y)
                < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (_pendingEquipmentDragItem is not { } equipment)
        {
            return;
        }

        _equipmentDragStarted = true;
        if (listBox.IsMouseCaptured)
        {
            listBox.ReleaseMouseCapture();
        }
        listBox.SelectedItem = equipment;
        using var preview = new EquipmentDragPreview(listBox, equipment);
        _equipmentDragPreview = preview;
        listBox.GiveFeedback += EquipmentDragSource_GiveFeedback;
        preview.Show();
        try
        {
            DragDrop.DoDragDrop(
                listBox,
                new System.Windows.DataObject(typeof(EquipmentItem), equipment),
                System.Windows.DragDropEffects.Move);
        }
        finally
        {
            listBox.GiveFeedback -= EquipmentDragSource_GiveFeedback;
            _equipmentDragPreview = null;
            _pendingEquipmentDragItem = null;
            _equipmentDragStarted = false;
        }
    }

    private void EquipmentDragSource_GiveFeedback(
        object sender,
        System.Windows.GiveFeedbackEventArgs e)
    {
        _equipmentDragPreview?.UpdatePosition();
        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void EquipmentLaneListBox_DragOver(
        object sender,
        System.Windows.DragEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox listBox
            && listBox.DataContext is EquipmentLane
            && e.Data.GetDataPresent(typeof(EquipmentItem)))
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            listBox.Background =
                System.Windows.Application.Current.TryFindResource("AccentSoftBrush")
                    as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.Transparent;
            listBox.BorderBrush =
                System.Windows.Application.Current.TryFindResource("AccentBrush")
                    as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.DodgerBlue;
            listBox.BorderThickness = new Thickness(2);
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void EquipmentLaneListBox_DragLeave(
        object sender,
        System.Windows.DragEventArgs e) =>
        ResetEquipmentDropTarget(sender);

    private async void EquipmentLaneListBox_Drop(
        object sender,
        System.Windows.DragEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (sender is not System.Windows.Controls.ListBox listBox
            || listBox.DataContext is not EquipmentLane targetLane
            || e.Data.GetData(typeof(EquipmentItem)) is not EquipmentItem equipment
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        e.Handled = true;
        ResetEquipmentDropTarget(sender);
        await viewModel.AssignEquipmentAsync(
            equipment,
            targetLane,
            GetEquipmentDropIndex(listBox, e, targetLane));
    }

    private static int GetEquipmentDropIndex(
        System.Windows.Controls.ListBox listBox,
        System.Windows.DragEventArgs e,
        EquipmentLane targetLane)
    {
        var hit = listBox.InputHitTest(e.GetPosition(listBox)) as DependencyObject;
        var container = FindVisualAncestor<ListBoxItem>(hit);
        if (container?.DataContext is not EquipmentItem targetEquipment)
        {
            return targetLane.Items.Count;
        }

        var index = targetLane.Items.IndexOf(targetEquipment);
        if (index < 0)
        {
            return targetLane.Items.Count;
        }

        var position = e.GetPosition(container);
        return position.Y > container.ActualHeight / 2
            ? index + 1
            : index;
    }

    private static void ResetEquipmentDropTarget(object sender)
    {
        if (sender is not System.Windows.Controls.ListBox listBox)
        {
            return;
        }

        listBox.Background = System.Windows.Media.Brushes.Transparent;
        listBox.BorderBrush = System.Windows.Media.Brushes.Transparent;
        listBox.BorderThickness = new Thickness(0);
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void FireDrillCredentialsListBox_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox listBox ||
            e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(listBox, source) is not System.Windows.Controls.ListBoxItem
            {
                DataContext: FireDrillCredentialSummary summary
            } ||
            DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var profileWindow = new ClientInfoWindow
        {
            Owner = this,
            DataContext = viewModel.CreateClientInfoProfile(summary)
        };
        profileWindow.Show();
        profileWindow.Activate();
        e.Handled = true;
    }

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
        _notificationService = new WindowsNotificationService();
        ICredentialStore credentialStore = currentUser.IsReadOnlyPreview
            ? ReadOnlyPreviewCredentialStore.Instance
            : new WindowsCredentialStore(LocalUserDataPath.ResolveCredentialScope(
                currentUser.DatabaseInstanceId,
                currentUser.CredentialOwnerSid));

        var viewModel = new MainWindowViewModel(
            repository,
            new SqlServerClientProvider(repository),
            new SqlServerTicketProvider(repository),
            new WhdRestPoster(whdRestClient),
            new SageNativeUiPoster(new SageNativeUiAutomation()),
            whdRestClient,
            new AppDialogService(),
            _notificationService,
            credentialStore,
            currentUser,
            _localPreferences,
            new V2AppUpdateService(_localPreferences.UpdateChannel),
            () => System.Windows.Application.Current.Shutdown());

        viewModel.ActiveClientSessionSelectionRestoreRequested +=
            ViewModel_ActiveClientSessionSelectionRestoreRequested;
        viewModel.AdminCommandTrackingStarted += ViewModel_AdminCommandTrackingStarted;
        DataContext = viewModel;
        if (currentUser.IsReadOnlyPreview)
        {
            Title = $"TechBench V2 - READ-ONLY PREVIEW: {currentUser.LoginName}";
            viewModel.StatusMessage =
                $"READ-ONLY PREVIEW of {currentUser.DisplayName} ({currentUser.LoginName}); "
                + $"authenticated as {currentUser.AuthenticationLabel}.";
            StartPreviewExpiryMonitor(currentUser);
        }
        else
        {
            viewModel.StatusMessage =
                $"Connected to {connectionFactory.Options.Server}/{connectionFactory.Options.Database} "
                + $"as {currentUser.DisplayName}. Server-backed workspace ready.";
        }
        if (!string.IsNullOrWhiteSpace(App.UpdateCompletionVersion))
        {
            viewModel.Updates.MarkUpdateCompleted(App.UpdateCompletionVersion);
        }

        viewModel.Updates.StartAutomaticChecks();
        WhdApiTokenPasswordBox.Password = viewModel.WhdApiToken;
    }

    protected override void OnClosed(EventArgs e)
    {
        _previewExpiryTimer?.Stop();
        _previewExpiryTimer = null;
        SaveWindowPreferences();
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _notificationService.Dispose();
        base.OnClosed(e);
    }

    private void ActiveClientSessionsListBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SetSelectedActiveClientSessions(
                ActiveClientSessionsListBox.SelectedItems.Cast<ClientSessionInfo>());
        }
    }

    private void ViewModel_ActiveClientSessionSelectionRestoreRequested(
        object? sender,
        EventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var selectedSessionIds = viewModel.SelectedActiveClientSessions
            .Select(static session => session.SessionId)
            .ToHashSet();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            ActiveClientSessionsListBox.UnselectAll();
            foreach (var session in ActiveClientSessionsListBox.Items.OfType<ClientSessionInfo>())
            {
                if (selectedSessionIds.Contains(session.SessionId))
                {
                    ActiveClientSessionsListBox.SelectedItems.Add(session);
                }
            }
        });
    }

    private void ViewModel_AdminCommandTrackingStarted(
        object? sender,
        AdminCommandTrackingBatch batch)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var window = new AdminCommandResponseWindow(viewModel, batch)
        {
            Owner = this
        };
        window.Show();
        window.Activate();
    }

    internal static TimeSpan ResolvePreviewTimeRemaining(CurrentUserContext currentUser)
    {
        if (!currentUser.IsReadOnlyPreview
            || currentUser.PreviewExpiresAtUtc is not DateTime expiresAtUtc)
        {
            return TimeSpan.Zero;
        }

        return expiresAtUtc - currentUser.ServerUtc;
    }

    private void StartPreviewExpiryMonitor(CurrentUserContext currentUser)
    {
        var remaining = ResolvePreviewTimeRemaining(currentUser);
        // Leave a small safety margin so the client closes before a new SQL
        // connection can race the server's hard session expiry.
        _previewExpiryDeadlineUtc = DateTime.UtcNow.Add(remaining).AddSeconds(-2);
        _previewExpiryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _previewExpiryTimer.Tick += PreviewExpiryTimer_Tick;
        _previewExpiryTimer.Start();

        if (remaining <= TimeSpan.FromSeconds(2))
        {
            Dispatcher.BeginInvoke(HandlePreviewExpiry);
        }
    }

    private void PreviewExpiryTimer_Tick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow >= _previewExpiryDeadlineUtc)
        {
            HandlePreviewExpiry();
        }
    }

    private void HandlePreviewExpiry()
    {
        if (_previewExpiryHandled)
        {
            return;
        }

        _previewExpiryHandled = true;
        _previewExpiryTimer?.Stop();
        AppDialogWindow.Info(
            "Read-only preview expired",
            "This 30-minute user preview has expired. TechBench V2 will close; reopen it to start another preview.");
        Close();
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
