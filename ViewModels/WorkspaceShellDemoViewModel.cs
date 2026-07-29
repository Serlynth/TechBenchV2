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
                OnPropertyChanged(nameof(WorkspaceHeaderTitle));
            }
        }
    }

    public string StatusMessage =>
        $"Local UI preview for {CurrentSection}. No SQL connection is used.";

    public string ModuleBrandName => "TechBench";
    public string ModuleLogoSource =>
        "/TechBenchV2;component/Assets/csri-techbench-logo.png";
    public double ModuleLogoDisplayWidth => 252;
    public double ModuleLogoOffsetX => 0;
    public double ModuleLogoOffsetY => 2.5;
    public string WorkspaceHeaderEyebrow => "WORKSPACE";
    public string WorkspaceHeaderTitle => CurrentSection;
    public string WorkspaceStateLabel => "LOCAL UI PREVIEW";

    public string DatabasePath => "No SQL connection";

    public bool CanAccessFireDrill => true;

    public bool CanAccessClientUsers => true;

    public bool CanAccessEquipmentBoard => true;

    public bool CanAccessAdminCenter => true;

    public bool CanAccessBenchModules => false;

    public bool IsTechBenchModule => true;

    public bool IsSalesBenchModule => false;

    public bool IsAdminBenchModule => false;
}
