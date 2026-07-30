using System.Windows;
using TechBench.ViewModels;

namespace TechBench;

public partial class WorkspaceShellDemoWindow : Window
{
    public WorkspaceShellDemoWindow()
    {
        InitializeComponent();
        DataContext = new WorkspaceShellDemoViewModel();
    }
}
