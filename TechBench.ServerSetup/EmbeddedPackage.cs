using System.IO.Compression;
using System.Reflection;
using TechBench.ServerManager;

namespace TechBench.ServerSetup;

internal sealed class EmbeddedPackage : IDisposable
{
    private const string ResourceName = "TechBench.ServerSetup.Payload.zip";
    private const long MaximumFileBytes = 512L * 1024 * 1024;
    private const long MaximumExpandedBytes = 1024L * 1024 * 1024;

    private EmbeddedPackage(string directory, PackageManifest manifest)
    {
        Directory = directory;
        Manifest = manifest;
    }

    public string Directory { get; }
    public PackageManifest Manifest { get; }

    public static EmbeddedPackage ExtractAndVerify(IProgress<string>? progress = null)
    {
        var paths = AppPaths.Installed;
        SecureDirectory.EnsureAdministratorsOnly(paths.ManagerDataDirectory);
        var setupRoot = Path.Combine(paths.ManagerDataDirectory, "setup");
        SecureDirectory.EnsureAdministratorsOnly(setupRoot);
        var destination = Path.Combine(setupRoot, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(destination);

        try
        {
            progress?.Report("Extracting the verified server package...");
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException("This setup executable does not contain the TechBench server payload.");
            ExtractSafe(resource, destination);
            progress?.Report("Verifying package hashes and versions...");
            var manifest = PackageManifest.LoadAndVerify(destination);
            return new EmbeddedPackage(destination, manifest);
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    private static void ExtractSafe(Stream input, string destination)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long expanded = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count is < 1 or > 10000)
            throw new InvalidDataException("The embedded server package contains an invalid number of entries.");
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
            if (entry.Length > MaximumFileBytes || expanded > MaximumExpandedBytes)
                throw new InvalidDataException("The embedded package exceeds its allowed expanded size.");
        }
        archive.ExtractToDirectory(destination);
    }

    public void Dispose() => TryDelete(Directory);

    private static void TryDelete(string path)
    {
        try { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true); }
        catch { }
    }
}
