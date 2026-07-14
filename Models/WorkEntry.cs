namespace TechBench.Models;

public sealed class WorkEntry
{
    public int Id { get; set; }
    public DateTime WorkDate { get; set; } = DateTime.Today;
    public int? ClientId { get; set; }
    public string? ManualClientName { get; set; }
    public int? TicketId { get; set; }
    public string? TicketNumberText { get; set; }
    public bool HasTimeRange { get; set; } = true;
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public int DurationMinutes { get; set; }
    public bool Billable { get; set; } = true;
    public string Note { get; set; } = string.Empty;
    public string? InternalNote { get; set; }
    public string Tags { get; set; } = string.Empty;
    public FollowUpState FollowUpState { get; set; }
    public DateTime? FollowUpDueDate { get; set; }
    public bool WhdPosted { get; set; }
    public DateTime? WhdPostedAt { get; set; }
    public bool SagePosted { get; set; }
    public DateTime? SagePostedAt { get; set; }
    public string? SageTicketNumber { get; set; }
    public PostingStatus PostingStatus { get; set; } = PostingStatus.Draft;
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public string ClientName { get; set; } = string.Empty;
    public string? TicketNumber { get; set; }
    public string? TicketSubject { get; set; }
    public string? SearchSnippet { get; set; }
    public bool IsSelectedForBatch { get; set; }

    public bool HasTicket => TicketId.HasValue || !string.IsNullOrWhiteSpace(TicketNumberText);
    public string ClientDisplay => !string.IsNullOrWhiteSpace(ClientName)
        ? ClientName
        : !string.IsNullOrWhiteSpace(ManualClientName)
            ? ManualClientName
            : "(No client)";

    public string TicketDisplay
    {
        get
        {
            var ticket = !string.IsNullOrWhiteSpace(TicketNumber)
                ? TicketNumber
                : TicketNumberText;

            return string.IsNullOrWhiteSpace(ticket) ? "No Ticket" : ticket!;
        }
    }

    public string TimeRange => HasTimeRange ? $"{StartTime:hh\\:mm} - {EndTime:hh\\:mm}" : string.Empty;

    public string DurationLabel
    {
        get
        {
            var hours = DurationMinutes / 60;
            var minutes = DurationMinutes % 60;
            return hours > 0 ? $"{hours}h {minutes:00}m" : $"{minutes}m";
        }
    }

    public string BillableLabel => Billable ? "Billable" : "Non-billable";
    public bool HasTags => !string.IsNullOrWhiteSpace(Tags);
    public bool HasFollowUp => FollowUpState is FollowUpState.FollowUp or FollowUpState.Waiting;
    public bool IsFollowUpOverdue => HasFollowUp
        && FollowUpDueDate.HasValue
        && FollowUpDueDate.Value.Date < DateTime.Today;
    public string FollowUpLabel => FollowUpState switch
    {
        FollowUpState.FollowUp => "Follow-up",
        FollowUpState.Waiting => "Waiting",
        FollowUpState.Completed => "Completed",
        _ => string.Empty
    };
    public string FollowUpBadge => FollowUpDueDate.HasValue && HasFollowUp
        ? $"{FollowUpLabel} {FollowUpDueDate.Value:M/d}"
        : FollowUpLabel;
    public bool NeedsWhdPosting => HasTicket && !WhdPosted;
    public bool ShowWhdBadge => HasTicket || WhdPosted;
    public string WhdBadge => WhdPosted ? "WHD posted" : "WHD pending";
    public bool NeedsSagePosting => Billable && !SagePosted;
    public bool ShowSageBadge => Billable || SagePosted;
    public string SageBadge => SagePosted
        ? string.IsNullOrWhiteSpace(SageTicketNumber) ? "Sage posted" : $"Sage posted #{SageTicketNumber}"
        : string.IsNullOrWhiteSpace(SageTicketNumber) ? "Sage pending" : $"Sage pending #{SageTicketNumber}";
    public string PostingStatusLabel => PostingStatus switch
    {
        PostingStatus.PostedToWhd => "Posted to WHD",
        PostingStatus.PostedToSage => "Posted to Sage",
        PostingStatus.PostedToBoth => "Posted to Both",
        _ => PostingStatus.ToString()
    };

    public bool ModifiedAfterPosting
    {
        get
        {
            var lastPostedAt = new[] { WhdPostedAt, SagePostedAt }
                .Where(static value => value.HasValue)
                .Select(static value => value!.Value)
                .DefaultIfEmpty(DateTime.MaxValue)
                .Max();

            return (WhdPosted || SagePosted) && UpdatedAt > lastPostedAt.AddSeconds(1);
        }
    }

    public string NotePreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Note))
            {
                return "(No work note yet)";
            }

            var flattened = Note.ReplaceLineEndings(" ").Trim();
            return flattened.Length <= 140 ? flattened : $"{flattened[..140]}...";
        }
    }

    public string DisplayPreview => string.IsNullOrWhiteSpace(SearchSnippet)
        ? NotePreview
        : SearchSnippet!;
}
