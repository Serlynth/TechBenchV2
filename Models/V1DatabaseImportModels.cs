namespace TechBench.Models;

public sealed record V1ImportReferenceResolution(
    int MatchedClientCount,
    int UnmatchedClientCount,
    int MatchedTicketCount,
    int UnmatchedTicketCount);

public sealed class V1DatabaseImportPackage
{
    public string SourcePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string FileHash { get; init; } = string.Empty;
    public IReadOnlyList<V1WorkEntryImportRow> WorkEntries { get; init; } = [];
    public IReadOnlyList<V1WorkEntryLinkImportRow> Links { get; init; } = [];
    public IReadOnlyList<V1PostingLogImportRow> PostingLogs { get; init; } = [];
    public bool HasEditorDraft { get; init; }
    public int ExcludedSharedItemCount { get; init; }
    public IReadOnlyDictionary<string, int> ExcludedItemCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
}

public sealed class V1WorkEntryImportRow
{
    public long LegacyId { get; init; }
    public string ContentHash { get; init; } = string.Empty;
    public WorkEntry WorkEntry { get; init; } = new();

    public long? LegacyClientId { get; init; }
    public string? LegacyClientName { get; init; }
    public string? LegacyClientSource { get; init; }
    public string? LegacyClientExternalId { get; init; }
    public string? LegacyClientWhdLocationName { get; init; }
    public string? LegacyClientSageCustomerId { get; init; }
    public string? LegacyClientSageCustomerName { get; init; }

    public long? LegacyTicketId { get; init; }
    public long? LegacyTicketClientId { get; init; }
    public string? LegacyTicketClientName { get; init; }
    public string? LegacyTicketNumber { get; init; }
    public string? LegacyTicketSubject { get; init; }
    public string? LegacyTicketStatus { get; init; }
    public string? LegacyTicketSource { get; init; }
    public string? LegacyTicketExternalId { get; init; }
    public int? LegacyTicketWhdStatusTypeId { get; init; }

    public int? ResolvedClientId { get; set; }
    public int? ResolvedTicketId { get; set; }
}

public sealed class V1WorkEntryLinkImportRow
{
    public long LegacyId { get; init; }
    public string ContentHash { get; init; } = string.Empty;
    public long SourceLegacyWorkEntryId { get; init; }
    public long TargetLegacyWorkEntryId { get; init; }
    public WorkEntryLinkType LinkType { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

public sealed class V1PostingLogImportRow
{
    public long LegacyId { get; init; }
    public string ContentHash { get; init; } = string.Empty;
    public long LegacyWorkEntryId { get; init; }
    public string Destination { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? ExternalReference { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}
