using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class GoogleSheetsCsvImportServiceTests
{
    [Fact]
    public void ParsesGroupedSheetRowsQuotedNotesAndMixedDurations()
    {
        var client = new Client { Id = 7, Name = "Marrone" };
        const string csv = """
            6/8/2026,,
            marrone,45,"Updated PC, verified Windows and email."

            6/9/2026,,
            marrone,1.5,Completed follow-up work.
            """;

        var rows = GoogleSheetsCsvImportService.Parse(
            csv,
            WorklogImportDurationMode.MixedHoursAndMinutes,
            [client],
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["marrone"] = client.Id },
            []);

        Assert.Equal(2, rows.Count);
        Assert.Equal(45, rows[0].DurationMinutes);
        Assert.Equal("Updated PC, verified Windows and email.", rows[0].Note);
        Assert.Equal(90, rows[1].DurationMinutes);
        Assert.All(rows, row => Assert.Equal(client.Id, row.SelectedClient?.Id));
    }

    [Fact]
    public void DetectsPotentialDuplicateAndLeavesUnmatchedClientCustom()
    {
        const string csv = "7/14/2026,unknown,15,Updated workstation.";
        var existing = new WorkEntry
        {
            WorkDate = new DateTime(2026, 7, 14),
            ManualClientName = "unknown",
            DurationMinutes = 15,
            Note = "Updated workstation."
        };

        var row = Assert.Single(GoogleSheetsCsvImportService.Parse(
            csv,
            WorklogImportDurationMode.AllMinutes,
            [],
            new Dictionary<string, int>(),
            [existing]));

        Assert.Null(row.SelectedClient);
        Assert.True(row.IsDuplicate);
        Assert.False(row.IsSelected);
        Assert.Equal("unknown", row.BuildEntry().ManualClientName);
    }
}
