namespace TechBench.Models;

/// <summary>Result of asking the server service to synchronize Sage customers.</summary>
public sealed class SageSyncRequestResult
{
    public Guid? RequestId { get; init; }
    public bool Accepted { get; init; }
    public string Message { get; init; } = string.Empty;
    public int QueueDepth { get; init; }
    public bool AllowLargeRemoval { get; init; }
    public Guid? ConfirmedRequestId { get; init; }
}
