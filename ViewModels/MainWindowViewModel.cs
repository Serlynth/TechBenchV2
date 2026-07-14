using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using TechBench.Data;
using TechBench.Models;
using TechBench.Providers;
using TechBench.Services;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const int DefaultWhdAutoSyncMinutes = 5;
    private readonly TechBenchRepository _repository;
    private readonly IClientProvider _clientProvider;
    private readonly ITicketProvider _ticketProvider;
    private readonly IWorkEntryPoster _whdPoster;
    private readonly IWorkEntryPoster _sagePoster;
    private readonly WhdRestClient _whdRestClient;
    private readonly ISageOdbcProcessClient _sageOdbcClient;
    private readonly IUserDialogService _dialogService;
    private readonly IUserNotificationService _notificationService;
    private readonly ICredentialStore _credentialStore;
    private readonly DatabaseBackupService _databaseBackupService;
    private readonly PostingExecutionCoordinator _postingCoordinator = new();
    private readonly DispatcherTimer _whdAutoSyncTimer = new();
    private readonly DispatcherTimer _sageVerificationTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly HashSet<string> _knownWhdTicketKeys = new(StringComparer.OrdinalIgnoreCase);
    private string _currentSection = "Today";
    private string _statusMessage = "Ready";
    private DateTime _selectedDate = DateTime.Today;
    private WorkEntry? _selectedEntry;
    private int _totalBillableMinutes;
    private int _totalNonBillableMinutes;
    private int _weekBillableMinutes;
    private int _weekNonBillableMinutes;
    private int _historyBillableMinutes;
    private int _historyNonBillableMinutes;
    private int _historyEntryCount;
    private int _historyWhdPendingCount;
    private int _historySagePendingCount;
    private int _whdPendingCount;
    private int _sagePendingCount;
    private string _clientSearchText = string.Empty;
    private string _editorClientFilterText = string.Empty;
    private bool _isEditorClientDropDownOpen;
    private bool _isSyncingEditorClientFilterText;
    private int? _editorTicketOptionsClientId;
    private DateTime? _historyStartDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime? _historyEndDate = DateTime.Today;
    private string _historyRangePreset = "This Month";
    private string _historyGroupBy = "Week";
    private string _historyViewMode = "Grouped";
    private bool _isUpdatingHistoryRange;

    private Client? _ticketClientFilter;
    private Ticket? _selectedTicket;
    private TicketStatusOption? _selectedTicketStatus;

    private Client? _searchClient;
    private DateTime? _searchStartDate = DateTime.Today.AddDays(-14);
    private DateTime? _searchEndDate = DateTime.Today;
    private string _searchTicketText = string.Empty;
    private string _searchKeyword = string.Empty;
    private string _searchStatusFilter = "Any";
    private PostingLog? _selectedPostingLog;
    private string _postingLogKeyword = string.Empty;
    private string _postingLogDestinationFilter = "Any";
    private string _postingLogResultFilter = "Any";
    private DateTime? _postingLogStartDate = DateTime.Today.AddDays(-30);
    private DateTime? _postingLogEndDate = DateTime.Today;

    private string _whdBaseUrl = string.Empty;
    private string _whdUsername = string.Empty;
    private string _whdApiToken = string.Empty;
    private string _selectedWhdAuthenticationMode = "Auto (detect once)";
    private bool _whdAutoSyncEnabled = true;
    private string _whdAutoSyncMinutesText = DefaultWhdAutoSyncMinutes.ToString();
    private bool _isWhdAutoSyncRunning;
    private bool _isSageVerificationRunning;
    private bool _isSagePostingRunning;
    private int _sageVerificationCursor;
    private DateTime? _lastWhdAutoSyncAt;
    private string _sageEmployeeId = string.Empty;
    private string _sageActivityItemId = string.Empty;
    private Client? _selectedSageMappingClient;
    private string _sageMappedCustomerId = string.Empty;
    private string _sageDsn = string.Empty;
    private string _sageUsername = string.Empty;
    private string _sagePassword = string.Empty;
    private string _sageCompanyPath = string.Empty;
    private bool _sageNativeAutoSave;
    private bool _isLightTheme;
    private bool _isEntryOperationRunning;
    private string _entryOperationText = string.Empty;
    private bool _isPostedEditorUnlocked;
    private bool _isSynchronizingEditorReferences;
    private bool _isRefreshingTodayEntries;
    private bool _hasCloseoutIssues;
    private WorkEntry? _lastDeletedEntry;
    private string _databaseHealthLabel = "Database check has not run.";
    private string _lastDatabaseBackupLabel = "No local backup has been created yet.";
    private bool _isDatabaseHealthy = true;

    public MainWindowViewModel(
        TechBenchRepository repository,
        IClientProvider clientProvider,
        ITicketProvider ticketProvider,
        IWorkEntryPoster whdPoster,
        IWorkEntryPoster sagePoster,
        WhdRestClient whdRestClient,
        ISageOdbcProcessClient sageOdbcClient,
        IUserDialogService dialogService,
        IUserNotificationService notificationService,
        ICredentialStore credentialStore,
        DatabaseBackupService databaseBackupService,
        IAppUpdateService appUpdateService,
        Action shutdownApplication)
    {
        _repository = repository;
        _clientProvider = clientProvider;
        _ticketProvider = ticketProvider;
        _whdPoster = whdPoster;
        _sagePoster = sagePoster;
        _whdRestClient = whdRestClient;
        _sageOdbcClient = sageOdbcClient;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _credentialStore = credentialStore;
        _databaseBackupService = databaseBackupService;
        Updates = new AppUpdateViewModel(
            appUpdateService,
            () => _databaseBackupService.CreateBackup("Pre-update database backup"),
            PersistEditorDraftBeforeExit,
            shutdownApplication,
            () => !IsEntryOperationRunning,
            _notificationService.ShowUpdateAvailable);

        NavigateCommand = new RelayCommand(parameter => Navigate(parameter?.ToString() ?? "Today"));
        EditEntryCommand = new RelayCommand(EditEntry, parameter => parameter is WorkEntry { Id: > 0 });
        NewEntryCommand = new RelayCommand(_ => NewEntry());
        SaveEntryCommand = new RelayCommand(_ => SaveEntry(), _ => CanSaveEditor());
        DeleteEntryCommand = new RelayCommand(_ => DeleteEntry(), _ => CanDeleteEditorEntry());
        DuplicateEntryCommand = new RelayCommand(_ => DuplicateEntry(), _ => Editor.Id > 0);
        UndoDeleteCommand = new RelayCommand(_ => UndoDelete(), _ => _lastDeletedEntry is not null);
        UnlockPostedEntryCommand = new RelayCommand(_ => UnlockPostedEntry(), _ => IsEditorLocked);
        RefreshAllCommand = new RelayCommand(_ => RefreshAll());
        ExportDailyCsvCommand = new RelayCommand(_ => ExportDailyCsv());
        ExportWeeklyCsvCommand = new RelayCommand(_ => ExportWeeklyCsv());
        RefreshHistoryCommand = new RelayCommand(_ => RefreshHistory());
        ExportHistoryCsvCommand = new RelayCommand(_ => ExportHistoryCsv());
        PostWhdCommand = new AsyncRelayCommand(PostWhdAsync, CanPostWhdEntry);
        PostSageCommand = new AsyncRelayCommand(PostSageAsync, CanPostSageEntry);
        VerifySageSaveCommand = new AsyncRelayCommand(VerifySageSaveAsync, CanVerifySageSave);
        BatchPostWhdCommand = new AsyncRelayCommand(BatchPostWhdAsync);
        MarkWhdPostedCommand = new RelayCommand(parameter => MarkPosted(parameter, "WHD"), CanResolveSavedEntry);
        OpenWhdTicketCommand = new RelayCommand(OpenWhdTicket, CanOpenWhdTicket);
        SelectCloseoutIssueCommand = new RelayCommand(SelectCloseoutIssue, parameter => parameter is CloseoutItem { HasIssue: true });
        SelectAllPostingQueueCommand = new RelayCommand(_ => SetPostingQueueSelection(true));
        SelectFailedPostingQueueCommand = new RelayCommand(_ => SelectFailedPostingQueueEntries());
        ClearPostingQueueSelectionCommand = new RelayCommand(_ => SetPostingQueueSelection(false));
        RunSearchCommand = new RelayCommand(_ => RunSearch());
        ClearSearchCommand = new RelayCommand(_ => ClearSearch());
        RefreshPostingLogsCommand = new RelayCommand(_ => RefreshPostingLogs());
        ClearPostingLogFiltersCommand = new RelayCommand(_ => ClearPostingLogFilters());
        OpenPostingLogEntryCommand = new RelayCommand(OpenPostingLogEntry, parameter => parameter is PostingLog { WorkEntryId: > 0 } || SelectedPostingLog is { WorkEntryId: > 0 });
        ChangeTicketStatusCommand = new AsyncRelayCommand(ChangeTicketStatusAsync, CanChangeTicketStatus);
        SelectEditorClientCommand = new RelayCommand(SelectEditorClient, parameter => parameter is Client);
        SaveSageCustomerMappingCommand = new RelayCommand(_ => SaveSageCustomerMapping(), _ => SelectedSageMappingClient is not null);
        SaveSettingsCommand = new RelayCommand(_ => SaveSettings());
        TestWhdConnectionCommand = new AsyncRelayCommand(TestWhdConnectionAsync);
        SyncWhdTicketsCommand = new AsyncRelayCommand(SyncWhdTicketsNowAsync);
        SyncWhdClientsCommand = new AsyncRelayCommand(SyncWhdClientsAsync);
        SyncWhdStatusesCommand = new AsyncRelayCommand(SyncWhdStatusesAsync);
        TestSageConnectionCommand = new RelayCommand(_ => TestSageConnection());
        TestSageOdbcCommand = new AsyncRelayCommand(TestSageOdbcAsync);
        SyncSageCustomersCommand = new AsyncRelayCommand(SyncSageCustomersAsync);
        BackupDatabaseCommand = new RelayCommand(_ => BackupDatabase(), _ => !IsEntryOperationRunning);
        CheckDatabaseHealthCommand = new RelayCommand(_ => CheckDatabaseHealth(), _ => !IsEntryOperationRunning);
        OpenBackupFolderCommand = new RelayCommand(_ => OpenBackupFolder());
        InitializeNoteFeatures();

        StatusFilterOptions.Add("Any");
        StatusFilterOptions.Add("Draft");
        StatusFilterOptions.Add("Ready");
        StatusFilterOptions.Add("Posted to WHD");
        StatusFilterOptions.Add("Posted to Sage");
        StatusFilterOptions.Add("Posted to Both");
        StatusFilterOptions.Add("Failed");

        HistoryRangeOptions.Add("This Week");
        HistoryRangeOptions.Add("Last Week");
        HistoryRangeOptions.Add("This Month");
        HistoryRangeOptions.Add("Last Month");
        HistoryRangeOptions.Add("This Year");
        HistoryRangeOptions.Add("Last Year");
        HistoryRangeOptions.Add("Custom");

        HistoryGroupOptions.Add("Day");
        HistoryGroupOptions.Add("Week");
        HistoryGroupOptions.Add("Month");
        HistoryGroupOptions.Add("Year");

        PostingLogDestinationOptions.Add("Any");
        PostingLogDestinationOptions.Add("WHD");
        PostingLogDestinationOptions.Add("Sage");

        PostingLogResultOptions.Add("Any");
        PostingLogResultOptions.Add("Success");
        PostingLogResultOptions.Add("Failed");

        WhdAuthenticationModeOptions.Add("Auto (detect once)");
        WhdAuthenticationModeOptions.Add("Username + application API key");
        WhdAuthenticationModeOptions.Add("Technician API key");
        WhdAuthenticationModeOptions.Add("Username + password");

        foreach (var template in _repository.GetTemplates())
        {
            NoteTemplates.Add(template);
        }

        Editor.PropertyChanged += HandleEditorPropertyChanged;
        _whdAutoSyncTimer.Tick += HandleWhdAutoSyncTimerTick;
        _sageVerificationTimer.Tick += HandleSageVerificationTimerTick;

        LoadSettings();
        RefreshDatabaseSafetyStatus();
        RefreshAll();
        PrimeKnownWhdTicketKeys();
        ConfigureWhdAutoSyncTimer();
        ConfigureSageVerificationTimer();
        RunSearch();
        NewEntry();
        RestoreEditorDraft();

        if (_databaseBackupService.LastBackupResult is { Succeeded: false } backupFailure)
        {
            StatusMessage = backupFailure.Message;
        }
        else if (!_isDatabaseHealthy)
        {
            StatusMessage = DatabaseHealthLabel;
        }
    }

    public WorkEntryEditorViewModel Editor { get; } = new();
    public AppUpdateViewModel Updates { get; }
    public ObservableCollection<Client> Clients { get; } = new();
    public ObservableCollection<Client> EditorClients { get; } = new();
    public ObservableCollection<Client> ManagedClients { get; } = new();
    public ObservableCollection<Ticket> TicketsForEditor { get; } = new();
    public ObservableCollection<Ticket> Tickets { get; } = new();
    public ObservableCollection<TicketStatusOption> TicketStatusOptions { get; } = new();
    public ObservableCollection<WorkEntry> Entries { get; } = new();
    public ObservableCollection<DayWorkGroup> WeekGroups { get; } = new();
    public ObservableCollection<HistoryWorkGroup> HistoryGroups { get; } = new();
    public ObservableCollection<WorkEntry> HistoryTimelineEntries { get; } = new();
    public ObservableCollection<WorkEntry> SearchResults { get; } = new();
    public ObservableCollection<WorkEntry> PostingQueue { get; } = new();
    public ObservableCollection<PostingLog> PostingLogs { get; } = new();
    public ObservableCollection<CloseoutItem> DailyCloseoutItems { get; } = new();
    public ObservableCollection<NoteTemplate> NoteTemplates { get; } = new();
    public ObservableCollection<string> HistoryRangeOptions { get; } = new();
    public ObservableCollection<string> HistoryGroupOptions { get; } = new();
    public ObservableCollection<string> StatusFilterOptions { get; } = new();
    public ObservableCollection<string> PostingLogDestinationOptions { get; } = new();
    public ObservableCollection<string> PostingLogResultOptions { get; } = new();
    public ObservableCollection<string> WhdAuthenticationModeOptions { get; } = new();

    public RelayCommand NavigateCommand { get; }
    public RelayCommand EditEntryCommand { get; }
    public RelayCommand NewEntryCommand { get; }
    public RelayCommand SaveEntryCommand { get; }
    public RelayCommand DeleteEntryCommand { get; }
    public RelayCommand DuplicateEntryCommand { get; }
    public RelayCommand UndoDeleteCommand { get; }
    public RelayCommand UnlockPostedEntryCommand { get; }
    public RelayCommand RefreshAllCommand { get; }
    public RelayCommand ExportDailyCsvCommand { get; }
    public RelayCommand ExportWeeklyCsvCommand { get; }
    public RelayCommand RefreshHistoryCommand { get; }
    public RelayCommand ExportHistoryCsvCommand { get; }
    public AsyncRelayCommand PostWhdCommand { get; }
    public AsyncRelayCommand PostSageCommand { get; }
    public AsyncRelayCommand VerifySageSaveCommand { get; }
    public AsyncRelayCommand BatchPostWhdCommand { get; }
    public RelayCommand MarkWhdPostedCommand { get; }
    public RelayCommand OpenWhdTicketCommand { get; }
    public RelayCommand SelectCloseoutIssueCommand { get; }
    public RelayCommand SelectAllPostingQueueCommand { get; }
    public RelayCommand SelectFailedPostingQueueCommand { get; }
    public RelayCommand ClearPostingQueueSelectionCommand { get; }
    public RelayCommand RunSearchCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand RefreshPostingLogsCommand { get; }
    public RelayCommand ClearPostingLogFiltersCommand { get; }
    public RelayCommand OpenPostingLogEntryCommand { get; }
    public AsyncRelayCommand ChangeTicketStatusCommand { get; }
    public RelayCommand SelectEditorClientCommand { get; }
    public RelayCommand SaveSageCustomerMappingCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand TestWhdConnectionCommand { get; }
    public AsyncRelayCommand SyncWhdTicketsCommand { get; }
    public AsyncRelayCommand SyncWhdClientsCommand { get; }
    public AsyncRelayCommand SyncWhdStatusesCommand { get; }
    public RelayCommand TestSageConnectionCommand { get; }
    public AsyncRelayCommand TestSageOdbcCommand { get; }
    public AsyncRelayCommand SyncSageCustomersCommand { get; }
    public RelayCommand BackupDatabaseCommand { get; }
    public RelayCommand CheckDatabaseHealthCommand { get; }
    public RelayCommand OpenBackupFolderCommand { get; }

    public string DatabasePath => _repository.DatabasePath;
    public string DatabaseBackupDirectory => _databaseBackupService.BackupDirectory;

    public string DatabaseHealthLabel
    {
        get => _databaseHealthLabel;
        private set => SetProperty(ref _databaseHealthLabel, value);
    }

    public string LastDatabaseBackupLabel
    {
        get => _lastDatabaseBackupLabel;
        private set => SetProperty(ref _lastDatabaseBackupLabel, value);
    }

    public bool IsDatabaseHealthy
    {
        get => _isDatabaseHealthy;
        private set => SetProperty(ref _isDatabaseHealthy, value);
    }
    public string EditorTitle => Editor.Id > 0 ? "Edit Entry" : "New Entry";
    public string EditorSubtitle => Editor.SelectedClient?.DisplayName
        ?? (Editor.UseManualClient && !string.IsNullOrWhiteSpace(Editor.ManualClientName)
            ? Editor.ManualClientName
            : "Select a client to begin");
    public bool IsEditorLocked => Editor.HasPostedDestination && !_isPostedEditorUnlocked;
    public bool IsEditorEditable => !IsEditorLocked && !IsEntryOperationRunning;
    public bool ShowOpenWhdAction => Editor.SelectedTicket is { Id: > 0 };
    public bool HasTodayEntries => Entries.Count > 0;
    public bool HasPostingQueueEntries => PostingQueue.Count > 0;
    public bool HasUndoDelete => _lastDeletedEntry is not null;
    public string UndoDeleteLabel => _lastDeletedEntry is null
        ? string.Empty
        : $"Deleted entry for {_lastDeletedEntry.ClientDisplay}.";
    public bool HasCloseoutIssues
    {
        get => _hasCloseoutIssues;
        private set => SetProperty(ref _hasCloseoutIssues, value);
    }

    public bool IsEntryOperationRunning
    {
        get => _isEntryOperationRunning;
        private set
        {
            if (SetProperty(ref _isEntryOperationRunning, value))
            {
                OnPropertyChanged(nameof(IsEditorEditable));
                OnPropertyChanged(nameof(WorkspaceStateLabel));
                RaiseEditorWorkflowCommandStates();
                BackupDatabaseCommand.RaiseCanExecuteChanged();
                CheckDatabaseHealthCommand.RaiseCanExecuteChanged();
                ImportGoogleSheetsCommand.RaiseCanExecuteChanged();
                Updates.RefreshCommandStates();
            }
        }
    }

    public string EntryOperationText
    {
        get => _entryOperationText;
        private set => SetProperty(ref _entryOperationText, value);
    }

    public string WorkspaceStateLabel => IsEntryOperationRunning
        ? "W O R K I N G"
        : EditorSaveStatus.ToUpperInvariant();

    public string CurrentSection
    {
        get => _currentSection;
        set
        {
            if (SetProperty(ref _currentSection, value))
            {
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    public string WindowTitle => $"TechBench - {CurrentSection}";

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public DateTime SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (SetProperty(ref _selectedDate, value.Date))
            {
                RefreshTodayEntries();
                RefreshWeek();
                RefreshPostingQueue();
                UpdateTotals();
            }
        }
    }

    public WorkEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (ReferenceEquals(_selectedEntry, value))
            {
                return;
            }

            if (_isRefreshingTodayEntries)
            {
                _selectedEntry = value;
                OnPropertyChanged();
                return;
            }

            var sameEntry = value is { Id: > 0 }
                && (_selectedEntry?.Id == value.Id || Editor.Id == value.Id);
            if (!sameEntry && Editor.IsDirty)
            {
                var discard = _dialogService.Confirm(
                    "Unsaved changes",
                    "Discard the unsaved changes in the current entry?",
                    "Discard",
                    "Keep editing");
                if (!discard)
                {
                    OnPropertyChanged();
                    return;
                }

                ClearPersistedEditorDraft();
            }

            _selectedEntry = value;
            OnPropertyChanged();
            if (value is null)
            {
                return;
            }

            if (sameEntry && Editor.IsDirty)
            {
                UpdateEditorPostingState(value);
                return;
            }

            LoadEntryIntoEditor(value);
            StatusMessage = $"Editing {value.ClientDisplay}. Use New Entry to start a separate note.";
            RaiseEntryCommandStates();
            OpenWhdTicketCommand.RaiseCanExecuteChanged();
        }
    }

    public string TotalBillableLabel => FormatMinutes(_totalBillableMinutes);
    public string TotalNonBillableLabel => FormatMinutes(_totalNonBillableMinutes);
    public string WeekTotalLabel => FormatMinutes(_weekBillableMinutes + _weekNonBillableMinutes);
    public string WeekBillableLabel => FormatMinutes(_weekBillableMinutes);
    public string WeekNonBillableLabel => FormatMinutes(_weekNonBillableMinutes);
    public string HistoryTotalLabel => FormatMinutes(_historyBillableMinutes + _historyNonBillableMinutes);
    public string HistoryBillableLabel => FormatMinutes(_historyBillableMinutes);
    public string HistoryNonBillableLabel => FormatMinutes(_historyNonBillableMinutes);
    public string HistoryEntryCountLabel => $"{_historyEntryCount} entries";
    public string HistoryWhdPendingLabel => _historyWhdPendingCount.ToString();
    public string HistorySagePendingLabel => _historySagePendingCount.ToString();

    public DateTime? HistoryStartDate
    {
        get => _historyStartDate;
        set
        {
            if (SetProperty(ref _historyStartDate, value?.Date) && !_isUpdatingHistoryRange)
            {
                SetHistoryPresetWithoutApplying("Custom");
            }
        }
    }

    public DateTime? HistoryEndDate
    {
        get => _historyEndDate;
        set
        {
            if (SetProperty(ref _historyEndDate, value?.Date) && !_isUpdatingHistoryRange)
            {
                SetHistoryPresetWithoutApplying("Custom");
            }
        }
    }

    public string HistoryRangePreset
    {
        get => _historyRangePreset;
        set
        {
            if (SetProperty(ref _historyRangePreset, value) && !_isUpdatingHistoryRange)
            {
                ApplyHistoryRangePreset(value);
                RefreshHistory();
            }
        }
    }

    public string HistoryGroupBy
    {
        get => _historyGroupBy;
        set
        {
            if (SetProperty(ref _historyGroupBy, value))
            {
                RefreshHistory();
            }
        }
    }

    public string HistoryViewMode
    {
        get => _historyViewMode;
        set
        {
            if (SetProperty(ref _historyViewMode, value))
            {
                OnPropertyChanged(nameof(IsHistoryGroupedView));
                OnPropertyChanged(nameof(IsHistoryInlineTimelineView));
            }
        }
    }

    public bool IsHistoryGroupedView
    {
        get => HistoryViewMode == "Grouped";
        set
        {
            if (value)
            {
                HistoryViewMode = "Grouped";
            }
        }
    }

    public bool IsHistoryInlineTimelineView
    {
        get => HistoryViewMode == "Inline";
        set
        {
            if (value)
            {
                HistoryViewMode = "Inline";
            }
        }
    }

    public int WhdPendingCount
    {
        get => _whdPendingCount;
        private set => SetProperty(ref _whdPendingCount, value);
    }

    public int SagePendingCount
    {
        get => _sagePendingCount;
        private set => SetProperty(ref _sagePendingCount, value);
    }

    public string ClientSearchText
    {
        get => _clientSearchText;
        set
        {
            if (SetProperty(ref _clientSearchText, value))
            {
                RefreshClients();
            }
        }
    }

    public string EditorClientFilterText
    {
        get => _editorClientFilterText;
        set
        {
            if (SetProperty(ref _editorClientFilterText, value))
            {
                if (!_isSyncingEditorClientFilterText)
                {
                    RefreshEditorClientOptions();
                }
            }
        }
    }

    public bool IsEditorClientDropDownOpen
    {
        get => _isEditorClientDropDownOpen;
        set => SetProperty(ref _isEditorClientDropDownOpen, value);
    }

    public Client? TicketClientFilter
    {
        get => _ticketClientFilter;
        set
        {
            if (SetProperty(ref _ticketClientFilter, value))
            {
                RefreshTicketList();
            }
        }
    }

    public Ticket? SelectedTicket
    {
        get => _selectedTicket;
        set
        {
            if (SetProperty(ref _selectedTicket, value))
            {
                SelectedTicketStatus = ResolveTicketStatusOption(value);
                TryAutoMatchSageCustomerForTicket(value);
                ChangeTicketStatusCommand.RaiseCanExecuteChanged();
                OpenWhdTicketCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public TicketStatusOption? SelectedTicketStatus
    {
        get => _selectedTicketStatus;
        set
        {
            if (SetProperty(ref _selectedTicketStatus, value))
            {
                ChangeTicketStatusCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public Client? SearchClient
    {
        get => _searchClient;
        set => SetProperty(ref _searchClient, value);
    }

    public DateTime? SearchStartDate
    {
        get => _searchStartDate;
        set => SetProperty(ref _searchStartDate, value?.Date);
    }

    public DateTime? SearchEndDate
    {
        get => _searchEndDate;
        set => SetProperty(ref _searchEndDate, value?.Date);
    }

    public string SearchTicketText
    {
        get => _searchTicketText;
        set => SetProperty(ref _searchTicketText, value);
    }

    public string SearchKeyword
    {
        get => _searchKeyword;
        set => SetProperty(ref _searchKeyword, value);
    }

    public string SearchStatusFilter
    {
        get => _searchStatusFilter;
        set => SetProperty(ref _searchStatusFilter, value);
    }

    public PostingLog? SelectedPostingLog
    {
        get => _selectedPostingLog;
        set
        {
            if (SetProperty(ref _selectedPostingLog, value))
            {
                OpenPostingLogEntryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PostingLogKeyword
    {
        get => _postingLogKeyword;
        set => SetProperty(ref _postingLogKeyword, value);
    }

    public string PostingLogDestinationFilter
    {
        get => _postingLogDestinationFilter;
        set => SetProperty(ref _postingLogDestinationFilter, value);
    }

    public string PostingLogResultFilter
    {
        get => _postingLogResultFilter;
        set => SetProperty(ref _postingLogResultFilter, value);
    }

    public DateTime? PostingLogStartDate
    {
        get => _postingLogStartDate;
        set => SetProperty(ref _postingLogStartDate, value?.Date);
    }

    public DateTime? PostingLogEndDate
    {
        get => _postingLogEndDate;
        set => SetProperty(ref _postingLogEndDate, value?.Date);
    }

    public string WhdBaseUrl
    {
        get => _whdBaseUrl;
        set => SetProperty(ref _whdBaseUrl, value);
    }

    public string WhdUsername
    {
        get => _whdUsername;
        set => SetProperty(ref _whdUsername, value);
    }

    public string WhdApiToken
    {
        get => _whdApiToken;
        set => SetProperty(ref _whdApiToken, value);
    }

    public string SelectedWhdAuthenticationMode
    {
        get => _selectedWhdAuthenticationMode;
        set => SetProperty(ref _selectedWhdAuthenticationMode, value);
    }

    public bool WhdAutoSyncEnabled
    {
        get => _whdAutoSyncEnabled;
        set
        {
            if (SetProperty(ref _whdAutoSyncEnabled, value))
            {
                ConfigureWhdAutoSyncTimer();
                OnPropertyChanged(nameof(WhdAutoSyncStatusLabel));
            }
        }
    }

    public string WhdAutoSyncMinutesText
    {
        get => _whdAutoSyncMinutesText;
        set
        {
            if (SetProperty(ref _whdAutoSyncMinutesText, value))
            {
                ConfigureWhdAutoSyncTimer();
                OnPropertyChanged(nameof(WhdAutoSyncStatusLabel));
            }
        }
    }

    public string WhdAutoSyncStatusLabel
    {
        get
        {
            if (!WhdAutoSyncEnabled)
            {
                return "Auto-sync is off.";
            }

            var interval = ResolveWhdAutoSyncIntervalMinutes();
            return _lastWhdAutoSyncAt.HasValue
                ? $"Auto-sync every {interval} min. Last sync: {_lastWhdAutoSyncAt.Value:g}."
                : $"Auto-sync every {interval} min. Waiting for next sync.";
        }
    }

    public string SageEmployeeId
    {
        get => _sageEmployeeId;
        set => SetProperty(ref _sageEmployeeId, value);
    }

    public string SageActivityItemId
    {
        get => _sageActivityItemId;
        set => SetProperty(ref _sageActivityItemId, value);
    }

    public Client? SelectedSageMappingClient
    {
        get => _selectedSageMappingClient;
        set
        {
            if (SetProperty(ref _selectedSageMappingClient, value))
            {
                SageMappedCustomerId = value is null
                    ? string.Empty
                    : !string.IsNullOrWhiteSpace(value.SageCustomerId)
                        ? value.SageCustomerId
                        : _repository.GetSettings().GetValueOrDefault(BuildSageCustomerSettingKey(value.Id), string.Empty);
                SaveSageCustomerMappingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SageMappedCustomerId
    {
        get => _sageMappedCustomerId;
        set => SetProperty(ref _sageMappedCustomerId, value);
    }

    public string SageDsn
    {
        get => _sageDsn;
        set => SetProperty(ref _sageDsn, value);
    }

    public string SageUsername
    {
        get => _sageUsername;
        set => SetProperty(ref _sageUsername, value);
    }

    public string SagePassword
    {
        get => _sagePassword;
        set => SetProperty(ref _sagePassword, value);
    }

    public string SageCompanyPath
    {
        get => _sageCompanyPath;
        set => SetProperty(ref _sageCompanyPath, value);
    }

    public bool SageNativeAutoSave
    {
        get => _sageNativeAutoSave;
        set => SetProperty(ref _sageNativeAutoSave, value);
    }

    public bool IsLightTheme
    {
        get => _isLightTheme;
        set
        {
            if (SetProperty(ref _isLightTheme, value))
            {
                ThemeService.Apply(value ? AppTheme.Light : AppTheme.Dark);
            }
        }
    }

    private void Navigate(string section)
    {
        if (section == "Today")
        {
            SelectedDate = DateTime.Today;
        }

        CurrentSection = section;
        RefreshCurrentSectionData();
        StatusMessage = section switch
        {
            "Today" => $"Showing worklog for {SelectedDate:dddd, MMM d}",
            "This Week" => "Showing weekly grouped worklog",
            "History" => "Showing historical worklog",
            "Posting Queue" => "Showing entries still pending WHD or Sage posting",
            "Posting History" => "Showing WHD and Sage posting history",
            "Client List" => "Showing synced/imported clients",
            "Ticket List" => "Showing assigned non-closed tickets",
            _ => $"Showing {section}"
        };
    }

    private void RefreshCurrentSectionData()
    {
        switch (CurrentSection)
        {
            case "Today":
                RefreshTodayEntries();
                break;
            case "This Week":
                RefreshWeek();
                break;
            case "History":
                RefreshHistory();
                break;
            case "Search":
                RunSearch();
                break;
            case "Posting Queue":
                RefreshPostingQueue();
                break;
            case "Posting History":
                RefreshPostingLogs();
                break;
            case "Client List":
                RefreshClients();
                break;
            case "Ticket List":
                RefreshTicketList();
                break;
        }
    }

    private void RefreshAll()
    {
        RefreshClients();
        RefreshTicketStatusOptions();
        RefreshTicketList();
        RefreshTodayEntries();
        RefreshWeek();
        RefreshHistory();
        RefreshPostingQueue();
        RefreshPostingLogs();
        UpdateTotals();
    }

    private void RefreshClients()
    {
        var editorClientId = Editor.SelectedClient?.Id;
        var ticketFilterId = TicketClientFilter?.Id;
        var searchClientId = SearchClient?.Id;
        var sageMappingClientId = SelectedSageMappingClient?.Id;

        Clients.Clear();
        foreach (var client in _clientProvider.SearchClientsAsync(ClientSearchText).GetAwaiter().GetResult())
        {
            Clients.Add(client);
        }

        ManagedClients.Clear();
        foreach (var client in _repository.GetClients(searchTerm: ClientSearchText))
        {
            ManagedClients.Add(client);
        }

        if (!Editor.IsDirty)
        {
            _isSynchronizingEditorReferences = true;
            try
            {
                Editor.RunWithoutDirtyTracking(() =>
                    Editor.SelectedClient = editorClientId.HasValue
                        ? Clients.FirstOrDefault(client => client.Id == editorClientId.Value)
                        : Editor.SelectedClient);
            }
            finally
            {
                _isSynchronizingEditorReferences = false;
            }
        }
        TicketClientFilter = ticketFilterId.HasValue ? Clients.FirstOrDefault(client => client.Id == ticketFilterId.Value) : TicketClientFilter;
        SearchClient = searchClientId.HasValue ? Clients.FirstOrDefault(client => client.Id == searchClientId.Value) : SearchClient;
        SelectedSageMappingClient = sageMappingClientId.HasValue ? Clients.FirstOrDefault(client => client.Id == sageMappingClientId.Value) : SelectedSageMappingClient;
        RefreshEditorClientOptions();
    }

    private void RefreshEditorClientOptions()
    {
        var selectedClient = Editor.SelectedClient;
        var clients = _clientProvider.SearchClientsAsync(EditorClientFilterText).GetAwaiter().GetResult().ToList();
        if (selectedClient is not null)
        {
            clients.RemoveAll(client => client.Id == selectedClient.Id);
            clients.Insert(0, selectedClient);
        }

        var wasSynchronizing = _isSynchronizingEditorReferences;
        _isSynchronizingEditorReferences = true;
        try
        {
            Editor.RunWithoutDirtyTracking(() =>
            {
                if (selectedClient is null)
                {
                    EditorClients.Clear();
                }
                else
                {
                    for (var index = EditorClients.Count - 1; index >= 0; index--)
                    {
                        if (!ReferenceEquals(EditorClients[index], selectedClient))
                        {
                            EditorClients.RemoveAt(index);
                        }
                    }

                    if (!EditorClients.Contains(selectedClient))
                    {
                        EditorClients.Insert(0, selectedClient);
                    }
                }

                foreach (var client in clients.Where(candidate => selectedClient is null || candidate.Id != selectedClient.Id))
                {
                    EditorClients.Add(client);
                }

                if (selectedClient is not null && !ReferenceEquals(Editor.SelectedClient, selectedClient))
                {
                    Editor.SelectedClient = selectedClient;
                }
            });
        }
        finally
        {
            _isSynchronizingEditorReferences = wasSynchronizing;
        }
    }

    private void RefreshEditorTickets(int? preferredTicketId = null)
    {
        var selectedTicketId = preferredTicketId ?? (Editor.SelectedTicket is { Id: > 0 } ? Editor.SelectedTicket.Id : null);
        var selectedClient = Editor.SelectedClient;
        var wasSynchronizing = _isSynchronizingEditorReferences;
        _isSynchronizingEditorReferences = true;
        try
        {
            Editor.RunWithoutDirtyTracking(() =>
            {
                if (selectedClient is null)
                {
                    var noClientTicket = CreateNoTicketOption(0);
                    TicketsForEditor.Clear();
                    TicketsForEditor.Add(noClientTicket);
                    _editorTicketOptionsClientId = null;
                    Editor.SelectedTicket = noClientTicket;
                    return;
                }

                if (_editorTicketOptionsClientId != selectedClient.Id || TicketsForEditor.Count == 0)
                {
                    TicketsForEditor.Clear();
                    TicketsForEditor.Add(CreateNoTicketOption(selectedClient.Id));
                    foreach (var ticket in _ticketProvider.SearchTicketsAsync(selectedClient.Id, null).GetAwaiter().GetResult())
                    {
                        TicketsForEditor.Add(ticket);
                    }

                    _editorTicketOptionsClientId = selectedClient.Id;
                }

                var noTicket = TicketsForEditor.First(ticket => ticket.Id <= 0);
                var selectedTicket = selectedTicketId.HasValue
                    ? TicketsForEditor.FirstOrDefault(ticket => ticket.Id == selectedTicketId.Value)
                    : noTicket;

                if (selectedTicket is null
                    && selectedTicketId.HasValue
                    && _repository.GetTicket(selectedTicketId.Value) is { } savedTicket
                    && savedTicket.ClientId == selectedClient.Id)
                {
                    TicketsForEditor.Add(savedTicket);
                    selectedTicket = savedTicket;
                }

                Editor.SelectedTicket = selectedTicket ?? noTicket;
            });
        }
        finally
        {
            _isSynchronizingEditorReferences = wasSynchronizing;
        }

        if (!wasSynchronizing)
        {
            SelectedTicketStatus = ResolveTicketStatusOption(Editor.SelectedTicket);
            TryAutoMatchSageCustomerForTicket(Editor.SelectedTicket);
            ChangeTicketStatusCommand.RaiseCanExecuteChanged();
        }
    }

    private void RefreshTicketList()
    {
        _editorTicketOptionsClientId = null;
        var selectedTicketId = SelectedTicket?.Id;
        Tickets.Clear();
        foreach (var ticket in _repository.GetTickets(TicketClientFilter?.Id))
        {
            Tickets.Add(ticket);
        }

        SelectedTicket = selectedTicketId.HasValue
            ? Tickets.FirstOrDefault(ticket => ticket.Id == selectedTicketId.Value)
            : SelectedTicket;
    }

    private void RefreshTicketStatusOptions()
    {
        var selectedStatusName = SelectedTicketStatus?.Name;
        TicketStatusOptions.Clear();
        foreach (var option in _repository.GetTicketStatusOptions())
        {
            TicketStatusOptions.Add(option);
        }

        var activeTicket = Editor.SelectedTicket is { Id: > 0 } ? Editor.SelectedTicket : SelectedTicket;
        SelectedTicketStatus = !string.IsNullOrWhiteSpace(selectedStatusName)
            ? TicketStatusOptions.FirstOrDefault(option => option.Name.Equals(selectedStatusName, StringComparison.OrdinalIgnoreCase))
            : ResolveTicketStatusOption(activeTicket);
    }

    private void RefreshTodayEntries()
    {
        var selectedId = SelectedEntry?.Id;
        var editorWasDirty = Editor.IsDirty;
        _isRefreshingTodayEntries = true;
        try
        {
            Entries.Clear();
            foreach (var entry in _repository.GetWorkEntries(new WorkEntryQuery
            {
                StartDate = SelectedDate,
                EndDate = SelectedDate
            }).OrderBy(static entry => entry.StartTime))
            {
                Entries.Add(entry);
            }

            _selectedEntry = selectedId.HasValue
                ? Entries.FirstOrDefault(entry => entry.Id == selectedId.Value)
                : null;
            OnPropertyChanged(nameof(SelectedEntry));
        }
        finally
        {
            _isRefreshingTodayEntries = false;
        }

        if (_selectedEntry is not null)
        {
            if (editorWasDirty && Editor.Id == _selectedEntry.Id)
            {
                UpdateEditorPostingState(_selectedEntry);
            }
            else
            {
                LoadEntryIntoEditor(_selectedEntry);
            }
        }

        OnPropertyChanged(nameof(HasTodayEntries));
        UpdateTotals();
    }

    private void RefreshWeek()
    {
        var start = GetWeekStart(SelectedDate);
        var end = start.AddDays(6);
        var entries = _repository.GetWorkEntries(new WorkEntryQuery
        {
            StartDate = start,
            EndDate = end
        });

        WeekGroups.Clear();
        foreach (var day in Enumerable.Range(0, 7).Select(offset => start.AddDays(offset)))
        {
            var dayEntries = entries
                .Where(entry => entry.WorkDate.Date == day.Date)
                .OrderBy(entry => entry.StartTime)
                .ToList();

            WeekGroups.Add(new DayWorkGroup
            {
                Date = day,
                Entries = new ObservableCollection<WorkEntry>(dayEntries)
            });
        }

        _weekBillableMinutes = entries.Where(static entry => entry.Billable).Sum(static entry => entry.DurationMinutes);
        _weekNonBillableMinutes = entries.Where(static entry => !entry.Billable).Sum(static entry => entry.DurationMinutes);
        OnPropertyChanged(nameof(WeekTotalLabel));
        OnPropertyChanged(nameof(WeekBillableLabel));
        OnPropertyChanged(nameof(WeekNonBillableLabel));
    }

    private void RefreshHistory()
    {
        var start = HistoryStartDate?.Date ?? DateTime.Today;
        var end = HistoryEndDate?.Date ?? start;
        if (end < start)
        {
            HistoryGroups.Clear();
            HistoryTimelineEntries.Clear();
            _historyBillableMinutes = 0;
            _historyNonBillableMinutes = 0;
            _historyEntryCount = 0;
            _historyWhdPendingCount = 0;
            _historySagePendingCount = 0;
            RaiseHistoryTotalsChanged();
            StatusMessage = "History start date must be before end date.";
            return;
        }

        var entries = _repository.GetWorkEntries(new WorkEntryQuery
        {
            StartDate = start,
            EndDate = end
        }).ToList();

        var groups = BuildHistoryGroups(entries);
        HistoryGroups.Clear();
        foreach (var group in groups)
        {
            HistoryGroups.Add(group);
        }

        HistoryTimelineEntries.Clear();
        foreach (var entry in entries
                     .OrderByDescending(static entry => entry.WorkDate)
                     .ThenBy(static entry => entry.StartTime))
        {
            HistoryTimelineEntries.Add(entry);
        }

        _historyBillableMinutes = entries.Where(static entry => entry.Billable).Sum(static entry => entry.DurationMinutes);
        _historyNonBillableMinutes = entries.Where(static entry => !entry.Billable).Sum(static entry => entry.DurationMinutes);
        _historyEntryCount = entries.Count;
        _historyWhdPendingCount = entries.Count(static entry => entry.NeedsWhdPosting);
        _historySagePendingCount = entries.Count(static entry => entry.NeedsSagePosting);
        RaiseHistoryTotalsChanged();
    }

    private IReadOnlyList<HistoryWorkGroup> BuildHistoryGroups(IReadOnlyList<WorkEntry> entries)
    {
        return entries
            .GroupBy(GetHistoryGroupStartDate)
            .OrderByDescending(static group => group.Key)
            .Select(group =>
            {
                var groupStart = group.Key;
                var groupEnd = GetHistoryGroupEndDate(groupStart);
                var orderedEntries = group
                    .OrderByDescending(static entry => entry.WorkDate)
                    .ThenBy(static entry => entry.StartTime)
                    .ToList();

                var dayGroups = HistoryGroupBy == "Week"
                    ? orderedEntries
                        .GroupBy(static entry => entry.WorkDate.Date)
                        .OrderBy(static group => group.Key)
                        .Select(group => new DayWorkGroup
                        {
                            Date = group.Key,
                            Entries = new ObservableCollection<WorkEntry>(
                                group.OrderBy(static entry => entry.StartTime))
                        })
                        .ToList()
                    : [];

                return new HistoryWorkGroup
                {
                    Header = BuildHistoryGroupHeader(groupStart, groupEnd),
                    DateRangeLabel = groupStart.Date == groupEnd.Date
                        ? $"{groupStart:M/d/yyyy}"
                        : $"{groupStart:M/d/yyyy} - {groupEnd:M/d/yyyy}",
                    StartDate = groupStart,
                    EndDate = groupEnd,
                    Entries = new ObservableCollection<WorkEntry>(orderedEntries),
                    DayGroups = new ObservableCollection<DayWorkGroup>(dayGroups)
                };
            })
            .ToList();
    }

    private DateTime GetHistoryGroupStartDate(WorkEntry entry)
    {
        return HistoryGroupBy switch
        {
            "Day" => entry.WorkDate.Date,
            "Month" => new DateTime(entry.WorkDate.Year, entry.WorkDate.Month, 1),
            "Year" => new DateTime(entry.WorkDate.Year, 1, 1),
            _ => GetWeekStart(entry.WorkDate)
        };
    }

    private DateTime GetHistoryGroupEndDate(DateTime groupStart)
    {
        return HistoryGroupBy switch
        {
            "Day" => groupStart,
            "Month" => groupStart.AddMonths(1).AddDays(-1),
            "Year" => groupStart.AddYears(1).AddDays(-1),
            _ => groupStart.AddDays(6)
        };
    }

    private string BuildHistoryGroupHeader(DateTime groupStart, DateTime groupEnd)
    {
        return HistoryGroupBy switch
        {
            "Day" => $"{groupStart:dddd, MMM d, yyyy}",
            "Month" => $"{groupStart:MMMM yyyy}",
            "Year" => $"{groupStart:yyyy}",
            _ => $"Week of {groupStart:MMM d, yyyy}"
        };
    }

    private void RefreshPostingQueue()
    {
        var selectedIds = PostingQueue
            .Where(static entry => entry.IsSelectedForBatch)
            .Select(static entry => entry.Id)
            .ToHashSet();

        PostingQueue.Clear();
        var queue = _repository.GetWorkEntries(new WorkEntryQuery { PendingAnyOnly = true })
            .OrderByDescending(static entry => entry.WorkDate)
            .ThenBy(static entry => entry.StartTime);

        foreach (var entry in queue)
        {
            entry.IsSelectedForBatch = selectedIds.Contains(entry.Id);
            PostingQueue.Add(entry);
        }

        OnPropertyChanged(nameof(HasPostingQueueEntries));
    }

    private void RefreshPostingLogs()
    {
        var selectedId = SelectedPostingLog?.Id;
        var success = PostingLogResultFilter switch
        {
            "Success" => true,
            "Failed" => false,
            _ => (bool?)null
        };

        PostingLogs.Clear();
        foreach (var log in _repository.GetPostingLogs(
                     PostingLogDestinationFilter,
                     success,
                     PostingLogKeyword,
                     PostingLogStartDate,
                     PostingLogEndDate))
        {
            PostingLogs.Add(log);
        }

        SelectedPostingLog = selectedId.HasValue
            ? PostingLogs.FirstOrDefault(log => log.Id == selectedId.Value)
            : PostingLogs.FirstOrDefault();
    }

    private void ClearPostingLogFilters()
    {
        PostingLogKeyword = string.Empty;
        PostingLogDestinationFilter = "Any";
        PostingLogResultFilter = "Any";
        PostingLogStartDate = DateTime.Today.AddDays(-30);
        PostingLogEndDate = DateTime.Today;
        RefreshPostingLogs();
    }

    private void OpenPostingLogEntry(object? parameter)
    {
        var log = parameter as PostingLog ?? SelectedPostingLog;
        if (log is null)
        {
            return;
        }

        var entry = _repository.GetWorkEntry(log.WorkEntryId);
        if (entry is null)
        {
            StatusMessage = "That work entry no longer exists.";
            return;
        }

        EditEntry(entry);
        StatusMessage = $"Opened {entry.ClientDisplay} from posting history.";
    }

    private void UpdateTotals()
    {
        _totalBillableMinutes = Entries.Where(static entry => entry.Billable).Sum(static entry => entry.DurationMinutes);
        _totalNonBillableMinutes = Entries.Where(static entry => !entry.Billable).Sum(static entry => entry.DurationMinutes);
        WhdPendingCount = Entries.Count(static entry => entry.NeedsWhdPosting);
        SagePendingCount = Entries.Count(static entry => entry.NeedsSagePosting);
        OnPropertyChanged(nameof(TotalBillableLabel));
        OnPropertyChanged(nameof(TotalNonBillableLabel));
        RefreshDailyCloseout();
    }

    private void RefreshDailyCloseout()
    {
        var missingDuration = Entries.Count(static entry => entry.DurationMinutes <= 0);
        var missingNote = Entries.Count(static entry => string.IsNullOrWhiteSpace(entry.Note));
        var whdPending = Entries.Count(static entry => entry.NeedsWhdPosting);
        var sagePending = Entries.Count(static entry => entry.NeedsSagePosting);
        var errors = Entries.Count(static entry => !string.IsNullOrWhiteSpace(entry.LastError));
        var modifiedAfterPosting = Entries.Count(static entry => entry.ModifiedAfterPosting);
        var openFollowUps = Entries.Count(static entry => entry.HasFollowUp);
        var duplicates = Entries
            .GroupBy(static entry => new
            {
                Client = entry.ClientDisplay.Trim().ToUpperInvariant(),
                Ticket = entry.TicketDisplay.Trim().ToUpperInvariant(),
                Note = (entry.Note ?? string.Empty).ReplaceLineEndings(" ").Trim().ToUpperInvariant(),
                entry.DurationMinutes
            })
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key.Note) && group.Count() > 1)
            .Sum(static group => group.Count());

        var items = new[]
        {
            BuildCloseoutItem("missing-duration", "Missing duration", missingDuration, "Entries need a duration before posting."),
            BuildCloseoutItem("missing-note", "Missing note", missingNote, "Entries should have a work note."),
            BuildCloseoutItem("open-follow-ups", "Open follow-ups", openFollowUps, "Notes still have a follow-up or waiting action."),
            BuildCloseoutItem("whd-pending", "WHD pending", whdPending, "Ticket notes still need WHD posting."),
            BuildCloseoutItem("sage-pending", "Sage pending", sagePending, "Entries still need Sage ticket posting."),
            BuildCloseoutItem("errors", "Errors", errors, "Entries have posting or sync errors."),
            BuildCloseoutItem("edited-after-posting", "Edited after posting", modifiedAfterPosting, "Entries changed after posting."),
            BuildCloseoutItem("duplicates", "Possible duplicates", duplicates, "Entries look similar enough to review.")
        };

        DailyCloseoutItems.Clear();
        foreach (var item in items.Where(static item => item.HasIssue))
        {
            DailyCloseoutItems.Add(item);
        }
        HasCloseoutIssues = DailyCloseoutItems.Count > 0;
    }

    private IReadOnlyList<WorkEntry> GetSelectedPostingQueueEntries()
    {
        return PostingQueue
            .Where(static entry => entry.IsSelectedForBatch)
            .ToList();
    }

    private IReadOnlyList<WorkEntry> GetSelectedPostingQueueEntriesForPosting()
    {
        var selectedIds = PostingQueue
            .Where(static entry => entry.IsSelectedForBatch)
            .Select(static entry => entry.Id)
            .ToHashSet();

        if (Editor.IsDirty && Editor.Id > 0 && selectedIds.Contains(Editor.Id) && SaveEditor() is null)
        {
            return Array.Empty<WorkEntry>();
        }

        return PostingQueue
            .Where(entry => selectedIds.Contains(entry.Id))
            .ToList();
    }

    private void SetPostingQueueSelection(bool isSelected)
    {
        var entries = PostingQueue.ToList();
        PostingQueue.Clear();
        foreach (var entry in entries)
        {
            entry.IsSelectedForBatch = isSelected;
            PostingQueue.Add(entry);
        }
    }

    private void SelectFailedPostingQueueEntries()
    {
        var entries = PostingQueue.ToList();
        PostingQueue.Clear();
        foreach (var entry in entries)
        {
            entry.IsSelectedForBatch = !string.IsNullOrWhiteSpace(entry.LastError);
            PostingQueue.Add(entry);
        }
    }

    private void SelectCloseoutIssue(object? parameter)
    {
        if (parameter is not CloseoutItem { HasIssue: true } item)
        {
            return;
        }

        var entry = item.Key switch
        {
            "missing-duration" => Entries.FirstOrDefault(static candidate => candidate.DurationMinutes <= 0),
            "missing-note" => Entries.FirstOrDefault(static candidate => string.IsNullOrWhiteSpace(candidate.Note)),
            "open-follow-ups" => Entries.FirstOrDefault(static candidate => candidate.HasFollowUp),
            "whd-pending" => Entries.FirstOrDefault(static candidate => candidate.NeedsWhdPosting),
            "sage-pending" => Entries.FirstOrDefault(static candidate => candidate.NeedsSagePosting),
            "errors" => Entries.FirstOrDefault(static candidate => !string.IsNullOrWhiteSpace(candidate.LastError)),
            "edited-after-posting" => Entries.FirstOrDefault(static candidate => candidate.ModifiedAfterPosting),
            "duplicates" => FindFirstPossibleDuplicateEntry(),
            _ => null
        };

        if (entry is null)
        {
            StatusMessage = $"No matching entries found for {item.Label}.";
            return;
        }

        SelectedEntry = entry;
        StatusMessage = $"Selected {entry.ClientDisplay}: {item.Label}.";
    }

    private WorkEntry? FindFirstPossibleDuplicateEntry()
    {
        return Entries
            .GroupBy(static entry => new
            {
                Client = entry.ClientDisplay.Trim().ToUpperInvariant(),
                Ticket = entry.TicketDisplay.Trim().ToUpperInvariant(),
                Note = (entry.Note ?? string.Empty).ReplaceLineEndings(" ").Trim().ToUpperInvariant(),
                entry.DurationMinutes
            })
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key.Note) && group.Count() > 1)
            .SelectMany(static group => group)
            .FirstOrDefault();
    }

    private void LoadEntryIntoEditor(WorkEntry entry)
    {
        var client = ResolveEditorClient(entry.ClientId);
        IReadOnlyList<Client> clients = client is null ? [] : [client];
        var ticket = ResolveEditorTicket(entry.TicketId);
        IReadOnlyList<Ticket> tickets = ticket is null ? [] : [ticket];
        _isSynchronizingEditorReferences = true;
        try
        {
            Editor.LoadFrom(entry, clients, tickets);
        }
        finally
        {
            _isSynchronizingEditorReferences = false;
        }
        _isPostedEditorUnlocked = false;
        RaiseEditorStateProperties();
        SyncEditorClientFilterText(Editor.SelectedClient?.DisplayName ?? string.Empty);
        RefreshEditorTickets(entry.TicketId);
        Editor.MarkClean();
        EditorSaveStatus = $"Saved {entry.UpdatedAt:h:mm tt}";
        RefreshRecentClientEntries();
    }

    private void NewEntry()
    {
        if (Editor.IsDirty)
        {
            var discard = _dialogService.Confirm(
                "Unsaved changes",
                "Discard the unsaved changes and start a new entry?",
                "Discard",
                "Keep editing");
            if (!discard)
            {
                return;
            }

            ClearPersistedEditorDraft();
        }

        _selectedEntry = null;
        OnPropertyChanged(nameof(SelectedEntry));
        SelectedDate = DateTime.Today;
        _isPostedEditorUnlocked = false;
        _isSynchronizingEditorReferences = true;
        try
        {
            Editor.LoadNew(DateTime.Today);
        }
        finally
        {
            _isSynchronizingEditorReferences = false;
        }
        SyncEditorClientFilterText(string.Empty);
        RefreshEditorClientOptions();
        RefreshEditorTickets();
        Editor.MarkClean();
        EditorSaveStatus = "Saved";
        StatusMessage = "New entry ready";
        RaiseEditorStateProperties();
        RaiseEntryCommandStates();
    }

    private void EditEntry(object? parameter)
    {
        if (parameter is not WorkEntry { Id: > 0 } entry)
        {
            return;
        }

        var savedEntry = _repository.GetWorkEntry(entry.Id) ?? entry;
        CurrentSection = "Today";
        SelectedDate = DateTime.Today;
        SelectedEntry = Entries.FirstOrDefault(candidate => candidate.Id == savedEntry.Id) ?? savedEntry;
        StatusMessage = $"Editing {savedEntry.ClientDisplay} from worklog history.";
    }

    private void SaveEntry()
    {
        _ = SaveEditor();
    }

    private WorkEntry? SaveEditor()
    {
        if (IsEditorLocked)
        {
            StatusMessage = "Unlock this posted entry before changing it.";
            return null;
        }

        if (!Editor.TryBuildEntry(out var entry, out var validationMessage))
        {
            Editor.RunWithoutDirtyTracking(() => Editor.LastError = validationMessage);
            StatusMessage = validationMessage;
            return null;
        }

        entry.LastError = null;
        TechBenchRepository.UpdatePostingStatus(entry);
        var id = _repository.SaveWorkEntry(entry);
        Editor.RunWithoutDirtyTracking(() => Editor.Id = id);
        Editor.MarkClean();
        ClearPersistedEditorDraft();
        _selectedDate = DateTime.Today;
        OnPropertyChanged(nameof(SelectedDate));
        RefreshAll();
        var savedEntry = Entries.FirstOrDefault(saved => saved.Id == id) ?? _repository.GetWorkEntry(id);
        if (savedEntry is not null)
        {
            _selectedEntry = savedEntry;
            OnPropertyChanged(nameof(SelectedEntry));
            LoadEntryIntoEditor(savedEntry);
        }

        StatusMessage = savedEntry is null
            ? "Saved work entry."
            : $"Saved work entry for {savedEntry.ClientDisplay}.";
        return savedEntry;
    }

    private void DeleteEntry()
    {
        if (Editor.Id <= 0)
        {
            return;
        }

        var entry = _repository.GetWorkEntry(Editor.Id);
        if (entry is null || entry.WhdPosted || entry.SagePosted)
        {
            StatusMessage = "Posted entries cannot be deleted.";
            return;
        }

        var confirmed = _dialogService.Confirm(
            "Delete entry",
            "Delete this work entry? You can undo this until another entry is deleted.",
            "Delete",
            "Cancel");
        if (!confirmed)
        {
            return;
        }

        _lastDeletedEntry = entry;
        _repository.DeleteWorkEntry(Editor.Id);
        RefreshAll();
        NewEntry();
        OnPropertyChanged(nameof(HasUndoDelete));
        OnPropertyChanged(nameof(UndoDeleteLabel));
        UndoDeleteCommand.RaiseCanExecuteChanged();
        StatusMessage = "Entry deleted. Undo is available.";
    }

    private void UndoDelete()
    {
        if (_lastDeletedEntry is null)
        {
            return;
        }

        var restored = _lastDeletedEntry;
        restored.Id = 0;
        restored.LastError = null;
        TechBenchRepository.UpdatePostingStatus(restored);
        var id = _repository.SaveWorkEntry(restored);
        _lastDeletedEntry = null;
        OnPropertyChanged(nameof(HasUndoDelete));
        OnPropertyChanged(nameof(UndoDeleteLabel));
        UndoDeleteCommand.RaiseCanExecuteChanged();
        _selectedDate = DateTime.Today;
        OnPropertyChanged(nameof(SelectedDate));
        RefreshAll();
        SelectedEntry = Entries.FirstOrDefault(entry => entry.Id == id) ?? _repository.GetWorkEntry(id);
        StatusMessage = $"Restored entry for {restored.ClientDisplay}.";
    }

    private void UnlockPostedEntry()
    {
        if (!IsEditorLocked)
        {
            return;
        }

        var confirmed = _dialogService.Confirm(
            "Edit posted entry",
            "Changes in TechBench will not update records already posted to WHD or Sage. Unlock this entry anyway?");
        if (!confirmed)
        {
            return;
        }

        _isPostedEditorUnlocked = true;
        RaiseEditorStateProperties();
        StatusMessage = "Posted entry unlocked for local editing.";
    }

    private void DuplicateEntry()
    {
        var source = Editor.Id > 0 ? _repository.GetWorkEntry(Editor.Id) : null;
        if (source is null)
        {
            return;
        }

        var copy = new WorkEntry
        {
            WorkDate = SelectedDate,
            ClientId = source.ClientId,
            ManualClientName = source.ManualClientName,
            TicketId = source.TicketId,
            TicketNumberText = source.TicketNumberText,
            HasTimeRange = source.HasTimeRange,
            StartTime = source.StartTime,
            EndTime = source.EndTime,
            DurationMinutes = source.DurationMinutes,
            Billable = source.Billable,
            Note = source.Note,
            InternalNote = source.InternalNote,
            Tags = source.Tags,
            FollowUpState = source.FollowUpState,
            FollowUpDueDate = source.FollowUpDueDate,
            PostingStatus = PostingStatus.Draft
        };

        TechBenchRepository.UpdatePostingStatus(copy);
        var id = _repository.SaveWorkEntry(copy);
        RefreshAll();
        SelectedEntry = Entries.FirstOrDefault(entry => entry.Id == id);
        StatusMessage = $"Duplicated entry for {source.ClientDisplay}.";
    }

    private void ExportDailyCsv()
    {
        ExportEntries(
            Entries,
            $"TechBench-{SelectedDate:yyyy-MM-dd}.csv",
            $"Exported {Entries.Count} entries for {SelectedDate:MMM d}");
    }

    private void ExportWeeklyCsv()
    {
        var start = GetWeekStart(SelectedDate);
        var end = start.AddDays(6);
        var entries = _repository.GetWorkEntries(new WorkEntryQuery { StartDate = start, EndDate = end });
        ExportEntries(
            entries,
            $"TechBench-week-{start:yyyy-MM-dd}.csv",
            $"Exported weekly worklog for {start:MMM d} - {end:MMM d}");
    }

    private void ExportHistoryCsv()
    {
        var start = HistoryStartDate?.Date ?? DateTime.Today;
        var end = HistoryEndDate?.Date ?? start;
        if (end < start)
        {
            _dialogService.Error("Export history", "History start date must be before end date.");
            return;
        }

        var entries = _repository.GetWorkEntries(new WorkEntryQuery { StartDate = start, EndDate = end });
        ExportEntries(
            entries,
            $"TechBench-history-{start:yyyy-MM-dd}-to-{end:yyyy-MM-dd}.csv",
            $"Exported history for {start:MMM d, yyyy} - {end:MMM d, yyyy}");
    }

    private void ExportEntries(IEnumerable<WorkEntry> entries, string fileName, string successMessage)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = fileName,
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".csv"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, CsvExportService.BuildWorkEntryCsv(entries));
        StatusMessage = successMessage;
    }

    private async Task PostWhdAsync(object? parameter)
    {
        var ownsOperationState = !IsEntryOperationRunning;
        if (ownsOperationState)
        {
            IsEntryOperationRunning = true;
        }

        EntryOperationText = "Posting the work note to WHD...";
        try
        {
            var entry = ResolveEntryForPosting(parameter);
            if (entry is null)
            {
                StatusMessage = "Save the work entry before posting it to Web Help Desk.";
                return;
            }

            if (!entry.HasTicket)
            {
                StatusMessage = "Select a Web Help Desk ticket before posting the work note.";
                return;
            }

            await PostEntryAsync(entry, _whdPoster, "WHD");
        }
        finally
        {
            if (ownsOperationState)
            {
                EntryOperationText = string.Empty;
                IsEntryOperationRunning = false;
            }
        }
    }

    private async Task PostSageAsync(object? parameter)
    {
        var ownsOperationState = !IsEntryOperationRunning;
        if (ownsOperationState)
        {
            IsEntryOperationRunning = true;
        }

        EntryOperationText = "Creating and verifying the Sage ticket...";
        var entry = ResolveEntryForPosting(parameter);
        if (entry is null)
        {
            StatusMessage = "Save the work entry before creating a Sage ticket.";
            if (ownsOperationState)
            {
                EntryOperationText = string.Empty;
                IsEntryOperationRunning = false;
            }
            return;
        }

        if (!entry.Billable)
        {
            StatusMessage = "Native Sage posting currently supports billable entries only.";
            if (ownsOperationState)
            {
                EntryOperationText = string.Empty;
                IsEntryOperationRunning = false;
            }
            return;
        }

        _sageVerificationTimer.Stop();
        try
        {
            if (_isSageVerificationRunning)
            {
                StatusMessage = "Waiting for the current read-only Sage ODBC check to finish...";
                while (_isSageVerificationRunning)
                {
                    await Task.Delay(100);
                }
            }

            if (HasSageDraft(entry))
            {
                StatusMessage = "Checking Sage ODBC for a ticket from the previous attempt...";
                var verification = await VerifySageEntryAsync(entry, showFeedback: false);
                if (verification.IsSaved)
                {
                    StatusMessage = verification.Message;
                    return;
                }

                if (verification.Found)
                {
                    StatusMessage = verification.Message;
                    _dialogService.Info(
                        "Create Sage ticket",
                        $"TechBench found prior Sage data for this entry but could not verify it safely. No new ticket was created.\n\n{verification.Message}");
                    return;
                }

                var createAnother = _dialogService.Confirm(
                    "Possible duplicate Sage ticket",
                    "A previous Sage creation attempt exists, but ODBC cannot confirm whether it saved. Creating another ticket could produce a duplicate. Create another Sage ticket anyway?");
                if (!createAnother)
                {
                    StatusMessage = "Canceled Sage ticket creation to avoid a possible duplicate.";
                    return;
                }
            }

            _isSagePostingRunning = true;
            await PostEntryAsync(entry, _sagePoster, "Sage");
        }
        finally
        {
            _isSagePostingRunning = false;
            ConfigureSageVerificationTimer();
            if (ownsOperationState)
            {
                EntryOperationText = string.Empty;
                IsEntryOperationRunning = false;
            }
        }
    }

    private async Task BatchPostWhdAsync(object? parameter)
    {
        var entries = GetSelectedPostingQueueEntriesForPosting()
            .Where(static entry => entry.NeedsWhdPosting)
            .ToList();

        if (entries.Count == 0)
        {
            _dialogService.Error("Batch post WHD", "Select one or more queue entries that are still WHD pending.");
            return;
        }

        var confirmed = _dialogService.Confirm(
            "Batch post WHD",
            $"Post {entries.Count} selected entr{(entries.Count == 1 ? "y" : "ies")} to Web Help Desk?");
        if (!confirmed)
        {
            return;
        }

        await PostEntriesBatchAsync(entries, _whdPoster, "WHD");
    }

    private async Task PostEntriesBatchAsync(IReadOnlyList<WorkEntry> entries, IWorkEntryPoster poster, string destination)
    {
        var successCount = 0;
        var failureCount = 0;

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = _repository.GetWorkEntry(entries[index].Id) ?? entries[index];
            StatusMessage = $"{destination} batch {index + 1}/{entries.Count}: {entry.ClientDisplay} ({entry.TicketDisplay})";
            var success = await PostEntryAsync(entry, poster, destination, refreshAfter: false, confirmAlreadyPosted: false);
            if (success)
            {
                successCount++;
            }
            else
            {
                failureCount++;
            }
        }

        RefreshAll();
        StatusMessage = $"{destination} batch complete: {successCount} succeeded, {failureCount} failed.";
    }

    private async Task<bool> PostEntryAsync(
        WorkEntry entry,
        IWorkEntryPoster poster,
        string destination,
        bool refreshAfter = true,
        bool confirmAlreadyPosted = true)
    {
        await using var postingLease = await _postingCoordinator.TryAcquireAsync(entry.Id, destination);
        if (postingLease is null)
        {
            StatusMessage = $"A {destination} post for {entry.ClientDisplay} ({entry.TicketDisplay}) is already running.";
            return false;
        }

        entry = _repository.GetWorkEntry(entry.Id) ?? entry;
        var client = entry.ClientId.HasValue ? _repository.GetClient(entry.ClientId.Value) : null;
        if (client is null)
        {
            if (destination == "WHD")
            {
                _dialogService.Error($"Post to {destination}", "Select a synced Web Help Desk ticket before posting.");
                return false;
            }

            client = new Client
            {
                Id = 0,
                Name = string.IsNullOrWhiteSpace(entry.ClientName)
                    ? entry.ManualClientName ?? "Manual client"
                    : entry.ClientName,
                Source = "Manual",
                IsActive = true
            };
        }

        var alreadyPosted = destination == "WHD" ? entry.WhdPosted : entry.SagePosted;
        if (alreadyPosted && confirmAlreadyPosted)
        {
            var confirmed = _dialogService.Confirm(
                $"Already posted to {destination}",
                $"This entry is already marked posted to {destination}. Record another manual posting attempt?");
            if (!confirmed)
            {
                return false;
            }
        }

        var ticket = entry.TicketId.HasValue ? _repository.GetTicket(entry.TicketId.Value) : null;
        var outstandingAttempt = _repository.GetOutstandingPostingAttempt(entry.Id, destination);
        if (outstandingAttempt is not null)
        {
            var retry = _dialogService.Confirm(
                $"Unconfirmed {destination} attempt",
                $"A previous {destination} attempt for {entry.ClientDisplay} ({entry.TicketDisplay}) ended without a confirmed result. "
                + "Retrying may create a duplicate. Verify the destination first, or continue only if you intend to retry. Continue?");
            if (!retry)
            {
                StatusMessage = $"Canceled {destination} retry to avoid a possible duplicate.";
                return false;
            }

            _repository.AbandonOutstandingPostingAttempts(
                entry.Id,
                destination,
                "User explicitly authorized a retry after an unconfirmed prior outcome.");
        }

        var attemptStart = _repository.TryBeginPostingAttempt(
            entry.Id,
            destination,
            Guid.NewGuid().ToString("N"),
            CreatePostingPayloadHash(entry, client, ticket, destination));
        if (!attemptStart.Started || attemptStart.Attempt is null)
        {
            StatusMessage = $"A {destination} attempt for {entry.ClientDisplay} ({entry.TicketDisplay}) is already active or awaiting reconciliation.";
            return false;
        }

        PostingResult result;
        try
        {
            result = await poster.PostAsync(entry, client, ticket, BuildPostingSettings());
        }
        catch (OperationCanceledException)
        {
            result = PostingResult.Uncertain(
                $"The {destination} operation was canceled after it began. Its external outcome is unknown; verify before retrying.");
        }
        catch (Exception ex)
        {
            result = PostingResult.Uncertain(
                $"The {destination} operation ended unexpectedly after it began: {ex.Message} Verify the destination before retrying.");
        }

        var attemptStatus = result.OutcomeUncertain
            ? PostingAttemptStatus.Unknown
            : result.Success ? PostingAttemptStatus.Succeeded : PostingAttemptStatus.Failed;

        _repository.AddPostingLog(new PostingLog
        {
            WorkEntryId = entry.Id,
            Destination = destination,
            Payload = result.Payload,
            Success = result.Success,
            Message = result.Message,
            ExternalReference = result.ExternalReference,
            CreatedAt = DateTime.Now
        });

        if (destination == "Sage" && !string.IsNullOrWhiteSpace(result.ExternalReference))
        {
            entry.SageTicketNumber = NormalizeSageTicketNumber(result.ExternalReference);
        }

        if (result.Success)
        {
            if (result.MarkPosted && destination == "WHD")
            {
                entry.WhdPosted = true;
                entry.WhdPostedAt = DateTime.Now;
            }
            else if (result.MarkPosted)
            {
                entry.SagePosted = true;
                entry.SagePostedAt = DateTime.Now;
            }

            entry.LastError = null;
            StatusMessage = result.Message;
        }
        else
        {
            entry.LastError = result.Message;
            StatusMessage = result.Message;
        }

        TechBenchRepository.UpdatePostingStatus(entry);
        _repository.SaveWorkEntry(entry);
        _repository.CompletePostingAttempt(
            attemptStart.Attempt.Id,
            attemptStatus,
            result.Message,
            result.ExternalReference);
        ConfigureSageVerificationTimer();
        if (refreshAfter)
        {
            RefreshAll();
            SelectedEntry = Entries.FirstOrDefault(saved => saved.Id == entry.Id) ?? _repository.GetWorkEntry(entry.Id);
        }

        return result.Success;
    }

    private void MarkPosted(object? parameter, string destination)
    {
        var entry = ResolveEntry(parameter);
        if (entry is null)
        {
            _dialogService.Error($"Mark {destination} posted", "Save the work entry before marking it posted.");
            return;
        }

        if (destination == "WHD")
        {
            entry.WhdPosted = true;
            entry.WhdPostedAt = DateTime.Now;
        }
        _repository.ResolveOutstandingPostingAttempts(
            entry.Id,
            destination,
            $"Marked {destination} posted manually after external verification.");
        entry.LastError = null;
        TechBenchRepository.UpdatePostingStatus(entry);
        _repository.SaveWorkEntry(entry);
        _repository.AddPostingLog(new PostingLog
        {
            WorkEntryId = entry.Id,
            Destination = destination,
            Payload = "Manual posted marker",
            Success = true,
            Message = $"Marked {destination} posted manually.",
            CreatedAt = DateTime.Now
        });

        RefreshAll();
        SelectedEntry = Entries.FirstOrDefault(saved => saved.Id == entry.Id) ?? _repository.GetWorkEntry(entry.Id);
        StatusMessage = $"Marked {destination} posted";
    }

    private bool CanPostWhdEntry(object? parameter)
    {
        if (IsEntryOperationRunning)
        {
            return false;
        }

        return parameter is WorkEntry entry
            ? entry is { Id: > 0, HasTicket: true, WhdPosted: false }
            : (Editor.Id > 0 || (Editor.HasClientReference && IsEditorEditable))
              && !Editor.HasNoTicket
              && !Editor.WhdPosted;
    }

    private bool CanPostSageEntry(object? parameter)
    {
        if (IsEntryOperationRunning)
        {
            return false;
        }

        return parameter is WorkEntry entry
            ? entry is { Id: > 0, Billable: true, SagePosted: false }
            : (Editor.Id > 0 || (Editor.HasClientReference && IsEditorEditable))
              && Editor.Billable
              && !Editor.SagePosted;
    }

    private bool CanSaveEditor() => Editor.HasClientReference && IsEditorEditable;

    private bool CanResolveSavedEntry(object? parameter) => parameter is WorkEntry { Id: > 0 } || Editor.Id > 0;

    private bool CanDeleteEditorEntry() => Editor.Id > 0
        && !Editor.WhdPosted
        && !Editor.SagePosted
        && !IsEntryOperationRunning;

    private bool CanVerifySageSave(object? parameter)
    {
        var entry = parameter as WorkEntry;
        if (entry is null && Editor.Id > 0)
        {
            entry = _repository.GetWorkEntry(Editor.Id);
        }

        return entry is { Id: > 0, NeedsSagePosting: true }
            && HasSageDraft(entry);
    }

    private async Task VerifySageSaveAsync(object? parameter)
    {
        if (_isSagePostingRunning)
        {
            _dialogService.Info("Check Sage save", "Sage ticket creation is still running. Check the save after it finishes.");
            return;
        }

        var entry = ResolveEntry(parameter);
        if (entry is null || !HasSageDraft(entry))
        {
            _dialogService.Error(
                "Check Sage save",
                "Create the Sage ticket from TechBench first so its exact draft number can be verified.");
            return;
        }

        var result = await VerifySageEntryAsync(entry, showFeedback: true);
        StatusMessage = result.Message;
        if (!result.IsSaved)
        {
            if (result.Message.StartsWith("Sage save verification could not run", StringComparison.Ordinal))
            {
                _dialogService.Error("Check Sage save", result.Message);
            }
            else
            {
                _dialogService.Info("Check Sage save", result.Message);
            }
        }
    }

    private async void HandleSageVerificationTimerTick(object? sender, EventArgs e)
    {
        if (_isSageVerificationRunning || _isSagePostingRunning)
        {
            return;
        }

        var pending = _repository.GetWorkEntries(new WorkEntryQuery { PendingSageOnly = true })
            .Where(HasSageDraft)
            .ToArray();
        if (pending.Length == 0)
        {
            _sageVerificationTimer.Stop();
            return;
        }

        if (_sageVerificationCursor >= pending.Length)
        {
            _sageVerificationCursor = 0;
        }

        var entry = pending[_sageVerificationCursor];
        _sageVerificationCursor = (_sageVerificationCursor + 1) % pending.Length;
        var result = await VerifySageEntryAsync(entry, showFeedback: false);
        if (result.IsSaved)
        {
            StatusMessage = result.Message;
        }
    }

    private async Task<SageTimeTicketVerificationResult> VerifySageEntryAsync(
        WorkEntry entry,
        bool showFeedback)
    {
        if (_isSageVerificationRunning || _isSagePostingRunning)
        {
            return new SageTimeTicketVerificationResult(false, false, "A Sage save check is already running.");
        }

        _isSageVerificationRunning = true;
        try
        {
            var settings = BuildPostingSettings();
            var dsn = settings.GetValueOrDefault("Sage.Dsn", string.Empty);
            var username = settings.GetValueOrDefault("Sage.Username", string.Empty);
            var password = settings.GetValueOrDefault("Sage.Password", string.Empty);
            var request = new SageTimeTicketVerificationRequest(
                entry.SageTicketNumber,
                entry.WorkDate,
                entry.DurationMinutes,
                entry.Note);
            var result = await _sageOdbcClient.VerifyTimeTicketAsync(dsn, username, password, request);

            if (result.IsSaved)
            {
                entry.SageTicketNumber = result.TicketNumber ?? entry.SageTicketNumber;
                entry.SagePosted = true;
                entry.SagePostedAt = DateTime.Now;
                entry.LastError = null;
                TechBenchRepository.UpdatePostingStatus(entry);
                _repository.SaveWorkEntry(entry);
                _repository.ResolveOutstandingPostingAttempts(
                    entry.Id,
                    "Sage",
                    result.Message,
                    string.IsNullOrWhiteSpace(entry.SageTicketNumber) ? null : $"SAGE-{entry.SageTicketNumber}");
                _repository.AddPostingLog(new PostingLog
                {
                    WorkEntryId = entry.Id,
                    Destination = "Sage",
                    Payload = BuildSageVerificationPayload(entry.SageTicketNumber),
                    Success = true,
                    Message = result.Message,
                    ExternalReference = $"SAGE-{entry.SageTicketNumber}",
                    CreatedAt = DateTime.Now
                });
                RefreshAll();
                ConfigureSageVerificationTimer();
            }
            else if (showFeedback)
            {
                entry.LastError = result.Message;
                TechBenchRepository.UpdatePostingStatus(entry);
                _repository.SaveWorkEntry(entry);
                _repository.AddPostingLog(new PostingLog
                {
                    WorkEntryId = entry.Id,
                    Destination = "Sage",
                    Payload = BuildSageVerificationPayload(entry.SageTicketNumber),
                    Success = false,
                    Message = result.Message,
                    ExternalReference = $"SAGE-{entry.SageTicketNumber}",
                    CreatedAt = DateTime.Now
                });
                RefreshAll();
                SelectedEntry = Entries.FirstOrDefault(saved => saved.Id == entry.Id) ?? _repository.GetWorkEntry(entry.Id);
            }

            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            var message = $"Sage save verification could not run: {ex.Message}";
            return new SageTimeTicketVerificationResult(false, false, message);
        }
        finally
        {
            _isSageVerificationRunning = false;
            VerifySageSaveCommand.RaiseCanExecuteChanged();
        }
    }

    private void ConfigureSageVerificationTimer()
    {
        _sageVerificationTimer.Stop();
        var hasDrafts = _repository.GetWorkEntries(new WorkEntryQuery { PendingSageOnly = true })
            .Any(HasSageDraft);
        if (!_isSagePostingRunning
            && !string.IsNullOrWhiteSpace(SageDsn)
            && hasDrafts)
        {
            _sageVerificationTimer.Start();
        }
    }

    private static string NormalizeSageTicketNumber(string reference) =>
        reference.StartsWith("SAGE-", StringComparison.OrdinalIgnoreCase)
            ? reference[5..].Trim()
            : reference.Trim();

    private static string BuildSageVerificationPayload(string? ticketNumber)
    {
        const string method = "Read-only ODBC verification";
        return string.IsNullOrWhiteSpace(ticketNumber)
            ? method
            : $"{method} for Sage ticket #{ticketNumber}";
    }

    private bool HasSageDraft(WorkEntry entry) =>
        entry.NeedsSagePosting
        && (!string.IsNullOrWhiteSpace(entry.SageTicketNumber)
            || _repository.HasSuccessfulSageDraftLog(entry.Id)
            || _repository.GetOutstandingPostingAttempt(entry.Id, "Sage") is not null);

    private WorkEntry? ResolveEntry(object? parameter)
    {
        return parameter switch
        {
            WorkEntry entry when entry.Id > 0 => _repository.GetWorkEntry(entry.Id) ?? entry,
            _ when Editor.Id > 0 => _repository.GetWorkEntry(Editor.Id),
            _ => null
        };
    }

    private WorkEntry? ResolveEntryForPosting(object? parameter)
    {
        var parameterEntryId = (parameter as WorkEntry)?.Id;
        var targetsEditor = parameter is null
            || (Editor.Id > 0 && parameterEntryId == Editor.Id);

        if (targetsEditor && (Editor.IsDirty || Editor.Id == 0))
        {
            return SaveEditor();
        }

        return ResolveEntry(parameter);
    }

    private static string CreatePostingPayloadHash(
        WorkEntry entry,
        Client client,
        Ticket? ticket,
        string destination)
    {
        var snapshot = JsonSerializer.Serialize(new
        {
            entry.Id,
            Destination = destination.Trim(),
            WorkDate = entry.WorkDate.Date,
            entry.ClientId,
            entry.ManualClientName,
            entry.TicketId,
            entry.TicketNumberText,
            entry.DurationMinutes,
            entry.Billable,
            entry.Note,
            entry.InternalNote,
            ClientExternalId = client.ExternalId,
            client.SageCustomerId,
            TicketExternalId = ticket?.ExternalId,
            TicketNumber = ticket?.TicketNumber
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
    }

    private void RunSearch()
    {
        var query = new WorkEntryQuery
        {
            StartDate = SearchStartDate,
            EndDate = SearchEndDate,
            ClientId = SearchClient?.Id,
            TicketText = SearchTicketText,
            Keyword = SearchKeyword,
            Tags = SearchTags,
            FollowUpState = SearchOpenFollowUpsOnly ? null : SearchFollowUpOption?.Value,
            OpenFollowUpsOnly = SearchOpenFollowUpsOnly,
            PostingStatus = ParseStatusFilter(SearchStatusFilter)
        };

        SearchResults.Clear();
        foreach (var entry in _repository.GetWorkEntries(query))
        {
            SearchResults.Add(entry);
        }

        StatusMessage = $"Search returned {SearchResults.Count} entries";
    }

    private void ClearSearch()
    {
        SearchClient = null;
        SearchStartDate = DateTime.Today.AddDays(-14);
        SearchEndDate = DateTime.Today;
        SearchTicketText = string.Empty;
        SearchKeyword = string.Empty;
        SearchTags = string.Empty;
        SearchFollowUpOption = SearchFollowUpOptions.FirstOrDefault();
        SearchOpenFollowUpsOnly = false;
        SearchStatusFilter = "Any";
        RunSearch();
    }

    private void SelectEditorClient(object? parameter)
    {
        if (parameter is not Client client)
        {
            return;
        }

        Editor.UseManualClient = false;
        Editor.ManualClientName = string.Empty;
        Editor.SelectedClient = client;
        SyncEditorClientFilterText(client.DisplayName);
        IsEditorClientDropDownOpen = false;
        RefreshEditorTickets();
        SaveEntryCommand.RaiseCanExecuteChanged();
    }

    private void OpenWhdTicket(object? parameter)
    {
        var ticket = ResolveWhdTicket(parameter);
        if (ticket is null)
        {
            _dialogService.Error("Open WHD ticket", "Select a synced Web Help Desk ticket first.");
            return;
        }

        if (!TryResolveWhdTicketId(ticket, out var whdTicketId))
        {
            _dialogService.Error("Open WHD ticket", $"Cannot read the WHD ticket ID from {ticket.TicketNumber}.");
            return;
        }

        if (!TryBuildWhdTicketUri(whdTicketId, out var uri, out var errorMessage))
        {
            _dialogService.Error("Open WHD ticket", errorMessage);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString())
            {
                UseShellExecute = true
            });
            StatusMessage = $"Opened {ticket.TicketNumber} in Web Help Desk.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            _dialogService.Error("Open WHD ticket", $"Could not open the ticket in your browser: {ex.Message}");
        }
    }

    private bool CanOpenWhdTicket(object? parameter)
    {
        var ticket = ResolveWhdTicket(parameter);
        return ticket is not null
            && ticket.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase)
            && TryResolveWhdTicketId(ticket, out _);
    }

    private async Task ChangeTicketStatusAsync(object? parameter)
    {
        var selectedTicket = parameter as Ticket ?? SelectedTicket;
        if (selectedTicket is null || selectedTicket.Id <= 0)
        {
            _dialogService.Error("Ticket status", "Select a ticket before changing status.");
            return;
        }

        if (SelectedTicketStatus is null)
        {
            _dialogService.Error("Ticket status", "Select a status before saving.");
            return;
        }

        var ticket = _repository.GetTicket(selectedTicket.Id) ?? selectedTicket;
        var status = SelectedTicketStatus;
        var statusName = string.IsNullOrWhiteSpace(status.Name) ? "Open" : status.Name.Trim();

        if (ticket.WhdStatusTypeId.HasValue
            && status.WhdStatusTypeId.HasValue
            && ticket.WhdStatusTypeId.Value == status.WhdStatusTypeId.Value)
        {
            StatusMessage = $"{ticket.TicketNumber} is already {statusName}.";
            return;
        }

        if (!ticket.WhdStatusTypeId.HasValue
            && ticket.Status.Equals(statusName, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = $"{ticket.TicketNumber} is already {statusName}.";
            return;
        }

        if (status.IsClosed)
        {
            var confirmed = _dialogService.Confirm(
                "Close ticket?",
                $"Change {ticket.TicketNumber} to {statusName}? Closed tickets will drop off the active ticket list after the change.");
            if (!confirmed)
            {
                return;
            }
        }

        if (!ticket.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase))
        {
            _dialogService.Error("Ticket status", "TechBench only changes status for synced Web Help Desk tickets.");
            return;
        }

        if (!HasWhdConnectionFields())
        {
            _dialogService.Error("Ticket status", "Enter and save the Web Help Desk connection settings before changing WHD ticket status.");
            return;
        }

        if (!status.WhdStatusTypeId.HasValue)
        {
            _dialogService.Error("Ticket status", "This status does not have a synced WHD status ID. Sync Ticket Statuses first.");
            return;
        }

        if (!TryResolveWhdTicketId(ticket, out var whdTicketId))
        {
            _dialogService.Error("Ticket status", $"Cannot read the WHD ticket ID from {ticket.TicketNumber}.");
            return;
        }

        StatusMessage = $"Changing {ticket.TicketNumber} to {statusName} in Web Help Desk...";
        var result = await _whdRestClient.UpdateTicketStatusAsync(
            BuildWhdConnectionSettings(),
            whdTicketId,
            status.WhdStatusTypeId.Value,
            statusName);

        if (!result.Success)
        {
            StatusMessage = result.Message;
            _dialogService.Error("Ticket status", result.Message);
            return;
        }

        StatusMessage = result.Message;

        ticket.Status = statusName;
        ticket.WhdStatusTypeId = status.WhdStatusTypeId;
        ticket.IsClosed = status.IsClosed;
        ticket.LastSyncedAt = DateTime.Now;
        _repository.SaveTicket(ticket);
        RefreshTicketList();
        RefreshEditorTickets();
    }

    private bool CanChangeTicketStatus(object? parameter)
    {
        var ticket = parameter as Ticket ?? SelectedTicket;
        return ticket is { Id: > 0 }
            && ticket.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase)
            && SelectedTicketStatus is not null;
    }

    private void SaveSageCustomerMapping()
    {
        if (SelectedSageMappingClient is null)
        {
            _dialogService.Error("Sage customer mapping", "Select a client before saving a Sage Customer ID mapping.");
            return;
        }

        _repository.SaveSetting(BuildSageCustomerSettingKey(SelectedSageMappingClient.Id), SageMappedCustomerId.Trim());
        _repository.SaveClientSageMapping(SelectedSageMappingClient.Id, SageMappedCustomerId.Trim());
        RefreshClients();
        StatusMessage = $"Saved Sage Customer ID mapping for {SelectedSageMappingClient.Name}.";
    }

    private void RefreshDatabaseSafetyStatus()
    {
        var integrity = _databaseBackupService.LastIntegrityResult
            ?? _databaseBackupService.CheckIntegrity();
        IsDatabaseHealthy = integrity.IsHealthy;
        DatabaseHealthLabel = integrity.Message;

        var latestBackup = _databaseBackupService.GetLatestBackup();
        LastDatabaseBackupLabel = latestBackup is null
            ? "No local backup has been created yet."
            : $"Last verified backup: {latestBackup.LastWriteTime:g}";
    }

    private void BackupDatabase()
    {
        var result = _databaseBackupService.CreateBackup();
        RefreshDatabaseSafetyStatus();
        StatusMessage = result.Message;
        if (!result.Succeeded)
        {
            _dialogService.Error("Back up local data", result.Message);
        }
    }

    private void CheckDatabaseHealth()
    {
        var result = _databaseBackupService.CheckIntegrity();
        RefreshDatabaseSafetyStatus();
        StatusMessage = result.Message;
        if (result.IsHealthy)
        {
            _dialogService.Info("Check local data", result.Message);
        }
        else
        {
            _dialogService.Error("Check local data", result.Message);
        }
    }

    private void OpenBackupFolder()
    {
        try
        {
            Directory.CreateDirectory(DatabaseBackupDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = DatabaseBackupDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage = $"Could not open the backup folder: {ex.Message}";
            _dialogService.Error("Open backups", StatusMessage);
        }
    }

    private void LoadSettings()
    {
        var settings = _repository.GetSettings();
        WhdBaseUrl = settings.GetValueOrDefault("Whd.BaseUrl", string.Empty);
        WhdUsername = settings.GetValueOrDefault("Whd.Username", string.Empty);
        WhdApiToken = LoadCredentialWithLegacyMigration(settings, "Whd.ApiToken");
        SelectedWhdAuthenticationMode = ToWhdAuthenticationModeLabel(
            settings.GetValueOrDefault("Whd.AuthenticationMode", WhdAuthenticationMode.Auto.ToString()));
        WhdAutoSyncEnabled = settings.GetValueOrDefault("Whd.AutoSyncEnabled", "true").Equals("true", StringComparison.OrdinalIgnoreCase);
        WhdAutoSyncMinutesText = settings.GetValueOrDefault("Whd.AutoSyncMinutes", DefaultWhdAutoSyncMinutes.ToString());
        SageEmployeeId = settings.GetValueOrDefault("Sage.EmployeeId", string.Empty);
        SageActivityItemId = settings.GetValueOrDefault("Sage.ActivityItemId", string.Empty);
        SageDsn = settings.GetValueOrDefault("Sage.Dsn", "techbench");
        SageUsername = settings.GetValueOrDefault("Sage.Username", string.Empty);
        SagePassword = LoadCredentialWithLegacyMigration(settings, "Sage.Password");
        SageCompanyPath = settings.GetValueOrDefault("Sage.CompanyPath", string.Empty);
        SageNativeAutoSave = settings.GetValueOrDefault("Sage.NativeAutoSave", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
        IsLightTheme = settings.GetValueOrDefault("Theme", "Dark").Equals("Light", StringComparison.OrdinalIgnoreCase);
        ThemeService.Apply(IsLightTheme ? AppTheme.Light : AppTheme.Dark);
    }

    private void SaveSettings()
    {
        SaveWhdConnectionSettings();
        _repository.SaveSetting("Sage.EmployeeId", SageEmployeeId.Trim());
        _repository.DeleteSetting("Sage.DefaultCustomerId");
        _repository.SaveSetting("Sage.ActivityItemId", SageActivityItemId.Trim());
        _repository.SaveSetting("Sage.NativeAutoSave", SageNativeAutoSave.ToString());
        if (SelectedSageMappingClient is not null)
        {
            _repository.SaveSetting(BuildSageCustomerSettingKey(SelectedSageMappingClient.Id), SageMappedCustomerId.Trim());
            _repository.SaveClientSageMapping(SelectedSageMappingClient.Id, SageMappedCustomerId.Trim());
        }
        SaveSageConnectionSettings();
        _repository.SaveSetting("Theme", IsLightTheme ? "Light" : "Dark");
        ConfigureSageVerificationTimer();
        StatusMessage = "Settings saved.";
    }

    private async Task TestWhdConnectionAsync(object? parameter)
    {
        StatusMessage = "Testing Web Help Desk connection...";
        var result = await _whdRestClient.TestConnectionAsync(BuildWhdConnectionSettings());
        StatusMessage = result.Message;

        if (result.Success)
        {
            _dialogService.Info("Web Help Desk", result.Message);
        }
        else
        {
            _dialogService.Error("Web Help Desk", result.Message);
        }
    }

    private async Task SyncWhdTicketsNowAsync(object? parameter)
    {
        SaveWhdConnectionSettings();

        if (!HasWhdConnectionFields())
        {
            const string message = "Enter the Web Help Desk base URL, username, and API key/password before syncing tickets.";
            StatusMessage = message;
            _dialogService.Error("Web Help Desk tickets", message);
            return;
        }

        await RunWhdTicketSyncAsync(showNotifications: true, isManual: true);
    }

    private async Task SyncWhdClientsAsync(object? parameter)
    {
        SaveWhdConnectionSettings();

        if (!HasWhdConnectionFields())
        {
            const string message = "Enter the Web Help Desk base URL, username, and API key/password before syncing clients.";
            StatusMessage = message;
            _dialogService.Error("Web Help Desk clients", message);
            return;
        }

        StatusMessage = "Syncing Web Help Desk clients...";
        var result = await _whdRestClient.GetClientsAsync(BuildWhdConnectionSettings());
        if (!result.Success)
        {
            StatusMessage = result.Message;
            _dialogService.Error("Web Help Desk clients", result.Message);
            return;
        }

        var matchedCount = SaveWhdClients(result.Clients);
        RefreshAll();
        StatusMessage = matchedCount > 0
            ? $"{result.Message} Auto-matched {matchedCount} to Sage customer(s)."
            : result.Message;
        _dialogService.Info("Web Help Desk clients", StatusMessage);
    }

    private async Task SyncWhdStatusesAsync(object? parameter)
    {
        SaveWhdConnectionSettings();

        if (!HasWhdConnectionFields())
        {
            const string message = "Enter the Web Help Desk base URL, username, and API key/password before syncing statuses.";
            StatusMessage = message;
            _dialogService.Error("Web Help Desk status types", message);
            return;
        }

        StatusMessage = "Syncing Web Help Desk ticket statuses...";
        var result = await _whdRestClient.GetStatusTypesAsync(BuildWhdConnectionSettings());
        if (!result.Success)
        {
            StatusMessage = result.Message;
            _dialogService.Error("Web Help Desk status types", result.Message);
            return;
        }

        SaveWhdStatusTypes(result.StatusTypes);
        RefreshTicketStatusOptions();
        StatusMessage = result.Message;
    }

    private async void HandleWhdAutoSyncTimerTick(object? sender, EventArgs e)
    {
        await RunWhdAutoSyncAsync();
    }

    private async Task RunWhdAutoSyncAsync()
    {
        if (!WhdAutoSyncEnabled || _isWhdAutoSyncRunning || !HasWhdConnectionFields())
        {
            return;
        }

        await RunWhdTicketSyncAsync(showNotifications: true, isManual: false);
    }

    private async Task RunWhdTicketSyncAsync(bool showNotifications, bool isManual)
    {
        if (_isWhdAutoSyncRunning)
        {
            return;
        }

        _isWhdAutoSyncRunning = true;
        var operationName = isManual ? "WHD ticket sync" : "WHD auto-sync";
        try
        {
            var knownBeforeSync = new HashSet<string>(_knownWhdTicketKeys, StringComparer.OrdinalIgnoreCase);
            StatusMessage = $"{operationName} running...";
            var result = await _whdRestClient.GetMyTicketsAsync(BuildWhdConnectionSettings());
            if (!result.Success)
            {
                StatusMessage = $"{operationName} failed: {result.Message}";
                if (isManual)
                {
                    _dialogService.Error("Web Help Desk tickets", result.Message);
                }

                return;
            }

            var newTickets = result.Tickets
                .Where(ticket => !ticket.IsClosed
                    && TryGetWhdTicketKey(ticket, out var key)
                    && !knownBeforeSync.Contains(key))
                .ToList();

            SaveWhdTickets(result.Tickets, result.IsComplete);
            UpdateKnownWhdTicketKeys(result.Tickets, replace: result.IsComplete);
            _lastWhdAutoSyncAt = DateTime.Now;
            RefreshAll();
            StatusMessage = !result.IsComplete
                ? $"{operationName} completed partially. {result.Message}"
                : newTickets.Count == 0
                ? $"{operationName} complete at {_lastWhdAutoSyncAt.Value:g}. No new tickets."
                : $"{operationName} added {newTickets.Count} new ticket(s).";
            OnPropertyChanged(nameof(WhdAutoSyncStatusLabel));

            if (showNotifications && newTickets.Count > 0)
            {
                _notificationService.ShowNewWhdTickets(newTickets);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"{operationName} failed: {ex.Message}";
            if (isManual)
            {
                _dialogService.Error("Web Help Desk tickets", ex.Message);
            }
        }
        finally
        {
            _isWhdAutoSyncRunning = false;
        }
    }

    private void SaveWhdTickets(IReadOnlyList<WhdSyncedTicket> whdTickets, bool reconcileMissing)
    {
        var syncedAt = DateTime.Now;
        _repository.SynchronizeWhdTickets(whdTickets, syncedAt, reconcileMissing);
    }

    private int SaveWhdClients(IReadOnlyList<WhdSyncedClient> whdClients)
    {
        return _repository.SynchronizeWhdClients(whdClients, DateTime.Now);
    }

    private void SaveWhdStatusTypes(IReadOnlyList<WhdStatusType> statusTypes)
    {
        var syncedAt = DateTime.Now;
        foreach (var statusType in statusTypes)
        {
            _repository.UpsertTicketStatusOption(new TicketStatusOption
            {
                Name = statusType.Name,
                Source = "WHD",
                ExternalId = $"WHD-{statusType.Id}",
                WhdStatusTypeId = statusType.Id,
                IsClosed = statusType.IsClosed,
                LastSyncedAt = syncedAt
            });
        }
    }

    private WhdConnectionSettings BuildWhdConnectionSettings()
    {
        return new WhdConnectionSettings
        {
            BaseUrl = WhdBaseUrl.Trim(),
            Username = WhdUsername.Trim(),
            Secret = WhdApiToken,
            AuthenticationMode = ParseWhdAuthenticationMode(SelectedWhdAuthenticationMode)
        };
    }

    private bool HasWhdConnectionFields()
    {
        var authenticationMode = ParseWhdAuthenticationMode(SelectedWhdAuthenticationMode);
        return !string.IsNullOrWhiteSpace(WhdBaseUrl)
            && !string.IsNullOrWhiteSpace(WhdApiToken)
            && (authenticationMode == WhdAuthenticationMode.TechnicianApiKey
                || !string.IsNullOrWhiteSpace(WhdUsername));
    }

    private void SaveWhdConnectionSettings()
    {
        _repository.SaveSetting("Whd.BaseUrl", WhdBaseUrl.Trim());
        _repository.SaveSetting("Whd.Username", WhdUsername.Trim());
        _credentialStore.SetSecret("Whd.ApiToken", WhdApiToken);
        _repository.DeleteSetting("Whd.ApiToken");
        _repository.SaveSetting("Whd.AuthenticationMode", ParseWhdAuthenticationMode(SelectedWhdAuthenticationMode).ToString());
        _repository.SaveSetting("Whd.AutoSyncEnabled", WhdAutoSyncEnabled.ToString());
        _repository.SaveSetting("Whd.AutoSyncMinutes", ResolveWhdAutoSyncIntervalMinutes().ToString());
        ConfigureWhdAutoSyncTimer();
    }

    private void ConfigureWhdAutoSyncTimer()
    {
        _whdAutoSyncTimer.Stop();
        _whdAutoSyncTimer.Interval = TimeSpan.FromMinutes(ResolveWhdAutoSyncIntervalMinutes());
        if (WhdAutoSyncEnabled)
        {
            _whdAutoSyncTimer.Start();
        }

        OnPropertyChanged(nameof(WhdAutoSyncStatusLabel));
    }

    private int ResolveWhdAutoSyncIntervalMinutes()
    {
        if (!int.TryParse(WhdAutoSyncMinutesText, out var minutes))
        {
            return DefaultWhdAutoSyncMinutes;
        }

        return Math.Clamp(minutes, 1, 120);
    }

    private void PrimeKnownWhdTicketKeys()
    {
        _knownWhdTicketKeys.Clear();
        foreach (var ticket in _repository.GetTickets(includeClosed: true)
                     .Where(static ticket => ticket.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase)))
        {
            if (TryGetWhdTicketKey(ticket, out var key))
            {
                _knownWhdTicketKeys.Add(key);
            }
        }
    }

    private void UpdateKnownWhdTicketKeys(IEnumerable<WhdSyncedTicket> tickets, bool replace)
    {
        if (replace)
        {
            _knownWhdTicketKeys.Clear();
        }
        foreach (var ticket in tickets)
        {
            if (TryGetWhdTicketKey(ticket, out var key))
            {
                _knownWhdTicketKeys.Add(key);
            }
        }
    }

    private void TestSageConnection()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(SageEmployeeId))
        {
            missing.Add("employee ID");
        }

        if (string.IsNullOrWhiteSpace(SageActivityItemId))
        {
            missing.Add("activity item ID");
        }

        if (missing.Count > 0)
        {
            _dialogService.Error("Sage 50", $"Enter Sage {string.Join(", ", missing)} before creating time tickets.");
            return;
        }

        var result = SageNativeUiAutomation.CheckAvailability();
        StatusMessage = result;
        if (result.StartsWith("Found Sage 50", StringComparison.Ordinal))
        {
            _dialogService.Info("Sage 50", result);
        }
        else
        {
            _dialogService.Error("Sage 50", result);
        }
    }

    private async Task TestSageOdbcAsync(object? parameter)
    {
        SaveSageConnectionSettings();

        if (string.IsNullOrWhiteSpace(SageDsn))
        {
            const string message = "Enter the Sage ODBC DSN before testing the customer table.";
            StatusMessage = message;
            _dialogService.Error("Sage ODBC", message);
            return;
        }

        StatusMessage = "Testing Sage ODBC customer access...";
        try
        {
            var sampleCustomers = await _sageOdbcClient.ReadCustomersAsync(
                SageDsn,
                SageUsername,
                SagePassword,
                maxRows: 1);
            var sampleText = sampleCustomers.Count == 0
                ? "Connected to Sage ODBC, but no customers were returned."
                : $"Connected to Sage ODBC. Sample customer: {sampleCustomers[0].CustomerId} - {sampleCustomers[0].CustomerName}.";
            StatusMessage = sampleText;
            _dialogService.Info("Sage ODBC", sampleText);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            var message = $"Sage ODBC customer test failed: {ex.Message}";
            StatusMessage = message;
            _dialogService.Error("Sage ODBC", message);
        }
    }

    private async Task SyncSageCustomersAsync(object? parameter)
    {
        SaveSageConnectionSettings();

        if (string.IsNullOrWhiteSpace(SageDsn))
        {
            const string message = "Enter the Sage ODBC DSN before syncing customers.";
            StatusMessage = message;
            _dialogService.Error("Sage customers", message);
            return;
        }

        StatusMessage = "Syncing Sage customers...";
        IReadOnlyList<SageCustomer> customers;
        try
        {
            customers = await _sageOdbcClient.ReadCustomersAsync(SageDsn, SageUsername, SagePassword);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            var message = $"Sage customer sync failed: {ex.Message}";
            StatusMessage = message;
            _dialogService.Error("Sage customers", message);
            return;
        }

        var (savedCount, staleCount) = _repository.SynchronizeSageCustomers(customers, DateTime.Now);

        RefreshClients();
        StatusMessage = staleCount > 0
            ? $"Synced {savedCount} active Sage customers from {SageDsn.Trim()}. Removed or deactivated {staleCount} old Sage customer(s)."
            : $"Synced {savedCount} active Sage customers from {SageDsn.Trim()}.";
        _dialogService.Info("Sage customers", StatusMessage);
    }

    private void SaveSageConnectionSettings()
    {
        _repository.SaveSetting("Sage.Dsn", SageDsn.Trim());
        _repository.SaveSetting("Sage.Username", SageUsername.Trim());
        _credentialStore.SetSecret("Sage.Password", SagePassword);
        _repository.DeleteSetting("Sage.Password");
        _repository.SaveSetting("Sage.CompanyPath", SageCompanyPath.Trim());
        ConfigureSageVerificationTimer();
    }

    private IReadOnlyDictionary<string, string> BuildPostingSettings()
    {
        return new Dictionary<string, string>(_repository.GetSettings(), StringComparer.OrdinalIgnoreCase)
        {
            ["Whd.BaseUrl"] = WhdBaseUrl.Trim(),
            ["Whd.Username"] = WhdUsername.Trim(),
            ["Whd.ApiToken"] = WhdApiToken,
            ["Whd.AuthenticationMode"] = ParseWhdAuthenticationMode(SelectedWhdAuthenticationMode).ToString(),
            ["Sage.Password"] = SagePassword,
            ["Sage.EmployeeId"] = SageEmployeeId.Trim(),
            ["Sage.ActivityItemId"] = SageActivityItemId.Trim(),
            ["Sage.NativeAutoSave"] = SageNativeAutoSave.ToString()
        };
    }

    private static WhdAuthenticationMode ParseWhdAuthenticationMode(string? label)
    {
        return label?.Trim() switch
        {
            "Username + application API key" => WhdAuthenticationMode.ApplicationApiKey,
            "Technician API key" => WhdAuthenticationMode.TechnicianApiKey,
            "Username + password" => WhdAuthenticationMode.UsernamePassword,
            _ when Enum.TryParse<WhdAuthenticationMode>(label, ignoreCase: true, out var parsed) => parsed,
            _ => WhdAuthenticationMode.Auto
        };
    }

    private static string ToWhdAuthenticationModeLabel(string? value)
    {
        return ParseWhdAuthenticationMode(value) switch
        {
            WhdAuthenticationMode.ApplicationApiKey => "Username + application API key",
            WhdAuthenticationMode.TechnicianApiKey => "Technician API key",
            WhdAuthenticationMode.UsernamePassword => "Username + password",
            _ => "Auto (detect once)"
        };
    }

    private string LoadCredentialWithLegacyMigration(
        IReadOnlyDictionary<string, string> settings,
        string key)
    {
        var protectedValue = _credentialStore.GetSecret(key);
        if (!string.IsNullOrEmpty(protectedValue))
        {
            if (settings.ContainsKey(key))
            {
                _repository.DeleteSetting(key);
            }

            return protectedValue;
        }

        var legacyValue = settings.GetValueOrDefault(key, string.Empty);
        if (string.IsNullOrEmpty(legacyValue))
        {
            return string.Empty;
        }

        _credentialStore.SetSecret(key, legacyValue);
        _repository.DeleteSetting(key);
        return legacyValue;
    }

    private void HandleEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        HandleNoteEditorPropertyChanged(e);
        if (!_isSynchronizingEditorReferences
            && e.PropertyName == nameof(WorkEntryEditorViewModel.UseManualClient))
        {
            _isSynchronizingEditorReferences = true;
            try
            {
                if (Editor.UseManualClient)
                {
                    Editor.RunWithoutDirtyTracking(() => Editor.SelectedClient = null);
                    SyncEditorClientFilterText(string.Empty);
                }
                else
                {
                    Editor.RunWithoutDirtyTracking(() => Editor.ManualClientName = string.Empty);
                }
            }
            finally
            {
                _isSynchronizingEditorReferences = false;
            }

            RefreshEditorClientOptions();
            RefreshEditorTickets();
        }

        if (!_isSynchronizingEditorReferences
            && e.PropertyName == nameof(WorkEntryEditorViewModel.SelectedClient))
        {
            SyncEditorClientFilterText(Editor.SelectedClient?.DisplayName ?? string.Empty);
            RefreshEditorTickets();
        }

        if (!_isSynchronizingEditorReferences
            && e.PropertyName == nameof(WorkEntryEditorViewModel.SelectedTicket))
        {
            SelectedTicketStatus = ResolveTicketStatusOption(Editor.SelectedTicket);
            TryAutoMatchSageCustomerForTicket(Editor.SelectedTicket);
            ChangeTicketStatusCommand.RaiseCanExecuteChanged();
        }

        if (e.PropertyName == nameof(WorkEntryEditorViewModel.Id))
        {
            OnPropertyChanged(nameof(EditorTitle));
        }

        if (e.PropertyName is nameof(WorkEntryEditorViewModel.SelectedClient)
            or nameof(WorkEntryEditorViewModel.ManualClientName)
            or nameof(WorkEntryEditorViewModel.UseManualClient))
        {
            OnPropertyChanged(nameof(EditorSubtitle));
        }

        if (e.PropertyName == nameof(WorkEntryEditorViewModel.SelectedTicket))
        {
            OnPropertyChanged(nameof(ShowOpenWhdAction));
        }

        if (e.PropertyName == nameof(WorkEntryEditorViewModel.IsDirty))
        {
            OnPropertyChanged(nameof(WorkspaceStateLabel));
        }

        if (e.PropertyName is nameof(WorkEntryEditorViewModel.HasPostedDestination)
            or nameof(WorkEntryEditorViewModel.WhdPosted)
            or nameof(WorkEntryEditorViewModel.SagePosted))
        {
            RaiseEditorStateProperties();
        }
        else
        {
            RaiseEditorWorkflowCommandStates();
        }
    }

    private void RaiseEntryCommandStates()
    {
        PostWhdCommand.RaiseCanExecuteChanged();
        PostSageCommand.RaiseCanExecuteChanged();
        MarkWhdPostedCommand.RaiseCanExecuteChanged();
        VerifySageSaveCommand.RaiseCanExecuteChanged();
        OpenWhdTicketCommand.RaiseCanExecuteChanged();
        RaiseEditorWorkflowCommandStates();
    }

    private void RaiseEditorWorkflowCommandStates()
    {
        SaveEntryCommand.RaiseCanExecuteChanged();
        DeleteEntryCommand.RaiseCanExecuteChanged();
        DuplicateEntryCommand.RaiseCanExecuteChanged();
        UnlockPostedEntryCommand.RaiseCanExecuteChanged();
        InsertRecentNoteCommand.RaiseCanExecuteChanged();
    }

    private void RaiseEditorStateProperties()
    {
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(EditorSubtitle));
        OnPropertyChanged(nameof(IsEditorLocked));
        OnPropertyChanged(nameof(IsEditorEditable));
        OnPropertyChanged(nameof(ShowOpenWhdAction));
        OnPropertyChanged(nameof(WorkspaceStateLabel));
        RaiseEditorWorkflowCommandStates();
    }

    private void UpdateEditorPostingState(WorkEntry entry)
    {
        Editor.RunWithoutDirtyTracking(() =>
        {
            Editor.WhdPosted = entry.WhdPosted;
            Editor.WhdPostedAt = entry.WhdPostedAt;
            Editor.SagePosted = entry.SagePosted;
            Editor.SagePostedAt = entry.SagePostedAt;
            Editor.SageTicketNumber = entry.SageTicketNumber;
            Editor.PostingStatus = entry.PostingStatus;
            Editor.LastError = entry.LastError ?? string.Empty;
            Editor.ModifiedAfterPosting = entry.ModifiedAfterPosting;
        });
        RaiseEditorStateProperties();
    }

    private void SyncEditorClientFilterText(string value)
    {
        _isSyncingEditorClientFilterText = true;
        try
        {
            EditorClientFilterText = value;
            IsEditorClientDropDownOpen = false;
        }
        finally
        {
            _isSyncingEditorClientFilterText = false;
        }
    }

    private Client? ResolveEditorClient(int? clientId)
    {
        if (!clientId.HasValue)
        {
            return null;
        }

        var client = EditorClients.FirstOrDefault(candidate => candidate.Id == clientId.Value)
            ?? Clients.FirstOrDefault(candidate => candidate.Id == clientId.Value)
            ?? _repository.GetClient(clientId.Value);
        if (client is not null && EditorClients.All(candidate => candidate.Id != client.Id))
        {
            EditorClients.Insert(0, client);
        }

        return client;
    }

    private Ticket? ResolveEditorTicket(int? ticketId)
    {
        if (!ticketId.HasValue)
        {
            return null;
        }

        return TicketsForEditor.FirstOrDefault(candidate => candidate.Id == ticketId.Value)
            ?? _repository.GetTicket(ticketId.Value);
    }

    private static PostingStatus? ParseStatusFilter(string status)
    {
        return status switch
        {
            "Draft" => PostingStatus.Draft,
            "Ready" => PostingStatus.Ready,
            "Posted to WHD" => PostingStatus.PostedToWhd,
            "Posted to Sage" => PostingStatus.PostedToSage,
            "Posted to Both" => PostingStatus.PostedToBoth,
            "Failed" => PostingStatus.Failed,
            _ => null
        };
    }

    private void ApplyHistoryRangePreset(string preset)
    {
        var today = DateTime.Today;
        var thisWeekStart = GetWeekStart(today);
        var thisMonthStart = new DateTime(today.Year, today.Month, 1);
        var thisYearStart = new DateTime(today.Year, 1, 1);

        var range = preset switch
        {
            "This Week" => (Start: thisWeekStart, End: thisWeekStart.AddDays(6)),
            "Last Week" => (Start: thisWeekStart.AddDays(-7), End: thisWeekStart.AddDays(-1)),
            "Last Month" => (Start: thisMonthStart.AddMonths(-1), End: thisMonthStart.AddDays(-1)),
            "This Year" => (Start: thisYearStart, End: today),
            "Last Year" => (Start: thisYearStart.AddYears(-1), End: thisYearStart.AddDays(-1)),
            "This Month" => (Start: thisMonthStart, End: today),
            _ => (Start: HistoryStartDate ?? today, End: HistoryEndDate ?? today)
        };

        _isUpdatingHistoryRange = true;
        try
        {
            HistoryStartDate = range.Start;
            HistoryEndDate = range.End;
        }
        finally
        {
            _isUpdatingHistoryRange = false;
        }
    }

    private void SetHistoryPresetWithoutApplying(string preset)
    {
        _isUpdatingHistoryRange = true;
        try
        {
            HistoryRangePreset = preset;
        }
        finally
        {
            _isUpdatingHistoryRange = false;
        }
    }

    private void RaiseHistoryTotalsChanged()
    {
        OnPropertyChanged(nameof(HistoryTotalLabel));
        OnPropertyChanged(nameof(HistoryBillableLabel));
        OnPropertyChanged(nameof(HistoryNonBillableLabel));
        OnPropertyChanged(nameof(HistoryEntryCountLabel));
        OnPropertyChanged(nameof(HistoryWhdPendingLabel));
        OnPropertyChanged(nameof(HistorySagePendingLabel));
    }

    private static DateTime GetWeekStart(DateTime date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return date.Date.AddDays(-offset);
    }

    private static string FormatMinutes(int minutes)
    {
        var hours = minutes / 60;
        var remainder = minutes % 60;
        return hours > 0 ? $"{hours}h {remainder:00}m" : $"{remainder}m";
    }

    private static CloseoutItem BuildCloseoutItem(string key, string label, int count, string detail)
    {
        return new CloseoutItem
        {
            Key = key,
            Label = label,
            Value = count.ToString(),
            Detail = detail,
            HasIssue = count > 0
        };
    }

    private static Ticket CreateNoTicketOption(int clientId)
    {
        return new Ticket
        {
            Id = 0,
            ClientId = clientId,
            TicketNumber = "No Ticket",
            Subject = string.Empty,
            Status = "None",
            Source = "Local"
        };
    }

    private TicketStatusOption? ResolveTicketStatusOption(Ticket? ticket)
    {
        if (ticket is null)
        {
            return null;
        }

        if (ticket.WhdStatusTypeId.HasValue)
        {
            var byId = TicketStatusOptions.FirstOrDefault(option => option.WhdStatusTypeId == ticket.WhdStatusTypeId);
            if (byId is not null)
            {
                return byId;
            }
        }

        return TicketStatusOptions.FirstOrDefault(option => option.Name.Equals(ticket.Status, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryResolveWhdTicketId(Ticket ticket, out int whdTicketId)
    {
        var candidates = new[]
        {
            ticket.ExternalId,
            ticket.TicketNumber
        };

        foreach (var candidate in candidates)
        {
            var normalized = candidate?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (normalized.StartsWith("WHD-", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[4..];
            }

            if (int.TryParse(normalized, out whdTicketId) && whdTicketId > 0)
            {
                return true;
            }
        }

        whdTicketId = 0;
        return false;
    }

    private static bool TryGetWhdTicketKey(Ticket ticket, out string key)
    {
        key = ticket.ExternalId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            key = ticket.TicketNumber.Trim();
        }

        return !string.IsNullOrWhiteSpace(key);
    }

    private static bool TryGetWhdTicketKey(WhdSyncedTicket ticket, out string key)
    {
        key = ticket.ExternalId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            key = ticket.TicketNumber.Trim();
        }

        return !string.IsNullOrWhiteSpace(key);
    }

    private Ticket? ResolveWhdTicket(object? parameter)
    {
        return parameter switch
        {
            Ticket { Id: > 0 } ticket => _repository.GetTicket(ticket.Id) ?? ticket,
            WorkEntry { TicketId: not null } entry => _repository.GetTicket(entry.TicketId.Value),
            _ when Editor.SelectedTicket is { Id: > 0 } editorTicket => _repository.GetTicket(editorTicket.Id) ?? editorTicket,
            _ when SelectedEntry?.TicketId is int selectedEntryTicketId => _repository.GetTicket(selectedEntryTicketId),
            _ when SelectedTicket is { Id: > 0 } selectedTicket => _repository.GetTicket(selectedTicket.Id) ?? selectedTicket,
            _ => null
        };
    }

    private bool TryBuildWhdTicketUri(int whdTicketId, out Uri uri, out string errorMessage)
    {
        uri = new Uri("about:blank");
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(WhdBaseUrl))
        {
            errorMessage = "Enter the Web Help Desk base URL in Settings first.";
            return false;
        }

        if (!Uri.TryCreate(WhdBaseUrl.Trim(), UriKind.Absolute, out var input)
            || !input.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "The Web Help Desk base URL must use https://.";
            return false;
        }

        var path = input.AbsolutePath.TrimEnd('/');
        const string webObjectsPath = "/helpdesk/WebObjects/Helpdesk.woa";
        string whdRootPath;

        var webObjectsIndex = path.IndexOf(webObjectsPath, StringComparison.OrdinalIgnoreCase);
        if (webObjectsIndex >= 0)
        {
            whdRootPath = path[..(webObjectsIndex + webObjectsPath.Length)];
        }
        else
        {
            var helpdeskIndex = path.IndexOf("/helpdesk", StringComparison.OrdinalIgnoreCase);
            whdRootPath = helpdeskIndex >= 0
                ? $"{path[..(helpdeskIndex + "/helpdesk".Length)]}/WebObjects/Helpdesk.woa"
                : webObjectsPath;
        }

        var builder = new UriBuilder(input)
        {
            Path = $"{whdRootPath.TrimEnd('/')}/wa/TicketActions/view",
            Query = $"ticket={whdTicketId}",
            Fragment = string.Empty
        };

        uri = builder.Uri;
        return true;
    }

    private static string BuildSageCustomerSettingKey(int clientId)
    {
        return $"Sage.CustomerId.{clientId}";
    }

    private void TryAutoMatchSageCustomerForTicket(Ticket? ticket)
    {
        if (ticket is not { Id: > 0, ClientId: > 0 })
        {
            return;
        }

        var matchedClient = _repository.TryAutoMatchSageCustomerForClient(ticket.ClientId);
        if (matchedClient is null)
        {
            return;
        }

        RefreshClients();
        StatusMessage = $"Auto-matched {matchedClient.Name} to Sage customer {matchedClient.SageCustomerLabel}.";
    }

    public void Dispose()
    {
        DisposeNoteFeatures();
        Updates.Dispose();
        _whdAutoSyncTimer.Stop();
        _whdAutoSyncTimer.Tick -= HandleWhdAutoSyncTimerTick;
        _sageVerificationTimer.Stop();
        _sageVerificationTimer.Tick -= HandleSageVerificationTimerTick;
    }
}
