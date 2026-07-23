using System.IO;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace TechBench.Services;

public sealed class V2AppUpdateService : IAppUpdateService
{
    public const string ReleaseRepositoryUrl =
        "https://github.com/Serlynth/TechBenchV2-Releases";
    public const string ReleaseChannel = "v2";

    private readonly UpdateManager _updateManager;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private UpdateInfo? _pendingUpdate;

    public V2AppUpdateService()
    {
        _updateManager = new UpdateManager(
            new GithubSource(
                ReleaseRepositoryUrl,
                accessToken: null,
                prerelease: true),
            new UpdateOptions
            {
                ExplicitChannel = ReleaseChannel,
                // 5.0.1/5.0.2 were published before the intended 0.5.x numbering
                // was clarified. Permit the one-time move to the corrected line.
                AllowVersionDowngrade = true
            });
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

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
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
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task DownloadUpdateAsync(
        IProgress<int> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var update = _pendingUpdate
            ?? throw new InvalidOperationException(
                "Check for a TechBench V2 update before downloading it.");

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await _updateManager.DownloadUpdatesAsync(
                update,
                value => progress.Report(value),
                cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task CleanupDownloadedUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
        {
            return;
        }

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(
                () =>
                {
                    var packagesDirectory = ResolveInstalledPackagesDirectory();
                    if (packagesDirectory is not null)
                    {
                        CleanupPackageDirectory(packagesDirectory);
                    }
                },
                cancellationToken);
            _pendingUpdate = null;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal string? ResolveInstalledPackagesDirectory()
    {
        if (!IsInstalled || string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return null;
        }

        var contentDirectory = Directory.GetParent(Environment.ProcessPath);
        var installationRoot = contentDirectory?.Parent;
        if (contentDirectory is null
            || installationRoot is null
            || !contentDirectory.Name.Equals("current", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(installationRoot.FullName, "Update.exe")))
        {
            return null;
        }

        var packagesDirectory = Path.Combine(installationRoot.FullName, "packages");
        return Directory.Exists(packagesDirectory)
            ? packagesDirectory
            : null;
    }

    internal static void CleanupPackageDirectory(string packagesDirectory)
    {
        if (string.IsNullOrWhiteSpace(packagesDirectory)
            || !Directory.Exists(packagesDirectory))
        {
            return;
        }

        var root = Path.GetFullPath(packagesDirectory)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(
                     packagesDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var candidate = Path.GetFullPath(file);
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || (!candidate.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
                    && !candidate.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            for (var attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    File.Delete(candidate);
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(100 * (attempt + 1));
                }
                catch (UnauthorizedAccessException) when (attempt < 3)
                {
                    Thread.Sleep(100 * (attempt + 1));
                }
                catch (IOException)
                {
                    break;
                }
                catch (UnauthorizedAccessException)
                {
                    break;
                }
            }
        }
    }

    public void BeginApplyAndRestart()
    {
        var update = _pendingUpdate
            ?? throw new InvalidOperationException(
                "No downloaded TechBench V2 update is ready to install.");
        var version = update.TargetFullRelease.Version.ToNormalizedString();

        _updateManager.WaitExitThenApplyUpdates(
            update.TargetFullRelease,
            silent: false,
            restart: true,
            restartArgs: ["--updated-to", version]);
    }

    private static string GetAssemblyVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(V2AppUpdateService).Assembly;
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
