namespace TechBench.Models;

public sealed class EditorDraft
{
    public int WorkEntryId { get; set; }
    public DateTime WorkDate { get; set; } = DateTime.Today;
    public int? ClientId { get; set; }
    public bool UseManualClient { get; set; }
    public string ManualClientName { get; set; } = string.Empty;
    public int? TicketId { get; set; }
    public string ManualTicketNumber { get; set; } = string.Empty;
    public string StartTimeText { get; set; } = string.Empty;
    public string EndTimeText { get; set; } = string.Empty;
    public string DurationMinutesText { get; set; } = string.Empty;
    public bool Billable { get; set; } = true;
    public string Note { get; set; } = string.Empty;
    public string InternalNote { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public FollowUpState FollowUpState { get; set; }
    public DateTime? FollowUpDueDate { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
