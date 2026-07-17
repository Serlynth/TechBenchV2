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
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("RequestWhdServerSyncCommand"));
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("RefreshWhdAdministrationCommand"));
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
