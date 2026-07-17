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
}
