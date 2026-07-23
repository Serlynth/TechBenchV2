:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @FailureCount int = 0;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.ClientResponses.0011'
      AND [SchemaVersion] = 11
      AND [ReleaseVersion] = N'0.5.24'
)
BEGIN
    PRINT N'FAIL: V0011 client response migration marker is missing or invalid.';
    SET @FailureCount += 1;
END;

IF (SELECT MAX([SchemaVersion]) FROM [tb_deploy].[SchemaMigrations]) <> 11
BEGIN
    PRINT N'FAIL: installed schema version is not 11.';
    SET @FailureCount += 1;
END;

IF COL_LENGTH(N'tb_security.ClientSessionCommands', N'ResponseMessage') IS NULL
BEGIN
    PRINT N'FAIL: ClientSessionCommands.ResponseMessage is missing.';
    SET @FailureCount += 1;
END;

IF OBJECT_ID(N'tb_app.AdminGetRecentClientSessionResponses', N'P') IS NULL
BEGIN
    PRINT N'FAIL: the Admin client-response procedure is missing.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission
    INNER JOIN sys.database_principals AS principal
        ON principal.[principal_id] = permission.[grantee_principal_id]
    WHERE principal.[name] = N'tb_role_admin'
      AND permission.[class] = 1
      AND permission.[major_id] =
          OBJECT_ID(N'tb_app.AdminGetRecentClientSessionResponses', N'P')
      AND permission.[permission_name] = N'EXECUTE'
      AND permission.[state] IN (N'G', N'W')
)
BEGIN
    PRINT N'FAIL: the Admin role cannot read recent client responses.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission
    INNER JOIN sys.database_principals AS principal
        ON principal.[principal_id] = permission.[grantee_principal_id]
    WHERE principal.[name] IN (N'tb_role_user', N'tb_role_admin')
      AND permission.[state] IN (N'G', N'W')
      AND permission.[major_id] =
          OBJECT_ID(N'tb_security.ClientSessionCommands', N'U')
      AND permission.[permission_name] IN
          (N'SELECT', N'INSERT', N'UPDATE', N'DELETE', N'CONTROL', N'ALTER')
)
BEGIN
    PRINT N'FAIL: a human role has direct client-command table permission.';
    SET @FailureCount += 1;
END;

DECLARE @AcknowledgeDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AcknowledgeClientSessionCommand', N'P'));
DECLARE @AdminResponseDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminGetRecentClientSessionResponses', N'P'));
IF CHARINDEX(N'@ResponseMessage nvarchar(500)', @AcknowledgeDefinition) = 0
   OR CHARINDEX(N'[ResponseMessage] = @ResponseMessage', @AcknowledgeDefinition) = 0
   OR CHARINDEX(N'@IsAdmin <> 1', @AdminResponseDefinition) = 0
   OR CHARINDEX(N'DATEADD(DAY, -7', @AdminResponseDefinition) = 0
BEGIN
    PRINT N'FAIL: client response validation, ownership, or retention boundaries are missing.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'CONVERT(int, 11) AS [SchemaVersion]',
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities', N'P'))) = 0
BEGIN
    PRINT N'FAIL: GetRepositoryCapabilities does not report schema version 11.';
    SET @FailureCount += 1;
END;

IF @FailureCount > 0
BEGIN
    RAISERROR(N'TechBench V0011 client response verification failed with %d issue(s).',
        16, 1, @FailureCount);
    RETURN;
END;

PRINT N'TechBench V0011 client response verification passed.';
GO
