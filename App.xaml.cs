using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using Microsoft.Data.SqlClient;
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

    internal static string? UpdateCompletionVersion { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override async void OnStartup(StartupEventArgs e)
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
                "TechBench V2",
                "TechBench V2 is already running. Use the existing window so the same entry cannot be posted twice.");
            Shutdown();
            return;
        }

        base.OnStartup(e);
        SqlServerConnectionOptions? connectionOptions = null;
        SqlServerConnectionFactory? connectionFactory = null;
        CurrentUserContext? currentUser = null;
        string? connectionStatus = null;
        try
        {
            connectionOptions = SqlServerConnectionConfig.Resolve();
            if (connectionOptions is not null)
            {
                connectionFactory = new SqlServerConnectionFactory(connectionOptions);
                currentUser = await connectionFactory.GetCurrentUserContextAsync();
            }
        }
        catch (SqlException ex)
        {
            connectionStatus = ResolveSqlConnectionError(ex);
        }
        catch (TaskCanceledException)
        {
            connectionStatus =
                "The saved SQL Server connection did not complete before it was cancelled.";
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

        if (currentUser is null)
        {
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

            connectionFactory = connectionWindow.ConnectionFactory;
            currentUser = connectionWindow.CurrentUser;
        }

        try
        {
            MainWindow = new MainWindow(connectionFactory!, currentUser);
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

    private static string ResolveSqlConnectionError(SqlException exception)
    {
        return exception.Number switch
        {
            -2 => "The saved SQL Server did not respond before the connection timed out.",
            53 => "The saved SQL Server or instance could not be found.",
            229 => "Your Windows account does not have permission to use TechBench.",
            4060 => "The saved TechBench database could not be opened.",
            18456 => "SQL Server did not accept your Windows domain identity.",
            _ => $"The saved SQL Server connection failed: {exception.Message}"
        };
    }
}
