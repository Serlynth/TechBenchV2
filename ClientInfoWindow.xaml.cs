using System.Windows;
using System.Windows.Input;
using TechBench.Models;

namespace TechBench;

public partial class ClientInfoWindow : Window
{
    public ClientInfoWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void EquipmentCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: EquipmentItem equipment })
        {
            return;
        }

        if (DataContext is ViewModels.ClientInfoProfileViewModel viewModel)
        {
            viewModel.SelectedEquipment = equipment;
        }
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        if (DataContext is ViewModels.ClientInfoProfileViewModel
            {
                SelectedEquipment: not null
            } viewModel)
        {
            viewModel.SelectedEquipment = null;
            e.Handled = true;
            return;
        }

        Close();
        e.Handled = true;
    }
}
