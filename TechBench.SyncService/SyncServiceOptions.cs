namespace TechBench.SyncService;

public sealed class SyncServiceOptions
{
    public const string SectionName = "TechBenchSync";

    public string SqlServer { get; set; } = "CSRI-SQL.CSRI.local";
    public string Database { get; set; } = "TechBench";
    public bool TrustServerCertificate { get; set; }
    public int PollSeconds { get; set; } = 20;
    public int LeaseSeconds { get; set; } = 300;
    public int DeltaOverlapMinutes { get; set; } = 5;
    public int CommandTimeoutSeconds { get; set; } = 300;
    public int WhdRequestTimeoutSeconds { get; set; } = 90;
    public string? SecretPath { get; set; }
    public string? SageSecretPath { get; set; }
    public string? FireDrillSecretPath { get; set; }
    public string? SageOdbcWorkerPath { get; set; }
    public int SageOdbcTimeoutSeconds { get; set; } = 120;
    public int FinalizationTimeoutSeconds { get; set; } = 15;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Clamp(PollSeconds, 5, 300));
    public int EffectiveLeaseSeconds => Math.Clamp(LeaseSeconds, 120, 3600);
    public TimeSpan DeltaOverlap => TimeSpan.FromMinutes(Math.Clamp(DeltaOverlapMinutes, 1, 60));
    public int EffectiveCommandTimeoutSeconds => Math.Clamp(CommandTimeoutSeconds, 30, 1800);
    public TimeSpan WhdRequestTimeout => TimeSpan.FromSeconds(Math.Clamp(WhdRequestTimeoutSeconds, 15, 600));
    public TimeSpan SageOdbcTimeout => TimeSpan.FromSeconds(Math.Clamp(SageOdbcTimeoutSeconds, 30, 900));
    public TimeSpan FinalizationTimeout => TimeSpan.FromSeconds(Math.Clamp(FinalizationTimeoutSeconds, 5, 30));

    public string ResolveSecretPath()
    {
        if (!string.IsNullOrWhiteSpace(SecretPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(SecretPath));
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CSRI",
            "TechBench Sync Service",
            "whd.secret");
    }

    public string ResolveSageSecretPath()
    {
        if (!string.IsNullOrWhiteSpace(SageSecretPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(SageSecretPath));
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CSRI",
            "TechBench Sync Service",
            "sage.secret");
    }

    public string ResolveFireDrillSecretPath()
    {
        if (!string.IsNullOrWhiteSpace(FireDrillSecretPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(FireDrillSecretPath));
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CSRI",
            "TechBench Sync Service",
            "firedrill.secret");
    }

    public string ResolveSageOdbcWorkerPath()
    {
        if (!string.IsNullOrWhiteSpace(SageOdbcWorkerPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(SageOdbcWorkerPath));
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "sage-odbc-worker",
            "TechBench.SageOdbcWorker.exe");
    }
}
