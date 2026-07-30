using TechBench.Models;

namespace TechBench.Services;

public static class BenchModuleAccess
{
    public static bool CanAccessModules(CurrentUserContext currentUser)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        return currentUser.IsAdmin;
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
            || CanAccessModules(currentUser)
                ? module
                : BenchModule.TechBench;
    }
}
