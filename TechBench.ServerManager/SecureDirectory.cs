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
}
