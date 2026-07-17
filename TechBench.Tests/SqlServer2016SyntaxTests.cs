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
    public void V0002VerifierUsesTheSchemaV6WhdServiceGrantBoundary()
    {
        var path = Path.Combine(FindSqlDirectory(), "91-V0002-OperationalVerify.sql");
        var source = File.ReadAllText(path);

        Assert.Contains(
            "@InstalledSchemaVersion NOT IN (2, 3, 4, 5, 6)",
            source,
            StringComparison.OrdinalIgnoreCase);

        var expectedGrantsStart = source.IndexOf(
            "DECLARE @ExpectedGrants",
            StringComparison.OrdinalIgnoreCase);
        var schemaV6Boundary = source.IndexOf(
            "IF @InstalledSchemaVersion < 6",
            expectedGrantsStart,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(expectedGrantsStart >= 0 && schemaV6Boundary > expectedGrantsStart);

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
            Assert.InRange(position, expectedGrantsStart, schemaV6Boundary - 1);
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
        "@InstalledSchemaVersion NOT IN (3, 4, 5, 6)")]
    [InlineData(
        "93-V0004-AdminSharedVerify.sql",
        "@InstalledSchemaVersion NOT IN (4, 5, 6)")]
    [InlineData(
        "94-V0005-TechBenchV1ImportVerify.sql",
        "@InstalledSchemaVersion NOT IN (5, 6)")]
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

    [Fact]
    public void V0004VerifierUsesTheSchemaV6WhdServiceGrantBoundary()
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
        var schemaV6Boundary = verifySource.IndexOf(
            "IF @InstalledSchemaVersion < 6",
            expectedGrantsStart,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(expectedGrantsStart >= 0 && schemaV6Boundary > expectedGrantsStart);

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
            Assert.InRange(position, expectedGrantsStart, schemaV6Boundary - 1);
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
