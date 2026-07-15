using TechBench.ViewModels;

namespace TechBench.Tests;

public sealed class WhdNoteSyncDecisionTests
{
    [Theory]
    [InlineData("Same", "Same", "Old", 0)]
    [InlineData("Local change", "Original", "Original", 1)]
    [InlineData("Original", "WHD change", "Original", 2)]
    [InlineData("Local change", "WHD change", "Original", 3)]
    [InlineData("Local change", "WHD change", null, 3)]
    public void ChoosesTheSafeThreeWaySynchronizationAction(
        string localNote,
        string remoteNote,
        string? lastSyncedNote,
        int expected)
    {
        Assert.Equal(
            (MainWindowViewModel.WhdNoteSyncDecision)expected,
            MainWindowViewModel.DecideWhdNoteSync(localNote, remoteNote, lastSyncedNote));
    }
}
