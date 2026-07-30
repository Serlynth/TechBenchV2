namespace TechBench.Tests;

public sealed class DatabaseConnectionUpdateRecoveryTests
{
    [Theory]
    [InlineData(
        "The TechBench database schema is version 8, but this client requires version 7. Contact the TechBench administrator.")]
    [InlineData(
        "the techbench DATABASE SCHEMA IS VERSION 9, BUT THIS CLIENT REQUIRES VERSION 8.")]
    public void SchemaMismatch_IsRecognizedForAutomaticUpdateRecovery(string status)
    {
        Assert.True(DatabaseConnectionWindow.IsSchemaVersionMismatch(status));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("The TechBench database could not be opened.")]
    [InlineData("TechBench 0.5.19 is up to date.")]
    public void OtherConnectionStatuses_DoNotTriggerAutomaticUpdateRecovery(string? status)
    {
        Assert.False(DatabaseConnectionWindow.IsSchemaVersionMismatch(status));
    }

    [Fact]
    public void ConnectionWindow_ExposesDatabaseIndependentUpdateActions()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "DatabaseConnectionWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "DatabaseConnectionWindow.xaml.cs"));

        Assert.Contains("x:Name=\"UpdateButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Check for updates\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CheckForUpdatesAsync(automaticRecovery: true)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("DownloadAndInstallUpdateAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CleanupDownloadedUpdatesAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("System.Windows.Application.Current.Shutdown()", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionWindow_UsesSimplifiedWindowsAuthentication()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "DatabaseConnectionWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "DatabaseConnectionWindow.xaml.cs"));

        Assert.Contains("Source=\"Assets/csri-techbench-logo.png\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Connect\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewAnotherUser", xaml + codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("TrustServerCertificateCheckBox", xaml + codeBehind, StringComparison.Ordinal);
        Assert.Contains("TrustServerCertificate: true", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void DatabaseOpenFailure_ExplainsStaleWindowsGroupToken()
    {
        var message = DatabaseConnectionWindow.BuildDatabaseOpenFailureMessage(
            new DatabaseConnectionWindow.WindowsAccessDiagnostic(
                @"CSRI\kallen",
                IsUser: false,
                IsAdmin: false));

        Assert.Contains(@"CSRI\kallen", message, StringComparison.Ordinal);
        Assert.Contains(@"CSRI\TechBench_Admins", message, StringComparison.Ordinal);
        Assert.Contains("fully sign out of Windows and sign back in", message, StringComparison.Ordinal);
        Assert.Contains("locking Windows", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DatabaseOpenFailure_WithAdminToken_ExplainsSqlRepair()
    {
        var message = DatabaseConnectionWindow.BuildDatabaseOpenFailureMessage(
            new DatabaseConnectionWindow.WindowsAccessDiagnostic(
                @"CSRI\kallen",
                IsUser: false,
                IsAdmin: true));

        Assert.Contains(@"CSRI\TechBench_Admins", message, StringComparison.Ordinal);
        Assert.Contains("DBA", message, StringComparison.Ordinal);
        Assert.Contains("SQL installer", message, StringComparison.Ordinal);
    }

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

        throw new DirectoryNotFoundException("TechBench repository root was not found.");
    }
}
