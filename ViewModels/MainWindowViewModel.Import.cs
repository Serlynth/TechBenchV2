using System.IO;
using Microsoft.Data.Sqlite;
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
            var backup = _databaseBackupService.CreateBackup("Pre-import backup");
            if (!backup.Succeeded)
            {
                _dialogService.Error("Import worklog", $"Import stopped because the safety backup failed. {backup.Message}");
                StatusMessage = backup.Message;
                return;
            }

            var count = _repository.ImportWorkEntries(entries, importViewModel.BuildAliasMappings());
            RefreshDatabaseSafetyStatus();
            RefreshAll();
            RunSearch();
            StatusMessage = $"Imported {count} worklog notes from {Path.GetFileName(path)}.";
            _dialogService.Info("Import worklog", $"Imported {count} notes. Backup: {Path.GetFileName(backup.BackupPath)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
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
