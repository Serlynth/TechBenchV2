using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TechBench.Services;

internal sealed record AnyDeskLaunchResult(
    bool Succeeded,
    bool PasswordSubmitted = false,
    string? ErrorMessage = null);

internal static partial class AnyDeskLauncher
{
    private const string AnyDeskAppPathKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\AnyDesk.exe";

    public static AnyDeskLaunchResult Launch(
        string? address,
        string? password)
    {
        var normalizedAddress = NormalizeAddress(address);
        if (!IsValidAddress(normalizedAddress))
        {
            return new AnyDeskLaunchResult(
                false,
                ErrorMessage:
                    "Enter a valid 9- or 10-digit AnyDesk ID or an AnyDesk Alias before connecting.");
        }

        var executable = FindAnyDeskExecutable();
        if (executable is null)
        {
            return new AnyDeskLaunchResult(
                false,
                ErrorMessage:
                    "AnyDesk was not found. Install the standard AnyDesk client and try again.");
        }

        var hasPassword = !string.IsNullOrWhiteSpace(password);
        try
        {
            using var process = Process.Start(
                CreateStartInfo(
                    executable,
                    normalizedAddress,
                    hasPassword));
            if (process is null)
            {
                return new AnyDeskLaunchResult(
                    false,
                    ErrorMessage: "Windows did not start AnyDesk.");
            }

            if (hasPassword)
            {
                WritePasswordToStandardInput(
                    process.StandardInput,
                    password!);
                process.StandardInput.Close();
            }

            return new AnyDeskLaunchResult(
                true,
                PasswordSubmitted: hasPassword);
        }
        catch (Exception ex) when (
            ex is Win32Exception
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            return new AnyDeskLaunchResult(
                false,
                ErrorMessage: ex.Message);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executable,
        string address,
        bool submitPassword)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = submitPassword,
            CreateNoWindow = false
        };
        startInfo.ArgumentList.Add(address);
        if (submitPassword)
        {
            startInfo.ArgumentList.Add("--with-password");
        }

        return startInfo;
    }

    internal static void WritePasswordToStandardInput(
        TextWriter standardInput,
        string password)
    {
        standardInput.WriteLine(password);
        standardInput.Flush();
    }

    internal static string NormalizeAddress(string? address)
    {
        var trimmed = address?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.All(static character =>
                char.IsDigit(character)
                || char.IsWhiteSpace(character)
                || character == '-'))
        {
            return new string(
                trimmed
                    .Where(char.IsDigit)
                    .ToArray());
        }

        return trimmed;
    }

    internal static bool IsValidAddress(string address) =>
        NumericAddressPattern().IsMatch(address)
        || AliasAddressPattern().IsMatch(address);

    internal static string? SelectExistingExecutable(
        IEnumerable<string?> candidates,
        Func<string, bool> fileExists)
    {
        foreach (var candidate in candidates)
        {
            var path = candidate?.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(path)
                && fileExists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string? FindAnyDeskExecutable()
    {
        var candidates = new List<string?>();
        AddRegistryCandidate(
            candidates,
            RegistryHive.CurrentUser,
            RegistryView.Registry64);
        AddRegistryCandidate(
            candidates,
            RegistryHive.CurrentUser,
            RegistryView.Registry32);
        AddRegistryCandidate(
            candidates,
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        AddRegistryCandidate(
            candidates,
            RegistryHive.LocalMachine,
            RegistryView.Registry32);

        AddInstallCandidate(
            candidates,
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"));
        AddInstallCandidate(
            candidates,
            Environment.GetEnvironmentVariable("ProgramFiles"));
        AddInstallCandidate(
            candidates,
            Environment.GetEnvironmentVariable("ProgramW6432"));
        AddInstallCandidate(
            candidates,
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData));

        foreach (var directory in
                 (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries
                     | StringSplitOptions.TrimEntries))
        {
            candidates.Add(
                Path.Combine(
                    directory.Trim('"'),
                    "AnyDesk.exe"));
        }

        return SelectExistingExecutable(candidates, File.Exists);
    }

    private static void AddRegistryCandidate(
        ICollection<string?> candidates,
        RegistryHive hive,
        RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var appPathKey =
                baseKey.OpenSubKey(AnyDeskAppPathKey);
            candidates.Add(appPathKey?.GetValue(null) as string);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
                or IOException
                or System.Security.SecurityException)
        {
            // Common install locations are checked when registry lookup fails.
        }
    }

    private static void AddInstallCandidate(
        ICollection<string?> candidates,
        string? root)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            candidates.Add(
                Path.Combine(
                    root,
                    "AnyDesk",
                    "AnyDesk.exe"));
        }
    }

    [GeneratedRegex(@"^\d{9,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericAddressPattern();

    [GeneratedRegex(
        @"^[A-Za-z0-9._-]+@[A-Za-z0-9._-]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AliasAddressPattern();
}
