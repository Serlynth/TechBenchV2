:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/* Every authenticated TechBench user may search and explicitly reveal.
   The procedures own encryption-key access. */
GRANT EXECUTE ON OBJECT::[tb_app].[SearchFireDrillCredentials] TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[RevealFireDrillCredential] TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[AdminRequestFireDrillSync] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[GetFireDrillSyncStatus] TO [tb_role_admin];

GRANT EXECUTE ON OBJECT::[tb_service].[GetFireDrillSyncConfiguration] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ClaimFireDrillSyncWork] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[RenewFireDrillSyncLease] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplyFireDrillCredentialSnapshot] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[CompleteFireDrillSyncWork] TO [tb_role_sync_service];

REVOKE EXECUTE ON OBJECT::[tb_app].[SearchFireDrillCredentials] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[RevealFireDrillCredential] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminRequestFireDrillSync] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[GetFireDrillSyncStatus] FROM [tb_preview_reader];

PRINT N'TechBench V0008 Credentials grants applied.';
GO
