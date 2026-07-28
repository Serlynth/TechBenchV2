using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TechBench.Tests;

public sealed partial class SqlServer2016SyntaxTests
{
    private const string GeneratedStandaloneFileName = "Deploy-CSRI-Standalone.sql";

    [Fact]
    public void SourceScriptsParseWithSqlServer2016Grammar()
    {
        var sqlDirectory = FindSqlDirectory();
        var files = Directory
            .EnumerateFiles(sqlDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).Equals(
                GeneratedStandaloneFileName,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.NotEmpty(files);

        var failures = new List<string>();
        foreach (var file in files)
        {
            var source = PreprocessSqlCmd(File.ReadAllText(file));
            var parser = new TSql130Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(source);
            _ = parser.Parse(reader, out var errors);
            failures.AddRange(errors.Select(error => FormatError(file, error)));
        }

        Assert.True(
            failures.Count == 0,
            $"SQL Server 2016 syntax parsing failed:{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void V0005DefersConflictCountBindingsUntilAfterTheColumnIsAdded()
    {
        var path = Path.Combine(FindSqlDirectory(), "24-V0005-TechBenchV1ImportSchema.sql");
        var source = PreprocessSqlCmd(File.ReadAllText(path));
        var parser = new TSql130Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(source);
        var fragment = parser.Parse(reader, out var errors);
        Assert.True(
            errors.Count == 0,
            string.Join(Environment.NewLine, errors.Select(error => FormatError(path, error))));

        var visitor = new ConflictCountBindingVisitor();
        fragment.Accept(visitor);

        var columnDefinition = Assert.Single(visitor.StaticIdentifiers);
        var deferredDdl = Assert.Single(visitor.DeferredDdl);
        var deferredParser = new TSql130Parser(initialQuotedIdentifiers: true);
        using var deferredReader = new StringReader(deferredDdl.Value);
        _ = deferredParser.Parse(deferredReader, out var deferredErrors);
        Assert.True(
            deferredErrors.Count == 0,
            "Deferred ConflictCount DDL is not valid SQL Server 2016 syntax:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, deferredErrors.Select(static error =>
                $"line {error.Line}, column {error.Column}, SQL{error.Number}: {error.Message}")));

        Assert.Contains(
            "ADD CONSTRAINT [CK_ImportBatches_Counts]",
            deferredDdl.Value,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "CREATE INDEX [IX_ImportBatches_OwnerSourceFileHash]",
            deferredDdl.Value,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "ADD [ConflictCount]",
            deferredDdl.Value,
            StringComparison.OrdinalIgnoreCase);

        var executePosition = source.IndexOf(
            "EXEC sys.sp_executesql @ConflictCountDependentSql",
            StringComparison.OrdinalIgnoreCase);
        Assert.True(
            executePosition > columnDefinition.StartOffset,
            "The ConflictCount-dependent dynamic DDL must execute after the static ALTER TABLE ADD.");
    }

    [Fact]
    public void V0002VerifierUsesTheSchemaV6AndV7ServiceGrantBoundaries()
    {
        var path = Path.Combine(FindSqlDirectory(), "91-V0002-OperationalVerify.sql");
        var source = File.ReadAllText(path);

        Assert.Contains(
            "@InstalledSchemaVersion NOT IN (2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15)",
            source,
            StringComparison.OrdinalIgnoreCase);

        var expectedGrantsStart = source.IndexOf(
            "DECLARE @ExpectedGrants",
            StringComparison.OrdinalIgnoreCase);
        var schemaV7Boundary = source.IndexOf(
            "IF @InstalledSchemaVersion < 7",
            expectedGrantsStart,
            StringComparison.OrdinalIgnoreCase);
        var schemaV6Boundary = source.IndexOf(
            "IF @InstalledSchemaVersion < 6",
            expectedGrantsStart,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(
            expectedGrantsStart >= 0
            && schemaV7Boundary > expectedGrantsStart
            && schemaV6Boundary > schemaV7Boundary);

        foreach (var retainedGrant in new[]
                 {
                     "AcquireSyncLease",
                     "SyncApplySageCustomerSnapshot"
                 })
        {
            var tuple = $"(N'tb_role_admin', N'tb_app.{retainedGrant}')";
            var position = source.IndexOf(
                tuple,
                expectedGrantsStart,
                StringComparison.OrdinalIgnoreCase);
            Assert.InRange(position, schemaV7Boundary + 1, schemaV6Boundary - 1);
        }

        foreach (var serviceOwnedGrant in new[]
                 {
                     "SyncApplyClientSnapshot",
                     "SyncApplyTicketSnapshot",
                     "SyncApplyTicketStatusSnapshot"
                 })
        {
            var tuple = $"(N'tb_role_admin', N'tb_app.{serviceOwnedGrant}')";
            var position = source.IndexOf(
                tuple,
                expectedGrantsStart,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(
                position > schemaV6Boundary,
                $"{serviceOwnedGrant} must be expected only before the schema V6 service boundary.");
        }
    }

    [Theory]
    [InlineData(
        "92-V0003-SharedReferenceVerify.sql",
        "@InstalledSchemaVersion NOT IN (3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15)")]
    [InlineData(
        "93-V0004-AdminSharedVerify.sql",
        "@InstalledSchemaVersion NOT IN (4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15)")]
    [InlineData(
        "94-V0005-TechBenchV1ImportVerify.sql",
        "@InstalledSchemaVersion NOT IN (5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15)")]
    public void EarlierSchemaVerifiersAcceptTheFinalSchemaVersion(
        string fileName,
        string expectedVersionCheck)
    {
        var path = Path.Combine(FindSqlDirectory(), fileName);
        Assert.Contains(
            expectedVersionCheck,
            File.ReadAllText(path),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("92-V0003-SharedReferenceVerify.sql")]
    [InlineData("93-V0004-AdminSharedVerify.sql")]
    [InlineData("94-V0005-TechBenchV1ImportVerify.sql")]
    public void EarlierSchemaVerifiersIgnoreCapabilityProcedureWhitespace(string fileName)
    {
        var source = File.ReadAllText(Path.Combine(FindSqlDirectory(), fileName));

        Assert.Contains(
            "N'CONVERT(int,' + CONVERT(nvarchar(10), @InstalledSchemaVersion) + N')'",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "REPLACE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities')), N' ', N'')",
            source,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V0004VerifierUsesTheSchemaV6AndV7ServiceGrantBoundaries()
    {
        var sqlDirectory = FindSqlDirectory();
        var verifySource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "93-V0004-AdminSharedVerify.sql"));
        var grantSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "54-V0006-WhdServerSyncGrants.sql"));
        var expectedGrantsStart = verifySource.IndexOf(
            "DECLARE @ExpectedGrants",
            StringComparison.OrdinalIgnoreCase);
        var schemaV7Boundary = verifySource.IndexOf(
            "IF @InstalledSchemaVersion < 7",
            expectedGrantsStart,
            StringComparison.OrdinalIgnoreCase);
        var schemaV6Boundary = verifySource.IndexOf(
            "IF @InstalledSchemaVersion < 6",
            expectedGrantsStart,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(
            expectedGrantsStart >= 0
            && schemaV7Boundary > expectedGrantsStart
            && schemaV6Boundary > schemaV7Boundary);

        foreach (var retainedGrant in new[]
                 {
                     "AcquireSyncLease",
                     "ReleaseSyncLease",
                     "BeginSyncRun",
                     "CompleteSyncRun",
                     "SyncApplySageCustomerSnapshot",
                     "SyncUpsertSageCustomer",
                     "SyncRemoveStaleSageCustomers",
                     "SyncUpsertClientExternalIdentity"
                 })
        {
            var tuple = $"(N'tb_role_admin', N'tb_app.{retainedGrant}')";
            var position = verifySource.IndexOf(
                tuple,
                expectedGrantsStart,
                StringComparison.OrdinalIgnoreCase);
            Assert.InRange(position, schemaV7Boundary + 1, schemaV6Boundary - 1);
        }

        foreach (var serviceOwnedGrant in new[]
                 {
                     "SyncApplyClientSnapshot",
                     "SyncApplyTicketSnapshot",
                     "SyncApplyTicketStatusSnapshot",
                     "SyncUpsertClient",
                     "SyncUpsertTicketStatusOption",
                     "SyncUpsertTicket"
                 })
        {
            var tuple = $"(N'tb_role_admin', N'tb_app.{serviceOwnedGrant}')";
            var position = verifySource.IndexOf(
                tuple,
                expectedGrantsStart,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(
                position > schemaV6Boundary,
                $"{serviceOwnedGrant} must be expected only before the schema V6 service boundary.");
            Assert.Contains(
                $"REVOKE EXECUTE ON OBJECT::[tb_app].[{serviceOwnedGrant}] FROM [tb_role_admin]",
                grantSource,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void V0006PublishesSchemaV6RepositoryCapabilities()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSqlDirectory(),
            "48-V0006-WhdServerSyncProcedures.sql"));

        Assert.Contains(
            "ALTER PROCEDURE [tb_app].[GetRepositoryCapabilities]",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "CONVERT(int, 6) AS [SchemaVersion]",
            source,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V0006VerifierForwardAllowsSchemaV7AutomaticMatchingProcedures()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSqlDirectory(),
            "95-V0006-WhdServerSyncVerify.sql"));
        var schemaV7Start = source.IndexOf(
            "IF @InstalledSchemaVersion >= 7",
            StringComparison.OrdinalIgnoreCase);
        Assert.True(schemaV7Start >= 0);

        var requiredGrantCheck = source.IndexOf(
            "IF EXISTS",
            schemaV7Start,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(requiredGrantCheck > schemaV7Start);
        var schemaV7AllowList = source[schemaV7Start..requiredGrantCheck];

        Assert.Contains(
            "(N'tb_service.GetAutomaticClientMatchCandidates')",
            schemaV7AllowList,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "(N'tb_service.ApplyAutomaticClientMatch')",
            schemaV7AllowList,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "(N'tb_service.ApplyAutomaticWhdFamilyMember')",
            schemaV7AllowList,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EarlierServicePermissionVerifiersAllowEveryFinalSchemaServiceGrant()
    {
        var sqlDirectory = FindSqlDirectory();
        var grantedProcedures = Directory
            .EnumerateFiles(sqlDirectory, "*Grants.sql")
            .SelectMany(path => Regex.Matches(
                File.ReadAllText(path),
                @"GRANT\s+EXECUTE\s+ON\s+OBJECT::\[tb_service\]\.\[(?<procedure>[^\]]+)\]\s+TO\s+\[tb_role_sync_service\]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Select(match => match.Groups["procedure"].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(grantedProcedures);

        foreach (var verifierFile in new[]
                 {
                     "95-V0006-WhdServerSyncVerify.sql",
                     "96-V0007-ServerOwnedSageAndAdminPreviewVerify.sql"
                 })
        {
            var verifierSource = File.ReadAllText(Path.Combine(sqlDirectory, verifierFile));
            foreach (var procedure in grantedProcedures)
            {
                Assert.Contains(
                    $"(N'tb_service.{procedure}')",
                    verifierSource,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void NormalTicketReadsNeverUseTheAdminOrganizationWideBypass()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSqlDirectory(),
            "48-V0006-WhdServerSyncProcedures.sql"));

        foreach (var procedureName in new[] { "SearchTickets", "GetTicket" })
        {
            var body = Regex.Replace(
                ProcedureBody(source, procedureName, "tb_app"),
                @"\s+",
                string.Empty);
            Assert.Contains("UserTechnicianMappings", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TechnicianGroupMemberships", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("OR@Admin=1", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WhdTicketApplicationRecoversAssignedTechniciansOmittedFromTheTechnicianList()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSqlDirectory(),
            "48-V0006-WhdServerSyncProcedures.sql"));
        var body = Regex.Replace(
            ProcedureBody(source, "ApplyWhdTicketBatch", "tb_service"),
            @"\s+",
            string.Empty);

        Assert.Contains("MERGE[tb_whd].[Technicians]", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[AssignedTechExternalId]ISNOTNULL", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[IsActive]=1", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[LastSyncedAtUtc]=@SyncedAtUtc", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhdAdminCanQueueTechnicianOnlySynchronization()
    {
        var sqlDirectory = FindSqlDirectory();
        var schema = Regex.Replace(
            File.ReadAllText(Path.Combine(sqlDirectory, "25-V0006-WhdServerSyncSchema.sql")),
            @"\s+",
            string.Empty);
        var procedure = Regex.Replace(
            ProcedureBody(
                File.ReadAllText(Path.Combine(sqlDirectory, "48-V0006-WhdServerSyncProcedures.sql")),
                "AdminRequestWhdSync",
                "tb_app"),
            @"\s+",
            string.Empty);

        Assert.Contains(
            "CHECK([RequestType]IN(N'Full',N'Incremental',N'Technicians'))",
            schema,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "@RequestTypeNOTIN(N'Full',N'Incremental',N'Technicians')",
            procedure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "@RequestType=N'Technicians'ANDwork_type.[WorkType]=N'Technicians'",
            procedure,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TagSuggestionsComeOnlyFromTheEffectiveUsersSavedWork()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSqlDirectory(),
            "41-V0002-WorkProcedures.sql"));
        var body = Regex.Replace(
            ProcedureBody(source, "GetDistinctTags", "tb_app"),
            @"\s+",
            string.Empty);

        Assert.Contains("[tb_data].[WorkEntries]", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("work_entry.[OwnerWindowsSid]=@UserSid", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("STRING_SPLIT", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[tb_data].[OrganizationTags]", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V0007PublishesServerOwnedSageAndReadOnlyPreviewContracts()
    {
        var sqlDirectory = FindSqlDirectory();
        var procedureSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "49-V0007-ServerOwnedSageAndAdminPreviewProcedures.sql"));
        var grantSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "55-V0007-ServerOwnedSageAndAdminPreviewGrants.sql"));
        var schemaSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "26-V0007-ServerOwnedSageAndAdminPreviewSchema.sql"));

        Assert.Contains("CONVERT(int, 7) AS [SchemaVersion]", procedureSource);
        Assert.Contains("CREATE PROCEDURE [tb_service].[ClaimSageSyncWork]", procedureSource);
        Assert.Contains("CREATE PROCEDURE [tb_service].[ApplySageCustomerSnapshot]", procedureSource);
        Assert.Contains("CREATE PROCEDURE [tb_service].[GetAutomaticClientMatchCandidates]", procedureSource);
        Assert.Contains("CREATE PROCEDURE [tb_service].[ApplyAutomaticClientMatch]", procedureSource);
        Assert.Contains("CREATE PROCEDURE [tb_service].[ApplyAutomaticWhdFamilyMember]", procedureSource);
        var requestSageBody = ProcedureBody(
            procedureSource,
            "AdminRequestSageSync",
            "tb_app");
        Assert.Contains("[SettingKey] = N'Sage.SyncDsn'", requestSageBody);
        Assert.Contains("[SettingKey] = N'Sage.SyncUsername'", requestSageBody);
        Assert.Contains("THROW 51923", requestSageBody);
        Assert.Contains("THROW 51924", requestSageBody);
        Assert.Contains("@AllowLargeRemoval bit = 0", requestSageBody);
        Assert.Contains("@ConfirmedRequestId uniqueidentifier = NULL", requestSageBody);
        Assert.Contains("[AllowLargeRemoval]", requestSageBody);
        Assert.Contains("[RequiresLargeRemovalConfirmation]", schemaSource);
        Assert.Contains("[ConfirmedRequestId]", schemaSource);
        Assert.Contains("[ExistingCount]", schemaSource);
        Assert.Contains("CREATE PROCEDURE [tb_app].[AdminBeginUserPreview]", procedureSource);
        Assert.Contains("@read_only=1", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "INSERT INTO [tb_sync].[SageSyncRequests]",
            ProcedureBody(procedureSource, "ClaimSageSyncWork"),
            StringComparison.OrdinalIgnoreCase);

        var postingLogBody = ProcedureBody(procedureSource, "GetPostingLogs", "tb_app");
        Assert.Contains(
            "CASE WHEN @IsReadOnlyPreview = 1 THEN N'' ELSE posting_log.[Payload] END AS [Payload]",
            postingLogBody,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "WHEN @IsReadOnlyPreview = 0 THEN posting_log.[Message]",
            postingLogBody,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "@IsReadOnlyPreview = 0",
            postingLogBody[postingLogBody.IndexOf("@KeywordPattern IS NULL", StringComparison.OrdinalIgnoreCase)..],
            StringComparison.OrdinalIgnoreCase);

        foreach (var procedureName in new[] { "SearchWorkEntries", "GetWorkEntry" })
        {
            Assert.Contains(
                "CASE WHEN @IsReadOnlyPreview = 1 THEN NULL ELSE work_entry.[LastError] END AS [LastError]",
                ProcedureBody(procedureSource, procedureName, "tb_app"),
                StringComparison.OrdinalIgnoreCase);
        }

        var applySageBody = ProcedureBody(
            procedureSource,
            "ApplySageCustomerSnapshot",
            "tb_service");
        Assert.Contains("DECLARE @RawSnapshot TABLE", applySageBody);
        Assert.Contains("[JsonType] <> 5", applySageBody);
        Assert.Contains("[CustomerIdCount] <> 1", applySageBody);
        Assert.Contains("LEN(LTRIM(RTRIM([CustomerId]))) > 120", applySageBody);
        Assert.Contains("HAVING COUNT(*) > 1", applySageBody);
        Assert.Contains("@ConfirmationMatches <> 1", applySageBody);
        Assert.Contains("@ExistingCount >= 20", applySageBody);
        Assert.Contains("@StaleCount >= 10", applySageBody);
        Assert.Contains("confirmed_request.[ExistingCount] = @ExistingCount", applySageBody);
        Assert.Contains("confirmed_request.[ReadCount] = @ReadCount", applySageBody);
        Assert.Contains("confirmed_request.[StaleCount] = @StaleCount", applySageBody);
        Assert.Contains("[RequiresLargeRemovalConfirmation] = 1", applySageBody);

        var automaticMatchBody = ProcedureBody(
            procedureSource,
            "ApplyAutomaticClientMatch",
            "tb_service");
        Assert.Contains("@MatchScore < CONVERT(decimal(6,5), 0.86000)", automaticMatchBody);
        Assert.Contains("whd_client.[Source] = N'WHD'", automaticMatchBody);
        Assert.Contains("sage_client.[Source] = N'Sage'", automaticMatchBody);
        Assert.Contains("[MatchStatus] = N'Matched'", automaticMatchBody);
        Assert.Contains("@Action = N'ClientAutoMatched'", automaticMatchBody);
        var familyMatchBody = ProcedureBody(
            procedureSource,
            "ApplyAutomaticWhdFamilyMember",
            "tb_service");
        Assert.Contains("@MatchScore < CONVERT(decimal(6,5), 0.86000)", familyMatchBody);
        Assert.Contains("target_client.[Source] = N'Both'", familyMatchBody);
        Assert.Contains("source_client.[Source] = N'WHD'", familyMatchBody);
        Assert.Contains("sage_identity.[SourceSystem] = N'Sage'", familyMatchBody);
        Assert.Contains("@Action = N'ClientAutoMatchedLocationFamily'", familyMatchBody);
        Assert.Contains(
            "GRANT EXECUTE ON OBJECT::[tb_service].[GetAutomaticClientMatchCandidates] TO [tb_role_sync_service]",
            grantSource,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "GRANT EXECUTE ON OBJECT::[tb_service].[ApplyAutomaticClientMatch] TO [tb_role_sync_service]",
            grantSource,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "GRANT EXECUTE ON OBJECT::[tb_service].[ApplyAutomaticWhdFamilyMember] TO [tb_role_sync_service]",
            grantSource,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "GRANT IMPERSONATE ON USER::[tb_preview_reader] TO [tb_role_admin]",
            grantSource,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "GRANT EXECUTE ON OBJECT::[tb_app].[GetEditorDraft] TO [tb_preview_reader]",
            grantSource,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V0007VerifierAllowsOnlyDatabaseConnectAndApprovedPreviewExecutions()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSqlDirectory(),
            "96-V0007-ServerOwnedSageAndAdminPreviewVerify.sql"));
        const string failureMessage =
            "PRINT N'FAIL: the preview reader has data/control or unexpected execution grants.';";
        var failureIndex = source.IndexOf(failureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(failureIndex >= 0);

        var predicateStart = source.LastIndexOf(
            "IF EXISTS",
            failureIndex,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(predicateStart >= 0);

        var predicate = Regex.Replace(
            source[predicateStart..failureIndex],
            @"\s+",
            string.Empty);
        Assert.Contains(
            "(permission_row.[class]=0" +
            "ANDpermission_row.[major_id]=0" +
            "ANDpermission_row.[minor_id]=0" +
            "ANDpermission_row.[permission_name]=N'CONNECT')",
            predicate,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "(permission_row.[class]=1" +
            "ANDpermission_row.[minor_id]=0" +
            "ANDpermission_row.[permission_name]=N'EXECUTE'" +
            "ANDEXISTS",
            predicate,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ANDNOT", predicate, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V0007RefreshesPreviewEligibilityAndReplacesRlsAtomically()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSqlDirectory(),
            "49-V0007-ServerOwnedSageAndAdminPreviewProcedures.sql"));

        var ensureBody = ProcedureBody(source, "EnsureCurrentUser", "tb_security");
        var roleRefresh = ensureBody.IndexOf(
            "UPDATE [tb_security].[Users] WITH (UPDLOCK, HOLDLOCK)",
            StringComparison.OrdinalIgnoreCase);
        var zeroRoleThrow = ensureBody.IndexOf(
            "IF @HasApplicationRole = 0",
            StringComparison.OrdinalIgnoreCase);
        Assert.True(roleRefresh >= 0 && zeroRoleThrow > roleRefresh);
        Assert.Contains("[IsTechnician] = @IsTechnician", ensureBody);
        Assert.Contains("[IsManager] = @IsManager", ensureBody);
        Assert.Contains("[IsAdmin] = @IsAdmin", ensureBody);
        Assert.Contains("[IsSyncOperator] = @IsSyncOperator", ensureBody);
        Assert.Contains("DATEADD(hour, -1, SYSUTCDATETIME())", ensureBody);

        foreach (var procedureName in new[]
                 {
                     "AdminListPreviewUsers",
                     "ActivateReadOnlyPreview"
                 })
        {
            Assert.Contains(
                "DATEADD(hour, -1, SYSUTCDATETIME())",
                ProcedureBody(source, procedureName, "tb_app"),
                StringComparison.OrdinalIgnoreCase);
        }

        var beginBody = ProcedureBody(source, "AdminBeginUserPreview", "tb_app");
        Assert.Contains("DATEADD(hour, -1, @Now)", beginBody);
        Assert.Contains("opened TechBench V2 within the past hour", beginBody);

        var rlsStart = source.IndexOf(
            "All DDL participates in one transaction",
            StringComparison.OrdinalIgnoreCase);
        var rlsEnd = source.IndexOf(
            "PRINT N'TechBench V0007 server-owned Sage sync",
            rlsStart,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(rlsStart >= 0 && rlsEnd > rlsStart);
        var rlsDeployment = source[rlsStart..rlsEnd];
        var beginTransaction = rlsDeployment.IndexOf("BEGIN TRANSACTION", StringComparison.OrdinalIgnoreCase);
        var dropPolicy = rlsDeployment.IndexOf(
            "DROP SECURITY POLICY",
            beginTransaction,
            StringComparison.OrdinalIgnoreCase);
        var alterFunction = rlsDeployment.IndexOf(
            "EXEC sys.sp_executesql @RlsFunctionSql",
            StringComparison.OrdinalIgnoreCase);
        var createPolicy = rlsDeployment.IndexOf(
            "EXEC sys.sp_executesql @RlsPolicySql",
            StringComparison.OrdinalIgnoreCase);
        var commit = rlsDeployment.IndexOf("COMMIT TRANSACTION", StringComparison.OrdinalIgnoreCase);
        Assert.True(
            beginTransaction >= 0
            && dropPolicy > beginTransaction
            && alterFunction > dropPolicy
            && createPolicy > alterFunction
            && commit > createPolicy);
        Assert.Contains("ROLLBACK TRANSACTION", rlsDeployment, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP FUNCTION", rlsDeployment, StringComparison.OrdinalIgnoreCase);

        foreach (var variableName in new[] { "@RlsFunctionSql", "@RlsPolicySql" })
        {
            var dynamicSql = ExtractUnicodeSqlLiteral(source, $"DECLARE {variableName}");
            var parser = new TSql130Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(dynamicSql);
            _ = parser.Parse(reader, out var errors);
            Assert.True(
                errors.Count == 0,
                $"{variableName} is not valid SQL Server 2016 SQL:{Environment.NewLine}"
                + string.Join(Environment.NewLine, errors.Select(static error =>
                    $"line {error.Line}, column {error.Column}, SQL{error.Number}: {error.Message}")));
        }
    }

    [Fact]
    public void StandaloneBuilderOrdersAllV0007Stages()
    {
        var root = Directory.GetParent(FindSqlDirectory())!.Parent!.FullName;
        var source = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Build-StandaloneSqlDeployment.ps1"));

        var schema = source.IndexOf("26-V0007-ServerOwnedSageAndAdminPreviewSchema.sql", StringComparison.Ordinal);
        var procedures = source.IndexOf("49-V0007-ServerOwnedSageAndAdminPreviewProcedures.sql", StringComparison.Ordinal);
        var grants = source.IndexOf("55-V0007-ServerOwnedSageAndAdminPreviewGrants.sql", StringComparison.Ordinal);
        var verify = source.IndexOf("96-V0007-ServerOwnedSageAndAdminPreviewVerify.sql", StringComparison.Ordinal);

        Assert.True(schema >= 0 && procedures > schema && grants > procedures && verify > grants);
    }

    [Fact]
    public void V0008PublishesEncryptedCredentialContractsWithoutRevealOrCopyAuditing()
    {
        var sqlDirectory = FindSqlDirectory();
        var schemaSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "27-V0008-FireDrillCredentialsSchema.sql"));
        var procedureSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "50-V0008-FireDrillCredentialsProcedures.sql"));
        var grantSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "56-V0008-FireDrillCredentialsGrants.sql"));

        Assert.Contains("WITH ALGORITHM = AES_256", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[AdminEncrypted] varbinary(max)", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[AdPasswordEncrypted] varbinary(max)", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[AdPassword] nvarchar", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH EXECUTE AS OWNER", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DecryptByKeyAutoCert", procedureSource, StringComparison.OrdinalIgnoreCase);
        var revealStart = procedureSource.IndexOf(
            "CREATE OR ALTER PROCEDURE [tb_app].[RevealFireDrillCredential]",
            StringComparison.OrdinalIgnoreCase);
        var revealEnd = procedureSource.IndexOf("\nGO", revealStart, StringComparison.OrdinalIgnoreCase);
        Assert.True(revealStart >= 0 && revealEnd > revealStart);
        var revealBody = procedureSource[revealStart..revealEnd];
        Assert.Contains("SESSION_CONTEXT(N'TechBench.PreviewSessionId')", revealBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EnsureCurrentUser", revealBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WriteAuditEvent", revealBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FireDrillCredentialRevealed", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FireDrillCredentialCopied", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DROP PROCEDURE [tb_app].[AuditFireDrillCredentialCopy]", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("N'04:00'", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[SettingKey] <> N'FireDrill.SourcePath' OR @CanReadServerPaths = 1", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@IsReadOnlyPreview = 0", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Configure the Credentials workbook path in Server Manager", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE [SettingKey]=N'FireDrill.SourcePath'), N'') AS [SourcePath]", procedureSource, StringComparison.OrdinalIgnoreCase);

        var claimStart = procedureSource.IndexOf(
            "CREATE OR ALTER PROCEDURE [tb_service].[ClaimFireDrillSyncWork]",
            StringComparison.OrdinalIgnoreCase);
        var claimEnd = procedureSource.IndexOf("\nGO", claimStart, StringComparison.OrdinalIgnoreCase);
        Assert.True(claimStart >= 0 && claimEnd > claimStart);
        var claimBody = procedureSource[claimStart..claimEnd];
        Assert.Contains("IS_ROLEMEMBER(N'tb_role_sync_service')", claimBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUSER_SID(ORIGINAL_LOGIN())", claimBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EnsureCurrentUser", claimBody, StringComparison.OrdinalIgnoreCase);

        foreach (var procedure in new[]
                 {
                     "SearchFireDrillCredentials",
                     "RevealFireDrillCredential"
                 })
        {
            Assert.Contains(
                $"GRANT EXECUTE ON OBJECT::[tb_app].[{procedure}] TO [tb_role_user]",
                grantSource,
                StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(
            "GRANT EXECUTE ON OBJECT::[tb_app].[AuditFireDrillCredentialCopy]",
            grantSource,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "GRANT EXECUTE ON OBJECT::[tb_app].[AdminRequestFireDrillSync] TO [tb_role_admin]",
            grantSource,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "GRANT EXECUTE ON OBJECT::[tb_app].[AdminRequestFireDrillSync] TO [tb_role_user]",
            grantSource,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FireDrillWorkbookLocationMustBeConfiguredInServerManager()
    {
        var root = Directory.GetParent(FindSqlDirectory())!.Parent!.FullName;
        var managerSource = File.ReadAllText(Path.Combine(
            root,
            "TechBench.ServerManager",
            "ServerManagerForm.cs"));
        var procedureSource = File.ReadAllText(Path.Combine(
            FindSqlDirectory(),
            "50-V0008-FireDrillCredentialsProcedures.sql"));

        Assert.Contains("Setting(\"FireDrill.SourcePath\")", managerSource, StringComparison.Ordinal);
        Assert.Contains("Workbook UNC path (Admin-only)", managerSource, StringComparison.Ordinal);
        Assert.Contains("WHERE [SettingKey]=N'FireDrill.SourcePath'), N'') AS [SourcePath]", procedureSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StandaloneBuilderOrdersAllV0008Stages()
    {
        var root = Directory.GetParent(FindSqlDirectory())!.Parent!.FullName;
        var source = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Build-StandaloneSqlDeployment.ps1"));

        var schema = source.IndexOf("27-V0008-FireDrillCredentialsSchema.sql", StringComparison.Ordinal);
        var procedures = source.IndexOf("50-V0008-FireDrillCredentialsProcedures.sql", StringComparison.Ordinal);
        var grants = source.IndexOf("56-V0008-FireDrillCredentialsGrants.sql", StringComparison.Ordinal);
        var verify = source.IndexOf("97-V0008-FireDrillCredentialsVerify.sql", StringComparison.Ordinal);

        Assert.True(schema >= 0 && procedures > schema && grants > procedures && verify > grants);
    }

    [Fact]
    public void StandaloneBuilderOrdersAllV0010ClientPresenceStages()
    {
        var root = Directory.GetParent(FindSqlDirectory())!.Parent!.FullName;
        var source = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Build-StandaloneSqlDeployment.ps1"));

        var schema = source.IndexOf("29-V0010-ClientPresenceSchema.sql", StringComparison.Ordinal);
        var procedures = source.IndexOf("51-V0010-ClientPresenceProcedures.sql", StringComparison.Ordinal);
        var grants = source.IndexOf("57-V0010-ClientPresenceGrants.sql", StringComparison.Ordinal);
        var verify = source.IndexOf("99-V0010-ClientPresenceVerify.sql", StringComparison.Ordinal);

        Assert.True(schema >= 0 && procedures > schema && grants > procedures && verify > grants);
    }

    [Fact]
    public void V0011PersistsClientResponsesAndOrdersEveryDeploymentStage()
    {
        var sqlDirectory = FindSqlDirectory();
        var schemaSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "30-V0011-ClientResponsesSchema.sql"));
        var procedureSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "52-V0011-ClientResponsesProcedures.sql"));
        var grantSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "58-V0011-ClientResponsesGrants.sql"));
        var verifySource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "100-V0011-ClientResponsesVerify.sql"));

        Assert.Contains("[ResponseMessage] nvarchar(500)", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("N'SaveFailed'", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@ResponseMessage nvarchar(500) = NULL", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[AdminGetRecentClientSessionResponses]", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GRANT EXECUTE ON OBJECT::[tb_app].[AdminGetRecentClientSessionResponses]", grantSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT IN (11, 12, 13, 14, 15)", verifySource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MAX([SchemaVersion]) FROM [tb_deploy].[SchemaMigrations]) <> 11", verifySource, StringComparison.OrdinalIgnoreCase);

        var root = Directory.GetParent(sqlDirectory)!.Parent!.FullName;
        var builder = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Build-StandaloneSqlDeployment.ps1"));
        var schema = builder.IndexOf("30-V0011-ClientResponsesSchema.sql", StringComparison.Ordinal);
        var procedures = builder.IndexOf("52-V0011-ClientResponsesProcedures.sql", StringComparison.Ordinal);
        var grants = builder.IndexOf("58-V0011-ClientResponsesGrants.sql", StringComparison.Ordinal);
        var verify = builder.IndexOf("100-V0011-ClientResponsesVerify.sql", StringComparison.Ordinal);

        Assert.True(schema >= 0 && procedures > schema && grants > procedures && verify > grants);
    }

    [Fact]
    public void V0012PersistsFlexibleEncryptedCredentialFieldsAndOrdersEveryDeploymentStage()
    {
        var sqlDirectory = FindSqlDirectory();
        var schemaSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "31-V0012-FlexibleCredentialFieldsSchema.sql"));
        var procedureSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "53-V0012-FlexibleCredentialFieldsProcedures.sql"));

        Assert.Contains("[tb_data].[FireDrillCredentialFields]", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[ValueEncrypted] varbinary(max)", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OPENJSON(row_data.[FieldsJson])", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EncryptByKey", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOR JSON PATH", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CONVERT(int, 14) AS [SchemaVersion]", procedureSource, StringComparison.OrdinalIgnoreCase);

        var root = Directory.GetParent(sqlDirectory)!.Parent!.FullName;
        var builder = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Build-StandaloneSqlDeployment.ps1"));
        var schema = builder.IndexOf("31-V0012-FlexibleCredentialFieldsSchema.sql", StringComparison.Ordinal);
        var procedures = builder.IndexOf("53-V0012-FlexibleCredentialFieldsProcedures.sql", StringComparison.Ordinal);
        var grants = builder.IndexOf("59-V0012-FlexibleCredentialFieldsGrants.sql", StringComparison.Ordinal);
        var verify = builder.IndexOf("101-V0012-FlexibleCredentialFieldsVerify.sql", StringComparison.Ordinal);

        Assert.True(schema >= 0 && procedures > schema && grants > procedures && verify > grants);
    }

    [Fact]
    public void V0013PersistsWhdClientContactDetailsAndOrdersDeployment()
    {
        var sqlDirectory = FindSqlDirectory();
        var schemaSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "32-V0013-WhdClientContactDetailsSchema.sql"));
        var applySource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "48-V0006-WhdServerSyncProcedures.sql"));
        var verifySource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "102-V0013-WhdClientContactDetailsVerify.sql"));

        Assert.Contains("[WhdContactEmail] nvarchar(255)", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[WhdPhone] nvarchar(80)", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[WhdAddress] nvarchar(600)", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'$.contactEmail'", applySource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("schema version 13", verifySource, StringComparison.OrdinalIgnoreCase);

        var root = Directory.GetParent(sqlDirectory)!.Parent!.FullName;
        var builder = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Build-StandaloneSqlDeployment.ps1"));
        var schema = builder.IndexOf("32-V0013-WhdClientContactDetailsSchema.sql", StringComparison.Ordinal);
        var procedures = builder.IndexOf("48-V0006-WhdServerSyncProcedures.sql", StringComparison.Ordinal);
        var verify = builder.IndexOf("102-V0013-WhdClientContactDetailsVerify.sql", StringComparison.Ordinal);

        Assert.True(schema >= 0 && procedures > schema && verify > procedures);
    }

    [Fact]
    public void V0014PersistsAdminEquipmentBoardAndOrdersEveryDeploymentStage()
    {
        var sqlDirectory = FindSqlDirectory();
        var schemaSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "33-V0014-EquipmentBoardSchema.sql"));
        var procedureSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "54-V0014-EquipmentBoardProcedures.sql"));
        var grantSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "60-V0014-EquipmentBoardGrants.sql"));
        var verifySource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "103-V0014-EquipmentBoardVerify.sql"));

        Assert.Contains("[tb_inventory].[Equipment]", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[AssignedToWindowsSid] varbinary(85)", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[WorkflowStage] nvarchar(24)", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("N'Deployment'", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[AdminMoveEquipment]", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@TargetWorkflowStage nvarchar(24)", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS_ROLEMEMBER(N'tb_role_admin')", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "@SourceStage NOT IN (N'Assigned', N'Deployment')",
            procedureSource,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "GRANT EXECUTE ON OBJECT::[tb_app].[AdminMoveEquipment] TO [tb_role_admin]",
            grantSource,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("schema version 14", verifySource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "N'IS_ROLEMEMBER(N''tb_role_admin'')'",
            verifySource,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "N'ISROLEMEMBER(N''tb_role_admin'')'",
            verifySource,
            StringComparison.OrdinalIgnoreCase);

        var root = Directory.GetParent(sqlDirectory)!.Parent!.FullName;
        var builder = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Build-StandaloneSqlDeployment.ps1"));
        var schema = builder.IndexOf("33-V0014-EquipmentBoardSchema.sql", StringComparison.Ordinal);
        var procedures = builder.IndexOf("54-V0014-EquipmentBoardProcedures.sql", StringComparison.Ordinal);
        var grants = builder.IndexOf("60-V0014-EquipmentBoardGrants.sql", StringComparison.Ordinal);
        var verify = builder.IndexOf("103-V0014-EquipmentBoardVerify.sql", StringComparison.Ordinal);

        Assert.True(schema >= 0 && procedures > schema && grants > procedures && verify > grants);
    }

    [Fact]
    public void V0014PreservesTheAccumulatedRepositoryCapabilityContract()
    {
        var sqlDirectory = FindSqlDirectory();
        var procedureSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "54-V0014-EquipmentBoardProcedures.sql"));
        var capabilityBody = ProcedureBody(
            procedureSource,
            "GetRepositoryCapabilities",
            "tb_app");
        var verificationSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "103-V0014-EquipmentBoardVerify.sql"));

        Assert.Contains(
            "CONVERT(int, 15) AS [SchemaVersion]",
            capabilityBody,
            StringComparison.OrdinalIgnoreCase);

        string[] accumulatedCapabilities =
        [
            "[FullTextSearchAvailable]",
            "[SupportsTickets]",
            "[SupportsWorkEntries]",
            "[SupportsPrivateNotes]",
            "[SupportsPostingLeases]",
            "[SupportsSyncLeases]",
            "[SupportsImports]",
            "[SupportsTechBenchV1Import]",
            "[SupportsServerSageSync]",
            "[SupportsAdminUserPreview]",
            "[SupportsFireDrillCredentials]",
            "[EquipmentBoardAvailable]"
        ];

        foreach (var capability in accumulatedCapabilities)
        {
            Assert.Contains(capability, capabilityBody, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(capability, verificationSource, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(
            "capabilities dropped one or more capabilities introduced by an earlier schema version",
            verificationSource,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V0015EncryptsEquipmentAnyDeskPasswordsAndOrdersDeployment()
    {
        var sqlDirectory = FindSqlDirectory();
        var schemaSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "34-V0015-EquipmentAnyDeskSchema.sql"));
        var procedureSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "54-V0014-EquipmentBoardProcedures.sql"));
        var verifySource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "104-V0015-EquipmentAnyDeskVerify.sql"));

        Assert.Contains("[AnyDeskNumber] nvarchar(80)", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[AnyDeskPasswordEncrypted] varbinary(max)", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[AnyDeskPassword] nvarchar", schemaSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@AnyDeskNumber nvarchar(80)", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@AnyDeskPassword nvarchar(max)", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EncryptByKey", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DecryptByKeyAutoCert", procedureSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "CAST(N'' AS nvarchar(max)) AS [AnyDeskPassword]",
            procedureSource,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "shared inventory reads do not expose the number while keeping the AnyDesk password private",
            verifySource,
            StringComparison.OrdinalIgnoreCase);

        var root = Directory.GetParent(sqlDirectory)!.Parent!.FullName;
        var builder = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Build-StandaloneSqlDeployment.ps1"));
        var schema = builder.IndexOf("34-V0015-EquipmentAnyDeskSchema.sql", StringComparison.Ordinal);
        var procedures = builder.IndexOf("54-V0014-EquipmentBoardProcedures.sql", StringComparison.Ordinal);
        var grants = builder.IndexOf("60-V0014-EquipmentBoardGrants.sql", StringComparison.Ordinal);
        var verify = builder.IndexOf("104-V0015-EquipmentAnyDeskVerify.sql", StringComparison.Ordinal);

        Assert.True(schema >= 0 && procedures > schema && grants > procedures && verify > grants);
    }

    [Fact]
    public void AutomaticWhdSynchronizationQueuesTicketsOnly()
    {
        var procedureSource = File.ReadAllText(Path.Combine(
            FindSqlDirectory(),
            "48-V0006-WhdServerSyncProcedures.sql"));
        var claimStart = procedureSource.IndexOf(
            "CREATE PROCEDURE [tb_service].[ClaimWhdSyncWork]",
            StringComparison.OrdinalIgnoreCase);
        var claimEnd = procedureSource.IndexOf(
            "CREATE PROCEDURE [tb_service].[RenewWhdSyncLease]",
            claimStart,
            StringComparison.OrdinalIgnoreCase);

        Assert.True(claimStart >= 0 && claimEnd > claimStart);
        var claimBody = procedureSource[claimStart..claimEnd];
        Assert.Contains(
            "WHERE work_type.[WorkType] = N'Tickets'",
            claimBody,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@IncludeReferenceWork", claimBody, StringComparison.OrdinalIgnoreCase);

        var verificationSource = File.ReadAllText(Path.Combine(
            FindSqlDirectory(),
            "95-V0006-WhdServerSyncVerify.sql"));
        Assert.Contains(
            "WHEREwork_type.[WorkType]=N''Tickets''",
            verificationSource,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "daily reference cadence",
            verificationSource,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V0009PermitsOnlyVerifiedMissingWhdTechNoteRecoveryDeletion()
    {
        var sqlDirectory = FindSqlDirectory();
        var procedureSource = File.ReadAllText(Path.Combine(
            sqlDirectory,
            "41-V0002-WorkProcedures.sql"));
        var body = ProcedureBody(procedureSource, "DeleteWorkEntry", "tb_app");

        Assert.Contains("@ConfirmMissingWhdTechNote bit = 0", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@SagePosted = 1", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHD sync pending:%TechNote #%was not found.%", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("posting_log.[ExternalReference]", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHD-TECHNOTE-%", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("posting_log.[Success] = 0", body, StringComparison.OrdinalIgnoreCase);

        var root = Directory.GetParent(sqlDirectory)!.Parent!.FullName;
        var builder = File.ReadAllText(Path.Combine(root, "scripts", "Build-StandaloneSqlDeployment.ps1"));
        var migration = builder.IndexOf("28-V0009-WhdMissingNoteRecovery.sql", StringComparison.Ordinal);
        var procedures = builder.IndexOf("41-V0002-WorkProcedures.sql", StringComparison.Ordinal);
        var verification = builder.IndexOf("98-V0009-WhdMissingNoteRecoveryVerify.sql", StringComparison.Ordinal);
        Assert.True(migration >= 0 && procedures > migration && verification > procedures);
    }

    private static string ProcedureBody(
        string source,
        string procedureName,
        string schemaName = "tb_service")
    {
        var start = source.IndexOf(
            $"CREATE PROCEDURE [{schemaName}].[{procedureName}]",
            StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            start = source.IndexOf(
                $"ALTER PROCEDURE [{schemaName}].[{procedureName}]",
                StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(start >= 0);
        var end = source.IndexOf("\nGO", start, StringComparison.OrdinalIgnoreCase);
        Assert.True(end > start);
        return source[start..end];
    }

    private static string ExtractUnicodeSqlLiteral(string source, string declarationMarker)
    {
        var declaration = source.IndexOf(declarationMarker, StringComparison.OrdinalIgnoreCase);
        Assert.True(declaration >= 0);
        var literal = source.IndexOf("= N'", declaration, StringComparison.OrdinalIgnoreCase);
        Assert.True(literal >= 0);

        var result = new StringBuilder();
        for (var index = literal + 4; index < source.Length; index++)
        {
            if (source[index] != '\'')
            {
                result.Append(source[index]);
                continue;
            }

            if (index + 1 < source.Length && source[index + 1] == '\'')
            {
                result.Append('\'');
                index++;
                continue;
            }

            return result.ToString();
        }

        throw new InvalidOperationException($"Unterminated SQL literal for {declarationMarker}.");
    }

    private static string PreprocessSqlCmd(string source)
    {
        var preprocessed = SqlCmdDirectiveLine().Replace(
            source,
            static match => "-- SQLCMD directive omitted by syntax test: " + match.Value.Trim());

        return preprocessed
            .Replace("$(DatabaseName)", "TechBenchSyntax", StringComparison.Ordinal)
            .Replace("$(UserGroup)", "CSRI\\TechBench_SyntaxUsers", StringComparison.Ordinal)
            .Replace("$(AdminGroup)", "CSRI\\TechBench_SyntaxAdmins", StringComparison.Ordinal)
            .Replace("$(SyncServicePrincipal)", "CSRI\\TechBench_SyntaxSync", StringComparison.Ordinal);
    }

    private static string FormatError(string file, ParseError error) =>
        $"{Path.GetFileName(file)}:{error.Line}:{error.Column}: SQL{error.Number}: {error.Message}";

    private static string FindSqlDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "database", "sqlserver2016");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate database{Path.DirectorySeparatorChar}sqlserver2016 above {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex(
        @"^[\t ]*:(?:ON[\t ]+ERROR|setvar)\b[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SqlCmdDirectiveLine();

    private sealed class ConflictCountBindingVisitor : TSqlFragmentVisitor
    {
        public List<Identifier> StaticIdentifiers { get; } = [];
        public List<StringLiteral> DeferredDdl { get; } = [];

        public override void ExplicitVisit(Identifier node)
        {
            if (node.Value.Equals("ConflictCount", StringComparison.OrdinalIgnoreCase))
            {
                StaticIdentifiers.Add(node);
            }
        }

        public override void ExplicitVisit(StringLiteral node)
        {
            if (node.Value.Contains("[ConflictCount]", StringComparison.OrdinalIgnoreCase)
                && node.Value.Contains("CK_ImportBatches_Counts", StringComparison.OrdinalIgnoreCase)
                && node.Value.Contains("IX_ImportBatches_OwnerSourceFileHash", StringComparison.OrdinalIgnoreCase))
            {
                DeferredDdl.Add(node);
            }
        }
    }
}
