using System.Windows;
using TechBench.ViewModels;

namespace TechBench;

public partial class WorklogImportWindow : Window
{
    public WorklogImportWindow(WorklogImportViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public WorklogImportViewModel ViewModel => (WorklogImportViewModel)DataContext;

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshSummary();
        if (ViewModel.SelectedCount <= 0)
        {
            System.Windows.MessageBox.Show(
                this,
                "Select at least one valid row to import.",
                "Import Worklog",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
