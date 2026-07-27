using System.Collections.ObjectModel;
using System.ComponentModel;

namespace TechBench.Models;

/// <summary>An active item shown on the shared equipment assignment board.</summary>
public sealed class EquipmentItem
{
    public long EquipmentId { get; init; }
    public string AssetTag { get; init; } = string.Empty;
    public string DeviceType { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public string PartNumber { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int? ClientId { get; init; }
    public string ClientName { get; init; } = string.Empty;
    public long? ClientUserId { get; init; }
    public string ClientUserDisplayName { get; init; } = string.Empty;
    public string ClientUserEmail { get; init; } = string.Empty;
    public string LocationName { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public string AssignedToLoginName { get; init; } = string.Empty;
    public string AssignedToDisplayName { get; init; } = string.Empty;
    public string WorkflowStage { get; init; } = EquipmentWorkflowStages.Stock;
    public int SortOrder { get; init; }
    public DateTime? AssignedAtUtc { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public byte[]? RowVersion { get; init; }

    public bool IsInStock => EquipmentWorkflowStages.IsStock(WorkflowStage);
    public bool IsDeployment => EquipmentWorkflowStages.IsDeployment(WorkflowStage);

    public string DeviceGlyph => DeviceType.Trim().ToLowerInvariant() switch
    {
        "desktop" => "\uE211",
        "laptop" => "\uE770",
        "server" => "\uE968",
        "switch" => "\uE8AB",
        "firewall" => "\uE72E",
        "access point" => "\uE701",
        "printer" => "\uE749",
        "ups" => "\uE945",
        "phone" => "\uE717",
        _ => "\uE713"
    };

    public string DeviceBadgeColor => DeviceType.Trim().ToLowerInvariant() switch
    {
        "desktop" => "#2F7DE1",
        "laptop" => "#7C5CFC",
        "server" => "#19A974",
        "switch" => "#00A5B5",
        "firewall" => "#E05260",
        "access point" => "#E89C32",
        "printer" => "#65758B",
        "ups" => "#D4A514",
        "phone" => "#A260D4",
        _ => "#5E7A91"
    };

    public string DeviceTypeLabel => DeviceType.Trim().ToUpperInvariant();

    public string ModelLine
    {
        get
        {
            var values = new[] { Manufacturer, Model }
                .Where(static value => !string.IsNullOrWhiteSpace(value));
            return string.Join(" ", values);
        }
    }

    public bool HasAssetTag => !string.IsNullOrWhiteSpace(AssetTag);
    public bool HasSerialNumber => !string.IsNullOrWhiteSpace(SerialNumber);
    public bool HasIpAddress => !string.IsNullOrWhiteSpace(IpAddress);
    public bool HasClientName => !string.IsNullOrWhiteSpace(ClientName);
    public bool HasClientUser => !string.IsNullOrWhiteSpace(ClientUserDisplayName);
    public bool HasDeploymentOwner =>
        IsDeployment
        && (!string.IsNullOrWhiteSpace(AssignedToDisplayName)
            || !string.IsNullOrWhiteSpace(AssignedToLoginName));
    public string AssetTagChipLabel => AssetTag.Trim();
    public string SerialChipLabel => SerialNumber.Trim();
    public string IpChipLabel => IpAddress.Trim();
    public string ClientChipLabel
    {
        get
        {
            var values = new[] { ClientName, ClientUserDisplayName }
                .Where(static value => !string.IsNullOrWhiteSpace(value));
            return string.Join(" · ", values);
        }
    }
    public string StatusLabel => IsDeployment
        ? HasClientName
            ? HasClientUser
                ? "User assigned"
                : "Client assigned"
            : "Ready"
        : IsInStock
            ? "New"
            : "In progress";
    public string StatusColor => IsDeployment
        ? "#38D996"
        : IsInStock
            ? "#27C7E8"
            : "#F6B73C";

    public string AssignmentLabel => IsDeployment
        ? HasClientName
            ? ClientChipLabel
            : "Ready for deployment"
        : IsInStock
        ? "Stock"
        : string.IsNullOrWhiteSpace(AssignedToDisplayName)
            ? AssignedToLoginName
            : AssignedToDisplayName;
    public string DeploymentOwnerLabel =>
        $"Deploying: {(string.IsNullOrWhiteSpace(AssignedToDisplayName)
            ? AssignedToLoginName
            : AssignedToDisplayName)}";

    public string IdentityLine
    {
        get
        {
            var values = new[] { Manufacturer, Model, SerialNumber }
                .Where(static value => !string.IsNullOrWhiteSpace(value));
            return string.Join(" · ", values);
        }
    }

    public string LocationLine
    {
        get
        {
            var values = new[] { ClientName, IpAddress }
                .Where(static value => !string.IsNullOrWhiteSpace(value));
            return string.Join(" · ", values);
        }
    }
}

/// <summary>
/// A customer available for inventory deployment. The production version maps this
/// record to tb_data.Clients; the local demo supplies representative in-memory data.
/// </summary>
public sealed class InventoryClient
{
    public int ClientId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string PrimaryLocation { get; init; } = string.Empty;
    public ObservableCollection<InventoryClientUser> Users { get; } = new();

    public override string ToString() => Name;
}

/// <summary>
/// A person or shared role at a client. Account passwords remain in the encrypted
/// Credentials workbook/store and are deliberately not part of this inventory model.
/// </summary>
public sealed class InventoryClientUser
{
    public long ClientUserId { get; init; }
    public int ClientId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string RoleDepartment { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    public string LocationName { get; init; } = string.Empty;
    public bool IsActive { get; init; } = true;

    public string DisplayLabel => string.IsNullOrWhiteSpace(RoleDepartment)
        ? DisplayName
        : $"{DisplayName} — {RoleDepartment}";

    public override string ToString() => DisplayLabel;
}

/// <summary>
/// Append-only history entry for an asset's client/user ownership. This allows the
/// eventual SQL-backed inventory to answer who had a device and when.
/// </summary>
public sealed class EquipmentAssignmentHistoryEntry
{
    public long EquipmentAssignmentHistoryId { get; init; }
    public long EquipmentId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string WorkflowStage { get; init; } = string.Empty;
    public string AssignedToLoginName { get; init; } = string.Empty;
    public string AssignedToDisplayName { get; init; } = string.Empty;
    public int? ClientId { get; init; }
    public long? ClientUserId { get; init; }
    public string ClientName { get; init; } = string.Empty;
    public string ClientUserDisplayName { get; init; } = string.Empty;
    public string LocationName { get; init; } = string.Empty;
    public DateTime AssignedAtUtc { get; init; }
    public DateTime? UnassignedAtUtc { get; init; }
    public string Notes { get; init; } = string.Empty;

    public string ClientChipLabel
    {
        get
        {
            var values = new[]
                {
                    ClientName,
                    ClientUserDisplayName,
                    LocationName
                }
                .Where(static value => !string.IsNullOrWhiteSpace(value));
            return string.Join(" · ", values);
        }
    }
}

public static class EquipmentWorkflowStages
{
    public const string Stock = "Stock";
    public const string Assigned = "Assigned";
    public const string Deployment = "Deployment";

    public static bool IsStock(string? value) =>
        string.Equals(value, Stock, StringComparison.OrdinalIgnoreCase);

    public static bool IsDeployment(string? value) =>
        string.Equals(value, Deployment, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A Stock, technician, or Deployment lane on the equipment assignment board.</summary>
public sealed class EquipmentLane : INotifyPropertyChanged
{
    public EquipmentLane(
        string title,
        string? assignedToLoginName,
        string? workflowStage = null)
    {
        Title = title;
        AssignedToLoginName = assignedToLoginName ?? string.Empty;
        WorkflowStage = workflowStage
            ?? (string.IsNullOrWhiteSpace(AssignedToLoginName)
                ? EquipmentWorkflowStages.Stock
                : EquipmentWorkflowStages.Assigned);
        Items.CollectionChanged += (_, _) =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountLabel)));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }
    public string AssignedToLoginName { get; }
    public string WorkflowStage { get; }
    public bool IsStock => EquipmentWorkflowStages.IsStock(WorkflowStage);
    public bool IsDeployment => EquipmentWorkflowStages.IsDeployment(WorkflowStage);
    public string Subtitle => IsDeployment
        ? string.IsNullOrWhiteSpace(AssignedToLoginName)
            ? "Awaiting deployment owner"
            : "Deploying technician"
        : IsStock
            ? "Available inventory"
            : "Assigned technician";
    public string Initials
    {
        get
        {
            if (IsStock)
            {
                return "\uE719";
            }

            if (IsDeployment && string.IsNullOrWhiteSpace(AssignedToLoginName))
            {
                return "\uE73E";
            }

            var words = Title.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return words.Length switch
            {
                0 => "?",
                1 => words[0][..1].ToUpperInvariant(),
                _ => string.Concat(
                    words[0][..1],
                    words[^1][..1]).ToUpperInvariant()
            };
        }
    }

    public string LaneAccentColor
    {
        get
        {
            if (IsStock)
            {
                return "#20C6E8";
            }

            if (IsDeployment && string.IsNullOrWhiteSpace(AssignedToLoginName))
            {
                return "#38D996";
            }

            var colors = new[]
            {
                "#4E8DFF",
                "#1FCDAA",
                "#B57BFF",
                "#F3A64A",
                "#E66AC2"
            };
            var index = Title.Aggregate(
                0,
                static (current, character) => (current + character) % 5);
            return colors[index];
        }
    }

    public string AvatarFontFamily =>
        IsStock || (IsDeployment && string.IsNullOrWhiteSpace(AssignedToLoginName))
        ? "Segoe MDL2 Assets"
        : "Segoe UI";

    public ObservableCollection<EquipmentItem> Items { get; } = new();
    public int ItemCount => Items.Count;
    public string CountLabel => Items.Count == 1 ? "1 item" : $"{Items.Count} items";
}
