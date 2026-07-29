using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class BenchModuleAccessTests
{
    [Theory]
    [InlineData(@"CSRI\rskoog")]
    [InlineData(@"csri\RSKOOG")]
    [InlineData(@" CSRI\rskoog ")]
    public void PrivateModulesAreAvailableOnlyToRyanSkoog(string loginName)
    {
        var user = CreateUser(loginName);

        Assert.True(BenchModuleAccess.CanAccessPrivateModules(user));
        Assert.Equal(
            BenchModule.SalesBench,
            BenchModuleAccess.ResolveRequestedModule("SalesBench", user));
        Assert.Equal(
            BenchModule.AdminBench,
            BenchModuleAccess.ResolveRequestedModule("AdminBench", user));
    }

    [Theory]
    [InlineData(@"CSRI\other.user")]
    [InlineData(@"OTHER\rskoog")]
    [InlineData("")]
    public void PrivateModulesAreDeniedToEveryOtherLogin(string loginName)
    {
        var user = CreateUser(loginName);

        Assert.False(BenchModuleAccess.CanAccessPrivateModules(user));
        Assert.Equal(
            BenchModule.TechBench,
            BenchModuleAccess.ResolveRequestedModule("SalesBench", user));
        Assert.Equal(
            BenchModule.TechBench,
            BenchModuleAccess.ResolveRequestedModule("AdminBench", user));
    }

    [Fact]
    public void PreviewCannotExposePrivateModulesByImpersonatingRyan()
    {
        var user = CreateUser(
            loginName: @"CSRI\rskoog",
            authenticatedLoginName: @"CSRI\other.admin");

        Assert.False(BenchModuleAccess.CanAccessPrivateModules(user));
    }

    [Fact]
    public void AuthenticatedRyanKeepsModuleAccessDuringAUserPreview()
    {
        var user = CreateUser(
            loginName: @"CSRI\preview.user",
            authenticatedLoginName: @"CSRI\rskoog");

        Assert.True(BenchModuleAccess.CanAccessPrivateModules(user));
    }

    private static CurrentUserContext CreateUser(
        string loginName,
        string? authenticatedLoginName = null) =>
        new(
            UserSid: [1, 2, 3],
            LoginName: loginName,
            DisplayName: "Test User",
            DatabaseInstanceId: Guid.NewGuid(),
            SchemaVersion: 15,
            ServerUtc: DateTime.UtcNow,
            IsTechnician: true,
            IsManager: true,
            IsAdmin: true,
            IsSyncOperator: true,
            AuthenticatedUserSid: authenticatedLoginName is null
                ? null
                : [4, 5, 6],
            AuthenticatedLoginName: authenticatedLoginName,
            AuthenticatedDisplayName: authenticatedLoginName,
            IsReadOnlyPreview: authenticatedLoginName is not null);
}
