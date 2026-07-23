using System.Collections.ObjectModel;
using System.Windows.Threading;
using Microsoft.Data.SqlClient;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly Guid _clientSessionId = Guid.NewGuid();
    private readonly DispatcherTimer _clientSessionTimer = new()
    {
        Interval = TimeSpan.FromSeconds(15)
    };
    private ClientSessionInfo? _selectedActiveClientSession;
    private WhdSyncServiceStatus _adminWhdSyncStatus = new();
    private SageSyncServiceStatus _adminSageSyncStatus = new();
    private ClientSessionCommand? _pendingClientSessionCommand;
    private string _adminSessionMessage =
        "A TechBench update is ready. Please save your work and close TechBench.";
    private string _adminCenterActionStatus = "Ready";
    private bool _isAdminCenterBusy;
    private bool _isClientSessionHeartbeatRunning;
    private bool _clientSessionWasRegistered;

    public ObservableCollection<ClientSessionInfo> ActiveClientSessions { get; } = new();

    public AsyncRelayCommand RequestAdminWhdSyncCommand { get; private set; } = null!;

    public AsyncRelayCommand RequestAdminSageSyncCommand { get; private set; } = null!;

    public AsyncRelayCommand ConfirmAdminSageRemovalCommand { get; private set; } = null!;

    public AsyncRelayCommand RefreshAdminCenterCommand { get; private set; } = null!;

    public AsyncRelayCommand NotifyClientForUpdateCommand { get; private set; } = null!;

    public AsyncRelayCommand RequireClientSignOutCommand { get; private set; } = null!;

    public ClientSessionInfo? SelectedActiveClientSession
    {
        get => _selectedActiveClientSession;
        set
        {
            if (SetProperty(ref _selectedActiveClientSession, value))
            {
                NotifyClientForUpdateCommand?.RaiseCanExecuteChanged();
                RequireClientSignOutCommand?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(HasSelectedActiveClientSession));
            }
        }
    }

    public bool HasSelectedActiveClientSession => SelectedActiveClientSession is not null;

    public string AdminSessionMessage
    {
        get => _adminSessionMessage;
        set
        {
            if (SetProperty(ref _adminSessionMessage, value))
            {
                NotifyClientForUpdateCommand?.RaiseCanExecuteChanged();
                RequireClientSignOutCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string AdminCenterActionStatus
    {
        get => _adminCenterActionStatus;
        private set => SetProperty(ref _adminCenterActionStatus, value);
    }

    public bool IsAdminCenterBusy
    {
        get => _isAdminCenterBusy;
        private set
        {
            if (SetProperty(ref _isAdminCenterBusy, value))
            {
                RefreshAdminCenterCommand?.RaiseCanExecuteChanged();
                RequestAdminWhdSyncCommand?.RaiseCanExecuteChanged();
                RequestAdminSageSyncCommand?.RaiseCanExecuteChanged();
                ConfirmAdminSageRemovalCommand?.RaiseCanExecuteChanged();
                NotifyClientForUpdateCommand?.RaiseCanExecuteChanged();
                RequireClientSignOutCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string AdminWhdSyncSummary => _adminWhdSyncStatus.Summary;

    public string AdminWhdSyncDetails =>
        $"Queue: {_adminWhdSyncStatus.QueueDepth} | Last success: "
        + FormatAdminTimestamp(_adminWhdSyncStatus.LastSuccessfulRunAt);

    public string AdminSageSyncSummary => _adminSageSyncStatus.Summary;

    public string AdminSageSyncDetails =>
        $"Queue: {_adminSageSyncStatus.QueueDepth} | Last success: "
        + FormatAdminTimestamp(_adminSageSyncStatus.LastSuccessfulRunAt);

    public bool AdminSageRequiresConfirmation =>
        _adminSageSyncStatus.RequiresLargeRemovalConfirmation;

    private void InitializeAdminCenter()
    {
        RequestAdminWhdSyncCommand = new AsyncRelayCommand(
            RequestAdminWhdSyncAsync,
            _ => CanAccessAdminCenter && !IsAdminCenterBusy);
        RequestAdminSageSyncCommand = new AsyncRelayCommand(
            parameter => RequestAdminSageSyncAsync(false),
            _ => CanAccessAdminCenter && !IsAdminCenterBusy);
        ConfirmAdminSageRemovalCommand = new AsyncRelayCommand(
            parameter => RequestAdminSageSyncAsync(true),
            _ => CanAccessAdminCenter
                && !IsAdminCenterBusy
                && AdminSageRequiresConfirmation);
        RefreshAdminCenterCommand = new AsyncRelayCommand(
            _ => RefreshAdminCenterAsync(),
            _ => CanAccessAdminCenter && !IsAdminCenterBusy);
        NotifyClientForUpdateCommand = new AsyncRelayCommand(
            parameter => QueueClientCommandAsync(ClientSessionCommandTypes.UpdateNotice),
            _ => CanQueueSelectedClientCommand());
        RequireClientSignOutCommand = new AsyncRelayCommand(
            parameter => QueueClientCommandAsync(ClientSessionCommandTypes.SignOut),
            _ => CanQueueSelectedClientCommand());

        if (!CanWrite || IsReadOnlyPreview)
        {
            return;
        }

        _clientSessionTimer.Tick += HandleClientSessionTimerTick;
        _clientSessionTimer.Start();
        _ = RunClientSessionHeartbeatAsync();
        if (CanAccessAdminCenter)
        {
            _ = RefreshAdminCenterAsync();
        }
    }

    private async void HandleClientSessionTimerTick(object? sender, EventArgs e)
    {
        await RunClientSessionHeartbeatAsync();
        if (CanAccessAdminCenter
            && CurrentSection.Equals("Admin Center", StringComparison.Ordinal)
            && !IsAdminCenterBusy)
        {
            await RefreshAdminCenterAsync();
        }
    }

    private async Task RunClientSessionHeartbeatAsync()
    {
        if (_isDisposed || _isClientSessionHeartbeatRunning || !CanWrite || IsReadOnlyPreview)
        {
            return;
        }

        _isClientSessionHeartbeatRunning = true;
        try
        {
            var result = await Task.Run(() => _repository.HeartbeatClientSession(
                _clientSessionId,
                Environment.MachineName,
                _clientVersion,
                CurrentSection,
                Editor.IsDirty || _settingsHaveUnsavedChanges || HasPendingTemplateChanges(),
                IsEntryOperationRunning));
            _clientSessionWasRegistered = true;

            if (result.PendingCommand is not null)
            {
                _pendingClientSessionCommand = result.PendingCommand;
            }

            if (_pendingClientSessionCommand is not null
                && (!IsEntryOperationRunning
                    || !_pendingClientSessionCommand.CommandType.Equals(
                        ClientSessionCommandTypes.SignOut,
                        StringComparison.Ordinal)))
            {
                await ProcessClientSessionCommandAsync(_pendingClientSessionCommand);
            }
        }
        catch (Exception ex) when (
            ex is SqlException
                or InvalidOperationException
                or TimeoutException
                or TaskCanceledException)
        {
            if (CanAccessAdminCenter)
            {
                AdminCenterActionStatus = $"Session status will retry: {ex.Message}";
            }
        }
        finally
        {
            _isClientSessionHeartbeatRunning = false;
        }
    }

    private async Task ProcessClientSessionCommandAsync(ClientSessionCommand command)
    {
        if (command.CommandType.Equals(ClientSessionCommandTypes.UpdateNotice, StringComparison.Ordinal))
        {
            await Task.Run(() => _repository.AcknowledgeClientSessionCommand(
                _clientSessionId,
                command.CommandId,
                "Displayed"));
            _pendingClientSessionCommand = null;
            _notificationService.ShowAdminMessage("TechBench update requested", command.Message);
            _dialogService.Info(
                "TechBench update requested",
                $"{command.RequestedBy} sent this message:\n\n{command.Message}");
            return;
        }

        if (!command.CommandType.Equals(ClientSessionCommandTypes.SignOut, StringComparison.Ordinal))
        {
            await Task.Run(() => _repository.AcknowledgeClientSessionCommand(
                _clientSessionId,
                command.CommandId,
                "Ignored"));
            _pendingClientSessionCommand = null;
            return;
        }

        if (IsEntryOperationRunning)
        {
            StatusMessage =
                "An Admin requested TechBench sign-out. TechBench will close after the current operation finishes.";
            return;
        }

        PersistEditorDraftBeforeExit();
        await Task.Run(() => _repository.AcknowledgeClientSessionCommand(
            _clientSessionId,
            command.CommandId,
            "SignedOut"));
        _pendingClientSessionCommand = null;
        _notificationService.ShowAdminMessage("TechBench is signing out", command.Message);
        _shutdownApplication();
    }

    private void RefreshAdminCenter()
    {
        if (CanAccessAdminCenter && !IsAdminCenterBusy)
        {
            _ = RefreshAdminCenterAsync();
        }
    }

    private async Task RefreshAdminCenterAsync()
    {
        if (!CanAccessAdminCenter || IsAdminCenterBusy)
        {
            return;
        }

        IsAdminCenterBusy = true;
        try
        {
            await RunClientSessionHeartbeatAsync();
            var snapshot = await Task.Run(() => new AdminCenterSnapshot(
                _repository.GetWhdSyncStatus(),
                _repository.GetSageSyncStatus(),
                _repository.GetActiveClientSessions(_clientSessionId)));
            _adminWhdSyncStatus = snapshot.WhdStatus;
            _adminSageSyncStatus = snapshot.SageStatus;
            var selectedSessionId = SelectedActiveClientSession?.SessionId;
            ActiveClientSessions.Clear();
            foreach (var session in snapshot.Sessions)
            {
                ActiveClientSessions.Add(session);
            }

            SelectedActiveClientSession = ActiveClientSessions.FirstOrDefault(
                session => session.SessionId == selectedSessionId);
            RaiseAdminSyncProperties();
            AdminCenterActionStatus =
                $"Refreshed {ActiveClientSessions.Count} active TechBench session(s) at {DateTime.Now:t}.";
        }
        catch (Exception ex)
        {
            AdminCenterActionStatus = $"Admin Center refresh failed: {ex.Message}";
        }
        finally
        {
            IsAdminCenterBusy = false;
        }
    }

    private async Task RequestAdminWhdSyncAsync(object? parameter)
    {
        IsAdminCenterBusy = true;
        try
        {
            var result = await Task.Run(_repository.RequestWhdSync);
            AdminCenterActionStatus = result.Message;
            _adminWhdSyncStatus = await Task.Run(_repository.GetWhdSyncStatus);
            RaiseAdminSyncProperties();
        }
        finally
        {
            IsAdminCenterBusy = false;
        }
    }

    private async Task RequestAdminSageSyncAsync(bool allowLargeRemoval)
    {
        Guid? confirmedRequestId = null;
        if (allowLargeRemoval)
        {
            if (!_adminSageSyncStatus.RequiresLargeRemovalConfirmation
                || _adminSageSyncStatus.LatestRequestId is not Guid requestId)
            {
                AdminCenterActionStatus = "No Sage large-removal approval is currently required.";
                return;
            }

            if (!_dialogService.Confirm(
                    "Confirm Sage customer removal",
                    "The server detected an unusually large set of Sage customers that would become stale. "
                    + "Queue the confirmed synchronization?",
                    "Approve and sync",
                    "Cancel"))
            {
                return;
            }

            confirmedRequestId = requestId;
        }

        IsAdminCenterBusy = true;
        try
        {
            var result = await Task.Run(() => _repository.RequestSageSync(
                allowLargeRemoval,
                confirmedRequestId));
            AdminCenterActionStatus = result.Message;
            _adminSageSyncStatus = await Task.Run(_repository.GetSageSyncStatus);
            RaiseAdminSyncProperties();
        }
        finally
        {
            IsAdminCenterBusy = false;
        }
    }

    private bool CanQueueSelectedClientCommand() =>
        CanAccessAdminCenter
        && !IsAdminCenterBusy
        && SelectedActiveClientSession is { IsCurrentSession: false }
        && !string.IsNullOrWhiteSpace(AdminSessionMessage);

    private async Task QueueClientCommandAsync(string commandType)
    {
        var target = SelectedActiveClientSession;
        if (target is null || target.IsCurrentSession)
        {
            return;
        }

        if (commandType.Equals(ClientSessionCommandTypes.SignOut, StringComparison.Ordinal)
            && !_dialogService.Confirm(
                "Require TechBench sign-out",
                $"Ask {target.UserLabel} on {target.MachineName} to close TechBench? "
                + "TechBench will preserve a recovery draft and wait for any current posting operation.",
                "Require sign-out",
                "Cancel"))
        {
            return;
        }

        IsAdminCenterBusy = true;
        try
        {
            await RunClientSessionHeartbeatAsync();
            _ = await Task.Run(() => _repository.QueueClientSessionCommand(
                _clientSessionId,
                target.SessionId,
                commandType,
                AdminSessionMessage.Trim()));
            AdminCenterActionStatus = commandType.Equals(
                ClientSessionCommandTypes.SignOut,
                StringComparison.Ordinal)
                ? $"Sign-out request sent to {target.UserLabel} on {target.MachineName}."
                : $"Update notice sent to {target.UserLabel} on {target.MachineName}.";
        }
        finally
        {
            IsAdminCenterBusy = false;
        }
    }

    private void RaiseAdminSyncProperties()
    {
        OnPropertyChanged(nameof(AdminWhdSyncSummary));
        OnPropertyChanged(nameof(AdminWhdSyncDetails));
        OnPropertyChanged(nameof(AdminSageSyncSummary));
        OnPropertyChanged(nameof(AdminSageSyncDetails));
        OnPropertyChanged(nameof(AdminSageRequiresConfirmation));
        ConfirmAdminSageRemovalCommand.RaiseCanExecuteChanged();
    }

    private void DisposeAdminCenter()
    {
        _clientSessionTimer.Stop();
        _clientSessionTimer.Tick -= HandleClientSessionTimerTick;
        if (_clientSessionWasRegistered)
        {
            try
            {
                _repository.CloseClientSession(_clientSessionId);
            }
            catch (Exception)
            {
                // A stale heartbeat expires automatically if SQL Server is unavailable during exit.
            }
        }
    }

    private static string FormatAdminTimestamp(DateTime? value) =>
        value is DateTime timestamp ? timestamp.ToLocalTime().ToString("g") : "Never";

    private sealed record AdminCenterSnapshot(
        WhdSyncServiceStatus WhdStatus,
        SageSyncServiceStatus SageStatus,
        IReadOnlyList<ClientSessionInfo> Sessions);
}
