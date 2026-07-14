using System.Collections.ObjectModel;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed class HistoryWorkGroup
{
    public string Header { get; init; } = string.Empty;
    public string DateRangeLabel { get; init; } = string.Empty;
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public ObservableCollection<WorkEntry> Entries { get; init; } = new();
    public ObservableCollection<DayWorkGroup> DayGroups { get; init; } = new();
    public bool HasDayGroups => DayGroups.Count > 0;

    public int EntryCount => Entries.Count;
    public int BillableMinutes => Entries.Where(static entry => entry.Billable).Sum(static entry => entry.DurationMinutes);
    public int NonBillableMinutes => Entries.Where(static entry => !entry.Billable).Sum(static entry => entry.DurationMinutes);
    public int TotalMinutes => BillableMinutes + NonBillableMinutes;
    public int WhdPendingCount => Entries.Count(static entry => entry.NeedsWhdPosting);
    public int SagePendingCount => Entries.Count(static entry => entry.NeedsSagePosting);

    public string TotalLabel => FormatMinutes(TotalMinutes);
    public string BillableLabel => FormatMinutes(BillableMinutes);
    public string NonBillableLabel => FormatMinutes(NonBillableMinutes);
    public string Summary => $"{EntryCount} entries | Total {TotalLabel} | Billable {BillableLabel} | Non-billable {NonBillableLabel}";
    public string PostingSummary => $"WHD pending {WhdPendingCount} | Sage pending {SagePendingCount}";

    private static string FormatMinutes(int minutes)
    {
        var hours = minutes / 60;
        var remainder = minutes % 60;
        return hours > 0 ? $"{hours}h {remainder:00}m" : $"{remainder}m";
    }
}
