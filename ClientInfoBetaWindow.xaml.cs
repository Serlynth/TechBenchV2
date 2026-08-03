using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TechBench.ViewModels;

namespace TechBench;

public partial class ClientInfoBetaWindow : Window
{
    public ClientInfoBetaWindow()
    {
        InitializeComponent();
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
