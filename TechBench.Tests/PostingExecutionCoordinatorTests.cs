using TechBench.Services;

namespace TechBench.Tests;

public sealed class PostingExecutionCoordinatorTests
{
    [Fact]
    public async Task AllowsOnlyOneOperationPerEntryAndDestination()
    {
        var coordinator = new PostingExecutionCoordinator();
        var first = await coordinator.TryAcquireAsync(42, "WHD");
        var blocked = await coordinator.TryAcquireAsync(42, "WHD");
        var otherDestination = await coordinator.TryAcquireAsync(42, "Sage");

        Assert.NotNull(first);
        Assert.Null(blocked);
        Assert.NotNull(otherDestination);

        await first.DisposeAsync();
        var retry = await coordinator.TryAcquireAsync(42, "WHD");
        Assert.NotNull(retry);

        await retry.DisposeAsync();
        await otherDestination.DisposeAsync();
    }
}
