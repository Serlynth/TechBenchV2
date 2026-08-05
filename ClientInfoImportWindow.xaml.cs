using System.Windows;

namespace TechBench;

public partial class ClientInfoImportWindow : Window
{
    public ClientInfoImportWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
