using System.Globalization;
using System.Text.Json;
using TechBench.Data;
using TechBench.Models;
using TechBench.Providers;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    internal enum WhdNoteSyncDecision
    {
        AlreadySynchronized,
        PushLocal,
        PullRemote,
        Conflict
    }

    private enum WhdSyncIntent
    {
        PushLocal,
        PullRemote
    }

    private async Task SyncWhdNoteAsync(object? parameter)
    {
        var entry = ResolveEntry(parameter);
        if (entry is null)
        {
            StatusMessage = "Save the entry before synchronizing its WHD note.";
            return;
        }

        var ownsOperationState = !IsEntryOperationRunning;
        if (ownsOperationState)
        {
            IsEntryOperationRunning = true;
        }

        EntryOperationText = "Reading the exact work note from WHD...";
        try
        {
            await SynchronizeWhdEntryAsync(entry, WhdSyncIntent.PullRemote, allowConflictPrompt: true);
        }
        finally
        {
            if (ownsOperationState)
            {
                EntryOperationText = string.Empty;
                IsEntryOperationRunning = false;
            }
        }
    }

    private bool CanSyncWhdNote(object? parameter)
    {
        if (IsEntryOperationRunning)
        {
            return false;
        }

        return parameter is WorkEntry entry
            ? entry is { Id: > 0, HasTicket: true, WhdPosted: true, SagePosted: false }
            : Editor is { Id: > 0, WhdPosted: true, SagePosted: false } && !Editor.HasNoTicket;
    }

    private async Task<bool> SynchronizeWhdEntryAsync(
        WorkEntry entry,
        WhdSyncIntent intent,
        bool allowConflictPrompt,
        bool refreshAfter = true)
    {
        await using var syncLease = await _postingCoordinator.TryAcquireAsync(entry.Id, "WHD");
        if (syncLease is null)
        {
            StatusMessage = $"A WHD operation for {entry.ClientDisplay} ({entry.TicketDisplay}) is already running.";
            return false;
        }

        return await SynchronizeWhdEntryCoreAsync(entry, intent, allowConflictPrompt, refreshAfter);
    }

    private async Task<bool> SynchronizeWhdEntryCoreAsync(
        WorkEntry entry,
        WhdSyncIntent intent,
        bool allowConflictPrompt,
        bool refreshAfter)
    {
        if (_isSageVerificationRunning)
        {
            StatusMessage = "Waiting for the current read-only Sage verification before synchronizing WHD...";
            while (_isSageVerificationRunning)
            {
                await Task.Delay(100);
            }
        }

        entry = _repository.GetWorkEntry(entry.Id) ?? entry;
        if (entry.SagePosted)
        {
            StatusMessage = "This entry is locked because it was posted to Sage. WHD synchronization was not run.";
            return false;
        }

        if (!entry.WhdPosted || !entry.HasTicket)
        {
            StatusMessage = "Post and verify the work note in WHD before synchronizing it.";
            return false;
        }

        if (!TryGetTrackedWhdNote(entry.Id, out var trackingLog, out var techNoteId, out var lastSyncedNote, out var trackingError))
        {
            RecordWhdSyncFailure(entry, trackingError, null, refreshAfter);
            return false;
        }

        var ticket = ResolveWhdTicket(entry);
        if (ticket is null || !TryResolveWhdTicketId(ticket, out var whdTicketId))
        {
            RecordWhdSyncFailure(
                entry,
                "TechBench could not resolve the WHD ticket for the tracked TechNote.",
                trackingLog.ExternalReference,
                refreshAfter);
            return false;
        }

        var remote = await _whdRestClient.GetTechNoteAsync(
            BuildWhdConnectionSettings(),
            whdTicketId,
            techNoteId);
        if (!remote.Success)
        {
            RecordWhdSyncFailure(entry, remote.Message, trackingLog.ExternalReference, refreshAfter);
            return false;
        }

        var localText = NormalizeWhdNote(entry.Note);
        var remoteText = NormalizeWhdNote(remote.NoteText);
        var snapshotText = lastSyncedNote is null ? null : NormalizeWhdNote(lastSyncedNote);
        var syncDecision = DecideWhdNoteSync(localText, remoteText, snapshotText);

        if (syncDecision == WhdNoteSyncDecision.AlreadySynchronized)
        {
            RecordWhdSyncSuccess(
                entry,
                remote.NoteText,
                $"Verified WHD TechNote #{techNoteId}; the work note is synchronized.",
                trackingLog.ExternalReference!,
                BuildWhdNoteSnapshotPayload(remote.NoteText),
                refreshAfter);
            return true;
        }

        if (intent == WhdSyncIntent.PullRemote)
        {
            var useWhd = allowConflictPrompt && _dialogService.Confirm(
                "Sync WHD note",
                $"WHD TechNote #{techNoteId} differs from the TechBench work note. Replace the local work note with the WHD version?",
                "Use WHD note",
                "Review later");
            if (!useWhd)
            {
                RecordWhdSyncConflict(
                    entry,
                    "The WHD and TechBench work notes differ. No note was changed.",
                    trackingLog.ExternalReference,
                    refreshAfter);
                return false;
            }

            RecordWhdSyncSuccess(
                entry,
                remote.NoteText,
                $"Updated the TechBench work note from WHD TechNote #{techNoteId}.",
                trackingLog.ExternalReference!,
                BuildWhdNoteSnapshotPayload(remote.NoteText),
                refreshAfter);
            return true;
        }

        if (syncDecision == WhdNoteSyncDecision.PullRemote)
        {
            RecordWhdSyncSuccess(
                entry,
                remote.NoteText,
                $"WHD TechNote #{techNoteId} had a newer work note; TechBench was updated to match it.",
                trackingLog.ExternalReference!,
                BuildWhdNoteSnapshotPayload(remote.NoteText),
                refreshAfter);
            return true;
        }

        if (syncDecision == WhdNoteSyncDecision.Conflict)
        {
            var replaceWhd = allowConflictPrompt && _dialogService.Confirm(
                "WHD note conflict",
                snapshotText is null
                    ? $"TechBench cannot establish a prior sync baseline for WHD TechNote #{techNoteId}, and its text differs from this entry. Replace the WHD note with the TechBench work note?"
                    : $"Both TechBench and WHD TechNote #{techNoteId} changed since the last verified sync. Replace the WHD note with the TechBench work note?",
                "Update WHD",
                "Keep both unchanged");
            if (!replaceWhd)
            {
                RecordWhdSyncConflict(
                    entry,
                    "Both versions contain changes. No note was overwritten.",
                    trackingLog.ExternalReference,
                    refreshAfter);
                return false;
            }
        }

        var update = await _whdRestClient.UpdateTechNoteAsync(
            BuildWhdConnectionSettings(),
            whdTicketId,
            techNoteId,
            entry.Note);
        if (!update.Success)
        {
            RecordWhdSyncFailure(entry, update.Message, trackingLog.ExternalReference, refreshAfter, update.Payload);
            return false;
        }

        RecordWhdSyncSuccess(
            entry,
            entry.Note,
            update.Message,
            update.ExternalReference ?? trackingLog.ExternalReference!,
            update.Payload,
            refreshAfter);
        return true;
    }

    private bool TryGetTrackedWhdNote(
        int workEntryId,
        out PostingLog trackingLog,
        out int techNoteId,
        out string? lastSyncedNote,
        out string errorMessage)
    {
        trackingLog = _repository.GetLatestVerifiedWhdPostingLog(workEntryId) ?? new PostingLog();
        techNoteId = 0;
        lastSyncedNote = null;
        errorMessage = string.Empty;

        if (trackingLog.Id <= 0
            || !TryParseWhdTechNoteId(trackingLog.ExternalReference, out techNoteId))
        {
            errorMessage = "WHD sync pending: TechBench does not have the exact TechNote ID for this older or manually marked entry. It will not create a replacement note.";
            return false;
        }

        lastSyncedNote = TryReadWhdSnapshotNote(trackingLog.Payload);
        return true;
    }

    private void RecordWhdSyncSuccess(
        WorkEntry entry,
        string synchronizedNote,
        string message,
        string externalReference,
        string payload,
        bool refreshAfter)
    {
        entry.Note = synchronizedNote.Trim();
        entry.WhdPosted = true;
        entry.WhdPostedAt = DateTime.Now;
        entry.LastError = null;
        TechBenchRepository.UpdatePostingStatus(entry);
        _repository.SaveWorkEntry(entry);
        _repository.AddPostingLog(new PostingLog
        {
            WorkEntryId = entry.Id,
            Destination = "WHD",
            Payload = string.IsNullOrWhiteSpace(payload) ? BuildWhdNoteSnapshotPayload(synchronizedNote) : payload,
            Success = true,
            Message = message,
            ExternalReference = externalReference,
            CreatedAt = DateTime.Now
        });
        StatusMessage = message;
        RefreshAfterWhdSync(entry.Id, refreshAfter);
    }

    private void RecordWhdSyncFailure(
        WorkEntry entry,
        string message,
        string? externalReference,
        bool refreshAfter,
        string? payload = null)
    {
        var fullMessage = message.StartsWith("WHD sync pending:", StringComparison.OrdinalIgnoreCase)
            ? message
            : $"WHD sync pending: {message}";
        entry.LastError = fullMessage;
        TechBenchRepository.UpdatePostingStatus(entry);
        _repository.SaveWorkEntry(entry);
        _repository.AddPostingLog(new PostingLog
        {
            WorkEntryId = entry.Id,
            Destination = "WHD",
            Payload = string.IsNullOrWhiteSpace(payload) ? BuildWhdNoteSnapshotPayload(entry.Note) : payload,
            Success = false,
            Message = fullMessage,
            ExternalReference = externalReference,
            CreatedAt = DateTime.Now
        });
        StatusMessage = fullMessage;
        RefreshAfterWhdSync(entry.Id, refreshAfter);
    }

    private void RecordWhdSyncConflict(
        WorkEntry entry,
        string message,
        string? externalReference,
        bool refreshAfter)
    {
        var fullMessage = $"WHD sync conflict: {message}";
        entry.LastError = fullMessage;
        TechBenchRepository.UpdatePostingStatus(entry);
        _repository.SaveWorkEntry(entry);
        _repository.AddPostingLog(new PostingLog
        {
            WorkEntryId = entry.Id,
            Destination = "WHD",
            Payload = BuildWhdNoteSnapshotPayload(entry.Note),
            Success = false,
            Message = fullMessage,
            ExternalReference = externalReference,
            CreatedAt = DateTime.Now
        });
        StatusMessage = fullMessage;
        RefreshAfterWhdSync(entry.Id, refreshAfter);
    }

    private void RefreshAfterWhdSync(int workEntryId, bool refreshAfter)
    {
        if (!refreshAfter)
        {
            return;
        }

        RefreshAll();
        if (Editor.Id == workEntryId)
        {
            var refreshed = Entries.FirstOrDefault(entry => entry.Id == workEntryId)
                ?? _repository.GetWorkEntry(workEntryId);
            if (refreshed is not null)
            {
                _selectedEntry = refreshed;
                OnPropertyChanged(nameof(SelectedEntry));
                LoadEntryIntoEditor(refreshed);
            }
        }
    }

    private static bool TryParseWhdTechNoteId(string? externalReference, out int techNoteId)
    {
        const string prefix = "WHD-TECHNOTE-";
        var value = externalReference?.Trim() ?? string.Empty;
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[prefix.Length..];
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out techNoteId)
            && techNoteId > 0;
    }

    private static string? TryReadWhdSnapshotNote(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("noteText", out var noteText)
                && noteText.ValueKind == JsonValueKind.String
                    ? noteText.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildWhdNoteSnapshotPayload(string? noteText) => JsonSerializer.Serialize(
        new { noteText = (noteText ?? string.Empty).Trim() },
        new JsonSerializerOptions { WriteIndented = true });

    private static string NormalizeWhdNote(string? noteText) =>
        (noteText ?? string.Empty).ReplaceLineEndings("\n").Trim();

    internal static WhdNoteSyncDecision DecideWhdNoteSync(
        string? localNote,
        string? remoteNote,
        string? lastSyncedNote)
    {
        var local = NormalizeWhdNote(localNote);
        var remote = NormalizeWhdNote(remoteNote);
        if (local.Equals(remote, StringComparison.Ordinal))
        {
            return WhdNoteSyncDecision.AlreadySynchronized;
        }

        if (lastSyncedNote is null)
        {
            return WhdNoteSyncDecision.Conflict;
        }

        var snapshot = NormalizeWhdNote(lastSyncedNote);
        if (local.Equals(snapshot, StringComparison.Ordinal))
        {
            return WhdNoteSyncDecision.PullRemote;
        }

        return remote.Equals(snapshot, StringComparison.Ordinal)
            ? WhdNoteSyncDecision.PushLocal
            : WhdNoteSyncDecision.Conflict;
    }
}
