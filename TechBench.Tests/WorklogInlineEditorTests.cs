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
    public void WeekAndHistoryShareAPersistedResizableEditorWidth()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var codeBehind = ReadRepositoryFile("MainWindow.xaml.cs");
        var preferences = ReadRepositoryFile("Services", "LocalPreferenceStore.cs");

        Assert.Contains("x:Name=\"ThisWeekInlineEditorPane\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HistoryInlineEditorPane\"", xaml, StringComparison.Ordinal);
        Assert.Equal(
            2,
            xaml.Split("InlineWorkEntryEditorResizeThumb_DragDelta", StringSplitOptions.None).Length - 1);
        Assert.Contains("InlineWorkEntryEditorResizeThumb_DragCompleted", xaml, StringComparison.Ordinal);
        Assert.Contains("ApplyInlineWorkEntryEditorWidth", codeBehind, StringComparison.Ordinal);
        Assert.Contains("InlineEditorPaneWidth", preferences, StringComparison.Ordinal);
    }

    [Fact]
    public void WeekAndHistoryInlineEditorIncludesTheFullMoreMenu()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var viewModel = ReadRepositoryFile("ViewModels", "MainWindowViewModel.cs");
        const string templateMarker = "<DataTemplate x:Key=\"InlineWorkEntryEditorTemplate\">";
        var templateStart = xaml.IndexOf(templateMarker, StringComparison.Ordinal);
        var templateEnd = xaml.IndexOf("</DataTemplate>", templateStart, StringComparison.Ordinal);

        Assert.True(templateStart >= 0, "The inline editor template was not found.");
        Assert.True(templateEnd > templateStart, "The inline editor template is incomplete.");

        var template = xaml[templateStart..templateEnd];
        Assert.Contains("Content=\"More\"", template, StringComparison.Ordinal);
        Assert.Contains("Click=\"MoreActionsButton_Click\"", template, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding LinkSageTicketCommand}\"", template, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DuplicateEntryCommand}\"", template, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DeleteEntryCommand}\"", template, StringComparison.Ordinal);
        Assert.Contains("Delete from TechBench...", viewModel, StringComparison.Ordinal);
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
    public void WhdImagesUseTheSinglePostOrAttachActionWithoutMarkdownLabels()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var viewModel = ReadRepositoryFile("ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("x:Key=\"WhdImageAttachmentPickerTemplate\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("UploadWhdImagesCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Send to WHD", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Sync WHD Note", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Mark WHD Posted (Manual)", xaml, StringComparison.Ordinal);
        Assert.Contains("next Post or Attach Images to WHD action", xaml, StringComparison.Ordinal);
        Assert.Contains("not stored in TechBench or SQL", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Personal Note (Markdown)", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Personal Note Markdown", xaml, StringComparison.Ordinal);
        Assert.Contains("UploadWhdImagesToTechNoteAsync", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingWhdNotesOnlyAllowImageAttachments()
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
        Assert.DoesNotContain("SynchronizeWhdEntry", postedBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("PostAsync", postedBranch, StringComparison.Ordinal);
        Assert.Contains("UploadWhdImagesToTechNoteAsync", postedBranch, StringComparison.Ordinal);
        Assert.Contains("HandleWhdImageUploadResult", postedBranch, StringComparison.Ordinal);
        Assert.Contains("TryGetTrackedWhdNoteId", postedBranch, StringComparison.Ordinal);
        Assert.Contains("already posted to WHD", postedBranch, StringComparison.Ordinal);
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
    public void WhdPostedEntriesAllowExplicitLocalOnlyDeletionWhileSageStaysLocked()
    {
        var viewModel = ReadRepositoryFile("ViewModels", "MainWindowViewModel.cs");
        var deleteStart = viewModel.IndexOf(
            "private Task DeleteEntryAsync",
            StringComparison.Ordinal);
        var deleteEnd = viewModel.IndexOf(
            "private bool DeleteLocalEntry",
            deleteStart,
            StringComparison.Ordinal);

        Assert.True(deleteStart >= 0, "The entry deletion workflow was not found.");
        Assert.True(deleteEnd > deleteStart, "The entry deletion workflow is incomplete.");

        var deletion = viewModel[deleteStart..deleteEnd];
        var sageLock = deletion.IndexOf("if (entry.SagePosted)", StringComparison.Ordinal);
        var whdBranch = deletion.IndexOf("if (entry.WhdPosted)", StringComparison.Ordinal);
        var localDelete = deletion.IndexOf(
            "DeleteLocalEntry(entry, allowWhdPostedLocalDelete: true)",
            StringComparison.Ordinal);

        Assert.True(sageLock >= 0, "Sage-posted entries must have an explicit deletion lock.");
        Assert.True(whdBranch > sageLock, "The Sage lock must be checked before WHD-posted local deletion.");
        Assert.True(localDelete > whdBranch, "WHD-posted local deletion must require its explicit branch.");
        Assert.Contains("Only continue if you already deleted this note in WHD", deletion, StringComparison.Ordinal);
        Assert.Contains("I deleted it - Delete", deletion, StringComparison.Ordinal);
        Assert.Contains("This never deletes or changes anything in WHD", deletion, StringComparison.Ordinal);
        Assert.Contains("Undo restores an unposted TechBench draft", deletion, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteTechNoteAsync", deletion, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordWhdSync", deletion, StringComparison.Ordinal);

        var canDeleteStart = viewModel.IndexOf("private bool CanDeleteEditorEntry()", StringComparison.Ordinal);
        var canDeleteEnd = viewModel.IndexOf("private bool CanLinkSageTicket", canDeleteStart, StringComparison.Ordinal);
        var canDelete = viewModel[canDeleteStart..canDeleteEnd];
        Assert.Contains("!Editor.SagePosted", canDelete, StringComparison.Ordinal);
        Assert.DoesNotContain("!Editor.WhdPosted", canDelete, StringComparison.Ordinal);

        var undoStart = viewModel.IndexOf("private void UndoDelete()", StringComparison.Ordinal);
        var undoEnd = viewModel.IndexOf("private void DuplicateEntry()", undoStart, StringComparison.Ordinal);
        var undo = viewModel[undoStart..undoEnd];
        Assert.Contains("restored.WhdPosted = false", undo, StringComparison.Ordinal);
        Assert.Contains("restored.WhdPostedAt = null", undo, StringComparison.Ordinal);
        Assert.Contains("restored.PostingStatus = PostingStatus.Draft", undo, StringComparison.Ordinal);
    }

    [Fact]
    public void InitialWhdPostIsPersistedBeforeImageUploadBegins()
    {
        var viewModel = ReadRepositoryFile("ViewModels", "MainWindowViewModel.cs");
        var attemptStart = viewModel.IndexOf("var attemptStatus = result.OutcomeUncertain", StringComparison.Ordinal);
        var methodEnd = viewModel.IndexOf("private bool CanPostWhdEntry", attemptStart, StringComparison.Ordinal);
        var completion = viewModel.IndexOf("_repository.CompletePostingAttempt", attemptStart, StringComparison.Ordinal);
        var upload = viewModel.IndexOf("UploadWhdImagesToTechNoteAsync", completion, StringComparison.Ordinal);

        Assert.True(attemptStart >= 0 && methodEnd > attemptStart, "The posting completion branch was not found.");
        Assert.True(completion > attemptStart, "The verified WHD post must be completed durably.");
        Assert.True(upload > completion && upload < methodEnd, "Images must upload only after the WHD post is durable.");
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
