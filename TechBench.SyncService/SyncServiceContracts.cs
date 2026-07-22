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
    string? Admin,
    string? CsriAdmin,
    string? FireboxDbCsri,
    string? AuthpointUser,
    string? SslVpnPassword,
    string? AdAuthUser,
    string? AdPassword,
    string? RustPassword);

public sealed record FireDrillSyncCounts(int ReadCount, int SavedCount, int StaleCount);

public sealed record FireDrillSyncExecutionResult(
    FireDrillSyncCounts Counts,
    DateTimeOffset SourceModifiedAtUtc,
    string Message);
