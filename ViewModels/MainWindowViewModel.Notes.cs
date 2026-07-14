using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using Microsoft.Win32;
using TechBench.Data;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly DispatcherTimer _editorAutoSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(1200)
    };
    private bool _isHandlingEditorAutoSave;
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

    public ObservableCollection<WorkEntry> RecentClientEntries { get; } = new();
    public ObservableCollection<FollowUpOption> FollowUpOptions { get; } = new();
    public ObservableCollection<FollowUpOption> SearchFollowUpOptions { get; } = new();

    public RelayCommand SaveAndNewCommand { get; private set; } = null!;
    public RelayCommand InsertRecentNoteCommand { get; private set; } = null!;
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
                DeleteNoteTemplateCommand.RaiseCanExecuteChanged();
            }
        }
    }

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

    public bool HasRecentClientEntries => RecentClientEntries.Count > 0;
    public string RecentClientNotesHeader => $"Recent client notes ({RecentClientEntries.Count})";
    public bool FullTextSearchAvailable => _repository.FullTextSearchAvailable;

    private void InitializeNoteFeatures()
    {
        SaveAndNewCommand = new RelayCommand(_ => SaveAndStartNew(), _ => CanSaveEditor());
        InsertRecentNoteCommand = new RelayCommand(InsertRecentNote, parameter => parameter is WorkEntry && IsEditorEditable);
        ImportGoogleSheetsCommand = new RelayCommand(_ => ImportGoogleSheetsCsv(), _ => !IsEntryOperationRunning);
        NewNoteTemplateCommand = new RelayCommand(_ => StartNewNoteTemplate());
        SaveNoteTemplateCommand = new RelayCommand(_ => SaveManagedNoteTemplate(), _ => CanSaveManagedNoteTemplate());
        DeleteNoteTemplateCommand = new RelayCommand(_ => DeleteManagedNoteTemplate(), _ => ManagedNoteTemplate is { Id: > 0 });

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
        _editorAutoSaveTimer.Tick += HandleEditorAutoSaveTimerTick;
    }

    private void HandleNoteEditorPropertyChanged(PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkEntryEditorViewModel.SelectedClient))
        {
            RefreshRecentClientEntries();
        }

        if (_isRestoringEditorDraft
            || _isHandlingEditorAutoSave
            || !WorkEntryEditorViewModel.IsEditableProperty(e.PropertyName)
            || !Editor.IsDirty)
        {
            return;
        }

        EditorSaveStatus = "Unsaved";
        _editorAutoSaveTimer.Stop();
        _editorAutoSaveTimer.Start();
    }

    private void HandleEditorAutoSaveTimerTick(object? sender, EventArgs e)
    {
        _editorAutoSaveTimer.Stop();
        AutoSaveEditor();
    }

    private void AutoSaveEditor()
    {
        if (_isHandlingEditorAutoSave || IsEntryOperationRunning || IsEditorLocked || !Editor.IsDirty)
        {
            return;
        }

        _isHandlingEditorAutoSave = true;
        EditorSaveStatus = "Saving";
        try
        {
            _repository.SaveEditorDraft(Editor.BuildDraft());
            _lastEditorSaveAt = DateTime.Now;

            var shouldCreateEntry = Editor.Id > 0 || !string.IsNullOrWhiteSpace(Editor.Note);
            if (shouldCreateEntry && Editor.TryBuildEntry(out var entry, out _))
            {
                entry.LastError = null;
                TechBenchRepository.UpdatePostingStatus(entry);
                var id = _repository.SaveWorkEntry(entry);
                Editor.RunWithoutDirtyTracking(() => Editor.Id = id);
                var savedEntry = _repository.GetWorkEntry(id);
                if (savedEntry is not null)
                {
                    _selectedEntry = savedEntry;
                    OnPropertyChanged(nameof(SelectedEntry));
                }

                Editor.MarkClean();
                _repository.ClearEditorDraft();
                RefreshCurrentSectionData();
                EditorSaveStatus = $"Saved {_lastEditorSaveAt.Value:h:mm tt}";
                StatusMessage = $"Autosaved work entry #{id} locally.";
            }
            else
            {
                EditorSaveStatus = $"Draft saved {_lastEditorSaveAt.Value:h:mm tt}";
            }
        }
        catch (Exception ex)
        {
            EditorSaveStatus = "Autosave failed";
            StatusMessage = $"The editor draft could not be saved: {ex.Message}";
        }
        finally
        {
            _isHandlingEditorAutoSave = false;
            OnPropertyChanged(nameof(WorkspaceStateLabel));
        }
    }

    private void RestoreEditorDraft()
    {
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

        var clients = _repository.GetClients(includeInactive: true);
        var tickets = draft.ClientId.HasValue
            ? _repository.GetTickets(draft.ClientId.Value, includeClosed: true)
            : Array.Empty<Ticket>();
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

        SyncEditorClientFilterText(Editor.SelectedClient?.DisplayName ?? string.Empty);
        RefreshEditorClientOptions();
        RefreshEditorTickets(draft.TicketId);
        RefreshRecentClientEntries();
        _lastEditorSaveAt = draft.UpdatedAt;
        EditorSaveStatus = $"Draft recovered {draft.UpdatedAt:h:mm tt}";
        StatusMessage = "Recovered the last autosaved editor draft.";
        RaiseEditorStateProperties();
    }

    private void ClearPersistedEditorDraft()
    {
        _editorAutoSaveTimer.Stop();
        _repository.ClearEditorDraft();
        _lastEditorSaveAt = DateTime.Now;
        EditorSaveStatus = $"Saved {_lastEditorSaveAt.Value:h:mm tt}";
    }

    private void PersistEditorDraftBeforeExit()
    {
        _editorAutoSaveTimer.Stop();
        if (!Editor.IsDirty)
        {
            return;
        }

        try
        {
            _repository.SaveEditorDraft(Editor.BuildDraft());
        }
        catch
        {
        }
    }

    private void SaveAndStartNew()
    {
        var saved = SaveEditor();
        if (saved is null)
        {
            return;
        }

        NewEntry();
        StatusMessage = $"Saved work entry #{saved.Id}. New note ready.";
    }

    private void RefreshRecentClientEntries()
    {
        RecentClientEntries.Clear();
        if (Editor.SelectedClient is not { Id: > 0 } client)
        {
            RaiseRecentClientProperties();
            return;
        }

        foreach (var entry in _repository.GetWorkEntries(new WorkEntryQuery { ClientId = client.Id })
                     .Where(entry => entry.Id != Editor.Id)
                     .Take(5))
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
        return !string.IsNullOrWhiteSpace(TemplateName)
            && !string.IsNullOrWhiteSpace(TemplateText);
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
            || !_dialogService.Confirm("Delete template", $"Delete the note template '{template.Name}'?"))
        {
            return;
        }

        _repository.DeleteTemplate(template.Id);
        ReloadNoteTemplates();
        StartNewNoteTemplate();
        StatusMessage = "Note template deleted.";
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
        _editorAutoSaveTimer.Stop();
        _editorAutoSaveTimer.Tick -= HandleEditorAutoSaveTimerTick;
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
