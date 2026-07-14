using System.Collections.ObjectModel;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed class DayWorkGroup
{
    public DateTime Date { get; init; }
    public ObservableCollection<WorkEntry> Entries { get; init; } = new();
    public ObservableCollection<DayWorkGroup> DayGroups { get; } = new();
    public bool HasDayGroups => false;
    public int BillableMinutes => Entries.Where(static entry => entry.Billable).Sum(static entry => entry.DurationMinutes);
    public int NonBillableMinutes => Entries.Where(static entry => !entry.Billable).Sum(static entry => entry.DurationMinutes);
    public int TotalMinutes => BillableMinutes + NonBillableMinutes;
    public string Header => $"{Date:dddd, MMM d}";
    public string Summary => $"Total {FormatMinutes(TotalMinutes)} | Billable {FormatMinutes(BillableMinutes)} | Non-billable {FormatMinutes(NonBillableMinutes)}";
    public string TotalLabel => FormatMinutes(TotalMinutes);
    public string BillableLabel => FormatMinutes(BillableMinutes);
    public string NonBillableLabel => FormatMinutes(NonBillableMinutes);

    private static string FormatMinutes(int minutes)
    {
        var hours = minutes / 60;
        var remainder = minutes % 60;
        return hours > 0 ? $"{hours}h {remainder:00}m" : $"{remainder}m";
    }
}
