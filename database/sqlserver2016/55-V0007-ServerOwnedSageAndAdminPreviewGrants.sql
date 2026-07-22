:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/* Sage ingestion is service-owned in V0007. Admins may enqueue and inspect
   work, but cannot run any legacy workstation-side Sage apply lifecycle. */
REVOKE EXECUTE ON OBJECT::[tb_app].[AcquireSyncLease] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[ReleaseSyncLease] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[BeginSyncRun] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[CompleteSyncRun] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertClient] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertSageCustomer] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncRemoveStaleSageCustomers] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertClientExternalIdentity] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplySageCustomerSnapshot] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplyClientSnapshot] FROM [tb_role_admin];

REVOKE EXECUTE ON OBJECT::[tb_app].[AcquireSyncLease] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[ReleaseSyncLease] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[BeginSyncRun] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[CompleteSyncRun] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertClient] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertSageCustomer] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncRemoveStaleSageCustomers] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertClientExternalIdentity] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplySageCustomerSnapshot] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplyClientSnapshot] FROM [tb_role_sync_operator];

GRANT EXECUTE ON OBJECT::[tb_app].[AdminRequestSageSync] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[GetSageSyncStatus] TO [tb_role_admin];

GRANT EXECUTE ON OBJECT::[tb_service].[GetSageSyncConfiguration] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ClaimSageSyncWork] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[RenewSageSyncLease] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplySageCustomerSnapshot] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[CompleteSageSyncWork] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[GetAutomaticClientMatchCandidates] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplyAutomaticClientMatch] TO [tb_role_sync_service];

/* Admin preview is server-issued and activated per physical SQL connection.
   Only the Admin role may impersonate the WITHOUT LOGIN reader principal. */
GRANT EXECUTE ON OBJECT::[tb_app].[AdminListPreviewUsers] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminBeginUserPreview] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[ActivateReadOnlyPreview] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminEndUserPreview] TO [tb_role_admin];
GRANT IMPERSONATE ON USER::[tb_preview_reader] TO [tb_role_admin];

REVOKE IMPERSONATE ON USER::[tb_preview_reader] FROM [tb_role_user];
REVOKE IMPERSONATE ON USER::[tb_preview_reader] FROM [tb_role_manager];
REVOKE IMPERSONATE ON USER::[tb_preview_reader] FROM [tb_role_sync_operator];
REVOKE IMPERSONATE ON USER::[tb_preview_reader] FROM [tb_role_sync_service];

GRANT EXECUTE ON OBJECT::[tb_app].[GetCurrentUserContext] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetRepositoryCapabilities] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[SearchClients] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClient] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[SearchTickets] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetTicket] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetTicketStatusOptions] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[SearchWorkEntries] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetWorkEntry] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetDistinctTags] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetWorkEntryLinks] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetTemplates] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetCommonLinks] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetSettings] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetPostingLogs] TO [tb_preview_reader];

/* Defense in depth: the reader has no private-data or mutation entry point. */
REVOKE EXECUTE ON OBJECT::[tb_app].[GetEditorDraft] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveEditorDraft] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteEditorDraft] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveWorkEntry] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteWorkEntry] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveWorkEntryLink] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteWorkEntryLink] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveTicket] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveUserSetting] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteUserSetting] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminRequestSageSync] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminBeginUserPreview] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[ActivateReadOnlyPreview] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminEndUserPreview] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminListPreviewUsers] FROM [tb_preview_reader];

PRINT N'TechBench V0007 server-owned Sage sync and read-only Admin preview grants applied.';
GO
