using TechBench.Models;

namespace TechBench.SyncService;

public sealed record WhdServiceConfiguration(
    string BaseUrl,
    string Username,
    WhdAuthenticationMode AuthenticationMode,
    bool AutoSyncEnabled,
    int AutoSyncMinutes,
    DateTimeOffset? CursorUtc)
{
    public bool IsConfigured =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(Username);
}

public sealed record WhdSyncWork(
    Guid WorkId,
    Guid LeaseId,
    string WorkType,
    bool IsFullSync,
    DateTimeOffset? CursorUtc,
    Guid? RequestId,
    DateTimeOffset LeaseExpiresUtc);

public sealed record WhdSyncCounts(
    int TicketsRead,
    int ItemsRead,
    int ItemsSaved);

public sealed record WhdSyncExecutionResult(
    WhdSyncCounts Counts,
    DateTimeOffset? NextCursorUtc,
    string Message);

public sealed record SageSyncConfiguration(string Dsn, string Username)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Dsn)
        && !string.IsNullOrWhiteSpace(Username);
}

public sealed record SageSyncWork(
    Guid WorkId,
    Guid LeaseId,
    DateTimeOffset LeaseExpiresUtc,
    bool AllowLargeRemoval);

public sealed record SageSyncCounts(
    int ReadCount,
    int SavedCount,
    int StaleCount,
    int MatchedCount,
    int ExistingCount,
    bool RequiresLargeRemovalConfirmation,
    string? Message);

public sealed record SageSyncExecutionResult(
    SageSyncCounts Counts,
    string Message);

public sealed record SageSyncCustomer(
    string CustomerId,
    string CustomerName,
    string? ContactName,
    string? Telephone,
    bool IsActive);

public sealed record FireDrillSyncConfiguration(
    string SourcePath,
    bool DailySyncEnabled,
    string DailySyncTime)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SourcePath);
}

public sealed record FireDrillSyncWork(
    Guid WorkId,
    Guid LeaseId,
    DateTimeOffset LeaseExpiresUtc);

public sealed record FireDrillCredentialRow(
    string ClientName,
    string? FireboxIp,
    string? Status,
    string RowHashHex,
    IReadOnlyList<FireDrillCredentialFieldRow> Fields);

public sealed record FireDrillCredentialFieldRow(
    string FieldKey,
    string Label,
    int SortOrder,
    string? Value);

public sealed record CredentialsWorkbookContents(
    IReadOnlyList<FireDrillCredentialRow> Credentials,
    IReadOnlyList<CredentialsClientUserRow>? ClientUsers);

public sealed record CredentialsClientUserRow(
    string SourceKey,
    string ClientName,
    string DisplayName,
    string? RoleDepartment,
    string? Email,
    string? LocationName,
    bool IsActive,
    string RowHashHex,
    IReadOnlyList<CredentialsClientUserAccountRow> Accounts);

public sealed record CredentialsClientUserAccountRow(
    string SourceKey,
    string AccountSystem,
    string RowHashHex,
    IReadOnlyList<FireDrillCredentialFieldRow> Fields);

public sealed record FireDrillSyncCounts(int ReadCount, int SavedCount, int StaleCount);

public sealed record CredentialsClientUserSyncCounts(
    int UserReadCount,
    int UserSavedCount,
    int UserStaleCount,
    int AccountReadCount,
    int AccountSavedCount,
    int AccountStaleCount);

public sealed record FireDrillSyncExecutionResult(
    FireDrillSyncCounts Counts,
    DateTimeOffset SourceModifiedAtUtc,
    string Message);

public sealed record AuthPointMfaWork(
    Guid ChallengeId,
    Guid LeaseId,
    string ProviderLogin,
    string ClientMachine,
    string ActionScope,
    long SecretId,
    DateTimeOffset ExpiresAtUtc);
