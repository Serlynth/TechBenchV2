namespace TechBench.Models;

public sealed class WhdSyncResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<WhdSyncedTicket> Tickets { get; init; } = Array.Empty<WhdSyncedTicket>();
    public bool IsComplete { get; init; }

    public static WhdSyncResult Failed(string message) => new()
    {
        Success = false,
        Message = message
    };

    public static WhdSyncResult Succeeded(
        string message,
        IReadOnlyList<WhdSyncedTicket> tickets,
        bool isComplete = true) => new()
        {
            Success = true,
            Message = message,
            Tickets = tickets,
            IsComplete = isComplete
        };
}

public sealed class WhdSyncedClient
{
    public string ExternalId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? LocationName { get; init; }
    public string? ContactName { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed class WhdSyncedTicket
{
    public string ExternalId { get; init; } = string.Empty;
    public string TicketNumber { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Status { get; init; } = "Open";
    public int? StatusTypeId { get; init; }
    public bool IsClosed { get; init; }
    public WhdSyncedClient Client { get; init; } = new();
}

public sealed class WhdStatusTypeSyncResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<WhdStatusType> StatusTypes { get; init; } = Array.Empty<WhdStatusType>();

    public static WhdStatusTypeSyncResult Failed(string message) => new()
    {
        Success = false,
        Message = message
    };

    public static WhdStatusTypeSyncResult Succeeded(string message, IReadOnlyList<WhdStatusType> statusTypes) => new()
    {
        Success = true,
        Message = message,
        StatusTypes = statusTypes
    };
}

public sealed class WhdClientSyncResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<WhdSyncedClient> Clients { get; init; } = Array.Empty<WhdSyncedClient>();
    public bool IsComplete { get; init; }

    public static WhdClientSyncResult Failed(string message) => new()
    {
        Success = false,
        Message = message
    };

    public static WhdClientSyncResult Succeeded(
        string message,
        IReadOnlyList<WhdSyncedClient> clients,
        bool isComplete = true) => new()
        {
            Success = true,
            Message = message,
            Clients = clients,
            IsComplete = isComplete
        };
}

public sealed class WhdStatusType
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsClosed { get; init; }
}
