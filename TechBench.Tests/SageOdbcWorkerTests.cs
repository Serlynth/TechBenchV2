using TechBench.Services;

namespace TechBench.Tests;

public sealed class SageOdbcWorkerTests
{
    [Fact]
    public void RejectsUnknownWorkerOperationsWithoutTouchingOdbc()
    {
        var request = new SageOdbcWorkerRequest(
            "unknown",
            "dsn",
            "user",
            "password",
            Verification: null,
            MaxRows: 0,
            IncludeInactive: false);

        var error = Assert.Throws<InvalidOperationException>(() => SageOdbcWorker.Execute(request));

        Assert.Contains("Unsupported", error.Message);
    }
}
