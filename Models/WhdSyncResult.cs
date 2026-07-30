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
    public string? ContactEmail { get; init; }
    public string? Phone { get; init; }
    public string? Address { get; init; }
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
    public bool IsDeleted { get; init; }
    public DateTimeOffset? LastUpdatedUtc { get; init; }
    public string? AssignedTechnicianExternalId { get; init; }
    public string? AssignedTechnicianName { get; init; }
    public string? AssignedGroupExternalId { get; init; }
    public string? AssignedGroupName { get; init; }
    public WhdSyncedClient Client { get; init; } = new();
}

public sealed class WhdSyncedTechnician
{
    public string ExternalId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Username { get; init; }
    public string? Email { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed class WhdSyncedTechnicianGroup
{
    public string ExternalId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsActive { get; init; } = true;
    public IReadOnlyList<string> TechnicianExternalIds { get; init; } = Array.Empty<string>();
}

public sealed class WhdTicketLookupResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public WhdSyncedTicket? Ticket { get; init; }

    public static WhdTicketLookupResult Failed(string message) => new()
    {
        Success = false,
        Message = message
    };

    public static WhdTicketLookupResult Succeeded(WhdSyncedTicket ticket) => new()
    {
        Success = true,
        Message = $"Found Web Help Desk ticket {ticket.TicketNumber}.",
        Ticket = ticket
    };
}

public sealed class WhdTechNoteLookupResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public int TechNoteId { get; init; }
    public string NoteText { get; init; } = string.Empty;
    public int? DurationMinutes { get; init; }

    public static WhdTechNoteLookupResult Failed(string message) => new()
    {
        Success = false,
        Message = message
    };

    public static WhdTechNoteLookupResult Succeeded(
        int techNoteId,
        string noteText,
        int? durationMinutes) => new()
        {
            Success = true,
            Message = $"Read Web Help Desk TechNote #{techNoteId}.",
            TechNoteId = techNoteId,
            NoteText = noteText,
            DurationMinutes = durationMinutes
        };
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

public sealed class WhdTechnicianSyncResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<WhdSyncedTechnician> Technicians { get; init; } = Array.Empty<WhdSyncedTechnician>();
    public bool IsComplete { get; init; }

    public static WhdTechnicianSyncResult Failed(string message) => new() { Message = message };

    public static WhdTechnicianSyncResult Succeeded(
        string message,
        IReadOnlyList<WhdSyncedTechnician> technicians,
        bool isComplete = true) => new()
        {
            Success = true,
            Message = message,
            Technicians = technicians,
            IsComplete = isComplete
        };
}

public sealed class WhdTechnicianGroupSyncResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<WhdSyncedTechnicianGroup> Groups { get; init; } = Array.Empty<WhdSyncedTechnicianGroup>();
    public bool IsComplete { get; init; }

    public static WhdTechnicianGroupSyncResult Failed(string message) => new() { Message = message };

    public static WhdTechnicianGroupSyncResult Succeeded(
        string message,
        IReadOnlyList<WhdSyncedTechnicianGroup> groups,
        bool isComplete = true) => new()
        {
            Success = true,
            Message = message,
            Groups = groups,
            IsComplete = isComplete
        };
}
