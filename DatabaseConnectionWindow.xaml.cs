using System.Security.Principal;
using System.Windows;
using Microsoft.Data.SqlClient;
using TechBench.Data;
using TechBench.Models;

namespace TechBench;

public partial class DatabaseConnectionWindow : Window
{
    public DatabaseConnectionWindow(
        SqlServerConnectionOptions? initialOptions,
        string? initialStatus = null)
    {
        InitializeComponent();
        ServerTextBox.Text = initialOptions?.Server ?? string.Empty;
        DatabaseTextBox.Text =
            initialOptions?.Database ?? SqlServerConnectionOptions.DefaultDatabaseName;
        TrustServerCertificateCheckBox.IsChecked =
            initialOptions?.TrustServerCertificate ?? false;
        WindowsIdentityTextBlock.Text =
            WindowsIdentity.GetCurrent().Name ?? Environment.UserName;
        StatusTextBlock.Text = initialStatus ?? string.Empty;
        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(ServerTextBox.Text))
            {
                ServerTextBox.Focus();
            }
            else
            {
                DatabaseTextBox.Focus();
            }
        };
    }

    public SqlServerConnectionFactory? ConnectionFactory { get; private set; }

    public CurrentUserContext? CurrentUser { get; private set; }

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
                TrustServerCertificateCheckBox.IsChecked == true)
                .NormalizeAndValidate();
            var connectionFactory = new SqlServerConnectionFactory(options);
            var currentUser = await connectionFactory.GetCurrentUserContextAsync();
            SqlServerConnectionConfig.Save(options);

            ConnectionFactory = connectionFactory;
            CurrentUser = currentUser;
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
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private static string ResolveSqlError(SqlException exception)
    {
        return exception.Number switch
        {
            -2 => "SQL Server did not respond before the connection timed out.",
            53 => "The SQL Server or instance could not be found.",
            229 => "Your Windows account does not have permission to use TechBench.",
            4060 => "The TechBench database could not be opened.",
            18456 => "SQL Server did not accept your Windows domain identity.",
            _ => $"Could not connect to SQL Server: {exception.Message}"
        };
    }
}
