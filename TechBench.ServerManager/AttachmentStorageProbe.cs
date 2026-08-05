using System.Runtime.InteropServices;

namespace TechBench.ServerManager;

internal sealed record AttachmentStorageProbeResult(
    string RootPath,
    long UsedBytes,
    long AvailableBytes)
{
    public string Summary =>
        $"Read/write/delete test passed. {FormatBytes(UsedBytes)} stored; "
        + $"{FormatBytes(AvailableBytes)} free.";

    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024d):0.#} MB",
        _ => $"{bytes / (1024d * 1024d * 1024d):0.##} GB"
    };
}

internal static class AttachmentStorageProbe
{
    public static string ValidateRootPath(string value)
    {
        var path = value.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                "Choose a shared attachment storage folder.");
        }

        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Attachment storage must use a UNC path such as "
                + @"\\CSRI-SQL\TechBenchFiles\ClientAttachments so every TechBench workstation reaches the same folder.");
        }

        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (fullPath.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries).Length < 3)
        {
            throw new InvalidOperationException(
                "Choose a folder beneath the server share, not the share root itself. "
                + @"For example: \\CSRI-SQL\TechBenchFiles\ClientAttachments.");
        }

        return fullPath;
    }

    public static AttachmentStorageProbeResult Test(string value)
    {
        var rootPath = ValidateRootPath(value);
        Directory.CreateDirectory(rootPath);

        var testPath = Path.Combine(
            rootPath,
            $".techbench-access-test-{Guid.NewGuid():N}.tmp");
        var expected = Guid.NewGuid().ToByteArray();
        try
        {
            File.WriteAllBytes(testPath, expected);
            var returned = File.ReadAllBytes(testPath);
            if (!returned.SequenceEqual(expected))
            {
                throw new IOException(
                    "The attachment share returned different data during its read/write test.");
            }
        }
        finally
        {
            if (File.Exists(testPath))
            {
                File.Delete(testPath);
            }
        }

        long usedBytes = 0;
        foreach (var file in Directory.EnumerateFiles(
                     rootPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            usedBytes += new FileInfo(file).Length;
        }

        if (!GetDiskFreeSpaceEx(
                rootPath,
                out var availableBytes,
                out _,
                out _))
        {
            throw new IOException(
                "TechBench passed the file-access test but could not read free-space information. "
                + $"Windows error {Marshal.GetLastWin32Error()}.");
        }

        return new AttachmentStorageProbeResult(
            rootPath,
            usedBytes,
            checked((long)Math.Min(availableBytes, (ulong)long.MaxValue)));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);
}
