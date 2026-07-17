using TechBench.Services;
using TechBench.ViewModels;

namespace TechBench.Tests;

public sealed class AppUpdateViewModelTests
{
    [Fact]
    public async Task CheckForUpdates_ShowsAvailableUpdate()
    {
        var service = new FakeAppUpdateService
        {
            AvailableUpdate = new AppUpdateRelease("1.1.0", "Release notes")
        };
        using var viewModel = CreateViewModel(service);

        await viewModel.CheckForUpdatesAsync(userInitiated: true);

        Assert.True(viewModel.HasAvailableUpdate);
        Assert.True(viewModel.IsBannerVisible);
        Assert.Equal("TechBench 1.1.0 is available", viewModel.BannerTitle);
        Assert.Equal("Later", viewModel.DismissButtonText);
        Assert.Equal("UPDATE 1.1.0", viewModel.HeaderUpdateLabel);
        Assert.Equal("Version 1.1.0 is ready to download.", viewModel.StatusText);
    }

    [Fact]
    public async Task HeaderUpdateAlert_ReopensDismissedBanner()
    {
        var service = new FakeAppUpdateService
        {
            AvailableUpdate = new AppUpdateRelease("1.1.0", "Release notes")
        };
        using var viewModel = CreateViewModel(service);

        await viewModel.CheckForUpdatesAsync(userInitiated: false);
        viewModel.DismissBannerCommand.Execute(null);

        Assert.False(viewModel.IsBannerVisible);
        Assert.True(viewModel.HasAvailableUpdate);

        viewModel.ShowUpdateBannerCommand.Execute(null);

        Assert.True(viewModel.IsBannerVisible);
    }

    [Fact]
    public async Task HourlyCheck_DoesNotReopenDismissedBannerForSameVersion()
    {
        var service = new FakeAppUpdateService
        {
            AvailableUpdate = new AppUpdateRelease("1.1.0", "Release notes")
        };
        using var viewModel = CreateViewModel(service);

        await viewModel.CheckForUpdatesAsync(userInitiated: false);
        viewModel.DismissBannerCommand.Execute(null);
        await viewModel.CheckForUpdatesAsync(userInitiated: false);

        Assert.False(viewModel.IsBannerVisible);
        Assert.Equal(TimeSpan.FromHours(1), AppUpdateViewModel.AutomaticCheckInterval);
    }

    [Fact]
    public async Task CheckForUpdates_NotifiesOnlyOncePerVersion()
    {
        var notifications = new List<string>();
        var service = new FakeAppUpdateService
        {
            AvailableUpdate = new AppUpdateRelease("1.1.0", "Release notes")
        };
        using var viewModel = CreateViewModel(
            service,
            notifyUpdateAvailable: notifications.Add);

        await viewModel.CheckForUpdatesAsync(userInitiated: false);
        await viewModel.CheckForUpdatesAsync(userInitiated: true);

        Assert.Equal(["1.1.0"], notifications);
    }

    [Fact]
    public async Task DownloadAndInstall_PreparesAndRequestsRestart()
    {
        var prepared = false;
        var shutDown = false;
        var service = new FakeAppUpdateService
        {
            AvailableUpdate = new AppUpdateRelease("1.1.0", string.Empty)
        };
        using var viewModel = CreateViewModel(
            service,
            prepareForRestart: () => prepared = true,
            shutdownApplication: () => shutDown = true);
        await viewModel.CheckForUpdatesAsync(userInitiated: true);

        await viewModel.DownloadAndInstallAsync();

        Assert.True(prepared);
        Assert.True(service.DownloadCalled);
        Assert.True(service.ApplyCalled);
        Assert.True(shutDown);
    }

    [Fact]
    public async Task CheckForUpdates_ExplainsLooseExecutableLimitation()
    {
        var service = new FakeAppUpdateService { IsInstalled = false };
        using var viewModel = CreateViewModel(service);

        await viewModel.CheckForUpdatesAsync(userInitiated: true);

        Assert.False(service.CheckCalled);
        Assert.Contains("Setup.exe", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    private static AppUpdateViewModel CreateViewModel(
        FakeAppUpdateService service,
        Action? prepareForRestart = null,
        Action? shutdownApplication = null,
        Action<string>? notifyUpdateAvailable = null)
    {
        return new AppUpdateViewModel(
            service,
            prepareForRestart ?? (() => { }),
            shutdownApplication ?? (() => { }),
            () => true,
            notifyUpdateAvailable);
    }

    private sealed class FakeAppUpdateService : IAppUpdateService
    {
        public bool IsInstalled { get; set; } = true;
        public string CurrentVersion { get; set; } = "1.0.0";
        public AppUpdateRelease? AvailableUpdate { get; set; }
        public bool CheckCalled { get; private set; }
        public bool DownloadCalled { get; private set; }
        public bool ApplyCalled { get; private set; }

        public Task<AppUpdateRelease?> CheckForUpdatesAsync(
            CancellationToken cancellationToken = default)
        {
            CheckCalled = true;
            return Task.FromResult(AvailableUpdate);
        }

        public Task DownloadUpdateAsync(
            IProgress<int> progress,
            CancellationToken cancellationToken = default)
        {
            DownloadCalled = true;
            progress.Report(100);
            return Task.CompletedTask;
        }

        public void BeginApplyAndRestart()
        {
            ApplyCalled = true;
        }
    }
}
