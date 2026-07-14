namespace TechBench.Models;

public sealed class TicketStatusOption
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = "WHD";
    public string? ExternalId { get; set; }
    public int? WhdStatusTypeId { get; set; }
    public bool IsClosed { get; set; }
    public DateTime? LastSyncedAt { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Open" : Name;
}
