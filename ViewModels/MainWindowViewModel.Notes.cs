using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using Microsoft.Win32;
using TechBench.Data;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly DispatcherTimer _editorDraftTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(1200)
    };
    private readonly DispatcherTimer _noteLinkSearchTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250)
    };
    private bool _isPersistingEditorDraft;
    private bool _isRestoringEditorDraft;
    private DateTime? _lastEditorSaveAt;
    private string _editorSaveStatus = "Saved";
    private string _searchTags = string.Empty;
    private FollowUpOption? _searchFollowUpOption;
    private bool _searchOpenFollowUpsOnly;
    private NoteTemplate? _managedNoteTemplate;
    private string _templateName = string.Empty;
    private string _templateCategory = string.Empty;
    private string _templateText = string.Empty;
    private bool _isNoteLinkPickerOpen;
    private string _noteLinkSearchText = string.Empty;
    private WorkEntryLinkTypeOption? _selectedNoteLinkTypeOption;
    private WorkEntry? _pendingFollowUpSource;
    private IReadOnlyList<WorkEntryLink> _lastDeletedEntryLinks = [];

    public ObservableCollection<WorkEntry> RecentClientEntries { get; } = new();
    public ObservableCollection<WorkEntryLink> RelatedNotes { get; } = new();
    public ObservableCollection<WorkEntry> NoteLinkCandidates { get; } = new();
    public ObservableCollection<WorkEntryLinkTypeOption> NoteLinkTypeOptions { get; } = new();
    public ObservableCollection<FollowUpOption> FollowUpOptions { get; } = new();
    public ObservableCollection<FollowUpOption> SearchFollowUpOptions { get; } = new();
    public ObservableCollection<string> TagSuggestions { get; } = new();

    public RelayCommand InsertRecentNoteCommand { get; private set; } = null!;
    public RelayCommand ToggleNoteLinkPickerCommand { get; private set; } = null!;
    public RelayCommand LinkExistingNoteCommand { get; private set; } = null!;
    public RelayCommand RemoveNoteLinkCommand { get; private set; } = null!;
    public RelayCommand OpenRelatedNoteCommand { get; private set; } = null!;
    public RelayCommand ContinueThisWorkCommand { get; private set; } = null!;
    public RelayCommand CancelPendingNoteLinkCommand { get; private set; } = null!;
    public RelayCommand ImportGoogleSheetsCommand { get; private set; } = null!;
    public RelayCommand NewNoteTemplateCommand { get; private set; } = null!;
    public RelayCommand SaveNoteTemplateCommand { get; private set; } = null!;
    public RelayCommand DeleteNoteTemplateCommand { get; private set; } = null!;

    public string SearchTags
    {
        get => _searchTags;
        set => SetProperty(ref _searchTags, value);
    }

    public FollowUpOption? SearchFollowUpOption
    {
        get => _searchFollowUpOption;
        set => SetProperty(ref _searchFollowUpOption, value);
    }

    public bool SearchOpenFollowUpsOnly
    {
        get => _searchOpenFollowUpsOnly;
        set => SetProperty(ref _searchOpenFollowUpsOnly, value);
    }

    public NoteTemplate? ManagedNoteTemplate
    {
        get => _managedNoteTemplate;
        set
        {
            if (SetProperty(ref _managedNoteTemplate, value))
            {
                TemplateName = value?.Name ?? string.Empty;
                TemplateCategory = value?.Category ?? string.Empty;
                TemplateText = value?.TemplateText ?? string.Empty;
                OnPropertyChanged(nameof(CanEditManagedNoteTemplate));
                DeleteNoteTemplateCommand.RaiseCanExecuteChanged();
                SaveNoteTemplateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanEditManagedNoteTemplate =>
        _currentUser.IsAdmin
        && (ManagedNoteTemplate is null
            || ManagedNoteTemplate.ScopeType.Equals(
                "Organization",
                StringComparison.OrdinalIgnoreCase));

    public string TemplateName
    {
        get => _templateName;
        set
        {
            if (SetProperty(ref _templateName, value))
            {
                SaveNoteTemplateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TemplateCategory
    {
        get => _templateCategory;
        set => SetProperty(ref _templateCategory, value);
    }

    public string TemplateText
    {
        get => _templateText;
        set
        {
            if (SetProperty(ref _templateText, value))
            {
                SaveNoteTemplateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string EditorSaveStatus
    {
        get => _editorSaveStatus;
        private set
        {
            if (SetProperty(ref _editorSaveStatus, value))
            {
                OnPropertyChanged(nameof(WorkspaceStateLabel));
            }
        }
    }

    public bool IsNoteLinkPickerOpen
    {
        get => _isNoteLinkPickerOpen;
        set
        {
            if (!SetProperty(ref _isNoteLinkPickerOpen, value))
            {
                return;
            }

            OnPropertyChanged(nameof(NoteLinkPickerButtonLabel));

            if (value)
            {
                RefreshNoteLinkCandidates();
            }
            else
            {
                _noteLinkSearchTimer.Stop();
                NoteLinkCandidates.Clear();
                RaiseNoteLinkProperties();
            }
        }
    }

    public string NoteLinkSearchText
    {
        get => _noteLinkSearchText;
        set
        {
            if (!SetProperty(ref _noteLinkSearchText, value) || !IsNoteLinkPickerOpen)
            {
                return;
            }

            _noteLinkSearchTimer.Stop();
            _noteLinkSearchTimer.Start();
        }
    }

    public WorkEntryLinkTypeOption? SelectedNoteLinkTypeOption
    {
        get => _selectedNoteLinkTypeOption;
        set => SetProperty(ref _selectedNoteLinkTypeOption, value);
    }

    public WorkEntry? PendingFollowUpSource => _pendingFollowUpSource;
    public bool HasPendingFollowUp => PendingFollowUpSource is not null;
    public bool ShowRelatedNotesSection => Editor.Id > 0 || HasPendingFollowUp;
    public bool HasRelatedNotes => RelatedNotes.Count > 0;
    public bool HasNoteLinkCandidates => NoteLinkCandidates.Count > 0;
    public string RelatedNotesHeader => $"Related notes ({RelatedNotes.Count})";
    public string NoteLinkPickerButtonLabel => IsNoteLinkPickerOpen ? "Close note picker" : "Link existing note";

    public bool HasRecentClientEntries => RecentClientEntries.Count > 0;
    public string RecentClientNotesHeader => $"Recent client notes ({RecentClientEntries.Count})";
    public bool FullTextSearchAvailable => _repository.FullTextSearchAvailable;

    private void InitializeNoteFeatures()
    {
        InsertRecentNoteCommand = new RelayCommand(InsertRecentNote, parameter => parameter is WorkEntry && IsEditorEditable);
        ToggleNoteLinkPickerCommand = new RelayCommand(
            _ => IsNoteLinkPickerOpen = !IsNoteLinkPickerOpen,
            _ => CanWrite && Editor.Id > 0 && !IsEntryOperationRunning && !IsEditorLocked);
        LinkExistingNoteCommand = new RelayCommand(
            LinkExistingNote,
            parameter => CanWrite && parameter is WorkEntry { Id: > 0 } && Editor.Id > 0 && !IsEntryOperationRunning && !IsEditorLocked);
        RemoveNoteLinkCommand = new RelayCommand(
            RemoveNoteLink,
            parameter => CanWrite && parameter is WorkEntryLink { Id: > 0 } && !IsEntryOperationRunning && !IsEditorLocked);
        OpenRelatedNoteCommand = new RelayCommand(
            OpenRelatedNote,
            parameter => parameter is WorkEntryLink or WorkEntry);
        ContinueThisWorkCommand = new RelayCommand(
            _ => ContinueThisWork(),
            _ => CanWrite && Editor.Id > 0 && !IsEntryOperationRunning);
        CancelPendingNoteLinkCommand = new RelayCommand(
            _ => CancelPendingNoteLink(),
            _ => CanWrite && HasPendingFollowUp);
        ImportGoogleSheetsCommand = new RelayCommand(
            _ => ImportGoogleSheetsCsv(),
            _ => CanWrite && !IsEntryOperationRunning);
        NewNoteTemplateCommand = new RelayCommand(
            _ => StartNewNoteTemplate(),
            _ => _currentUser.IsAdmin && CanWrite);
        SaveNoteTemplateCommand = new RelayCommand(_ => SaveManagedNoteTemplate(), _ => CanSaveManagedNoteTemplate());
        DeleteNoteTemplateCommand = new RelayCommand(
            _ => DeleteManagedNoteTemplate(),
            _ => ManagedNoteTemplate is { Id: > 0 } template
                && CanManageNoteTemplate(template));

        FollowUpOptions.Add(new FollowUpOption(FollowUpState.None, "None"));
        FollowUpOptions.Add(new FollowUpOption(FollowUpState.FollowUp, "Follow-up"));
        FollowUpOptions.Add(new FollowUpOption(FollowUpState.Waiting, "Waiting"));
        FollowUpOptions.Add(new FollowUpOption(FollowUpState.Completed, "Completed"));
        SearchFollowUpOptions.Add(new FollowUpOption(null, "Any"));
        foreach (var option in FollowUpOptions)
        {
            SearchFollowUpOptions.Add(option);
        }

        SearchFollowUpOption = SearchFollowUpOptions[0];
        NoteLinkTypeOptions.Add(new WorkEntryLinkTypeOption(WorkEntryLinkType.Related, "Related"));
        NoteLinkTypeOptions.Add(new WorkEntryLinkTypeOption(WorkEntryLinkType.FollowUpTo, "Follow-up to"));
        SelectedNoteLinkTypeOption = NoteLinkTypeOptions[0];
        _editorDraftTimer.Tick += HandleEditorDraftTimerTick;
        _noteLinkSearchTimer.Tick += HandleNoteLinkSearchTimerTick;
    }

    private void HandleNoteEditorPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_isSynchronizingEditorReferences || _isRestoringEditorDraft)
        {
            return;
        }

        if (e.PropertyName == nameof(WorkEntryEditorViewModel.SelectedClient))
        {
            RefreshRecentClientEntries();
        }

        if (IsNoteLinkPickerOpen
            && e.PropertyName is nameof(WorkEntryEditorViewModel.SelectedClient)
                or nameof(WorkEntryEditorViewModel.SelectedTicket)
                or nameof(WorkEntryEditorViewModel.ManualClientName))
        {
            RefreshNoteLinkCandidates();
        }

        if (_isPersistingEditorDraft
            || !WorkEntryEditorViewModel.IsEditableProperty(e.PropertyName)
            || !Editor.IsDirty)
        {
            return;
        }

        EditorSaveStatus = "Unsaved";
        _editorDraftTimer.Stop();
        _editorDraftTimer.Start();
    }

    private void HandleEditorDraftTimerTick(object? sender, EventArgs e)
    {
        _editorDraftTimer.Stop();
        PersistEditorRecoveryDraft();
    }

    private void HandleNoteLinkSearchTimerTick(object? sender, EventArgs e)
    {
        _noteLinkSearchTimer.Stop();
        RefreshNoteLinkCandidates();
    }

    private void PersistEditorRecoveryDraft()
    {
        if (!CanWrite || _isPersistingEditorDraft || IsEntryOperationRunning || IsEditorLocked || !Editor.IsDirty)
        {
            return;
        }

        _isPersistingEditorDraft = true;
        EditorSaveStatus = "Backing up draft";
        try
        {
            var draft = BuildEditorRecoveryDraft();
            _repository.SaveEditorDraft(draft);
            _lastEditorSaveAt = draft.UpdatedAt;
            EditorSaveStatus = $"Unsaved - recovery copy {_lastEditorSaveAt.Value:h:mm tt}";
        }
        catch (Exception ex)
        {
            EditorSaveStatus = "Unsaved - recovery failed";
            StatusMessage = $"The editor draft could not be saved: {ex.Message}";
        }
        finally
        {
            _isPersistingEditorDraft = false;
            OnPropertyChanged(nameof(WorkspaceStateLabel));
        }
    }

    private void RestoreEditorDraft()
    {
        if (!CanWrite)
        {
            EditorSaveStatus = "Read-only preview";
            return;
        }

        var draft = _repository.GetEditorDraft();
        if (draft is null)
        {
            EditorSaveStatus = "Saved";
            return;
        }

        if (draft.WorkEntryId > 0 && _repository.GetWorkEntry(draft.WorkEntryId) is null)
        {
            draft.WorkEntryId = 0;
        }

        var client = ResolveEditorClient(draft.ClientId);
        IReadOnlyList<Client> clients = client is null ? [] : [client];
        var ticket = ResolveEditorTicket(draft.TicketId);
        IReadOnlyList<Ticket> tickets = ticket is null ? [] : [ticket];
        _isRestoringEditorDraft = true;
        _isSynchronizingEditorReferences = true;
        try
        {
            Editor.LoadDraft(draft, clients, tickets);
        }
        finally
        {
            _isSynchronizingEditorReferences = false;
            _isRestoringEditorDraft = false;
        }

        if (draft.WorkEntryId > 0)
        {
            _selectedEntry = Entries.FirstOrDefault(entry => entry.Id == draft.WorkEntryId)
                ?? _repository.GetWorkEntry(draft.WorkEntryId);
            OnPropertyChanged(nameof(SelectedEntry));
        }

        _pendingFollowUpSource = draft.PendingFollowUpSourceId is > 0
            ? _repository.GetWorkEntry(draft.PendingFollowUpSourceId.Value)
            : null;

        SyncEditorClientFilterText(Editor.SelectedClient?.DisplayName ?? string.Empty);
        RefreshEditorTickets(draft.TicketId);
        RefreshRecentClientEntries();
        RefreshRelatedNotes();
        _lastEditorSaveAt = draft.UpdatedAt;
        EditorSaveStatus = $"Unsaved - recovered {draft.UpdatedAt:h:mm tt}";
        StatusMessage = "Recovered the last autosaved editor draft.";
        RaiseEditorStateProperties();
    }

    private void ClearPersistedEditorDraft()
    {
        _editorDraftTimer.Stop();
        _repository.ClearEditorDraft();
        _lastEditorSaveAt = DateTime.Now;
        EditorSaveStatus = $"Saved {_lastEditorSaveAt.Value:h:mm tt}";
    }

    private void PersistEditorDraftBeforeExit()
    {
        _editorDraftTimer.Stop();
        if (!CanWrite || !Editor.IsDirty)
        {
            return;
        }

        try
        {
            _repository.SaveEditorDraft(BuildEditorRecoveryDraft());
        }
        catch
        {
        }
    }

    private bool TrySaveEditorRecoveryDraftForForcedSignOut(out string result)
    {
        _editorDraftTimer.Stop();
        if (!CanWrite)
        {
            result = "TechBench could not save a recovery draft because this session is read-only.";
            return false;
        }

        if (!Editor.IsDirty)
        {
            result = "TechBench closed safely; there were no unsaved entry changes.";
            return true;
        }

        try
        {
            var draft = BuildEditorRecoveryDraft();
            _repository.SaveEditorDraft(draft);
            _lastEditorSaveAt = draft.UpdatedAt;
            EditorSaveStatus = $"Unsaved - recovery copy {_lastEditorSaveAt.Value:h:mm tt}";
            result =
                "Unsaved entry work was saved as a server recovery draft before TechBench closed. "
                + "It was not posted to WHD or Sage.";
            return true;
        }
        catch (Exception ex)
        {
            EditorSaveStatus = "Unsaved - recovery failed";
            result =
                "TechBench could not save the current entry recovery draft, so forced sign-out was canceled: "
                + ex.Message;
            return false;
        }
        finally
        {
            OnPropertyChanged(nameof(WorkspaceStateLabel));
        }
    }

    private EditorDraft BuildEditorRecoveryDraft()
    {
        var draft = Editor.BuildDraft();
        draft.PendingFollowUpSourceId = _pendingFollowUpSource?.Id;
        return draft;
    }

    private void RefreshRecentClientEntries()
    {
        RecentClientEntries.Clear();
        if (Editor.SelectedClient is not { Id: > 0 } client)
        {
            RaiseRecentClientProperties();
            return;
        }

        foreach (var entry in _repository.GetWorkEntries(new WorkEntryQuery
                 {
                     ClientId = client.Id,
                     ExcludeId = Editor.Id > 0 ? Editor.Id : null,
                     MaxResults = 5
                 }))
        {
            RecentClientEntries.Add(entry);
        }

        RaiseRecentClientProperties();
    }

    private void RaiseRecentClientProperties()
    {
        OnPropertyChanged(nameof(HasRecentClientEntries));
        OnPropertyChanged(nameof(RecentClientNotesHeader));
    }

    private void RefreshRelatedNotes()
    {
        RelatedNotes.Clear();
        if (Editor.Id > 0)
        {
            foreach (var link in _repository.GetWorkEntryLinks(Editor.Id))
            {
                RelatedNotes.Add(link);
            }
        }

        RaiseNoteLinkProperties();
    }

    private void RefreshNoteLinkCandidates()
    {
        NoteLinkCandidates.Clear();
        if (!IsNoteLinkPickerOpen || Editor.Id <= 0)
        {
            RaiseNoteLinkProperties();
            return;
        }

        var excludedIds = RelatedNotes
            .Select(static link => link.RelatedEntry.Id)
            .Append(Editor.Id)
            .ToHashSet();
        var candidates = new List<WorkEntry>();

        void AddCandidates(IEnumerable<WorkEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (!excludedIds.Add(entry.Id))
                {
                    continue;
                }

                candidates.Add(entry);
            }
        }

        var searchText = NoteLinkSearchText.Trim();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            AddCandidates(_repository.GetWorkEntries(new WorkEntryQuery
            {
                Keyword = searchText,
                ExcludeId = Editor.Id,
                MaxResults = 20
            }));
        }
        else
        {
            if (Editor.SelectedTicket is { Id: > 0 } ticket)
            {
                AddCandidates(_repository.GetWorkEntries(new WorkEntryQuery
                {
                    TicketId = ticket.Id,
                    ExcludeId = Editor.Id,
                    MaxResults = 8
                }));
            }

            if (Editor.SelectedClient is { Id: > 0 } client)
            {
                AddCandidates(_repository.GetWorkEntries(new WorkEntryQuery
                {
                    ClientId = client.Id,
                    ExcludeId = Editor.Id,
                    MaxResults = 12
                }));
            }

            var recentEntries = _repository.GetWorkEntries(new WorkEntryQuery
            {
                ExcludeId = Editor.Id,
                MaxResults = 24
            });
            if (Editor.UseManualClient && !string.IsNullOrWhiteSpace(Editor.ManualClientName))
            {
                var manualClientName = Editor.ManualClientName.Trim();
                AddCandidates(recentEntries.Where(entry =>
                    entry.ClientDisplay.Equals(manualClientName, StringComparison.OrdinalIgnoreCase)));
            }

            AddCandidates(recentEntries);
        }

        foreach (var candidate in candidates.Take(12))
        {
            NoteLinkCandidates.Add(candidate);
        }

        RaiseNoteLinkProperties();
    }

    private void LinkExistingNote(object? parameter)
    {
        if (parameter is not WorkEntry { Id: > 0 } candidate
            || Editor.Id <= 0
            || IsEditorLocked
            || SelectedNoteLinkTypeOption is not { } linkType)
        {
            return;
        }

        _repository.SaveWorkEntryLink(Editor.Id, candidate.Id, linkType.Value);
        IsNoteLinkPickerOpen = false;
        _noteLinkSearchText = string.Empty;
        OnPropertyChanged(nameof(NoteLinkSearchText));
        RefreshRelatedNotes();
        StatusMessage = $"Linked the {candidate.WorkDate:M/d/yyyy} note as {linkType.DisplayName.ToLowerInvariant()}.";
    }

    private void RemoveNoteLink(object? parameter)
    {
        if (parameter is not WorkEntryLink { Id: > 0 } link || IsEditorLocked)
        {
            return;
        }

        _repository.DeleteWorkEntryLink(link.Id);
        RefreshRelatedNotes();
        if (IsNoteLinkPickerOpen)
        {
            RefreshNoteLinkCandidates();
        }

        StatusMessage = "Note link removed.";
    }

    private void OpenRelatedNote(object? parameter)
    {
        var targetId = parameter switch
        {
            WorkEntryLink link => link.RelatedEntry.Id,
            WorkEntry entry => entry.Id,
            _ => 0
        };
        var target = targetId > 0 ? _repository.GetWorkEntry(targetId) : null;
        if (target is null)
        {
            StatusMessage = "The linked note is no longer available.";
            RefreshRelatedNotes();
            return;
        }

        SelectedEntry = target;
        if (Editor.Id != target.Id)
        {
            return;
        }

        CurrentSection = "Today";
        SelectedDate = target.WorkDate;
        StatusMessage = $"Opened the linked {target.WorkDate:M/d/yyyy} note for {target.ClientDisplay}.";
    }

    private void ContinueThisWork()
    {
        var source = Editor.Id > 0 ? _repository.GetWorkEntry(Editor.Id) : null;
        if (source is null)
        {
            return;
        }

        NewEntry();
        if (Editor.Id != 0 || _selectedEntry is not null)
        {
            return;
        }

        _pendingFollowUpSource = source;
        _isSynchronizingEditorReferences = true;
        try
        {
            if (source.ClientId.HasValue && ResolveEditorClient(source.ClientId) is { } client)
            {
                Editor.SelectedClient = client;
                Editor.UseManualClient = false;
                Editor.ManualClientName = string.Empty;
            }
            else
            {
                Editor.SelectedClient = null;
                Editor.UseManualClient = true;
                Editor.ManualClientName = source.ManualClientName ?? source.ClientDisplay;
            }

            Editor.ManualTicketNumber = source.TicketNumberText ?? string.Empty;
            Editor.UseOtherWhdTicket = !source.TicketId.HasValue
                && !string.IsNullOrWhiteSpace(Editor.ManualTicketNumber);
            Editor.Billable = source.Billable;
        }
        finally
        {
            _isSynchronizingEditorReferences = false;
        }

        SyncEditorClientFilterText(Editor.SelectedClient?.DisplayName ?? string.Empty);
        RefreshEditorClientOptions();
        RefreshEditorTickets(source.TicketId);
        RefreshRecentClientEntries();
        RefreshRelatedNotes();
        EditorSaveStatus = "Unsaved";
        RaiseNoteLinkProperties();
        PersistEditorRecoveryDraft();
        StatusMessage = $"Started a follow-up to the {source.WorkDate:M/d/yyyy} note. The link will be created when this note is saved.";
    }

    private void CancelPendingNoteLink()
    {
        if (_pendingFollowUpSource is null)
        {
            return;
        }

        _pendingFollowUpSource = null;
        RaiseNoteLinkProperties();
        if (Editor.IsDirty)
        {
            PersistEditorRecoveryDraft();
        }

        StatusMessage = "The new note will be saved without a follow-up link.";
    }

    private void ResetNoteLinkEditorState(bool clearPendingFollowUp = true)
    {
        _noteLinkSearchTimer.Stop();
        _isNoteLinkPickerOpen = false;
        _noteLinkSearchText = string.Empty;
        if (clearPendingFollowUp)
        {
            _pendingFollowUpSource = null;
        }

        NoteLinkCandidates.Clear();
        RelatedNotes.Clear();
        OnPropertyChanged(nameof(IsNoteLinkPickerOpen));
        OnPropertyChanged(nameof(NoteLinkSearchText));
        RaiseNoteLinkProperties();
    }

    private void RaiseNoteLinkProperties()
    {
        OnPropertyChanged(nameof(PendingFollowUpSource));
        OnPropertyChanged(nameof(HasPendingFollowUp));
        OnPropertyChanged(nameof(ShowRelatedNotesSection));
        OnPropertyChanged(nameof(HasRelatedNotes));
        OnPropertyChanged(nameof(HasNoteLinkCandidates));
        OnPropertyChanged(nameof(RelatedNotesHeader));
        OnPropertyChanged(nameof(NoteLinkPickerButtonLabel));
        ToggleNoteLinkPickerCommand.RaiseCanExecuteChanged();
        LinkExistingNoteCommand.RaiseCanExecuteChanged();
        RemoveNoteLinkCommand.RaiseCanExecuteChanged();
        ContinueThisWorkCommand.RaiseCanExecuteChanged();
        CancelPendingNoteLinkCommand.RaiseCanExecuteChanged();
    }

    private void InsertRecentNote(object? parameter)
    {
        if (parameter is not WorkEntry entry || string.IsNullOrWhiteSpace(entry.Note))
        {
            return;
        }

        var existing = Editor.Note.TrimEnd();
        Editor.Note = string.IsNullOrWhiteSpace(existing)
            ? entry.Note
            : $"{existing}{Environment.NewLine}{Environment.NewLine}{entry.Note}";
        StatusMessage = $"Inserted text from the {entry.WorkDate:M/d/yyyy} client note.";
    }

    private static string NormalizeTagText(string value)
    {
        return string.Join(", ", value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private void StartNewNoteTemplate()
    {
        ManagedNoteTemplate = null;
        TemplateName = string.Empty;
        TemplateCategory = string.Empty;
        TemplateText = string.Empty;
    }

    private bool CanSaveManagedNoteTemplate()
    {
        return CanWrite
            && CanEditManagedNoteTemplate
            && !string.IsNullOrWhiteSpace(TemplateName)
            && !string.IsNullOrWhiteSpace(TemplateText)
            && (ManagedNoteTemplate is null
                || CanManageNoteTemplate(ManagedNoteTemplate));
    }

    private void SaveManagedNoteTemplate()
    {
        if (!CanSaveManagedNoteTemplate())
        {
            return;
        }

        var template = new NoteTemplate
        {
            Id = ManagedNoteTemplate?.Id ?? 0,
            ScopeType = ManagedNoteTemplate?.ScopeType ?? "Organization",
            Name = TemplateName.Trim(),
            Category = TemplateCategory.Trim(),
            TemplateText = TemplateText.Trim()
        };
        var id = _repository.SaveTemplate(template);
        ReloadNoteTemplates(id);
        StatusMessage = $"Saved note template: {template.Name}.";
    }

    private void DeleteManagedNoteTemplate()
    {
        if (ManagedNoteTemplate is not { Id: > 0 } template
            || !CanManageNoteTemplate(template)
            || !_dialogService.Confirm(
                "Delete template",
                $"Delete the note template '{template.Name}'?",
                "Delete",
                "Cancel"))
        {
            return;
        }

        _repository.DeleteTemplate(template.Id);
        ReloadNoteTemplates();
        StartNewNoteTemplate();
        StatusMessage = "Note template deleted.";
    }

    private bool CanManageNoteTemplate(NoteTemplate template) =>
        _currentUser.IsAdmin
        && CanWrite
        && template.ScopeType.Equals(
            "Organization",
            StringComparison.OrdinalIgnoreCase);

    private bool HasPendingTemplateChanges()
    {
        return ManagedNoteTemplate is null
            ? !string.IsNullOrWhiteSpace(TemplateName)
              || !string.IsNullOrWhiteSpace(TemplateCategory)
              || !string.IsNullOrWhiteSpace(TemplateText)
            : !TemplateName.Equals(ManagedNoteTemplate.Name, StringComparison.Ordinal)
              || !TemplateCategory.Equals(ManagedNoteTemplate.Category, StringComparison.Ordinal)
              || !TemplateText.Equals(ManagedNoteTemplate.TemplateText, StringComparison.Ordinal);
    }

    private void ReloadNoteTemplates(int? selectedId = null)
    {
        NoteTemplates.Clear();
        foreach (var template in _repository.GetTemplates())
        {
            NoteTemplates.Add(template);
        }

        ManagedNoteTemplate = selectedId.HasValue
            ? NoteTemplates.FirstOrDefault(template => template.Id == selectedId.Value)
            : null;
    }

    private void DisposeNoteFeatures()
    {
        PersistEditorDraftBeforeExit();
        _editorDraftTimer.Stop();
        _editorDraftTimer.Tick -= HandleEditorDraftTimerTick;
        _noteLinkSearchTimer.Stop();
        _noteLinkSearchTimer.Tick -= HandleNoteLinkSearchTimerTick;
    }

    private void ImportGoogleSheetsCsv()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Google Sheets worklog",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ImportGoogleSheetsCsv(dialog.FileName);
    }

    partial void ImportGoogleSheetsCsv(string path);
}
