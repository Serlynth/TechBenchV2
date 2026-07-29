using TechBench.Models;

namespace TechBench.Services;

public static class BenchModuleAccess
{
    internal const string PrivateModuleLoginName = @"CSRI\rskoog";

    public static bool CanAccessPrivateModules(CurrentUserContext currentUser)
    {
        ArgumentNullException.ThrowIfNull(currentUser);

        // In a read-only preview, LoginName is the account being previewed.
        // AuthenticatedLoginName remains the person at the keyboard and must
        // therefore take precedence for this private beta gate.
        var authenticatedLoginName =
            currentUser.AuthenticatedLoginName ?? currentUser.LoginName;

        return string.Equals(
            authenticatedLoginName?.Trim(),
            PrivateModuleLoginName,
            StringComparison.OrdinalIgnoreCase);
    }

    public static BenchModule ResolveRequestedModule(
        object? requestedModule,
        CurrentUserContext currentUser)
    {
        if (!Enum.TryParse<BenchModule>(
                requestedModule?.ToString(),
                ignoreCase: true,
                out var module))
        {
            return BenchModule.TechBench;
        }

        return module == BenchModule.TechBench
            || CanAccessPrivateModules(currentUser)
                ? module
                : BenchModule.TechBench;
    }
}
