using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class ClientSearchMatcherTests
{
    private static readonly Client PhiladelphiaMontessori = new()
    {
        Id = 76,
        Name = "Philadelphia Montessori Charter School",
        WhdLocationName = "PMCS",
        WhdContactName = "Amanda Wilson",
        SageCustomerId = "19555"
    };

    [Theory]
    [InlineData("a mont")]
    [InlineData(" A MONT ")]
    [InlineData("PMCS")]
    [InlineData("amanda")]
    [InlineData("19555")]
    public void MatchesClientNameAndAlternateFieldsWithoutSql(string searchTerm)
    {
        Assert.True(ClientSearchMatcher.Matches(
            PhiladelphiaMontessori,
            searchTerm));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptySearchMatchesEveryClient(string? searchTerm)
    {
        Assert.True(ClientSearchMatcher.Matches(
            PhiladelphiaMontessori,
            searchTerm));
    }

    [Fact]
    public void UnrelatedSearchDoesNotMatch()
    {
        Assert.False(ClientSearchMatcher.Matches(
            PhiladelphiaMontessori,
            "Marrone"));
    }
}
