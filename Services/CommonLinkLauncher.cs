using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TechBench.Services;

internal sealed record CommonLinkLaunchResult(bool Succeeded, string? ErrorMessage = null);

internal static class CommonLinkLauncher
{
    private const string ChromeAppPathKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe";

    public static CommonLinkLaunchResult OpenDefault(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
            return new CommonLinkLaunchResult(true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new CommonLinkLaunchResult(false, ex.Message);
        }
    }

    public static CommonLinkLaunchResult OpenChromeIncognito(string url)
    {
        var chromeExecutable = FindChromeExecutable();
        if (chromeExecutable is null)
        {
            return new CommonLinkLaunchResult(
                false,
                "Google Chrome was not found. Install Chrome or turn off Chrome Incognito for this link.");
        }

        try
        {
            using var process = Process.Start(CreateChromeIncognitoStartInfo(chromeExecutable, url));
            return new CommonLinkLaunchResult(true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new CommonLinkLaunchResult(false, ex.Message);
        }
    }

    internal static ProcessStartInfo CreateChromeIncognitoStartInfo(string chromeExecutable, string url)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = chromeExecutable,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--incognito");
        startInfo.ArgumentList.Add(url);
        return startInfo;
    }

    internal static string? SelectExistingChromeExecutable(
        IEnumerable<string?> candidates,
        Func<string, bool> fileExists)
    {
        foreach (var candidate in candidates)
        {
            var path = candidate?.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(path) && fileExists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string? FindChromeExecutable()
    {
        var candidates = new List<string?>();
        AddRegistryCandidate(candidates, RegistryHive.CurrentUser, RegistryView.Registry64);
        AddRegistryCandidate(candidates, RegistryHive.CurrentUser, RegistryView.Registry32);
        AddRegistryCandidate(candidates, RegistryHive.LocalMachine, RegistryView.Registry64);
        AddRegistryCandidate(candidates, RegistryHive.LocalMachine, RegistryView.Registry32);

        AddInstallCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddInstallCandidate(candidates, Environment.GetEnvironmentVariable("ProgramW6432"));
        AddInstallCandidate(candidates, Environment.GetEnvironmentVariable("ProgramFiles"));
        AddInstallCandidate(candidates, Environment.GetEnvironmentVariable("ProgramFiles(x86)"));

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(directory.Trim('"'), "chrome.exe"));
        }

        return SelectExistingChromeExecutable(candidates, File.Exists);
    }

    private static void AddRegistryCandidate(
        ICollection<string?> candidates,
        RegistryHive hive,
        RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var appPathKey = baseKey.OpenSubKey(ChromeAppPathKey);
            candidates.Add(appPathKey?.GetValue(null) as string);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Common install locations are checked when registry discovery is unavailable.
        }
    }

    private static void AddInstallCandidate(ICollection<string?> candidates, string? root)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
        }
    }
}
