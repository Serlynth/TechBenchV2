namespace TechBench.Models;

public sealed class NoteTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string TemplateText { get; set; } = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Category)
        ? Name
        : $"{Category}: {Name}";
}
