namespace TechBench.Models;

public enum WhdAuthenticationMode
{
    Auto,
    UsernamePassword,
    ApplicationApiKey,
    TechnicianApiKey
}

public sealed class WhdConnectionSettings
{
    public string BaseUrl { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Secret { get; init; } = string.Empty;
    public WhdAuthenticationMode AuthenticationMode { get; init; } = WhdAuthenticationMode.Auto;
}
