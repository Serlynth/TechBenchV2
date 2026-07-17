namespace TechBench.Models;

public sealed record CurrentUserContext(
    byte[] UserSid,
    string LoginName,
    string DisplayName,
    Guid DatabaseInstanceId,
    int SchemaVersion,
    DateTime ServerUtc,
    bool IsTechnician,
    bool IsManager,
    bool IsAdmin,
    bool IsSyncOperator)
{
    public bool CanManageClients => IsAdmin;

    public bool CanRunSharedSync => IsAdmin;

    public bool CanManageSharedConfiguration => IsAdmin;
}
