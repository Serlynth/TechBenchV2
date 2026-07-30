using System.IO;
using System.Text;

namespace TechBench.Services;

internal static class CrashLog
{
    private static readonly object WriteLock = new();

    internal static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CSRI",
        "TechBench V2",
        "crash.log");

    internal static void Record(string source, Exception exception)
    {
        try
        {
            lock (WriteLock)
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var details = new StringBuilder()
                    .AppendLine("============================================================")
                    .AppendLine($"{DateTimeOffset.Now:O} | {source}")
                    .AppendLine($"Version: {typeof(CrashLog).Assembly.GetName().Version}")
                    .AppendLine(exception.ToString())
                    .ToString();
                File.AppendAllText(FilePath, details, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never replace the original failure.
        }
    }
}
