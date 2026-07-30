using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using TechBench.Data;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class V1ImportSafetyContractTests
{
    [Fact]
    public void ImportButtonIsAvailableToEveryUserInSettings()
    {
        var document = XDocument.Load(RepositoryFile("MainWindow.xaml"));
        var button = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "Button"
                && (string?)element.Attribute("Content") == "Import V1 Database...");

        Assert.Equal(
            "{Binding ImportV1DatabaseCommand}",
            (string?)button.Attribute("Command"));
        Assert.DoesNotContain(
            button.AncestorsAndSelf().Attributes(),
            attribute => attribute.Value.Contains(
                "CanManageOrganizationSettings",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ReaderExcludesEverySharedAndTransientV1TableByContract()
    {
        Assert.Equal(
            new[]
            {
                "ClientAliases",
                "Clients",
                "CommonLinks",
                "Templates",
                "Tickets",
                "TicketStatusOptions"
            },
            ReadPrivateStringArray("SharedExcludedTables")
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        Assert.Equal(
            new[] { "EditorDrafts", "PostingAttempts", "Settings" },
            ReadPrivateStringArray("OtherExcludedTables")
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    [Fact]
    public void ProductionBuildKeepsOnlyTheReadOnlySqliteImportSurface()
    {
        var document = XDocument.Load(RepositoryFile("TechBench.csproj"));
        var packageReferences = document
            .Descendants("PackageReference")
            .ToDictionary(
                element => (string)element.Attribute("Include")!,
                element => element,
                StringComparer.OrdinalIgnoreCase);

        foreach (var requiredPackage in new[]
        {
            "Microsoft.Data.Sqlite",
            "SQLitePCLRaw.lib.e_sqlite3"
        })
        {
            Assert.True(
                packageReferences.TryGetValue(requiredPackage, out var reference),
                $"The production V1 reader dependency '{requiredPackage}' is missing.");
            Assert.Null(reference!.Attribute("Condition"));
        }

        var productionItemGroup = Assert.Single(
            document.Descendants("ItemGroup"),
            element =>
                ((string?)element.Attribute("Condition"))?.Contains(
                    "TechBenchTestBuild",
                    StringComparison.Ordinal) == true
                && ((string?)element.Attribute("Condition"))?.Contains(
                    "!= 'true'",
                    StringComparison.Ordinal) == true);
        var removedSources = productionItemGroup
            .Elements("Compile")
            .Select(element => (string?)element.Attribute("Remove"))
            .Where(static value => value is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(@"Data\SqliteConnectionFactory.cs", removedSources);
        Assert.Contains(@"Data\TechBenchRepository.cs", removedSources);
        Assert.Contains(@"Providers\LocalClientProvider.cs", removedSources);
        Assert.Contains(@"Providers\LocalTicketProvider.cs", removedSources);
    }

    [Fact]
    public void ReleaseGuardRejectsLocalDatabasesAndRequiresX86ImporterRuntime()
    {
        var script = File.ReadAllText(
            RepositoryFile(@"scripts\Publish-TechBenchRelease.ps1"));

        Assert.Contains("db|sqlite|sqlite3", script, StringComparison.Ordinal);
        Assert.Contains("wal|shm|journal", script, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Data.Sqlite.dll", script, StringComparison.Ordinal);
        Assert.Contains("SQLitePCLRaw.core.dll", script, StringComparison.Ordinal);
        Assert.Contains("SQLitePCLRaw.provider.e_sqlite3.dll", script, StringComparison.Ordinal);
        Assert.Contains("e_sqlite3.dll", script, StringComparison.Ordinal);
        Assert.Contains("TechBench.WHD.dll", script, StringComparison.Ordinal);
        Assert.Contains("incompatible TechBench.WHD assembly", script, StringComparison.Ordinal);
        Assert.Contains("-p:PlatformTarget=x86", script, StringComparison.Ordinal);
        Assert.Contains("0x014c", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharedWhdBuildOutputsAreIsolatedByTargetArchitecture()
    {
        var project = File.ReadAllText(
            RepositoryFile(@"TechBench.WHD\TechBench.WHD.csproj"));

        Assert.Contains(@"bin\$(Configuration)\$(PlatformTarget)\", project, StringComparison.Ordinal);
        Assert.Contains(@"obj\$(Configuration)\$(PlatformTarget)\", project, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkEntryRetryHashIncludesResolvedServerReferences()
    {
        var method = typeof(SqlServerTechBenchRepository).GetMethod(
            "BuildEffectiveWorkEntryHash",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var entry = new WorkEntry
        {
            ManualClientName = "Legacy customer",
            TicketNumberText = "WHD-100"
        };
        var row = new V1WorkEntryImportRow
        {
            LegacyId = 1,
            ContentHash = new string('A', 64),
            WorkEntry = entry
        };

        var unresolved = InvokeEffectiveHash(method!, row, entry);
        row.ResolvedClientId = 42;
        var clientResolved = InvokeEffectiveHash(method!, row, entry);
        row.ResolvedTicketId = 9001;
        var ticketResolved = InvokeEffectiveHash(method!, row, entry);
        var repeated = InvokeEffectiveHash(method!, row, entry);

        Assert.Equal(64, unresolved.Length);
        Assert.NotEqual(unresolved, clientResolved);
        Assert.NotEqual(clientResolved, ticketResolved);
        Assert.Equal(ticketResolved, repeated);
    }

    [Fact]
    public void ImporterDoesNotSubmitDependentsOfAConflictedWorkEntry()
    {
        var source = File.ReadAllText(
            RepositoryFile(@"Data\SqlServerTechBenchRepository.V1Import.cs"));

        Assert.Contains("var conflictedWorkEntryIds = new HashSet<long>();", source, StringComparison.Ordinal);
        Assert.Contains("conflictedWorkEntryIds.Add(row.LegacyId);", source, StringComparison.Ordinal);
        Assert.Contains("conflictedWorkEntryIds.Contains(row.SourceLegacyWorkEntryId)", source, StringComparison.Ordinal);
        Assert.Contains("conflictedWorkEntryIds.Contains(row.TargetLegacyWorkEntryId)", source, StringComparison.Ordinal);
        Assert.Contains("conflictedWorkEntryIds.Contains(row.LegacyWorkEntryId)", source, StringComparison.Ordinal);
    }

    private static string InvokeEffectiveHash(
        MethodInfo method,
        V1WorkEntryImportRow row,
        WorkEntry entry) =>
        Assert.IsType<string>(method.Invoke(null, new object[] { row, entry }));

    private static string[] ReadPrivateStringArray(string fieldName)
    {
        var field = typeof(V1DatabaseImportReader).GetField(
            fieldName,
            BindingFlags.NonPublic | BindingFlags.Static);
        return Assert.IsType<string[]>(field?.GetValue(null));
    }

    private static string RepositoryFile(
        string relativePath,
        [CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourceFile)!,
            "..",
            relativePath));
}
