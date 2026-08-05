using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TechBench.SyncService;

public sealed record AuthPointProtectedCredentials(
    string AccessPassword,
    string ApiKey);

public sealed class AuthPointSecretStore
{
    private static readonly byte[] Entropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("CSRI.TechBench.SyncService.AuthPoint.v1"));
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AuthPointSecretStore(IOptions<SyncServiceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = options.Value.ResolveAuthPointSecretPath();
    }

    public string Path => _path;
    public bool Exists => File.Exists(_path);

    public AuthPointProtectedCredentials Read()
    {
        if (!File.Exists(_path))
        {
            throw new InvalidOperationException(
                "The server-local WatchGuard AuthPoint API credentials have not been configured in TechBench Server Manager.");
        }

        var protectedBytes = File.ReadAllBytes(_path);
        byte[]? plainBytes = null;
        try
        {
            plainBytes = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.LocalMachine);
            var credentials = JsonSerializer.Deserialize<AuthPointProtectedCredentials>(plainBytes, JsonOptions)
                ?? throw new InvalidOperationException(
                    "The protected WatchGuard AuthPoint API credential file is invalid.");
            Validate(credentials);
            return credentials;
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "The server-local WatchGuard AuthPoint API credentials could not be decrypted on this computer.",
                ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "The protected WatchGuard AuthPoint API credential file is invalid.",
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

    public void Write(string credentialsJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
        {
            throw new ArgumentException(
                "WatchGuard AuthPoint API credentials are required.",
                nameof(credentialsJson));
        }

        AuthPointProtectedCredentials credentials;
        try
        {
            credentials = JsonSerializer.Deserialize<AuthPointProtectedCredentials>(credentialsJson, JsonOptions)
                ?? throw new ArgumentException(
                    "AuthPoint credentials must be JSON with accessPassword and apiKey values.",
                    nameof(credentialsJson));
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                "AuthPoint credentials must be JSON with accessPassword and apiKey values.",
                nameof(credentialsJson),
                ex);
        }

        Write(credentials);
    }

    public void Write(AuthPointProtectedCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        Validate(credentials);
        var directory = System.IO.Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException(
                "The AuthPoint credential path has no parent directory.");
        Directory.CreateDirectory(directory);

        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(credentials);
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
                4096,
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

    private static void Validate(AuthPointProtectedCredentials credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials.AccessPassword)
            || string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            throw new ArgumentException(
                "Both the WatchGuard API access password and API key are required.");
        }

        if (credentials.AccessPassword.Contains('\r')
            || credentials.AccessPassword.Contains('\n')
            || credentials.ApiKey.Contains('\r')
            || credentials.ApiKey.Contains('\n'))
        {
            throw new ArgumentException(
                "WatchGuard API credentials cannot contain line breaks.");
        }
    }
}
