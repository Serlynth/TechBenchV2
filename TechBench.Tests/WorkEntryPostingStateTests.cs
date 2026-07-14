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
}
