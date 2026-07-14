using TechBench.Services;

namespace TechBench.Tests;

public sealed class WindowsCredentialStoreTests
{
    [Fact]
    public void RoundTripsAndDeletesProtectedCredential()
    {
        var store = new WindowsCredentialStore();
        var key = $"Tests.{Guid.NewGuid():N}";

        try
        {
            store.SetSecret(key, "temporary test secret");
            Assert.Equal("temporary test secret", store.GetSecret(key));
        }
        finally
        {
            store.SetSecret(key, string.Empty);
        }

        Assert.Equal(string.Empty, store.GetSecret(key));
    }
}
