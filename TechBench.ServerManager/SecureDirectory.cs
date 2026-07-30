using System.Security.AccessControl;
using System.Security.Principal;

namespace TechBench.ServerManager;

internal static class SecureDirectory
{
    public static void EnsureAdministratorsOnly(string path)
    {
        Directory.CreateDirectory(path);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        foreach (var identity in new[] { administrators, system })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public static void GrantReadAndExecute(string path, string accountName)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        if (string.IsNullOrWhiteSpace(accountName) || accountName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The installed Windows service account could not be resolved.", nameof(accountName));

        SecurityIdentifier serviceSid;
        try
        {
            serviceSid = (SecurityIdentifier)new NTAccount(accountName.Trim()).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException ex)
        {
            throw new InvalidOperationException($"Windows could not resolve service account '{accountName}'.", ex);
        }

        GrantReadAndExecute(path, serviceSid);
    }

    public static void GrantBuiltInUsersReadAndExecute(string path)
    {
        GrantReadAndExecute(path, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
    }

    private static void GrantReadAndExecute(string path, SecurityIdentifier identity)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);

        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);
    }
}
