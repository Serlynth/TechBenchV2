namespace TechBench.Models;

public sealed class WhdTechnician
{
    public string ExternalId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public bool IsActive { get; init; } = true;

    public string DisplayName => string.IsNullOrWhiteSpace(Username)
        ? Name
        : $"{Name} ({Username})";
}
