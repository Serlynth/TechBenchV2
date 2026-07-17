using System.IO;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    public AsyncRelayCommand ImportV1DatabaseCommand { get; private set; } = null!;

    private void InitializeV1DatabaseImport()
    {
        ImportV1DatabaseCommand = new AsyncRelayCommand(
            _ => ImportV1DatabaseAsync(),
            _ => !IsEntryOperationRunning);
    }

    private async Task ImportV1DatabaseAsync()
    {
        var suggestedPath = ResolveSuggestedV1DatabasePath();
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a closed TechBench V1 database or verified backup",
            Filter = "TechBench V1 databases (*.db)|*.db|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Path.GetDirectoryName(suggestedPath),
            FileName = Path.GetFileName(suggestedPath)
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsEntryOperationRunning = true;
        EntryOperationText = "Validating the V1 database...";
        try
        {
            var package = await new V1DatabaseImportReader()
                .ReadAsync(dialog.FileName)
                .ConfigureAwait(true);
            if (package.WorkEntries.Count == 0
                && package.Links.Count == 0
                && package.PostingLogs.Count == 0)
            {
                _dialogService.Info(
                    "Import V1 database",
                    "The selected file is a valid TechBench V1 database, but it contains no personal work history to import.");
                StatusMessage = "The selected V1 database contained no personal work history.";
                return;
            }

            EntryOperationText = "Matching V1 references to shared clients and tickets...";
            var resolution = await Task.Run(
                    () => _repository.ResolveV1ImportReferences(package))
                .ConfigureAwait(true);

            var totalItems = package.WorkEntries.Count
                + package.Links.Count
                + package.PostingLogs.Count;
            var sourceHashLabel = package.FileHash.Length >= 12
                ? package.FileHash[..12]
                : package.FileHash;
            var draftNotice = package.HasEditorDraft
                ? "\n\nThe V1 editor draft is not imported because its local record IDs cannot be safely reused. Save any unfinished V1 draft as a note before migrating it."
                : string.Empty;
            var excludedNotice = package.ExcludedSharedItemCount > 0
                ? $"\n\n{package.ExcludedSharedItemCount} shared/configuration item(s) are intentionally excluded. Common Links, templates, aliases, organization settings, credentials, active posting attempts, and shared client/ticket caches remain centrally managed."
                : "\n\nShared configuration, credentials, active posting attempts, and client/ticket caches are intentionally not imported.";
            var confirmed = _dialogService.Confirm(
                "Import V1 database",
                $"Import this read-only V1 snapshot into {_currentUser.DisplayName} ({_currentUser.LoginName})?\n\n"
                + $"Source: {package.FileName}\n"
                + $"SHA-256: {sourceHashLabel}...\n\n"
                + $"Work entries: {package.WorkEntries.Count}\n"
                + $"Personal note links: {package.Links.Count}\n"
                + $"Posting audit records: {package.PostingLogs.Count}\n\n"
                + $"Matched client references: {resolution.MatchedClientCount}\n"
                + $"Unmatched client references kept by name: {resolution.UnmatchedClientCount}\n"
                + $"Matched ticket references: {resolution.MatchedTicketCount}\n"
                + $"Unmatched ticket references kept by number: {resolution.UnmatchedTicketCount}"
                + excludedNotice
                + draftNotice
                + "\n\nClose TechBench V1 before continuing. The source file will not be changed. Re-running this import safely skips records already migrated.",
                confirmText: $"Import {totalItems}",
                cancelText: "Cancel");
            if (!confirmed)
            {
                StatusMessage = "V1 database import cancelled before any SQL records were changed.";
                return;
            }

            EntryOperationText = "Importing personal V1 history into SQL Server...";
            V1DatabaseImportResult result;
            try
            {
                result = await Task.Run(() => _repository.ImportV1Database(package))
                    .ConfigureAwait(true);
            }
            catch (V1ImportInProgressException)
            {
                var abandonPriorImport = _dialogService.Confirm(
                    "Incomplete V1 import",
                    "A different V1 database import for this Windows user was interrupted and is still marked active on SQL Server.\n\n"
                    + "First close any other TechBench V2 window running under your account. Abandon that incomplete batch and import the selected database instead? Records already committed by the old batch remain safely mapped and will not be duplicated.",
                    confirmText: "Abandon and retry",
                    cancelText: "Cancel");
                if (!abandonPriorImport)
                {
                    StatusMessage = "V1 database import cancelled; the prior incomplete batch was left unchanged.";
                    return;
                }

                EntryOperationText = "Abandoning the incomplete V1 import and retrying...";
                result = await Task.Run(() =>
                    {
                        _repository.AbandonV1Import();
                        return _repository.ImportV1Database(package);
                    })
                    .ConfigureAwait(true);
            }

            RefreshAll(forceRemoteRefresh: false);
            var conflictDetails = result.ConflictMessages.Count == 0
                ? string.Empty
                : "\n\nConflicts were left unchanged:\n- "
                    + string.Join("\n- ", result.ConflictMessages.Take(5))
                    + (result.ConflictMessages.Count > 5
                        ? $"\n- ...and {result.ConflictMessages.Count - 5} more"
                        : string.Empty);
            var resultMessage =
                $"V1 import batch {result.BatchId} completed for {_currentUser.DisplayName}.\n\n"
                + $"Work entries: {result.WorkEntriesImported} imported, {result.WorkEntriesSkipped} already present\n"
                + $"Note links: {result.LinksImported} imported, {result.LinksSkipped} already present\n"
                + $"Posting records: {result.PostingLogsImported} imported, {result.PostingLogsSkipped} already present\n"
                + $"Conflicts: {result.ConflictCount}"
                + conflictDetails;
            StatusMessage = result.ImportedCount > 0
                ? $"Imported {result.ImportedCount} V1 record(s) into the shared SQL workspace."
                : "No new V1 records were added; this database was already imported.";

            if (result.ConflictCount > 0)
            {
                _dialogService.Error("Import V1 database", resultMessage);
            }
            else
            {
                _dialogService.Info("Import V1 database", resultMessage);
            }
        }
        catch (V1DatabaseImportException ex)
        {
            StatusMessage = $"V1 database validation failed: {ex.Message}";
            _dialogService.Error("Import V1 database", StatusMessage);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "V1 database import cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"V1 database import failed: {ex.Message}";
            _dialogService.Error("Import V1 database", StatusMessage);
        }
        finally
        {
            EntryOperationText = string.Empty;
            IsEntryOperationRunning = false;
        }
    }

    private static string ResolveSuggestedV1DatabasePath()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var v1Directory = Path.Combine(localAppData, "TechBench");
        var defaultPath = Path.Combine(v1Directory, "techbench.db");
        var configuredLocation = Path.Combine(v1Directory, "database-location.txt");
        try
        {
            if (File.Exists(configuredLocation))
            {
                var configuredPath = File.ReadAllText(configuredLocation).Trim();
                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    return Path.GetFullPath(configuredPath);
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            // The picker can still use the original default location.
        }

        return defaultPath;
    }
}
