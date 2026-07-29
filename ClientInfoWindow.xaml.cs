using System.Windows;
using System.Windows.Input;
using TechBench.ViewModels;

namespace TechBench;

public partial class ClientInfoWindow : Window
{
    public ClientInfoWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        if (DataContext is ClientInfoProfileViewModel
            {
                IsEquipmentDetailsVisible: true
            } viewModel)
        {
            viewModel.CloseEquipmentDetailsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        Close();
        e.Handled = true;
    }
}
