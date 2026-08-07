using TechBench.Models;

namespace TechBench.Tests;

public sealed class ClientCanonicalLinkStatusTests
{
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    public void ExternalSourceEligibilityRejectsAnyClientInfoWorkspace(
        bool isActive,
        bool isClientInfoLive,
        bool hasClientInfoWorkspace,
        bool expected)
    {
        var client = new Client
        {
            IsActive = isActive,
            IsClientInfoLive = isClientInfoLive,
            HasClientInfoWorkspace = hasClientInfoWorkspace
        };

        Assert.Equal(expected, client.IsExternalSourceLinkEligible);
    }

    [Theory]
    [InlineData(true, false, false, "TB only")]
    [InlineData(true, true, false, "WHD linked")]
    [InlineData(true, false, true, "Sage linked")]
    [InlineData(true, true, true, "Fully linked")]
    [InlineData(false, true, false, "Needs review")]
    [InlineData(false, false, true, "Needs review")]
    [InlineData(false, true, true, "Needs review")]
    public void DescribesCanonicalAndSourceOnlyLinkStates(
        bool isClientInfoLive,
        bool hasWhdIdentity,
        bool hasSageIdentity,
        string expected)
    {
        var client = new Client
        {
            Id = 735,
            Name = "Marrone & O'Rourke",
            IsClientInfoLive = isClientInfoLive,
            HasWhdIdentity = hasWhdIdentity,
            HasSageIdentity = hasSageIdentity
        };

        Assert.Equal(expected, client.CanonicalLinkStatusLabel);
        Assert.Equal("ID 735", client.InternalIdLabel);
    }
}
