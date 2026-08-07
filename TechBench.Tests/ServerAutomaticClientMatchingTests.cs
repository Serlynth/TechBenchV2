using TechBench.SyncService;

namespace TechBench.Tests;

public sealed class ServerAutomaticClientMatchingTests
{
    [Theory]
    [InlineData("WHD", false)]
    [InlineData("WHD", true)]
    [InlineData("Sage", false)]
    [InlineData("Sage", true)]
    public void LiveTechBenchClientSafelyAbsorbsAnExactSourceRegardlessOfArrivalOrder(
        string sourceSystem,
        bool sourceArrivesFirst)
    {
        var canonical = Candidate(
            100,
            "Manual",
            "Marrone & O'Rourke",
            isTechBenchLive: true);
        var source = sourceSystem == "WHD"
            ? Candidate(
                200,
                "WHD",
                "Marrone and O'Rourke LLP",
                "WHD-LOCATION-463")
            : Candidate(
                200,
                "Sage",
                "Marrone and O'Rourke LLP",
                sageCustomerId: "69832");
        var candidates = sourceArrivesFirst
            ? new[] { source, canonical }
            : new[] { canonical, source };

        var match = Assert.Single(
            ServerAutomaticClientMatcher.FindSafeCanonicalSourceMatches(candidates));

        Assert.Equal(canonical.Id, match.CanonicalClient.Id);
        Assert.Equal(source.Id, match.SourceClient.Id);
        Assert.Equal(sourceSystem, match.SourceSystem);
        Assert.True(match.Score >= 0.86);
    }

    [Fact]
    public void LiveTechBenchClientCanAbsorbIndependentWhdAndSageSources()
    {
        var candidates = new[]
        {
            Candidate(100, "Manual", "Northwind Accounting", isTechBenchLive: true),
            Candidate(200, "WHD", "Northwind Accounting LLC", "WHD-LOCATION-20"),
            Candidate(300, "Sage", "Northwind Accounting LLC", sageCustomerId: "30020")
        };

        var matches = ServerAutomaticClientMatcher.FindSafeCanonicalSourceMatches(candidates);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.Equal(100, match.CanonicalClient.Id));
        Assert.Contains(matches, match => match.SourceSystem == "WHD"
            && match.SourceClient.Id == 200);
        Assert.Contains(matches, match => match.SourceSystem == "Sage"
            && match.SourceClient.Id == 300);
    }

    [Fact]
    public void CanonicalSourceMatcherLeavesAmbiguousTechBenchTargetsUnmatched()
    {
        var candidates = new[]
        {
            Candidate(100, "Manual", "Northwind Accounting", isTechBenchLive: true),
            Candidate(101, "Manual", "Northwind Accounting LLC", isTechBenchLive: true),
            Candidate(200, "WHD", "Northwind Accounting", "WHD-LOCATION-20")
        };

        Assert.Empty(
            ServerAutomaticClientMatcher.FindSafeCanonicalSourceMatches(candidates));
    }

    [Fact]
    public void AmbiguousTechBenchTargetsAlsoBlockDuplicateSourcePairPromotion()
    {
        var candidates = new[]
        {
            Candidate(100, "Manual", "Marrone and O'Rourke", isTechBenchLive: true),
            Candidate(101, "Manual", "Marrone & O'Rourke", isTechBenchLive: true),
            Candidate(200, "WHD", "Marrone & O'Rourke", "WHD-LOCATION-200"),
            Candidate(300, "Sage", "Marrone & O'Rourke", sageCustomerId: "69832")
        };

        Assert.Empty(
            ServerAutomaticClientMatcher.FindSafeCanonicalSourceMatches(candidates));
        Assert.Empty(ServerAutomaticClientMatcher.FindSafeAutomaticMatches(candidates));
    }

    [Fact]
    public void CanonicalSourceMatcherSkipsSourcesAlreadyLinkedToTheCanonicalClient()
    {
        var candidates = new[]
        {
            Candidate(
                100,
                "Both",
                "Northwind Accounting",
                "WHD-LOCATION-10",
                "30010",
                isTechBenchLive: true),
            Candidate(200, "WHD", "Northwind Accounting", "WHD-LOCATION-20"),
            Candidate(300, "Sage", "Northwind Accounting", sageCustomerId: "30020")
        };

        Assert.Empty(
            ServerAutomaticClientMatcher.FindSafeCanonicalSourceMatches(candidates));
    }

    [Theory]
    [InlineData("WHD")]
    [InlineData("Sage")]
    public void LinkedExternalNameCanMatchTheSecondSourceWithoutRiskingTheCanonicalId(
        string existingSourceSystem)
    {
        var canonical = existingSourceSystem == "WHD"
            ? new AutomaticClientMatchCandidate(
                100,
                "Internal Account 100",
                "WHD",
                "WHD-LOCATION-20",
                true,
                "Northwind Accounting LLC",
                null,
                null,
                new byte[8],
                true)
            : new AutomaticClientMatchCandidate(
                100,
                "Internal Account 100",
                "Sage",
                null,
                true,
                null,
                "30020",
                "Northwind Accounting LLC",
                new byte[8],
                true);
        var source = existingSourceSystem == "WHD"
            ? Candidate(
                200,
                "Sage",
                "Northwind Accounting",
                sageCustomerId: "30020")
            : Candidate(
                200,
                "WHD",
                "Northwind Accounting",
                "WHD-LOCATION-20");
        var candidates = new[] { canonical, source };

        var canonicalMatch = Assert.Single(
            ServerAutomaticClientMatcher.FindSafeCanonicalSourceMatches(candidates));

        Assert.Equal(100, canonicalMatch.CanonicalClient.Id);
        Assert.Equal(200, canonicalMatch.SourceClient.Id);
        Assert.Empty(ServerAutomaticClientMatcher.FindSafeAutomaticMatches(candidates));
    }

    [Fact]
    public void LegacyPairMatcherNeverConsumesLiveTechBenchCandidates()
    {
        var candidates = new[]
        {
            Candidate(
                100,
                "WHD",
                "Northwind Accounting",
                "WHD-LOCATION-10",
                isTechBenchLive: true),
            Candidate(
                200,
                "Sage",
                "Northwind Accounting",
                sageCustomerId: "30020")
        };

        Assert.Empty(ServerAutomaticClientMatcher.FindSafeAutomaticMatches(candidates));
    }

    [Fact]
    public void LiveTechBenchClientWithSageSafelyAbsorbsANumberedWhdFamily()
    {
        var candidates = new[]
        {
            new AutomaticClientMatchCandidate(
                100,
                "Community Medical Group",
                "Sage",
                null,
                true,
                null,
                "30020",
                "Community Medical Group LLC",
                new byte[8],
                true),
            Candidate(
                200,
                "WHD",
                "Community Medical Group 1",
                "WHD-LOCATION-20"),
            Candidate(
                201,
                "WHD",
                "Community Medical Group 2",
                "WHD-LOCATION-21")
        };

        var match = Assert.Single(
            ServerAutomaticClientMatcher.FindSafeCanonicalSourceMatches(candidates));

        Assert.Equal(100, match.CanonicalClient.Id);
        Assert.Equal(200, match.SourceClient.Id);
        Assert.Equal("WHD", match.SourceSystem);
        Assert.Equal("COMMUNITY MEDICAL GROUP", match.SourceFamilyKey);
        Assert.Equal(
            new[] { 201 },
            Assert.IsAssignableFrom<IReadOnlyList<AutomaticClientMatchCandidate>>(
                    match.AdditionalWhdFamilyMembers)
                .Select(static client => client.Id));
        Assert.Empty(ServerAutomaticClientMatcher.FindSafeAutomaticMatches(candidates));
    }

    [Fact]
    public void NumberedWhdFamilyFinishesAfterSageArrivesSecond()
    {
        var canonical = new AutomaticClientMatchCandidate(
            100,
            "Preferred TechBench Name",
            "Both",
            "WHD-LOCATION-901",
            true,
            "Contoso Academy 1",
            "S-501",
            "Contoso Academy",
            new byte[8],
            true);
        var secondLocation = new AutomaticClientMatchCandidate(
            201,
            "Contoso Academy 2",
            "WHD",
            "WHD-LOCATION-902",
            true,
            "Contoso Academy 2",
            null,
            null,
            new byte[8]);

        var matches = ServerAutomaticClientMatcher
            .FindSafeCanonicalWhdFamilyMembers([canonical, secondLocation]);

        var match = Assert.Single(matches);
        Assert.Equal(100, match.SageClient.Id);
        Assert.Equal(201, match.WhdClient.Id);
        Assert.Equal(1d, match.Score);
    }

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
    public void ServiceMatcherGroupsNumberedWhdLocationsUnderOneSageCustomer()
    {
        var candidates = new[]
        {
            Candidate(1, "WHD", "People for People 700", "WHD-LOCATION-299"),
            Candidate(2, "WHD", "People for People 800", "WHD-LOCATION-298"),
            Candidate(3, "Sage", "People for People Charter School", sageCustomerId: "37313")
        };

        var matches = ServerAutomaticClientMatcher.FindSafeAutomaticMatches(candidates);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.Equal(3, match.SageClient.Id));
        Assert.Contains(matches, match => match.WhdClient.Id == 1);
        Assert.Contains(matches, match => match.WhdClient.Id == 2);
        Assert.All(matches, match => Assert.True(match.Score >= 0.86));
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
            "[tb_service].[ApplyAutomaticClientSourceMatch]",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[tb_service].[ApplyAutomaticWhdFamilyMember]",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ServerAutomaticClientMatcher.FindSafeCanonicalSourceMatches(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "MaxCanonicalSourceMatchesPerReconciliation",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var attemptedCanonicalIds = new HashSet<int>();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplyCanonicalWhdFamilyMembersAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetBoolean(reader, \"IsTechBenchLive\", false)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            ".GroupBy(static match => match.SageClient.Id)",
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
        string? sageCustomerId = null,
        bool isTechBenchLive = false) =>
        new(
            id,
            name,
            source,
            externalId,
            true,
            source == "WHD" ? name : null,
            sageCustomerId,
            source == "Sage" ? name : null,
            new byte[8],
            isTechBenchLive);
}
