using System.Text.Json;

namespace TechBench.Models;

public sealed record EquipmentDeploymentState
{
    public const string SettingKeyPrefix = "Equipment.Deployed.";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public long EquipmentId { get; init; }
    public DateTime DeployedAtUtc { get; init; }
    public string DeployedByLoginName { get; init; } = string.Empty;
    public string DeployedByDisplayName { get; init; } = string.Empty;
    public string AssignedToLoginName { get; init; } = string.Empty;
    public string AssignedToDisplayName { get; init; } = string.Empty;
    public int? ClientId { get; init; }
    public string ClientName { get; init; } = string.Empty;
    public long? ClientUserId { get; init; }
    public string ClientUserDisplayName { get; init; } = string.Empty;
    public string LocationName { get; init; } = string.Empty;

    public string SettingKey => BuildSettingKey(EquipmentId);

    public string ChangedByLabel =>
        string.IsNullOrWhiteSpace(DeployedByDisplayName)
            ? DeployedByLoginName
            : DeployedByDisplayName;

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public EquipmentAssignmentHistoryEntry ToHistoryEntry() => new()
    {
        EquipmentId = EquipmentId,
        EventType = "Deployed",
        WorkflowStage = EquipmentWorkflowStages.Deployed,
        AssignedToLoginName = AssignedToLoginName,
        AssignedToDisplayName = AssignedToDisplayName,
        ClientId = ClientId,
        ClientName = ClientName,
        ClientUserId = ClientUserId,
        ClientUserDisplayName = ClientUserDisplayName,
        LocationName = LocationName,
        AssignedAtUtc = DeployedAtUtc,
        Notes = string.IsNullOrWhiteSpace(ChangedByLabel)
            ? "Equipment deployment completed."
            : $"Equipment deployment completed by {ChangedByLabel}."
    };

    public static EquipmentDeploymentState Create(
        EquipmentItem equipment,
        CurrentUserContext currentUser,
        DateTime deployedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(currentUser);

        return new EquipmentDeploymentState
        {
            EquipmentId = equipment.EquipmentId,
            DeployedAtUtc = deployedAtUtc.ToUniversalTime(),
            DeployedByLoginName = currentUser.LoginName,
            DeployedByDisplayName = currentUser.DisplayName,
            AssignedToLoginName = equipment.AssignedToLoginName,
            AssignedToDisplayName = equipment.AssignedToDisplayName,
            ClientId = equipment.ClientId,
            ClientName = equipment.ClientName,
            ClientUserId = equipment.ClientUserId,
            ClientUserDisplayName = equipment.ClientUserDisplayName,
            LocationName = equipment.LocationName
        };
    }

    public static string BuildSettingKey(long equipmentId) =>
        $"{SettingKeyPrefix}{equipmentId}";

    public static IReadOnlyDictionary<long, EquipmentDeploymentState>
        ReadFromSettings(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var states = new Dictionary<long, EquipmentDeploymentState>();
        foreach (var (key, value) in settings)
        {
            if (!key.StartsWith(
                    SettingKeyPrefix,
                    StringComparison.OrdinalIgnoreCase)
                || !long.TryParse(
                    key[SettingKeyPrefix.Length..],
                    out var equipmentId)
                || equipmentId <= 0)
            {
                continue;
            }

            try
            {
                var state = JsonSerializer.Deserialize<EquipmentDeploymentState>(
                    value,
                    JsonOptions);
                if (state is not null && state.EquipmentId == equipmentId)
                {
                    states[equipmentId] = state;
                }
            }
            catch (JsonException)
            {
                // Ignore a malformed lifecycle setting without hiding equipment.
            }
        }

        return states;
    }

    public static IReadOnlyList<EquipmentItem> Apply(
        IEnumerable<EquipmentItem> equipment,
        IReadOnlyDictionary<long, EquipmentDeploymentState> states)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(states);

        return equipment
            .Select(item => states.ContainsKey(item.EquipmentId)
                ? CopyWithWorkflowStage(
                    item,
                    EquipmentWorkflowStages.Deployed)
                : item)
            .ToArray();
    }

    private static EquipmentItem CopyWithWorkflowStage(
        EquipmentItem source,
        string workflowStage) => new()
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
        AnyDeskNumber = source.AnyDeskNumber,
        AnyDeskPassword = source.AnyDeskPassword,
        ClientId = source.ClientId,
        ClientName = source.ClientName,
        ClientUserId = source.ClientUserId,
        ClientUserDisplayName = source.ClientUserDisplayName,
        ClientUserEmail = source.ClientUserEmail,
        LocationName = source.LocationName,
        Notes = source.Notes,
        AssignedToLoginName = source.AssignedToLoginName,
        AssignedToDisplayName = source.AssignedToDisplayName,
        WorkflowStage = workflowStage,
        SortOrder = source.SortOrder,
        AssignedAtUtc = source.AssignedAtUtc,
        CreatedAtUtc = source.CreatedAtUtc,
        UpdatedAtUtc = source.UpdatedAtUtc,
        RowVersion = source.RowVersion
    };
}
