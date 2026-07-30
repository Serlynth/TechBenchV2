namespace TechBench.Models;

/// <summary>Maps a TechBench/AD user to the WHD technician used by the service.</summary>
public sealed class WhdUserMapping
{
    public int Id { get; init; }
    public string UserSid { get; init; } = string.Empty;
    public string LoginName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? WhdTechnicianExternalId { get; set; }
    public string WhdTechnicianName { get; init; } = string.Empty;

    public string UserLabel => string.IsNullOrWhiteSpace(DisplayName)
        ? LoginName
        : string.IsNullOrWhiteSpace(LoginName)
            ? DisplayName
            : $"{DisplayName} ({LoginName})";
}
