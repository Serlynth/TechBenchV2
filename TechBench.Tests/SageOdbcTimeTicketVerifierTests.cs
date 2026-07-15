using TechBench.Models;
using TechBench.Services;
using TechBench.ViewModels;

namespace TechBench.Tests;

public sealed class SageOdbcTimeTicketVerifierTests
{
    [Theory]
    [InlineData("147775", "147775")]
    [InlineData(" SAGE-147775 ", "147775")]
    [InlineData("#147775", "147775")]
    public void NormalizesManualSageTicketNumbers(string input, string expected)
    {
        Assert.True(MainWindowViewModel.TryNormalizeManualSageTicketNumber(input, out var ticketNumber));
        Assert.Equal(expected, ticketNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SAGE-")]
    [InlineData("147A75")]
    [InlineData("https://example.com")]
    public void RejectsInvalidManualSageTicketNumbers(string input)
    {
        Assert.False(MainWindowViewModel.TryNormalizeManualSageTicketNumber(input, out _));
    }

    [Fact]
    public void SelectsOfficialSageTicketDatColumns()
    {
        var columns = new[]
        {
            "TicketNumber",
            "TicketType",
            "TicketDate",
            "Duration_Hours",
            "Duration_Minutes",
            "DurationUnitQty",
            "BillType",
            "CharMemo",
            "InternalMemo"
        };

        var table = SageOdbcTimeTicketVerifier.SelectTicketTable("Ticket", columns);

        Assert.NotNull(table);
        Assert.Equal("TicketNumber", table.TicketNumberColumn);
        Assert.Equal("TicketType", table.TicketTypeColumn);
        Assert.Equal("TicketDate", table.TicketDateColumn);
        Assert.Equal("Duration_Hours", table.DurationHoursColumn);
        Assert.Equal("Duration_Minutes", table.DurationMinutesColumn);
        Assert.Equal("BillType", table.BillTypeColumn);
        Assert.Equal(new[] { "CharMemo", "InternalMemo" }, table.MemoColumns);
    }

    [Fact]
    public void RejectsUnrelatedTables()
    {
        var table = SageOdbcTimeTicketVerifier.SelectTicketTable(
            "Customers",
            new[] { "CustomerID", "CustomerName" });

        Assert.Null(table);
    }

    [Theory]
    [InlineData(false, null, "Sage pending")]
    [InlineData(false, "147773", "Sage pending #147773")]
    [InlineData(true, "147773", "Sage posted #147773")]
    public void SageBadgeReflectsVerifiedStateOnly(bool posted, string? ticketNumber, string expected)
    {
        var entry = new WorkEntry { SagePosted = posted, SageTicketNumber = ticketNumber };

        Assert.Equal(expected, entry.SageBadge);
    }

    [Fact]
    public void AcceptsSageMemoTruncationWhenPrefixIsSubstantial()
    {
        const string expected = "Created the new scope on the server but did not activate it. Finished and updated the checklist.";
        const string sageMemo = "Created the new scope on the server but did not activate it.";

        Assert.True(SageOdbcTimeTicketVerifier.NotesCorrespond(sageMemo, expected));
        Assert.False(SageOdbcTimeTicketVerifier.NotesCorrespond("Different note text that is long enough to compare.", expected));
    }

    [Theory]
    [InlineData("Updated my PC.\0\0\0", "Updated my PC.")]
    [InlineData("  Updated\r\nmy   PC.  ", "Updated my PC.")]
    [InlineData("Updated my PC.\u00A0", "updated my pc.")]
    public void NormalizesZenMemoPaddingAndWhitespace(string sageMemo, string expected)
    {
        Assert.True(SageOdbcTimeTicketVerifier.NotesCorrespond(sageMemo, expected));
    }

    [Fact]
    public void AcceptsNoteFromEitherSageMemoColumn()
    {
        Assert.True(SageOdbcTimeTicketVerifier.MemoValuesCorrespond(
            new[] { string.Empty, "Updated my PC." },
            "Updated my PC."));
    }
}
