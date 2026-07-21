using System.Text;
using TechBench.ServerManager;

namespace TechBench.Tests;

public sealed class NativeServerSetupTests
{
    [Fact]
    public void ExistingPackageMarkerPermitsOnlySameSchemaUpdatesAndAcceptsBom()
    {
        var root = Path.Combine(Path.GetTempPath(), "TechBench-Setup-Test-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths("test", root, Path.Combine(root, "data"), Path.Combine(root, "manager"), Path.Combine(root, "manager-data"), Path.Combine(root, "shortcut.lnk"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "package-manifest.json"), """
                { "Product": "TechBench Sync Service", "PackageFormatVersion": 1, "RequiredDatabaseSchemaVersion": 7 }
                """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Assert.True(PackageInstaller.InstalledPackageDeclaresRequiredSchema(paths, 7));
            Assert.False(PackageInstaller.InstalledPackageDeclaresRequiredSchema(paths, 8));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SqlLoginFailureExplainsTheAdTokenRepair()
    {
        var message = SqlAdminRepository.DescribeDatabaseLoginFailure("CSRI\\rwaters", "TechBench");
        Assert.Contains("CSRI\\rwaters", message, StringComparison.Ordinal);
        Assert.Contains("CSRI\\TechBench_Admins", message, StringComparison.Ordinal);
        Assert.Contains("fully sign out", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("whoami /groups", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetupIsAnElevatedNativeExeWithEmbeddedVerifiedPayload()
    {
        var project = ReadRepositoryFile("TechBench.ServerSetup", "TechBench.ServerSetup.csproj");
        var manifest = ReadRepositoryFile("TechBench.ServerSetup", "app.manifest");
        var package = ReadRepositoryFile("TechBench.ServerSetup", "EmbeddedPackage.cs");
        var engine = ReadRepositoryFile("TechBench.ServerSetup", "SetupEngine.cs");
        var publisher = ReadRepositoryFile("scripts", "Publish-TechBenchServer.ps1");

        Assert.Contains("<OutputType>WinExe</OutputType>", project, StringComparison.Ordinal);
        Assert.Contains("TechBench.ServerSetup.Payload.zip", project, StringComparison.Ordinal);
        Assert.Contains("level=\"requireAdministrator\"", manifest, StringComparison.Ordinal);
        Assert.Contains("PackageManifest.LoadAndVerify", package, StringComparison.Ordinal);
        Assert.Contains("PackageInstaller.Apply(package.Directory", engine, StringComparison.Ordinal);
        Assert.Contains("InstalledPackageDeclaresRequiredSchema", ReadRepositoryFile("TechBench.ServerManager", "ReleaseUpdater.cs"), StringComparison.Ordinal);
        Assert.Contains("CreateNoWindow = true", engine, StringComparison.Ordinal);
        Assert.Contains("TechBenchServerSetup.exe", publisher, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
