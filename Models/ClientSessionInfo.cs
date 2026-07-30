namespace TechBench.Models;

public sealed class ClientSessionInfo
{
    public Guid SessionId { get; init; }

    public string LoginName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public bool IsAdmin { get; init; }

    public string MachineName { get; init; } = string.Empty;

    public string ClientVersion { get; init; } = string.Empty;

    public string CurrentSection { get; init; } = string.Empty;

    public bool HasUnsavedChanges { get; init; }

    public bool IsBusy { get; init; }

    public DateTime StartedAt { get; init; }

    public DateTime LastSeenAt { get; init; }

    public bool IsCurrentSession { get; init; }

    public string UserLabel => string.IsNullOrWhiteSpace(DisplayName)
        ? LoginName
        : DisplayName;

    public string ActivityLabel => IsBusy
        ? "Busy"
        : HasUnsavedChanges
            ? "Unsaved work"
            : "Available";

    public string LastSeenLabel => LastSeenAt == default
        ? "Unknown"
        : LastSeenAt.ToString("h:mm:ss tt");
}
