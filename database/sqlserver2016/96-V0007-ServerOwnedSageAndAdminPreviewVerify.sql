:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @FailureCount int = 0;

IF NOT EXISTS
(
    SELECT 1 FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.ServerOwnedSageAndAdminPreview.0007'
      AND [SchemaVersion] = 7
      AND [ReleaseVersion] = N'2.0.0-alpha.8'
)
BEGIN
    PRINT N'FAIL: V0007 migration marker is missing or invalid.';
    SET @FailureCount += 1;
END;

IF (SELECT MAX([SchemaVersion]) FROM [tb_deploy].[SchemaMigrations]) <> 7
BEGIN
    PRINT N'FAIL: installed schema version is not 7.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
    FROM [tb_data].[OrganizationSettings]
    WHERE [SettingKey] = N'Sage.ActivityItemId'
)
BEGIN
    PRINT N'FAIL: Sage Activity Item ID remains organization-scoped.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredObjects TABLE
(
    [ObjectName] nvarchar(300) NOT NULL PRIMARY KEY,
    [ObjectType] char(2) NOT NULL
);
INSERT INTO @RequiredObjects([ObjectName], [ObjectType]) VALUES
    (N'tb_sync.SageSyncRequests', N'U'),
    (N'tb_sync.SageSyncLeases', N'U'),
    (N'tb_sync.SageSyncHealth', N'U'),
    (N'tb_security.AdminUserPreviewSessions', N'U'),
    (N'tb_app.AdminRequestSageSync', N'P'),
    (N'tb_app.GetSageSyncStatus', N'P'),
    (N'tb_service.GetSageSyncConfiguration', N'P'),
    (N'tb_service.ClaimSageSyncWork', N'P'),
    (N'tb_service.RenewSageSyncLease', N'P'),
    (N'tb_service.ApplySageCustomerSnapshot', N'P'),
    (N'tb_service.CompleteSageSyncWork', N'P'),
    (N'tb_service.GetAutomaticClientMatchCandidates', N'P'),
    (N'tb_service.ApplyAutomaticClientMatch', N'P'),
    (N'tb_service.ApplyAutomaticWhdFamilyMember', N'P'),
    (N'tb_app.AdminListPreviewUsers', N'P'),
    (N'tb_app.AdminBeginUserPreview', N'P'),
    (N'tb_app.ActivateReadOnlyPreview', N'P'),
    (N'tb_app.AdminEndUserPreview', N'P'),
    (N'tb_security.FilterWhdTicketAccess', N'IF');

IF EXISTS
(
    SELECT 1 FROM @RequiredObjects AS required
    WHERE OBJECT_ID(required.[ObjectName], required.[ObjectType]) IS NULL
)
BEGIN
    PRINT N'FAIL: one or more V0007 objects are missing.';
    SET @FailureCount += 1;
END;

IF DATABASE_PRINCIPAL_ID(N'tb_preview_reader') IS NULL
BEGIN
    PRINT N'FAIL: the WITHOUT LOGIN preview reader is missing.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredColumns TABLE
(
    [ObjectName] nvarchar(300) NOT NULL,
    [ColumnName] sysname NOT NULL,
    PRIMARY KEY ([ObjectName], [ColumnName])
);
INSERT INTO @RequiredColumns([ObjectName], [ColumnName]) VALUES
    (N'tb_sync.SageSyncRequests', N'RequestId'),
    (N'tb_sync.SageSyncRequests', N'RequestedByWindowsSid'),
    (N'tb_sync.SageSyncRequests', N'RequestedAtUtc'),
    (N'tb_sync.SageSyncRequests', N'StartedAtUtc'),
    (N'tb_sync.SageSyncRequests', N'CompletedAtUtc'),
    (N'tb_sync.SageSyncRequests', N'Status'),
    (N'tb_sync.SageSyncRequests', N'AllowLargeRemoval'),
    (N'tb_sync.SageSyncRequests', N'RequiresLargeRemovalConfirmation'),
    (N'tb_sync.SageSyncRequests', N'ConfirmedRequestId'),
    (N'tb_sync.SageSyncRequests', N'ExistingCount'),
    (N'tb_sync.SageSyncRequests', N'ReadCount'),
    (N'tb_sync.SageSyncRequests', N'SavedCount'),
    (N'tb_sync.SageSyncRequests', N'StaleCount'),
    (N'tb_sync.SageSyncRequests', N'AttemptCount'),
    (N'tb_sync.SageSyncRequests', N'Message'),
    (N'tb_sync.SageSyncLeases', N'RequestId'),
    (N'tb_sync.SageSyncLeases', N'LeaseId'),
    (N'tb_sync.SageSyncLeases', N'WorkerId'),
    (N'tb_sync.SageSyncLeases', N'ExpiresAtUtc'),
    (N'tb_sync.SageSyncHealth', N'LastAttemptAtUtc'),
    (N'tb_sync.SageSyncHealth', N'LastSuccessfulAtUtc'),
    (N'tb_sync.SageSyncHealth', N'LastError'),
    (N'tb_security.AdminUserPreviewSessions', N'PreviewSessionId'),
    (N'tb_security.AdminUserPreviewSessions', N'ActorWindowsSid'),
    (N'tb_security.AdminUserPreviewSessions', N'TargetWindowsSid'),
    (N'tb_security.AdminUserPreviewSessions', N'ClientInstanceId'),
    (N'tb_security.AdminUserPreviewSessions', N'ExpiresAtUtc'),
    (N'tb_security.AdminUserPreviewSessions', N'EndedAtUtc');

IF EXISTS
(
    SELECT 1 FROM @RequiredColumns AS required
    WHERE COL_LENGTH(required.[ObjectName], required.[ColumnName]) IS NULL
)
BEGIN
    PRINT N'FAIL: a required V0007 column is missing.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredIndexes TABLE
(
    [ObjectName] nvarchar(300) NOT NULL,
    [IndexName] sysname NOT NULL,
    PRIMARY KEY ([ObjectName], [IndexName])
);
INSERT INTO @RequiredIndexes([ObjectName], [IndexName]) VALUES
    (N'tb_sync.SageSyncRequests', N'IX_SageSyncRequests_StatusRequested'),
    (N'tb_sync.SageSyncRequests', N'IX_SageSyncRequests_RequestedAt'),
    (N'tb_security.AdminUserPreviewSessions', N'IX_AdminUserPreviewSessions_ActorActive'),
    (N'tb_security.AdminUserPreviewSessions', N'IX_AdminUserPreviewSessions_Expires');

IF EXISTS
(
    SELECT 1 FROM @RequiredIndexes AS required
    WHERE NOT EXISTS
    (
        SELECT 1 FROM sys.indexes AS index_row
        WHERE index_row.[object_id] = OBJECT_ID(required.[ObjectName], N'U')
          AND index_row.[name] = required.[IndexName]
          AND index_row.[is_disabled] = 0
    )
)
BEGIN
    PRINT N'FAIL: a required V0007 index is missing or disabled.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredParameters TABLE
(
    [ProcedureName] nvarchar(300) NOT NULL,
    [ParameterName] sysname NOT NULL,
    PRIMARY KEY ([ProcedureName], [ParameterName])
);
INSERT INTO @RequiredParameters([ProcedureName], [ParameterName]) VALUES
    (N'tb_app.AdminRequestSageSync', N'@RequestId'),
    (N'tb_app.AdminRequestSageSync', N'@AllowLargeRemoval'),
    (N'tb_app.AdminRequestSageSync', N'@ConfirmedRequestId'),
    (N'tb_service.ClaimSageSyncWork', N'@WorkerId'),
    (N'tb_service.ClaimSageSyncWork', N'@LeaseSeconds'),
    (N'tb_service.RenewSageSyncLease', N'@WorkId'),
    (N'tb_service.RenewSageSyncLease', N'@LeaseId'),
    (N'tb_service.RenewSageSyncLease', N'@WorkerId'),
    (N'tb_service.RenewSageSyncLease', N'@LeaseSeconds'),
    (N'tb_service.ApplySageCustomerSnapshot', N'@WorkId'),
    (N'tb_service.ApplySageCustomerSnapshot', N'@LeaseId'),
    (N'tb_service.ApplySageCustomerSnapshot', N'@WorkerId'),
    (N'tb_service.ApplySageCustomerSnapshot', N'@Json'),
    (N'tb_service.ApplySageCustomerSnapshot', N'@SyncedAtUtc'),
    (N'tb_service.CompleteSageSyncWork', N'@WorkId'),
    (N'tb_service.CompleteSageSyncWork', N'@LeaseId'),
    (N'tb_service.CompleteSageSyncWork', N'@WorkerId'),
    (N'tb_service.CompleteSageSyncWork', N'@Succeeded'),
    (N'tb_service.CompleteSageSyncWork', N'@Message'),
    (N'tb_service.ApplyAutomaticClientMatch', N'@WhdClientId'),
    (N'tb_service.ApplyAutomaticClientMatch', N'@SageClientId'),
    (N'tb_service.ApplyAutomaticClientMatch', N'@ExpectedWhdRowVersion'),
    (N'tb_service.ApplyAutomaticClientMatch', N'@ExpectedSageRowVersion'),
    (N'tb_service.ApplyAutomaticClientMatch', N'@MatchScore'),
    (N'tb_service.ApplyAutomaticWhdFamilyMember', N'@TargetClientId'),
    (N'tb_service.ApplyAutomaticWhdFamilyMember', N'@SourceWhdClientId'),
    (N'tb_service.ApplyAutomaticWhdFamilyMember', N'@ExpectedSourceWhdRowVersion'),
    (N'tb_service.ApplyAutomaticWhdFamilyMember', N'@ExpectedSageCustomerId'),
    (N'tb_service.ApplyAutomaticWhdFamilyMember', N'@MatchScore'),
    (N'tb_app.AdminBeginUserPreview', N'@TargetLoginName'),
    (N'tb_app.AdminBeginUserPreview', N'@ClientInstanceId'),
    (N'tb_app.ActivateReadOnlyPreview', N'@PreviewSessionId'),
    (N'tb_app.AdminEndUserPreview', N'@PreviewSessionId');

IF EXISTS
(
    SELECT 1 FROM @RequiredParameters AS required
    WHERE NOT EXISTS
    (
        SELECT 1 FROM sys.parameters AS parameter_row
        WHERE parameter_row.[object_id] = OBJECT_ID(required.[ProcedureName], N'P')
          AND parameter_row.[name] = required.[ParameterName]
    )
)
BEGIN
    PRINT N'FAIL: the V0007 procedure parameter contract is incomplete.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredAdminProcedures TABLE ([ObjectName] nvarchar(300) NOT NULL PRIMARY KEY);
INSERT INTO @RequiredAdminProcedures([ObjectName]) VALUES
    (N'tb_app.AdminRequestSageSync'),
    (N'tb_app.GetSageSyncStatus'),
    (N'tb_app.AdminListPreviewUsers'),
    (N'tb_app.AdminBeginUserPreview'),
    (N'tb_app.ActivateReadOnlyPreview'),
    (N'tb_app.AdminEndUserPreview');

IF EXISTS
(
    SELECT 1 FROM @RequiredAdminProcedures AS required
    WHERE NOT EXISTS
    (
        SELECT 1 FROM sys.database_permissions AS permission_row
        WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_role_admin')
          AND permission_row.[class] = 1
          AND permission_row.[major_id] = OBJECT_ID(required.[ObjectName], N'P')
          AND permission_row.[permission_name] = N'EXECUTE'
          AND permission_row.[state] IN (N'G', N'W')
    )
)
BEGIN
    PRINT N'FAIL: a required V0007 Admin EXECUTE grant is missing.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1 FROM sys.database_permissions AS permission_row
    WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_role_admin')
      AND permission_row.[class] = 4
      AND permission_row.[major_id] = DATABASE_PRINCIPAL_ID(N'tb_preview_reader')
      AND permission_row.[permission_name] = N'IMPERSONATE'
      AND permission_row.[state] IN (N'G', N'W')
)
BEGIN
    PRINT N'FAIL: the Admin role lacks the narrow preview-reader IMPERSONATE grant.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1 FROM sys.database_permissions AS permission_row
    INNER JOIN sys.database_principals AS grantee
        ON grantee.[principal_id] = permission_row.[grantee_principal_id]
    WHERE permission_row.[class] = 4
      AND permission_row.[major_id] = DATABASE_PRINCIPAL_ID(N'tb_preview_reader')
      AND permission_row.[permission_name] = N'IMPERSONATE'
      AND permission_row.[state] IN (N'G', N'W')
      AND grantee.[name] <> N'tb_role_admin'
)
BEGIN
    PRINT N'FAIL: a principal other than the Admin role can impersonate the preview reader.';
    SET @FailureCount += 1;
END;

DECLARE @LegacySageAdminProcedures TABLE ([ObjectName] nvarchar(300) NOT NULL PRIMARY KEY);
INSERT INTO @LegacySageAdminProcedures([ObjectName]) VALUES
    (N'tb_app.AcquireSyncLease'),
    (N'tb_app.ReleaseSyncLease'),
    (N'tb_app.BeginSyncRun'),
    (N'tb_app.CompleteSyncRun'),
    (N'tb_app.SyncUpsertClient'),
    (N'tb_app.SyncUpsertSageCustomer'),
    (N'tb_app.SyncRemoveStaleSageCustomers'),
    (N'tb_app.SyncUpsertClientExternalIdentity'),
    (N'tb_app.SyncApplySageCustomerSnapshot'),
    (N'tb_app.SyncApplyClientSnapshot');

IF EXISTS
(
    SELECT 1 FROM @LegacySageAdminProcedures AS legacy
    INNER JOIN sys.database_permissions AS permission_row
        ON permission_row.[class] = 1
       AND permission_row.[major_id] = OBJECT_ID(legacy.[ObjectName], N'P')
       AND permission_row.[permission_name] = N'EXECUTE'
       AND permission_row.[state] IN (N'G', N'W')
    WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_role_admin')
)
BEGIN
    PRINT N'FAIL: Admin retains a legacy workstation-side Sage ingestion grant.';
    SET @FailureCount += 1;
END;

DECLARE @ServiceProcedures TABLE ([ObjectName] nvarchar(300) NOT NULL PRIMARY KEY);
INSERT INTO @ServiceProcedures([ObjectName]) VALUES
    (N'tb_service.GetWhdSyncConfiguration'),
    (N'tb_service.ClaimWhdSyncWork'),
    (N'tb_service.RenewWhdSyncLease'),
    (N'tb_service.ApplyWhdClientSnapshot'),
    (N'tb_service.ApplyWhdTicketBatch'),
    (N'tb_service.ApplyWhdTicketStatusSnapshot'),
    (N'tb_service.ApplyWhdTechnicianSnapshot'),
    (N'tb_service.ApplyWhdTechGroupSnapshot'),
    (N'tb_service.CompleteWhdSyncWork'),
    (N'tb_service.GetSageSyncConfiguration'),
    (N'tb_service.ClaimSageSyncWork'),
    (N'tb_service.RenewSageSyncLease'),
    (N'tb_service.ApplySageCustomerSnapshot'),
    (N'tb_service.CompleteSageSyncWork'),
    (N'tb_service.GetAutomaticClientMatchCandidates'),
    (N'tb_service.ApplyAutomaticClientMatch'),
    (N'tb_service.ApplyAutomaticWhdFamilyMember');

IF EXISTS
(
    SELECT 1 FROM @ServiceProcedures AS required
    WHERE NOT EXISTS
    (
        SELECT 1 FROM sys.database_permissions AS permission_row
        WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_role_sync_service')
          AND permission_row.[class] = 1
          AND permission_row.[major_id] = OBJECT_ID(required.[ObjectName], N'P')
          AND permission_row.[permission_name] = N'EXECUTE'
          AND permission_row.[state] IN (N'G', N'W')
    )
)
BEGIN
    PRINT N'FAIL: a required WHD/Sage service EXECUTE grant is missing.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1 FROM sys.database_permissions AS permission_row
    WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_role_sync_service')
      AND permission_row.[state] IN (N'G', N'W')
      AND
      (
          permission_row.[permission_name] IN
              (N'SELECT', N'INSERT', N'UPDATE', N'DELETE', N'ALTER', N'CONTROL', N'TAKE OWNERSHIP', N'IMPERSONATE')
          OR
          (
              permission_row.[permission_name] = N'EXECUTE'
              AND
              (
                  permission_row.[class] <> 1
                  OR NOT EXISTS
                  (
                      SELECT 1 FROM @ServiceProcedures AS allowed
                      WHERE OBJECT_ID(allowed.[ObjectName], N'P') = permission_row.[major_id]
                  )
              )
          )
      )
)
BEGIN
    PRINT N'FAIL: the sync service role has direct data/control or unexpected execution grants.';
    SET @FailureCount += 1;
END;

DECLARE @PreviewReadProcedures TABLE ([ObjectName] nvarchar(300) NOT NULL PRIMARY KEY);
INSERT INTO @PreviewReadProcedures([ObjectName]) VALUES
    (N'tb_app.GetCurrentUserContext'),
    (N'tb_app.GetRepositoryCapabilities'),
    (N'tb_app.SearchClients'),
    (N'tb_app.GetClient'),
    (N'tb_app.SearchTickets'),
    (N'tb_app.GetTicket'),
    (N'tb_app.GetTicketStatusOptions'),
    (N'tb_app.SearchWorkEntries'),
    (N'tb_app.GetWorkEntry'),
    (N'tb_app.GetWorkEntryLinks'),
    (N'tb_app.GetDistinctTags'),
    (N'tb_app.GetTemplates'),
    (N'tb_app.GetCommonLinks'),
    (N'tb_app.GetSettings'),
    (N'tb_app.GetPostingLogs');

IF EXISTS
(
    SELECT 1 FROM @PreviewReadProcedures AS required
    WHERE NOT EXISTS
    (
        SELECT 1 FROM sys.database_permissions AS permission_row
        WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_preview_reader')
          AND permission_row.[class] = 1
          AND permission_row.[major_id] = OBJECT_ID(required.[ObjectName], N'P')
          AND permission_row.[permission_name] = N'EXECUTE'
          AND permission_row.[state] IN (N'G', N'W')
    )
)
BEGIN
    PRINT N'FAIL: a required preview-safe read EXECUTE grant is missing.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1 FROM sys.database_permissions AS permission_row
    WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_preview_reader')
      AND permission_row.[state] IN (N'G', N'W')
      AND NOT
      (
          (
              permission_row.[class] = 0
              AND permission_row.[major_id] = 0
              AND permission_row.[minor_id] = 0
              AND permission_row.[permission_name] = N'CONNECT'
          )
          OR
          (
              permission_row.[class] = 1
              AND permission_row.[minor_id] = 0
              AND permission_row.[permission_name] = N'EXECUTE'
              AND EXISTS
              (
                  SELECT 1 FROM @PreviewReadProcedures AS allowed
                  WHERE OBJECT_ID(allowed.[ObjectName], N'P') = permission_row.[major_id]
              )
          )
      )
)
BEGIN
    PRINT N'FAIL: the preview reader has data/control or unexpected execution grants.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1 FROM sys.database_role_members
    WHERE [member_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_preview_reader')
)
BEGIN
    PRINT N'FAIL: the preview reader must not be a member of any database role.';
    SET @FailureCount += 1;
END;

DECLARE @EnsureDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_security.EnsureCurrentUser', N'P'));
DECLARE @ContextDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetCurrentUserContext', N'P'));
DECLARE @ListPreviewDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminListPreviewUsers', N'P'));
DECLARE @BeginPreviewDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminBeginUserPreview', N'P'));
DECLARE @ActivatePreviewDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.ActivateReadOnlyPreview', N'P'));
DECLARE @TicketAccessDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_security.FilterWhdTicketAccess', N'IF'));
DECLARE @SearchWorkDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SearchWorkEntries', N'P'));
DECLARE @GetWorkDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetWorkEntry', N'P'));
DECLARE @SettingsDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetSettings', N'P'));
DECLARE @PostingLogsDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetPostingLogs', N'P'));

SELECT @EnsureDefinition = REPLACE(REPLACE(REPLACE(@EnsureDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @ContextDefinition = REPLACE(REPLACE(REPLACE(@ContextDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @ListPreviewDefinition = REPLACE(REPLACE(REPLACE(@ListPreviewDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @BeginPreviewDefinition = REPLACE(REPLACE(REPLACE(@BeginPreviewDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @ActivatePreviewDefinition = REPLACE(REPLACE(REPLACE(@ActivatePreviewDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @TicketAccessDefinition = REPLACE(REPLACE(REPLACE(@TicketAccessDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @SearchWorkDefinition = REPLACE(REPLACE(REPLACE(@SearchWorkDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @GetWorkDefinition = REPLACE(REPLACE(REPLACE(@GetWorkDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @SettingsDefinition = REPLACE(REPLACE(REPLACE(@SettingsDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @PostingLogsDefinition = REPLACE(REPLACE(REPLACE(@PostingLogsDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');

IF CHARINDEX(N'SESSION_CONTEXT(N''TechBench.PreviewSessionId'')', @EnsureDefinition) = 0
   OR CHARINDEX(N'IFUSER_NAME()=N''tb_preview_reader''', @EnsureDefinition) = 0
   OR CHARINDEX(N'AdminUserPreviewSessions', @EnsureDefinition) = 0
   OR CHARINDEX(N'target_user.[IsAdmin]=0', @EnsureDefinition) = 0
   OR CHARINDEX(N'target_user.[LastSeenAtUtc]>=DATEADD(hour,-1,SYSUTCDATETIME())', @EnsureDefinition) = 0
BEGIN
    PRINT N'FAIL: EnsureCurrentUser does not securely resolve the server-issued preview target.';
    SET @FailureCount += 1;
END;

DECLARE @RoleRefreshPosition int = CHARINDEX(N'UPDATE[tb_security].[Users]WITH(UPDLOCK,HOLDLOCK)', @EnsureDefinition);
DECLARE @ZeroRoleThrowPosition int = CHARINDEX(N'IF@HasApplicationRole=0THROW51002', @EnsureDefinition);
IF @RoleRefreshPosition = 0
   OR @ZeroRoleThrowPosition <= @RoleRefreshPosition
   OR CHARINDEX(N'[IsTechnician]=@IsTechnician', @EnsureDefinition) = 0
   OR CHARINDEX(N'[IsManager]=@IsManager', @EnsureDefinition) = 0
   OR CHARINDEX(N'[IsAdmin]=@IsAdmin', @EnsureDefinition) = 0
   OR CHARINDEX(N'[IsSyncOperator]=@IsSyncOperator', @EnsureDefinition) = 0
BEGIN
    PRINT N'FAIL: EnsureCurrentUser does not persist refreshed zero role flags before denying access.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'AuthenticatedUserSid', @ContextDefinition) = 0
   OR CHARINDEX(N'IsReadOnlyPreview', @ContextDefinition) = 0
   OR CHARINDEX(N'PreviewSessionId', @ContextDefinition) = 0
   OR CHARINDEX(N'PreviewExpiresAtUtc', @ContextDefinition) = 0
BEGIN
    PRINT N'FAIL: GetCurrentUserContext lacks authenticated/preview context fields.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'IS_ROLEMEMBER(N''tb_role_admin'')<>1', @BeginPreviewDefinition) = 0
   OR CHARINDEX(N'[IsTechnician]=1', @BeginPreviewDefinition) = 0
   OR CHARINDEX(N'[IsAdmin]=0', @BeginPreviewDefinition) = 0
   OR CHARINDEX(N'[LastSeenAtUtc]>=DATEADD(hour,-1,@Now)', @BeginPreviewDefinition) = 0
   OR CHARINDEX(N'DATEADD(minute,30,@Now)', @BeginPreviewDefinition) = 0
BEGIN
    PRINT N'FAIL: AdminBeginUserPreview lacks live Admin, target, or expiry validation.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'sp_set_session_context', @ActivatePreviewDefinition) = 0
   OR CHARINDEX(N'@read_only=1', @ActivatePreviewDefinition) = 0
   OR CHARINDEX(N'IS_ROLEMEMBER(N''tb_role_admin'')<>1', @ActivatePreviewDefinition) = 0
   OR CHARINDEX(N'target_user.[LastSeenAtUtc]>=DATEADD(hour,-1,SYSUTCDATETIME())', @ActivatePreviewDefinition) = 0
BEGIN
    PRINT N'FAIL: ActivateReadOnlyPreview does not set a read-only server-issued context after live Admin validation.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'[LastSeenAtUtc]>=DATEADD(hour,-1,SYSUTCDATETIME())', @ListPreviewDefinition) = 0
BEGIN
    PRINT N'FAIL: AdminListPreviewUsers includes authorization records older than one hour.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'USER_NAME()=N''tb_preview_reader''', @TicketAccessDefinition) = 0
   OR CHARINDEX(N'USER_NAME()<>N''tb_preview_reader''', @TicketAccessDefinition) = 0
   OR CHARINDEX(N'SESSION_CONTEXT(N''TechBench.PreviewSessionId'')ISNULL', @TicketAccessDefinition) = 0
   OR CHARINDEX(N'AdminUserPreviewSessions', @TicketAccessDefinition) = 0
   OR CHARINDEX(N'mapping.[WindowsSid]=preview_session.[TargetWindowsSid]', @TicketAccessDefinition) = 0
   OR CHARINDEX(N'target_user.[LastSeenAtUtc]>=DATEADD(hour,-1,SYSUTCDATETIME())', @TicketAccessDefinition) = 0
BEGIN
    PRINT N'FAIL: WHD row security does not prevent the authenticated Admin bypass from winning in preview.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'@IsReadOnlyPreview=0', @SearchWorkDefinition) = 0
   OR CHARINDEX(N'@IsReadOnlyPreview=0', @GetWorkDefinition) = 0
   OR CHARINDEX(N'@IsReadOnlyPreview=0', @SettingsDefinition) = 0
   OR CHARINDEX(N'WHEN@IsReadOnlyPreview=1THENNULLELSEwork_entry.[LastError]ENDAS[LastError]', @SearchWorkDefinition) = 0
   OR CHARINDEX(N'WHEN@IsReadOnlyPreview=1THENNULLELSEwork_entry.[LastError]ENDAS[LastError]', @GetWorkDefinition) = 0
BEGIN
    PRINT N'FAIL: preview-safe reads do not mask personal notes, posting errors, or user-owned settings.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'WHEN@IsReadOnlyPreview=1THENN''''ELSEposting_log.[Payload]', @PostingLogsDefinition) = 0
   OR CHARINDEX(N'WHEN@IsReadOnlyPreview=0THENposting_log.[Message]', @PostingLogsDefinition) = 0
   OR CHARINDEX(N'@IsReadOnlyPreview=0AND(posting_log.[Message]LIKE@KeywordPatternORposting_log.[Payload]LIKE@KeywordPattern)', @PostingLogsDefinition) = 0
BEGIN
    PRINT N'FAIL: preview-safe posting history does not redact payload/message content or block keyword inference.';
    SET @FailureCount += 1;
END;

DECLARE @RequestSageDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminRequestSageSync', N'P'));
DECLARE @ClaimSageDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_service.ClaimSageSyncWork', N'P'));
DECLARE @ApplySageDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_service.ApplySageCustomerSnapshot', N'P'));
DECLARE @CompleteSageDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_service.CompleteSageSyncWork', N'P'));
DECLARE @SageConfigDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_service.GetSageSyncConfiguration', N'P'));
SELECT @RequestSageDefinition = REPLACE(REPLACE(REPLACE(@RequestSageDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @ClaimSageDefinition = REPLACE(REPLACE(REPLACE(@ClaimSageDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @ApplySageDefinition = REPLACE(REPLACE(REPLACE(@ApplySageDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @CompleteSageDefinition = REPLACE(REPLACE(REPLACE(@CompleteSageDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @SageConfigDefinition = REPLACE(REPLACE(REPLACE(@SageConfigDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');

IF CHARINDEX(N'IS_ROLEMEMBER(N''tb_role_admin'')<>1', @RequestSageDefinition) = 0
   OR CHARINDEX(N'sp_getapplock', @RequestSageDefinition) = 0
   OR CHARINDEX(N'INSERTINTO[tb_sync].[SageSyncRequests]', @RequestSageDefinition) = 0
   OR CHARINDEX(N'@AllowLargeRemovalbit=0', @RequestSageDefinition) = 0
   OR CHARINDEX(N'@ConfirmedRequestIduniqueidentifier=NULL', @RequestSageDefinition) = 0
   OR CHARINDEX(N'[AllowLargeRemoval]', @RequestSageDefinition) = 0
   OR CHARINDEX(N'[RequiresLargeRemovalConfirmation]=1', @RequestSageDefinition) = 0
   OR CHARINDEX(N'[CompletedAtUtc]>=DATEADD(hour,-1,@Now)', @RequestSageDefinition) = 0
BEGIN
    PRINT N'FAIL: the Admin-only Sage request queue is incomplete.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'sp_getapplock', @ClaimSageDefinition) = 0
   OR CHARINDEX(N'READCOMMITTEDLOCK', @ClaimSageDefinition) = 0
   OR CHARINDEX(N'INSERTINTO[tb_sync].[SageSyncRequests]', @ClaimSageDefinition) > 0
BEGIN
    PRINT N'FAIL: ClaimSageSyncWork is not a manual-queue-only, RCSI-safe lease claim.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'COALESCE(ISJSON(@Json),0)<>1', @ApplySageDefinition) = 0
   OR CHARINDEX(N'@ReadCount=0', @ApplySageDefinition) = 0
   OR CHARINDEX(N'SageSyncLeases', @ApplySageDefinition) = 0
   OR CHARINDEX(N'BEGINTRANSACTION', @ApplySageDefinition) = 0
   OR CHARINDEX(N'ClientExternalIdentities', @ApplySageDefinition) = 0
   OR CHARINDEX(N'@RawSnapshotTABLE', @ApplySageDefinition) = 0
   OR CHARINDEX(N'[JsonType]<>5', @ApplySageDefinition) = 0
   OR CHARINDEX(N'[CustomerIdCount]<>1', @ApplySageDefinition) = 0
   OR CHARINDEX(N'LEN(LTRIM(RTRIM([CustomerId])))>120', @ApplySageDefinition) = 0
   OR CHARINDEX(N'HAVINGCOUNT(*)>1', @ApplySageDefinition) = 0
   OR CHARINDEX(N'@ConfirmationMatches<>1', @ApplySageDefinition) = 0
   OR CHARINDEX(N'@ExistingCount>=20', @ApplySageDefinition) = 0
   OR CHARINDEX(N'@StaleCount>=10', @ApplySageDefinition) = 0
   OR CHARINDEX(N'confirmed_request.[ExistingCount]=@ExistingCount', @ApplySageDefinition) = 0
   OR CHARINDEX(N'confirmed_request.[ReadCount]=@ReadCount', @ApplySageDefinition) = 0
   OR CHARINDEX(N'confirmed_request.[StaleCount]=@StaleCount', @ApplySageDefinition) = 0
   OR CHARINDEX(N'[RequiresLargeRemovalConfirmation]=1', @ApplySageDefinition) = 0
BEGIN
    PRINT N'FAIL: ApplySageCustomerSnapshot lacks lossless validation, destructive-delta confirmation, lease enforcement, or atomic identity reconciliation.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'@Succeeded=1AND@ReadCount=0', @CompleteSageDefinition) = 0
   OR CHARINDEX(N'[tb_sync].[SageSyncHealth]', @CompleteSageDefinition) = 0
   OR CHARINDEX(N'DELETEFROM[tb_sync].[SageSyncLeases]', @CompleteSageDefinition) = 0
BEGIN
    PRINT N'FAIL: CompleteSageSyncWork lacks apply-before-success, health, or lease completion safeguards.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'Sage.SyncDsn', @SageConfigDefinition) = 0
   OR CHARINDEX(N'Sage.SyncUsername', @SageConfigDefinition) = 0
BEGIN
    PRINT N'FAIL: the service-owned Sage configuration contract is incomplete.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'CONVERT(int,7)', REPLACE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities')), N' ', N'')) = 0
BEGIN
    PRINT N'FAIL: GetRepositoryCapabilities does not report schema version 7.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS (SELECT 1 FROM [tb_sync].[SageSyncHealth] WHERE [HealthId] = 1)
BEGIN
    PRINT N'FAIL: the Sage synchronization health singleton is missing.';
    SET @FailureCount += 1;
END;

IF @FailureCount > 0
BEGIN
    RAISERROR(
        N'TechBench V0007 server-owned Sage and Admin-preview verification failed with %d issue(s).',
        16, 1, @FailureCount);
    RETURN;
END;

PRINT N'TechBench V0007 server-owned Sage and Admin-preview verification passed.';
SELECT
    DB_NAME() AS [DatabaseName],
    MAX([SchemaVersion]) AS [SchemaVersion],
    MAX(CASE
        WHEN [MigrationId] = N'SqlServer2016.ServerOwnedSageAndAdminPreview.0007'
            THEN [AppliedAtUtc]
        END) AS [ServerOwnedSageAndAdminPreviewAppliedAtUtc]
FROM [tb_deploy].[SchemaMigrations];
GO
