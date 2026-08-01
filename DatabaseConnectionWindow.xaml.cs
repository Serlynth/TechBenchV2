using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Data.SqlClient;
using TechBench.Data;
using TechBench.Models;
using TechBench.Services;

namespace TechBench;

public partial class DatabaseConnectionWindow : Window
{
    private readonly IAppUpdateService _updateService;
    private readonly LocalPreferences _localPreferences;
    private readonly CancellationTokenSource _updateCancellation = new();
    private AppUpdateRelease? _availableUpdate;
    private bool _isCheckingOrInstallingUpdate;
    private bool _schemaVersionMismatch;
    private readonly bool _forceForeground;

    public DatabaseConnectionWindow(
        SqlServerConnectionOptions? initialOptions,
        string? initialStatus = null,
        IAppUpdateService? updateService = null,
        bool forceForeground = false)
    {
        _localPreferences = LoadLocalPreferences();
        var savedModule = ModuleBranding.Resolve(
            _localPreferences.LastBenchModule);
        ThemeService.Apply(
            _localPreferences.Theme.Equals(
                "Light",
                StringComparison.OrdinalIgnoreCase)
                ? AppTheme.Light
                : AppTheme.Dark,
            savedModule);
        InitializeComponent();
        _forceForeground = forceForeground;
        Title = $"Connect to {savedModule}";
        WelcomeTitleTextBlock.Text = $"Welcome to {savedModule}";
        ModuleLogoImage.Source = new BitmapImage(
            new Uri(ModuleBranding.LogoSource(savedModule), UriKind.Relative));
        _updateService = updateService
            ?? new V2AppUpdateService(_localPreferences.UpdateChannel);
        var selectedChannel =
            (_updateService as IAppUpdateChannelService)?
                .SelectedReleaseChannel
            ?? V2AppUpdateService.ResolveReleaseChannel(
                _localPreferences.UpdateChannel,
                V2AppUpdateService.CompiledReleaseChannel);
        UpdateChannelCheckBox.IsChecked = selectedChannel.Equals(
            V2AppUpdateService.ClientInfoBetaReleaseChannel,
            StringComparison.OrdinalIgnoreCase);
        UpdateChannelCheckBox.IsEnabled =
            _updateService is IAppUpdateChannelService;
        UpdateChannelCheckBox.Click += UpdateChannelCheckBox_Click;
        RefreshUpdateChannelDescription();
        ServerTextBox.Text =
            initialOptions?.Server ?? SqlServerConnectionOptions.DefaultServerName;
        DatabaseTextBox.Text =
            initialOptions?.Database ?? SqlServerConnectionOptions.DefaultDatabaseName;
        WindowsIdentityTextBlock.Text =
            WindowsIdentity.GetCurrent().Name ?? Environment.UserName;
        StatusTextBlock.Text = initialStatus ?? string.Empty;
        _schemaVersionMismatch = IsSchemaVersionMismatch(initialStatus);
        UpdateConnectButtonState();
        Loaded += DatabaseConnectionWindow_Loaded;
        Closed += DatabaseConnectionWindow_Closed;
    }

    public SqlServerConnectionFactory? ConnectionFactory { get; private set; }

    public CurrentUserContext? CurrentUser { get; private set; }

    public CurrentUserContext? AuthenticatedUser { get; private set; }

    internal static bool IsSchemaVersionMismatch(string? status)
    {
        return !string.IsNullOrWhiteSpace(status)
            && status.Contains(
                "database schema is version",
                StringComparison.OrdinalIgnoreCase)
            && status.Contains(
                "client requires version",
                StringComparison.OrdinalIgnoreCase);
    }

    private async void DatabaseConnectionWindow_Loaded(object sender, RoutedEventArgs e)
    {
        FitToWorkingArea();
        if (_forceForeground)
        {
            WindowActivationService.BringToForeground(this);
        }

        try
        {
            await _updateService.CleanupDownloadedUpdatesAsync(
                _updateCancellation.Token);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or OperationCanceledException)
        {
            // Installer-cache cleanup is best effort and must not prevent login
            // or schema-mismatch update recovery.
        }

        if (_schemaVersionMismatch)
        {
            UpdateButton.Focus();
            await CheckForUpdatesAsync(automaticRecovery: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(ServerTextBox.Text))
        {
            ServerTextBox.Focus();
        }
        else if (string.IsNullOrWhiteSpace(DatabaseTextBox.Text))
        {
            DatabaseTextBox.Focus();
        }
        else
        {
            ConnectButton.Focus();
        }
    }

    private void DatabaseConnectionWindow_Closed(object? sender, EventArgs e)
    {
        _updateCancellation.Cancel();
        _updateCancellation.Dispose();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        StatusTextBlock.Text =
            $"Connecting as {WindowsIdentityTextBlock.Text}...";
        try
        {
            var options = new SqlServerConnectionOptions(
                ServerTextBox.Text,
                DatabaseTextBox.Text,
                TrustServerCertificate: true)
                .NormalizeAndValidate();
            var connectionFactory = new SqlServerConnectionFactory(options);
            var authenticatedUser = await connectionFactory.GetCurrentUserContextAsync();

#if TECHBENCH_CLIENT_INFO_BETA
            var authPointRequirement =
                await connectionFactory.GetAuthPointLoginRequirementAsync();
            if (authPointRequirement.IsRequired)
            {
                StatusTextBlock.Text =
                    $"Windows identity verified for {authenticatedUser.DisplayName}. Waiting for AuthPoint...";
                var authPointLogin = new AuthPointLoginWindow(
                    connectionFactory,
                    authPointRequirement)
                {
                    Owner = this
                };
                if (authPointLogin.ShowDialog() != true)
                {
                    StatusTextBlock.Text =
                        "TechBench sign-in was cancelled before AuthPoint approval.";
                    return;
                }
            }
#endif

            SqlServerConnectionConfig.Save(options);

            ConnectionFactory = connectionFactory;
            CurrentUser = authenticatedUser;
            AuthenticatedUser = authenticatedUser;
            DialogResult = true;
        }
        catch (SqlException ex)
        {
            StatusTextBlock.Text = ResolveSqlError(ex);
        }
        catch (TaskCanceledException)
        {
            StatusTextBlock.Text =
                "The SQL Server connection was cancelled before it completed.";
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            StatusTextBlock.Text = ex.Message;
            _schemaVersionMismatch = IsSchemaVersionMismatch(ex.Message);
            if (_schemaVersionMismatch)
            {
                await CheckForUpdatesAsync(automaticRecovery: true);
            }
        }
        finally
        {
            UpdateConnectButtonState();
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
        {
            await CheckForUpdatesAsync(automaticRecovery: false);
            return;
        }

        await DownloadAndInstallUpdateAsync();
    }

    private void UpdateChannelCheckBox_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_updateService is not IAppUpdateChannelService channelService)
        {
            return;
        }

        var selectedChannel = UpdateChannelCheckBox.IsChecked == true
            ? V2AppUpdateService.ClientInfoBetaReleaseChannel
            : V2AppUpdateService.StableReleaseChannel;
        try
        {
            channelService.SelectReleaseChannel(selectedChannel);
            _localPreferences.UpdateChannel = selectedChannel;
            LocalPreferenceStore.Save(_localPreferences);
            _availableUpdate = null;
            UpdateButton.Content = "Check for updates";
            StatusTextBlock.Text =
                selectedChannel == V2AppUpdateService.ClientInfoBetaReleaseChannel
                    ? "Client Info Beta selected. Select Check for updates to look for beta builds."
                    : "Stable selected. Select Check for updates to return to stable releases.";
            RefreshUpdateChannelDescription();
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            UpdateChannelCheckBox.IsChecked =
                channelService.SelectedReleaseChannel.Equals(
                    V2AppUpdateService.ClientInfoBetaReleaseChannel,
                    StringComparison.OrdinalIgnoreCase);
            StatusTextBlock.Text =
                $"Could not change the update channel: {ex.Message}";
        }
    }

    private async Task CheckForUpdatesAsync(bool automaticRecovery)
    {
        if (_isCheckingOrInstallingUpdate)
        {
            return;
        }

        if (!_updateService.IsInstalled)
        {
            StatusTextBlock.Text =
                "This portable copy cannot update itself. Install the current TechBenchV2Setup.exe once to enable automatic updates.";
            return;
        }

        SetUpdateBusy(true);
        StatusTextBlock.Text = automaticRecovery
            ? "The database is newer than this client. Checking for a compatible TechBench update..."
            : "Checking for TechBench updates...";

        try
        {
            _availableUpdate = await _updateService.CheckForUpdatesAsync(
                _updateCancellation.Token);
            if (_availableUpdate is null)
            {
                UpdateButton.Content = "Check for updates";
                StatusTextBlock.Text = _schemaVersionMismatch
                    ? $"No compatible update is published yet. This client is version {_updateService.CurrentVersion}; install the matching client before connecting."
                    : $"TechBench {_updateService.CurrentVersion} is up to date.";
                return;
            }

            UpdateButton.Content = $"Install {_availableUpdate.Version}";
            StatusTextBlock.Text = _schemaVersionMismatch
                ? $"TechBench {_availableUpdate.Version} is available and matches the newer server. Select Install to update and restart."
                : $"TechBench {_availableUpdate.Version} is available. Select Install to update and restart.";
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested)
        {
            // The connection window is closing.
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Update check failed: {ex.Message}";
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private async Task DownloadAndInstallUpdateAsync()
    {
        if (_availableUpdate is null || _isCheckingOrInstallingUpdate)
        {
            return;
        }

        var version = _availableUpdate.Version;
        SetUpdateBusy(true);
        try
        {
            var progress = new Progress<int>(value =>
            {
                StatusTextBlock.Text =
                    $"Downloading TechBench {version}: {Math.Clamp(value, 0, 100)}%";
            });
            await _updateService.DownloadUpdateAsync(
                progress,
                _updateCancellation.Token);

            StatusTextBlock.Text =
                $"Installing TechBench {version} and restarting...";
            _updateService.BeginApplyAndRestart();
            System.Windows.Application.Current.Shutdown();
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested)
        {
            // The connection window is closing.
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Update failed: {ex.Message}";
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private void SetUpdateBusy(bool value)
    {
        _isCheckingOrInstallingUpdate = value;
        UpdateButton.IsEnabled = !value;
        UpdateChannelCheckBox.IsEnabled =
            !value && _updateService is IAppUpdateChannelService;
        UpdateConnectButtonState();
    }

    private void UpdateConnectButtonState()
    {
        ConnectButton.IsEnabled =
            !_isCheckingOrInstallingUpdate && !_schemaVersionMismatch;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void FitToWorkingArea()
    {
        const double edgeMargin = 16;
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(420, workArea.Width - edgeMargin);
        var availableHeight = Math.Max(360, workArea.Height - edgeMargin);

        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);

        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
    }

    private static string ResolveSqlError(SqlException exception)
    {
        return exception.Number switch
        {
            -2 => "SQL Server did not respond before the connection timed out.",
            53 => "The SQL Server or instance could not be found.",
            229 => "Your Windows account does not have permission to use TechBench.",
            4060 => BuildDatabaseOpenFailureMessage(GetCurrentWindowsAccess()),
            18456 => "SQL Server did not accept your Windows domain identity.",
            51913 => "That non-Admin user must have opened TechBench V2 within the past hour and still have TechBench access.",
            _ => $"Could not connect to SQL Server: {exception.Message}"
        };
    }

    private void RefreshUpdateChannelDescription()
    {
        UpdateChannelDescriptionTextBlock.Text =
            UpdateChannelCheckBox.IsChecked == true
                ? "Beta builds include features still being tested. Turn this off at any time to check for Stable."
                : "Stable releases are selected. Turn this on when you want to install Client Info Beta.";
    }

    private static LocalPreferences LoadLocalPreferences()
    {
        try
        {
            return LocalPreferenceStore.LoadOrCreate();
        }
        catch (InvalidOperationException)
        {
            return new LocalPreferences();
        }
        catch (IOException)
        {
            return new LocalPreferences();
        }
        catch (UnauthorizedAccessException)
        {
            return new LocalPreferences();
        }
    }

    private static WindowsAccessDiagnostic GetCurrentWindowsAccess()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return new WindowsAccessDiagnostic(
                identity.Name ?? Environment.UserName,
                principal.IsInRole(@"CSRI\TechBench_Users"),
                principal.IsInRole(@"CSRI\TechBench_Admins"));
        }
        catch
        {
            return new WindowsAccessDiagnostic(
                WindowsIdentity.GetCurrent().Name ?? Environment.UserName,
                IsUser: false,
                IsAdmin: false,
                CouldInspectGroups: false);
        }
    }

    internal static string BuildDatabaseOpenFailureMessage(
        WindowsAccessDiagnostic access)
    {
        if (!access.CouldInspectGroups)
        {
            return
                $"The TechBench database could not be opened for {access.IdentityName}. "
                + "TechBench could not inspect this Windows sign-in's domain groups. "
                + @"Run `whoami /groups` and verify that CSRI\TechBench_Admins or "
                + @"CSRI\TechBench_Users is present.";
        }

        if (!access.IsUser && !access.IsAdmin)
        {
            return
                $"The TechBench database could not be opened for {access.IdentityName}. "
                + @"This Windows sign-in does not currently contain CSRI\TechBench_Admins "
                + @"or CSRI\TechBench_Users. If the account was recently added, fully sign "
                + "out of Windows and sign back in. Closing TechBench, locking Windows, or "
                + "restarting only the app will not refresh domain-group membership.";
        }

        var recognizedGroup = access.IsAdmin
            ? @"CSRI\TechBench_Admins"
            : @"CSRI\TechBench_Users";
        return
            $"The TechBench database could not be opened for {access.IdentityName}, even "
            + $"though this Windows sign-in contains {recognizedGroup}. Have the DBA rerun "
            + "the current TechBench SQL installer to repair the group login and TechBench "
            + "database user, then try again.";
    }

    internal sealed record WindowsAccessDiagnostic(
        string IdentityName,
        bool IsUser,
        bool IsAdmin,
        bool CouldInspectGroups = true);
}
