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
    WHERE [MigrationId]=N'SqlServer2016.EquipmentAnyDesk.0015'
      AND [SchemaVersion]=15
)
BEGIN
    PRINT N'FAIL: V0015 equipment AnyDesk migration is not installed.';
    SET @FailureCount+=1;
END;

IF COL_LENGTH(N'tb_inventory.Equipment', N'AnyDeskNumber') IS NULL
   OR COL_LENGTH(N'tb_inventory.Equipment', N'AnyDeskPasswordEncrypted') IS NULL
BEGIN
    PRINT N'FAIL: one or more equipment AnyDesk columns are missing.';
    SET @FailureCount+=1;
END;

DECLARE @CapabilitiesDefinition nvarchar(max)=
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities', N'P')), N'');
IF CHARINDEX(N'CONVERT(int, 15) AS [SchemaVersion]', @CapabilitiesDefinition)=0
BEGIN
    PRINT N'FAIL: repository capabilities do not report schema version 15.';
    SET @FailureCount+=1;
END;

DECLARE @InventoryDefinition nvarchar(max)=
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetEquipmentInventory', N'P')), N'');
IF CHARINDEX(N'equipment.[AnyDeskNumber]', @InventoryDefinition)=0
   OR CHARINDEX(N'CAST(N'''' AS nvarchar(max)) AS [AnyDeskPassword]', @InventoryDefinition)=0
   OR CHARINDEX(N'DecryptByKey', @InventoryDefinition)>0
   OR CHARINDEX(N'[AnyDeskPasswordEncrypted]', @InventoryDefinition)>0
BEGIN
    PRINT N'FAIL: shared inventory reads do not expose the number while keeping the AnyDesk password private.';
    SET @FailureCount+=1;
END;

DECLARE @AdminReadDefinition nvarchar(max)=
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminGetEquipmentBoard', N'P')), N'');
DECLARE @SecureReadDefinition nvarchar(max)=
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminGetEquipmentBoardSecure', N'P')), N'');
DECLARE @AdminSaveDefinition nvarchar(max)=
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminSaveEquipment', N'P')), N'');
DECLARE @EncryptDefinition nvarchar(max)=
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_security.EncryptEquipmentAnyDeskPassword', N'P')), N'');
DECLARE @EnsureCurrentUserDefinition nvarchar(max)=
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_security.EnsureCurrentUser', N'P')), N'');

IF CHARINDEX(N'IS_ROLEMEMBER(N''tb_role_admin'')', @AdminReadDefinition)=0
   OR CHARINDEX(N'AdminGetEquipmentBoardSecure', @AdminReadDefinition)=0
   OR CHARINDEX(N'WITH EXECUTE AS OWNER', @AdminReadDefinition)>0
   OR CHARINDEX(N'equipment.[AnyDeskNumber]', @SecureReadDefinition)=0
   OR CHARINDEX(N'DecryptByKeyAutoCert', @SecureReadDefinition)=0
   OR CHARINDEX(N'WITH EXECUTE AS OWNER', @SecureReadDefinition)=0
   OR CHARINDEX(N'@AnyDeskNumber nvarchar(80)', @AdminSaveDefinition)=0
   OR CHARINDEX(N'@AnyDeskPassword nvarchar(max)', @AdminSaveDefinition)=0
   OR CHARINDEX(N'IS_ROLEMEMBER(N''tb_role_admin'')', @AdminSaveDefinition)=0
   OR CHARINDEX(N'EncryptEquipmentAnyDeskPassword', @AdminSaveDefinition)=0
   OR CHARINDEX(N'WITH EXECUTE AS OWNER', @AdminSaveDefinition)>0
   OR CHARINDEX(N'[AnyDeskPasswordEncrypted]', @AdminSaveDefinition)=0
   OR CHARINDEX(N'EncryptByKey', @EncryptDefinition)=0
   OR CHARINDEX(N'WITH EXECUTE AS OWNER', @EncryptDefinition)=0
BEGIN
    PRINT N'FAIL: caller-authorized Admin equipment procedures do not securely delegate AnyDesk cryptography to owner-executed helpers.';
    SET @FailureCount+=1;
END;

IF CHARINDEX(
    N'IS_ROLEMEMBER(N''tb_role_admin'')',
    @EnsureCurrentUserDefinition)=0
   OR CHARINDEX(
        N'IS_ROLEMEMBER(N''tb_role_admin'', ORIGINAL_LOGIN())',
        @EnsureCurrentUserDefinition)>0
BEGIN
    PRINT N'FAIL: current-user role detection is not running in the authenticated caller context required for Windows group membership.';
    SET @FailureCount+=1;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission
    WHERE permission.[class]=1
      AND permission.[major_id] IN
      (
          OBJECT_ID(N'tb_app.AdminGetEquipmentBoardSecure', N'P'),
          OBJECT_ID(N'tb_security.EncryptEquipmentAnyDeskPassword', N'P')
      )
      AND permission.[permission_name]=N'EXECUTE'
      AND permission.[state] IN (N'G', N'W')
      AND permission.[grantee_principal_id] IN
      (
          USER_ID(N'tb_role_user'),
          USER_ID(N'tb_role_admin'),
          USER_ID(N'tb_role_sync_service'),
          USER_ID(N'tb_preview_reader')
      )
)
BEGIN
    PRINT N'FAIL: an owner-executed equipment encryption helper has a direct application-role grant.';
    SET @FailureCount+=1;
END;

IF CHARINDEX(N'[AnyDeskPassword] nvarchar', @AdminSaveDefinition)>0
   OR COL_LENGTH(N'tb_inventory.Equipment', N'AnyDeskPassword') IS NOT NULL
BEGIN
    PRINT N'FAIL: an AnyDesk password can be persisted as plaintext.';
    SET @FailureCount+=1;
END;

IF @FailureCount>0
    THROW 52221, N'TechBench V0015 equipment AnyDesk verification failed.', 1;

PRINT N'TechBench V0015 equipment AnyDesk verification passed.';
GO
