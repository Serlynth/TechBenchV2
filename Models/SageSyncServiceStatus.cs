namespace TechBench.Models;

/// <summary>Health and queue state for the manual server-side Sage customer sync.</summary>
public sealed class SageSyncServiceStatus
{
    public Guid? LatestRequestId { get; init; }
    public Guid? ConfirmedRequestId { get; init; }
    public string Health { get; init; } = "Unknown";
    public string Message { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
    public int QueueDepth { get; init; }
    public DateTime? LastRunAt { get; init; }
    public DateTime? LastSuccessfulRunAt { get; init; }
    public int? LastReadCount { get; init; }
    public int? LastSavedCount { get; init; }
    public int? LastStaleCount { get; init; }
    public int? ExistingCount { get; init; }
    public bool AllowLargeRemoval { get; init; }
    public bool RequiresLargeRemovalConfirmation { get; init; }

    public string Summary => string.IsNullOrWhiteSpace(Message)
        ? Health
        : $"{Health}: {Message}";
}
