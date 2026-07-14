using System.Windows.Threading;
using TechBench.Services;

namespace TechBench.ViewModels;

public sealed class AppUpdateViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan InitialCheckDelay = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(1);

    private readonly IAppUpdateService _updateService;
    private readonly Func<DatabaseBackupResult> _createPreUpdateBackup;
    private readonly Action _prepareForRestart;
    private readonly Action _shutdownApplication;
    private readonly Func<bool> _canInstall;
    private readonly Action<string> _notifyUpdateAvailable;
    private readonly DispatcherTimer _automaticCheckTimer = new();
    private AppUpdateRelease? _availableUpdate;
    private bool _isChecking;
    private bool _isDownloading;
    private bool _isBannerDismissed;
    private int _downloadProgress;
    private string _statusText;
    private string? _completedVersion;
    private string? _lastNotifiedVersion;

    public AppUpdateViewModel(
        IAppUpdateService updateService,
        Func<DatabaseBackupResult> createPreUpdateBackup,
        Action prepareForRestart,
        Action shutdownApplication,
        Func<bool> canInstall,
        Action<string>? notifyUpdateAvailable = null)
    {
        _updateService = updateService;
        _createPreUpdateBackup = createPreUpdateBackup;
        _prepareForRestart = prepareForRestart;
        _shutdownApplication = shutdownApplication;
        _canInstall = canInstall;
        _notifyUpdateAvailable = notifyUpdateAvailable ?? (_ => { });
        _statusText = updateService.IsInstalled
            ? "TechBench checks for stable updates automatically."
            : "Install TechBench with Setup.exe to enable automatic updates.";

        CheckForUpdatesCommand = new AsyncRelayCommand(
            _ => CheckForUpdatesAsync(userInitiated: true),
            _ => !IsChecking && !IsDownloading);
        InstallUpdateCommand = new AsyncRelayCommand(
            _ => DownloadAndInstallAsync(),
            _ => CanInstallUpdate);
        DismissBannerCommand = new RelayCommand(
            _ => DismissBanner(),
            _ => !IsDownloading && IsBannerVisible);
        ShowUpdateBannerCommand = new RelayCommand(
            _ => ShowUpdateBanner(),
            _ => HasAvailableUpdate);

        _automaticCheckTimer.Interval = InitialCheckDelay;
        _automaticCheckTimer.Tick += HandleAutomaticCheckTimerTick;
    }

    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public AsyncRelayCommand InstallUpdateCommand { get; }
    public RelayCommand DismissBannerCommand { get; }
    public RelayCommand ShowUpdateBannerCommand { get; }

    public string CurrentVersionLabel => $"Version {_updateService.CurrentVersion}";
    public bool IsInstalled => _updateService.IsInstalled;
    public bool IsChecking => _isChecking;
    public bool IsDownloading => _isDownloading;
    public bool HasAvailableUpdate => _availableUpdate is not null;
    public bool IsUpdateCompletion => !string.IsNullOrWhiteSpace(_completedVersion);
    public bool CanInstallUpdate =>
        HasAvailableUpdate && !IsChecking && !IsDownloading && _canInstall();
    public bool IsBannerVisible =>
        !_isBannerDismissed && (HasAvailableUpdate || IsUpdateCompletion);
    public bool IsProgressVisible => IsDownloading;
    public bool IsUpdateActionVisible => HasAvailableUpdate && !IsUpdateCompletion;
    public int DownloadProgress => _downloadProgress;
    public string DownloadProgressLabel => $"{DownloadProgress}%";
    public string DismissButtonText => IsUpdateCompletion ? "Dismiss" : "Later";
    public string HeaderUpdateLabel => IsDownloading
        ? $"DOWNLOADING {DownloadProgress}%"
        : _availableUpdate is null
            ? string.Empty
            : $"UPDATE {_availableUpdate.Version}";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string BannerTitle => IsUpdateCompletion
        ? $"TechBench updated to {_completedVersion}"
        : _availableUpdate is null
            ? string.Empty
            : $"TechBench {_availableUpdate.Version} is available";

    public string BannerDetail => IsUpdateCompletion
        ? "The update installed successfully."
        : IsDownloading
            ? $"Downloading the update: {DownloadProgress}%"
            : "Download and install now. TechBench will save your draft, restart, and reopen automatically.";

    public void StartAutomaticChecks()
    {
        if (!IsInstalled || _automaticCheckTimer.IsEnabled)
        {
            return;
        }

        _automaticCheckTimer.Start();
    }

    public void MarkUpdateCompleted(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        _completedVersion = version.Trim();
        _isBannerDismissed = false;
        StatusText = $"Updated successfully to version {_completedVersion}.";
        RaiseDisplayProperties();
    }

    public void RefreshCommandStates()
    {
        InstallUpdateCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _automaticCheckTimer.Stop();
        _automaticCheckTimer.Tick -= HandleAutomaticCheckTimerTick;
    }

    internal async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (IsChecking || IsDownloading)
        {
            return;
        }

        if (!IsInstalled)
        {
            StatusText = "Install TechBench with Setup.exe once; automatic updates work after that.";
            return;
        }

        SetChecking(true);
        if (userInitiated)
        {
            StatusText = "Checking for updates...";
        }

        try
        {
            var update = await _updateService.CheckForUpdatesAsync();
            var previousVersion = _availableUpdate?.Version;
            _availableUpdate = update;
            _completedVersion = null;
            _isBannerDismissed = update is not null
                && !userInitiated
                && string.Equals(previousVersion, update.Version, StringComparison.OrdinalIgnoreCase)
                && _isBannerDismissed;
            StatusText = update is null
                ? $"TechBench {_updateService.CurrentVersion} is up to date."
                : $"Version {update.Version} is ready to download.";
            NotifyUpdateAvailableOnce(update);
            RaiseDisplayProperties();
        }
        catch (Exception ex)
        {
            StatusText = $"Update check failed: {ex.Message}";
        }
        finally
        {
            SetChecking(false);
        }
    }

    internal async Task DownloadAndInstallAsync()
    {
        if (!CanInstallUpdate || _availableUpdate is null)
        {
            return;
        }

        SetDownloading(true);
        SetDownloadProgress(0);
        StatusText = $"Downloading TechBench {_availableUpdate.Version}...";

        try
        {
            var progress = new Progress<int>(SetDownloadProgress);
            await _updateService.DownloadUpdateAsync(progress);

            StatusText = "Saving your draft and creating a verified database backup...";
            _prepareForRestart();
            var backup = _createPreUpdateBackup();
            if (!backup.Succeeded)
            {
                StatusText = $"Update stopped: {backup.Message}";
                return;
            }

            StatusText = "Installing the update and restarting TechBench...";
            _updateService.BeginApplyAndRestart();
            _shutdownApplication();
        }
        catch (Exception ex)
        {
            StatusText = $"Update failed: {ex.Message}";
        }
        finally
        {
            SetDownloading(false);
        }
    }

    private async void HandleAutomaticCheckTimerTick(object? sender, EventArgs e)
    {
        _automaticCheckTimer.Stop();
        await CheckForUpdatesAsync(userInitiated: false);
        _automaticCheckTimer.Interval = AutomaticCheckInterval;
        _automaticCheckTimer.Start();
    }

    private void DismissBanner()
    {
        _isBannerDismissed = true;
        RaiseDisplayProperties();
    }

    private void ShowUpdateBanner()
    {
        if (!HasAvailableUpdate)
        {
            return;
        }

        _isBannerDismissed = false;
        RaiseDisplayProperties();
    }

    private void NotifyUpdateAvailableOnce(AppUpdateRelease? update)
    {
        if (update is null
            || string.Equals(_lastNotifiedVersion, update.Version, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastNotifiedVersion = update.Version;
        _notifyUpdateAvailable(update.Version);
    }

    private void SetChecking(bool value)
    {
        if (_isChecking == value)
        {
            return;
        }

        _isChecking = value;
        OnPropertyChanged(nameof(IsChecking));
        RaiseCommandStates();
    }

    private void SetDownloading(bool value)
    {
        if (_isDownloading == value)
        {
            return;
        }

        _isDownloading = value;
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsProgressVisible));
        OnPropertyChanged(nameof(BannerDetail));
        OnPropertyChanged(nameof(HeaderUpdateLabel));
        RaiseCommandStates();
    }

    private void SetDownloadProgress(int value)
    {
        var normalized = Math.Clamp(value, 0, 100);
        if (_downloadProgress == normalized)
        {
            return;
        }

        _downloadProgress = normalized;
        OnPropertyChanged(nameof(DownloadProgress));
        OnPropertyChanged(nameof(DownloadProgressLabel));
        OnPropertyChanged(nameof(BannerDetail));
        OnPropertyChanged(nameof(HeaderUpdateLabel));
    }

    private void RaiseDisplayProperties()
    {
        OnPropertyChanged(nameof(HasAvailableUpdate));
        OnPropertyChanged(nameof(IsUpdateCompletion));
        OnPropertyChanged(nameof(CanInstallUpdate));
        OnPropertyChanged(nameof(IsBannerVisible));
        OnPropertyChanged(nameof(IsUpdateActionVisible));
        OnPropertyChanged(nameof(BannerTitle));
        OnPropertyChanged(nameof(BannerDetail));
        OnPropertyChanged(nameof(DismissButtonText));
        OnPropertyChanged(nameof(HeaderUpdateLabel));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        CheckForUpdatesCommand.RaiseCanExecuteChanged();
        InstallUpdateCommand.RaiseCanExecuteChanged();
        DismissBannerCommand.RaiseCanExecuteChanged();
        ShowUpdateBannerCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanInstallUpdate));
    }
}
