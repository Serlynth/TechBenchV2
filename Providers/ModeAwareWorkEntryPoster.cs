using TechBench.Models;

namespace TechBench.Providers;

public sealed class ModeAwareWorkEntryPoster : IWorkEntryPoster
{
    private readonly string _mockModeSettingKey;
    private readonly IWorkEntryPoster _mockPoster;
    private readonly IWorkEntryPoster _livePoster;

    public ModeAwareWorkEntryPoster(
        string mockModeSettingKey,
        IWorkEntryPoster mockPoster,
        IWorkEntryPoster livePoster)
    {
        _mockModeSettingKey = mockModeSettingKey;
        _mockPoster = mockPoster;
        _livePoster = livePoster;
    }

    public string DestinationName => _livePoster.DestinationName;

    public Task<PostingResult> PostAsync(
        WorkEntry entry,
        Client client,
        Ticket? ticket,
        IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken = default)
    {
        var useMockPoster = settings.TryGetValue(_mockModeSettingKey, out var configuredValue)
            && bool.TryParse(configuredValue, out var mockModeEnabled)
            && mockModeEnabled;

        var poster = useMockPoster ? _mockPoster : _livePoster;
        return poster.PostAsync(entry, client, ticket, settings, cancellationToken);
    }
}
