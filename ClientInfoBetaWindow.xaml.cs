using System.Windows;
using System.Windows.Controls;
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
            || sender is not DataGrid { DataContext: ClientInfoBetaViewModel viewModel })
        {
            return;
        }

        var command = commandSelector(viewModel);
        if (command.CanExecute(null))
        {
            command.Execute(null);
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
}
