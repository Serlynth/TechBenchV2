namespace TechBench.Models;

public sealed class NoteTemplate
{
    public int Id { get; set; }
    public string ScopeType { get; set; } = "Organization";
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string TemplateText { get; set; } = string.Empty;
    public byte[]? RowVersion { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Category)
        ? Name
        : $"{Category}: {Name}";
}
