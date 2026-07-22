using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace TechBench.SyncService;

public sealed class FireDrillSecretStore
{
    private static readonly byte[] Entropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("CSRI.TechBench.SyncService.FireDrillWorkbook.v1"));
    private readonly string _path;

    public FireDrillSecretStore(IOptions<SyncServiceOptions> options) =>
        _path = options.Value.ResolveFireDrillSecretPath();

    public string Path => _path;
    public bool Exists => File.Exists(_path);

    public string Read()
    {
        if (!File.Exists(_path))
            throw new InvalidOperationException("The server-local FireDrill workbook password has not been configured in TechBench Server Manager.");
        var protectedBytes = File.ReadAllBytes(_path);
        byte[]? plainBytes = null;
        try
        {
            plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            var value = Encoding.UTF8.GetString(plainBytes);
            if (string.IsNullOrEmpty(value)) throw new InvalidOperationException("The protected FireDrill workbook password is empty.");
            return value;
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("The FireDrill workbook password could not be decrypted on this server.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plainBytes is not null) CryptographicOperations.ZeroMemory(plainBytes);
        }
    }
}
