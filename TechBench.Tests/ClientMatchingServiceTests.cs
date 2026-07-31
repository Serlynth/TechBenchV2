using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class ClientMatchingServiceTests
{
    [Theory]
    [InlineData("FRIEND'S CENTRAL SCHOOL", "Friends Central School")]
    [InlineData("Marrone & O'Rourke, LLC", "Marrone and ORourke")]
    [InlineData("The Example Company Inc.", "Example Co")]
    public void NormalizedCompanyNamesIgnoreFormattingAndLegalSuffixes(string first, string second)
    {
        Assert.Equal(
            ClientMatchingService.NormalizeCompanyName(first),
            ClientMatchingService.NormalizeCompanyName(second));
    }

    [Fact]
    public void SuggestsFriendsCentralDespiteMinorSpellingAndSuffixDifferences()
    {
        var whd = new Client
        {
            Id = 1,
            Source = "WHD",
            WhdLocationName = "Freinds Central"
        };
        var correct = new Client
        {
            Id = 2,
            Source = "Sage",
            SageCustomerId = "30462",
            SageCustomerName = "FRIEND'S CENTRAL SCHOOL"
        };
        var unrelated = new Client
        {
            Id = 3,
            Source = "Sage",
            SageCustomerId = "30464",
            SageCustomerName = "Germantown Friends School"
        };

        var suggestion = ClientMatchingService.FindBestSuggestion(whd, [unrelated, correct]);

        Assert.NotNull(suggestion);
        Assert.Equal(correct.Id, suggestion.Candidate.Id);
        Assert.True(suggestion.Score >= 0.68);
    }

    [Fact]
    public void SuggestsCurrentSageNameForAnAlreadyMatchedRenamedClient()
    {
        var client = new Client
        {
            Id = 463,
            Name = "Teeters Harvey Marrone & O'Rourke LLP",
            Source = "Both",
            WhdLocationName = "Marrone & O'Rourke LLP",
            SageCustomerId = "69832",
            SageCustomerName = "Marrone & O'Rourke"
        };

        var suggestion = ClientMatchingService.SuggestCanonicalName(client);

        Assert.Equal("Marrone & O'Rourke", suggestion);
    }

    [Fact]
    public void SuggestsCandidateNameOnlyAfterAReasonableWhdNameMatch()
    {
        var whd = new Client
        {
            Id = 1,
            Name = "Marrone & O'Rourke LLP",
            Source = "WHD",
            WhdLocationName = "Marrone & O'Rourke LLP"
        };
        var sage = new Client
        {
            Id = 2,
            Name = "Marrone & O'Rourke",
            Source = "Sage",
            SageCustomerId = "69832",
            SageCustomerName = "Marrone & O'Rourke"
        };

        Assert.Equal(
            "Marrone & O'Rourke",
            ClientMatchingService.SuggestCanonicalName(whd, sage));
    }

    [Fact]
    public void AutomaticallyMatchesUniqueDelanceyAndDevineLocationPairs()
    {
        var whdClients = new[]
        {
            new Client
            {
                Id = 1,
                Name = "Delancy Street Partners, LLC",
                Source = "WHD",
                ExternalId = "WHD-LOCATION-289",
                WhdLocationName = "Delancy Street Partners, LLC"
            },
            new Client
            {
                Id = 2,
                Name = "Devine & Partners",
                Source = "WHD",
                ExternalId = "WHD-LOCATION-63",
                WhdLocationName = "Devine & Partners"
            },
            new Client
            {
                Id = 5,
                Name = "Friends Central",
                Source = "WHD",
                ExternalId = "WHD-LOCATION-44",
                WhdLocationName = "Friends Central"
            }
        };
        var sageClients = new[]
        {
            new Client
            {
                Id = 3,
                Name = "Delancey Street Partners, LLC",
                Source = "Sage",
                SageCustomerId = "68710",
                SageCustomerName = "Delancey Street Partners, LLC"
            },
            new Client
            {
                Id = 4,
                Name = "DEVINE & PARTNERS COMMUNICATIONS GROUP",
                Source = "Sage",
                SageCustomerId = "19104",
                SageCustomerName = "DEVINE & PARTNERS COMMUNICATIONS GROUP"
            },
            new Client
            {
                Id = 6,
                Name = "FRIEND'S CENTRAL SCHOOL",
                Source = "Sage",
                SageCustomerId = "30462",
                SageCustomerName = "FRIEND'S CENTRAL SCHOOL"
            }
        };

        var matches = ClientMatchingService.FindSafeAutomaticMatches(whdClients, sageClients);

        Assert.Equal(3, matches.Count);
        Assert.Contains(matches, match => match.WhdClient.Id == 1 && match.SageClient.Id == 3);
        Assert.Contains(matches, match => match.WhdClient.Id == 2 && match.SageClient.Id == 4);
        Assert.Contains(matches, match => match.WhdClient.Id == 5 && match.SageClient.Id == 6);
    }

    [Fact]
    public void DoesNotAutomaticallyMatchAmbiguousSimilarLocations()
    {
        var whd = new Client
        {
            Id = 1,
            Name = "Alpha School",
            Source = "WHD",
            ExternalId = "WHD-LOCATION-1",
            WhdLocationName = "Alpha School"
        };
        var sageClients = new[]
        {
            new Client
            {
                Id = 2,
                Source = "Sage",
                SageCustomerId = "A1",
                SageCustomerName = "Alpha School East"
            },
            new Client
            {
                Id = 3,
                Source = "Sage",
                SageCustomerId = "A2",
                SageCustomerName = "Alpha School West"
            }
        };

        Assert.Empty(ClientMatchingService.FindSafeAutomaticMatches([whd], sageClients));
    }

    [Fact]
    public void DoesNotAutomaticallyMatchGenericShortPrefix()
    {
        var whd = new Client
        {
            Id = 1,
            Name = "Main Line",
            Source = "WHD",
            ExternalId = "WHD-LOCATION-1",
            WhdLocationName = "Main Line"
        };
        var sage = new Client
        {
            Id = 2,
            Source = "Sage",
            SageCustomerId = "M1",
            SageCustomerName = "Main Line Health"
        };

        Assert.Empty(ClientMatchingService.FindSafeAutomaticMatches([whd], [sage]));
    }

    [Fact]
    public void DoesNotAutomaticallyChooseWhenMultipleWhdLocationsCompeteForOneSageCustomer()
    {
        var whdClients = new[]
        {
            new Client
            {
                Id = 1,
                Name = "9Prime - Restaurant",
                Source = "WHD",
                ExternalId = "WHD-LOCATION-402",
                WhdLocationName = "9Prime - Restaurant"
            },
            new Client
            {
                Id = 2,
                Name = "9Prime - Speak Easy",
                Source = "WHD",
                ExternalId = "WHD-LOCATION-403",
                WhdLocationName = "9Prime - Speak Easy"
            }
        };
        var sage = new Client
        {
            Id = 3,
            Name = "9Prime",
            Source = "Sage",
            SageCustomerId = "68767",
            SageCustomerName = "9Prime"
        };

        Assert.Empty(ClientMatchingService.FindSafeAutomaticMatches(whdClients, [sage]));
    }

    [Fact]
    public void AutomaticallyGroupsNumberedWhdLocationsUnderOneSageCustomer()
    {
        var whdClients = new[]
        {
            new Client
            {
                Id = 1,
                Name = "People for People 700",
                Source = "WHD",
                ExternalId = "WHD-LOCATION-299",
                WhdLocationName = "People for People 700"
            },
            new Client
            {
                Id = 2,
                Name = "People for People 800",
                Source = "WHD",
                ExternalId = "WHD-LOCATION-298",
                WhdLocationName = "People for People 800"
            }
        };
        var sage = new Client
        {
            Id = 3,
            Name = "People for People Charter School",
            Source = "Sage",
            SageCustomerId = "37313",
            SageCustomerName = "People for People Charter School"
        };

        var matches = ClientMatchingService.FindSafeAutomaticMatches(whdClients, [sage]);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.Equal(sage.Id, match.SageClient.Id));
        Assert.Contains(matches, match => match.WhdClient.Id == 1);
        Assert.Contains(matches, match => match.WhdClient.Id == 2);
    }
}
