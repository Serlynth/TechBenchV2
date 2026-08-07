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
    public string? WhdContactEmail { get; set; }
    public string? WhdPhone { get; set; }
    public string? WhdAddress { get; set; }
    public string? SageCustomerId { get; set; }
    public string? SageCustomerName { get; set; }
    public string? SageContactName { get; set; }
    public string? SageTelephone { get; set; }
    public string MatchStatus { get; set; } = "Unmatched";
    public bool HasWhdIdentity { get; set; }
    public bool HasSageIdentity { get; set; }
    public bool IsClientInfoLive { get; set; }
    public bool HasClientInfoWorkspace { get; set; }
    public string ClientInfoReviewStatus { get; set; } = string.Empty;
    public byte[]? RowVersion { get; set; }

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
    public string InternalIdLabel => $"ID {Id}";
    public string CanonicalCandidateLabel => $"{Name} ({InternalIdLabel})";
    public bool IsExternalSourceLinkEligible => IsActive
        && !IsClientInfoLive
        && !HasClientInfoWorkspace;
    public string CanonicalLinkStatusLabel => !IsClientInfoLive
        ? "Needs review"
        : HasWhdIdentity && HasSageIdentity
            ? "Fully linked"
            : HasWhdIdentity
                ? "WHD linked"
                : HasSageIdentity
                    ? "Sage linked"
                    : "TB only";
    public string ClientInfoStatusLabel => !IsClientInfoLive
        ? "Source record"
        : string.IsNullOrWhiteSpace(ClientInfoReviewStatus)
            ? "Live"
            : $"Live · {ClientInfoReviewStatus}";
    public string ActiveStatus => IsActive ? "Active" : "Inactive";
    public string LastSyncedLabel => LastSyncedAt.HasValue ? LastSyncedAt.Value.ToString("g") : "-";

    public override string ToString() => DisplayName;
}
