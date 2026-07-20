using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace TechBench.SyncService;

public sealed class SageSecretStore
{
    private static readonly byte[] Entropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("CSRI.TechBench.SyncService.SageOdbc.v1"));

    private readonly string _path;

    public SageSecretStore(IOptions<SyncServiceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = options.Value.ResolveSageSecretPath();
    }

    public string Path => _path;

    public bool Exists => File.Exists(_path);

    public string Read()
    {
        if (!File.Exists(_path))
        {
            throw new InvalidOperationException(
                "The server-local Sage ODBC credential has not been configured. Run Set-TechBenchSageSyncCredential.ps1 on the service host.");
        }

        var protectedBytes = File.ReadAllBytes(_path);
        byte[]? plainBytes = null;
        try
        {
            plainBytes = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.LocalMachine);
            var secret = Encoding.UTF8.GetString(plainBytes);
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException("The protected Sage ODBC credential is empty.");
            }

            return secret;
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "The server-local Sage ODBC credential could not be decrypted on this computer.",
                ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plainBytes is not null)
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
    }

    public void Write(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("A nonempty Sage ODBC password is required.", nameof(secret));
        }

        var directory = System.IO.Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The Sage credential path has no parent directory.");
        Directory.CreateDirectory(directory);

        var plainBytes = Encoding.UTF8.GetBytes(secret);
        byte[]? protectedBytes = null;
        var temporaryPath = System.IO.Path.Combine(
            directory,
            $".{System.IO.Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            protectedBytes = ProtectedData.Protect(
                plainBytes,
                Entropy,
                DataProtectionScope.LocalMachine);
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Delete()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
