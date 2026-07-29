namespace TechBench.ViewModels;

public sealed class WorkspaceShellDemoViewModel : ObservableObject
{
    private string _currentSection = "Today";

    public WorkspaceShellDemoViewModel()
    {
        NavigateCommand = new RelayCommand(parameter =>
            CurrentSection = parameter?.ToString() ?? "Today");
    }

    public RelayCommand NavigateCommand { get; }

    public string CurrentSection
    {
        get => _currentSection;
        private set
        {
            if (SetProperty(ref _currentSection, value))
            {
                OnPropertyChanged(nameof(StatusMessage));
            }
        }
    }

    public string StatusMessage =>
        $"Local UI preview for {CurrentSection}. No SQL connection is used.";

    public string WorkspaceStateLabel => "LOCAL UI PREVIEW";

    public string DatabasePath => "No SQL connection";

    public bool CanAccessFireDrill => true;

    public bool CanAccessClientUsers => true;

    public bool CanAccessEquipmentBoard => true;

    public bool CanAccessAdminCenter => true;
}
