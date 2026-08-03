using System.IO;
using System.Security.Cryptography;
using TechBench.Data;
using TechBench.Models;

namespace TechBench.Services;

public interface IClientAttachmentMetadataStore
{
    ClientAttachmentStorageConfiguration GetConfiguration();
    ClientInfoAttachment Save(ClientInfoAttachment attachment);
    ClientInfoAttachment SetArchived(
        ClientInfoAttachment attachment,
        bool isArchived);
}

public sealed class ClientAttachmentStorageService
{
    private readonly IClientAttachmentMetadataStore _store;

    public ClientAttachmentStorageService(ITechBenchRepository repository)
        : this(new RepositoryAttachmentMetadataStore(repository))
    {
    }

    public ClientAttachmentStorageService(IClientAttachmentMetadataStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public const string RootPathSettingKey = "ClientAttachments.RootPath";
    public const string MaximumFileSizeSettingKey =
        "ClientAttachments.MaximumFileSizeMegabytes";
    public const string AllowedExtensionsSettingKey =
        "ClientAttachments.AllowedExtensions";

    private static readonly HashSet<string> ProhibitedExtensions = new(
        [
            ".ade", ".adp", ".app", ".application", ".bat", ".chm",
            ".cmd", ".com", ".cpl", ".dll", ".exe", ".hta", ".inf",
            ".ins", ".isp", ".jar", ".js", ".jse", ".lnk", ".msc",
            ".msi", ".msp", ".mst", ".pif", ".ps1", ".reg", ".scr",
            ".sct", ".shb", ".sys", ".url", ".vb", ".vbe", ".vbs",
            ".ws", ".wsc", ".wsf", ".wsh"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".gif"] = "image/gif",
            [".bmp"] = "image/bmp",
            [".webp"] = "image/webp",
            [".tif"] = "image/tiff",
            [".tiff"] = "image/tiff",
            [".pdf"] = "application/pdf",
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".csv"] = "text/csv",
            [".txt"] = "text/plain",
            [".rtf"] = "application/rtf",
            [".ppt"] = "application/vnd.ms-powerpoint",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".zip"] = "application/zip"
        };

    public ClientAttachmentStorageConfiguration GetConfiguration() =>
        _store.GetConfiguration();

    public ClientInfoAttachment Upload(
        int clientId,
        string sourcePath,
        string? category = null,
        string? caption = null)
    {
        if (clientId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientId));
        }

        var configuration = GetRequiredConfiguration();
        var source = new FileInfo(sourcePath);
        if (!source.Exists)
        {
            throw new FileNotFoundException(
                "The selected attachment no longer exists.",
                sourcePath);
        }

        var extension = NormalizeExtension(source.Extension);
        ValidateExtension(extension, configuration.AllowedExtensions);
        if (source.Length > configuration.MaximumFileSizeBytes)
        {
            throw new InvalidOperationException(
                $"{source.Name} is {FormatBytes(source.Length)}, which exceeds the "
                + $"{configuration.MaximumFileSizeMegabytes} MB attachment limit.");
        }

        var contentType = ContentTypes.GetValueOrDefault(
            extension,
            "application/octet-stream");
        var storageKind = contentType.StartsWith(
            "image/",
            StringComparison.OrdinalIgnoreCase)
            ? "Photos"
            : "Documents";
        var attachmentId = Guid.NewGuid();
        var relativePath = Path.Combine(
            $"Client-{clientId:D6}",
            storageKind,
            $"{attachmentId:N}{extension}");
        var destination = ResolvePath(configuration.RootPath, relativePath);
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException(
                "TechBench could not resolve the attachment destination folder.");
        Directory.CreateDirectory(destinationDirectory);

        var temporaryDestination = destination
            + $".uploading-{Guid.NewGuid():N}";
        byte[] hash;
        try
        {
            hash = CopyAndHash(source.FullName, temporaryDestination);
            File.Move(temporaryDestination, destination, overwrite: false);
        }
        catch
        {
            TryDelete(temporaryDestination);
            throw;
        }

        try
        {
            return _store.Save(new ClientInfoAttachment
            {
                AttachmentId = attachmentId,
                ClientId = clientId,
                RelativePath = relativePath.Replace('/', '\\'),
                OriginalFileName = source.Name,
                ContentType = contentType,
                Category = NormalizeCategory(category, storageKind),
                Caption = caption?.Trim() ?? string.Empty,
                FileSizeBytes = source.Length,
                ContentSha256 = hash
            });
        }
        catch
        {
            // Never leave a new file behind when its SQL metadata could not be
            // committed. Existing files are never deleted by this service.
            TryDelete(destination);
            throw;
        }
    }

    public ClientInfoAttachment SaveMetadata(
        ClientInfoAttachment attachment,
        string category,
        string? caption) =>
        _store.Save(attachment with
        {
            Category = NormalizeCategory(category, attachment.IsImage
                ? "Photos"
                : "Documents"),
            Caption = caption?.Trim() ?? string.Empty
        });

    public ClientInfoAttachment SetArchived(
        ClientInfoAttachment attachment,
        bool isArchived) =>
        _store.SetArchived(attachment, isArchived);

    public string ResolvePath(ClientInfoAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return ResolvePath(
            GetRequiredConfiguration().RootPath,
            attachment.RelativePath);
    }

    public static string ResolvePath(string rootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new InvalidOperationException(
                "Client attachment storage has not been configured in TechBench Server Manager.");
        }

        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                "The attachment record contains an invalid relative path.");
        }

        var root = Path.GetFullPath(rootPath.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The attachment path escapes the configured storage root.");
        }

        return candidate;
    }

    public static IReadOnlySet<string> ParseAllowedExtensions(string value) =>
        value.Split(
                [',', ';', ' ', '\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Select(NormalizeExtension)
            .Where(extension => extension.Length > 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static void ValidateExtension(
        string extension,
        string allowedExtensions)
    {
        var normalized = NormalizeExtension(extension);
        if (ProhibitedExtensions.Contains(normalized))
        {
            throw new InvalidOperationException(
                $"TechBench blocks the potentially executable attachment type {normalized}.");
        }

        var allowed = ParseAllowedExtensions(allowedExtensions);
        if (allowed.Count == 0 || !allowed.Contains(normalized))
        {
            throw new InvalidOperationException(
                $"The attachment type {normalized} is not allowed by Server Manager.");
        }
    }

    private ClientAttachmentStorageConfiguration GetRequiredConfiguration()
    {
        var configuration = GetConfiguration();
        if (!configuration.IsConfigured)
        {
            throw new InvalidOperationException(
                "Client attachment storage has not been configured in TechBench Server Manager.");
        }

        return configuration;
    }

    private static byte[] CopyAndHash(string sourcePath, string destinationPath)
    {
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
        }

        destination.Flush(flushToDisk: true);
        return hash.GetHashAndReset();
    }

    private static string NormalizeExtension(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Length == 0 || normalized.StartsWith('.')
            ? normalized
            : "." + normalized;
    }

    private static string NormalizeCategory(string? category, string fallback)
    {
        var normalized = category?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? fallback
            : normalized.Length <= 80
                ? normalized
                : normalized[..80];
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes / (1024d * 1024d):0.#} MB"
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original upload/database exception.
        }
    }

    private sealed class RepositoryAttachmentMetadataStore(
        ITechBenchRepository repository) : IClientAttachmentMetadataStore
    {
        public ClientAttachmentStorageConfiguration GetConfiguration() =>
            repository.GetClientAttachmentStorageConfiguration();

        public ClientInfoAttachment Save(ClientInfoAttachment attachment) =>
            repository.SaveClientInfoAttachment(attachment);

        public ClientInfoAttachment SetArchived(
            ClientInfoAttachment attachment,
            bool isArchived) =>
            repository.SetClientInfoAttachmentArchived(attachment, isArchived);
    }
}
