namespace TechBench.Models;

/// <summary>Result of asking the server service to synchronize client credentials.</summary>
public sealed class CredentialsSyncRequestResult
{
    public Guid? RequestId { get; init; }
    public bool Accepted { get; init; }
    public string Message { get; init; } = string.Empty;
    public int QueueDepth { get; init; }
}
