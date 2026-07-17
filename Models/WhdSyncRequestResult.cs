namespace TechBench.Models;

public sealed class WhdSyncRequestResult
{
    public bool Accepted { get; init; }
    public string Message { get; init; } = string.Empty;
    public int QueueDepth { get; init; }
}
