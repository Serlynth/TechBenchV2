using TechBench.SyncService;

namespace TechBench.Tests;

public sealed class ServerAutomaticClientMatchingTests
{
    [Fact]
    public void ServiceMatcherAppliesUniqueStrongPairsAndLeavesCompetingLocationsUnmatched()
    {
        var candidates = new[]
        {
            Candidate(1, "WHD", "Delancy Street Partners, LLC", "WHD-LOCATION-289"),
            Candidate(2, "WHD", "9Prime - Restaurant", "WHD-LOCATION-402"),
            Candidate(3, "WHD", "9Prime - Speak Easy", "WHD-LOCATION-403"),
            Candidate(4, "Sage", "Delancey Street Partners, LLC", sageCustomerId: "68710"),
            Candidate(5, "Sage", "9Prime", sageCustomerId: "68767")
        };

        var matches = ServerAutomaticClientMatcher.FindSafeAutomaticMatches(candidates);

        var match = Assert.Single(matches);
        Assert.Equal(1, match.WhdClient.Id);
        Assert.Equal(4, match.SageClient.Id);
        Assert.True(match.Score >= 0.86);
    }

    [Fact]
    public void SyncServiceRunsSafeMatchingAfterBothWhdAndSageCustomerSnapshots()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "TechBench.SyncService",
            "SyncSqlRepository.cs"));
        var project = File.ReadAllText(Path.Combine(
            root,
            "TechBench.SyncService",
            "TechBench.SyncService.csproj"));

        Assert.True(
            source.Split(
                "await ReconcileAutomaticClientMatchesAsync(cancellationToken)",
                StringSplitOptions.None).Length - 1 >= 2,
            "Both WHD and Sage customer snapshot paths must run automatic matching.");
        Assert.Contains(
            "[tb_service].[GetAutomaticClientMatchCandidates]",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[tb_service].[ApplyAutomaticClientMatch]",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ServerAutomaticClientMatcher.FindSafeAutomaticMatches(candidates)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "..\\Models\\Client.cs",
            project,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TechBenchV2.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the TechBench V2 repository root.");
    }

    private static AutomaticClientMatchCandidate Candidate(
        int id,
        string source,
        string name,
        string? externalId = null,
        string? sageCustomerId = null) =>
        new(
            id,
            name,
            source,
            externalId,
            true,
            source == "WHD" ? name : null,
            sageCustomerId,
            source == "Sage" ? name : null,
            new byte[8]);
}
