:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/* Desktop Admins may orchestrate WHD work, not apply untrusted snapshots. */
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplyClientSnapshot] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplyTicketSnapshot] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplyTicketStatusSnapshot] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertTicket] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertTicketStatusOption] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertClient] FROM [tb_role_admin];

GRANT EXECUTE ON OBJECT::[tb_app].[AdminRequestWhdSync] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[GetWhdSyncStatus] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminGetWhdUserMappings] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminReconcileWhdAuthorizedUsers] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveWhdUserMapping] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminGetWhdTechnicians] TO [tb_role_admin];

GRANT EXECUTE ON OBJECT::[tb_app].[SearchTickets] TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetTicket] TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_service].[GetWhdSyncConfiguration] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ClaimWhdSyncWork] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[RenewWhdSyncLease] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplyWhdClientSnapshot] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplyWhdTicketBatch] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplyWhdTicketStatusSnapshot] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplyWhdTechnicianSnapshot] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplyWhdTechGroupSnapshot] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[CompleteWhdSyncWork] TO [tb_role_sync_service];

PRINT N'TechBench V0006 WHD server-sync grants applied.';
GO
