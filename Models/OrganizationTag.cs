namespace TechBench.Models;

public sealed class OrganizationTag
{
    public int Id { get; set; }

    public string Tag { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public byte[]? RowVersion { get; set; }
}
