using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TechBench.ServerManager;

internal sealed class ReleaseUpdater
{
    private const string ReleasesApi = "https://api.github.com/repos/Serlynth/TechBenchV2-Releases/releases";
    private const int ReleasesPerPage = 100;
    private const int MaximumReleasePages = 10;
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    private readonly AppPaths _paths;
    private readonly HttpClient _http;

    public ReleaseUpdater(AppPaths paths)
        : this(paths, CreateHttpClient())
    {
    }

    internal ReleaseUpdater(AppPaths paths, HttpClient http)
    {
        _paths = paths;
        _http = http;
    }

    public async Task<ReleasePackage?> FindUpdateAsync(CancellationToken cancellationToken)
    {
        ReleasePackage? best = null;
        for (var page = 1; page <= MaximumReleasePages; page++)
        {
            var uri = $"{ReleasesApi}?per_page={ReleasesPerPage}&page={page}";
            using var response = await _http.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    "GitHub returned an invalid server release list.");
            }

            var releaseCount = 0;
            foreach (var release in document.RootElement.EnumerateArray())
            {
                releaseCount++;
                var candidate = ParseRelease(release);
                if (candidate is not null
                    && (best is null
                        || SemanticVersion.CompareForUpdate(
                            candidate.Version,
                            best.Version) > 0))
                {
                    best = candidate;
                }
            }

            if (releaseCount < ReleasesPerPage)
            {
                break;
            }
        }
        return best;
    }

    private static ReleasePackage? ParseRelease(JsonElement release)
    {
        if (release.GetProperty("draft").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
        var version = NormalizeServerReleaseTag(tag);
        if (version is null) return null;
        if (!SemanticVersion.TryParse(version, out var parsed)) return null;
        var isPrerelease = release.GetProperty("prerelease").GetBoolean();
        if (isPrerelease != parsed.IsPrerelease || isPrerelease) return null;

        var zipName = $"TechBenchSyncService-{version}-win-x64.zip";
        var checksumName = zipName + ".sha256";
        JsonElement? zip = null;
        JsonElement? checksum = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (name == zipName) zip = asset;
            else if (name == checksumName) checksum = asset;
        }
        if (zip is null || checksum is null) return null;
        return new ReleasePackage(
            version,
            zipName,
            ApprovedAssetUri(zip.Value),
            zip.Value.GetProperty("size").GetInt64(),
            checksumName,
            ApprovedAssetUri(checksum.Value),
            checksum.Value.GetProperty("size").GetInt64());
    }

    internal static string? NormalizeServerReleaseTag(string tag)
    {
        if (tag.StartsWith("server-v", StringComparison.OrdinalIgnoreCase))
            return tag[8..];
        if (tag.StartsWith('v'))
            return tag[1..];
        return null;
    }

    public async Task<string> DownloadAndPrepareAsync(ReleasePackage package, IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (package.ZipSize is < 1 or > MaximumPackageBytes || package.ChecksumSize is < 1 or > 16384)
            throw new InvalidDataException("GitHub reported an invalid service package size.");

        SecureDirectory.EnsureAdministratorsOnly(_paths.ManagerDataDirectory);
        var operationRoot = Path.Combine(_paths.ManagerDataDirectory, "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operationRoot);
        try
        {
            var zipPath = Path.Combine(operationRoot, package.ZipName);
            var checksumPath = Path.Combine(operationRoot, package.ChecksumName);
            progress.Report($"Downloading {package.Version}...");
            await DownloadBoundedAsync(package.ZipUrl, zipPath, MaximumPackageBytes, cancellationToken);
            await DownloadBoundedAsync(package.ChecksumUrl, checksumPath, 16384, cancellationToken);
            VerifyChecksum(zipPath, checksumPath, package.ZipName);

            var packageDirectory = Path.Combine(operationRoot, "package");
            progress.Report("Verifying package contents...");
            ExtractSafe(zipPath, packageDirectory);
            var manifest = PackageManifest.LoadAndVerify(packageDirectory, package.Version);
            if (!PackageInstaller.InstalledPackageDeclaresRequiredSchema(_paths, manifest.RequiredDatabaseSchemaVersion))
                new SqlAdminRepository(_paths).VerifyRequiredSchema(manifest.RequiredDatabaseSchemaVersion);

            var helper = Path.Combine(packageDirectory, "server-manager", "TechBench.ServerManager.exe");
            if (!File.Exists(helper)) throw new InvalidDataException("The package does not contain the compiled Server Manager.");
            return packageDirectory;
        }
        catch
        {
            UpdateCacheCleanup.CleanupFailedOperation(_paths, operationRoot);
            throw;
        }
    }

    public static void LaunchInstaller(string packageDirectory, int managerProcessId)
    {
        _ = Process.Start(CreateInstallerStartInfo(packageDirectory, managerProcessId))
            ?? throw new InvalidOperationException("The compiled update helper could not be started.");
    }

    internal static ProcessStartInfo CreateInstallerStartInfo(string packageDirectory, int managerProcessId)
    {
        packageDirectory = Path.GetFullPath(packageDirectory);
        var helper = Path.Combine(packageDirectory, "server-manager", "TechBench.ServerManager.exe");
        return new ProcessStartInfo
        {
            FileName = helper,
            // The installed Manager normally starts with its installation directory as the
            // current directory. Do not inherit it: a child process whose current directory
            // is there prevents Windows from moving that directory during self-update.
            WorkingDirectory = packageDirectory,
            UseShellExecute = true,
            Arguments = $"--apply-update --package-directory {Quote(packageDirectory)} --manager-pid {managerProcessId}"
        };
    }

    private async Task DownloadBoundedAsync(Uri uri, string destination, long maximumBytes, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri ||
            !finalUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            !new[] { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" }
                .Contains(finalUri.Host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The release download redirected to an unapproved host.");
        if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("The release download is larger than allowed.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            total += count;
            if (total > maximumBytes) throw new InvalidDataException("The release download exceeded its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
    }

    private static void VerifyChecksum(string zipPath, string checksumPath, string zipName)
    {
        var text = File.ReadAllText(checksumPath).Trim();
        var match = Regex.Match(text, $"^(?<hash>[0-9A-Fa-f]{{64}})\\s+\\*?{Regex.Escape(zipName)}$");
        if (!match.Success) throw new InvalidDataException("The SHA-256 sidecar is malformed or names a different package.");
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath)));
        if (!actual.Equals(match.Groups["hash"].Value, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded package failed SHA-256 verification.");
    }

    private static void ExtractSafe(string zipPath, string destination)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long expanded = 0;
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > 10000) throw new InvalidDataException("The package contains too many entries.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized) || normalized.Contains(':'))
                throw new InvalidDataException($"Unsafe package path: {entry.FullName}");
            var target = Path.GetFullPath(Path.Combine(destination, normalized));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !seen.Add(target))
                throw new InvalidDataException($"Unsafe or duplicate package path: {entry.FullName}");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException($"Symbolic links are not allowed in the package: {entry.FullName}");
            expanded += entry.Length;
            if (entry.Length > MaximumPackageBytes || expanded > MaximumExpandedBytes)
                throw new InvalidDataException("The expanded package exceeds its size limit.");
        }
        archive.ExtractToDirectory(destination);
    }

    private static Uri ApprovedAssetUri(JsonElement asset)
    {
        var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/Serlynth/TechBenchV2-Releases/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub returned an unapproved release asset URL.");
        return uri;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TechBench-ServerManager/2");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"") + '"';
}

internal sealed class PackageManifest
{
    public string Product { get; set; } = string.Empty;
    public int PackageFormatVersion { get; set; }
    public string Version { get; set; } = string.Empty;
    public string Runtime { get; set; } = string.Empty;
    public string SageOdbcWorkerRuntime { get; set; } = string.Empty;
    public bool SelfContained { get; set; }
    public int RequiredDatabaseSchemaVersion { get; set; }
    public List<PackageFile> Files { get; set; } = [];

    public static PackageManifest LoadAndVerify(string packageDirectory, string? expectedVersion = null)
    {
        var manifestPath = Path.Combine(packageDirectory, "package-manifest.json");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(manifestPath), JsonOptions())
            ?? throw new InvalidDataException("The package manifest is empty.");
        if (manifest.Product != "TechBench Sync Service" || manifest.PackageFormatVersion != 1 ||
            manifest.Runtime != "win-x64" || manifest.SageOdbcWorkerRuntime != "win-x86" || !manifest.SelfContained ||
            manifest.RequiredDatabaseSchemaVersion < 1 ||
            (expectedVersion is not null && manifest.Version != expectedVersion))
            throw new InvalidDataException("The package manifest does not identify the expected TechBench release.");

        var root = Path.GetFullPath(packageDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var full = Path.GetFullPath(Path.Combine(packageDirectory, file.Path));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !seen.Add(full) || !File.Exists(full))
                throw new InvalidDataException($"The package manifest contains an unsafe, missing, or duplicate path: {file.Path}");
            var info = new FileInfo(full);
            if (info.Length != file.Length || !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The package failed manifest verification: {file.Path}");
        }
        foreach (var actualFile in Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories))
        {
            if (actualFile.Equals(manifestPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Contains(Path.GetFullPath(actualFile)))
                throw new InvalidDataException($"The package contains a file not covered by its manifest: {Path.GetRelativePath(packageDirectory, actualFile)}");
        }
        var required = new[]
        {
            "TechBench.SyncService.exe", "TechBench.SyncService.runtimeconfig.json", "TechBench.SyncService.deps.json",
            "appsettings.json", "sage-odbc-worker\\TechBench.SageOdbcWorker.exe",
            "server-manager\\TechBench.ServerManager.exe", "server-manager\\TechBench.ServerManager.runtimeconfig.json",
            "server-manager\\TechBench.ServerManager.deps.json"
        };
        if (required.Any(name => !manifest.Files.Any(file => file.Path.Equals(name, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("The package manifest is missing a required executable or configuration file.");
        foreach (var executable in new[]
        {
            "TechBench.SyncService.exe",
            "sage-odbc-worker\\TechBench.SageOdbcWorker.exe",
            "server-manager\\TechBench.ServerManager.exe"
        })
        {
            var productVersion = FileVersionInfo.GetVersionInfo(Path.Combine(packageDirectory, executable)).ProductVersion?.Split('+', 2)[0];
            if (!manifest.Version.Equals(productVersion, StringComparison.Ordinal))
                throw new InvalidDataException($"The executable version does not match package {manifest.Version}: {executable}");
        }
        return manifest;
    }

    private static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true };
}

internal sealed class PackageFile
{
    public string Path { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

internal readonly record struct SemanticVersion(int Major, int Minor, int Patch, string PreRelease)
{
    public bool IsPrerelease => !string.IsNullOrEmpty(PreRelease);
    public static bool TryParse(string value, out SemanticVersion version)
    {
        var match = Regex.Match(value ?? string.Empty, "^v?(?<major>\\d+)\\.(?<minor>\\d+)\\.(?<patch>\\d+)(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\\+.*)?$");
        if (!match.Success) { version = default; return false; }
        version = new(int.Parse(match.Groups["major"].Value), int.Parse(match.Groups["minor"].Value), int.Parse(match.Groups["patch"].Value), match.Groups["pre"].Value);
        return true;
    }
    public static int Compare(string left, string right)
    {
        if (!TryParse(left, out var l) || !TryParse(right, out var r)) return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        var core = l.Major != r.Major ? l.Major.CompareTo(r.Major) : l.Minor != r.Minor ? l.Minor.CompareTo(r.Minor) : l.Patch.CompareTo(r.Patch);
        if (core != 0) return core;
        if (!l.IsPrerelease && !r.IsPrerelease) return 0;
        if (!l.IsPrerelease) return 1;
        if (!r.IsPrerelease) return -1;
        return ComparePrerelease(l.PreRelease, r.PreRelease);
    }

    public static int CompareForUpdate(string left, string right)
    {
        if (!TryParse(left, out var l) || !TryParse(right, out var r))
            return Compare(left, right);

        static bool CorrectedLine(SemanticVersion version) => version.Major is 0 or 1;
        static bool MistakenLegacyLine(SemanticVersion version) => version.Major is 2 or 5;

        if (CorrectedLine(l) && MistakenLegacyLine(r)) return 1;
        if (MistakenLegacyLine(l) && CorrectedLine(r)) return -1;
        return Compare(left, right);
    }
    private static int ComparePrerelease(string left, string right)
    {
        var a = left.Split('.'); var b = right.Split('.');
        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            if (index >= a.Length) return -1;
            if (index >= b.Length) return 1;
            var an = int.TryParse(a[index], out var ai); var bn = int.TryParse(b[index], out var bi);
            var result = an && bn ? ai.CompareTo(bi) : an != bn ? (an ? -1 : 1) : string.Compare(a[index], b[index], StringComparison.OrdinalIgnoreCase);
            if (result != 0) return result;
        }
        return 0;
    }
}
