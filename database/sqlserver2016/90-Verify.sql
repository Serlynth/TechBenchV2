:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @FailureCount int = 0;

IF
(
    SELECT [compatibility_level]
    FROM sys.databases
    WHERE [name] = DB_NAME()
) <> 130
BEGIN
    PRINT N'FAIL: Database compatibility level is not 130.';
    SET @FailureCount += 1;
END;

IF
(
    SELECT [owner_sid]
    FROM sys.databases
    WHERE [name] = DB_NAME()
) <> SUSER_SID(N'$(DatabaseOwnerLogin)')
BEGIN
    PRINT N'FAIL: Database owner SID does not match DatabaseOwnerLogin.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.Baseline.0001'
      AND [SchemaVersion] = 1
)
BEGIN
    PRINT N'FAIL: Baseline migration marker or schema version 1 is missing.';
    SET @FailureCount += 1;
END;

IF TRY_CONVERT(
       uniqueidentifier,
       (
           SELECT [Value]
           FROM [tb_data].[ServerMetadata]
           WHERE [Key] = N'Server.InstanceId'
       )) IS NULL
BEGIN
    PRINT N'FAIL: Server.InstanceId metadata is missing or invalid.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredObjects TABLE
(
    [ObjectName] nvarchar(300) NOT NULL,
    [ObjectType] char(2) NOT NULL
);

INSERT INTO @RequiredObjects([ObjectName], [ObjectType])
VALUES
    (N'tb_security.Users', N'U'),
    (N'tb_data.Clients', N'U'),
    (N'tb_data.ServerMetadata', N'U'),
    (N'tb_audit.AuditEvents', N'U'),
    (N'tb_security.EnsureCurrentUser', N'P'),
    (N'tb_app.GetCurrentUserContext', N'P'),
    (N'tb_app.SearchClients', N'P'),
    (N'tb_app.GetClient', N'P'),
    (N'tb_app.AdminSaveClient', N'P'),
    (N'tb_app.ReadAuditEvents', N'P');

DECLARE @ObjectName nvarchar(300);
DECLARE @ObjectType char(2);

DECLARE ObjectCursor CURSOR LOCAL FAST_FORWARD FOR
SELECT [ObjectName], [ObjectType]
FROM @RequiredObjects;

OPEN ObjectCursor;
FETCH NEXT FROM ObjectCursor INTO @ObjectName, @ObjectType;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(@ObjectName, @ObjectType) IS NULL
    BEGIN
        PRINT N'FAIL: Required object missing: ' + @ObjectName;
        SET @FailureCount += 1;
    END;

    FETCH NEXT FROM ObjectCursor INTO @ObjectName, @ObjectType;
END;

CLOSE ObjectCursor;
DEALLOCATE ObjectCursor;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'tb_data.Clients')
      AND [name] = N'RowVersion'
      AND [system_type_id] = 189
)
BEGIN
    PRINT N'FAIL: Clients.RowVersion is not a SQL Server rowversion column.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission
    INNER JOIN sys.database_principals AS grantee
        ON grantee.principal_id = permission.grantee_principal_id
    WHERE grantee.name = N'tb_role_sync_operator'
      AND permission.class = 1
      AND permission.major_id = OBJECT_ID(N'tb_app.SearchClients')
      AND permission.permission_name = N'EXECUTE'
      AND permission.state IN (N'G', N'W')
)
BEGIN
    PRINT N'FAIL: tb_role_sync_operator cannot execute tb_app.SearchClients.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'tb_security.Users')
      AND [name] = N'WindowsSid'
      AND [system_type_id] = 165
)
BEGIN
    PRINT N'FAIL: Users.WindowsSid is not varbinary.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredMembership TABLE
(
    [RoleName] sysname NOT NULL,
    [MemberName] sysname NOT NULL
);

INSERT INTO @RequiredMembership([RoleName], [MemberName])
VALUES
    (N'tb_role_deployer', N'$(DeploymentGroup)'),
    (N'tb_role_user', N'$(TechnicianGroup)'),
    (N'tb_role_user', N'$(ManagerGroup)'),
    (N'tb_role_manager', N'$(ManagerGroup)'),
    (N'tb_role_user', N'$(AdminGroup)'),
    (N'tb_role_manager', N'$(AdminGroup)'),
    (N'tb_role_admin', N'$(AdminGroup)'),
    (N'tb_role_sync_operator', N'$(SyncOperatorGroup)'),
    (N'tb_role_auditor', N'$(AuditReaderGroup)');

SELECT @FailureCount = @FailureCount + COUNT(*)
FROM @RequiredMembership AS required_membership
WHERE NOT EXISTS
(
    SELECT 1
    FROM sys.database_role_members AS drm
    INNER JOIN sys.database_principals AS role_principal
        ON role_principal.principal_id = drm.role_principal_id
    INNER JOIN sys.database_principals AS member_principal
        ON member_principal.principal_id = drm.member_principal_id
    WHERE role_principal.name = required_membership.[RoleName]
      AND member_principal.name = required_membership.[MemberName]
);

IF EXISTS
(
    SELECT 1
    FROM sys.database_role_members AS drm
    INNER JOIN sys.database_principals AS role_principal
        ON role_principal.principal_id = drm.role_principal_id
    INNER JOIN sys.database_principals AS member_principal
        ON member_principal.principal_id = drm.member_principal_id
    WHERE role_principal.name IN
        (N'db_datareader', N'db_datawriter', N'db_ddladmin', N'db_owner')
      AND member_principal.name IN
        (
            N'$(TechnicianGroup)',
            N'$(ManagerGroup)',
            N'$(AdminGroup)',
            N'$(SyncOperatorGroup)',
            N'$(AuditReaderGroup)'
        )
)
BEGIN
    PRINT N'FAIL: An application AD group belongs to a direct-access fixed database role.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission
    INNER JOIN sys.database_principals AS grantee
        ON grantee.principal_id = permission.grantee_principal_id
    WHERE grantee.name = N'tb_role_user'
      AND permission.class = 1
      AND permission.major_id = OBJECT_ID(N'tb_app.SearchClients')
      AND permission.permission_name = N'EXECUTE'
      AND permission.state IN (N'G', N'W')
)
BEGIN
    PRINT N'FAIL: tb_role_user cannot execute tb_app.SearchClients.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission
    INNER JOIN sys.database_principals AS grantee
        ON grantee.principal_id = permission.grantee_principal_id
    WHERE grantee.name = N'tb_role_admin'
      AND permission.class = 1
      AND permission.major_id = OBJECT_ID(N'tb_app.AdminSaveClient')
      AND permission.permission_name = N'EXECUTE'
      AND permission.state IN (N'G', N'W')
)
BEGIN
    PRINT N'FAIL: tb_role_admin cannot execute tb_app.AdminSaveClient.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission
    INNER JOIN sys.database_principals AS grantee
        ON grantee.principal_id = permission.grantee_principal_id
    LEFT JOIN sys.objects AS secured_object
        ON permission.class = 1
       AND secured_object.object_id = permission.major_id
    LEFT JOIN sys.schemas AS secured_schema
        ON
        (
            permission.class = 3
            AND secured_schema.schema_id = permission.major_id
        )
        OR
        (
            permission.class = 1
            AND secured_schema.schema_id = secured_object.schema_id
        )
    WHERE grantee.name IN
        (
            N'tb_role_user',
            N'tb_role_manager',
            N'tb_role_admin',
            N'tb_role_sync_operator',
            N'tb_role_auditor'
        )
      AND secured_schema.name IN (N'tb_data', N'tb_security', N'tb_audit')
      AND permission.permission_name IN
        (N'SELECT', N'INSERT', N'UPDATE', N'DELETE', N'CONTROL', N'ALTER')
      AND permission.state IN (N'G', N'W')
)
BEGIN
    PRINT N'FAIL: An application role has direct table/schema data permission.';
    SET @FailureCount += 1;
END;

IF @FailureCount > 0
BEGIN
    RAISERROR(
        N'TechBench SQL Server verification failed with %d issue(s).',
        16,
        1,
        @FailureCount);
    RETURN;
END;

PRINT N'TechBench SQL Server verification passed.';

SELECT
    DB_NAME() AS [DatabaseName],
    database_info.[compatibility_level] AS [CompatibilityLevel],
    SUSER_SNAME(database_info.[owner_sid]) AS [DatabaseOwner],
    metadata.[Value] AS [ServerInstanceId],
    migration.[MigrationId],
    migration.[SchemaVersion],
    migration.[ReleaseVersion],
    migration.[AppliedAtUtc],
    migration.[AppliedByLogin]
FROM sys.databases AS database_info
CROSS JOIN
(
    SELECT [Value]
    FROM [tb_data].[ServerMetadata]
    WHERE [Key] = N'Server.InstanceId'
) AS metadata
CROSS JOIN
(
    SELECT
        [MigrationId],
        [SchemaVersion],
        [ReleaseVersion],
        [AppliedAtUtc],
        [AppliedByLogin]
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.Baseline.0001'
) AS migration
WHERE database_info.[name] = DB_NAME();
GO
