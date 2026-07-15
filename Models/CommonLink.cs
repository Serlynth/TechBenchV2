namespace TechBench.Models;

public sealed class CommonLink
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string? BuiltInKey { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public bool IsBuiltIn => !string.IsNullOrWhiteSpace(BuiltInKey);

    public string DisplayHost
    {
        get
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri))
            {
                return Url;
            }

            return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? uri.Host[4..]
                : uri.Host;
        }
    }

    public string DisplayInitial => string.IsNullOrWhiteSpace(Name)
        ? "?"
        : Name.Trim()[..1].ToUpperInvariant();
}
