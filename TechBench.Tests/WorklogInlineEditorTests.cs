namespace TechBench.Tests;

using System.Text.RegularExpressions;

public sealed class WorklogInlineEditorTests
{
    [Fact]
    public void WeekAndHistoryHostTheEditorWithoutForcingTodayNavigation()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var viewModel = ReadRepositoryFile("ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("x:Key=\"InlineWorkEntryEditorTemplate\"", xaml, StringComparison.Ordinal);
        Assert.Equal(
            3,
            xaml.Split("InlineWorkEntryEditorTemplate", StringSplitOptions.None).Length - 1);
        Assert.Contains("ConverterParameter=This Week", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=History", xaml, StringComparison.Ordinal);
        Assert.Contains("Editing {savedEntry.ClientDisplay} here", viewModel, StringComparison.Ordinal);
        Assert.Contains("var editInline = CurrentSection is \"This Week\" or \"History\";", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingKeepsTheEntryAndTicketOpenForPosting()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var viewModel = ReadRepositoryFile("ViewModels", "MainWindowViewModel.cs");
        var saveStart = viewModel.IndexOf(
            "private Task SaveEntryAsync",
            StringComparison.Ordinal);
        var saveEnd = viewModel.IndexOf(
            "private WorkEntry? SaveEditor",
            saveStart,
            StringComparison.Ordinal);
        var saveMethod = viewModel[saveStart..saveEnd];

        Assert.DoesNotContain("NewEntry();", saveMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("SynchronizeWhdEntryAsync", saveMethod, StringComparison.Ordinal);
        Assert.Contains("This save is local to TechBench", saveMethod, StringComparison.Ordinal);
        Assert.Contains("The saved ticket stays selected for posting", xaml, StringComparison.Ordinal);
        Assert.Contains("Save in TechBench and keep this entry open (Ctrl+S)", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineEditorStaticResourcesAreRegisteredBeforeItsDeferredTemplate()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        const string templateMarker = "<DataTemplate x:Key=\"InlineWorkEntryEditorTemplate\">";
        var templateStart = xaml.IndexOf(templateMarker, StringComparison.Ordinal);
        var templateEnd = xaml.IndexOf("</DataTemplate>", templateStart, StringComparison.Ordinal);

        Assert.True(templateStart >= 0, "The inline editor template was not found.");
        Assert.True(templateEnd > templateStart, "The inline editor template is incomplete.");

        var template = xaml[templateStart..templateEnd];
        var referencedKeys = Regex.Matches(template, @"\{StaticResource\s+([^,}\s]+)")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);

        foreach (var key in referencedKeys)
        {
            var localDeclaration = xaml.IndexOf($"x:Key=\"{key}\"", StringComparison.Ordinal);
            if (localDeclaration >= 0)
            {
                Assert.True(
                    localDeclaration < templateStart,
                    $"StaticResource '{key}' must be registered before InlineWorkEntryEditorTemplate is created.");
            }
        }
    }

    [Fact]
    public void WhdImagesUseTheSinglePostOrUpdateActionWithoutMarkdownLabels()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var viewModel = ReadRepositoryFile("ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("x:Key=\"WhdImageAttachmentPickerTemplate\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("UploadWhdImagesCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Send to WHD", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Sync WHD Note", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Mark WHD Posted (Manual)", xaml, StringComparison.Ordinal);
        Assert.Contains("next Post/Update WHD action", xaml, StringComparison.Ordinal);
        Assert.Contains("not stored in TechBench or SQL", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Personal Note (Markdown)", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Personal Note Markdown", xaml, StringComparison.Ordinal);
        Assert.Contains("UploadWhdImagesToTechNoteAsync", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingWhdNoteUpdatesAlsoAttachSelectedImages()
    {
        var viewModel = ReadRepositoryFile("ViewModels", "MainWindowViewModel.cs");
        var postedBranchStart = viewModel.IndexOf(
            "if (destination == \"WHD\" && entry.WhdPosted)",
            StringComparison.Ordinal);
        var newPostBranchStart = viewModel.IndexOf(
            "var client = entry.ClientId.HasValue",
            postedBranchStart,
            StringComparison.Ordinal);

        Assert.True(postedBranchStart >= 0, "The existing WHD note branch was not found.");
        Assert.True(newPostBranchStart > postedBranchStart, "The existing WHD note branch is incomplete.");

        var postedBranch = viewModel[postedBranchStart..newPostBranchStart];
        Assert.Contains("SynchronizeWhdEntryCoreAsync", postedBranch, StringComparison.Ordinal);
        Assert.Contains("UploadWhdImagesToTechNoteAsync", postedBranch, StringComparison.Ordinal);
        Assert.Contains("HandleWhdImageUploadResult", postedBranch, StringComparison.Ordinal);
        Assert.Contains("RefreshAfterWhdSync", postedBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void WhdImagePickerBelongsToTheMainNoteInsteadOfThePersonalNote()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        const string personalNoteMarker = "<Expander Header=\"{Binding Editor.InternalNoteHeader}\"";
        const string mainNoteMarker = "Text=\"{Binding Editor.Note, UpdateSourceTrigger=PropertyChanged}\"";
        const string imagePickerMarker = "ContentTemplate=\"{StaticResource WhdImageAttachmentPickerTemplate}\"";
        var personalNoteCount = 0;
        var cursor = 0;

        while (xaml.IndexOf(personalNoteMarker, cursor, StringComparison.Ordinal) is var personalStart
               && personalStart >= 0)
        {
            var personalEnd = xaml.IndexOf("</Expander>", personalStart, StringComparison.Ordinal);
            Assert.True(personalEnd > personalStart, "The Personal Note expander is incomplete.");
            var personalNote = xaml[personalStart..personalEnd];
            Assert.DoesNotContain(imagePickerMarker, personalNote, StringComparison.Ordinal);

            var mainNoteStart = xaml.LastIndexOf(mainNoteMarker, personalStart, StringComparison.Ordinal);
            var imagePickerStart = xaml.LastIndexOf(imagePickerMarker, personalStart, StringComparison.Ordinal);
            Assert.True(mainNoteStart >= 0, "The main Sage/WHD Note field was not found.");
            Assert.True(
                imagePickerStart > mainNoteStart,
                "The WHD image picker must follow the main Sage/WHD Note field.");

            personalNoteCount++;
            cursor = personalEnd + "</Expander>".Length;
        }

        Assert.Equal(2, personalNoteCount);
    }

    [Fact]
    public void WhdDeletionIsExactRemoteFirstAndSagePostedEntriesStayLocked()
    {
        var viewModel = ReadRepositoryFile("ViewModels", "MainWindowViewModel.cs");
        var deleteStart = viewModel.IndexOf(
            "private async Task DeleteEntryAsync",
            StringComparison.Ordinal);
        var deleteEnd = viewModel.IndexOf(
            "private bool DeleteLocalEntry",
            deleteStart,
            StringComparison.Ordinal);

        Assert.True(deleteStart >= 0, "The async entry deletion workflow was not found.");
        Assert.True(deleteEnd > deleteStart, "The entry deletion workflow is incomplete.");

        var deletion = viewModel[deleteStart..deleteEnd];
        var sageLock = deletion.IndexOf("if (entry.SagePosted)", StringComparison.Ordinal);
        var exactWhdDelete = deletion.IndexOf("DeleteTechNoteAsync", StringComparison.Ordinal);
        var recoveryRecord = deletion.IndexOf("RecordWhdSyncFailure", exactWhdDelete, StringComparison.Ordinal);
        var localDelete = deletion.IndexOf("DeleteLocalEntry(entry", StringComparison.Ordinal);

        Assert.True(sageLock >= 0, "Sage-posted entries must have an explicit deletion lock.");
        Assert.True(exactWhdDelete > sageLock, "The Sage lock must run before any WHD deletion.");
        Assert.True(recoveryRecord > exactWhdDelete, "A verified-missing recovery record must follow the exact WHD deletion.");
        Assert.True(localDelete > recoveryRecord, "The SQL recovery record must be saved before the local entry is deleted.");
        Assert.Contains("TryAcquireAsync(entry.Id, \"Sage\")", deletion, StringComparison.Ordinal);
        Assert.Contains("TryAcquireAsync(entry.Id, \"WHD\")", deletion, StringComparison.Ordinal);
        Assert.Contains("was not found. It was deleted at the user's request.", deletion, StringComparison.Ordinal);
        Assert.Contains("confirmMissingWhdTechNote: true", deletion, StringComparison.Ordinal);
        Assert.Contains("The TechBench entry was kept", deletion, StringComparison.Ordinal);
        Assert.Contains("permanently locked", deletion, StringComparison.Ordinal);
        Assert.Contains("did not delete either the local entry or its WHD TechNote", deletion, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var candidate = Path.Combine([current, .. parts]);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
