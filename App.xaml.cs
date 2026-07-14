using System.Threading;
using System.Windows;
using TechBench.Services;
using Velopack;

namespace TechBench;

public partial class App : System.Windows.Application
{
#if VISUAL_QA
    private const string SingleInstanceMutexName = @"Local\CSRI.TechBench.VisualQA.SingleInstance.v1";
#else
    private const string SingleInstanceMutexName = @"Local\CSRI.TechBench.SingleInstance.v1";
#endif
    private Mutex? _singleInstanceMutex;

    internal static string? UpdateCompletionVersion { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        UpdateCompletionVersion = ReadArgumentValue(e.Args, "--updated-to");

        if (e.Args.Any(static argument =>
                argument.Equals(SageOdbcWorker.WorkerArgument, StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Environment.ExitCode = SageOdbcWorker.RunAsync().GetAwaiter().GetResult();
            Shutdown(Environment.ExitCode);
            return;
        }

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            AppDialogWindow.Info(
                "TechBench",
                "TechBench is already running. Use the existing window so the same entry cannot be posted twice.");
            Shutdown();
            return;
        }

        base.OnStartup(e);
        MainWindow = new MainWindow();
#if VISUAL_QA
        MainWindow.ShowActivated = false;
#endif
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    private static string? ReadArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }
}
