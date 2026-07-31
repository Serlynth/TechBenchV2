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

public sealed class ClientSecretAuthPointWindow : Window
{
    private readonly ITechBenchRepository _repository;
    private readonly long _secretId;
    private readonly bool _forClipboard;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _cancel;
    private ClientSecretMfaChallenge? _challenge;
    private bool _approved;
    private bool _closing;

    public ClientSecretAuthPointWindow(
        ITechBenchRepository repository,
        long secretId,
        string secretLabel,
        bool forClipboard)
    {
        _repository = repository;
        _secretId = secretId;
        _forClipboard = forClipboard;
        Title = "WatchGuard AuthPoint verification";
        Width = 540;
        Height = 270;
        MinWidth = 460;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)FindResource("WindowBackgroundBrush");

        var root = new Grid { Margin = new Thickness(26) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = forClipboard ? "Approve copying this secret" : "Approve revealing this secret",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        content.Children.Add(new TextBlock
        {
            Text = secretLabel,
            Margin = new Thickness(0, 0, 0, 14)
        });
        _status = new TextBlock
        {
            Text = "Starting AuthPoint verification...",
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
            Content = "Cancel",
            MinWidth = 96,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        _cancel.Click += (_, _) => Close();
        Grid.SetRow(_cancel, 1);
        root.Children.Add(_cancel);
        Content = root;
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => CancelOutstandingChallenge();
    }

    public byte[]? AuthorizationToken { get; private set; }

    private async Task RunAsync()
    {
        try
        {
            _challenge = await Task.Run(() =>
                _repository.BeginClientSecretMfaChallenge(_secretId, _forClipboard));
            if (!_challenge.IsRequired)
            {
                _approved = true;
                DialogResult = true;
                return;
            }

            _status.Text = $"A push was sent to {_challenge.ProviderLogin}. Approve it in WatchGuard AuthPoint.";
            while (!_closing)
            {
                await Task.Delay(650);
                var current = await Task.Run(() =>
                    _repository.GetClientSecretMfaChallenge(
                        _challenge.ChallengeId,
                        _challenge.ChallengeNonce));
                switch (current.Status)
                {
                    case "Queued":
                        _status.Text = "Waiting for the TechBench server to send the AuthPoint push...";
                        continue;
                    case "Processing":
                        _status.Text = "Approve the push in WatchGuard AuthPoint...";
                        continue;
                    case "Approved" when current.AuthorizationToken is { Length: 32 }:
                        AuthorizationToken = current.AuthorizationToken;
                        _approved = true;
                        DialogResult = true;
                        return;
                    default:
                        _progress.IsIndeterminate = false;
                        _status.Text = string.IsNullOrWhiteSpace(current.OutcomeMessage)
                            ? "AuthPoint did not approve this request."
                            : current.OutcomeMessage;
                        _cancel.Content = "Close";
                        return;
                }
            }
        }
        catch (Exception exception)
        {
            _progress.IsIndeterminate = false;
            _status.Text = exception.Message;
            _cancel.Content = "Close";
        }
    }

    private void CancelOutstandingChallenge()
    {
        _closing = true;
        if (_challenge is not { IsRequired: true } challenge)
        {
            return;
        }

        var nonce = challenge.ChallengeNonce.ToArray();
        CryptographicOperations.ZeroMemory(challenge.ChallengeNonce);
        if (_approved)
        {
            CryptographicOperations.ZeroMemory(nonce);
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                _repository.CancelClientSecretMfaChallenge(
                    challenge.ChallengeId,
                    nonce);
            }
            catch
            {
                // Cancellation is best-effort; SQL expiry still fails closed.
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
            }
        });
    }
}
#endif
