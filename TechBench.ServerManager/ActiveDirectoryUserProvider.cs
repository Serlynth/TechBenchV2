using System.DirectoryServices.AccountManagement;

namespace TechBench.ServerManager;

internal sealed class ActiveDirectoryUserProvider
{
    internal const string DomainDnsName = "CSRI.local";
    internal const string DomainNetBiosName = "CSRI";
    internal const string UserGroupName = "TechBench_Users";
    internal const string AdminGroupName = "TechBench_Admins";

    public IReadOnlyList<DirectoryUser> LoadAuthorizedUsers()
    {
        using var context = new PrincipalContext(ContextType.Domain, DomainDnsName);
        var users = new Dictionary<string, DirectoryUser>(StringComparer.OrdinalIgnoreCase);

        AddGroupMembers(context, UserGroupName, isAdmin: false, users);
        AddGroupMembers(context, AdminGroupName, isAdmin: true, users);

        return users.Values
            .OrderBy(static user => user.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static user => user.LoginName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static IReadOnlyList<UserMapping> MergeMappings(
        IEnumerable<DirectoryUser> directoryUsers,
        IEnumerable<UserMapping> savedMappings)
    {
        var saved = savedMappings.ToDictionary(
            static mapping => mapping.LoginName,
            StringComparer.OrdinalIgnoreCase);

        return directoryUsers
            .Select(user => new UserMapping(
                user.LoginName,
                user.DisplayName,
                user.IsAdmin,
                saved.TryGetValue(user.LoginName, out var mapping)
                    ? mapping.TechnicianExternalId
                    : string.Empty,
                saved.TryGetValue(user.LoginName, out mapping)
                    ? mapping.AuthPointLogin
                    : string.Empty,
                saved.TryGetValue(user.LoginName, out mapping)
                    && mapping.AuthPointEnabled,
                saved.TryGetValue(user.LoginName, out mapping)
                    ? mapping.AuthPointRowVersion
                    : null))
            .OrderBy(static mapping => mapping.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static mapping => mapping.LoginName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static IReadOnlyList<Technician> RestoreMappedTechnicianLabels(
        IEnumerable<Technician> technicians,
        IEnumerable<UserMapping> mappings)
    {
        var mappedNames = mappings
            .Where(static mapping =>
                !string.IsNullOrWhiteSpace(mapping.TechnicianExternalId)
                && !string.IsNullOrWhiteSpace(mapping.DisplayName))
            .GroupBy(
                static mapping => mapping.TechnicianExternalId,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(static mapping => mapping.DisplayName.Trim())
                    .First(),
                StringComparer.OrdinalIgnoreCase);

        return technicians
            .Select(technician =>
                IsPlaceholderTechnicianLabel(technician)
                && mappedNames.TryGetValue(technician.ExternalId, out var mappedName)
                    ? technician with { Label = mappedName }
                    : technician)
            .ToList();
    }

    private static bool IsPlaceholderTechnicianLabel(Technician technician)
    {
        var label = technician.Label.Trim();
        var externalId = technician.ExternalId.Trim();
        var rawId = externalId.StartsWith("WHD-TECH-", StringComparison.OrdinalIgnoreCase)
            ? externalId["WHD-TECH-".Length..]
            : externalId;

        return string.IsNullOrWhiteSpace(label)
            || label.Equals(externalId, StringComparison.OrdinalIgnoreCase)
            || label.Equals(rawId, StringComparison.OrdinalIgnoreCase)
            || label.Equals($"Technician {rawId}", StringComparison.OrdinalIgnoreCase)
            || label.StartsWith("WHD-TECH-", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddGroupMembers(
        PrincipalContext context,
        string groupName,
        bool isAdmin,
        IDictionary<string, DirectoryUser> users)
    {
        using var group = GroupPrincipal.FindByIdentity(
            context,
            IdentityType.SamAccountName,
            groupName)
            ?? throw new InvalidOperationException(
                $"Active Directory group '{DomainNetBiosName}\\{groupName}' was not found.");

        using var members = group.GetMembers(recursive: true);
        foreach (var principal in members)
        {
            using (principal)
            {
                if (principal is not UserPrincipal user
                    || string.IsNullOrWhiteSpace(user.SamAccountName))
                {
                    continue;
                }

                var loginName = $"{DomainNetBiosName}\\{user.SamAccountName.Trim()}";
                var displayName = FirstNonBlank(
                    user.DisplayName,
                    user.Name,
                    user.SamAccountName);

                if (users.TryGetValue(loginName, out var existing))
                {
                    users[loginName] = existing with
                    {
                        DisplayName = displayName,
                        IsAdmin = existing.IsAdmin || isAdmin
                    };
                }
                else
                {
                    users.Add(
                        loginName,
                        new DirectoryUser(
                            loginName,
                            displayName,
                            isAdmin,
                            ToSqlSidHex(user.Sid)));
                }
            }
        }
    }

    private static string ToSqlSidHex(System.Security.Principal.SecurityIdentifier? sid)
    {
        if (sid is null)
        {
            throw new InvalidOperationException(
                "Active Directory returned a user without a Windows SID. "
                + "No SQL users were changed.");
        }

        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        return "0x" + Convert.ToHexString(bytes);
    }

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim()
        ?? "Unknown user";
}
