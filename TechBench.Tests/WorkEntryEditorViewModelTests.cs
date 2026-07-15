using TechBench.Models;
using TechBench.ViewModels;

namespace TechBench.Tests;

public sealed class WorkEntryEditorViewModelTests
{
    [Fact]
    public void LoadingAndMarkingCleanControlsDirtyState()
    {
        var editor = new WorkEntryEditorViewModel();

        editor.LoadNew(new DateTime(2026, 7, 13));

        Assert.False(editor.IsDirty);

        editor.Note = "Updated workstation.";

        Assert.True(editor.IsDirty);

        editor.MarkClean();

        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void ManualClientModeRequiresManualNameAndDoesNotPersistSelectedClient()
    {
        var editor = new WorkEntryEditorViewModel();
        editor.LoadNew(new DateTime(2026, 7, 13));
        editor.SelectedClient = new Client { Id = 42, Name = "Synced client" };
        editor.UseManualClient = true;
        editor.DurationMinutesText = "15";

        Assert.False(editor.TryBuildEntry(out _, out _));

        editor.ManualClientName = "Walk-in contact";

        Assert.True(editor.TryBuildEntry(out var entry, out var validationMessage), validationMessage);
        Assert.Null(entry.ClientId);
        Assert.Equal("Walk-in contact", entry.ManualClientName);
    }

    [Fact]
    public void TimeRangeIsAuthoritativeForDuration()
    {
        var editor = new WorkEntryEditorViewModel();
        editor.LoadNew(new DateTime(2026, 7, 13));
        editor.SelectedClient = new Client { Id = 1, Name = "CSRI" };
        editor.StartTimeText = "08:00";
        editor.DurationMinutesText = "120";
        editor.EndTimeText = "08:45";

        Assert.True(editor.TryBuildEntry(out var entry, out var validationMessage), validationMessage);
        Assert.True(editor.IsDurationCalculated);
        Assert.Equal(45, entry.DurationMinutes);
        Assert.Equal("0", editor.DurationHoursText);
        Assert.Equal("45", editor.DurationMinutePartText);
    }

    [Fact]
    public void ClockOptionsIncludeQuarterHoursAndPreserveExactSavedTimes()
    {
        var editor = new WorkEntryEditorViewModel();

        Assert.Equal("Not set", editor.TimeOptions[0].DisplayText);
        Assert.Contains(editor.TimeOptions, option => option.Value == "08:15");

        editor.StartTimeText = "08:07";
        editor.EndTimeText = "08:22";

        Assert.Contains(editor.TimeOptions, option => option.Value == "08:07");
        Assert.Contains(editor.TimeOptions, option => option.Value == "08:22");
        Assert.Equal("0", editor.DurationHoursText);
        Assert.Equal("15", editor.DurationMinutePartText);
    }

    [Fact]
    public void DurationHoursAndMinutesBuildCanonicalTotal()
    {
        var editor = new WorkEntryEditorViewModel();
        editor.LoadNew(new DateTime(2026, 7, 14));
        editor.SelectedClient = new Client { Id = 1, Name = "CSRI" };
        editor.DurationHoursText = "1";
        editor.DurationMinutePartText = "30";

        Assert.Equal("90", editor.DurationMinutesText);
        Assert.True(editor.TryBuildEntry(out var entry, out var validationMessage), validationMessage);
        Assert.Equal(90, entry.DurationMinutes);
    }

    [Fact]
    public void CanonicalDurationPopulatesHoursAndMinutes()
    {
        var editor = new WorkEntryEditorViewModel();

        editor.DurationMinutesText = "135";

        Assert.Equal("2", editor.DurationHoursText);
        Assert.Equal("15", editor.DurationMinutePartText);
    }

    [Fact]
    public void DurationMinutePartMustBeLessThanSixty()
    {
        var editor = new WorkEntryEditorViewModel();
        editor.LoadNew(new DateTime(2026, 7, 14));
        editor.SelectedClient = new Client { Id = 1, Name = "CSRI" };
        editor.DurationHoursText = "0";
        editor.DurationMinutePartText = "60";

        Assert.False(editor.TryBuildEntry(out _, out var validationMessage));
        Assert.Contains("minutes must be from 0 to 59", validationMessage);
    }

    [Fact]
    public void LoadingExistingManualEntryEnablesManualModeWithoutDirtyingEditor()
    {
        var editor = new WorkEntryEditorViewModel();
        var entry = new WorkEntry
        {
            Id = 5,
            WorkDate = new DateTime(2026, 7, 13),
            ManualClientName = "Manual customer",
            DurationMinutes = 30,
            Note = "Completed work."
        };

        editor.LoadFrom(entry, Array.Empty<Client>(), Array.Empty<Ticket>());

        Assert.True(editor.UseManualClient);
        Assert.True(editor.HasClientReference);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void LoadingExistingEntryRestoresClientAndTicketSelections()
    {
        var client = new Client { Id = 42, Name = "Northwind" };
        var ticket = new Ticket
        {
            Id = 73,
            ClientId = client.Id,
            TicketNumber = "WHD-73",
            Subject = "Workstation setup"
        };
        var entry = new WorkEntry
        {
            Id = 9,
            WorkDate = new DateTime(2026, 7, 14),
            ClientId = client.Id,
            TicketId = ticket.Id,
            DurationMinutes = 30,
            Note = "Configured the workstation."
        };
        var editor = new WorkEntryEditorViewModel();

        editor.LoadFrom(entry, [client], [ticket]);

        Assert.Same(client, editor.SelectedClient);
        Assert.Same(ticket, editor.SelectedTicket);
        Assert.Equal("Northwind", editor.SelectedClient?.ToString());
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void DraftRoundTripPreservesNoteMetadata()
    {
        var client = new Client { Id = 9, Name = "CSRI" };
        var editor = new WorkEntryEditorViewModel();
        editor.LoadNew(new DateTime(2026, 7, 14));
        editor.SelectedClient = client;
        editor.DurationMinutesText = "30";
        editor.Note = "Completed maintenance.";
        editor.Tags = " onsite, Project, onsite ";
        editor.FollowUpState = FollowUpState.Waiting;
        editor.FollowUpDueDate = new DateTime(2026, 7, 16);

        var draft = editor.BuildDraft();
        var restored = new WorkEntryEditorViewModel();
        restored.LoadDraft(draft, [client], []);

        Assert.True(restored.IsDirty);
        Assert.Equal("Completed maintenance.", restored.Note);
        Assert.Equal(FollowUpState.Waiting, restored.FollowUpState);
        Assert.True(restored.TryBuildEntry(out var entry, out var validationMessage), validationMessage);
        Assert.Equal("onsite, Project", entry.Tags);
    }

    [Fact]
    public void AlternateWhdTicketNumberRoundTripsWithoutARegularTicketSelection()
    {
        var client = new Client { Id = 9, Name = "CSRI" };
        var editor = new WorkEntryEditorViewModel();
        editor.LoadNew(new DateTime(2026, 7, 15));
        editor.SelectedClient = client;
        editor.UseOtherWhdTicket = true;
        editor.ManualTicketNumber = "WHD-456";
        editor.DurationMinutesText = "30";
        editor.Note = "Added the follow-up work note.";

        Assert.True(editor.TryBuildEntry(out var entry, out var validationMessage), validationMessage);
        Assert.Null(entry.TicketId);
        Assert.Equal("456", entry.TicketNumberText);
        Assert.False(editor.HasNoTicket);

        var restored = new WorkEntryEditorViewModel();
        restored.LoadFrom(entry, [client], []);

        Assert.True(restored.UseOtherWhdTicket);
        Assert.Equal("456", restored.ManualTicketNumber);
        Assert.Null(restored.SelectedTicket);
        Assert.False(restored.IsDirty);
    }

    [Fact]
    public void AlternateWhdTicketRequiresAPositiveNumericNumber()
    {
        var editor = new WorkEntryEditorViewModel();
        editor.LoadNew(new DateTime(2026, 7, 15));
        editor.SelectedClient = new Client { Id = 9, Name = "CSRI" };
        editor.UseOtherWhdTicket = true;
        editor.ManualTicketNumber = "ticket-456";
        editor.DurationMinutesText = "30";

        Assert.False(editor.TryBuildEntry(out _, out var validationMessage));
        Assert.Contains("numeric", validationMessage, StringComparison.OrdinalIgnoreCase);
    }
}
