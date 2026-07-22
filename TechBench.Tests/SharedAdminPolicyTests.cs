using TechBench.Data;
using TechBench.Models;
using TechBench.Services;
using TechBench.ViewModels;

namespace TechBench.Tests;

public sealed class SharedAdminPolicyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void OnlyAdminsCanRunSharedSync(bool isAdmin, bool expected)
    {
        var user = CreateUser(isAdmin, isSyncOperator: false);

        Assert.Equal(expected, user.CanRunSharedSync);
        Assert.Equal(expected, user.CanManageSharedConfiguration);
    }

    [Fact]
    public void LegacySyncOperatorWithoutAdminCannotRunSharedSync()
    {
        var user = CreateUser(isAdmin: false, isSyncOperator: true);

        Assert.True(user.IsSyncOperator);
        Assert.False(user.CanRunSharedSync);
        Assert.False(user.CanManageSharedConfiguration);
    }

    [Fact]
    public void SharedAutoSyncScheduleIsNotAWorkstationPreference()
    {
        Assert.Null(typeof(LocalPreferences).GetProperty("WhdAutoSyncEnabled"));
        Assert.Null(typeof(LocalPreferences).GetProperty("WhdAutoSyncMinutes"));
    }

    [Fact]
    public void SettingsManualCustomerIdMappingSurfaceIsRemoved()
    {
        Assert.Null(typeof(MainWindowViewModel).GetProperty("SelectedSageMappingClient"));
        Assert.Null(typeof(MainWindowViewModel).GetProperty("SageMappedCustomerId"));
        Assert.Null(typeof(MainWindowViewModel).GetProperty("SaveSageCustomerMappingCommand"));
        Assert.Null(typeof(ITechBenchRepository).GetMethod("SaveClientSageMapping"));
    }

    [Fact]
    public void ClientSettingsShowPersonalPostingInputsButNotSharedIntegrationConfiguration()
    {
        var source = File.ReadAllText(FindRepositoryFile("MainWindow.xaml"));

        Assert.Contains("My Web Help Desk Posting", source, StringComparison.Ordinal);
        Assert.Contains("{Binding WhdUsername", source, StringComparison.Ordinal);
        Assert.Contains("WhdApiTokenPasswordBox", source, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding WhdBaseUrl", source, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding WhdAuthenticationModeOptions", source, StringComparison.Ordinal);

        Assert.Contains("My Sage 50 Posting", source, StringComparison.Ordinal);
        Assert.Contains("{Binding SageEmployeeId", source, StringComparison.Ordinal);
        Assert.Contains("{Binding SageActivityItemId", source, StringComparison.Ordinal);
        Assert.Contains("{Binding SageDsn", source, StringComparison.Ordinal);
        Assert.Contains("SagePasswordBox", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Test My Sage ODBC", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Billing type\"", source, StringComparison.Ordinal);

        var viewModel = File.ReadAllText(FindRepositoryFile("ViewModels", "MainWindowViewModel.cs"));
        Assert.Contains(
            "_repository.SaveSetting(\"Sage.ActivityItemId\", SageActivityItemId.Trim())",
            viewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_sageOdbcClient.ReadCustomersAsync", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientUsesOneEditablePersonalTagPickerWithoutCommonTagAdministration()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("MainWindow.xaml"));
        var notesViewModel = File.ReadAllText(FindRepositoryFile(
            "ViewModels",
            "MainWindowViewModel.Notes.cs"));

        Assert.Contains("Text=\"{Binding Editor.Tags, Mode=TwoWay", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding TagSuggestions}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("choose one you have used before", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Add saved tag", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Common Tags", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ManagedOrganizationTags", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedEditorTagSuggestion", notesViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void NewEntryShowsWorkDateBeforeClientAndTicketSelection()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("MainWindow.xaml"));
        var editorStart = xaml.IndexOf(
            "Text=\"{Binding EditorTitle}\"",
            StringComparison.Ordinal);
        var date = xaml.IndexOf("Text=\"Work date\"", editorStart, StringComparison.Ordinal);
        var client = xaml.IndexOf("Text=\"Client\"", editorStart, StringComparison.Ordinal);
        var ticket = xaml.IndexOf("Text=\"Ticket\"", editorStart, StringComparison.Ordinal);

        Assert.True(editorStart >= 0);
        Assert.True(date > editorStart);
        Assert.True(client > date);
        Assert.True(ticket > date);
    }

    [Fact]
    public void WorkstationDoesNotExposeOrganizationWhdSyncCommandsOrTimer()
    {
        Assert.Null(typeof(MainWindowViewModel).GetProperty("SyncWhdTicketsCommand"));
        Assert.Null(typeof(MainWindowViewModel).GetProperty("SyncWhdClientsCommand"));
        Assert.Null(typeof(MainWindowViewModel).GetProperty("SyncWhdStatusesCommand"));
        Assert.Null(typeof(MainWindowViewModel).GetProperty("RequestWhdServerSyncCommand"));
        Assert.Null(typeof(MainWindowViewModel).GetProperty("RefreshWhdAdministrationCommand"));
        Assert.DoesNotContain(
            typeof(MainWindowViewModel).GetFields(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic),
            field => field.Name.Contains("WhdAutoSyncTimer", StringComparison.Ordinal));

        var source = File.ReadAllText(FindRepositoryFile(
            "ViewModels",
            "MainWindowViewModel.cs"));
        Assert.DoesNotContain("_whdRestClient.GetTicketAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_whdRestClient.GetOrganizationTickets", source, StringComparison.Ordinal);
        Assert.Contains("server-synchronized ticket inventory", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryExposesServerWhdQueueStatusAndMappingContracts()
    {
        Assert.NotNull(typeof(ITechBenchRepository).GetMethod(nameof(ITechBenchRepository.GetWhdSyncStatus)));
        Assert.NotNull(typeof(ITechBenchRepository).GetMethod(nameof(ITechBenchRepository.RequestWhdSync)));
        Assert.NotNull(typeof(ITechBenchRepository).GetMethod(nameof(ITechBenchRepository.GetWhdUserMappings)));
        Assert.NotNull(typeof(ITechBenchRepository).GetMethod(nameof(ITechBenchRepository.SaveWhdUserMapping)));
        Assert.NotNull(typeof(ITechBenchRepository).GetMethod(nameof(ITechBenchRepository.GetWhdTechnicians)));
    }

    [Fact]
    public void RepositoryExposesManualServerSageQueueAndStatusContracts()
    {
        var requestMethod = typeof(ITechBenchRepository).GetMethod(
            nameof(ITechBenchRepository.RequestSageSync));
        Assert.NotNull(requestMethod);
        var requestParameters = requestMethod!.GetParameters();
        Assert.Equal(2, requestParameters.Length);
        Assert.Equal(typeof(bool), requestParameters[0].ParameterType);
        Assert.Equal(typeof(Guid?), requestParameters[1].ParameterType);
        Assert.NotNull(typeof(ITechBenchRepository).GetMethod(
            nameof(ITechBenchRepository.GetSageSyncStatus)));

        Assert.NotNull(typeof(SageSyncServiceStatus).GetProperty(
            nameof(SageSyncServiceStatus.RequiresLargeRemovalConfirmation)));
        Assert.Null(typeof(MainWindowViewModel).GetProperty("SyncSageCustomersCommand"));
        Assert.Null(typeof(MainWindowViewModel).GetProperty("CanConfirmLargeSageRemoval"));

        Assert.Null(typeof(LocalPreferences).GetProperty("SageCustomerSyncMinutes"));
        Assert.Null(typeof(LocalPreferences).GetProperty("SageCustomerAutoSyncEnabled"));
    }

    [Fact]
    public void PeriodicSharedRefreshDoesNotPollServerSynchronizationOperations()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "ViewModels",
            "MainWindowViewModel.cs"));
        var timerStart = source.IndexOf(
            "private void HandleSharedDataRefreshTimerTick",
            StringComparison.Ordinal);
        var start = source.IndexOf(
            "private void ReloadOrganizationSettings()",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "private void RefreshTagSuggestions()",
            start,
            StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        Assert.True(timerStart >= 0 && start > timerStart);
        var timerBody = source[timerStart..start];
        Assert.Contains("CurrentSection.Equals(\"Settings\"", timerBody);
        Assert.Contains("if (!_settingsHaveUnsavedChanges)", timerBody);
        Assert.Contains("ReloadOrganizationSettings();", timerBody);

        var reloadBody = source[start..end];
        Assert.Contains("WhdBaseUrl = settings.GetValueOrDefault(", reloadBody);
        Assert.DoesNotContain("SageActivityItemId", reloadBody);
        Assert.DoesNotContain("Sage.SyncDsn", reloadBody);
        Assert.DoesNotContain("Sage.SyncUsername", reloadBody);
        Assert.DoesNotContain("RefreshWhdSyncServiceStatus", reloadBody);
        Assert.DoesNotContain("RefreshSageSyncServiceStatus", reloadBody);
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidateParts = new[] { directory.FullName }.Concat(relativeParts).ToArray();
            var candidate = Path.Combine(candidateParts);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the TechBenchV2 repository root.");
    }

    private static CurrentUserContext CreateUser(
        bool isAdmin,
        bool isSyncOperator) =>
        new(
            UserSid: [1, 2, 3],
            LoginName: "CSRI\\test.user",
            DisplayName: "test.user",
            DatabaseInstanceId: Guid.NewGuid(),
            SchemaVersion: 4,
            ServerUtc: DateTime.UtcNow,
            IsTechnician: true,
            IsManager: isAdmin,
            IsAdmin: isAdmin,
            IsSyncOperator: isSyncOperator);
}
