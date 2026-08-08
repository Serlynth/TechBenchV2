using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TechBench.Models;
using TechBench.Services;
using TechBench.ViewModels;

namespace TechBench;

public partial class ClientInfoBetaWindow : Window
{
    private readonly LocalPreferences _localPreferences;
    private INotifyPropertyChanged? _viewModelNotifications;

    public ClientInfoBetaWindow()
        : this(LocalPreferenceStore.LoadOrCreate())
    {
    }

    public ClientInfoBetaWindow(LocalPreferences localPreferences)
    {
        _localPreferences = localPreferences
            ?? throw new ArgumentNullException(nameof(localPreferences));
        InitializeComponent();
        ApplyLayoutPreferences();
        DataContextChanged += (_, _) => AttachViewModelNotifications();
        Loaded += (_, _) => AttachViewModelNotifications();
        Closed += (_, _) =>
        {
            DetachViewModelNotifications();
            (DataContext as ClientInfoBetaViewModel)?.ClearRevealedSecrets();
            SaveLayoutPreferences();
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static void ExecuteEdit(
        object sender,
        MouseButtonEventArgs e,
        Func<ClientInfoBetaViewModel, RelayCommand> commandSelector)
    {
        if (e.ChangedButton != MouseButton.Left
            || sender is not Selector selector)
        {
            return;
        }

        var viewModel = selector.DataContext as ClientInfoBetaViewModel
            ?? Window.GetWindow(selector)?.DataContext as ClientInfoBetaViewModel;
        if (viewModel is null)
        {
            return;
        }

        var command = commandSelector(viewModel);
        var item = selector.SelectedItem;
        if (command.CanExecute(item))
        {
            command.Execute(item);
            e.Handled = true;
        }
    }

    private void Locations_DoubleClick(object sender, MouseButtonEventArgs e) =>
        ExecuteEdit(sender, e, viewModel => viewModel.EditLocationCommand);

    private void Users_DoubleClick(object sender, MouseButtonEventArgs e) =>
        ExecuteEdit(sender, e, viewModel => viewModel.EditPersonCommand);

    private void Users_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: ClientInfoPerson person }
            && DataContext is ClientInfoBetaViewModel viewModel)
        {
            viewModel.SelectedPerson = person;
        }
    }

    private void ApplyLayoutPreferences()
    {
        if (_localPreferences.ProfileWindowWidth is >= 980 and var width
            && double.IsFinite(width))
        {
            Width = width;
        }

        if (_localPreferences.ProfileWindowHeight is >= 650 and var height
            && double.IsFinite(height))
        {
            Height = height;
        }

        ApplyColumnWidths(
            LocationsDataGrid,
            _localPreferences.LocationGridColumnWidths);
        ApplyColumnWidths(
            PeopleDataGrid,
            _localPreferences.PeopleGridColumnWidths);
        ApplyColumnWidths(
            CredentialsDataGrid,
            _localPreferences.AccessGridColumnWidths);
        CredentialDetailsColumn.Width = new GridLength(
            _localPreferences.AccessDetailsPaneWidth,
            GridUnitType.Pixel);
        WindowState = _localPreferences.ProfileWindowState.Equals(
            "Maximized",
            StringComparison.OrdinalIgnoreCase)
            ? WindowState.Maximized
            : WindowState.Normal;
    }

    private void SaveLayoutPreferences()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (bounds.Width >= MinWidth && double.IsFinite(bounds.Width))
        {
            _localPreferences.ProfileWindowWidth = bounds.Width;
        }

        if (bounds.Height >= MinHeight && double.IsFinite(bounds.Height))
        {
            _localPreferences.ProfileWindowHeight = bounds.Height;
        }

        _localPreferences.ProfileWindowState =
            WindowState == WindowState.Maximized ? "Maximized" : "Normal";
        SaveColumnWidths(
            LocationsDataGrid,
            _localPreferences.LocationGridColumnWidths,
            widths => _localPreferences.LocationGridColumnWidths = widths);
        SaveColumnWidths(
            PeopleDataGrid,
            _localPreferences.PeopleGridColumnWidths,
            widths => _localPreferences.PeopleGridColumnWidths = widths);
        SaveColumnWidths(
            CredentialsDataGrid,
            _localPreferences.AccessGridColumnWidths,
            widths => _localPreferences.AccessGridColumnWidths = widths);
        var credentialDetailsWidth = CredentialDetailsColumn.ActualWidth;
        if (credentialDetailsWidth is >= 260 and <= 720
            && double.IsFinite(credentialDetailsWidth))
        {
            _localPreferences.AccessDetailsPaneWidth =
                credentialDetailsWidth;
        }
        TrySaveLocalPreferences();
    }

    private void AttachViewModelNotifications()
    {
        DetachViewModelNotifications();
        _viewModelNotifications = DataContext as INotifyPropertyChanged;
        if (_viewModelNotifications is not null)
        {
            _viewModelNotifications.PropertyChanged += ViewModel_PropertyChanged;
        }

        ApplyLifecycleVisibility();
    }

    private void DetachViewModelNotifications()
    {
        if (_viewModelNotifications is not null)
        {
            _viewModelNotifications.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModelNotifications = null;
        }
    }

    private void ViewModel_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClientInfoBetaViewModel.IsLive)
            or nameof(ClientInfoBetaViewModel.Profile))
        {
            ApplyLifecycleVisibility();
        }
    }

    private void ApplyLifecycleVisibility()
    {
        var visibility = DataContext is ClientInfoBetaViewModel { IsLive: true }
            ? Visibility.Collapsed
            : Visibility.Visible;
        LocationsReviewColumn.Visibility = visibility;
        PeopleReviewColumn.Visibility = visibility;
        CredentialsReviewColumn.Visibility = visibility;
        FactsReviewColumn.Visibility = visibility;
    }

    private static void ApplyColumnWidths(
        DataGrid grid,
        IReadOnlyList<double> widths)
    {
        if (widths.Count != grid.Columns.Count)
        {
            return;
        }

        for (var index = 0; index < widths.Count; index++)
        {
            grid.Columns[index].Width = new DataGridLength(
                widths[index],
                DataGridLengthUnitType.Pixel);
        }
    }

    private static void SaveColumnWidths(
        DataGrid grid,
        IReadOnlyList<double> existingWidths,
        Action<List<double>> save)
    {
        var widths = grid.Columns
            .Select(static column => column.ActualWidth)
            .ToList();
        if (widths.Count != grid.Columns.Count
            || widths.Any(static width =>
                !double.IsFinite(width) || width is < 40 or > 1600))
        {
            save(existingWidths.ToList());
            return;
        }

        save(widths);
    }

    private void TrySaveLocalPreferences()
    {
        try
        {
            LocalPreferenceStore.Save(_localPreferences);
        }
        catch
        {
            // A local layout preference should never block Client Info.
        }
    }

    private void Resources_DoubleClick(object sender, MouseButtonEventArgs e) =>
        ExecuteEdit(sender, e, viewModel => viewModel.EditResourceCommand);

    private void Credentials_DoubleClick(object sender, MouseButtonEventArgs e) =>
        ExecuteEdit(sender, e, viewModel => viewModel.EditCredentialCommand);

    private void Facts_DoubleClick(object sender, MouseButtonEventArgs e) =>
        ExecuteEdit(sender, e, viewModel => viewModel.EditFactCommand);

    private void Attachments_DoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || DataContext is not ClientInfoBetaViewModel viewModel
            || !viewModel.OpenAttachmentCommand.CanExecute(null))
        {
            return;
        }

        viewModel.OpenAttachmentCommand.Execute(null);
        e.Handled = true;
    }

    private void Attachments_PreviewDragOver(
        object sender,
        System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void Attachments_Drop(
        object sender,
        System.Windows.DragEventArgs e)
    {
        if (DataContext is not ClientInfoBetaViewModel viewModel
            || e.Data.GetData(System.Windows.DataFormats.FileDrop)
                is not string[] paths)
        {
            return;
        }

        e.Handled = true;
        await viewModel.UploadAttachmentFilesAsync(paths);
    }
}
