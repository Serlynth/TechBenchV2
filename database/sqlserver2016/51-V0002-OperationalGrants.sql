:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

GRANT EXECUTE ON OBJECT::[tb_app].[GetRepositoryCapabilities]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[EnsureWorkspaceDefaults]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SearchTickets]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetTicket]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveTicket]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetTicketStatusOptions]
    TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[SearchWorkEntries]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetWorkEntry]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetDistinctTags]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveWorkEntry]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteWorkEntry]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetWorkEntryLinks]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveWorkEntryLink]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteWorkEntryLink]
    TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[GetEditorDraft]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveEditorDraft]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteEditorDraft]
    TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[GetTemplates]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveTemplate]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteTemplate]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveTemplate]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteTemplate]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetCommonLinks]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveCommonLink]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteCommonLink]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveCommonLink]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteCommonLink]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetSettings]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveUserSetting]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteUserSetting]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveSetting]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteSetting]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClientAliases]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveClientAlias]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteClientAlias]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveClientAlias]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteClientAlias]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClientExternalIdentities]
    TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[AddPostingLog]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetLatestVerifiedWhdPostingLog]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[BeginPostingAttempt]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[HeartbeatPostingAttempt]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetOutstandingPostingAttempt]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[CompletePostingAttempt]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[ResolveOutstandingPostingAttempts]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[MarkWorkEntryPosted]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AbandonOutstandingPostingAttempts]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[HasSuccessfulSageDraftLog]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetPostingLogs]
    TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[BeginImportBatch]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AddImportLegacyMapping]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[CompleteImportBatch]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetImportBatches]
    TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[SearchWorkEntries]
    TO [tb_role_manager];
GRANT EXECUTE ON OBJECT::[tb_app].[GetWorkEntry]
    TO [tb_role_manager];
GRANT EXECUTE ON OBJECT::[tb_app].[GetDistinctTags]
    TO [tb_role_manager];
GRANT EXECUTE ON OBJECT::[tb_app].[GetPostingLogs]
    TO [tb_role_manager];
GRANT EXECUTE ON OBJECT::[tb_app].[GetSyncRuns]
    TO [tb_role_manager];
GRANT EXECUTE ON OBJECT::[tb_app].[GetImportBatches]
    TO [tb_role_manager];

GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveOrganizationSetting]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteOrganizationSetting]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveExternalMapping]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminMergeClients]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[ReconcileClientMatches]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[GetSyncRuns]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[GetImportBatches]
    TO [tb_role_admin];

GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertClient]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertSageCustomer]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncRemoveStaleSageCustomers]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertClientExternalIdentity]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertTicketStatusOption]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertTicket]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[AcquireSyncLease]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[ReleaseSyncLease]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[BeginSyncRun]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[CompleteSyncRun]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[GetSyncRuns]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncApplyClientSnapshot]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncApplyTicketSnapshot]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncApplyTicketStatusSnapshot]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncApplySageCustomerSnapshot]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[ReconcileClientMatches]
    TO [tb_role_sync_operator];

PRINT N'TechBench V0002 stored-procedure-only grants applied.';
GO
