:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @FailureCount int=0;

IF NOT EXISTS
(
    SELECT 1 FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId]=N'SqlServer2016.FireDrillCredentials.0008'
      AND [SchemaVersion]=8 AND [ReleaseVersion]=N'0.5.6'
)
BEGIN PRINT N'FAIL: V0008 migration marker is missing or invalid.'; SET @FailureCount+=1; END;

IF (SELECT MAX([SchemaVersion]) FROM [tb_deploy].[SchemaMigrations]) NOT IN (8, 9, 10, 11)
BEGIN PRINT N'FAIL: installed schema version is not 8 or 9.'; SET @FailureCount+=1; END;

DECLARE @Objects TABLE([Name] nvarchar(300) PRIMARY KEY,[Type] char(2));
INSERT INTO @Objects VALUES
 (N'tb_data.FireDrillCredentials',N'U'),(N'tb_sync.FireDrillSyncRequests',N'U'),
 (N'tb_sync.FireDrillSyncLeases',N'U'),(N'tb_sync.FireDrillSyncHealth',N'U'),
 (N'tb_app.SearchFireDrillCredentials',N'P'),(N'tb_app.RevealFireDrillCredential',N'P'),
 (N'tb_app.AdminRequestFireDrillSync',N'P'),
 (N'tb_app.GetFireDrillSyncStatus',N'P'),(N'tb_service.GetFireDrillSyncConfiguration',N'P'),
 (N'tb_service.ClaimFireDrillSyncWork',N'P'),(N'tb_service.RenewFireDrillSyncLease',N'P'),
 (N'tb_service.ApplyFireDrillCredentialSnapshot',N'P'),(N'tb_service.CompleteFireDrillSyncWork',N'P');
IF EXISTS(SELECT 1 FROM @Objects WHERE OBJECT_ID([Name],[Type]) IS NULL)
BEGIN PRINT N'FAIL: one or more V0008 objects are missing.'; SET @FailureCount+=1; END;

IF NOT EXISTS(SELECT 1 FROM sys.certificates WHERE [name]=N'tb_FireDrillCredentialCertificate')
   OR NOT EXISTS(SELECT 1 FROM sys.symmetric_keys WHERE [name]=N'tb_FireDrillCredentialKey')
BEGIN PRINT N'FAIL: Credentials encryption objects are missing.'; SET @FailureCount+=1; END;

IF OBJECT_ID(N'tb_app.AuditFireDrillCredentialCopy',N'P') IS NOT NULL
BEGIN PRINT N'FAIL: the obsolete Credentials copy-audit procedure still exists.'; SET @FailureCount+=1; END;

DECLARE @GetSettingsDefinition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetSettings'));
DECLARE @ServiceConfigurationDefinition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'tb_service.GetFireDrillSyncConfiguration'));
DECLARE @ClaimDefinition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'tb_service.ClaimFireDrillSyncWork'));
IF CHARINDEX(N'[SettingKey] <> N''FireDrill.SourcePath'' OR @CanReadServerPaths = 1',@GetSettingsDefinition)=0
BEGIN PRINT N'FAIL: ordinary clients are not blocked from receiving the Credentials source path.'; SET @FailureCount+=1; END;
IF CHARINDEX(N'WHERE [SettingKey]=N''FireDrill.SourcePath''), N'''') AS [SourcePath]',@ServiceConfigurationDefinition)=0
BEGIN PRINT N'FAIL: the Credentials service configuration does not require an explicitly configured path.'; SET @FailureCount+=1; END;
IF CHARINDEX(N'@SourcePath IS NOT NULL',@ClaimDefinition)=0
BEGIN PRINT N'FAIL: automatic Credentials synchronization is not gated on a configured path.'; SET @FailureCount+=1; END;
IF CHARINDEX(N'IS_ROLEMEMBER(N''tb_role_sync_service'')',@ClaimDefinition)=0
   OR CHARINDEX(N'SUSER_SID(ORIGINAL_LOGIN())',@ClaimDefinition)=0
   OR CHARINDEX(N'[tb_security].[EnsureCurrentUser]',@ClaimDefinition)>0
BEGIN PRINT N'FAIL: Credentials work is not claimed through the dedicated service identity.'; SET @FailureCount+=1; END;

DECLARE @UserProcedures TABLE([Name] nvarchar(300) PRIMARY KEY);
INSERT INTO @UserProcedures VALUES
 (N'tb_app.SearchFireDrillCredentials'),(N'tb_app.RevealFireDrillCredential');
IF EXISTS
(
 SELECT 1 FROM @UserProcedures required
 WHERE NOT EXISTS
 (
  SELECT 1 FROM sys.database_permissions permission_row
  WHERE permission_row.[grantee_principal_id]=DATABASE_PRINCIPAL_ID(N'tb_role_user')
    AND permission_row.[class]=1 AND permission_row.[major_id]=OBJECT_ID(required.[Name],N'P')
    AND permission_row.[permission_name]=N'EXECUTE' AND permission_row.[state] IN (N'G',N'W')
 )
)
BEGIN PRINT N'FAIL: a Credentials user procedure grant is missing.'; SET @FailureCount+=1; END;

IF EXISTS
(
 SELECT 1 FROM sys.database_permissions permission_row
 WHERE permission_row.[grantee_principal_id] IN
       (DATABASE_PRINCIPAL_ID(N'tb_role_user'),DATABASE_PRINCIPAL_ID(N'tb_role_admin'),DATABASE_PRINCIPAL_ID(N'tb_role_sync_service'))
   AND permission_row.[class] IN (0,1,3)
   AND permission_row.[permission_name] IN (N'SELECT',N'INSERT',N'UPDATE',N'DELETE',N'CONTROL',N'ALTER',N'VIEW DEFINITION')
   AND
   (
     permission_row.[class]=0
     OR permission_row.[major_id] IN
        (OBJECT_ID(N'tb_data.FireDrillCredentials'),OBJECT_ID(N'tb_sync.FireDrillSyncRequests'),OBJECT_ID(N'tb_sync.FireDrillSyncLeases'))
     OR (permission_row.[class]=3 AND permission_row.[major_id] IN (SCHEMA_ID(N'tb_data'),SCHEMA_ID(N'tb_sync')))
   )
)
BEGIN PRINT N'FAIL: a Credentials role has direct data/control permission.'; SET @FailureCount+=1; END;

IF @FailureCount>0
BEGIN
    DECLARE @Message nvarchar(2048)=N'TechBench V0008 Credentials verification failed with '+CONVERT(nvarchar(20),@FailureCount)+N' issue(s).';
    THROW 50000,@Message,1;
END;

PRINT N'TechBench V0008 Credentials verification passed.';
GO
