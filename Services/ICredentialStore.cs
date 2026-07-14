namespace TechBench.Services;

public interface ICredentialStore
{
    string GetSecret(string key);
    void SetSecret(string key, string value);
}
