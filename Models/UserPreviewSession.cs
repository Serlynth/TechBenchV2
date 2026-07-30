namespace TechBench.Models;

public sealed record UserPreviewSession(
    Guid PreviewSessionId,
    Guid ClientInstanceId,
    byte[] UserSid,
    string LoginName,
    string DisplayName,
    bool IsTechnician,
    bool IsManager,
    bool IsAdmin,
    bool IsSyncOperator,
    DateTime ExpiresAtUtc)
{
    public string UserLabel => string.IsNullOrWhiteSpace(DisplayName)
        ? LoginName
        : $"{DisplayName} ({LoginName})";
}
