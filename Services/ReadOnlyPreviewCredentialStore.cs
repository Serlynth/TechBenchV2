namespace TechBench.Services;

/// <summary>
/// Prevents an Admin preview from reading or persisting personal WHD/Sage
/// secrets from the authenticated Windows user's Credential Manager profile.
/// SQL-backed legacy secret keys are separately filtered by the repository.
/// </summary>
public sealed class ReadOnlyPreviewCredentialStore : ICredentialStore
{
    public static ReadOnlyPreviewCredentialStore Instance { get; } = new();

    private ReadOnlyPreviewCredentialStore()
    {
    }

    public string GetSecret(string key) => string.Empty;

    public void SetSecret(string key, string value)
    {
        // A preview is intentionally incapable of changing local credentials.
    }
}
