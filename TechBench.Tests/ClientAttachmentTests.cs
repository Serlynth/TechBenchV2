using System.Security.Cryptography;
using TechBench.Models;
using TechBench.Services;
using TechBench.ServerManager;

namespace TechBench.Tests;

public sealed class ClientAttachmentTests
{
    [Fact]
    public void UploadUsesInternalClientIdAndGeneratedPhotoName()
    {
        var directory = NewTestDirectory();
        try
        {
            var source = Path.Combine(directory, "Rack photo.jpg");
            var expected = RandomNumberGenerator.GetBytes(2048);
            File.WriteAllBytes(source, expected);
            var root = Path.Combine(directory, "storage");
            var store = new RecordingAttachmentStore(root, ".jpg,.pdf");
            var service = new ClientAttachmentStorageService(store);

            var attachment = service.Upload(76, source);

            Assert.StartsWith(
                @"Client-000076\Photos\",
                attachment.RelativePath,
                StringComparison.Ordinal);
            Assert.EndsWith(".jpg", attachment.RelativePath, StringComparison.Ordinal);
            Assert.DoesNotContain("Rack photo", attachment.RelativePath, StringComparison.Ordinal);
            Assert.Equal("Rack photo.jpg", attachment.OriginalFileName);
            Assert.Equal("image/jpeg", attachment.ContentType);
            Assert.Equal("Photos", attachment.Category);
            Assert.Equal(SHA256.HashData(expected), attachment.ContentSha256);
            Assert.Equal(
                expected,
                File.ReadAllBytes(service.ResolvePath(attachment)));
            Assert.Equal(attachment.AttachmentId, store.Saved?.AttachmentId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FailedMetadataCommitRemovesOnlyNewUpload()
    {
        var directory = NewTestDirectory();
        try
        {
            var source = Path.Combine(directory, "site.png");
            File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(64));
            var root = Path.Combine(directory, "storage");
            Directory.CreateDirectory(root);
            var existing = Path.Combine(root, "existing.txt");
            File.WriteAllText(existing, "keep");
            var store = new RecordingAttachmentStore(root, ".png")
            {
                ThrowOnSave = true
            };
            var service = new ClientAttachmentStorageService(store);

            Assert.Throws<InvalidOperationException>(
                () => service.Upload(42, source));

            Assert.True(File.Exists(existing));
            Assert.Empty(Directory.EnumerateFiles(
                Path.Combine(root, "Client-000042"),
                "*",
                SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExecutableTypesStayBlockedEvenWhenConfigured()
    {
        var directory = NewTestDirectory();
        try
        {
            var source = Path.Combine(directory, "unsafe.exe");
            File.WriteAllText(source, "not an executable");
            var service = new ClientAttachmentStorageService(
                new RecordingAttachmentStore(
                    Path.Combine(directory, "storage"),
                    ".jpg,.exe"));

            var exception = Assert.Throws<InvalidOperationException>(
                () => service.Upload(1, source));

            Assert.Contains("blocks", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResolvedAttachmentPathCannotEscapeStorageRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidOperationException>(() =>
            ClientAttachmentStorageService.ResolvePath(
                root,
                @"Client-000001\..\..\outside.txt"));
    }

    [Fact]
    public void ServerManagerNormalizesSafeExtensionsAndRejectsScripts()
    {
        Assert.Equal(
            ".jpeg,.jpg,.pdf",
            ServerManagerForm.NormalizeAttachmentExtensions(
                " JPG; .pdf, jpeg, .jpg "));
        Assert.Throws<InvalidOperationException>(() =>
            ServerManagerForm.NormalizeAttachmentExtensions(".jpg,.ps1"));
        Assert.Throws<InvalidOperationException>(() =>
            ServerManagerForm.NormalizeAttachmentExtensions(".pdf,.application"));
        Assert.Throws<InvalidOperationException>(() =>
            AttachmentStorageProbe.ValidateRootPath(@"C:\ClientAttachments"));
        Assert.Equal(
            @"\\server\share\ClientAttachments",
            AttachmentStorageProbe.ValidateRootPath(
                @"\\server\share\ClientAttachments\"));
    }

    [Fact]
    public void SqlAndClientUiKeepFilesOutOfTheDatabase()
    {
        var schema = ReadRepositoryFile(
            "database",
            "sqlserver2016",
            "38-V0015-ClientAttachmentsSchema.sql");
        var procedures = ReadRepositoryFile(
            "database",
            "sqlserver2016",
            "66-V0015-ClientAttachmentsProcedures.sql");
        var xaml = ReadRepositoryFile("ClientInfoBetaWindow.xaml");
        var manager = ReadRepositoryFile(
            "TechBench.ServerManager",
            "ServerManagerForm.cs");

        Assert.Contains("[RelativePath] nvarchar(400)", schema, StringComparison.Ordinal);
        Assert.Contains("[ContentSha256] binary(32)", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("varbinary(max)", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetClientAttachmentStorageConfiguration", procedures, StringComparison.Ordinal);
        Assert.Contains("WriteAuditEvent", procedures, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(procedures, "BEGIN TRANSACTION;"));
        Assert.Equal(2, CountOccurrences(procedures, "COMMIT TRANSACTION;"));
        Assert.Contains("Header=\"Attachments\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Attachments_Drop", xaml, StringComparison.Ordinal);
        Assert.Contains("BuildAttachmentStorageTab", manager, StringComparison.Ordinal);
        Assert.Contains("Test access", manager, StringComparison.Ordinal);
    }

    private static string NewTestDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchAttachmentTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(
                   search,
                   index,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "TechBenchV2.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(
            new[] { directory!.FullName }.Concat(parts).ToArray()));
    }

    private sealed class RecordingAttachmentStore(
        string rootPath,
        string allowedExtensions) : IClientAttachmentMetadataStore
    {
        public bool ThrowOnSave { get; init; }
        public ClientInfoAttachment? Saved { get; private set; }

        public ClientAttachmentStorageConfiguration GetConfiguration() => new()
        {
            RootPath = rootPath,
            MaximumFileSizeMegabytes = 10,
            AllowedExtensions = allowedExtensions
        };

        public ClientInfoAttachment Save(ClientInfoAttachment attachment)
        {
            if (ThrowOnSave)
            {
                throw new InvalidOperationException("SQL metadata write failed.");
            }

            Saved = attachment with
            {
                UploadedBy = "Test User",
                UploadedAtUtc = DateTime.UtcNow,
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 1]
            };
            return Saved;
        }

        public ClientInfoAttachment SetArchived(
            ClientInfoAttachment attachment,
            bool isArchived) => attachment with
        {
            IsArchived = isArchived
        };
    }
}
