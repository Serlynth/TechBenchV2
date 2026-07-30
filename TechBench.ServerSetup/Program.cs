using System.Security.Principal;

namespace TechBench.ServerSetup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("TechBench Server Setup must run as an administrator.", "TechBench Server Setup",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Application.ThreadException += (_, eventArgs) => ShowFatal(eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            ShowFatal(eventArgs.ExceptionObject as Exception ?? new Exception("Unknown setup error."));
        Application.Run(new SetupForm());
    }

    private static void ShowFatal(Exception exception) => MessageBox.Show(
        $"TechBench Server Setup encountered an error.\n\n{exception.Message}",
        "TechBench Server Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
