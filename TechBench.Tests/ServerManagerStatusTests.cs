using TechBench.ServerManager;

namespace TechBench.Tests;

public sealed class ServerManagerStatusTests
{
    [Fact]
    public void RunningSyncLabelsStoredErrorAsPreviousFailure()
    {
        var status = new SyncStatus
        {
            Status = "Running",
            QueueDepth = 1,
            LastAttemptAtUtc = new DateTime(2026, 7, 29, 17, 4, 0, DateTimeKind.Utc),
            LastSuccessfulAtUtc = new DateTime(2026, 7, 29, 16, 26, 0, DateTimeKind.Utc),
            LastError = "Web Help Desk sync failed: The operation was canceled."
        };

        var text = ServerManagerForm.FormatStatus(status, sage: false);

        Assert.Contains("Health: Running: Synchronization is in progress.", text, StringComparison.Ordinal);
        Assert.Contains("Previous failure: Web Help Desk sync failed", text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Health: Running: Web Help Desk sync failed",
            text,
            StringComparison.Ordinal);
    }
}
