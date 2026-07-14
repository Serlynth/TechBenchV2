namespace TechBench.Models;

public sealed class CloseoutItem
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public bool HasIssue { get; init; }
    public bool IsVisible { get; init; } = true;
}
