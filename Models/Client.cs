namespace TechBench.Models;

public sealed class Client
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = "WHD";
    public string? ExternalId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastSyncedAt { get; set; }
    public string? WhdLocationName { get; set; }
    public string? WhdContactName { get; set; }
    public string? SageCustomerId { get; set; }
    public string? SageCustomerName { get; set; }
    public string? SageContactName { get; set; }
    public string? SageTelephone { get; set; }
    public string MatchStatus { get; set; } = "Unmatched";

    public string DisplayName => IsActive ? Name : $"{Name} (inactive)";
    public string SourceLabel => string.IsNullOrWhiteSpace(Source) ? "WHD" : Source;
    public string ExternalIdLabel => Source.Equals("Sage", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(ExternalId) ? "-" : ExternalId;
    public string WhdLocationLabel => string.IsNullOrWhiteSpace(WhdLocationName) ? "-" : WhdLocationName;
    public string WhdContactLabel => string.IsNullOrWhiteSpace(WhdContactName) ? "-" : WhdContactName;
    public string SageCustomerLabel => string.IsNullOrWhiteSpace(SageCustomerId)
        ? "-"
        : string.IsNullOrWhiteSpace(SageCustomerName)
            ? SageCustomerId
            : $"{SageCustomerId} - {SageCustomerName}";
    public string MatchStatusLabel => string.IsNullOrWhiteSpace(MatchStatus) ? "Unmatched" : MatchStatus;
    public string ActiveStatus => IsActive ? "Active" : "Inactive";
    public string LastSyncedLabel => LastSyncedAt.HasValue ? LastSyncedAt.Value.ToString("g") : "-";

    public override string ToString() => DisplayName;
}
