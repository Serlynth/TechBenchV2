using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using TechBench.Data;
using TechBench.Models;
using TechBench.Services;
using Velopack;

namespace TechBench;

public partial class App : System.Windows.Application
{
#if VISUAL_QA
    private const string SingleInstanceMutexName = @"Local\CSRI.TechBenchV2.VisualQA.SingleInstance.v2";
#else
    private const string SingleInstanceMutexName = @"Local\CSRI.TechBenchV2.SingleInstance.v2";
#endif
    private Mutex? _singleInstanceMutex;
    private SqlServerConnectionFactory? _connectionFactory;

    internal static string? UpdateCompletionVersion { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            CrashLog.Record(
                "AppDomain.CurrentDomain.UnhandledException",
                eventArgs.ExceptionObject as Exception
                    ?? new InvalidOperationException(
                        $"Unhandled non-Exception object: {eventArgs.ExceptionObject}"));
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            CrashLog.Record(
                "TaskScheduler.UnobservedTaskException",
                eventArgs.Exception);

        VelopackApp.Build().Run();

        var app = new App();
        app.DispatcherUnhandledException += (_, eventArgs) =>
        {
            CrashLog.Record(
                "Application.DispatcherUnhandledException",
                eventArgs.Exception);
            eventArgs.Handled = true;
            try
            {
                AppDialogWindow.Error(
                    "TechBench V2 recovered from an interface error",
                    "TechBench prevented an unexpected interface error from closing the application."
                    + $"\n\n{eventArgs.Exception.Message}"
                    + $"\n\nDiagnostic details were saved to:\n{CrashLog.FilePath}");
            }
            catch
            {
                // The original exception is already logged. Avoid a second
                // exception in the recovery dialog from terminating the process.
            }
        };
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        UpdateCompletionVersion = ReadArgumentValue(e.Args, "--updated-to");
        var isEquipmentDemo = e.Args.Any(argument =>
            argument.Equals("--equipment-demo", StringComparison.OrdinalIgnoreCase));
        var isShellDemo = e.Args.Any(argument =>
            argument.Equals("--shell-demo", StringComparison.OrdinalIgnoreCase));

        var mutexName = isShellDemo
            ? $@"{SingleInstanceMutexName}.ShellDemo"
            : isEquipmentDemo
                ? $@"{SingleInstanceMutexName}.EquipmentDemo"
                : SingleInstanceMutexName;
        _singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            AppDialogWindow.Info(
                "TechBench V2",
                "TechBench V2 is already running. Use the existing window so the same entry cannot be posted twice.");
            Shutdown();
            return;
        }

        base.OnStartup(e);
        if (isShellDemo)
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow = new WorkspaceShellDemoWindow();
            MainWindow.Show();
            return;
        }

        if (isEquipmentDemo)
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow = new EquipmentBoardDemoWindow();
            MainWindow.Show();
            return;
        }

        SqlServerConnectionOptions? connectionOptions = null;
        string? connectionStatus = null;
        try
        {
            connectionOptions = SqlServerConnectionConfig.Resolve();
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            connectionStatus = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            connectionStatus =
                $"The saved SQL Server configuration could not be read: {ex.Message}";
        }

        // This lightweight connection screen is intentionally shown on every
        // interactive launch. Windows remains the authenticated identity.
        // Keep the process alive while this is the only window. Otherwise,
        // closing a successful modal connection window can trigger WPF's
        // default last-window shutdown before the workspace is shown.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var connectionWindow = new DatabaseConnectionWindow(
            connectionOptions,
            connectionStatus);
        if (connectionWindow.ShowDialog() != true
            || connectionWindow.ConnectionFactory is null
            || connectionWindow.CurrentUser is null)
        {
            Shutdown();
            return;
        }

        _connectionFactory = connectionWindow.ConnectionFactory;
        CurrentUserContext currentUser = connectionWindow.CurrentUser;

        try
        {
            MainWindow = new MainWindow(_connectionFactory, currentUser);
        }
        catch (Exception ex)
        {
            CrashLog.Record("MainWindow construction", ex);
            AppDialogWindow.Error(
                "TechBench V2",
                $"TechBench V2 connected to SQL Server, but the workspace could not be opened:\n\n{ex.Message}"
                + $"\n\nDiagnostic details were saved to:\n{CrashLog.FilePath}");
            Shutdown();
            return;
        }
#if VISUAL_QA
        MainWindow.ShowActivated = false;
#endif
        MainWindow.Show();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_connectionFactory?.IsReadOnlyPreview == true)
        {
            try
            {
                using var cleanupTimeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(5));
                _connectionFactory.EndUserPreviewAsync(cleanupTimeout.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                // Preview sessions are server-expiring. Application shutdown
                // must continue if best-effort early revocation is unavailable.
            }
        }

        _connectionFactory = null;
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
