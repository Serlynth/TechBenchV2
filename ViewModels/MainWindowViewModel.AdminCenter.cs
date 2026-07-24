using System.Collections.ObjectModel;
using System.Windows.Threading;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly Guid _clientSessionId = Guid.NewGuid();
    private readonly DispatcherTimer _clientSessionTimer = new()
    {
        Interval = TimeSpan.FromSeconds(15)
    };
    private readonly List<ClientSessionInfo> _selectedActiveClientSessions = [];
    private readonly List<AdminCommandTrackingBatch> _activeAdminCommandTrackingBatches = [];
    private ClientSessionInfo? _selectedActiveClientSession;
    private WhdSyncServiceStatus _adminWhdSyncStatus = new();
    private SageSyncServiceStatus _adminSageSyncStatus = new();
    private ClientSessionCommand? _pendingClientSessionCommand;
    private string _adminSessionMessage =
        "A TechBench update is ready. Please save your work and close TechBench.";
    private string _adminCenterActionStatus = "Ready";
    private bool _isAdminCenterBusy;
    private bool _isAdminCommandTrackingRefreshRunning;
    private bool _isClientSessionHeartbeatRunning;
    private bool _clientSessionWasRegistered;

    public ObservableCollection<ClientSessionInfo> ActiveClientSessions { get; } = new();

    public ObservableCollection<ClientSessionCommandResponse> RecentClientResponses { get; } = new();

    public IReadOnlyList<ClientSessionInfo> SelectedActiveClientSessions =>
        _selectedActiveClientSessions;

    public event EventHandler? ActiveClientSessionSelectionRestoreRequested;

    public event EventHandler<AdminCommandTrackingBatch>? AdminCommandTrackingStarted;

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

    public string SelectedActiveClientSummary => _selectedActiveClientSessions.Count switch
    {
        0 => "No clients selected",
        1 => _selectedActiveClientSessions[0].UserLabel,
        _ => $"{_selectedActiveClientSessions.Count} clients selected"
    };

    public string SelectedActiveClientDetails => _selectedActiveClientSessions.Count switch
    {
        0 => "Select one or more clients from the list.",
        1 => $"{_selectedActiveClientSessions[0].MachineName} | "
            + $"{_selectedActiveClientSessions[0].CurrentSection} | "
            + _selectedActiveClientSessions[0].ActivityLabel,
        _ => string.Join(
            ", ",
            _selectedActiveClientSessions.Select(static session =>
                $"{session.UserLabel} ({session.MachineName})"))
    };

    public string NotifySelectedClientsLabel =>
        $"Notify selected ({_selectedActiveClientSessions.Count})";

    public string SignOutSelectedClientsLabel =>
        $"Require selected sign-out ({_selectedActiveClientSessions.Count})";

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
        $"Queue: {_adminWhdSyncStatus.QueueDepth} | Last attempt: "
        + FormatAdminTimestamp(_adminWhdSyncStatus.LastRunAt)
        + " | Last success: "
        + FormatAdminTimestamp(_adminWhdSyncStatus.LastSuccessfulRunAt);

    public string AdminSageSyncSummary => _adminSageSyncStatus.Summary;

    public string AdminSageSyncDetails =>
        $"Queue: {_adminSageSyncStatus.QueueDepth} | Last attempt: "
        + FormatAdminTimestamp(_adminSageSyncStatus.LastRunAt)
        + " | Last success: "
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
        catch (Exception ex)
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
            _notificationService.ShowAdminMessage("TechBench update requested", command.Message);
            var response = _dialogService.Prompt(
                "TechBench update requested",
                $"{command.RequestedBy} sent this message:\n\n{command.Message}"
                + "\n\nSend a response so the Admin knows you saw it.",
                "I saw this message.",
                "Send response",
                "Seen - no reply");
            var acknowledgementResult = response is null ? "Dismissed" : "Acknowledged";
            var responseMessage = string.IsNullOrWhiteSpace(response)
                ? "Message seen; no written response was sent."
                : response.Trim();
            await Task.Run(() => _repository.AcknowledgeClientSessionCommand(
                _clientSessionId,
                command.CommandId,
                acknowledgementResult,
                responseMessage));
            _pendingClientSessionCommand = null;
            return;
        }

        if (!command.CommandType.Equals(ClientSessionCommandTypes.SignOut, StringComparison.Ordinal))
        {
            await Task.Run(() => _repository.AcknowledgeClientSessionCommand(
                _clientSessionId,
                command.CommandId,
                "Ignored",
                "The installed client did not recognize this command."));
            _pendingClientSessionCommand = null;
            return;
        }

        if (IsEntryOperationRunning)
        {
            StatusMessage =
                "An Admin requested TechBench sign-out. TechBench will close after the current operation finishes.";
            return;
        }

        if (!TrySaveEditorRecoveryDraftForForcedSignOut(out var saveResult))
        {
            await Task.Run(() => _repository.AcknowledgeClientSessionCommand(
                _clientSessionId,
                command.CommandId,
                "SaveFailed",
                saveResult));
            _pendingClientSessionCommand = null;
            StatusMessage = saveResult;
            _notificationService.ShowAdminMessage(
                "TechBench sign-out was stopped",
                saveResult);
            _dialogService.Error(
                "TechBench sign-out was stopped",
                $"{saveResult}\n\nTechBench remains open. Your work was not posted.");
            return;
        }

        _notificationService.ShowAdminMessage("TechBench sign-out required", command.Message);
        _dialogService.Info(
            "TechBench sign-out required",
            $"{command.RequestedBy} requested that TechBench close:\n\n{command.Message}"
            + "\n\nYour current TechBench work was saved locally as a recovery draft and was not "
            + "posted to WHD or Sage. TechBench will close after you select OK.");
        await Task.Run(() => _repository.AcknowledgeClientSessionCommand(
            _clientSessionId,
            command.CommandId,
            "SignedOut",
            saveResult));
        _pendingClientSessionCommand = null;
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
                _repository.GetActiveClientSessions(_clientSessionId),
                _repository.GetRecentClientSessionResponses()));
            _adminWhdSyncStatus = snapshot.WhdStatus;
            _adminSageSyncStatus = snapshot.SageStatus;
            var selectedSessionIds = _selectedActiveClientSessions
                .Select(static session => session.SessionId)
                .ToHashSet();
            ActiveClientSessions.Clear();
            foreach (var session in snapshot.Sessions)
            {
                ActiveClientSessions.Add(session);
            }

            SetSelectedActiveClientSessions(ActiveClientSessions.Where(
                session => selectedSessionIds.Contains(session.SessionId)));
            ActiveClientSessionSelectionRestoreRequested?.Invoke(this, EventArgs.Empty);
            ApplyRecentClientResponses(snapshot.Responses);

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
        && _selectedActiveClientSessions.Any(static session => !session.IsCurrentSession)
        && !string.IsNullOrWhiteSpace(AdminSessionMessage);

    private async Task QueueClientCommandAsync(string commandType)
    {
        var targets = _selectedActiveClientSessions
            .Where(static session => !session.IsCurrentSession)
            .GroupBy(static session => session.SessionId)
            .Select(static group => group.First())
            .ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        if (commandType.Equals(ClientSessionCommandTypes.SignOut, StringComparison.Ordinal)
            && !_dialogService.Confirm(
                "Require TechBench sign-out",
                $"Ask {targets.Length} selected TechBench client(s) to close? "
                + "Each client will preserve a recovery draft and wait for any current posting operation.",
                "Require sign-out",
                "Cancel"))
        {
            return;
        }

        IsAdminCenterBusy = true;
        try
        {
            await RunClientSessionHeartbeatAsync();
            var message = AdminSessionMessage.Trim();
            var queuedCommands = new List<(ClientSessionInfo Session, ClientSessionCommand Command)>();
            var errors = new List<string>();
            foreach (var target in targets)
            {
                try
                {
                    var command = await Task.Run(() => _repository.QueueClientSessionCommand(
                        _clientSessionId,
                        target.SessionId,
                        commandType,
                        message));
                    queuedCommands.Add((target, command));
                }
                catch (Exception ex)
                {
                    errors.Add($"{target.UserLabel} on {target.MachineName}: {ex.Message}");
                }
            }

            if (queuedCommands.Count > 0)
            {
                var trackingBatch = new AdminCommandTrackingBatch(
                    commandType,
                    message,
                    queuedCommands);
                _activeAdminCommandTrackingBatches.Add(trackingBatch);
                AdminCommandTrackingStarted?.Invoke(this, trackingBatch);
            }

            var action = commandType.Equals(
                ClientSessionCommandTypes.SignOut,
                StringComparison.Ordinal)
                ? "Sign-out request"
                : "Update notice";
            AdminCenterActionStatus =
                $"{action} sent to {queuedCommands.Count} selected client(s).";
            if (errors.Count > 0)
            {
                AdminCenterActionStatus +=
                    $" {errors.Count} could not be queued: {string.Join(" | ", errors)}";
            }
        }
        finally
        {
            IsAdminCenterBusy = false;
        }
    }

    public void SetSelectedActiveClientSessions(IEnumerable<ClientSessionInfo> sessions)
    {
        _selectedActiveClientSessions.Clear();
        _selectedActiveClientSessions.AddRange(
            sessions.Where(static session => !session.IsCurrentSession));
        SelectedActiveClientSession = _selectedActiveClientSessions.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedActiveClientSessions));
        OnPropertyChanged(nameof(HasSelectedActiveClientSession));
        OnPropertyChanged(nameof(SelectedActiveClientSummary));
        OnPropertyChanged(nameof(SelectedActiveClientDetails));
        OnPropertyChanged(nameof(NotifySelectedClientsLabel));
        OnPropertyChanged(nameof(SignOutSelectedClientsLabel));
        NotifyClientForUpdateCommand?.RaiseCanExecuteChanged();
        RequireClientSignOutCommand?.RaiseCanExecuteChanged();
    }

    public async Task RefreshAdminCommandTrackingAsync()
    {
        if (!CanAccessAdminCenter || _isAdminCommandTrackingRefreshRunning)
        {
            return;
        }

        _isAdminCommandTrackingRefreshRunning = true;
        try
        {
            var responses = await Task.Run(_repository.GetRecentClientSessionResponses);
            ApplyRecentClientResponses(responses);
        }
        catch (Exception ex)
        {
            AdminCenterActionStatus = $"Response tracking will retry: {ex.Message}";
        }
        finally
        {
            _isAdminCommandTrackingRefreshRunning = false;
        }
    }

    private void ApplyRecentClientResponses(
        IReadOnlyList<ClientSessionCommandResponse> responses)
    {
        RecentClientResponses.Clear();
        foreach (var response in responses)
        {
            RecentClientResponses.Add(response);
        }

        foreach (var batch in _activeAdminCommandTrackingBatches)
        {
            batch.ApplyResponses(responses);
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
        IReadOnlyList<ClientSessionInfo> Sessions,
        IReadOnlyList<ClientSessionCommandResponse> Responses);
}
