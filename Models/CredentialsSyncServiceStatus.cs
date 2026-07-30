namespace TechBench.Models;

/// <summary>Health, queue state, and row counts for server-owned Credentials sync.</summary>
public sealed class CredentialsSyncServiceStatus
{
    public Guid? LatestRequestId { get; init; }
    public string Health { get; init; } = "NeverRun";
    public string Message { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
    public int QueueDepth { get; init; }
    public DateTime? LastRunAt { get; init; }
    public DateTime? LastSuccessfulRunAt { get; init; }
    public int? LastReadCount { get; init; }
    public int? LastSavedCount { get; init; }
    public int? LastStaleCount { get; init; }

    public bool IsActive =>
        IsRunning
        || QueueDepth > 0
        || Health.Equals("Queued", StringComparison.OrdinalIgnoreCase)
        || Health.Equals("Running", StringComparison.OrdinalIgnoreCase);

    public string Summary => string.IsNullOrWhiteSpace(Message)
        ? Health
        : $"{Health}: {Message}";
}
