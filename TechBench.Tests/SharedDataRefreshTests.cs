using TechBench.ViewModels;

namespace TechBench.Tests;

public sealed class SharedDataRefreshTests
{
    [Theory]
    [InlineData("1", 1)]
    [InlineData("15", 15)]
    [InlineData("120", 120)]
    [InlineData("0", 1)]
    [InlineData("999", 120)]
    [InlineData("not-a-number", 7)]
    public void RefreshIntervalIsClampedAndUsesSafeFallback(
        string value,
        int expected)
    {
        Assert.Equal(
            expected,
            MainWindowViewModel.NormalizeSharedDataRefreshIntervalMinutes(
                value,
                fallback: 7));
    }
}
