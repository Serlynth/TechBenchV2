using System.IO;
using Microsoft.Data.SqlClient;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    partial void ImportGoogleSheetsCsv(string path)
    {
        try
        {
            var csv = File.ReadAllText(path);
            var clients = _repository.GetClients(includeInactive: true);
            var importViewModel = new WorklogImportViewModel(
                Path.GetFileName(path),
                csv,
                clients,
                _repository.GetClientAliases(),
                _repository.GetWorkEntries(new WorkEntryQuery()));
            var window = new WorklogImportWindow(importViewModel)
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            if (window.ShowDialog() != true)
            {
                return;
            }

            var entries = importViewModel.BuildSelectedEntries();
            if (entries.Count == 0)
            {
                return;
            }

            IsEntryOperationRunning = true;
            EntryOperationText = "Importing worklog notes...";
            var count = _repository.ImportWorkEntries(entries, importViewModel.BuildAliasMappings());
            RefreshAll();
            RunSearch();
            StatusMessage = $"Imported {count} worklog notes from {Path.GetFileName(path)}.";
            _dialogService.Info("Import worklog", $"Imported {count} notes into the shared SQL workspace.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqlException or InvalidOperationException)
        {
            StatusMessage = $"Worklog import failed: {ex.Message}";
            _dialogService.Error("Import worklog", StatusMessage);
        }
        finally
        {
            EntryOperationText = string.Empty;
            IsEntryOperationRunning = false;
        }
    }
}
