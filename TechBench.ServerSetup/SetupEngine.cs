using System.Diagnostics;
using TechBench.ServerManager;

namespace TechBench.ServerSetup;

internal static class SetupEngine
{
    public static ServiceDetails CurrentService() => new WindowsServiceManager(AppPaths.Installed).GetDetails();

    public static int InstallOrUpdate(string serviceAccount, IProgress<string>? progress = null)
    {
        using var package = EmbeddedPackage.ExtractAndVerify(progress);
        var service = CurrentService();
        if (service.Installed)
        {
            progress?.Report($"Updating the existing service from {service.Version} to {package.Manifest.Version}...");
            CloseRunningManager();
            return PackageInstaller.Apply(package.Directory, managerProcessId: 0);
        }

        if (string.IsNullOrWhiteSpace(serviceAccount) || !serviceAccount.Contains('\\'))
            throw new ArgumentException("Enter the domain service account in DOMAIN\\username form.");
        progress?.Report($"Installing the service as {serviceAccount.Trim()}...");
        return RunFreshInstall(package.Directory, serviceAccount.Trim());
    }

    private static int RunFreshInstall(string packageDirectory, string serviceAccount)
    {
        var script = Path.Combine(packageDirectory, "Install-TechBenchSyncService.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("The embedded installer engine is missing.", script);
        var powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo
        {
            FileName = powerShell,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
            "-File", script, "-ServiceAccount", serviceAccount, "-SourceDirectory", packageDirectory
        }) start.ArgumentList.Add(argument);
        var messages = new List<string>();
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The native setup could not start its secured installation engine.");
        process.OutputDataReceived += (_, eventArgs) => AddMessage(messages, eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => AddMessage(messages, eventArgs.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var detail = string.Join(Environment.NewLine, messages.TakeLast(8));
            throw new InvalidOperationException(
                $"The TechBench service installation did not complete (exit code {process.ExitCode})." +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : $"{Environment.NewLine}{Environment.NewLine}{detail}"));
        }
        if (File.Exists(AppPaths.Installed.ManagerExecutable))
            Process.Start(new ProcessStartInfo(AppPaths.Installed.ManagerExecutable) { UseShellExecute = true });
        return 0;
    }

    private static void AddMessage(List<string> messages, string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (messages) messages.Add(message.Trim());
    }

    private static void CloseRunningManager()
    {
        foreach (var process in Process.GetProcessesByName("TechBench.ServerManager"))
        {
            using (process)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10000);
                }
                catch (InvalidOperationException) { }
            }
        }
    }
}
