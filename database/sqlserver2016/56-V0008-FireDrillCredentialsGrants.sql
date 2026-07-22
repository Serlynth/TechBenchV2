:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/* Every authenticated TechBench user may search, explicitly reveal, and copy.
   The procedures own encryption-key access and write an audit trail. */
GRANT EXECUTE ON OBJECT::[tb_app].[SearchFireDrillCredentials] TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[RevealFireDrillCredential] TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AuditFireDrillCredentialCopy] TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[AdminRequestFireDrillSync] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[GetFireDrillSyncStatus] TO [tb_role_admin];

GRANT EXECUTE ON OBJECT::[tb_service].[GetFireDrillSyncConfiguration] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ClaimFireDrillSyncWork] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[RenewFireDrillSyncLease] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplyFireDrillCredentialSnapshot] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[CompleteFireDrillSyncWork] TO [tb_role_sync_service];

REVOKE EXECUTE ON OBJECT::[tb_app].[SearchFireDrillCredentials] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[RevealFireDrillCredential] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AuditFireDrillCredentialCopy] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminRequestFireDrillSync] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[GetFireDrillSyncStatus] FROM [tb_preview_reader];

PRINT N'TechBench V0008 FireDrill credential grants applied.';
GO
