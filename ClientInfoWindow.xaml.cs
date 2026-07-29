using System.Windows;
using System.Windows.Input;
using TechBench.Models;

namespace TechBench;

public partial class ClientInfoWindow : Window
{
    public event EventHandler<EquipmentItem>? EquipmentOpenRequested;

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

        EquipmentOpenRequested?.Invoke(this, equipment);
        Close();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        Close();
        e.Handled = true;
    }
}
