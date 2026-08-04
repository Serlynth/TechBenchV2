using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TechBench.Services;
using TechBench.ViewModels;

namespace TechBench;

public partial class ClientInfoBetaWindow : Window
{
    private readonly LocalPreferences _localPreferences;

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
        Closed += (_, _) =>
        {
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

    private void People_DoubleClick(object sender, MouseButtonEventArgs e) =>
        ExecuteEdit(sender, e, viewModel => viewModel.EditPersonCommand);

    private void PeopleLocationsSplitter_DragCompleted(
        object sender,
        DragCompletedEventArgs e) =>
        SavePeopleLocationsSplitRatio();

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

        var ratio = _localPreferences.PeopleLocationsSplitRatio;
        LocationsPaneColumn.Width = new GridLength(ratio, GridUnitType.Star);
        PeoplePaneColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);
        ApplyColumnWidths(
            LocationsDataGrid,
            _localPreferences.LocationGridColumnWidths);
        ApplyColumnWidths(
            PeopleDataGrid,
            _localPreferences.PeopleGridColumnWidths);
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
        SavePeopleLocationsSplitRatio(saveToDisk: false);
        TrySaveLocalPreferences();
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

    private void SavePeopleLocationsSplitRatio(bool saveToDisk = true)
    {
        var totalWidth = LocationsPaneColumn.ActualWidth
            + PeoplePaneColumn.ActualWidth;
        if (totalWidth > 0 && double.IsFinite(totalWidth))
        {
            _localPreferences.PeopleLocationsSplitRatio = Math.Clamp(
                LocationsPaneColumn.ActualWidth / totalWidth,
                0.2,
                0.8);
        }

        if (saveToDisk)
        {
            TrySaveLocalPreferences();
        }
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
