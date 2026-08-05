using System.Security.Cryptography;
using System.Text;

namespace TechBench.ServerManager;

internal sealed class ProtectedSecretStore(string path, string entropyLabel, string friendlyName)
{
    private readonly byte[] _entropy = SHA256.HashData(Encoding.UTF8.GetBytes(entropyLabel));

    public string Path { get; } = path;
    public bool Exists => File.Exists(Path);

    public void Write(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException($"The {friendlyName} cannot be empty.", nameof(secret));
        }

        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("The protected credential path has no parent directory.");
        if (!Directory.Exists(directory))
            throw new InvalidOperationException("The protected service data directory is missing. Install the TechBench Sync Service before saving server credentials.");
        var plain = Encoding.UTF8.GetBytes(secret);
        byte[]? encrypted = null;
        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            encrypted = ProtectedData.Protect(plain, _entropy, DataProtectionScope.LocalMachine);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(encrypted);
                stream.Flush(true);
            }
            File.Move(temporary, Path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static ProtectedSecretStore Whd(AppPaths paths) => new(
        paths.WhdSecretPath,
        "CSRI.TechBench.SyncService.WHD.v1",
        "WHD API key, token, or password");

    public static ProtectedSecretStore Sage(AppPaths paths) => new(
        paths.SageSecretPath,
        "CSRI.TechBench.SyncService.SageOdbc.v1",
        "Sage ODBC password");

    public static ProtectedSecretStore FireDrill(AppPaths paths) => new(
        paths.FireDrillSecretPath,
        "CSRI.TechBench.SyncService.FireDrillWorkbook.v1",
        "FireDrill workbook password");

    public static ProtectedSecretStore AuthPoint(AppPaths paths) => new(
        paths.AuthPointSecretPath,
        "CSRI.TechBench.SyncService.AuthPoint.v1",
        "WatchGuard AuthPoint API credentials");
}
