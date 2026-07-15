using TechBench.Models;

namespace TechBench.Tests;

public sealed class WorkEntryTagsTests
{
    [Fact]
    public void ParseTrimsAndDeduplicatesTagsWithoutChangingTheirOrder()
    {
        var tags = WorkEntryTags.Parse(" onsite, Project, onsite,  Waiting ");

        Assert.Equal(["onsite", "Project", "Waiting"], tags);
        Assert.Equal("onsite, Project, Waiting", WorkEntryTags.Normalize(" onsite, Project, onsite, Waiting "));
    }

    [Fact]
    public void AddPreservesExistingTagsAndIgnoresDuplicateCasing()
    {
        Assert.Equal("network, onsite", WorkEntryTags.Add("network", "onsite"));
        Assert.Equal("network, onsite", WorkEntryTags.Add("network, onsite", "NETWORK"));
    }
}
