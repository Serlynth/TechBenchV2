namespace TechBench.Models;

public static class EquipmentInventoryFilter
{
    public const string AllStatuses = "All statuses";
    public const string StockStatus = "Stock";
    public const string InProgressStatus = "In progress";
    public const string DeploymentStatus = "Deployment";
    public const string DeployedStatus = "Deployed";
    public const string AllDeviceTypes = "All device types";
    public const string AllClients = "All clients";
    public const string AllTechnicians = "All technicians";

    public static bool Matches(
        EquipmentItem item,
        string? searchText,
        string? status,
        string? deviceType,
        string? client,
        string? technician,
        bool stockOnly)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (stockOnly && !item.IsInStock)
        {
            return false;
        }

        if (!MatchesStatus(item, status)
            || !MatchesExactOption(
                item.DeviceType,
                deviceType,
                AllDeviceTypes)
            || !MatchesExactOption(
                item.ClientName,
                client,
                AllClients)
            || !MatchesTechnician(item, technician))
        {
            return false;
        }

        var query = searchText?.Trim() ?? string.Empty;
        return query.Length == 0
            || SearchValues(item).Any(value =>
                value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesStatus(EquipmentItem item, string? status) =>
        status switch
        {
            null or "" or AllStatuses => true,
            StockStatus => item.IsInStock,
            InProgressStatus =>
                !item.IsInStock && !item.IsDeployment && !item.IsDeployed,
            DeploymentStatus => item.IsDeployment,
            DeployedStatus => item.IsDeployed,
            _ => true
        };

    private static bool MatchesExactOption(
        string value,
        string? selected,
        string allOption) =>
        string.IsNullOrWhiteSpace(selected)
        || selected.Equals(allOption, StringComparison.Ordinal)
        || value.Equals(selected, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesTechnician(
        EquipmentItem item,
        string? technician) =>
        string.IsNullOrWhiteSpace(technician)
        || technician.Equals(AllTechnicians, StringComparison.Ordinal)
        || item.TechnicianLabel.Equals(
            technician,
            StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SearchValues(EquipmentItem item)
    {
        yield return item.Name;
        yield return item.AssetTag;
        yield return item.SerialNumber;
        yield return item.PartNumber;
        yield return item.DeviceType;
        yield return item.Manufacturer;
        yield return item.Model;
        yield return item.IpAddress;
        yield return item.ClientName;
        yield return item.ClientUserDisplayName;
        yield return item.ClientUserEmail;
        yield return item.AssignedToDisplayName;
        yield return item.AssignedToLoginName;
        yield return item.LocationName;
    }
}
