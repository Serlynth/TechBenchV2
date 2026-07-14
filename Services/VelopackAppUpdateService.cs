using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace TechBench.Services;

public sealed class VelopackAppUpdateService : IAppUpdateService
{
    public const string ReleaseRepositoryUrl = "https://github.com/Serlynth/TechBench-Releases";

    private readonly UpdateManager _updateManager;
    private UpdateInfo? _pendingUpdate;

    public VelopackAppUpdateService()
    {
        _updateManager = new UpdateManager(
            new GithubSource(ReleaseRepositoryUrl, accessToken: null, prerelease: false));
    }

    public bool IsInstalled => _updateManager.IsInstalled;

    public string CurrentVersion =>
        _updateManager.CurrentVersion?.ToNormalizedString()
        ?? GetAssemblyVersion();

    public async Task<AppUpdateRelease?> CheckForUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsInstalled)
        {
            _pendingUpdate = null;
            return null;
        }

        _pendingUpdate = await _updateManager.CheckForUpdatesAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (_pendingUpdate is null)
        {
            return null;
        }

        var release = _pendingUpdate.TargetFullRelease;
        return new AppUpdateRelease(
            release.Version.ToNormalizedString(),
            release.NotesMarkdown?.Trim() ?? string.Empty);
    }

    public async Task DownloadUpdateAsync(
        IProgress<int> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var update = _pendingUpdate
            ?? throw new InvalidOperationException("Check for an update before downloading it.");

        await _updateManager.DownloadUpdatesAsync(
            update,
            value => progress.Report(value),
            cancellationToken);
    }

    public void BeginApplyAndRestart()
    {
        var update = _pendingUpdate
            ?? throw new InvalidOperationException("No downloaded update is ready to install.");
        var version = update.TargetFullRelease.Version.ToNormalizedString();

        _updateManager.WaitExitThenApplyUpdates(
            update.TargetFullRelease,
            silent: false,
            restart: true,
            restartArgs: ["--updated-to", version]);
    }

    private static string GetAssemblyVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(VelopackAppUpdateService).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Split('+', 2)[0];
        }

        var version = assembly.GetName().Version;
        return version is null
            ? "Unknown"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }
}
