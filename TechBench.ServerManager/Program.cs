using System.Security.Principal;

namespace TechBench.ServerManager;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (TryArgument(args, "--package-directory", out var packageDirectory) &&
            args.Any(value => value.Equals("--apply-update", StringComparison.OrdinalIgnoreCase)))
        {
            _ = int.TryParse(Argument(args, "--manager-pid"), out var processId);
            return PackageInstaller.Apply(Path.GetFullPath(packageDirectory!), processId);
        }

        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("TechBench Server Manager must run as an administrator.", "TechBench Server Manager",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        try { SecureDirectory.EnsureAdministratorsOnly(AppPaths.Installed.ManagerDataDirectory); }
        catch (Exception ex)
        {
            MessageBox.Show($"TechBench could not protect its server-management data directory.\n\n{ex.Message}",
                "TechBench Server Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        using var mutex = new Mutex(true, "Global\\CSRI.TechBench.ServerManager", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("TechBench Server Manager is already running. Look for its icon in the notification area.",
                "TechBench Server Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.ThreadException += (_, eventArgs) => ShowFatal(eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => ShowFatal(eventArgs.ExceptionObject as Exception ?? new Exception("Unknown error"));
        Application.Run(new ServerManagerForm(AppPaths.Installed));
        return 0;
    }

    private static void ShowFatal(Exception exception)
    {
        var path = Path.Combine(AppPaths.Installed.ManagerDataDirectory, "errors.log");
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.AppendAllText(path, $"{DateTime.Now:O} {exception}{Environment.NewLine}"); } catch { }
        MessageBox.Show($"TechBench Server Manager encountered an error.\n\n{exception.Message}\n\nDetails: {path}",
            "TechBench Server Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static bool TryArgument(string[] args, string name, out string? value)
    {
        value = Argument(args, name);
        return value is not null;
    }
    private static string? Argument(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }
}
