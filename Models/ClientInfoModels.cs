namespace TechBench.Models;

public sealed record ClientInfoClientSummary
{
    public int ClientId { get; init; }
    public string ClientName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public string ReviewStatus { get; init; } = "Unverified";
    public string CutoverState { get; init; } = "NotStarted";
    public bool IsLive { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public byte[]? RowVersion { get; init; }
    public long LocationCount { get; init; }
    public long PersonCount { get; init; }
    public long ResourceCount { get; init; }
    public long CredentialCount { get; init; }

    public string InternalIdLabel => $"ID {ClientId}";
    public string CountLabel =>
        $"{LocationCount} locations · {PersonCount} people · {ResourceCount} systems · {CredentialCount} credentials";
}

public sealed record ClientInfoProfile
{
    public int ClientId { get; init; }
    public string ClientName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public string WhdContactName { get; init; } = string.Empty;
    public string WhdContactEmail { get; init; } = string.Empty;
    public string WhdPhone { get; init; } = string.Empty;
    public string WhdAddress { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string ReviewStatus { get; init; } = "Unverified";
    public bool IsLive { get; init; }
    public DateTime? LastVerifiedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public byte[]? RowVersion { get; init; }
    public string CutoverState { get; init; } = "NotStarted";
    public byte[]? CutoverRowVersion { get; init; }
}

public sealed record ClientInfoLocation
{
    public long LocationId { get; init; }
    public int ClientId { get; init; }
    public string LocalKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string LocationType { get; init; } = string.Empty;
    public string Address1 { get; init; } = string.Empty;
    public string Address2 { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string StateProvince { get; init; } = string.Empty;
    public string PostalCode { get; init; } = string.Empty;
    public string MainPhone { get; init; } = string.Empty;
    public string TimeZoneId { get; init; } = string.Empty;
    public bool IsPrimary { get; init; }
    public string ReviewStatus { get; init; } = "Unverified";
    public bool IsActive { get; init; } = true;
    public DateTime? LastVerifiedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public byte[]? RowVersion { get; init; }

    public string AddressLabel => string.Join(
        ", ",
        new[] { Address1, Address2, City, StateProvince, PostalCode }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record ClientInfoPerson
{
    public long PersonId { get; init; }
    public int ClientId { get; init; }
    public long? LocationId { get; init; }
    public string LocationName { get; init; } = string.Empty;
    public string LocalKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string RoleDepartment { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    public string MobilePhone { get; init; } = string.Empty;
    public string ContactType { get; init; } = string.Empty;
    public bool IsPrimary { get; init; }
    public string ReviewStatus { get; init; } = "Unverified";
    public bool IsActive { get; init; } = true;
    public DateTime? LastVerifiedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public byte[]? RowVersion { get; init; }
}

public sealed record ClientInfoResource
{
    public long ResourceId { get; init; }
    public int ClientId { get; init; }
    public long? LocationId { get; init; }
    public string LocationName { get; init; } = string.Empty;
    public long? ParentResourceId { get; init; }
    public long? EquipmentId { get; init; }
    public string LocalKey { get; init; } = string.Empty;
    public string ResourceType { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string AddressOrUrl { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public string ReviewStatus { get; init; } = "Unverified";
    public bool IsActive { get; init; } = true;
    public DateTime? LastVerifiedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public byte[]? RowVersion { get; init; }
    public IReadOnlyList<ClientInfoResourceField> Fields { get; init; } = [];
    public string Category =>
        ClientInfoResourceCategories.Classify(ResourceType);
    public string TypeLabel =>
        ClientInfoResourceCategories.GetTypeLabel(ResourceType);
}

public sealed record ClientInfoResourceField
{
    public long ResourceFieldId { get; init; }
    public long ResourceId { get; init; }
    public string FieldKey { get; init; } = string.Empty;
    public string FieldLabel { get; init; } = string.Empty;
    public string ValueText { get; init; } = string.Empty;
    public string ValueType { get; init; } = "Text";
    public int SortOrder { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public byte[]? RowVersion { get; init; }
}

public sealed record ClientInfoCredential
{
    public long CredentialId { get; init; }
    public int ClientId { get; init; }
    public long? ResourceId { get; init; }
    public long? PersonId { get; init; }
    public string LocalKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string LoginUrl { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public string ReviewStatus { get; init; } = "Unverified";
    public bool IsActive { get; init; } = true;
    public DateTime? LastVerifiedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public byte[]? RowVersion { get; init; }
    public int SecretCount { get; init; }
    public IReadOnlyList<ClientInfoSecretSummary> Secrets { get; init; } = [];
}

public sealed record ClientInfoSecretSummary
{
    public long SecretId { get; init; }
    public long CredentialId { get; init; }
    public string SecretType { get; init; } = string.Empty;
    public string SecretLabel { get; init; } = string.Empty;
    public bool IsCurrent { get; init; }
    public DateTime? LastVerifiedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public byte[]? RowVersion { get; init; }
}

public sealed record RevealedClientInfoSecret
{
    public long SecretId { get; init; }
    public long CredentialId { get; init; }
    public int ClientId { get; init; }
    public string CredentialName { get; init; } = string.Empty;
    public string SecretType { get; init; } = string.Empty;
    public string SecretLabel { get; init; } = string.Empty;
    public string SecretValue { get; init; } = string.Empty;
    public byte[]? RowVersion { get; init; }
}

public sealed record ClientInfoFact
{
    public long FactId { get; init; }
    public int ClientId { get; init; }
    public string LocalKey { get; init; } = string.Empty;
    public string SectionName { get; init; } = string.Empty;
    public string FieldLabel { get; init; } = string.Empty;
    public string ValueText { get; init; } = string.Empty;
    public string ValueType { get; init; } = "Text";
    public string ReviewStatus { get; init; } = "Unverified";
    public int SortOrder { get; init; }
    public bool IsActive { get; init; } = true;
    public DateTime? LastVerifiedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public byte[]? RowVersion { get; init; }
}

public sealed record ClientInfoSnapshot
{
    public ClientInfoProfile Profile { get; init; } = new();
    public IReadOnlyList<ClientInfoLocation> Locations { get; init; } = [];
    public IReadOnlyList<ClientInfoPerson> People { get; init; } = [];
    public IReadOnlyList<ClientInfoResource> Resources { get; init; } = [];
    public IReadOnlyList<ClientInfoCredential> Credentials { get; init; } = [];
    public IReadOnlyList<ClientInfoFact> Facts { get; init; } = [];
    public IReadOnlyList<ClientInfoImportBatch> ImportBatches { get; init; } = [];
}

public sealed record ClientInfoImportBatch
{
    public Guid BatchId { get; init; }
    public int ClientId { get; init; }
    public string ClientName { get; init; } = string.Empty;
    public string TemplateVersion { get; init; } = string.Empty;
    public Guid WorkbookId { get; init; }
    public string State { get; init; } = "Draft";
    public string Message { get; init; } = string.Empty;
    public DateTime? CreatedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public DateTime? ApprovedAtUtc { get; init; }
    public DateTime? PromotedAtUtc { get; init; }
    public byte[]? RowVersion { get; init; }
    public int RecordCount { get; init; }
    public int SecretCount { get; init; }
    public int SecretMatchCount { get; init; }
    public int SecretMismatchCount { get; init; }
    public int SecretWorkbookOnlyCount { get; init; }
    public int BlockingIssueCount { get; init; }
    public int WarningCount { get; init; }
    public IReadOnlyList<ClientInfoImportIssue> Issues { get; init; } = [];
}

public sealed record ClientInfoImportIssue
{
    public long IssueId { get; init; }
    public long? ImportRecordId { get; init; }
    public string Severity { get; init; } = string.Empty;
    public string IssueCode { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool IsResolved { get; init; }
    public string ResolutionNote { get; init; } = string.Empty;
    public DateTime? ResolvedAtUtc { get; init; }
    public byte[]? RowVersion { get; init; }
}

public sealed record ClientInfoImportRecord(
    string RecordType,
    string LocalKey,
    string? ParentLocalKey,
    string PayloadJson,
    string SourceSheet,
    int SourceRow,
    string ReviewStatus);

public sealed record ClientInfoImportSecret(
    string CredentialLocalKey,
    string SecretType,
    string SecretLabel,
    string SecretValue);

public sealed record ClientInfoWorkbookPackage
{
    public string TemplateVersion { get; init; } = string.Empty;
    public Guid WorkbookId { get; init; }
    public int ClientId { get; init; }
    public string ClientName { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public DateTime? SourceModifiedAtUtc { get; init; }
    public byte[] ContentSha256 { get; init; } = [];
    public IReadOnlyList<ClientInfoImportRecord> Records { get; init; } = [];
    public IReadOnlyList<ClientInfoImportSecret> Secrets { get; init; } = [];
}
