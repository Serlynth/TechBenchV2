using System.Collections.ObjectModel;
using System.Windows.Data;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed class EquipmentBoardDemoViewModel : ObservableObject
{
    private long _nextEquipmentId = 100;
    private EquipmentItem? _selectedEquipment;
    private bool _isEquipmentEditorVisible;
    private bool _isNewEquipment;
    private string _equipmentAssetTag = string.Empty;
    private string _equipmentDeviceType = "Desktop";
    private string _equipmentName = string.Empty;
    private string _equipmentSerialNumber = string.Empty;
    private string _equipmentPartNumber = string.Empty;
    private string _equipmentIpAddress = string.Empty;
    private string _equipmentManufacturer = string.Empty;
    private string _equipmentModel = string.Empty;
    private InventoryClient? _equipmentClient;
    private InventoryClientUser? _equipmentClientUser;
    private string _equipmentLocationName = string.Empty;
    private string _equipmentNotes = string.Empty;
    private string _equipmentSearchText = string.Empty;
    private string _equipmentBoardStatus =
        "Demo data is loaded. Drag cards between Stock, technicians, and Deployment.";

    public EquipmentBoardDemoViewModel()
    {
        NewEquipmentCommand = new RelayCommand(_ => BeginNewEquipment());
        SaveEquipmentCommand = new RelayCommand(
            _ => SaveEquipment(),
            _ => CanSaveEquipment());
        ArchiveEquipmentCommand = new RelayCommand(
            _ => ArchiveEquipment(),
            _ => !_isNewEquipment && SelectedEquipment is not null);
        CancelEquipmentEditCommand = new RelayCommand(_ => CloseEquipmentEditor());
        ClearEquipmentSearchCommand = new RelayCommand(
            _ => EquipmentSearchText = string.Empty,
            _ => !string.IsNullOrWhiteSpace(EquipmentSearchText));
        ResetDemoCommand = new RelayCommand(_ => LoadSampleData());
        LoadSampleClients();
        LoadSampleData();
    }

    public ObservableCollection<EquipmentLane> EquipmentLanes { get; } = new();
    public ObservableCollection<EquipmentLane> DeploymentLanes { get; } = new();
    public ObservableCollection<InventoryClient> ClientOptions { get; } = new();
    public ObservableCollection<InventoryClientUser> ClientUserOptions { get; } = new();
    public ObservableCollection<EquipmentAssignmentHistoryEntry> AssignmentHistory { get; } = new();

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

    public RelayCommand NewEquipmentCommand { get; }
    public RelayCommand SaveEquipmentCommand { get; }
    public RelayCommand ArchiveEquipmentCommand { get; }
    public RelayCommand CancelEquipmentEditCommand { get; }
    public RelayCommand ClearEquipmentSearchCommand { get; }
    public RelayCommand ResetDemoCommand { get; }

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
        private set => SetProperty(ref _isEquipmentEditorVisible, value);
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
        set => SetEditorProperty(ref _equipmentDeviceType, value);
    }

    public string EquipmentAssetTag
    {
        get => _equipmentAssetTag;
        set => SetEditorProperty(ref _equipmentAssetTag, value);
    }

    public string EquipmentName
    {
        get => _equipmentName;
        set => SetEditorProperty(ref _equipmentName, value);
    }

    public string EquipmentSerialNumber
    {
        get => _equipmentSerialNumber;
        set => SetEditorProperty(ref _equipmentSerialNumber, value);
    }

    public string EquipmentPartNumber
    {
        get => _equipmentPartNumber;
        set => SetEditorProperty(ref _equipmentPartNumber, value);
    }

    public string EquipmentIpAddress
    {
        get => _equipmentIpAddress;
        set => SetEditorProperty(ref _equipmentIpAddress, value);
    }

    public string EquipmentManufacturer
    {
        get => _equipmentManufacturer;
        set => SetEditorProperty(ref _equipmentManufacturer, value);
    }

    public string EquipmentModel
    {
        get => _equipmentModel;
        set => SetEditorProperty(ref _equipmentModel, value);
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

            RefreshClientUserOptions();
            EquipmentLocationName = value?.PrimaryLocation ?? string.Empty;
            SaveEquipmentCommand.RaiseCanExecuteChanged();
        }
    }

    public InventoryClientUser? EquipmentClientUser
    {
        get => _equipmentClientUser;
        set
        {
            if (SetProperty(ref _equipmentClientUser, value))
            {
                if (!string.IsNullOrWhiteSpace(value?.LocationName))
                {
                    EquipmentLocationName = value.LocationName;
                }
                SaveEquipmentCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string EquipmentLocationName
    {
        get => _equipmentLocationName;
        set => SetEditorProperty(ref _equipmentLocationName, value);
    }

    public string EquipmentNotes
    {
        get => _equipmentNotes;
        set => SetEditorProperty(ref _equipmentNotes, value);
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
                ClearEquipmentSearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string EquipmentDeviceCountLabel
    {
        get
        {
            var count = AllEquipmentLanes.Sum(static lane => lane.Items.Count);
            return count == 1 ? "1 device tracked" : $"{count} devices tracked";
        }
    }

    public string EquipmentDeploymentCountLabel
    {
        get
        {
            var count = DeploymentLanes.Sum(static lane => lane.Items.Count);
            return count == 1 ? "1 item" : $"{count} items";
        }
    }

    public void AssignEquipment(
        EquipmentItem equipment,
        EquipmentLane targetLane,
        int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(targetLane);

        var sourceLane = AllEquipmentLanes.FirstOrDefault(lane =>
            lane.Items.Any(item => item.EquipmentId == equipment.EquipmentId));
        if (sourceLane is null)
        {
            return;
        }

        var sourceIndex = sourceLane.Items.IndexOf(equipment);
        if (sourceIndex < 0)
        {
            return;
        }

        targetIndex = Math.Clamp(targetIndex, 0, targetLane.Items.Count);
        if (ReferenceEquals(sourceLane, targetLane))
        {
            if (sourceIndex < targetIndex)
            {
                targetIndex--;
            }

            if (sourceIndex == targetIndex)
            {
                return;
            }

            sourceLane.Items.Move(sourceIndex, targetIndex);
            SelectedEquipment = null;
            EquipmentBoardStatus =
                $"Demo: moved {equipment.Name} to priority {targetIndex + 1} in {targetLane.Title}. This change is not saved.";
            return;
        }

        var moved = CopyEquipment(
            equipment,
            workflowStage: targetLane.WorkflowStage,
            assignedToLoginName: targetLane.IsStock
                ? string.Empty
                : targetLane.AssignedToLoginName,
            assignedToDisplayName: targetLane.IsStock
                || string.IsNullOrWhiteSpace(targetLane.AssignedToLoginName)
                    ? string.Empty
                    : targetLane.Title,
            assignedAtUtc: targetLane.IsStock
                ? null
                : string.Equals(
                    equipment.AssignedToLoginName,
                    targetLane.AssignedToLoginName,
                    StringComparison.OrdinalIgnoreCase)
                    ? equipment.AssignedAtUtc
                    : DateTime.UtcNow);
        sourceLane.Items.Remove(equipment);
        targetLane.Items.Insert(targetIndex, moved);
        SelectedEquipment = null;
        EquipmentBoardStatus =
            $"Demo: assigned {moved.Name} to {targetLane.Title} at priority {targetIndex + 1}. This change is not saved.";
        OnPropertyChanged(nameof(EquipmentDeviceCountLabel));
        OnPropertyChanged(nameof(EquipmentDeploymentCountLabel));
    }

    private void LoadSampleClients()
    {
        ClientOptions.Clear();

        var northwind = new InventoryClient
        {
            ClientId = 101,
            Name = "Northwind Accounting",
            PrimaryLocation = "Main Office"
        };
        northwind.Users.Add(new InventoryClientUser
        {
            ClientUserId = 1001,
            ClientId = northwind.ClientId,
            DisplayName = "Morgan Bennett",
            RoleDepartment = "Office Manager",
            Email = "morgan.bennett@example.test",
            Phone = "(202) 555-0101",
            LocationName = "Main Office"
        });
        northwind.Users.Add(new InventoryClientUser
        {
            ClientUserId = 1002,
            ClientId = northwind.ClientId,
            DisplayName = "Riley Morris",
            RoleDepartment = "Accounting",
            Email = "riley.morris@example.test",
            Phone = "(202) 555-0102",
            LocationName = "Main Office"
        });

        var contoso = new InventoryClient
        {
            ClientId = 102,
            Name = "Contoso Academy",
            PrimaryLocation = "Main Campus"
        };
        contoso.Users.Add(new InventoryClientUser
        {
            ClientUserId = 2001,
            ClientId = contoso.ClientId,
            DisplayName = "Avery Jacobs",
            RoleDepartment = "Technology",
            Email = "technology@example.test",
            Phone = "(202) 555-0201",
            LocationName = "Main Campus"
        });
        contoso.Users.Add(new InventoryClientUser
        {
            ClientUserId = 2002,
            ClientId = contoso.ClientId,
            DisplayName = "Front Office",
            RoleDepartment = "Shared Workstation",
            Email = "frontoffice@example.test",
            Phone = "(202) 555-0202",
            LocationName = "Administration Building"
        });

        var fabrikam = new InventoryClient
        {
            ClientId = 103,
            Name = "Fabrikam Financial",
            PrimaryLocation = "Headquarters"
        };
        fabrikam.Users.Add(new InventoryClientUser
        {
            ClientUserId = 3001,
            ClientId = fabrikam.ClientId,
            DisplayName = "Sydney Morgan",
            RoleDepartment = "Executive Office",
            Email = "sydney.morgan@example.test",
            Phone = "(202) 555-0301",
            LocationName = "Headquarters"
        });

        var adventureWorks = new InventoryClient
        {
            ClientId = 104,
            Name = "Adventure Works",
            PrimaryLocation = "Main Office"
        };
        adventureWorks.Users.Add(new InventoryClientUser
        {
            ClientUserId = 4001,
            ClientId = adventureWorks.ClientId,
            DisplayName = "Reception",
            RoleDepartment = "Shared Phone",
            Email = "reception@example.test",
            Phone = "(202) 555-0401",
            LocationName = "Main Office"
        });

        var communityCenter = new InventoryClient
        {
            ClientId = 105,
            Name = "Community Center",
            PrimaryLocation = "Main Building"
        };
        communityCenter.Users.Add(new InventoryClientUser
        {
            ClientUserId = 5001,
            ClientId = communityCenter.ClientId,
            DisplayName = "Jamie Brooks",
            RoleDepartment = "Front Desk",
            Email = "jamie.brooks@example.test",
            Phone = "(202) 555-0501",
            LocationName = "Main Building"
        });

        foreach (var client in new[]
                 {
                     northwind,
                     contoso,
                     fabrikam,
                     adventureWorks,
                     communityCenter
                 })
        {
            ClientOptions.Add(client);
        }
    }

    private void LoadSampleData()
    {
        CloseEquipmentEditor();
        EquipmentLanes.Clear();
        DeploymentLanes.Clear();
        _nextEquipmentId = 100;

        var stock = new EquipmentLane(
            "Stock Room",
            null,
            EquipmentWorkflowStages.Stock);
        var alex = new EquipmentLane("Alex Morgan", @"DEMO\amorgan");
        var jordan = new EquipmentLane("Jordan Lee", @"DEMO\jlee");
        var taylor = new EquipmentLane("Taylor Rivera", @"DEMO\trivera");
        var casey = new EquipmentLane("Casey Patel", @"DEMO\cpatel");
        EquipmentLanes.Add(stock);
        EquipmentLanes.Add(alex);
        EquipmentLanes.Add(jordan);
        EquipmentLanes.Add(taylor);
        EquipmentLanes.Add(casey);

        var unassignedDeployment = new EquipmentLane(
            "Unassigned",
            null,
            EquipmentWorkflowStages.Deployment);
        var alexDeployment = new EquipmentLane(
            alex.Title,
            alex.AssignedToLoginName,
            EquipmentWorkflowStages.Deployment);
        var jordanDeployment = new EquipmentLane(
            jordan.Title,
            jordan.AssignedToLoginName,
            EquipmentWorkflowStages.Deployment);
        var taylorDeployment = new EquipmentLane(
            taylor.Title,
            taylor.AssignedToLoginName,
            EquipmentWorkflowStages.Deployment);
        var caseyDeployment = new EquipmentLane(
            casey.Title,
            casey.AssignedToLoginName,
            EquipmentWorkflowStages.Deployment);
        DeploymentLanes.Add(unassignedDeployment);
        DeploymentLanes.Add(alexDeployment);
        DeploymentLanes.Add(jordanDeployment);
        DeploymentLanes.Add(taylorDeployment);
        DeploymentLanes.Add(caseyDeployment);

        stock.Items.Add(CreateSample(
            "Laptop", "New Sales Laptop", "Dell", "Latitude 5550",
            "DL-5550-01842", "LAT5550-I7", string.Empty, string.Empty,
            "New in box; needs standard setup."));
        stock.Items.Add(CreateSample(
            "Firewall", "Replacement Firebox", "WatchGuard", "Firebox M390",
            "WG-M390-88213", "WGM390", "192.0.2.10", string.Empty,
            "Spare appliance for emergency replacement."));
        stock.Items.Add(CreateSample(
            "Switch", "48-Port PoE Switch", "Aruba", "2930F",
            "CN91KZ1042", "JL256A", "192.0.2.11", string.Empty,
            "Stock; firmware baseline complete."));
        stock.Items.Add(CreateSample(
            "Access Point", "WiFi 6 Access Point", "Aruba", "AP-515",
            "CNKJL77291", "Q9H63A", string.Empty, string.Empty,
            "Awaiting assignment."));

        alex.Items.Add(CreateSample(
            "Desktop", "Accounting Workstation", "HP", "ProDesk 600 G6",
            "2UA1148Q7Z", "1D2W3UT", "192.0.2.21", "Northwind Accounting",
            "Replacing failed front-office desktop.", alex));
        alex.Items.Add(CreateSample(
            "Printer", "Front Office MFP", "Brother", "MFC-L8900CDW",
            "U64912M3F911", "MFCL8900CDW", "192.0.2.22", "Northwind Accounting",
            "Configure scan-to-email.", alex));

        jordan.Items.Add(CreateSample(
            "Server", "File Server Refresh", "Dell", "PowerEdge T550",
            "8J7P3X3", "PET550", "192.0.2.31", "Contoso Academy",
            "RAID configured; migrating data.", jordan));
        jordan.Items.Add(CreateSample(
            "UPS", "Server Room UPS", "APC", "Smart-UPS 2200",
            "AS2216150912", "SMT2200RM2U", "192.0.2.32", "Contoso Academy",
            "Replace batteries and install network card.", jordan));

        taylor.Items.Add(CreateSample(
            "Laptop", "Executive Laptop", "Lenovo", "ThinkPad T14 Gen 5",
            "PF5M82Q1", "21MLCTO1WW", "192.0.2.41", "Fabrikam Financial",
            "BitLocker and Microsoft 365 setup.", taylor));

        casey.Items.Add(CreateSample(
            "Phone", "Reception Phone", "Yealink", "T54W",
            "816210090128", "SIP-T54W", "192.0.2.51", "Adventure Works",
            "Provision extension and test transfer keys.", casey));

        alexDeployment.Items.Add(CreateSample(
            "Desktop", "Ready Front Desk PC", "Dell", "OptiPlex 7020",
            "DX9K7P2", "OPT7020", "192.0.2.61", "Community Center",
            "Configuration complete; ready for onsite deployment.",
            alex,
            EquipmentWorkflowStages.Deployment,
            "Jamie Brooks"));

        EquipmentBoardStatus =
            "Demo reset: prioritize work above, then drag completed equipment into Deployment below.";
        OnPropertyChanged(nameof(EquipmentDeviceCountLabel));
        OnPropertyChanged(nameof(EquipmentDeploymentCountLabel));
        RefreshEquipmentSearch();
    }

    private EquipmentItem CreateSample(
        string deviceType,
        string name,
        string manufacturer,
        string model,
        string serialNumber,
        string partNumber,
        string ipAddress,
        string clientName,
        string notes,
        EquipmentLane? assignedLane = null,
        string? workflowStage = null,
        string? clientUserDisplayName = null)
    {
        var client = ClientOptions.FirstOrDefault(candidate =>
            candidate.Name.Equals(clientName, StringComparison.OrdinalIgnoreCase));
        var clientUser = client?.Users.FirstOrDefault(candidate =>
            candidate.DisplayName.Equals(
                clientUserDisplayName ?? string.Empty,
                StringComparison.OrdinalIgnoreCase));

        return new EquipmentItem
        {
            EquipmentId = _nextEquipmentId++,
            AssetTag = $"TB-{_nextEquipmentId - 100:0000}",
            DeviceType = deviceType,
            Name = name,
            Manufacturer = manufacturer,
            Model = model,
            SerialNumber = serialNumber,
            PartNumber = partNumber,
            IpAddress = ipAddress,
            ClientId = client?.ClientId,
            ClientName = clientName,
            ClientUserId = clientUser?.ClientUserId,
            ClientUserDisplayName = clientUser?.DisplayName ?? string.Empty,
            ClientUserEmail = clientUser?.Email ?? string.Empty,
            LocationName = clientUser?.LocationName ?? client?.PrimaryLocation ?? string.Empty,
            Notes = notes,
            WorkflowStage = workflowStage
                ?? (assignedLane is null
                    ? EquipmentWorkflowStages.Stock
                    : EquipmentWorkflowStages.Assigned),
            AssignedToLoginName = assignedLane?.AssignedToLoginName ?? string.Empty,
            AssignedToDisplayName = assignedLane?.Title ?? string.Empty,
            AssignedAtUtc = assignedLane is null ? null : DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
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
        EquipmentClient = null;
        EquipmentClientUser = null;
        EquipmentLocationName = string.Empty;
        EquipmentNotes = string.Empty;
        IsEquipmentEditorVisible = true;
        RaiseEditorStateChanged();
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
        EquipmentClient = ClientOptions.FirstOrDefault(client =>
            client.ClientId == item.ClientId
            || client.Name.Equals(item.ClientName, StringComparison.OrdinalIgnoreCase));
        EquipmentClientUser = ClientUserOptions.FirstOrDefault(user =>
            user.ClientUserId == item.ClientUserId
            || user.DisplayName.Equals(
                item.ClientUserDisplayName,
                StringComparison.OrdinalIgnoreCase));
        EquipmentLocationName = item.LocationName;
        EquipmentNotes = item.Notes;
        IsEquipmentEditorVisible = true;
        RaiseEditorStateChanged();
    }

    private void CloseEquipmentEditor()
    {
        _isNewEquipment = false;
        SelectedEquipment = null;
        IsEquipmentEditorVisible = false;
        RaiseEditorStateChanged();
    }

    private bool CanSaveEquipment() =>
        IsEquipmentEditorVisible
        && !string.IsNullOrWhiteSpace(EquipmentDeviceType)
        && !string.IsNullOrWhiteSpace(EquipmentName);

    private void SaveEquipment()
    {
        if (!CanSaveEquipment())
        {
            return;
        }

        var source = _isNewEquipment ? null : SelectedEquipment;
        var saved = new EquipmentItem
        {
            EquipmentId = source?.EquipmentId ?? _nextEquipmentId++,
            AssetTag = EquipmentAssetTag.Trim(),
            DeviceType = EquipmentDeviceType.Trim(),
            Name = EquipmentName.Trim(),
            SerialNumber = EquipmentSerialNumber.Trim(),
            PartNumber = EquipmentPartNumber.Trim(),
            IpAddress = EquipmentIpAddress.Trim(),
            Manufacturer = EquipmentManufacturer.Trim(),
            Model = EquipmentModel.Trim(),
            ClientId = EquipmentClient?.ClientId,
            ClientName = EquipmentClient?.Name ?? string.Empty,
            ClientUserId = EquipmentClientUser?.ClientUserId,
            ClientUserDisplayName = EquipmentClientUser?.DisplayName ?? string.Empty,
            ClientUserEmail = EquipmentClientUser?.Email ?? string.Empty,
            LocationName = EquipmentLocationName.Trim(),
            Notes = EquipmentNotes.Trim(),
            WorkflowStage = source?.WorkflowStage ?? EquipmentWorkflowStages.Stock,
            AssignedToLoginName = source?.AssignedToLoginName ?? string.Empty,
            AssignedToDisplayName = source?.AssignedToDisplayName ?? string.Empty,
            AssignedAtUtc = source?.AssignedAtUtc,
            CreatedAtUtc = source?.CreatedAtUtc ?? DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        if (source is null)
        {
            EquipmentLanes[0].Items.Add(saved);
            OnPropertyChanged(nameof(EquipmentDeviceCountLabel));
        }
        else
        {
            ReplaceEquipment(source, saved);
        }

        if (saved.ClientId.HasValue
            && (source?.ClientId != saved.ClientId
                || source?.ClientUserId != saved.ClientUserId
                || !string.Equals(
                    source?.LocationName,
                    saved.LocationName,
                    StringComparison.OrdinalIgnoreCase)))
        {
            AssignmentHistory.Insert(0, new EquipmentAssignmentHistoryEntry
            {
                EquipmentId = saved.EquipmentId,
                ClientId = saved.ClientId,
                ClientUserId = saved.ClientUserId,
                ClientName = saved.ClientName,
                ClientUserDisplayName = saved.ClientUserDisplayName,
                LocationName = saved.LocationName,
                AssignedAtUtc = DateTime.UtcNow,
                Notes = "Local prototype assignment"
            });
        }

        _isNewEquipment = false;
        SelectedEquipment = saved;
        EquipmentBoardStatus =
            $"Demo: saved {saved.DeviceType} “{saved.Name}” for this session only.";
        RaiseEditorStateChanged();
    }

    private void ArchiveEquipment()
    {
        var equipment = SelectedEquipment;
        if (equipment is null)
        {
            return;
        }

        foreach (var lane in AllEquipmentLanes)
        {
            if (lane.Items.Remove(equipment))
            {
                break;
            }
        }

        CloseEquipmentEditor();
        EquipmentBoardStatus =
            $"Demo: removed {equipment.Name} from the board for this session only.";
        OnPropertyChanged(nameof(EquipmentDeviceCountLabel));
        OnPropertyChanged(nameof(EquipmentDeploymentCountLabel));
    }

    private void ReplaceEquipment(EquipmentItem source, EquipmentItem replacement)
    {
        foreach (var lane in AllEquipmentLanes)
        {
            var index = lane.Items.IndexOf(source);
            if (index >= 0)
            {
                lane.Items[index] = replacement;
                return;
            }
        }
    }

    private static EquipmentItem CopyEquipment(
        EquipmentItem source,
        string workflowStage,
        string assignedToLoginName,
        string assignedToDisplayName,
        DateTime? assignedAtUtc) =>
        new()
        {
            EquipmentId = source.EquipmentId,
            AssetTag = source.AssetTag,
            DeviceType = source.DeviceType,
            Name = source.Name,
            SerialNumber = source.SerialNumber,
            PartNumber = source.PartNumber,
            IpAddress = source.IpAddress,
            Manufacturer = source.Manufacturer,
            Model = source.Model,
            ClientId = source.ClientId,
            ClientName = source.ClientName,
            ClientUserId = source.ClientUserId,
            ClientUserDisplayName = source.ClientUserDisplayName,
            ClientUserEmail = source.ClientUserEmail,
            LocationName = source.LocationName,
            Notes = source.Notes,
            WorkflowStage = workflowStage,
            AssignedToLoginName = assignedToLoginName,
            AssignedToDisplayName = assignedToDisplayName,
            SortOrder = source.SortOrder,
            AssignedAtUtc = assignedAtUtc,
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private void RefreshClientUserOptions()
    {
        var previousId = _equipmentClientUser?.ClientUserId;
        ClientUserOptions.Clear();

        if (_equipmentClient is not null)
        {
            foreach (var user in _equipmentClient.Users
                         .Where(static user => user.IsActive)
                         .OrderBy(static user => user.DisplayName))
            {
                ClientUserOptions.Add(user);
            }
        }

        _equipmentClientUser = previousId.HasValue
            ? ClientUserOptions.FirstOrDefault(user => user.ClientUserId == previousId.Value)
            : null;
        OnPropertyChanged(nameof(EquipmentClientUser));
    }

    private void SetEditorProperty(
        ref string field,
        string? value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value ?? string.Empty, propertyName))
        {
            SaveEquipmentCommand.RaiseCanExecuteChanged();
        }
    }

    private void RaiseEditorStateChanged()
    {
        OnPropertyChanged(nameof(EquipmentEditorTitle));
        OnPropertyChanged(nameof(EquipmentAssignmentLabel));
        SaveEquipmentCommand.RaiseCanExecuteChanged();
        ArchiveEquipmentCommand.RaiseCanExecuteChanged();
    }

    private void RefreshEquipmentSearch()
    {
        foreach (var lane in AllEquipmentLanes)
        {
            var view = CollectionViewSource.GetDefaultView(lane.Items);
            view.Filter = item =>
                item is EquipmentItem equipment
                && MatchesEquipmentSearch(equipment);
            view.Refresh();
        }
    }

    private bool MatchesEquipmentSearch(EquipmentItem equipment)
    {
        var query = EquipmentSearchText.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        return new[]
        {
            equipment.Name,
            equipment.DeviceType,
            equipment.AssetTag,
            equipment.SerialNumber,
            equipment.PartNumber,
            equipment.IpAddress,
            equipment.Manufacturer,
            equipment.Model,
            equipment.ClientName,
            equipment.ClientUserDisplayName,
            equipment.ClientUserEmail,
            equipment.LocationName
        }.Any(value =>
            value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
    }

    private IEnumerable<EquipmentLane> AllEquipmentLanes =>
        EquipmentLanes.Concat(DeploymentLanes);
}
