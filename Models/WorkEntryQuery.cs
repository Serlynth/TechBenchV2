namespace TechBench.Models;

public sealed class WorkEntryQuery
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int? ClientId { get; set; }
    public int? ExcludeId { get; set; }
    public int? MaxResults { get; set; }
    public string? TicketText { get; set; }
    public PostingStatus? PostingStatus { get; set; }
    public string? Keyword { get; set; }
    public string? Tags { get; set; }
    public FollowUpState? FollowUpState { get; set; }
    public bool OpenFollowUpsOnly { get; set; }
    public bool PendingWhdOnly { get; set; }
    public bool PendingSageOnly { get; set; }
    public bool PendingAnyOnly { get; set; }
}
