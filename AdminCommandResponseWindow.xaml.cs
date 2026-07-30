using System.Windows;
using System.Windows.Threading;
using TechBench.ViewModels;

namespace TechBench;

public partial class AdminCommandResponseWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly AdminCommandTrackingBatch _batch;
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private bool _isRefreshing;

    public AdminCommandResponseWindow(
        MainWindowViewModel viewModel,
        AdminCommandTrackingBatch batch)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _batch = batch;
        DataContext = batch;
        _batch.TrackingUpdated += Batch_TrackingUpdated;
        _refreshTimer.Tick += RefreshTimer_Tick;
        Loaded += AdminCommandResponseWindow_Loaded;
        Closed += AdminCommandResponseWindow_Closed;
    }

    private async void AdminCommandResponseWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Start();
        await RefreshResponsesAsync();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshResponsesAsync();
    }

    private async Task RefreshResponsesAsync()
    {
        if (_isRefreshing || _batch.AllResponded)
        {
            if (_batch.AllResponded)
            {
                LiveStatusTextBlock.Text = "All selected users have responded.";
                _refreshTimer.Stop();
            }

            return;
        }

        _isRefreshing = true;
        try
        {
            await _viewModel.RefreshAdminCommandTrackingAsync();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void Batch_TrackingUpdated(object? sender, EventArgs e)
    {
        ProgressTextBlock.Text = _batch.ProgressLabel;
        if (_batch.AllResponded)
        {
            LiveStatusTextBlock.Text = "All selected users have responded.";
            _refreshTimer.Stop();
        }
    }

    private void AdminCommandResponseWindow_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;
        _batch.TrackingUpdated -= Batch_TrackingUpdated;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
