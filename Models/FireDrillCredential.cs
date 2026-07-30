namespace TechBench.Models;

public sealed record FireDrillCredentialSummary(
    long CredentialId,
    string ClientName,
    string FireboxIp,
    string Status,
    DateTime LastSyncedAtUtc,
    IReadOnlyList<FireDrillCredentialField> Fields)
{
    public string LastSyncedLabel => LastSyncedAtUtc == DateTime.MinValue
        ? "Never"
        : LastSyncedAtUtc.ToLocalTime().ToString("g");
}

public sealed record FireDrillCredential(
    long CredentialId,
    string ClientName,
    string FireboxIp,
    string Status,
    DateTime LastSyncedAtUtc,
    IReadOnlyList<FireDrillCredentialField> Fields);

public sealed record FireDrillCredentialField
{
    public string Label { get; init; } = string.Empty;
    public string FieldName { get; init; } = string.Empty;
    public int SortOrder { get; init; }
    public string Value { get; init; } = string.Empty;
}

public sealed record FireDrillCredentialFieldGroup(
    string Name,
    int SortOrder,
    IReadOnlyList<FireDrillCredentialField> Fields);
