namespace TechBench.Models;

public sealed class V1ImportInProgressException : InvalidOperationException
{
    public V1ImportInProgressException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed record V1DatabaseImportResult(
    Guid BatchId,
    int WorkEntriesImported,
    int WorkEntriesSkipped,
    int LinksImported,
    int LinksSkipped,
    int PostingLogsImported,
    int PostingLogsSkipped,
    int ConflictCount,
    IReadOnlyList<string> ConflictMessages)
{
    public int ImportedCount =>
        WorkEntriesImported + LinksImported + PostingLogsImported;

    public int SkippedCount =>
        WorkEntriesSkipped + LinksSkipped + PostingLogsSkipped;
}

internal sealed record V1ImportItemOutcome(
    string Outcome,
    long? NewEntityId,
    string? Message)
{
    public bool Imported => Outcome.Equals("Imported", StringComparison.OrdinalIgnoreCase);
    public bool Skipped => Outcome.Equals("Skipped", StringComparison.OrdinalIgnoreCase);
    public bool Conflict => Outcome.Equals("Conflict", StringComparison.OrdinalIgnoreCase);
}
