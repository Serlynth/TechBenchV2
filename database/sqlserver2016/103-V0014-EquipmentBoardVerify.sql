:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @FailureCount int=0;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId]=N'SqlServer2016.EquipmentBoard.0014'
      AND [SchemaVersion]=14
)
BEGIN
    PRINT N'FAIL: V0014 equipment-board migration is not installed.';
    SET @FailureCount+=1;
END;

IF OBJECT_ID(N'tb_inventory.Equipment', N'U') IS NULL
BEGIN
    PRINT N'FAIL: the equipment table is missing.';
    SET @FailureCount+=1;
END;

IF OBJECT_ID(N'tb_inventory.ClientUsers', N'U') IS NULL
BEGIN
    PRINT N'FAIL: the inventory client-user table is missing.';
    SET @FailureCount+=1;
END;

IF OBJECT_ID(N'tb_inventory.ClientUserAccounts', N'U') IS NULL
BEGIN
    PRINT N'FAIL: the inventory client-user account table is missing.';
    SET @FailureCount+=1;
END;

IF OBJECT_ID(N'tb_inventory.ClientUserAccountFields', N'U') IS NULL
BEGIN
    PRINT N'FAIL: the encrypted client-user account-field table is missing.';
    SET @FailureCount+=1;
END;

IF OBJECT_ID(N'tb_inventory.EquipmentAssignmentHistory', N'U') IS NULL
BEGIN
    PRINT N'FAIL: the equipment assignment-history table is missing.';
    SET @FailureCount+=1;
END;

IF COL_LENGTH(N'tb_inventory.ClientUsers', N'SourceRowHash') IS NULL
BEGIN
    PRINT N'FAIL: client users cannot track workbook row changes.';
    SET @FailureCount+=1;
END;

IF COL_LENGTH(N'tb_inventory.Equipment', N'WorkflowStage') IS NULL
   OR COL_LENGTH(N'tb_inventory.Equipment', N'AssetTag') IS NULL
   OR COL_LENGTH(N'tb_inventory.Equipment', N'ClientId') IS NULL
   OR COL_LENGTH(N'tb_inventory.Equipment', N'ClientUserId') IS NULL
   OR COL_LENGTH(N'tb_inventory.Equipment', N'LocationName') IS NULL
BEGIN
    PRINT N'FAIL: one or more client/user-aware equipment columns are missing.';
    SET @FailureCount+=1;
END;

DECLARE @RequiredProcedures TABLE ([ObjectName] nvarchar(256) NOT NULL PRIMARY KEY);
INSERT INTO @RequiredProcedures([ObjectName]) VALUES
    (N'tb_app.AdminGetEquipmentBoard'),
    (N'tb_app.AdminGetInventoryClients'),
    (N'tb_app.AdminGetEquipmentAssignmentHistory'),
    (N'tb_app.AdminSaveEquipment'),
    (N'tb_app.AdminMoveEquipment'),
    (N'tb_app.AdminArchiveEquipment');

IF EXISTS
(
    SELECT 1
    FROM @RequiredProcedures
    WHERE OBJECT_ID([ObjectName], N'P') IS NULL
)
BEGIN
    PRINT N'FAIL: one or more equipment-board procedures are missing.';
    SET @FailureCount+=1;
END;

IF OBJECT_ID(N'tb_service.ApplyCredentialsClientUserSnapshot', N'P') IS NULL
BEGIN
    PRINT N'FAIL: the Credentials client-user import procedure is missing.';
    SET @FailureCount+=1;
END
ELSE IF CHARINDEX(
    N'OPENJSON(person_row.[AccountsJson])',
    COALESCE(
        OBJECT_DEFINITION(
            OBJECT_ID(N'tb_service.ApplyCredentialsClientUserSnapshot', N'P')),
        N''))=0
    OR CHARINDEX(
        N'[tb_inventory].[ClientUserAccounts]',
        COALESCE(
            OBJECT_DEFINITION(
                OBJECT_ID(N'tb_service.ApplyCredentialsClientUserSnapshot', N'P')),
            N''))=0
    OR CHARINDEX(
        N'[tb_inventory].[ClientUserAccountFields]',
        COALESCE(
            OBJECT_DEFINITION(
                OBJECT_ID(N'tb_service.ApplyCredentialsClientUserSnapshot', N'P')),
            N''))=0
    OR CHARINDEX(
        N'EncryptByKey',
        COALESCE(
            OBJECT_DEFINITION(
                OBJECT_ID(N'tb_service.ApplyCredentialsClientUserSnapshot', N'P')),
            N''))=0
BEGIN
    PRINT N'FAIL: the Credentials client-user import does not parse and encrypt account fields.';
    SET @FailureCount+=1;
END;

IF OBJECT_ID(N'tb_service.ApplyCredentialsClientUserSnapshot', N'P') IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM sys.database_permissions
       WHERE [grantee_principal_id]=DATABASE_PRINCIPAL_ID(N'tb_role_sync_service')
         AND [major_id]=OBJECT_ID(N'tb_service.ApplyCredentialsClientUserSnapshot')
         AND [permission_name]=N'EXECUTE'
         AND [state] IN (N'G',N'W')
   )
BEGIN
    PRINT N'FAIL: the Sync Service cannot import Credentials client users.';
    SET @FailureCount+=1;
END;

DECLARE @ClientUserReadProcedures TABLE ([ObjectName] nvarchar(256) NOT NULL PRIMARY KEY);
INSERT INTO @ClientUserReadProcedures([ObjectName]) VALUES
    (N'tb_app.SearchClientUsers'),
    (N'tb_app.RevealClientUser'),
    (N'tb_app.GetEquipmentInventory');

IF EXISTS
(
    SELECT 1 FROM @ClientUserReadProcedures
    WHERE OBJECT_ID([ObjectName], N'P') IS NULL
)
BEGIN
    PRINT N'FAIL: one or more client-user read procedures are missing.';
    SET @FailureCount+=1;
END;

IF EXISTS
(
    SELECT role_name.[RoleName], required.[ObjectName]
    FROM (VALUES (N'tb_role_user'), (N'tb_role_admin')) role_name([RoleName])
    CROSS JOIN @ClientUserReadProcedures required
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.database_permissions
        WHERE [grantee_principal_id]=DATABASE_PRINCIPAL_ID(role_name.[RoleName])
          AND [major_id]=OBJECT_ID(required.[ObjectName])
          AND [permission_name]=N'EXECUTE'
          AND [state] IN (N'G',N'W')
    )
)
BEGIN
    PRINT N'FAIL: one or more client-user read grants are missing.';
    SET @FailureCount+=1;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.database_permissions permission_row
    INNER JOIN @ClientUserReadProcedures required
        ON OBJECT_ID(required.[ObjectName])=permission_row.[major_id]
    WHERE permission_row.[grantee_principal_id] IN
    (
        DATABASE_PRINCIPAL_ID(N'tb_preview_reader'),
        DATABASE_PRINCIPAL_ID(N'tb_role_sync_service')
    )
      AND permission_row.[permission_name]=N'EXECUTE'
      AND permission_row.[state] IN (N'G',N'W')
)
BEGIN
    PRINT N'FAIL: preview or Sync Service can execute a client-user read procedure.';
    SET @FailureCount+=1;
END;

IF CHARINDEX(
    N'DecryptByKeyAutoCert',
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.RevealClientUser', N'P')), N''))=0
   OR CHARINDEX(
    N'ClientUserAccountId',
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.RevealClientUser', N'P')), N''))=0
BEGIN
    PRINT N'FAIL: client-user reveal does not decrypt account fields with their account authenticator.';
    SET @FailureCount+=1;
END;

DECLARE @EquipmentInventoryDefinition nvarchar(max)=
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetEquipmentInventory', N'P')), N'');

IF CHARINDEX(N'@ClientId', @EquipmentInventoryDefinition)=0
   OR CHARINDEX(N'@ClientUserId', @EquipmentInventoryDefinition)=0
   OR CHARINDEX(N'@ClientName', @EquipmentInventoryDefinition)=0
   OR CHARINDEX(N'equipment.[IsArchived] = 0', @EquipmentInventoryDefinition)=0
BEGIN
    PRINT N'FAIL: the shared equipment inventory read is not client-scoped and archive-safe.';
    SET @FailureCount+=1;
END;

DECLARE @RepositoryCapabilitiesDefinition nvarchar(max)=
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities', N'P')), N'');

IF CHARINDEX(N'CONVERT(int, 14) AS [SchemaVersion]', @RepositoryCapabilitiesDefinition)=0
BEGIN
    PRINT N'FAIL: repository capabilities do not report schema version 14.';
    SET @FailureCount+=1;
END;

DECLARE @RequiredCapabilityTokens TABLE ([Token] nvarchar(128) NOT NULL PRIMARY KEY);
INSERT INTO @RequiredCapabilityTokens([Token]) VALUES
    (N'[FullTextSearchAvailable]'),
    (N'[SupportsTickets]'),
    (N'[SupportsWorkEntries]'),
    (N'[SupportsPrivateNotes]'),
    (N'[SupportsPostingLeases]'),
    (N'[SupportsSyncLeases]'),
    (N'[SupportsImports]'),
    (N'[SupportsTechBenchV1Import]'),
    (N'[SupportsServerSageSync]'),
    (N'[SupportsAdminUserPreview]'),
    (N'[SupportsFireDrillCredentials]'),
    (N'[EquipmentBoardAvailable]');

IF EXISTS
(
    SELECT 1
    FROM @RequiredCapabilityTokens
    WHERE CHARINDEX([Token], @RepositoryCapabilitiesDefinition)=0
)
BEGIN
    PRINT N'FAIL: repository capabilities dropped one or more capabilities introduced by an earlier schema version.';
    SET @FailureCount+=1;
END;

IF CHARINDEX(
    N'@TargetWorkflowStage',
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminMoveEquipment', N'P')), N''))=0
BEGIN
    PRINT N'FAIL: equipment assignment does not persist Stock, Assigned, and Deployment stages.';
    SET @FailureCount+=1;
END;

IF CHARINDEX(
    N'@SourceStage NOT IN (N''Assigned'', N''Deployment'')',
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminMoveEquipment', N'P')), N''))=0
BEGIN
    PRINT N'FAIL: equipment priority maintenance does not isolate each technician deployment lane.';
    SET @FailureCount+=1;
END;

IF CHARINDEX(
    N'@ClientUserId',
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminSaveEquipment', N'P')), N''))=0
   OR CHARINDEX(
    N'[tb_inventory].[EquipmentAssignmentHistory]',
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminSaveEquipment', N'P')), N''))=0
BEGIN
    PRINT N'FAIL: equipment saves do not validate client users or record assignment history.';
    SET @FailureCount+=1;
END;

IF EXISTS
(
    SELECT required.[ObjectName]
    FROM @RequiredProcedures AS required
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.database_permissions
        WHERE [grantee_principal_id]=DATABASE_PRINCIPAL_ID(N'tb_role_admin')
          AND [major_id]=OBJECT_ID(required.[ObjectName])
          AND [permission_name]=N'EXECUTE'
          AND [state] IN (N'G',N'W')
    )
)
BEGIN
    PRINT N'FAIL: one or more Admin equipment-board grants are missing.';
    SET @FailureCount+=1;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission_row
    INNER JOIN @RequiredProcedures AS required
        ON OBJECT_ID(required.[ObjectName])=permission_row.[major_id]
    WHERE permission_row.[grantee_principal_id] IN
    (
        DATABASE_PRINCIPAL_ID(N'tb_role_user'),
        DATABASE_PRINCIPAL_ID(N'tb_preview_reader')
    )
      AND permission_row.[permission_name]=N'EXECUTE'
      AND permission_row.[state] IN (N'G',N'W')
)
BEGIN
    PRINT N'FAIL: a non-Admin principal can execute an equipment-board procedure.';
    SET @FailureCount+=1;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.database_permissions
    WHERE [grantee_principal_id] IN
    (
        DATABASE_PRINCIPAL_ID(N'tb_role_user'),
        DATABASE_PRINCIPAL_ID(N'tb_role_admin'),
        DATABASE_PRINCIPAL_ID(N'tb_role_sync_service'),
        DATABASE_PRINCIPAL_ID(N'tb_preview_reader')
    )
      AND [major_id] IN
      (
          OBJECT_ID(N'tb_inventory.Equipment'),
          OBJECT_ID(N'tb_inventory.ClientUsers'),
          OBJECT_ID(N'tb_inventory.ClientUserAccounts'),
          OBJECT_ID(N'tb_inventory.ClientUserAccountFields'),
          OBJECT_ID(N'tb_inventory.EquipmentAssignmentHistory')
      )
      AND [permission_name] IN (N'SELECT',N'INSERT',N'UPDATE',N'DELETE',N'CONTROL',N'ALTER')
      AND [state] IN (N'G',N'W')
)
BEGIN
    PRINT N'FAIL: a TechBench principal has a direct inventory-table grant.';
    SET @FailureCount+=1;
END;

IF CHARINDEX(
    N'IS_ROLEMEMBER(N''tb_role_admin'')',
    REPLACE(
        COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminMoveEquipment', N'P')), N''),
        N' ',
        N''))=0
BEGIN
    PRINT N'FAIL: equipment assignment does not enforce Admin access.';
    SET @FailureCount+=1;
END;

IF @FailureCount>0
    THROW 52220, N'TechBench V0014 equipment-board verification failed.', 1;

PRINT N'TechBench V0014 equipment-board verification passed.';
GO
