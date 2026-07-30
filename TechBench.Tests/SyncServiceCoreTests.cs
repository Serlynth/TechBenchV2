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
    public void ManualFullRequestUsesSuccessfulTicketCursorInsteadOfHistoricalRescan()
    {
        var cursor = new DateTimeOffset(2026, 7, 29, 16, 26, 0, TimeSpan.Zero);
        var work = new WhdSyncWork(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Tickets",
            IsFullSync: true,
            CursorUtc: null,
            RequestId: Guid.NewGuid(),
            LeaseExpiresUtc: cursor.AddMinutes(5));
        var configuration = Configuration(
            "https://whd.example.test",
            "sync-user",
            cursor);

        Assert.Equal(cursor, WhdSyncEngine.ResolveTicketCursor(work, configuration));
    }

    [Fact]
    public void FirstTicketSyncWithoutCursorStillUsesHistoricalBootstrap()
    {
        var work = new WhdSyncWork(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Tickets",
            IsFullSync: true,
            CursorUtc: null,
            RequestId: Guid.NewGuid(),
            LeaseExpiresUtc: DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Null(WhdSyncEngine.ResolveTicketCursor(
            work,
            Configuration("https://whd.example.test", "sync-user")));
    }

    [Fact]
    public void RunningWhdStatusDoesNotPresentPreviousFailureAsCurrentResult()
    {
        var status = new WhdSyncServiceStatus
        {
            Health = "Error",
            Message = "Web Help Desk sync failed: The operation was canceled.",
            IsRunning = true,
            QueueDepth = 1
        };

        Assert.Equal("Running: Synchronization is in progress.", status.Summary);
    }

    [Fact]
    public void SageServiceConfigurationRequiresDsnAndUsername()
    {
        Assert.True(new SageSyncConfiguration("techbench", "sage-user").IsConfigured);
        Assert.False(new SageSyncConfiguration(" ", "sage-user").IsConfigured);
        Assert.False(new SageSyncConfiguration("techbench", " ").IsConfigured);
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
            SageOdbcTimeoutSeconds = 1,
            FinalizationTimeoutSeconds = 1,
            SecretPath = customPath,
            SageSecretPath = customPath + ".sage",
            SageOdbcWorkerPath = customPath + ".exe"
        };

        Assert.Equal(TimeSpan.FromSeconds(5), options.PollInterval);
        Assert.Equal(120, options.EffectiveLeaseSeconds);
        Assert.Equal(TimeSpan.FromMinutes(60), options.DeltaOverlap);
        Assert.Equal(30, options.EffectiveCommandTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(300), options.WhdRequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.SageOdbcTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), options.FinalizationTimeout);
        Assert.Equal(Path.GetFullPath(customPath), options.ResolveSecretPath());
        Assert.Equal(Path.GetFullPath(customPath + ".sage"), options.ResolveSageSecretPath());
        Assert.Equal(Path.GetFullPath(customPath + ".exe"), options.ResolveSageOdbcWorkerPath());
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
        const string legacyTemporarySentinel = "do-not-overwrite";

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path + ".new", legacyTemporarySentinel);
            Assert.False(store.Exists);
            store.Write(firstSecret);

            Assert.True(store.Exists);
            Assert.Equal(firstSecret, store.Read());
            Assert.False(ContainsSequence(
                File.ReadAllBytes(path),
                Encoding.UTF8.GetBytes(firstSecret)));

            store.Write(secondSecret);

            Assert.Equal(secondSecret, store.Read());
            Assert.Equal(legacyTemporarySentinel, File.ReadAllText(path + ".new"));
            Assert.Empty(Directory.GetFiles(directory, ".whd.secret.*.tmp"));

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

    [Fact]
    public void SageSecretStoreProtectsRoundTripsAndUsesDistinctEntropy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBench.Tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "sage.secret");
        var options = Options.Create(new SyncServiceOptions
        {
            SecretPath = path,
            SageSecretPath = path
        });
        var sageStore = new SageSecretStore(options);
        const string secret = "sage-test-secret-1!";

        try
        {
            sageStore.Write(secret);
            Assert.Equal(secret, sageStore.Read());
            Assert.False(ContainsSequence(
                File.ReadAllBytes(path),
                Encoding.UTF8.GetBytes(secret)));
            Assert.Empty(Directory.GetFiles(directory, ".sage.secret.*.tmp"));

            var whdStore = new WhdSecretStore(options);
            var error = Assert.Throws<InvalidOperationException>(() => whdStore.Read());
            Assert.Contains("decrypt", error.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task SageProcessClientRejectsMissingX86WorkerBeforeSendingCredentials()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            "TechBench.Tests",
            Guid.NewGuid().ToString("N"),
            "TechBench.SageOdbcWorker.exe");
        var client = new SageOdbcWorkerProcessClient(Options.Create(new SyncServiceOptions
        {
            SageOdbcWorkerPath = missingPath
        }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ReadCustomersAsync("techbench", "sage-user", "secret", CancellationToken.None));

        Assert.Contains("32-bit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    private static WhdServiceConfiguration Configuration(
        string baseUrl,
        string username,
        DateTimeOffset? cursorUtc = null) => new(
        baseUrl,
        username,
        WhdAuthenticationMode.ApplicationApiKey,
        AutoSyncEnabled: true,
        AutoSyncMinutes: 5,
        CursorUtc: cursorUtc);

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
