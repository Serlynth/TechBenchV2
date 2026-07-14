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
            AppDialogWindow.Info(
                "Import Worklog",
                "Select at least one valid row to import.",
                this);
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
