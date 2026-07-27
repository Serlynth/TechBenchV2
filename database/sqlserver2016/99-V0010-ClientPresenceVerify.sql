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
    WHERE [MigrationId] = N'SqlServer2016.ClientPresence.0010'
      AND [SchemaVersion] = 10
      AND [ReleaseVersion] = N'0.5.23'
)
BEGIN
    PRINT N'FAIL: V0010 client presence migration marker is missing or invalid.';
    SET @FailureCount += 1;
END;

IF (SELECT MAX([SchemaVersion]) FROM [tb_deploy].[SchemaMigrations]) NOT IN (10, 11, 12, 13, 14)
BEGIN
    PRINT N'FAIL: installed schema version is not 10 or 11.';
    SET @FailureCount += 1;
END;

IF OBJECT_ID(N'tb_security.ClientSessions', N'U') IS NULL
   OR OBJECT_ID(N'tb_security.ClientSessionCommands', N'U') IS NULL
BEGIN
    PRINT N'FAIL: client presence tables are missing.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredProcedures TABLE ([ProcedureName] sysname NOT NULL);
INSERT INTO @RequiredProcedures([ProcedureName])
VALUES
    (N'tb_app.HeartbeatClientSession'),
    (N'tb_app.AdminGetActiveClientSessions'),
    (N'tb_app.AdminQueueClientSessionCommand'),
    (N'tb_app.AcknowledgeClientSessionCommand'),
    (N'tb_app.CloseClientSession');

IF EXISTS
(
    SELECT 1
    FROM @RequiredProcedures
    WHERE OBJECT_ID([ProcedureName], N'P') IS NULL
)
BEGIN
    PRINT N'FAIL: one or more V0010 client presence procedures are missing.';
    SET @FailureCount += 1;
END;

DECLARE @ExpectedGrants TABLE
(
    [RoleName] sysname NOT NULL,
    [ProcedureName] sysname NOT NULL
);
INSERT INTO @ExpectedGrants([RoleName], [ProcedureName])
VALUES
    (N'tb_role_user', N'tb_app.HeartbeatClientSession'),
    (N'tb_role_user', N'tb_app.AcknowledgeClientSessionCommand'),
    (N'tb_role_user', N'tb_app.CloseClientSession'),
    (N'tb_role_admin', N'tb_app.AdminGetActiveClientSessions'),
    (N'tb_role_admin', N'tb_app.AdminQueueClientSessionCommand');

IF EXISTS
(
    SELECT 1
    FROM @ExpectedGrants AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.database_permissions AS permission
        INNER JOIN sys.database_principals AS principal
            ON principal.[principal_id] = permission.[grantee_principal_id]
        WHERE principal.[name] = expected.[RoleName]
          AND permission.[class] = 1
          AND permission.[major_id] = OBJECT_ID(expected.[ProcedureName], N'P')
          AND permission.[permission_name] = N'EXECUTE'
          AND permission.[state] IN (N'G', N'W')
    )
)
BEGIN
    PRINT N'FAIL: one or more V0010 client presence execution grants are missing.';
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
      AND
      (
          permission.[class] = 0
          OR permission.[major_id] IN
              (
                  OBJECT_ID(N'tb_security.ClientSessions', N'U'),
                  OBJECT_ID(N'tb_security.ClientSessionCommands', N'U')
              )
      )
      AND permission.[permission_name] IN
          (N'SELECT', N'INSERT', N'UPDATE', N'DELETE', N'CONTROL', N'ALTER')
)
BEGIN
    PRINT N'FAIL: a human role has direct client presence table/control permission.';
    SET @FailureCount += 1;
END;

DECLARE @HeartbeatDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.HeartbeatClientSession', N'P'));
DECLARE @AdminCommandDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminQueueClientSessionCommand', N'P'));
IF CHARINDEX(N'[WindowsSid] = @UserSid', @HeartbeatDefinition) = 0
   OR CHARINDEX(N'[AcknowledgedAtUtc] IS NULL', @HeartbeatDefinition) = 0
   OR CHARINDEX(N'@IsAdmin <> 1', @AdminCommandDefinition) = 0
   OR CHARINDEX(N'@RequesterSessionId = @TargetSessionId', @AdminCommandDefinition) = 0
BEGIN
    PRINT N'FAIL: client presence ownership/Admin/self-command boundaries are missing.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'CONVERT(int, 11) AS [SchemaVersion]',
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities', N'P'))) = 0
   AND CHARINDEX(N'CONVERT(int, 12) AS [SchemaVersion]',
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities', N'P'))) = 0
   AND CHARINDEX(N'CONVERT(int, 13) AS [SchemaVersion]',
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities', N'P'))) = 0
   AND CHARINDEX(N'CONVERT(int, 14) AS [SchemaVersion]',
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities', N'P'))) = 0
BEGIN
    PRINT N'FAIL: GetRepositoryCapabilities does not report a supported final schema version.';
    SET @FailureCount += 1;
END;

IF @FailureCount > 0
BEGIN
    RAISERROR(N'TechBench V0010 client presence verification failed with %d issue(s).', 16, 1, @FailureCount);
    RETURN;
END;

PRINT N'TechBench V0010 client presence verification passed.';
GO
