using System.IO;

namespace TechBench.ViewModels;

public sealed record PendingWhdImage(string FilePath)
{
    public string FileName => Path.GetFileName(FilePath);

    public string SizeLabel
    {
        get
        {
            try
            {
                var bytes = new FileInfo(FilePath).Length;
                return bytes >= 1024 * 1024
                    ? $"{bytes / (1024d * 1024d):0.##} MB"
                    : $"{Math.Max(1, bytes / 1024d):0.#} KB";
            }
            catch (IOException)
            {
                return "File unavailable";
            }
            catch (UnauthorizedAccessException)
            {
                return "File unavailable";
            }
        }
    }
}
