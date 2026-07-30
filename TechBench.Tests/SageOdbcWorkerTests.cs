using TechBench.Services;

namespace TechBench.Tests;

public sealed class SageOdbcWorkerTests
{
    [Fact]
    public void ServerWorkerUsesEstablishedCustomerNormalization()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "TechBench.SageOdbcWorker",
            "Program.cs"));

        Assert.Contains("preserveInvalidRows: false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("preserveInvalidRows: true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnknownWorkerOperationsWithoutTouchingOdbc()
    {
        var request = new SageOdbcWorkerRequest(
            "unknown",
            "dsn",
            "user",
            "password",
            Verification: null,
            MaxRows: 0,
            IncludeInactive: false);

        var error = Assert.Throws<InvalidOperationException>(() => SageOdbcWorker.Execute(request));

        Assert.Contains("Unsupported", error.Message);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TechBenchV2.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the TechBench V2 repository root.");
    }
}
