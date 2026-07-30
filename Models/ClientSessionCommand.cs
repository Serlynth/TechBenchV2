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

public sealed class ClientSessionCommandResponse
{
    public long CommandId { get; init; }

    public Guid SessionId { get; init; }

    public string LoginName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string MachineName { get; init; } = string.Empty;

    public string CommandType { get; init; } = string.Empty;

    public string OriginalMessage { get; init; } = string.Empty;

    public string AcknowledgementResult { get; init; } = string.Empty;

    public string ResponseMessage { get; init; } = string.Empty;

    public string RequestedBy { get; init; } = string.Empty;

    public DateTime RequestedAt { get; init; }

    public DateTime AcknowledgedAt { get; init; }

    public string UserLabel => string.IsNullOrWhiteSpace(DisplayName)
        ? LoginName
        : DisplayName;

    public string ResponseLabel => string.IsNullOrWhiteSpace(ResponseMessage)
        ? AcknowledgementResult
        : ResponseMessage;

    public string AcknowledgedAtLabel => AcknowledgedAt == default
        ? "Unknown"
        : AcknowledgedAt.ToLocalTime().ToString("g");
}
