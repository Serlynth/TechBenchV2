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
