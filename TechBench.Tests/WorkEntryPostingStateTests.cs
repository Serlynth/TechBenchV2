using System.Collections.ObjectModel;
using TechBench.Models;
using TechBench.ViewModels;

namespace TechBench.Tests;

public sealed class WorkEntryPostingStateTests
{
    [Fact]
    public void NonBillableEntryDoesNotRequireOrShowPendingSagePosting()
    {
        var entry = new WorkEntry { Billable = false, SagePosted = false };

        Assert.False(entry.NeedsSagePosting);
        Assert.False(entry.ShowSageBadge);

        entry.SagePosted = true;
        Assert.False(entry.NeedsSagePosting);
        Assert.True(entry.ShowSageBadge);
    }

    [Fact]
    public void HistorySagePendingCountIncludesOnlyUnpostedBillableEntries()
    {
        var group = new HistoryWorkGroup
        {
            Entries = new ObservableCollection<WorkEntry>
            {
                new() { Billable = true, SagePosted = false },
                new() { Billable = true, SagePosted = true },
                new() { Billable = false, SagePosted = false }
            }
        };

        Assert.Equal(1, group.SagePendingCount);
    }

    [Fact]
    public void EditedWhdNoteRemainsPostedWithoutSynchronization()
    {
        var postedAt = DateTime.Now.AddMinutes(-5);
        var entry = new WorkEntry
        {
            TicketNumberText = "123",
            WhdPosted = true,
            WhdPostedAt = postedAt,
            UpdatedAt = postedAt.AddMinutes(1)
        };

        Assert.False(entry.NeedsWhdPosting);
        Assert.Equal("WHD posted", entry.WhdBadge);

        entry.SagePosted = true;
        entry.SagePostedAt = DateTime.Now;

        Assert.False(entry.NeedsWhdPosting);
    }

    [Fact]
    public void LegacyWhdSyncErrorsDoNotReopenPostedEntries()
    {
        var entry = new WorkEntry
        {
            TicketNumberText = "123",
            WhdPosted = true,
            WhdPostedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            LastError = "WHD sync conflict: Both versions changed."
        };

        Assert.False(entry.NeedsWhdPosting);
        Assert.Equal("WHD posted", entry.WhdBadge);
        Assert.True(entry.HasObsoleteWhdMutationError);
        Assert.Null(entry.DisplayLastError);
    }
}
