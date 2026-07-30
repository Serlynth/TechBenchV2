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
    bool IsSyncOperator,
    byte[]? AuthenticatedUserSid = null,
    string? AuthenticatedLoginName = null,
    string? AuthenticatedDisplayName = null,
    bool IsReadOnlyPreview = false,
    Guid? PreviewSessionId = null,
    DateTime? PreviewExpiresAtUtc = null)
{
    public byte[] CredentialOwnerSid => AuthenticatedUserSid ?? UserSid;

    public string AuthenticationLabel =>
        AuthenticatedDisplayName
        ?? AuthenticatedLoginName
        ?? DisplayName;

    public bool CanWrite => !IsReadOnlyPreview;

    public bool CanManageClients => IsAdmin && CanWrite;

    public bool CanRunSharedSync => IsAdmin && CanWrite;

    public bool CanManageSharedConfiguration => IsAdmin && CanWrite;
}
