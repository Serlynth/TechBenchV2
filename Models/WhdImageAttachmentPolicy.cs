namespace TechBench.Models;

public static class WhdImageAttachmentPolicy
{
    private static readonly HashSet<string> SupportedExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".bmp",
        ".gif",
        ".heic",
        ".heif",
        ".jpeg",
        ".jpg",
        ".png",
        ".tif",
        ".tiff",
        ".webp"
    };

    public const string FileDialogFilter =
        "Image files (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff;*.webp;*.heic;*.heif)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff;*.webp;*.heic;*.heif";

    public static bool IsSupported(string? filePath) =>
        !string.IsNullOrWhiteSpace(filePath)
        && SupportedExtensions.Contains(Path.GetExtension(filePath));

    public static string GetMediaType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".heic" => "image/heic",
            ".heif" => "image/heif",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".tif" or ".tiff" => "image/tiff",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
}
