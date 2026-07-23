using TechBench.Models;
using TechBench.Providers;

namespace TechBench.Tests;

public sealed class WhdRestPosterTests
{
    [Fact]
    public void WhdNoteTimestampUsesTheEntryWorkDateAndStartTime()
    {
        var entry = new WorkEntry
        {
            WorkDate = new DateTime(2026, 7, 20),
            HasTimeRange = true,
            StartTime = new TimeSpan(9, 30, 0)
        };

        var timestampUtc = WhdRestPoster.GetWhdNoteTimestampUtc(entry);
        var localTimestamp = timestampUtc.ToLocalTime();

        Assert.Equal(entry.WorkDate.Date, localTimestamp.Date);
        Assert.Equal(entry.StartTime, localTimestamp.TimeOfDay);
    }

    [Fact]
    public void PersonalPostingUsesUserCredentialsAndIgnoresServerAuthenticationMode()
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Whd.BaseUrl"] = " https://whd.example.test:8443 ",
            ["Whd.Username"] = " rsk_user ",
            ["Whd.ApiToken"] = "user-private-password-or-token",
            ["Whd.AuthenticationMode"] = WhdAuthenticationMode.ApplicationApiKey.ToString()
        };

        var personal = WhdRestPoster.BuildPersonalWhdConnectionSettings(settings);

        Assert.Equal("https://whd.example.test:8443", personal.BaseUrl);
        Assert.Equal("rsk_user", personal.Username);
        Assert.Equal("user-private-password-or-token", personal.Secret);
        Assert.Equal(WhdAuthenticationMode.Auto, personal.AuthenticationMode);
    }
}
