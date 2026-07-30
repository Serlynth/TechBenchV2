namespace TechBench.Models;

public sealed class WorkEntryLink
{
    public int Id { get; set; }
    public int SourceWorkEntryId { get; set; }
    public int TargetWorkEntryId { get; set; }
    public int CurrentWorkEntryId { get; set; }
    public WorkEntryLinkType LinkType { get; set; }
    public DateTime CreatedAt { get; set; }
    public byte[]? RowVersion { get; set; }
    public WorkEntry RelatedEntry { get; set; } = new();

    public string RelationshipLabel => LinkType switch
    {
        WorkEntryLinkType.FollowUpTo when SourceWorkEntryId == CurrentWorkEntryId => "Follow-up to",
        WorkEntryLinkType.FollowUpTo => "Followed by",
        _ => "Related"
    };
}
