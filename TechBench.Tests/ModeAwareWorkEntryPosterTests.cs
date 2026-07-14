using TechBench.Models;
using TechBench.Providers;

namespace TechBench.Tests;

public sealed class ModeAwareWorkEntryPosterTests
{
    [Theory]
    [InlineData(null, "live")]
    [InlineData("not-a-boolean", "live")]
    [InlineData("true", "mock")]
    [InlineData("false", "live")]
    public async Task SelectsPosterFromExplicitMockSetting(string? configuredValue, string expectedMessage)
    {
        var settings = new Dictionary<string, string>();
        if (configuredValue is not null)
        {
            settings["Destination.MockMode"] = configuredValue;
        }

        var poster = new ModeAwareWorkEntryPoster(
            "Destination.MockMode",
            new StubPoster("mock"),
            new StubPoster("live"));

        var result = await poster.PostAsync(new WorkEntry(), new Client(), null, settings);

        Assert.Equal(expectedMessage, result.Message);
    }

    [Fact]
    public async Task MockPostersNeverMarkEntriesPosted()
    {
        var settings = new Dictionary<string, string>
        {
            ["Whd.MockMode"] = "true",
            ["Sage.MockMode"] = "true"
        };
        var entry = new WorkEntry { Id = 1, DurationMinutes = 15 };

        var whdResult = await new MockWhdPoster().PostAsync(entry, new Client(), null, settings);
        var sageResult = await new MockSagePoster().PostAsync(entry, new Client(), null, settings);

        Assert.True(whdResult.Success);
        Assert.False(whdResult.MarkPosted);
        Assert.True(sageResult.Success);
        Assert.False(sageResult.MarkPosted);
    }

    private sealed class StubPoster(string message) : IWorkEntryPoster
    {
        public string DestinationName => "Test";

        public Task<PostingResult> PostAsync(
            WorkEntry entry,
            Client client,
            Ticket? ticket,
            IReadOnlyDictionary<string, string> settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PostingResult.Succeeded(message, string.Empty));
        }
    }
}
