using System.Diagnostics;
using System.Drawing;
using TechBench.ServerManager;

namespace TechBench.ServerSetup;

internal sealed class SetupForm : Form
{
    private readonly Label _installedValue = new();
    private readonly Label _targetValue = new();
    private readonly Label _accountLabel = new();
    private readonly TextBox _accountBox = new();
    private readonly TextBox _log = new();
    private readonly Button _installButton = new();
    private readonly Button _closeButton = new();
    private ServiceDetails _service = new(false, "Not installed", string.Empty, "None");

    public SetupForm()
    {
        Text = "TechBench Server Setup";
        ClientSize = new Size(680, 470);
        MinimumSize = new Size(696, 509);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Font = new Font("Segoe UI", 9F);
        BuildLayout();
        Shown += (_, _) => RefreshState();
    }

    private void BuildLayout()
    {
        var title = new Label { Text = "TechBench Server Setup", Font = new Font(Font.FontFamily, 18F, FontStyle.Bold), AutoSize = true, Location = new Point(24, 22) };
        var intro = new Label
        {
            Text = "Install, repair, or update the TechBench Server Manager and Sync Service from one verified package.",
            AutoSize = false, Location = new Point(27, 62), Size = new Size(625, 36)
        };
        var state = new GroupBox { Text = "Installation", Location = new Point(24, 105), Size = new Size(632, 116) };
        state.Controls.Add(new Label { Text = "Installed", AutoSize = true, Location = new Point(18, 30) });
        _installedValue.Location = new Point(135, 30); _installedValue.AutoSize = true;
        state.Controls.Add(_installedValue);
        state.Controls.Add(new Label { Text = "This setup", AutoSize = true, Location = new Point(18, 60) });
        _targetValue.Location = new Point(135, 60); _targetValue.AutoSize = true;
        _targetValue.Text = SetupProductVersion();
        state.Controls.Add(_targetValue);

        _accountLabel.Text = "Windows service account";
        _accountLabel.AutoSize = true;
        _accountLabel.Location = new Point(42, 241);
        _accountBox.Location = new Point(205, 237);
        _accountBox.Size = new Size(330, 27);
        _accountBox.Text = "CSRI\\TechBench_Sync";
        Controls.Add(_accountLabel);
        Controls.Add(_accountBox);

        _log.Location = new Point(24, 282);
        _log.Size = new Size(632, 112);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = SystemColors.Window;

        _installButton.Location = new Point(364, 414);
        _installButton.Size = new Size(172, 34);
        _installButton.Text = "Install / Update";
        _installButton.Click += InstallClicked;
        _closeButton.Location = new Point(548, 414);
        _closeButton.Size = new Size(108, 34);
        _closeButton.Text = "Close";
        _closeButton.Click += (_, _) => Close();
        AcceptButton = _installButton;
        CancelButton = _closeButton;
        Controls.AddRange([title, intro, state, _log, _installButton, _closeButton]);
    }

    private void RefreshState()
    {
        _service = SetupEngine.CurrentService();
        _installedValue.Text = _service.Installed
            ? $"{_service.Version} — {_service.Status} — {_service.Account}"
            : "Not installed";
        _accountLabel.Visible = _accountBox.Visible = !_service.Installed;
        _installButton.Text = _service.Installed ? "Update / Repair" : "Install";
        Log(_service.Installed
            ? "The existing service account, SQL settings, and protected secrets will be preserved."
            : "A secure password dialog will appear for the Windows service account.");
    }

    private void InstallClicked(object? sender, EventArgs e)
    {
        if (_service.Installed && MessageBox.Show(
                "Server Setup will close Server Manager, stop the Sync Service, replace the verified binaries, and restart the service. Settings and protected secrets are preserved. Continue?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        ToggleBusy(true);
        try
        {
            var progress = new Progress<string>(Log);
            var result = SetupEngine.InstallOrUpdate(_accountBox.Text, progress);
            if (result != 0) throw new InvalidOperationException("Server Setup did not complete successfully.");
            MessageBox.Show("TechBench Server installation completed successfully.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            ToggleBusy(false);
            RefreshState();
        }
    }

    private void ToggleBusy(bool busy)
    {
        UseWaitCursor = busy;
        _installButton.Enabled = _closeButton.Enabled = !busy;
        _accountBox.Enabled = !busy;
        Application.DoEvents();
    }

    private void Log(string message)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(message)); return; }
        _log.AppendText($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
    }

    private static string SetupProductVersion()
    {
        var value = FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion;
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Split('+', 2)[0];
    }
}
