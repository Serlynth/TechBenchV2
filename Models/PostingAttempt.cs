namespace TechBench.Models;

public enum PostingAttemptStatus
{
    Started,
    Succeeded,
    Failed,
    Unknown,
    Abandoned
}

public sealed class PostingAttempt
{
    public int Id { get; set; }
    public int WorkEntryId { get; set; }
    public string Destination { get; set; } = string.Empty;
    public string AttemptKey { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public PostingAttemptStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ExternalReference { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public sealed record PostingAttemptStartResult(
    bool Started,
    PostingAttempt? Attempt,
    PostingAttempt? OutstandingAttempt);
