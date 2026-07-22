:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @FailureCount int = 0;
DECLARE @InstalledSchemaVersion int =
(
    SELECT MAX([SchemaVersion]) FROM [tb_deploy].[SchemaMigrations]
);

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.WhdServerSync.0006'
      AND [SchemaVersion] = 6
      AND [ReleaseVersion] = N'2.0.0-alpha.6'
)
BEGIN
    PRINT N'FAIL: V0006 migration marker is missing.';
    SET @FailureCount += 1;
END;

IF @InstalledSchemaVersion NOT IN (6, 7)
BEGIN
    PRINT N'FAIL: V0006 verification supports installed schema version 6 or 7.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredObjects TABLE
(
    [ObjectName] nvarchar(300) NOT NULL PRIMARY KEY,
    [ObjectType] char(2) NOT NULL
);
INSERT INTO @RequiredObjects([ObjectName], [ObjectType]) VALUES
    (N'tb_whd.Technicians', N'U'),
    (N'tb_whd.TechnicianGroups', N'U'),
    (N'tb_whd.TechnicianGroupMemberships', N'U'),
    (N'tb_whd.UserTechnicianMappings', N'U'),
    (N'tb_sync.WhdSyncRequests', N'U'),
    (N'tb_sync.WhdSyncWork', N'U'),
    (N'tb_sync.WhdSyncLeases', N'U'),
    (N'tb_sync.WhdSyncCursors', N'U'),
    (N'tb_sync.WhdSyncHealth', N'U'),
    (N'tb_service.GetWhdSyncConfiguration', N'P'),
    (N'tb_service.ClaimWhdSyncWork', N'P'),
    (N'tb_service.RenewWhdSyncLease', N'P'),
    (N'tb_service.ApplyWhdClientSnapshot', N'P'),
    (N'tb_service.ApplyWhdTicketBatch', N'P'),
    (N'tb_service.ApplyWhdTicketStatusSnapshot', N'P'),
    (N'tb_service.ApplyWhdTechnicianSnapshot', N'P'),
    (N'tb_service.ApplyWhdTechGroupSnapshot', N'P'),
    (N'tb_service.CompleteWhdSyncWork', N'P'),
    (N'tb_security.FilterWhdTicketAccess', N'IF'),
    (N'tb_app.AdminRequestWhdSync', N'P'),
    (N'tb_app.GetWhdSyncStatus', N'P'),
    (N'tb_app.AdminGetWhdUserMappings', N'P'),
    (N'tb_app.AdminSaveWhdUserMapping', N'P'),
    (N'tb_app.AdminGetWhdTechnicians', N'P');

IF EXISTS
(
    SELECT 1
    FROM @RequiredObjects AS required
    WHERE OBJECT_ID(required.[ObjectName], required.[ObjectType]) IS NULL
)
BEGIN
    PRINT N'FAIL: one or more V0006 objects are missing.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredColumns TABLE
(
    [ObjectName] nvarchar(300) NOT NULL,
    [ColumnName] sysname NOT NULL,
    PRIMARY KEY ([ObjectName], [ColumnName])
);
INSERT INTO @RequiredColumns([ObjectName], [ColumnName]) VALUES
    (N'tb_data.Tickets', N'WhdLastUpdatedUtc'),
    (N'tb_data.Tickets', N'IsWhdDeleted'),
    (N'tb_data.Tickets', N'AssignedTechExternalId'),
    (N'tb_data.Tickets', N'AssignedTechName'),
    (N'tb_data.Tickets', N'AssignedGroupExternalId'),
    (N'tb_data.Tickets', N'AssignedGroupName'),
    (N'tb_whd.Technicians', N'Username');

IF EXISTS
(
    SELECT 1
    FROM @RequiredColumns AS required
    WHERE COL_LENGTH(required.[ObjectName], required.[ColumnName]) IS NULL
)
BEGIN
    PRINT N'FAIL: a required V0006 column is missing.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredIndexes TABLE
(
    [ObjectName] nvarchar(300) NOT NULL,
    [IndexName] sysname NOT NULL,
    PRIMARY KEY ([ObjectName], [IndexName])
);
INSERT INTO @RequiredIndexes([ObjectName], [IndexName]) VALUES
    (N'tb_data.Tickets', N'IX_Tickets_WhdAssignedTech'),
    (N'tb_whd.TechnicianGroupMemberships', N'IX_WhdMemberships_Group'),
    (N'tb_sync.WhdSyncRequests', N'IX_WhdSyncRequests_StatusRequested'),
    (N'tb_sync.WhdSyncRequests', N'IX_WhdSyncRequests_RequestedAt'),
    (N'tb_sync.WhdSyncWork', N'IX_WhdSyncWork_Claim'),
    (N'tb_sync.WhdSyncWork', N'IX_WhdSyncWork_RequestState'),
    (N'tb_sync.WhdSyncWork', N'IX_WhdSyncWork_ReferenceHistory');

IF EXISTS
(
    SELECT 1
    FROM @RequiredIndexes AS required
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes AS index_row
        WHERE index_row.[object_id] = OBJECT_ID(required.[ObjectName], N'U')
          AND index_row.[name] = required.[IndexName]
          AND index_row.[is_disabled] = 0
    )
)
BEGIN
    PRINT N'FAIL: a required V0006 index is missing or disabled.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredParameters TABLE
(
    [ProcedureName] nvarchar(300) NOT NULL,
    [ParameterName] sysname NOT NULL,
    PRIMARY KEY ([ProcedureName], [ParameterName])
);
INSERT INTO @RequiredParameters([ProcedureName], [ParameterName]) VALUES
    (N'tb_service.ClaimWhdSyncWork', N'@WorkerId'),
    (N'tb_service.ClaimWhdSyncWork', N'@LeaseSeconds'),
    (N'tb_service.RenewWhdSyncLease', N'@WorkId'),
    (N'tb_service.RenewWhdSyncLease', N'@LeaseId'),
    (N'tb_service.RenewWhdSyncLease', N'@WorkerId'),
    (N'tb_service.RenewWhdSyncLease', N'@LeaseSeconds'),
    (N'tb_service.ApplyWhdClientSnapshot', N'@WorkId'),
    (N'tb_service.ApplyWhdClientSnapshot', N'@LeaseId'),
    (N'tb_service.ApplyWhdClientSnapshot', N'@WorkerId'),
    (N'tb_service.ApplyWhdClientSnapshot', N'@Json'),
    (N'tb_service.ApplyWhdClientSnapshot', N'@SyncedAtUtc'),
    (N'tb_service.ApplyWhdTicketBatch', N'@WorkId'),
    (N'tb_service.ApplyWhdTicketBatch', N'@LeaseId'),
    (N'tb_service.ApplyWhdTicketBatch', N'@WorkerId'),
    (N'tb_service.ApplyWhdTicketBatch', N'@Json'),
    (N'tb_service.ApplyWhdTicketBatch', N'@SyncedAtUtc'),
    (N'tb_service.ApplyWhdTicketStatusSnapshot', N'@Json'),
    (N'tb_service.ApplyWhdTechnicianSnapshot', N'@Json'),
    (N'tb_service.ApplyWhdTechGroupSnapshot', N'@Json'),
    (N'tb_service.CompleteWhdSyncWork', N'@WorkId'),
    (N'tb_service.CompleteWhdSyncWork', N'@LeaseId'),
    (N'tb_service.CompleteWhdSyncWork', N'@WorkerId'),
    (N'tb_service.CompleteWhdSyncWork', N'@Succeeded'),
    (N'tb_app.AdminRequestWhdSync', N'@RequestType'),
    (N'tb_app.AdminRequestWhdSync', N'@RequestId'),
    (N'tb_app.AdminSaveWhdUserMapping', N'@WindowsLoginName'),
    (N'tb_app.AdminSaveWhdUserMapping', N'@DisplayName'),
    (N'tb_app.AdminSaveWhdUserMapping', N'@IsAdmin'),
    (N'tb_app.AdminSaveWhdUserMapping', N'@TechnicianExternalId');

IF EXISTS
(
    SELECT 1
    FROM @RequiredParameters AS required
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.parameters AS parameter_row
        WHERE parameter_row.[object_id] = OBJECT_ID(required.[ProcedureName], N'P')
          AND parameter_row.[name] = required.[ParameterName]
    )
)
BEGIN
    PRINT N'FAIL: V0006 procedure parameter contract is incomplete.';
    SET @FailureCount += 1;
END;

IF N'$(UserGroup)' = N'$(AdminGroup)'
   OR N'$(UserGroup)' = N'$(SyncServicePrincipal)'
   OR N'$(AdminGroup)' = N'$(SyncServicePrincipal)'
BEGIN
    PRINT N'FAIL: application and service principals are not pairwise distinct.';
    SET @FailureCount += 1;
END;

IF DATABASE_PRINCIPAL_ID(N'tb_role_sync_service') IS NULL
BEGIN
    PRINT N'FAIL: tb_role_sync_service is missing.';
    SET @FailureCount += 1;
END;

IF
(
    SELECT COUNT(*)
    FROM sys.database_role_members AS drm
    INNER JOIN sys.database_principals AS role_principal
        ON role_principal.[principal_id] = drm.[role_principal_id]
    WHERE role_principal.[name] = N'tb_role_sync_service'
) <> 1
OR NOT EXISTS
(
    SELECT 1
    FROM sys.database_role_members AS drm
    INNER JOIN sys.database_principals AS role_principal
        ON role_principal.[principal_id] = drm.[role_principal_id]
    INNER JOIN sys.database_principals AS member_principal
        ON member_principal.[principal_id] = drm.[member_principal_id]
    WHERE role_principal.[name] = N'tb_role_sync_service'
      AND member_principal.[name] = N'$(SyncServicePrincipal)'
)
BEGIN
    PRINT N'FAIL: tb_role_sync_service must contain only the configured service principal.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.database_role_members AS drm
    INNER JOIN sys.database_principals AS role_principal
        ON role_principal.[principal_id] = drm.[role_principal_id]
    INNER JOIN sys.database_principals AS member_principal
        ON member_principal.[principal_id] = drm.[member_principal_id]
    WHERE member_principal.[name] = N'$(SyncServicePrincipal)'
      AND role_principal.[name] <> N'tb_role_sync_service'
)
BEGIN
    PRINT N'FAIL: the service principal is a member of a database role other than tb_role_sync_service.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_security].[Users]
    WHERE [WindowsSid] = SUSER_SID(N'$(SyncServicePrincipal)')
      AND [LoginName] = N'$(SyncServicePrincipal)'
      AND [IsTechnician] = 0
      AND [IsManager] = 0
      AND [IsAdmin] = 0
      AND [IsSyncOperator] = 0
)
BEGIN
    PRINT N'FAIL: the service audit actor is missing or has application privileges.';
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
    (N'tb_service.CompleteWhdSyncWork');

IF @InstalledSchemaVersion >= 7
BEGIN
    INSERT INTO @ServiceProcedures([ObjectName]) VALUES
        (N'tb_service.GetSageSyncConfiguration'),
        (N'tb_service.ClaimSageSyncWork'),
        (N'tb_service.RenewSageSyncLease'),
        (N'tb_service.ApplySageCustomerSnapshot'),
        (N'tb_service.CompleteSageSyncWork'),
        (N'tb_service.GetAutomaticClientMatchCandidates'),
        (N'tb_service.ApplyAutomaticClientMatch'),
        (N'tb_service.ApplyAutomaticWhdFamilyMember');
END;

IF EXISTS
(
    SELECT 1
    FROM @ServiceProcedures AS required
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.database_permissions AS permission_row
        WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_role_sync_service')
          AND permission_row.[class] = 1
          AND permission_row.[major_id] = OBJECT_ID(required.[ObjectName], N'P')
          AND permission_row.[permission_name] = N'EXECUTE'
          AND permission_row.[state] IN (N'G', N'W')
    )
)
BEGIN
    PRINT N'FAIL: a required service EXECUTE grant is missing.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission_row
    WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_role_sync_service')
      AND permission_row.[state] IN (N'G', N'W')
      AND
      (
          permission_row.[permission_name] IN
              (N'SELECT', N'INSERT', N'UPDATE', N'DELETE', N'ALTER', N'CONTROL', N'TAKE OWNERSHIP')
          OR
          (
              permission_row.[permission_name] = N'EXECUTE'
              AND
              (
                  permission_row.[class] <> 1
                  OR NOT EXISTS
                  (
                      SELECT 1
                      FROM @ServiceProcedures AS allowed
                      WHERE OBJECT_ID(allowed.[ObjectName], N'P') = permission_row.[major_id]
                  )
              )
          )
      )
)
BEGIN
    PRINT N'FAIL: tb_role_sync_service has direct data/control or unexpected execution grants.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.database_role_members AS drm
    INNER JOIN sys.database_principals AS containing_role
        ON containing_role.[principal_id] = drm.[role_principal_id]
    INNER JOIN sys.database_principals AS member_role
        ON member_role.[principal_id] = drm.[member_principal_id]
    WHERE member_role.[name] = N'tb_role_sync_service'
)
OR EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission_row
    WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'$(SyncServicePrincipal)')
      AND permission_row.[state] IN (N'G', N'W')
      AND permission_row.[permission_name] IN
          (N'SELECT', N'INSERT', N'UPDATE', N'DELETE', N'ALTER', N'CONTROL', N'TAKE OWNERSHIP', N'EXECUTE')
)
BEGIN
    PRINT N'FAIL: the service role is nested or the service principal has direct grants.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission_row
    WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_role_admin')
      AND permission_row.[permission_name] = N'EXECUTE'
      AND permission_row.[state] IN (N'G', N'W')
      AND permission_row.[major_id] IN
      (
          OBJECT_ID(N'tb_app.SyncApplyClientSnapshot'),
          OBJECT_ID(N'tb_app.SyncApplyTicketSnapshot'),
          OBJECT_ID(N'tb_app.SyncApplyTicketStatusSnapshot'),
          OBJECT_ID(N'tb_app.SyncUpsertClient'),
          OBJECT_ID(N'tb_app.SyncUpsertTicket'),
          OBJECT_ID(N'tb_app.SyncUpsertTicketStatusOption')
      )
)
BEGIN
    PRINT N'FAIL: Admin retains old direct WHD snapshot mutation grants.';
    SET @FailureCount += 1;
END;

DECLARE @ClaimDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_service.ClaimWhdSyncWork'));
DECLARE @CompleteDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_service.CompleteWhdSyncWork'));
DECLARE @MappingDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminSaveWhdUserMapping'));
DECLARE @TechnicianListDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminGetWhdTechnicians'));
DECLARE @SearchDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SearchTickets'));
DECLARE @GetTicketDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetTicket'));

SELECT @ClaimDefinition = REPLACE(REPLACE(REPLACE(@ClaimDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @CompleteDefinition = REPLACE(REPLACE(REPLACE(@CompleteDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @MappingDefinition = REPLACE(REPLACE(REPLACE(@MappingDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @TechnicianListDefinition = REPLACE(REPLACE(REPLACE(@TechnicianListDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @SearchDefinition = REPLACE(REPLACE(REPLACE(@SearchDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @GetTicketDefinition = REPLACE(REPLACE(REPLACE(@GetTicketDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');

IF CHARINDEX(N'sp_getapplock', @ClaimDefinition) = 0
   OR CHARINDEX(N'READCOMMITTEDLOCK', @ClaimDefinition) = 0
   OR CHARINDEX(N'DATEADD(day,-1,@Now)', @ClaimDefinition) = 0
BEGIN
    PRINT N'FAIL: ClaimWhdSyncWork lacks queue serialization, RCSI-safe claiming, or daily reference cadence.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'@WorkType<>N''Tickets''', @CompleteDefinition) = 0
   OR CHARINDEX(N'TRY_CONVERT(datetimeoffset(3),@CursorValue)', @CompleteDefinition) = 0
   OR CHARINDEX(N'@HasPendingWork=0', @CompleteDefinition) = 0
BEGIN
    PRINT N'FAIL: CompleteWhdSyncWork lacks ticket-only valid cursor or request-level health protection.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'@TechnicianExternalIdnvarchar(120)=NULL', @MappingDefinition) = 0
   OR CHARINDEX(N'SUSER_SID(@WindowsLoginName,0)', @MappingDefinition) = 0
   OR CHARINDEX(N'INSERTINTO[tb_security].[Users]', @MappingDefinition) = 0
   OR CHARINDEX(N'WriteAuditEvent', @MappingDefinition) = 0
   OR CHARINDEX(N'DELETEFROM[tb_whd].[UserTechnicianMappings]', @MappingDefinition) = 0
BEGIN
    PRINT N'FAIL: WHD user mapping does not support audited removal.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'WHERE[IsActive]=1', @TechnicianListDefinition) = 0
BEGIN
    PRINT N'FAIL: WHD technician mapping choices include inactive technicians.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'UserTechnicianMappings', @SearchDefinition) = 0
   OR CHARINDEX(N'TechnicianGroupMemberships', @SearchDefinition) = 0
   OR CHARINDEX(N'UserTechnicianMappings', @GetTicketDefinition) = 0
   OR CHARINDEX(N'TechnicianGroupMemberships', @GetTicketDefinition) = 0
   OR CHARINDEX(N'OR@Admin=1', @SearchDefinition) > 0
   OR CHARINDEX(N'OR@Admin=1', @GetTicketDefinition) > 0
BEGIN
    PRINT N'FAIL: normal WHD ticket reads are not strictly scoped to the mapped technician or groups.';
    SET @FailureCount += 1;
END;

DECLARE @TicketAccessDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_security.FilterWhdTicketAccess', N'IF'));
SELECT @TicketAccessDefinition = REPLACE(REPLACE(REPLACE(
    @TicketAccessDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');

IF @TicketAccessDefinition IS NULL
   OR CHARINDEX(N'USER_NAME()=N''dbo''', @TicketAccessDefinition) = 0
   OR CHARINDEX(N'IS_ROLEMEMBER(N''tb_role_admin'')=1', @TicketAccessDefinition) = 0
   OR CHARINDEX(N'IS_ROLEMEMBER(N''tb_role_sync_service'')=1', @TicketAccessDefinition) = 0
   OR CHARINDEX(N'SUSER_SID(ORIGINAL_LOGIN())', @TicketAccessDefinition) = 0
   OR CHARINDEX(N'UserTechnicianMappings', @TicketAccessDefinition) = 0
   OR CHARINDEX(N'TechnicianGroupMemberships', @TicketAccessDefinition) = 0
BEGIN
    PRINT N'FAIL: the WHD ticket row-access predicate is incomplete.';
    SET @FailureCount += 1;
END;

DECLARE @TicketPolicyId int =
(
    SELECT policy.[object_id]
    FROM sys.security_policies AS policy
    INNER JOIN sys.schemas AS schema_row
        ON schema_row.[schema_id] = policy.[schema_id]
    WHERE schema_row.[name] = N'tb_security'
      AND policy.[name] = N'WhdTicketAccessPolicy'
      AND policy.[is_enabled] = 1
      AND policy.[is_schema_bound] = 1
);

IF @TicketPolicyId IS NULL
   OR
   (
       SELECT COUNT(*)
       FROM sys.security_predicates AS predicate_row
       WHERE predicate_row.[object_id] = @TicketPolicyId
         AND predicate_row.[target_object_id] = OBJECT_ID(N'tb_data.Tickets', N'U')
         AND predicate_row.[predicate_definition] LIKE N'%FilterWhdTicketAccess%'
         AND
         (
             (predicate_row.[predicate_type_desc] = N'FILTER' AND predicate_row.[operation_desc] IS NULL)
             OR (predicate_row.[predicate_type_desc] = N'BLOCK'
                 AND predicate_row.[operation_desc] IN (N'AFTER INSERT', N'AFTER UPDATE'))
         )
   ) <> 3
BEGIN
    PRINT N'FAIL: the enabled WHD ticket security policy does not contain the required filter and block predicates.';
    SET @FailureCount += 1;
END;

DECLARE @ArrayApplyProcedures TABLE ([ObjectName] nvarchar(300) NOT NULL PRIMARY KEY);
INSERT INTO @ArrayApplyProcedures([ObjectName]) VALUES
    (N'tb_service.ApplyWhdClientSnapshot'),
    (N'tb_service.ApplyWhdTicketBatch'),
    (N'tb_service.ApplyWhdTicketStatusSnapshot'),
    (N'tb_service.ApplyWhdTechnicianSnapshot'),
    (N'tb_service.ApplyWhdTechGroupSnapshot');

IF EXISTS
(
    SELECT 1
    FROM @ArrayApplyProcedures AS procedure_row
    CROSS APPLY
    (
        SELECT REPLACE(REPLACE(REPLACE(
            OBJECT_DEFINITION(OBJECT_ID(procedure_row.[ObjectName], N'P')),
            N' ', N''), CHAR(13), N''), CHAR(10), N'') AS [Definition]
    ) AS normalized
    WHERE CHARINDEX(N'COALESCE(ISJSON(@Json),0)<>1', normalized.[Definition]) = 0
       OR CHARINDEX(N'LEFT(LTRIM(@Json),1)<>N''[''', normalized.[Definition]) = 0
       OR CHARINDEX(N'BEGINTRANSACTION', normalized.[Definition]) = 0
       OR CHARINDEX(N'HOLDLOCK', normalized.[Definition]) = 0
)
BEGIN
    PRINT N'FAIL: an apply procedure lacks array validation or atomic lease-bound application.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS (SELECT 1 FROM [tb_sync].[WhdSyncHealth] WHERE [HealthId] = 1)
BEGIN
    PRINT N'FAIL: the WHD synchronization health singleton is missing.';
    SET @FailureCount += 1;
END;

IF @FailureCount > 0
BEGIN
    RAISERROR(
        N'TechBench V0006 WHD server-sync verification failed with %d issue(s).',
        16,
        1,
        @FailureCount);
    RETURN;
END;

PRINT N'TechBench V0006 WHD server-sync verification passed.';
SELECT
    DB_NAME() AS [DatabaseName],
    MAX([SchemaVersion]) AS [SchemaVersion],
    MAX(CASE
        WHEN [MigrationId] = N'SqlServer2016.WhdServerSync.0006'
            THEN [AppliedAtUtc]
        END) AS [WhdServerSyncAppliedAtUtc]
FROM [tb_deploy].[SchemaMigrations];
GO
