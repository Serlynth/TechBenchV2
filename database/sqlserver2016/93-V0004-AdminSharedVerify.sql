:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @FailureCount int = 0;
DECLARE @InstalledSchemaVersion int =
(
    SELECT MAX([SchemaVersion])
    FROM [tb_deploy].[SchemaMigrations]
);

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.AdminOwnedSharedConfig.0004'
      AND [SchemaVersion] = 4
      AND [ReleaseVersion] = N'2.0.0-alpha.4'
)
BEGIN
    PRINT N'FAIL: AdminOwnedSharedConfig.0004 migration marker is missing or invalid.';
    SET @FailureCount += 1;
END;

IF @InstalledSchemaVersion NOT IN (4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14)
BEGIN
    PRINT N'FAIL: V0004 verification supports installed schema version 4, 5, 6, 7, 8, or 9.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
    FROM [tb_user].[UserSettings]
    WHERE [SettingKey] NOT IN
    (
        N'Whd.Username',
        N'Sage.Username',
        N'Sage.EmployeeId',
        N'Sage.ActivityItemId',
        N'Whd.ApiToken',
        N'Sage.Password',
        N'Sage.DefaultCustomerId'
    )
)
BEGIN
    PRINT N'FAIL: An unauthorized setting remains in per-user SQL storage.';
    SET @FailureCount += 1;
END;

DECLARE @GetCurrentAccessDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_security.GetCurrentAccess'));

IF @GetCurrentAccessDefinition IS NULL
   OR CHARINDEX(N'IF @IsAdmin <> 1', @GetCurrentAccessDefinition) = 0
   OR CHARINDEX(N'SET @IsSyncOperator = 0', @GetCurrentAccessDefinition) = 0
BEGIN
    PRINT N'FAIL: GetCurrentAccess does not mask legacy Sync Operator mutation authority for non-Admins.';
    SET @FailureCount += 1;
END;

DECLARE @AdminCheckedSyncLifecycle TABLE
(
    [ObjectName] nvarchar(300) NOT NULL PRIMARY KEY
);

INSERT INTO @AdminCheckedSyncLifecycle([ObjectName])
VALUES
    (N'tb_app.ReleaseSyncLease'),
    (N'tb_app.BeginSyncRun'),
    (N'tb_app.CompleteSyncRun'),
    (N'tb_security.RenewSyncRunLease');

DECLARE @MissingSyncRuntimeAdminCheckCount int =
(
    SELECT COUNT(*)
    FROM @AdminCheckedSyncLifecycle AS sync_procedure
    WHERE CHARINDEX(
              N'IF @IsAdmin <> 1',
              OBJECT_DEFINITION(OBJECT_ID(sync_procedure.[ObjectName]))) = 0
);

IF @MissingSyncRuntimeAdminCheckCount > 0
BEGIN
    PRINT N'FAIL: A synchronization lifecycle procedure lacks its runtime Admin check.';
    SET @FailureCount += @MissingSyncRuntimeAdminCheckCount;
END;

DECLARE @SnapshotRuntimeContracts TABLE
(
    [ObjectName] nvarchar(300) NOT NULL PRIMARY KEY
);

INSERT INTO @SnapshotRuntimeContracts([ObjectName])
VALUES
    (N'tb_app.SyncApplyClientSnapshot'),
    (N'tb_app.SyncApplyTicketSnapshot'),
    (N'tb_app.SyncApplyTicketStatusSnapshot'),
    (N'tb_app.SyncApplySageCustomerSnapshot');

DECLARE @MissingSnapshotRuntimeContractCount int =
(
    SELECT COUNT(*)
    FROM @SnapshotRuntimeContracts AS snapshot_procedure
    WHERE CHARINDEX(
              N'[tb_security].[RenewSyncRunLease]',
              OBJECT_DEFINITION(OBJECT_ID(snapshot_procedure.[ObjectName]))) = 0
);

IF @MissingSnapshotRuntimeContractCount > 0
BEGIN
    PRINT N'FAIL: A snapshot sync procedure bypasses the Admin-checked lease renewal boundary.';
    SET @FailureCount += @MissingSnapshotRuntimeContractCount;
END;

IF CHARINDEX(
       N'CONVERT(int,' + CONVERT(nvarchar(10), @InstalledSchemaVersion) + N')',
       REPLACE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities')), N' ', N'')) = 0
BEGIN
    PRINT N'FAIL: GetRepositoryCapabilities does not report the installed schema version.';
    SET @FailureCount += 1;
END;

DECLARE @EnsureDefaultsDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.EnsureWorkspaceDefaults'));
DECLARE @CatalogGatePosition int =
    CHARINDEX(N'IF @InitializeWorkspaceCatalogs = 1', @EnsureDefaultsDefinition);
DECLARE @AutoDefaultsInsertPosition int =
    CHARINDEX(
        N'FROM @DefaultOrganizationSettings AS default_setting',
        @EnsureDefaultsDefinition);
DECLARE @CommonLinkSeedPosition int =
    CHARINDEX(N'INSERT INTO [tb_data].[CommonLinks]', @EnsureDefaultsDefinition);
DECLARE @TemplateSeedPosition int =
    CHARINDEX(N'INSERT INTO [tb_data].[Templates]', @EnsureDefaultsDefinition);
DECLARE @FirstOrganizationSettingInsertPosition int =
    CHARINDEX(
        N'INSERT INTO [tb_data].[OrganizationSettings]',
        @EnsureDefaultsDefinition);
DECLARE @SecondOrganizationSettingInsertPosition int =
    CHARINDEX(
        N'INSERT INTO [tb_data].[OrganizationSettings]',
        @EnsureDefaultsDefinition,
        @FirstOrganizationSettingInsertPosition + 1);
DECLARE @MarkerLookupPosition int =
    CHARINDEX(N'WorkspaceDefaults.Initialized', @EnsureDefaultsDefinition);
DECLARE @MarkerInsertPosition int =
    CHARINDEX(
        N'WorkspaceDefaults.Initialized',
        @EnsureDefaultsDefinition,
        @MarkerLookupPosition + 1);
DECLARE @MarkerActorPosition int =
    CHARINDEX(N'@UserSid', @EnsureDefaultsDefinition, @MarkerInsertPosition);

IF @EnsureDefaultsDefinition IS NULL
   OR CHARINDEX(N'@IsAdmin <> 1', @EnsureDefaultsDefinition) = 0
   OR CHARINDEX(N'WHERE NOT EXISTS', @EnsureDefaultsDefinition) = 0
   OR CHARINDEX(N'UPDATE [tb_data].[CommonLinks]', @EnsureDefaultsDefinition) > 0
   OR CHARINDEX(N'UPDATE [tb_data].[Templates]', @EnsureDefaultsDefinition) > 0
   OR CHARINDEX(N'UPDATE [tb_data].[OrganizationSettings]', @EnsureDefaultsDefinition) > 0
   OR CHARINDEX(N'[tb_data].[OrganizationSettings]', @EnsureDefaultsDefinition) = 0
   OR CHARINDEX(N'Whd.AutoSyncEnabled'', N''true', @EnsureDefaultsDefinition) = 0
   OR CHARINDEX(N'Whd.AutoSyncMinutes'', N''5', @EnsureDefaultsDefinition) = 0
   OR @CatalogGatePosition = 0
   OR @AutoDefaultsInsertPosition = 0
   OR @FirstOrganizationSettingInsertPosition = 0
   OR @SecondOrganizationSettingInsertPosition = 0
   OR @AutoDefaultsInsertPosition >= @CatalogGatePosition
   OR @FirstOrganizationSettingInsertPosition >= @CatalogGatePosition
   OR @CommonLinkSeedPosition <= @CatalogGatePosition
   OR @TemplateSeedPosition <= @CatalogGatePosition
   OR @SecondOrganizationSettingInsertPosition <= @CatalogGatePosition
   OR @MarkerLookupPosition <= @AutoDefaultsInsertPosition
   OR @MarkerLookupPosition >= @CatalogGatePosition
   OR @MarkerInsertPosition <= @CatalogGatePosition
   OR CHARINDEX(N'N''4''', @EnsureDefaultsDefinition, @MarkerInsertPosition) = 0
   OR @MarkerActorPosition <= @MarkerInsertPosition
   OR @MarkerActorPosition > @MarkerInsertPosition + 300
BEGIN
    PRINT N'FAIL: EnsureWorkspaceDefaults does not enforce one-time catalog seeding plus recurring insert-missing auto-sync defaults.';
    SET @FailureCount += 1;
END;

DECLARE @WorkspaceDefaultTokens TABLE
(
    [Token] nvarchar(200) NOT NULL PRIMARY KEY
);

INSERT INTO @WorkspaceDefaultTokens([Token])
VALUES
    (N'watchguard-cloud'),
    (N'microsoft-365-admin'),
    (N'barracuda-cloud-control'),
    (N'eset-protect'),
    (N'email2phone'),
    (N'godaddy-dns'),
    (N'network-solutions-dns'),
    (N'Exchange certificate update'),
    (N'VPN troubleshooting'),
    (N'Microsoft 365 licensing'),
    (N'Firewall rule change'),
    (N'Password reset'),
    (N'Backup verification'),
    (N'Server reboot/maintenance');

DECLARE @MissingWorkspaceDefaultTokenCount int =
(
    SELECT COUNT(*)
    FROM @WorkspaceDefaultTokens AS expected_default
    WHERE CHARINDEX(expected_default.[Token], @EnsureDefaultsDefinition) = 0
);

IF @MissingWorkspaceDefaultTokenCount > 0
BEGIN
    PRINT N'FAIL: EnsureWorkspaceDefaults is missing one or more required shared defaults.';
    SET @FailureCount += @MissingWorkspaceDefaultTokenCount;
END;

DECLARE @SaveCommonLinkDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveCommonLink'));
DECLARE @DeleteCommonLinkDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteCommonLink'));

IF @SaveCommonLinkDefinition IS NULL
   OR CHARINDEX(N'@IsAdmin <> 1', @SaveCommonLinkDefinition) = 0
   OR CHARINDEX(N'schema version 4', @SaveCommonLinkDefinition) = 0
   OR CHARINDEX(N'Built-in Common Links cannot be changed', @SaveCommonLinkDefinition) > 0
   OR @DeleteCommonLinkDefinition IS NULL
   OR CHARINDEX(N'@IsAdmin <> 1', @DeleteCommonLinkDefinition) = 0
   OR CHARINDEX(N'AND [BuiltInKey] IS NOT NULL', @DeleteCommonLinkDefinition) = 0
BEGIN
    PRINT N'FAIL: Common Links are not Admin-managed, editable, and protected from built-in deletion.';
    SET @FailureCount += 1;
END;

DECLARE @SaveClientAliasDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveClientAlias'));
DECLARE @DeleteClientAliasDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteClientAlias'));

IF @SaveClientAliasDefinition IS NULL
   OR CHARINDEX(N'@IsAdmin <> 1', @SaveClientAliasDefinition) = 0
   OR CHARINDEX(N'UPDLOCK', @SaveClientAliasDefinition) = 0
   OR CHARINDEX(N'HOLDLOCK', @SaveClientAliasDefinition) = 0
   OR @DeleteClientAliasDefinition IS NULL
   OR CHARINDEX(N'@IsAdmin <> 1', @DeleteClientAliasDefinition) = 0
BEGIN
    PRINT N'FAIL: Client-alias create, change, and delete are not Admin-only.';
    SET @FailureCount += 1;
END;

DECLARE @SaveUserSettingDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveUserSetting'));
DECLARE @DeleteUserSettingDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteUserSetting'));

IF @SaveUserSettingDefinition IS NULL
   OR CHARINDEX(N'@SettingKey NOT IN', @SaveUserSettingDefinition) = 0
   OR CHARINDEX(N'Whd.Username', @SaveUserSettingDefinition) = 0
   OR CHARINDEX(N'Sage.Username', @SaveUserSettingDefinition) = 0
   OR CHARINDEX(N'Sage.EmployeeId', @SaveUserSettingDefinition) = 0
   OR CHARINDEX(N'Whd.ApiToken', @SaveUserSettingDefinition) > 0
   OR CHARINDEX(N'Sage.Password', @SaveUserSettingDefinition) > 0
   OR CHARINDEX(N'Sage.DefaultCustomerId', @SaveUserSettingDefinition) > 0
BEGIN
    PRINT N'FAIL: SaveUserSetting does not enforce the V0004 identity-setting allowlist.';
    SET @FailureCount += 1;
END;

DECLARE @DeletableUserSettingTokens TABLE
(
    [Token] nvarchar(200) NOT NULL PRIMARY KEY
);

INSERT INTO @DeletableUserSettingTokens([Token])
VALUES
    (N'Whd.Username'),
    (N'Sage.Username'),
    (N'Sage.EmployeeId'),
    (N'Whd.ApiToken'),
    (N'Sage.Password'),
    (N'Sage.DefaultCustomerId');

IF @DeleteUserSettingDefinition IS NULL
   OR CHARINDEX(N'@SettingKey NOT IN', @DeleteUserSettingDefinition) = 0
BEGIN
    PRINT N'FAIL: DeleteUserSetting does not enforce an allowlist.';
    SET @FailureCount += 1;
END;

DECLARE @MissingDeletableSettingTokenCount int =
(
    SELECT COUNT(*)
    FROM @DeletableUserSettingTokens AS expected_key
    WHERE CHARINDEX(expected_key.[Token], @DeleteUserSettingDefinition) = 0
);

IF @MissingDeletableSettingTokenCount > 0
BEGIN
    PRINT N'FAIL: DeleteUserSetting cannot remove every approved identity or legacy migration key.';
    SET @FailureCount += @MissingDeletableSettingTokenCount;
END;

DECLARE @SaveWorkEntryDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveWorkEntry'));

IF @SaveWorkEntryDefinition IS NULL
   OR CHARINDEX(N'[tb_data].[WorkEntries]', @SaveWorkEntryDefinition) = 0
   OR CHARINDEX(N'[tb_data].[OrganizationTags]', @SaveWorkEntryDefinition) > 0
BEGIN
    PRINT N'FAIL: SaveWorkEntry still publishes into the Admin-managed organization-tag catalog.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredV4Procedures TABLE
(
    [ObjectName] nvarchar(300) NOT NULL PRIMARY KEY
);

INSERT INTO @RequiredV4Procedures([ObjectName])
VALUES
    (N'tb_app.AdminGetOrganizationTags'),
    (N'tb_app.AdminSaveOrganizationTag'),
    (N'tb_app.AdminDeleteOrganizationTag');

DECLARE @MissingV4ProcedureCount int =
(
    SELECT COUNT(*)
    FROM @RequiredV4Procedures AS required_procedure
    WHERE OBJECT_ID(required_procedure.[ObjectName], N'P') IS NULL
);

IF @MissingV4ProcedureCount > 0
BEGIN
    PRINT N'FAIL: One or more V0004 organization-tag Admin procedures are missing.';
    SET @FailureCount += @MissingV4ProcedureCount;
END;

DECLARE @AdminGetTagsDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminGetOrganizationTags'));
DECLARE @AdminSaveTagDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminSaveOrganizationTag'));
DECLARE @AdminDeleteTagDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminDeleteOrganizationTag'));

IF @AdminGetTagsDefinition IS NULL
   OR CHARINDEX(N'@IsAdmin <> 1', @AdminGetTagsDefinition) = 0
   OR CHARINDEX(N'[CreatedAtUtc] AS [UpdatedAt]', @AdminGetTagsDefinition) = 0
   OR CHARINDEX(N'[RowVersion]', @AdminGetTagsDefinition) = 0
BEGIN
    PRINT N'FAIL: AdminGetOrganizationTags does not enforce Admin access or return its concurrency contract.';
    SET @FailureCount += 1;
END;

IF @AdminSaveTagDefinition IS NULL
   OR CHARINDEX(N'@IsAdmin <> 1', @AdminSaveTagDefinition) = 0
   OR CHARINDEX(N'@ExpectedRowVersion binary(8) = NULL', @AdminSaveTagDefinition) = 0
   OR CHARINDEX(N'@RequestId uniqueidentifier = NULL', @AdminSaveTagDefinition) = 0
   OR CHARINDEX(N'SHA2_256', @AdminSaveTagDefinition) = 0
   OR CHARINDEX(N'UPDLOCK', @AdminSaveTagDefinition) = 0
   OR CHARINDEX(N'HOLDLOCK', @AdminSaveTagDefinition) = 0
   OR CHARINDEX(N'[RowVersion] = @ExpectedRowVersion', @AdminSaveTagDefinition) = 0
BEGIN
    PRINT N'FAIL: AdminSaveOrganizationTag does not enforce the Admin/concurrency/canonical-hash contract.';
    SET @FailureCount += 1;
END;

IF @AdminDeleteTagDefinition IS NULL
   OR CHARINDEX(N'@IsAdmin <> 1', @AdminDeleteTagDefinition) = 0
   OR CHARINDEX(N'@ExpectedRowVersion binary(8)', @AdminDeleteTagDefinition) = 0
   OR CHARINDEX(N'@RequestId uniqueidentifier = NULL', @AdminDeleteTagDefinition) = 0
   OR CHARINDEX(N'[RowVersion] = @ExpectedRowVersion', @AdminDeleteTagDefinition) = 0
BEGIN
    PRINT N'FAIL: AdminDeleteOrganizationTag does not enforce the Admin/concurrency contract.';
    SET @FailureCount += 1;
END;

IF
(
    SELECT COUNT(*)
    FROM sys.parameters
    WHERE [object_id] = OBJECT_ID(N'tb_app.AdminSaveOrganizationTag')
) <> 4
   OR NOT EXISTS
   (
       SELECT 1
       FROM sys.parameters
       WHERE [object_id] = OBJECT_ID(N'tb_app.AdminSaveOrganizationTag')
         AND [parameter_id] = 1
         AND [name] = N'@Id'
         AND TYPE_NAME([user_type_id]) = N'int'
   )
   OR NOT EXISTS
   (
       SELECT 1
       FROM sys.parameters
       WHERE [object_id] = OBJECT_ID(N'tb_app.AdminSaveOrganizationTag')
         AND [parameter_id] = 2
         AND [name] = N'@Tag'
         AND TYPE_NAME([user_type_id]) = N'nvarchar'
         AND [max_length] = 2000
   )
   OR NOT EXISTS
   (
       SELECT 1
       FROM sys.parameters
       WHERE [object_id] = OBJECT_ID(N'tb_app.AdminSaveOrganizationTag')
         AND [parameter_id] = 3
         AND [name] = N'@ExpectedRowVersion'
         AND TYPE_NAME([user_type_id]) = N'binary'
         AND [max_length] = 8
   )
   OR NOT EXISTS
   (
       SELECT 1
       FROM sys.parameters
       WHERE [object_id] = OBJECT_ID(N'tb_app.AdminSaveOrganizationTag')
         AND [parameter_id] = 4
         AND [name] = N'@RequestId'
         AND TYPE_NAME([user_type_id]) = N'uniqueidentifier'
   )
BEGIN
    PRINT N'FAIL: AdminSaveOrganizationTag parameter metadata does not match the desktop contract.';
    SET @FailureCount += 1;
END;

IF
(
    SELECT COUNT(*)
    FROM sys.parameters
    WHERE [object_id] = OBJECT_ID(N'tb_app.AdminDeleteOrganizationTag')
) <> 3
   OR NOT EXISTS
   (
       SELECT 1
       FROM sys.parameters
       WHERE [object_id] = OBJECT_ID(N'tb_app.AdminDeleteOrganizationTag')
         AND [parameter_id] = 1
         AND [name] = N'@Id'
         AND TYPE_NAME([user_type_id]) = N'int'
   )
   OR NOT EXISTS
   (
       SELECT 1
       FROM sys.parameters
       WHERE [object_id] = OBJECT_ID(N'tb_app.AdminDeleteOrganizationTag')
         AND [parameter_id] = 2
         AND [name] = N'@ExpectedRowVersion'
         AND TYPE_NAME([user_type_id]) = N'binary'
         AND [max_length] = 8
   )
   OR NOT EXISTS
   (
       SELECT 1
       FROM sys.parameters
       WHERE [object_id] = OBJECT_ID(N'tb_app.AdminDeleteOrganizationTag')
         AND [parameter_id] = 3
         AND [name] = N'@RequestId'
         AND TYPE_NAME([user_type_id]) = N'uniqueidentifier'
   )
BEGIN
    PRINT N'FAIL: AdminDeleteOrganizationTag parameter metadata does not match the desktop contract.';
    SET @FailureCount += 1;
END;

DECLARE @ExpectedGrants TABLE
(
    [RoleName] sysname NOT NULL,
    [ObjectName] nvarchar(300) NOT NULL,
    PRIMARY KEY ([RoleName], [ObjectName])
);

INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
VALUES
    (N'tb_role_user', N'tb_app.GetRepositoryCapabilities'),
    (N'tb_role_user', N'tb_app.GetTemplates'),
    (N'tb_role_user', N'tb_app.GetCommonLinks'),
    (N'tb_role_user', N'tb_app.GetClientAliases'),
    (N'tb_role_user', N'tb_app.GetDistinctTags'),
    (N'tb_role_user', N'tb_app.GetSettings'),
    (N'tb_role_user', N'tb_app.SaveUserSetting'),
    (N'tb_role_user', N'tb_app.DeleteUserSetting'),
    (N'tb_role_user', N'tb_app.SaveWorkEntry'),
    (N'tb_role_admin', N'tb_app.EnsureWorkspaceDefaults'),
    (N'tb_role_admin', N'tb_app.SaveTemplate'),
    (N'tb_role_admin', N'tb_app.DeleteTemplate'),
    (N'tb_role_admin', N'tb_app.SaveCommonLink'),
    (N'tb_role_admin', N'tb_app.DeleteCommonLink'),
    (N'tb_role_admin', N'tb_app.SaveClientAlias'),
    (N'tb_role_admin', N'tb_app.DeleteClientAlias'),
    (N'tb_role_admin', N'tb_app.AdminGetOrganizationTags'),
    (N'tb_role_admin', N'tb_app.AdminSaveOrganizationTag'),
    (N'tb_role_admin', N'tb_app.AdminDeleteOrganizationTag'),
    (N'tb_role_admin', N'tb_app.AdminSaveOrganizationSetting'),
    (N'tb_role_admin', N'tb_app.AdminDeleteOrganizationSetting'),
    (N'tb_role_admin', N'tb_app.AdminSaveExternalMapping'),
    (N'tb_role_admin', N'tb_app.AdminMergeClients'),
    (N'tb_role_admin', N'tb_app.ReconcileClientMatches'),
    (N'tb_role_sync_operator', N'tb_app.GetSyncRuns');

/* V0007 moves organization-wide Sage ingestion to tb_role_sync_service. */
IF @InstalledSchemaVersion < 7
BEGIN
    INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
    VALUES
        (N'tb_role_admin', N'tb_app.AcquireSyncLease'),
        (N'tb_role_admin', N'tb_app.ReleaseSyncLease'),
        (N'tb_role_admin', N'tb_app.BeginSyncRun'),
        (N'tb_role_admin', N'tb_app.CompleteSyncRun'),
        (N'tb_role_admin', N'tb_app.SyncApplySageCustomerSnapshot'),
        (N'tb_role_admin', N'tb_app.SyncUpsertSageCustomer'),
        (N'tb_role_admin', N'tb_app.SyncRemoveStaleSageCustomers'),
        (N'tb_role_admin', N'tb_app.SyncUpsertClientExternalIdentity');
END;

/* V0006 moves organization-wide WHD mutations to tb_role_sync_service. */
IF @InstalledSchemaVersion < 6
BEGIN
    INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
    VALUES
        (N'tb_role_admin', N'tb_app.SyncApplyClientSnapshot'),
        (N'tb_role_admin', N'tb_app.SyncApplyTicketSnapshot'),
        (N'tb_role_admin', N'tb_app.SyncApplyTicketStatusSnapshot'),
        (N'tb_role_admin', N'tb_app.SyncUpsertClient'),
        (N'tb_role_admin', N'tb_app.SyncUpsertTicketStatusOption'),
        (N'tb_role_admin', N'tb_app.SyncUpsertTicket');
END;

DECLARE @MissingGrantCount int =
(
    SELECT COUNT(*)
    FROM @ExpectedGrants AS expected_grant
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.database_permissions AS permission
        INNER JOIN sys.database_principals AS grantee
            ON grantee.[principal_id] = permission.[grantee_principal_id]
        WHERE grantee.[name] = expected_grant.[RoleName]
          AND permission.[class] = 1
          AND permission.[major_id] = OBJECT_ID(expected_grant.[ObjectName])
          AND permission.[permission_name] = N'EXECUTE'
          AND permission.[state] IN (N'G', N'W')
    )
);

IF @MissingGrantCount > 0
BEGIN
    PRINT N'FAIL: One or more required V0004 procedure grants are missing.';
    SET @FailureCount += @MissingGrantCount;
END;

DECLARE @SharedMutationProcedures TABLE
(
    [ObjectName] nvarchar(300) NOT NULL PRIMARY KEY
);

INSERT INTO @SharedMutationProcedures([ObjectName])
VALUES
    (N'tb_app.EnsureWorkspaceDefaults'),
    (N'tb_app.SaveTemplate'),
    (N'tb_app.DeleteTemplate'),
    (N'tb_app.AdminSaveTemplate'),
    (N'tb_app.AdminDeleteTemplate'),
    (N'tb_app.SaveCommonLink'),
    (N'tb_app.DeleteCommonLink'),
    (N'tb_app.AdminSaveCommonLink'),
    (N'tb_app.AdminDeleteCommonLink'),
    (N'tb_app.SaveClientAlias'),
    (N'tb_app.DeleteClientAlias'),
    (N'tb_app.AdminSaveClientAlias'),
    (N'tb_app.AdminDeleteClientAlias'),
    (N'tb_app.AdminGetOrganizationTags'),
    (N'tb_app.AdminSaveOrganizationTag'),
    (N'tb_app.AdminDeleteOrganizationTag'),
    (N'tb_app.AdminSaveOrganizationSetting'),
    (N'tb_app.AdminDeleteOrganizationSetting'),
    (N'tb_app.AdminSaveExternalMapping'),
    (N'tb_app.AdminMergeClients'),
    (N'tb_app.ReconcileClientMatches'),
    (N'tb_app.SyncUpsertClient'),
    (N'tb_app.SyncUpsertSageCustomer'),
    (N'tb_app.SyncRemoveStaleSageCustomers'),
    (N'tb_app.SyncUpsertClientExternalIdentity'),
    (N'tb_app.SyncUpsertTicketStatusOption'),
    (N'tb_app.SyncUpsertTicket'),
    (N'tb_app.AcquireSyncLease'),
    (N'tb_app.ReleaseSyncLease'),
    (N'tb_app.BeginSyncRun'),
    (N'tb_app.CompleteSyncRun'),
    (N'tb_app.SyncApplyClientSnapshot'),
    (N'tb_app.SyncApplyTicketSnapshot'),
    (N'tb_app.SyncApplyTicketStatusSnapshot'),
    (N'tb_app.SyncApplySageCustomerSnapshot');

DECLARE @ForbiddenMutationGrantCount int =
(
    SELECT COUNT(*)
    FROM @SharedMutationProcedures AS shared_procedure
    INNER JOIN sys.database_permissions AS permission
        ON permission.[class] = 1
       AND permission.[major_id] = OBJECT_ID(shared_procedure.[ObjectName])
       AND permission.[permission_name] = N'EXECUTE'
       AND permission.[state] IN (N'G', N'W')
    INNER JOIN sys.database_principals AS grantee
        ON grantee.[principal_id] = permission.[grantee_principal_id]
    WHERE grantee.[name] IN
    (
        N'tb_role_user',
        N'tb_role_manager',
        N'tb_role_sync_operator'
    )
);

IF @ForbiddenMutationGrantCount > 0
BEGIN
    PRINT N'FAIL: A non-Admin role retains a shared-configuration or sync mutation grant.';
    SET @FailureCount += @ForbiddenMutationGrantCount;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission
    INNER JOIN sys.database_principals AS grantee
        ON grantee.[principal_id] = permission.[grantee_principal_id]
    LEFT JOIN sys.objects AS secured_object
        ON permission.[class] = 1
       AND secured_object.[object_id] = permission.[major_id]
    LEFT JOIN sys.schemas AS secured_schema
        ON
        (
            permission.[class] = 3
            AND secured_schema.[schema_id] = permission.[major_id]
        )
        OR
        (
            permission.[class] = 1
            AND secured_schema.[schema_id] = secured_object.[schema_id]
        )
    WHERE grantee.[name] IN
    (
        N'tb_role_user',
        N'tb_role_manager',
        N'tb_role_admin',
        N'tb_role_sync_operator'
    )
      AND secured_schema.[name] IN
      (
          N'tb_data',
          N'tb_private',
          N'tb_user',
          N'tb_ops',
          N'tb_security',
          N'tb_audit'
      )
      AND permission.[permission_name] IN
      (
          N'SELECT', N'INSERT', N'UPDATE', N'DELETE', N'CONTROL', N'ALTER'
      )
      AND permission.[state] IN (N'G', N'W')
)
BEGIN
    PRINT N'FAIL: An application role has direct table/schema data permission.';
    SET @FailureCount += 1;
END;

IF @FailureCount > 0
BEGIN
    RAISERROR(
        N'TechBench V0004 Admin-owned shared-configuration verification failed with %d issue(s).',
        16,
        1,
        @FailureCount);
    RETURN;
END;

PRINT N'TechBench V0004 Admin-owned shared-configuration verification passed.';

SELECT
    DB_NAME() AS [DatabaseName],
    MAX([SchemaVersion]) AS [SchemaVersion],
    MAX
    (
        CASE
            WHEN [MigrationId] = N'SqlServer2016.AdminOwnedSharedConfig.0004'
                THEN [AppliedAtUtc]
        END
    ) AS [AdminOwnedSharedConfigAppliedAtUtc]
FROM [tb_deploy].[SchemaMigrations];
GO
