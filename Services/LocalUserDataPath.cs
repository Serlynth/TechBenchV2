using System.Security.Cryptography;

namespace TechBench.Services;

public static class LocalUserDataPath
{
    public static string ResolveCredentialScope(
        Guid databaseInstanceId,
        ReadOnlySpan<byte> userSid) =>
        $"Databases/{databaseInstanceId:N}/{BuildSidKey(userSid)}";

    private static string BuildSidKey(ReadOnlySpan<byte> userSid)
    {
        if (userSid.IsEmpty)
        {
            throw new ArgumentException("The Windows user SID cannot be empty.", nameof(userSid));
        }

        var hash = SHA256.HashData(userSid);
        return Convert.ToHexString(hash)[..24];
    }
}
