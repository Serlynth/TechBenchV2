:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Remove legacy shared-mutation authority from non-Admin roles. Admin users
    are also members of tb_role_user, so these revokes are paired with explicit
    tb_role_admin grants below.
*/
REVOKE EXECUTE ON OBJECT::[tb_app].[EnsureWorkspaceDefaults]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveTemplate]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteTemplate]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminSaveTemplate]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminDeleteTemplate]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveCommonLink]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteCommonLink]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminSaveCommonLink]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminDeleteCommonLink]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveClientAlias]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteClientAlias]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminSaveClientAlias]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminDeleteClientAlias]
    FROM [tb_role_user];

REVOKE EXECUTE ON OBJECT::[tb_app].[EnsureWorkspaceDefaults]
    FROM [tb_role_manager];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveTemplate]
    FROM [tb_role_manager];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteTemplate]
    FROM [tb_role_manager];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveCommonLink]
    FROM [tb_role_manager];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteCommonLink]
    FROM [tb_role_manager];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveClientAlias]
    FROM [tb_role_manager];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteClientAlias]
    FROM [tb_role_manager];

REVOKE EXECUTE ON OBJECT::[tb_app].[EnsureWorkspaceDefaults]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveTemplate]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteTemplate]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveCommonLink]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteCommonLink]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveClientAlias]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteClientAlias]
    FROM [tb_role_sync_operator];

REVOKE EXECUTE ON OBJECT::[tb_app].[AdminSaveOrganizationSetting]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminDeleteOrganizationSetting]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminSaveExternalMapping]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminMergeClients]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[ReconcileClientMatches]
    FROM [tb_role_user];

REVOKE EXECUTE ON OBJECT::[tb_app].[AdminSaveOrganizationSetting]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminDeleteOrganizationSetting]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminSaveExternalMapping]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminMergeClients]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[ReconcileClientMatches]
    FROM [tb_role_sync_operator];

REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertClient]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertSageCustomer]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncRemoveStaleSageCustomers]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertClientExternalIdentity]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertTicketStatusOption]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertTicket]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[AcquireSyncLease]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[ReleaseSyncLease]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[BeginSyncRun]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[CompleteSyncRun]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplyClientSnapshot]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplyTicketSnapshot]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplyTicketStatusSnapshot]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplySageCustomerSnapshot]
    FROM [tb_role_sync_operator];

/* Read/use contracts remain available to every normal TechBench user. */
GRANT EXECUTE ON OBJECT::[tb_app].[GetRepositoryCapabilities]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetTemplates]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetCommonLinks]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClientAliases]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetDistinctTags]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetSettings]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveUserSetting]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteUserSetting]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveWorkEntry]
    TO [tb_role_user];

/* Organization configuration and shared reference catalogs are Admin-owned. */
GRANT EXECUTE ON OBJECT::[tb_app].[EnsureWorkspaceDefaults]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveTemplate]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteTemplate]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveTemplate]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteTemplate]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveCommonLink]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteCommonLink]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveCommonLink]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteCommonLink]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveClientAlias]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteClientAlias]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveClientAlias]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteClientAlias]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminGetOrganizationTags]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveOrganizationTag]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteOrganizationTag]
    TO [tb_role_admin];
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

/* Shared synchronization is now an Admin action, including Sage sync. */
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertClient]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertSageCustomer]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncRemoveStaleSageCustomers]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertClientExternalIdentity]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertTicketStatusOption]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertTicket]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AcquireSyncLease]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[ReleaseSyncLease]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[BeginSyncRun]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[CompleteSyncRun]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[GetSyncRuns]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncApplyClientSnapshot]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncApplyTicketSnapshot]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncApplyTicketStatusSnapshot]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncApplySageCustomerSnapshot]
    TO [tb_role_admin];

/* A legacy sync operator may inspect history but cannot initiate or apply sync. */
GRANT EXECUTE ON OBJECT::[tb_app].[GetSyncRuns]
    TO [tb_role_sync_operator];

PRINT N'TechBench V0004 Admin-owned shared-configuration grants applied.';
GO
