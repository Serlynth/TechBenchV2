namespace TechBench.Models;

public sealed record ClientUserSummary(
    long ClientUserId,
    int ClientId,
    string ClientName,
    string DisplayName,
    string RoleDepartment,
    string Email,
    string Phone,
    string LocationName,
    DateTime LastSyncedAtUtc,
    int AccountCount,
    IReadOnlyList<ClientUserAccountGroup> Accounts)
{
    public string LastSyncedLabel => LastSyncedAtUtc == DateTime.MinValue
        ? "Never"
        : LastSyncedAtUtc.ToLocalTime().ToString("g");
}

public sealed record ClientUserAccountGroup(
    string Name,
    int SortOrder,
    IReadOnlyList<FireDrillCredentialField> Fields);
