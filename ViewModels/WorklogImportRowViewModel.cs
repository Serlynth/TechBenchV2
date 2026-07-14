using TechBench.Models;

namespace TechBench.ViewModels;

public sealed class WorklogImportRowViewModel : ObservableObject
{
    private Client? _selectedClient;
    private int _durationMinutes;
    private bool _isSelected = true;
    private bool _isDuplicate;

    public int SourceRowNumber { get; init; }
    public DateTime WorkDate { get; init; }
    public string SourceClient { get; init; } = string.Empty;
    public decimal RawDuration { get; init; }
    public string SourceDuration { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;

    public Client? SelectedClient
    {
        get => _selectedClient;
        set
        {
            if (SetProperty(ref _selectedClient, value))
            {
                OnPropertyChanged(nameof(MatchLabel));
                OnPropertyChanged(nameof(IsValid));
            }
        }
    }

    public int DurationMinutes
    {
        get => _durationMinutes;
        set
        {
            if (SetProperty(ref _durationMinutes, value))
            {
                OnPropertyChanged(nameof(IsValid));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsDuplicate
    {
        get => _isDuplicate;
        set
        {
            if (SetProperty(ref _isDuplicate, value))
            {
                OnPropertyChanged(nameof(MatchLabel));
            }
        }
    }

    public bool IsValid => WorkDate != default
        && DurationMinutes > 0
        && !string.IsNullOrWhiteSpace(SourceClient)
        && !string.IsNullOrWhiteSpace(Note);

    public string MatchLabel => IsDuplicate
        ? "Possible duplicate"
        : SelectedClient is null ? "Custom client" : "Matched";

    public WorkEntry BuildEntry()
    {
        return new WorkEntry
        {
            WorkDate = WorkDate.Date,
            ClientId = SelectedClient?.Id,
            ManualClientName = SelectedClient is null ? SourceClient.Trim() : null,
            HasTimeRange = false,
            DurationMinutes = DurationMinutes,
            Billable = true,
            Note = Note.Trim(),
            PostingStatus = PostingStatus.Draft
        };
    }
}
