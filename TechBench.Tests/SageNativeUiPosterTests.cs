using TechBench.Models;
using TechBench.Providers;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class SageNativeUiPosterTests
{
    [Fact]
    public void IdentifierFieldsUseOneAtomicWriteBeforeSageValidation()
    {
        var source = ReadRepositoryFile(
            Path.Combine("Services", "SageNativeUiAutomation.cs"));
        var validatedEntry = ExtractMethod(
            source,
            "private static IntPtr EnterValidatedTextField(",
            "private static IntPtr WaitForStableTextFieldValue(");
        var atomicEntry = ExtractMethod(
            source,
            "private static void EnterAtomicTextField(",
            "private static IntPtr EnterValidatedTextField(");

        Assert.Contains("EnterAtomicTextField(", validatedEntry, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.SetWindowText(field, value)", atomicEntry, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.ReadWindowText(field)", atomicEntry, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.VkTab", atomicEntry, StringComparison.Ordinal);
        Assert.DoesNotContain("SendUnicodeText", atomicEntry, StringComparison.Ordinal);
        Assert.True(
            atomicEntry.IndexOf("SetWindowText", StringComparison.Ordinal)
            < atomicEntry.IndexOf("VkTab", StringComparison.Ordinal));
    }

    [Fact]
    public void AllowsBoundedWaitForSlowSageNoteDialog()
    {
        Assert.InRange(
            SageNativeUiAutomation.NoteDialogOpenTimeout,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(3, -2)]
    public void ComputesKeyboardNavigationFromNativeComboItems(int currentIndex, int expectedDelta)
    {
        var items = new[] { "Employee Rate", "Activity Rate", "Override Rate", "Flat Fee" };

        var delta = SageNativeUiAutomation.FindItemNavigationDelta(items, currentIndex, "Activity Rate");

        Assert.Equal(expectedDelta, delta);
    }

    [Theory]
    [InlineData("", "", "", false)]
    [InlineData("RS", "", "", true)]
    [InlineData("", "69832", "", true)]
    [InlineData("", "", "13001A", true)]
    public void DetectsVisibleDataInAnExistingTimeTicket(
        string employeeId,
        string customerId,
        string activityItem,
        bool expected)
    {
        var result = SageNativeUiAutomation.ContainsEnteredTicketData(
            [employeeId, customerId, activityItem]);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task BuildsNativeRequestAndMarksPostedAfterSageAcceptsSave()
    {
        var automation = new RecordingAutomation(new SageTimeTicketAutomationResult(
            true,
            "Submitted Save for Sage ticket 147773.",
            "147773",
            SaveSubmitted: true));
        var poster = new SageNativeUiPoster(automation);
        var entry = BuildEntry();
        entry.InternalNote = "- This should stay out of Sage.";
        var client = new Client { Id = 8, Name = "Example Client", SageCustomerId = "80000" };
        var settings = BuildSettings();

        var result = await poster.PostAsync(entry, client, new Ticket { TicketNumber = "4567" }, settings);

        Assert.True(result.Success);
        Assert.True(result.MarkPosted);
        Assert.Equal("SAGE-147773", result.ExternalReference);
        Assert.NotNull(automation.Request);
        Assert.Equal("RS", automation.Request.EmployeeId);
        Assert.Equal("80000", automation.Request.CustomerId);
        Assert.Equal("13001A", automation.Request.ActivityItemId);
        Assert.Equal("Activity Rate", automation.Request.BillingType);
        Assert.Equal("Regular", automation.Request.ExpectedPayLevel);
        Assert.Equal("Investigated the issue.", automation.Request.Note);
        Assert.True(automation.Request.AutoSave);
    }

    [Fact]
    public async Task SubmittedSaveWithoutVisibleTicketNumberStillMarksPosted()
    {
        var automation = new RecordingAutomation(new SageTimeTicketAutomationResult(
            true,
            "Submitted Save.",
            SaveSubmitted: true));
        var poster = new SageNativeUiPoster(automation);

        var result = await poster.PostAsync(
            BuildEntry(),
            new Client { Id = 8, Name = "Example Client", SageCustomerId = "80000" },
            null,
            BuildSettings());

        Assert.True(result.Success);
        Assert.True(result.MarkPosted);
        Assert.Null(result.ExternalReference);
    }

    [Fact]
    public async Task DoesNotMarkPostedWhenSaveWasNotSubmitted()
    {
        var automation = new RecordingAutomation(new SageTimeTicketAutomationResult(
            true,
            "Filled the Sage ticket."));
        var poster = new SageNativeUiPoster(automation);

        var result = await poster.PostAsync(
            BuildEntry(),
            new Client { Id = 8, Name = "Example Client", SageCustomerId = "80000" },
            null,
            BuildSettings());

        Assert.False(result.Success);
        Assert.False(result.MarkPosted);
        Assert.Contains("without confirming", result.Message);
    }

    [Fact]
    public async Task DoesNotFallBackToGlobalCustomerIdWhenSelectedClientIsUnmapped()
    {
        var automation = new RecordingAutomation(new SageTimeTicketAutomationResult(true, "unexpected"));
        var poster = new SageNativeUiPoster(automation);
        var settings = new Dictionary<string, string>(BuildSettings())
        {
            ["Sage.DefaultCustomerId"] = "WRONG-CUSTOMER"
        };

        var result = await poster.PostAsync(
            BuildEntry(),
            new Client { Id = 8, Name = "Unmapped Client" },
            null,
            settings);

        Assert.False(result.Success);
        Assert.Contains("not linked to a Sage Customer ID", result.Message);
        Assert.Null(automation.Request);
    }

    [Theory]
    [InlineData(false, 15, "supports billable entries only")]
    [InlineData(true, 0, "positive duration")]
    [InlineData(true, 1440, "cannot exceed 23:59")]
    public async Task RejectsUnsupportedEntriesBeforeOpeningSage(bool billable, int minutes, string expectedError)
    {
        var automation = new RecordingAutomation(new SageTimeTicketAutomationResult(true, "unexpected"));
        var poster = new SageNativeUiPoster(automation);
        var entry = BuildEntry();
        entry.Billable = billable;
        entry.DurationMinutes = minutes;

        var result = await poster.PostAsync(
            entry,
            new Client { Id = 8, Name = "Example Client", SageCustomerId = "80000" },
            null,
            BuildSettings());

        Assert.False(result.Success);
        Assert.Contains(expectedError, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(automation.Request);
    }

    private static WorkEntry BuildEntry() => new()
    {
        Id = 42,
        WorkDate = new DateTime(2026, 7, 9),
        DurationMinutes = 15,
        Billable = true,
        Note = "Investigated the issue."
    };

    private static IReadOnlyDictionary<string, string> BuildSettings() =>
        new Dictionary<string, string>
        {
            ["Sage.EmployeeId"] = "RS",
            ["Sage.ActivityItemId"] = "13001A"
        };

    private static string ExtractMethod(
        string source,
        string startMarker,
        string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source[start..end];
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "TechBenchV2.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }

    private sealed class RecordingAutomation(SageTimeTicketAutomationResult result) : ISageTimeTicketAutomation
    {
        public SageTimeTicketRequest? Request { get; private set; }

        public SageTimeTicketAutomationResult CreateTimeTicket(
            SageTimeTicketRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return result;
        }
    }

}
