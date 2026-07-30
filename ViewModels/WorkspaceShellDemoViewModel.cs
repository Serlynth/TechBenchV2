using TechBench.Models;
using TechBench.Services;

namespace TechBench.ViewModels;

public sealed class WorkspaceShellDemoViewModel : ObservableObject
{
    private string _currentSection = "Today";
    private BenchModule _activeBenchModule = BenchModule.TechBench;

    public WorkspaceShellDemoViewModel()
    {
        NavigateCommand = new RelayCommand(parameter =>
            CurrentSection = parameter?.ToString() ?? "Today");
        SwitchBenchModuleCommand = new RelayCommand(SwitchBenchModule);
    }

    public RelayCommand NavigateCommand { get; }
    public RelayCommand SwitchBenchModuleCommand { get; }

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

    public BenchModule ActiveBenchModule
    {
        get => _activeBenchModule;
        private set
        {
            if (!SetProperty(ref _activeBenchModule, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ModuleBrandName));
            OnPropertyChanged(nameof(ModuleLogoSource));
            OnPropertyChanged(nameof(IsTechBenchModule));
            OnPropertyChanged(nameof(IsSalesBenchModule));
            OnPropertyChanged(nameof(IsAdminBenchModule));
            OnPropertyChanged(nameof(HasModuleWorkspace));
            OnPropertyChanged(nameof(ShowsEmptyModuleShell));
            OnPropertyChanged(nameof(WorkspaceHeaderEyebrow));
            OnPropertyChanged(nameof(WorkspaceHeaderTitle));
            OnPropertyChanged(nameof(StatusMessage));
        }
    }

    public string StatusMessage =>
        $"Local {ModuleBrandName} preview. No SQL connection is used.";

    public string ModuleBrandName => ActiveBenchModule.ToString();
    public string ModuleLogoSource => ActiveBenchModule switch
    {
        BenchModule.SalesBench =>
            "/TechBenchV2;component/Assets/csri-salesbench-logo.png",
        BenchModule.AdminBench =>
            "/TechBenchV2;component/Assets/csri-adminbench-logo.png",
        _ => "/TechBenchV2;component/Assets/csri-techbench-logo.png"
    };
    public double ModuleLogoDisplayWidth => 252;
    public string WorkspaceHeaderEyebrow => IsTechBenchModule
        ? "WORKSPACE"
        : "LOCAL MODULE PREVIEW";
    public string WorkspaceHeaderTitle => IsSalesBenchModule
        ? ModuleBrandName
        : CurrentSection;
    public string WorkspaceStateLabel => "LOCAL UI PREVIEW";

    public string DatabasePath => "No SQL connection";

    public bool CanAccessFireDrill => true;

    public bool CanAccessClientUsers => true;

    public bool CanAccessEquipmentBoard => true;

    public bool CanAccessAdminCenter => true;

    public bool CanAccessBenchModules => true;

    public bool IsTechBenchModule => ActiveBenchModule == BenchModule.TechBench;

    public bool IsSalesBenchModule => ActiveBenchModule == BenchModule.SalesBench;

    public bool IsAdminBenchModule => ActiveBenchModule == BenchModule.AdminBench;

    public bool HasModuleWorkspace => !IsSalesBenchModule;

    public bool ShowsEmptyModuleShell => IsSalesBenchModule;

    private void SwitchBenchModule(object? parameter)
    {
        if (!Enum.TryParse<BenchModule>(
                parameter?.ToString(),
                ignoreCase: true,
                out var module))
        {
            return;
        }

        ActiveBenchModule = module;
        CurrentSection = module == BenchModule.AdminBench
            ? "Client Match"
            : "Today";
        ThemeService.Apply(AppTheme.Dark, module);
    }
}
