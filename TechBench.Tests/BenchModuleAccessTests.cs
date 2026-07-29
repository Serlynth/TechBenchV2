using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class BenchModuleAccessTests
{
    [Theory]
    [InlineData(@"CSRI\rskoog")]
    [InlineData(@"CSRI\other.admin")]
    [InlineData(@"OTHER\admin")]
    public void TechBenchAdminsCanAccessModulesRegardlessOfLoginName(
        string loginName)
    {
        var user = CreateUser(loginName, isAdmin: true);

        Assert.True(BenchModuleAccess.CanAccessModules(user));
        Assert.Equal(
            BenchModule.SalesBench,
            BenchModuleAccess.ResolveRequestedModule("SalesBench", user));
        Assert.Equal(
            BenchModule.AdminBench,
            BenchModuleAccess.ResolveRequestedModule("AdminBench", user));
    }

    [Theory]
    [InlineData(@"CSRI\other.user")]
    [InlineData(@"CSRI\rskoog")]
    [InlineData("")]
    public void NonAdminsCannotAccessModulesRegardlessOfLoginName(
        string loginName)
    {
        var user = CreateUser(loginName, isAdmin: false);

        Assert.False(BenchModuleAccess.CanAccessModules(user));
        Assert.Equal(
            BenchModule.TechBench,
            BenchModuleAccess.ResolveRequestedModule("SalesBench", user));
        Assert.Equal(
            BenchModule.TechBench,
            BenchModuleAccess.ResolveRequestedModule("AdminBench", user));
    }

    [Fact]
    public void UnknownModuleRequestsReturnToTechBench()
    {
        var admin = CreateUser(@"CSRI\admin", isAdmin: true);

        Assert.Equal(
            BenchModule.TechBench,
            BenchModuleAccess.ResolveRequestedModule("UnknownBench", admin));
    }

    private static CurrentUserContext CreateUser(
        string loginName,
        bool isAdmin) =>
        new(
            UserSid: [1, 2, 3],
            LoginName: loginName,
            DisplayName: "Test User",
            DatabaseInstanceId: Guid.NewGuid(),
            SchemaVersion: 15,
            ServerUtc: DateTime.UtcNow,
            IsTechnician: true,
            IsManager: isAdmin,
            IsAdmin: isAdmin,
            IsSyncOperator: false);
}
