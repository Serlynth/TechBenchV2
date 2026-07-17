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
