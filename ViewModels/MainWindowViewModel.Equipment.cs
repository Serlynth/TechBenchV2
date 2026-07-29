using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Data;
using ExcelDataReader.Exceptions;
using Microsoft.Data.SqlClient;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    private EquipmentItem? _selectedEquipment;
    private bool _isEquipmentEditorVisible;
    private bool _isEquipmentBoardBusy;
    private bool _isNewEquipment;
    private string _equipmentDeviceType = "Desktop";
    private string _equipmentAssetTag = string.Empty;
    private string _equipmentName = string.Empty;
    private string _equipmentSerialNumber = string.Empty;
    private string _equipmentPartNumber = string.Empty;
    private string _equipmentIpAddress = string.Empty;
    private string _equipmentManufacturer = string.Empty;
    private string _equipmentModel = string.Empty;
    private string _equipmentAnyDeskNumber = string.Empty;
    private string _equipmentAnyDeskPassword = string.Empty;
    private bool _showEquipmentAnyDeskPassword;
    private InventoryClient? _equipmentClient;
    private InventoryClientUser? _equipmentClientUser;
    private string _equipmentLocationName = string.Empty;
    private string _equipmentNotes = string.Empty;
    private string _equipmentBoardStatus =
        "Drag equipment from Stock to a technician, then into Deployment when work is complete.";
    private string _equipmentSearchText = string.Empty;
    private int _equipmentBoardItemCount;
    private string _inventoryEquipmentSearchText = string.Empty;
    private string _inventoryStatusFilter =
        EquipmentInventoryFilter.AllStatuses;
    private string _inventoryDeviceTypeFilter =
        EquipmentInventoryFilter.AllDeviceTypes;
    private string _inventoryClientFilter =
        EquipmentInventoryFilter.AllClients;
    private string _inventoryTechnicianFilter =
        EquipmentInventoryFilter.AllTechnicians;
    private bool _inventoryStockOnly;

    public ObservableCollection<EquipmentLane> EquipmentLanes { get; } = new();
    public ObservableCollection<EquipmentLane> DeploymentLanes { get; } = new();
    public ObservableCollection<EquipmentItem> StockInventoryItems { get; } = new();
    public ObservableCollection<EquipmentItem> InventoryEquipmentItems { get; } = new();
    public ObservableCollection<string> InventoryStatusFilterOptions { get; } =
    [
        EquipmentInventoryFilter.AllStatuses,
        EquipmentInventoryFilter.StockStatus,
        EquipmentInventoryFilter.InProgressStatus,
        EquipmentInventoryFilter.DeploymentStatus
    ];
    public ObservableCollection<string> InventoryDeviceTypeFilterOptions { get; } = new();
    public ObservableCollection<string> InventoryClientFilterOptions { get; } = new();
    public ObservableCollection<string> InventoryTechnicianFilterOptions { get; } = new();
    public ObservableCollection<InventoryClient> InventoryClientOptions { get; } = new();
    public ObservableCollection<InventoryClientUser> InventoryClientUserOptions { get; } = new();
    public ObservableCollection<EquipmentAssignmentHistoryEntry>
        EquipmentAssignmentHistory { get; } = new();
    public ObservableCollection<string> EquipmentTypeOptions { get; } =
    [
        "Desktop",
        "Laptop",
        "Server",
        "Switch",
        "Firewall",
        "Access Point",
        "Printer",
        "UPS",
        "Phone",
        "Other"
    ];

    public AsyncRelayCommand RefreshEquipmentBoardCommand { get; private set; } = null!;
    public AsyncRelayCommand ImportEquipmentBuildSheetCommand { get; private set; } = null!;
    public RelayCommand NewEquipmentCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveEquipmentCommand { get; private set; } = null!;
    public AsyncRelayCommand ArchiveEquipmentCommand { get; private set; } = null!;
    public RelayCommand CancelEquipmentEditCommand { get; private set; } = null!;
    public RelayCommand ClearEquipmentSearchCommand { get; private set; } = null!;
    public RelayCommand ClearInventoryEquipmentFiltersCommand { get; private set; } = null!;
    public RelayCommand CopyEquipmentDetailsCommand { get; private set; } = null!;
    public RelayCommand LaunchAnyDeskCommand { get; private set; } = null!;

    public EquipmentItem? SelectedEquipment
    {
        get => _selectedEquipment;
        set
        {
            if (SetProperty(ref _selectedEquipment, value) && value is not null)
            {
                LoadEquipmentEditor(value);
            }
        }
    }

    public bool IsEquipmentEditorVisible
    {
        get => _isEquipmentEditorVisible;
        private set
        {
            if (SetProperty(ref _isEquipmentEditorVisible, value))
            {
                OnPropertyChanged(nameof(EquipmentEditorTitle));
                OnPropertyChanged(nameof(IsEquipmentQuickViewVisible));
                OnPropertyChanged(nameof(IsEquipmentInventoryEditorVisible));
                CopyEquipmentDetailsCommand?.RaiseCanExecuteChanged();
                LaunchAnyDeskCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsEquipmentQuickViewVisible =>
        IsEquipmentEditorVisible
        && !CurrentSection.Equals(
            "Inventory",
            StringComparison.Ordinal);

    public bool IsEquipmentInventoryEditorVisible =>
        IsEquipmentEditorVisible
        && CurrentSection.Equals(
            "Inventory",
            StringComparison.Ordinal);

    public bool IsEquipmentBoardBusy
    {
        get => _isEquipmentBoardBusy;
        private set
        {
            if (SetProperty(ref _isEquipmentBoardBusy, value))
            {
                RefreshEquipmentBoardCommand?.RaiseCanExecuteChanged();
                ImportEquipmentBuildSheetCommand?.RaiseCanExecuteChanged();
                NewEquipmentCommand?.RaiseCanExecuteChanged();
                SaveEquipmentCommand?.RaiseCanExecuteChanged();
                ArchiveEquipmentCommand?.RaiseCanExecuteChanged();
                CancelEquipmentEditCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string EquipmentEditorTitle => _isNewEquipment
        ? "Add Equipment"
        : "Equipment Details";

    public string EquipmentAssignmentLabel => _isNewEquipment
        ? "Stock"
        : SelectedEquipment?.AssignmentLabel ?? "Stock";

    public string EquipmentDeviceType
    {
        get => _equipmentDeviceType;
        set
        {
            if (SetEquipmentEditorProperty(ref _equipmentDeviceType, value))
            {
                OnPropertyChanged(nameof(EquipmentSupportsAnyDesk));
                if (!EquipmentSupportsAnyDesk)
                {
                    ShowEquipmentAnyDeskPassword = false;
                }
            }
        }
    }

    public string EquipmentAssetTag
    {
        get => _equipmentAssetTag;
        set => SetEquipmentEditorProperty(ref _equipmentAssetTag, value);
    }

    public string EquipmentName
    {
        get => _equipmentName;
        set => SetEquipmentEditorProperty(ref _equipmentName, value);
    }

    public string EquipmentSerialNumber
    {
        get => _equipmentSerialNumber;
        set => SetEquipmentEditorProperty(ref _equipmentSerialNumber, value);
    }

    public string EquipmentPartNumber
    {
        get => _equipmentPartNumber;
        set => SetEquipmentEditorProperty(ref _equipmentPartNumber, value);
    }

    public string EquipmentIpAddress
    {
        get => _equipmentIpAddress;
        set => SetEquipmentEditorProperty(ref _equipmentIpAddress, value);
    }

    public string EquipmentManufacturer
    {
        get => _equipmentManufacturer;
        set => SetEquipmentEditorProperty(ref _equipmentManufacturer, value);
    }

    public string EquipmentModel
    {
        get => _equipmentModel;
        set => SetEquipmentEditorProperty(ref _equipmentModel, value);
    }

    public InventoryClient? EquipmentClient
    {
        get => _equipmentClient;
        set
        {
            if (!SetProperty(ref _equipmentClient, value))
            {
                return;
            }

            RefreshInventoryClientUserOptions();
            if (value is null)
            {
                EquipmentClientUser = null;
                EquipmentLocationName = string.Empty;
            }
            else if (string.IsNullOrWhiteSpace(EquipmentLocationName))
            {
                EquipmentLocationName = value.PrimaryLocation;
            }

            SaveEquipmentCommand?.RaiseCanExecuteChanged();
        }
    }

    public InventoryClientUser? EquipmentClientUser
    {
        get => _equipmentClientUser;
        set
        {
            if (SetProperty(ref _equipmentClientUser, value))
            {
                if (value is not null
                    && !string.IsNullOrWhiteSpace(value.LocationName))
                {
                    EquipmentLocationName = value.LocationName;
                }
                SaveEquipmentCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string EquipmentLocationName
    {
        get => _equipmentLocationName;
        set => SetEquipmentEditorProperty(ref _equipmentLocationName, value);
    }

    public string EquipmentNotes
    {
        get => _equipmentNotes;
        set => SetEquipmentEditorProperty(ref _equipmentNotes, value);
    }

    public string EquipmentBoardStatus
    {
        get => _equipmentBoardStatus;
        private set => SetProperty(ref _equipmentBoardStatus, value);
    }

    public string EquipmentSearchText
    {
        get => _equipmentSearchText;
        set
        {
            if (SetProperty(ref _equipmentSearchText, value ?? string.Empty))
            {
                RefreshEquipmentSearch();
                ClearEquipmentSearchCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string InventoryEquipmentSearchText
    {
        get => _inventoryEquipmentSearchText;
        set
        {
            if (SetProperty(
                    ref _inventoryEquipmentSearchText,
                    value ?? string.Empty))
            {
                RefreshInventoryEquipmentFilter();
            }
        }
    }

    public string InventoryStatusFilter
    {
        get => _inventoryStatusFilter;
        set
        {
            if (SetProperty(
                    ref _inventoryStatusFilter,
                    value ?? EquipmentInventoryFilter.AllStatuses))
            {
                RefreshInventoryEquipmentFilter();
            }
        }
    }

    public string InventoryDeviceTypeFilter
    {
        get => _inventoryDeviceTypeFilter;
        set
        {
            if (SetProperty(
                    ref _inventoryDeviceTypeFilter,
                    value ?? EquipmentInventoryFilter.AllDeviceTypes))
            {
                RefreshInventoryEquipmentFilter();
            }
        }
    }

    public string InventoryClientFilter
    {
        get => _inventoryClientFilter;
        set
        {
            if (SetProperty(
                    ref _inventoryClientFilter,
                    value ?? EquipmentInventoryFilter.AllClients))
            {
                RefreshInventoryEquipmentFilter();
            }
        }
    }

    public string InventoryTechnicianFilter
    {
        get => _inventoryTechnicianFilter;
        set
        {
            if (SetProperty(
                    ref _inventoryTechnicianFilter,
                    value ?? EquipmentInventoryFilter.AllTechnicians))
            {
                RefreshInventoryEquipmentFilter();
            }
        }
    }

    public bool InventoryStockOnly
    {
        get => _inventoryStockOnly;
        set
        {
            if (SetProperty(ref _inventoryStockOnly, value))
            {
                RefreshInventoryEquipmentFilter();
            }
        }
    }

    public string InventoryEquipmentCountLabel
    {
        get
        {
            var visibleCount = GetVisibleInventoryEquipmentCount();
            var totalCount = InventoryEquipmentItems.Count;
            return visibleCount == totalCount
                ? $"{totalCount} device{(totalCount == 1 ? string.Empty : "s")}"
                : $"{visibleCount} of {totalCount} devices";
        }
    }

    public bool HasInventoryEquipmentResults =>
        GetVisibleInventoryEquipmentCount() > 0;

    public string EquipmentDeviceCountLabel
    {
        get
        {
            var count = _equipmentBoardItemCount;
            return $"{count} device{(count == 1 ? string.Empty : "s")}";
        }
    }

    public string StockInventoryCountLabel
    {
        get
        {
            var count = StockInventoryItems.Count;
            return count == 1 ? "1 item in stock" : $"{count} items in stock";
        }
    }

    public bool HasStockInventoryItems => StockInventoryItems.Count > 0;

    public bool EquipmentSupportsAnyDesk =>
        EquipmentDeviceType.Equals("Desktop", StringComparison.OrdinalIgnoreCase)
        || EquipmentDeviceType.Equals("Laptop", StringComparison.OrdinalIgnoreCase);

    public string EquipmentAnyDeskNumber
    {
        get => _equipmentAnyDeskNumber;
        set
        {
            if (SetEquipmentEditorProperty(
                    ref _equipmentAnyDeskNumber,
                    value))
            {
                LaunchAnyDeskCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string EquipmentAnyDeskPassword
    {
        get => _equipmentAnyDeskPassword;
        set
        {
            SetEquipmentEditorProperty(
                ref _equipmentAnyDeskPassword,
                value);
        }
    }

    public bool ShowEquipmentAnyDeskPassword
    {
        get => _showEquipmentAnyDeskPassword;
        set => SetProperty(ref _showEquipmentAnyDeskPassword, value);
    }

    public int HiddenEquipmentTechnicianCount =>
        _localPreferences.HiddenEquipmentTechnicians.Count;

    public bool HasHiddenEquipmentTechnicians =>
        HiddenEquipmentTechnicianCount > 0;

    public string HiddenEquipmentTechnicianLabel =>
        HiddenEquipmentTechnicianCount == 1
            ? "Show 1 hidden tech"
            : $"Show {HiddenEquipmentTechnicianCount} hidden techs";

    public string EquipmentDeploymentCountLabel
    {
        get
        {
            var count = DeploymentLanes.Sum(static lane => lane.Items.Count);
            return count == 1 ? "1 item" : $"{count} items";
        }
    }

    private void InitializeEquipmentBoard()
    {
        RefreshEquipmentBoardCommand = new AsyncRelayCommand(
            _ => RefreshEquipmentBoardAsync(),
            _ => CanAccessEquipmentBoard && !IsEquipmentBoardBusy);
        ImportEquipmentBuildSheetCommand = new AsyncRelayCommand(
            _ => ImportEquipmentBuildSheetAsync(),
            _ => CanEditEquipmentRecords());
        NewEquipmentCommand = new RelayCommand(
            _ => BeginNewEquipment(),
            _ => CanEditEquipmentRecords());
        SaveEquipmentCommand = new AsyncRelayCommand(
            _ => SaveEquipmentAsync(),
            _ => CanSaveEquipment());
        ArchiveEquipmentCommand = new AsyncRelayCommand(
            _ => ArchiveEquipmentAsync(),
            _ => CanEditEquipmentRecords()
                && !_isNewEquipment
                && SelectedEquipment is { EquipmentId: > 0 });
        CancelEquipmentEditCommand = new RelayCommand(
            _ => CloseEquipmentEditor(),
            _ => !IsEquipmentBoardBusy);
        ClearEquipmentSearchCommand = new RelayCommand(
            _ => EquipmentSearchText = string.Empty,
            _ => !string.IsNullOrWhiteSpace(EquipmentSearchText));
        ClearInventoryEquipmentFiltersCommand = new RelayCommand(
            _ => ClearInventoryEquipmentFilters(),
            _ => HasActiveInventoryEquipmentFilters());
        CopyEquipmentDetailsCommand = new RelayCommand(
            _ => CopyEquipmentDetails(),
            _ => IsEquipmentEditorVisible
                && (!string.IsNullOrWhiteSpace(EquipmentName)
                    || SelectedEquipment is not null));
        LaunchAnyDeskCommand = new RelayCommand(
            LaunchAnyDesk,
            CanLaunchAnyDesk);
    }

    private async Task RefreshEquipmentBoardAsync(long? selectEquipmentId = null)
    {
        if (!CanAccessEquipmentBoard || IsEquipmentBoardBusy)
        {
            return;
        }

        IsEquipmentBoardBusy = true;
        EquipmentBoardStatus = "Refreshing equipment and technician lanes…";
        try
        {
            var result = await Task.Run(() =>
            {
                var mappings = _repository.GetWhdUserMappings();
                var equipment = _repository.GetEquipmentBoard();
                var clients = _repository.GetInventoryClients();
                return (mappings, equipment, clients);
            });

            RefreshInventoryClientOptions(result.clients);
            RebuildEquipmentLanes(result.mappings, result.equipment);
            var selectedId = selectEquipmentId ?? SelectedEquipment?.EquipmentId;
            SelectedEquipment = selectedId is > 0
                ? result.equipment.FirstOrDefault(item => item.EquipmentId == selectedId)
                : null;

            EquipmentBoardStatus =
                $"{result.equipment.Count} active item(s) across "
                + $"{EquipmentLanes.Count} work lane(s) plus Deployment. "
                + "Drag a card to set ownership, priority, or deployment readiness.";
        }
        catch (Exception ex) when (
            ex is SqlException
                or InvalidOperationException
                or TimeoutException)
        {
            EquipmentBoardStatus = $"Equipment Board refresh failed: {ex.Message}";
            _dialogService.Error("Equipment Board", EquipmentBoardStatus);
        }
        finally
        {
            IsEquipmentBoardBusy = false;
        }
    }

    private void RebuildEquipmentLanes(
        IReadOnlyList<WhdUserMapping> mappings,
        IReadOnlyList<EquipmentItem> equipment)
    {
        _equipmentBoardItemCount = equipment.Count;
        EnsureSignedInTechnicianIsInitiallyFirst(mappings);
        EquipmentLanes.Clear();
        DeploymentLanes.Clear();
        StockInventoryItems.Clear();
        RebuildInventoryEquipmentRegistry(equipment);
        var stockLane = new EquipmentLane(
            "Stock Room",
            null,
            EquipmentWorkflowStages.Stock);
        EquipmentLanes.Add(stockLane);
        var unassignedDeploymentLane = new EquipmentLane(
            "Unassigned",
            null,
            EquipmentWorkflowStages.Deployment);
        DeploymentLanes.Add(unassignedDeploymentLane);

        var lanesByLogin = new Dictionary<string, EquipmentLane>(
            StringComparer.OrdinalIgnoreCase);
        var deploymentLanesByLogin = new Dictionary<string, EquipmentLane>(
            StringComparer.OrdinalIgnoreCase);
        var hiddenTechnicians = _localPreferences.HiddenEquipmentTechnicians
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var technicianOrder = _localPreferences.EquipmentTechnicianOrder
            .Select(static (loginName, index) => (loginName, index))
            .ToDictionary(
                static entry => entry.loginName,
                static entry => entry.index,
                StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in mappings
                     .Where(static mapping => !string.IsNullOrWhiteSpace(mapping.LoginName))
                     .Where(mapping => !hiddenTechnicians.Contains(mapping.LoginName))
                     .OrderBy(mapping => technicianOrder.TryGetValue(
                         mapping.LoginName,
                         out var index)
                         ? index
                         : int.MaxValue)
                     .ThenBy(static mapping => mapping.DisplayName)
                     .ThenBy(static mapping => mapping.LoginName))
        {
            var title = string.IsNullOrWhiteSpace(mapping.DisplayName)
                ? mapping.LoginName
                : mapping.DisplayName;
            var lane = new EquipmentLane(title, mapping.LoginName);
            lanesByLogin[mapping.LoginName] = lane;
            EquipmentLanes.Add(lane);
            var deploymentLane = new EquipmentLane(
                title,
                mapping.LoginName,
                EquipmentWorkflowStages.Deployment);
            deploymentLanesByLogin[mapping.LoginName] = deploymentLane;
            DeploymentLanes.Add(deploymentLane);
        }

        foreach (var item in equipment
                     .OrderBy(static item => item.SortOrder)
                     .ThenBy(static item => item.EquipmentId))
        {
            if (item.IsDeployment)
            {
                if (string.IsNullOrWhiteSpace(item.AssignedToLoginName))
                {
                    unassignedDeploymentLane.Items.Add(item);
                    continue;
                }

                if (!deploymentLanesByLogin.TryGetValue(
                        item.AssignedToLoginName,
                        out var deploymentLane))
                {
                    if (hiddenTechnicians.Contains(item.AssignedToLoginName))
                    {
                        continue;
                    }

                    deploymentLane = new EquipmentLane(
                        string.IsNullOrWhiteSpace(item.AssignedToDisplayName)
                            ? item.AssignedToLoginName
                            : item.AssignedToDisplayName,
                        item.AssignedToLoginName,
                        EquipmentWorkflowStages.Deployment);
                    deploymentLanesByLogin[item.AssignedToLoginName] =
                        deploymentLane;
                    DeploymentLanes.Add(deploymentLane);
                }

                deploymentLane.Items.Add(item);
                continue;
            }

            if (item.IsInStock)
            {
                stockLane.Items.Add(item);
                continue;
            }

            if (!lanesByLogin.TryGetValue(item.AssignedToLoginName, out var lane))
            {
                if (hiddenTechnicians.Contains(item.AssignedToLoginName))
                {
                    continue;
                }

                lane = new EquipmentLane(
                    string.IsNullOrWhiteSpace(item.AssignedToDisplayName)
                        ? item.AssignedToLoginName
                        : item.AssignedToDisplayName,
                    item.AssignedToLoginName);
                lanesByLogin[item.AssignedToLoginName] = lane;
                EquipmentLanes.Add(lane);
            }

            lane.Items.Add(item);
        }

        foreach (var item in equipment
                     .Where(static item => item.IsInStock)
                     .OrderBy(static item => item.SortOrder)
                     .ThenBy(static item => item.Name)
                     .ThenBy(static item => item.EquipmentId))
        {
            StockInventoryItems.Add(item);
        }

        OnPropertyChanged(nameof(EquipmentDeviceCountLabel));
        OnPropertyChanged(nameof(EquipmentDeploymentCountLabel));
        OnPropertyChanged(nameof(StockInventoryCountLabel));
        OnPropertyChanged(nameof(HasStockInventoryItems));
        RefreshEquipmentSearch();
    }

    private void EnsureSignedInTechnicianIsInitiallyFirst(
        IReadOnlyList<WhdUserMapping> mappings)
    {
        var currentLoginName = _currentUser.LoginName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(currentLoginName)
            || string.Equals(
                _localPreferences.EquipmentTechnicianPriorityLoginName,
                currentLoginName,
                StringComparison.OrdinalIgnoreCase)
            || !mappings.Any(mapping => string.Equals(
                mapping.LoginName,
                currentLoginName,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var order = _localPreferences.EquipmentTechnicianOrder
            .Where(loginName => !string.Equals(
                loginName,
                currentLoginName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        order.Insert(0, currentLoginName);
        _localPreferences.EquipmentTechnicianOrder = order;
        _localPreferences.EquipmentTechnicianPriorityLoginName =
            currentLoginName;
        LocalPreferenceStore.Save(_localPreferences);
    }

    public void ReorderEquipmentTechnicianLane(
        EquipmentLane sourceLane,
        EquipmentLane targetLane)
    {
        ArgumentNullException.ThrowIfNull(sourceLane);
        ArgumentNullException.ThrowIfNull(targetLane);

        if (!sourceLane.IsReorderable
            || !targetLane.IsReorderable
            || string.Equals(
                sourceLane.AssignedToLoginName,
                targetLane.AssignedToLoginName,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var sourceWorkLane = EquipmentLanes.FirstOrDefault(lane =>
            string.Equals(
                lane.AssignedToLoginName,
                sourceLane.AssignedToLoginName,
                StringComparison.OrdinalIgnoreCase));
        var targetWorkLane = EquipmentLanes.FirstOrDefault(lane =>
            string.Equals(
                lane.AssignedToLoginName,
                targetLane.AssignedToLoginName,
                StringComparison.OrdinalIgnoreCase));
        if (sourceWorkLane is null || targetWorkLane is null)
        {
            return;
        }

        var sourceIndex = EquipmentLanes.IndexOf(sourceWorkLane);
        var targetIndex = EquipmentLanes.IndexOf(targetWorkLane);
        if (sourceIndex < 1 || targetIndex < 1)
        {
            return;
        }

        EquipmentLanes.Move(sourceIndex, targetIndex);
        SynchronizeDeploymentTechnicianOrder();
        _localPreferences.EquipmentTechnicianOrder = EquipmentLanes
            .Where(static lane => lane.IsReorderable)
            .Select(static lane => lane.AssignedToLoginName)
            .ToList();
        LocalPreferenceStore.Save(_localPreferences);
        EquipmentBoardStatus =
            $"Moved {sourceWorkLane.Title} to technician position {targetIndex}.";
    }

    public void HideEquipmentTechnicianLane(EquipmentLane lane)
    {
        ArgumentNullException.ThrowIfNull(lane);
        if (!lane.IsReorderable)
        {
            return;
        }

        var loginName = lane.AssignedToLoginName;
        if (!_localPreferences.HiddenEquipmentTechnicians.Contains(
                loginName,
                StringComparer.OrdinalIgnoreCase))
        {
            _localPreferences.HiddenEquipmentTechnicians.Add(loginName);
            LocalPreferenceStore.Save(_localPreferences);
        }

        var workLane = EquipmentLanes.FirstOrDefault(candidate =>
            string.Equals(
                candidate.AssignedToLoginName,
                loginName,
                StringComparison.OrdinalIgnoreCase));
        if (workLane is not null)
        {
            EquipmentLanes.Remove(workLane);
        }

        var deploymentLane = DeploymentLanes.FirstOrDefault(candidate =>
            string.Equals(
                candidate.AssignedToLoginName,
                loginName,
                StringComparison.OrdinalIgnoreCase));
        if (deploymentLane is not null)
        {
            DeploymentLanes.Remove(deploymentLane);
        }

        NotifyHiddenEquipmentTechniciansChanged();
        OnPropertyChanged(nameof(EquipmentDeploymentCountLabel));
        RefreshEquipmentSearch();
        EquipmentBoardStatus =
            $"Hidden {lane.Title} from this workstation's equipment board.";
    }

    public void ShowAllHiddenEquipmentTechnicians()
    {
        if (_localPreferences.HiddenEquipmentTechnicians.Count == 0)
        {
            return;
        }

        _localPreferences.HiddenEquipmentTechnicians.Clear();
        LocalPreferenceStore.Save(_localPreferences);
        NotifyHiddenEquipmentTechniciansChanged();
        EquipmentBoardStatus = "Restoring hidden technician columns…";
        _ = RefreshEquipmentBoardAsync();
    }

    private void NotifyHiddenEquipmentTechniciansChanged()
    {
        OnPropertyChanged(nameof(HiddenEquipmentTechnicianCount));
        OnPropertyChanged(nameof(HasHiddenEquipmentTechnicians));
        OnPropertyChanged(nameof(HiddenEquipmentTechnicianLabel));
    }

    private void SynchronizeDeploymentTechnicianOrder()
    {
        var desiredLogins = EquipmentLanes
            .Where(static lane => lane.IsReorderable)
            .Select(static lane => lane.AssignedToLoginName)
            .ToList();
        for (var desiredIndex = 0; desiredIndex < desiredLogins.Count; desiredIndex++)
        {
            var currentIndex = DeploymentLanes
                .Select(static (lane, index) => (lane, index))
                .FirstOrDefault(entry => string.Equals(
                    entry.lane.AssignedToLoginName,
                    desiredLogins[desiredIndex],
                    StringComparison.OrdinalIgnoreCase))
                .index;
            var destinationIndex = desiredIndex + 1;
            if (currentIndex > 0
                && currentIndex != destinationIndex)
            {
                DeploymentLanes.Move(currentIndex, destinationIndex);
            }
        }
    }

    public async Task OpenEquipmentFromInventoryAsync(EquipmentItem equipment)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        if (!CanAccessEquipmentBoard)
        {
            _dialogService.Error(
                "Equipment Board",
                CanAccessAdminCenter
                    ? "Equipment Board is not installed in this TechBench database yet."
                    : "Only TechBench Admins can open Equipment Board.");
            return;
        }

        await RefreshEquipmentBoardAsync(equipment.EquipmentId);
        StatusMessage =
            $"Showing {equipment.Name} details without leaving {CurrentSection}.";
    }

    private async Task ImportEquipmentBuildSheetAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import a PC configuration build sheet",
            Filter = "Excel workbooks (*.xlsx;*.xls)|*.xlsx;*.xls|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsEquipmentBoardBusy = true;
        EquipmentBoardStatus = "Reading build sheet…";
        try
        {
            var import = await Task.Run(
                () => new EquipmentBuildSheetImporter().Read(dialog.FileName));
            if (InventoryClientOptions.Count == 0)
            {
                var clients = await Task.Run(
                    () => _repository.GetInventoryClients());
                RefreshInventoryClientOptions(clients);
            }

            ApplyEquipmentBuildSheetImport(import);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or ExcelReaderException
                or NotSupportedException
                or ArgumentException)
        {
            EquipmentBoardStatus = $"Build sheet import failed: {ex.Message}";
            _dialogService.Error("Import build sheet", EquipmentBoardStatus);
        }
        finally
        {
            IsEquipmentBoardBusy = false;
        }
    }

    internal void ApplyEquipmentBuildSheetImport(
        EquipmentBuildSheetImport import)
    {
        ArgumentNullException.ThrowIfNull(import);

        BeginNewEquipment();
        EquipmentDeviceType = import.DeviceType;
        EquipmentName = string.IsNullOrWhiteSpace(import.MachineName)
            ? string.IsNullOrWhiteSpace(import.Model)
                ? import.Machine
                : import.Model
            : import.MachineName;
        EquipmentSerialNumber = import.SerialNumber;
        EquipmentPartNumber = import.PartNumber;
        EquipmentModel = import.Model;

        var warnings = new List<string>();
        var client = EquipmentBuildSheetImporter.FindClient(
            import.Customer,
            InventoryClientOptions);
        EquipmentClient = client;
        if (!string.IsNullOrWhiteSpace(import.Customer) && client is null)
        {
            warnings.Add($"customer “{import.Customer}”");
        }

        var clientUser = EquipmentBuildSheetImporter.FindClientUser(
            import,
            client);
        EquipmentClientUser = clientUser;
        EquipmentLocationName = string.Empty;
        if ((!string.IsNullOrWhiteSpace(import.EndUser)
             || !string.IsNullOrWhiteSpace(import.EmailAddress))
            && clientUser is null)
        {
            warnings.Add("end user");
        }

        EquipmentNotes = string.Empty;
        var warningText = warnings.Count == 0
            ? string.Empty
            : $" Unmatched: {string.Join("; ", warnings)}. You can choose them before saving.";
        EquipmentBoardStatus =
            $"Imported {import.SourceFileName}. Review the new device, then save when ready."
            + warningText;
    }

    private void BeginNewEquipment()
    {
        SelectedEquipment = null;
        _isNewEquipment = true;
        EquipmentAssetTag = string.Empty;
        EquipmentDeviceType = "Desktop";
        EquipmentName = string.Empty;
        EquipmentSerialNumber = string.Empty;
        EquipmentPartNumber = string.Empty;
        EquipmentIpAddress = string.Empty;
        EquipmentManufacturer = string.Empty;
        EquipmentModel = string.Empty;
        EquipmentAnyDeskNumber = string.Empty;
        EquipmentAnyDeskPassword = string.Empty;
        ShowEquipmentAnyDeskPassword = false;
        EquipmentClient = null;
        EquipmentClientUser = null;
        EquipmentLocationName = string.Empty;
        EquipmentNotes = string.Empty;
        EquipmentAssignmentHistory.Clear();
        IsEquipmentEditorVisible = true;
        OnPropertyChanged(nameof(EquipmentEditorTitle));
        OnPropertyChanged(nameof(EquipmentAssignmentLabel));
        SaveEquipmentCommand.RaiseCanExecuteChanged();
        ArchiveEquipmentCommand.RaiseCanExecuteChanged();
    }

    private void LoadEquipmentEditor(EquipmentItem item)
    {
        _isNewEquipment = false;
        EquipmentAssetTag = item.AssetTag;
        EquipmentDeviceType = item.DeviceType;
        EquipmentName = item.Name;
        EquipmentSerialNumber = item.SerialNumber;
        EquipmentPartNumber = item.PartNumber;
        EquipmentIpAddress = item.IpAddress;
        EquipmentManufacturer = item.Manufacturer;
        EquipmentModel = item.Model;
        EquipmentAnyDeskNumber = item.AnyDeskNumber;
        EquipmentAnyDeskPassword = item.AnyDeskPassword;
        ShowEquipmentAnyDeskPassword = false;
        EquipmentClient = InventoryClientOptions.FirstOrDefault(client =>
            client.ClientId == item.ClientId);
        EquipmentClientUser = InventoryClientUserOptions.FirstOrDefault(user =>
            user.ClientUserId == item.ClientUserId);
        EquipmentLocationName = item.LocationName;
        EquipmentNotes = item.Notes;
        IsEquipmentEditorVisible = true;
        OnPropertyChanged(nameof(EquipmentEditorTitle));
        OnPropertyChanged(nameof(EquipmentAssignmentLabel));
        SaveEquipmentCommand.RaiseCanExecuteChanged();
        ArchiveEquipmentCommand.RaiseCanExecuteChanged();
        _ = RefreshEquipmentAssignmentHistoryAsync(item.EquipmentId);
    }

    private void CloseEquipmentEditor()
    {
        _isNewEquipment = false;
        ShowEquipmentAnyDeskPassword = false;
        SelectedEquipment = null;
        IsEquipmentEditorVisible = false;
        OnPropertyChanged(nameof(EquipmentEditorTitle));
        OnPropertyChanged(nameof(EquipmentAssignmentLabel));
        SaveEquipmentCommand.RaiseCanExecuteChanged();
        ArchiveEquipmentCommand.RaiseCanExecuteChanged();
    }

    private bool CanEditEquipmentRecords() =>
        CanAccessEquipmentBoard
        && !IsEquipmentBoardBusy
        && CurrentSection.Equals(
            "Inventory",
            StringComparison.Ordinal);

    private bool CanSaveEquipment() =>
        CanEditEquipmentRecords()
        && IsEquipmentEditorVisible
        && !string.IsNullOrWhiteSpace(EquipmentDeviceType)
        && !string.IsNullOrWhiteSpace(EquipmentName);

    private async Task SaveEquipmentAsync()
    {
        if (!CanSaveEquipment())
        {
            return;
        }

        IsEquipmentBoardBusy = true;
        EquipmentBoardStatus = _isNewEquipment
            ? "Adding equipment to Stock…"
            : "Saving equipment details…";
        try
        {
            var source = _isNewEquipment ? null : SelectedEquipment;
            var record = new EquipmentItem
            {
                EquipmentId = source?.EquipmentId ?? 0,
                AssetTag = EquipmentAssetTag.Trim(),
                DeviceType = EquipmentDeviceType.Trim(),
                Name = EquipmentName.Trim(),
                SerialNumber = EquipmentSerialNumber.Trim(),
                PartNumber = EquipmentPartNumber.Trim(),
                IpAddress = EquipmentIpAddress.Trim(),
                Manufacturer = EquipmentManufacturer.Trim(),
                Model = EquipmentModel.Trim(),
                AnyDeskNumber = EquipmentSupportsAnyDesk
                    ? EquipmentAnyDeskNumber.Trim()
                    : string.Empty,
                AnyDeskPassword = EquipmentSupportsAnyDesk
                    ? EquipmentAnyDeskPassword
                    : string.Empty,
                ClientId = EquipmentClient?.ClientId,
                ClientName = EquipmentClient?.Name ?? string.Empty,
                ClientUserId = EquipmentClientUser?.ClientUserId,
                ClientUserDisplayName =
                    EquipmentClientUser?.DisplayName ?? string.Empty,
                ClientUserEmail = EquipmentClientUser?.Email ?? string.Empty,
                LocationName = EquipmentLocationName.Trim(),
                Notes = EquipmentNotes.Trim(),
                WorkflowStage = source?.WorkflowStage ?? EquipmentWorkflowStages.Stock,
                AssignedToLoginName = source?.AssignedToLoginName ?? string.Empty,
                AssignedToDisplayName = source?.AssignedToDisplayName ?? string.Empty,
                SortOrder = source?.SortOrder ?? 0,
                RowVersion = source?.RowVersion
            };
            var saved = await Task.Run(() => _repository.SaveEquipment(record));
            IsEquipmentBoardBusy = false;
            await RefreshEquipmentBoardAsync(saved.EquipmentId);
            EquipmentBoardStatus = $"Saved {saved.DeviceType}: {saved.Name}.";
        }
        catch (Exception ex) when (
            ex is SqlException
                or InvalidOperationException
                or TimeoutException
                or ArgumentException)
        {
            EquipmentBoardStatus = $"Equipment save failed: {ex.Message}";
            _dialogService.Error("Inventory", EquipmentBoardStatus);
        }
        finally
        {
            IsEquipmentBoardBusy = false;
        }
    }

    private async Task ArchiveEquipmentAsync()
    {
        var equipment = SelectedEquipment;
        if (equipment is null
            || !_dialogService.Confirm(
                "Archive Equipment",
                $"Remove {equipment.Name} from the active equipment board? "
                + "Its record will be archived, not permanently deleted.",
                "Archive",
                "Cancel"))
        {
            return;
        }

        IsEquipmentBoardBusy = true;
        EquipmentBoardStatus = $"Archiving {equipment.Name}…";
        try
        {
            await Task.Run(() => _repository.ArchiveEquipment(equipment));
            CloseEquipmentEditor();
            IsEquipmentBoardBusy = false;
            await RefreshEquipmentBoardAsync();
            EquipmentBoardStatus = $"Archived {equipment.Name}.";
        }
        catch (Exception ex) when (
            ex is SqlException
                or InvalidOperationException
                or TimeoutException)
        {
            EquipmentBoardStatus = $"Equipment archive failed: {ex.Message}";
            _dialogService.Error("Inventory", EquipmentBoardStatus);
        }
        finally
        {
            IsEquipmentBoardBusy = false;
        }
    }

    public async Task AssignEquipmentAsync(
        EquipmentItem equipment,
        EquipmentLane targetLane,
        int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(targetLane);

        if (!CanAccessEquipmentBoard
            || IsEquipmentBoardBusy)
        {
            return;
        }

        IsEquipmentBoardBusy = true;
        EquipmentBoardStatus =
            $"Moving {equipment.Name} to {targetLane.Title}…";
        try
        {
            var result = await Task.Run(() =>
            {
                var updated = _repository.MoveEquipment(
                    equipment,
                    targetLane.IsStock
                        ? null
                        : targetLane.AssignedToLoginName,
                    targetLane.WorkflowStage,
                    targetIndex);
                var mappings = _repository.GetWhdUserMappings();
                return (updated, mappings);
            });
            RebuildEquipmentLanes(
                result.mappings,
                result.updated);
            SelectedEquipment = null;
            EquipmentBoardStatus =
                $"Moved {equipment.Name} to {targetLane.Title}.";
        }
        catch (Exception ex) when (
            ex is SqlException
                or InvalidOperationException
                or TimeoutException
                or ArgumentException)
        {
            EquipmentBoardStatus = $"Equipment assignment failed: {ex.Message}";
            _dialogService.Error("Inventory", EquipmentBoardStatus);
            IsEquipmentBoardBusy = false;
            await RefreshEquipmentBoardAsync(equipment.EquipmentId);
        }
        finally
        {
            IsEquipmentBoardBusy = false;
        }
    }

    private bool SetEquipmentEditorProperty(
        ref string field,
        string? value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value ?? string.Empty, propertyName))
        {
            SaveEquipmentCommand?.RaiseCanExecuteChanged();
            return true;
        }

        return false;
    }

    private void RefreshEquipmentSearch()
    {
        foreach (var lane in AllEquipmentLanes)
        {
            var view = CollectionViewSource.GetDefaultView(lane.Items);
            view.Filter = MatchesEquipmentSearch;
            view.Refresh();
        }
    }

    private bool MatchesEquipmentSearch(object value)
    {
        if (value is not EquipmentItem item)
        {
            return false;
        }

        var query = EquipmentSearchText.Trim();
        return query.Length == 0
            || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.AssetTag.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.DeviceType.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.SerialNumber.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.PartNumber.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.IpAddress.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Manufacturer.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Model.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.ClientName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.ClientUserDisplayName.Contains(
                query,
                StringComparison.OrdinalIgnoreCase)
            || item.ClientUserEmail.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.LocationName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildInventoryEquipmentRegistry(
        IReadOnlyList<EquipmentItem> equipment)
    {
        InventoryEquipmentItems.Clear();
        foreach (var item in equipment
                     .OrderBy(static item => item.Name)
                     .ThenBy(static item => item.AssetTag)
                     .ThenBy(static item => item.EquipmentId))
        {
            InventoryEquipmentItems.Add(item);
        }

        ReplaceInventoryFilterOptions(
            InventoryDeviceTypeFilterOptions,
            EquipmentInventoryFilter.AllDeviceTypes,
            equipment.Select(static item => item.DeviceType));
        ReplaceInventoryFilterOptions(
            InventoryClientFilterOptions,
            EquipmentInventoryFilter.AllClients,
            equipment.Select(static item => item.ClientName));
        ReplaceInventoryFilterOptions(
            InventoryTechnicianFilterOptions,
            EquipmentInventoryFilter.AllTechnicians,
            equipment.Select(static item => item.TechnicianLabel));

        if (!InventoryDeviceTypeFilterOptions.Contains(
                InventoryDeviceTypeFilter,
                StringComparer.OrdinalIgnoreCase))
        {
            _inventoryDeviceTypeFilter =
                EquipmentInventoryFilter.AllDeviceTypes;
            OnPropertyChanged(nameof(InventoryDeviceTypeFilter));
        }

        if (!InventoryClientFilterOptions.Contains(
                InventoryClientFilter,
                StringComparer.OrdinalIgnoreCase))
        {
            _inventoryClientFilter = EquipmentInventoryFilter.AllClients;
            OnPropertyChanged(nameof(InventoryClientFilter));
        }

        if (!InventoryTechnicianFilterOptions.Contains(
                InventoryTechnicianFilter,
                StringComparer.OrdinalIgnoreCase))
        {
            _inventoryTechnicianFilter =
                EquipmentInventoryFilter.AllTechnicians;
            OnPropertyChanged(nameof(InventoryTechnicianFilter));
        }

        RefreshInventoryEquipmentFilter();
    }

    private static void ReplaceInventoryFilterOptions(
        ObservableCollection<string> options,
        string allOption,
        IEnumerable<string> values)
    {
        options.Clear();
        options.Add(allOption);
        foreach (var value in values
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static value => value))
        {
            options.Add(value);
        }
    }

    private void RefreshInventoryEquipmentFilter()
    {
        var view = CollectionViewSource.GetDefaultView(
            InventoryEquipmentItems);
        view.Filter = value =>
            value is EquipmentItem item
            && EquipmentInventoryFilter.Matches(
                item,
                InventoryEquipmentSearchText,
                InventoryStatusFilter,
                InventoryDeviceTypeFilter,
                InventoryClientFilter,
                InventoryTechnicianFilter,
                InventoryStockOnly);
        view.Refresh();

        OnPropertyChanged(nameof(InventoryEquipmentCountLabel));
        OnPropertyChanged(nameof(HasInventoryEquipmentResults));
        ClearInventoryEquipmentFiltersCommand?.RaiseCanExecuteChanged();
    }

    private int GetVisibleInventoryEquipmentCount() =>
        CollectionViewSource.GetDefaultView(InventoryEquipmentItems)
            .Cast<object>()
            .Count();

    private bool HasActiveInventoryEquipmentFilters() =>
        !string.IsNullOrWhiteSpace(InventoryEquipmentSearchText)
        || !InventoryStatusFilter.Equals(
            EquipmentInventoryFilter.AllStatuses,
            StringComparison.Ordinal)
        || !InventoryDeviceTypeFilter.Equals(
            EquipmentInventoryFilter.AllDeviceTypes,
            StringComparison.Ordinal)
        || !InventoryClientFilter.Equals(
            EquipmentInventoryFilter.AllClients,
            StringComparison.Ordinal)
        || !InventoryTechnicianFilter.Equals(
            EquipmentInventoryFilter.AllTechnicians,
            StringComparison.Ordinal)
        || InventoryStockOnly;

    private void ClearInventoryEquipmentFilters()
    {
        _inventoryEquipmentSearchText = string.Empty;
        _inventoryStatusFilter = EquipmentInventoryFilter.AllStatuses;
        _inventoryDeviceTypeFilter =
            EquipmentInventoryFilter.AllDeviceTypes;
        _inventoryClientFilter = EquipmentInventoryFilter.AllClients;
        _inventoryTechnicianFilter =
            EquipmentInventoryFilter.AllTechnicians;
        _inventoryStockOnly = false;
        OnPropertyChanged(nameof(InventoryEquipmentSearchText));
        OnPropertyChanged(nameof(InventoryStatusFilter));
        OnPropertyChanged(nameof(InventoryDeviceTypeFilter));
        OnPropertyChanged(nameof(InventoryClientFilter));
        OnPropertyChanged(nameof(InventoryTechnicianFilter));
        OnPropertyChanged(nameof(InventoryStockOnly));
        RefreshInventoryEquipmentFilter();
    }

    private void CopyEquipmentDetails()
    {
        var lines = new List<string>();
        AddEquipmentCopyLine(lines, "Name", EquipmentName);
        AddEquipmentCopyLine(lines, "Device type", EquipmentDeviceType);
        AddEquipmentCopyLine(lines, "Asset tag", EquipmentAssetTag);
        AddEquipmentCopyLine(lines, "Serial number", EquipmentSerialNumber);
        AddEquipmentCopyLine(lines, "Part number", EquipmentPartNumber);
        AddEquipmentCopyLine(lines, "Manufacturer", EquipmentManufacturer);
        AddEquipmentCopyLine(lines, "Model", EquipmentModel);
        AddEquipmentCopyLine(lines, "IP address", EquipmentIpAddress);
        AddEquipmentCopyLine(lines, "AnyDesk number", EquipmentAnyDeskNumber);
        AddEquipmentCopyLine(lines, "Client", EquipmentClient?.Name);
        AddEquipmentCopyLine(
            lines,
            "Client user",
            EquipmentClientUser?.DisplayLabel);
        AddEquipmentCopyLine(lines, "Site / room / desk", EquipmentLocationName);
        AddEquipmentCopyLine(
            lines,
            "Technician",
            SelectedEquipment?.TechnicianLabel);
        AddEquipmentCopyLine(
            lines,
            "Status",
            SelectedEquipment?.InventoryStatusLabel);
        AddEquipmentCopyLine(lines, "Notes", EquipmentNotes);

        if (lines.Count == 0)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(
                string.Join(Environment.NewLine, lines));
            StatusMessage = $"Copied equipment details for {EquipmentName}.";
        }
        catch (ExternalException ex)
        {
            _dialogService.Error(
                "Copy equipment details",
                $"Windows could not access the clipboard: {ex.Message}");
        }
    }

    private bool CanLaunchAnyDesk(object? parameter)
    {
        var address = parameter is EquipmentItem equipment
            ? equipment.AnyDeskNumber
            : EquipmentAnyDeskNumber;
        return !string.IsNullOrWhiteSpace(address);
    }

    private void LaunchAnyDesk(object? parameter)
    {
        var equipment = parameter as EquipmentItem;
        var address = equipment?.AnyDeskNumber
            ?? EquipmentAnyDeskNumber;
        var password = equipment?.AnyDeskPassword
            ?? EquipmentAnyDeskPassword;
        var equipmentName = equipment?.Name
            ?? EquipmentName;
        var result = AnyDeskLauncher.Launch(
            address,
            password);
        if (!result.Succeeded)
        {
            var message =
                result.ErrorMessage
                ?? "AnyDesk could not be started.";
            StatusMessage = $"AnyDesk launch failed: {message}";
            _dialogService.Error("Launch AnyDesk", message);
            return;
        }

        var target = string.IsNullOrWhiteSpace(equipmentName)
            ? AnyDeskLauncher.NormalizeAddress(address)
            : equipmentName.Trim();
        StatusMessage = result.PasswordSubmitted
            ? $"Opening AnyDesk for {target} and submitting the unattended-access password."
            : $"Opening AnyDesk for {target}. No unattended-access password was stored.";
    }

    private static void AddEquipmentCopyLine(
        ICollection<string> lines,
        string label,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"{label}: {value.Trim()}");
        }
    }

    private void RefreshInventoryClientOptions(
        IReadOnlyList<InventoryClient> clients)
    {
        var selectedClientId = EquipmentClient?.ClientId;
        InventoryClientOptions.Clear();
        foreach (var client in clients)
        {
            InventoryClientOptions.Add(client);
        }

        EquipmentClient = selectedClientId is > 0
            ? InventoryClientOptions.FirstOrDefault(client =>
                client.ClientId == selectedClientId)
            : null;
    }

    private void RefreshInventoryClientUserOptions()
    {
        var selectedUserId = EquipmentClientUser?.ClientUserId;
        InventoryClientUserOptions.Clear();
        if (EquipmentClient is not null)
        {
            foreach (var user in EquipmentClient.Users
                         .Where(static user => user.IsActive)
                         .OrderBy(static user => user.DisplayName))
            {
                InventoryClientUserOptions.Add(user);
            }
        }

        _equipmentClientUser = selectedUserId is > 0
            ? InventoryClientUserOptions.FirstOrDefault(user =>
                user.ClientUserId == selectedUserId)
            : null;
        OnPropertyChanged(nameof(EquipmentClientUser));
    }

    private async Task RefreshEquipmentAssignmentHistoryAsync(long equipmentId)
    {
        EquipmentAssignmentHistory.Clear();
        if (equipmentId <= 0)
        {
            return;
        }

        try
        {
            var history = await Task.Run(() =>
                _repository.GetEquipmentAssignmentHistory(equipmentId));
            if (SelectedEquipment?.EquipmentId != equipmentId)
            {
                return;
            }

            foreach (var entry in history)
            {
                EquipmentAssignmentHistory.Add(entry);
            }
        }
        catch (Exception ex) when (
            ex is SqlException
                or InvalidOperationException
                or TimeoutException)
        {
            EquipmentBoardStatus =
                $"Equipment history could not be loaded: {ex.Message}";
        }
    }

    private IEnumerable<EquipmentLane> AllEquipmentLanes =>
        EquipmentLanes.Concat(DeploymentLanes);
}
