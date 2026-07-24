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
    WHERE [MigrationId]=N'SqlServer2016.FlexibleCredentialFields.0012'
      AND [SchemaVersion]=12
)
BEGIN
    PRINT N'FAIL: V0012 flexible Credentials migration is not installed.';
    SET @FailureCount+=1;
END;

IF OBJECT_ID(N'tb_data.FireDrillCredentialFields', N'U') IS NULL
BEGIN
    PRINT N'FAIL: the flexible Credentials field table is missing.';
    SET @FailureCount+=1;
END;

DECLARE @CapabilitiesDefinition nvarchar(max)=
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities'));
DECLARE @ApplyDefinition nvarchar(max)=
    OBJECT_DEFINITION(OBJECT_ID(N'tb_service.ApplyFireDrillCredentialSnapshot'));
DECLARE @RevealDefinition nvarchar(max)=
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.RevealFireDrillCredential'));

IF CHARINDEX(N'CONVERT(int, 12) AS [SchemaVersion]', COALESCE(@CapabilitiesDefinition,N''))=0
   AND CHARINDEX(N'CONVERT(int, 13) AS [SchemaVersion]', COALESCE(@CapabilitiesDefinition,N''))=0
BEGIN
    PRINT N'FAIL: repository capabilities do not report a supported final schema version.';
    SET @FailureCount+=1;
END;

IF CHARINDEX(N'OPENJSON(row_data.[FieldsJson])', COALESCE(@ApplyDefinition,N''))=0
   OR CHARINDEX(N'[tb_data].[FireDrillCredentialFields]', COALESCE(@ApplyDefinition,N''))=0
BEGIN
    PRINT N'FAIL: the Credentials snapshot procedure does not persist flexible fields.';
    SET @FailureCount+=1;
END;

IF CHARINDEX(N'FOR JSON PATH', COALESCE(@RevealDefinition,N''))=0
   OR CHARINDEX(N'[ValueEncrypted]', COALESCE(@RevealDefinition,N''))=0
BEGIN
    PRINT N'FAIL: the Credentials reveal procedure does not return flexible fields.';
    SET @FailureCount+=1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.database_permissions
    WHERE [grantee_principal_id]=DATABASE_PRINCIPAL_ID(N'tb_role_user')
      AND [major_id]=OBJECT_ID(N'tb_app.SearchFireDrillCredentials')
      AND [permission_name]=N'EXECUTE' AND [state] IN (N'G',N'W')
)
   OR NOT EXISTS
(
    SELECT 1
    FROM sys.database_permissions
    WHERE [grantee_principal_id]=DATABASE_PRINCIPAL_ID(N'tb_role_user')
      AND [major_id]=OBJECT_ID(N'tb_app.RevealFireDrillCredential')
      AND [permission_name]=N'EXECUTE' AND [state] IN (N'G',N'W')
)
BEGIN
    PRINT N'FAIL: ordinary TechBench users cannot read flexible Credentials through approved procedures.';
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
      AND [major_id]=OBJECT_ID(N'tb_data.FireDrillCredentialFields')
      AND [permission_name] IN (N'SELECT',N'INSERT',N'UPDATE',N'DELETE',N'CONTROL',N'ALTER')
      AND [state] IN (N'G',N'W')
)
BEGIN
    PRINT N'FAIL: a TechBench principal has a direct flexible Credentials table grant.';
    SET @FailureCount+=1;
END;

IF @FailureCount>0
    THROW 52090, N'TechBench V0012 flexible Credentials verification failed.', 1;

PRINT N'TechBench V0012 flexible Credentials verification passed.';
GO
