namespace TechBench.Tests;

public sealed class ServerManagerTrayScriptTests
{
    [Fact]
    public void ManagerMinimizesToNotificationAreaAndCanBeRestored()
    {
        var source = ReadManagerScript();

        Assert.Contains("[Windows.Forms.NotifyIcon]::new()", source, StringComparison.Ordinal);
        Assert.Contains("[Windows.Forms.ContextMenuStrip]::new()", source, StringComparison.Ordinal);
        Assert.Contains("Open TechBench Server Manager", source, StringComparison.Ordinal);
        Assert.Contains("$script:NotifyIcon.Add_DoubleClick({ Show-ManagerWindow })", source, StringComparison.Ordinal);
        Assert.Contains("[Windows.Forms.FormWindowState]::Minimized", source, StringComparison.Ordinal);
        Assert.Contains("Hide-ManagerToTray", source, StringComparison.Ordinal);
        Assert.Contains("$script:MainForm.Show()", source, StringComparison.Ordinal);
        Assert.Contains("$script:MainForm.BringToFront()", source, StringComparison.Ordinal);
        Assert.Contains("$script:MainForm.Activate()", source, StringComparison.Ordinal);
        Assert.Contains("[Windows.Forms.Application]::Run($script:MainForm)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$script:MainForm.ShowDialog()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagerGivesOneTimeMinimizeFeedbackAndSupportsExplicitExit()
    {
        var source = ReadManagerScript();

        Assert.Contains("$script:TrayNoticeShown = $false", source, StringComparison.Ordinal);
        Assert.Contains("$script:NotifyIcon.ShowBalloonTip(3000)", source, StringComparison.Ordinal);
        Assert.Contains("Double-click the tray icon to reopen it.", source, StringComparison.Ordinal);
        Assert.Contains("$script:TrayExitMenuItem.Text = 'Exit'", source, StringComparison.Ordinal);
        Assert.Contains("$script:TrayExitMenuItem.Add_Click({ Request-ManagerExit })", source, StringComparison.Ordinal);
        Assert.Contains("$script:MainForm.Close()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MinimizingClearsAndRemasksEverySecretField()
    {
        var source = ReadManagerScript();
        var hideStart = source.IndexOf("function Hide-ManagerToTray", StringComparison.Ordinal);
        var exitStart = source.IndexOf("function Request-ManagerExit", hideStart, StringComparison.Ordinal);
        var hideFunction = source[hideStart..exitStart];

        Assert.Contains("function Clear-ManagerSecretFields", source, StringComparison.Ordinal);
        Assert.Contains("$script:ServicePasswordBox", source, StringComparison.Ordinal);
        Assert.Contains("$script:WhdSecretBox", source, StringComparison.Ordinal);
        Assert.Contains("$script:SageSecretBox", source, StringComparison.Ordinal);
        Assert.Contains("$script:ShowServicePasswordCheckBox", source, StringComparison.Ordinal);
        Assert.Contains("$script:ShowWhdSecretCheckBox", source, StringComparison.Ordinal);
        Assert.Contains("$script:ShowSageSecretCheckBox", source, StringComparison.Ordinal);
        Assert.Contains("$box.Clear()", source, StringComparison.Ordinal);
        Assert.Contains("$box.UseSystemPasswordChar = $true", source, StringComparison.Ordinal);
        Assert.Contains("$checkBox.Checked = $false", source, StringComparison.Ordinal);
        Assert.Contains("Clear-ManagerSecretFields", hideFunction, StringComparison.Ordinal);
        Assert.True(
            hideFunction.IndexOf("Clear-ManagerSecretFields", StringComparison.Ordinal) <
            hideFunction.IndexOf("$script:MainForm.Hide()", StringComparison.Ordinal));
    }

    [Fact]
    public void ManagerBlocksUnsafeExitAndDisposesTrayResources()
    {
        var source = ReadManagerScript();

        Assert.Contains("$script:TrayExitMenuItem.Enabled = -not $Busy", source, StringComparison.Ordinal);
        Assert.Contains("if ($script:OperationInProgress)", source, StringComparison.Ordinal);
        Assert.Contains("Wait for the current server operation to finish before exiting", source, StringComparison.Ordinal);
        Assert.Contains("$script:NotifyIcon.Visible = $false", source, StringComparison.Ordinal);
        Assert.Contains("$script:NotifyIcon.Dispose()", source, StringComparison.Ordinal);
        Assert.Contains("$script:TrayContextMenu.Dispose()", source, StringComparison.Ordinal);
        Assert.Contains("$script:ManagerIcon.Dispose()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagerLoadsPackagedIconWithoutHoldingTheFileOpen()
    {
        var source = ReadManagerScript();

        Assert.Contains("'csri-techbench-icon.ico'", source, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::ReadAllBytes($managerIconPath)", source, StringComparison.Ordinal);
        Assert.Contains("[IO.MemoryStream]::new($iconBytes, $false)", source, StringComparison.Ordinal);
        Assert.Contains("[Drawing.Icon]$sourceIcon.Clone()", source, StringComparison.Ordinal);
        Assert.Contains("$sourceIcon.Dispose()", source, StringComparison.Ordinal);
        Assert.Contains("$iconStream.Dispose()", source, StringComparison.Ordinal);
        Assert.Contains("[Drawing.SystemIcons]::Application", source, StringComparison.Ordinal);
    }

    private static string ReadManagerScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "TechBench-ServerManager.ps1");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the TechBenchV2 repository root.");
    }
}
