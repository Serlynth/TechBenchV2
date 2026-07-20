namespace TechBench.Tests;

public sealed class ServerManagerSynchronizationConfigurationScriptTests
{
    [Fact]
    public void ManagerOwnsOrganizationWideWhdAndSageConfiguration()
    {
        var source = ReadRepositoryFile("scripts", "TechBench-ServerManager.ps1");

        foreach (var settingKey in new[]
                 {
                     "Whd.BaseUrl",
                     "Whd.AuthenticationMode",
                     "Whd.ServiceUsername",
                     "Whd.AutoSyncEnabled",
                     "Whd.AutoSyncMinutes",
                     "Sage.SyncDsn",
                     "Sage.SyncUsername",
                     "Sage.ActivityItemId"
                 })
        {
            Assert.Contains($"'{settingKey}'", source, StringComparison.Ordinal);
        }

        Assert.Contains("Shared synchronization configuration", source, StringComparison.Ordinal);
        Assert.Contains("Organization-wide WHD username", source, StringComparison.Ordinal);
        Assert.Contains("Server 32-bit System DSN", source, StringComparison.Ordinal);
        Assert.Contains("AD user to WHD technician", source, StringComparison.Ordinal);
        Assert.Contains("Save-WhdSynchronizationConfiguration", source, StringComparison.Ordinal);
        Assert.Contains("Save-SageSynchronizationConfiguration", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagerUsesWindowsAuthenticationAndAdminStoredProceduresOnly()
    {
        var source = ReadRepositoryFile("scripts", "TechBench-ServerManager.ps1");

        Assert.Contains("$builder.IntegratedSecurity = $true", source, StringComparison.Ordinal);
        Assert.Contains("$builder.Encrypt = $true", source, StringComparison.Ordinal);
        Assert.Contains("tb_app.GetCurrentUserContext", source, StringComparison.Ordinal);
        Assert.Contains("if (-not $isAdmin)", source, StringComparison.Ordinal);
        Assert.Contains("tb_app.GetSettings", source, StringComparison.Ordinal);
        Assert.Contains("tb_app.AdminSaveOrganizationSetting", source, StringComparison.Ordinal);
        Assert.Contains("tb_app.AdminRequestWhdSync", source, StringComparison.Ordinal);
        Assert.Contains("tb_app.AdminRequestSageSync", source, StringComparison.Ordinal);
        Assert.Contains("tb_app.AdminGetWhdUserMappings", source, StringComparison.Ordinal);
        Assert.Contains("tb_app.AdminGetWhdTechnicians", source, StringComparison.Ordinal);
        Assert.Contains("tb_app.AdminSaveWhdUserMapping", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT * FROM [tb_data]", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE [tb_data]", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharedSettingsSaveAtomicallyWithOptimisticConcurrency()
    {
        var source = ReadRepositoryFile("scripts", "TechBench-ServerManager.ps1");
        var start = source.IndexOf(
            "function Save-TechBenchOrganizationSettings",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "function Save-WhdSynchronizationConfiguration",
            start,
            StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        var body = source[start..end];
        Assert.Contains("$connection.BeginTransaction()", body, StringComparison.Ordinal);
        Assert.Contains("@ExpectedRowVersion", body, StringComparison.Ordinal);
        Assert.Contains("$transaction.Commit()", body, StringComparison.Ordinal);
        Assert.Contains("$transaction.Rollback()", body, StringComparison.Ordinal);
        Assert.Contains("@RequestId", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientNoLongerOffersServerSyncConfigurationOrTriggers()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var viewModel = ReadRepositoryFile("ViewModels", "MainWindowViewModel.cs");

        Assert.DoesNotContain("Request Server Sync Now", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Request Server Sage Sync", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Confirm Large Removal", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Save WHD User Mapping", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Server service username", xaml, StringComparison.Ordinal);
        Assert.Contains("configured in TechBench Server Manager", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestWhdServerSyncCommand", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncSageCustomersCommand", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshWhdAdministration", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Sage.SyncDsn", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Sage.SyncUsername", viewModel, StringComparison.Ordinal);

        var saveStart = viewModel.IndexOf("private void SaveSettings()", StringComparison.Ordinal);
        var saveEnd = viewModel.IndexOf(
            "private async Task TestWhdConnectionAsync",
            saveStart,
            StringComparison.Ordinal);
        Assert.True(saveStart >= 0 && saveEnd > saveStart);
        var saveBody = viewModel[saveStart..saveEnd];
        Assert.DoesNotContain("SaveOrganizationSetting", saveBody, StringComparison.Ordinal);
        Assert.Contains("SaveWhdConnectionSettings();", saveBody, StringComparison.Ordinal);
        Assert.Contains("SaveSageConnectionSettings();", saveBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalSystemSecretsRemainMachineProtectedAndOutsideSqlSettings()
    {
        var source = ReadRepositoryFile("scripts", "TechBench-ServerManager.ps1");

        Assert.Contains("Set-TechBenchSyncCredential.ps1", source, StringComparison.Ordinal);
        Assert.Contains("Set-TechBenchSageSyncCredential.ps1", source, StringComparison.Ordinal);
        Assert.DoesNotContain("'Whd.ApiToken' =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("'Sage.Password' =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WhdSecretBox.Text", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SageSecretBox.Text", source, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }
                .Concat(relativeParts)
                .ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the TechBenchV2 repository root.");
    }
}
