namespace TechBench.Services;

public sealed record AppUpdateRelease(string Version, string ReleaseNotes);

public interface IAppUpdateService
{
    bool IsInstalled { get; }
    string CurrentVersion { get; }

    Task<AppUpdateRelease?> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    Task DownloadUpdateAsync(
        IProgress<int> progress,
        CancellationToken cancellationToken = default);

    Task CleanupDownloadedUpdatesAsync(
        CancellationToken cancellationToken = default);

    void BeginApplyAndRestart();
}
