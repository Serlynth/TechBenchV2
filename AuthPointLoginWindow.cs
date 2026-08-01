#if TECHBENCH_CLIENT_INFO_BETA
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using TechBench.Data;
using TechBench.Models;
using Button = System.Windows.Controls.Button;
using Grid = System.Windows.Controls.Grid;
using ProgressBar = System.Windows.Controls.ProgressBar;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;

namespace TechBench;

public sealed class AuthPointLoginWindow : Window
{
    private readonly SqlServerConnectionFactory _connectionFactory;
    private readonly AuthPointLoginRequirement _requirement;
    private readonly Guid _clientInstanceId = Guid.NewGuid();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _cancel;
    private ClientSecretMfaChallenge? _challenge;
    private bool _approved;
    private bool _closing;

    public AuthPointLoginWindow(
        SqlServerConnectionFactory connectionFactory,
        AuthPointLoginRequirement requirement)
    {
        _connectionFactory = connectionFactory;
        _requirement = requirement;
        Title = "Sign in with WatchGuard AuthPoint";
        Width = 560;
        Height = 300;
        MinWidth = 480;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)FindResource("WindowBackgroundBrush");

        var root = new Grid { Margin = new Thickness(26) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "Complete TechBench sign-in",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        content.Children.Add(new TextBlock
        {
            Text = "Windows verified your identity. Approve one AuthPoint push to open the Client Info beta.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        _status = new TextBlock
        {
            Text = "Starting AuthPoint sign-in...",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        };
        content.Children.Add(_status);
        _progress = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 7
        };
        content.Children.Add(_progress);
        root.Children.Add(content);

        _cancel = new Button
        {
            Content = "Cancel sign-in",
            MinWidth = 112,
            IsCancel = true,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        _cancel.Click += (_, _) => Close();
        Grid.SetRow(_cancel, 1);
        root.Children.Add(_cancel);
        Content = root;
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => CancelOutstandingChallenge();
    }

    private async Task RunAsync()
    {
        byte[]? authorizationToken = null;
        try
        {
            _challenge = await _connectionFactory.BeginAuthPointLoginAsync(
                _clientInstanceId,
                _cancellation.Token);
            if (!_challenge.IsRequired)
            {
                _approved = true;
                DialogResult = true;
                return;
            }

            var providerLogin = string.IsNullOrWhiteSpace(_challenge.ProviderLogin)
                ? _requirement.ProviderLogin
                : _challenge.ProviderLogin;
            _status.Text = $"A push was sent to {providerLogin}. Approve it in WatchGuard AuthPoint.";
            while (!_closing)
            {
                await Task.Delay(650, _cancellation.Token);
                var current = await _connectionFactory.GetAuthPointLoginStatusAsync(
                    _challenge.ChallengeId,
                    _challenge.ChallengeNonce,
                    _cancellation.Token);
                switch (current.Status)
                {
                    case "Queued":
                        _status.Text = "Waiting for the TechBench server to send the AuthPoint push...";
                        continue;
                    case "Processing":
                        _status.Text = "Approve the sign-in push in WatchGuard AuthPoint...";
                        continue;
                    case "Approved" when current.AuthorizationToken is { Length: 32 }:
                        authorizationToken = current.AuthorizationToken;
                        await _connectionFactory.ActivateAuthPointLoginSessionAsync(
                            _challenge.ChallengeId,
                            _challenge.ChallengeNonce,
                            authorizationToken,
                            _clientInstanceId,
                            _cancellation.Token);
                        _approved = true;
                        DialogResult = true;
                        return;
                    default:
                        _progress.IsIndeterminate = false;
                        _status.Text = string.IsNullOrWhiteSpace(current.OutcomeMessage)
                            ? "AuthPoint did not approve this TechBench sign-in."
                            : current.OutcomeMessage;
                        _cancel.Content = "Close";
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (_closing)
        {
            // The user closed the sign-in window.
        }
        catch (Exception exception)
        {
            _progress.IsIndeterminate = false;
            _status.Text = exception.Message;
            _cancel.Content = "Close";
        }
        finally
        {
            if (authorizationToken is not null)
            {
                CryptographicOperations.ZeroMemory(authorizationToken);
            }
        }
    }

    private void CancelOutstandingChallenge()
    {
        _closing = true;
        _cancellation.Cancel();
        if (_challenge is not { IsRequired: true } challenge)
        {
            _cancellation.Dispose();
            return;
        }

        var nonce = challenge.ChallengeNonce.ToArray();
        CryptographicOperations.ZeroMemory(challenge.ChallengeNonce);
        if (_approved)
        {
            CryptographicOperations.ZeroMemory(nonce);
            _cancellation.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _connectionFactory.CancelAuthPointLoginAsync(
                    challenge.ChallengeId,
                    nonce,
                    timeout.Token);
            }
            catch
            {
                // Cancellation is best-effort; SQL expiry still fails closed.
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
                _cancellation.Dispose();
            }
        });
    }
}
#endif
