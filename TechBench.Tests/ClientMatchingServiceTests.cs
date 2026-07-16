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
}
