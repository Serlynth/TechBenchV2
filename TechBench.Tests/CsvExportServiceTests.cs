using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class CsvExportServiceTests
{
    [Fact]
    public void IncludesNoteMetadataAndManualClientName()
    {
        var csv = CsvExportService.BuildWorkEntryCsv(new[]
        {
            new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 14),
                ManualClientName = "Walk-in client",
                DurationMinutes = 30,
                Note = "Completed setup.",
                Tags = "setup, onsite",
                FollowUpState = FollowUpState.Waiting,
                FollowUpDueDate = new DateTime(2026, 7, 16)
            }
        });

        Assert.StartsWith("WorkDate,StartTime,EndTime,DurationMinutes,Client,Ticket,Billable,WHDPosted,SagePosted,Status,Tags,FollowUp,FollowUpDueDate,Note,InternalNote", csv);
        Assert.Contains("Walk-in client", csv);
        Assert.Contains("\"setup, onsite\",Waiting,2026-07-16", csv);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://example.test\")")]
    [InlineData("+cmd|' /C calc'!A0")]
    [InlineData("-2+3")]
    [InlineData("@SUM(1,2)")]
    [InlineData("  =1+1")]
    public void NeutralizesSpreadsheetFormulas(string dangerousText)
    {
        var csv = CsvExportService.BuildWorkEntryCsv(new[]
        {
            new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 13),
                ManualClientName = dangerousText,
                Note = dangerousText,
                DurationMinutes = 15
            }
        });

        Assert.Contains($"'{dangerousText}".Replace("\"", "\"\""), csv);
        Assert.DoesNotContain($",{dangerousText},", csv);
    }
}
