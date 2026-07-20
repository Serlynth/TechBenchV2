using Microsoft.Data.SqlClient;
using TechBench.Data;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class UserPreviewSecurityTests
{
    [Fact]
    public void PreviewLifetimeUsesServerTimeAndHardExpiry()
    {
        var serverUtc = new DateTime(2026, 7, 20, 14, 0, 0, DateTimeKind.Utc);
        var context = CreateUser(
            isAdmin: false,
            isReadOnlyPreview: true,
            previewedSid: [1, 2, 3],
            authenticatedSid: [9, 8, 7]) with
        {
            ServerUtc = serverUtc,
            PreviewExpiresAtUtc = serverUtc.AddMinutes(30)
        };

        Assert.Equal(TimeSpan.FromMinutes(30), MainWindow.ResolvePreviewTimeRemaining(context));
    }

    [Fact]
    public void PreviewContextIsReadOnlyAndUsesAuthenticatedIdentityForCredentials()
    {
        byte[] previewedSid = [1, 2, 3];
        byte[] authenticatedSid = [9, 8, 7];
        var context = CreateUser(
            isAdmin: true,
            isReadOnlyPreview: true,
            previewedSid,
            authenticatedSid);

        Assert.False(context.CanWrite);
        Assert.False(context.CanManageClients);
        Assert.False(context.CanRunSharedSync);
        Assert.False(context.CanManageSharedConfiguration);
        Assert.True(authenticatedSid.SequenceEqual(context.CredentialOwnerSid));
        Assert.Equal("Authenticated Admin", context.AuthenticationLabel);
    }

    [Fact]
    public void NormalAdminContextRetainsAdministrativeCapabilities()
    {
        var context = CreateUser(
            isAdmin: true,
            isReadOnlyPreview: false,
            previewedSid: [1, 2, 3],
            authenticatedSid: null);

        Assert.True(context.CanWrite);
        Assert.True(context.CanManageClients);
        Assert.True(context.CanRunSharedSync);
        Assert.True(context.CanManageSharedConfiguration);
        Assert.True(context.UserSid.SequenceEqual(context.CredentialOwnerSid));
    }

    [Fact]
    public void PreviewConnectionStringDisablesPoolingAndIdentifiesTheApplication()
    {
        var options = new SqlServerConnectionOptions("SQL01", "TechBench");

        var builder = new SqlConnectionStringBuilder(
            options.BuildConnectionString(
                pooling: false,
                applicationName: "TechBench V2 Read-only User Preview"));

        Assert.False(builder.Pooling);
        Assert.Equal("TechBench V2 Read-only User Preview", builder.ApplicationName);
        Assert.True(builder.IntegratedSecurity);
    }

    [Fact]
    public void PreviewFactoryCannotExposeAnUnactivatedConnection()
    {
        var factory = new SqlServerConnectionFactory(
            new SqlServerConnectionOptions("SQL01", "TechBench"));
        var authenticatedAdmin = CreateUser(
            isAdmin: true,
            isReadOnlyPreview: false,
            previewedSid: [9, 8, 7],
            authenticatedSid: null);
        var session = CreateSession();

        var previewFactory = factory.CreateReadOnlyPreviewFactory(
            session,
            authenticatedAdmin);

        Assert.True(previewFactory.IsReadOnlyPreview);
        Assert.Equal(session.PreviewSessionId, previewFactory.PreviewSessionId);
        var connectionString = new SqlConnectionStringBuilder(
            previewFactory.BuildConnectionString());
        Assert.False(connectionString.Pooling);
        Assert.Equal(
            "TechBench V2 Read-only User Preview",
            connectionString.ApplicationName);
        Assert.Throws<InvalidOperationException>(() => previewFactory.CreateConnection());
    }

    [Fact]
    public void NonAdminCannotCreatePreviewFactory()
    {
        var factory = new SqlServerConnectionFactory(
            new SqlServerConnectionOptions("SQL01", "TechBench"));
        var ordinaryUser = CreateUser(
            isAdmin: false,
            isReadOnlyPreview: false,
            previewedSid: [1, 2, 3],
            authenticatedSid: null);

        Assert.Throws<UnauthorizedAccessException>(() =>
            factory.CreateReadOnlyPreviewFactory(CreateSession(), ordinaryUser));
    }

    [Fact]
    public void PreviewUsesExactServerProcedureContracts()
    {
        Assert.Equal(
            "[tb_app].[AdminBeginUserPreview]",
            SqlServerConnectionFactory.BeginUserPreviewStoredProcedure);
        Assert.Equal(
            "[tb_app].[ActivateReadOnlyPreview]",
            SqlServerConnectionFactory.ActivateReadOnlyPreviewStoredProcedure);
        Assert.Equal(
            "[tb_app].[AdminEndUserPreview]",
            SqlServerConnectionFactory.EndUserPreviewStoredProcedure);
        Assert.Equal(
            "[tb_app].[AdminListPreviewUsers]",
            SqlServerConnectionFactory.ListPreviewUsersStoredProcedure);
        Assert.Equal(
            "EXECUTE AS USER = N'tb_preview_reader';",
            SqlServerConnectionFactory.PreviewReaderExecutionStatement);
    }

    [Theory]
    [InlineData("preview.user", "CSRI\\authenticated.admin", "IGNORED", "CSRI\\preview.user")]
    [InlineData(" preview.user ", "CSRI\\authenticated.admin", "IGNORED", "CSRI\\preview.user")]
    [InlineData("OTHER\\preview.user", "CSRI\\authenticated.admin", "IGNORED", "OTHER\\preview.user")]
    [InlineData("preview.user", "authenticated.admin", "CSRI", "CSRI\\preview.user")]
    public void PreviewLoginAcceptsDomainOrShortUsername(
        string enteredLogin,
        string authenticatedLogin,
        string fallbackDomain,
        string expected)
    {
        Assert.Equal(
            expected,
            SqlServerConnectionFactory.NormalizePreviewLoginName(
                enteredLogin,
                authenticatedLogin,
                fallbackDomain));
    }

    [Fact]
    public void PreviewLoginRejectsMissingUsername()
    {
        Assert.Throws<ArgumentException>(() =>
            SqlServerConnectionFactory.NormalizePreviewLoginName(
                "   ",
                "CSRI\\authenticated.admin",
                "CSRI"));
        Assert.Throws<ArgumentException>(() =>
            SqlServerConnectionFactory.NormalizePreviewLoginName(
                null,
                "CSRI\\authenticated.admin",
                "CSRI"));
    }

    [Fact]
    public void PreviewCredentialStoreNeverReadsOrPersistsSecrets()
    {
        var store = ReadOnlyPreviewCredentialStore.Instance;

        Assert.Equal(string.Empty, store.GetSecret("Whd.ApiToken"));
        store.SetSecret("Whd.ApiToken", "must-not-be-written");
        Assert.Equal(string.Empty, store.GetSecret("Whd.ApiToken"));
    }

    private static CurrentUserContext CreateUser(
        bool isAdmin,
        bool isReadOnlyPreview,
        byte[] previewedSid,
        byte[]? authenticatedSid) =>
        new(
            UserSid: previewedSid,
            LoginName: "CSRI\\preview.user",
            DisplayName: "Preview User",
            DatabaseInstanceId: Guid.NewGuid(),
            SchemaVersion: SqlServerConnectionFactory.SupportedSchemaVersion,
            ServerUtc: DateTime.UtcNow,
            IsTechnician: true,
            IsManager: isAdmin,
            IsAdmin: isAdmin,
            IsSyncOperator: false,
            AuthenticatedUserSid: authenticatedSid,
            AuthenticatedLoginName: authenticatedSid is null
                ? null
                : "CSRI\\authenticated.admin",
            AuthenticatedDisplayName: authenticatedSid is null
                ? null
                : "Authenticated Admin",
            IsReadOnlyPreview: isReadOnlyPreview,
            PreviewSessionId: isReadOnlyPreview ? Guid.NewGuid() : null,
            PreviewExpiresAtUtc: isReadOnlyPreview ? DateTime.UtcNow.AddMinutes(10) : null);

    private static UserPreviewSession CreateSession() =>
        new(
            PreviewSessionId: Guid.NewGuid(),
            ClientInstanceId: Guid.NewGuid(),
            UserSid: [1, 2, 3],
            LoginName: "CSRI\\preview.user",
            DisplayName: "Preview User",
            IsTechnician: true,
            IsManager: false,
            IsAdmin: false,
            IsSyncOperator: false,
            ExpiresAtUtc: DateTime.UtcNow.AddMinutes(10));
}
