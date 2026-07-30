using Microsoft.Data.SqlClient;
using TechBench.Models;

namespace TechBench.Data;

public sealed partial class SqlServerTechBenchRepository
{
    private const int TooManyStoredProcedureArgumentsErrorNumber = 8144;
    private const int StoredProcedureAcceptsNoArgumentsErrorNumber = 8146;
    private const int InvalidEquipmentWorkflowStageErrorNumber = 52211;

    public IReadOnlyList<EquipmentItem> GetEquipmentBoard()
    {
        try
        {
            return GetEquipmentBoard(includeDeployedParameter: true);
        }
        catch (SqlException ex)
            when (HasLegacyEquipmentProcedureSignature(ex))
        {
            // Schema 15 installations deployed before 0.5.109 do not expose
            // @IncludeDeployed yet. Keep the Board usable until its SQL update
            // is applied; the older procedure naturally returns active lanes.
            return GetEquipmentBoard(includeDeployedParameter: false);
        }
    }

    private IReadOnlyList<EquipmentItem> GetEquipmentBoard(
        bool includeDeployedParameter) =>
        QueryAsync(
            Procedures.GetEquipmentBoard,
            includeDeployedParameter
                ? command => AddBit(command, "@IncludeDeployed", true)
                : null,
            (reader, token) => ReadListAsync(reader, token, ReadEquipmentItem),
            CancellationToken.None).GetAwaiter().GetResult();

    public IReadOnlyList<EquipmentItem> GetEquipmentInventory(
        int? clientId = null,
        long? clientUserId = null,
        string? clientName = null) =>
        QueryAsync(
            Procedures.GetEquipmentInventory,
            command =>
            {
                AddInt(command, "@ClientId", clientId);
                AddBigInt(command, "@ClientUserId", clientUserId);
                AddText(command, "@ClientName", 240, clientName);
            },
            (reader, token) => ReadListAsync(reader, token, ReadEquipmentItem),
            CancellationToken.None).GetAwaiter().GetResult();

    public IReadOnlyList<InventoryClient> GetInventoryClients() =>
        QueryAsync(
            Procedures.GetInventoryClients,
            null,
            ReadInventoryClientsAsync,
            CancellationToken.None).GetAwaiter().GetResult();

    public IReadOnlyList<EquipmentAssignmentHistoryEntry> GetEquipmentAssignmentHistory(
        long equipmentId)
    {
        if (equipmentId <= 0)
        {
            return [];
        }

        return QueryAsync(
            Procedures.GetEquipmentAssignmentHistory,
            command => AddBigInt(command, "@EquipmentId", equipmentId),
            (reader, token) => ReadListAsync(
                reader,
                token,
                ReadEquipmentAssignmentHistoryEntry),
            CancellationToken.None).GetAwaiter().GetResult();
    }

    public EquipmentItem SaveEquipment(EquipmentItem equipment)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        return QueryAsync(
            Procedures.SaveEquipment,
            command =>
            {
                AddBigInt(
                    command,
                    "@EquipmentId",
                    equipment.EquipmentId > 0 ? equipment.EquipmentId : null);
                AddText(command, "@AssetTag", 80, equipment.AssetTag);
                AddRequiredText(command, "@DeviceType", 80, equipment.DeviceType);
                AddRequiredText(command, "@Name", 180, equipment.Name);
                AddText(command, "@SerialNumber", 120, equipment.SerialNumber);
                AddText(command, "@PartNumber", 120, equipment.PartNumber);
                AddText(command, "@IpAddress", 80, equipment.IpAddress);
                AddText(command, "@Manufacturer", 120, equipment.Manufacturer);
                AddText(command, "@Model", 120, equipment.Model);
                AddText(command, "@AnyDeskNumber", 80, equipment.AnyDeskNumber);
                AddMaxText(command, "@AnyDeskPassword", equipment.AnyDeskPassword);
                AddInt(command, "@ClientId", equipment.ClientId);
                AddBigInt(command, "@ClientUserId", equipment.ClientUserId);
                AddText(command, "@LocationName", 240, equipment.LocationName);
                AddMaxText(command, "@Notes", equipment.Notes);
                AddBinary(command, "@ExpectedRowVersion", 8, equipment.RowVersion);
            },
            (reader, token) => ReadSingleAsync(reader, token, ReadEquipmentItem),
            CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException(
                "SQL Server did not return the saved equipment record.");
    }

    public IReadOnlyList<EquipmentItem> MoveEquipment(
        EquipmentItem equipment,
        string? targetWindowsLoginName,
        string targetWorkflowStage,
        int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        if (equipment.EquipmentId <= 0)
        {
            throw new ArgumentException(
                "Only saved equipment can be assigned.",
                nameof(equipment));
        }

        try
        {
            return MoveEquipment(
                equipment,
                targetWindowsLoginName,
                targetWorkflowStage,
                targetIndex,
                includeDeployedParameter: true);
        }
        catch (SqlException ex)
            when (HasLegacyEquipmentProcedureSignature(ex))
        {
            try
            {
                return MoveEquipment(
                    equipment,
                    targetWindowsLoginName,
                    targetWorkflowStage,
                    targetIndex,
                    includeDeployedParameter: false);
            }
            catch (SqlException fallbackException)
                when (IsMissingDeployedLifecycle(
                    fallbackException,
                    targetWorkflowStage))
            {
                throw CreateMissingDeployedLifecycleException(
                    fallbackException);
            }
        }
        catch (SqlException ex)
            when (IsMissingDeployedLifecycle(ex, targetWorkflowStage))
        {
            throw CreateMissingDeployedLifecycleException(ex);
        }
    }

    private IReadOnlyList<EquipmentItem> MoveEquipment(
        EquipmentItem equipment,
        string? targetWindowsLoginName,
        string targetWorkflowStage,
        int targetIndex,
        bool includeDeployedParameter) =>
        QueryAsync(
            Procedures.MoveEquipment,
            command =>
            {
                AddBigInt(command, "@EquipmentId", equipment.EquipmentId);
                AddText(
                    command,
                    "@TargetWindowsLoginName",
                    256,
                    targetWindowsLoginName);
                AddRequiredText(
                    command,
                    "@TargetWorkflowStage",
                    24,
                    targetWorkflowStage);
                AddInt(command, "@TargetIndex", Math.Max(0, targetIndex));
                if (includeDeployedParameter)
                {
                    AddBit(command, "@IncludeDeployed", true);
                }

                AddBinary(command, "@ExpectedRowVersion", 8, equipment.RowVersion);
            },
            (reader, token) => ReadListAsync(reader, token, ReadEquipmentItem),
            CancellationToken.None).GetAwaiter().GetResult();

    private static bool IsMissingDeployedLifecycle(
        SqlException exception,
        string targetWorkflowStage) =>
        exception.Number == InvalidEquipmentWorkflowStageErrorNumber
        && EquipmentWorkflowStages.IsDeployed(targetWorkflowStage);

    private static bool HasLegacyEquipmentProcedureSignature(
        SqlException exception) =>
        exception.Number is
            TooManyStoredProcedureArgumentsErrorNumber
            or StoredProcedureAcceptsNoArgumentsErrorNumber;

    private static InvalidOperationException
        CreateMissingDeployedLifecycleException(SqlException innerException) =>
        new(
            "Mark Deployed requires the matching TechBench server/SQL update. "
            + "Install the current beta server package, then refresh Inventory.",
            innerException);

    public void ArchiveEquipment(EquipmentItem equipment)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        if (equipment.EquipmentId <= 0)
        {
            return;
        }

        ExecuteNonQueryAsync(
            Procedures.ArchiveEquipment,
            command =>
            {
                AddBigInt(command, "@EquipmentId", equipment.EquipmentId);
                AddBinary(command, "@ExpectedRowVersion", 8, equipment.RowVersion);
            },
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private static EquipmentItem ReadEquipmentItem(
        Microsoft.Data.SqlClient.SqlDataReader reader) => new()
    {
        EquipmentId = GetInt64(reader, "EquipmentId"),
        AssetTag = GetString(reader, "AssetTag"),
        DeviceType = GetString(reader, "DeviceType"),
        Name = GetString(reader, "Name"),
        SerialNumber = GetString(reader, "SerialNumber"),
        PartNumber = GetString(reader, "PartNumber"),
        IpAddress = GetString(reader, "IpAddress"),
        Manufacturer = GetString(reader, "Manufacturer"),
        Model = GetString(reader, "Model"),
        AnyDeskNumber = GetString(reader, "AnyDeskNumber"),
        AnyDeskPassword = GetString(reader, "AnyDeskPassword"),
        ClientId = GetNullableInt32(reader, "ClientId"),
        ClientName = GetString(reader, "ClientName"),
        ClientUserId = GetNullableInt64(reader, "ClientUserId"),
        ClientUserDisplayName = GetString(reader, "ClientUserDisplayName"),
        ClientUserEmail = GetString(reader, "ClientUserEmail"),
        LocationName = GetString(reader, "LocationName"),
        Notes = GetString(reader, "Notes"),
        AssignedToLoginName = GetString(reader, "AssignedToLoginName"),
        AssignedToDisplayName = GetString(reader, "AssignedToDisplayName"),
        WorkflowStage = GetString(reader, "WorkflowStage"),
        SortOrder = GetInt32(reader, "SortOrder"),
        AssignedAtUtc = GetNullableDateTime(reader, "AssignedAtUtc"),
        CreatedAtUtc = GetDateTime(reader, "CreatedAtUtc", DateTime.MinValue),
        UpdatedAtUtc = GetDateTime(reader, "UpdatedAtUtc", DateTime.MinValue),
        RowVersion = GetBytes(reader, "RowVersion")
    };

    private static async Task<IReadOnlyList<InventoryClient>> ReadInventoryClientsAsync(
        Microsoft.Data.SqlClient.SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var clients = new List<InventoryClient>();
        var byId = new Dictionary<int, InventoryClient>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var clientId = GetInt32(reader, "ClientId");
            if (!byId.TryGetValue(clientId, out var client))
            {
                client = new InventoryClient
                {
                    ClientId = clientId,
                    Name = GetString(reader, "ClientName"),
                    PrimaryLocation = GetString(reader, "PrimaryLocation")
                };
                byId.Add(clientId, client);
                clients.Add(client);
            }

            var clientUserId = GetNullableInt64(reader, "ClientUserId");
            if (clientUserId is not > 0)
            {
                continue;
            }

            client.Users.Add(new InventoryClientUser
            {
                ClientUserId = clientUserId.Value,
                ClientId = clientId,
                DisplayName = GetString(reader, "ClientUserDisplayName"),
                RoleDepartment = GetString(reader, "RoleDepartment"),
                Email = GetString(reader, "Email"),
                Phone = GetString(reader, "Phone"),
                LocationName = GetString(reader, "LocationName"),
                IsActive = GetBoolean(reader, "IsActive", true)
            });
        }

        return clients;
    }

    private static EquipmentAssignmentHistoryEntry ReadEquipmentAssignmentHistoryEntry(
        Microsoft.Data.SqlClient.SqlDataReader reader) => new()
    {
        EquipmentAssignmentHistoryId =
            GetInt64(reader, "EquipmentAssignmentHistoryId"),
        EquipmentId = GetInt64(reader, "EquipmentId"),
        EventType = GetString(reader, "EventType"),
        WorkflowStage = GetString(reader, "WorkflowStage"),
        AssignedToLoginName = GetString(reader, "AssignedToLoginName"),
        AssignedToDisplayName = GetString(reader, "AssignedToDisplayName"),
        ClientId = GetNullableInt32(reader, "ClientId"),
        ClientUserId = GetNullableInt64(reader, "ClientUserId"),
        ClientName = GetString(reader, "ClientName"),
        ClientUserDisplayName = GetString(reader, "ClientUserDisplayName"),
        LocationName = GetString(reader, "LocationName"),
        AssignedAtUtc = GetDateTime(reader, "ChangedAtUtc", DateTime.MinValue),
        Notes = GetString(reader, "Notes")
    };
}
