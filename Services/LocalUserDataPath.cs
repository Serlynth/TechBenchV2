using System.IO;
using System.Security.Cryptography;

namespace TechBench.Services;

public static class LocalUserDataPath
{
    public static string ResolveDatabasePath(
        Guid serverInstanceId,
        Guid userId)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(
            appData,
            "TechBenchV2",
            "Users",
            serverInstanceId.ToString("N"),
            userId.ToString("N"),
            "techbench-v2-local.db");
    }

    public static string ResolveCredentialScope(
        Guid serverInstanceId,
        Guid userId) =>
        $"Users/{serverInstanceId:N}/{userId:N}";

    public static string ResolveDatabasePath(
        Guid databaseInstanceId,
        ReadOnlySpan<byte> userSid)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(
            appData,
            "TechBenchV2",
            "Databases",
            databaseInstanceId.ToString("N"),
            BuildSidKey(userSid),
            "transition-local.db");
    }

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
