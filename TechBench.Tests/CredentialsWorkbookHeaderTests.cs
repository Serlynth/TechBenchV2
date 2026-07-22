using TechBench.SyncService;

namespace TechBench.Tests;

public sealed class CredentialsWorkbookHeaderTests
{
    [Theory]
    [InlineData("*if enabled -Firebox-DB\\csri")]
    [InlineData("*if enabled-Firebox-DB\\csri")]
    [InlineData("  *IF   ENABLED   -Firebox-DB\\csri  ")]
    public void FireboxDatabaseHeaderAcceptsCurrentAndLegacySpellings(string header)
    {
        Assert.True(FireDrillSyncEngine.IsExpectedHeader(5, header));
    }

    [Fact]
    public void ActualWorkbookHeadersAreAcceptedInOrder()
    {
        string[] headers =
        [
            "Client", "Firebox IP", "Status", "Admin", "csriadmin",
            "*if enabled -Firebox-DB\\csri", "Authpoint User", "sslvpnpassword",
            "AD Auth User", "AD Password", "RustPW"
        ];

        Assert.All(headers.Select((header, index) => (header, index)),
            item => Assert.True(FireDrillSyncEngine.IsExpectedHeader(item.index, item.header)));
    }

    [Fact]
    public void WrongColumnHeaderIsRejected()
    {
        Assert.False(FireDrillSyncEngine.IsExpectedHeader(5, "Firebox database password"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RowWithoutClientIsSkippedEvenWhenOtherCellsContainData(string? client)
    {
        Assert.True(FireDrillSyncEngine.ShouldSkipRow(client));
    }

    [Fact]
    public void RowWithClientIsImported()
    {
        Assert.False(FireDrillSyncEngine.ShouldSkipRow("Example Client"));
    }
}
