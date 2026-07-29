namespace TechBench.Models;

/// <summary>Health and queue state reported by the server-side WHD sync service.</summary>
public sealed class WhdSyncServiceStatus
{
    public string Health { get; init; } = "Unknown";
    public string Message { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
    public int QueueDepth { get; init; }
    public DateTime? LastRunAt { get; init; }
    public DateTime? LastSuccessfulRunAt { get; init; }

    public string Summary => IsRunning
        ? "Running: Synchronization is in progress."
        : string.IsNullOrWhiteSpace(Message)
            ? Health
            : $"{Health}: {Message}";
}
