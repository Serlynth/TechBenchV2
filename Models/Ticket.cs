namespace TechBench.Models;

public sealed class Ticket
{
    public int Id { get; set; }
    public string TicketNumber { get; set; } = string.Empty;
    public int ClientId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
    public string Source { get; set; } = "Manual";
    public string? ExternalId { get; set; }
    public int? WhdStatusTypeId { get; set; }
    public bool IsClosed { get; set; }
    public DateTime? LastSyncedAt { get; set; }

    public string DisplayName => Id == 0
        ? "No Ticket"
        : string.IsNullOrWhiteSpace(Subject)
        ? TicketNumber
        : $"{TicketNumber} - {Subject}";
}
