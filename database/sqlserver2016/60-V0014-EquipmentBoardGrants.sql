:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

GRANT EXECUTE ON OBJECT::[tb_app].[AdminGetEquipmentBoard] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminGetInventoryClients] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminGetEquipmentAssignmentHistory] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveEquipment] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminMoveEquipment] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminArchiveEquipment] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplyCredentialsClientUserSnapshot]
    TO [tb_role_sync_service];

REVOKE EXECUTE ON OBJECT::[tb_app].[AdminGetEquipmentBoard] FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminGetInventoryClients] FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminGetEquipmentAssignmentHistory] FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminSaveEquipment] FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminMoveEquipment] FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminArchiveEquipment] FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminGetEquipmentBoard] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminGetInventoryClients] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminGetEquipmentAssignmentHistory] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminSaveEquipment] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminMoveEquipment] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminArchiveEquipment] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_service].[ApplyCredentialsClientUserSnapshot]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_service].[ApplyCredentialsClientUserSnapshot]
    FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_service].[ApplyCredentialsClientUserSnapshot]
    FROM [tb_preview_reader];

PRINT N'TechBench V0014 equipment-board grants applied.';
GO
