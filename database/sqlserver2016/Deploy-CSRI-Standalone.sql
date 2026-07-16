/*
    Self-contained TechBench V2 database deployment for CSRI-SQL.

    Requirements:
      - Run in SQL Server Management Studio connected to CSRI-SQL.
      - Use an existing SQL Server sysadmin login.
      - Enable Query > SQLCMD Mode before execution.

    This file has no external file references and contains no password.
*/

:ON ERROR EXIT

:setvar DatabaseName "TechBench"
:setvar UserGroup "CSRI\TechBench_Users"
:setvar AdminGroup "CSRI\TechBench_Admins"

USE [master];
GO

IF UPPER(CONVERT(nvarchar(128), SERVERPROPERTY(N'MachineName'))) <> N'CSRI-SQL'
BEGIN
    ;THROW 51000, N'This deployment is restricted to CSRI-SQL.', 1;
END;
GO

-- ============================================================================
-- BEGIN 00-Preflight.sql
-- ============================================================================

:ON ERROR EXIT

USE [master];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DatabaseName sysname = N'$(DatabaseName)';
DECLARE @UserGroup sysname = N'$(UserGroup)';
DECLARE @AdminGroup sysname = N'$(AdminGroup)';
DECLARE @ProductMajorVersion int =
    TRY_CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion'));
DECLARE @ProductVersion nvarchar(128) =
    CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductVersion'));
DECLARE @ProductBuild int =
    TRY_CONVERT(int, PARSENAME(@ProductVersion, 2));

IF @ProductMajorVersion IS NULL OR @ProductMajorVersion < 13
BEGIN
    RAISERROR(
        N'TechBench V2 requires SQL Server 2016 (13.x) or newer. Detected version: %s.',
        16,
        1,
        @ProductVersion);
    RETURN;
END;

IF IS_SRVROLEMEMBER(N'sysadmin') <> 1
BEGIN
    RAISERROR(
        N'The initial TechBench deployment must run under a SQL Server sysadmin login.',
        16,
        1);
    RETURN;
END;

IF NULLIF(LTRIM(RTRIM(@DatabaseName)), N'') IS NULL
   OR LEN(@DatabaseName) > 128
   OR CHARINDEX(N']', @DatabaseName) > 0
BEGIN
    RAISERROR(
        N'DatabaseName must be a nonempty SQL identifier of at most 128 characters and cannot contain ].',
        16,
        1);
    RETURN;
END;

IF NULLIF(LTRIM(RTRIM(@UserGroup)), N'') IS NULL
   OR NULLIF(LTRIM(RTRIM(@AdminGroup)), N'') IS NULL
BEGIN
    RAISERROR(N'UserGroup and AdminGroup must both be supplied.', 16, 1);
    RETURN;
END;

IF @UserGroup NOT LIKE N'%\%'
   OR @AdminGroup NOT LIKE N'%\%'
BEGIN
    RAISERROR(
        N'Application groups must use DOMAIN\name format.',
        16,
        1);
    RETURN;
END;

IF @UserGroup = @AdminGroup
BEGIN
    RAISERROR(
        N'UserGroup and AdminGroup must be distinct so ordinary users do not receive administration rights.',
        16,
        1);
    RETURN;
END;

BEGIN TRY
    EXEC master.dbo.xp_logininfo
        @acctname = @UserGroup,
        @option = N'members';

    EXEC master.dbo.xp_logininfo
        @acctname = @AdminGroup,
        @option = N'members';
END TRY
BEGIN CATCH
    DECLARE @GroupError nvarchar(2048) = ERROR_MESSAGE();
    RAISERROR(
        N'SQL Server could not resolve one of the TechBench AD groups: %s',
        16,
        1,
        @GroupError);
    RETURN;
END CATCH;

IF SUSER_SNAME(0x01) IS NULL
BEGIN
    RAISERROR(
        N'The built-in SQL Server owner principal (SID 0x01) could not be resolved.',
        16,
        1);
    RETURN;
END;

PRINT N'TechBench SQL Server preflight passed.';
PRINT N'  Server version: ' + CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductVersion'));
IF @ProductMajorVersion = 13 AND ISNULL(@ProductBuild, 0) < 6300
BEGIN
    PRINT N'WARNING: SQL Server 2016 SP3 starts at build 13.0.6300.2. Patch and vendor-test this instance before production deployment.';
END;
IF @ProductMajorVersion = 13
BEGIN
    PRINT N'WARNING: SQL Server 2016 normal extended support ended July 14, 2026. Confirm ESU coverage or an upgrade plan.';
END;
PRINT N'  Database: ' + @DatabaseName;
PRINT N'  Database owner: ' + SUSER_SNAME(0x01) + N' (built-in SID 0x01)';
PRINT N'  User group: ' + @UserGroup;
PRINT N'  Admin group: ' + @AdminGroup;
PRINT N'AD group resolution passed.';
GO

-- ============================================================================
-- END 00-Preflight.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 10-CreateDatabase.sql
-- ============================================================================

:ON ERROR EXIT

USE [master];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DatabaseName sysname = N'$(DatabaseName)';
DECLARE @DatabaseOwnerLogin sysname = SUSER_SNAME(0x01);
DECLARE @Sql nvarchar(max);

IF @DatabaseOwnerLogin IS NULL
BEGIN
    RAISERROR(
        N'The built-in SQL Server owner principal (SID 0x01) could not be resolved.',
        16,
        1);
    RETURN;
END;

IF DB_ID(@DatabaseName) IS NULL
BEGIN
    SET @Sql = N'CREATE DATABASE ' + QUOTENAME(@DatabaseName) + N';';
    EXEC sys.sp_executesql @Sql;
END;

SET @Sql =
    N'ALTER AUTHORIZATION ON DATABASE::' + QUOTENAME(@DatabaseName)
    + N' TO ' + QUOTENAME(@DatabaseOwnerLogin) + N';';
EXEC sys.sp_executesql @Sql;
GO

ALTER DATABASE [$(DatabaseName)] SET COMPATIBILITY_LEVEL = 130;
ALTER DATABASE [$(DatabaseName)] SET RECOVERY SIMPLE;
ALTER DATABASE [$(DatabaseName)] SET AUTO_CLOSE OFF;
ALTER DATABASE [$(DatabaseName)] SET AUTO_SHRINK OFF;
ALTER DATABASE [$(DatabaseName)] SET PAGE_VERIFY CHECKSUM;
ALTER DATABASE [$(DatabaseName)] SET TRUSTWORTHY OFF;
ALTER DATABASE [$(DatabaseName)] SET DB_CHAINING OFF;
ALTER DATABASE [$(DatabaseName)] SET ALLOW_SNAPSHOT_ISOLATION ON;
GO

USE [master];
GO

DECLARE @DatabaseName sysname = N'$(DatabaseName)';
DECLARE @DataLogicalName sysname;
DECLARE @LogLogicalName sysname;
DECLARE @DataSizePages int;
DECLARE @LogSizePages int;
DECLARE @Sql nvarchar(max);

SELECT TOP (1)
    @DataLogicalName = [name],
    @DataSizePages = [size]
FROM sys.master_files
WHERE [database_id] = DB_ID(@DatabaseName)
  AND [type] = 0
ORDER BY [file_id];

SELECT TOP (1)
    @LogLogicalName = [name],
    @LogSizePages = [size]
FROM sys.master_files
WHERE [database_id] = DB_ID(@DatabaseName)
  AND [type] = 1
ORDER BY [file_id];

IF @DataLogicalName IS NULL OR @LogLogicalName IS NULL
BEGIN
    RAISERROR(
        N'TechBench database data or log file metadata could not be resolved.',
        16,
        1);
    RETURN;
END;

SET @Sql =
    N'ALTER DATABASE ' + QUOTENAME(@DatabaseName)
    + N' MODIFY FILE (NAME = N' + QUOTENAME(@DataLogicalName, N'''')
    + CASE
        WHEN @DataSizePages < 32768 THEN N', SIZE = 256MB'
        ELSE N''
      END
    + N', FILEGROWTH = 64MB);';
EXEC sys.sp_executesql @Sql;

SET @Sql =
    N'ALTER DATABASE ' + QUOTENAME(@DatabaseName)
    + N' MODIFY FILE (NAME = N' + QUOTENAME(@LogLogicalName, N'''')
    + CASE
        WHEN @LogSizePages < 16384 THEN N', SIZE = 128MB'
        ELSE N''
      END
    + N', FILEGROWTH = 64MB);';
EXEC sys.sp_executesql @Sql;
GO

IF EXISTS
(
    SELECT 1
    FROM sys.databases
    WHERE name = N'$(DatabaseName)'
      AND is_read_committed_snapshot_on = 0
)
BEGIN
    ALTER DATABASE [$(DatabaseName)]
        SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
END;
GO

PRINT N'TechBench database exists with compatibility level 130, SIMPLE recovery, and fixed-MB file growth.';
GO

-- ============================================================================
-- END 10-CreateDatabase.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 20-BaselineSchema.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF SCHEMA_ID(N'tb_deploy') IS NULL
    EXEC(N'CREATE SCHEMA [tb_deploy] AUTHORIZATION [dbo];');
IF SCHEMA_ID(N'tb_security') IS NULL
    EXEC(N'CREATE SCHEMA [tb_security] AUTHORIZATION [dbo];');
IF SCHEMA_ID(N'tb_data') IS NULL
    EXEC(N'CREATE SCHEMA [tb_data] AUTHORIZATION [dbo];');
IF SCHEMA_ID(N'tb_audit') IS NULL
    EXEC(N'CREATE SCHEMA [tb_audit] AUTHORIZATION [dbo];');
IF SCHEMA_ID(N'tb_app') IS NULL
    EXEC(N'CREATE SCHEMA [tb_app] AUTHORIZATION [dbo];');

IF OBJECT_ID(N'tb_deploy.SchemaMigrations', N'U') IS NULL
BEGIN
    CREATE TABLE [tb_deploy].[SchemaMigrations]
    (
        [MigrationId] nvarchar(150) NOT NULL,
        [SchemaVersion] int NOT NULL,
        [ReleaseVersion] nvarchar(40) NOT NULL,
        [ScriptChecksum] varchar(64) NULL,
        [AppliedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_SchemaMigrations_AppliedAtUtc]
            DEFAULT (SYSUTCDATETIME()),
        [AppliedByLogin] nvarchar(256) NOT NULL
            CONSTRAINT [DF_SchemaMigrations_AppliedByLogin]
            DEFAULT (ORIGINAL_LOGIN()),
        CONSTRAINT [PK_SchemaMigrations]
            PRIMARY KEY CLUSTERED ([MigrationId]),
        CONSTRAINT [CK_SchemaMigrations_SchemaVersion]
            CHECK ([SchemaVersion] > 0)
    );
END;

IF EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.Baseline.0001'
)
BEGIN
    PRINT N'SqlServer2016.Baseline.0001 is already installed.';
    RETURN;
END;

IF OBJECT_ID(N'tb_security.Users', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_data.Clients', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_data.ServerMetadata', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_audit.AuditEvents', N'U') IS NOT NULL
BEGIN
    RAISERROR(
        N'Baseline objects already exist without the baseline migration marker. Stop and investigate the partial deployment.',
        16,
        1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    CREATE TABLE [tb_security].[Users]
    (
        [WindowsSid] varbinary(85) NOT NULL,
        [LoginName] nvarchar(256) NOT NULL,
        [DisplayName] nvarchar(160) NOT NULL,
        [IsTechnician] bit NOT NULL
            CONSTRAINT [DF_Users_IsTechnician] DEFAULT (0),
        [IsManager] bit NOT NULL
            CONSTRAINT [DF_Users_IsManager] DEFAULT (0),
        [IsAdmin] bit NOT NULL
            CONSTRAINT [DF_Users_IsAdmin] DEFAULT (0),
        [IsSyncOperator] bit NOT NULL
            CONSTRAINT [DF_Users_IsSyncOperator] DEFAULT (0),
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_Users_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [LastSeenAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_Users_LastSeenAtUtc] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED ([WindowsSid]),
        CONSTRAINT [CK_Users_WindowsSidLength]
            CHECK (DATALENGTH([WindowsSid]) BETWEEN 8 AND 85),
        CONSTRAINT [CK_Users_RoleHierarchy]
            CHECK
            (
                ([IsAdmin] = 0 OR ([IsManager] = 1 AND [IsTechnician] = 1))
                AND ([IsManager] = 0 OR [IsTechnician] = 1)
            )
    );

    CREATE UNIQUE INDEX [UX_Users_LoginName]
        ON [tb_security].[Users]([LoginName]);

    CREATE TABLE [tb_data].[Clients]
    (
        [Id] int IDENTITY(1,1) NOT NULL,
        [Name] nvarchar(240) NOT NULL,
        [Source] nvarchar(80) NOT NULL
            CONSTRAINT [DF_Clients_Source] DEFAULT (N'Manual'),
        [ExternalId] nvarchar(500) NULL,
        [IsActive] bit NOT NULL
            CONSTRAINT [DF_Clients_IsActive] DEFAULT (1),
        [LastSyncedAtUtc] datetime2(3) NULL,
        [WhdLocationName] nvarchar(240) NULL,
        [WhdContactName] nvarchar(240) NULL,
        [SageCustomerId] nvarchar(120) NULL,
        [SageCustomerName] nvarchar(240) NULL,
        [SageContactName] nvarchar(240) NULL,
        [SageTelephone] nvarchar(80) NULL,
        [MatchStatus] nvarchar(80) NOT NULL
            CONSTRAINT [DF_Clients_MatchStatus] DEFAULT (N'Unmatched'),
        [CreatedByWindowsSid] varbinary(85) NOT NULL,
        [UpdatedByWindowsSid] varbinary(85) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_Clients_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_Clients_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_Clients] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_Clients_CreatedBy]
            FOREIGN KEY ([CreatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [FK_Clients_UpdatedBy]
            FOREIGN KEY ([UpdatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_Clients_Name]
            CHECK (LEN(LTRIM(RTRIM([Name]))) > 0),
        CONSTRAINT [CK_Clients_Source]
            CHECK ([Source] IN (N'Manual', N'WHD', N'Sage', N'Both')),
        CONSTRAINT [CK_Clients_MatchStatus]
            CHECK (LEN(LTRIM(RTRIM([MatchStatus]))) > 0)
    );

    CREATE INDEX [IX_Clients_ActiveName]
        ON [tb_data].[Clients]([IsActive], [Name])
        INCLUDE
        (
            [Source],
            [ExternalId],
            [LastSyncedAtUtc],
            [WhdLocationName],
            [SageCustomerId],
            [MatchStatus]
        );

    CREATE INDEX [IX_Clients_ExternalId]
        ON [tb_data].[Clients]([ExternalId])
        WHERE [ExternalId] IS NOT NULL;

    CREATE INDEX [IX_Clients_SageCustomerId]
        ON [tb_data].[Clients]([SageCustomerId])
        WHERE [SageCustomerId] IS NOT NULL;

    CREATE TABLE [tb_audit].[AuditEvents]
    (
        [Id] bigint IDENTITY(1,1) NOT NULL,
        [ActorWindowsSid] varbinary(85) NOT NULL,
        [ActorLoginName] nvarchar(256) NOT NULL,
        [Action] nvarchar(120) NOT NULL,
        [EntityType] nvarchar(120) NOT NULL,
        [EntityId] nvarchar(120) NOT NULL,
        [RequestId] uniqueidentifier NOT NULL,
        [DataJson] nvarchar(max) NULL,
        [OccurredAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_AuditEvents_OccurredAtUtc]
            DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_AuditEvents] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_AuditEvents_Actor]
            FOREIGN KEY ([ActorWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_AuditEvents_DataJson]
            CHECK ([DataJson] IS NULL OR ISJSON([DataJson]) = 1)
    );

    CREATE INDEX [IX_AuditEvents_OccurredAtUtc]
        ON [tb_audit].[AuditEvents]([OccurredAtUtc] DESC, [Id] DESC);

    CREATE INDEX [IX_AuditEvents_Entity]
        ON [tb_audit].[AuditEvents]([EntityType], [EntityId], [Id] DESC);

    CREATE TABLE [tb_data].[ServerMetadata]
    (
        [Key] nvarchar(120) NOT NULL,
        [Value] nvarchar(1000) NOT NULL,
        [UpdatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_ServerMetadata_UpdatedAtUtc]
            DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_ServerMetadata] PRIMARY KEY CLUSTERED ([Key])
    );

    INSERT INTO [tb_data].[ServerMetadata]([Key], [Value])
    VALUES
    (
        N'Server.InstanceId',
        CONVERT(nvarchar(36), NEWID())
    );

    INSERT INTO [tb_deploy].[SchemaMigrations]
    (
        [MigrationId],
        [SchemaVersion],
        [ReleaseVersion],
        [ScriptChecksum]
    )
    VALUES
    (
        N'SqlServer2016.Baseline.0001',
        1,
        N'2.0.0-alpha.1',
        NULL
    );

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'SqlServer2016.Baseline.0001 installed.';
GO

-- ============================================================================
-- END 20-BaselineSchema.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 30-Security.sql
-- ============================================================================

:ON ERROR EXIT

USE [master];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Principal sysname;
DECLARE @Sql nvarchar(max);

DECLARE PrincipalCursor CURSOR LOCAL FAST_FORWARD FOR
SELECT [PrincipalName]
FROM
(
    VALUES
        (N'$(UserGroup)'),
        (N'$(AdminGroup)')
) AS Principals([PrincipalName]);

OPEN PrincipalCursor;
FETCH NEXT FROM PrincipalCursor INTO @Principal;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF SUSER_ID(@Principal) IS NULL
    BEGIN
        SET @Sql =
            N'CREATE LOGIN ' + QUOTENAME(@Principal)
            + N' FROM WINDOWS WITH DEFAULT_DATABASE = '
            + QUOTENAME(N'$(DatabaseName)') + N';';
        EXEC sys.sp_executesql @Sql;
    END;

    FETCH NEXT FROM PrincipalCursor INTO @Principal;
END;

CLOSE PrincipalCursor;
DEALLOCATE PrincipalCursor;
GO

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @LegacyRoleName sysname;
DECLARE @LegacyMemberName sysname;
DECLARE @LegacySql nvarchar(max);

DECLARE LegacyMembershipCursor CURSOR LOCAL FAST_FORWARD FOR
SELECT
    role_principal.[name],
    member_principal.[name]
FROM sys.database_role_members AS drm
INNER JOIN sys.database_principals AS role_principal
    ON role_principal.[principal_id] = drm.[role_principal_id]
INNER JOIN sys.database_principals AS member_principal
    ON member_principal.[principal_id] = drm.[member_principal_id]
WHERE role_principal.[name] IN
    (N'tb_role_auditor', N'tb_role_deployer');

OPEN LegacyMembershipCursor;
FETCH NEXT FROM LegacyMembershipCursor
INTO @LegacyRoleName, @LegacyMemberName;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @LegacySql =
        N'ALTER ROLE ' + QUOTENAME(@LegacyRoleName)
        + N' DROP MEMBER ' + QUOTENAME(@LegacyMemberName) + N';';
    EXEC sys.sp_executesql @LegacySql;

    FETCH NEXT FROM LegacyMembershipCursor
    INTO @LegacyRoleName, @LegacyMemberName;
END;

CLOSE LegacyMembershipCursor;
DEALLOCATE LegacyMembershipCursor;

IF DATABASE_PRINCIPAL_ID(N'tb_role_auditor') IS NOT NULL
    DROP ROLE [tb_role_auditor];
IF DATABASE_PRINCIPAL_ID(N'tb_role_deployer') IS NOT NULL
    DROP ROLE [tb_role_deployer];

IF DATABASE_PRINCIPAL_ID(N'tb_role_user') IS NULL
    CREATE ROLE [tb_role_user] AUTHORIZATION [dbo];
IF DATABASE_PRINCIPAL_ID(N'tb_role_manager') IS NULL
    CREATE ROLE [tb_role_manager] AUTHORIZATION [dbo];
IF DATABASE_PRINCIPAL_ID(N'tb_role_admin') IS NULL
    CREATE ROLE [tb_role_admin] AUTHORIZATION [dbo];
IF DATABASE_PRINCIPAL_ID(N'tb_role_sync_operator') IS NULL
    CREATE ROLE [tb_role_sync_operator] AUTHORIZATION [dbo];

DECLARE @Principal sysname;
DECLARE @DefaultSchema sysname;
DECLARE @Sql nvarchar(max);

DECLARE UserCursor CURSOR LOCAL FAST_FORWARD FOR
SELECT [PrincipalName], [DefaultSchema]
FROM
(
    VALUES
        (N'$(UserGroup)', N'tb_app'),
        (N'$(AdminGroup)', N'tb_app')
) AS Principals([PrincipalName], [DefaultSchema]);

OPEN UserCursor;
FETCH NEXT FROM UserCursor INTO @Principal, @DefaultSchema;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF DATABASE_PRINCIPAL_ID(@Principal) IS NULL
    BEGIN
        SET @Sql =
            N'CREATE USER ' + QUOTENAME(@Principal)
            + N' FOR LOGIN ' + QUOTENAME(@Principal)
            + N' WITH DEFAULT_SCHEMA = ' + QUOTENAME(@DefaultSchema) + N';';
        EXEC sys.sp_executesql @Sql;
    END;

    FETCH NEXT FROM UserCursor INTO @Principal, @DefaultSchema;
END;

CLOSE UserCursor;
DEALLOCATE UserCursor;

DECLARE @Membership TABLE
(
    [RoleName] sysname NOT NULL,
    [MemberName] sysname NOT NULL
);

INSERT INTO @Membership([RoleName], [MemberName])
VALUES
    (N'tb_role_user', N'$(UserGroup)'),
    (N'tb_role_user', N'$(AdminGroup)'),
    (N'tb_role_manager', N'$(AdminGroup)'),
    (N'tb_role_admin', N'$(AdminGroup)'),
    (N'tb_role_sync_operator', N'$(AdminGroup)');

DECLARE @RoleName sysname;
DECLARE @MemberName sysname;

DECLARE MembershipCursor CURSOR LOCAL FAST_FORWARD FOR
SELECT [RoleName], [MemberName]
FROM @Membership;

OPEN MembershipCursor;
FETCH NEXT FROM MembershipCursor INTO @RoleName, @MemberName;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.database_role_members AS drm
        INNER JOIN sys.database_principals AS role_principal
            ON role_principal.principal_id = drm.role_principal_id
        INNER JOIN sys.database_principals AS member_principal
            ON member_principal.principal_id = drm.member_principal_id
        WHERE role_principal.name = @RoleName
          AND member_principal.name = @MemberName
    )
    BEGIN
        SET @Sql =
            N'ALTER ROLE ' + QUOTENAME(@RoleName)
            + N' ADD MEMBER ' + QUOTENAME(@MemberName) + N';';
        EXEC sys.sp_executesql @Sql;
    END;

    FETCH NEXT FROM MembershipCursor INTO @RoleName, @MemberName;
END;

CLOSE MembershipCursor;
DEALLOCATE MembershipCursor;

PRINT N'TechBench AD logins, database users, and role memberships are configured.';
GO

-- ============================================================================
-- END 30-Security.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 40-StoredProcedures.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'tb_security.EnsureCurrentUser', N'P') IS NOT NULL
    DROP PROCEDURE [tb_security].[EnsureCurrentUser];
GO

CREATE PROCEDURE [tb_security].[EnsureCurrentUser]
    @UserSid varbinary(85) OUTPUT,
    @LoginName nvarchar(256) OUTPUT,
    @DisplayName nvarchar(160) OUTPUT,
    @IsTechnician bit OUTPUT,
    @IsManager bit OUTPUT,
    @IsAdmin bit OUTPUT,
    @IsSyncOperator bit OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @UserSid = SUSER_SID(ORIGINAL_LOGIN());
    SET @LoginName = CONVERT(nvarchar(256), ORIGINAL_LOGIN());
    SET @IsTechnician =
        CONVERT(bit, CASE WHEN IS_ROLEMEMBER(N'tb_role_user') = 1 THEN 1 ELSE 0 END);
    SET @IsManager =
        CONVERT(bit, CASE WHEN IS_ROLEMEMBER(N'tb_role_manager') = 1 THEN 1 ELSE 0 END);
    SET @IsAdmin =
        CONVERT(bit, CASE WHEN IS_ROLEMEMBER(N'tb_role_admin') = 1 THEN 1 ELSE 0 END);
    SET @IsSyncOperator =
        CONVERT(
            bit,
            CASE WHEN IS_ROLEMEMBER(N'tb_role_sync_operator') = 1 THEN 1 ELSE 0 END);

    IF @UserSid IS NULL
       OR DATALENGTH(@UserSid) NOT BETWEEN 8 AND 85
       OR NULLIF(LTRIM(RTRIM(@LoginName)), N'') IS NULL
    BEGIN
        THROW 51000, N'SQL Server did not provide a valid authenticated Windows identity.', 1;
    END;

    IF @IsTechnician = 0
       AND @IsManager = 0
       AND @IsAdmin = 0
       AND @IsSyncOperator = 0
    BEGIN
        THROW 51002, N'The Windows login is not assigned to a TechBench application role.', 1;
    END;

    IF @IsAdmin = 1
    BEGIN
        SET @IsManager = 1;
        SET @IsTechnician = 1;
    END
    ELSE IF @IsManager = 1
    BEGIN
        SET @IsTechnician = 1;
    END;

    SET @DisplayName =
        CASE
            WHEN CHARINDEX(N'\', @LoginName) > 0
                THEN RIGHT(@LoginName, LEN(@LoginName) - CHARINDEX(N'\', @LoginName))
            ELSE @LoginName
        END;
    SET @DisplayName = LEFT(@DisplayName, 160);

    BEGIN TRY
        BEGIN TRANSACTION;

        UPDATE [tb_security].[Users] WITH (UPDLOCK, HOLDLOCK)
        SET
            [LoginName] = @LoginName,
            [DisplayName] =
                CASE
                    WHEN NULLIF(LTRIM(RTRIM([DisplayName])), N'') IS NULL
                        OR [DisplayName] = [LoginName]
                        OR [DisplayName] =
                            CASE
                                WHEN CHARINDEX(N'\', [LoginName]) > 0
                                    THEN RIGHT(
                                        [LoginName],
                                        LEN([LoginName]) - CHARINDEX(N'\', [LoginName]))
                                ELSE [LoginName]
                            END
                        THEN @DisplayName
                    ELSE [DisplayName]
                END,
            [IsTechnician] = @IsTechnician,
            [IsManager] = @IsManager,
            [IsAdmin] = @IsAdmin,
            [IsSyncOperator] = @IsSyncOperator,
            [LastSeenAtUtc] = SYSUTCDATETIME()
        WHERE [WindowsSid] = @UserSid;

        IF @@ROWCOUNT = 0
        BEGIN
            INSERT INTO [tb_security].[Users]
            (
                [WindowsSid],
                [LoginName],
                [DisplayName],
                [IsTechnician],
                [IsManager],
                [IsAdmin],
                [IsSyncOperator]
            )
            VALUES
            (
                @UserSid,
                @LoginName,
                @DisplayName,
                @IsTechnician,
                @IsManager,
                @IsAdmin,
                @IsSyncOperator
            );
        END;

        SELECT
            @DisplayName = [DisplayName]
        FROM [tb_security].[Users]
        WHERE [WindowsSid] = @UserSid;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_app.GetCurrentUserContext', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetCurrentUserContext];
GO

CREATE PROCEDURE [tb_app].[GetCurrentUserContext]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @LoginName nvarchar(256);
    DECLARE @DisplayName nvarchar(160);
    DECLARE @IsTechnician bit;
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;
    DECLARE @DatabaseInstanceId uniqueidentifier;
    DECLARE @SchemaVersion int;

    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @UserSid OUTPUT,
        @LoginName = @LoginName OUTPUT,
        @DisplayName = @DisplayName OUTPUT,
        @IsTechnician = @IsTechnician OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT @DatabaseInstanceId = TRY_CONVERT(uniqueidentifier, [Value])
    FROM [tb_data].[ServerMetadata]
    WHERE [Key] = N'Server.InstanceId';

    SELECT @SchemaVersion = MAX([SchemaVersion])
    FROM [tb_deploy].[SchemaMigrations];

    IF @DatabaseInstanceId IS NULL OR @SchemaVersion IS NULL
    BEGIN
        THROW 51020, N'The TechBench database metadata is incomplete.', 1;
    END;

    SELECT
        @UserSid AS [UserSid],
        @LoginName AS [LoginName],
        @DisplayName AS [DisplayName],
        @DatabaseInstanceId AS [DatabaseInstanceId],
        @SchemaVersion AS [SchemaVersion],
        SYSUTCDATETIME() AS [ServerUtc],
        @IsTechnician AS [IsTechnician],
        @IsManager AS [IsManager],
        @IsAdmin AS [IsAdmin],
        @IsSyncOperator AS [IsSyncOperator];
END;
GO

IF OBJECT_ID(N'tb_app.SearchClients', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SearchClients];
GO

CREATE PROCEDURE [tb_app].[SearchClients]
    @IncludeInactive bit = 0,
    @Search nvarchar(240) = NULL,
    @Limit int = 250
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @LoginName nvarchar(256);
    DECLARE @DisplayName nvarchar(160);
    DECLARE @IsTechnician bit;
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @UserSid OUTPUT,
        @LoginName = @LoginName OUTPUT,
        @DisplayName = @DisplayName OUTPUT,
        @IsTechnician = @IsTechnician OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @Limit =
        CASE
            WHEN @Limit IS NULL OR @Limit < 1 THEN 1
            WHEN @Limit > 1000 THEN 1000
            ELSE @Limit
        END;

    SET @Search = NULLIF(LTRIM(RTRIM(@Search)), N'');

    DECLARE @Pattern nvarchar(500) = NULL;
    IF @Search IS NOT NULL
    BEGIN
        SET @Pattern = REPLACE(@Search, N'~', N'~~');
        SET @Pattern = REPLACE(@Pattern, N'%', N'~%');
        SET @Pattern = REPLACE(@Pattern, N'_', N'~_');
        SET @Pattern = REPLACE(@Pattern, N'[', N'~[');
        SET @Pattern = N'%' + @Pattern + N'%';
    END;

    SELECT TOP (@Limit)
        client.[Id],
        client.[Name],
        client.[Source],
        client.[ExternalId],
        client.[IsActive],
        client.[LastSyncedAtUtc] AS [LastSyncedAt],
        client.[WhdLocationName],
        client.[WhdContactName],
        client.[SageCustomerId],
        client.[SageCustomerName],
        client.[SageContactName],
        client.[SageTelephone],
        client.[MatchStatus],
        client.[RowVersion]
    FROM [tb_data].[Clients] AS client
    WHERE (@IncludeInactive = 1 OR client.[IsActive] = 1)
      AND
      (
          @Pattern IS NULL
          OR client.[Name] LIKE @Pattern ESCAPE N'~'
          OR client.[WhdLocationName] LIKE @Pattern ESCAPE N'~'
          OR client.[WhdContactName] LIKE @Pattern ESCAPE N'~'
          OR client.[SageCustomerId] LIKE @Pattern ESCAPE N'~'
          OR client.[SageCustomerName] LIKE @Pattern ESCAPE N'~'
          OR client.[SageContactName] LIKE @Pattern ESCAPE N'~'
      )
    ORDER BY client.[Name], client.[Id];
END;
GO

IF OBJECT_ID(N'tb_app.GetClient', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetClient];
GO

CREATE PROCEDURE [tb_app].[GetClient]
    @Id int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @LoginName nvarchar(256);
    DECLARE @DisplayName nvarchar(160);
    DECLARE @IsTechnician bit;
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @UserSid OUTPUT,
        @LoginName = @LoginName OUTPUT,
        @DisplayName = @DisplayName OUTPUT,
        @IsTechnician = @IsTechnician OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT
        client.[Id],
        client.[Name],
        client.[Source],
        client.[ExternalId],
        client.[IsActive],
        client.[LastSyncedAtUtc] AS [LastSyncedAt],
        client.[WhdLocationName],
        client.[WhdContactName],
        client.[SageCustomerId],
        client.[SageCustomerName],
        client.[SageContactName],
        client.[SageTelephone],
        client.[MatchStatus],
        client.[RowVersion],
        client.[CreatedAtUtc],
        client.[UpdatedAtUtc]
    FROM [tb_data].[Clients] AS client
    WHERE client.[Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveClient', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveClient];
GO

CREATE PROCEDURE [tb_app].[AdminSaveClient]
    @Id int = NULL,
    @Name nvarchar(max),
    @Source nvarchar(max) = N'Manual',
    @ExternalId nvarchar(max) = NULL,
    @IsActive bit = 1,
    @LastSyncedAtUtc datetime2(3) = NULL,
    @WhdLocationName nvarchar(max) = NULL,
    @WhdContactName nvarchar(max) = NULL,
    @SageCustomerId nvarchar(max) = NULL,
    @SageCustomerName nvarchar(max) = NULL,
    @SageContactName nvarchar(max) = NULL,
    @SageTelephone nvarchar(max) = NULL,
    @MatchStatus nvarchar(max) = N'Unmatched',
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorWindowsSid varbinary(85);
    DECLARE @ActorLoginName nvarchar(256);
    DECLARE @DisplayName nvarchar(160);
    DECLARE @IsTechnician bit;
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @ActorWindowsSid OUTPUT,
        @LoginName = @ActorLoginName OUTPUT,
        @DisplayName = @DisplayName OUTPUT,
        @IsTechnician = @IsTechnician OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
    BEGIN
        THROW 51003, N'Only a current TechBench Admin may save shared clients.', 1;
    END;

    SET @Name = NULLIF(LTRIM(RTRIM(@Name)), N'');
    SET @Source = COALESCE(NULLIF(LTRIM(RTRIM(@Source)), N''), N'Manual');
    SET @ExternalId = NULLIF(LTRIM(RTRIM(@ExternalId)), N'');
    SET @WhdLocationName = NULLIF(LTRIM(RTRIM(@WhdLocationName)), N'');
    SET @WhdContactName = NULLIF(LTRIM(RTRIM(@WhdContactName)), N'');
    SET @SageCustomerId = NULLIF(LTRIM(RTRIM(@SageCustomerId)), N'');
    SET @SageCustomerName = NULLIF(LTRIM(RTRIM(@SageCustomerName)), N'');
    SET @SageContactName = NULLIF(LTRIM(RTRIM(@SageContactName)), N'');
    SET @SageTelephone = NULLIF(LTRIM(RTRIM(@SageTelephone)), N'');
    SET @MatchStatus =
        COALESCE(NULLIF(LTRIM(RTRIM(@MatchStatus)), N''), N'Unmatched');
    SET @RequestId = COALESCE(@RequestId, NEWID());

    IF @Name IS NULL
        THROW 51010, N'Client name is required.', 1;
    IF LEN(@Name) > 240
        THROW 51010, N'Client name exceeds 240 characters.', 1;
    IF LEN(@Source) > 80
        THROW 51010, N'Client source exceeds 80 characters.', 1;
    IF @Source NOT IN (N'Manual', N'WHD', N'Sage', N'Both')
        THROW 51010, N'Client source must be Manual, WHD, Sage, or Both.', 1;
    IF LEN(@ExternalId) > 500
        THROW 51010, N'External client ID exceeds 500 characters.', 1;
    IF LEN(@WhdLocationName) > 240
        THROW 51010, N'WHD location name exceeds 240 characters.', 1;
    IF LEN(@WhdContactName) > 240
        THROW 51010, N'WHD contact name exceeds 240 characters.', 1;
    IF LEN(@SageCustomerId) > 120
        THROW 51010, N'Sage customer ID exceeds 120 characters.', 1;
    IF LEN(@SageCustomerName) > 240
        THROW 51010, N'Sage customer name exceeds 240 characters.', 1;
    IF LEN(@SageContactName) > 240
        THROW 51010, N'Sage contact name exceeds 240 characters.', 1;
    IF LEN(@SageTelephone) > 80
        THROW 51010, N'Sage telephone exceeds 80 characters.', 1;
    IF LEN(@MatchStatus) > 80
        THROW 51010, N'Client match status exceeds 80 characters.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51010, N'ExpectedRowVersion is required when updating a client.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @Action nvarchar(120);
    DECLARE @DataJson nvarchar(max);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[Clients]
            (
                [Name],
                [Source],
                [ExternalId],
                [IsActive],
                [LastSyncedAtUtc],
                [WhdLocationName],
                [WhdContactName],
                [SageCustomerId],
                [SageCustomerName],
                [SageContactName],
                [SageTelephone],
                [MatchStatus],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                CONVERT(nvarchar(240), @Name),
                CONVERT(nvarchar(80), @Source),
                CONVERT(nvarchar(500), @ExternalId),
                @IsActive,
                @LastSyncedAtUtc,
                CONVERT(nvarchar(240), @WhdLocationName),
                CONVERT(nvarchar(240), @WhdContactName),
                CONVERT(nvarchar(120), @SageCustomerId),
                CONVERT(nvarchar(240), @SageCustomerName),
                CONVERT(nvarchar(240), @SageContactName),
                CONVERT(nvarchar(80), @SageTelephone),
                CONVERT(nvarchar(80), @MatchStatus),
                @ActorWindowsSid,
                @ActorWindowsSid,
                @NowUtc,
                @NowUtc
            );

            SET @Id = CONVERT(int, SCOPE_IDENTITY());
            SET @Action = N'ClientCreated';
        END
        ELSE
        BEGIN
            UPDATE [tb_data].[Clients]
            SET
                [Name] = CONVERT(nvarchar(240), @Name),
                [Source] = CONVERT(nvarchar(80), @Source),
                [ExternalId] = CONVERT(nvarchar(500), @ExternalId),
                [IsActive] = @IsActive,
                [LastSyncedAtUtc] = @LastSyncedAtUtc,
                [WhdLocationName] = CONVERT(nvarchar(240), @WhdLocationName),
                [WhdContactName] = CONVERT(nvarchar(240), @WhdContactName),
                [SageCustomerId] = CONVERT(nvarchar(120), @SageCustomerId),
                [SageCustomerName] = CONVERT(nvarchar(240), @SageCustomerName),
                [SageContactName] = CONVERT(nvarchar(240), @SageContactName),
                [SageTelephone] = CONVERT(nvarchar(80), @SageTelephone),
                [MatchStatus] = CONVERT(nvarchar(80), @MatchStatus),
                [UpdatedByWindowsSid] = @ActorWindowsSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
            BEGIN
                IF NOT EXISTS
                (
                    SELECT 1
                    FROM [tb_data].[Clients]
                    WHERE [Id] = @Id
                )
                    THROW 51011, N'The shared client no longer exists.', 1;

                THROW 51012, N'The shared client changed after it was loaded.', 1;
            END;

            SET @Action = N'ClientUpdated';
        END;

        SELECT @DataJson =
        (
            SELECT
                client.[Name] AS [name],
                client.[Source] AS [source],
                client.[IsActive] AS [isActive],
                client.[MatchStatus] AS [matchStatus],
                CONVERT(varchar(18), client.[RowVersion], 1) AS [rowVersion]
            FROM [tb_data].[Clients] AS client
            WHERE client.[Id] = @Id
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );

        INSERT INTO [tb_audit].[AuditEvents]
        (
            [ActorWindowsSid],
            [ActorLoginName],
            [Action],
            [EntityType],
            [EntityId],
            [RequestId],
            [DataJson],
            [OccurredAtUtc]
        )
        VALUES
        (
            @ActorWindowsSid,
            @ActorLoginName,
            @Action,
            N'Client',
            CONVERT(nvarchar(120), @Id),
            @RequestId,
            @DataJson,
            @NowUtc
        );

        SELECT
            client.[Id],
            client.[Name],
            client.[Source],
            client.[ExternalId],
            client.[IsActive],
            client.[LastSyncedAtUtc] AS [LastSyncedAt],
            client.[WhdLocationName],
            client.[WhdContactName],
            client.[SageCustomerId],
            client.[SageCustomerName],
            client.[SageContactName],
            client.[SageTelephone],
            client.[MatchStatus],
            client.[RowVersion],
            client.[CreatedAtUtc],
            client.[UpdatedAtUtc]
        FROM [tb_data].[Clients] AS client
        WHERE client.[Id] = @Id;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_app.ReadAuditEvents', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ReadAuditEvents];
GO

CREATE PROCEDURE [tb_app].[ReadAuditEvents]
    @SinceUtc datetime2(3) = NULL,
    @Limit int = 250
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF IS_ROLEMEMBER(N'tb_role_admin') <> 1
    BEGIN
        THROW 51003, N'Only a current TechBench Admin may read TechBench audit events.', 1;
    END;

    SET @Limit =
        CASE
            WHEN @Limit IS NULL OR @Limit < 1 THEN 1
            WHEN @Limit > 1000 THEN 1000
            ELSE @Limit
        END;

    SELECT TOP (@Limit)
        audit_event.[Id],
        audit_event.[ActorWindowsSid],
        audit_event.[ActorLoginName],
        audit_event.[Action],
        audit_event.[EntityType],
        audit_event.[EntityId],
        audit_event.[RequestId],
        audit_event.[DataJson],
        audit_event.[OccurredAtUtc]
    FROM [tb_audit].[AuditEvents] AS audit_event
    WHERE @SinceUtc IS NULL
       OR audit_event.[OccurredAtUtc] >= @SinceUtc
    ORDER BY audit_event.[OccurredAtUtc] DESC, audit_event.[Id] DESC;
END;
GO

PRINT N'TechBench stored procedures created.';
GO

-- ============================================================================
-- END 40-StoredProcedures.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 50-Grants.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

GRANT EXECUTE ON OBJECT::[tb_app].[GetCurrentUserContext]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SearchClients]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClient]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetCurrentUserContext]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SearchClients]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClient]
    TO [tb_role_sync_operator];

GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveClient]
    TO [tb_role_admin];

GRANT EXECUTE ON OBJECT::[tb_app].[ReadAuditEvents]
    TO [tb_role_admin];

PRINT N'TechBench stored-procedure-only grants applied.';
GO

-- ============================================================================
-- END 50-Grants.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 90-Verify.sql
-- ============================================================================

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
) <> 0x01
BEGIN
    PRINT N'FAIL: Database owner is not the built-in SQL Server owner principal (SID 0x01).';
    SET @FailureCount += 1;
END;

IF
(
    SELECT [recovery_model_desc]
    FROM sys.databases
    WHERE [name] = DB_NAME()
) <> N'SIMPLE'
BEGIN
    PRINT N'FAIL: Database recovery model is not SIMPLE.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.databases
    WHERE [name] = DB_NAME()
      AND
      (
          [is_auto_close_on] <> 0
          OR [is_auto_shrink_on] <> 0
          OR [page_verify_option_desc] <> N'CHECKSUM'
          OR [is_trustworthy_on] <> 0
          OR [is_db_chaining_on] <> 0
          OR [snapshot_isolation_state] <> 1
          OR [is_read_committed_snapshot_on] <> 1
      )
)
BEGIN
    PRINT N'FAIL: One or more required TechBench database safety/concurrency options are incorrect.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.database_files
    WHERE [type] = 0
      AND [file_id] = 1
      AND [size] >= 32768
      AND [growth] = 8192
      AND [is_percent_growth] = 0
)
BEGIN
    PRINT N'FAIL: Primary data file is not at least 256 MB with fixed 64 MB growth.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.database_files
    WHERE [type] = 1
      AND [size] >= 16384
      AND [growth] = 8192
      AND [is_percent_growth] = 0
)
BEGIN
    PRINT N'FAIL: Log file is not at least 128 MB with fixed 64 MB growth.';
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

IF DATABASE_PRINCIPAL_ID(N'tb_role_auditor') IS NOT NULL
   OR DATABASE_PRINCIPAL_ID(N'tb_role_deployer') IS NOT NULL
BEGIN
    PRINT N'FAIL: An obsolete TechBench preview database role still exists.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredMembership TABLE
(
    [RoleName] sysname NOT NULL,
    [MemberName] sysname NOT NULL
);

INSERT INTO @RequiredMembership([RoleName], [MemberName])
VALUES
    (N'tb_role_user', N'$(UserGroup)'),
    (N'tb_role_user', N'$(AdminGroup)'),
    (N'tb_role_manager', N'$(AdminGroup)'),
    (N'tb_role_admin', N'$(AdminGroup)'),
    (N'tb_role_sync_operator', N'$(AdminGroup)');

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
            N'$(UserGroup)',
            N'$(AdminGroup)'
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

IF NOT EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission
    INNER JOIN sys.database_principals AS grantee
        ON grantee.principal_id = permission.grantee_principal_id
    WHERE grantee.name = N'tb_role_admin'
      AND permission.class = 1
      AND permission.major_id = OBJECT_ID(N'tb_app.ReadAuditEvents')
      AND permission.permission_name = N'EXECUTE'
      AND permission.state IN (N'G', N'W')
)
BEGIN
    PRINT N'FAIL: tb_role_admin cannot execute tb_app.ReadAuditEvents.';
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
            N'tb_role_sync_operator'
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
    database_info.[recovery_model_desc] AS [RecoveryModel],
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

-- ============================================================================
-- END 90-Verify.sql
-- ============================================================================

PRINT N'TechBench deployment completed successfully on CSRI-SQL.';
GO
