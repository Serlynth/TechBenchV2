using System.Collections.ObjectModel;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.ViewModels;

public sealed class WorklogImportViewModel : ObservableObject
{
    private readonly string _csv;
    private readonly IReadOnlyDictionary<string, int> _aliases;
    private readonly IReadOnlyList<WorkEntry> _existingEntries;
    private WorklogImportDurationOption _selectedDurationOption;

    public WorklogImportViewModel(
        string fileName,
        string csv,
        IReadOnlyList<Client> clients,
        IReadOnlyDictionary<string, int> aliases,
        IReadOnlyList<WorkEntry> existingEntries)
    {
        FileName = fileName;
        _csv = csv;
        _aliases = aliases;
        _existingEntries = existingEntries;
        foreach (var client in clients.OrderBy(static client => client.Name, StringComparer.OrdinalIgnoreCase))
        {
            Clients.Add(client);
        }

        DurationOptions.Add(new WorklogImportDurationOption(
            WorklogImportDurationMode.MixedHoursAndMinutes,
            "Mixed: under 10 = hours; 10+ = minutes"));
        DurationOptions.Add(new WorklogImportDurationOption(
            WorklogImportDurationMode.AllMinutes,
            "All values are minutes"));
        DurationOptions.Add(new WorklogImportDurationOption(
            WorklogImportDurationMode.AllHours,
            "All values are hours"));
        _selectedDurationOption = DurationOptions[0];
        Reparse();
    }

    public string FileName { get; }
    public ObservableCollection<Client> Clients { get; } = new();
    public ObservableCollection<WorklogImportRowViewModel> Rows { get; } = new();
    public ObservableCollection<WorklogImportDurationOption> DurationOptions { get; } = new();

    public WorklogImportDurationOption SelectedDurationOption
    {
        get => _selectedDurationOption;
        set
        {
            if (value is not null && SetProperty(ref _selectedDurationOption, value))
            {
                Reparse();
            }
        }
    }

    public int SelectedCount => Rows.Count(static row => row.IsSelected && row.IsValid);
    public int DuplicateCount => Rows.Count(static row => row.IsDuplicate);
    public int UnmatchedCount => Rows.Count(static row => row.SelectedClient is null);
    public string Summary => $"{Rows.Count} rows | {SelectedCount} selected | {DuplicateCount} possible duplicates | {UnmatchedCount} custom clients";

    public IReadOnlyList<WorkEntry> BuildSelectedEntries()
    {
        return Rows
            .Where(static row => row.IsSelected && row.IsValid)
            .Select(static row => row.BuildEntry())
            .ToArray();
    }

    public IReadOnlyDictionary<string, int> BuildAliasMappings()
    {
        return Rows
            .Where(static row => row.IsSelected && row.SelectedClient is { Id: > 0 })
            .GroupBy(static row => row.SourceClient.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().SelectedClient!.Id,
                StringComparer.OrdinalIgnoreCase);
    }

    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(DuplicateCount));
        OnPropertyChanged(nameof(UnmatchedCount));
        OnPropertyChanged(nameof(Summary));
    }

    private void Reparse()
    {
        Rows.Clear();
        foreach (var row in GoogleSheetsCsvImportService.Parse(
                     _csv,
                     SelectedDurationOption.Value,
                     Clients,
                     _aliases,
                     _existingEntries))
        {
            row.PropertyChanged += (_, _) => RefreshSummary();
            Rows.Add(row);
        }

        RefreshSummary();
    }
}
