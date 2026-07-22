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
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        UpdateCompletionVersion = ReadArgumentValue(e.Args, "--updated-to");

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
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
        // interactive launch. Windows remains the authenticated identity; the
        // optional username only requests an Admin-only, read-only preview.
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
            AppDialogWindow.Error(
                "TechBench V2",
                $"TechBench V2 connected to SQL Server, but the workspace could not be opened:\n\n{ex.Message}");
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
