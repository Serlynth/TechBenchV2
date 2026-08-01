using System.Net;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using TechBench.SyncService;

namespace TechBench.Tests;

public sealed class AuthPointMfaTests
{
    private static readonly AuthPointApiConfiguration Configuration = new(
        true,
        "https://api.usa.cloud.watchguard.com",
        "ACC-1234567",
        "1234",
        "access-id");
    private static readonly AuthPointProtectedCredentials Credentials = new(
        "access-password-super-secret",
        "api-key-super-secret");

    [Fact]
    public async Task PushAuthenticationChecksPolicyBeforeApprovingTransaction()
    {
        var handler = new QueueHandler(
            Json(HttpStatusCode.OK, """{"access_token":"bearer-secret","expires_in":3600}"""),
            Json(HttpStatusCode.OK, Policy(push: true, password: false)),
            Json(HttpStatusCode.OK, """{"transactionId":"03b68c49-3770-4f71-9f90-c0da1fc9584e"}"""),
            Json(HttpStatusCode.OK, """{"authenticationResult":"AUTHORIZED"}"""));
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new AuthPointApiClient(httpClient);

        var result = await client.AuthenticatePushAsync(
            Configuration,
            Credentials,
            "user@example.com",
            "TECH-01",
            CancellationToken.None);

        Assert.Equal(AuthPointMfaResultKind.Approved, result.Kind);
        Assert.Equal("AUTHORIZED", result.Code);
        Assert.Equal(4, handler.Requests.Count);
        Assert.EndsWith("/oauth/token", handler.Requests[0].Uri, StringComparison.Ordinal);
        Assert.EndsWith("/authenticationpolicy", handler.Requests[1].Uri, StringComparison.Ordinal);
        Assert.EndsWith("/transactions", handler.Requests[2].Uri, StringComparison.Ordinal);
        Assert.EndsWith(
            "/transactions/03b68c49-3770-4f71-9f90-c0da1fc9584e",
            handler.Requests[3].Uri,
            StringComparison.Ordinal);
        Assert.Contains("\"type\":\"PUSH\"", handler.Requests[2].Body, StringComparison.Ordinal);
        Assert.Contains("\"password\":\"\"", handler.Requests[2].Body, StringComparison.Ordinal);
        Assert.Contains("\"machineName\":\"TECH-01\"", handler.Requests[2].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingPushIsPolledUntilWatchGuardReportsApproval()
    {
        var handler = new QueueHandler(
            Json(HttpStatusCode.OK, """{"access_token":"bearer-secret","expires_in":3600}"""),
            Json(HttpStatusCode.OK, Policy(push: true, password: false)),
            Json(HttpStatusCode.OK, """{"transactionId":"03b68c49-3770-4f71-9f90-c0da1fc9584e"}"""),
            Json(HttpStatusCode.OK, """{"authenticationResult":"PENDING"}"""),
            Json(HttpStatusCode.OK, """{"authenticationResult":"AUTHORIZED"}"""));
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new AuthPointApiClient(
            httpClient,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2));

        var result = await client.AuthenticatePushAsync(
            Configuration,
            Credentials,
            "user@example.com",
            "TECH-01",
            CancellationToken.None);

        Assert.Equal(AuthPointMfaResultKind.Approved, result.Kind);
        Assert.Equal("AUTHORIZED", result.Code);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(handler.Requests[3].Uri, handler.Requests[4].Uri);
    }

    [Theory]
    [InlineData("http://api.usa.cloud.watchguard.com")]
    [InlineData("https://watchguard.example.com")]
    [InlineData("https://api.usa.cloud.watchguard.com.evil.example")]
    [InlineData("https://api.usa.cloud.watchguard.com/path")]
    [InlineData("https://user@api.usa.cloud.watchguard.com")]
    public async Task RejectsUnapprovedWatchGuardApiHosts(string baseUrl)
    {
        using var httpClient = new HttpClient(new QueueHandler());
        var client = new AuthPointApiClient(httpClient);
        var result = await client.AuthenticatePushAsync(
            Configuration with { BaseApiUrl = baseUrl },
            Credentials,
            "user@example.com",
            "TECH-01",
            CancellationToken.None);

        Assert.Equal(AuthPointMfaResultKind.Error, result.Kind);
        Assert.Equal("CONFIG_INVALID_HOST", result.Code);
    }

    [Fact]
    public async Task RefusesPolicyThatRequiresAnAuthPointPassword()
    {
        var handler = new QueueHandler(
            Json(HttpStatusCode.OK, """{"access_token":"bearer-secret"}"""),
            Json(HttpStatusCode.OK, Policy(push: true, password: true)));
        using var httpClient = new HttpClient(handler);
        var result = await new AuthPointApiClient(httpClient).AuthenticatePushAsync(
            Configuration,
            Credentials,
            "user@example.com",
            "TECH-01",
            CancellationToken.None);

        Assert.Equal(AuthPointMfaResultKind.Error, result.Kind);
        Assert.Equal("POLICY_PASSWORD_REQUIRED", result.Code);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RefusesAnOverallocatedAuthPointUser()
    {
        var handler = new QueueHandler(
            Json(HttpStatusCode.OK, """{"access_token":"bearer-secret"}"""),
            Json(HttpStatusCode.OK, Policy(
                push: true,
                password: false,
                isInOverallocated: true)));
        using var httpClient = new HttpClient(handler);

        var result = await new AuthPointApiClient(httpClient).AuthenticatePushAsync(
            Configuration,
            Credentials,
            "user@example.com",
            "TECH-01",
            CancellationToken.None);

        Assert.Equal(AuthPointMfaResultKind.Denied, result.Kind);
        Assert.Equal("POLICY_NOT_ALLOWED", result.Code);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DeniedPushFailsClosed()
    {
        var handler = new QueueHandler(
            Json(HttpStatusCode.OK, """{"access_token":"bearer-secret"}"""),
            Json(HttpStatusCode.OK, Policy(push: true, password: false)),
            Json(HttpStatusCode.OK, """{"transactionId":"03b68c49-3770-4f71-9f90-c0da1fc9584e"}"""),
            Json(HttpStatusCode.Forbidden, """{"detail":"201005001"}"""));
        using var httpClient = new HttpClient(handler);
        var result = await new AuthPointApiClient(httpClient).AuthenticatePushAsync(
            Configuration,
            Credentials,
            "user@example.com",
            "TECH-01",
            CancellationToken.None);

        Assert.Equal(AuthPointMfaResultKind.Denied, result.Kind);
        Assert.Equal("PUSH_DENIED", result.Code);
    }

    [Fact]
    public async Task ProviderErrorsNeverReturnCredentialValues()
    {
        var handler = new QueueHandler(Json(HttpStatusCode.InternalServerError, "{}"));
        using var httpClient = new HttpClient(handler);
        var result = await new AuthPointApiClient(httpClient).AuthenticatePushAsync(
            Configuration,
            Credentials,
            "user@example.com",
            "TECH-01",
            CancellationToken.None);

        var output = result.Code + result.Message + result.TransactionId;
        Assert.DoesNotContain(Credentials.AccessPassword, output, StringComparison.Ordinal);
        Assert.DoesNotContain(Credentials.ApiKey, output, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlAuthorizationIsSidBoundShortLivedAndSingleUse()
    {
        var source = ReadSql("64-V0015-AuthPointMfaProcedures.sql");
        Assert.Contains("SUSER_SID(ORIGINAL_LOGIN())", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[ActorWindowsSid]=@ActorSid", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[ActionScope]=@AccessAction", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[SecretId]=@SecretId", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HASHBYTES(N'SHA2_512',@AuthorizationToken)", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATEADD(second,60,@NowUtc)", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[Status]=N'Consumed'", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[AuthorizationTokenHash]=NULL", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[AuthorizationExpiresAtUtc]>@NowUtc", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoginMfaSessionIsSidClientAndTokenBound()
    {
        var schema = ReadSql("37-V0015-AuthPointMfaSchema.sql");
        var procedures = ReadSql("64-V0015-AuthPointMfaProcedures.sql");
        var grants = ReadSql("65-V0015-AuthPointMfaGrants.sql");

        Assert.Contains("[tb_security].[MfaLoginSessions]", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[ClientInstanceId] uniqueidentifier", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[SessionTokenHash] binary(64)", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[RequireAtLogin] bit", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUSER_SID(ORIGINAL_LOGIN())", procedures, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[ClientInstanceId]=@ClientInstanceId", procedures, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[SessionTokenHash]=HASHBYTES(N'SHA2_512',@MfaSessionToken)", procedures, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATEADD(hour,12", procedures, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AuthPoint.RequireAllUsers", procedures, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::[tb_security].[MfaLoginSessions]", grants, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExecuteAsOwnerProceduresPreserveCallerIdentityAndRelyOnExplicitGrants()
    {
        var procedures = ReadSql("64-V0015-AuthPointMfaProcedures.sql");
        var grants = ReadSql("65-V0015-AuthPointMfaGrants.sql");

        Assert.Contains("SUSER_SID(ORIGINAL_LOGIN())", procedures, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "IF IS_ROLEMEMBER(N'tb_role_sync_service')<>1",
            procedures,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "GRANT EXECUTE ON OBJECT::[tb_service].[ClaimAuthPointMfaChallenge]",
            grants,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::[tb_security].[MfaChallenges]",
            grants,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlChallengesPreventReplayAndCrossUserPolling()
    {
        var source = ReadSql("64-V0015-AuthPointMfaProcedures.sql");
        Assert.Contains("[ChallengeNonceHash]=HASHBYTES(N'SHA2_256',@ChallengeNonce)", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[ActorWindowsSid]=@ActorSid", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[AttemptCount]<3", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATEADD(minute,-2,@NowUtc)", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATEADD(minute,-15,@NowUtc)", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH (UPDLOCK,HOLDLOCK)", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthPointSqlIsSqlServer2016CompatibleAndKeepsSchemaAt15()
    {
        foreach (var file in new[]
                 {
                     "37-V0015-AuthPointMfaSchema.sql",
                     "64-V0015-AuthPointMfaProcedures.sql",
                     "65-V0015-AuthPointMfaGrants.sql",
                     "107-V0015-AuthPointMfaVerify.sql"
                 })
        {
            var parser = new TSql130Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(RemoveSqlCmdCommands(ReadSql(file)));
            _ = parser.Parse(reader, out var errors);
            Assert.Empty(errors);
        }

        Assert.Contains(
            "(N'SqlServer2016.AuthPointMfa.0015', 15, N'0.6.6-beta.1'",
            ReadSql("37-V0015-AuthPointMfaSchema.sql"),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "(N'SqlServer2016.AuthPointLoginMfa.0015', 15, N'0.6.6-beta.2'",
            ReadSql("37-V0015-AuthPointMfaSchema.sql"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FireDrillAndStableClientStayOutsideAuthPointPath()
    {
        var fireDrill = ReadSql("50-V0008-FireDrillCredentialsProcedures.sql");
        Assert.DoesNotContain("MfaChallenge", fireDrill, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BeginClientSecretMfaChallenge", fireDrill, StringComparison.OrdinalIgnoreCase);

        var connectionWindow = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "DatabaseConnectionWindow.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "ViewModels",
            "ClientInfoBetaViewModel.cs"));
        Assert.Contains("#if TECHBENCH_CLIENT_INFO_BETA", connectionWindow, StringComparison.Ordinal);
        Assert.Contains("AuthPointLoginWindow", connectionWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientSecretAuthPointWindow", viewModel, StringComparison.Ordinal);
        Assert.Contains("RevealClientInfoSecret", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerManagerSupportsGlobalAndPerUserLoginRequirements()
    {
        var form = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "TechBench.ServerManager",
            "ServerManagerForm.cs"));
        var repository = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "TechBench.ServerManager",
            "SqlAdminRepository.cs"));
        var procedures = ReadSql("64-V0015-AuthPointMfaProcedures.sql");

        Assert.Contains("_authPointEnabled", form, StringComparison.Ordinal);
        Assert.Contains("_authPointRequireAllUsers", form, StringComparison.Ordinal);
        Assert.Contains("RequireAtLogin", form, StringComparison.Ordinal);
        Assert.Contains("Save per-user requirements", form, StringComparison.Ordinal);
        Assert.Contains("\"AuthPoint.RequireAllUsers\"", repository, StringComparison.Ordinal);
        Assert.DoesNotContain("reader.GetBoolean(requireOrdinal)", repository, StringComparison.Ordinal);
        Assert.Contains("AdminSaveAuthPointLoginPolicy", procedures, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServerSecretsAreDpapiProtectedAndNeverSqlSettings()
    {
        var store = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "TechBench.SyncService",
            "AuthPointSecretStore.cs"));
        var worker = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "TechBench.SyncService",
            "AuthPointMfaWorker.cs"));
        var sql = ReadSql("64-V0015-AuthPointMfaProcedures.sql")
                  + ReadSql("37-V0015-AuthPointMfaSchema.sql");
        Assert.Contains("DataProtectionScope.LocalMachine", store, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory", store, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthPoint.ApiKey", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AuthPoint.AccessPassword", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("result.Message,", worker, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string Policy(
        bool push,
        bool password,
        bool isInOverallocated = false) => $$"""
        {
          "hasPolicy": true,
          "policyResponse": { "password": {{password.ToString().ToLowerInvariant()}}, "push": {{push.ToString().ToLowerInvariant()}} },
          "isInQuarantine": false,
          "isAllowedToAuthenticate": true,
          "isInForgotToken": false,
          "isBlocked": false,
          "isInOverallocated": {{isInOverallocated.ToString().ToLowerInvariant()}}
        }
        """;

    private static string ReadSql(string name) => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "database",
        "sqlserver2016",
        name));

    private static string RemoveSqlCmdCommands(string source) => string.Join(
        Environment.NewLine,
        source.Split('\n').Where(line => !line.TrimStart().StartsWith(':')));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TechBench.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The TechBench repository root was not found.");
    }

    private sealed record RequestSnapshot(string Uri, string Body);

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestSnapshot(request.RequestUri!.AbsoluteUri, body));
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No fake AuthPoint response remains.");
            }

            return _responses.Dequeue();
        }
    }
}
