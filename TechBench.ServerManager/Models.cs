namespace TechBench.ServerManager;

internal sealed record ServiceDetails(bool Installed, string Status, string Account, string Version);

internal sealed record SyncRequestReceipt(Guid RequestId, string Status);

internal sealed class SynchronizationConfiguration
{
    public Dictionary<string, string> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, byte[]> RowVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SyncStatus WhdStatus { get; set; } = new();
    public SyncStatus SageStatus { get; set; } = new();
    public SyncStatus FireDrillStatus { get; set; } = new();
    public List<UserMapping> UserMappings { get; } = [];
    public List<Technician> Technicians { get; } = [];
}

internal sealed class SyncStatus
{
    public Guid? RequestId { get; set; }
    public string Status { get; set; } = "NeverRun";
    public string Message { get; set; } = string.Empty;
    public int QueueDepth { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? LastSuccessfulAtUtc { get; set; }
    public string LastError { get; set; } = string.Empty;
    public bool RequiresLargeRemovalConfirmation { get; set; }
    public int ExistingCount { get; set; }
    public int ReadCount { get; set; }
    public int SavedCount { get; set; }
    public int StaleCount { get; set; }
}

internal sealed record DirectoryUser(string LoginName, string DisplayName, bool IsAdmin);

internal sealed record UserMapping(
    string LoginName,
    string DisplayName,
    bool IsAdmin,
    string TechnicianExternalId)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName)
        ? LoginName
        : $"{DisplayName} ({LoginName})";

    public override string ToString() => Label;
}

internal sealed record UserMappingAssignment(
    string LoginName,
    string DisplayName,
    bool IsAdmin,
    string TechnicianExternalId);

internal sealed record Technician(string ExternalId, string Label)
{
    public override string ToString() => Label;
}

internal sealed record ReleasePackage(
    string Version,
    string ZipName,
    Uri ZipUrl,
    long ZipSize,
    string ChecksumName,
    Uri ChecksumUrl,
    long ChecksumSize);
