using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed class WorkEntryEditorViewModel : ObservableObject
{
    private static readonly HashSet<string> EditablePropertyNames =
    [
        nameof(WorkDate),
        nameof(SelectedClient),
        nameof(UseManualClient),
        nameof(ManualClientName),
        nameof(SelectedTicket),
        nameof(UseOtherWhdTicket),
        nameof(ManualTicketNumber),
        nameof(StartTimeText),
        nameof(EndTimeText),
        nameof(DurationMinutesText),
        nameof(DurationHoursText),
        nameof(DurationMinutePartText),
        nameof(Billable),
        nameof(Note),
        nameof(InternalNote),
        nameof(IncludePersonalNoteInWhd),
        nameof(Tags),
        nameof(FollowUpState),
        nameof(FollowUpDueDate)
    ];

    private int _id;
    private DateTime _workDate = DateTime.Today;
    private Client? _selectedClient;
    private bool _useManualClient;
    private string _manualClientName = string.Empty;
    private Ticket? _selectedTicket;
    private bool _useOtherWhdTicket;
    private string _manualTicketNumber = string.Empty;
    private string _startTimeText = string.Empty;
    private string _endTimeText = string.Empty;
    private string _durationMinutesText = string.Empty;
    private string _durationHoursText = string.Empty;
    private string _durationMinutePartText = string.Empty;
    private bool _isSynchronizingDuration;
    private bool _billable = true;
    private string _note = string.Empty;
    private string _internalNote = string.Empty;
    private bool _includePersonalNoteInWhd;
    private string _tags = string.Empty;
    private FollowUpState _followUpState;
    private DateTime? _followUpDueDate;
    private bool _whdPosted;
    private bool _sagePosted;
    private DateTime? _whdPostedAt;
    private DateTime? _sagePostedAt;
    private string? _sageTicketNumber;
    private PostingStatus _postingStatus = PostingStatus.Draft;
    private string _lastError = string.Empty;
    private bool _modifiedAfterPosting;
    private bool _isDirty;
    private int _dirtyTrackingSuppression;
    private readonly ObservableCollection<TimeOption> _timeOptions;

    public WorkEntryEditorViewModel()
    {
        _timeOptions = BuildTimeOptions();
        TimeOptions = new ReadOnlyObservableCollection<TimeOption>(_timeOptions);
        PropertyChanged += HandleOwnPropertyChanged;
    }

    public ReadOnlyObservableCollection<TimeOption> TimeOptions { get; }

    public int Id
    {
        get => _id;
        set
        {
            if (SetProperty(ref _id, value))
            {
                OnPropertyChanged(nameof(IsExistingEntry));
            }
        }
    }

    public bool IsExistingEntry => Id > 0;

    public DateTime WorkDate
    {
        get => _workDate;
        set => SetProperty(ref _workDate, value.Date);
    }

    public Client? SelectedClient
    {
        get => _selectedClient;
        set
        {
            if (SetProperty(ref _selectedClient, value))
            {
                OnPropertyChanged(nameof(HasClientReference));
            }
        }
    }

    public bool UseManualClient
    {
        get => _useManualClient;
        set
        {
            if (SetProperty(ref _useManualClient, value))
            {
                OnPropertyChanged(nameof(HasClientReference));
            }
        }
    }

    public string ManualClientName
    {
        get => _manualClientName;
        set
        {
            if (SetProperty(ref _manualClientName, value))
            {
                OnPropertyChanged(nameof(HasClientReference));
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
                OnPropertyChanged(nameof(HasNoTicket));
                OnPropertyChanged(nameof(HasSelectedSyncedTicket));
                OnPropertyChanged(nameof(TicketWarningText));
            }
        }
    }

    public bool UseOtherWhdTicket
    {
        get => _useOtherWhdTicket;
        set
        {
            if (SetProperty(ref _useOtherWhdTicket, value))
            {
                OnPropertyChanged(nameof(HasNoTicket));
                OnPropertyChanged(nameof(HasSelectedSyncedTicket));
                OnPropertyChanged(nameof(TicketWarningText));
            }
        }
    }

    public string ManualTicketNumber
    {
        get => _manualTicketNumber;
        set
        {
            if (SetProperty(ref _manualTicketNumber, value))
            {
                OnPropertyChanged(nameof(HasNoTicket));
                OnPropertyChanged(nameof(TicketWarningText));
            }
        }
    }

    public string StartTimeText
    {
        get => _startTimeText;
        set
        {
            EnsureTimeOption(value);
            if (SetProperty(ref _startTimeText, value))
            {
                UpdateDurationFromTimes();
                OnPropertyChanged(nameof(IsDurationCalculated));
            }
        }
    }

    public string EndTimeText
    {
        get => _endTimeText;
        set
        {
            EnsureTimeOption(value);
            if (SetProperty(ref _endTimeText, value))
            {
                UpdateDurationFromTimes();
                OnPropertyChanged(nameof(IsDurationCalculated));
            }
        }
    }

    public string DurationMinutesText
    {
        get => _durationMinutesText;
        set
        {
            SetProperty(ref _durationMinutesText, value);
            if (!_isSynchronizingDuration)
            {
                SynchronizeDurationPartsFromTotal(value);
            }
        }
    }

    public string DurationHoursText
    {
        get => _durationHoursText;
        set
        {
            if (SetProperty(ref _durationHoursText, value))
            {
                SynchronizeDurationTotalFromParts();
            }
        }
    }

    public string DurationMinutePartText
    {
        get => _durationMinutePartText;
        set
        {
            if (SetProperty(ref _durationMinutePartText, value))
            {
                SynchronizeDurationTotalFromParts();
            }
        }
    }

    public bool Billable
    {
        get => _billable;
        set => SetProperty(ref _billable, value);
    }

    private static ObservableCollection<TimeOption> BuildTimeOptions()
    {
        var options = new ObservableCollection<TimeOption>
        {
            new(string.Empty, "Not set", -1)
        };

        for (var minutes = 0; minutes < 24 * 60; minutes += 15)
        {
            options.Add(CreateTimeOption(minutes));
        }

        return options;
    }

    private void EnsureTimeOption(string value)
    {
        if (!TryParseClockTime(value, out var time))
        {
            return;
        }

        var minutes = (int)time.TotalMinutes;
        if (_timeOptions.Any(option => option.MinutesSinceMidnight == minutes))
        {
            return;
        }

        var insertIndex = 1;
        while (insertIndex < _timeOptions.Count
               && _timeOptions[insertIndex].MinutesSinceMidnight < minutes)
        {
            insertIndex++;
        }

        _timeOptions.Insert(insertIndex, CreateTimeOption(minutes));
    }

    private static TimeOption CreateTimeOption(int minutes)
    {
        var time = TimeSpan.FromMinutes(minutes);
        return new TimeOption(
            time.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
            DateTime.Today.Add(time).ToString("h:mm tt", CultureInfo.CurrentCulture),
            minutes);
    }

    public string Note
    {
        get => _note;
        set
        {
            if (SetProperty(ref _note, value))
            {
                OnPropertyChanged(nameof(NoteCharacterCount));
                OnPropertyChanged(nameof(NoteWordCount));
                OnPropertyChanged(nameof(NoteCountLabel));
            }
        }
    }

    public string InternalNote
    {
        get => _internalNote;
        set => SetProperty(ref _internalNote, value);
    }

    public bool IncludePersonalNoteInWhd
    {
        get => _includePersonalNoteInWhd;
        set => SetProperty(ref _includePersonalNoteInWhd, value);
    }

    public string Tags
    {
        get => _tags;
        set => SetProperty(ref _tags, value);
    }

    public FollowUpState FollowUpState
    {
        get => _followUpState;
        set
        {
            if (SetProperty(ref _followUpState, value))
            {
                OnPropertyChanged(nameof(HasOpenFollowUp));
            }
        }
    }

    public DateTime? FollowUpDueDate
    {
        get => _followUpDueDate;
        set => SetProperty(ref _followUpDueDate, value?.Date);
    }

    public int NoteCharacterCount => Note.Length;
    public int NoteWordCount => Note.Split(
        (char[]?)null,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    public string NoteCountLabel => $"{NoteWordCount} words | {NoteCharacterCount} characters";
    public bool HasOpenFollowUp => FollowUpState is FollowUpState.FollowUp or FollowUpState.Waiting;
    public string InternalNoteHeader => string.IsNullOrWhiteSpace(InternalNote)
        ? "Personal Note (Markdown)"
        : "Personal Note (Markdown, contains text)";

    public bool WhdPosted
    {
        get => _whdPosted;
        set => SetProperty(ref _whdPosted, value);
    }

    public bool SagePosted
    {
        get => _sagePosted;
        set => SetProperty(ref _sagePosted, value);
    }

    public DateTime? WhdPostedAt
    {
        get => _whdPostedAt;
        set => SetProperty(ref _whdPostedAt, value);
    }

    public DateTime? SagePostedAt
    {
        get => _sagePostedAt;
        set => SetProperty(ref _sagePostedAt, value);
    }

    public string? SageTicketNumber
    {
        get => _sageTicketNumber;
        set => SetProperty(ref _sageTicketNumber, value);
    }

    public PostingStatus PostingStatus
    {
        get => _postingStatus;
        set
        {
            if (SetProperty(ref _postingStatus, value))
            {
                OnPropertyChanged(nameof(PostingStatusLabel));
            }
        }
    }

    public string PostingStatusLabel => PostingStatus switch
    {
        PostingStatus.PostedToWhd => "Posted to WHD",
        PostingStatus.PostedToSage => "Posted to Sage",
        PostingStatus.PostedToBoth => "Posted to Both",
        _ => PostingStatus.ToString()
    };

    public string PostingSuccessMessage
    {
        get
        {
            var posted = new List<string>();

            if (WhdPosted)
            {
                posted.Add(WhdPostedAt.HasValue
                    ? $"Posted to WHD at {WhdPostedAt.Value.ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture)}"
                    : "Posted to WHD");
            }

            if (SagePosted)
            {
                posted.Add(SagePostedAt.HasValue
                    ? $"Posted to Sage at {SagePostedAt.Value.ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture)}"
                    : "Posted to Sage");
            }

            return string.Join("; ", posted);
        }
    }

    public bool HasPostingSuccess => !string.IsNullOrWhiteSpace(PostingSuccessMessage);

    public string LastError
    {
        get => _lastError;
        set
        {
            if (SetProperty(ref _lastError, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(LastError);

    public bool ModifiedAfterPosting
    {
        get => _modifiedAfterPosting;
        set => SetProperty(ref _modifiedAfterPosting, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public bool HasPostedDestination => WhdPosted || SagePosted;
    public bool HasClientReference => UseManualClient
        ? !string.IsNullOrWhiteSpace(ManualClientName)
        : SelectedClient is not null;
    public bool HasNoTicket => UseOtherWhdTicket
        ? !TryParseOtherWhdTicketNumber(ManualTicketNumber, out _)
        : SelectedTicket is null || SelectedTicket.Id <= 0;
    public bool HasSelectedSyncedTicket => !UseOtherWhdTicket && SelectedTicket is { Id: > 0 };
    public bool IsDurationCalculated => TryParseClockTime(StartTimeText, out var start)
        && TryParseClockTime(EndTimeText, out var end)
        && end >= start;
    public string TicketWarningText => HasNoTicket ? "No Ticket" : string.Empty;

    public void LoadNew(DateTime workDate)
    {
        RunWithoutDirtyTracking(() =>
        {
            Id = 0;
            WorkDate = workDate;
            SelectedClient = null;
            UseManualClient = false;
            ManualClientName = string.Empty;
            SelectedTicket = null;
            UseOtherWhdTicket = false;
            ManualTicketNumber = string.Empty;
            StartTimeText = string.Empty;
            EndTimeText = string.Empty;
            DurationMinutesText = string.Empty;
            Billable = true;
            Note = string.Empty;
            InternalNote = string.Empty;
            IncludePersonalNoteInWhd = false;
            Tags = string.Empty;
            FollowUpState = FollowUpState.None;
            FollowUpDueDate = null;
            WhdPosted = false;
            SagePosted = false;
            WhdPostedAt = null;
            SagePostedAt = null;
            SageTicketNumber = null;
            PostingStatus = PostingStatus.Draft;
            LastError = string.Empty;
            ModifiedAfterPosting = false;
        });
        MarkClean();
    }

    public void LoadFrom(WorkEntry entry, IReadOnlyList<Client> clients, IReadOnlyList<Ticket> tickets)
    {
        RunWithoutDirtyTracking(() =>
        {
            Id = entry.Id;
            WorkDate = entry.WorkDate;
            SelectedClient = entry.ClientId.HasValue
                ? clients.FirstOrDefault(client => client.Id == entry.ClientId.Value)
                : null;
            ManualClientName = entry.ManualClientName ?? string.Empty;
            UseManualClient = SelectedClient is null && !string.IsNullOrWhiteSpace(ManualClientName);
            SelectedTicket = tickets.FirstOrDefault(ticket => ticket.Id == entry.TicketId);
            ManualTicketNumber = entry.TicketNumberText ?? string.Empty;
            UseOtherWhdTicket = !entry.TicketId.HasValue && !string.IsNullOrWhiteSpace(ManualTicketNumber);
            StartTimeText = entry.HasTimeRange ? entry.StartTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture) : string.Empty;
            EndTimeText = entry.HasTimeRange ? entry.EndTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture) : string.Empty;
            DurationMinutesText = entry.DurationMinutes > 0 ? entry.DurationMinutes.ToString(CultureInfo.InvariantCulture) : string.Empty;
            Billable = entry.Billable;
            Note = entry.Note;
            InternalNote = entry.InternalNote ?? string.Empty;
            IncludePersonalNoteInWhd = entry.IncludePersonalNoteInWhd;
            Tags = entry.Tags;
            FollowUpState = entry.FollowUpState;
            FollowUpDueDate = entry.FollowUpDueDate;
            WhdPosted = entry.WhdPosted;
            SagePosted = entry.SagePosted;
            WhdPostedAt = entry.WhdPostedAt;
            SagePostedAt = entry.SagePostedAt;
            SageTicketNumber = entry.SageTicketNumber;
            PostingStatus = entry.PostingStatus;
            LastError = entry.LastError ?? string.Empty;
            ModifiedAfterPosting = entry.ModifiedAfterPosting;
        });
        MarkClean();
    }

    public void MarkClean()
    {
        IsDirty = false;
    }

    public EditorDraft BuildDraft()
    {
        return new EditorDraft
        {
            WorkEntryId = Id,
            WorkDate = WorkDate,
            ClientId = UseManualClient ? null : SelectedClient?.Id,
            UseManualClient = UseManualClient,
            ManualClientName = ManualClientName,
            TicketId = !UseOtherWhdTicket && SelectedTicket is { Id: > 0 } ? SelectedTicket.Id : null,
            ManualTicketNumber = UseOtherWhdTicket ? ManualTicketNumber : string.Empty,
            StartTimeText = StartTimeText,
            EndTimeText = EndTimeText,
            DurationMinutesText = DurationMinutesText,
            Billable = Billable,
            Note = Note,
            InternalNote = InternalNote,
            IncludePersonalNoteInWhd = IncludePersonalNoteInWhd,
            Tags = Tags,
            FollowUpState = FollowUpState,
            FollowUpDueDate = FollowUpDueDate,
            UpdatedAt = DateTime.Now
        };
    }

    public void LoadDraft(EditorDraft draft, IReadOnlyList<Client> clients, IReadOnlyList<Ticket> tickets)
    {
        RunWithoutDirtyTracking(() =>
        {
            Id = draft.WorkEntryId;
            WorkDate = draft.WorkDate;
            SelectedClient = draft.ClientId.HasValue
                ? clients.FirstOrDefault(client => client.Id == draft.ClientId.Value)
                : null;
            UseManualClient = draft.UseManualClient;
            ManualClientName = draft.ManualClientName;
            SelectedTicket = draft.TicketId.HasValue
                ? tickets.FirstOrDefault(ticket => ticket.Id == draft.TicketId.Value)
                : null;
            ManualTicketNumber = draft.ManualTicketNumber;
            UseOtherWhdTicket = !draft.TicketId.HasValue && !string.IsNullOrWhiteSpace(ManualTicketNumber);
            StartTimeText = draft.StartTimeText;
            EndTimeText = draft.EndTimeText;
            DurationMinutesText = draft.DurationMinutesText;
            Billable = draft.Billable;
            Note = draft.Note;
            InternalNote = draft.InternalNote;
            IncludePersonalNoteInWhd = draft.IncludePersonalNoteInWhd;
            Tags = draft.Tags;
            FollowUpState = draft.FollowUpState;
            FollowUpDueDate = draft.FollowUpDueDate;
        });
        IsDirty = true;
    }

    public void RunWithoutDirtyTracking(Action action)
    {
        _dirtyTrackingSuppression++;
        try
        {
            action();
        }
        finally
        {
            _dirtyTrackingSuppression--;
        }
    }

    public bool TryBuildEntry(out WorkEntry entry, out string validationMessage)
    {
        entry = new WorkEntry();
        validationMessage = string.Empty;

        var manualClientName = UseManualClient ? ManualClientName.Trim() : string.Empty;
        if ((!UseManualClient && SelectedClient is null)
            || (UseManualClient && string.IsNullOrWhiteSpace(manualClientName)))
        {
            validationMessage = "Select a synced client or type a client/contact name before saving.";
            return false;
        }

        var otherWhdTicketId = 0;
        if (UseOtherWhdTicket
            && !TryParseOtherWhdTicketNumber(ManualTicketNumber, out otherWhdTicketId))
        {
            validationMessage = "Enter a valid numeric Web Help Desk ticket number.";
            return false;
        }

        if (!TryResolveTimeAndDuration(out var start, out var end, out var durationMinutes, out var hasTimeRange, out validationMessage))
        {
            return false;
        }

        entry = new WorkEntry
        {
            Id = Id,
            WorkDate = WorkDate.Date,
            ClientId = UseManualClient ? null : SelectedClient?.Id,
            ManualClientName = string.IsNullOrWhiteSpace(manualClientName) ? null : manualClientName,
            TicketId = !UseOtherWhdTicket && SelectedTicket is { Id: > 0 } ? SelectedTicket.Id : null,
            TicketNumberText = UseOtherWhdTicket
                ? otherWhdTicketId.ToString(CultureInfo.InvariantCulture)
                : null,
            HasTimeRange = hasTimeRange,
            StartTime = start,
            EndTime = end,
            DurationMinutes = durationMinutes,
            Billable = Billable,
            Note = Note.Trim(),
            InternalNote = string.IsNullOrWhiteSpace(InternalNote) ? null : InternalNote,
            IncludePersonalNoteInWhd = IncludePersonalNoteInWhd && !string.IsNullOrWhiteSpace(InternalNote),
            Tags = WorkEntryTags.Normalize(Tags),
            FollowUpState = FollowUpState,
            FollowUpDueDate = FollowUpState is FollowUpState.FollowUp or FollowUpState.Waiting
                ? FollowUpDueDate?.Date
                : null,
            WhdPosted = WhdPosted,
            WhdPostedAt = WhdPostedAt,
            SagePosted = SagePosted,
            SagePostedAt = SagePostedAt,
            SageTicketNumber = SageTicketNumber,
            PostingStatus = PostingStatus,
            LastError = string.IsNullOrWhiteSpace(LastError) ? null : LastError.Trim()
        };

        return true;
    }

    private static bool TryParseOtherWhdTicketNumber(string? value, out int ticketId)
    {
        var normalized = value?.Trim();
        if (normalized?.StartsWith("WHD-", StringComparison.OrdinalIgnoreCase) == true)
        {
            normalized = normalized[4..];
        }

        return int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out ticketId)
            && ticketId > 0;
    }

    public static bool IsEditableProperty(string? propertyName)
    {
        return propertyName is not null && EditablePropertyNames.Contains(propertyName);
    }

    private bool TryResolveTimeAndDuration(
        out TimeSpan start,
        out TimeSpan end,
        out int durationMinutes,
        out bool hasTimeRange,
        out string validationMessage)
    {
        validationMessage = string.Empty;
        start = TimeSpan.Zero;
        end = TimeSpan.Zero;
        durationMinutes = 0;
        hasTimeRange = false;

        var hasStart = !string.IsNullOrWhiteSpace(StartTimeText);
        var hasEnd = !string.IsNullOrWhiteSpace(EndTimeText);
        var hasDuration = !string.IsNullOrWhiteSpace(DurationHoursText)
            || !string.IsNullOrWhiteSpace(DurationMinutePartText);

        if (!hasStart && !hasEnd && !hasDuration)
        {
            validationMessage = "Enter either start/end times or a duration in hours and minutes.";
            return false;
        }

        if (hasStart != hasEnd)
        {
            validationMessage = "Enter both start and end time, or leave both blank and enter a duration.";
            return false;
        }

        if (hasDuration)
        {
            if (!TryParseDurationPart(DurationHoursText, int.MaxValue / 60, out var durationHours)
                || !TryParseDurationPart(DurationMinutePartText, 59, out var durationMinutePart))
            {
                validationMessage = "Duration hours must be zero or greater, and minutes must be from 0 to 59.";
                return false;
            }

            durationMinutes = (durationHours * 60) + durationMinutePart;
            if (durationMinutes <= 0)
            {
                validationMessage = "Duration must be greater than zero.";
                return false;
            }
        }

        if (!hasStart && !hasEnd)
        {
            return true;
        }

        if (!TryParseClockTime(StartTimeText, out start))
        {
            validationMessage = "Start time must be a valid time, such as 08:30.";
            return false;
        }

        if (!TryParseClockTime(EndTimeText, out end))
        {
            validationMessage = "End time must be a valid time, such as 09:15.";
            return false;
        }

        if (end < start)
        {
            validationMessage = "End time must be after start time.";
            return false;
        }

        hasTimeRange = true;
        durationMinutes = (int)Math.Round((end - start).TotalMinutes, MidpointRounding.AwayFromZero);

        if (durationMinutes <= 0)
        {
            validationMessage = "Duration must be greater than zero minutes.";
            return false;
        }

        return true;
    }

    private void UpdateDurationFromTimes()
    {
        if (TryParseClockTime(StartTimeText, out var start)
            && TryParseClockTime(EndTimeText, out var end)
            && end >= start)
        {
            DurationMinutesText = ((int)Math.Round((end - start).TotalMinutes, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        }
    }

    private static bool TryParseClockTime(string value, out TimeSpan time)
    {
        time = default;
        var text = value.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (TryParseCompactClockTime(text, out time))
        {
            return true;
        }

        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedDate)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsedDate))
        {
            time = parsedDate.TimeOfDay;
            return time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
        }

        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out time))
        {
            return time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
        }

        return false;
    }

    private static bool TryParseCompactClockTime(string text, out TimeSpan time)
    {
        time = default;
        var compact = text.Trim();
        var isPm = compact.EndsWith("pm", StringComparison.OrdinalIgnoreCase);
        var isAm = compact.EndsWith("am", StringComparison.OrdinalIgnoreCase);
        if (isPm || isAm)
        {
            compact = compact[..^2].Trim();
        }

        compact = compact.Replace(":", string.Empty, StringComparison.Ordinal);
        if (compact.Length is < 1 or > 4 || compact.Any(static character => !char.IsDigit(character)))
        {
            return false;
        }

        var hoursText = compact.Length <= 2 ? compact : compact[..^2];
        var minutesText = compact.Length <= 2 ? "0" : compact[^2..];
        if (!int.TryParse(hoursText, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(minutesText, CultureInfo.InvariantCulture, out var minutes)
            || minutes > 59)
        {
            return false;
        }

        if (isPm && hours is >= 1 and < 12)
        {
            hours += 12;
        }
        else if (isAm && hours == 12)
        {
            hours = 0;
        }

        if (hours is < 0 or > 23)
        {
            return false;
        }

        time = new TimeSpan(hours, minutes, 0);
        return true;
    }

    private void SynchronizeDurationPartsFromTotal(string totalMinutesText)
    {
        _isSynchronizingDuration = true;
        try
        {
            if (int.TryParse(totalMinutesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalMinutes)
                && totalMinutes >= 0)
            {
                DurationHoursText = (totalMinutes / 60).ToString(CultureInfo.InvariantCulture);
                DurationMinutePartText = (totalMinutes % 60).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                DurationHoursText = string.Empty;
                DurationMinutePartText = string.Empty;
            }
        }
        finally
        {
            _isSynchronizingDuration = false;
        }
    }

    private void SynchronizeDurationTotalFromParts()
    {
        if (_isSynchronizingDuration)
        {
            return;
        }

        _isSynchronizingDuration = true;
        try
        {
            if (string.IsNullOrWhiteSpace(DurationHoursText)
                && string.IsNullOrWhiteSpace(DurationMinutePartText))
            {
                DurationMinutesText = string.Empty;
                return;
            }

            if (!TryParseDurationPart(DurationHoursText, int.MaxValue / 60, out var durationHours)
                || !TryParseDurationPart(DurationMinutePartText, 59, out var durationMinutePart))
            {
                DurationMinutesText = string.Empty;
                return;
            }

            DurationMinutesText = ((durationHours * 60) + durationMinutePart).ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _isSynchronizingDuration = false;
        }
    }

    private static bool TryParseDurationPart(string value, int maximum, out int result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = 0;
            return true;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
            && result >= 0
            && result <= maximum;
    }

    private void HandleOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WhdPosted) or nameof(SagePosted) or nameof(WhdPostedAt) or nameof(SagePostedAt))
        {
            OnPropertyChanged(nameof(PostingStatusLabel));
            OnPropertyChanged(nameof(PostingSuccessMessage));
            OnPropertyChanged(nameof(HasPostingSuccess));
            OnPropertyChanged(nameof(HasPostedDestination));
        }

        if (e.PropertyName == nameof(InternalNote))
        {
            OnPropertyChanged(nameof(InternalNoteHeader));
        }

        if (_dirtyTrackingSuppression == 0
            && IsEditableProperty(e.PropertyName))
        {
            IsDirty = true;
        }
    }

}
