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
    public void RepositoryExposesAdminManagedCommonTagCatalog()
    {
        Assert.NotNull(typeof(ITechBenchRepository).GetMethod(
            nameof(ITechBenchRepository.GetOrganizationTags)));
        Assert.NotNull(typeof(ITechBenchRepository).GetMethod(
            nameof(ITechBenchRepository.SaveOrganizationTag)));
        Assert.NotNull(typeof(ITechBenchRepository).GetMethod(
            nameof(ITechBenchRepository.DeleteOrganizationTag)));
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
        Assert.Contains("SageActivityItemId = settings.GetValueOrDefault(", reloadBody);
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
