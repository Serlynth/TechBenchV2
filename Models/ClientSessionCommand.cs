namespace TechBench.Models;

public static class ClientSessionCommandTypes
{
    public const string UpdateNotice = "UpdateNotice";

    public const string SignOut = "SignOut";
}

public sealed class ClientSessionCommand
{
    public long CommandId { get; init; }

    public Guid SessionId { get; init; }

    public string CommandType { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string RequestedBy { get; init; } = string.Empty;

    public DateTime RequestedAt { get; init; }
}

public sealed class ClientSessionHeartbeatResult
{
    public DateTime ServerTime { get; init; }

    public ClientSessionCommand? PendingCommand { get; init; }
}
