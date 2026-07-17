using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TechBench.Models;
using TechBench.SyncService;

namespace TechBench.Tests;

public sealed class SyncServiceCoreTests
{
    [Fact]
    public void ServiceConfigurationRequiresHttpsAndAUsername()
    {
        Assert.True(Configuration("https://whd.example.test", "sync-user").IsConfigured);
        Assert.False(Configuration("http://whd.example.test", "sync-user").IsConfigured);
        Assert.False(Configuration("not a URI", "sync-user").IsConfigured);
        Assert.False(Configuration("https://whd.example.test", "  ").IsConfigured);
    }

    [Fact]
    public void OptionsClampOperationalLimitsAndResolveCustomSecretPath()
    {
        var customPath = Path.Combine(
            Path.GetTempPath(),
            "TechBench.Tests",
            Guid.NewGuid().ToString("N"),
            "credential.bin");
        var options = new SyncServiceOptions
        {
            PollSeconds = 1,
            LeaseSeconds = 30,
            DeltaOverlapMinutes = 90,
            CommandTimeoutSeconds = 5,
            WhdRequestTimeoutSeconds = 1,
            SecretPath = customPath
        };

        Assert.Equal(TimeSpan.FromSeconds(5), options.PollInterval);
        Assert.Equal(120, options.EffectiveLeaseSeconds);
        Assert.Equal(TimeSpan.FromMinutes(60), options.DeltaOverlap);
        Assert.Equal(30, options.EffectiveCommandTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(15), options.WhdRequestTimeout);
        Assert.Equal(Path.GetFullPath(customPath), options.ResolveSecretPath());
        Assert.False(options.TrustServerCertificate);
    }

    [Fact]
    public void SecretStoreProtectsRoundTripsOverwritesAndDeletesCredential()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBench.Tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "whd.secret");
        var store = CreateSecretStore(path);
        const string firstSecret = "first-test-secret-1!";
        const string secondSecret = "second-test-secret-2!";

        try
        {
            Assert.False(store.Exists);
            store.Write(firstSecret);

            Assert.True(store.Exists);
            Assert.Equal(firstSecret, store.Read());
            Assert.False(ContainsSequence(
                File.ReadAllBytes(path),
                Encoding.UTF8.GetBytes(firstSecret)));

            store.Write(secondSecret);

            Assert.Equal(secondSecret, store.Read());
            Assert.False(File.Exists(path + ".new"));

            store.Delete();
            Assert.False(store.Exists);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void SecretStoreRejectsMissingAndEmptyCredentials()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "TechBench.Tests",
            Guid.NewGuid().ToString("N"),
            "whd.secret");
        var store = CreateSecretStore(path);

        var missing = Assert.Throws<InvalidOperationException>(() => store.Read());
        Assert.Contains("has not been configured", missing.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => store.Write("  "));
    }

    private static WhdServiceConfiguration Configuration(string baseUrl, string username) => new(
        baseUrl,
        username,
        WhdAuthenticationMode.ApplicationApiKey,
        AutoSyncEnabled: true,
        AutoSyncMinutes: 5,
        CursorUtc: null);

    private static WhdSecretStore CreateSecretStore(string path) => new(
        Options.Create(new SyncServiceOptions { SecretPath = path }));

    private static bool ContainsSequence(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return true;
        }

        for (var index = 0; index <= source.Length - value.Length; index++)
        {
            if (source[index..(index + value.Length)].SequenceEqual(value))
            {
                return true;
            }
        }

        return false;
    }
}
