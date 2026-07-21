using System.Diagnostics;
using System.Drawing;

namespace TechBench.ServerManager;

internal sealed class ServerManagerForm : Form
{
    private readonly AppPaths _paths;
    private readonly WindowsServiceManager _service;
    private readonly SqlAdminRepository _repository;
    private readonly ActiveDirectoryUserProvider _directoryUsers = new();
    private readonly ReleaseUpdater _updater;
    private readonly ProtectedSecretStore _whdSecret;
    private readonly ProtectedSecretStore _sageSecret;
    private SynchronizationConfiguration? _configuration;
    private ReleasePackage? _availableUpdate;
    private bool _allowExit;
    private bool _operationInProgress;

    private readonly Label _serviceStatus = ValueLabel();
    private readonly Label _serviceVersion = ValueLabel();
    private readonly Label _serviceAccount = ValueLabel();
    private readonly TextBox _accountBox = Field();
    private readonly TextBox _servicePassword = PasswordField(show: true);
    private readonly Button _startButton = Button("Start");
    private readonly Button _stopButton = Button("Stop");
    private readonly Button _restartButton = Button("Restart");
    private readonly Label _whdSecretStatus = ValueLabel();
    private readonly Label _sageSecretStatus = ValueLabel();
    private readonly TextBox _whdSecretBox = PasswordField();
    private readonly TextBox _sageSecretBox = PasswordField();
    private readonly Label _updateStatus = ValueLabel();
    private readonly Button _installUpdateButton = Button("Download && Install");
    private readonly RichTextBox _log = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, DetectUrls = false };
    private readonly TextBox _sqlServer = Field();
    private readonly TextBox _sqlDatabase = Field();
    private readonly CheckBox _trustServerCertificate = new() { Text = "Trust this SQL Server certificate", AutoSize = true };

    private readonly TextBox _whdBaseUrl = Field();
    private readonly ComboBox _whdMode = DropDown();
    private readonly TextBox _whdUsername = Field();
    private readonly CheckBox _whdAuto = new() { Text = "Automatically synchronize organization tickets", AutoSize = true };
    private readonly NumericUpDown _whdMinutes = new() { Minimum = 1, Maximum = 120, Value = 5, Width = 80 };
    private readonly Label _whdSyncStatus = StatusLabel();
    private readonly DataGridView _mappingGrid = new()
    {
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoGenerateColumns = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.Fixed3D,
        Dock = DockStyle.Fill,
        EditMode = DataGridViewEditMode.EditOnEnter,
        MinimumSize = new Size(0, 300),
        MultiSelect = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    private readonly Label _mappingSummary = ValueLabel();

    private readonly TextBox _sageDsn = Field();
    private readonly TextBox _sageUsername = Field();
    private readonly Label _sageSyncStatus = StatusLabel();
    private readonly Button _confirmSageButton = Button("Confirm large-removal sync");

    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _trayStart = new("Start service");
    private readonly ToolStripMenuItem _trayStop = new("Stop service");

    public ServerManagerForm(AppPaths paths)
    {
        _paths = paths;
        _service = new(paths);
        _repository = new(paths);
        _updater = new(paths);
        _whdSecret = ProtectedSecretStore.Whd(paths);
        _sageSecret = ProtectedSecretStore.Sage(paths);

        Text = "TechBench Server Manager";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1180, 760);
        Size = new Size(1500, 900);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        Controls.Add(BuildLayout());
        _trayIcon = BuildTrayIcon();
        WireEvents();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        AddLog("Native compiled Server Manager is ready.");
        LoadLocalSqlConfiguration();
        await RefreshEverythingAsync(showErrors: false);
        TryRepairShortcut();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            ClearSecretFields();
            Hide();
            _trayIcon.Visible = true;
            _trayIcon.ShowBalloonTip(1800, "TechBench Server Manager", "Still running in the notification area.", ToolTipIcon.Info);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            ClearSecretFields();
            Hide();
            _trayIcon.Visible = true;
            AddLog("Server Manager moved to the notification area.");
            return;
        }
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnFormClosing(e);
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        root.Controls.Add(new Label
        {
            Text = "TechBench Server Manager",
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(4, 0, 0, 12)
        }, 0, 0);

        root.Controls.Add(BuildManagerTabs(), 0, 1);
        var activity = new GroupBox
        {
            Text = "Activity (newest first)",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Margin = new Padding(0, 8, 0, 0)
        };
        activity.Controls.Add(_log);
        root.Controls.Add(activity, 0, 2);
        return root;
    }

    private TabControl BuildManagerTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildServiceTab());
        tabs.TabPages.Add(BuildSqlTab());
        tabs.TabPages.Add(BuildWhdTab());
        tabs.TabPages.Add(BuildSageTab());
        tabs.TabPages.Add(BuildUpdatesTab());
        return tabs;
    }

    private TabPage BuildServiceTab()
    {
        var serviceGroup = Group("Service", 190);
        var serviceTable = Grid(4, 4);
        serviceTable.Controls.Add(Label("Status"), 0, 0); serviceTable.Controls.Add(_serviceStatus, 1, 0);
        serviceTable.Controls.Add(Label("Runs as"), 2, 0); serviceTable.Controls.Add(_serviceAccount, 3, 0);
        serviceTable.Controls.Add(Label("Version"), 0, 1); serviceTable.Controls.Add(_serviceVersion, 1, 1);
        var refresh = Button("Refresh"); refresh.Click += async (_, _) => await RunAsync("Refreshing service status...", RefreshServiceAsync);
        _startButton.Click += async (_, _) => await ServiceActionAsync("Starting service...", _service.Start);
        _stopButton.Click += async (_, _) => await ServiceActionAsync("Stopping service...", _service.Stop);
        _restartButton.Click += async (_, _) => await ServiceActionAsync("Restarting service...", _service.Restart);
        var buttons = ButtonRow(refresh, _startButton, _stopButton, _restartButton);
        serviceTable.Controls.Add(buttons, 0, 3); serviceTable.SetColumnSpan(buttons, 4);
        serviceGroup.Controls.Add(serviceTable);

        var identityGroup = Group("Windows service identity", 180);
        var identity = Grid(3, 3);
        identity.Controls.Add(Label("Domain account"), 0, 0); identity.Controls.Add(_accountBox, 1, 0); identity.SetColumnSpan(_accountBox, 2);
        identity.Controls.Add(Label("Password"), 0, 1); identity.Controls.Add(_servicePassword, 1, 1);
        var showServicePassword = new CheckBox { Text = "Show", Checked = true, AutoSize = true };
        showServicePassword.CheckedChanged += (_, _) => _servicePassword.UseSystemPasswordChar = !showServicePassword.Checked;
        identity.Controls.Add(showServicePassword, 2, 1);
        var applyIdentity = Button("Apply account / password");
        applyIdentity.Click += async (_, _) => await RunAsync("Applying the Windows service identity...", async () =>
        {
            await Task.Run(() => _service.ChangeIdentity(_accountBox.Text, _servicePassword.Text));
            _servicePassword.Clear(); await RefreshServiceAsync(); AddLog("Windows service identity updated.");
        });
        identity.Controls.Add(applyIdentity, 1, 2); identity.SetColumnSpan(applyIdentity, 2);
        identityGroup.Controls.Add(identity);

        return BuildStackedTab("Service", serviceGroup, identityGroup);
    }

    private TabPage BuildSqlTab()
    {
        var sqlGroup = Group("SQL Server connection", 185);
        var sql = Grid(3, 4);
        sql.Controls.Add(Label("Server"), 0, 0); sql.Controls.Add(_sqlServer, 1, 0); sql.SetColumnSpan(_sqlServer, 2);
        sql.Controls.Add(Label("Database"), 0, 1); sql.Controls.Add(_sqlDatabase, 1, 1); sql.SetColumnSpan(_sqlDatabase, 2);
        sql.Controls.Add(_trustServerCertificate, 1, 2); sql.SetColumnSpan(_trustServerCertificate, 2);
        var saveSql = Button("Save && Test");
        saveSql.Click += async (_, _) => await SaveSqlConfigurationAsync();
        sql.Controls.Add(saveSql, 1, 3);
        sqlGroup.Controls.Add(sql);
        return BuildStackedTab("SQL Server", sqlGroup);
    }

    private TabPage BuildUpdatesTab()
    {
        var updateGroup = Group("Service and Manager updates", 130);
        var update = Grid(3, 2);
        var check = Button("Check for updates");
        check.Click += async (_, _) => await CheckForUpdatesAsync();
        _installUpdateButton.Enabled = false;
        _installUpdateButton.Click += async (_, _) => await InstallUpdateAsync();
        update.Controls.Add(check, 0, 0); update.Controls.Add(_installUpdateButton, 1, 0); update.Controls.Add(_updateStatus, 2, 0);
        var note = new Label
        {
            Text = "Updates are verified, installed by this EXE, and rolled back if the service cannot restart.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            MaximumSize = new Size(900, 0)
        };
        update.Controls.Add(note, 0, 1); update.SetColumnSpan(note, 3);
        updateGroup.Controls.Add(update);
        return BuildStackedTab("Updates", updateGroup);
    }

    private GroupBox BuildCredentialGroup(
        string title,
        string label,
        TextBox box,
        Label status,
        ProtectedSecretStore store,
        string name)
    {
        var group = Group(title, 120);
        var layout = Grid(4, 2);
        AddSecretRow(layout, 0, label, box, status, store, name);
        group.Controls.Add(layout);
        return group;
    }

    private static TabPage BuildStackedTab(string title, params Control[] sections)
    {
        var page = new TabPage(title) { Padding = new Padding(16), AutoScroll = true };
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(0, 0, 10, 0)
        };
        panel.SizeChanged += (_, _) => ResizeFlowChildren(panel);
        panel.Controls.AddRange(sections);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildWhdTab()
    {
        _whdMode.Items.AddRange(["Auto", "UsernamePassword", "ApplicationApiKey", "TechnicianApiKey"]);
        var page = new TabPage("Web Help Desk") { Padding = new Padding(8) };
        var sections = new TabControl { Dock = DockStyle.Fill };
        sections.TabPages.Add(BuildWhdConnectionTab());
        sections.TabPages.Add(BuildWhdMappingsTab());
        page.Controls.Add(sections);
        return page;
    }

    private TabPage BuildWhdConnectionTab()
    {
        var credential = BuildCredentialGroup(
            "Protected WHD credential",
            "API key, token, or password",
            _whdSecretBox,
            _whdSecretStatus,
            _whdSecret,
            "WHD");
        var settings = Group("Connection and synchronization", 335);
        var layout = Grid(2, 7);
        layout.Controls.Add(Label("Base URL"), 0, 0); layout.Controls.Add(_whdBaseUrl, 1, 0);
        layout.Controls.Add(Label("Authentication mode"), 0, 1); layout.Controls.Add(_whdMode, 1, 1);
        layout.Controls.Add(Label("Organization-wide WHD username"), 0, 2); layout.Controls.Add(_whdUsername, 1, 2);
        layout.Controls.Add(_whdAuto, 1, 3);
        var interval = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        interval.Controls.Add(_whdMinutes); interval.Controls.Add(new Label { Text = "minutes (1-120)", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        layout.Controls.Add(Label("Every"), 0, 4); layout.Controls.Add(interval, 1, 4);
        var save = Button("Save settings + mappings"); save.Click += async (_, _) => await SaveWhdAsync(false);
        var sync = Button("Sync now"); sync.Click += async (_, _) => await SaveWhdAsync(true);
        var refresh = Button("Refresh"); refresh.Click += async (_, _) => await RefreshConfigurationAsync(true);
        layout.Controls.Add(ButtonRow(save, sync, refresh), 1, 5);
        layout.Controls.Add(_whdSyncStatus, 0, 6); layout.SetColumnSpan(_whdSyncStatus, 2);
        settings.Controls.Add(layout);
        return BuildStackedTab("Connection & Sync", credential, settings);
    }

    private TabPage BuildWhdMappingsTab()
    {
        var page = new TabPage("User Mappings") { Padding = new Padding(16) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var mappingHeader = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Margin = new Padding(3, 18, 3, 6),
            WrapContents = false
        };
        mappingHeader.Controls.Add(new Label { Text = "AD users to WHD technicians", Font = new Font(Font, FontStyle.Bold), AutoSize = true });
        mappingHeader.Controls.Add(new Label
        {
            Text = "Members of CSRI\\TechBench_Users and CSRI\\TechBench_Admins are listed once. Only active WHD technicians are available.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            MaximumSize = new Size(760, 0)
        });
        layout.Controls.Add(mappingHeader, 0, 0);
        ConfigureMappingGrid();
        layout.Controls.Add(_mappingGrid, 0, 1);
        var map = Button("Save all mappings"); map.Click += async (_, _) => await SaveMappingsAsync();
        var mappingActions = ButtonRow(map, _mappingSummary);
        layout.Controls.Add(mappingActions, 0, 2);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildSageTab()
    {
        var credential = BuildCredentialGroup(
            "Protected Sage credential",
            "ODBC password",
            _sageSecretBox,
            _sageSecretStatus,
            _sageSecret,
            "Sage");
        var settings = Group("Customer synchronization", 285);
        var layout = Grid(2, 6);
        layout.Controls.Add(Label("Server 32-bit System DSN"), 0, 0); layout.Controls.Add(_sageDsn, 1, 0);
        layout.Controls.Add(Label("Sage ODBC username"), 0, 1); layout.Controls.Add(_sageUsername, 1, 1);
        var save = Button("Save settings"); save.Click += async (_, _) => await SaveSageAsync(false, false);
        var sync = Button("Sync customers now"); sync.Click += async (_, _) => await SaveSageAsync(true, false);
        var refresh = Button("Refresh"); refresh.Click += async (_, _) => await RefreshConfigurationAsync(true);
        _confirmSageButton.Visible = false;
        _confirmSageButton.Click += async (_, _) => await SaveSageAsync(true, true);
        layout.Controls.Add(ButtonRow(save, sync, refresh, _confirmSageButton), 1, 2);
        layout.Controls.Add(_sageSyncStatus, 0, 3); layout.SetColumnSpan(_sageSyncStatus, 2);
        var note = new Label
        {
            Text = "Sage synchronization is server-owned and manual only. The 32-bit ODBC worker runs under the TechBench service account.",
            AutoSize = true, MaximumSize = new Size(720, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 18, 3, 3)
        };
        layout.Controls.Add(note, 0, 4); layout.SetColumnSpan(note, 2);
        settings.Controls.Add(layout);
        return BuildStackedTab("Sage 50", credential, settings);
    }

    private void AddSecretRow(TableLayoutPanel layout, int row, string label, TextBox box, Label status, ProtectedSecretStore store, string name)
    {
        layout.Controls.Add(Label(label), 0, row); layout.Controls.Add(box, 1, row);
        var show = new CheckBox { Text = "Show", AutoSize = true };
        show.CheckedChanged += (_, _) => box.UseSystemPasswordChar = !show.Checked;
        layout.Controls.Add(show, 2, row);
        layout.Controls.Add(status, 3, row);
        var save = Button("Save / Rotate");
        save.Click += async (_, _) => await RunAsync($"Saving the {name} credential...", async () =>
        {
            await Task.Run(() => store.Write(box.Text)); box.Clear(); RefreshSecretStatus(); AddLog($"Protected {name} credential saved.");
        });
        layout.Controls.Add(save, 1, row + 1); layout.SetColumnSpan(save, 2);
    }

    private NotifyIcon BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_trayStart); menu.Items.Add(_trayStop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _trayStart.Click += async (_, _) => await ServiceActionAsync("Starting service...", _service.Start);
        _trayStop.Click += async (_, _) => await ServiceActionAsync("Stopping service...", _service.Stop);
        var tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "TechBench Server Manager", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => RestoreFromTray();
        return tray;
    }

    private void WireEvents()
    {
        _mappingGrid.DataError += (_, args) => args.ThrowException = false;
    }

    private async Task RefreshEverythingAsync(bool showErrors)
    {
        await RefreshServiceAsync();
        RefreshSecretStatus();
        await RefreshConfigurationAsync(showErrors);
    }

    private void LoadLocalSqlConfiguration()
    {
        try
        {
            var configuration = _paths.ReadConfiguration();
            _sqlServer.Text = configuration.SqlServer;
            _sqlDatabase.Text = configuration.Database;
            _trustServerCertificate.Checked = configuration.TrustServerCertificate;
        }
        catch (Exception ex) { AddLog("ERROR: Installed SQL configuration could not be read: " + ex.Message); }
    }

    private async Task SaveSqlConfigurationAsync()
    {
        await RunAsync("Saving and testing the SQL Server connection...", async () =>
        {
            var configuration = new ServiceConfiguration(_sqlServer.Text.Trim(), _sqlDatabase.Text.Trim(), _trustServerCertificate.Checked);
            _paths.SaveConfiguration(configuration);
            _configuration = await Task.Run(_repository.Load);
            ApplyConfiguration(_configuration);
            var details = _service.GetDetails();
            if (details.Status == "Running") await Task.Run(_service.Restart);
            AddLog($"SQL connection verified with Windows authentication. Service configuration saved and {(details.Status == "Running" ? "restarted" : "left stopped")}.");
            await RefreshServiceAsync();
        });
    }

    private Task RefreshServiceAsync()
    {
        var details = _service.GetDetails();
        _serviceStatus.Text = details.Status;
        _serviceVersion.Text = details.Version;
        _serviceAccount.Text = details.Account;
        if (!_accountBox.Focused) _accountBox.Text = details.Account;
        _startButton.Enabled = details.Installed && details.Status != "Running";
        _stopButton.Enabled = details.Installed && details.Status != "Stopped";
        _restartButton.Enabled = details.Installed;
        _trayStart.Enabled = _startButton.Enabled; _trayStop.Enabled = _stopButton.Enabled;
        _trayIcon.Text = $"TechBench: {details.Status}";
        return Task.CompletedTask;
    }

    private void RefreshSecretStatus()
    {
        _whdSecretStatus.Text = _whdSecret.Exists ? "Configured" : "Not configured";
        _sageSecretStatus.Text = _sageSecret.Exists ? "Configured" : "Not configured";
    }

    private async Task RefreshConfigurationAsync(bool showErrors)
    {
        try
        {
            _configuration = await Task.Run(_repository.Load);
            try
            {
                var directoryUsers = await Task.Run(_directoryUsers.LoadAuthorizedUsers);
                var mergedMappings = ActiveDirectoryUserProvider.MergeMappings(
                    directoryUsers,
                    _configuration.UserMappings);
                _configuration.UserMappings.Clear();
                _configuration.UserMappings.AddRange(mergedMappings);
            }
            catch (Exception directoryError)
            {
                AddLog("WARNING: Active Directory users could not be refreshed; showing registered SQL users only. " + directoryError.Message);
            }
            ApplyConfiguration(_configuration);
            AddLog("Shared WHD and Sage settings refreshed from SQL Server.");
        }
        catch (Exception ex)
        {
            _whdSyncStatus.Text = "Configuration unavailable: " + FriendlySqlError(ex);
            _sageSyncStatus.Text = _whdSyncStatus.Text;
            AddLog("ERROR: " + FriendlySqlError(ex));
            if (showErrors) ShowError(FriendlySqlError(ex));
        }
    }

    private void ApplyConfiguration(SynchronizationConfiguration configuration)
    {
        string Setting(string key, string fallback = "") => configuration.Settings.TryGetValue(key, out var value) ? value : fallback;
        _whdBaseUrl.Text = Setting("Whd.BaseUrl");
        _whdMode.SelectedItem = Setting("Whd.AuthenticationMode", "Auto");
        if (_whdMode.SelectedIndex < 0) _whdMode.SelectedIndex = 0;
        _whdUsername.Text = Setting("Whd.ServiceUsername");
        _whdAuto.Checked = bool.TryParse(Setting("Whd.AutoSyncEnabled"), out var enabled) && enabled;
        if (decimal.TryParse(Setting("Whd.AutoSyncMinutes"), out var minutes)) _whdMinutes.Value = Math.Clamp(minutes, 1, 120);
        _sageDsn.Text = Setting("Sage.SyncDsn"); _sageUsername.Text = Setting("Sage.SyncUsername");
        _whdSyncStatus.Text = FormatStatus(configuration.WhdStatus, false);
        _sageSyncStatus.Text = FormatStatus(configuration.SageStatus, true);
        _confirmSageButton.Visible = configuration.SageStatus.RequiresLargeRemovalConfirmation;
        PopulateMappingGrid(configuration);
    }

    private async Task SaveWhdAsync(bool requestSync)
    {
        await RunAsync(requestSync ? "Saving WHD settings and requesting synchronization..." : "Saving WHD settings...", async () =>
        {
            if (!Uri.TryCreate(_whdBaseUrl.Text.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Enter the complete HTTPS Web Help Desk base URL.");
            if (string.IsNullOrWhiteSpace(_whdUsername.Text)) throw new InvalidOperationException("Enter the organization-wide WHD username.");
            var settings = new Dictionary<string, string>
            {
                ["Whd.BaseUrl"] = uri.ToString().TrimEnd('/'), ["Whd.AuthenticationMode"] = _whdMode.Text,
                ["Whd.ServiceUsername"] = _whdUsername.Text.Trim(), ["Whd.AutoSyncEnabled"] = _whdAuto.Checked.ToString(),
                ["Whd.AutoSyncMinutes"] = decimal.ToInt32(_whdMinutes.Value).ToString()
            };
            await Task.Run(() => _repository.SaveSettings(settings, _configuration?.RowVersions ?? new Dictionary<string, byte[]>()));
            AddLog("Shared WHD configuration saved.");
            await SavePendingMappingsAsync(requireAuthorizedUsers: false);
            if (requestSync) AddLog("WHD sync request: " + await Task.Run(_repository.RequestWhdSync));
            await RefreshConfigurationAsync(false);
        });
    }

    private async Task SaveSageAsync(bool requestSync, bool confirmLargeRemoval)
    {
        await RunAsync(requestSync ? "Saving Sage settings and requesting synchronization..." : "Saving Sage settings...", async () =>
        {
            if (string.IsNullOrWhiteSpace(_sageDsn.Text)) throw new InvalidOperationException("Enter the server 32-bit Sage System DSN.");
            if (string.IsNullOrWhiteSpace(_sageUsername.Text)) throw new InvalidOperationException("Enter the organization-wide Sage ODBC username.");
            var settings = new Dictionary<string, string>
            {
                ["Sage.SyncDsn"] = _sageDsn.Text.Trim(), ["Sage.SyncUsername"] = _sageUsername.Text.Trim()
            };
            await Task.Run(() => _repository.SaveSettings(settings, _configuration?.RowVersions ?? new Dictionary<string, byte[]>()));
            AddLog("Shared Sage configuration saved.");
            await SavePendingMappingsAsync(requireAuthorizedUsers: false);
            if (requestSync)
            {
                var confirmedId = confirmLargeRemoval ? _configuration?.SageStatus.RequestId : null;
                if (confirmLargeRemoval && confirmedId is null) throw new InvalidOperationException("No rejected Sage snapshot is waiting for confirmation.");
                AddLog("Sage sync request: " + await Task.Run(() => _repository.RequestSageSync(confirmLargeRemoval, confirmedId)));
            }
            await RefreshConfigurationAsync(false);
        });
    }

    private async Task SaveMappingsAsync()
    {
        await RunAsync("Saving all WHD user mappings...", async () =>
        {
            await SavePendingMappingsAsync(requireAuthorizedUsers: true);
            await RefreshConfigurationAsync(false);
        });
    }

    private async Task SavePendingMappingsAsync(bool requireAuthorizedUsers)
    {
        // A DataGridViewComboBoxCell can still contain an uncommitted selection when
        // focus moves directly from its dropdown to a Save button.
        _mappingGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        if (!_mappingGrid.EndEdit())
            throw new InvalidOperationException("The current WHD technician selection could not be committed.");

        var assignments = _mappingGrid.Rows
            .Cast<DataGridViewRow>()
            .Where(static row => row.Tag is UserMapping)
            .Select(row =>
            {
                var mapping = (UserMapping)row.Tag!;
                return new UserMappingAssignment(
                    mapping.LoginName,
                    mapping.DisplayName,
                    mapping.IsAdmin,
                    Convert.ToString(row.Cells["WHD technician"].Value) ?? string.Empty);
            })
            .ToList();

        if (assignments.Count == 0)
        {
            if (requireAuthorizedUsers)
                throw new InvalidOperationException("No authorized TechBench AD users were found to map.");
            return;
        }

        await Task.Run(() => _repository.SaveMappings(assignments));
        AddLog($"Saved WHD technician mappings for {assignments.Count} authorized TechBench users.");
    }

    private void ConfigureMappingGrid()
    {
        if (_mappingGrid.Columns.Count > 0) return;
        _mappingGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "TechBench user",
            HeaderText = "TechBench user",
            ReadOnly = true,
            FillWeight = 45
        });
        _mappingGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Access",
            HeaderText = "Access",
            ReadOnly = true,
            FillWeight = 15
        });
        _mappingGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "WHD technician",
            HeaderText = "WHD technician",
            DisplayMember = nameof(Technician.Label),
            ValueMember = nameof(Technician.ExternalId),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FlatStyle = FlatStyle.Standard,
            FillWeight = 40
        });
    }

    private void PopulateMappingGrid(SynchronizationConfiguration configuration)
    {
        _mappingGrid.Rows.Clear();
        var technicians = configuration.Technicians.ToList();
        var technicianColumn = (DataGridViewComboBoxColumn)_mappingGrid.Columns["WHD technician"];
        technicianColumn.DataSource = technicians;
        var activeIds = technicians.Select(static technician => technician.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in configuration.UserMappings)
        {
            var technicianId = activeIds.Contains(mapping.TechnicianExternalId)
                ? mapping.TechnicianExternalId
                : string.Empty;
            var rowIndex = _mappingGrid.Rows.Add(
                mapping.Label,
                mapping.IsAdmin ? "Admin" : "User",
                technicianId);
            _mappingGrid.Rows[rowIndex].Tag = mapping;
        }

        var mappedCount = _mappingGrid.Rows
            .Cast<DataGridViewRow>()
            .Count(row => !string.IsNullOrWhiteSpace(Convert.ToString(row.Cells["WHD technician"].Value)));
        _mappingSummary.Text = $"{configuration.UserMappings.Count} AD users; {mappedCount} mapped";
    }

    private async Task CheckForUpdatesAsync()
    {
        await RunAsync("Checking the public TechBench release repository...", async () =>
        {
            _availableUpdate = await _updater.FindUpdateAsync(CancellationToken.None);
            if (_availableUpdate is null || SemanticVersion.CompareForUpdate(_availableUpdate.Version, _paths.CurrentVersion) <= 0)
            {
                _availableUpdate = null; _updateStatus.Text = $"Current ({_paths.CurrentVersion})"; _installUpdateButton.Enabled = false;
                MessageBox.Show($"TechBench {_paths.CurrentVersion} is already current.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                _updateStatus.Text = $"Version {_availableUpdate.Version} available"; _installUpdateButton.Enabled = true;
                AddLog($"Update {_availableUpdate.Version} is available.");
            }
        });
    }

    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is null) return;
        await RunAsync("Preparing the verified update...", async () =>
        {
            var progress = new Progress<string>(AddLog);
            var packageDirectory = await _updater.DownloadAndPrepareAsync(_availableUpdate, progress, CancellationToken.None);
            AddLog("Starting the compiled update helper. The Manager will reopen automatically.");
            ReleaseUpdater.LaunchInstaller(packageDirectory, Environment.ProcessId);
            _allowExit = true;
            BeginInvoke(Close);
        });
    }

    private async Task ServiceActionAsync(string busy, Action action) => await RunAsync(busy, async () =>
    {
        await Task.Run(action); await RefreshServiceAsync(); AddLog(busy.Replace("...", " completed."));
    });

    private async Task RunAsync(string busy, Func<Task> action)
    {
        if (_operationInProgress)
        {
            AddLog("Wait for the current server operation to finish.");
            return;
        }
        _operationInProgress = true;
        UseWaitCursor = true;
        AddLog(busy);
        try { await action(); }
        catch (Exception ex) { AddLog("ERROR: " + ex.Message); ShowError(ex.Message); }
        finally { UseWaitCursor = false; _operationInProgress = false; }
    }

    private void TryRepairShortcut()
    {
        try
        {
            if (Path.GetFullPath(Application.ExecutablePath).Equals(Path.GetFullPath(_paths.ManagerExecutable), StringComparison.OrdinalIgnoreCase))
            {
                ShortcutManager.Create(_paths);
                AddLog("Start Menu shortcut verified; it targets TechBench.ServerManager.exe directly.");
            }
        }
        catch (Exception ex) { AddLog("WARNING: Start Menu shortcut could not be repaired: " + ex.Message); }
    }

    private void RestoreFromTray()
    {
        Show(); WindowState = FormWindowState.Normal; Activate(); BringToFront();
    }
    private void ExitApplication()
    {
        if (_operationInProgress)
        {
            MessageBox.Show("Wait for the current server operation to finish before exiting.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _allowExit = true; Close();
    }
    private void ClearSecretFields()
    {
        _servicePassword.Clear(); _whdSecretBox.Clear(); _sageSecretBox.Clear();
    }
    private void AddLog(string message)
    {
        var entry = $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}";
        _log.Select(0, 0);
        _log.SelectedText = entry;
        _log.Select(0, 0);
        _log.ScrollToCaret();
    }
    private void ShowError(string message) => MessageBox.Show(message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);

    private static string FriendlySqlError(Exception exception)
    {
        var text = exception.Message;
        if (text.Contains("certificate chain", StringComparison.OrdinalIgnoreCase))
            return text + " Set TechBenchSync.TrustServerCertificate to true in the installed service appsettings.json, or install a certificate trusted by this server.";
        return text;
    }
    private static string FormatStatus(SyncStatus status, bool sage)
    {
        static string Time(DateTime? value) => value?.ToLocalTime().ToString("g") ?? "Never";
        var health = string.IsNullOrWhiteSpace(status.LastError) ? status.Status : $"{status.Status}: {status.LastError}";
        var result = $"Health: {health}\r\nQueue: {status.QueueDepth} | Last attempt: {Time(status.LastAttemptAtUtc)} | Last success: {Time(status.LastSuccessfulAtUtc)}";
        return sage ? result + $"\r\nLast snapshot: read {status.ReadCount}, saved {status.SavedCount}, stale {status.StaleCount}" : result;
    }

    private static void ResizeFlowChildren(FlowLayoutPanel panel)
    {
        foreach (Control child in panel.Controls) child.Width = Math.Max(100, panel.ClientSize.Width - panel.Padding.Horizontal - 25);
    }
    private static GroupBox Group(string text, int height) => new() { Text = text, Height = height, Margin = new Padding(0, 0, 0, 10), Padding = new Padding(12) };
    private static TableLayoutPanel Grid(int columns, int rows)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, AutoSize = false };
        if (columns == 2)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        }
        else if (columns == 3)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }
        else
        {
            for (var index = 0; index < columns; index++)
                grid.ColumnStyles.Add(new ColumnStyle(index % 2 == 0 ? SizeType.AutoSize : SizeType.Percent, index % 2 == 0 ? 0 : 50));
        }
        for (var index = 0; index < rows; index++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return grid;
    }
    private static FlowLayoutPanel ButtonRow(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        row.Controls.AddRange(controls); return row;
    }
    private static Label Label(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 8, 3) };
    private static Label ValueLabel() => new() { AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };
    private static Label StatusLabel() => new() { AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = Color.FromArgb(50, 70, 90), Margin = new Padding(3, 12, 3, 3) };
    private static TextBox Field() => new() { Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4) };
    private static TextBox PasswordField(bool show = false) => new() { Dock = DockStyle.Fill, UseSystemPasswordChar = !show, Margin = new Padding(3, 4, 3, 4) };
    private static ComboBox DropDown() => new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 4, 3, 4) };
    private static Button Button(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(105, 32), Margin = new Padding(3, 3, 7, 3) };
}
