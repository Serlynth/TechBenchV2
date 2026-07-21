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
:setvar SyncServicePrincipal "CSRI\TechBench_Sync"

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
DECLARE @SyncServicePrincipal sysname = N'$(SyncServicePrincipal)';
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
   OR NULLIF(LTRIM(RTRIM(@SyncServicePrincipal)), N'') IS NULL
BEGIN
    RAISERROR(N'UserGroup, AdminGroup, and SyncServicePrincipal must all be supplied.', 16, 1);
    RETURN;
END;

IF @UserGroup NOT LIKE N'%\%'
   OR @AdminGroup NOT LIKE N'%\%'
   OR @SyncServicePrincipal NOT LIKE N'%\%'
BEGIN
    RAISERROR(
        N'Application groups and SyncServicePrincipal must use DOMAIN\name format.',
        16,
        1);
    RETURN;
END;

IF @UserGroup = @AdminGroup
   OR @UserGroup = @SyncServicePrincipal
   OR @AdminGroup = @SyncServicePrincipal
BEGIN
    RAISERROR(
        N'UserGroup, AdminGroup, and SyncServicePrincipal must be three distinct principals.',
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

    /* Resolves the dedicated service account without enumerating members. */
    EXEC master.dbo.xp_logininfo
        @acctname = @SyncServicePrincipal;
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
PRINT N'  WHD sync service principal: ' + @SyncServicePrincipal;
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
-- BEGIN 21-V0002-OperationalSchema.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.Baseline.0001'
      AND [SchemaVersion] = 1
)
BEGIN
    RAISERROR(
        N'The TechBench SQL Server baseline must be installed before OperationalStorage.0002.',
        16,
        1);
    RETURN;
END;

IF EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.OperationalStorage.0002'
      AND [SchemaVersion] = 2
)
BEGIN
    PRINT N'SqlServer2016.OperationalStorage.0002 is already installed.';
    RETURN;
END;

IF OBJECT_ID(N'tb_data.Tickets', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_data.TicketStatusOptions', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_data.WorkEntries', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_private.WorkEntryPersonalNotes', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_data.WorkEntryLinks', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_user.EditorDrafts', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_data.Templates', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_data.CommonLinks', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_data.OrganizationSettings', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_user.UserSettings', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_data.ClientAliases', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_data.ClientExternalIdentities', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_ops.PostingLogs', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_ops.PostingAttempts', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_ops.PostingLeases', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_ops.SyncLeases', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_ops.SyncRuns', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_ops.ImportBatches', N'U') IS NOT NULL
   OR OBJECT_ID(N'tb_ops.LegacyIdMappings', N'U') IS NOT NULL
BEGIN
    RAISERROR(
        N'Operational-storage objects exist without the V0002 migration marker. Stop and investigate the partial deployment.',
        16,
        1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    IF SCHEMA_ID(N'tb_private') IS NULL
        EXEC(N'CREATE SCHEMA [tb_private] AUTHORIZATION [dbo];');
    IF SCHEMA_ID(N'tb_user') IS NULL
        EXEC(N'CREATE SCHEMA [tb_user] AUTHORIZATION [dbo];');
    IF SCHEMA_ID(N'tb_ops') IS NULL
        EXEC(N'CREATE SCHEMA [tb_ops] AUTHORIZATION [dbo];');

    CREATE TABLE [tb_data].[TicketStatusOptions]
    (
        [Id] int IDENTITY(1,1) NOT NULL,
        [Name] nvarchar(160) NOT NULL,
        [Source] nvarchar(40) NOT NULL
            CONSTRAINT [DF_TicketStatusOptions_Source] DEFAULT (N'WHD'),
        [ExternalId] nvarchar(240) NULL,
        [WhdStatusTypeId] int NULL,
        [IsClosed] bit NOT NULL
            CONSTRAINT [DF_TicketStatusOptions_IsClosed] DEFAULT (0),
        [LastSyncedAtUtc] datetime2(3) NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_TicketStatusOptions_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_TicketStatusOptions_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_TicketStatusOptions] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [CK_TicketStatusOptions_Name]
            CHECK (LEN(LTRIM(RTRIM([Name]))) > 0),
        CONSTRAINT [CK_TicketStatusOptions_Source]
            CHECK (LEN(LTRIM(RTRIM([Source]))) > 0)
    );

    CREATE UNIQUE INDEX [UX_TicketStatusOptions_SourceExternalId]
        ON [tb_data].[TicketStatusOptions]([Source], [ExternalId])
        WHERE [ExternalId] IS NOT NULL;
    CREATE INDEX [IX_TicketStatusOptions_Name]
        ON [tb_data].[TicketStatusOptions]([Name]);

    CREATE TABLE [tb_data].[Tickets]
    (
        [Id] int IDENTITY(1,1) NOT NULL,
        [TicketNumber] nvarchar(120) NOT NULL,
        [ClientId] int NOT NULL,
        [Subject] nvarchar(500) NOT NULL
            CONSTRAINT [DF_Tickets_Subject] DEFAULT (N''),
        [Status] nvarchar(160) NOT NULL
            CONSTRAINT [DF_Tickets_Status] DEFAULT (N'Open'),
        [Source] nvarchar(40) NOT NULL
            CONSTRAINT [DF_Tickets_Source] DEFAULT (N'Manual'),
        [ExternalId] nvarchar(240) NULL,
        [WhdStatusTypeId] int NULL,
        [IsClosed] bit NOT NULL
            CONSTRAINT [DF_Tickets_IsClosed] DEFAULT (0),
        [LastSyncedAtUtc] datetime2(3) NULL,
        [CreatedByWindowsSid] varbinary(85) NOT NULL,
        [UpdatedByWindowsSid] varbinary(85) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_Tickets_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_Tickets_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_Tickets] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_Tickets_Client]
            FOREIGN KEY ([ClientId]) REFERENCES [tb_data].[Clients]([Id]),
        CONSTRAINT [FK_Tickets_CreatedBy]
            FOREIGN KEY ([CreatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [FK_Tickets_UpdatedBy]
            FOREIGN KEY ([UpdatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_Tickets_TicketNumber]
            CHECK (LEN(LTRIM(RTRIM([TicketNumber]))) > 0),
        CONSTRAINT [CK_Tickets_Source]
            CHECK (LEN(LTRIM(RTRIM([Source]))) > 0)
    );

    CREATE INDEX [IX_Tickets_ClientId]
        ON [tb_data].[Tickets]([ClientId], [IsClosed], [TicketNumber]);
    CREATE INDEX [IX_Tickets_TicketNumber]
        ON [tb_data].[Tickets]([TicketNumber]);
    CREATE UNIQUE INDEX [UX_Tickets_SourceExternalId]
        ON [tb_data].[Tickets]([Source], [ExternalId])
        WHERE [ExternalId] IS NOT NULL;

    CREATE TABLE [tb_data].[WorkEntries]
    (
        [Id] int IDENTITY(1,1) NOT NULL,
        [OwnerWindowsSid] varbinary(85) NOT NULL,
        [WorkDate] date NOT NULL,
        [ClientId] int NULL,
        [ManualClientName] nvarchar(240) NULL,
        [TicketId] int NULL,
        [TicketNumberText] nvarchar(120) NULL,
        [HasTimeRange] bit NOT NULL
            CONSTRAINT [DF_WorkEntries_HasTimeRange] DEFAULT (1),
        [StartTime] time(0) NOT NULL
            CONSTRAINT [DF_WorkEntries_StartTime] DEFAULT ('00:00'),
        [EndTime] time(0) NOT NULL
            CONSTRAINT [DF_WorkEntries_EndTime] DEFAULT ('00:00'),
        [DurationMinutes] int NOT NULL,
        [Billable] bit NOT NULL
            CONSTRAINT [DF_WorkEntries_Billable] DEFAULT (1),
        [Note] nvarchar(max) NOT NULL
            CONSTRAINT [DF_WorkEntries_Note] DEFAULT (N''),
        [Tags] nvarchar(1000) NOT NULL
            CONSTRAINT [DF_WorkEntries_Tags] DEFAULT (N''),
        [FollowUpState] nvarchar(30) NOT NULL
            CONSTRAINT [DF_WorkEntries_FollowUpState] DEFAULT (N'None'),
        [FollowUpDueDate] date NULL,
        [WhdPosted] bit NOT NULL
            CONSTRAINT [DF_WorkEntries_WhdPosted] DEFAULT (0),
        [WhdPostedAtUtc] datetime2(3) NULL,
        [SagePosted] bit NOT NULL
            CONSTRAINT [DF_WorkEntries_SagePosted] DEFAULT (0),
        [SagePostedAtUtc] datetime2(3) NULL,
        [SageTicketNumber] nvarchar(120) NULL,
        [PostingStatus] nvarchar(40) NOT NULL
            CONSTRAINT [DF_WorkEntries_PostingStatus] DEFAULT (N'Draft'),
        [LastError] nvarchar(max) NULL,
        [CreatedByWindowsSid] varbinary(85) NOT NULL,
        [UpdatedByWindowsSid] varbinary(85) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_WorkEntries_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_WorkEntries_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_WorkEntries] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_WorkEntries_Owner]
            FOREIGN KEY ([OwnerWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [FK_WorkEntries_Client]
            FOREIGN KEY ([ClientId]) REFERENCES [tb_data].[Clients]([Id]),
        CONSTRAINT [FK_WorkEntries_Ticket]
            FOREIGN KEY ([TicketId]) REFERENCES [tb_data].[Tickets]([Id]),
        CONSTRAINT [FK_WorkEntries_CreatedBy]
            FOREIGN KEY ([CreatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [FK_WorkEntries_UpdatedBy]
            FOREIGN KEY ([UpdatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_WorkEntries_Duration]
            CHECK ([DurationMinutes] >= 0 AND [DurationMinutes] <= 1440),
        CONSTRAINT [CK_WorkEntries_Client]
            CHECK ([ClientId] IS NOT NULL
                OR NULLIF(LTRIM(RTRIM([ManualClientName])), N'') IS NOT NULL),
        CONSTRAINT [CK_WorkEntries_FollowUpState]
            CHECK ([FollowUpState] IN (N'None', N'FollowUp', N'Waiting', N'Completed')),
        CONSTRAINT [CK_WorkEntries_PostingStatus]
            CHECK ([PostingStatus] IN
                (N'Draft', N'Ready', N'PostedToWhd', N'PostedToSage', N'PostedToBoth', N'Failed'))
    );

    CREATE INDEX [IX_WorkEntries_OwnerDate]
        ON [tb_data].[WorkEntries]([OwnerWindowsSid], [WorkDate] DESC, [Id] DESC);
    CREATE INDEX [IX_WorkEntries_ClientId]
        ON [tb_data].[WorkEntries]([ClientId], [WorkDate] DESC);
    CREATE INDEX [IX_WorkEntries_TicketId]
        ON [tb_data].[WorkEntries]([TicketId], [WorkDate] DESC);
    CREATE INDEX [IX_WorkEntries_FollowUp]
        ON [tb_data].[WorkEntries]([OwnerWindowsSid], [FollowUpState], [FollowUpDueDate]);
    CREATE INDEX [IX_WorkEntries_Posting]
        ON [tb_data].[WorkEntries]([OwnerWindowsSid], [PostingStatus], [WhdPosted], [SagePosted]);

    CREATE TABLE [tb_private].[WorkEntryPersonalNotes]
    (
        [WorkEntryId] int NOT NULL,
        [OwnerWindowsSid] varbinary(85) NOT NULL,
        [Note] nvarchar(max) NOT NULL
            CONSTRAINT [DF_WorkEntryPersonalNotes_Note] DEFAULT (N''),
        [IncludeInWhd] bit NOT NULL
            CONSTRAINT [DF_WorkEntryPersonalNotes_IncludeInWhd] DEFAULT (0),
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_WorkEntryPersonalNotes_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_WorkEntryPersonalNotes_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_WorkEntryPersonalNotes] PRIMARY KEY CLUSTERED ([WorkEntryId]),
        CONSTRAINT [FK_WorkEntryPersonalNotes_WorkEntry]
            FOREIGN KEY ([WorkEntryId])
            REFERENCES [tb_data].[WorkEntries]([Id])
            ON DELETE CASCADE,
        CONSTRAINT [FK_WorkEntryPersonalNotes_Owner]
            FOREIGN KEY ([OwnerWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid])
    );

    CREATE INDEX [IX_WorkEntryPersonalNotes_Owner]
        ON [tb_private].[WorkEntryPersonalNotes]([OwnerWindowsSid], [WorkEntryId]);

    CREATE TABLE [tb_data].[WorkEntryLinks]
    (
        [Id] int IDENTITY(1,1) NOT NULL,
        [SourceWorkEntryId] int NOT NULL,
        [TargetWorkEntryId] int NOT NULL,
        [LinkType] nvarchar(30) NOT NULL
            CONSTRAINT [DF_WorkEntryLinks_LinkType] DEFAULT (N'Related'),
        [CreatedByWindowsSid] varbinary(85) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_WorkEntryLinks_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_WorkEntryLinks] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_WorkEntryLinks_Source]
            FOREIGN KEY ([SourceWorkEntryId])
            REFERENCES [tb_data].[WorkEntries]([Id]),
        CONSTRAINT [FK_WorkEntryLinks_Target]
            FOREIGN KEY ([TargetWorkEntryId])
            REFERENCES [tb_data].[WorkEntries]([Id]),
        CONSTRAINT [FK_WorkEntryLinks_CreatedBy]
            FOREIGN KEY ([CreatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_WorkEntryLinks_DifferentEntries]
            CHECK ([SourceWorkEntryId] <> [TargetWorkEntryId]),
        CONSTRAINT [CK_WorkEntryLinks_LinkType]
            CHECK ([LinkType] IN (N'Related', N'FollowUpTo'))
    );

    CREATE UNIQUE INDEX [UX_WorkEntryLinks_Pair]
        ON [tb_data].[WorkEntryLinks]
        (
            [SourceWorkEntryId],
            [TargetWorkEntryId],
            [LinkType]
        );
    CREATE INDEX [IX_WorkEntryLinks_Target]
        ON [tb_data].[WorkEntryLinks]([TargetWorkEntryId], [SourceWorkEntryId]);

    CREATE TABLE [tb_user].[EditorDrafts]
    (
        [OwnerWindowsSid] varbinary(85) NOT NULL,
        [DeviceId] uniqueidentifier NOT NULL,
        [Payload] nvarchar(max) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_EditorDrafts_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_EditorDrafts_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_EditorDrafts]
            PRIMARY KEY CLUSTERED ([OwnerWindowsSid], [DeviceId]),
        CONSTRAINT [FK_EditorDrafts_Owner]
            FOREIGN KEY ([OwnerWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_EditorDrafts_Payload]
            CHECK (ISJSON([Payload]) = 1)
    );

    CREATE TABLE [tb_data].[Templates]
    (
        [Id] int IDENTITY(1,1) NOT NULL,
        [ScopeType] nvarchar(20) NOT NULL
            CONSTRAINT [DF_Templates_ScopeType] DEFAULT (N'User'),
        [OwnerWindowsSid] varbinary(85) NULL,
        [Name] nvarchar(160) NOT NULL,
        [Category] nvarchar(160) NOT NULL
            CONSTRAINT [DF_Templates_Category] DEFAULT (N''),
        [TemplateText] nvarchar(max) NOT NULL
            CONSTRAINT [DF_Templates_TemplateText] DEFAULT (N''),
        [CreatedByWindowsSid] varbinary(85) NOT NULL,
        [UpdatedByWindowsSid] varbinary(85) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_Templates_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_Templates_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_Templates] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_Templates_Owner]
            FOREIGN KEY ([OwnerWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [FK_Templates_CreatedBy]
            FOREIGN KEY ([CreatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [FK_Templates_UpdatedBy]
            FOREIGN KEY ([UpdatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_Templates_Name]
            CHECK (LEN(LTRIM(RTRIM([Name]))) > 0),
        CONSTRAINT [CK_Templates_Scope]
            CHECK
            (
                ([ScopeType] = N'Organization' AND [OwnerWindowsSid] IS NULL)
                OR
                ([ScopeType] = N'User' AND [OwnerWindowsSid] IS NOT NULL)
            )
    );

    CREATE INDEX [IX_Templates_CategoryName]
        ON [tb_data].[Templates]([ScopeType], [OwnerWindowsSid], [Category], [Name]);

    CREATE TABLE [tb_data].[CommonLinks]
    (
        [Id] int IDENTITY(1,1) NOT NULL,
        [ScopeType] nvarchar(20) NOT NULL
            CONSTRAINT [DF_CommonLinks_ScopeType] DEFAULT (N'User'),
        [OwnerWindowsSid] varbinary(85) NULL,
        [Name] nvarchar(160) NOT NULL,
        [Url] nvarchar(2048) NOT NULL,
        [UrlHash] binary(32) NOT NULL,
        [SortOrder] int NOT NULL
            CONSTRAINT [DF_CommonLinks_SortOrder] DEFAULT (0),
        [BuiltInKey] nvarchar(120) NULL,
        [CreatedByWindowsSid] varbinary(85) NOT NULL,
        [UpdatedByWindowsSid] varbinary(85) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_CommonLinks_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_CommonLinks_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_CommonLinks] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_CommonLinks_Owner]
            FOREIGN KEY ([OwnerWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [FK_CommonLinks_CreatedBy]
            FOREIGN KEY ([CreatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [FK_CommonLinks_UpdatedBy]
            FOREIGN KEY ([UpdatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_CommonLinks_Name]
            CHECK (LEN(LTRIM(RTRIM([Name]))) > 0),
        CONSTRAINT [CK_CommonLinks_Url]
            CHECK (LEN(LTRIM(RTRIM([Url]))) > 0),
        CONSTRAINT [CK_CommonLinks_Scope]
            CHECK
            (
                ([ScopeType] = N'Organization' AND [OwnerWindowsSid] IS NULL)
                OR
                ([ScopeType] = N'User' AND [OwnerWindowsSid] IS NOT NULL)
            )
    );

    CREATE UNIQUE INDEX [UX_CommonLinks_OrganizationUrl]
        ON [tb_data].[CommonLinks]([UrlHash])
        WHERE [ScopeType] = N'Organization';
    CREATE UNIQUE INDEX [UX_CommonLinks_UserUrl]
        ON [tb_data].[CommonLinks]([OwnerWindowsSid], [UrlHash])
        WHERE [ScopeType] = N'User';
    CREATE UNIQUE INDEX [UX_CommonLinks_BuiltInKey]
        ON [tb_data].[CommonLinks]([BuiltInKey])
        WHERE [BuiltInKey] IS NOT NULL;
    CREATE INDEX [IX_CommonLinks_SortOrder]
        ON [tb_data].[CommonLinks]([ScopeType], [OwnerWindowsSid], [SortOrder], [Name]);

    CREATE TABLE [tb_data].[OrganizationSettings]
    (
        [SettingKey] nvarchar(200) NOT NULL,
        [SettingValue] nvarchar(max) NOT NULL,
        [UpdatedByWindowsSid] varbinary(85) NOT NULL,
        [UpdatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_OrganizationSettings_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_OrganizationSettings] PRIMARY KEY CLUSTERED ([SettingKey]),
        CONSTRAINT [FK_OrganizationSettings_UpdatedBy]
            FOREIGN KEY ([UpdatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid])
    );

    CREATE TABLE [tb_user].[UserSettings]
    (
        [OwnerWindowsSid] varbinary(85) NOT NULL,
        [SettingKey] nvarchar(200) NOT NULL,
        [SettingValue] nvarchar(max) NOT NULL,
        [UpdatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_UserSettings_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_UserSettings]
            PRIMARY KEY CLUSTERED ([OwnerWindowsSid], [SettingKey]),
        CONSTRAINT [FK_UserSettings_Owner]
            FOREIGN KEY ([OwnerWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid])
    );

    CREATE TABLE [tb_data].[ClientAliases]
    (
        [Id] bigint IDENTITY(1,1) NOT NULL,
        [ScopeType] nvarchar(20) NOT NULL
            CONSTRAINT [DF_ClientAliases_ScopeType] DEFAULT (N'User'),
        [OwnerWindowsSid] varbinary(85) NULL,
        [Alias] nvarchar(240) NOT NULL,
        [ClientId] int NOT NULL,
        [CreatedByWindowsSid] varbinary(85) NOT NULL,
        [UpdatedByWindowsSid] varbinary(85) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_ClientAliases_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_ClientAliases_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_ClientAliases] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_ClientAliases_Owner]
            FOREIGN KEY ([OwnerWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [FK_ClientAliases_Client]
            FOREIGN KEY ([ClientId])
            REFERENCES [tb_data].[Clients]([Id])
            ON DELETE CASCADE,
        CONSTRAINT [FK_ClientAliases_CreatedBy]
            FOREIGN KEY ([CreatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [FK_ClientAliases_UpdatedBy]
            FOREIGN KEY ([UpdatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_ClientAliases_Alias]
            CHECK (LEN(LTRIM(RTRIM([Alias]))) > 0),
        CONSTRAINT [CK_ClientAliases_Scope]
            CHECK
            (
                ([ScopeType] = N'Organization' AND [OwnerWindowsSid] IS NULL)
                OR
                ([ScopeType] = N'User' AND [OwnerWindowsSid] IS NOT NULL)
            )
    );

    CREATE UNIQUE INDEX [UX_ClientAliases_OrganizationAlias]
        ON [tb_data].[ClientAliases]([Alias])
        WHERE [ScopeType] = N'Organization';
    CREATE UNIQUE INDEX [UX_ClientAliases_UserAlias]
        ON [tb_data].[ClientAliases]([OwnerWindowsSid], [Alias])
        WHERE [ScopeType] = N'User';
    CREATE INDEX [IX_ClientAliases_ClientId]
        ON [tb_data].[ClientAliases]([ClientId], [ScopeType], [Alias]);

    CREATE TABLE [tb_data].[ClientExternalIdentities]
    (
        [Id] bigint IDENTITY(1,1) NOT NULL,
        [ClientId] int NOT NULL,
        [SourceSystem] nvarchar(40) NOT NULL,
        [ExternalId] nvarchar(500) NOT NULL,
        [ExternalName] nvarchar(240) NULL,
        [LastSyncedAtUtc] datetime2(3) NULL,
        [CreatedByWindowsSid] varbinary(85) NOT NULL,
        [UpdatedByWindowsSid] varbinary(85) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_ClientExternalIdentities_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_ClientExternalIdentities_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_ClientExternalIdentities] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_ClientExternalIdentities_Client]
            FOREIGN KEY ([ClientId])
            REFERENCES [tb_data].[Clients]([Id])
            ON DELETE CASCADE,
        CONSTRAINT [FK_ClientExternalIdentities_CreatedBy]
            FOREIGN KEY ([CreatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [FK_ClientExternalIdentities_UpdatedBy]
            FOREIGN KEY ([UpdatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_ClientExternalIdentities_Source]
            CHECK (LEN(LTRIM(RTRIM([SourceSystem]))) > 0),
        CONSTRAINT [CK_ClientExternalIdentities_ExternalId]
            CHECK (LEN(LTRIM(RTRIM([ExternalId]))) > 0)
    );

    CREATE UNIQUE INDEX [UX_ClientExternalIdentities_SourceExternalId]
        ON [tb_data].[ClientExternalIdentities]([SourceSystem], [ExternalId]);
    CREATE INDEX [IX_ClientExternalIdentities_Client]
        ON [tb_data].[ClientExternalIdentities]([ClientId], [SourceSystem]);

    CREATE TABLE [tb_ops].[PostingLogs]
    (
        [Id] bigint IDENTITY(1,1) NOT NULL,
        [WorkEntryId] int NOT NULL,
        [OwnerWindowsSid] varbinary(85) NOT NULL,
        [Destination] nvarchar(40) NOT NULL,
        [Payload] nvarchar(max) NOT NULL
            CONSTRAINT [DF_PostingLogs_Payload] DEFAULT (N''),
        [Success] bit NOT NULL,
        [Message] nvarchar(max) NOT NULL
            CONSTRAINT [DF_PostingLogs_Message] DEFAULT (N''),
        [ExternalReference] nvarchar(500) NULL,
        [RequestId] uniqueidentifier NOT NULL
            CONSTRAINT [DF_PostingLogs_RequestId] DEFAULT (NEWID()),
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_PostingLogs_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_PostingLogs] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_PostingLogs_WorkEntry]
            FOREIGN KEY ([WorkEntryId])
            REFERENCES [tb_data].[WorkEntries]([Id])
            ON DELETE CASCADE,
        CONSTRAINT [FK_PostingLogs_Owner]
            FOREIGN KEY ([OwnerWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_PostingLogs_Destination]
            CHECK ([Destination] IN (N'WHD', N'Sage'))
    );

    CREATE INDEX [IX_PostingLogs_WorkEntryDestination]
        ON [tb_ops].[PostingLogs]([WorkEntryId], [Destination], [CreatedAtUtc] DESC);
    CREATE INDEX [IX_PostingLogs_OwnerCreated]
        ON [tb_ops].[PostingLogs]([OwnerWindowsSid], [CreatedAtUtc] DESC);

    CREATE TABLE [tb_ops].[PostingAttempts]
    (
        [Id] bigint IDENTITY(1,1) NOT NULL,
        [WorkEntryId] int NOT NULL,
        [OwnerWindowsSid] varbinary(85) NOT NULL,
        [DeviceId] uniqueidentifier NULL,
        [Destination] nvarchar(40) NOT NULL,
        [AttemptKey] nvarchar(120) NOT NULL,
        [PayloadHash] char(64) NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [Message] nvarchar(max) NOT NULL
            CONSTRAINT [DF_PostingAttempts_Message] DEFAULT (N''),
        [ExternalReference] nvarchar(500) NULL,
        [StartedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_PostingAttempts_StartedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [CompletedAtUtc] datetime2(3) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_PostingAttempts] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_PostingAttempts_WorkEntry]
            FOREIGN KEY ([WorkEntryId])
            REFERENCES [tb_data].[WorkEntries]([Id])
            ON DELETE CASCADE,
        CONSTRAINT [FK_PostingAttempts_Owner]
            FOREIGN KEY ([OwnerWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [UQ_PostingAttempts_AttemptKey] UNIQUE ([AttemptKey]),
        CONSTRAINT [CK_PostingAttempts_Destination]
            CHECK ([Destination] IN (N'WHD', N'Sage')),
        CONSTRAINT [CK_PostingAttempts_Status]
            CHECK ([Status] IN
                (N'Started', N'Succeeded', N'Failed', N'Unknown', N'Abandoned')),
        CONSTRAINT [CK_PostingAttempts_PayloadHash]
            CHECK (LEN([PayloadHash]) = 64)
    );

    CREATE INDEX [IX_PostingAttempts_Outstanding]
        ON [tb_ops].[PostingAttempts]
        ([WorkEntryId], [Destination], [Status], [StartedAtUtc] DESC);
    CREATE INDEX [IX_PostingAttempts_Owner]
        ON [tb_ops].[PostingAttempts]([OwnerWindowsSid], [StartedAtUtc] DESC);

    CREATE TABLE [tb_ops].[PostingLeases]
    (
        [WorkEntryId] int NOT NULL,
        [Destination] nvarchar(40) NOT NULL,
        [AttemptId] bigint NOT NULL,
        [LeaseToken] uniqueidentifier NOT NULL,
        [OwnerWindowsSid] varbinary(85) NOT NULL,
        [DeviceId] uniqueidentifier NULL,
        [AcquiredAtUtc] datetime2(3) NOT NULL,
        [HeartbeatAtUtc] datetime2(3) NOT NULL,
        [ExpiresAtUtc] datetime2(3) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_PostingLeases]
            PRIMARY KEY CLUSTERED ([WorkEntryId], [Destination]),
        CONSTRAINT [FK_PostingLeases_WorkEntry]
            FOREIGN KEY ([WorkEntryId])
            REFERENCES [tb_data].[WorkEntries]([Id])
            ON DELETE CASCADE,
        CONSTRAINT [FK_PostingLeases_Attempt]
            FOREIGN KEY ([AttemptId])
            REFERENCES [tb_ops].[PostingAttempts]([Id]),
        CONSTRAINT [FK_PostingLeases_Owner]
            FOREIGN KEY ([OwnerWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [UQ_PostingLeases_LeaseToken] UNIQUE ([LeaseToken]),
        CONSTRAINT [CK_PostingLeases_Destination]
            CHECK ([Destination] IN (N'WHD', N'Sage')),
        CONSTRAINT [CK_PostingLeases_Expiry]
            CHECK ([ExpiresAtUtc] > [AcquiredAtUtc])
    );

    CREATE TABLE [tb_ops].[SyncLeases]
    (
        [SourceSystem] nvarchar(40) NOT NULL,
        [LeaseId] uniqueidentifier NOT NULL,
        [OwnerWindowsSid] varbinary(85) NOT NULL,
        [DeviceId] uniqueidentifier NOT NULL,
        [AcquiredAtUtc] datetime2(3) NOT NULL,
        [ExpiresAtUtc] datetime2(3) NOT NULL,
        [UpdatedAtUtc] datetime2(3) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_SyncLeases] PRIMARY KEY CLUSTERED ([SourceSystem]),
        CONSTRAINT [FK_SyncLeases_Owner]
            FOREIGN KEY ([OwnerWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_SyncLeases_Source]
            CHECK (LEN(LTRIM(RTRIM([SourceSystem]))) > 0),
        CONSTRAINT [CK_SyncLeases_Expiry]
            CHECK ([ExpiresAtUtc] > [AcquiredAtUtc])
    );

    CREATE TABLE [tb_ops].[SyncRuns]
    (
        [Id] uniqueidentifier NOT NULL,
        [SourceSystem] nvarchar(40) NOT NULL,
        [LeaseId] uniqueidentifier NOT NULL,
        [OwnerWindowsSid] varbinary(85) NOT NULL,
        [DeviceId] uniqueidentifier NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [ReadCount] int NOT NULL
            CONSTRAINT [DF_SyncRuns_ReadCount] DEFAULT (0),
        [SavedCount] int NOT NULL
            CONSTRAINT [DF_SyncRuns_SavedCount] DEFAULT (0),
        [StaleCount] int NOT NULL
            CONSTRAINT [DF_SyncRuns_StaleCount] DEFAULT (0),
        [Message] nvarchar(max) NOT NULL
            CONSTRAINT [DF_SyncRuns_Message] DEFAULT (N''),
        [StartedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_SyncRuns_StartedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [CompletedAtUtc] datetime2(3) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_SyncRuns] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_SyncRuns_Owner]
            FOREIGN KEY ([OwnerWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_SyncRuns_Status]
            CHECK ([Status] IN (N'Started', N'Succeeded', N'Failed', N'Abandoned')),
        CONSTRAINT [CK_SyncRuns_Counts]
            CHECK ([ReadCount] >= 0 AND [SavedCount] >= 0 AND [StaleCount] >= 0)
    );

    CREATE INDEX [IX_SyncRuns_SourceStarted]
        ON [tb_ops].[SyncRuns]([SourceSystem], [StartedAtUtc] DESC);

    CREATE TABLE [tb_ops].[ImportBatches]
    (
        [Id] uniqueidentifier NOT NULL,
        [SourceSystem] nvarchar(80) NOT NULL,
        [FileName] nvarchar(500) NULL,
        [FileHash] char(64) NULL,
        [OwnerWindowsSid] varbinary(85) NOT NULL,
        [DeviceId] uniqueidentifier NULL,
        [Status] nvarchar(30) NOT NULL,
        [ReadCount] int NOT NULL
            CONSTRAINT [DF_ImportBatches_ReadCount] DEFAULT (0),
        [ImportedCount] int NOT NULL
            CONSTRAINT [DF_ImportBatches_ImportedCount] DEFAULT (0),
        [SkippedCount] int NOT NULL
            CONSTRAINT [DF_ImportBatches_SkippedCount] DEFAULT (0),
        [ErrorCount] int NOT NULL
            CONSTRAINT [DF_ImportBatches_ErrorCount] DEFAULT (0),
        [Message] nvarchar(max) NOT NULL
            CONSTRAINT [DF_ImportBatches_Message] DEFAULT (N''),
        [StartedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_ImportBatches_StartedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [CompletedAtUtc] datetime2(3) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_ImportBatches] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_ImportBatches_Owner]
            FOREIGN KEY ([OwnerWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_ImportBatches_Status]
            CHECK ([Status] IN (N'Started', N'Succeeded', N'Failed', N'Abandoned')),
        CONSTRAINT [CK_ImportBatches_Counts]
            CHECK ([ReadCount] >= 0
                AND [ImportedCount] >= 0
                AND [SkippedCount] >= 0
                AND [ErrorCount] >= 0)
    );

    CREATE INDEX [IX_ImportBatches_OwnerStarted]
        ON [tb_ops].[ImportBatches]([OwnerWindowsSid], [StartedAtUtc] DESC);
    CREATE INDEX [IX_ImportBatches_SourceStarted]
        ON [tb_ops].[ImportBatches]([SourceSystem], [StartedAtUtc] DESC);

    CREATE TABLE [tb_ops].[LegacyIdMappings]
    (
        [ImportBatchId] uniqueidentifier NOT NULL,
        [EntityType] nvarchar(80) NOT NULL,
        [LegacyId] nvarchar(240) NOT NULL,
        [NewEntityId] bigint NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_LegacyIdMappings_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_LegacyIdMappings]
            PRIMARY KEY CLUSTERED ([ImportBatchId], [EntityType], [LegacyId]),
        CONSTRAINT [FK_LegacyIdMappings_Batch]
            FOREIGN KEY ([ImportBatchId])
            REFERENCES [tb_ops].[ImportBatches]([Id])
            ON DELETE CASCADE,
        CONSTRAINT [CK_LegacyIdMappings_EntityType]
            CHECK (LEN(LTRIM(RTRIM([EntityType]))) > 0),
        CONSTRAINT [CK_LegacyIdMappings_LegacyId]
            CHECK (LEN(LTRIM(RTRIM([LegacyId]))) > 0)
    );

    CREATE INDEX [IX_LegacyIdMappings_NewEntity]
        ON [tb_ops].[LegacyIdMappings]([EntityType], [NewEntityId]);

    INSERT INTO [tb_deploy].[SchemaMigrations]
    (
        [MigrationId],
        [SchemaVersion],
        [ReleaseVersion],
        [ScriptChecksum]
    )
    VALUES
    (
        N'SqlServer2016.OperationalStorage.0002',
        2,
        N'2.0.0-alpha.2',
        NULL
    );

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'SqlServer2016.OperationalStorage.0002 installed.';
GO

-- ============================================================================
-- END 21-V0002-OperationalSchema.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 22-V0003-SharedReferenceData.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    V0003 promotes shared reference data to an organization-wide boundary.
    It is intentionally additive so an installed V0002 database upgrades in place.
*/

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.OperationalStorage.0002'
      AND [SchemaVersion] = 2
)
BEGIN
    RAISERROR(
        N'The TechBench V0002 operational schema must be installed before SharedReferenceData.0003.',
        16,
        1);
    RETURN;
END;

IF EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.SharedReferenceData.0003'
      AND [SchemaVersion] = 3
)
BEGIN
    PRINT N'SqlServer2016.SharedReferenceData.0003 is already installed.';
    RETURN;
END;

IF OBJECT_ID(N'tb_data.OrganizationTags', N'U') IS NOT NULL
BEGIN
    RAISERROR(
        N'OrganizationTags exists without the V0003 migration marker. Stop and investigate the partial deployment.',
        16,
        1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    CREATE TABLE [tb_data].[OrganizationTags]
    (
        [Id] int IDENTITY(1,1) NOT NULL,
        [Tag] nvarchar(1000) NOT NULL,
        [TagHash] binary(32) NOT NULL,
        [CreatedByWindowsSid] varbinary(85) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_OrganizationTags_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_OrganizationTags] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_OrganizationTags_CreatedBy]
            FOREIGN KEY ([CreatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_OrganizationTags_Canonical]
            CHECK
            (
                LEN([Tag]) > 0
                AND [Tag] = LTRIM(RTRIM([Tag]))
                AND DATALENGTH([Tag]) <= 2000
            )
    );

    CREATE UNIQUE INDEX [UX_OrganizationTags_TagHash]
        ON [tb_data].[OrganizationTags]([TagHash]);

    ;WITH parsed_tags AS
    (
        SELECT
            LTRIM(RTRIM(tag.[value])) AS [Tag],
            CONVERT(
                binary(32),
                HASHBYTES
                (
                    N'SHA2_256',
                    CONVERT(
                        varbinary(2000),
                        UPPER(LTRIM(RTRIM(tag.[value])))
                    )
                )
            ) AS [TagHash],
            work_entry.[CreatedByWindowsSid],
            work_entry.[Id] AS [WorkEntryId]
        FROM [tb_data].[WorkEntries] AS work_entry
        CROSS APPLY STRING_SPLIT(COALESCE(work_entry.[Tags], N''), N',') AS tag
        WHERE NULLIF(LTRIM(RTRIM(tag.[value])), N'') IS NOT NULL
    ),
    ranked_tags AS
    (
        SELECT
            [Tag],
            [TagHash],
            [CreatedByWindowsSid],
            ROW_NUMBER() OVER
            (
                PARTITION BY [TagHash]
                ORDER BY [WorkEntryId], [Tag]
            ) AS [TagRank]
        FROM parsed_tags
    )
    INSERT INTO [tb_data].[OrganizationTags]
    (
        [Tag],
        [TagHash],
        [CreatedByWindowsSid]
    )
    SELECT
        [Tag],
        [TagHash],
        [CreatedByWindowsSid]
    FROM ranked_tags
    WHERE [TagRank] = 1;

    /*
        Common Links are one organization catalog. Existing organization rows win;
        otherwise the newest row wins, with built-ins preferred. Identical URLs are
        already interchangeable under the catalog's URL uniqueness contract.
    */
    ;WITH ranked_links AS
    (
        SELECT
            [Id],
            ROW_NUMBER() OVER
            (
                PARTITION BY [UrlHash]
                ORDER BY
                    CASE WHEN [ScopeType] = N'Organization' THEN 0 ELSE 1 END,
                    CASE WHEN [BuiltInKey] IS NOT NULL THEN 0 ELSE 1 END,
                    [UpdatedAtUtc] DESC,
                    [Id] DESC
            ) AS [LinkRank]
        FROM [tb_data].[CommonLinks]
    )
    DELETE common_link
    FROM [tb_data].[CommonLinks] AS common_link
    INNER JOIN ranked_links AS ranked_link
        ON ranked_link.[Id] = common_link.[Id]
    WHERE ranked_link.[LinkRank] > 1;

    UPDATE [tb_data].[CommonLinks]
    SET
        [ScopeType] = N'Organization',
        [OwnerWindowsSid] = NULL
    WHERE [ScopeType] <> N'Organization'
       OR [OwnerWindowsSid] IS NOT NULL;

    DECLARE @CommonLinkDefault sysname;
    SELECT @CommonLinkDefault = default_constraint.[name]
    FROM sys.default_constraints AS default_constraint
    INNER JOIN sys.columns AS column_definition
        ON column_definition.[object_id] = default_constraint.[parent_object_id]
       AND column_definition.[column_id] = default_constraint.[parent_column_id]
    WHERE default_constraint.[parent_object_id] = OBJECT_ID(N'tb_data.CommonLinks')
      AND column_definition.[name] = N'ScopeType';

    IF @CommonLinkDefault IS NOT NULL
    BEGIN
        DECLARE @DropCommonLinkDefaultSql nvarchar(max) =
            N'ALTER TABLE [tb_data].[CommonLinks] DROP CONSTRAINT '
            + QUOTENAME(@CommonLinkDefault)
            + N';';
        EXEC (@DropCommonLinkDefaultSql);
    END;

    ALTER TABLE [tb_data].[CommonLinks]
        ADD CONSTRAINT [DF_CommonLinks_ScopeType]
        DEFAULT (N'Organization') FOR [ScopeType];

    /*
        Client aliases are shared matching knowledge. Preserve an existing
        organization mapping when present; otherwise choose the most recently
        updated mapping deterministically before promoting it.
    */
    ;WITH ranked_aliases AS
    (
        SELECT
            [Id],
            ROW_NUMBER() OVER
            (
                PARTITION BY [Alias]
                ORDER BY
                    CASE WHEN [ScopeType] = N'Organization' THEN 0 ELSE 1 END,
                    [UpdatedAtUtc] DESC,
                    [Id] DESC
            ) AS [AliasRank]
        FROM [tb_data].[ClientAliases]
    )
    DELETE client_alias
    FROM [tb_data].[ClientAliases] AS client_alias
    INNER JOIN ranked_aliases AS ranked_alias
        ON ranked_alias.[Id] = client_alias.[Id]
    WHERE ranked_alias.[AliasRank] > 1;

    UPDATE [tb_data].[ClientAliases]
    SET
        [ScopeType] = N'Organization',
        [OwnerWindowsSid] = NULL
    WHERE [ScopeType] <> N'Organization'
       OR [OwnerWindowsSid] IS NOT NULL;

    DECLARE @ClientAliasDefault sysname;
    SELECT @ClientAliasDefault = default_constraint.[name]
    FROM sys.default_constraints AS default_constraint
    INNER JOIN sys.columns AS column_definition
        ON column_definition.[object_id] = default_constraint.[parent_object_id]
       AND column_definition.[column_id] = default_constraint.[parent_column_id]
    WHERE default_constraint.[parent_object_id] = OBJECT_ID(N'tb_data.ClientAliases')
      AND column_definition.[name] = N'ScopeType';

    IF @ClientAliasDefault IS NOT NULL
    BEGIN
        DECLARE @DropClientAliasDefaultSql nvarchar(max) =
            N'ALTER TABLE [tb_data].[ClientAliases] DROP CONSTRAINT '
            + QUOTENAME(@ClientAliasDefault)
            + N';';
        EXEC (@DropClientAliasDefaultSql);
    END;

    ALTER TABLE [tb_data].[ClientAliases]
        ADD CONSTRAINT [DF_ClientAliases_ScopeType]
        DEFAULT (N'Organization') FOR [ScopeType];

    /*
        Promote a personal template only when its name/category has no competing
        text. Exact duplicates are collapsed deterministically. Conflicting
        personal variants remain personal so the migration never discards or
        guesses between different note content.
    */
    DELETE personal_template
    FROM [tb_data].[Templates] AS personal_template
    WHERE personal_template.[ScopeType] = N'User'
      AND EXISTS
      (
          SELECT 1
          FROM [tb_data].[Templates] AS organization_template
          WHERE organization_template.[ScopeType] = N'Organization'
            AND organization_template.[Name] = personal_template.[Name]
            AND organization_template.[Category] = personal_template.[Category]
            AND organization_template.[TemplateText] = personal_template.[TemplateText]
      );

    UPDATE candidate
    SET
        [ScopeType] = N'Organization',
        [OwnerWindowsSid] = NULL
    FROM [tb_data].[Templates] AS candidate
    WHERE candidate.[ScopeType] = N'User'
      AND NOT EXISTS
      (
          SELECT 1
          FROM [tb_data].[Templates] AS conflict
          WHERE conflict.[Name] = candidate.[Name]
            AND conflict.[Category] = candidate.[Category]
            AND conflict.[TemplateText] <> candidate.[TemplateText]
      )
      AND NOT EXISTS
      (
          SELECT 1
          FROM [tb_data].[Templates] AS earlier_duplicate
          WHERE earlier_duplicate.[ScopeType] = N'User'
            AND earlier_duplicate.[Name] = candidate.[Name]
            AND earlier_duplicate.[Category] = candidate.[Category]
            AND earlier_duplicate.[TemplateText] = candidate.[TemplateText]
            AND earlier_duplicate.[Id] < candidate.[Id]
      );

    DELETE personal_template
    FROM [tb_data].[Templates] AS personal_template
    WHERE personal_template.[ScopeType] = N'User'
      AND EXISTS
      (
          SELECT 1
          FROM [tb_data].[Templates] AS organization_template
          WHERE organization_template.[ScopeType] = N'Organization'
            AND organization_template.[Name] = personal_template.[Name]
            AND organization_template.[Category] = personal_template.[Category]
            AND organization_template.[TemplateText] = personal_template.[TemplateText]
      );

    /* Promote shared connection/configuration keys; retain identity settings. */
    DECLARE @SharedSettingKeys TABLE
    (
        [SettingKey] nvarchar(200) NOT NULL PRIMARY KEY
    );

    INSERT INTO @SharedSettingKeys([SettingKey])
    VALUES
        (N'Whd.BaseUrl'),
        (N'Whd.AuthenticationMode'),
        (N'Sage.ActivityItemId');

    INSERT INTO [tb_data].[OrganizationSettings]
    (
        [SettingKey],
        [SettingValue],
        [UpdatedByWindowsSid],
        [UpdatedAtUtc]
    )
    SELECT
        shared_key.[SettingKey],
        latest_setting.[SettingValue],
        latest_setting.[OwnerWindowsSid],
        latest_setting.[UpdatedAtUtc]
    FROM @SharedSettingKeys AS shared_key
    CROSS APPLY
    (
        SELECT TOP (1)
            user_setting.[SettingValue],
            user_setting.[OwnerWindowsSid],
            user_setting.[UpdatedAtUtc]
        FROM [tb_user].[UserSettings] AS user_setting
        WHERE user_setting.[SettingKey] = shared_key.[SettingKey]
        ORDER BY
            user_setting.[UpdatedAtUtc] DESC,
            user_setting.[OwnerWindowsSid] DESC
    ) AS latest_setting
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM [tb_data].[OrganizationSettings] AS organization_setting
        WHERE organization_setting.[SettingKey] = shared_key.[SettingKey]
    );

    DELETE user_setting
    FROM [tb_user].[UserSettings] AS user_setting
    INNER JOIN @SharedSettingKeys AS shared_key
        ON shared_key.[SettingKey] = user_setting.[SettingKey];

    INSERT INTO [tb_deploy].[SchemaMigrations]
    (
        [MigrationId],
        [SchemaVersion],
        [ReleaseVersion],
        [ScriptChecksum]
    )
    VALUES
    (
        N'SqlServer2016.SharedReferenceData.0003',
        3,
        N'2.0.0-alpha.3',
        NULL
    );

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'SqlServer2016.SharedReferenceData.0003 installed.';
GO

-- ============================================================================
-- END 22-V0003-SharedReferenceData.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 23-V0004-AdminOwnedSharedConfig.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    V0004 makes every organization-wide setting and reference catalog an
    Admin-owned boundary. It is additive and upgrades an installed V0003
    database in place without changing or deleting shared templates or links.
*/

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.SharedReferenceData.0003'
      AND [SchemaVersion] = 3
)
BEGIN
    RAISERROR(
        N'The TechBench V0003 shared-reference schema must be installed before AdminOwnedSharedConfig.0004.',
        16,
        1);
    RETURN;
END;

IF EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.AdminOwnedSharedConfig.0004'
      AND [SchemaVersion] = 4
)
BEGIN
    PRINT N'SqlServer2016.AdminOwnedSharedConfig.0004 is already installed.';
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    /*
        Only per-user external identities remain saveable. Legacy secrets are
        retained temporarily so the desktop migration can delete them after
        transferring them to Windows Credential Manager; they cannot be saved
        again under V0004.
    */
    DELETE FROM [tb_user].[UserSettings]
    WHERE [SettingKey] NOT IN
    (
        N'Whd.Username',
        N'Sage.Username',
        N'Sage.EmployeeId',
        N'Whd.ApiToken',
        N'Sage.Password',
        N'Sage.DefaultCustomerId'
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
        N'SqlServer2016.AdminOwnedSharedConfig.0004',
        4,
        N'2.0.0-alpha.4',
        NULL
    );

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'SqlServer2016.AdminOwnedSharedConfig.0004 installed.';
GO

-- ============================================================================
-- END 23-V0004-AdminOwnedSharedConfig.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 24-V0005-TechBenchV1ImportSchema.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    V0005 adds the durable identity boundary required for each authenticated
    user to import one TechBench V1 SQLite history without duplicating a
    partial or repeated import. Shared catalogs are deliberately untouched.
*/

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.AdminOwnedSharedConfig.0004'
      AND [SchemaVersion] = 4
)
BEGIN
    RAISERROR(
        N'The TechBench V0004 Admin-owned schema must be installed before TechBenchV1Import.0005.',
        16,
        1);
    RETURN;
END;

IF EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.TechBenchV1Import.0005'
      AND [SchemaVersion] = 5
)
BEGIN
    /*
        Alpha V0005 was deployed internally before reverse link mappings were
        allowed to converge. Repair that index shape on repeat deployments so
        an installed database receives the same contract as a clean install.
    */
    IF OBJECT_ID(N'tb_ops.LegacyEntityMappings', N'U') IS NULL
    BEGIN
        RAISERROR(
            N'The V0005 migration marker exists but tb_ops.LegacyEntityMappings is missing.',
            16,
            1);
        RETURN;
    END;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF EXISTS
        (
            SELECT 1
            FROM sys.indexes
            WHERE [object_id] = OBJECT_ID(N'tb_ops.LegacyEntityMappings')
              AND [name] = N'UX_LegacyEntityMappings_NewEntity'
        )
            DROP INDEX [UX_LegacyEntityMappings_NewEntity]
                ON [tb_ops].[LegacyEntityMappings];

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes
            WHERE [object_id] = OBJECT_ID(N'tb_ops.LegacyEntityMappings')
              AND [name] = N'IX_LegacyEntityMappings_NewEntity'
        )
            CREATE INDEX [IX_LegacyEntityMappings_NewEntity]
                ON [tb_ops].[LegacyEntityMappings]
                (
                    [OwnerWindowsSid],
                    [SourceSystem],
                    [EntityType],
                    [NewEntityId]
                );

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes
            WHERE [object_id] = OBJECT_ID(N'tb_ops.LegacyEntityMappings')
              AND [name] = N'UX_LegacyEntityMappings_WorkEntryNewEntity'
        )
            CREATE UNIQUE INDEX [UX_LegacyEntityMappings_WorkEntryNewEntity]
                ON [tb_ops].[LegacyEntityMappings]
                (
                    [OwnerWindowsSid],
                    [SourceSystem],
                    [NewEntityId]
                )
                WHERE [EntityType] = N'WorkEntry';

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes
            WHERE [object_id] = OBJECT_ID(N'tb_ops.LegacyEntityMappings')
              AND [name] = N'UX_LegacyEntityMappings_PostingLogNewEntity'
        )
            CREATE UNIQUE INDEX [UX_LegacyEntityMappings_PostingLogNewEntity]
                ON [tb_ops].[LegacyEntityMappings]
                (
                    [OwnerWindowsSid],
                    [SourceSystem],
                    [NewEntityId]
                )
                WHERE [EntityType] = N'PostingLog';

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    PRINT N'SqlServer2016.TechBenchV1Import.0005 is already installed; its reverse mapping indexes are current.';
    RETURN;
END;

IF OBJECT_ID(N'tb_ops.LegacyEntityMappings', N'U') IS NOT NULL
   OR COL_LENGTH(N'tb_ops.ImportBatches', N'ConflictCount') IS NOT NULL
   OR EXISTS
   (
       SELECT 1
       FROM sys.indexes
       WHERE [object_id] = OBJECT_ID(N'tb_ops.ImportBatches')
         AND [name] IN
         (
             N'IX_ImportBatches_OwnerSourceFileHash',
             N'UX_ImportBatches_ActiveTechBenchV1'
         )
   )
BEGIN
    RAISERROR(
        N'V0005 import objects already exist without their migration marker. Resolve the partial deployment before continuing.',
        16,
        1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    ALTER TABLE [tb_ops].[ImportBatches]
        ADD [ConflictCount] int NOT NULL
            CONSTRAINT [DF_ImportBatches_ConflictCount] DEFAULT (0);

    ALTER TABLE [tb_ops].[ImportBatches]
        DROP CONSTRAINT [CK_ImportBatches_Counts];

    /*
        SQL Server binds column references while compiling the batch. Compile
        every statement that consumes the newly added column only after the
        ALTER TABLE above has updated the catalog. The dynamic batch remains
        inside this transaction and is rolled back by the surrounding CATCH.
    */
    DECLARE @ConflictCountDependentSql nvarchar(max) = N'
        ALTER TABLE [tb_ops].[ImportBatches]
            ADD CONSTRAINT [CK_ImportBatches_Counts]
                CHECK
                (
                    [ReadCount] >= 0
                    AND [ImportedCount] >= 0
                    AND [SkippedCount] >= 0
                    AND [ConflictCount] >= 0
                    AND [ErrorCount] >= 0
                );

        CREATE INDEX [IX_ImportBatches_OwnerSourceFileHash]
            ON [tb_ops].[ImportBatches]
            (
                [OwnerWindowsSid],
                [SourceSystem],
                [FileHash],
                [Status]
            )
            INCLUDE
            (
                [ImportedCount],
                [SkippedCount],
                [ConflictCount],
                [ErrorCount],
                [CompletedAtUtc]
            )
            WHERE [FileHash] IS NOT NULL;';
    EXEC sys.sp_executesql @ConflictCountDependentSql;

    CREATE UNIQUE INDEX [UX_ImportBatches_ActiveTechBenchV1]
        ON [tb_ops].[ImportBatches]([OwnerWindowsSid])
        WHERE [SourceSystem] = N'TechBenchV1'
          AND [Status] = N'Started';

    CREATE TABLE [tb_ops].[LegacyEntityMappings]
    (
        [OwnerWindowsSid] varbinary(85) NOT NULL,
        [SourceSystem] nvarchar(80) NOT NULL,
        [EntityType] nvarchar(80) NOT NULL,
        [LegacyId] bigint NOT NULL,
        [NewEntityId] bigint NOT NULL,
        [ContentHash] char(64) NOT NULL,
        [FirstImportBatchId] uniqueidentifier NOT NULL,
        [LastSeenImportBatchId] uniqueidentifier NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_LegacyEntityMappings_CreatedAtUtc]
                DEFAULT (SYSUTCDATETIME()),
        [LastSeenAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_LegacyEntityMappings_LastSeenAtUtc]
                DEFAULT (SYSUTCDATETIME()),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_LegacyEntityMappings]
            PRIMARY KEY CLUSTERED
            (
                [OwnerWindowsSid],
                [SourceSystem],
                [EntityType],
                [LegacyId]
            ),
        CONSTRAINT [FK_LegacyEntityMappings_Owner]
            FOREIGN KEY ([OwnerWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [FK_LegacyEntityMappings_FirstBatch]
            FOREIGN KEY ([FirstImportBatchId])
            REFERENCES [tb_ops].[ImportBatches]([Id]),
        CONSTRAINT [FK_LegacyEntityMappings_LastSeenBatch]
            FOREIGN KEY ([LastSeenImportBatchId])
            REFERENCES [tb_ops].[ImportBatches]([Id]),
        CONSTRAINT [CK_LegacyEntityMappings_Source]
            CHECK ([SourceSystem] = N'TechBenchV1'),
        CONSTRAINT [CK_LegacyEntityMappings_EntityType]
            CHECK
            (
                [EntityType] IN
                (
                    N'WorkEntry',
                    N'WorkEntryLink',
                    N'PostingLog'
                )
            ),
        CONSTRAINT [CK_LegacyEntityMappings_LegacyId]
            CHECK ([LegacyId] > 0),
        CONSTRAINT [CK_LegacyEntityMappings_NewEntityId]
            CHECK ([NewEntityId] > 0),
        CONSTRAINT [CK_LegacyEntityMappings_ContentHash]
            CHECK
            (
                LEN([ContentHash]) = 64
                AND [ContentHash] COLLATE Latin1_General_100_BIN2
                    NOT LIKE '%[^0-9A-F]%'
            )
    );

    /*
        More than one legacy link row may describe the same equivalent SQL
        relationship. Keep a fast reverse lookup without forcing those legacy
        IDs to manufacture duplicate WorkEntryLinks.
    */
    CREATE INDEX [IX_LegacyEntityMappings_NewEntity]
        ON [tb_ops].[LegacyEntityMappings]
        (
            [OwnerWindowsSid],
            [SourceSystem],
            [EntityType],
            [NewEntityId]
        );

    CREATE UNIQUE INDEX [UX_LegacyEntityMappings_WorkEntryNewEntity]
        ON [tb_ops].[LegacyEntityMappings]
        (
            [OwnerWindowsSid],
            [SourceSystem],
            [NewEntityId]
        )
        WHERE [EntityType] = N'WorkEntry';

    CREATE UNIQUE INDEX [UX_LegacyEntityMappings_PostingLogNewEntity]
        ON [tb_ops].[LegacyEntityMappings]
        (
            [OwnerWindowsSid],
            [SourceSystem],
            [NewEntityId]
        )
        WHERE [EntityType] = N'PostingLog';

    CREATE INDEX [IX_LegacyEntityMappings_LastSeenBatch]
        ON [tb_ops].[LegacyEntityMappings]([LastSeenImportBatchId]);

    INSERT INTO [tb_deploy].[SchemaMigrations]
    (
        [MigrationId],
        [SchemaVersion],
        [ReleaseVersion],
        [ScriptChecksum]
    )
    VALUES
    (
        N'SqlServer2016.TechBenchV1Import.0005',
        5,
        N'2.0.0-alpha.5',
        NULL
    );

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'SqlServer2016.TechBenchV1Import.0005 installed.';
GO

-- ============================================================================
-- END 24-V0005-TechBenchV1ImportSchema.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 25-V0006-WhdServerSyncSchema.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.TechBenchV1Import.0005'
      AND [SchemaVersion] = 5
)
BEGIN
    RAISERROR(N'V0005 must be installed before WHDServerSync.0006.', 16, 1);
    RETURN;
END;

IF SCHEMA_ID(N'tb_whd') IS NULL
    EXEC(N'CREATE SCHEMA [tb_whd] AUTHORIZATION [dbo];');
IF SCHEMA_ID(N'tb_sync') IS NULL
    EXEC(N'CREATE SCHEMA [tb_sync] AUTHORIZATION [dbo];');
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'tb_data.Tickets', N'WhdLastUpdatedUtc') IS NULL
        ALTER TABLE [tb_data].[Tickets] ADD [WhdLastUpdatedUtc] datetime2(3) NULL;
    IF COL_LENGTH(N'tb_data.Tickets', N'IsWhdDeleted') IS NULL
        ALTER TABLE [tb_data].[Tickets] ADD [IsWhdDeleted] bit NOT NULL
            CONSTRAINT [DF_Tickets_IsWhdDeleted] DEFAULT (0);
    IF COL_LENGTH(N'tb_data.Tickets', N'AssignedTechExternalId') IS NULL
        ALTER TABLE [tb_data].[Tickets] ADD [AssignedTechExternalId] nvarchar(120) NULL;
    IF COL_LENGTH(N'tb_data.Tickets', N'AssignedTechName') IS NULL
        ALTER TABLE [tb_data].[Tickets] ADD [AssignedTechName] nvarchar(240) NULL;
    IF COL_LENGTH(N'tb_data.Tickets', N'AssignedGroupExternalId') IS NULL
        ALTER TABLE [tb_data].[Tickets] ADD [AssignedGroupExternalId] nvarchar(120) NULL;
    IF COL_LENGTH(N'tb_data.Tickets', N'AssignedGroupName') IS NULL
        ALTER TABLE [tb_data].[Tickets] ADD [AssignedGroupName] nvarchar(240) NULL;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_data.Tickets')
          AND [name] = N'IX_Tickets_WhdAssignedTech'
    )
        EXEC sys.sp_executesql N'
            CREATE INDEX [IX_Tickets_WhdAssignedTech]
                ON [tb_data].[Tickets]([Source], [AssignedTechExternalId], [IsClosed])
                INCLUDE ([AssignedGroupExternalId], [IsWhdDeleted]);';

    IF OBJECT_ID(N'tb_whd.Technicians', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_whd].[Technicians]
        (
            [ExternalId] nvarchar(120) NOT NULL,
            [DisplayName] nvarchar(240) NOT NULL,
            [Username] nvarchar(240) NULL,
            [Email] nvarchar(320) NULL,
            [IsActive] bit NOT NULL CONSTRAINT [DF_WhdTechnicians_IsActive] DEFAULT (1),
            [WhdLastUpdatedUtc] datetime2(3) NULL,
            [LastSyncedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_WhdTechnicians_LastSynced] DEFAULT (SYSUTCDATETIME()),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_WhdTechnicians] PRIMARY KEY CLUSTERED ([ExternalId])
        );
    END;

    IF COL_LENGTH(N'tb_whd.Technicians', N'Username') IS NULL
        ALTER TABLE [tb_whd].[Technicians] ADD [Username] nvarchar(240) NULL;

    IF OBJECT_ID(N'tb_whd.TechnicianGroups', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_whd].[TechnicianGroups]
        (
            [ExternalId] nvarchar(120) NOT NULL,
            [DisplayName] nvarchar(240) NOT NULL,
            [IsActive] bit NOT NULL CONSTRAINT [DF_WhdGroups_IsActive] DEFAULT (1),
            [WhdLastUpdatedUtc] datetime2(3) NULL,
            [LastSyncedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_WhdGroups_LastSynced] DEFAULT (SYSUTCDATETIME()),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_WhdTechnicianGroups] PRIMARY KEY CLUSTERED ([ExternalId])
        );
    END;

    IF OBJECT_ID(N'tb_whd.TechnicianGroupMemberships', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_whd].[TechnicianGroupMemberships]
        (
            [TechnicianExternalId] nvarchar(120) NOT NULL,
            [GroupExternalId] nvarchar(120) NOT NULL,
            [LastSyncedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_WhdMemberships_LastSynced] DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT [PK_WhdTechnicianGroupMemberships]
                PRIMARY KEY CLUSTERED ([TechnicianExternalId], [GroupExternalId]),
            CONSTRAINT [FK_WhdMemberships_Technician]
                FOREIGN KEY ([TechnicianExternalId])
                REFERENCES [tb_whd].[Technicians]([ExternalId]),
            CONSTRAINT [FK_WhdMemberships_Group]
                FOREIGN KEY ([GroupExternalId])
                REFERENCES [tb_whd].[TechnicianGroups]([ExternalId])
        );
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_whd.TechnicianGroupMemberships')
          AND [name] = N'IX_WhdMemberships_Group'
    )
        CREATE INDEX [IX_WhdMemberships_Group]
            ON [tb_whd].[TechnicianGroupMemberships]([GroupExternalId], [TechnicianExternalId]);

    IF OBJECT_ID(N'tb_whd.UserTechnicianMappings', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_whd].[UserTechnicianMappings]
        (
            [Id] int IDENTITY(1,1) NOT NULL,
            [WindowsSid] varbinary(85) NOT NULL,
            [TechnicianExternalId] nvarchar(120) NOT NULL,
            [UpdatedByWindowsSid] varbinary(85) NOT NULL,
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_WhdUserMappings_Updated] DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT [PK_WhdUserTechnicianMappings] PRIMARY KEY CLUSTERED ([WindowsSid]),
            CONSTRAINT [UQ_WhdUserTechnicianMappings_Id] UNIQUE ([Id]),
            CONSTRAINT [FK_WhdUserMappings_User]
                FOREIGN KEY ([WindowsSid]) REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_WhdUserMappings_Technician]
                FOREIGN KEY ([TechnicianExternalId]) REFERENCES [tb_whd].[Technicians]([ExternalId]),
            CONSTRAINT [FK_WhdUserMappings_UpdatedBy]
                FOREIGN KEY ([UpdatedByWindowsSid]) REFERENCES [tb_security].[Users]([WindowsSid])
        );
    END;

    IF OBJECT_ID(N'tb_sync.WhdSyncRequests', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_sync].[WhdSyncRequests]
        (
            [RequestId] uniqueidentifier NOT NULL,
            [RequestedByWindowsSid] varbinary(85) NOT NULL,
            [RequestedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_WhdRequests_Requested] DEFAULT (SYSUTCDATETIME()),
            [RequestType] nvarchar(40) NOT NULL,
            [Status] nvarchar(30) NOT NULL
                CONSTRAINT [DF_WhdRequests_Status] DEFAULT (N'Queued'),
            [CompletedAtUtc] datetime2(3) NULL,
            [Message] nvarchar(1000) NULL,
            CONSTRAINT [PK_WhdSyncRequests] PRIMARY KEY CLUSTERED ([RequestId]),
            CONSTRAINT [FK_WhdRequests_Requester]
                FOREIGN KEY ([RequestedByWindowsSid]) REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_WhdRequests_Type]
                CHECK ([RequestType] IN (N'Full', N'Incremental')),
            CONSTRAINT [CK_WhdRequests_Status]
                CHECK ([Status] IN (N'Queued', N'Running', N'Completed', N'Failed'))
        );
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_sync.WhdSyncRequests')
          AND [name] = N'IX_WhdSyncRequests_StatusRequested'
    )
        CREATE INDEX [IX_WhdSyncRequests_StatusRequested]
            ON [tb_sync].[WhdSyncRequests]([Status], [RequestedAtUtc] DESC)
            INCLUDE ([RequestType], [CompletedAtUtc]);

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_sync.WhdSyncRequests')
          AND [name] = N'IX_WhdSyncRequests_RequestedAt'
    )
        CREATE INDEX [IX_WhdSyncRequests_RequestedAt]
            ON [tb_sync].[WhdSyncRequests]([RequestedAtUtc] DESC, [RequestId])
            INCLUDE ([Status], [RequestType], [CompletedAtUtc]);

    IF OBJECT_ID(N'tb_sync.WhdSyncWork', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_sync].[WhdSyncWork]
        (
            [WorkId] uniqueidentifier NOT NULL,
            [RequestId] uniqueidentifier NOT NULL,
            [WorkType] nvarchar(40) NOT NULL,
            [State] nvarchar(30) NOT NULL CONSTRAINT [DF_WhdWork_State] DEFAULT (N'Queued'),
            [PayloadJson] nvarchar(max) NULL,
            [CreatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_WhdWork_Created] DEFAULT (SYSUTCDATETIME()),
            [CompletedAtUtc] datetime2(3) NULL,
            [ErrorMessage] nvarchar(2000) NULL,
            CONSTRAINT [PK_WhdSyncWork] PRIMARY KEY CLUSTERED ([WorkId]),
            CONSTRAINT [FK_WhdWork_Request]
                FOREIGN KEY ([RequestId]) REFERENCES [tb_sync].[WhdSyncRequests]([RequestId]),
            CONSTRAINT [CK_WhdWork_Type]
                CHECK ([WorkType] IN (N'Clients', N'Tickets', N'Statuses', N'Technicians', N'Groups')),
            CONSTRAINT [CK_WhdWork_State]
                CHECK ([State] IN (N'Queued', N'Leased', N'Completed', N'Failed')),
            CONSTRAINT [CK_WhdWork_Payload]
                CHECK ([PayloadJson] IS NULL OR ISJSON([PayloadJson]) = 1)
        );
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_sync.WhdSyncWork')
          AND [name] = N'IX_WhdSyncWork_Claim'
    )
        CREATE INDEX [IX_WhdSyncWork_Claim]
            ON [tb_sync].[WhdSyncWork]([State], [CreatedAtUtc])
            INCLUDE ([RequestId], [WorkType]);

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_sync.WhdSyncWork')
          AND [name] = N'IX_WhdSyncWork_RequestState'
    )
        CREATE INDEX [IX_WhdSyncWork_RequestState]
            ON [tb_sync].[WhdSyncWork]([RequestId], [State])
            INCLUDE ([WorkType], [CompletedAtUtc]);

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_sync.WhdSyncWork')
          AND [name] = N'IX_WhdSyncWork_ReferenceHistory'
    )
        CREATE INDEX [IX_WhdSyncWork_ReferenceHistory]
            ON [tb_sync].[WhdSyncWork]([WorkType], [State], [CompletedAtUtc]);

    IF OBJECT_ID(N'tb_sync.WhdSyncLeases', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_sync].[WhdSyncLeases]
        (
            [WorkId] uniqueidentifier NOT NULL,
            [LeaseId] uniqueidentifier NOT NULL,
            [WorkerId] uniqueidentifier NOT NULL,
            [AcquiredAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_WhdLeases_Acquired] DEFAULT (SYSUTCDATETIME()),
            [ExpiresAtUtc] datetime2(3) NOT NULL,
            CONSTRAINT [PK_WhdSyncLeases] PRIMARY KEY CLUSTERED ([WorkId]),
            CONSTRAINT [UQ_WhdSyncLeases_Lease] UNIQUE ([LeaseId]),
            CONSTRAINT [FK_WhdLeases_Work]
                FOREIGN KEY ([WorkId]) REFERENCES [tb_sync].[WhdSyncWork]([WorkId]),
            CONSTRAINT [CK_WhdLeases_Expiry] CHECK ([ExpiresAtUtc] > [AcquiredAtUtc])
        );
    END;

    IF OBJECT_ID(N'tb_sync.WhdSyncCursors', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_sync].[WhdSyncCursors]
        (
            [CursorName] nvarchar(80) NOT NULL,
            [CursorValue] nvarchar(400) NULL,
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_WhdCursors_Updated] DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT [PK_WhdSyncCursors] PRIMARY KEY CLUSTERED ([CursorName])
        );
    END;

    IF OBJECT_ID(N'tb_sync.WhdSyncHealth', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_sync].[WhdSyncHealth]
        (
            [HealthId] tinyint NOT NULL
                CONSTRAINT [PK_WhdSyncHealth] PRIMARY KEY
                CONSTRAINT [CK_WhdSyncHealth_OneRow] CHECK ([HealthId] = 1),
            [LastSuccessfulAtUtc] datetime2(3) NULL,
            [LastAttemptAtUtc] datetime2(3) NULL,
            [LastError] nvarchar(2000) NULL,
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_WhdHealth_Updated] DEFAULT (SYSUTCDATETIME())
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM [tb_sync].[WhdSyncHealth] WHERE [HealthId] = 1)
        INSERT INTO [tb_sync].[WhdSyncHealth]([HealthId]) VALUES (1);

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_deploy].[SchemaMigrations]
        WHERE [MigrationId] = N'SqlServer2016.WhdServerSync.0006'
    )
    BEGIN
        INSERT INTO [tb_deploy].[SchemaMigrations]
            ([MigrationId], [SchemaVersion], [ReleaseVersion], [ScriptChecksum])
        VALUES
            (N'SqlServer2016.WhdServerSync.0006', 6, N'2.0.0-alpha.6', NULL);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'SqlServer2016.WhdServerSync.0006 is installed.';
GO

-- ============================================================================
-- END 25-V0006-WhdServerSyncSchema.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 26-V0007-ServerOwnedSageAndAdminPreviewSchema.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.WhdServerSync.0006'
      AND [SchemaVersion] = 6
)
BEGIN
    RAISERROR(N'V0006 must be installed before ServerOwnedSageAndAdminPreview.0007.', 16, 1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    IF DATABASE_PRINCIPAL_ID(N'tb_preview_reader') IS NULL
        CREATE USER [tb_preview_reader] WITHOUT LOGIN;

    IF OBJECT_ID(N'tb_sync.SageSyncRequests', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_sync].[SageSyncRequests]
        (
            [RequestId] uniqueidentifier NOT NULL,
            [RequestedByWindowsSid] varbinary(85) NOT NULL,
            [RequestedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_SageSyncRequests_Requested] DEFAULT (SYSUTCDATETIME()),
            [StartedAtUtc] datetime2(3) NULL,
            [CompletedAtUtc] datetime2(3) NULL,
            [Status] nvarchar(30) NOT NULL
                CONSTRAINT [DF_SageSyncRequests_Status] DEFAULT (N'Queued'),
            [AllowLargeRemoval] bit NOT NULL
                CONSTRAINT [DF_SageSyncRequests_AllowLargeRemoval] DEFAULT (0),
            [RequiresLargeRemovalConfirmation] bit NOT NULL
                CONSTRAINT [DF_SageSyncRequests_RequiresLargeRemovalConfirmation] DEFAULT (0),
            [ConfirmedRequestId] uniqueidentifier NULL,
            [ExistingCount] int NOT NULL
                CONSTRAINT [DF_SageSyncRequests_ExistingCount] DEFAULT (0),
            [ReadCount] int NOT NULL
                CONSTRAINT [DF_SageSyncRequests_ReadCount] DEFAULT (0),
            [SavedCount] int NOT NULL
                CONSTRAINT [DF_SageSyncRequests_SavedCount] DEFAULT (0),
            [StaleCount] int NOT NULL
                CONSTRAINT [DF_SageSyncRequests_StaleCount] DEFAULT (0),
            [AttemptCount] int NOT NULL
                CONSTRAINT [DF_SageSyncRequests_AttemptCount] DEFAULT (0),
            [Message] nvarchar(2000) NULL,
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_SageSyncRequests] PRIMARY KEY CLUSTERED ([RequestId]),
            CONSTRAINT [FK_SageSyncRequests_Requester]
                FOREIGN KEY ([RequestedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_SageSyncRequests_ConfirmedRequest]
                FOREIGN KEY ([ConfirmedRequestId])
                REFERENCES [tb_sync].[SageSyncRequests]([RequestId]),
            CONSTRAINT [CK_SageSyncRequests_Status]
                CHECK ([Status] IN (N'Queued', N'Running', N'Completed', N'Failed')),
            CONSTRAINT [CK_SageSyncRequests_ConfirmationBinding]
                CHECK
                (
                    ([AllowLargeRemoval] = 0 AND [ConfirmedRequestId] IS NULL)
                    OR ([AllowLargeRemoval] = 1 AND [ConfirmedRequestId] IS NOT NULL)
                ),
            CONSTRAINT [CK_SageSyncRequests_Counts]
                CHECK
                (
                    [ExistingCount] >= 0
                    AND [ReadCount] >= 0
                    AND [SavedCount] >= 0
                    AND [StaleCount] >= 0
                    AND [AttemptCount] >= 0
                ),
            CONSTRAINT [CK_SageSyncRequests_Times]
                CHECK
                (
                    ([StartedAtUtc] IS NULL OR [StartedAtUtc] >= [RequestedAtUtc])
                    AND
                    (
                        [CompletedAtUtc] IS NULL
                        OR
                        (
                            [StartedAtUtc] IS NOT NULL
                            AND [CompletedAtUtc] >= [StartedAtUtc]
                        )
                    )
                )
        );
    END;

    /* Keep the stage rerunnable across pre-release V7 database rehearsals. */
    IF COL_LENGTH(N'tb_sync.SageSyncRequests', N'AllowLargeRemoval') IS NULL
        ALTER TABLE [tb_sync].[SageSyncRequests]
            ADD [AllowLargeRemoval] bit NOT NULL
                CONSTRAINT [DF_SageSyncRequests_AllowLargeRemoval] DEFAULT (0) WITH VALUES;

    IF COL_LENGTH(N'tb_sync.SageSyncRequests', N'RequiresLargeRemovalConfirmation') IS NULL
        ALTER TABLE [tb_sync].[SageSyncRequests]
            ADD [RequiresLargeRemovalConfirmation] bit NOT NULL
                CONSTRAINT [DF_SageSyncRequests_RequiresLargeRemovalConfirmation] DEFAULT (0) WITH VALUES;

    IF COL_LENGTH(N'tb_sync.SageSyncRequests', N'ExistingCount') IS NULL
        ALTER TABLE [tb_sync].[SageSyncRequests]
            ADD [ExistingCount] int NOT NULL
                CONSTRAINT [DF_SageSyncRequests_ExistingCount] DEFAULT (0) WITH VALUES;

    IF COL_LENGTH(N'tb_sync.SageSyncRequests', N'ConfirmedRequestId') IS NULL
        ALTER TABLE [tb_sync].[SageSyncRequests]
            ADD [ConfirmedRequestId] uniqueidentifier NULL;

    IF OBJECT_ID(N'tb_sync.CK_SageSyncRequests_ConfirmationBinding', N'C') IS NULL
    BEGIN
        UPDATE [tb_sync].[SageSyncRequests]
        SET [AllowLargeRemoval] = 0
        WHERE [ConfirmedRequestId] IS NULL AND [AllowLargeRemoval] <> 0;

        ALTER TABLE [tb_sync].[SageSyncRequests] WITH CHECK
            ADD CONSTRAINT [CK_SageSyncRequests_ConfirmationBinding]
                CHECK
                (
                    ([AllowLargeRemoval] = 0 AND [ConfirmedRequestId] IS NULL)
                    OR ([AllowLargeRemoval] = 1 AND [ConfirmedRequestId] IS NOT NULL)
                );
    END;

    IF OBJECT_ID(N'tb_sync.FK_SageSyncRequests_ConfirmedRequest', N'F') IS NULL
        ALTER TABLE [tb_sync].[SageSyncRequests] WITH CHECK
            ADD CONSTRAINT [FK_SageSyncRequests_ConfirmedRequest]
                FOREIGN KEY ([ConfirmedRequestId])
                REFERENCES [tb_sync].[SageSyncRequests]([RequestId]);

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_sync.SageSyncRequests')
          AND [name] = N'IX_SageSyncRequests_StatusRequested'
    )
        CREATE INDEX [IX_SageSyncRequests_StatusRequested]
            ON [tb_sync].[SageSyncRequests]([Status], [RequestedAtUtc], [RequestId])
            INCLUDE
            (
                [StartedAtUtc], [CompletedAtUtc], [AllowLargeRemoval],
                [RequiresLargeRemovalConfirmation], [ConfirmedRequestId],
                [ExistingCount], [ReadCount],
                [SavedCount], [StaleCount], [AttemptCount]
            );

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_sync.SageSyncRequests')
          AND [name] = N'IX_SageSyncRequests_RequestedAt'
    )
        CREATE INDEX [IX_SageSyncRequests_RequestedAt]
            ON [tb_sync].[SageSyncRequests]([RequestedAtUtc] DESC, [RequestId])
            INCLUDE
            (
                [Status], [CompletedAtUtc], [AllowLargeRemoval],
                [RequiresLargeRemovalConfirmation], [ConfirmedRequestId], [ExistingCount],
                [ReadCount], [SavedCount], [StaleCount]
            );

    IF OBJECT_ID(N'tb_sync.SageSyncLeases', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_sync].[SageSyncLeases]
        (
            [RequestId] uniqueidentifier NOT NULL,
            [LeaseId] uniqueidentifier NOT NULL,
            [WorkerId] uniqueidentifier NOT NULL,
            [AcquiredAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_SageSyncLeases_Acquired] DEFAULT (SYSUTCDATETIME()),
            [ExpiresAtUtc] datetime2(3) NOT NULL,
            CONSTRAINT [PK_SageSyncLeases] PRIMARY KEY CLUSTERED ([RequestId]),
            CONSTRAINT [UQ_SageSyncLeases_LeaseId] UNIQUE ([LeaseId]),
            CONSTRAINT [FK_SageSyncLeases_Request]
                FOREIGN KEY ([RequestId])
                REFERENCES [tb_sync].[SageSyncRequests]([RequestId]),
            CONSTRAINT [CK_SageSyncLeases_Expiry]
                CHECK ([ExpiresAtUtc] > [AcquiredAtUtc])
        );
    END;

    IF OBJECT_ID(N'tb_sync.SageSyncHealth', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_sync].[SageSyncHealth]
        (
            [HealthId] tinyint NOT NULL
                CONSTRAINT [PK_SageSyncHealth] PRIMARY KEY
                CONSTRAINT [CK_SageSyncHealth_OneRow] CHECK ([HealthId] = 1),
            [LastAttemptAtUtc] datetime2(3) NULL,
            [LastSuccessfulAtUtc] datetime2(3) NULL,
            [LastError] nvarchar(2000) NULL,
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_SageSyncHealth_Updated] DEFAULT (SYSUTCDATETIME())
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM [tb_sync].[SageSyncHealth] WHERE [HealthId] = 1)
        INSERT INTO [tb_sync].[SageSyncHealth]([HealthId]) VALUES (1);

    IF OBJECT_ID(N'tb_security.AdminUserPreviewSessions', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_security].[AdminUserPreviewSessions]
        (
            [PreviewSessionId] uniqueidentifier NOT NULL,
            [ActorWindowsSid] varbinary(85) NOT NULL,
            [TargetWindowsSid] varbinary(85) NOT NULL,
            [ClientInstanceId] uniqueidentifier NOT NULL,
            [StartedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_AdminUserPreviewSessions_Started] DEFAULT (SYSUTCDATETIME()),
            [ExpiresAtUtc] datetime2(3) NOT NULL,
            [EndedAtUtc] datetime2(3) NULL,
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_AdminUserPreviewSessions]
                PRIMARY KEY CLUSTERED ([PreviewSessionId]),
            CONSTRAINT [FK_AdminUserPreviewSessions_Actor]
                FOREIGN KEY ([ActorWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_AdminUserPreviewSessions_Target]
                FOREIGN KEY ([TargetWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_AdminUserPreviewSessions_DifferentUsers]
                CHECK ([ActorWindowsSid] <> [TargetWindowsSid]),
            CONSTRAINT [CK_AdminUserPreviewSessions_Expiry]
                CHECK ([ExpiresAtUtc] > [StartedAtUtc]),
            CONSTRAINT [CK_AdminUserPreviewSessions_Ended]
                CHECK ([EndedAtUtc] IS NULL OR [EndedAtUtc] >= [StartedAtUtc])
        );
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_security.AdminUserPreviewSessions')
          AND [name] = N'IX_AdminUserPreviewSessions_ActorActive'
    )
        CREATE INDEX [IX_AdminUserPreviewSessions_ActorActive]
            ON [tb_security].[AdminUserPreviewSessions]
                ([ActorWindowsSid], [EndedAtUtc], [ExpiresAtUtc] DESC)
            INCLUDE ([TargetWindowsSid], [ClientInstanceId]);

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_security.AdminUserPreviewSessions')
          AND [name] = N'IX_AdminUserPreviewSessions_Expires'
    )
        CREATE INDEX [IX_AdminUserPreviewSessions_Expires]
            ON [tb_security].[AdminUserPreviewSessions]([ExpiresAtUtc], [PreviewSessionId])
            INCLUDE ([EndedAtUtc]);

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_deploy].[SchemaMigrations]
        WHERE [MigrationId] = N'SqlServer2016.ServerOwnedSageAndAdminPreview.0007'
    )
    BEGIN
        INSERT INTO [tb_deploy].[SchemaMigrations]
            ([MigrationId], [SchemaVersion], [ReleaseVersion], [ScriptChecksum])
        VALUES
            (N'SqlServer2016.ServerOwnedSageAndAdminPreview.0007', 7, N'2.0.0-alpha.8', NULL);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'SqlServer2016.ServerOwnedSageAndAdminPreview.0007 is installed.';
GO

-- ============================================================================
-- END 26-V0007-ServerOwnedSageAndAdminPreviewSchema.sql
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
        (N'$(AdminGroup)'),
        (N'$(SyncServicePrincipal)')
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
IF DATABASE_PRINCIPAL_ID(N'tb_role_sync_service') IS NULL
    CREATE ROLE [tb_role_sync_service] AUTHORIZATION [dbo];

DECLARE @Principal sysname;
DECLARE @DefaultSchema sysname;
DECLARE @Sql nvarchar(max);

DECLARE UserCursor CURSOR LOCAL FAST_FORWARD FOR
SELECT [PrincipalName], [DefaultSchema]
FROM
(
    VALUES
        (N'$(UserGroup)', N'tb_app'),
        (N'$(AdminGroup)', N'tb_app'),
        (N'$(SyncServicePrincipal)', N'tb_service')
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

/* Keep the service boundary exact across redeployments or principal changes. */
DECLARE ServiceBoundaryCursor CURSOR LOCAL STATIC READ_ONLY FOR
SELECT role_principal.[name], member_principal.[name]
FROM sys.database_role_members AS drm
INNER JOIN sys.database_principals AS role_principal
    ON role_principal.[principal_id] = drm.[role_principal_id]
INNER JOIN sys.database_principals AS member_principal
    ON member_principal.[principal_id] = drm.[member_principal_id]
WHERE
    (role_principal.[name] = N'tb_role_sync_service'
     AND member_principal.[name] <> N'$(SyncServicePrincipal)')
 OR (member_principal.[name] = N'$(SyncServicePrincipal)'
     AND role_principal.[name] <> N'tb_role_sync_service')
 OR (member_principal.[name] = N'tb_role_sync_service'
     AND role_principal.[name] <> N'tb_role_sync_service');

OPEN ServiceBoundaryCursor;
FETCH NEXT FROM ServiceBoundaryCursor INTO @RoleName, @MemberName;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @Sql = N'ALTER ROLE ' + QUOTENAME(@RoleName)
        + N' DROP MEMBER ' + QUOTENAME(@MemberName) + N';';
    EXEC sys.sp_executesql @Sql;

    FETCH NEXT FROM ServiceBoundaryCursor INTO @RoleName, @MemberName;
END;

CLOSE ServiceBoundaryCursor;
DEALLOCATE ServiceBoundaryCursor;

/* The Windows service identity is deliberately not an application Admin. */
IF NOT EXISTS
(
    SELECT 1
    FROM sys.database_role_members AS drm
    INNER JOIN sys.database_principals AS role_principal
        ON role_principal.principal_id = drm.role_principal_id
    INNER JOIN sys.database_principals AS member_principal
        ON member_principal.principal_id = drm.member_principal_id
    WHERE role_principal.name = N'tb_role_sync_service'
      AND member_principal.name = N'$(SyncServicePrincipal)'
)
BEGIN
    SET @Sql = N'ALTER ROLE [tb_role_sync_service] ADD MEMBER '
        + QUOTENAME(N'$(SyncServicePrincipal)') + N';';
    EXEC sys.sp_executesql @Sql;
END;

DECLARE @SyncServiceSid varbinary(85) = SUSER_SID(N'$(SyncServicePrincipal)');
IF @SyncServiceSid IS NULL
    THROW 51790, N'The WHD sync service principal could not be resolved after login creation.', 1;

/* Preserve historical FK actors if AD recreates the service principal with
   the same name but a different SID, while freeing the unique login name. */
UPDATE [tb_security].[Users]
SET [LoginName] = LEFT
    (
        N'Retired:' + CONVERT(nvarchar(170), [WindowsSid], 1) + N':' + [LoginName],
        256
    )
WHERE [LoginName] = N'$(SyncServicePrincipal)'
  AND [WindowsSid] <> @SyncServiceSid;

IF NOT EXISTS (SELECT 1 FROM [tb_security].[Users] WHERE [WindowsSid] = @SyncServiceSid)
BEGIN
    INSERT INTO [tb_security].[Users]
    (
        [WindowsSid], [LoginName], [DisplayName], [IsTechnician], [IsManager], [IsAdmin], [IsSyncOperator]
    )
    VALUES
    (
        @SyncServiceSid, N'$(SyncServicePrincipal)', N'TechBench WHD Sync Service', 0, 0, 0, 0
    );
END;

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
-- BEGIN 41-V0002-WorkProcedures.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'tb_security.WriteAuditEvent', N'P') IS NOT NULL
    DROP PROCEDURE [tb_security].[WriteAuditEvent];
GO

CREATE PROCEDURE [tb_security].[WriteAuditEvent]
    @Action nvarchar(120),
    @EntityType nvarchar(120),
    @EntityId nvarchar(120),
    @RequestId uniqueidentifier = NULL,
    @DataJson nvarchar(max) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorWindowsSid varbinary(85) = SUSER_SID(ORIGINAL_LOGIN());
    DECLARE @ActorLoginName nvarchar(256) =
        CONVERT(nvarchar(256), ORIGINAL_LOGIN());

    IF @ActorWindowsSid IS NULL
       OR NULLIF(LTRIM(RTRIM(@ActorLoginName)), N'') IS NULL
    BEGIN
        THROW 51100, N'An authenticated Windows identity is required for auditing.', 1;
    END;

    IF @DataJson IS NOT NULL AND ISJSON(@DataJson) <> 1
    BEGIN
        THROW 51101, N'Audit DataJson must contain valid JSON.', 1;
    END;

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
        @EntityType,
        @EntityId,
        COALESCE(@RequestId, NEWID()),
        @DataJson,
        SYSUTCDATETIME()
    );
END;
GO

IF OBJECT_ID(N'tb_app.SearchTickets', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SearchTickets];
GO

CREATE PROCEDURE [tb_app].[SearchTickets]
    @ClientId int = NULL,
    @Search nvarchar(240) = NULL,
    @IncludeClosed bit = 0,
    @Limit int = 500
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
            WHEN @Limit > 2000 THEN 2000
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
        ticket.[Id],
        ticket.[TicketNumber],
        ticket.[ClientId],
        ticket.[Subject],
        ticket.[Status],
        ticket.[Source],
        ticket.[ExternalId],
        ticket.[WhdStatusTypeId],
        ticket.[IsClosed],
        ticket.[LastSyncedAtUtc] AS [LastSyncedAt],
        ticket.[RowVersion]
    FROM [tb_data].[Tickets] AS ticket
    WHERE (@ClientId IS NULL OR ticket.[ClientId] = @ClientId)
      AND (@IncludeClosed = 1 OR ticket.[IsClosed] = 0)
      AND
      (
          @Pattern IS NULL
          OR ticket.[TicketNumber] LIKE @Pattern ESCAPE N'~'
          OR ticket.[Subject] LIKE @Pattern ESCAPE N'~'
          OR ticket.[Status] LIKE @Pattern ESCAPE N'~'
          OR ticket.[ExternalId] LIKE @Pattern ESCAPE N'~'
      )
    ORDER BY ticket.[IsClosed], ticket.[TicketNumber], ticket.[Id];
END;
GO

IF OBJECT_ID(N'tb_app.GetTicket', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetTicket];
GO

CREATE PROCEDURE [tb_app].[GetTicket]
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
        ticket.[Id],
        ticket.[TicketNumber],
        ticket.[ClientId],
        ticket.[Subject],
        ticket.[Status],
        ticket.[Source],
        ticket.[ExternalId],
        ticket.[WhdStatusTypeId],
        ticket.[IsClosed],
        ticket.[LastSyncedAtUtc] AS [LastSyncedAt],
        ticket.[RowVersion]
    FROM [tb_data].[Tickets] AS ticket
    WHERE ticket.[Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.SaveTicket', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveTicket];
GO

CREATE PROCEDURE [tb_app].[SaveTicket]
    @Id int = NULL,
    @TicketNumber nvarchar(120),
    @ClientId int,
    @Subject nvarchar(500) = N'',
    @Status nvarchar(160) = N'Open',
    @Source nvarchar(40) = N'Manual',
    @ExternalId nvarchar(240) = NULL,
    @WhdStatusTypeId int = NULL,
    @IsClosed bit = 0,
    @LastSyncedAtUtc datetime2(3) = NULL,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
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

    SET @TicketNumber = NULLIF(LTRIM(RTRIM(@TicketNumber)), N'');
    SET @Subject = COALESCE(LTRIM(RTRIM(@Subject)), N'');
    SET @Status = COALESCE(NULLIF(LTRIM(RTRIM(@Status)), N''), N'Open');
    SET @Source = COALESCE(NULLIF(LTRIM(RTRIM(@Source)), N''), N'Manual');
    SET @ExternalId = NULLIF(LTRIM(RTRIM(@ExternalId)), N'');

    IF @TicketNumber IS NULL
        THROW 51110, N'TicketNumber is required.', 1;
    IF NOT EXISTS (SELECT 1 FROM [tb_data].[Clients] WHERE [Id] = @ClientId)
        THROW 51110, N'The selected client does not exist.', 1;
    IF @IsAdmin <> 1 AND @IsSyncOperator <> 1
    BEGIN
        IF @Id IS NULL
        BEGIN
            IF @Source <> N'Manual'
                THROW 51111, N'Only an Admin or Sync Operator may create synchronized tickets.', 1;

            /* Manual tickets cannot manufacture synchronization identity/state. */
            SET @ExternalId = NULL;
            SET @WhdStatusTypeId = NULL;
            SET @LastSyncedAtUtc = NULL;
        END
        ELSE
        BEGIN
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[Tickets]
                WHERE [Id] = @Id
                  AND [TicketNumber] = @TicketNumber
                  AND [ClientId] = @ClientId
                  AND [Subject] = @Subject
                  AND [Source] = @Source
                  AND
                  (
                      [ExternalId] = @ExternalId
                      OR ([ExternalId] IS NULL AND @ExternalId IS NULL)
                  )
            )
                THROW 51111, N'Technicians may update ticket status but may not change synchronization identity, client, source, or subject.', 1;

            IF @Source = N'WHD'
               AND NOT EXISTS
               (
                   SELECT 1
                   FROM [tb_data].[TicketStatusOptions]
                   WHERE [Source] = N'WHD'
                     AND [WhdStatusTypeId] = @WhdStatusTypeId
                     AND [Name] = @Status
                     AND [IsClosed] = @IsClosed
               )
               AND NOT EXISTS
               (
                   SELECT 1
                   FROM [tb_data].[Tickets]
                   WHERE [Id] = @Id
                     AND [Status] = @Status
                     AND [IsClosed] = @IsClosed
                     AND
                     (
                         [WhdStatusTypeId] = @WhdStatusTypeId
                         OR ([WhdStatusTypeId] IS NULL AND @WhdStatusTypeId IS NULL)
                     )
               )
                THROW 51115, N'The selected WHD status metadata is not a synchronized status option.', 1;
        END;
    END;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51112, N'ExpectedRowVersion is required when updating a ticket.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @Action nvarchar(120);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[Tickets]
            (
                [TicketNumber],
                [ClientId],
                [Subject],
                [Status],
                [Source],
                [ExternalId],
                [WhdStatusTypeId],
                [IsClosed],
                [LastSyncedAtUtc],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @TicketNumber,
                @ClientId,
                @Subject,
                @Status,
                @Source,
                @ExternalId,
                @WhdStatusTypeId,
                @IsClosed,
                @LastSyncedAtUtc,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );

            SET @Id = CONVERT(int, SCOPE_IDENTITY());
            SET @Action = N'TicketCreated';
        END
        ELSE
        BEGIN
            UPDATE [tb_data].[Tickets]
            SET
                [TicketNumber] = @TicketNumber,
                [ClientId] = @ClientId,
                [Subject] = @Subject,
                [Status] = @Status,
                [Source] = @Source,
                [ExternalId] = @ExternalId,
                [WhdStatusTypeId] = @WhdStatusTypeId,
                [IsClosed] = @IsClosed,
                [LastSyncedAtUtc] =
                    CASE
                        WHEN @IsAdmin = 1 OR @IsSyncOperator = 1
                            THEN @LastSyncedAtUtc
                        ELSE [LastSyncedAtUtc]
                    END,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM [tb_data].[Tickets] WHERE [Id] = @Id)
                    THROW 51113, N'The ticket no longer exists.', 1;
                THROW 51114, N'The ticket changed after it was loaded.', 1;
            END;

            SET @Action = N'TicketUpdated';
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
            @EntityType = N'Ticket',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        ticket.[Id],
        ticket.[TicketNumber],
        ticket.[ClientId],
        ticket.[Subject],
        ticket.[Status],
        ticket.[Source],
        ticket.[ExternalId],
        ticket.[WhdStatusTypeId],
        ticket.[IsClosed],
        ticket.[LastSyncedAtUtc] AS [LastSyncedAt],
        ticket.[RowVersion]
    FROM [tb_data].[Tickets] AS ticket
    WHERE ticket.[Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.GetTicketStatusOptions', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetTicketStatusOptions];
GO

CREATE PROCEDURE [tb_app].[GetTicketStatusOptions]
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
        [Id],
        [Name],
        [Source],
        [ExternalId],
        [WhdStatusTypeId],
        [IsClosed],
        [LastSyncedAtUtc] AS [LastSyncedAt],
        [RowVersion]
    FROM [tb_data].[TicketStatusOptions]
    ORDER BY [IsClosed], [Name], [Id];
END;
GO

IF OBJECT_ID(N'tb_app.SearchWorkEntries', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SearchWorkEntries];
GO

CREATE PROCEDURE [tb_app].[SearchWorkEntries]
    @StartDate date = NULL,
    @EndDate date = NULL,
    @ClientId int = NULL,
    @TicketId int = NULL,
    @ExcludeId int = NULL,
    @TicketText nvarchar(120) = NULL,
    @PostingStatus nvarchar(40) = NULL,
    @Keyword nvarchar(240) = NULL,
    @Tags nvarchar(500) = NULL,
    @FollowUpState nvarchar(30) = NULL,
    @OpenFollowUpsOnly bit = 0,
    @PendingWhdOnly bit = 0,
    @PendingSageOnly bit = 0,
    @PendingAnyOnly bit = 0,
    @IncludeAllUsers bit = 0,
    @Limit int = 500
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

    IF @IncludeAllUsers = 1 AND @IsManager <> 1 AND @IsAdmin <> 1
        THROW 51120, N'Only a Manager or Admin may search other users'' work entries.', 1;

    SET @Limit =
        CASE
            WHEN @Limit IS NULL OR @Limit < 1 THEN 1
            WHEN @Limit > 2000 THEN 2000
            ELSE @Limit
        END;
    SET @TicketText = NULLIF(LTRIM(RTRIM(@TicketText)), N'');
    SET @PostingStatus = NULLIF(LTRIM(RTRIM(@PostingStatus)), N'');
    SET @Keyword = NULLIF(LTRIM(RTRIM(@Keyword)), N'');
    SET @Tags = NULLIF(LTRIM(RTRIM(@Tags)), N'');
    SET @FollowUpState = NULLIF(LTRIM(RTRIM(@FollowUpState)), N'');

    DECLARE @KeywordPattern nvarchar(500) =
        CASE WHEN @Keyword IS NULL THEN NULL ELSE N'%' + @Keyword + N'%' END;
    DECLARE @TicketPattern nvarchar(300) =
        CASE WHEN @TicketText IS NULL THEN NULL ELSE N'%' + @TicketText + N'%' END;
    DECLARE @TagPattern nvarchar(700) =
        CASE WHEN @Tags IS NULL THEN NULL ELSE N'%' + @Tags + N'%' END;

    SELECT TOP (@Limit)
        work_entry.[Id],
        work_entry.[OwnerWindowsSid],
        work_entry.[WorkDate],
        work_entry.[ClientId],
        work_entry.[ManualClientName],
        work_entry.[TicketId],
        work_entry.[TicketNumberText],
        work_entry.[HasTimeRange],
        work_entry.[StartTime],
        work_entry.[EndTime],
        work_entry.[DurationMinutes],
        work_entry.[Billable],
        work_entry.[Note],
        CASE
            WHEN work_entry.[OwnerWindowsSid] = @UserSid
                THEN personal_note.[Note]
            ELSE NULL
        END AS [InternalNote],
        CASE
            WHEN work_entry.[OwnerWindowsSid] = @UserSid
                THEN personal_note.[Note]
            ELSE NULL
        END AS [PersonalNote],
        CASE
            WHEN work_entry.[OwnerWindowsSid] = @UserSid
                THEN COALESCE(personal_note.[IncludeInWhd], 0)
            ELSE CONVERT(bit, 0)
        END AS [IncludePersonalNoteInWhd],
        work_entry.[Tags],
        work_entry.[FollowUpState],
        work_entry.[FollowUpDueDate],
        work_entry.[WhdPosted],
        work_entry.[WhdPostedAtUtc] AS [WhdPostedAt],
        work_entry.[SagePosted],
        work_entry.[SagePostedAtUtc] AS [SagePostedAt],
        work_entry.[SageTicketNumber],
        work_entry.[PostingStatus],
        work_entry.[LastError],
        work_entry.[CreatedAtUtc] AS [CreatedAt],
        work_entry.[UpdatedAtUtc] AS [UpdatedAt],
        client.[Name] AS [ClientName],
        ticket.[TicketNumber],
        ticket.[Subject] AS [TicketSubject],
        work_entry.[RowVersion],
        CASE
            WHEN work_entry.[OwnerWindowsSid] = @UserSid
                THEN personal_note.[RowVersion]
            ELSE NULL
        END AS [PersonalNoteRowVersion]
    FROM [tb_data].[WorkEntries] AS work_entry
    LEFT JOIN [tb_data].[Clients] AS client
        ON client.[Id] = work_entry.[ClientId]
    LEFT JOIN [tb_data].[Tickets] AS ticket
        ON ticket.[Id] = work_entry.[TicketId]
    LEFT JOIN [tb_private].[WorkEntryPersonalNotes] AS personal_note
        ON personal_note.[WorkEntryId] = work_entry.[Id]
       AND personal_note.[OwnerWindowsSid] = @UserSid
    WHERE (@IncludeAllUsers = 1 OR work_entry.[OwnerWindowsSid] = @UserSid)
      AND (@StartDate IS NULL OR work_entry.[WorkDate] >= @StartDate)
      AND (@EndDate IS NULL OR work_entry.[WorkDate] <= @EndDate)
      AND (@ClientId IS NULL OR work_entry.[ClientId] = @ClientId)
      AND (@TicketId IS NULL OR work_entry.[TicketId] = @TicketId)
      AND (@ExcludeId IS NULL OR work_entry.[Id] <> @ExcludeId)
      AND
      (
          @TicketPattern IS NULL
          OR ticket.[TicketNumber] LIKE @TicketPattern
          OR work_entry.[TicketNumberText] LIKE @TicketPattern
      )
      AND (@PostingStatus IS NULL OR work_entry.[PostingStatus] = @PostingStatus)
      AND (@TagPattern IS NULL OR work_entry.[Tags] LIKE @TagPattern)
      AND (@FollowUpState IS NULL OR work_entry.[FollowUpState] = @FollowUpState)
      AND
      (
          @OpenFollowUpsOnly = 0
          OR work_entry.[FollowUpState] IN (N'FollowUp', N'Waiting')
      )
      AND
      (
          @PendingWhdOnly = 0
          OR
          (
              (work_entry.[TicketId] IS NOT NULL
                  OR NULLIF(LTRIM(RTRIM(work_entry.[TicketNumberText])), N'') IS NOT NULL)
              AND work_entry.[SagePosted] = 0
              AND
              (
                  work_entry.[WhdPosted] = 0
                  OR work_entry.[WhdPostedAtUtc] IS NULL
                  OR work_entry.[UpdatedAtUtc] > work_entry.[WhdPostedAtUtc]
                  OR work_entry.[LastError] LIKE N'WHD sync conflict:%'
              )
          )
      )
      AND
      (
          @PendingSageOnly = 0
          OR (work_entry.[Billable] = 1 AND work_entry.[SagePosted] = 0)
      )
      AND
      (
          @PendingAnyOnly = 0
          OR
          (
              (work_entry.[Billable] = 1 AND work_entry.[SagePosted] = 0)
              OR
              (
                  (work_entry.[TicketId] IS NOT NULL
                      OR NULLIF(LTRIM(RTRIM(work_entry.[TicketNumberText])), N'') IS NOT NULL)
                  AND work_entry.[SagePosted] = 0
                  AND
                  (
                      work_entry.[WhdPosted] = 0
                      OR work_entry.[WhdPostedAtUtc] IS NULL
                      OR work_entry.[UpdatedAtUtc] > work_entry.[WhdPostedAtUtc]
                      OR work_entry.[LastError] LIKE N'WHD sync conflict:%'
                  )
              )
          )
      )
      AND
      (
          @KeywordPattern IS NULL
          OR work_entry.[Note] LIKE @KeywordPattern
          OR work_entry.[Tags] LIKE @KeywordPattern
          OR work_entry.[ManualClientName] LIKE @KeywordPattern
          OR work_entry.[TicketNumberText] LIKE @KeywordPattern
          OR client.[Name] LIKE @KeywordPattern
          OR ticket.[TicketNumber] LIKE @KeywordPattern
          OR ticket.[Subject] LIKE @KeywordPattern
          OR
          (
              work_entry.[OwnerWindowsSid] = @UserSid
              AND personal_note.[Note] LIKE @KeywordPattern
          )
      )
    ORDER BY work_entry.[WorkDate] DESC, work_entry.[StartTime] DESC, work_entry.[Id] DESC;
END;
GO

IF OBJECT_ID(N'tb_app.GetWorkEntry', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetWorkEntry];
GO

CREATE PROCEDURE [tb_app].[GetWorkEntry]
    @Id int,
    @IncludeAllUsers bit = 0
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

    IF @IncludeAllUsers = 1 AND @IsManager <> 1 AND @IsAdmin <> 1
        THROW 51120, N'Only a Manager or Admin may read another user''s work entry.', 1;

    DECLARE @CanReadAll bit =
        CONVERT(
            bit,
            CASE
                WHEN @IncludeAllUsers = 1
                 AND (@IsManager = 1 OR @IsAdmin = 1)
                    THEN 1
                ELSE 0
            END);

    SELECT
        work_entry.[Id],
        work_entry.[OwnerWindowsSid],
        work_entry.[WorkDate],
        work_entry.[ClientId],
        work_entry.[ManualClientName],
        work_entry.[TicketId],
        work_entry.[TicketNumberText],
        work_entry.[HasTimeRange],
        work_entry.[StartTime],
        work_entry.[EndTime],
        work_entry.[DurationMinutes],
        work_entry.[Billable],
        work_entry.[Note],
        CASE WHEN work_entry.[OwnerWindowsSid] = @UserSid THEN personal_note.[Note] END
            AS [InternalNote],
        CASE WHEN work_entry.[OwnerWindowsSid] = @UserSid THEN personal_note.[Note] END
            AS [PersonalNote],
        CASE
            WHEN work_entry.[OwnerWindowsSid] = @UserSid
                THEN COALESCE(personal_note.[IncludeInWhd], 0)
            ELSE CONVERT(bit, 0)
        END AS [IncludePersonalNoteInWhd],
        work_entry.[Tags],
        work_entry.[FollowUpState],
        work_entry.[FollowUpDueDate],
        work_entry.[WhdPosted],
        work_entry.[WhdPostedAtUtc] AS [WhdPostedAt],
        work_entry.[SagePosted],
        work_entry.[SagePostedAtUtc] AS [SagePostedAt],
        work_entry.[SageTicketNumber],
        work_entry.[PostingStatus],
        work_entry.[LastError],
        work_entry.[CreatedAtUtc] AS [CreatedAt],
        work_entry.[UpdatedAtUtc] AS [UpdatedAt],
        client.[Name] AS [ClientName],
        ticket.[TicketNumber],
        ticket.[Subject] AS [TicketSubject],
        work_entry.[RowVersion],
        CASE WHEN work_entry.[OwnerWindowsSid] = @UserSid THEN personal_note.[RowVersion] END
            AS [PersonalNoteRowVersion]
    FROM [tb_data].[WorkEntries] AS work_entry
    LEFT JOIN [tb_data].[Clients] AS client
        ON client.[Id] = work_entry.[ClientId]
    LEFT JOIN [tb_data].[Tickets] AS ticket
        ON ticket.[Id] = work_entry.[TicketId]
    LEFT JOIN [tb_private].[WorkEntryPersonalNotes] AS personal_note
        ON personal_note.[WorkEntryId] = work_entry.[Id]
       AND personal_note.[OwnerWindowsSid] = @UserSid
    WHERE work_entry.[Id] = @Id
      AND (work_entry.[OwnerWindowsSid] = @UserSid OR @CanReadAll = 1);
END;
GO

IF OBJECT_ID(N'tb_app.GetDistinctTags', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetDistinctTags];
GO

CREATE PROCEDURE [tb_app].[GetDistinctTags]
    @IncludeAllUsers bit = 0
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

    /*
        @IncludeAllUsers is retained for desktop contract compatibility. Tags are
        now a canonical organization catalog, so every authorized user receives
        the same list without this procedure reading or exposing work entries.
    */
    SELECT [Tag]
    FROM [tb_data].[OrganizationTags]
    ORDER BY [Tag];
END;
GO

IF OBJECT_ID(N'tb_app.SaveWorkEntry', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveWorkEntry];
GO

CREATE PROCEDURE [tb_app].[SaveWorkEntry]
    @Id int = NULL,
    @WorkDate date,
    @ClientId int = NULL,
    @ManualClientName nvarchar(240) = NULL,
    @TicketId int = NULL,
    @TicketNumberText nvarchar(120) = NULL,
    @HasTimeRange bit = 1,
    @StartTime time(0) = '00:00',
    @EndTime time(0) = '00:00',
    @DurationMinutes int,
    @Billable bit = 1,
    @Note nvarchar(max) = N'',
    @PersonalNote nvarchar(max) = NULL,
    @IncludePersonalNoteInWhd bit = 0,
    @Tags nvarchar(1000) = N'',
    @FollowUpState nvarchar(30) = N'None',
    @FollowUpDueDate date = NULL,
    @PostingStatus nvarchar(40) = N'Draft',
    @LastError nvarchar(max) = NULL,
    @ExpectedRowVersion binary(8) = NULL,
    @ExpectedPersonalNoteRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
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

    SET @ManualClientName = NULLIF(LTRIM(RTRIM(@ManualClientName)), N'');
    SET @TicketNumberText = NULLIF(LTRIM(RTRIM(@TicketNumberText)), N'');
    SET @Note = COALESCE(@Note, N'');
    SET @PersonalNote = NULLIF(LTRIM(RTRIM(@PersonalNote)), N'');
    SET @Tags = COALESCE(LTRIM(RTRIM(@Tags)), N'');
    SET @FollowUpState =
        COALESCE(NULLIF(LTRIM(RTRIM(@FollowUpState)), N''), N'None');
    SET @PostingStatus =
        COALESCE(NULLIF(LTRIM(RTRIM(@PostingStatus)), N''), N'Draft');
    SET @LastError = NULLIF(LTRIM(RTRIM(@LastError)), N'');

    IF @ClientId IS NULL AND @ManualClientName IS NULL
        THROW 51130, N'A client or manual client name is required.', 1;
    IF @ClientId IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM [tb_data].[Clients] WHERE [Id] = @ClientId)
        THROW 51130, N'The selected client does not exist.', 1;
    IF @TicketId IS NOT NULL
       AND NOT EXISTS
       (
           SELECT 1
           FROM [tb_data].[Tickets]
           WHERE [Id] = @TicketId
             AND (@ClientId IS NULL OR [ClientId] = @ClientId)
       )
        THROW 51130, N'The selected ticket does not exist for the selected client.', 1;
    IF @DurationMinutes < 0 OR @DurationMinutes > 1440
        THROW 51130, N'DurationMinutes must be between 0 and 1440.', 1;
    IF @FollowUpState NOT IN (N'None', N'FollowUp', N'Waiting', N'Completed')
        THROW 51130, N'FollowUpState is invalid.', 1;
    IF @PostingStatus NOT IN (N'Draft', N'Ready')
        THROW 51130, N'PostingStatus may be only Draft or Ready in SaveWorkEntry.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51131, N'ExpectedRowVersion is required when updating a work entry.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @Action nvarchar(120);
    DECLARE @ExistingWhdPosted bit;
    DECLARE @ExistingSagePosted bit;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[WorkEntries]
            (
                [OwnerWindowsSid],
                [WorkDate],
                [ClientId],
                [ManualClientName],
                [TicketId],
                [TicketNumberText],
                [HasTimeRange],
                [StartTime],
                [EndTime],
                [DurationMinutes],
                [Billable],
                [Note],
                [Tags],
                [FollowUpState],
                [FollowUpDueDate],
                [WhdPosted],
                [WhdPostedAtUtc],
                [SagePosted],
                [SagePostedAtUtc],
                [SageTicketNumber],
                [PostingStatus],
                [LastError],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @UserSid,
                @WorkDate,
                @ClientId,
                @ManualClientName,
                @TicketId,
                @TicketNumberText,
                @HasTimeRange,
                @StartTime,
                @EndTime,
                @DurationMinutes,
                @Billable,
                @Note,
                @Tags,
                @FollowUpState,
                @FollowUpDueDate,
                0,
                NULL,
                0,
                NULL,
                NULL,
                CASE WHEN @LastError IS NOT NULL THEN N'Failed' ELSE @PostingStatus END,
                @LastError,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );

            SET @Id = CONVERT(int, SCOPE_IDENTITY());
            SET @Action = N'WorkEntryCreated';
        END
        ELSE
        BEGIN
            SELECT
                @ExistingWhdPosted = [WhdPosted],
                @ExistingSagePosted = [SagePosted]
            FROM [tb_data].[WorkEntries] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @Id
              AND [OwnerWindowsSid] = @UserSid
              AND [RowVersion] = @ExpectedRowVersion;

            IF @ExistingWhdPosted IS NULL
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM [tb_data].[WorkEntries] WHERE [Id] = @Id)
                    THROW 51132, N'The work entry no longer exists.', 1;
                IF NOT EXISTS
                (
                    SELECT 1
                    FROM [tb_data].[WorkEntries]
                    WHERE [Id] = @Id
                      AND [OwnerWindowsSid] = @UserSid
                )
                    THROW 51133, N'Only the work-entry owner may update it.', 1;
                THROW 51134, N'The work entry changed after it was loaded.', 1;
            END;

            IF @ExistingSagePosted = 1
                THROW 51137, N'A work entry already posted to Sage cannot be changed.', 1;

            IF EXISTS
            (
                SELECT 1
                FROM [tb_ops].[PostingAttempts] WITH (UPDLOCK, HOLDLOCK)
                WHERE [WorkEntryId] = @Id
                  AND [OwnerWindowsSid] = @UserSid
                  AND [Status] IN (N'Started', N'Unknown')
            )
            OR EXISTS
            (
                SELECT 1
                FROM [tb_ops].[PostingLeases] WITH (UPDLOCK, HOLDLOCK)
                WHERE [WorkEntryId] = @Id
                  AND [OwnerWindowsSid] = @UserSid
            )
                THROW 51139, N'A work entry cannot be changed while an external posting attempt is active.', 1;

            UPDATE [tb_data].[WorkEntries]
            SET
                [WorkDate] = @WorkDate,
                [ClientId] = @ClientId,
                [ManualClientName] = @ManualClientName,
                [TicketId] = @TicketId,
                [TicketNumberText] = @TicketNumberText,
                [HasTimeRange] = @HasTimeRange,
                [StartTime] = @StartTime,
                [EndTime] = @EndTime,
                [DurationMinutes] = @DurationMinutes,
                [Billable] = @Billable,
                [Note] = @Note,
                [Tags] = @Tags,
                [FollowUpState] = @FollowUpState,
                [FollowUpDueDate] = @FollowUpDueDate,
                [PostingStatus] =
                    CASE
                        WHEN @LastError IS NOT NULL THEN N'Failed'
                        WHEN [WhdPosted] = 1 AND [SagePosted] = 1 THEN N'PostedToBoth'
                        WHEN [SagePosted] = 1 THEN N'PostedToSage'
                        WHEN [WhdPosted] = 1 THEN N'PostedToWhd'
                        ELSE @PostingStatus
                    END,
                [LastError] = @LastError,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id
              AND [OwnerWindowsSid] = @UserSid
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM [tb_data].[WorkEntries] WHERE [Id] = @Id)
                    THROW 51132, N'The work entry no longer exists.', 1;
                IF NOT EXISTS
                (
                    SELECT 1
                    FROM [tb_data].[WorkEntries]
                    WHERE [Id] = @Id
                      AND [OwnerWindowsSid] = @UserSid
                )
                    THROW 51133, N'Only the work-entry owner may update it.', 1;
                THROW 51134, N'The work entry changed after it was loaded.', 1;
            END;

            SET @Action = N'WorkEntryUpdated';
        END;

        IF EXISTS
        (
            SELECT 1
            FROM [tb_private].[WorkEntryPersonalNotes]
            WHERE [WorkEntryId] = @Id
              AND [OwnerWindowsSid] = @UserSid
        )
        BEGIN
            IF @ExpectedPersonalNoteRowVersion IS NULL
                THROW 51135, N'ExpectedPersonalNoteRowVersion is required for an existing personal note.', 1;

            IF @PersonalNote IS NULL AND @IncludePersonalNoteInWhd = 0
            BEGIN
                DELETE FROM [tb_private].[WorkEntryPersonalNotes]
                WHERE [WorkEntryId] = @Id
                  AND [OwnerWindowsSid] = @UserSid
                  AND [RowVersion] = @ExpectedPersonalNoteRowVersion;
            END
            ELSE
            BEGIN
                UPDATE [tb_private].[WorkEntryPersonalNotes]
                SET
                    [Note] = COALESCE(@PersonalNote, N''),
                    [IncludeInWhd] = @IncludePersonalNoteInWhd,
                    [UpdatedAtUtc] = @NowUtc
                WHERE [WorkEntryId] = @Id
                  AND [OwnerWindowsSid] = @UserSid
                  AND [RowVersion] = @ExpectedPersonalNoteRowVersion;
            END;

            IF @@ROWCOUNT = 0
                THROW 51136, N'The personal note changed after it was loaded.', 1;
        END
        ELSE
        BEGIN
            IF @ExpectedPersonalNoteRowVersion IS NOT NULL
                THROW 51136, N'The personal note changed after it was loaded.', 1;

            IF @PersonalNote IS NOT NULL OR @IncludePersonalNoteInWhd = 1
            BEGIN
                INSERT INTO [tb_private].[WorkEntryPersonalNotes]
                (
                    [WorkEntryId],
                    [OwnerWindowsSid],
                    [Note],
                    [IncludeInWhd],
                    [CreatedAtUtc],
                    [UpdatedAtUtc]
                )
                VALUES
                (
                    @Id,
                    @UserSid,
                    COALESCE(@PersonalNote, N''),
                    @IncludePersonalNoteInWhd,
                    @NowUtc,
                    @NowUtc
                );
            END;
        END;

        /*
            Publish newly entered tags to the organization catalog in the same
            transaction as the work-entry save. Range locks serialize first use
            of a tag across workstations; no direct table grant is required.
        */
        ;WITH parsed_tags AS
        (
            SELECT DISTINCT
                LTRIM(RTRIM(tag.[value])) AS [Tag],
                CONVERT
                (
                    binary(32),
                    HASHBYTES
                    (
                        N'SHA2_256',
                        CONVERT
                        (
                            varbinary(2000),
                            UPPER(LTRIM(RTRIM(tag.[value])))
                        )
                    )
                ) AS [TagHash]
            FROM STRING_SPLIT(@Tags, N',') AS tag
            WHERE NULLIF(LTRIM(RTRIM(tag.[value])), N'') IS NOT NULL
        )
        INSERT INTO [tb_data].[OrganizationTags]
        (
            [Tag],
            [TagHash],
            [CreatedByWindowsSid],
            [CreatedAtUtc]
        )
        SELECT
            parsed_tag.[Tag],
            parsed_tag.[TagHash],
            @UserSid,
            @NowUtc
        FROM parsed_tags AS parsed_tag
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[OrganizationTags] WITH (UPDLOCK, HOLDLOCK)
            WHERE [TagHash] = parsed_tag.[TagHash]
        );

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
            @EntityType = N'WorkEntry',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        work_entry.[Id],
        work_entry.[OwnerWindowsSid],
        work_entry.[WorkDate],
        work_entry.[ClientId],
        work_entry.[ManualClientName],
        work_entry.[TicketId],
        work_entry.[TicketNumberText],
        work_entry.[HasTimeRange],
        work_entry.[StartTime],
        work_entry.[EndTime],
        work_entry.[DurationMinutes],
        work_entry.[Billable],
        work_entry.[Note],
        personal_note.[Note] AS [InternalNote],
        personal_note.[Note] AS [PersonalNote],
        COALESCE(personal_note.[IncludeInWhd], 0) AS [IncludePersonalNoteInWhd],
        work_entry.[Tags],
        work_entry.[FollowUpState],
        work_entry.[FollowUpDueDate],
        work_entry.[WhdPosted],
        work_entry.[WhdPostedAtUtc] AS [WhdPostedAt],
        work_entry.[SagePosted],
        work_entry.[SagePostedAtUtc] AS [SagePostedAt],
        work_entry.[SageTicketNumber],
        work_entry.[PostingStatus],
        work_entry.[LastError],
        work_entry.[CreatedAtUtc] AS [CreatedAt],
        work_entry.[UpdatedAtUtc] AS [UpdatedAt],
        client.[Name] AS [ClientName],
        ticket.[TicketNumber],
        ticket.[Subject] AS [TicketSubject],
        work_entry.[RowVersion],
        personal_note.[RowVersion] AS [PersonalNoteRowVersion]
    FROM [tb_data].[WorkEntries] AS work_entry
    LEFT JOIN [tb_data].[Clients] AS client
        ON client.[Id] = work_entry.[ClientId]
    LEFT JOIN [tb_data].[Tickets] AS ticket
        ON ticket.[Id] = work_entry.[TicketId]
    LEFT JOIN [tb_private].[WorkEntryPersonalNotes] AS personal_note
        ON personal_note.[WorkEntryId] = work_entry.[Id]
       AND personal_note.[OwnerWindowsSid] = @UserSid
    WHERE work_entry.[Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteWorkEntry', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteWorkEntry];
GO

CREATE PROCEDURE [tb_app].[DeleteWorkEntry]
    @Id int,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
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
    DECLARE @WhdPosted bit;
    DECLARE @SagePosted bit;

    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @UserSid OUTPUT,
        @LoginName = @LoginName OUTPUT,
        @DisplayName = @DisplayName OUTPUT,
        @IsTechnician = @IsTechnician OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT
            @WhdPosted = [WhdPosted],
            @SagePosted = [SagePosted]
        FROM [tb_data].[WorkEntries] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Id] = @Id
          AND [OwnerWindowsSid] = @UserSid
          AND [RowVersion] = @ExpectedRowVersion;

        IF @WhdPosted IS NULL
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM [tb_data].[WorkEntries] WHERE [Id] = @Id)
                THROW 51132, N'The work entry no longer exists.', 1;
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[WorkEntries]
                WHERE [Id] = @Id
                  AND [OwnerWindowsSid] = @UserSid
            )
                THROW 51133, N'Only the work-entry owner may delete it.', 1;
            THROW 51134, N'The work entry changed after it was loaded.', 1;
        END;

        IF @WhdPosted = 1 OR @SagePosted = 1
            THROW 51138, N'A work entry posted to WHD or Sage cannot be deleted.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM [tb_ops].[PostingAttempts] WITH (UPDLOCK, HOLDLOCK)
            WHERE [WorkEntryId] = @Id
              AND [OwnerWindowsSid] = @UserSid
              AND [Status] IN (N'Started', N'Unknown')
        )
        OR EXISTS
        (
            SELECT 1
            FROM [tb_ops].[PostingLeases] WITH (UPDLOCK, HOLDLOCK)
            WHERE [WorkEntryId] = @Id
              AND [OwnerWindowsSid] = @UserSid
        )
            THROW 51139, N'A work entry cannot be deleted while an external posting attempt is active.', 1;

        DELETE FROM [tb_data].[WorkEntryLinks]
        WHERE [SourceWorkEntryId] = @Id
           OR [TargetWorkEntryId] = @Id;

        DELETE FROM [tb_data].[WorkEntries]
        WHERE [Id] = @Id
          AND [OwnerWindowsSid] = @UserSid
          AND [RowVersion] = @ExpectedRowVersion;

        IF @@ROWCOUNT = 0
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM [tb_data].[WorkEntries] WHERE [Id] = @Id)
                THROW 51132, N'The work entry no longer exists.', 1;
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[WorkEntries]
                WHERE [Id] = @Id
                  AND [OwnerWindowsSid] = @UserSid
            )
                THROW 51133, N'Only the work-entry owner may delete it.', 1;
            THROW 51134, N'The work entry changed after it was loaded.', 1;
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'WorkEntryDeleted',
            @EntityType = N'WorkEntry',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_app.GetWorkEntryLinks', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetWorkEntryLinks];
GO

CREATE PROCEDURE [tb_app].[GetWorkEntryLinks]
    @WorkEntryId int
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

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_data].[WorkEntries]
        WHERE [Id] = @WorkEntryId
          AND [OwnerWindowsSid] = @UserSid
    )
        THROW 51140, N'The work entry does not exist or is not owned by the current user.', 1;

    SELECT
        link.[Id],
        link.[SourceWorkEntryId],
        link.[TargetWorkEntryId],
        @WorkEntryId AS [CurrentWorkEntryId],
        link.[LinkType],
        link.[CreatedAtUtc] AS [CreatedAt],
        link.[RowVersion],
        related.[Id] AS [RelatedWorkEntryId],
        related.[WorkDate] AS [RelatedWorkDate],
        related.[ClientId] AS [RelatedClientId],
        related.[ManualClientName] AS [RelatedManualClientName],
        related.[TicketId] AS [RelatedTicketId],
        related.[TicketNumberText] AS [RelatedTicketNumberText],
        related.[Note] AS [RelatedNote],
        related.[Tags] AS [RelatedTags],
        related.[FollowUpState] AS [RelatedFollowUpState],
        related.[FollowUpDueDate] AS [RelatedFollowUpDueDate],
        related.[PostingStatus] AS [RelatedPostingStatus],
        related.[RowVersion] AS [RelatedRowVersion]
    FROM [tb_data].[WorkEntryLinks] AS link
    INNER JOIN [tb_data].[WorkEntries] AS related
        ON related.[Id] =
            CASE
                WHEN link.[SourceWorkEntryId] = @WorkEntryId
                    THEN link.[TargetWorkEntryId]
                ELSE link.[SourceWorkEntryId]
            END
       AND related.[OwnerWindowsSid] = @UserSid
    WHERE link.[SourceWorkEntryId] = @WorkEntryId
       OR link.[TargetWorkEntryId] = @WorkEntryId
    ORDER BY related.[WorkDate] DESC, related.[Id] DESC;
END;
GO

IF OBJECT_ID(N'tb_app.SaveWorkEntryLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveWorkEntryLink];
GO

CREATE PROCEDURE [tb_app].[SaveWorkEntryLink]
    @SourceWorkEntryId int,
    @TargetWorkEntryId int,
    @LinkType nvarchar(30) = N'Related',
    @RequestId uniqueidentifier = NULL
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

    SET @LinkType =
        COALESCE(NULLIF(LTRIM(RTRIM(@LinkType)), N''), N'Related');
    IF @SourceWorkEntryId = @TargetWorkEntryId
        THROW 51141, N'A work entry cannot link to itself.', 1;
    IF @LinkType NOT IN (N'Related', N'FollowUpTo')
        THROW 51141, N'LinkType must be Related or FollowUpTo.', 1;
    IF
    (
        SELECT COUNT(*)
        FROM [tb_data].[WorkEntries]
        WHERE [Id] IN (@SourceWorkEntryId, @TargetWorkEntryId)
          AND [OwnerWindowsSid] = @UserSid
    ) <> 2
        THROW 51142, N'Both linked work entries must belong to the current user.', 1;

    DECLARE @Id int;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @Id = [Id]
        FROM [tb_data].[WorkEntryLinks] WITH (UPDLOCK, HOLDLOCK)
        WHERE [SourceWorkEntryId] = @SourceWorkEntryId
          AND [TargetWorkEntryId] = @TargetWorkEntryId
          AND [LinkType] = @LinkType;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[WorkEntryLinks]
            (
                [SourceWorkEntryId],
                [TargetWorkEntryId],
                [LinkType],
                [CreatedByWindowsSid]
            )
            VALUES
            (
                @SourceWorkEntryId,
                @TargetWorkEntryId,
                @LinkType,
                @UserSid
            );
            SET @Id = CONVERT(int, SCOPE_IDENTITY());
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'WorkEntryLinkSaved',
            @EntityType = N'WorkEntryLink',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [SourceWorkEntryId],
        [TargetWorkEntryId],
        [LinkType],
        [CreatedAtUtc] AS [CreatedAt],
        [RowVersion]
    FROM [tb_data].[WorkEntryLinks]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteWorkEntryLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteWorkEntryLink];
GO

CREATE PROCEDURE [tb_app].[DeleteWorkEntryLink]
    @Id int,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
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

    DELETE link
    FROM [tb_data].[WorkEntryLinks] AS link
    INNER JOIN [tb_data].[WorkEntries] AS source_entry
        ON source_entry.[Id] = link.[SourceWorkEntryId]
    INNER JOIN [tb_data].[WorkEntries] AS target_entry
        ON target_entry.[Id] = link.[TargetWorkEntryId]
    WHERE link.[Id] = @Id
      AND source_entry.[OwnerWindowsSid] = @UserSid
      AND target_entry.[OwnerWindowsSid] = @UserSid
      AND (@ExpectedRowVersion IS NULL OR link.[RowVersion] = @ExpectedRowVersion);

    IF @@ROWCOUNT = 0
        THROW 51143, N'The work-entry link was not found, changed, or is not owned by the current user.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'WorkEntryLinkDeleted',
        @EntityType = N'WorkEntryLink',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.GetEditorDraft', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetEditorDraft];
GO

CREATE PROCEDURE [tb_app].[GetEditorDraft]
    @DeviceId uniqueidentifier
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
        [DeviceId],
        [Payload],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_user].[EditorDrafts]
    WHERE [OwnerWindowsSid] = @UserSid
      AND [DeviceId] = @DeviceId;
END;
GO

IF OBJECT_ID(N'tb_app.SaveEditorDraft', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveEditorDraft];
GO

CREATE PROCEDURE [tb_app].[SaveEditorDraft]
    @DeviceId uniqueidentifier,
    @Payload nvarchar(max),
    @ExpectedRowVersion binary(8) = NULL
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

    IF ISJSON(@Payload) <> 1
        THROW 51150, N'Editor draft payload must be valid JSON.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRANSACTION;

        IF EXISTS
        (
            SELECT 1
            FROM [tb_user].[EditorDrafts] WITH (UPDLOCK, HOLDLOCK)
            WHERE [OwnerWindowsSid] = @UserSid
              AND [DeviceId] = @DeviceId
        )
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 51151, N'ExpectedRowVersion is required for an existing editor draft.', 1;

            UPDATE [tb_user].[EditorDrafts]
            SET
                [Payload] = @Payload,
                [UpdatedAtUtc] = @NowUtc
            WHERE [OwnerWindowsSid] = @UserSid
              AND [DeviceId] = @DeviceId
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51152, N'The editor draft changed after it was loaded.', 1;
        END
        ELSE
        BEGIN
            IF @ExpectedRowVersion IS NOT NULL
                THROW 51152, N'The editor draft changed after it was loaded.', 1;

            INSERT INTO [tb_user].[EditorDrafts]
            (
                [OwnerWindowsSid],
                [DeviceId],
                [Payload],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @UserSid,
                @DeviceId,
                @Payload,
                @NowUtc,
                @NowUtc
            );
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [DeviceId],
        [Payload],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_user].[EditorDrafts]
    WHERE [OwnerWindowsSid] = @UserSid
      AND [DeviceId] = @DeviceId;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteEditorDraft', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteEditorDraft];
GO

CREATE PROCEDURE [tb_app].[DeleteEditorDraft]
    @DeviceId uniqueidentifier,
    @ExpectedRowVersion binary(8) = NULL
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

    DELETE FROM [tb_user].[EditorDrafts]
    WHERE [OwnerWindowsSid] = @UserSid
      AND [DeviceId] = @DeviceId
      AND (@ExpectedRowVersion IS NULL OR [RowVersion] = @ExpectedRowVersion);

    IF @@ROWCOUNT = 0
       AND EXISTS
       (
           SELECT 1
           FROM [tb_user].[EditorDrafts]
           WHERE [OwnerWindowsSid] = @UserSid
             AND [DeviceId] = @DeviceId
       )
        THROW 51152, N'The editor draft changed after it was loaded.', 1;
END;
GO

PRINT N'TechBench V0002 ticket, work-entry, private-note, link, and draft procedures created.';
GO

-- ============================================================================
-- END 41-V0002-WorkProcedures.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 42-V0002-SharedProcedures.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'tb_security.GetCurrentAccess', N'P') IS NOT NULL
    DROP PROCEDURE [tb_security].[GetCurrentAccess];
GO

CREATE PROCEDURE [tb_security].[GetCurrentAccess]
    @UserSid varbinary(85) OUTPUT,
    @IsManager bit OUTPUT,
    @IsAdmin bit OUTPUT,
    @IsSyncOperator bit OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @LoginName nvarchar(256);
    DECLARE @DisplayName nvarchar(160);
    DECLARE @IsTechnician bit;

    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @UserSid OUTPUT,
        @LoginName = @LoginName OUTPUT,
        @DisplayName = @DisplayName OUTPUT,
        @IsTechnician = @IsTechnician OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;
END;
GO

IF OBJECT_ID(N'tb_app.GetRepositoryCapabilities', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetRepositoryCapabilities];
GO

CREATE PROCEDURE [tb_app].[GetRepositoryCapabilities]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT
        CONVERT(int, 2) AS [SchemaVersion],
        CONVERT(bit, 0) AS [FullTextSearchAvailable],
        CONVERT(bit, 1) AS [SupportsTickets],
        CONVERT(bit, 1) AS [SupportsWorkEntries],
        CONVERT(bit, 1) AS [SupportsPrivateNotes],
        CONVERT(bit, 1) AS [SupportsPostingLeases],
        CONVERT(bit, 1) AS [SupportsSyncLeases],
        CONVERT(bit, 1) AS [SupportsImports];
END;
GO

IF OBJECT_ID(N'tb_app.EnsureWorkspaceDefaults', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[EnsureWorkspaceDefaults];
GO

CREATE PROCEDURE [tb_app].[EnsureWorkspaceDefaults]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @ChangedCount int = 0;

    DECLARE @DefaultLinks TABLE
    (
        [BuiltInKey] nvarchar(120) NOT NULL PRIMARY KEY,
        [Name] nvarchar(160) NOT NULL,
        [Url] nvarchar(2048) NOT NULL,
        [SortOrder] int NOT NULL
    );

    INSERT INTO @DefaultLinks([BuiltInKey], [Name], [Url], [SortOrder])
    VALUES
        (N'watchguard-cloud', N'WatchGuard Cloud', N'https://cloud.watchguard.com/', 0),
        (N'microsoft-365-admin', N'Microsoft 365 Admin Center', N'https://admin.microsoft.com/', 1),
        (N'barracuda-cloud-control', N'Barracuda Cloud Control', N'https://login.barracuda.com/', 2),
        (N'eset-protect', N'ESET PROTECT Console', N'https://protect.eset.com/', 3),
        (N'email2phone', N'Email2Phone', N'https://user.email2phone.net/client/#/authentication/signin', 4),
        (N'godaddy-dns', N'GoDaddy', N'https://dcc.godaddy.com/control/portfolio', 10),
        (N'network-solutions-dns', N'Network Solutions', N'https://www.networksolutions.com/my-account/login', 11);

    DECLARE @DefaultTemplates TABLE
    (
        [Name] nvarchar(160) NOT NULL PRIMARY KEY,
        [Category] nvarchar(160) NOT NULL,
        [TemplateText] nvarchar(max) NOT NULL
    );

    INSERT INTO @DefaultTemplates([Name], [Category], [TemplateText])
    VALUES
        (
            N'Exchange certificate update',
            N'Microsoft 365',
            N'Updated Exchange certificate binding, verified mail flow, and confirmed Outlook connectivity.'
        ),
        (
            N'VPN troubleshooting',
            N'Network',
            N'Investigated VPN connection failure, validated credentials and MFA status, reviewed client logs, and confirmed successful reconnect.'
        ),
        (
            N'Microsoft 365 licensing',
            N'Microsoft 365',
            N'Reviewed Microsoft 365 license assignment, adjusted user licensing, and confirmed service availability.'
        ),
        (
            N'Firewall rule change',
            N'Network',
            N'Reviewed requested firewall rule change, validated source and destination scope, applied the rule, and confirmed expected traffic.'
        ),
        (
            N'Password reset',
            N'Help Desk',
            N'Reset user password, confirmed MFA status, and verified successful sign-in with the user.'
        ),
        (
            N'Backup verification',
            N'Infrastructure',
            N'Reviewed backup job status, checked warnings or failures, and documented restore-point availability.'
        ),
        (
            N'Server reboot/maintenance',
            N'Infrastructure',
            N'Performed scheduled server maintenance, rebooted services as needed, and verified post-maintenance availability.'
        );

    BEGIN TRY
        BEGIN TRANSACTION;

        /* Range-update locking serializes concurrent first-run initialization. */
        DECLARE @ExistingOrganizationLinkCount int;
        SELECT @ExistingOrganizationLinkCount = COUNT(*)
        FROM [tb_data].[CommonLinks] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ScopeType] = N'Organization';

        DECLARE @BuiltInKey nvarchar(120);
        DECLARE @Name nvarchar(160);
        DECLARE @Url nvarchar(2048);
        DECLARE @SortOrder int;
        DECLARE @LinkId int;

        DECLARE DefaultLinkCursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT [BuiltInKey], [Name], [Url], [SortOrder]
        FROM @DefaultLinks
        ORDER BY [SortOrder], [BuiltInKey];

        OPEN DefaultLinkCursor;
        FETCH NEXT FROM DefaultLinkCursor
        INTO @BuiltInKey, @Name, @Url, @SortOrder;

        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @LinkId = NULL;

            SELECT TOP (1) @LinkId = [Id]
            FROM [tb_data].[CommonLinks] WITH (UPDLOCK, HOLDLOCK)
            WHERE [BuiltInKey] = @BuiltInKey
               OR
               (
                   [ScopeType] = N'Organization'
                   AND [Url] = @Url
               )
            ORDER BY
                CASE WHEN [BuiltInKey] = @BuiltInKey THEN 0 ELSE 1 END,
                [Id];

            IF @LinkId IS NULL
            BEGIN
                INSERT INTO [tb_data].[CommonLinks]
                (
                    [ScopeType],
                    [OwnerWindowsSid],
                    [Name],
                    [Url],
                    [UrlHash],
                    [SortOrder],
                    [BuiltInKey],
                    [CreatedByWindowsSid],
                    [UpdatedByWindowsSid],
                    [CreatedAtUtc],
                    [UpdatedAtUtc]
                )
                VALUES
                (
                    N'Organization',
                    NULL,
                    @Name,
                    @Url,
                    CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), @Url))),
                    @SortOrder,
                    @BuiltInKey,
                    @UserSid,
                    @UserSid,
                    @NowUtc,
                    @NowUtc
                );

                SET @ChangedCount += 1;
            END
            ELSE IF EXISTS
            (
                SELECT 1
                FROM [tb_data].[CommonLinks]
                WHERE [Id] = @LinkId
                  AND
                  (
                      [ScopeType] <> N'Organization'
                      OR [OwnerWindowsSid] IS NOT NULL
                      OR [Name] <> @Name
                      OR [Url] <> @Url
                      OR [UrlHash] <>
                         CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), @Url)))
                      OR [SortOrder] <> @SortOrder
                      OR [BuiltInKey] IS NULL
                      OR [BuiltInKey] <> @BuiltInKey
                  )
            )
            BEGIN
                UPDATE [tb_data].[CommonLinks]
                SET
                    [ScopeType] = N'Organization',
                    [OwnerWindowsSid] = NULL,
                    [Name] = @Name,
                    [Url] = @Url,
                    [UrlHash] =
                        CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), @Url))),
                    [SortOrder] = @SortOrder,
                    [BuiltInKey] = @BuiltInKey,
                    [UpdatedByWindowsSid] = @UserSid,
                    [UpdatedAtUtc] = @NowUtc
                WHERE [Id] = @LinkId;

                SET @ChangedCount += 1;
            END;

            FETCH NEXT FROM DefaultLinkCursor
            INTO @BuiltInKey, @Name, @Url, @SortOrder;
        END;

        CLOSE DefaultLinkCursor;
        DEALLOCATE DefaultLinkCursor;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[Templates] WITH (UPDLOCK, HOLDLOCK)
            WHERE [ScopeType] = N'Organization'
        )
        BEGIN
            INSERT INTO [tb_data].[Templates]
            (
                [ScopeType],
                [OwnerWindowsSid],
                [Name],
                [Category],
                [TemplateText],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            SELECT
                N'Organization',
                NULL,
                default_template.[Name],
                default_template.[Category],
                default_template.[TemplateText],
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            FROM @DefaultTemplates AS default_template;

            SET @ChangedCount += @@ROWCOUNT;
        END;

        IF @ChangedCount > 0
        BEGIN
            EXEC [tb_security].[WriteAuditEvent]
                @Action = N'WorkspaceDefaultsEnsured',
                @EntityType = N'WorkspaceDefaults',
                @EntityId = N'Organization';
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF CURSOR_STATUS(N'local', N'DefaultLinkCursor') >= 0
            CLOSE DefaultLinkCursor;
        IF CURSOR_STATUS(N'local', N'DefaultLinkCursor') > -3
            DEALLOCATE DefaultLinkCursor;
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_app.GetTemplates', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetTemplates];
GO

CREATE PROCEDURE [tb_app].[GetTemplates]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT
        [Id],
        [ScopeType],
        [Name],
        [Category],
        [TemplateText],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[Templates]
    WHERE [ScopeType] = N'Organization'
       OR ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
    ORDER BY
        CASE WHEN [ScopeType] = N'User' THEN 0 ELSE 1 END,
        [Category],
        [Name],
        [Id];
END;
GO

IF OBJECT_ID(N'tb_app.SaveTemplate', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveTemplate];
GO

CREATE PROCEDURE [tb_app].[SaveTemplate]
    @Id int = NULL,
    @ScopeType nvarchar(20) = N'User',
    @Name nvarchar(160),
    @Category nvarchar(160) = N'',
    @TemplateText nvarchar(max) = N'',
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @ScopeType =
        COALESCE(NULLIF(LTRIM(RTRIM(@ScopeType)), N''), N'User');
    SET @Name = NULLIF(LTRIM(RTRIM(@Name)), N'');
    SET @Category = COALESCE(LTRIM(RTRIM(@Category)), N'');
    SET @TemplateText = COALESCE(@TemplateText, N'');

    IF @ScopeType NOT IN (N'User', N'Organization')
        THROW 51200, N'Template ScopeType must be User or Organization.', 1;
    IF @ScopeType = N'Organization' AND @IsAdmin <> 1
        THROW 51201, N'Only an Admin may save organization templates.', 1;
    IF @Name IS NULL
        THROW 51200, N'Template name is required.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51202, N'ExpectedRowVersion is required when updating a template.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @Action nvarchar(120);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[Templates]
            (
                [ScopeType],
                [OwnerWindowsSid],
                [Name],
                [Category],
                [TemplateText],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @ScopeType,
                CASE WHEN @ScopeType = N'User' THEN @UserSid ELSE NULL END,
                @Name,
                @Category,
                @TemplateText,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );

            SET @Id = CONVERT(int, SCOPE_IDENTITY());
            SET @Action = N'TemplateCreated';
        END
        ELSE
        BEGIN
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[Templates]
                WHERE [Id] = @Id
                  AND
                  (
                      ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
                      OR ([ScopeType] = N'Organization' AND @IsAdmin = 1)
                  )
            )
                THROW 51203, N'The template does not exist or cannot be edited by the current user.', 1;

            UPDATE [tb_data].[Templates]
            SET
                [ScopeType] = @ScopeType,
                [OwnerWindowsSid] =
                    CASE WHEN @ScopeType = N'User' THEN @UserSid ELSE NULL END,
                [Name] = @Name,
                [Category] = @Category,
                [TemplateText] = @TemplateText,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51204, N'The template changed after it was loaded.', 1;
            SET @Action = N'TemplateUpdated';
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
            @EntityType = N'Template',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [ScopeType],
        [Name],
        [Category],
        [TemplateText],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[Templates]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteTemplate', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteTemplate];
GO

CREATE PROCEDURE [tb_app].[DeleteTemplate]
    @Id int,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    DELETE FROM [tb_data].[Templates]
    WHERE [Id] = @Id
      AND [RowVersion] = @ExpectedRowVersion
      AND
      (
          ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
          OR ([ScopeType] = N'Organization' AND @IsAdmin = 1)
      );

    IF @@ROWCOUNT = 0
        THROW 51205, N'The template was not found, changed, or cannot be deleted by the current user.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'TemplateDeleted',
        @EntityType = N'Template',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.GetCommonLinks', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetCommonLinks];
GO

CREATE PROCEDURE [tb_app].[GetCommonLinks]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT
        [Id],
        [ScopeType],
        [Name],
        [Url],
        [SortOrder],
        [BuiltInKey],
        [CreatedAtUtc] AS [CreatedAt],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[CommonLinks]
    WHERE [ScopeType] = N'Organization'
       OR ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
    ORDER BY
        CASE WHEN [ScopeType] = N'Organization' THEN 0 ELSE 1 END,
        [SortOrder],
        [Name],
        [Id];
END;
GO

IF OBJECT_ID(N'tb_app.SaveCommonLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveCommonLink];
GO

CREATE PROCEDURE [tb_app].[SaveCommonLink]
    @Id int = NULL,
    @ScopeType nvarchar(20) = N'User',
    @Name nvarchar(160),
    @Url nvarchar(2048),
    @SortOrder int = 0,
    @BuiltInKey nvarchar(120) = NULL,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @ScopeType =
        COALESCE(NULLIF(LTRIM(RTRIM(@ScopeType)), N''), N'User');
    SET @Name = NULLIF(LTRIM(RTRIM(@Name)), N'');
    SET @Url = NULLIF(LTRIM(RTRIM(@Url)), N'');
    SET @BuiltInKey = NULLIF(LTRIM(RTRIM(@BuiltInKey)), N'');

    IF @ScopeType NOT IN (N'User', N'Organization')
        THROW 51210, N'Common-link ScopeType must be User or Organization.', 1;
    IF @ScopeType = N'Organization' AND @IsAdmin <> 1
        THROW 51211, N'Only an Admin may save organization Common Links.', 1;
    IF @BuiltInKey IS NOT NULL AND @IsAdmin <> 1
        THROW 51211, N'Only an Admin may assign a built-in Common Link key.', 1;
    IF @Name IS NULL OR @Url IS NULL
        THROW 51210, N'Common-link name and URL are required.', 1;
    IF @Id IS NOT NULL
       AND EXISTS
       (
           SELECT 1
           FROM [tb_data].[CommonLinks]
           WHERE [Id] = @Id
             AND [BuiltInKey] IS NOT NULL
       )
        THROW 51216, N'Built-in Common Links cannot be changed.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51212, N'ExpectedRowVersion is required when updating a Common Link.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @UrlHash binary(32) =
        CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), @Url)));

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[CommonLinks]
            (
                [ScopeType],
                [OwnerWindowsSid],
                [Name],
                [Url],
                [UrlHash],
                [SortOrder],
                [BuiltInKey],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @ScopeType,
                CASE WHEN @ScopeType = N'User' THEN @UserSid ELSE NULL END,
                @Name,
                @Url,
                @UrlHash,
                @SortOrder,
                @BuiltInKey,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );
            SET @Id = CONVERT(int, SCOPE_IDENTITY());
        END
        ELSE
        BEGIN
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[CommonLinks]
                WHERE [Id] = @Id
                  AND
                  (
                      ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
                      OR ([ScopeType] = N'Organization' AND @IsAdmin = 1)
                  )
            )
                THROW 51213, N'The Common Link does not exist or cannot be edited by the current user.', 1;

            UPDATE [tb_data].[CommonLinks]
            SET
                [ScopeType] = @ScopeType,
                [OwnerWindowsSid] =
                    CASE WHEN @ScopeType = N'User' THEN @UserSid ELSE NULL END,
                [Name] = @Name,
                [Url] = @Url,
                [UrlHash] = @UrlHash,
                [SortOrder] = @SortOrder,
                [BuiltInKey] = @BuiltInKey,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51214, N'The Common Link changed after it was loaded.', 1;
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'CommonLinkSaved',
            @EntityType = N'CommonLink',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [ScopeType],
        [Name],
        [Url],
        [SortOrder],
        [BuiltInKey],
        [CreatedAtUtc] AS [CreatedAt],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[CommonLinks]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteCommonLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteCommonLink];
GO

CREATE PROCEDURE [tb_app].[DeleteCommonLink]
    @Id int,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF EXISTS
    (
        SELECT 1
        FROM [tb_data].[CommonLinks]
        WHERE [Id] = @Id
          AND [BuiltInKey] IS NOT NULL
    )
        THROW 51216, N'Built-in Common Links cannot be removed.', 1;

    DELETE FROM [tb_data].[CommonLinks]
    WHERE [Id] = @Id
      AND [RowVersion] = @ExpectedRowVersion
      AND
      (
          ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
          OR ([ScopeType] = N'Organization' AND @IsAdmin = 1)
      );

    IF @@ROWCOUNT = 0
        THROW 51215, N'The Common Link was not found, changed, or cannot be deleted by the current user.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'CommonLinkDeleted',
        @EntityType = N'CommonLink',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.GetSettings', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetSettings];
GO

CREATE PROCEDURE [tb_app].[GetSettings]
    @ScopeType nvarchar(40) = NULL,
    @DeviceId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    ;WITH settings AS
    (
        SELECT
            CONVERT(nvarchar(20), N'Organization') AS [ScopeType],
            [SettingKey],
            [SettingValue],
            [UpdatedAtUtc],
            [RowVersion],
            CONVERT(int, 1) AS [ScopePriority]
        FROM [tb_data].[OrganizationSettings]

        UNION ALL

        SELECT
            CONVERT(nvarchar(20), N'User') AS [ScopeType],
            [SettingKey],
            [SettingValue],
            [UpdatedAtUtc],
            [RowVersion],
            CONVERT(int, 2) AS [ScopePriority]
        FROM [tb_user].[UserSettings]
        WHERE [OwnerWindowsSid] = @UserSid
    ),
    ranked AS
    (
        SELECT
            [ScopeType],
            [SettingKey],
            [SettingValue],
            [UpdatedAtUtc],
            [RowVersion],
            ROW_NUMBER() OVER
            (
                PARTITION BY [SettingKey]
                ORDER BY [ScopePriority] DESC
            ) AS [Rank]
        FROM settings
    )
    SELECT
        [ScopeType],
        [SettingKey],
        [SettingValue],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM ranked
    WHERE [Rank] = 1
    ORDER BY [SettingKey];
END;
GO

IF OBJECT_ID(N'tb_app.SaveUserSetting', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveUserSetting];
GO

CREATE PROCEDURE [tb_app].[SaveUserSetting]
    @SettingKey nvarchar(200),
    @SettingValue nvarchar(max),
    @ExpectedRowVersion binary(8) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @SettingKey = NULLIF(LTRIM(RTRIM(@SettingKey)), N'');
    SET @SettingValue = COALESCE(@SettingValue, N'');
    IF @SettingKey IS NULL
        THROW 51220, N'SettingKey is required.', 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF EXISTS
        (
            SELECT 1
            FROM [tb_user].[UserSettings] WITH (UPDLOCK, HOLDLOCK)
            WHERE [OwnerWindowsSid] = @UserSid
              AND [SettingKey] = @SettingKey
        )
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 51221, N'ExpectedRowVersion is required for an existing user setting.', 1;

            UPDATE [tb_user].[UserSettings]
            SET
                [SettingValue] = @SettingValue,
                [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [OwnerWindowsSid] = @UserSid
              AND [SettingKey] = @SettingKey
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51222, N'The user setting changed after it was loaded.', 1;
        END
        ELSE
        BEGIN
            IF @ExpectedRowVersion IS NOT NULL
                THROW 51222, N'The user setting changed after it was loaded.', 1;

            INSERT INTO [tb_user].[UserSettings]
            (
                [OwnerWindowsSid],
                [SettingKey],
                [SettingValue]
            )
            VALUES
            (
                @UserSid,
                @SettingKey,
                @SettingValue
            );
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        CONVERT(nvarchar(20), N'User') AS [ScopeType],
        [SettingKey],
        [SettingValue],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_user].[UserSettings]
    WHERE [OwnerWindowsSid] = @UserSid
      AND [SettingKey] = @SettingKey;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteUserSetting', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteUserSetting];
GO

CREATE PROCEDURE [tb_app].[DeleteUserSetting]
    @SettingKey nvarchar(200),
    @ExpectedRowVersion binary(8) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    DELETE FROM [tb_user].[UserSettings]
    WHERE [OwnerWindowsSid] = @UserSid
      AND [SettingKey] = @SettingKey
      AND (@ExpectedRowVersion IS NULL OR [RowVersion] = @ExpectedRowVersion);
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveOrganizationSetting', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveOrganizationSetting];
GO

CREATE PROCEDURE [tb_app].[AdminSaveOrganizationSetting]
    @SettingKey nvarchar(200),
    @SettingValue nvarchar(max),
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51223, N'Only an Admin may save organization settings.', 1;

    SET @SettingKey = NULLIF(LTRIM(RTRIM(@SettingKey)), N'');
    SET @SettingValue = COALESCE(@SettingValue, N'');
    IF @SettingKey IS NULL
        THROW 51220, N'SettingKey is required.', 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF EXISTS
        (
            SELECT 1
            FROM [tb_data].[OrganizationSettings] WITH (UPDLOCK, HOLDLOCK)
            WHERE [SettingKey] = @SettingKey
        )
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 51221, N'ExpectedRowVersion is required for an existing organization setting.', 1;

            UPDATE [tb_data].[OrganizationSettings]
            SET
                [SettingValue] = @SettingValue,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [SettingKey] = @SettingKey
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51222, N'The organization setting changed after it was loaded.', 1;
        END
        ELSE
        BEGIN
            IF @ExpectedRowVersion IS NOT NULL
                THROW 51222, N'The organization setting changed after it was loaded.', 1;

            INSERT INTO [tb_data].[OrganizationSettings]
            (
                [SettingKey],
                [SettingValue],
                [UpdatedByWindowsSid]
            )
            VALUES
            (
                @SettingKey,
                @SettingValue,
                @UserSid
            );
        END;

        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'OrganizationSettingSaved',
            @EntityType = N'OrganizationSetting',
            @EntityId = @SettingKey,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        CONVERT(nvarchar(20), N'Organization') AS [ScopeType],
        [SettingKey],
        [SettingValue],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[OrganizationSettings]
    WHERE [SettingKey] = @SettingKey;
END;
GO

IF OBJECT_ID(N'tb_app.AdminDeleteOrganizationSetting', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminDeleteOrganizationSetting];
GO

CREATE PROCEDURE [tb_app].[AdminDeleteOrganizationSetting]
    @SettingKey nvarchar(200),
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51223, N'Only an Admin may delete organization settings.', 1;

    DELETE FROM [tb_data].[OrganizationSettings]
    WHERE [SettingKey] = @SettingKey
      AND (@ExpectedRowVersion IS NULL OR [RowVersion] = @ExpectedRowVersion);

    IF @@ROWCOUNT > 0
    BEGIN
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'OrganizationSettingDeleted',
            @EntityType = N'OrganizationSetting',
            @EntityId = @SettingKey,
            @RequestId = @RequestId;
    END;
END;
GO

IF OBJECT_ID(N'tb_app.GetClientAliases', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetClientAliases];
GO

CREATE PROCEDURE [tb_app].[GetClientAliases]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    ;WITH aliases AS
    (
        SELECT
            [Id],
            [ScopeType],
            [Alias],
            [ClientId],
            [UpdatedAtUtc],
            [RowVersion],
            CASE WHEN [ScopeType] = N'User' THEN 2 ELSE 1 END AS [ScopePriority]
        FROM [tb_data].[ClientAliases]
        WHERE [ScopeType] = N'Organization'
           OR ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
    ),
    ranked AS
    (
        SELECT
            [Id],
            [ScopeType],
            [Alias],
            [ClientId],
            [UpdatedAtUtc],
            [RowVersion],
            ROW_NUMBER() OVER
            (
                PARTITION BY [Alias]
                ORDER BY [ScopePriority] DESC, [Id] DESC
            ) AS [Rank]
        FROM aliases
    )
    SELECT
        [Id],
        [ScopeType],
        [Alias],
        [ClientId],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM ranked
    WHERE [Rank] = 1
    ORDER BY [Alias];
END;
GO

IF OBJECT_ID(N'tb_app.SaveClientAlias', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveClientAlias];
GO

CREATE PROCEDURE [tb_app].[SaveClientAlias]
    @Id bigint = NULL,
    @ScopeType nvarchar(20) = N'User',
    @Alias nvarchar(240),
    @ClientId int,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @ScopeType =
        COALESCE(NULLIF(LTRIM(RTRIM(@ScopeType)), N''), N'User');
    SET @Alias = NULLIF(LTRIM(RTRIM(@Alias)), N'');

    IF @ScopeType NOT IN (N'User', N'Organization')
        THROW 51230, N'Client-alias ScopeType must be User or Organization.', 1;
    IF @ScopeType = N'Organization' AND @IsAdmin <> 1
        THROW 51231, N'Only an Admin may save organization client aliases.', 1;
    IF @Alias IS NULL
        THROW 51230, N'Client alias is required.', 1;
    IF NOT EXISTS (SELECT 1 FROM [tb_data].[Clients] WHERE [Id] = @ClientId)
        THROW 51230, N'The selected client does not exist.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51232, N'ExpectedRowVersion is required when updating a client alias.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[ClientAliases]
            (
                [ScopeType],
                [OwnerWindowsSid],
                [Alias],
                [ClientId],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @ScopeType,
                CASE WHEN @ScopeType = N'User' THEN @UserSid ELSE NULL END,
                @Alias,
                @ClientId,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );
            SET @Id = CONVERT(bigint, SCOPE_IDENTITY());
        END
        ELSE
        BEGIN
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[ClientAliases]
                WHERE [Id] = @Id
                  AND
                  (
                      ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
                      OR ([ScopeType] = N'Organization' AND @IsAdmin = 1)
                  )
            )
                THROW 51233, N'The client alias does not exist or cannot be edited by the current user.', 1;

            UPDATE [tb_data].[ClientAliases]
            SET
                [ScopeType] = @ScopeType,
                [OwnerWindowsSid] =
                    CASE WHEN @ScopeType = N'User' THEN @UserSid ELSE NULL END,
                [Alias] = @Alias,
                [ClientId] = @ClientId,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51234, N'The client alias changed after it was loaded.', 1;
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'ClientAliasSaved',
            @EntityType = N'ClientAlias',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [ScopeType],
        [Alias],
        [ClientId],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[ClientAliases]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteClientAlias', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteClientAlias];
GO

CREATE PROCEDURE [tb_app].[DeleteClientAlias]
    @Id bigint,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    DELETE FROM [tb_data].[ClientAliases]
    WHERE [Id] = @Id
      AND [RowVersion] = @ExpectedRowVersion
      AND
      (
          ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
          OR ([ScopeType] = N'Organization' AND @IsAdmin = 1)
      );

    IF @@ROWCOUNT = 0
        THROW 51235, N'The client alias was not found, changed, or cannot be deleted by the current user.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'ClientAliasDeleted',
        @EntityType = N'ClientAlias',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.GetClientExternalIdentities', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetClientExternalIdentities];
GO

CREATE PROCEDURE [tb_app].[GetClientExternalIdentities]
    @ClientId int = NULL,
    @SourceSystem nvarchar(40) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT
        [Id],
        [ClientId],
        [SourceSystem],
        [ExternalId],
        [ExternalName],
        [LastSyncedAtUtc] AS [LastSyncedAt],
        [RowVersion]
    FROM [tb_data].[ClientExternalIdentities]
    WHERE (@ClientId IS NULL OR [ClientId] = @ClientId)
      AND (@SourceSystem IS NULL OR [SourceSystem] = @SourceSystem)
    ORDER BY [ClientId], [SourceSystem], [ExternalId];
END;
GO

IF OBJECT_ID(N'tb_app.SyncUpsertClientExternalIdentity', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncUpsertClientExternalIdentity];
GO

CREATE PROCEDURE [tb_app].[SyncUpsertClientExternalIdentity]
    @ClientId int,
    @SourceSystem nvarchar(40),
    @ExternalId nvarchar(500),
    @ExternalName nvarchar(240) = NULL,
    @LastSyncedAtUtc datetime2(3) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 AND @IsSyncOperator <> 1
        THROW 51240, N'Only an Admin or Sync Operator may save external client identities.', 1;

    SET @SourceSystem = NULLIF(LTRIM(RTRIM(@SourceSystem)), N'');
    SET @ExternalId = NULLIF(LTRIM(RTRIM(@ExternalId)), N'');
    SET @ExternalName = NULLIF(LTRIM(RTRIM(@ExternalName)), N'');
    IF @SourceSystem IS NULL OR @ExternalId IS NULL
        THROW 51241, N'SourceSystem and ExternalId are required.', 1;
    IF NOT EXISTS (SELECT 1 FROM [tb_data].[Clients] WHERE [Id] = @ClientId)
        THROW 51241, N'The selected client does not exist.', 1;

    DECLARE @Id bigint;
    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @Id = [Id]
        FROM [tb_data].[ClientExternalIdentities] WITH (UPDLOCK, HOLDLOCK)
        WHERE [SourceSystem] = @SourceSystem
          AND [ExternalId] = @ExternalId;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[ClientExternalIdentities]
            (
                [ClientId],
                [SourceSystem],
                [ExternalId],
                [ExternalName],
                [LastSyncedAtUtc],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @ClientId,
                @SourceSystem,
                @ExternalId,
                @ExternalName,
                @LastSyncedAtUtc,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );
            SET @Id = CONVERT(bigint, SCOPE_IDENTITY());
        END
        ELSE
        BEGIN
            UPDATE [tb_data].[ClientExternalIdentities]
            SET
                [ClientId] = @ClientId,
                [ExternalName] = @ExternalName,
                [LastSyncedAtUtc] = @LastSyncedAtUtc,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [ClientId],
        [SourceSystem],
        [ExternalId],
        [ExternalName],
        [LastSyncedAtUtc] AS [LastSyncedAt],
        [RowVersion]
    FROM [tb_data].[ClientExternalIdentities]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.SyncUpsertTicketStatusOption', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncUpsertTicketStatusOption];
GO

CREATE PROCEDURE [tb_app].[SyncUpsertTicketStatusOption]
    @Name nvarchar(160),
    @Source nvarchar(40) = N'WHD',
    @ExternalId nvarchar(240) = NULL,
    @WhdStatusTypeId int = NULL,
    @IsClosed bit = 0,
    @SyncedAtUtc datetime2(3) = NULL,
    @LastSyncedAtUtc datetime2(3) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    /* @LastSyncedAtUtc is the desktop repository contract. Keep
       @SyncedAtUtc as the snapshot/deployment compatibility alias. */
    SET @SyncedAtUtc = COALESCE(@LastSyncedAtUtc, @SyncedAtUtc);

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 AND @IsSyncOperator <> 1
        THROW 51250, N'Only an Admin or Sync Operator may synchronize ticket statuses.', 1;

    SET @Name = NULLIF(LTRIM(RTRIM(@Name)), N'');
    SET @Source = COALESCE(NULLIF(LTRIM(RTRIM(@Source)), N''), N'WHD');
    SET @ExternalId = NULLIF(LTRIM(RTRIM(@ExternalId)), N'');
    IF @Name IS NULL
        THROW 51251, N'Ticket-status name is required.', 1;

    DECLARE @Id int;
    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @Id = [Id]
        FROM [tb_data].[TicketStatusOptions] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Source] = @Source
          AND
          (
              ([ExternalId] = @ExternalId)
              OR (@ExternalId IS NULL AND [ExternalId] IS NULL AND [Name] = @Name)
          );

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[TicketStatusOptions]
            (
                [Name],
                [Source],
                [ExternalId],
                [WhdStatusTypeId],
                [IsClosed],
                [LastSyncedAtUtc],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @Name,
                @Source,
                @ExternalId,
                @WhdStatusTypeId,
                @IsClosed,
                @SyncedAtUtc,
                @NowUtc,
                @NowUtc
            );
            SET @Id = CONVERT(int, SCOPE_IDENTITY());
        END
        ELSE
        BEGIN
            UPDATE [tb_data].[TicketStatusOptions]
            SET
                [Name] = @Name,
                [WhdStatusTypeId] = @WhdStatusTypeId,
                [IsClosed] = @IsClosed,
                [LastSyncedAtUtc] = @SyncedAtUtc,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [Name],
        [Source],
        [ExternalId],
        [WhdStatusTypeId],
        [IsClosed],
        [LastSyncedAtUtc] AS [LastSyncedAt],
        [RowVersion]
    FROM [tb_data].[TicketStatusOptions]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.SyncUpsertTicket', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncUpsertTicket];
GO

CREATE PROCEDURE [tb_app].[SyncUpsertTicket]
    @TicketNumber nvarchar(120),
    @ClientId int,
    @Subject nvarchar(500) = N'',
    @Status nvarchar(160) = N'Open',
    @Source nvarchar(40) = N'WHD',
    @ExternalId nvarchar(240) = NULL,
    @WhdStatusTypeId int = NULL,
    @IsClosed bit = 0,
    @SyncedAtUtc datetime2(3) = NULL,
    @LastSyncedAtUtc datetime2(3) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    /* @LastSyncedAtUtc is the desktop repository contract. Keep
       @SyncedAtUtc as the snapshot/deployment compatibility alias. */
    SET @SyncedAtUtc = COALESCE(@LastSyncedAtUtc, @SyncedAtUtc);

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 AND @IsSyncOperator <> 1
        THROW 51260, N'Only an Admin or Sync Operator may synchronize tickets.', 1;

    SET @TicketNumber = NULLIF(LTRIM(RTRIM(@TicketNumber)), N'');
    SET @Subject = COALESCE(LTRIM(RTRIM(@Subject)), N'');
    SET @Status = COALESCE(NULLIF(LTRIM(RTRIM(@Status)), N''), N'Open');
    SET @Source = COALESCE(NULLIF(LTRIM(RTRIM(@Source)), N''), N'WHD');
    SET @ExternalId = NULLIF(LTRIM(RTRIM(@ExternalId)), N'');

    IF @TicketNumber IS NULL
        THROW 51261, N'TicketNumber is required.', 1;
    IF NOT EXISTS (SELECT 1 FROM [tb_data].[Clients] WHERE [Id] = @ClientId)
        THROW 51261, N'The selected client does not exist.', 1;

    DECLARE @Id int;
    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @Id = [Id]
        FROM [tb_data].[Tickets] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Source] = @Source
          AND
          (
              ([ExternalId] = @ExternalId)
              OR (@ExternalId IS NULL AND [ExternalId] IS NULL
                  AND [TicketNumber] = @TicketNumber)
          );

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[Tickets]
            (
                [TicketNumber],
                [ClientId],
                [Subject],
                [Status],
                [Source],
                [ExternalId],
                [WhdStatusTypeId],
                [IsClosed],
                [LastSyncedAtUtc],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @TicketNumber,
                @ClientId,
                @Subject,
                @Status,
                @Source,
                @ExternalId,
                @WhdStatusTypeId,
                @IsClosed,
                @SyncedAtUtc,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );
            SET @Id = CONVERT(int, SCOPE_IDENTITY());
        END
        ELSE
        BEGIN
            UPDATE [tb_data].[Tickets]
            SET
                [TicketNumber] = @TicketNumber,
                [ClientId] = @ClientId,
                [Subject] = @Subject,
                [Status] = @Status,
                [WhdStatusTypeId] = @WhdStatusTypeId,
                [IsClosed] = @IsClosed,
                [LastSyncedAtUtc] = @SyncedAtUtc,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [TicketNumber],
        [ClientId],
        [Subject],
        [Status],
        [Source],
        [ExternalId],
        [WhdStatusTypeId],
        [IsClosed],
        [LastSyncedAtUtc] AS [LastSyncedAt],
        [RowVersion]
    FROM [tb_data].[Tickets]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.AdminMergeClients', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminMergeClients];
GO

CREATE PROCEDURE [tb_app].[AdminMergeClients]
    @TargetClientId int = NULL,
    @SourceClientId int = NULL,
    @ExpectedTargetRowVersion binary(8) = NULL,
    @ExpectedSourceRowVersion binary(8) = NULL,
    @WhdClientId int = NULL,
    @SageClientId int = NULL,
    @ExpectedWhdRowVersion binary(8) = NULL,
    @ExpectedSageRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51270, N'Only an Admin may merge clients.', 1;

    SET @WhdClientId = COALESCE(@TargetClientId, @WhdClientId);
    SET @SageClientId = COALESCE(@SourceClientId, @SageClientId);
    SET @ExpectedWhdRowVersion =
        COALESCE(@ExpectedTargetRowVersion, @ExpectedWhdRowVersion);
    SET @ExpectedSageRowVersion =
        COALESCE(@ExpectedSourceRowVersion, @ExpectedSageRowVersion);

    IF @WhdClientId IS NULL
       OR @SageClientId IS NULL
       OR @ExpectedWhdRowVersion IS NULL
       OR @ExpectedSageRowVersion IS NULL
        THROW 51271, N'Both client IDs and expected row versions are required.', 1;
    IF @WhdClientId = @SageClientId
        THROW 51271, N'WhdClientId and SageClientId must be different.', 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[Clients] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @WhdClientId
              AND [RowVersion] = @ExpectedWhdRowVersion
        )
            THROW 51272, N'The target client changed or no longer exists.', 1;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[Clients] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @SageClientId
              AND [RowVersion] = @ExpectedSageRowVersion
        )
            THROW 51273, N'The source client changed or no longer exists.', 1;

        UPDATE target_client
        SET
            [Source] =
                CASE
                    WHEN target_client.[Source] = N'Both'
                      OR source_client.[Source] = N'Both'
                      OR
                      (
                          target_client.[Source] IN (N'WHD', N'Sage')
                          AND source_client.[Source] IN (N'WHD', N'Sage')
                          AND target_client.[Source] <> source_client.[Source]
                      )
                        THEN N'Both'
                    WHEN target_client.[Source] = N'Manual'
                        THEN source_client.[Source]
                    ELSE target_client.[Source]
                END,
            [IsActive] =
                CONVERT(
                    bit,
                    CASE
                        WHEN target_client.[IsActive] = 1
                          OR source_client.[IsActive] = 1
                            THEN 1
                        ELSE 0
                    END),
            [LastSyncedAtUtc] =
                CASE
                    WHEN target_client.[LastSyncedAtUtc] IS NULL
                        THEN source_client.[LastSyncedAtUtc]
                    WHEN source_client.[LastSyncedAtUtc] IS NULL
                        THEN target_client.[LastSyncedAtUtc]
                    WHEN source_client.[LastSyncedAtUtc] > target_client.[LastSyncedAtUtc]
                        THEN source_client.[LastSyncedAtUtc]
                    ELSE target_client.[LastSyncedAtUtc]
                END,
            [WhdLocationName] =
                COALESCE(target_client.[WhdLocationName], source_client.[WhdLocationName]),
            [WhdContactName] =
                COALESCE(target_client.[WhdContactName], source_client.[WhdContactName]),
            [SageCustomerId] =
                COALESCE(target_client.[SageCustomerId], source_client.[SageCustomerId]),
            [SageCustomerName] =
                COALESCE(target_client.[SageCustomerName], source_client.[SageCustomerName]),
            [SageContactName] =
                COALESCE(target_client.[SageContactName], source_client.[SageContactName]),
            [SageTelephone] =
                COALESCE(target_client.[SageTelephone], source_client.[SageTelephone]),
            [MatchStatus] = N'Matched',
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        FROM [tb_data].[Clients] AS target_client
        CROSS JOIN [tb_data].[Clients] AS source_client
        WHERE target_client.[Id] = @WhdClientId
          AND source_client.[Id] = @SageClientId;

        UPDATE [tb_data].[Tickets]
        SET
            [ClientId] = @WhdClientId,
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ClientId] = @SageClientId;

        UPDATE [tb_data].[WorkEntries]
        SET
            [ClientId] = @WhdClientId,
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ClientId] = @SageClientId;

        UPDATE [tb_data].[ClientAliases]
        SET
            [ClientId] = @WhdClientId,
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ClientId] = @SageClientId;

        DELETE source_identity
        FROM [tb_data].[ClientExternalIdentities] AS source_identity
        WHERE source_identity.[ClientId] = @SageClientId
          AND EXISTS
          (
              SELECT 1
              FROM [tb_data].[ClientExternalIdentities] AS target_identity
              WHERE target_identity.[ClientId] = @WhdClientId
                AND target_identity.[SourceSystem] = source_identity.[SourceSystem]
                AND target_identity.[ExternalId] = source_identity.[ExternalId]
          );

        UPDATE [tb_data].[ClientExternalIdentities]
        SET
            [ClientId] = @WhdClientId,
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ClientId] = @SageClientId;

        DELETE FROM [tb_data].[Clients]
        WHERE [Id] = @SageClientId
          AND [RowVersion] = @ExpectedSageRowVersion;

        IF @@ROWCOUNT = 0
            THROW 51273, N'The source client changed during the merge.', 1;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @WhdClientId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'ClientMerged',
            @EntityType = N'Client',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    EXEC [tb_app].[GetClient] @Id = @WhdClientId;
END;
GO

IF OBJECT_ID(N'tb_app.ReconcileClientMatches', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ReconcileClientMatches];
GO

CREATE PROCEDURE [tb_app].[ReconcileClientMatches]
    @Mode nvarchar(20) = N'Exact'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 AND @IsSyncOperator <> 1
        THROW 51280, N'Only an Admin or Sync Operator may reconcile client matches.', 1;

    SET @Mode = COALESCE(NULLIF(LTRIM(RTRIM(@Mode)), N''), N'Exact');
    IF @Mode NOT IN (N'Exact', N'Strong', N'Safe')
        THROW 51281, N'Mode must be Exact, Strong, or Safe.', 1;

    DECLARE @MatchedCount int = 0;

    IF @Mode = N'Exact'
    BEGIN
        UPDATE client
        SET
            [MatchStatus] = N'Matched',
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        FROM [tb_data].[Clients] AS client
        WHERE client.[MatchStatus] <> N'Matched'
          AND EXISTS
          (
              SELECT 1
              FROM [tb_data].[ClientExternalIdentities] AS whd_identity
              WHERE whd_identity.[ClientId] = client.[Id]
                AND whd_identity.[SourceSystem] = N'WHD'
          )
          AND EXISTS
          (
              SELECT 1
              FROM [tb_data].[ClientExternalIdentities] AS sage_identity
              WHERE sage_identity.[ClientId] = client.[Id]
                AND sage_identity.[SourceSystem] = N'Sage'
          );

        SET @MatchedCount = @@ROWCOUNT;
    END;

    SELECT
        @Mode AS [Mode],
        @MatchedCount AS [MatchedCount],
        CONVERT(bit, CASE WHEN @Mode = N'Exact' THEN 1 ELSE 0 END)
            AS [AutomaticChangesApplied];
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveTemplate', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveTemplate];
GO

CREATE PROCEDURE [tb_app].[AdminSaveTemplate]
    @Id int = NULL,
    @Name nvarchar(160),
    @Category nvarchar(160) = N'',
    @TemplateText nvarchar(max) = N'',
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    EXEC [tb_app].[SaveTemplate]
        @Id = @Id,
        @ScopeType = N'User',
        @Name = @Name,
        @Category = @Category,
        @TemplateText = @TemplateText,
        @ExpectedRowVersion = @ExpectedRowVersion,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.AdminDeleteTemplate', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminDeleteTemplate];
GO

CREATE PROCEDURE [tb_app].[AdminDeleteTemplate]
    @Id int,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    EXEC [tb_app].[DeleteTemplate]
        @Id = @Id,
        @ExpectedRowVersion = @ExpectedRowVersion,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveCommonLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveCommonLink];
GO

CREATE PROCEDURE [tb_app].[AdminSaveCommonLink]
    @Id int = NULL,
    @Name nvarchar(160),
    @Url nvarchar(2048),
    @SortOrder int = 0,
    @BuiltInKey nvarchar(120) = NULL,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    EXEC [tb_app].[SaveCommonLink]
        @Id = @Id,
        @ScopeType = N'User',
        @Name = @Name,
        @Url = @Url,
        @SortOrder = @SortOrder,
        @BuiltInKey = @BuiltInKey,
        @ExpectedRowVersion = @ExpectedRowVersion,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.AdminDeleteCommonLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminDeleteCommonLink];
GO

CREATE PROCEDURE [tb_app].[AdminDeleteCommonLink]
    @Id int,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    EXEC [tb_app].[DeleteCommonLink]
        @Id = @Id,
        @ExpectedRowVersion = @ExpectedRowVersion,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveClientAlias', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveClientAlias];
GO

CREATE PROCEDURE [tb_app].[AdminSaveClientAlias]
    @Alias nvarchar(240),
    @ClientId int,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;
    DECLARE @Id bigint;
    DECLARE @ExpectedRowVersion binary(8);

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT
        @Id = [Id],
        @ExpectedRowVersion = [RowVersion]
    FROM [tb_data].[ClientAliases]
    WHERE [ScopeType] = N'User'
      AND [OwnerWindowsSid] = @UserSid
      AND [Alias] = @Alias;

    EXEC [tb_app].[SaveClientAlias]
        @Id = @Id,
        @ScopeType = N'User',
        @Alias = @Alias,
        @ClientId = @ClientId,
        @ExpectedRowVersion = @ExpectedRowVersion,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.AdminDeleteClientAlias', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminDeleteClientAlias];
GO

CREATE PROCEDURE [tb_app].[AdminDeleteClientAlias]
    @Alias nvarchar(240),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;
    DECLARE @Id bigint;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT @Id = [Id]
    FROM [tb_data].[ClientAliases]
    WHERE [ScopeType] = N'User'
      AND [OwnerWindowsSid] = @UserSid
      AND [Alias] = @Alias;

    IF @Id IS NOT NULL
    BEGIN
        DELETE FROM [tb_data].[ClientAliases]
        WHERE [Id] = @Id
          AND [OwnerWindowsSid] = @UserSid;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'ClientAliasDeleted',
            @EntityType = N'ClientAlias',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;
    END;
END;
GO

IF OBJECT_ID(N'tb_app.SaveSetting', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveSetting];
GO

CREATE PROCEDURE [tb_app].[SaveSetting]
    @ScopeType nvarchar(40) = N'User',
    @SettingKey nvarchar(200),
    @SettingValue nvarchar(max),
    @DeviceId uniqueidentifier = NULL,
    @ExpectedRowVersion binary(8) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @ScopeType <> N'User'
        THROW 51224, N'The desktop SaveSetting contract supports user-scoped settings only.', 1;

    EXEC [tb_app].[SaveUserSetting]
        @SettingKey = @SettingKey,
        @SettingValue = @SettingValue,
        @ExpectedRowVersion = @ExpectedRowVersion;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteSetting', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteSetting];
GO

CREATE PROCEDURE [tb_app].[DeleteSetting]
    @ScopeType nvarchar(40) = N'User',
    @SettingKey nvarchar(200),
    @DeviceId uniqueidentifier = NULL,
    @ExpectedRowVersion binary(8) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @ScopeType <> N'User'
        THROW 51224, N'The desktop DeleteSetting contract supports user-scoped settings only.', 1;

    EXEC [tb_app].[DeleteUserSetting]
        @SettingKey = @SettingKey,
        @ExpectedRowVersion = @ExpectedRowVersion;
END;
GO

PRINT N'TechBench V0002 templates, links, settings, aliases, mapping, and shared-data procedures created.';
GO

-- ============================================================================
-- END 42-V0002-SharedProcedures.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 43-V0002-PostingProcedures.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'tb_app.AddPostingLog', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AddPostingLog];
GO

CREATE PROCEDURE [tb_app].[AddPostingLog]
    @WorkEntryId bigint,
    @Destination nvarchar(40),
    @Payload nvarchar(max) = N'',
    @Success bit,
    @Message nvarchar(max) = N'',
    @ExternalReference nvarchar(500) = NULL,
    @CreatedAtUtc datetime2(3) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @Destination = NULLIF(LTRIM(RTRIM(@Destination)), N'');
    IF @Destination NOT IN (N'WHD', N'Sage')
        THROW 51300, N'Destination must be WHD or Sage.', 1;
    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_data].[WorkEntries]
        WHERE [Id] = @WorkEntryId
          AND [OwnerWindowsSid] = @UserSid
    )
        THROW 51301, N'The work entry does not exist or is not owned by the current user.', 1;

    DECLARE @Id bigint;

    INSERT INTO [tb_ops].[PostingLogs]
    (
        [WorkEntryId],
        [OwnerWindowsSid],
        [Destination],
        [Payload],
        [Success],
        [Message],
        [ExternalReference],
        [RequestId],
        [CreatedAtUtc]
    )
    VALUES
    (
        CONVERT(int, @WorkEntryId),
        @UserSid,
        @Destination,
        COALESCE(@Payload, N''),
        @Success,
        COALESCE(@Message, N''),
        NULLIF(LTRIM(RTRIM(@ExternalReference)), N''),
        COALESCE(@RequestId, NEWID()),
        COALESCE(@CreatedAtUtc, SYSUTCDATETIME())
    );

    SET @Id = CONVERT(bigint, SCOPE_IDENTITY());

    SELECT
        [Id],
        [WorkEntryId],
        [Destination],
        [Payload],
        [Success],
        [Message],
        [ExternalReference],
        [CreatedAtUtc] AS [CreatedAt]
    FROM [tb_ops].[PostingLogs]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.GetLatestVerifiedWhdPostingLog', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetLatestVerifiedWhdPostingLog];
GO

CREATE PROCEDURE [tb_app].[GetLatestVerifiedWhdPostingLog]
    @WorkEntryId bigint
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT TOP (1)
        posting_log.[Id],
        posting_log.[WorkEntryId],
        posting_log.[Destination],
        posting_log.[Payload],
        posting_log.[Success],
        posting_log.[Message],
        posting_log.[ExternalReference],
        posting_log.[CreatedAtUtc] AS [CreatedAt]
    FROM [tb_ops].[PostingLogs] AS posting_log
    OUTER APPLY
    (
        SELECT TOP (1) CONVERT(bit, 1) AS [HasNoteText]
        FROM OPENJSON
        (
            CASE
                WHEN ISJSON(posting_log.[Payload]) = 1 THEN posting_log.[Payload]
                ELSE N'{}'
            END
        ) AS payload_property
        WHERE payload_property.[key] = N'noteText'
    ) AS note_payload
    WHERE posting_log.[WorkEntryId] = @WorkEntryId
      AND posting_log.[OwnerWindowsSid] = @UserSid
      AND posting_log.[Destination] = N'WHD'
      AND posting_log.[Success] = 1
      AND posting_log.[ExternalReference] LIKE N'WHD-TECHNOTE-%'
    ORDER BY
        CASE
            WHEN note_payload.[HasNoteText] = 1 THEN 0
            ELSE 1
        END,
        posting_log.[Id] DESC;
END;
GO

IF OBJECT_ID(N'tb_app.BeginPostingAttempt', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[BeginPostingAttempt];
GO

CREATE PROCEDURE [tb_app].[BeginPostingAttempt]
    @WorkEntryId bigint,
    @Destination nvarchar(40),
    @AttemptKey nvarchar(120),
    @PayloadHash char(64),
    @DeviceId uniqueidentifier = NULL,
    @LeaseSeconds int = 180
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @Destination = NULLIF(LTRIM(RTRIM(@Destination)), N'');
    SET @AttemptKey = NULLIF(LTRIM(RTRIM(@AttemptKey)), N'');
    SET @PayloadHash = NULLIF(LTRIM(RTRIM(@PayloadHash)), '');
    SET @LeaseSeconds =
        CASE
            WHEN @LeaseSeconds IS NULL OR @LeaseSeconds < 30 THEN 30
            WHEN @LeaseSeconds > 1800 THEN 1800
            ELSE @LeaseSeconds
        END;

    IF @Destination NOT IN (N'WHD', N'Sage')
        THROW 51310, N'Destination must be WHD or Sage.', 1;
    IF @AttemptKey IS NULL OR @PayloadHash IS NULL OR LEN(@PayloadHash) <> 64
        THROW 51310, N'AttemptKey and a 64-character PayloadHash are required.', 1;
    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @AttemptId bigint;
    DECLARE @LeaseToken uniqueidentifier;
    DECLARE @LeaseExpiresAtUtc datetime2(3);
    DECLARE @SagePosted bit;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @SagePosted = [SagePosted]
        FROM [tb_data].[WorkEntries] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Id] = @WorkEntryId
          AND [OwnerWindowsSid] = @UserSid;

        IF @SagePosted IS NULL
            THROW 51311, N'The work entry does not exist or is not owned by the current user.', 1;
        IF @SagePosted = 1
            THROW 51313, N'An entry already posted to Sage is permanently immutable.', 1;

        SELECT
            @AttemptId = posting_attempt.[Id]
        FROM [tb_ops].[PostingAttempts] AS posting_attempt WITH (UPDLOCK, HOLDLOCK)
        WHERE posting_attempt.[WorkEntryId] = @WorkEntryId
          AND posting_attempt.[Destination] = @Destination
          AND posting_attempt.[Status] IN (N'Started', N'Unknown');

        IF @AttemptId IS NOT NULL
        BEGIN
            IF EXISTS
            (
                SELECT 1
                FROM [tb_ops].[PostingLeases]
                WHERE [AttemptId] = @AttemptId
                  AND [ExpiresAtUtc] <= @NowUtc
            )
            BEGIN
                UPDATE [tb_ops].[PostingAttempts]
                SET
                    [Status] = N'Unknown',
                    [Message] =
                        N'The posting lease expired before an external outcome was confirmed.',
                    [CompletedAtUtc] = @NowUtc
                WHERE [Id] = @AttemptId
                  AND [Status] = N'Started';

                DELETE FROM [tb_ops].[PostingLeases]
                WHERE [AttemptId] = @AttemptId;
            END;

            COMMIT TRANSACTION;

            SELECT CONVERT(bit, 0) AS [Started];
            RETURN;
        END;

        DELETE posting_lease
        FROM [tb_ops].[PostingLeases] AS posting_lease WITH (UPDLOCK, HOLDLOCK)
        LEFT JOIN [tb_ops].[PostingAttempts] AS posting_attempt
            ON posting_attempt.[Id] = posting_lease.[AttemptId]
        WHERE posting_lease.[WorkEntryId] = @WorkEntryId
          AND posting_lease.[Destination] = @Destination
          AND
          (
              posting_attempt.[Id] IS NULL
              OR posting_attempt.[Status] NOT IN (N'Started', N'Unknown')
          );

        SET @LeaseToken = NEWID();
        SET @LeaseExpiresAtUtc = DATEADD(second, @LeaseSeconds, @NowUtc);

        INSERT INTO [tb_ops].[PostingAttempts]
        (
            [WorkEntryId],
            [OwnerWindowsSid],
            [DeviceId],
            [Destination],
            [AttemptKey],
            [PayloadHash],
            [Status],
            [Message],
            [StartedAtUtc]
        )
        VALUES
        (
            CONVERT(int, @WorkEntryId),
            @UserSid,
            @DeviceId,
            @Destination,
            @AttemptKey,
            @PayloadHash,
            N'Started',
            N'External posting started.',
            @NowUtc
        );

        SET @AttemptId = CONVERT(bigint, SCOPE_IDENTITY());

        INSERT INTO [tb_ops].[PostingLeases]
        (
            [WorkEntryId],
            [Destination],
            [AttemptId],
            [LeaseToken],
            [OwnerWindowsSid],
            [DeviceId],
            [AcquiredAtUtc],
            [HeartbeatAtUtc],
            [ExpiresAtUtc]
        )
        VALUES
        (
            CONVERT(int, @WorkEntryId),
            @Destination,
            @AttemptId,
            @LeaseToken,
            @UserSid,
            @DeviceId,
            @NowUtc,
            @NowUtc,
            @LeaseExpiresAtUtc
        );

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;

        IF ERROR_NUMBER() IN (2601, 2627)
        BEGIN
            SELECT CONVERT(bit, 0) AS [Started];
            RETURN;
        END;

        THROW;
    END CATCH;

    SELECT
        CONVERT(bit, 1) AS [Started],
        @AttemptId AS [AttemptId],
        @LeaseToken AS [LeaseToken],
        @LeaseExpiresAtUtc AS [LeaseExpiresAtUtc];
END;
GO

IF OBJECT_ID(N'tb_app.HeartbeatPostingAttempt', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[HeartbeatPostingAttempt];
GO

CREATE PROCEDURE [tb_app].[HeartbeatPostingAttempt]
    @AttemptId bigint,
    @LeaseToken uniqueidentifier,
    @LeaseSeconds int = 180
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @LeaseSeconds =
        CASE
            WHEN @LeaseSeconds IS NULL OR @LeaseSeconds < 30 THEN 30
            WHEN @LeaseSeconds > 1800 THEN 1800
            ELSE @LeaseSeconds
        END;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    UPDATE posting_lease
    SET
        [HeartbeatAtUtc] = @NowUtc,
        [ExpiresAtUtc] = DATEADD(second, @LeaseSeconds, @NowUtc)
    FROM [tb_ops].[PostingLeases] AS posting_lease
    INNER JOIN [tb_ops].[PostingAttempts] AS posting_attempt
        ON posting_attempt.[Id] = posting_lease.[AttemptId]
    WHERE posting_lease.[AttemptId] = @AttemptId
      AND posting_lease.[LeaseToken] = @LeaseToken
      AND posting_lease.[OwnerWindowsSid] = @UserSid
      AND posting_lease.[ExpiresAtUtc] > @NowUtc
      AND posting_attempt.[Status] = N'Started';

    IF @@ROWCOUNT = 0
        THROW 51312, N'The posting lease is missing, expired, or no longer owned by the current user.', 1;
END;
GO

IF OBJECT_ID(N'tb_app.GetOutstandingPostingAttempt', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetOutstandingPostingAttempt];
GO

CREATE PROCEDURE [tb_app].[GetOutstandingPostingAttempt]
    @WorkEntryId bigint,
    @Destination nvarchar(40)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT TOP (1)
        posting_attempt.[Id],
        posting_attempt.[WorkEntryId],
        posting_attempt.[Destination],
        posting_attempt.[AttemptKey],
        posting_attempt.[PayloadHash],
        posting_attempt.[Status],
        posting_attempt.[Message],
        posting_attempt.[ExternalReference],
        posting_attempt.[StartedAtUtc] AS [StartedAt],
        posting_attempt.[CompletedAtUtc] AS [CompletedAt],
        posting_attempt.[RowVersion],
        posting_lease.[LeaseToken],
        posting_lease.[ExpiresAtUtc] AS [LeaseExpiresAtUtc]
    FROM [tb_ops].[PostingAttempts] AS posting_attempt
    LEFT JOIN [tb_ops].[PostingLeases] AS posting_lease
        ON posting_lease.[AttemptId] = posting_attempt.[Id]
    WHERE posting_attempt.[WorkEntryId] = @WorkEntryId
      AND posting_attempt.[Destination] = @Destination
      AND posting_attempt.[OwnerWindowsSid] = @UserSid
      AND posting_attempt.[Status] IN (N'Started', N'Unknown')
    ORDER BY posting_attempt.[Id] DESC;
END;
GO

IF OBJECT_ID(N'tb_app.CompletePostingAttempt', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[CompletePostingAttempt];
GO

CREATE PROCEDURE [tb_app].[CompletePostingAttempt]
    @AttemptId bigint,
    @Status nvarchar(40),
    @Message nvarchar(max) = N'',
    @ExternalReference nvarchar(500) = NULL,
    @MarkPosted bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @Status = NULLIF(LTRIM(RTRIM(@Status)), N'');
    SET @ExternalReference = NULLIF(LTRIM(RTRIM(@ExternalReference)), N'');
    IF @Status NOT IN (N'Succeeded', N'Failed', N'Unknown', N'Abandoned')
        THROW 51320, N'Completed posting Status is invalid.', 1;
    IF @MarkPosted IS NULL
        THROW 51320, N'MarkPosted must be 0 or 1.', 1;

    DECLARE @WorkEntryId int;
    DECLARE @Destination nvarchar(40);
    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @Success bit = CONVERT(bit, CASE WHEN @Status = N'Succeeded' THEN 1 ELSE 0 END);
    DECLARE @NormalizedSageTicketNumber nvarchar(120);
    DECLARE @ExistingSagePosted bit;
    DECLARE @LockedAttemptId bigint;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT
            @WorkEntryId = [WorkEntryId],
            @Destination = [Destination]
        FROM [tb_ops].[PostingAttempts]
        WHERE [Id] = @AttemptId
          AND [OwnerWindowsSid] = @UserSid
          AND [Status] IN (N'Started', N'Unknown');

        IF @WorkEntryId IS NULL
            THROW 51321, N'The posting attempt is missing, final, or not owned by the current user.', 1;

        SELECT @ExistingSagePosted = [SagePosted]
        FROM [tb_data].[WorkEntries] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Id] = @WorkEntryId
          AND [OwnerWindowsSid] = @UserSid;

        IF @ExistingSagePosted IS NULL
            THROW 51321, N'The posting attempt no longer has an owned work entry.', 1;
        IF @ExistingSagePosted = 1
            THROW 51322, N'An entry already posted to Sage is permanently immutable.', 1;

        SELECT
            @LockedAttemptId = [Id],
            @Destination = [Destination]
        FROM [tb_ops].[PostingAttempts] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Id] = @AttemptId
          AND [WorkEntryId] = @WorkEntryId
          AND [OwnerWindowsSid] = @UserSid
          AND [Status] IN (N'Started', N'Unknown');

        IF @LockedAttemptId IS NULL
            THROW 51321, N'The posting attempt became final before it could be completed.', 1;

        SET @NormalizedSageTicketNumber =
            CASE
                WHEN @Destination <> N'Sage' OR @ExternalReference IS NULL
                    THEN NULL
                WHEN UPPER(LEFT(@ExternalReference, 5)) = N'SAGE-'
                    THEN NULLIF(LTRIM(RTRIM(SUBSTRING(@ExternalReference, 6, 120))), N'')
                ELSE LEFT(@ExternalReference, 120)
            END;

        UPDATE [tb_ops].[PostingAttempts]
        SET
            [Status] = @Status,
            [Message] = COALESCE(@Message, N''),
            [ExternalReference] = @ExternalReference,
            [CompletedAtUtc] = @NowUtc
        WHERE [Id] = @AttemptId;

        DELETE FROM [tb_ops].[PostingLeases]
        WHERE [AttemptId] = @AttemptId
          AND [OwnerWindowsSid] = @UserSid;

        IF @Status = N'Succeeded' AND @MarkPosted = 1
        BEGIN
            UPDATE [tb_data].[WorkEntries]
            SET
                [WhdPosted] =
                    CASE WHEN @Destination = N'WHD' THEN 1 ELSE [WhdPosted] END,
                [WhdPostedAtUtc] =
                    CASE WHEN @Destination = N'WHD' THEN @NowUtc ELSE [WhdPostedAtUtc] END,
                [SagePosted] =
                    CASE WHEN @Destination = N'Sage' THEN 1 ELSE [SagePosted] END,
                [SagePostedAtUtc] =
                    CASE WHEN @Destination = N'Sage' THEN @NowUtc ELSE [SagePostedAtUtc] END,
                [SageTicketNumber] =
                    CASE
                        WHEN @Destination = N'Sage' AND @NormalizedSageTicketNumber IS NOT NULL
                            THEN @NormalizedSageTicketNumber
                        ELSE [SageTicketNumber]
                    END,
                [PostingStatus] =
                    CASE
                        WHEN
                            (@Destination = N'WHD' OR [WhdPosted] = 1)
                            AND (@Destination = N'Sage' OR [SagePosted] = 1)
                                THEN N'PostedToBoth'
                        WHEN @Destination = N'WHD' THEN N'PostedToWhd'
                        ELSE N'PostedToSage'
                    END,
                [LastError] = NULL,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @WorkEntryId
              AND [OwnerWindowsSid] = @UserSid;
        END
        ELSE IF
            @Status = N'Succeeded'
            AND @MarkPosted = 0
            AND @Destination = N'Sage'
        BEGIN
            UPDATE [tb_data].[WorkEntries]
            SET
                [SageTicketNumber] =
                    CASE
                        WHEN @NormalizedSageTicketNumber IS NOT NULL
                            THEN @NormalizedSageTicketNumber
                        ELSE [SageTicketNumber]
                    END,
                [PostingStatus] =
                    CASE
                        WHEN [WhdPosted] = 1 THEN N'PostedToWhd'
                        ELSE N'Ready'
                    END,
                [LastError] = NULL,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @WorkEntryId
              AND [OwnerWindowsSid] = @UserSid
              AND [SagePosted] = 0;

            IF @@ROWCOUNT = 0
                THROW 51322, N'The successful Sage draft state could not be saved because the entry became immutable.', 1;
        END
        ELSE IF @Status <> N'Succeeded'
        BEGIN
            UPDATE [tb_data].[WorkEntries]
            SET
                [PostingStatus] = N'Failed',
                [LastError] = COALESCE(NULLIF(@Message, N''), N'External posting outcome was not successful.'),
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @WorkEntryId
              AND [OwnerWindowsSid] = @UserSid;
        END;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_ops].[PostingLogs] WITH (UPDLOCK, HOLDLOCK)
            WHERE [WorkEntryId] = @WorkEntryId
              AND [OwnerWindowsSid] = @UserSid
              AND [Destination] = @Destination
              AND [Success] = @Success
              AND [Message] = COALESCE(@Message, N'')
              AND
              (
                  [ExternalReference] = @ExternalReference
                  OR ([ExternalReference] IS NULL AND @ExternalReference IS NULL)
              )
        )
        BEGIN
            INSERT INTO [tb_ops].[PostingLogs]
            (
                [WorkEntryId],
                [OwnerWindowsSid],
                [Destination],
                [Payload],
                [Success],
                [Message],
                [ExternalReference],
                [RequestId],
                [CreatedAtUtc]
            )
            VALUES
            (
                @WorkEntryId,
                @UserSid,
                @Destination,
                N'Posting attempt completion',
                @Success,
                COALESCE(@Message, N''),
                @ExternalReference,
                NEWID(),
                @NowUtc
            );
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @AttemptId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'PostingAttemptCompleted',
            @EntityType = N'PostingAttempt',
            @EntityId = @AuditEntityId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_app.ResolveOutstandingPostingAttempts', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ResolveOutstandingPostingAttempts];
GO

CREATE PROCEDURE [tb_app].[ResolveOutstandingPostingAttempts]
    @WorkEntryId bigint,
    @Destination nvarchar(40),
    @Message nvarchar(max),
    @ExternalReference nvarchar(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @Destination = NULLIF(LTRIM(RTRIM(@Destination)), N'');
    SET @ExternalReference = NULLIF(LTRIM(RTRIM(@ExternalReference)), N'');
    IF @Destination NOT IN (N'WHD', N'Sage')
        THROW 51330, N'Destination must be WHD or Sage.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @AffectedCount int;
    DECLARE @SagePosted bit;
    DECLARE @NormalizedSageTicketNumber nvarchar(120) =
        CASE
            WHEN @Destination <> N'Sage' OR @ExternalReference IS NULL
                THEN NULL
            WHEN UPPER(LEFT(@ExternalReference, 5)) = N'SAGE-'
                THEN NULLIF(LTRIM(RTRIM(SUBSTRING(@ExternalReference, 6, 120))), N'')
            ELSE LEFT(@ExternalReference, 120)
        END;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @SagePosted = [SagePosted]
        FROM [tb_data].[WorkEntries] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Id] = @WorkEntryId
          AND [OwnerWindowsSid] = @UserSid;

        IF @SagePosted IS NULL
            THROW 51338, N'The work entry does not exist or is not owned by the current user.', 1;
        IF @SagePosted = 1
            THROW 51339, N'An entry already posted to Sage is permanently immutable.', 1;

        UPDATE [tb_ops].[PostingAttempts]
        SET
            [Status] = N'Succeeded',
            [Message] = COALESCE(@Message, N''),
            [ExternalReference] = COALESCE(@ExternalReference, [ExternalReference]),
            [CompletedAtUtc] = @NowUtc
        WHERE [WorkEntryId] = @WorkEntryId
          AND [Destination] = @Destination
          AND [OwnerWindowsSid] = @UserSid
          AND [Status] IN (N'Started', N'Unknown');

        SET @AffectedCount = @@ROWCOUNT;

        IF @AffectedCount > 0
        BEGIN
            DELETE FROM [tb_ops].[PostingLeases]
            WHERE [WorkEntryId] = @WorkEntryId
              AND [Destination] = @Destination
              AND [OwnerWindowsSid] = @UserSid;

            UPDATE [tb_data].[WorkEntries]
            SET
                [WhdPosted] =
                    CASE WHEN @Destination = N'WHD' THEN 1 ELSE [WhdPosted] END,
                [WhdPostedAtUtc] =
                    CASE WHEN @Destination = N'WHD' THEN @NowUtc ELSE [WhdPostedAtUtc] END,
                [SagePosted] =
                    CASE WHEN @Destination = N'Sage' THEN 1 ELSE [SagePosted] END,
                [SagePostedAtUtc] =
                    CASE WHEN @Destination = N'Sage' THEN @NowUtc ELSE [SagePostedAtUtc] END,
                [SageTicketNumber] =
                    CASE
                        WHEN @Destination = N'Sage' AND @NormalizedSageTicketNumber IS NOT NULL
                            THEN @NormalizedSageTicketNumber
                        ELSE [SageTicketNumber]
                    END,
                [PostingStatus] =
                    CASE
                        WHEN
                            (@Destination = N'WHD' OR [WhdPosted] = 1)
                            AND (@Destination = N'Sage' OR [SagePosted] = 1)
                                THEN N'PostedToBoth'
                        WHEN @Destination = N'WHD' THEN N'PostedToWhd'
                        ELSE N'PostedToSage'
                    END,
                [LastError] = NULL,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @WorkEntryId
              AND [OwnerWindowsSid] = @UserSid;

            INSERT INTO [tb_ops].[PostingLogs]
            (
                [WorkEntryId],
                [OwnerWindowsSid],
                [Destination],
                [Payload],
                [Success],
                [Message],
                [ExternalReference]
            )
            VALUES
            (
                CONVERT(int, @WorkEntryId),
                @UserSid,
                @Destination,
                N'Manual reconciliation',
                1,
                COALESCE(@Message, N''),
                @ExternalReference
            );
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT @AffectedCount AS [AffectedCount];
END;
GO

IF OBJECT_ID(N'tb_app.MarkWorkEntryPosted', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[MarkWorkEntryPosted];
GO

CREATE PROCEDURE [tb_app].[MarkWorkEntryPosted]
    @WorkEntryId int,
    @Destination nvarchar(40),
    @ExpectedRowVersion binary(8),
    @Message nvarchar(max) = NULL,
    @ExternalReference nvarchar(500) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @Destination = NULLIF(LTRIM(RTRIM(@Destination)), N'');
    SET @ExternalReference = NULLIF(LTRIM(RTRIM(@ExternalReference)), N'');
    SET @Message = COALESCE(
        NULLIF(LTRIM(RTRIM(@Message)), N''),
        N'Manually marked posted after external verification.');

    IF @Destination NOT IN (N'WHD', N'Sage')
        THROW 51331, N'Destination must be WHD or Sage.', 1;
    IF @ExpectedRowVersion IS NULL
        THROW 51332, N'ExpectedRowVersion is required for a manual posted marker.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @EffectiveRequestId uniqueidentifier = COALESCE(@RequestId, NEWID());
    DECLARE @WhdPosted bit;
    DECLARE @SagePosted bit;
    DECLARE @NormalizedSageTicketNumber nvarchar(120) =
        CASE
            WHEN @Destination <> N'Sage' OR @ExternalReference IS NULL
                THEN NULL
            WHEN UPPER(LEFT(@ExternalReference, 5)) = N'SAGE-'
                THEN NULLIF(LTRIM(RTRIM(SUBSTRING(@ExternalReference, 6, 120))), N'')
            ELSE LEFT(@ExternalReference, 120)
        END;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT
            @WhdPosted = [WhdPosted],
            @SagePosted = [SagePosted]
        FROM [tb_data].[WorkEntries] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Id] = @WorkEntryId
          AND [OwnerWindowsSid] = @UserSid
          AND [RowVersion] = @ExpectedRowVersion;

        IF @WhdPosted IS NULL
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM [tb_data].[WorkEntries] WHERE [Id] = @WorkEntryId)
                THROW 51333, N'The work entry no longer exists.', 1;
            IF NOT EXISTS
            (
                SELECT 1 FROM [tb_data].[WorkEntries]
                WHERE [Id] = @WorkEntryId AND [OwnerWindowsSid] = @UserSid
            )
                THROW 51334, N'Only the work-entry owner may mark it posted.', 1;
            THROW 51335, N'The work entry changed after it was loaded.', 1;
        END;

        IF @SagePosted = 1
            THROW 51336, N'An entry already posted to Sage is permanently immutable.', 1;
        IF @Destination = N'WHD' AND @WhdPosted = 1
            THROW 51337, N'The work entry is already marked posted to WHD.', 1;

        UPDATE [tb_ops].[PostingAttempts]
        SET
            [Status] = N'Succeeded',
            [Message] = @Message,
            [ExternalReference] = COALESCE(@ExternalReference, [ExternalReference]),
            [CompletedAtUtc] = @NowUtc
        WHERE [WorkEntryId] = @WorkEntryId
          AND [Destination] = @Destination
          AND [OwnerWindowsSid] = @UserSid
          AND [Status] IN (N'Started', N'Unknown');

        DELETE FROM [tb_ops].[PostingLeases]
        WHERE [WorkEntryId] = @WorkEntryId
          AND [Destination] = @Destination
          AND [OwnerWindowsSid] = @UserSid;

        UPDATE [tb_data].[WorkEntries]
        SET
            [WhdPosted] = CASE WHEN @Destination = N'WHD' THEN 1 ELSE [WhdPosted] END,
            [WhdPostedAtUtc] = CASE WHEN @Destination = N'WHD' THEN @NowUtc ELSE [WhdPostedAtUtc] END,
            [SagePosted] = CASE WHEN @Destination = N'Sage' THEN 1 ELSE [SagePosted] END,
            [SagePostedAtUtc] = CASE WHEN @Destination = N'Sage' THEN @NowUtc ELSE [SagePostedAtUtc] END,
            [SageTicketNumber] =
                CASE
                    WHEN @Destination = N'Sage' AND @NormalizedSageTicketNumber IS NOT NULL
                        THEN @NormalizedSageTicketNumber
                    ELSE [SageTicketNumber]
                END,
            [PostingStatus] =
                CASE
                    WHEN @Destination = N'Sage' AND @WhdPosted = 1 THEN N'PostedToBoth'
                    WHEN @Destination = N'Sage' THEN N'PostedToSage'
                    ELSE N'PostedToWhd'
                END,
            [LastError] = NULL,
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = @NowUtc
        WHERE [Id] = @WorkEntryId
          AND [OwnerWindowsSid] = @UserSid
          AND [RowVersion] = @ExpectedRowVersion;

        IF @@ROWCOUNT = 0
            THROW 51335, N'The work entry changed while it was being marked posted.', 1;

        INSERT INTO [tb_ops].[PostingLogs]
        (
            [WorkEntryId], [OwnerWindowsSid], [Destination], [Payload], [Success],
            [Message], [ExternalReference], [RequestId], [CreatedAtUtc]
        )
        VALUES
        (
            @WorkEntryId, @UserSid, @Destination, N'Manual external verification', 1,
            @Message, @ExternalReference, @EffectiveRequestId, @NowUtc
        );

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @WorkEntryId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'WorkEntryManuallyMarkedPosted',
            @EntityType = N'WorkEntry',
            @EntityId = @AuditEntityId,
            @RequestId = @EffectiveRequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_app.AbandonOutstandingPostingAttempts', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AbandonOutstandingPostingAttempts];
GO

CREATE PROCEDURE [tb_app].[AbandonOutstandingPostingAttempts]
    @WorkEntryId bigint,
    @Destination nvarchar(40),
    @Message nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @AffectedCount int;

    BEGIN TRY
        BEGIN TRANSACTION;

        UPDATE [tb_ops].[PostingAttempts]
        SET
            [Status] = N'Abandoned',
            [Message] = COALESCE(@Message, N''),
            [CompletedAtUtc] = @NowUtc
        WHERE [WorkEntryId] = @WorkEntryId
          AND [Destination] = @Destination
          AND [OwnerWindowsSid] = @UserSid
          AND [Status] IN (N'Started', N'Unknown');

        SET @AffectedCount = @@ROWCOUNT;

        DELETE FROM [tb_ops].[PostingLeases]
        WHERE [WorkEntryId] = @WorkEntryId
          AND [Destination] = @Destination
          AND [OwnerWindowsSid] = @UserSid;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT @AffectedCount AS [AffectedCount];
END;
GO

IF OBJECT_ID(N'tb_app.HasSuccessfulSageDraftLog', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[HasSuccessfulSageDraftLog];
GO

CREATE PROCEDURE [tb_app].[HasSuccessfulSageDraftLog]
    @WorkEntryId bigint
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT CONVERT
    (
        bit,
        CASE
            WHEN EXISTS
            (
                SELECT 1
                FROM [tb_ops].[PostingLogs]
                WHERE [WorkEntryId] = @WorkEntryId
                  AND [OwnerWindowsSid] = @UserSid
                  AND [Destination] = N'Sage'
                  AND [Success] = 1
            )
                THEN 1
            ELSE 0
        END
    ) AS [HasSuccessfulLog];
END;
GO

IF OBJECT_ID(N'tb_app.GetPostingLogs', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetPostingLogs];
GO

CREATE PROCEDURE [tb_app].[GetPostingLogs]
    @Destination nvarchar(40) = NULL,
    @Success bit = NULL,
    @Keyword nvarchar(240) = NULL,
    @StartDate date = NULL,
    @EndDate date = NULL,
    @Limit int = 250,
    @IncludeAllUsers bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IncludeAllUsers = 1 AND @IsManager <> 1 AND @IsAdmin <> 1
        THROW 51330, N'Only a Manager or Admin may read other users'' posting logs.', 1;

    SET @Limit =
        CASE
            WHEN @Limit IS NULL OR @Limit < 1 THEN 1
            WHEN @Limit > 1000 THEN 1000
            ELSE @Limit
        END;
    SET @Destination = NULLIF(LTRIM(RTRIM(@Destination)), N'');
    IF @Destination = N'Any'
        SET @Destination = NULL;
    SET @Keyword = NULLIF(LTRIM(RTRIM(@Keyword)), N'');
    DECLARE @KeywordPattern nvarchar(500) =
        CASE WHEN @Keyword IS NULL THEN NULL ELSE N'%' + @Keyword + N'%' END;

    SELECT TOP (@Limit)
        posting_log.[Id],
        posting_log.[WorkEntryId],
        posting_log.[Destination],
        posting_log.[Payload],
        posting_log.[Success],
        posting_log.[Message],
        posting_log.[ExternalReference],
        posting_log.[CreatedAtUtc] AS [CreatedAt]
    FROM [tb_ops].[PostingLogs] AS posting_log
    WHERE (@IncludeAllUsers = 1 OR posting_log.[OwnerWindowsSid] = @UserSid)
      AND (@Destination IS NULL OR posting_log.[Destination] = @Destination)
      AND (@Success IS NULL OR posting_log.[Success] = @Success)
      AND (@StartDate IS NULL OR posting_log.[CreatedAtUtc] >= @StartDate)
      AND
      (
          @EndDate IS NULL
          OR posting_log.[CreatedAtUtc] < DATEADD(day, 1, CONVERT(datetime2(3), @EndDate))
      )
      AND
      (
          @KeywordPattern IS NULL
          OR posting_log.[Message] LIKE @KeywordPattern
          OR posting_log.[Payload] LIKE @KeywordPattern
          OR posting_log.[Destination] LIKE @KeywordPattern
          OR posting_log.[ExternalReference] LIKE @KeywordPattern
          OR CONVERT(nvarchar(30), posting_log.[WorkEntryId]) LIKE @KeywordPattern
      )
    ORDER BY posting_log.[CreatedAtUtc] DESC, posting_log.[Id] DESC;
END;
GO

PRINT N'TechBench V0002 durable posting procedures created.';
GO

-- ============================================================================
-- END 43-V0002-PostingProcedures.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 44-V0002-SyncImportProcedures.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'tb_app.SyncUpsertClient', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncUpsertClient];
GO

CREATE PROCEDURE [tb_app].[SyncUpsertClient]
    @Name nvarchar(240),
    @Source nvarchar(80),
    @ExternalId nvarchar(500) = NULL,
    @IsActive bit = 1,
    @SyncedAtUtc datetime2(3) = NULL,
    @WhdLocationName nvarchar(240) = NULL,
    @WhdContactName nvarchar(240) = NULL,
    @SageCustomerId nvarchar(120) = NULL,
    @SageCustomerName nvarchar(240) = NULL,
    @SageContactName nvarchar(240) = NULL,
    @SageTelephone nvarchar(80) = NULL,
    @MatchStatus nvarchar(80) = N'Unmatched'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 AND @IsSyncOperator <> 1
        THROW 51400, N'Only an Admin or Sync Operator may synchronize clients.', 1;

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
    SET @SyncedAtUtc = COALESCE(@SyncedAtUtc, SYSUTCDATETIME());

    IF @Name IS NULL
        THROW 51401, N'Client name is required.', 1;

    DECLARE @IdentitySource nvarchar(40) =
        CASE
            WHEN @Source = N'WHD' THEN N'WHD'
            WHEN @Source = N'Sage' THEN N'Sage'
            ELSE NULL
        END;
    DECLARE @IdentityExternalId nvarchar(500) =
        CASE
            WHEN @IdentitySource = N'Sage'
                THEN COALESCE(@SageCustomerId, @ExternalId)
            ELSE @ExternalId
        END;
    DECLARE @ClientId int;
    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @IdentitySource IS NOT NULL AND @IdentityExternalId IS NOT NULL
        BEGIN
            SELECT @ClientId = [ClientId]
            FROM [tb_data].[ClientExternalIdentities] WITH (UPDLOCK, HOLDLOCK)
            WHERE [SourceSystem] = @IdentitySource
              AND [ExternalId] = @IdentityExternalId;
        END;

        IF @ClientId IS NULL AND @ExternalId IS NOT NULL
        BEGIN
            SELECT TOP (1) @ClientId = [Id]
            FROM [tb_data].[Clients] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Source] = @Source
              AND [ExternalId] = @ExternalId
            ORDER BY [Id];
        END;

        IF @ClientId IS NULL
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
                @Name,
                @Source,
                @ExternalId,
                @IsActive,
                @SyncedAtUtc,
                @WhdLocationName,
                @WhdContactName,
                @SageCustomerId,
                @SageCustomerName,
                @SageContactName,
                @SageTelephone,
                @MatchStatus,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );
            SET @ClientId = CONVERT(int, SCOPE_IDENTITY());
        END
        ELSE
        BEGIN
            UPDATE [tb_data].[Clients]
            SET
                [Name] =
                    CASE
                        WHEN @Source = N'WHD' AND @WhdLocationName IS NOT NULL
                            THEN @Name
                        WHEN NULLIF(LTRIM(RTRIM([Name])), N'') IS NULL
                            THEN @Name
                        ELSE [Name]
                    END,
                [Source] =
                    CASE
                        WHEN [Source] = @Source OR [Source] = N'Both' THEN [Source]
                        WHEN [Source] IN (N'WHD', N'Sage')
                         AND @Source IN (N'WHD', N'Sage')
                            THEN N'Both'
                        ELSE @Source
                    END,
                [ExternalId] = COALESCE([ExternalId], @ExternalId),
                [IsActive] = @IsActive,
                [LastSyncedAtUtc] = @SyncedAtUtc,
                [WhdLocationName] = COALESCE(@WhdLocationName, [WhdLocationName]),
                [WhdContactName] = COALESCE(@WhdContactName, [WhdContactName]),
                [SageCustomerId] = COALESCE(@SageCustomerId, [SageCustomerId]),
                [SageCustomerName] = COALESCE(@SageCustomerName, [SageCustomerName]),
                [SageContactName] = COALESCE(@SageContactName, [SageContactName]),
                [SageTelephone] = COALESCE(@SageTelephone, [SageTelephone]),
                [MatchStatus] =
                    CASE
                        WHEN [Source] = N'Both'
                            THEN N'Matched'
                        WHEN [Source] IN (N'WHD', N'Sage')
                         AND @Source IN (N'WHD', N'Sage')
                         AND [Source] <> @Source
                            THEN N'Matched'
                        ELSE @MatchStatus
                    END,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @ClientId;
        END;

        IF @IdentitySource IS NOT NULL AND @IdentityExternalId IS NOT NULL
        BEGIN
            IF EXISTS
            (
                SELECT 1
                FROM [tb_data].[ClientExternalIdentities]
                WHERE [SourceSystem] = @IdentitySource
                  AND [ExternalId] = @IdentityExternalId
            )
            BEGIN
                UPDATE [tb_data].[ClientExternalIdentities]
                SET
                    [ClientId] = @ClientId,
                    [ExternalName] = @Name,
                    [LastSyncedAtUtc] = @SyncedAtUtc,
                    [UpdatedByWindowsSid] = @UserSid,
                    [UpdatedAtUtc] = @NowUtc
                WHERE [SourceSystem] = @IdentitySource
                  AND [ExternalId] = @IdentityExternalId;
            END
            ELSE
            BEGIN
                INSERT INTO [tb_data].[ClientExternalIdentities]
                (
                    [ClientId],
                    [SourceSystem],
                    [ExternalId],
                    [ExternalName],
                    [LastSyncedAtUtc],
                    [CreatedByWindowsSid],
                    [UpdatedByWindowsSid],
                    [CreatedAtUtc],
                    [UpdatedAtUtc]
                )
                VALUES
                (
                    @ClientId,
                    @IdentitySource,
                    @IdentityExternalId,
                    @Name,
                    @SyncedAtUtc,
                    @UserSid,
                    @UserSid,
                    @NowUtc,
                    @NowUtc
                );
            END;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

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
        client.[RowVersion]
    FROM [tb_data].[Clients] AS client
    WHERE client.[Id] = @ClientId;
END;
GO

IF OBJECT_ID(N'tb_app.SyncUpsertSageCustomer', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncUpsertSageCustomer];
GO

CREATE PROCEDURE [tb_app].[SyncUpsertSageCustomer]
    @CustomerId nvarchar(120),
    @CustomerName nvarchar(240),
    @ContactName nvarchar(240) = NULL,
    @Telephone nvarchar(80) = NULL,
    @IsActive bit = 1,
    @SyncedAtUtc datetime2(3) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Result TABLE
    (
        [Id] int,
        [Name] nvarchar(240),
        [Source] nvarchar(80),
        [ExternalId] nvarchar(500),
        [IsActive] bit,
        [LastSyncedAt] datetime2(3),
        [WhdLocationName] nvarchar(240),
        [WhdContactName] nvarchar(240),
        [SageCustomerId] nvarchar(120),
        [SageCustomerName] nvarchar(240),
        [SageContactName] nvarchar(240),
        [SageTelephone] nvarchar(80),
        [MatchStatus] nvarchar(80),
        [RowVersion] binary(8)
    );

    INSERT INTO @Result
    EXEC [tb_app].[SyncUpsertClient]
        @Name = @CustomerName,
        @Source = N'Sage',
        @ExternalId = @CustomerId,
        @IsActive = @IsActive,
        @SyncedAtUtc = @SyncedAtUtc,
        @SageCustomerId = @CustomerId,
        @SageCustomerName = @CustomerName,
        @SageContactName = @ContactName,
        @SageTelephone = @Telephone,
        @MatchStatus = N'Unmatched';

    SELECT
        [Id] AS [ClientId],
        [Id],
        [RowVersion]
    FROM @Result;
END;
GO

IF OBJECT_ID(N'tb_app.SyncRemoveStaleSageCustomers', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncRemoveStaleSageCustomers];
GO

CREATE PROCEDURE [tb_app].[SyncRemoveStaleSageCustomers]
    @ActiveCustomerIdsJson nvarchar(max),
    @SyncedAtUtc datetime2(3) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 AND @IsSyncOperator <> 1
        THROW 51410, N'Only an Admin or Sync Operator may reconcile Sage customers.', 1;
    IF ISJSON(@ActiveCustomerIdsJson) <> 1
        THROW 51411, N'ActiveCustomerIdsJson must be a JSON array.', 1;

    DECLARE @ActiveIds TABLE
    (
        [CustomerId] nvarchar(120) NOT NULL PRIMARY KEY
    );

    INSERT INTO @ActiveIds([CustomerId])
    SELECT DISTINCT NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(120), [value]))), N'')
    FROM OPENJSON(@ActiveCustomerIdsJson)
    WHERE NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(120), [value]))), N'') IS NOT NULL;

    DECLARE @StaleClients TABLE ([ClientId] int NOT NULL PRIMARY KEY);

    INSERT INTO @StaleClients([ClientId])
    SELECT DISTINCT identity_row.[ClientId]
    FROM [tb_data].[ClientExternalIdentities] AS identity_row
    WHERE identity_row.[SourceSystem] = N'Sage'
      AND NOT EXISTS
      (
          SELECT 1
          FROM @ActiveIds AS active_id
          WHERE active_id.[CustomerId] = identity_row.[ExternalId]
      );

    DECLARE @StaleCount int = @@ROWCOUNT;

    DELETE identity_row
    FROM [tb_data].[ClientExternalIdentities] AS identity_row
    INNER JOIN @StaleClients AS stale
        ON stale.[ClientId] = identity_row.[ClientId]
    WHERE identity_row.[SourceSystem] = N'Sage';

    UPDATE client
    SET
        [Source] =
            CASE
                WHEN EXISTS
                (
                    SELECT 1
                    FROM [tb_data].[ClientExternalIdentities] AS remaining
                    WHERE remaining.[ClientId] = client.[Id]
                      AND remaining.[SourceSystem] = N'WHD'
                )
                    THEN N'WHD'
                ELSE N'Sage'
            END,
        [IsActive] =
            CASE
                WHEN EXISTS
                (
                    SELECT 1
                    FROM [tb_data].[ClientExternalIdentities] AS remaining
                    WHERE remaining.[ClientId] = client.[Id]
                      AND remaining.[SourceSystem] = N'WHD'
                )
                    THEN client.[IsActive]
                ELSE 0
            END,
        [SageCustomerId] = NULL,
        [SageCustomerName] = NULL,
        [SageContactName] = NULL,
        [SageTelephone] = NULL,
        [MatchStatus] = N'Unmatched',
        [LastSyncedAtUtc] = COALESCE(@SyncedAtUtc, SYSUTCDATETIME()),
        [UpdatedByWindowsSid] = @UserSid,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    FROM [tb_data].[Clients] AS client
    INNER JOIN @StaleClients AS stale
        ON stale.[ClientId] = client.[Id];

    SELECT @StaleCount AS [StaleCount], @StaleCount AS [AffectedCount];
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveExternalMapping', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveExternalMapping];
GO

CREATE PROCEDURE [tb_app].[AdminSaveExternalMapping]
    @ClientId int,
    @Source nvarchar(80),
    @ExternalId nvarchar(240),
    @ExternalName nvarchar(240) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51420, N'Only an Admin may save an external client mapping.', 1;

    DECLARE @Result TABLE
    (
        [Id] bigint,
        [ClientId] int,
        [SourceSystem] nvarchar(40),
        [ExternalId] nvarchar(500),
        [ExternalName] nvarchar(240),
        [LastSyncedAt] datetime2(3),
        [RowVersion] binary(8)
    );

    INSERT INTO @Result
    EXEC [tb_app].[SyncUpsertClientExternalIdentity]
        @ClientId = @ClientId,
        @SourceSystem = @Source,
        @ExternalId = @ExternalId,
        @ExternalName = @ExternalName,
        @LastSyncedAtUtc = NULL;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @ClientId);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'ExternalClientMappingSaved',
        @EntityType = N'Client',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.AcquireSyncLease', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AcquireSyncLease];
GO

CREATE PROCEDURE [tb_app].[AcquireSyncLease]
    @Source nvarchar(120),
    @LeaseSeconds int = 300,
    @DeviceId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 AND @IsSyncOperator <> 1
        THROW 51430, N'Only an Admin or Sync Operator may acquire a sync lease.', 1;

    SET @Source = NULLIF(LTRIM(RTRIM(@Source)), N'');
    SET @LeaseSeconds =
        CASE
            WHEN @LeaseSeconds IS NULL OR @LeaseSeconds < 30 THEN 30
            WHEN @LeaseSeconds > 3600 THEN 3600
            ELSE @LeaseSeconds
        END;
    IF @Source IS NULL OR LEN(@Source) > 40
        THROW 51431, N'Sync source is required and cannot exceed 40 characters.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @LeaseId uniqueidentifier;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @LeaseId = [LeaseId]
        FROM [tb_ops].[SyncLeases] WITH (UPDLOCK, HOLDLOCK)
        WHERE [SourceSystem] = @Source
          AND [ExpiresAtUtc] > @NowUtc;

        IF @LeaseId IS NOT NULL
        BEGIN
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_ops].[SyncLeases]
                WHERE [SourceSystem] = @Source
                  AND [LeaseId] = @LeaseId
                  AND [OwnerWindowsSid] = @UserSid
                  AND [DeviceId] = @DeviceId
            )
                THROW 51432, N'Another workstation currently owns this synchronization lease.', 1;

            UPDATE [tb_ops].[SyncLeases]
            SET
                [ExpiresAtUtc] = DATEADD(second, @LeaseSeconds, @NowUtc),
                [UpdatedAtUtc] = @NowUtc
            WHERE [SourceSystem] = @Source;
        END
        ELSE
        BEGIN
            DELETE FROM [tb_ops].[SyncLeases]
            WHERE [SourceSystem] = @Source;

            SET @LeaseId = NEWID();
            INSERT INTO [tb_ops].[SyncLeases]
            (
                [SourceSystem],
                [LeaseId],
                [OwnerWindowsSid],
                [DeviceId],
                [AcquiredAtUtc],
                [ExpiresAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @Source,
                @LeaseId,
                @UserSid,
                @DeviceId,
                @NowUtc,
                DATEADD(second, @LeaseSeconds, @NowUtc),
                @NowUtc
            );
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [LeaseId],
        [SourceSystem] AS [Source],
        [ExpiresAtUtc],
        [RowVersion]
    FROM [tb_ops].[SyncLeases]
    WHERE [SourceSystem] = @Source;
END;
GO

IF OBJECT_ID(N'tb_app.ReleaseSyncLease', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ReleaseSyncLease];
GO

CREATE PROCEDURE [tb_app].[ReleaseSyncLease]
    @LeaseId uniqueidentifier,
    @DeviceId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    DELETE FROM [tb_ops].[SyncLeases]
    WHERE [LeaseId] = @LeaseId
      AND [OwnerWindowsSid] = @UserSid
      AND [DeviceId] = @DeviceId;
END;
GO

IF OBJECT_ID(N'tb_app.BeginSyncRun', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[BeginSyncRun];
GO

CREATE PROCEDURE [tb_app].[BeginSyncRun]
    @Source nvarchar(120),
    @LeaseId uniqueidentifier,
    @DeviceId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_ops].[SyncLeases]
        WHERE [SourceSystem] = @Source
          AND [LeaseId] = @LeaseId
          AND [OwnerWindowsSid] = @UserSid
          AND [DeviceId] = @DeviceId
          AND [ExpiresAtUtc] > SYSUTCDATETIME()
    )
        THROW 51440, N'The synchronization lease is missing, expired, or owned by another workstation.', 1;

    DECLARE @RunId uniqueidentifier = NEWID();

    INSERT INTO [tb_ops].[SyncRuns]
    (
        [Id],
        [SourceSystem],
        [LeaseId],
        [OwnerWindowsSid],
        [DeviceId],
        [Status]
    )
    VALUES
    (
        @RunId,
        @Source,
        @LeaseId,
        @UserSid,
        @DeviceId,
        N'Started'
    );

    SELECT @RunId AS [RunId];
END;
GO

IF OBJECT_ID(N'tb_app.CompleteSyncRun', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[CompleteSyncRun];
GO

CREATE PROCEDURE [tb_app].[CompleteSyncRun]
    @RunId uniqueidentifier,
    @Succeeded bit,
    @ItemCount int = 0,
    @Message nvarchar(max) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    UPDATE [tb_ops].[SyncRuns]
    SET
        [Status] = CASE WHEN @Succeeded = 1 THEN N'Succeeded' ELSE N'Failed' END,
        [ReadCount] = CASE WHEN @ItemCount < 0 THEN 0 ELSE @ItemCount END,
        [Message] = COALESCE(@Message, N''),
        [CompletedAtUtc] = SYSUTCDATETIME()
    WHERE [Id] = @RunId
      AND [OwnerWindowsSid] = @UserSid
      AND [Status] = N'Started';

    IF @@ROWCOUNT = 0
        THROW 51441, N'The synchronization run is missing, final, or owned by another user.', 1;
END;
GO

IF OBJECT_ID(N'tb_app.GetSyncRuns', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetSyncRuns];
GO

CREATE PROCEDURE [tb_app].[GetSyncRuns]
    @Source nvarchar(120) = NULL,
    @Limit int = 100
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsManager <> 1 AND @IsAdmin <> 1 AND @IsSyncOperator <> 1
        THROW 51442, N'The current user cannot read synchronization history.', 1;

    SET @Limit =
        CASE WHEN @Limit < 1 THEN 1 WHEN @Limit > 1000 THEN 1000 ELSE @Limit END;

    SELECT TOP (@Limit)
        [Id] AS [RunId],
        [SourceSystem] AS [Source],
        [LeaseId],
        [Status],
        [ReadCount],
        [SavedCount],
        [StaleCount],
        [Message],
        [StartedAtUtc],
        [CompletedAtUtc],
        [RowVersion]
    FROM [tb_ops].[SyncRuns]
    WHERE @Source IS NULL OR [SourceSystem] = @Source
    ORDER BY [StartedAtUtc] DESC;
END;
GO

IF OBJECT_ID(N'tb_security.RenewSyncRunLease', N'P') IS NOT NULL
    DROP PROCEDURE [tb_security].[RenewSyncRunLease];
GO

CREATE PROCEDURE [tb_security].[RenewSyncRunLease]
    @RunId uniqueidentifier,
    @ExpectedSource nvarchar(40)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85) = SUSER_SID(ORIGINAL_LOGIN());
    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    UPDATE sync_lease
    SET
        [ExpiresAtUtc] = DATEADD(second, 300, @NowUtc),
        [UpdatedAtUtc] = @NowUtc
    FROM [tb_ops].[SyncLeases] AS sync_lease
    INNER JOIN [tb_ops].[SyncRuns] AS sync_run
        ON sync_run.[SourceSystem] = sync_lease.[SourceSystem]
       AND sync_run.[LeaseId] = sync_lease.[LeaseId]
       AND sync_run.[OwnerWindowsSid] = sync_lease.[OwnerWindowsSid]
       AND sync_run.[DeviceId] = sync_lease.[DeviceId]
    WHERE sync_run.[Id] = @RunId
      AND sync_run.[SourceSystem] = @ExpectedSource
      AND sync_run.[OwnerWindowsSid] = @UserSid
      AND sync_run.[Status] = N'Started'
      AND sync_lease.[ExpiresAtUtc] > @NowUtc;

    IF @@ROWCOUNT = 0
        THROW 51449, N'The source-specific synchronization lease expired or was replaced.', 1;
END;
GO

IF OBJECT_ID(N'tb_app.SyncApplyClientSnapshot', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncApplyClientSnapshot];
GO

CREATE PROCEDURE [tb_app].[SyncApplyClientSnapshot]
    @RunId uniqueidentifier,
    @SnapshotJson nvarchar(max),
    @SyncedAtUtc datetime2(3),
    @ReconcileMissing bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85) = SUSER_SID(ORIGINAL_LOGIN());
    IF ISJSON(@SnapshotJson) <> 1
        THROW 51450, N'SnapshotJson must be a JSON array.', 1;
    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_ops].[SyncRuns] AS sync_run
        INNER JOIN [tb_ops].[SyncLeases] AS sync_lease
            ON sync_lease.[SourceSystem] = sync_run.[SourceSystem]
           AND sync_lease.[LeaseId] = sync_run.[LeaseId]
           AND sync_lease.[OwnerWindowsSid] = sync_run.[OwnerWindowsSid]
           AND sync_lease.[DeviceId] = sync_run.[DeviceId]
        WHERE sync_run.[Id] = @RunId
          AND sync_run.[SourceSystem] = N'WHD-Clients'
          AND sync_run.[OwnerWindowsSid] = @UserSid
          AND sync_run.[Status] = N'Started'
          AND sync_lease.[ExpiresAtUtc] > SYSUTCDATETIME()
    )
        THROW 51451, N'The WHD client synchronization run or lease is not active for this workstation.', 1;

    EXEC [tb_security].[RenewSyncRunLease]
        @RunId = @RunId,
        @ExpectedSource = N'WHD-Clients';

    DECLARE @Snapshot TABLE
    (
        [ExternalId] nvarchar(500) NOT NULL PRIMARY KEY,
        [Name] nvarchar(240) NOT NULL,
        [LocationName] nvarchar(240) NULL,
        [ContactName] nvarchar(240) NULL,
        [IsActive] bit NOT NULL
    );

    INSERT INTO @Snapshot
    (
        [ExternalId],
        [Name],
        [LocationName],
        [ContactName],
        [IsActive]
    )
    SELECT
        [ExternalId],
        [Name],
        [LocationName],
        [ContactName],
        COALESCE([IsActive], 1)
    FROM OPENJSON(@SnapshotJson)
    WITH
    (
        [ExternalId] nvarchar(500) N'$.externalId',
        [Name] nvarchar(240) N'$.name',
        [LocationName] nvarchar(240) N'$.locationName',
        [ContactName] nvarchar(240) N'$.contactName',
        [IsActive] bit N'$.isActive'
    )
    WHERE NULLIF(LTRIM(RTRIM([ExternalId])), N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM([Name])), N'') IS NOT NULL;

    DECLARE @ExternalId nvarchar(500);
    DECLARE @Name nvarchar(240);
    DECLARE @LocationName nvarchar(240);
    DECLARE @ContactName nvarchar(240);
    DECLARE @IsActive bit;
    DECLARE @SavedCount int = 0;

    DECLARE SnapshotCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT [ExternalId], [Name], [LocationName], [ContactName], [IsActive]
    FROM @Snapshot;

    OPEN SnapshotCursor;
    FETCH NEXT FROM SnapshotCursor
    INTO @ExternalId, @Name, @LocationName, @ContactName, @IsActive;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC [tb_security].[RenewSyncRunLease]
            @RunId = @RunId,
            @ExpectedSource = N'WHD-Clients';

        DECLARE @ClientResult TABLE
        (
            [Id] int,
            [Name] nvarchar(240),
            [Source] nvarchar(80),
            [ExternalId] nvarchar(500),
            [IsActive] bit,
            [LastSyncedAt] datetime2(3),
            [WhdLocationName] nvarchar(240),
            [WhdContactName] nvarchar(240),
            [SageCustomerId] nvarchar(120),
            [SageCustomerName] nvarchar(240),
            [SageContactName] nvarchar(240),
            [SageTelephone] nvarchar(80),
            [MatchStatus] nvarchar(80),
            [RowVersion] binary(8)
        );

        INSERT INTO @ClientResult
        EXEC [tb_app].[SyncUpsertClient]
            @Name = @Name,
            @Source = N'WHD',
            @ExternalId = @ExternalId,
            @IsActive = @IsActive,
            @SyncedAtUtc = @SyncedAtUtc,
            @WhdLocationName = @LocationName,
            @WhdContactName = @ContactName,
            @MatchStatus = N'Unmatched';

        SET @SavedCount += 1;

        FETCH NEXT FROM SnapshotCursor
        INTO @ExternalId, @Name, @LocationName, @ContactName, @IsActive;
    END;

    CLOSE SnapshotCursor;
    DEALLOCATE SnapshotCursor;

    EXEC [tb_security].[RenewSyncRunLease]
        @RunId = @RunId,
        @ExpectedSource = N'WHD-Clients';

    DECLARE @StaleCount int = 0;
    IF @ReconcileMissing = 1
    BEGIN
        DECLARE @StaleClients TABLE ([ClientId] int NOT NULL PRIMARY KEY);
        INSERT INTO @StaleClients([ClientId])
        SELECT DISTINCT identity_row.[ClientId]
        FROM [tb_data].[ClientExternalIdentities] AS identity_row
        WHERE identity_row.[SourceSystem] = N'WHD'
          AND NOT EXISTS
          (
              SELECT 1
              FROM @Snapshot AS snapshot
              WHERE snapshot.[ExternalId] = identity_row.[ExternalId]
          );

        SET @StaleCount = @@ROWCOUNT;

        DELETE identity_row
        FROM [tb_data].[ClientExternalIdentities] AS identity_row
        INNER JOIN @StaleClients AS stale
            ON stale.[ClientId] = identity_row.[ClientId]
        WHERE identity_row.[SourceSystem] = N'WHD';

        UPDATE client
        SET
            [Source] =
                CASE
                    WHEN EXISTS
                    (
                        SELECT 1
                        FROM [tb_data].[ClientExternalIdentities] AS remaining
                        WHERE remaining.[ClientId] = client.[Id]
                          AND remaining.[SourceSystem] = N'Sage'
                    )
                        THEN N'Sage'
                    ELSE N'WHD'
                END,
            [IsActive] =
                CASE
                    WHEN EXISTS
                    (
                        SELECT 1
                        FROM [tb_data].[ClientExternalIdentities] AS remaining
                        WHERE remaining.[ClientId] = client.[Id]
                          AND remaining.[SourceSystem] = N'Sage'
                    )
                        THEN client.[IsActive]
                    ELSE 0
                END,
            [WhdLocationName] = NULL,
            [WhdContactName] = NULL,
            [MatchStatus] = N'Unmatched',
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        FROM [tb_data].[Clients] AS client
        INNER JOIN @StaleClients AS stale
            ON stale.[ClientId] = client.[Id];
    END;

    DECLARE @MatchedCount int =
    (
        SELECT COUNT(*)
        FROM [tb_data].[Clients]
        WHERE [Source] = N'Both'
    );

    UPDATE [tb_ops].[SyncRuns]
    SET
        [ReadCount] = (SELECT COUNT(*) FROM @Snapshot),
        [SavedCount] = @SavedCount,
        [StaleCount] = @StaleCount
    WHERE [Id] = @RunId;

    SELECT
        @SavedCount AS [SavedCount],
        @StaleCount AS [StaleCount],
        @MatchedCount AS [MatchedCount];
END;
GO

IF OBJECT_ID(N'tb_app.SyncApplyTicketStatusSnapshot', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncApplyTicketStatusSnapshot];
GO

CREATE PROCEDURE [tb_app].[SyncApplyTicketStatusSnapshot]
    @RunId uniqueidentifier,
    @SnapshotJson nvarchar(max),
    @SyncedAtUtc datetime2(3),
    @ReconcileMissing bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85) = SUSER_SID(ORIGINAL_LOGIN());
    IF ISJSON(@SnapshotJson) <> 1
        THROW 51450, N'SnapshotJson must be a JSON array.', 1;
    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_ops].[SyncRuns] AS sync_run
        INNER JOIN [tb_ops].[SyncLeases] AS sync_lease
            ON sync_lease.[SourceSystem] = sync_run.[SourceSystem]
           AND sync_lease.[LeaseId] = sync_run.[LeaseId]
           AND sync_lease.[OwnerWindowsSid] = sync_run.[OwnerWindowsSid]
           AND sync_lease.[DeviceId] = sync_run.[DeviceId]
        WHERE sync_run.[Id] = @RunId
          AND sync_run.[SourceSystem] = N'WHD-TicketStatuses'
          AND sync_run.[OwnerWindowsSid] = @UserSid
          AND sync_run.[Status] = N'Started'
          AND sync_lease.[ExpiresAtUtc] > SYSUTCDATETIME()
    )
        THROW 51451, N'The WHD ticket-status synchronization run or lease is not active for this workstation.', 1;

    EXEC [tb_security].[RenewSyncRunLease]
        @RunId = @RunId,
        @ExpectedSource = N'WHD-TicketStatuses';

    DECLARE @Snapshot TABLE
    (
        [Id] int NOT NULL PRIMARY KEY,
        [Name] nvarchar(160) NOT NULL,
        [IsClosed] bit NOT NULL
    );

    INSERT INTO @Snapshot([Id], [Name], [IsClosed])
    SELECT [Id], [Name], COALESCE([IsClosed], 0)
    FROM OPENJSON(@SnapshotJson)
    WITH
    (
        [Id] int N'$.id',
        [Name] nvarchar(160) N'$.name',
        [IsClosed] bit N'$.isClosed'
    )
    WHERE [Id] IS NOT NULL
      AND NULLIF(LTRIM(RTRIM([Name])), N'') IS NOT NULL;

    DECLARE @Id int;
    DECLARE @Name nvarchar(160);
    DECLARE @IsClosed bit;
    DECLARE @SavedCount int = 0;
    DECLARE @StatusExternalId nvarchar(240);

    DECLARE StatusCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT [Id], [Name], [IsClosed] FROM @Snapshot;
    OPEN StatusCursor;
    FETCH NEXT FROM StatusCursor INTO @Id, @Name, @IsClosed;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC [tb_security].[RenewSyncRunLease]
            @RunId = @RunId,
            @ExpectedSource = N'WHD-TicketStatuses';

        DECLARE @StatusResult TABLE
        (
            [Id] int,
            [Name] nvarchar(160),
            [Source] nvarchar(40),
            [ExternalId] nvarchar(240),
            [WhdStatusTypeId] int,
            [IsClosed] bit,
            [LastSyncedAt] datetime2(3),
            [RowVersion] binary(8)
        );
        SET @StatusExternalId = CONVERT(nvarchar(240), @Id);
        INSERT INTO @StatusResult
        EXEC [tb_app].[SyncUpsertTicketStatusOption]
            @Name = @Name,
            @Source = N'WHD',
            @ExternalId = @StatusExternalId,
            @WhdStatusTypeId = @Id,
            @IsClosed = @IsClosed,
            @SyncedAtUtc = @SyncedAtUtc;
        SET @SavedCount += 1;
        FETCH NEXT FROM StatusCursor INTO @Id, @Name, @IsClosed;
    END;
    CLOSE StatusCursor;
    DEALLOCATE StatusCursor;

    EXEC [tb_security].[RenewSyncRunLease]
        @RunId = @RunId,
        @ExpectedSource = N'WHD-TicketStatuses';

    DECLARE @StaleCount int = 0;
    IF @ReconcileMissing = 1
    BEGIN
        UPDATE status_option
        SET
            [IsClosed] = 1,
            [LastSyncedAtUtc] = @SyncedAtUtc,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        FROM [tb_data].[TicketStatusOptions] AS status_option
        WHERE status_option.[Source] = N'WHD'
          AND status_option.[WhdStatusTypeId] IS NOT NULL
          AND NOT EXISTS
          (
              SELECT 1
              FROM @Snapshot AS snapshot
              WHERE snapshot.[Id] = status_option.[WhdStatusTypeId]
          );
        SET @StaleCount = @@ROWCOUNT;
    END;

    UPDATE [tb_ops].[SyncRuns]
    SET
        [ReadCount] = (SELECT COUNT(*) FROM @Snapshot),
        [SavedCount] = @SavedCount,
        [StaleCount] = @StaleCount
    WHERE [Id] = @RunId;

    SELECT
        @SavedCount AS [SavedCount],
        @StaleCount AS [StaleCount],
        CONVERT(int, 0) AS [MatchedCount];
END;
GO

IF OBJECT_ID(N'tb_app.SyncApplyTicketSnapshot', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncApplyTicketSnapshot];
GO

CREATE PROCEDURE [tb_app].[SyncApplyTicketSnapshot]
    @RunId uniqueidentifier,
    @SnapshotJson nvarchar(max),
    @SyncedAtUtc datetime2(3),
    @ReconcileMissing bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85) = SUSER_SID(ORIGINAL_LOGIN());
    IF ISJSON(@SnapshotJson) <> 1
        THROW 51450, N'SnapshotJson must be a JSON array.', 1;
    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_ops].[SyncRuns] AS sync_run
        INNER JOIN [tb_ops].[SyncLeases] AS sync_lease
            ON sync_lease.[SourceSystem] = sync_run.[SourceSystem]
           AND sync_lease.[LeaseId] = sync_run.[LeaseId]
           AND sync_lease.[OwnerWindowsSid] = sync_run.[OwnerWindowsSid]
           AND sync_lease.[DeviceId] = sync_run.[DeviceId]
        WHERE sync_run.[Id] = @RunId
          AND sync_run.[SourceSystem] = N'WHD-Tickets'
          AND sync_run.[OwnerWindowsSid] = @UserSid
          AND sync_run.[Status] = N'Started'
          AND sync_lease.[ExpiresAtUtc] > SYSUTCDATETIME()
    )
        THROW 51451, N'The WHD ticket synchronization run or lease is not active for this workstation.', 1;

    EXEC [tb_security].[RenewSyncRunLease]
        @RunId = @RunId,
        @ExpectedSource = N'WHD-Tickets';

    DECLARE @Snapshot TABLE
    (
        [ExternalId] nvarchar(240) NOT NULL PRIMARY KEY,
        [TicketNumber] nvarchar(120) NOT NULL,
        [Subject] nvarchar(500) NOT NULL,
        [Status] nvarchar(160) NOT NULL,
        [StatusTypeId] int NULL,
        [IsClosed] bit NOT NULL,
        [ClientExternalId] nvarchar(500) NOT NULL,
        [ClientName] nvarchar(240) NOT NULL,
        [LocationName] nvarchar(240) NULL,
        [ContactName] nvarchar(240) NULL
    );

    INSERT INTO @Snapshot
    (
        [ExternalId],
        [TicketNumber],
        [Subject],
        [Status],
        [StatusTypeId],
        [IsClosed],
        [ClientExternalId],
        [ClientName],
        [LocationName],
        [ContactName]
    )
    SELECT
        [ExternalId],
        [TicketNumber],
        COALESCE([Subject], N''),
        COALESCE(NULLIF([Status], N''), N'Open'),
        [StatusTypeId],
        COALESCE([IsClosed], 0),
        [ClientExternalId],
        [ClientName],
        [LocationName],
        [ContactName]
    FROM OPENJSON(@SnapshotJson)
    WITH
    (
        [ExternalId] nvarchar(240) N'$.externalId',
        [TicketNumber] nvarchar(120) N'$.ticketNumber',
        [Subject] nvarchar(500) N'$.subject',
        [Status] nvarchar(160) N'$.status',
        [StatusTypeId] int N'$.statusTypeId',
        [IsClosed] bit N'$.isClosed',
        [ClientExternalId] nvarchar(500) N'$.client.externalId',
        [ClientName] nvarchar(240) N'$.client.name',
        [LocationName] nvarchar(240) N'$.client.locationName',
        [ContactName] nvarchar(240) N'$.client.contactName'
    )
    WHERE NULLIF(LTRIM(RTRIM([ExternalId])), N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM([TicketNumber])), N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM([ClientExternalId])), N'') IS NOT NULL;

    DECLARE @ExternalId nvarchar(240);
    DECLARE @TicketNumber nvarchar(120);
    DECLARE @Subject nvarchar(500);
    DECLARE @Status nvarchar(160);
    DECLARE @StatusTypeId int;
    DECLARE @IsClosed bit;
    DECLARE @ClientExternalId nvarchar(500);
    DECLARE @ClientName nvarchar(240);
    DECLARE @LocationName nvarchar(240);
    DECLARE @ContactName nvarchar(240);
    DECLARE @SavedCount int = 0;

    DECLARE TicketCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT
        [ExternalId],
        [TicketNumber],
        [Subject],
        [Status],
        [StatusTypeId],
        [IsClosed],
        [ClientExternalId],
        [ClientName],
        [LocationName],
        [ContactName]
    FROM @Snapshot;

    OPEN TicketCursor;
    FETCH NEXT FROM TicketCursor INTO
        @ExternalId, @TicketNumber, @Subject, @Status, @StatusTypeId, @IsClosed,
        @ClientExternalId, @ClientName, @LocationName, @ContactName;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC [tb_security].[RenewSyncRunLease]
            @RunId = @RunId,
            @ExpectedSource = N'WHD-Tickets';

        DECLARE @ClientResult TABLE
        (
            [Id] int,
            [Name] nvarchar(240),
            [Source] nvarchar(80),
            [ExternalId] nvarchar(500),
            [IsActive] bit,
            [LastSyncedAt] datetime2(3),
            [WhdLocationName] nvarchar(240),
            [WhdContactName] nvarchar(240),
            [SageCustomerId] nvarchar(120),
            [SageCustomerName] nvarchar(240),
            [SageContactName] nvarchar(240),
            [SageTelephone] nvarchar(80),
            [MatchStatus] nvarchar(80),
            [RowVersion] binary(8)
        );

        INSERT INTO @ClientResult
        EXEC [tb_app].[SyncUpsertClient]
            @Name = @ClientName,
            @Source = N'WHD',
            @ExternalId = @ClientExternalId,
            @IsActive = 1,
            @SyncedAtUtc = @SyncedAtUtc,
            @WhdLocationName = @LocationName,
            @WhdContactName = @ContactName,
            @MatchStatus = N'Unmatched';

        DECLARE @ClientId int = (SELECT TOP (1) [Id] FROM @ClientResult);
        DECLARE @TicketResult TABLE
        (
            [Id] int,
            [TicketNumber] nvarchar(120),
            [ClientId] int,
            [Subject] nvarchar(500),
            [Status] nvarchar(160),
            [Source] nvarchar(40),
            [ExternalId] nvarchar(240),
            [WhdStatusTypeId] int,
            [IsClosed] bit,
            [LastSyncedAt] datetime2(3),
            [RowVersion] binary(8)
        );

        INSERT INTO @TicketResult
        EXEC [tb_app].[SyncUpsertTicket]
            @ExternalId = @ExternalId,
            @TicketNumber = @TicketNumber,
            @ClientId = @ClientId,
            @Subject = @Subject,
            @Status = @Status,
            @WhdStatusTypeId = @StatusTypeId,
            @IsClosed = @IsClosed,
            @SyncedAtUtc = @SyncedAtUtc;

        SET @SavedCount += 1;

        FETCH NEXT FROM TicketCursor INTO
            @ExternalId, @TicketNumber, @Subject, @Status, @StatusTypeId, @IsClosed,
            @ClientExternalId, @ClientName, @LocationName, @ContactName;
    END;

    CLOSE TicketCursor;
    DEALLOCATE TicketCursor;

    EXEC [tb_security].[RenewSyncRunLease]
        @RunId = @RunId,
        @ExpectedSource = N'WHD-Tickets';

    DECLARE @StaleCount int = 0;
    IF @ReconcileMissing = 1
    BEGIN
        UPDATE ticket
        SET
            [IsClosed] = 1,
            [LastSyncedAtUtc] = @SyncedAtUtc,
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        FROM [tb_data].[Tickets] AS ticket
        WHERE ticket.[Source] = N'WHD'
          AND ticket.[ExternalId] IS NOT NULL
          AND NOT EXISTS
          (
              SELECT 1
              FROM @Snapshot AS snapshot
              WHERE snapshot.[ExternalId] = ticket.[ExternalId]
          );
        SET @StaleCount = @@ROWCOUNT;
    END;

    UPDATE [tb_ops].[SyncRuns]
    SET
        [ReadCount] = (SELECT COUNT(*) FROM @Snapshot),
        [SavedCount] = @SavedCount,
        [StaleCount] = @StaleCount
    WHERE [Id] = @RunId;

    SELECT
        @SavedCount AS [SavedCount],
        @StaleCount AS [StaleCount],
        CONVERT(int, 0) AS [MatchedCount];
END;
GO

IF OBJECT_ID(N'tb_app.SyncApplySageCustomerSnapshot', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncApplySageCustomerSnapshot];
GO

CREATE PROCEDURE [tb_app].[SyncApplySageCustomerSnapshot]
    @RunId uniqueidentifier,
    @SnapshotJson nvarchar(max),
    @SyncedAtUtc datetime2(3),
    @ReconcileMissing bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85) = SUSER_SID(ORIGINAL_LOGIN());
    IF ISJSON(@SnapshotJson) <> 1
        THROW 51450, N'SnapshotJson must be a JSON array.', 1;
    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_ops].[SyncRuns] AS sync_run
        INNER JOIN [tb_ops].[SyncLeases] AS sync_lease
            ON sync_lease.[SourceSystem] = sync_run.[SourceSystem]
           AND sync_lease.[LeaseId] = sync_run.[LeaseId]
           AND sync_lease.[OwnerWindowsSid] = sync_run.[OwnerWindowsSid]
           AND sync_lease.[DeviceId] = sync_run.[DeviceId]
        WHERE sync_run.[Id] = @RunId
          AND sync_run.[SourceSystem] = N'Sage-Customers'
          AND sync_run.[OwnerWindowsSid] = @UserSid
          AND sync_run.[Status] = N'Started'
          AND sync_lease.[ExpiresAtUtc] > SYSUTCDATETIME()
    )
        THROW 51451, N'The Sage customer synchronization run or lease is not active for this workstation.', 1;

    EXEC [tb_security].[RenewSyncRunLease]
        @RunId = @RunId,
        @ExpectedSource = N'Sage-Customers';

    DECLARE @Snapshot TABLE
    (
        [CustomerId] nvarchar(120) NOT NULL PRIMARY KEY,
        [CustomerName] nvarchar(240) NOT NULL,
        [ContactName] nvarchar(240) NULL,
        [Telephone] nvarchar(80) NULL,
        [IsActive] bit NOT NULL
    );

    INSERT INTO @Snapshot
    SELECT
        [CustomerId],
        [CustomerName],
        [ContactName],
        [Telephone],
        COALESCE([IsActive], 1)
    FROM OPENJSON(@SnapshotJson)
    WITH
    (
        [CustomerId] nvarchar(120) N'$.customerId',
        [CustomerName] nvarchar(240) N'$.customerName',
        [ContactName] nvarchar(240) N'$.contactName',
        [Telephone] nvarchar(80) N'$.telephone',
        [IsActive] bit N'$.isActive'
    )
    WHERE NULLIF(LTRIM(RTRIM([CustomerId])), N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM([CustomerName])), N'') IS NOT NULL;

    DECLARE @CustomerId nvarchar(120);
    DECLARE @CustomerName nvarchar(240);
    DECLARE @ContactName nvarchar(240);
    DECLARE @Telephone nvarchar(80);
    DECLARE @IsActive bit;
    DECLARE @SavedCount int = 0;

    DECLARE SageCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT [CustomerId], [CustomerName], [ContactName], [Telephone], [IsActive]
    FROM @Snapshot;
    OPEN SageCursor;
    FETCH NEXT FROM SageCursor
    INTO @CustomerId, @CustomerName, @ContactName, @Telephone, @IsActive;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC [tb_security].[RenewSyncRunLease]
            @RunId = @RunId,
            @ExpectedSource = N'Sage-Customers';

        DECLARE @SageClientResult TABLE
        (
            [Id] int,
            [Name] nvarchar(240),
            [Source] nvarchar(80),
            [ExternalId] nvarchar(500),
            [IsActive] bit,
            [LastSyncedAt] datetime2(3),
            [WhdLocationName] nvarchar(240),
            [WhdContactName] nvarchar(240),
            [SageCustomerId] nvarchar(120),
            [SageCustomerName] nvarchar(240),
            [SageContactName] nvarchar(240),
            [SageTelephone] nvarchar(80),
            [MatchStatus] nvarchar(80),
            [RowVersion] binary(8)
        );
        INSERT INTO @SageClientResult
        EXEC [tb_app].[SyncUpsertClient]
            @Name = @CustomerName,
            @Source = N'Sage',
            @ExternalId = @CustomerId,
            @IsActive = @IsActive,
            @SyncedAtUtc = @SyncedAtUtc,
            @SageCustomerId = @CustomerId,
            @SageCustomerName = @CustomerName,
            @SageContactName = @ContactName,
            @SageTelephone = @Telephone,
            @MatchStatus = N'Unmatched';
        SET @SavedCount += 1;
        FETCH NEXT FROM SageCursor
        INTO @CustomerId, @CustomerName, @ContactName, @Telephone, @IsActive;
    END;
    CLOSE SageCursor;
    DEALLOCATE SageCursor;

    EXEC [tb_security].[RenewSyncRunLease]
        @RunId = @RunId,
        @ExpectedSource = N'Sage-Customers';

    DECLARE @StaleCount int = 0;
    IF @ReconcileMissing = 1
    BEGIN
        DECLARE @ActiveJson nvarchar(max) =
        (
            SELECT [CustomerId] AS [value]
            FROM @Snapshot
            FOR JSON PATH
        );

        /* Build the scalar-array shape expected by SyncRemoveStaleSageCustomers. */
        SET @ActiveJson =
        (
            SELECT [CustomerId]
            FROM @Snapshot
            FOR JSON PATH
        );

        DECLARE @StaleResult TABLE
        (
            [StaleCount] int,
            [AffectedCount] int
        );

        /* The object array is parsed here directly to avoid a second JSON shape. */
        DECLARE @StaleClients TABLE ([ClientId] int NOT NULL PRIMARY KEY);
        INSERT INTO @StaleClients([ClientId])
        SELECT DISTINCT identity_row.[ClientId]
        FROM [tb_data].[ClientExternalIdentities] AS identity_row
        WHERE identity_row.[SourceSystem] = N'Sage'
          AND NOT EXISTS
          (
              SELECT 1
              FROM @Snapshot AS snapshot
              WHERE snapshot.[CustomerId] = identity_row.[ExternalId]
          );
        SET @StaleCount = @@ROWCOUNT;

        DELETE identity_row
        FROM [tb_data].[ClientExternalIdentities] AS identity_row
        INNER JOIN @StaleClients AS stale
            ON stale.[ClientId] = identity_row.[ClientId]
        WHERE identity_row.[SourceSystem] = N'Sage';

        UPDATE client
        SET
            [Source] =
                CASE
                    WHEN EXISTS
                    (
                        SELECT 1
                        FROM [tb_data].[ClientExternalIdentities] AS remaining
                        WHERE remaining.[ClientId] = client.[Id]
                          AND remaining.[SourceSystem] = N'WHD'
                    )
                        THEN N'WHD'
                    ELSE N'Sage'
                END,
            [IsActive] =
                CASE
                    WHEN EXISTS
                    (
                        SELECT 1
                        FROM [tb_data].[ClientExternalIdentities] AS remaining
                        WHERE remaining.[ClientId] = client.[Id]
                          AND remaining.[SourceSystem] = N'WHD'
                    )
                        THEN client.[IsActive]
                    ELSE 0
                END,
            [SageCustomerId] = NULL,
            [SageCustomerName] = NULL,
            [SageContactName] = NULL,
            [SageTelephone] = NULL,
            [MatchStatus] = N'Unmatched',
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        FROM [tb_data].[Clients] AS client
        INNER JOIN @StaleClients AS stale
            ON stale.[ClientId] = client.[Id];
    END;

    DECLARE @MatchedCount int =
    (
        SELECT COUNT(*)
        FROM [tb_data].[Clients]
        WHERE [Source] = N'Both'
    );

    UPDATE [tb_ops].[SyncRuns]
    SET
        [ReadCount] = (SELECT COUNT(*) FROM @Snapshot),
        [SavedCount] = @SavedCount,
        [StaleCount] = @StaleCount
    WHERE [Id] = @RunId;

    SELECT
        @SavedCount AS [SavedCount],
        @StaleCount AS [StaleCount],
        @MatchedCount AS [MatchedCount];
END;
GO

IF OBJECT_ID(N'tb_app.BeginImportBatch', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[BeginImportBatch];
GO

CREATE PROCEDURE [tb_app].[BeginImportBatch]
    @Source nvarchar(120),
    @ExpectedCount int = 0,
    @DeviceId uniqueidentifier = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @Source = NULLIF(LTRIM(RTRIM(@Source)), N'');
    IF @Source IS NULL OR LEN(@Source) > 80
        THROW 51460, N'Import source is required and cannot exceed 80 characters.', 1;

    DECLARE @BatchId uniqueidentifier = NEWID();

    INSERT INTO [tb_ops].[ImportBatches]
    (
        [Id],
        [SourceSystem],
        [OwnerWindowsSid],
        [DeviceId],
        [Status],
        [ReadCount]
    )
    VALUES
    (
        @BatchId,
        @Source,
        @UserSid,
        @DeviceId,
        N'Started',
        CASE WHEN @ExpectedCount < 0 THEN 0 ELSE @ExpectedCount END
    );

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @BatchId);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'ImportBatchStarted',
        @EntityType = N'ImportBatch',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;

    SELECT @BatchId AS [BatchId], @BatchId AS [ImportBatchId];
END;
GO

IF OBJECT_ID(N'tb_app.AddImportLegacyMapping', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AddImportLegacyMapping];
GO

CREATE PROCEDURE [tb_app].[AddImportLegacyMapping]
    @BatchId uniqueidentifier,
    @LegacyValue nvarchar(240),
    @EntityType nvarchar(80),
    @EntityId bigint
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_ops].[ImportBatches]
        WHERE [Id] = @BatchId
          AND [OwnerWindowsSid] = @UserSid
          AND [Status] = N'Started'
    )
        THROW 51461, N'The import batch is missing, final, or owned by another user.', 1;

    SET @LegacyValue = NULLIF(LTRIM(RTRIM(@LegacyValue)), N'');
    SET @EntityType = NULLIF(LTRIM(RTRIM(@EntityType)), N'');
    IF @LegacyValue IS NULL OR @EntityType IS NULL
        THROW 51462, N'LegacyValue and EntityType are required.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM [tb_ops].[LegacyIdMappings]
        WHERE [ImportBatchId] = @BatchId
          AND [EntityType] = @EntityType
          AND [LegacyId] = @LegacyValue
    )
    BEGIN
        UPDATE [tb_ops].[LegacyIdMappings]
        SET [NewEntityId] = @EntityId
        WHERE [ImportBatchId] = @BatchId
          AND [EntityType] = @EntityType
          AND [LegacyId] = @LegacyValue;
    END
    ELSE
    BEGIN
        INSERT INTO [tb_ops].[LegacyIdMappings]
        (
            [ImportBatchId],
            [EntityType],
            [LegacyId],
            [NewEntityId]
        )
        VALUES
        (
            @BatchId,
            @EntityType,
            @LegacyValue,
            @EntityId
        );
    END;
END;
GO

IF OBJECT_ID(N'tb_app.CompleteImportBatch', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[CompleteImportBatch];
GO

CREATE PROCEDURE [tb_app].[CompleteImportBatch]
    @BatchId uniqueidentifier,
    @Succeeded bit,
    @ImportedCount int,
    @Message nvarchar(max) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    UPDATE [tb_ops].[ImportBatches]
    SET
        [Status] = CASE WHEN @Succeeded = 1 THEN N'Succeeded' ELSE N'Failed' END,
        [ImportedCount] = CASE WHEN @ImportedCount < 0 THEN 0 ELSE @ImportedCount END,
        [Message] = COALESCE(@Message, N''),
        [CompletedAtUtc] = SYSUTCDATETIME()
    WHERE [Id] = @BatchId
      AND [OwnerWindowsSid] = @UserSid
      AND [Status] = N'Started';

    IF @@ROWCOUNT = 0
        THROW 51463, N'The import batch is missing, final, or owned by another user.', 1;
END;
GO

IF OBJECT_ID(N'tb_app.GetImportBatches', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetImportBatches];
GO

CREATE PROCEDURE [tb_app].[GetImportBatches]
    @IncludeAllUsers bit = 0,
    @Limit int = 100
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IncludeAllUsers = 1 AND @IsManager <> 1 AND @IsAdmin <> 1
        THROW 51464, N'Only a Manager or Admin may read other users'' imports.', 1;

    SET @Limit =
        CASE WHEN @Limit < 1 THEN 1 WHEN @Limit > 1000 THEN 1000 ELSE @Limit END;

    SELECT TOP (@Limit)
        [Id] AS [BatchId],
        [SourceSystem] AS [Source],
        [FileName],
        [FileHash],
        [Status],
        [ReadCount],
        [ImportedCount],
        [SkippedCount],
        [ErrorCount],
        [Message],
        [StartedAtUtc],
        [CompletedAtUtc],
        [RowVersion]
    FROM [tb_ops].[ImportBatches]
    WHERE @IncludeAllUsers = 1 OR [OwnerWindowsSid] = @UserSid
    ORDER BY [StartedAtUtc] DESC;
END;
GO

PRINT N'TechBench V0002 client sync, synchronization lease, snapshot, and import procedures created.';
GO

-- ============================================================================
-- END 44-V0002-SyncImportProcedures.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 45-V0003-SharedReferenceProcedures.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'tb_app.GetRepositoryCapabilities', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetRepositoryCapabilities];
GO

CREATE PROCEDURE [tb_app].[GetRepositoryCapabilities]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT
        CONVERT(int, 3) AS [SchemaVersion],
        CONVERT(bit, 0) AS [FullTextSearchAvailable],
        CONVERT(bit, 1) AS [SupportsTickets],
        CONVERT(bit, 1) AS [SupportsWorkEntries],
        CONVERT(bit, 1) AS [SupportsPrivateNotes],
        CONVERT(bit, 1) AS [SupportsPostingLeases],
        CONVERT(bit, 1) AS [SupportsSyncLeases],
        CONVERT(bit, 1) AS [SupportsImports];
END;
GO

IF OBJECT_ID(N'tb_app.GetCommonLinks', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetCommonLinks];
GO

CREATE PROCEDURE [tb_app].[GetCommonLinks]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT
        [Id],
        [ScopeType],
        [Name],
        [Url],
        [SortOrder],
        [BuiltInKey],
        [CreatedAtUtc] AS [CreatedAt],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[CommonLinks]
    WHERE [ScopeType] = N'Organization'
    ORDER BY [SortOrder], [Name], [Id];
END;
GO

IF OBJECT_ID(N'tb_app.SaveCommonLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveCommonLink];
GO

CREATE PROCEDURE [tb_app].[SaveCommonLink]
    @Id int = NULL,
    @ScopeType nvarchar(20) = N'Organization',
    @Name nvarchar(160),
    @Url nvarchar(2048),
    @SortOrder int = 0,
    @BuiltInKey nvarchar(120) = NULL,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @ScopeType =
        COALESCE(NULLIF(LTRIM(RTRIM(@ScopeType)), N''), N'Organization');
    SET @Name = NULLIF(LTRIM(RTRIM(@Name)), N'');
    SET @Url = NULLIF(LTRIM(RTRIM(@Url)), N'');
    SET @BuiltInKey = NULLIF(LTRIM(RTRIM(@BuiltInKey)), N'');

    IF @ScopeType <> N'Organization'
        THROW 51300, N'Common Links are organization-scoped in schema version 3.', 1;
    IF @IsAdmin <> 1
        THROW 51301, N'Only an Admin may save organization Common Links.', 1;
    IF @Name IS NULL OR @Url IS NULL
        THROW 51300, N'Common-link name and URL are required.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51302, N'ExpectedRowVersion is required when updating a Common Link.', 1;
    IF @Id IS NOT NULL
       AND EXISTS
       (
           SELECT 1
           FROM [tb_data].[CommonLinks]
           WHERE [Id] = @Id
             AND [BuiltInKey] IS NOT NULL
       )
        THROW 51303, N'Built-in Common Links cannot be changed.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @UrlHash binary(32) =
        CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), @Url)));
    DECLARE @Action nvarchar(120);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[CommonLinks]
            (
                [ScopeType],
                [OwnerWindowsSid],
                [Name],
                [Url],
                [UrlHash],
                [SortOrder],
                [BuiltInKey],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                N'Organization',
                NULL,
                @Name,
                @Url,
                @UrlHash,
                @SortOrder,
                @BuiltInKey,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );

            SET @Id = CONVERT(int, SCOPE_IDENTITY());
            SET @Action = N'CommonLinkCreated';
        END
        ELSE
        BEGIN
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[CommonLinks] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = @Id
                  AND [ScopeType] = N'Organization'
            )
                THROW 51304, N'The organization Common Link does not exist.', 1;

            UPDATE [tb_data].[CommonLinks]
            SET
                [ScopeType] = N'Organization',
                [OwnerWindowsSid] = NULL,
                [Name] = @Name,
                [Url] = @Url,
                [UrlHash] = @UrlHash,
                [SortOrder] = @SortOrder,
                [BuiltInKey] = @BuiltInKey,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51305, N'The Common Link changed after it was loaded.', 1;

            SET @Action = N'CommonLinkUpdated';
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
            @EntityType = N'CommonLink',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [ScopeType],
        [Name],
        [Url],
        [SortOrder],
        [BuiltInKey],
        [CreatedAtUtc] AS [CreatedAt],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[CommonLinks]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteCommonLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteCommonLink];
GO

CREATE PROCEDURE [tb_app].[DeleteCommonLink]
    @Id int,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51306, N'Only an Admin may delete organization Common Links.', 1;
    IF EXISTS
    (
        SELECT 1
        FROM [tb_data].[CommonLinks]
        WHERE [Id] = @Id
          AND [BuiltInKey] IS NOT NULL
    )
        THROW 51303, N'Built-in Common Links cannot be removed.', 1;

    DELETE FROM [tb_data].[CommonLinks]
    WHERE [Id] = @Id
      AND [ScopeType] = N'Organization'
      AND [RowVersion] = @ExpectedRowVersion;

    IF @@ROWCOUNT = 0
        THROW 51307, N'The Common Link was not found or changed after it was loaded.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'CommonLinkDeleted',
        @EntityType = N'CommonLink',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.GetClientAliases', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetClientAliases];
GO

CREATE PROCEDURE [tb_app].[GetClientAliases]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT
        [Id],
        [ScopeType],
        [Alias],
        [ClientId],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[ClientAliases]
    WHERE [ScopeType] = N'Organization'
    ORDER BY [Alias];
END;
GO

IF OBJECT_ID(N'tb_app.SaveClientAlias', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveClientAlias];
GO

CREATE PROCEDURE [tb_app].[SaveClientAlias]
    @Id bigint = NULL,
    @ScopeType nvarchar(20) = N'Organization',
    @Alias nvarchar(240),
    @ClientId int,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @ScopeType =
        COALESCE(NULLIF(LTRIM(RTRIM(@ScopeType)), N''), N'Organization');
    SET @Alias = NULLIF(LTRIM(RTRIM(@Alias)), N'');

    IF @ScopeType <> N'Organization'
        THROW 51310, N'Client aliases are organization-scoped in schema version 3.', 1;
    IF @Alias IS NULL
        THROW 51310, N'Client alias is required.', 1;
    IF NOT EXISTS (SELECT 1 FROM [tb_data].[Clients] WHERE [Id] = @ClientId)
        THROW 51310, N'The selected client does not exist.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @StoredAlias nvarchar(240);
    DECLARE @StoredClientId int;
    DECLARE @StoredRowVersion binary(8);
    DECLARE @Action nvarchar(120);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            SELECT
                @Id = [Id],
                @StoredAlias = [Alias],
                @StoredClientId = [ClientId],
                @StoredRowVersion = [RowVersion]
            FROM [tb_data].[ClientAliases] WITH (UPDLOCK, HOLDLOCK)
            WHERE [ScopeType] = N'Organization'
              AND [Alias] = @Alias;

            IF @Id IS NULL
            BEGIN
                INSERT INTO [tb_data].[ClientAliases]
                (
                    [ScopeType],
                    [OwnerWindowsSid],
                    [Alias],
                    [ClientId],
                    [CreatedByWindowsSid],
                    [UpdatedByWindowsSid],
                    [CreatedAtUtc],
                    [UpdatedAtUtc]
                )
                VALUES
                (
                    N'Organization',
                    NULL,
                    @Alias,
                    @ClientId,
                    @UserSid,
                    @UserSid,
                    @NowUtc,
                    @NowUtc
                );

                SET @Id = CONVERT(bigint, SCOPE_IDENTITY());
                SET @Action = N'ClientAliasCreated';
            END
            ELSE IF @StoredClientId <> @ClientId
            BEGIN
                IF @IsAdmin <> 1
                    THROW 51311, N'Only an Admin may change an existing organization client alias.', 1;

                UPDATE [tb_data].[ClientAliases]
                SET
                    [ClientId] = @ClientId,
                    [UpdatedByWindowsSid] = @UserSid,
                    [UpdatedAtUtc] = @NowUtc
                WHERE [Id] = @Id
                  AND [RowVersion] = @StoredRowVersion;

                IF @@ROWCOUNT = 0
                    THROW 51312, N'The client alias changed while it was being saved.', 1;

                SET @Action = N'ClientAliasUpdated';
            END;
            /* Same alias + client is intentionally an idempotent success. */
        END
        ELSE
        BEGIN
            SELECT
                @StoredAlias = [Alias],
                @StoredClientId = [ClientId],
                @StoredRowVersion = [RowVersion]
            FROM [tb_data].[ClientAliases] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @Id
              AND [ScopeType] = N'Organization';

            IF @StoredAlias IS NULL
                THROW 51313, N'The organization client alias does not exist.', 1;

            IF @StoredAlias <> @Alias OR @StoredClientId <> @ClientId
            BEGIN
                IF @IsAdmin <> 1
                    THROW 51311, N'Only an Admin may change an existing organization client alias.', 1;
                IF @ExpectedRowVersion IS NULL
                    THROW 51314, N'ExpectedRowVersion is required when changing a client alias.', 1;

                UPDATE [tb_data].[ClientAliases]
                SET
                    [Alias] = @Alias,
                    [ClientId] = @ClientId,
                    [UpdatedByWindowsSid] = @UserSid,
                    [UpdatedAtUtc] = @NowUtc
                WHERE [Id] = @Id
                  AND [RowVersion] = @ExpectedRowVersion;

                IF @@ROWCOUNT = 0
                    THROW 51312, N'The client alias changed after it was loaded.', 1;

                SET @Action = N'ClientAliasUpdated';
            END;
            /* Reusing the same loaded mapping is also idempotent for all users. */
        END;

        IF @Action IS NOT NULL
        BEGIN
            DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
            EXEC [tb_security].[WriteAuditEvent]
                @Action = @Action,
                @EntityType = N'ClientAlias',
                @EntityId = @AuditEntityId,
                @RequestId = @RequestId;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [ScopeType],
        [Alias],
        [ClientId],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[ClientAliases]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteClientAlias', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteClientAlias];
GO

CREATE PROCEDURE [tb_app].[DeleteClientAlias]
    @Id bigint,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51315, N'Only an Admin may delete organization client aliases.', 1;

    DELETE FROM [tb_data].[ClientAliases]
    WHERE [Id] = @Id
      AND [ScopeType] = N'Organization'
      AND [RowVersion] = @ExpectedRowVersion;

    IF @@ROWCOUNT = 0
        THROW 51316, N'The client alias was not found or changed after it was loaded.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'ClientAliasDeleted',
        @EntityType = N'ClientAlias',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.SaveTemplate', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveTemplate];
GO

CREATE PROCEDURE [tb_app].[SaveTemplate]
    @Id int = NULL,
    @ScopeType nvarchar(20) = N'Organization',
    @Name nvarchar(160),
    @Category nvarchar(160) = N'',
    @TemplateText nvarchar(max) = N'',
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @ScopeType =
        COALESCE(NULLIF(LTRIM(RTRIM(@ScopeType)), N''), N'Organization');
    SET @Name = NULLIF(LTRIM(RTRIM(@Name)), N'');
    SET @Category = COALESCE(LTRIM(RTRIM(@Category)), N'');
    SET @TemplateText = COALESCE(@TemplateText, N'');

    IF @ScopeType <> N'Organization'
        THROW 51330, N'Templates are organization-scoped in schema version 3; legacy personal templates are read-only.', 1;
    IF @IsAdmin <> 1
        THROW 51331, N'Only an Admin may save organization templates.', 1;
    IF @Name IS NULL
        THROW 51330, N'Template name is required.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51332, N'ExpectedRowVersion is required when updating a template.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @Action nvarchar(120);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[Templates]
            (
                [ScopeType],
                [OwnerWindowsSid],
                [Name],
                [Category],
                [TemplateText],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                N'Organization',
                NULL,
                @Name,
                @Category,
                @TemplateText,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );

            SET @Id = CONVERT(int, SCOPE_IDENTITY());
            SET @Action = N'TemplateCreated';
        END
        ELSE
        BEGIN
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[Templates] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = @Id
                  AND [ScopeType] = N'Organization'
                  AND [OwnerWindowsSid] IS NULL
            )
                THROW 51333, N'The organization template does not exist; legacy personal templates are read-only.', 1;

            UPDATE [tb_data].[Templates]
            SET
                [Name] = @Name,
                [Category] = @Category,
                [TemplateText] = @TemplateText,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id
              AND [ScopeType] = N'Organization'
              AND [OwnerWindowsSid] IS NULL
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51334, N'The template changed after it was loaded.', 1;

            SET @Action = N'TemplateUpdated';
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
            @EntityType = N'Template',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [ScopeType],
        [Name],
        [Category],
        [TemplateText],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[Templates]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteTemplate', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteTemplate];
GO

CREATE PROCEDURE [tb_app].[DeleteTemplate]
    @Id int,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51335, N'Only an Admin may delete organization templates.', 1;

    DELETE FROM [tb_data].[Templates]
    WHERE [Id] = @Id
      AND [ScopeType] = N'Organization'
      AND [OwnerWindowsSid] IS NULL
      AND [RowVersion] = @ExpectedRowVersion;

    IF @@ROWCOUNT = 0
        THROW 51336, N'The organization template was not found or changed; legacy personal templates are read-only.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'TemplateDeleted',
        @EntityType = N'Template',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.SaveUserSetting', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveUserSetting];
GO

CREATE PROCEDURE [tb_app].[SaveUserSetting]
    @SettingKey nvarchar(200),
    @SettingValue nvarchar(max),
    @ExpectedRowVersion binary(8) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @SettingKey = NULLIF(LTRIM(RTRIM(@SettingKey)), N'');
    SET @SettingValue = COALESCE(@SettingValue, N'');

    IF @SettingKey IS NULL
        THROW 51220, N'SettingKey is required.', 1;
    IF @SettingKey IN
       (
           N'Whd.BaseUrl',
           N'Whd.AuthenticationMode',
           N'Sage.ActivityItemId'
       )
        THROW 51320, N'This setting is organization-scoped and may be changed only through organization settings.', 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF EXISTS
        (
            SELECT 1
            FROM [tb_user].[UserSettings] WITH (UPDLOCK, HOLDLOCK)
            WHERE [OwnerWindowsSid] = @UserSid
              AND [SettingKey] = @SettingKey
        )
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 51221, N'ExpectedRowVersion is required for an existing user setting.', 1;

            UPDATE [tb_user].[UserSettings]
            SET
                [SettingValue] = @SettingValue,
                [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [OwnerWindowsSid] = @UserSid
              AND [SettingKey] = @SettingKey
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51222, N'The user setting changed after it was loaded.', 1;
        END
        ELSE
        BEGIN
            IF @ExpectedRowVersion IS NOT NULL
                THROW 51222, N'The user setting changed after it was loaded.', 1;

            INSERT INTO [tb_user].[UserSettings]
            (
                [OwnerWindowsSid],
                [SettingKey],
                [SettingValue]
            )
            VALUES
            (
                @UserSid,
                @SettingKey,
                @SettingValue
            );
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        CONVERT(nvarchar(20), N'User') AS [ScopeType],
        [SettingKey],
        [SettingValue],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_user].[UserSettings]
    WHERE [OwnerWindowsSid] = @UserSid
      AND [SettingKey] = @SettingKey;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteUserSetting', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteUserSetting];
GO

CREATE PROCEDURE [tb_app].[DeleteUserSetting]
    @SettingKey nvarchar(200),
    @ExpectedRowVersion binary(8) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @SettingKey = NULLIF(LTRIM(RTRIM(@SettingKey)), N'');

    IF @SettingKey IN
       (
           N'Whd.BaseUrl',
           N'Whd.AuthenticationMode',
           N'Sage.ActivityItemId'
       )
        THROW 51320, N'This setting is organization-scoped and may be changed only through organization settings.', 1;

    DELETE FROM [tb_user].[UserSettings]
    WHERE [OwnerWindowsSid] = @UserSid
      AND [SettingKey] = @SettingKey
      AND (@ExpectedRowVersion IS NULL OR [RowVersion] = @ExpectedRowVersion);
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveTemplate', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveTemplate];
GO

CREATE PROCEDURE [tb_app].[AdminSaveTemplate]
    @Id int = NULL,
    @Name nvarchar(160),
    @Category nvarchar(160) = N'',
    @TemplateText nvarchar(max) = N'',
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;

    EXEC [tb_app].[SaveTemplate]
        @Id = @Id,
        @ScopeType = N'Organization',
        @Name = @Name,
        @Category = @Category,
        @TemplateText = @TemplateText,
        @ExpectedRowVersion = @ExpectedRowVersion,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveCommonLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveCommonLink];
GO

CREATE PROCEDURE [tb_app].[AdminSaveCommonLink]
    @Id int = NULL,
    @Name nvarchar(160),
    @Url nvarchar(2048),
    @SortOrder int = 0,
    @BuiltInKey nvarchar(120) = NULL,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;

    EXEC [tb_app].[SaveCommonLink]
        @Id = @Id,
        @ScopeType = N'Organization',
        @Name = @Name,
        @Url = @Url,
        @SortOrder = @SortOrder,
        @BuiltInKey = @BuiltInKey,
        @ExpectedRowVersion = @ExpectedRowVersion,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveClientAlias', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveClientAlias];
GO

CREATE PROCEDURE [tb_app].[AdminSaveClientAlias]
    @Alias nvarchar(240),
    @ClientId int,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;
    DECLARE @Id bigint;
    DECLARE @ExpectedRowVersion binary(8);

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51317, N'Only an Admin may use the administrative client-alias operation.', 1;

    SELECT
        @Id = [Id],
        @ExpectedRowVersion = [RowVersion]
    FROM [tb_data].[ClientAliases]
    WHERE [ScopeType] = N'Organization'
      AND [Alias] = @Alias;

    EXEC [tb_app].[SaveClientAlias]
        @Id = @Id,
        @ScopeType = N'Organization',
        @Alias = @Alias,
        @ClientId = @ClientId,
        @ExpectedRowVersion = @ExpectedRowVersion,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.AdminDeleteClientAlias', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminDeleteClientAlias];
GO

CREATE PROCEDURE [tb_app].[AdminDeleteClientAlias]
    @Alias nvarchar(240),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;
    DECLARE @Id bigint;
    DECLARE @ExpectedRowVersion binary(8);

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51317, N'Only an Admin may use the administrative client-alias operation.', 1;

    SELECT
        @Id = [Id],
        @ExpectedRowVersion = [RowVersion]
    FROM [tb_data].[ClientAliases]
    WHERE [ScopeType] = N'Organization'
      AND [Alias] = @Alias;

    IF @Id IS NOT NULL
    BEGIN
        EXEC [tb_app].[DeleteClientAlias]
            @Id = @Id,
            @ExpectedRowVersion = @ExpectedRowVersion,
            @RequestId = @RequestId;
    END;
END;
GO

PRINT N'TechBench V0003 shared-reference procedures created.';
GO

-- ============================================================================
-- END 45-V0003-SharedReferenceProcedures.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 46-V0004-AdminSharedProcedures.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/*
    V0004 centralizes organization-wide mutation authority in TechBench_Admins.
    Existing signatures remain stable for the desktop client.
*/

IF OBJECT_ID(N'tb_security.GetCurrentAccess', N'P') IS NOT NULL
    DROP PROCEDURE [tb_security].[GetCurrentAccess];
GO

CREATE PROCEDURE [tb_security].[GetCurrentAccess]
    @UserSid varbinary(85) OUTPUT,
    @IsManager bit OUTPUT,
    @IsAdmin bit OUTPUT,
    @IsSyncOperator bit OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @LoginName nvarchar(256);
    DECLARE @DisplayName nvarchar(160);
    DECLARE @IsTechnician bit;

    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @UserSid OUTPUT,
        @LoginName = @LoginName OUTPUT,
        @DisplayName = @DisplayName OUTPUT,
        @IsTechnician = @IsTechnician OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    /*
        The legacy Sync Operator role remains visible in the user context for
        upgrade compatibility, but it no longer authorizes a shared mutation.
        Existing sync procedures therefore enforce Admin-only access without
        changing any public procedure signature.
    */
    IF @IsAdmin <> 1
        SET @IsSyncOperator = 0;
END;
GO

IF OBJECT_ID(N'tb_app.GetRepositoryCapabilities', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetRepositoryCapabilities];
GO

CREATE PROCEDURE [tb_app].[GetRepositoryCapabilities]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT
        CONVERT(int, 4) AS [SchemaVersion],
        CONVERT(bit, 0) AS [FullTextSearchAvailable],
        CONVERT(bit, 1) AS [SupportsTickets],
        CONVERT(bit, 1) AS [SupportsWorkEntries],
        CONVERT(bit, 1) AS [SupportsPrivateNotes],
        CONVERT(bit, 1) AS [SupportsPostingLeases],
        CONVERT(bit, 1) AS [SupportsSyncLeases],
        CONVERT(bit, 1) AS [SupportsImports];
END;
GO

IF OBJECT_ID(N'tb_app.EnsureWorkspaceDefaults', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[EnsureWorkspaceDefaults];
GO

CREATE PROCEDURE [tb_app].[EnsureWorkspaceDefaults]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51500, N'Only an Admin may initialize shared workspace defaults.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @ChangedCount int = 0;

    DECLARE @DefaultLinks TABLE
    (
        [BuiltInKey] nvarchar(120) NOT NULL PRIMARY KEY,
        [Name] nvarchar(160) NOT NULL,
        [Url] nvarchar(2048) NOT NULL,
        [UrlHash] binary(32) NOT NULL,
        [SortOrder] int NOT NULL
    );

    INSERT INTO @DefaultLinks([BuiltInKey], [Name], [Url], [UrlHash], [SortOrder])
    VALUES
        (N'watchguard-cloud', N'WatchGuard Cloud', N'https://cloud.watchguard.com/',
            CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), N'https://cloud.watchguard.com/'))), 0),
        (N'microsoft-365-admin', N'Microsoft 365 Admin Center', N'https://admin.microsoft.com/',
            CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), N'https://admin.microsoft.com/'))), 1),
        (N'barracuda-cloud-control', N'Barracuda Cloud Control', N'https://login.barracuda.com/',
            CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), N'https://login.barracuda.com/'))), 2),
        (N'eset-protect', N'ESET PROTECT Console', N'https://protect.eset.com/',
            CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), N'https://protect.eset.com/'))), 3),
        (N'email2phone', N'Email2Phone', N'https://user.email2phone.net/client/#/authentication/signin',
            CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), N'https://user.email2phone.net/client/#/authentication/signin'))), 4),
        (N'godaddy-dns', N'GoDaddy', N'https://dcc.godaddy.com/control/portfolio',
            CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), N'https://dcc.godaddy.com/control/portfolio'))), 10),
        (N'network-solutions-dns', N'Network Solutions', N'https://www.networksolutions.com/my-account/login',
            CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), N'https://www.networksolutions.com/my-account/login'))), 11);

    DECLARE @DefaultTemplates TABLE
    (
        [Name] nvarchar(160) NOT NULL PRIMARY KEY,
        [Category] nvarchar(160) NOT NULL,
        [TemplateText] nvarchar(max) NOT NULL
    );

    INSERT INTO @DefaultTemplates([Name], [Category], [TemplateText])
    VALUES
        (N'Exchange certificate update', N'Microsoft 365',
            N'Updated Exchange certificate binding, verified mail flow, and confirmed Outlook connectivity.'),
        (N'VPN troubleshooting', N'Network',
            N'Investigated VPN connection failure, validated credentials and MFA status, reviewed client logs, and confirmed successful reconnect.'),
        (N'Microsoft 365 licensing', N'Microsoft 365',
            N'Reviewed Microsoft 365 license assignment, adjusted user licensing, and confirmed service availability.'),
        (N'Firewall rule change', N'Network',
            N'Reviewed requested firewall rule change, validated source and destination scope, applied the rule, and confirmed expected traffic.'),
        (N'Password reset', N'Help Desk',
            N'Reset user password, confirmed MFA status, and verified successful sign-in with the user.'),
        (N'Backup verification', N'Infrastructure',
            N'Reviewed backup job status, checked warnings or failures, and documented restore-point availability.'),
        (N'Server reboot/maintenance', N'Infrastructure',
            N'Performed scheduled server maintenance, rebooted services as needed, and verified post-maintenance availability.');

    DECLARE @DefaultOrganizationSettings TABLE
    (
        [SettingKey] nvarchar(200) NOT NULL PRIMARY KEY,
        [SettingValue] nvarchar(max) NOT NULL
    );

    INSERT INTO @DefaultOrganizationSettings([SettingKey], [SettingValue])
    VALUES
        (N'Whd.AutoSyncEnabled', N'true'),
        (N'Whd.AutoSyncMinutes', N'5');

    BEGIN TRY
        BEGIN TRANSACTION;

        /*
            Auto-sync settings are required runtime configuration, so repair a
            missing row on every Admin initialization without overwriting an
            existing Admin value.
        */
        INSERT INTO [tb_data].[OrganizationSettings]
        (
            [SettingKey],
            [SettingValue],
            [UpdatedByWindowsSid],
            [UpdatedAtUtc]
        )
        SELECT
            default_setting.[SettingKey],
            default_setting.[SettingValue],
            @UserSid,
            @NowUtc
        FROM @DefaultOrganizationSettings AS default_setting
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[OrganizationSettings] WITH (UPDLOCK, HOLDLOCK)
            WHERE [SettingKey] = default_setting.[SettingKey]
        );

        SET @ChangedCount += @@ROWCOUNT;

        DECLARE @InitializeWorkspaceCatalogs bit = 0;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[OrganizationSettings] WITH (UPDLOCK, HOLDLOCK)
            WHERE [SettingKey] = N'WorkspaceDefaults.Initialized'
        )
            SET @InitializeWorkspaceCatalogs = 1;

        IF @InitializeWorkspaceCatalogs = 1
        BEGIN
            /*
                The original link/template catalogs are a one-time V0004 seed.
                Once marked initialized, later Admin rename/delete operations
                remain authoritative and are never recreated on app startup.
            */
            INSERT INTO [tb_data].[CommonLinks]
            (
                [ScopeType],
                [OwnerWindowsSid],
                [Name],
                [Url],
                [UrlHash],
                [SortOrder],
                [BuiltInKey],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            SELECT
                N'Organization',
                NULL,
                default_link.[Name],
                default_link.[Url],
                default_link.[UrlHash],
                default_link.[SortOrder],
                default_link.[BuiltInKey],
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            FROM @DefaultLinks AS default_link
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[CommonLinks] WITH (UPDLOCK, HOLDLOCK)
                WHERE [BuiltInKey] = default_link.[BuiltInKey]
                   OR
                   (
                       [ScopeType] = N'Organization'
                       AND [UrlHash] = default_link.[UrlHash]
                   )
            );

            SET @ChangedCount += @@ROWCOUNT;

            /*
                Template names identify defaults during the one-time seed.
                Existing organization templates keep their category and text.
            */
            INSERT INTO [tb_data].[Templates]
            (
                [ScopeType],
                [OwnerWindowsSid],
                [Name],
                [Category],
                [TemplateText],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            SELECT
                N'Organization',
                NULL,
                default_template.[Name],
                default_template.[Category],
                default_template.[TemplateText],
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            FROM @DefaultTemplates AS default_template
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[Templates] WITH (UPDLOCK, HOLDLOCK)
                WHERE [ScopeType] = N'Organization'
                  AND [Name] = default_template.[Name]
            );

            SET @ChangedCount += @@ROWCOUNT;

            INSERT INTO [tb_data].[OrganizationSettings]
            (
                [SettingKey],
                [SettingValue],
                [UpdatedByWindowsSid],
                [UpdatedAtUtc]
            )
            VALUES
            (
                N'WorkspaceDefaults.Initialized',
                N'4',
                @UserSid,
                @NowUtc
            );

            SET @ChangedCount += @@ROWCOUNT;
        END;

        IF @ChangedCount > 0
        BEGIN
            EXEC [tb_security].[WriteAuditEvent]
                @Action = N'WorkspaceDefaultsEnsured',
                @EntityType = N'WorkspaceDefaults',
                @EntityId = N'Organization';
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

/*
    Built-in links are ordinary Admin-managed catalog rows in V0004. Their key
    still prevents duplicate defaults; it no longer makes the row immutable.
*/
IF OBJECT_ID(N'tb_app.SaveCommonLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveCommonLink];
GO

CREATE PROCEDURE [tb_app].[SaveCommonLink]
    @Id int = NULL,
    @ScopeType nvarchar(20) = N'Organization',
    @Name nvarchar(160),
    @Url nvarchar(2048),
    @SortOrder int = 0,
    @BuiltInKey nvarchar(120) = NULL,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @ScopeType =
        COALESCE(NULLIF(LTRIM(RTRIM(@ScopeType)), N''), N'Organization');
    SET @Name = NULLIF(LTRIM(RTRIM(@Name)), N'');
    SET @Url = NULLIF(LTRIM(RTRIM(@Url)), N'');
    SET @BuiltInKey = NULLIF(LTRIM(RTRIM(@BuiltInKey)), N'');

    IF @ScopeType <> N'Organization'
        THROW 51300, N'Common Links are organization-scoped in schema version 4.', 1;
    IF @IsAdmin <> 1
        THROW 51301, N'Only an Admin may save organization Common Links.', 1;
    IF @Name IS NULL OR @Url IS NULL
        THROW 51300, N'Common-link name and URL are required.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51302, N'ExpectedRowVersion is required when updating a Common Link.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @UrlHash binary(32) =
        CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), @Url)));
    DECLARE @Action nvarchar(120);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[CommonLinks]
            (
                [ScopeType],
                [OwnerWindowsSid],
                [Name],
                [Url],
                [UrlHash],
                [SortOrder],
                [BuiltInKey],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                N'Organization',
                NULL,
                @Name,
                @Url,
                @UrlHash,
                @SortOrder,
                @BuiltInKey,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );

            SET @Id = CONVERT(int, SCOPE_IDENTITY());
            SET @Action = N'CommonLinkCreated';
        END
        ELSE
        BEGIN
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[CommonLinks] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = @Id
                  AND [ScopeType] = N'Organization'
            )
                THROW 51304, N'The organization Common Link does not exist.', 1;

            UPDATE [tb_data].[CommonLinks]
            SET
                [ScopeType] = N'Organization',
                [OwnerWindowsSid] = NULL,
                [Name] = @Name,
                [Url] = @Url,
                [UrlHash] = @UrlHash,
                [SortOrder] = @SortOrder,
                [BuiltInKey] = @BuiltInKey,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51305, N'The Common Link changed after it was loaded.', 1;

            SET @Action = N'CommonLinkUpdated';
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
            @EntityType = N'CommonLink',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [ScopeType],
        [Name],
        [Url],
        [SortOrder],
        [BuiltInKey],
        [CreatedAtUtc] AS [CreatedAt],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[CommonLinks]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteCommonLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteCommonLink];
GO

CREATE PROCEDURE [tb_app].[DeleteCommonLink]
    @Id int,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51306, N'Only an Admin may delete organization Common Links.', 1;
    IF EXISTS
    (
        SELECT 1
        FROM [tb_data].[CommonLinks]
        WHERE [Id] = @Id
          AND [BuiltInKey] IS NOT NULL
    )
        THROW 51303, N'Built-in Common Links may be edited but cannot be removed.', 1;

    DELETE FROM [tb_data].[CommonLinks]
    WHERE [Id] = @Id
      AND [ScopeType] = N'Organization'
      AND [RowVersion] = @ExpectedRowVersion;

    IF @@ROWCOUNT = 0
        THROW 51307, N'The Common Link was not found or changed after it was loaded.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'CommonLinkDeleted',
        @EntityType = N'CommonLink',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.SaveClientAlias', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveClientAlias];
GO

CREATE PROCEDURE [tb_app].[SaveClientAlias]
    @Id bigint = NULL,
    @ScopeType nvarchar(20) = N'Organization',
    @Alias nvarchar(240),
    @ClientId int,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51311, N'Only an Admin may save organization client aliases.', 1;

    SET @ScopeType =
        COALESCE(NULLIF(LTRIM(RTRIM(@ScopeType)), N''), N'Organization');
    SET @Alias = NULLIF(LTRIM(RTRIM(@Alias)), N'');

    IF @ScopeType <> N'Organization'
        THROW 51310, N'Client aliases are organization-scoped in schema version 4.', 1;
    IF @Alias IS NULL
        THROW 51310, N'Client alias is required.', 1;
    IF NOT EXISTS (SELECT 1 FROM [tb_data].[Clients] WHERE [Id] = @ClientId)
        THROW 51310, N'The selected client does not exist.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @StoredAlias nvarchar(240);
    DECLARE @StoredClientId int;
    DECLARE @StoredRowVersion binary(8);
    DECLARE @Action nvarchar(120);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            SELECT
                @Id = [Id],
                @StoredAlias = [Alias],
                @StoredClientId = [ClientId],
                @StoredRowVersion = [RowVersion]
            FROM [tb_data].[ClientAliases] WITH (UPDLOCK, HOLDLOCK)
            WHERE [ScopeType] = N'Organization'
              AND [Alias] = @Alias;

            IF @Id IS NULL
            BEGIN
                INSERT INTO [tb_data].[ClientAliases]
                (
                    [ScopeType],
                    [OwnerWindowsSid],
                    [Alias],
                    [ClientId],
                    [CreatedByWindowsSid],
                    [UpdatedByWindowsSid],
                    [CreatedAtUtc],
                    [UpdatedAtUtc]
                )
                VALUES
                (
                    N'Organization',
                    NULL,
                    @Alias,
                    @ClientId,
                    @UserSid,
                    @UserSid,
                    @NowUtc,
                    @NowUtc
                );

                SET @Id = CONVERT(bigint, SCOPE_IDENTITY());
                SET @Action = N'ClientAliasCreated';
            END
            ELSE IF @StoredClientId <> @ClientId
            BEGIN
                UPDATE [tb_data].[ClientAliases]
                SET
                    [ClientId] = @ClientId,
                    [UpdatedByWindowsSid] = @UserSid,
                    [UpdatedAtUtc] = @NowUtc
                WHERE [Id] = @Id
                  AND [RowVersion] = @StoredRowVersion;

                IF @@ROWCOUNT = 0
                    THROW 51312, N'The client alias changed while it was being saved.', 1;

                SET @Action = N'ClientAliasUpdated';
            END;
        END
        ELSE
        BEGIN
            SELECT
                @StoredAlias = [Alias],
                @StoredClientId = [ClientId],
                @StoredRowVersion = [RowVersion]
            FROM [tb_data].[ClientAliases] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @Id
              AND [ScopeType] = N'Organization';

            IF @StoredAlias IS NULL
                THROW 51313, N'The organization client alias does not exist.', 1;

            IF @StoredAlias <> @Alias OR @StoredClientId <> @ClientId
            BEGIN
                IF @ExpectedRowVersion IS NULL
                    THROW 51314, N'ExpectedRowVersion is required when changing a client alias.', 1;

                UPDATE [tb_data].[ClientAliases]
                SET
                    [Alias] = @Alias,
                    [ClientId] = @ClientId,
                    [UpdatedByWindowsSid] = @UserSid,
                    [UpdatedAtUtc] = @NowUtc
                WHERE [Id] = @Id
                  AND [RowVersion] = @ExpectedRowVersion;

                IF @@ROWCOUNT = 0
                    THROW 51312, N'The client alias changed after it was loaded.', 1;

                SET @Action = N'ClientAliasUpdated';
            END;
        END;

        IF @Action IS NOT NULL
        BEGIN
            DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
            EXEC [tb_security].[WriteAuditEvent]
                @Action = @Action,
                @EntityType = N'ClientAlias',
                @EntityId = @AuditEntityId,
                @RequestId = @RequestId;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [ScopeType],
        [Alias],
        [ClientId],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[ClientAliases]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.SaveUserSetting', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveUserSetting];
GO

CREATE PROCEDURE [tb_app].[SaveUserSetting]
    @SettingKey nvarchar(200),
    @SettingValue nvarchar(max),
    @ExpectedRowVersion binary(8) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @SettingKey = NULLIF(LTRIM(RTRIM(@SettingKey)), N'');
    SET @SettingValue = COALESCE(@SettingValue, N'');

    IF @SettingKey IS NULL
        THROW 51220, N'SettingKey is required.', 1;
    IF @SettingKey NOT IN
       (
           N'Whd.Username',
           N'Sage.Username',
           N'Sage.EmployeeId'
       )
        THROW 51510, N'Only approved per-user identity settings may be saved.', 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF EXISTS
        (
            SELECT 1
            FROM [tb_user].[UserSettings] WITH (UPDLOCK, HOLDLOCK)
            WHERE [OwnerWindowsSid] = @UserSid
              AND [SettingKey] = @SettingKey
        )
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 51221, N'ExpectedRowVersion is required for an existing user setting.', 1;

            UPDATE [tb_user].[UserSettings]
            SET
                [SettingValue] = @SettingValue,
                [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [OwnerWindowsSid] = @UserSid
              AND [SettingKey] = @SettingKey
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51222, N'The user setting changed after it was loaded.', 1;
        END
        ELSE
        BEGIN
            IF @ExpectedRowVersion IS NOT NULL
                THROW 51222, N'The user setting changed after it was loaded.', 1;

            INSERT INTO [tb_user].[UserSettings]
            (
                [OwnerWindowsSid],
                [SettingKey],
                [SettingValue]
            )
            VALUES
            (
                @UserSid,
                @SettingKey,
                @SettingValue
            );
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        CONVERT(nvarchar(20), N'User') AS [ScopeType],
        [SettingKey],
        [SettingValue],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_user].[UserSettings]
    WHERE [OwnerWindowsSid] = @UserSid
      AND [SettingKey] = @SettingKey;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteUserSetting', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteUserSetting];
GO

CREATE PROCEDURE [tb_app].[DeleteUserSetting]
    @SettingKey nvarchar(200),
    @ExpectedRowVersion binary(8) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @SettingKey = NULLIF(LTRIM(RTRIM(@SettingKey)), N'');

    IF @SettingKey IS NULL
        THROW 51220, N'SettingKey is required.', 1;
    IF @SettingKey NOT IN
       (
           N'Whd.Username',
           N'Sage.Username',
           N'Sage.EmployeeId',
           N'Whd.ApiToken',
           N'Sage.Password',
           N'Sage.DefaultCustomerId'
       )
        THROW 51511, N'Only approved per-user settings may be deleted.', 1;

    DELETE FROM [tb_user].[UserSettings]
    WHERE [OwnerWindowsSid] = @UserSid
      AND [SettingKey] = @SettingKey
      AND (@ExpectedRowVersion IS NULL OR [RowVersion] = @ExpectedRowVersion);
END;
GO

IF OBJECT_ID(N'tb_app.AdminGetOrganizationTags', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminGetOrganizationTags];
GO

CREATE PROCEDURE [tb_app].[AdminGetOrganizationTags]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51520, N'Only an Admin may manage organization tags.', 1;

    SELECT
        [Id],
        [Tag],
        [CreatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[OrganizationTags]
    ORDER BY [Tag], [Id];
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveOrganizationTag', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveOrganizationTag];
GO

CREATE PROCEDURE [tb_app].[AdminSaveOrganizationTag]
    @Id int = NULL,
    @Tag nvarchar(1000),
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51520, N'Only an Admin may manage organization tags.', 1;

    SET @Tag = NULLIF(LTRIM(RTRIM(@Tag)), N'');
    IF @Tag IS NULL
        THROW 51521, N'Tag is required.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51522, N'ExpectedRowVersion is required when updating an organization tag.', 1;

    DECLARE @TagHash binary(32) =
        CONVERT
        (
            binary(32),
            HASHBYTES
            (
                N'SHA2_256',
                CONVERT(varbinary(2000), UPPER(@Tag))
            )
        );
    DECLARE @Action nvarchar(120);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF EXISTS
        (
            SELECT 1
            FROM [tb_data].[OrganizationTags] WITH (UPDLOCK, HOLDLOCK)
            WHERE [TagHash] = @TagHash
              AND (@Id IS NULL OR [Id] <> @Id)
        )
            THROW 51523, N'An organization tag with the same normalized value already exists.', 1;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[OrganizationTags]
            (
                [Tag],
                [TagHash],
                [CreatedByWindowsSid],
                [CreatedAtUtc]
            )
            VALUES
            (
                @Tag,
                @TagHash,
                @UserSid,
                SYSUTCDATETIME()
            );

            SET @Id = CONVERT(int, SCOPE_IDENTITY());
            SET @Action = N'OrganizationTagCreated';
        END
        ELSE
        BEGIN
            UPDATE [tb_data].[OrganizationTags]
            SET
                [Tag] = @Tag,
                [TagHash] = @TagHash
            WHERE [Id] = @Id
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51524, N'The organization tag was not found or changed after it was loaded.', 1;

            SET @Action = N'OrganizationTagUpdated';
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
            @EntityType = N'OrganizationTag',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [Tag],
        [CreatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[OrganizationTags]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.AdminDeleteOrganizationTag', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminDeleteOrganizationTag];
GO

CREATE PROCEDURE [tb_app].[AdminDeleteOrganizationTag]
    @Id int,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51520, N'Only an Admin may manage organization tags.', 1;

    DELETE FROM [tb_data].[OrganizationTags]
    WHERE [Id] = @Id
      AND [RowVersion] = @ExpectedRowVersion;

    IF @@ROWCOUNT = 0
        THROW 51525, N'The organization tag was not found or changed after it was loaded.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'OrganizationTagDeleted',
        @EntityType = N'OrganizationTag',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

/*
    Work-entry tags remain the user's own comma-separated entry data. Saving a
    work entry no longer changes the Admin-managed organization tag catalog.
*/
IF OBJECT_ID(N'tb_app.SaveWorkEntry', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveWorkEntry];
GO

CREATE PROCEDURE [tb_app].[SaveWorkEntry]
    @Id int = NULL,
    @WorkDate date,
    @ClientId int = NULL,
    @ManualClientName nvarchar(240) = NULL,
    @TicketId int = NULL,
    @TicketNumberText nvarchar(120) = NULL,
    @HasTimeRange bit = 1,
    @StartTime time(0) = '00:00',
    @EndTime time(0) = '00:00',
    @DurationMinutes int,
    @Billable bit = 1,
    @Note nvarchar(max) = N'',
    @PersonalNote nvarchar(max) = NULL,
    @IncludePersonalNoteInWhd bit = 0,
    @Tags nvarchar(1000) = N'',
    @FollowUpState nvarchar(30) = N'None',
    @FollowUpDueDate date = NULL,
    @PostingStatus nvarchar(40) = N'Draft',
    @LastError nvarchar(max) = NULL,
    @ExpectedRowVersion binary(8) = NULL,
    @ExpectedPersonalNoteRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
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

    SET @ManualClientName = NULLIF(LTRIM(RTRIM(@ManualClientName)), N'');
    SET @TicketNumberText = NULLIF(LTRIM(RTRIM(@TicketNumberText)), N'');
    SET @Note = COALESCE(@Note, N'');
    SET @PersonalNote = NULLIF(LTRIM(RTRIM(@PersonalNote)), N'');
    SET @Tags = COALESCE(LTRIM(RTRIM(@Tags)), N'');
    SET @FollowUpState =
        COALESCE(NULLIF(LTRIM(RTRIM(@FollowUpState)), N''), N'None');
    SET @PostingStatus =
        COALESCE(NULLIF(LTRIM(RTRIM(@PostingStatus)), N''), N'Draft');
    SET @LastError = NULLIF(LTRIM(RTRIM(@LastError)), N'');

    IF @ClientId IS NULL AND @ManualClientName IS NULL
        THROW 51130, N'A client or manual client name is required.', 1;
    IF @ClientId IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM [tb_data].[Clients] WHERE [Id] = @ClientId)
        THROW 51130, N'The selected client does not exist.', 1;
    IF @TicketId IS NOT NULL
       AND NOT EXISTS
       (
           SELECT 1
           FROM [tb_data].[Tickets]
           WHERE [Id] = @TicketId
             AND (@ClientId IS NULL OR [ClientId] = @ClientId)
       )
        THROW 51130, N'The selected ticket does not exist for the selected client.', 1;
    IF @DurationMinutes < 0 OR @DurationMinutes > 1440
        THROW 51130, N'DurationMinutes must be between 0 and 1440.', 1;
    IF @FollowUpState NOT IN (N'None', N'FollowUp', N'Waiting', N'Completed')
        THROW 51130, N'FollowUpState is invalid.', 1;
    IF @PostingStatus NOT IN (N'Draft', N'Ready')
        THROW 51130, N'PostingStatus may be only Draft or Ready in SaveWorkEntry.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51131, N'ExpectedRowVersion is required when updating a work entry.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @Action nvarchar(120);
    DECLARE @ExistingWhdPosted bit;
    DECLARE @ExistingSagePosted bit;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[WorkEntries]
            (
                [OwnerWindowsSid],
                [WorkDate],
                [ClientId],
                [ManualClientName],
                [TicketId],
                [TicketNumberText],
                [HasTimeRange],
                [StartTime],
                [EndTime],
                [DurationMinutes],
                [Billable],
                [Note],
                [Tags],
                [FollowUpState],
                [FollowUpDueDate],
                [WhdPosted],
                [WhdPostedAtUtc],
                [SagePosted],
                [SagePostedAtUtc],
                [SageTicketNumber],
                [PostingStatus],
                [LastError],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @UserSid,
                @WorkDate,
                @ClientId,
                @ManualClientName,
                @TicketId,
                @TicketNumberText,
                @HasTimeRange,
                @StartTime,
                @EndTime,
                @DurationMinutes,
                @Billable,
                @Note,
                @Tags,
                @FollowUpState,
                @FollowUpDueDate,
                0,
                NULL,
                0,
                NULL,
                NULL,
                CASE WHEN @LastError IS NOT NULL THEN N'Failed' ELSE @PostingStatus END,
                @LastError,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );

            SET @Id = CONVERT(int, SCOPE_IDENTITY());
            SET @Action = N'WorkEntryCreated';
        END
        ELSE
        BEGIN
            SELECT
                @ExistingWhdPosted = [WhdPosted],
                @ExistingSagePosted = [SagePosted]
            FROM [tb_data].[WorkEntries] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @Id
              AND [OwnerWindowsSid] = @UserSid
              AND [RowVersion] = @ExpectedRowVersion;

            IF @ExistingWhdPosted IS NULL
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM [tb_data].[WorkEntries] WHERE [Id] = @Id)
                    THROW 51132, N'The work entry no longer exists.', 1;
                IF NOT EXISTS
                (
                    SELECT 1
                    FROM [tb_data].[WorkEntries]
                    WHERE [Id] = @Id
                      AND [OwnerWindowsSid] = @UserSid
                )
                    THROW 51133, N'Only the work-entry owner may update it.', 1;
                THROW 51134, N'The work entry changed after it was loaded.', 1;
            END;

            IF @ExistingSagePosted = 1
                THROW 51137, N'A work entry already posted to Sage cannot be changed.', 1;

            IF EXISTS
            (
                SELECT 1
                FROM [tb_ops].[PostingAttempts] WITH (UPDLOCK, HOLDLOCK)
                WHERE [WorkEntryId] = @Id
                  AND [OwnerWindowsSid] = @UserSid
                  AND [Status] IN (N'Started', N'Unknown')
            )
            OR EXISTS
            (
                SELECT 1
                FROM [tb_ops].[PostingLeases] WITH (UPDLOCK, HOLDLOCK)
                WHERE [WorkEntryId] = @Id
                  AND [OwnerWindowsSid] = @UserSid
            )
                THROW 51139, N'A work entry cannot be changed while an external posting attempt is active.', 1;

            UPDATE [tb_data].[WorkEntries]
            SET
                [WorkDate] = @WorkDate,
                [ClientId] = @ClientId,
                [ManualClientName] = @ManualClientName,
                [TicketId] = @TicketId,
                [TicketNumberText] = @TicketNumberText,
                [HasTimeRange] = @HasTimeRange,
                [StartTime] = @StartTime,
                [EndTime] = @EndTime,
                [DurationMinutes] = @DurationMinutes,
                [Billable] = @Billable,
                [Note] = @Note,
                [Tags] = @Tags,
                [FollowUpState] = @FollowUpState,
                [FollowUpDueDate] = @FollowUpDueDate,
                [PostingStatus] =
                    CASE
                        WHEN @LastError IS NOT NULL THEN N'Failed'
                        WHEN [WhdPosted] = 1 AND [SagePosted] = 1 THEN N'PostedToBoth'
                        WHEN [SagePosted] = 1 THEN N'PostedToSage'
                        WHEN [WhdPosted] = 1 THEN N'PostedToWhd'
                        ELSE @PostingStatus
                    END,
                [LastError] = @LastError,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id
              AND [OwnerWindowsSid] = @UserSid
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM [tb_data].[WorkEntries] WHERE [Id] = @Id)
                    THROW 51132, N'The work entry no longer exists.', 1;
                IF NOT EXISTS
                (
                    SELECT 1
                    FROM [tb_data].[WorkEntries]
                    WHERE [Id] = @Id
                      AND [OwnerWindowsSid] = @UserSid
                )
                    THROW 51133, N'Only the work-entry owner may update it.', 1;
                THROW 51134, N'The work entry changed after it was loaded.', 1;
            END;

            SET @Action = N'WorkEntryUpdated';
        END;

        IF EXISTS
        (
            SELECT 1
            FROM [tb_private].[WorkEntryPersonalNotes]
            WHERE [WorkEntryId] = @Id
              AND [OwnerWindowsSid] = @UserSid
        )
        BEGIN
            IF @ExpectedPersonalNoteRowVersion IS NULL
                THROW 51135, N'ExpectedPersonalNoteRowVersion is required for an existing personal note.', 1;

            IF @PersonalNote IS NULL AND @IncludePersonalNoteInWhd = 0
            BEGIN
                DELETE FROM [tb_private].[WorkEntryPersonalNotes]
                WHERE [WorkEntryId] = @Id
                  AND [OwnerWindowsSid] = @UserSid
                  AND [RowVersion] = @ExpectedPersonalNoteRowVersion;
            END
            ELSE
            BEGIN
                UPDATE [tb_private].[WorkEntryPersonalNotes]
                SET
                    [Note] = COALESCE(@PersonalNote, N''),
                    [IncludeInWhd] = @IncludePersonalNoteInWhd,
                    [UpdatedAtUtc] = @NowUtc
                WHERE [WorkEntryId] = @Id
                  AND [OwnerWindowsSid] = @UserSid
                  AND [RowVersion] = @ExpectedPersonalNoteRowVersion;
            END;

            IF @@ROWCOUNT = 0
                THROW 51136, N'The personal note changed after it was loaded.', 1;
        END
        ELSE
        BEGIN
            IF @ExpectedPersonalNoteRowVersion IS NOT NULL
                THROW 51136, N'The personal note changed after it was loaded.', 1;

            IF @PersonalNote IS NOT NULL OR @IncludePersonalNoteInWhd = 1
            BEGIN
                INSERT INTO [tb_private].[WorkEntryPersonalNotes]
                (
                    [WorkEntryId],
                    [OwnerWindowsSid],
                    [Note],
                    [IncludeInWhd],
                    [CreatedAtUtc],
                    [UpdatedAtUtc]
                )
                VALUES
                (
                    @Id,
                    @UserSid,
                    COALESCE(@PersonalNote, N''),
                    @IncludePersonalNoteInWhd,
                    @NowUtc,
                    @NowUtc
                );
            END;
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
            @EntityType = N'WorkEntry',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        work_entry.[Id],
        work_entry.[OwnerWindowsSid],
        work_entry.[WorkDate],
        work_entry.[ClientId],
        work_entry.[ManualClientName],
        work_entry.[TicketId],
        work_entry.[TicketNumberText],
        work_entry.[HasTimeRange],
        work_entry.[StartTime],
        work_entry.[EndTime],
        work_entry.[DurationMinutes],
        work_entry.[Billable],
        work_entry.[Note],
        personal_note.[Note] AS [InternalNote],
        personal_note.[Note] AS [PersonalNote],
        COALESCE(personal_note.[IncludeInWhd], 0) AS [IncludePersonalNoteInWhd],
        work_entry.[Tags],
        work_entry.[FollowUpState],
        work_entry.[FollowUpDueDate],
        work_entry.[WhdPosted],
        work_entry.[WhdPostedAtUtc] AS [WhdPostedAt],
        work_entry.[SagePosted],
        work_entry.[SagePostedAtUtc] AS [SagePostedAt],
        work_entry.[SageTicketNumber],
        work_entry.[PostingStatus],
        work_entry.[LastError],
        work_entry.[CreatedAtUtc] AS [CreatedAt],
        work_entry.[UpdatedAtUtc] AS [UpdatedAt],
        client.[Name] AS [ClientName],
        ticket.[TicketNumber],
        ticket.[Subject] AS [TicketSubject],
        work_entry.[RowVersion],
        personal_note.[RowVersion] AS [PersonalNoteRowVersion]
    FROM [tb_data].[WorkEntries] AS work_entry
    LEFT JOIN [tb_data].[Clients] AS client
        ON client.[Id] = work_entry.[ClientId]
    LEFT JOIN [tb_data].[Tickets] AS ticket
        ON ticket.[Id] = work_entry.[TicketId]
    LEFT JOIN [tb_private].[WorkEntryPersonalNotes] AS personal_note
        ON personal_note.[WorkEntryId] = work_entry.[Id]
       AND personal_note.[OwnerWindowsSid] = @UserSid
    WHERE work_entry.[Id] = @Id;
END;
GO

/*
    These lifecycle procedures previously relied only on the lease owner and
    their EXECUTE grants. Keep those ownership checks and add an explicit
    runtime Admin boundary so a future accidental grant cannot restore legacy
    Sync Operator write authority.
*/
IF OBJECT_ID(N'tb_app.ReleaseSyncLease', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ReleaseSyncLease];
GO

CREATE PROCEDURE [tb_app].[ReleaseSyncLease]
    @LeaseId uniqueidentifier,
    @DeviceId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51540, N'Only an Admin may release a synchronization lease.', 1;

    DELETE FROM [tb_ops].[SyncLeases]
    WHERE [LeaseId] = @LeaseId
      AND [OwnerWindowsSid] = @UserSid
      AND [DeviceId] = @DeviceId;
END;
GO

IF OBJECT_ID(N'tb_app.BeginSyncRun', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[BeginSyncRun];
GO

CREATE PROCEDURE [tb_app].[BeginSyncRun]
    @Source nvarchar(120),
    @LeaseId uniqueidentifier,
    @DeviceId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51540, N'Only an Admin may begin a synchronization run.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_ops].[SyncLeases]
        WHERE [SourceSystem] = @Source
          AND [LeaseId] = @LeaseId
          AND [OwnerWindowsSid] = @UserSid
          AND [DeviceId] = @DeviceId
          AND [ExpiresAtUtc] > SYSUTCDATETIME()
    )
        THROW 51440, N'The synchronization lease is missing, expired, or owned by another workstation.', 1;

    DECLARE @RunId uniqueidentifier = NEWID();

    INSERT INTO [tb_ops].[SyncRuns]
    (
        [Id],
        [SourceSystem],
        [LeaseId],
        [OwnerWindowsSid],
        [DeviceId],
        [Status]
    )
    VALUES
    (
        @RunId,
        @Source,
        @LeaseId,
        @UserSid,
        @DeviceId,
        N'Started'
    );

    SELECT @RunId AS [RunId];
END;
GO

IF OBJECT_ID(N'tb_app.CompleteSyncRun', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[CompleteSyncRun];
GO

CREATE PROCEDURE [tb_app].[CompleteSyncRun]
    @RunId uniqueidentifier,
    @Succeeded bit,
    @ItemCount int = 0,
    @Message nvarchar(max) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51540, N'Only an Admin may complete a synchronization run.', 1;

    UPDATE [tb_ops].[SyncRuns]
    SET
        [Status] = CASE WHEN @Succeeded = 1 THEN N'Succeeded' ELSE N'Failed' END,
        [ReadCount] = CASE WHEN @ItemCount < 0 THEN 0 ELSE @ItemCount END,
        [Message] = COALESCE(@Message, N''),
        [CompletedAtUtc] = SYSUTCDATETIME()
    WHERE [Id] = @RunId
      AND [OwnerWindowsSid] = @UserSid
      AND [Status] = N'Started';

    IF @@ROWCOUNT = 0
        THROW 51441, N'The synchronization run is missing, final, or owned by another user.', 1;
END;
GO

IF OBJECT_ID(N'tb_security.RenewSyncRunLease', N'P') IS NOT NULL
    DROP PROCEDURE [tb_security].[RenewSyncRunLease];
GO

CREATE PROCEDURE [tb_security].[RenewSyncRunLease]
    @RunId uniqueidentifier,
    @ExpectedSource nvarchar(40)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51540, N'Only an Admin may renew a synchronization run lease.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    UPDATE sync_lease
    SET
        [ExpiresAtUtc] = DATEADD(second, 300, @NowUtc),
        [UpdatedAtUtc] = @NowUtc
    FROM [tb_ops].[SyncLeases] AS sync_lease
    INNER JOIN [tb_ops].[SyncRuns] AS sync_run
        ON sync_run.[SourceSystem] = sync_lease.[SourceSystem]
       AND sync_run.[LeaseId] = sync_lease.[LeaseId]
       AND sync_run.[OwnerWindowsSid] = sync_lease.[OwnerWindowsSid]
       AND sync_run.[DeviceId] = sync_lease.[DeviceId]
    WHERE sync_run.[Id] = @RunId
      AND sync_run.[SourceSystem] = @ExpectedSource
      AND sync_run.[OwnerWindowsSid] = @UserSid
      AND sync_run.[Status] = N'Started'
      AND sync_lease.[ExpiresAtUtc] > @NowUtc;

    IF @@ROWCOUNT = 0
        THROW 51449, N'The source-specific synchronization lease expired or was replaced.', 1;
END;
GO

-- ============================================================================
-- END 46-V0004-AdminSharedProcedures.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 47-V0005-TechBenchV1ImportProcedures.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'tb_app.GetRepositoryCapabilities', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetRepositoryCapabilities];
GO

CREATE PROCEDURE [tb_app].[GetRepositoryCapabilities]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT
        CONVERT(int, 5) AS [SchemaVersion],
        CONVERT(bit, 0) AS [FullTextSearchAvailable],
        CONVERT(bit, 1) AS [SupportsTickets],
        CONVERT(bit, 1) AS [SupportsWorkEntries],
        CONVERT(bit, 1) AS [SupportsPrivateNotes],
        CONVERT(bit, 1) AS [SupportsPostingLeases],
        CONVERT(bit, 1) AS [SupportsSyncLeases],
        CONVERT(bit, 1) AS [SupportsImports],
        CONVERT(bit, 1) AS [SupportsTechBenchV1Import];
END;
GO

/* Reserve the TechBenchV1 source for the file-hash-aware import lifecycle. */
IF OBJECT_ID(N'tb_app.BeginImportBatch', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[BeginImportBatch];
GO

CREATE PROCEDURE [tb_app].[BeginImportBatch]
    @Source nvarchar(120),
    @ExpectedCount int = 0,
    @DeviceId uniqueidentifier = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @Source = NULLIF(LTRIM(RTRIM(@Source)), N'');
    IF @Source IS NULL OR LEN(@Source) > 80
        THROW 51460, N'Import source is required and cannot exceed 80 characters.', 1;
    IF @Source = N'TechBenchV1'
        THROW 51603, N'TechBenchV1 is reserved for BeginTechBenchV1Import, which requires file metadata.', 1;

    DECLARE @BatchId uniqueidentifier = NEWID();

    INSERT INTO [tb_ops].[ImportBatches]
    (
        [Id],
        [SourceSystem],
        [OwnerWindowsSid],
        [DeviceId],
        [Status],
        [ReadCount]
    )
    VALUES
    (
        @BatchId,
        @Source,
        @UserSid,
        @DeviceId,
        N'Started',
        CASE WHEN @ExpectedCount < 0 THEN 0 ELSE @ExpectedCount END
    );

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @BatchId);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'ImportBatchStarted',
        @EntityType = N'ImportBatch',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;

    SELECT @BatchId AS [BatchId], @BatchId AS [ImportBatchId];
END;
GO

IF OBJECT_ID(N'tb_app.BeginTechBenchV1Import', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[BeginTechBenchV1Import];
GO

CREATE PROCEDURE [tb_app].[BeginTechBenchV1Import]
    @FileName nvarchar(500),
    @FileHash char(64),
    @ExpectedCount int = 0,
    @DeviceId uniqueidentifier = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @FileName = NULLIF(LTRIM(RTRIM(@FileName)), N'');
    SET @FileHash = UPPER(LTRIM(RTRIM(@FileHash)));
    SET @ExpectedCount = CASE WHEN @ExpectedCount < 0 THEN 0 ELSE @ExpectedCount END;

    IF @FileName IS NULL
        THROW 51600, N'The TechBench V1 database file name is required.', 1;
    IF @FileHash IS NULL
       OR LEN(@FileHash) <> 64
       OR @FileHash COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9A-F]%'
        THROW 51601, N'FileHash must be a 64-character hexadecimal SHA-256 value.', 1;

    DECLARE @BatchId uniqueidentifier;
    DECLARE @Status nvarchar(30);
    DECLARE @ReadCount int;
    DECLARE @ImportedCount int;
    DECLARE @SkippedCount int;
    DECLARE @ConflictCount int;
    DECLARE @ErrorCount int;

    SELECT TOP (1)
        @BatchId = [Id],
        @Status = [Status],
        @ReadCount = [ReadCount],
        @ImportedCount = [ImportedCount],
        @SkippedCount = [SkippedCount],
        @ConflictCount = [ConflictCount],
        @ErrorCount = [ErrorCount]
    FROM [tb_ops].[ImportBatches]
    WHERE [OwnerWindowsSid] = @UserSid
      AND [SourceSystem] = N'TechBenchV1'
      AND [FileHash] = @FileHash
      AND [Status] = N'Succeeded'
      AND [ReadCount] = @ExpectedCount
      AND [ConflictCount] = 0
      AND [ErrorCount] = 0
    ORDER BY [CompletedAtUtc] DESC, [StartedAtUtc] DESC;

    IF @BatchId IS NOT NULL
    BEGIN
        SELECT
            @BatchId AS [BatchId],
            CONVERT(bit, 1) AS [AlreadyImported],
            CONVERT(bit, 0) AS [Resumed],
            @Status AS [Status],
            @ReadCount AS [ReadCount],
            @ImportedCount AS [ImportedCount],
            @SkippedCount AS [SkippedCount],
            @ConflictCount AS [ConflictCount],
            @ErrorCount AS [ErrorCount];
        RETURN;
    END;

    SET @BatchId = NULL;
    SELECT TOP (1)
        @BatchId = [Id],
        @Status = [Status],
        @ReadCount = [ReadCount],
        @ImportedCount = [ImportedCount],
        @SkippedCount = [SkippedCount],
        @ConflictCount = [ConflictCount],
        @ErrorCount = [ErrorCount]
    FROM [tb_ops].[ImportBatches]
    WHERE [OwnerWindowsSid] = @UserSid
      AND [SourceSystem] = N'TechBenchV1'
      AND [FileHash] = @FileHash
      AND [Status] = N'Started'
    ORDER BY [StartedAtUtc] DESC;

    IF @BatchId IS NOT NULL
    BEGIN
        SELECT
            @BatchId AS [BatchId],
            CONVERT(bit, 0) AS [AlreadyImported],
            CONVERT(bit, 1) AS [Resumed],
            @Status AS [Status],
            @ReadCount AS [ReadCount],
            @ImportedCount AS [ImportedCount],
            @SkippedCount AS [SkippedCount],
            @ConflictCount AS [ConflictCount],
            @ErrorCount AS [ErrorCount];
        RETURN;
    END;

    IF EXISTS
    (
        SELECT 1
        FROM [tb_ops].[ImportBatches]
        WHERE [OwnerWindowsSid] = @UserSid
          AND [SourceSystem] = N'TechBenchV1'
          AND [Status] = N'Started'
    )
        THROW 51602, N'Another TechBench V1 import for this user is still active. Resume, complete, or abandon that import first.', 1;

    SET @BatchId = NEWID();

    INSERT INTO [tb_ops].[ImportBatches]
    (
        [Id],
        [SourceSystem],
        [FileName],
        [FileHash],
        [OwnerWindowsSid],
        [DeviceId],
        [Status],
        [ReadCount]
    )
    VALUES
    (
        @BatchId,
        N'TechBenchV1',
        @FileName,
        @FileHash,
        @UserSid,
        @DeviceId,
        N'Started',
        @ExpectedCount
    );

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @BatchId);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'TechBenchV1ImportStarted',
        @EntityType = N'ImportBatch',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;

    SELECT
        @BatchId AS [BatchId],
        CONVERT(bit, 0) AS [AlreadyImported],
        CONVERT(bit, 0) AS [Resumed],
        N'Started' AS [Status],
        @ExpectedCount AS [ReadCount],
        CONVERT(int, 0) AS [ImportedCount],
        CONVERT(int, 0) AS [SkippedCount],
        CONVERT(int, 0) AS [ConflictCount],
        CONVERT(int, 0) AS [ErrorCount];
END;
GO

IF OBJECT_ID(N'tb_app.ResolveTechBenchV1Reference', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ResolveTechBenchV1Reference];
GO

/*
    Resolve exact V1 references inside SQL Server without exposing a capped
    shared-client or ticket list to the importer. External IDs are always
    source-qualified. Names are accepted only when the exact value identifies
    one shared client across organization aliases and canonical client names.
*/
CREATE PROCEDURE [tb_app].[ResolveTechBenchV1Reference]
    @ClientSourceSystem nvarchar(40) = NULL,
    @ClientExternalId nvarchar(500) = NULL,
    @SageCustomerId nvarchar(120) = NULL,
    @ClientName nvarchar(240) = NULL,
    @TicketSourceSystem nvarchar(40) = NULL,
    @TicketExternalId nvarchar(240) = NULL,
    @TicketNumber nvarchar(120) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @ClientSourceSystem = NULLIF(LTRIM(RTRIM(@ClientSourceSystem)), N'');
    SET @ClientExternalId = NULLIF(LTRIM(RTRIM(@ClientExternalId)), N'');
    SET @SageCustomerId = NULLIF(LTRIM(RTRIM(@SageCustomerId)), N'');
    SET @ClientName = NULLIF(LTRIM(RTRIM(@ClientName)), N'');
    SET @TicketSourceSystem = NULLIF(LTRIM(RTRIM(@TicketSourceSystem)), N'');
    SET @TicketExternalId = NULLIF(LTRIM(RTRIM(@TicketExternalId)), N'');
    SET @TicketNumber = NULLIF(LTRIM(RTRIM(@TicketNumber)), N'');

    /* In V1, Source=Both still stores the WHD location identity in ExternalId. */
    IF @ClientSourceSystem = N'Both'
        SET @ClientSourceSystem = N'WHD';
    IF @TicketSourceSystem = N'Both'
        SET @TicketSourceSystem = N'WHD';

    DECLARE @ClientResolutionStatus nvarchar(30) = N'NotFound';
    DECLARE @ResolvedClientId int = NULL;
    DECLARE @ClientMatchMethod nvarchar(40) = NULL;
    DECLARE @TicketResolutionStatus nvarchar(30) = N'NotResolved';
    DECLARE @ResolvedTicketId int = NULL;
    DECLARE @TicketMatchMethod nvarchar(40) = NULL;

    IF
    (
        (@ClientSourceSystem IS NULL AND @ClientExternalId IS NOT NULL)
        OR (@ClientSourceSystem IS NOT NULL AND @ClientExternalId IS NULL)
    )
    BEGIN
        SET @ClientResolutionStatus = N'InvalidInput';
    END
    ELSE
    BEGIN
        DECLARE @AuthoritativeClientCandidates TABLE
        (
            [ClientId] int NOT NULL,
            [MatchMethod] nvarchar(40) NOT NULL,
            PRIMARY KEY ([ClientId], [MatchMethod])
        );

        IF @ClientSourceSystem IS NOT NULL AND @ClientExternalId IS NOT NULL
        BEGIN
            INSERT INTO @AuthoritativeClientCandidates ([ClientId], [MatchMethod])
            SELECT identity_row.[ClientId], N'ClientExternalIdentity'
            FROM [tb_data].[ClientExternalIdentities] AS identity_row
            WHERE identity_row.[SourceSystem] = @ClientSourceSystem
              AND identity_row.[ExternalId] = @ClientExternalId;
        END;

        IF @SageCustomerId IS NOT NULL
        BEGIN
            INSERT INTO @AuthoritativeClientCandidates ([ClientId], [MatchMethod])
            SELECT client.[Id], N'SageCustomerId'
            FROM [tb_data].[Clients] AS client
            WHERE client.[SageCustomerId] = @SageCustomerId;
        END;

        DECLARE @AuthoritativeClientCount int;
        DECLARE @ExternalClientMatchCount int;
        SELECT @AuthoritativeClientCount = COUNT(DISTINCT [ClientId])
        FROM @AuthoritativeClientCandidates;
        SELECT @ExternalClientMatchCount = COUNT(*)
        FROM @AuthoritativeClientCandidates
        WHERE [MatchMethod] = N'ClientExternalIdentity';

        IF @AuthoritativeClientCount > 1
        BEGIN
            SET @ClientResolutionStatus =
                CASE
                    WHEN @ExternalClientMatchCount > 0 AND @SageCustomerId IS NOT NULL
                        THEN N'Conflict'
                    ELSE N'Ambiguous'
                END;
        END
        ELSE IF @AuthoritativeClientCount = 1
        BEGIN
            SELECT @ResolvedClientId = MIN([ClientId])
            FROM @AuthoritativeClientCandidates;

            SET @ClientResolutionStatus = N'Matched';
            SET @ClientMatchMethod =
                CASE
                    WHEN EXISTS
                    (
                        SELECT 1
                        FROM @AuthoritativeClientCandidates
                        WHERE [ClientId] = @ResolvedClientId
                          AND [MatchMethod] = N'ClientExternalIdentity'
                    )
                        THEN N'ClientExternalIdentity'
                    ELSE N'SageCustomerId'
                END;
        END
        ELSE
        BEGIN
            DECLARE @NamedClientCandidates TABLE
            (
                [ClientId] int NOT NULL,
                [MatchMethod] nvarchar(40) NOT NULL,
                PRIMARY KEY ([ClientId], [MatchMethod])
            );

            IF @ClientName IS NOT NULL
            BEGIN
                INSERT INTO @NamedClientCandidates ([ClientId], [MatchMethod])
                SELECT alias_row.[ClientId], N'OrganizationAlias'
                FROM [tb_data].[ClientAliases] AS alias_row
                WHERE alias_row.[ScopeType] = N'Organization'
                  AND alias_row.[Alias] = @ClientName;

                INSERT INTO @NamedClientCandidates ([ClientId], [MatchMethod])
                SELECT client.[Id], N'ClientName'
                FROM [tb_data].[Clients] AS client
                WHERE client.[Name] = @ClientName;
            END;

            DECLARE @NamedClientCount int;
            SELECT @NamedClientCount = COUNT(DISTINCT [ClientId])
            FROM @NamedClientCandidates;

            IF @NamedClientCount > 1
                SET @ClientResolutionStatus = N'Ambiguous';
            ELSE IF @NamedClientCount = 1
            BEGIN
                SELECT @ResolvedClientId = MIN([ClientId])
                FROM @NamedClientCandidates;

                SET @ClientResolutionStatus = N'Matched';
                SET @ClientMatchMethod =
                    CASE
                        WHEN EXISTS
                        (
                            SELECT 1
                            FROM @NamedClientCandidates
                            WHERE [ClientId] = @ResolvedClientId
                              AND [MatchMethod] = N'OrganizationAlias'
                        )
                            THEN N'OrganizationAlias'
                        ELSE N'ClientName'
                    END;
            END;
        END;
    END;

    IF @ClientResolutionStatus = N'Matched'
    BEGIN
        IF
        (
            (@TicketSourceSystem IS NULL AND @TicketExternalId IS NOT NULL)
            OR (@TicketSourceSystem IS NOT NULL AND @TicketExternalId IS NULL)
        )
        BEGIN
            SET @TicketResolutionStatus = N'InvalidInput';
        END
        ELSE IF @TicketSourceSystem IS NULL
             AND @TicketExternalId IS NULL
             AND @TicketNumber IS NULL
        BEGIN
            SET @TicketResolutionStatus = N'NotRequested';
        END
        ELSE
        BEGIN
            DECLARE @TicketCandidates TABLE
            (
                [TicketId] int NOT NULL,
                [ClientId] int NOT NULL,
                [MatchMethod] nvarchar(40) NOT NULL,
                PRIMARY KEY ([TicketId], [MatchMethod])
            );

            IF @TicketSourceSystem IS NOT NULL AND @TicketExternalId IS NOT NULL
            BEGIN
                INSERT INTO @TicketCandidates ([TicketId], [ClientId], [MatchMethod])
                SELECT ticket.[Id], ticket.[ClientId], N'TicketExternalIdentity'
                FROM [tb_data].[Tickets] AS ticket
                WHERE ticket.[Source] = @TicketSourceSystem
                  AND ticket.[ExternalId] = @TicketExternalId;
            END;

            IF @TicketNumber IS NOT NULL
            BEGIN
                INSERT INTO @TicketCandidates ([TicketId], [ClientId], [MatchMethod])
                SELECT ticket.[Id], ticket.[ClientId], N'TicketNumber'
                FROM [tb_data].[Tickets] AS ticket
                WHERE ticket.[ClientId] = @ResolvedClientId
                  AND ticket.[TicketNumber] = @TicketNumber;
            END;

            DECLARE @ExternalTicketOutsideClientCount int;
            DECLARE @EligibleTicketCount int;
            DECLARE @ExternalTicketMatchCount int;
            SELECT @ExternalTicketOutsideClientCount = COUNT(*)
            FROM @TicketCandidates
            WHERE [MatchMethod] = N'TicketExternalIdentity'
              AND [ClientId] <> @ResolvedClientId;
            SELECT @EligibleTicketCount = COUNT(DISTINCT [TicketId])
            FROM @TicketCandidates
            WHERE [ClientId] = @ResolvedClientId;
            SELECT @ExternalTicketMatchCount = COUNT(*)
            FROM @TicketCandidates
            WHERE [MatchMethod] = N'TicketExternalIdentity'
              AND [ClientId] = @ResolvedClientId;

            IF @ExternalTicketOutsideClientCount > 0
            BEGIN
                SET @TicketResolutionStatus = N'Conflict';
            END
            ELSE IF @EligibleTicketCount > 1
            BEGIN
                SET @TicketResolutionStatus =
                    CASE
                        WHEN @ExternalTicketMatchCount > 0 THEN N'Conflict'
                        ELSE N'Ambiguous'
                    END;
            END
            ELSE IF @EligibleTicketCount = 1
            BEGIN
                SELECT @ResolvedTicketId = MIN([TicketId])
                FROM @TicketCandidates
                WHERE [ClientId] = @ResolvedClientId;

                SET @TicketResolutionStatus = N'Matched';
                SET @TicketMatchMethod =
                    CASE
                        WHEN EXISTS
                        (
                            SELECT 1
                            FROM @TicketCandidates
                            WHERE [TicketId] = @ResolvedTicketId
                              AND [ClientId] = @ResolvedClientId
                              AND [MatchMethod] = N'TicketExternalIdentity'
                        )
                            THEN N'TicketExternalIdentity'
                        ELSE N'TicketNumber'
                    END;
            END
            ELSE
            BEGIN
                SET @TicketResolutionStatus = N'NotFound';
            END;
        END;
    END;

    SELECT
        @ClientResolutionStatus AS [ClientResolutionStatus],
        @ResolvedClientId AS [ClientId],
        @ClientMatchMethod AS [ClientMatchMethod],
        @TicketResolutionStatus AS [TicketResolutionStatus],
        @ResolvedTicketId AS [TicketId],
        @TicketMatchMethod AS [TicketMatchMethod];
END;
GO

IF OBJECT_ID(N'tb_app.ImportTechBenchV1WorkEntry', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ImportTechBenchV1WorkEntry];
GO

CREATE PROCEDURE [tb_app].[ImportTechBenchV1WorkEntry]
    @BatchId uniqueidentifier,
    @LegacyId bigint,
    @ContentHash char(64),
    @WorkDate date,
    @ClientId int = NULL,
    @ManualClientName nvarchar(240) = NULL,
    @TicketId int = NULL,
    @TicketNumberText nvarchar(120) = NULL,
    @HasTimeRange bit = 1,
    @StartTime time(0) = '00:00',
    @EndTime time(0) = '00:00',
    @DurationMinutes int = 0,
    @Billable bit = 1,
    @Note nvarchar(max) = N'',
    @PersonalNote nvarchar(max) = NULL,
    @IncludePersonalNoteInWhd bit = 0,
    @Tags nvarchar(1000) = N'',
    @FollowUpState nvarchar(30) = N'None',
    @FollowUpDueDate date = NULL,
    @WhdPosted bit = 0,
    @WhdPostedAtUtc datetime2(3) = NULL,
    @SagePosted bit = 0,
    @SagePostedAtUtc datetime2(3) = NULL,
    @SageTicketNumber nvarchar(120) = NULL,
    @LegacyPostingStatus nvarchar(40) = N'Draft',
    @LastError nvarchar(max) = NULL,
    @CreatedAtUtc datetime2(3) = NULL,
    @UpdatedAtUtc datetime2(3) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @ContentHash = UPPER(LTRIM(RTRIM(@ContentHash)));
    SET @ManualClientName = NULLIF(LTRIM(RTRIM(@ManualClientName)), N'');
    SET @TicketNumberText = NULLIF(LTRIM(RTRIM(@TicketNumberText)), N'');
    SET @Note = COALESCE(@Note, N'');
    SET @PersonalNote =
        CASE
            WHEN NULLIF(LTRIM(RTRIM(@PersonalNote)), N'') IS NULL THEN NULL
            ELSE @PersonalNote
        END;
    SET @Tags = COALESCE(LTRIM(RTRIM(@Tags)), N'');
    SET @FollowUpState = COALESCE(NULLIF(LTRIM(RTRIM(@FollowUpState)), N''), N'None');
    SET @LegacyPostingStatus = COALESCE(NULLIF(LTRIM(RTRIM(@LegacyPostingStatus)), N''), N'Draft');
    SET @LastError = NULLIF(LTRIM(RTRIM(@LastError)), N'');
    SET @SageTicketNumber = NULLIF(LTRIM(RTRIM(@SageTicketNumber)), N'');

    IF @LegacyId <= 0
        THROW 51610, N'LegacyId must be positive.', 1;
    IF @ContentHash IS NULL
       OR LEN(@ContentHash) <> 64
       OR @ContentHash COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9A-F]%'
        THROW 51611, N'ContentHash must be a 64-character hexadecimal SHA-256 value.', 1;
    IF @WorkDate IS NULL OR @CreatedAtUtc IS NULL OR @UpdatedAtUtc IS NULL
        THROW 51612, N'WorkDate, CreatedAtUtc, and UpdatedAtUtc are required.', 1;
    IF @UpdatedAtUtc < @CreatedAtUtc
        THROW 51612, N'UpdatedAtUtc cannot precede CreatedAtUtc.', 1;
    IF @ClientId IS NULL AND @ManualClientName IS NULL
        THROW 51613, N'A mapped client or legacy manual client name is required.', 1;
    IF @DurationMinutes < 0 OR @DurationMinutes > 1440
        THROW 51614, N'DurationMinutes must be between 0 and 1440.', 1;
    IF @FollowUpState NOT IN (N'None', N'FollowUp', N'Waiting', N'Completed')
        THROW 51615, N'FollowUpState is invalid.', 1;

    IF @ClientId IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM [tb_data].[Clients] WHERE [Id] = @ClientId)
        THROW 51616, N'The mapped shared client does not exist.', 1;
    IF @TicketId IS NOT NULL
       AND NOT EXISTS
       (
           SELECT 1
           FROM [tb_data].[Tickets]
           WHERE [Id] = @TicketId
             AND (@ClientId IS NULL OR [ClientId] = @ClientId)
       )
        THROW 51617, N'The mapped shared ticket does not exist for the mapped client.', 1;

    SET @WhdPostedAtUtc =
        CASE
            WHEN @WhdPosted = 1
                THEN COALESCE(@WhdPostedAtUtc, @UpdatedAtUtc, @CreatedAtUtc)
            ELSE NULL
        END;
    SET @SagePostedAtUtc =
        CASE
            WHEN @SagePosted = 1
                THEN COALESCE(@SagePostedAtUtc, @UpdatedAtUtc, @CreatedAtUtc)
            ELSE NULL
        END;

    DECLARE @SafePostingStatus nvarchar(40) =
        CASE
            WHEN @WhdPosted = 1 AND @SagePosted = 1 THEN N'PostedToBoth'
            WHEN @SagePosted = 1 THEN N'PostedToSage'
            WHEN @WhdPosted = 1 THEN N'PostedToWhd'
            WHEN @LastError IS NOT NULL THEN N'Failed'
            WHEN @LegacyPostingStatus IN (N'Draft', N'Ready', N'Failed')
                THEN @LegacyPostingStatus
            ELSE N'Draft'
        END;

    DECLARE @ExistingNewEntityId bigint;
    DECLARE @ExistingContentHash char(64);
    DECLARE @ExistingFirstImportBatchId uniqueidentifier;
    DECLARE @NewEntityId bigint;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_ops].[ImportBatches] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @BatchId
              AND [OwnerWindowsSid] = @UserSid
              AND [SourceSystem] = N'TechBenchV1'
              AND [Status] = N'Started'
              AND [FileName] IS NOT NULL
              AND [FileHash] IS NOT NULL
        )
            THROW 51618, N'The TechBench V1 import batch is missing, final, or owned by another user.', 1;

        SELECT
            @ExistingNewEntityId = [NewEntityId],
            @ExistingContentHash = [ContentHash],
            @ExistingFirstImportBatchId = [FirstImportBatchId]
        FROM [tb_ops].[LegacyEntityMappings] WITH (UPDLOCK, HOLDLOCK)
        WHERE [OwnerWindowsSid] = @UserSid
          AND [SourceSystem] = N'TechBenchV1'
          AND [EntityType] = N'WorkEntry'
          AND [LegacyId] = @LegacyId;

        IF @ExistingNewEntityId IS NOT NULL
        BEGIN
            IF @ExistingContentHash <> @ContentHash
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    @LegacyId AS [LegacyId],
                    @ExistingNewEntityId AS [NewEntityId],
                    N'Conflict' AS [Outcome],
                    CONVERT(bit, 0) AS [Imported],
                    CONVERT(bit, 0) AS [Skipped],
                    CONVERT(bit, 1) AS [Conflict],
                    N'The V1 work entry changed after it was previously imported.' AS [Message];
                RETURN;
            END;

            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[WorkEntries]
                WHERE [Id] = CONVERT(int, @ExistingNewEntityId)
                  AND [OwnerWindowsSid] = @UserSid
            )
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    @LegacyId AS [LegacyId],
                    @ExistingNewEntityId AS [NewEntityId],
                    N'Conflict' AS [Outcome],
                    CONVERT(bit, 0) AS [Imported],
                    CONVERT(bit, 0) AS [Skipped],
                    CONVERT(bit, 1) AS [Conflict],
                    N'The prior V1 mapping no longer points to a work entry owned by this user.' AS [Message];
                RETURN;
            END;

            IF EXISTS
            (
                SELECT 1
                FROM [tb_data].[WorkEntries]
                WHERE [Id] = CONVERT(int, @ExistingNewEntityId)
                  AND [OwnerWindowsSid] = @UserSid
                  AND
                  (
                      ISNULL([ClientId], -1) <> ISNULL(@ClientId, -1)
                      OR ISNULL([TicketId], -1) <> ISNULL(@TicketId, -1)
                  )
            )
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    @LegacyId AS [LegacyId],
                    @ExistingNewEntityId AS [NewEntityId],
                    N'Conflict' AS [Outcome],
                    CONVERT(bit, 0) AS [Imported],
                    CONVERT(bit, 0) AS [Skipped],
                    CONVERT(bit, 1) AS [Conflict],
                    N'The resolved client or ticket changed after this V1 work entry was first imported.' AS [Message];
                RETURN;
            END;

            UPDATE [tb_ops].[LegacyEntityMappings]
            SET
                [LastSeenImportBatchId] = @BatchId,
                [LastSeenAtUtc] = SYSUTCDATETIME()
            WHERE [OwnerWindowsSid] = @UserSid
              AND [SourceSystem] = N'TechBenchV1'
              AND [EntityType] = N'WorkEntry'
              AND [LegacyId] = @LegacyId;

            COMMIT TRANSACTION;
            SELECT
                @LegacyId AS [LegacyId],
                @ExistingNewEntityId AS [NewEntityId],
                CASE WHEN @ExistingFirstImportBatchId = @BatchId THEN N'Imported' ELSE N'Skipped' END AS [Outcome],
                CONVERT(bit, CASE WHEN @ExistingFirstImportBatchId = @BatchId THEN 1 ELSE 0 END) AS [Imported],
                CONVERT(bit, CASE WHEN @ExistingFirstImportBatchId = @BatchId THEN 0 ELSE 1 END) AS [Skipped],
                CONVERT(bit, 0) AS [Conflict],
                CASE
                    WHEN @ExistingFirstImportBatchId = @BatchId
                        THEN N'This V1 work entry was imported earlier in the current batch.'
                    ELSE N'This V1 work entry was already imported by a prior batch.'
                END AS [Message];
            RETURN;
        END;

        INSERT INTO [tb_data].[WorkEntries]
        (
            [OwnerWindowsSid],
            [WorkDate],
            [ClientId],
            [ManualClientName],
            [TicketId],
            [TicketNumberText],
            [HasTimeRange],
            [StartTime],
            [EndTime],
            [DurationMinutes],
            [Billable],
            [Note],
            [Tags],
            [FollowUpState],
            [FollowUpDueDate],
            [WhdPosted],
            [WhdPostedAtUtc],
            [SagePosted],
            [SagePostedAtUtc],
            [SageTicketNumber],
            [PostingStatus],
            [LastError],
            [CreatedByWindowsSid],
            [UpdatedByWindowsSid],
            [CreatedAtUtc],
            [UpdatedAtUtc]
        )
        VALUES
        (
            @UserSid,
            @WorkDate,
            @ClientId,
            @ManualClientName,
            @TicketId,
            @TicketNumberText,
            @HasTimeRange,
            @StartTime,
            @EndTime,
            @DurationMinutes,
            @Billable,
            @Note,
            @Tags,
            @FollowUpState,
            @FollowUpDueDate,
            @WhdPosted,
            @WhdPostedAtUtc,
            @SagePosted,
            @SagePostedAtUtc,
            @SageTicketNumber,
            @SafePostingStatus,
            @LastError,
            @UserSid,
            @UserSid,
            @CreatedAtUtc,
            @UpdatedAtUtc
        );

        SET @NewEntityId = CONVERT(bigint, SCOPE_IDENTITY());

        IF @PersonalNote IS NOT NULL OR @IncludePersonalNoteInWhd = 1
        BEGIN
            INSERT INTO [tb_private].[WorkEntryPersonalNotes]
            (
                [WorkEntryId],
                [OwnerWindowsSid],
                [Note],
                [IncludeInWhd],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                CONVERT(int, @NewEntityId),
                @UserSid,
                COALESCE(@PersonalNote, N''),
                @IncludePersonalNoteInWhd,
                @CreatedAtUtc,
                @UpdatedAtUtc
            );
        END;

        INSERT INTO [tb_ops].[LegacyEntityMappings]
        (
            [OwnerWindowsSid],
            [SourceSystem],
            [EntityType],
            [LegacyId],
            [NewEntityId],
            [ContentHash],
            [FirstImportBatchId],
            [LastSeenImportBatchId]
        )
        VALUES
        (
            @UserSid,
            N'TechBenchV1',
            N'WorkEntry',
            @LegacyId,
            @NewEntityId,
            @ContentHash,
            @BatchId,
            @BatchId
        );

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @NewEntityId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'TechBenchV1WorkEntryImported',
            @EntityType = N'WorkEntry',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        @LegacyId AS [LegacyId],
        @NewEntityId AS [NewEntityId],
        N'Imported' AS [Outcome],
        CONVERT(bit, 1) AS [Imported],
        CONVERT(bit, 0) AS [Skipped],
        CONVERT(bit, 0) AS [Conflict],
        N'The V1 work entry was imported.' AS [Message];
END;
GO

IF OBJECT_ID(N'tb_app.ImportTechBenchV1WorkEntryLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ImportTechBenchV1WorkEntryLink];
GO

CREATE PROCEDURE [tb_app].[ImportTechBenchV1WorkEntryLink]
    @BatchId uniqueidentifier,
    @LegacyId bigint,
    @ContentHash char(64),
    @LegacySourceWorkEntryId bigint,
    @LegacyTargetWorkEntryId bigint,
    @LinkType nvarchar(30) = N'Related',
    @CreatedAtUtc datetime2(3) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @ContentHash = UPPER(LTRIM(RTRIM(@ContentHash)));
    SET @LinkType = COALESCE(NULLIF(LTRIM(RTRIM(@LinkType)), N''), N'Related');

    IF @LegacyId <= 0
       OR @LegacySourceWorkEntryId <= 0
       OR @LegacyTargetWorkEntryId <= 0
        THROW 51620, N'Legacy link and work-entry IDs must be positive.', 1;
    IF @LegacySourceWorkEntryId = @LegacyTargetWorkEntryId
        THROW 51620, N'A V1 work-entry link cannot link an entry to itself.', 1;
    IF @ContentHash IS NULL
       OR LEN(@ContentHash) <> 64
       OR @ContentHash COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9A-F]%'
        THROW 51621, N'ContentHash must be a 64-character hexadecimal SHA-256 value.', 1;
    IF @LinkType NOT IN (N'Related', N'FollowUpTo')
        THROW 51622, N'LinkType must be Related or FollowUpTo.', 1;
    IF @CreatedAtUtc IS NULL
        THROW 51623, N'CreatedAtUtc is required.', 1;

    DECLARE @ExistingNewEntityId bigint;
    DECLARE @ExistingContentHash char(64);
    DECLARE @ExistingFirstImportBatchId uniqueidentifier;
    DECLARE @SourceWorkEntryId int;
    DECLARE @TargetWorkEntryId int;
    DECLARE @NewEntityId bigint;
    DECLARE @ExistingPairId int;
    DECLARE @ExistingPairType nvarchar(30);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_ops].[ImportBatches] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @BatchId
              AND [OwnerWindowsSid] = @UserSid
              AND [SourceSystem] = N'TechBenchV1'
              AND [Status] = N'Started'
              AND [FileName] IS NOT NULL
              AND [FileHash] IS NOT NULL
        )
            THROW 51624, N'The TechBench V1 import batch is missing, final, or owned by another user.', 1;

        /* Validate both prerequisites before either a replay or a new link. */
        SELECT @SourceWorkEntryId = CONVERT(int, [NewEntityId])
        FROM [tb_ops].[LegacyEntityMappings] WITH (UPDLOCK, HOLDLOCK)
        WHERE [OwnerWindowsSid] = @UserSid
          AND [SourceSystem] = N'TechBenchV1'
          AND [EntityType] = N'WorkEntry'
          AND [LegacyId] = @LegacySourceWorkEntryId
          AND [LastSeenImportBatchId] = @BatchId;

        SELECT @TargetWorkEntryId = CONVERT(int, [NewEntityId])
        FROM [tb_ops].[LegacyEntityMappings] WITH (UPDLOCK, HOLDLOCK)
        WHERE [OwnerWindowsSid] = @UserSid
          AND [SourceSystem] = N'TechBenchV1'
          AND [EntityType] = N'WorkEntry'
          AND [LegacyId] = @LegacyTargetWorkEntryId
          AND [LastSeenImportBatchId] = @BatchId;

        IF @SourceWorkEntryId IS NULL OR @TargetWorkEntryId IS NULL
        BEGIN
            COMMIT TRANSACTION;
            SELECT
                @LegacyId AS [LegacyId],
                CONVERT(bigint, NULL) AS [NewEntityId],
                N'Conflict' AS [Outcome],
                CONVERT(bit, 0) AS [Imported],
                CONVERT(bit, 0) AS [Skipped],
                CONVERT(bit, 1) AS [Conflict],
                N'One or both V1 work entries were not accepted in this import batch, so their link was not attached through a stale mapping.' AS [Message];
            RETURN;
        END;

        IF
        (
            SELECT COUNT(*)
            FROM [tb_data].[WorkEntries]
            WHERE [Id] IN (@SourceWorkEntryId, @TargetWorkEntryId)
              AND [OwnerWindowsSid] = @UserSid
        ) <> 2
            THROW 51626, N'Both mapped work entries must belong to the current user.', 1;

        IF @LinkType = N'Related' AND @SourceWorkEntryId > @TargetWorkEntryId
        BEGIN
            DECLARE @SwapWorkEntryId int = @SourceWorkEntryId;
            SET @SourceWorkEntryId = @TargetWorkEntryId;
            SET @TargetWorkEntryId = @SwapWorkEntryId;
        END;

        SELECT
            @ExistingNewEntityId = [NewEntityId],
            @ExistingContentHash = [ContentHash],
            @ExistingFirstImportBatchId = [FirstImportBatchId]
        FROM [tb_ops].[LegacyEntityMappings] WITH (UPDLOCK, HOLDLOCK)
        WHERE [OwnerWindowsSid] = @UserSid
          AND [SourceSystem] = N'TechBenchV1'
          AND [EntityType] = N'WorkEntryLink'
          AND [LegacyId] = @LegacyId;

        IF @ExistingNewEntityId IS NOT NULL
        BEGIN
            IF @ExistingContentHash <> @ContentHash
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    @LegacyId AS [LegacyId],
                    @ExistingNewEntityId AS [NewEntityId],
                    N'Conflict' AS [Outcome],
                    CONVERT(bit, 0) AS [Imported],
                    CONVERT(bit, 0) AS [Skipped],
                    CONVERT(bit, 1) AS [Conflict],
                    N'The V1 work-entry link changed after it was previously imported.' AS [Message];
                RETURN;
            END;

            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[WorkEntryLinks] AS link
                INNER JOIN [tb_data].[WorkEntries] AS source_entry
                    ON source_entry.[Id] = link.[SourceWorkEntryId]
                INNER JOIN [tb_data].[WorkEntries] AS target_entry
                    ON target_entry.[Id] = link.[TargetWorkEntryId]
                WHERE link.[Id] = CONVERT(int, @ExistingNewEntityId)
                  AND source_entry.[OwnerWindowsSid] = @UserSid
                  AND target_entry.[OwnerWindowsSid] = @UserSid
                  AND link.[LinkType] = @LinkType
                  AND
                  (
                      (
                          link.[SourceWorkEntryId] = @SourceWorkEntryId
                          AND link.[TargetWorkEntryId] = @TargetWorkEntryId
                      )
                      OR
                      (
                          @LinkType = N'Related'
                          AND link.[SourceWorkEntryId] = @TargetWorkEntryId
                          AND link.[TargetWorkEntryId] = @SourceWorkEntryId
                      )
                  )
            )
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    @LegacyId AS [LegacyId],
                    @ExistingNewEntityId AS [NewEntityId],
                    N'Conflict' AS [Outcome],
                    CONVERT(bit, 0) AS [Imported],
                    CONVERT(bit, 0) AS [Skipped],
                    CONVERT(bit, 1) AS [Conflict],
                    N'The prior V1 mapping no longer points to a link between this user''s work entries.' AS [Message];
                RETURN;
            END;

            UPDATE [tb_ops].[LegacyEntityMappings]
            SET
                [LastSeenImportBatchId] = @BatchId,
                [LastSeenAtUtc] = SYSUTCDATETIME()
            WHERE [OwnerWindowsSid] = @UserSid
              AND [SourceSystem] = N'TechBenchV1'
              AND [EntityType] = N'WorkEntryLink'
              AND [LegacyId] = @LegacyId;

            COMMIT TRANSACTION;
            SELECT
                @LegacyId AS [LegacyId],
                @ExistingNewEntityId AS [NewEntityId],
                CASE WHEN @ExistingFirstImportBatchId = @BatchId THEN N'Imported' ELSE N'Skipped' END AS [Outcome],
                CONVERT(bit, CASE WHEN @ExistingFirstImportBatchId = @BatchId THEN 1 ELSE 0 END) AS [Imported],
                CONVERT(bit, CASE WHEN @ExistingFirstImportBatchId = @BatchId THEN 0 ELSE 1 END) AS [Skipped],
                CONVERT(bit, 0) AS [Conflict],
                CASE
                    WHEN @ExistingFirstImportBatchId = @BatchId
                        THEN N'This V1 work-entry link was imported earlier in the current batch.'
                    ELSE N'This V1 work-entry link was already imported by a prior batch.'
                END AS [Message];
            RETURN;
        END;

        SELECT TOP (1)
            @ExistingPairId = link.[Id],
            @ExistingPairType = link.[LinkType]
        FROM [tb_data].[WorkEntryLinks] AS link WITH (UPDLOCK, HOLDLOCK)
        WHERE
        (
            link.[SourceWorkEntryId] = @SourceWorkEntryId
            AND link.[TargetWorkEntryId] = @TargetWorkEntryId
        )
        OR
        (
            @LinkType = N'Related'
            AND link.[SourceWorkEntryId] = @TargetWorkEntryId
            AND link.[TargetWorkEntryId] = @SourceWorkEntryId
        )
        ORDER BY link.[Id];

        IF @ExistingPairId IS NOT NULL AND @ExistingPairType <> @LinkType
        BEGIN
            COMMIT TRANSACTION;
            SELECT
                @LegacyId AS [LegacyId],
                CONVERT(bigint, @ExistingPairId) AS [NewEntityId],
                N'Conflict' AS [Outcome],
                CONVERT(bit, 0) AS [Imported],
                CONVERT(bit, 0) AS [Skipped],
                CONVERT(bit, 1) AS [Conflict],
                N'The mapped work entries already have a different relationship type.' AS [Message];
            RETURN;
        END;

        IF @ExistingPairId IS NULL
        BEGIN
            INSERT INTO [tb_data].[WorkEntryLinks]
            (
                [SourceWorkEntryId],
                [TargetWorkEntryId],
                [LinkType],
                [CreatedByWindowsSid],
                [CreatedAtUtc]
            )
            VALUES
            (
                @SourceWorkEntryId,
                @TargetWorkEntryId,
                @LinkType,
                @UserSid,
                @CreatedAtUtc
            );
            SET @NewEntityId = CONVERT(bigint, SCOPE_IDENTITY());
        END
        ELSE
        BEGIN
            SET @NewEntityId = CONVERT(bigint, @ExistingPairId);
        END;

        INSERT INTO [tb_ops].[LegacyEntityMappings]
        (
            [OwnerWindowsSid],
            [SourceSystem],
            [EntityType],
            [LegacyId],
            [NewEntityId],
            [ContentHash],
            [FirstImportBatchId],
            [LastSeenImportBatchId]
        )
        VALUES
        (
            @UserSid,
            N'TechBenchV1',
            N'WorkEntryLink',
            @LegacyId,
            @NewEntityId,
            @ContentHash,
            @BatchId,
            @BatchId
        );

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        @LegacyId AS [LegacyId],
        @NewEntityId AS [NewEntityId],
        N'Imported' AS [Outcome],
        CONVERT(bit, 1) AS [Imported],
        CONVERT(bit, 0) AS [Skipped],
        CONVERT(bit, 0) AS [Conflict],
        CASE
            WHEN @ExistingPairId IS NULL THEN N'The V1 work-entry link was imported.'
            ELSE N'The V1 work-entry link was imported by mapping it to an equivalent owned relationship.'
        END AS [Message];
END;
GO

IF OBJECT_ID(N'tb_app.ImportTechBenchV1PostingLog', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ImportTechBenchV1PostingLog];
GO

CREATE PROCEDURE [tb_app].[ImportTechBenchV1PostingLog]
    @BatchId uniqueidentifier,
    @LegacyId bigint,
    @ContentHash char(64),
    @LegacyWorkEntryId bigint,
    @Destination nvarchar(40),
    @Payload nvarchar(max) = N'',
    @Success bit = 0,
    @Message nvarchar(max) = N'',
    @ExternalReference nvarchar(500) = NULL,
    @CreatedAtUtc datetime2(3) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @ContentHash = UPPER(LTRIM(RTRIM(@ContentHash)));
    SET @Destination = NULLIF(LTRIM(RTRIM(@Destination)), N'');
    SET @Payload = COALESCE(@Payload, N'');
    SET @Message = COALESCE(@Message, N'');
    SET @ExternalReference = NULLIF(LTRIM(RTRIM(@ExternalReference)), N'');

    IF @LegacyId <= 0 OR @LegacyWorkEntryId <= 0
        THROW 51630, N'Legacy posting-log and work-entry IDs must be positive.', 1;
    IF @ContentHash IS NULL
       OR LEN(@ContentHash) <> 64
       OR @ContentHash COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9A-F]%'
        THROW 51631, N'ContentHash must be a 64-character hexadecimal SHA-256 value.', 1;
    IF @Destination NOT IN (N'WHD', N'Sage')
        THROW 51632, N'Destination must be WHD or Sage.', 1;
    IF @CreatedAtUtc IS NULL
        THROW 51633, N'CreatedAtUtc is required.', 1;

    DECLARE @ExistingNewEntityId bigint;
    DECLARE @ExistingContentHash char(64);
    DECLARE @ExistingFirstImportBatchId uniqueidentifier;
    DECLARE @WorkEntryId int;
    DECLARE @NewEntityId bigint;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_ops].[ImportBatches] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @BatchId
              AND [OwnerWindowsSid] = @UserSid
              AND [SourceSystem] = N'TechBenchV1'
              AND [Status] = N'Started'
              AND [FileName] IS NOT NULL
              AND [FileHash] IS NOT NULL
        )
            THROW 51634, N'The TechBench V1 import batch is missing, final, or owned by another user.', 1;

        /* Validate the prerequisite before either a replay or a new log. */
        SELECT @WorkEntryId = CONVERT(int, [NewEntityId])
        FROM [tb_ops].[LegacyEntityMappings] WITH (UPDLOCK, HOLDLOCK)
        WHERE [OwnerWindowsSid] = @UserSid
          AND [SourceSystem] = N'TechBenchV1'
          AND [EntityType] = N'WorkEntry'
          AND [LegacyId] = @LegacyWorkEntryId
          AND [LastSeenImportBatchId] = @BatchId;

        IF @WorkEntryId IS NULL
        BEGIN
            COMMIT TRANSACTION;
            SELECT
                @LegacyId AS [LegacyId],
                CONVERT(bigint, NULL) AS [NewEntityId],
                N'Conflict' AS [Outcome],
                CONVERT(bit, 0) AS [Imported],
                CONVERT(bit, 0) AS [Skipped],
                CONVERT(bit, 1) AS [Conflict],
                N'The V1 work entry was not accepted in this import batch, so its posting log was not attached through a stale mapping.' AS [Message];
            RETURN;
        END;
        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[WorkEntries]
            WHERE [Id] = @WorkEntryId
              AND [OwnerWindowsSid] = @UserSid
        )
            THROW 51636, N'The mapped work entry is not owned by the current user.', 1;

        SELECT
            @ExistingNewEntityId = [NewEntityId],
            @ExistingContentHash = [ContentHash],
            @ExistingFirstImportBatchId = [FirstImportBatchId]
        FROM [tb_ops].[LegacyEntityMappings] WITH (UPDLOCK, HOLDLOCK)
        WHERE [OwnerWindowsSid] = @UserSid
          AND [SourceSystem] = N'TechBenchV1'
          AND [EntityType] = N'PostingLog'
          AND [LegacyId] = @LegacyId;

        IF @ExistingNewEntityId IS NOT NULL
        BEGIN
            IF @ExistingContentHash <> @ContentHash
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    @LegacyId AS [LegacyId],
                    @ExistingNewEntityId AS [NewEntityId],
                    N'Conflict' AS [Outcome],
                    CONVERT(bit, 0) AS [Imported],
                    CONVERT(bit, 0) AS [Skipped],
                    CONVERT(bit, 1) AS [Conflict],
                    N'The V1 posting log changed after it was previously imported.' AS [Message];
                RETURN;
            END;

            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_ops].[PostingLogs]
                WHERE [Id] = @ExistingNewEntityId
                  AND [OwnerWindowsSid] = @UserSid
                  AND [WorkEntryId] = @WorkEntryId
            )
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    @LegacyId AS [LegacyId],
                    @ExistingNewEntityId AS [NewEntityId],
                    N'Conflict' AS [Outcome],
                    CONVERT(bit, 0) AS [Imported],
                    CONVERT(bit, 0) AS [Skipped],
                    CONVERT(bit, 1) AS [Conflict],
                    N'The prior V1 mapping no longer points to a posting log owned by this user.' AS [Message];
                RETURN;
            END;

            IF @Success = 1
            BEGIN
                UPDATE [tb_data].[WorkEntries]
                SET
                    [WhdPosted] =
                        CASE WHEN @Destination = N'WHD' THEN CONVERT(bit, 1) ELSE [WhdPosted] END,
                    [WhdPostedAtUtc] =
                        CASE
                            WHEN @Destination = N'WHD'
                                THEN COALESCE([WhdPostedAtUtc], @CreatedAtUtc)
                            ELSE [WhdPostedAtUtc]
                        END,
                    [SagePosted] =
                        CASE WHEN @Destination = N'Sage' THEN CONVERT(bit, 1) ELSE [SagePosted] END,
                    [SagePostedAtUtc] =
                        CASE
                            WHEN @Destination = N'Sage'
                                THEN COALESCE([SagePostedAtUtc], @CreatedAtUtc)
                            ELSE [SagePostedAtUtc]
                        END,
                    [PostingStatus] =
                        CASE
                            WHEN
                                (@Destination = N'WHD' OR [WhdPosted] = 1)
                                AND (@Destination = N'Sage' OR [SagePosted] = 1)
                                THEN N'PostedToBoth'
                            WHEN @Destination = N'Sage' OR [SagePosted] = 1
                                THEN N'PostedToSage'
                            ELSE N'PostedToWhd'
                        END
                WHERE [Id] = @WorkEntryId
                  AND [OwnerWindowsSid] = @UserSid;

                IF @@ROWCOUNT = 0
                    THROW 51636, N'The mapped posting-log work entry is not owned by the current user.', 1;
            END;

            UPDATE [tb_ops].[LegacyEntityMappings]
            SET
                [LastSeenImportBatchId] = @BatchId,
                [LastSeenAtUtc] = SYSUTCDATETIME()
            WHERE [OwnerWindowsSid] = @UserSid
              AND [SourceSystem] = N'TechBenchV1'
              AND [EntityType] = N'PostingLog'
              AND [LegacyId] = @LegacyId;

            COMMIT TRANSACTION;
            SELECT
                @LegacyId AS [LegacyId],
                @ExistingNewEntityId AS [NewEntityId],
                CASE WHEN @ExistingFirstImportBatchId = @BatchId THEN N'Imported' ELSE N'Skipped' END AS [Outcome],
                CONVERT(bit, CASE WHEN @ExistingFirstImportBatchId = @BatchId THEN 1 ELSE 0 END) AS [Imported],
                CONVERT(bit, CASE WHEN @ExistingFirstImportBatchId = @BatchId THEN 0 ELSE 1 END) AS [Skipped],
                CONVERT(bit, 0) AS [Conflict],
                CASE
                    WHEN @ExistingFirstImportBatchId = @BatchId
                        THEN N'This V1 posting log was imported earlier in the current batch.'
                    ELSE N'This V1 posting log was already imported by a prior batch.'
                END AS [Message];
            RETURN;
        END;

        INSERT INTO [tb_ops].[PostingLogs]
        (
            [WorkEntryId],
            [OwnerWindowsSid],
            [Destination],
            [Payload],
            [Success],
            [Message],
            [ExternalReference],
            [RequestId],
            [CreatedAtUtc]
        )
        VALUES
        (
            @WorkEntryId,
            @UserSid,
            @Destination,
            @Payload,
            @Success,
            @Message,
            @ExternalReference,
            COALESCE(@RequestId, NEWID()),
            @CreatedAtUtc
        );

        SET @NewEntityId = CONVERT(bigint, SCOPE_IDENTITY());

        INSERT INTO [tb_ops].[LegacyEntityMappings]
        (
            [OwnerWindowsSid],
            [SourceSystem],
            [EntityType],
            [LegacyId],
            [NewEntityId],
            [ContentHash],
            [FirstImportBatchId],
            [LastSeenImportBatchId]
        )
        VALUES
        (
            @UserSid,
            N'TechBenchV1',
            N'PostingLog',
            @LegacyId,
            @NewEntityId,
            @ContentHash,
            @BatchId,
            @BatchId
        );

        IF @Success = 1
        BEGIN
            /*
                A durable V1 success log is stronger evidence than a stale
                local posted flag. Reconcile conservatively so the imported
                item cannot be posted to the same destination a second time.
                Preserve UpdatedAtUtc: importing history is not a user edit.
            */
            UPDATE [tb_data].[WorkEntries]
            SET
                [WhdPosted] =
                    CASE WHEN @Destination = N'WHD' THEN CONVERT(bit, 1) ELSE [WhdPosted] END,
                [WhdPostedAtUtc] =
                    CASE
                        WHEN @Destination = N'WHD'
                            THEN COALESCE([WhdPostedAtUtc], @CreatedAtUtc)
                        ELSE [WhdPostedAtUtc]
                    END,
                [SagePosted] =
                    CASE WHEN @Destination = N'Sage' THEN CONVERT(bit, 1) ELSE [SagePosted] END,
                [SagePostedAtUtc] =
                    CASE
                        WHEN @Destination = N'Sage'
                            THEN COALESCE([SagePostedAtUtc], @CreatedAtUtc)
                        ELSE [SagePostedAtUtc]
                    END,
                [PostingStatus] =
                    CASE
                        WHEN
                            (@Destination = N'WHD' OR [WhdPosted] = 1)
                            AND (@Destination = N'Sage' OR [SagePosted] = 1)
                            THEN N'PostedToBoth'
                        WHEN @Destination = N'Sage' OR [SagePosted] = 1
                            THEN N'PostedToSage'
                        ELSE N'PostedToWhd'
                    END
            WHERE [Id] = @WorkEntryId
              AND [OwnerWindowsSid] = @UserSid;

            IF @@ROWCOUNT = 0
                THROW 51636, N'The mapped posting-log work entry is not owned by the current user.', 1;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        @LegacyId AS [LegacyId],
        @NewEntityId AS [NewEntityId],
        N'Imported' AS [Outcome],
        CONVERT(bit, 1) AS [Imported],
        CONVERT(bit, 0) AS [Skipped],
        CONVERT(bit, 0) AS [Conflict],
        N'The V1 posting log was imported.' AS [Message];
END;
GO

/* Generic imports cannot bypass the V1 outcome-count completion contract. */
IF OBJECT_ID(N'tb_app.CompleteImportBatch', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[CompleteImportBatch];
GO

CREATE PROCEDURE [tb_app].[CompleteImportBatch]
    @BatchId uniqueidentifier,
    @Succeeded bit,
    @ImportedCount int,
    @Message nvarchar(max) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF EXISTS
    (
        SELECT 1
        FROM [tb_ops].[ImportBatches]
        WHERE [Id] = @BatchId
          AND [OwnerWindowsSid] = @UserSid
          AND [SourceSystem] = N'TechBenchV1'
          AND [Status] = N'Started'
    )
        THROW 51643, N'TechBench V1 imports must be completed with CompleteTechBenchV1Import.', 1;

    UPDATE [tb_ops].[ImportBatches]
    SET
        [Status] = CASE WHEN @Succeeded = 1 THEN N'Succeeded' ELSE N'Failed' END,
        [ImportedCount] = CASE WHEN @ImportedCount < 0 THEN 0 ELSE @ImportedCount END,
        [Message] = COALESCE(@Message, N''),
        [CompletedAtUtc] = SYSUTCDATETIME()
    WHERE [Id] = @BatchId
      AND [OwnerWindowsSid] = @UserSid
      AND [SourceSystem] <> N'TechBenchV1'
      AND [Status] = N'Started';

    IF @@ROWCOUNT = 0
        THROW 51463, N'The import batch is missing, final, or owned by another user.', 1;
END;
GO

IF OBJECT_ID(N'tb_app.CompleteTechBenchV1Import', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[CompleteTechBenchV1Import];
GO

CREATE PROCEDURE [tb_app].[CompleteTechBenchV1Import]
    @BatchId uniqueidentifier,
    @Succeeded bit,
    @ReadCount int,
    @ImportedCount int,
    @SkippedCount int,
    @ConflictCount int,
    @ErrorCount int,
    @Message nvarchar(max) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @ReadCount < 0
       OR @ImportedCount < 0
       OR @SkippedCount < 0
       OR @ConflictCount < 0
       OR @ErrorCount < 0
        THROW 51640, N'Import completion counts cannot be negative.', 1;
    DECLARE @OutcomeCount bigint =
        CONVERT(bigint, @ImportedCount)
        + CONVERT(bigint, @SkippedCount)
        + CONVERT(bigint, @ConflictCount)
        + CONVERT(bigint, @ErrorCount);

    IF @OutcomeCount > CONVERT(bigint, @ReadCount)
        THROW 51641, N'Import outcome counts cannot exceed ReadCount.', 1;
    IF @Succeeded = 1 AND @ErrorCount <> 0
        THROW 51644, N'A successful import cannot contain errors.', 1;
    IF @Succeeded = 1 AND @OutcomeCount <> CONVERT(bigint, @ReadCount)
        THROW 51645, N'A successful import must account for every read item exactly once.', 1;

    UPDATE [tb_ops].[ImportBatches]
    SET
        [Status] = CASE WHEN @Succeeded = 1 THEN N'Succeeded' ELSE N'Failed' END,
        [ReadCount] = @ReadCount,
        [ImportedCount] = @ImportedCount,
        [SkippedCount] = @SkippedCount,
        [ConflictCount] = @ConflictCount,
        [ErrorCount] = @ErrorCount,
        [Message] = COALESCE(@Message, N''),
        [CompletedAtUtc] = SYSUTCDATETIME()
    WHERE [Id] = @BatchId
      AND [OwnerWindowsSid] = @UserSid
      AND [SourceSystem] = N'TechBenchV1'
      AND [Status] = N'Started'
      AND [FileName] IS NOT NULL
      AND [FileHash] IS NOT NULL;

    IF @@ROWCOUNT = 0
        THROW 51642, N'The TechBench V1 import batch is missing, final, or owned by another user.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @BatchId);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'TechBenchV1ImportCompleted',
        @EntityType = N'ImportBatch',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;

    SELECT
        [Id] AS [BatchId],
        [Status],
        [ReadCount],
        [ImportedCount],
        [SkippedCount],
        [ConflictCount],
        [ErrorCount],
        [Message],
        [StartedAtUtc],
        [CompletedAtUtc]
    FROM [tb_ops].[ImportBatches]
    WHERE [Id] = @BatchId
      AND [OwnerWindowsSid] = @UserSid;
END;
GO

IF OBJECT_ID(N'tb_app.AbandonTechBenchV1Import', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AbandonTechBenchV1Import];
GO

CREATE PROCEDURE [tb_app].[AbandonTechBenchV1Import]
    @BatchId uniqueidentifier = NULL,
    @Message nvarchar(max) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @Message = COALESCE(
        NULLIF(LTRIM(RTRIM(@Message)), N''),
        N'Abandoned by the user to recover a stale TechBench V1 import batch.');

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @BatchId IS NULL
        BEGIN
            /* The filtered unique index permits only one active V1 batch per owner. */
            SELECT @BatchId = [Id]
            FROM [tb_ops].[ImportBatches] WITH (UPDLOCK, HOLDLOCK)
            WHERE [OwnerWindowsSid] = @UserSid
              AND [SourceSystem] = N'TechBenchV1'
              AND [Status] = N'Started';
        END;

        UPDATE [tb_ops].[ImportBatches]
        SET
            [Status] = N'Abandoned',
            [Message] = @Message,
            [CompletedAtUtc] = SYSUTCDATETIME()
        WHERE [Id] = @BatchId
          AND [OwnerWindowsSid] = @UserSid
          AND [SourceSystem] = N'TechBenchV1'
          AND [Status] = N'Started';

        IF @@ROWCOUNT = 0
            THROW 51646, N'The TechBench V1 import batch is missing, final, or owned by another user.', 1;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @BatchId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'TechBenchV1ImportAbandoned',
            @EntityType = N'ImportBatch',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id] AS [BatchId],
        [Status],
        [ReadCount],
        [ImportedCount],
        [SkippedCount],
        [ConflictCount],
        [ErrorCount],
        [Message],
        [StartedAtUtc],
        [CompletedAtUtc]
    FROM [tb_ops].[ImportBatches]
    WHERE [Id] = @BatchId
      AND [OwnerWindowsSid] = @UserSid;
END;
GO

/* Include the V0005 conflict count in the existing per-user import history. */
IF OBJECT_ID(N'tb_app.GetImportBatches', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetImportBatches];
GO

CREATE PROCEDURE [tb_app].[GetImportBatches]
    @IncludeAllUsers bit = 0,
    @Limit int = 100
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IncludeAllUsers = 1 AND @IsManager <> 1 AND @IsAdmin <> 1
        THROW 51464, N'Only a Manager or Admin may read other users'' imports.', 1;

    SET @Limit =
        CASE WHEN @Limit < 1 THEN 1 WHEN @Limit > 1000 THEN 1000 ELSE @Limit END;

    SELECT TOP (@Limit)
        [Id] AS [BatchId],
        [SourceSystem] AS [Source],
        [FileName],
        [FileHash],
        [Status],
        [ReadCount],
        [ImportedCount],
        [SkippedCount],
        [ConflictCount],
        [ErrorCount],
        [Message],
        [StartedAtUtc],
        [CompletedAtUtc],
        [RowVersion]
    FROM [tb_ops].[ImportBatches]
    WHERE @IncludeAllUsers = 1 OR [OwnerWindowsSid] = @UserSid
    ORDER BY [StartedAtUtc] DESC;
END;
GO

PRINT N'TechBench V0005 owner-scoped TechBench V1 import procedures created.';
GO

-- ============================================================================
-- END 47-V0005-TechBenchV1ImportProcedures.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 48-V0006-WhdServerSyncProcedures.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF SCHEMA_ID(N'tb_service') IS NULL EXEC(N'CREATE SCHEMA [tb_service] AUTHORIZATION [dbo];');
GO

ALTER PROCEDURE [tb_app].[GetRepositoryCapabilities]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT
        CONVERT(int, 6) AS [SchemaVersion],
        CONVERT(bit, 0) AS [FullTextSearchAvailable],
        CONVERT(bit, 1) AS [SupportsTickets],
        CONVERT(bit, 1) AS [SupportsWorkEntries],
        CONVERT(bit, 1) AS [SupportsPrivateNotes],
        CONVERT(bit, 1) AS [SupportsPostingLeases],
        CONVERT(bit, 1) AS [SupportsSyncLeases],
        CONVERT(bit, 1) AS [SupportsImports],
        CONVERT(bit, 1) AS [SupportsTechBenchV1Import];
END;
GO

/* The service contract intentionally uses leases rather than caller identity. */
IF OBJECT_ID(N'tb_service.GetWhdSyncConfiguration', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[GetWhdSyncConfiguration];
GO
CREATE PROCEDURE [tb_service].[GetWhdSyncConfiguration]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        COALESCE(MAX(CASE WHEN s.[SettingKey] = N'Whd.BaseUrl' THEN s.[SettingValue] END), N'') AS [BaseUrl],
        COALESCE(MAX(CASE WHEN s.[SettingKey] = N'Whd.ServiceUsername' THEN s.[SettingValue] END), N'') AS [Username],
        COALESCE(MAX(CASE WHEN s.[SettingKey] = N'Whd.AuthenticationMode' THEN s.[SettingValue] END), N'Auto') AS [AuthenticationMode],
        COALESCE(
            TRY_CONVERT(bit, MAX(CASE WHEN s.[SettingKey] = N'Whd.AutoSyncEnabled' THEN s.[SettingValue] END)),
            CONVERT(bit, 1)) AS [AutoSyncEnabled],
        COALESCE(
            TRY_CONVERT(int, MAX(CASE WHEN s.[SettingKey] = N'Whd.AutoSyncMinutes' THEN s.[SettingValue] END)),
            5) AS [AutoSyncMinutes],
        c.[CursorValue],
        h.[LastSuccessfulAtUtc],
        h.[LastAttemptAtUtc],
        h.[LastError]
    FROM [tb_sync].[WhdSyncHealth] AS h
    LEFT JOIN [tb_sync].[WhdSyncCursors] AS c
        ON c.[CursorName] = N'WhdTickets'
    LEFT JOIN [tb_data].[OrganizationSettings] AS s
        ON s.[SettingKey] IN
           (
               N'Whd.BaseUrl',
               N'Whd.ServiceUsername',
               N'Whd.AuthenticationMode',
               N'Whd.AutoSyncEnabled',
               N'Whd.AutoSyncMinutes'
           )
    WHERE h.[HealthId] = 1
    GROUP BY
        c.[CursorValue],
        h.[LastSuccessfulAtUtc],
        h.[LastAttemptAtUtc],
        h.[LastError];
END;
GO

IF OBJECT_ID(N'tb_service.ClaimWhdSyncWork', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[ClaimWhdSyncWork];
GO
CREATE PROCEDURE [tb_service].[ClaimWhdSyncWork]
    @WorkerId uniqueidentifier,
    @LeaseSeconds int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF @WorkerId IS NULL OR @LeaseSeconds NOT BETWEEN 15 AND 3600 THROW 51800, N'WorkerId and a lease from 15 to 3600 seconds are required.', 1;
    DECLARE @WorkId uniqueidentifier, @LeaseId uniqueidentifier = NEWID(), @Now datetime2(3) = SYSUTCDATETIME(), @Until datetime2(3);
    DECLARE @ServiceSid varbinary(85) =
    (
        SELECT [WindowsSid]
        FROM [tb_security].[Users]
        WHERE [LoginName] = N'$(SyncServicePrincipal)'
    );
    IF @ServiceSid IS NULL
        THROW 51814, N'The configured WHD sync service principal has no TechBench service actor.', 1;
    SET @Until = DATEADD(second, @LeaseSeconds, @Now);
    BEGIN TRANSACTION;

    DECLARE @QueueLockResult int;
    EXEC @QueueLockResult = sys.sp_getapplock
        @Resource = N'TechBench.WHD.SyncQueue',
        @LockMode = N'Exclusive',
        @LockOwner = N'Transaction',
        @LockTimeout = 5000;
    IF @QueueLockResult < 0
        THROW 51817, N'Could not acquire the WHD synchronization queue lock.', 1;

    DECLARE @AutoEnabled bit = COALESCE
    (
        TRY_CONVERT(bit, (SELECT [SettingValue] FROM [tb_data].[OrganizationSettings] WHERE [SettingKey] = N'Whd.AutoSyncEnabled')),
        1
    );
    DECLARE @AutoMinutes int = COALESCE
    (
        TRY_CONVERT(int, (SELECT [SettingValue] FROM [tb_data].[OrganizationSettings] WHERE [SettingKey] = N'Whd.AutoSyncMinutes')),
        5
    );
    SET @AutoMinutes = CASE WHEN @AutoMinutes < 1 THEN 1 WHEN @AutoMinutes > 1440 THEN 1440 ELSE @AutoMinutes END;

    IF @AutoEnabled = 1
       AND NOT EXISTS
       (
           SELECT 1
           FROM [tb_sync].[WhdSyncWork]
           WHERE [State] IN (N'Queued', N'Leased')
       )
       AND NOT EXISTS
       (
           SELECT 1
           FROM [tb_sync].[WhdSyncHealth]
           WHERE [HealthId] = 1
             AND [LastAttemptAtUtc] > DATEADD(minute, -@AutoMinutes, @Now)
       )
    BEGIN
        DECLARE @AutoRequestId uniqueidentifier = NEWID();
        DECLARE @AutoRequestType nvarchar(40) = CASE
            WHEN EXISTS
            (
                SELECT 1 FROM [tb_sync].[WhdSyncCursors]
                WHERE [CursorName] = N'WhdTickets'
                  AND NULLIF([CursorValue], N'') IS NOT NULL
            ) THEN N'Incremental'
            ELSE N'Full'
        END;
        DECLARE @IncludeReferenceWork bit = CASE
            WHEN @AutoRequestType = N'Full' THEN 1
            WHEN
            (
                SELECT COUNT(DISTINCT [WorkType])
                FROM [tb_sync].[WhdSyncWork]
                WHERE [State] = N'Completed'
                  AND [WorkType] IN (N'Clients', N'Statuses', N'Technicians', N'Groups')
                  AND [CompletedAtUtc] >= DATEADD(day, -1, @Now)
            ) < 4 THEN 1
            ELSE 0
        END;

        INSERT INTO [tb_sync].[WhdSyncRequests]
            ([RequestId], [RequestedByWindowsSid], [RequestType])
        VALUES
            (@AutoRequestId, @ServiceSid, @AutoRequestType);

        INSERT INTO [tb_sync].[WhdSyncWork]
            ([WorkId], [RequestId], [WorkType])
        SELECT NEWID(), @AutoRequestId, work_type.[WorkType]
        FROM
        (
            VALUES
                (N'Clients'),
                (N'Statuses'),
                (N'Technicians'),
                (N'Groups'),
                (N'Tickets')
        ) AS work_type([WorkType])
        WHERE work_type.[WorkType] = N'Tickets'
           OR @IncludeReferenceWork = 1;
    END;

    SELECT TOP (1) @WorkId = w.[WorkId]
    FROM [tb_sync].[WhdSyncWork] AS w WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK, ROWLOCK)
    LEFT JOIN [tb_sync].[WhdSyncLeases] AS l WITH (UPDLOCK, HOLDLOCK) ON l.[WorkId] = w.[WorkId]
    WHERE w.[State] = N'Queued' OR (w.[State] = N'Leased' AND l.[ExpiresAtUtc] <= @Now)
    ORDER BY
        w.[CreatedAtUtc],
        CASE w.[WorkType]
            WHEN N'Clients' THEN 1
            WHEN N'Statuses' THEN 2
            WHEN N'Technicians' THEN 3
            WHEN N'Groups' THEN 4
            WHEN N'Tickets' THEN 5
            ELSE 6
        END,
        w.[WorkId];
    IF @WorkId IS NOT NULL
    BEGIN
        DELETE FROM [tb_sync].[WhdSyncLeases] WHERE [WorkId] = @WorkId;
        INSERT INTO [tb_sync].[WhdSyncLeases]([WorkId], [LeaseId], [WorkerId], [AcquiredAtUtc], [ExpiresAtUtc]) VALUES (@WorkId, @LeaseId, @WorkerId, @Now, @Until);
        UPDATE [tb_sync].[WhdSyncWork] SET [State] = N'Leased' WHERE [WorkId] = @WorkId;
        UPDATE r SET [Status] = N'Running' FROM [tb_sync].[WhdSyncRequests] AS r JOIN [tb_sync].[WhdSyncWork] AS w ON w.[RequestId] = r.[RequestId] WHERE w.[WorkId] = @WorkId AND r.[Status] = N'Queued';
    END;
    COMMIT TRANSACTION;
    SELECT w.[WorkId], l.[LeaseId], l.[WorkerId], l.[ExpiresAtUtc], w.[RequestId], r.[RequestType], w.[WorkType], w.[PayloadJson], c.[CursorValue]
    FROM [tb_sync].[WhdSyncWork] AS w JOIN [tb_sync].[WhdSyncLeases] AS l ON l.[WorkId] = w.[WorkId]
    JOIN [tb_sync].[WhdSyncRequests] AS r ON r.[RequestId] = w.[RequestId]
    LEFT JOIN [tb_sync].[WhdSyncCursors] AS c ON c.[CursorName] = N'WhdTickets'
    WHERE w.[WorkId] = @WorkId;
END;
GO

IF OBJECT_ID(N'tb_service.RenewWhdSyncLease', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[RenewWhdSyncLease];
GO
CREATE PROCEDURE [tb_service].[RenewWhdSyncLease]
    @WorkId uniqueidentifier, @LeaseId uniqueidentifier, @WorkerId uniqueidentifier, @LeaseSeconds int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF @LeaseSeconds NOT BETWEEN 15 AND 3600 THROW 51801, N'LeaseSeconds must be from 15 to 3600.', 1;
    DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @Until datetime2(3) = DATEADD(second, @LeaseSeconds, SYSUTCDATETIME());
    UPDATE [tb_sync].[WhdSyncLeases] SET [ExpiresAtUtc] = @Until
    WHERE [WorkId] = @WorkId AND [LeaseId] = @LeaseId AND [WorkerId] = @WorkerId AND [ExpiresAtUtc] > @Now;
    IF @@ROWCOUNT <> 1 THROW 51802, N'WHD sync lease is missing, expired, or owned by another worker.', 1;
    SELECT @WorkId AS [WorkId], @LeaseId AS [LeaseId], @Until AS [ExpiresAtUtc];
END;
GO

/* Every apply path validates the same unexpired lease before modifying data. */
IF OBJECT_ID(N'tb_service.ApplyWhdClientSnapshot', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[ApplyWhdClientSnapshot];
GO
CREATE PROCEDURE [tb_service].[ApplyWhdClientSnapshot]
    @WorkId uniqueidentifier, @LeaseId uniqueidentifier, @WorkerId uniqueidentifier, @Json nvarchar(max), @SyncedAtUtc datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF COALESCE(ISJSON(@Json), 0) <> 1
       OR LEFT(LTRIM(@Json), 1) <> N'['
       OR RIGHT(RTRIM(@Json), 1) <> N']'
       OR @SyncedAtUtc IS NULL
        THROW 51803, N'Valid JSON and SyncedAtUtc are required.', 1;

    DECLARE @ActorSid varbinary(85) =
    (
        SELECT [WindowsSid]
        FROM [tb_security].[Users]
        WHERE [LoginName] = N'$(SyncServicePrincipal)'
    );
    IF @ActorSid IS NULL
        THROW 51814, N'The WHD sync service actor is missing.', 1;

    DECLARE @Snapshot TABLE
    (
        [ExternalId] nvarchar(500) NOT NULL PRIMARY KEY,
        [Name] nvarchar(240) NOT NULL,
        [LocationName] nvarchar(240) NULL,
        [ContactName] nvarchar(240) NULL,
        [IsActive] bit NOT NULL
    );

    ;WITH parsed AS
    (
        SELECT
            NULLIF(LTRIM(RTRIM([ExternalId])), N'') AS [ExternalId],
            NULLIF(LTRIM(RTRIM([Name])), N'') AS [Name],
            NULLIF(LTRIM(RTRIM([LocationName])), N'') AS [LocationName],
            NULLIF(LTRIM(RTRIM([ContactName])), N'') AS [ContactName],
            COALESCE([IsActive], 1) AS [IsActive]
        FROM OPENJSON(@Json)
        WITH
        (
            [ExternalId] nvarchar(500) '$.externalId',
            [Name] nvarchar(240) '$.name',
            [LocationName] nvarchar(240) '$.locationName',
            [ContactName] nvarchar(240) '$.contactName',
            [IsActive] bit '$.isActive'
        )
    ),
    ranked AS
    (
        SELECT *, ROW_NUMBER() OVER
            (PARTITION BY [ExternalId] ORDER BY [ExternalId]) AS [RowNumber]
        FROM parsed
        WHERE [ExternalId] IS NOT NULL AND [Name] IS NOT NULL
    )
    INSERT INTO @Snapshot([ExternalId], [Name], [LocationName], [ContactName], [IsActive])
    SELECT [ExternalId], [Name], [LocationName], [ContactName], [IsActive]
    FROM ranked
    WHERE [RowNumber] = 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        DECLARE @ClientWorkType nvarchar(40);
        SELECT @ClientWorkType = work_item.[WorkType]
        FROM [tb_sync].[WhdSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN [tb_sync].[WhdSyncWork] AS work_item
            ON work_item.[WorkId] = lease.[WorkId]
        WHERE lease.[WorkId] = @WorkId
          AND lease.[LeaseId] = @LeaseId
          AND lease.[WorkerId] = @WorkerId
          AND lease.[ExpiresAtUtc] > SYSUTCDATETIME()
          AND work_item.[State] = N'Leased'
          AND work_item.[WorkType] IN (N'Clients', N'Tickets');

        IF @ClientWorkType IS NULL
            THROW 51804, N'Valid WHD client/ticket-work lease required.', 1;

        /* Preserve the shared customer match: WHD identities may point at a
           Sage/Both client rather than a standalone WHD client row. */
        UPDATE client
        SET
            [Name] = CASE WHEN client.[Source] = N'WHD' THEN snapshot.[Name] ELSE client.[Name] END,
            [WhdLocationName] = snapshot.[LocationName],
            [WhdContactName] = snapshot.[ContactName],
            [IsActive] = CASE WHEN client.[Source] = N'WHD' THEN snapshot.[IsActive] ELSE client.[IsActive] END,
            [LastSyncedAtUtc] = @SyncedAtUtc,
            [UpdatedAtUtc] = SYSUTCDATETIME(),
            [UpdatedByWindowsSid] = @ActorSid
        FROM [tb_data].[Clients] AS client
        INNER JOIN [tb_data].[ClientExternalIdentities] AS identity_row
            ON identity_row.[ClientId] = client.[Id]
           AND identity_row.[SourceSystem] = N'WHD'
        INNER JOIN @Snapshot AS snapshot
            ON snapshot.[ExternalId] = identity_row.[ExternalId];

        UPDATE identity_row
        SET
            [ExternalName] = snapshot.[Name],
            [LastSyncedAtUtc] = @SyncedAtUtc,
            [UpdatedByWindowsSid] = @ActorSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        FROM [tb_data].[ClientExternalIdentities] AS identity_row
        INNER JOIN @Snapshot AS snapshot
            ON snapshot.[ExternalId] = identity_row.[ExternalId]
        WHERE identity_row.[SourceSystem] = N'WHD';

        /* Seed identity rows for databases upgraded from the original
           Source/ExternalId representation. */
        INSERT INTO [tb_data].[ClientExternalIdentities]
        (
            [ClientId], [SourceSystem], [ExternalId], [ExternalName],
            [LastSyncedAtUtc], [CreatedByWindowsSid], [UpdatedByWindowsSid]
        )
        SELECT
            legacy.[Id], N'WHD', snapshot.[ExternalId], snapshot.[Name],
            @SyncedAtUtc, @ActorSid, @ActorSid
        FROM @Snapshot AS snapshot
        CROSS APPLY
        (
            SELECT TOP (1) client.[Id]
            FROM [tb_data].[Clients] AS client WITH (UPDLOCK, HOLDLOCK)
            WHERE client.[ExternalId] = snapshot.[ExternalId]
              AND client.[Source] IN (N'WHD', N'Both')
            ORDER BY CASE WHEN client.[Source] = N'Both' THEN 0 ELSE 1 END, client.[Id]
        ) AS legacy
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[ClientExternalIdentities] AS existing WITH (UPDLOCK, HOLDLOCK)
            WHERE existing.[SourceSystem] = N'WHD'
              AND existing.[ExternalId] = snapshot.[ExternalId]
        );

        DECLARE @NewClients TABLE
        (
            [ExternalId] nvarchar(500) NOT NULL PRIMARY KEY,
            [ClientId] int NOT NULL
        );

        INSERT INTO [tb_data].[Clients]
        (
            [Name], [Source], [ExternalId], [IsActive], [LastSyncedAtUtc],
            [WhdLocationName], [WhdContactName], [MatchStatus],
            [CreatedByWindowsSid], [UpdatedByWindowsSid]
        )
        OUTPUT inserted.[ExternalId], inserted.[Id]
            INTO @NewClients([ExternalId], [ClientId])
        SELECT
            snapshot.[Name], N'WHD', snapshot.[ExternalId], snapshot.[IsActive], @SyncedAtUtc,
            snapshot.[LocationName], snapshot.[ContactName], N'Unmatched', @ActorSid, @ActorSid
        FROM @Snapshot AS snapshot
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[ClientExternalIdentities] AS existing WITH (UPDLOCK, HOLDLOCK)
            WHERE existing.[SourceSystem] = N'WHD'
              AND existing.[ExternalId] = snapshot.[ExternalId]
        );

        INSERT INTO [tb_data].[ClientExternalIdentities]
        (
            [ClientId], [SourceSystem], [ExternalId], [ExternalName],
            [LastSyncedAtUtc], [CreatedByWindowsSid], [UpdatedByWindowsSid]
        )
        SELECT
            new_client.[ClientId], N'WHD', snapshot.[ExternalId], snapshot.[Name],
            @SyncedAtUtc, @ActorSid, @ActorSid
        FROM @NewClients AS new_client
        INNER JOIN @Snapshot AS snapshot
            ON snapshot.[ExternalId] = new_client.[ExternalId];

        /* Refresh legacy rows immediately after their WHD identity is seeded,
           rather than waiting for the next synchronization cycle. */
        UPDATE client
        SET
            [Name] = CASE WHEN client.[Source] = N'WHD' THEN snapshot.[Name] ELSE client.[Name] END,
            [WhdLocationName] = snapshot.[LocationName],
            [WhdContactName] = snapshot.[ContactName],
            [IsActive] = CASE WHEN client.[Source] = N'WHD' THEN snapshot.[IsActive] ELSE client.[IsActive] END,
            [LastSyncedAtUtc] = @SyncedAtUtc,
            [UpdatedAtUtc] = SYSUTCDATETIME(),
            [UpdatedByWindowsSid] = @ActorSid
        FROM [tb_data].[Clients] AS client
        INNER JOIN [tb_data].[ClientExternalIdentities] AS identity_row
            ON identity_row.[ClientId] = client.[Id]
           AND identity_row.[SourceSystem] = N'WHD'
        INNER JOIN @Snapshot AS snapshot
            ON snapshot.[ExternalId] = identity_row.[ExternalId];

        /* The Clients work is a complete active-location snapshot. Embedded
           client batches during Tickets work are deliberately upsert-only. */
        IF @ClientWorkType = N'Clients'
        BEGIN
            UPDATE client
            SET
                [IsActive] = 0,
                [UpdatedAtUtc] = SYSUTCDATETIME(),
                [UpdatedByWindowsSid] = @ActorSid
            FROM [tb_data].[Clients] AS client
            INNER JOIN [tb_data].[ClientExternalIdentities] AS identity_row
                ON identity_row.[ClientId] = client.[Id]
               AND identity_row.[SourceSystem] = N'WHD'
               AND identity_row.[ExternalId] LIKE N'WHD-LOCATION-%'
            WHERE client.[Source] = N'WHD'
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM @Snapshot AS active_location
                  WHERE active_location.[ExternalId] = identity_row.[ExternalId]
              );
        END;

        DECLARE @SavedCount int = (SELECT COUNT(*) FROM @Snapshot);
        DECLARE @InsertedCount int = (SELECT COUNT(*) FROM @NewClients);

        COMMIT TRANSACTION;

        SELECT
            @SavedCount AS [SavedCount],
            @InsertedCount AS [InsertedCount],
            @SyncedAtUtc AS [SyncedAtUtc];
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_service.ApplyWhdTicketBatch', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[ApplyWhdTicketBatch];
GO
CREATE PROCEDURE [tb_service].[ApplyWhdTicketBatch]
    @WorkId uniqueidentifier, @LeaseId uniqueidentifier, @WorkerId uniqueidentifier, @Json nvarchar(max), @SyncedAtUtc datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF COALESCE(ISJSON(@Json), 0) <> 1
       OR LEFT(LTRIM(@Json), 1) <> N'['
       OR RIGHT(RTRIM(@Json), 1) <> N']'
       OR @SyncedAtUtc IS NULL
        THROW 51805, N'Valid JSON and SyncedAtUtc are required.', 1;

    DECLARE @ActorSid varbinary(85) =
    (
        SELECT [WindowsSid]
        FROM [tb_security].[Users]
        WHERE [LoginName] = N'$(SyncServicePrincipal)'
    );
    IF @ActorSid IS NULL
        THROW 51814, N'The WHD sync service actor is missing.', 1;

    DECLARE @Tickets TABLE
    (
        [ExternalId] nvarchar(240) NOT NULL PRIMARY KEY,
        [TicketNumber] nvarchar(120) NOT NULL,
        [Subject] nvarchar(500) NULL,
        [Status] nvarchar(160) NULL,
        [StatusTypeId] int NULL,
        [ClientExternalId] nvarchar(500) NOT NULL,
        [IsClosed] bit NOT NULL,
        [IsDeleted] bit NOT NULL,
        [LastUpdatedUtc] datetime2(3) NULL,
        [AssignedTechExternalId] nvarchar(120) NULL,
        [AssignedTechName] nvarchar(240) NULL,
        [AssignedGroupExternalId] nvarchar(120) NULL,
        [AssignedGroupName] nvarchar(240) NULL
    );

    ;WITH parsed AS
    (
        SELECT
            NULLIF(LTRIM(RTRIM([ExternalId])), N'') AS [ExternalId],
            NULLIF(LTRIM(RTRIM([TicketNumber])), N'') AS [TicketNumber],
            NULLIF(LTRIM(RTRIM([Subject])), N'') AS [Subject],
            NULLIF(LTRIM(RTRIM([Status])), N'') AS [Status],
            [StatusTypeId],
            NULLIF(LTRIM(RTRIM([ClientExternalId])), N'') AS [ClientExternalId],
            COALESCE([IsClosed], 0) AS [IsClosed],
            COALESCE([IsDeleted], 0) AS [IsDeleted],
            [LastUpdatedUtc],
            NULLIF(LTRIM(RTRIM([AssignedTechExternalId])), N'') AS [AssignedTechExternalId],
            NULLIF(LTRIM(RTRIM([AssignedTechName])), N'') AS [AssignedTechName],
            NULLIF(LTRIM(RTRIM([AssignedGroupExternalId])), N'') AS [AssignedGroupExternalId],
            NULLIF(LTRIM(RTRIM([AssignedGroupName])), N'') AS [AssignedGroupName]
        FROM OPENJSON(@Json)
        WITH
        (
            [ExternalId] nvarchar(240) '$.externalId',
            [TicketNumber] nvarchar(120) '$.ticketNumber',
            [Subject] nvarchar(500) '$.subject',
            [Status] nvarchar(160) '$.status',
            [StatusTypeId] int '$.statusTypeId',
            [ClientExternalId] nvarchar(500) '$.clientExternalId',
            [IsClosed] bit '$.isClosed',
            [IsDeleted] bit '$.isDeleted',
            [LastUpdatedUtc] datetime2(3) '$.lastUpdatedUtc',
            [AssignedTechExternalId] nvarchar(120) '$.assignedTechnicianExternalId',
            [AssignedTechName] nvarchar(240) '$.assignedTechnicianName',
            [AssignedGroupExternalId] nvarchar(120) '$.assignedGroupExternalId',
            [AssignedGroupName] nvarchar(240) '$.assignedGroupName'
        )
    ),
    ranked AS
    (
        SELECT *, ROW_NUMBER() OVER
            (PARTITION BY [ExternalId] ORDER BY [LastUpdatedUtc] DESC, [TicketNumber]) AS [RowNumber]
        FROM parsed
        WHERE [ExternalId] IS NOT NULL
          AND [TicketNumber] IS NOT NULL
          AND [ClientExternalId] IS NOT NULL
    )
    INSERT INTO @Tickets
    (
        [ExternalId], [TicketNumber], [Subject], [Status], [StatusTypeId],
        [ClientExternalId], [IsClosed], [IsDeleted], [LastUpdatedUtc],
        [AssignedTechExternalId], [AssignedTechName],
        [AssignedGroupExternalId], [AssignedGroupName]
    )
    SELECT
        [ExternalId], [TicketNumber], [Subject], [Status], [StatusTypeId],
        [ClientExternalId], [IsClosed], [IsDeleted], [LastUpdatedUtc],
        [AssignedTechExternalId], [AssignedTechName],
        [AssignedGroupExternalId], [AssignedGroupName]
    FROM ranked
    WHERE [RowNumber] = 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_sync].[WhdSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [tb_sync].[WhdSyncWork] AS work_item
                ON work_item.[WorkId] = lease.[WorkId]
            WHERE lease.[WorkId] = @WorkId
              AND lease.[LeaseId] = @LeaseId
              AND lease.[WorkerId] = @WorkerId
              AND lease.[ExpiresAtUtc] > SYSUTCDATETIME()
              AND work_item.[State] = N'Leased'
              AND work_item.[WorkType] = N'Tickets'
        )
            THROW 51806, N'Valid WHD ticket-work lease required.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM @Tickets AS incoming
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[ClientExternalIdentities] AS identity_row
                WHERE identity_row.[SourceSystem] = N'WHD'
                  AND identity_row.[ExternalId] = incoming.[ClientExternalId]
            )
        )
            THROW 51815, N'A WHD ticket referenced a client identity that was not durably applied.', 1;

        UPDATE ticket
        SET
            [TicketNumber] = incoming.[TicketNumber],
            [ClientId] = identity_row.[ClientId],
            [Subject] = COALESCE(incoming.[Subject], ticket.[Subject]),
            [Status] = COALESCE(incoming.[Status], ticket.[Status]),
            [WhdStatusTypeId] = incoming.[StatusTypeId],
            [IsWhdDeleted] = incoming.[IsDeleted],
            [IsClosed] = CASE WHEN incoming.[IsDeleted] = 1 THEN 1 ELSE incoming.[IsClosed] END,
            [WhdLastUpdatedUtc] = COALESCE(incoming.[LastUpdatedUtc], ticket.[WhdLastUpdatedUtc]),
            [AssignedTechExternalId] = incoming.[AssignedTechExternalId],
            [AssignedTechName] = incoming.[AssignedTechName],
            [AssignedGroupExternalId] = incoming.[AssignedGroupExternalId],
            [AssignedGroupName] = incoming.[AssignedGroupName],
            [LastSyncedAtUtc] = @SyncedAtUtc,
            [UpdatedAtUtc] = SYSUTCDATETIME(),
            [UpdatedByWindowsSid] = @ActorSid
        FROM [tb_data].[Tickets] AS ticket
        INNER JOIN @Tickets AS incoming
            ON ticket.[Source] = N'WHD'
           AND ticket.[ExternalId] = incoming.[ExternalId]
        INNER JOIN [tb_data].[ClientExternalIdentities] AS identity_row
            ON identity_row.[SourceSystem] = N'WHD'
           AND identity_row.[ExternalId] = incoming.[ClientExternalId]
        WHERE incoming.[LastUpdatedUtc] IS NULL
           OR ticket.[WhdLastUpdatedUtc] IS NULL
           OR incoming.[LastUpdatedUtc] >= ticket.[WhdLastUpdatedUtc];

        INSERT INTO [tb_data].[Tickets]
        (
            [TicketNumber], [ClientId], [Subject], [Status], [Source], [ExternalId],
            [WhdStatusTypeId], [IsClosed], [LastSyncedAtUtc], [WhdLastUpdatedUtc],
            [IsWhdDeleted], [AssignedTechExternalId], [AssignedTechName],
            [AssignedGroupExternalId], [AssignedGroupName],
            [CreatedByWindowsSid], [UpdatedByWindowsSid]
        )
        SELECT
            incoming.[TicketNumber], identity_row.[ClientId],
            COALESCE(incoming.[Subject], N''), COALESCE(incoming.[Status], N'Open'),
            N'WHD', incoming.[ExternalId], incoming.[StatusTypeId],
            CASE WHEN incoming.[IsDeleted] = 1 THEN 1 ELSE incoming.[IsClosed] END,
            @SyncedAtUtc, incoming.[LastUpdatedUtc], incoming.[IsDeleted],
            incoming.[AssignedTechExternalId], incoming.[AssignedTechName],
            incoming.[AssignedGroupExternalId], incoming.[AssignedGroupName],
            @ActorSid, @ActorSid
        FROM @Tickets AS incoming
        INNER JOIN [tb_data].[ClientExternalIdentities] AS identity_row
            ON identity_row.[SourceSystem] = N'WHD'
           AND identity_row.[ExternalId] = incoming.[ClientExternalId]
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[Tickets] AS existing WITH (UPDLOCK, HOLDLOCK)
            WHERE existing.[Source] = N'WHD'
              AND existing.[ExternalId] = incoming.[ExternalId]
        );

        DECLARE @InsertedCount int = @@ROWCOUNT;
        DECLARE @SavedCount int = (SELECT COUNT(*) FROM @Tickets);

        COMMIT TRANSACTION;

        SELECT
            @SavedCount AS [SavedCount],
            @InsertedCount AS [InsertedCount],
            @SyncedAtUtc AS [SyncedAtUtc];
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_service.ApplyWhdTicketStatusSnapshot', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[ApplyWhdTicketStatusSnapshot];
GO
CREATE PROCEDURE [tb_service].[ApplyWhdTicketStatusSnapshot]
    @WorkId uniqueidentifier, @LeaseId uniqueidentifier, @WorkerId uniqueidentifier, @Json nvarchar(max), @SyncedAtUtc datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF COALESCE(ISJSON(@Json), 0) <> 1
       OR LEFT(LTRIM(@Json), 1) <> N'['
       OR RIGHT(RTRIM(@Json), 1) <> N']'
       OR @SyncedAtUtc IS NULL
        THROW 51807, N'Valid JSON and SyncedAtUtc are required.', 1;

    DECLARE @Snapshot TABLE
    (
        [ExternalId] nvarchar(240) NOT NULL PRIMARY KEY,
        [WhdStatusTypeId] int NULL,
        [Name] nvarchar(160) NOT NULL,
        [IsClosed] bit NOT NULL
    );

    ;WITH parsed AS
    (
        SELECT
            NULLIF(LTRIM(RTRIM([ExternalId])), N'') AS [ExternalId],
            [WhdStatusTypeId],
            NULLIF(LTRIM(RTRIM([Name])), N'') AS [Name],
            COALESCE([IsClosed], 0) AS [IsClosed]
        FROM OPENJSON(@Json)
        WITH
        (
            [ExternalId] nvarchar(240) '$.externalId',
            [WhdStatusTypeId] int '$.whdStatusTypeId',
            [Name] nvarchar(160) '$.name',
            [IsClosed] bit '$.isClosed'
        )
    ),
    ranked AS
    (
        SELECT *, ROW_NUMBER() OVER
            (PARTITION BY [ExternalId] ORDER BY [WhdStatusTypeId]) AS [RowNumber]
        FROM parsed
        WHERE [ExternalId] IS NOT NULL AND [Name] IS NOT NULL
    )
    INSERT INTO @Snapshot([ExternalId], [WhdStatusTypeId], [Name], [IsClosed])
    SELECT [ExternalId], [WhdStatusTypeId], [Name], [IsClosed]
    FROM ranked
    WHERE [RowNumber] = 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_sync].[WhdSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [tb_sync].[WhdSyncWork] AS work_item
                ON work_item.[WorkId] = lease.[WorkId]
            WHERE lease.[WorkId] = @WorkId
              AND lease.[LeaseId] = @LeaseId
              AND lease.[WorkerId] = @WorkerId
              AND lease.[ExpiresAtUtc] > SYSUTCDATETIME()
              AND work_item.[State] = N'Leased'
              AND work_item.[WorkType] = N'Statuses'
        )
            THROW 51808, N'Valid WHD status-work lease required.', 1;

        MERGE [tb_data].[TicketStatusOptions] AS target
        USING @Snapshot AS source
            ON target.[Source] = N'WHD'
           AND target.[ExternalId] = source.[ExternalId]
        WHEN MATCHED THEN
            UPDATE SET
                [Name] = source.[Name],
                [WhdStatusTypeId] = source.[WhdStatusTypeId],
                [IsClosed] = source.[IsClosed],
                [LastSyncedAtUtc] = @SyncedAtUtc,
                [UpdatedAtUtc] = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT
                ([Name], [Source], [ExternalId], [WhdStatusTypeId], [IsClosed], [LastSyncedAtUtc])
            VALUES
                (source.[Name], N'WHD', source.[ExternalId], source.[WhdStatusTypeId], source.[IsClosed], @SyncedAtUtc);

        COMMIT TRANSACTION;
        SELECT (SELECT COUNT(*) FROM @Snapshot) AS [SavedCount], @SyncedAtUtc AS [SyncedAtUtc];
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_service.ApplyWhdTechnicianSnapshot', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[ApplyWhdTechnicianSnapshot];
GO
CREATE PROCEDURE [tb_service].[ApplyWhdTechnicianSnapshot]
    @WorkId uniqueidentifier, @LeaseId uniqueidentifier, @WorkerId uniqueidentifier, @Json nvarchar(max), @SyncedAtUtc datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF COALESCE(ISJSON(@Json), 0) <> 1
       OR LEFT(LTRIM(@Json), 1) <> N'['
       OR RIGHT(RTRIM(@Json), 1) <> N']'
       OR @SyncedAtUtc IS NULL
        THROW 51809, N'Valid JSON and SyncedAtUtc are required.', 1;

    DECLARE @Snapshot TABLE
    (
        [ExternalId] nvarchar(120) NOT NULL PRIMARY KEY,
        [DisplayName] nvarchar(240) NOT NULL,
        [Username] nvarchar(240) NULL,
        [Email] nvarchar(320) NULL,
        [IsActive] bit NOT NULL,
        [LastUpdatedUtc] datetime2(3) NULL
    );

    ;WITH parsed AS
    (
        SELECT
            NULLIF(LTRIM(RTRIM([ExternalId])), N'') AS [ExternalId],
            NULLIF(LTRIM(RTRIM([DisplayName])), N'') AS [DisplayName],
            NULLIF(LTRIM(RTRIM([Username])), N'') AS [Username],
            NULLIF(LTRIM(RTRIM([Email])), N'') AS [Email],
            COALESCE([IsActive], 1) AS [IsActive],
            [LastUpdatedUtc]
        FROM OPENJSON(@Json)
        WITH
        (
            [ExternalId] nvarchar(120) '$.externalId',
            [DisplayName] nvarchar(240) '$.displayName',
            [Username] nvarchar(240) '$.username',
            [Email] nvarchar(320) '$.email',
            [IsActive] bit '$.isActive',
            [LastUpdatedUtc] datetime2(3) '$.lastUpdatedUtc'
        )
    ),
    ranked AS
    (
        SELECT *, ROW_NUMBER() OVER
            (PARTITION BY [ExternalId] ORDER BY [LastUpdatedUtc] DESC, [DisplayName]) AS [RowNumber]
        FROM parsed
        WHERE [ExternalId] IS NOT NULL AND [DisplayName] IS NOT NULL
    )
    INSERT INTO @Snapshot
        ([ExternalId], [DisplayName], [Username], [Email], [IsActive], [LastUpdatedUtc])
    SELECT [ExternalId], [DisplayName], [Username], [Email], [IsActive], [LastUpdatedUtc]
    FROM ranked
    WHERE [RowNumber] = 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_sync].[WhdSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [tb_sync].[WhdSyncWork] AS work_item
                ON work_item.[WorkId] = lease.[WorkId]
            WHERE lease.[WorkId] = @WorkId
              AND lease.[LeaseId] = @LeaseId
              AND lease.[WorkerId] = @WorkerId
              AND lease.[ExpiresAtUtc] > SYSUTCDATETIME()
              AND work_item.[State] = N'Leased'
              AND work_item.[WorkType] = N'Technicians'
        )
            THROW 51810, N'Valid WHD technician-work lease required.', 1;

        MERGE [tb_whd].[Technicians] AS target
        USING @Snapshot AS source
            ON target.[ExternalId] = source.[ExternalId]
        WHEN MATCHED THEN
            UPDATE SET
                [DisplayName] = source.[DisplayName],
                [Username] = source.[Username],
                [Email] = source.[Email],
                [IsActive] = source.[IsActive],
                [WhdLastUpdatedUtc] = source.[LastUpdatedUtc],
                [LastSyncedAtUtc] = @SyncedAtUtc
        WHEN NOT MATCHED BY TARGET THEN
            INSERT
                ([ExternalId], [DisplayName], [Username], [Email], [IsActive],
                 [WhdLastUpdatedUtc], [LastSyncedAtUtc])
            VALUES
                (source.[ExternalId], source.[DisplayName], source.[Username], source.[Email],
                 source.[IsActive], source.[LastUpdatedUtc], @SyncedAtUtc)
        WHEN NOT MATCHED BY SOURCE THEN
            UPDATE SET [IsActive] = 0, [LastSyncedAtUtc] = @SyncedAtUtc;

        COMMIT TRANSACTION;
        SELECT (SELECT COUNT(*) FROM @Snapshot) AS [SavedCount], @SyncedAtUtc AS [SyncedAtUtc];
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_service.ApplyWhdTechGroupSnapshot', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[ApplyWhdTechGroupSnapshot];
GO
CREATE PROCEDURE [tb_service].[ApplyWhdTechGroupSnapshot]
    @WorkId uniqueidentifier, @LeaseId uniqueidentifier, @WorkerId uniqueidentifier, @Json nvarchar(max), @SyncedAtUtc datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF COALESCE(ISJSON(@Json), 0) <> 1
       OR LEFT(LTRIM(@Json), 1) <> N'['
       OR RIGHT(RTRIM(@Json), 1) <> N']'
       OR @SyncedAtUtc IS NULL
        THROW 51811, N'Valid JSON and SyncedAtUtc are required.', 1;

    DECLARE @Groups TABLE
    (
        [ExternalId] nvarchar(120) NOT NULL PRIMARY KEY,
        [DisplayName] nvarchar(240) NOT NULL,
        [IsActive] bit NOT NULL,
        [LastUpdatedUtc] datetime2(3) NULL
    );
    DECLARE @Memberships TABLE
    (
        [TechnicianExternalId] nvarchar(120) NOT NULL,
        [GroupExternalId] nvarchar(120) NOT NULL,
        PRIMARY KEY ([TechnicianExternalId], [GroupExternalId])
    );

    ;WITH parsed AS
    (
        SELECT
            NULLIF(LTRIM(RTRIM([ExternalId])), N'') AS [ExternalId],
            NULLIF(LTRIM(RTRIM([DisplayName])), N'') AS [DisplayName],
            COALESCE([IsActive], 1) AS [IsActive],
            [LastUpdatedUtc]
        FROM OPENJSON(@Json)
        WITH
        (
            [ExternalId] nvarchar(120) '$.externalId',
            [DisplayName] nvarchar(240) '$.name',
            [IsActive] bit '$.isActive',
            [LastUpdatedUtc] datetime2(3) '$.lastUpdatedUtc'
        )
    ),
    ranked AS
    (
        SELECT *, ROW_NUMBER() OVER
            (PARTITION BY [ExternalId] ORDER BY [LastUpdatedUtc] DESC, [DisplayName]) AS [RowNumber]
        FROM parsed
        WHERE [ExternalId] IS NOT NULL AND [DisplayName] IS NOT NULL
    )
    INSERT INTO @Groups([ExternalId], [DisplayName], [IsActive], [LastUpdatedUtc])
    SELECT [ExternalId], [DisplayName], [IsActive], [LastUpdatedUtc]
    FROM ranked
    WHERE [RowNumber] = 1;

    INSERT INTO @Memberships([TechnicianExternalId], [GroupExternalId])
    SELECT DISTINCT
        NULLIF(LTRIM(RTRIM(member.[TechnicianExternalId])), N''),
        NULLIF(LTRIM(RTRIM(group_row.[ExternalId])), N'')
    FROM OPENJSON(@Json)
    WITH
    (
        [ExternalId] nvarchar(120) '$.externalId',
        [Technicians] nvarchar(max) '$.technicianExternalIds' AS JSON
    ) AS group_row
    CROSS APPLY OPENJSON(group_row.[Technicians])
    WITH ([TechnicianExternalId] nvarchar(120) '$') AS member
    WHERE NULLIF(LTRIM(RTRIM(member.[TechnicianExternalId])), N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(group_row.[ExternalId])), N'') IS NOT NULL;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_sync].[WhdSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [tb_sync].[WhdSyncWork] AS work_item
                ON work_item.[WorkId] = lease.[WorkId]
            WHERE lease.[WorkId] = @WorkId
              AND lease.[LeaseId] = @LeaseId
              AND lease.[WorkerId] = @WorkerId
              AND lease.[ExpiresAtUtc] > SYSUTCDATETIME()
              AND work_item.[State] = N'Leased'
              AND work_item.[WorkType] = N'Groups'
        )
            THROW 51812, N'Valid WHD group-work lease required.', 1;

        MERGE [tb_whd].[TechnicianGroups] AS target
        USING @Groups AS source
            ON target.[ExternalId] = source.[ExternalId]
        WHEN MATCHED THEN
            UPDATE SET
                [DisplayName] = source.[DisplayName],
                [IsActive] = source.[IsActive],
                [WhdLastUpdatedUtc] = source.[LastUpdatedUtc],
                [LastSyncedAtUtc] = @SyncedAtUtc
        WHEN NOT MATCHED BY TARGET THEN
            INSERT
                ([ExternalId], [DisplayName], [IsActive], [WhdLastUpdatedUtc], [LastSyncedAtUtc])
            VALUES
                (source.[ExternalId], source.[DisplayName], source.[IsActive],
                 source.[LastUpdatedUtc], @SyncedAtUtc)
        WHEN NOT MATCHED BY SOURCE THEN
            UPDATE SET [IsActive] = 0, [LastSyncedAtUtc] = @SyncedAtUtc;

        /* Membership is a complete snapshot. Replacing it atomically prevents
           removed group access from remaining visible to former members. */
        DELETE FROM [tb_whd].[TechnicianGroupMemberships];

        INSERT INTO [tb_whd].[TechnicianGroupMemberships]
            ([TechnicianExternalId], [GroupExternalId], [LastSyncedAtUtc])
        SELECT membership.[TechnicianExternalId], membership.[GroupExternalId], @SyncedAtUtc
        FROM @Memberships AS membership
        INNER JOIN [tb_whd].[Technicians] AS technician
            ON technician.[ExternalId] = membership.[TechnicianExternalId]
        INNER JOIN [tb_whd].[TechnicianGroups] AS group_row
            ON group_row.[ExternalId] = membership.[GroupExternalId]
        WHERE technician.[IsActive] = 1 AND group_row.[IsActive] = 1;

        COMMIT TRANSACTION;
        SELECT
            (SELECT COUNT(*) FROM @Groups) AS [SavedGroupCount],
            (SELECT COUNT(*) FROM @Memberships) AS [ReadMembershipCount],
            @SyncedAtUtc AS [SyncedAtUtc];
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_service.CompleteWhdSyncWork', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[CompleteWhdSyncWork];
GO
CREATE PROCEDURE [tb_service].[CompleteWhdSyncWork]
    @WorkId uniqueidentifier, @LeaseId uniqueidentifier, @WorkerId uniqueidentifier, @Succeeded bit, @CursorValue nvarchar(400) = NULL, @Message nvarchar(2000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetime2(3) = SYSUTCDATETIME();
    DECLARE @RequestId uniqueidentifier;
    DECLARE @WorkType nvarchar(40);

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT
            @RequestId = work_item.[RequestId],
            @WorkType = work_item.[WorkType]
        FROM [tb_sync].[WhdSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN [tb_sync].[WhdSyncWork] AS work_item WITH (UPDLOCK, HOLDLOCK)
            ON work_item.[WorkId] = lease.[WorkId]
        WHERE lease.[WorkId] = @WorkId
          AND lease.[LeaseId] = @LeaseId
          AND lease.[WorkerId] = @WorkerId
          AND lease.[ExpiresAtUtc] > @Now
          AND work_item.[State] = N'Leased';

        IF @RequestId IS NULL
            THROW 51813, N'Valid WHD work lease required.', 1;

        IF @CursorValue IS NOT NULL
           AND
           (
               @Succeeded <> 1
               OR @WorkType <> N'Tickets'
               OR TRY_CONVERT(datetimeoffset(3), @CursorValue) IS NULL
           )
            THROW 51816, N'Only successful Tickets work with a valid UTC cursor may advance WHD state.', 1;

        UPDATE [tb_sync].[WhdSyncWork]
        SET
            [State] = CASE WHEN @Succeeded = 1 THEN N'Completed' ELSE N'Failed' END,
            [CompletedAtUtc] = @Now,
            [ErrorMessage] = CASE WHEN @Succeeded = 1 THEN NULL ELSE @Message END
        WHERE [WorkId] = @WorkId;

        /* Cursor changes only after successful, durably applied Tickets work. */
        IF @Succeeded = 1 AND @WorkType = N'Tickets' AND @CursorValue IS NOT NULL
        BEGIN
            MERGE [tb_sync].[WhdSyncCursors] AS target
            USING
            (
                SELECT N'WhdTickets' AS [CursorName], @CursorValue AS [CursorValue]
            ) AS source
                ON target.[CursorName] = source.[CursorName]
            WHEN MATCHED AND
            (
                TRY_CONVERT(datetimeoffset(3), target.[CursorValue]) IS NULL
                OR TRY_CONVERT(datetimeoffset(3), source.[CursorValue])
                   > TRY_CONVERT(datetimeoffset(3), target.[CursorValue])
            ) THEN
                UPDATE SET
                    [CursorValue] = source.[CursorValue],
                    [UpdatedAtUtc] = @Now
            WHEN NOT MATCHED THEN
                INSERT ([CursorName], [CursorValue])
                VALUES (source.[CursorName], source.[CursorValue]);
        END;

        DELETE FROM [tb_sync].[WhdSyncLeases]
        WHERE [WorkId] = @WorkId;

        DECLARE @HasPendingWork bit = CASE WHEN EXISTS
        (
            SELECT 1
            FROM [tb_sync].[WhdSyncWork]
            WHERE [RequestId] = @RequestId
              AND [State] IN (N'Queued', N'Leased')
        ) THEN 1 ELSE 0 END;
        DECLARE @HasFailedWork bit = CASE WHEN EXISTS
        (
            SELECT 1
            FROM [tb_sync].[WhdSyncWork]
            WHERE [RequestId] = @RequestId
              AND [State] = N'Failed'
        ) THEN 1 ELSE 0 END;
        DECLARE @FailureMessage nvarchar(2000) =
        (
            SELECT TOP (1) [ErrorMessage]
            FROM [tb_sync].[WhdSyncWork]
            WHERE [RequestId] = @RequestId
              AND [State] = N'Failed'
            ORDER BY [CompletedAtUtc] DESC, [WorkId]
        );

        UPDATE [tb_sync].[WhdSyncRequests]
        SET
            [Status] = CASE
                WHEN @HasPendingWork = 1 THEN N'Running'
                WHEN @HasFailedWork = 1 THEN N'Failed'
                ELSE N'Completed'
            END,
            [CompletedAtUtc] = CASE WHEN @HasPendingWork = 0 THEN @Now ELSE NULL END,
            [Message] = LEFT
            (
                CASE
                    WHEN @HasFailedWork = 1 THEN COALESCE(@FailureMessage, N'WHD synchronization failed.')
                    WHEN @HasPendingWork = 0 THEN @Message
                    ELSE NULL
                END,
                1000
            )
        WHERE [RequestId] = @RequestId;

        /* Health is request-level: a successful sibling cannot hide a failed
           work item or claim the whole synchronization succeeded. */
        IF @HasPendingWork = 0
        BEGIN
            UPDATE [tb_sync].[WhdSyncHealth]
            SET
                [LastAttemptAtUtc] = @Now,
                [LastSuccessfulAtUtc] = CASE
                    WHEN @HasFailedWork = 0 THEN @Now
                    ELSE [LastSuccessfulAtUtc]
                END,
                [LastError] = CASE
                    WHEN @HasFailedWork = 1
                        THEN COALESCE(@FailureMessage, N'WHD synchronization failed.')
                    ELSE NULL
                END,
                [UpdatedAtUtc] = @Now
            WHERE [HealthId] = 1;
        END;

        COMMIT TRANSACTION;

        SELECT @WorkId AS [WorkId], @Succeeded AS [Succeeded];
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

/* Admin endpoints may request, monitor, and map users, but never submit snapshots. */
IF OBJECT_ID(N'tb_app.AdminRequestWhdSync', N'P') IS NOT NULL DROP PROCEDURE [tb_app].[AdminRequestWhdSync];
GO
CREATE PROCEDURE [tb_app].[AdminRequestWhdSync] @RequestType nvarchar(40)=N'Incremental', @RequestId uniqueidentifier=NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Sid varbinary(85), @Login nvarchar(256), @Name nvarchar(160);
    DECLARE @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @Sid OUTPUT,
        @LoginName = @Login OUTPUT,
        @DisplayName = @Name OUTPUT,
        @IsTechnician = @Tech OUTPUT,
        @IsManager = @Manager OUTPUT,
        @IsAdmin = @Admin OUTPUT,
        @IsSyncOperator = @Sync OUTPUT;

    IF @Admin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51820, N'Only a TechBench Admin may request WHD sync.', 1;
    IF @RequestType NOT IN (N'Full', N'Incremental')
        THROW 51821, N'RequestType must be Full or Incremental.', 1;

    IF @RequestType = N'Incremental'
       AND NOT EXISTS
       (
           SELECT 1
           FROM [tb_sync].[WhdSyncCursors]
           WHERE [CursorName] = N'WhdTickets'
             AND NULLIF([CursorValue], N'') IS NOT NULL
       )
        SET @RequestType = N'Full';

    SET @RequestId = COALESCE(@RequestId, NEWID());

    BEGIN TRY
        BEGIN TRANSACTION;

        DECLARE @QueueLockResult int;
        EXEC @QueueLockResult = sys.sp_getapplock
            @Resource = N'TechBench.WHD.SyncQueue',
            @LockMode = N'Exclusive',
            @LockOwner = N'Transaction',
            @LockTimeout = 5000;
        IF @QueueLockResult < 0
            THROW 51817, N'Could not acquire the WHD synchronization queue lock.', 1;

        DECLARE @ExistingRequestId uniqueidentifier =
        (
            SELECT TOP (1) request_row.[RequestId]
            FROM [tb_sync].[WhdSyncRequests] AS request_row
            INNER JOIN [tb_sync].[WhdSyncWork] AS work_item
                ON work_item.[RequestId] = request_row.[RequestId]
            WHERE work_item.[State] IN (N'Queued', N'Leased')
            ORDER BY request_row.[RequestedAtUtc], request_row.[RequestId]
        );
        IF @ExistingRequestId IS NOT NULL
        BEGIN
            COMMIT TRANSACTION;
            SELECT @ExistingRequestId AS [RequestId], N'AlreadyQueued' AS [Status];
            RETURN;
        END;

        INSERT INTO [tb_sync].[WhdSyncRequests]
            ([RequestId], [RequestedByWindowsSid], [RequestType])
        VALUES
            (@RequestId, @Sid, @RequestType);

        INSERT INTO [tb_sync].[WhdSyncWork]([WorkId], [RequestId], [WorkType])
        SELECT NEWID(), @RequestId, work_type.[WorkType]
        FROM
        (
            VALUES
                (N'Clients'),
                (N'Statuses'),
                (N'Technicians'),
                (N'Groups'),
                (N'Tickets')
        ) AS work_type([WorkType])
        WHERE @RequestType = N'Full'
           OR work_type.[WorkType] = N'Tickets';

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT @RequestId AS [RequestId], N'Queued' AS [Status];
END;
GO

IF OBJECT_ID(N'tb_app.GetWhdSyncStatus', N'P') IS NOT NULL DROP PROCEDURE [tb_app].[GetWhdSyncStatus];
GO
CREATE PROCEDURE [tb_app].[GetWhdSyncStatus]
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Sid varbinary(85), @Login nvarchar(256), @Name nvarchar(160);
    DECLARE @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @Sid OUTPUT,
        @LoginName = @Login OUTPUT,
        @DisplayName = @Name OUTPUT,
        @IsTechnician = @Tech OUTPUT,
        @IsManager = @Manager OUTPUT,
        @IsAdmin = @Admin OUTPUT,
        @IsSyncOperator = @Sync OUTPUT;

    IF @Admin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51822, N'Only a TechBench Admin may monitor WHD sync.', 1;

    SELECT TOP (1)
        request_row.[RequestId],
        request_row.[RequestType],
        request_row.[Status],
        request_row.[RequestedAtUtc],
        request_row.[CompletedAtUtc],
        request_row.[Message],
        SUM(CASE WHEN work_item.[State] = N'Completed' THEN 1 ELSE 0 END) AS [CompletedWorkCount],
        SUM(CASE WHEN work_item.[State] = N'Failed' THEN 1 ELSE 0 END) AS [FailedWorkCount],
        SUM(CASE WHEN work_item.[State] IN (N'Queued', N'Leased') THEN 1 ELSE 0 END) AS [QueueDepth]
    FROM [tb_sync].[WhdSyncRequests] AS request_row
    INNER JOIN [tb_sync].[WhdSyncWork] AS work_item
        ON work_item.[RequestId] = request_row.[RequestId]
    GROUP BY
        request_row.[RequestId], request_row.[RequestType], request_row.[Status],
        request_row.[RequestedAtUtc], request_row.[CompletedAtUtc], request_row.[Message]
    ORDER BY request_row.[RequestedAtUtc] DESC, request_row.[RequestId] DESC;

    SELECT [LastSuccessfulAtUtc], [LastAttemptAtUtc], [LastError], [UpdatedAtUtc]
    FROM [tb_sync].[WhdSyncHealth]
    WHERE [HealthId] = 1;
END;
GO

IF OBJECT_ID(N'tb_app.AdminGetWhdUserMappings', N'P') IS NOT NULL DROP PROCEDURE [tb_app].[AdminGetWhdUserMappings];
GO
CREATE PROCEDURE [tb_app].[AdminGetWhdUserMappings]
AS
BEGIN
 SET NOCOUNT ON; IF IS_ROLEMEMBER(N'tb_role_admin')<>1 THROW 51823,N'Only a TechBench Admin may manage WHD user mappings.',1; SELECT COALESCE(m.[Id],0) [Id],CONVERT(varchar(170),u.[WindowsSid],1) [UserSid],u.[LoginName],u.[DisplayName],m.[TechnicianExternalId],t.[DisplayName] [TechnicianDisplayName],m.[UpdatedAtUtc] FROM [tb_security].[Users] u LEFT JOIN [tb_whd].[UserTechnicianMappings] m ON m.[WindowsSid]=u.[WindowsSid] LEFT JOIN [tb_whd].[Technicians] t ON t.[ExternalId]=m.[TechnicianExternalId] WHERE u.[IsTechnician]=1 ORDER BY u.[LoginName]; END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveWhdUserMapping', N'P') IS NOT NULL DROP PROCEDURE [tb_app].[AdminSaveWhdUserMapping];
GO
CREATE PROCEDURE [tb_app].[AdminSaveWhdUserMapping]
    @WindowsLoginName nvarchar(256),
    @TechnicianExternalId nvarchar(120) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Actor varbinary(85), @Sid varbinary(85), @Login nvarchar(256), @Name nvarchar(160);
    DECLARE @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @Actor OUTPUT,
        @LoginName = @Login OUTPUT,
        @DisplayName = @Name OUTPUT,
        @IsTechnician = @Tech OUTPUT,
        @IsManager = @Manager OUTPUT,
        @IsAdmin = @Admin OUTPUT,
        @IsSyncOperator = @Sync OUTPUT;

    IF @Admin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51824, N'Only a TechBench Admin may manage WHD user mappings.', 1;

    SET @WindowsLoginName = NULLIF(LTRIM(RTRIM(@WindowsLoginName)), N'');
    SET @TechnicianExternalId = NULLIF(LTRIM(RTRIM(@TechnicianExternalId)), N'');
    SET @Sid = SUSER_SID(@WindowsLoginName);

    IF @Sid IS NULL
       OR NOT EXISTS (SELECT 1 FROM [tb_security].[Users] WHERE [WindowsSid] = @Sid)
        THROW 51825, N'The mapped Windows user must have signed in to TechBench.', 1;

    IF @TechnicianExternalId IS NOT NULL
       AND NOT EXISTS
       (
           SELECT 1
           FROM [tb_whd].[Technicians]
           WHERE [ExternalId] = @TechnicianExternalId
             AND [IsActive] = 1
       )
        THROW 51826, N'Unknown or inactive WHD technician.', 1;

    DECLARE @PreviousTechnicianExternalId nvarchar(120) =
    (
        SELECT [TechnicianExternalId]
        FROM [tb_whd].[UserTechnicianMappings]
        WHERE [WindowsSid] = @Sid
    );

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @TechnicianExternalId IS NULL
        BEGIN
            DELETE FROM [tb_whd].[UserTechnicianMappings]
            WHERE [WindowsSid] = @Sid;
        END
        ELSE
        BEGIN
            MERGE [tb_whd].[UserTechnicianMappings] AS target
            USING
            (
                SELECT @Sid AS [WindowsSid], @TechnicianExternalId AS [TechnicianExternalId]
            ) AS source
                ON target.[WindowsSid] = source.[WindowsSid]
            WHEN MATCHED THEN
                UPDATE SET
                    [TechnicianExternalId] = source.[TechnicianExternalId],
                    [UpdatedByWindowsSid] = @Actor,
                    [UpdatedAtUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT ([WindowsSid], [TechnicianExternalId], [UpdatedByWindowsSid])
                VALUES (source.[WindowsSid], source.[TechnicianExternalId], @Actor);
        END;

        DECLARE @AuditJson nvarchar(max) =
        (
            SELECT
                @WindowsLoginName AS [windowsLoginName],
                @PreviousTechnicianExternalId AS [previousTechnicianExternalId],
                @TechnicianExternalId AS [technicianExternalId]
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );
        DECLARE @AuditAction nvarchar(120) = CASE
            WHEN @TechnicianExternalId IS NULL THEN N'WhdUserMappingRemoved'
            ELSE N'WhdUserMappingSaved'
        END;
        DECLARE @AuditEntityId nvarchar(120) = LEFT(@WindowsLoginName, 120);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = @AuditAction,
            @EntityType = N'WhdUserMapping',
            @EntityId = @AuditEntityId,
            @RequestId = NULL,
            @DataJson = @AuditJson;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        COALESCE(mapping.[Id], 0) AS [Id],
        CONVERT(varchar(170), user_row.[WindowsSid], 1) AS [UserSid],
        user_row.[LoginName],
        user_row.[DisplayName],
        mapping.[TechnicianExternalId],
        technician.[DisplayName] AS [TechnicianDisplayName]
    FROM [tb_security].[Users] AS user_row
    LEFT JOIN [tb_whd].[UserTechnicianMappings] AS mapping
        ON mapping.[WindowsSid] = user_row.[WindowsSid]
    LEFT JOIN [tb_whd].[Technicians] AS technician
        ON technician.[ExternalId] = mapping.[TechnicianExternalId]
    WHERE user_row.[WindowsSid] = @Sid;
END;
GO

IF OBJECT_ID(N'tb_app.AdminGetWhdTechnicians', N'P') IS NOT NULL DROP PROCEDURE [tb_app].[AdminGetWhdTechnicians];
GO
CREATE PROCEDURE [tb_app].[AdminGetWhdTechnicians]
AS
BEGIN
 SET NOCOUNT ON; IF IS_ROLEMEMBER(N'tb_role_admin')<>1 THROW 51827,N'Only a TechBench Admin may view WHD technicians.',1; SELECT [ExternalId],[DisplayName],[Username],[Email],[IsActive],[WhdLastUpdatedUtc],[LastSyncedAtUtc] FROM [tb_whd].[Technicians] ORDER BY [DisplayName],[ExternalId]; END;
GO

/* WHD is access-scoped for ordinary users; non-WHD tickets retain V0002 behavior. */
IF OBJECT_ID(N'tb_app.SearchTickets', N'P') IS NOT NULL DROP PROCEDURE [tb_app].[SearchTickets];
GO
CREATE PROCEDURE [tb_app].[SearchTickets] @ClientId int=NULL,@Search nvarchar(240)=NULL,@IncludeClosed bit=0,@Limit int=500
AS
BEGIN
 SET NOCOUNT ON; DECLARE @Sid varbinary(85),@Login nvarchar(256),@Name nvarchar(160),@Tech bit,@Manager bit,@Admin bit,@Sync bit; EXEC [tb_security].[EnsureCurrentUser] @UserSid=@Sid OUTPUT,@LoginName=@Login OUTPUT,@DisplayName=@Name OUTPUT,@IsTechnician=@Tech OUTPUT,@IsManager=@Manager OUTPUT,@IsAdmin=@Admin OUTPUT,@IsSyncOperator=@Sync OUTPUT; SET @Limit=CASE WHEN @Limit IS NULL OR @Limit<1 THEN 1 WHEN @Limit>2000 THEN 2000 ELSE @Limit END; SET @Search=NULLIF(LTRIM(RTRIM(@Search)),N''); DECLARE @Pattern nvarchar(500)=CASE WHEN @Search IS NULL THEN NULL ELSE N'%'+REPLACE(REPLACE(REPLACE(REPLACE(@Search,N'~',N'~~'),N'%',N'~%'),N'_',N'~_'),N'[',N'~[')+N'%' END; SELECT TOP(@Limit) t.[Id],t.[TicketNumber],t.[ClientId],t.[Subject],t.[Status],t.[Source],t.[ExternalId],t.[WhdStatusTypeId],t.[IsClosed],t.[LastSyncedAtUtc] [LastSyncedAt],t.[WhdLastUpdatedUtc],t.[IsWhdDeleted],t.[AssignedTechExternalId],t.[AssignedTechName],t.[AssignedGroupExternalId],t.[AssignedGroupName],t.[RowVersion] FROM [tb_data].[Tickets] t WHERE (@ClientId IS NULL OR t.[ClientId]=@ClientId) AND (@IncludeClosed=1 OR t.[IsClosed]=0) AND (@Pattern IS NULL OR t.[TicketNumber] LIKE @Pattern ESCAPE N'~' OR t.[Subject] LIKE @Pattern ESCAPE N'~' OR t.[Status] LIKE @Pattern ESCAPE N'~' OR t.[ExternalId] LIKE @Pattern ESCAPE N'~') AND (t.[Source]<>N'WHD' OR EXISTS(SELECT 1 FROM [tb_whd].[UserTechnicianMappings] m WHERE m.[WindowsSid]=@Sid AND (m.[TechnicianExternalId]=t.[AssignedTechExternalId] OR EXISTS(SELECT 1 FROM [tb_whd].[TechnicianGroupMemberships] gm WHERE gm.[TechnicianExternalId]=m.[TechnicianExternalId] AND gm.[GroupExternalId]=t.[AssignedGroupExternalId])))) ORDER BY t.[IsClosed],t.[TicketNumber],t.[Id]; END;
GO

IF OBJECT_ID(N'tb_app.GetTicket', N'P') IS NOT NULL DROP PROCEDURE [tb_app].[GetTicket];
GO
CREATE PROCEDURE [tb_app].[GetTicket] @Id int
AS
BEGIN
 SET NOCOUNT ON; DECLARE @Sid varbinary(85),@Login nvarchar(256),@Name nvarchar(160),@Tech bit,@Manager bit,@Admin bit,@Sync bit; EXEC [tb_security].[EnsureCurrentUser] @UserSid=@Sid OUTPUT,@LoginName=@Login OUTPUT,@DisplayName=@Name OUTPUT,@IsTechnician=@Tech OUTPUT,@IsManager=@Manager OUTPUT,@IsAdmin=@Admin OUTPUT,@IsSyncOperator=@Sync OUTPUT; SELECT t.[Id],t.[TicketNumber],t.[ClientId],t.[Subject],t.[Status],t.[Source],t.[ExternalId],t.[WhdStatusTypeId],t.[IsClosed],t.[LastSyncedAtUtc] [LastSyncedAt],t.[WhdLastUpdatedUtc],t.[IsWhdDeleted],t.[AssignedTechExternalId],t.[AssignedTechName],t.[AssignedGroupExternalId],t.[AssignedGroupName],t.[RowVersion] FROM [tb_data].[Tickets] t WHERE t.[Id]=@Id AND (t.[Source]<>N'WHD' OR EXISTS(SELECT 1 FROM [tb_whd].[UserTechnicianMappings] m WHERE m.[WindowsSid]=@Sid AND (m.[TechnicianExternalId]=t.[AssignedTechExternalId] OR EXISTS(SELECT 1 FROM [tb_whd].[TechnicianGroupMemberships] gm WHERE gm.[TechnicianExternalId]=m.[TechnicianExternalId] AND gm.[GroupExternalId]=t.[AssignedGroupExternalId])))); END;
GO

/* Enforce the same WHD assignment boundary at the table, not only in ticket
   search procedures. This also protects SaveTicket, SaveWorkEntry, work-entry
   joins, and any future procedure that touches tb_data.Tickets. */
IF EXISTS
(
    SELECT 1
    FROM sys.security_policies AS policy
    INNER JOIN sys.schemas AS schema_row
        ON schema_row.[schema_id] = policy.[schema_id]
    WHERE schema_row.[name] = N'tb_security'
      AND policy.[name] = N'WhdTicketAccessPolicy'
)
BEGIN
    EXEC sys.sp_executesql
        N'ALTER SECURITY POLICY [tb_security].[WhdTicketAccessPolicy] WITH (STATE = OFF);';
    EXEC sys.sp_executesql
        N'DROP SECURITY POLICY [tb_security].[WhdTicketAccessPolicy];';
END;
GO

IF OBJECT_ID(N'tb_security.FilterWhdTicketAccess', N'IF') IS NOT NULL
    DROP FUNCTION [tb_security].[FilterWhdTicketAccess];
GO

CREATE FUNCTION [tb_security].[FilterWhdTicketAccess]
(
    @Source nvarchar(40),
    @AssignedTechExternalId nvarchar(120),
    @AssignedGroupExternalId nvarchar(120)
)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
(
    SELECT CONVERT(bit, 1) AS [AccessAllowed]
    WHERE @Source <> N'WHD'
       OR USER_NAME() = N'dbo'
       OR IS_ROLEMEMBER(N'db_owner') = 1
       OR IS_ROLEMEMBER(N'tb_role_admin') = 1
       OR IS_ROLEMEMBER(N'tb_role_sync_service') = 1
       OR EXISTS
       (
           SELECT 1
           FROM [tb_whd].[UserTechnicianMappings] AS mapping
           WHERE mapping.[WindowsSid] = SUSER_SID(ORIGINAL_LOGIN())
             AND
             (
                 mapping.[TechnicianExternalId] = @AssignedTechExternalId
                 OR EXISTS
                 (
                     SELECT 1
                     FROM [tb_whd].[TechnicianGroupMemberships] AS membership
                     WHERE membership.[TechnicianExternalId] = mapping.[TechnicianExternalId]
                       AND membership.[GroupExternalId] = @AssignedGroupExternalId
                 )
             )
       )
);
GO

CREATE SECURITY POLICY [tb_security].[WhdTicketAccessPolicy]
    ADD FILTER PREDICATE [tb_security].[FilterWhdTicketAccess]
        ([Source], [AssignedTechExternalId], [AssignedGroupExternalId])
        ON [tb_data].[Tickets],
    ADD BLOCK PREDICATE [tb_security].[FilterWhdTicketAccess]
        ([Source], [AssignedTechExternalId], [AssignedGroupExternalId])
        ON [tb_data].[Tickets] AFTER INSERT,
    ADD BLOCK PREDICATE [tb_security].[FilterWhdTicketAccess]
        ([Source], [AssignedTechExternalId], [AssignedGroupExternalId])
        ON [tb_data].[Tickets] AFTER UPDATE
    WITH (STATE = ON, SCHEMABINDING = ON);
GO

-- ============================================================================
-- END 48-V0006-WhdServerSyncProcedures.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 49-V0007-ServerOwnedSageAndAdminPreviewProcedures.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/*
    V0007 moves Sage customer synchronization behind the Windows service and
    adds short-lived, server-issued, read-only Admin preview sessions.
*/

ALTER PROCEDURE [tb_security].[EnsureCurrentUser]
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

    DECLARE @PreviewContext sql_variant = SESSION_CONTEXT(N'TechBench.PreviewSessionId');

    IF @PreviewContext IS NOT NULL
    BEGIN
        DECLARE @PreviewSessionId uniqueidentifier =
            TRY_CONVERT(uniqueidentifier, CONVERT(nvarchar(36), @PreviewContext));

        IF @PreviewSessionId IS NULL OR USER_NAME() <> N'tb_preview_reader'
            THROW 51900, N'The read-only user preview context is invalid.', 1;

        SELECT
            @UserSid = target_user.[WindowsSid],
            @LoginName = target_user.[LoginName],
            @DisplayName = target_user.[DisplayName],
            @IsTechnician = target_user.[IsTechnician],
            @IsManager = target_user.[IsManager],
            @IsAdmin = target_user.[IsAdmin],
            @IsSyncOperator = target_user.[IsSyncOperator]
        FROM [tb_security].[AdminUserPreviewSessions] AS preview_session
        INNER JOIN [tb_security].[Users] AS actor_user
            ON actor_user.[WindowsSid] = preview_session.[ActorWindowsSid]
        INNER JOIN [tb_security].[Users] AS target_user
            ON target_user.[WindowsSid] = preview_session.[TargetWindowsSid]
        WHERE preview_session.[PreviewSessionId] = @PreviewSessionId
          AND preview_session.[ActorWindowsSid] = SUSER_SID(ORIGINAL_LOGIN())
          AND preview_session.[EndedAtUtc] IS NULL
          AND preview_session.[ExpiresAtUtc] > SYSUTCDATETIME()
          AND actor_user.[IsAdmin] = 1
          AND target_user.[IsTechnician] = 1
          AND target_user.[IsAdmin] = 0
          AND target_user.[LastSeenAtUtc] >= DATEADD(hour, -1, SYSUTCDATETIME());

        IF @UserSid IS NULL
            THROW 51901, N'The read-only user preview session is missing, expired, or no longer authorized.', 1;

        RETURN;
    END;

    IF USER_NAME() = N'tb_preview_reader'
        THROW 51902, N'The preview reader cannot be used without a valid server-issued session.', 1;

    SET @UserSid = SUSER_SID(ORIGINAL_LOGIN());
    SET @LoginName = CONVERT(nvarchar(256), ORIGINAL_LOGIN());
    SET @IsTechnician =
        CONVERT(bit, CASE WHEN IS_ROLEMEMBER(N'tb_role_user') = 1 THEN 1 ELSE 0 END);
    SET @IsManager =
        CONVERT(bit, CASE WHEN IS_ROLEMEMBER(N'tb_role_manager') = 1 THEN 1 ELSE 0 END);
    SET @IsAdmin =
        CONVERT(bit, CASE WHEN IS_ROLEMEMBER(N'tb_role_admin') = 1 THEN 1 ELSE 0 END);
    SET @IsSyncOperator =
        CONVERT(bit, CASE WHEN IS_ROLEMEMBER(N'tb_role_sync_operator') = 1 THEN 1 ELSE 0 END);

    IF @UserSid IS NULL
       OR DATALENGTH(@UserSid) NOT BETWEEN 8 AND 85
       OR NULLIF(LTRIM(RTRIM(@LoginName)), N'') IS NULL
        THROW 51000, N'SQL Server did not provide a valid authenticated Windows identity.', 1;

    DECLARE @HasApplicationRole bit = CONVERT
    (
        bit,
        CASE
            WHEN @IsTechnician = 1 OR @IsManager = 1 OR @IsAdmin = 1 OR @IsSyncOperator = 1
                THEN 1
            ELSE 0
        END
    );

    IF @IsAdmin = 1
    BEGIN
        SET @IsManager = 1;
        SET @IsTechnician = 1;
    END
    ELSE IF @IsManager = 1
        SET @IsTechnician = 1;

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
                                 THEN RIGHT([LoginName], LEN([LoginName]) - CHARINDEX(N'\', [LoginName]))
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

        IF @@ROWCOUNT = 0 AND @HasApplicationRole = 1
        BEGIN
            INSERT INTO [tb_security].[Users]
            (
                [WindowsSid], [LoginName], [DisplayName], [IsTechnician],
                [IsManager], [IsAdmin], [IsSyncOperator]
            )
            VALUES
            (
                @UserSid, @LoginName, @DisplayName, @IsTechnician,
                @IsManager, @IsAdmin, @IsSyncOperator
            );
        END;

        /* A role refresh is authoritative. Immediately terminate sessions
           whose actor/target is no longer eligible, including the all-zero
           role state that is persisted before the access-denied THROW. */
        UPDATE [tb_security].[AdminUserPreviewSessions]
        SET [EndedAtUtc] = COALESCE([EndedAtUtc], SYSUTCDATETIME())
        WHERE [EndedAtUtc] IS NULL
          AND
          (
              ([ActorWindowsSid] = @UserSid AND @IsAdmin = 0)
              OR
              (
                  [TargetWindowsSid] = @UserSid
                  AND (@IsTechnician = 0 OR @IsAdmin = 1)
              )
          );

        SELECT @DisplayName = [DisplayName]
        FROM [tb_security].[Users]
        WHERE [WindowsSid] = @UserSid;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    IF @HasApplicationRole = 0
        THROW 51002, N'The Windows login is not assigned to a TechBench application role.', 1;
END;
GO

ALTER PROCEDURE [tb_app].[GetCurrentUserContext]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @LoginName nvarchar(256), @DisplayName nvarchar(160);
    DECLARE @IsTechnician bit, @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    DECLARE @DatabaseInstanceId uniqueidentifier, @SchemaVersion int;
    DECLARE @AuthenticatedUserSid varbinary(85) = SUSER_SID(ORIGINAL_LOGIN());
    DECLARE @AuthenticatedLoginName nvarchar(256) = CONVERT(nvarchar(256), ORIGINAL_LOGIN());
    DECLARE @AuthenticatedDisplayName nvarchar(160);
    DECLARE @PreviewSessionId uniqueidentifier = TRY_CONVERT
    (
        uniqueidentifier,
        CONVERT(nvarchar(36), SESSION_CONTEXT(N'TechBench.PreviewSessionId'))
    );
    DECLARE @PreviewExpiresAtUtc datetime2(3);

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

    SELECT @AuthenticatedDisplayName = [DisplayName]
    FROM [tb_security].[Users]
    WHERE [WindowsSid] = @AuthenticatedUserSid;

    SET @AuthenticatedDisplayName = COALESCE
    (
        NULLIF(LTRIM(RTRIM(@AuthenticatedDisplayName)), N''),
        CASE
            WHEN CHARINDEX(N'\', @AuthenticatedLoginName) > 0
                THEN RIGHT(@AuthenticatedLoginName, LEN(@AuthenticatedLoginName) - CHARINDEX(N'\', @AuthenticatedLoginName))
            ELSE @AuthenticatedLoginName
        END
    );

    IF USER_NAME() = N'tb_preview_reader'
    BEGIN
        SELECT @PreviewExpiresAtUtc = [ExpiresAtUtc]
        FROM [tb_security].[AdminUserPreviewSessions]
        WHERE [PreviewSessionId] = @PreviewSessionId
          AND [ActorWindowsSid] = @AuthenticatedUserSid
          AND [TargetWindowsSid] = @UserSid
          AND [EndedAtUtc] IS NULL
          AND [ExpiresAtUtc] > SYSUTCDATETIME();
    END
    ELSE
        SET @PreviewSessionId = NULL;

    IF @DatabaseInstanceId IS NULL OR @SchemaVersion IS NULL
        THROW 51020, N'The TechBench database metadata is incomplete.', 1;

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
        @IsSyncOperator AS [IsSyncOperator],
        @AuthenticatedUserSid AS [AuthenticatedUserSid],
        @AuthenticatedLoginName AS [AuthenticatedLoginName],
        @AuthenticatedDisplayName AS [AuthenticatedDisplayName],
        CONVERT(bit, CASE WHEN @PreviewSessionId IS NULL THEN 0 ELSE 1 END) AS [IsReadOnlyPreview],
        @PreviewSessionId AS [PreviewSessionId],
        @PreviewExpiresAtUtc AS [PreviewExpiresAtUtc];
END;
GO

ALTER PROCEDURE [tb_app].[GetRepositoryCapabilities]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT
        CONVERT(int, 7) AS [SchemaVersion],
        CONVERT(bit, 0) AS [FullTextSearchAvailable],
        CONVERT(bit, 1) AS [SupportsTickets],
        CONVERT(bit, 1) AS [SupportsWorkEntries],
        CONVERT(bit, 1) AS [SupportsPrivateNotes],
        CONVERT(bit, 1) AS [SupportsPostingLeases],
        CONVERT(bit, 1) AS [SupportsSyncLeases],
        CONVERT(bit, 1) AS [SupportsImports],
        CONVERT(bit, 1) AS [SupportsTechBenchV1Import],
        CONVERT(bit, 1) AS [SupportsServerSageSync],
        CONVERT(bit, 1) AS [SupportsAdminUserPreview];
END;
GO

IF OBJECT_ID(N'tb_app.AdminListPreviewUsers', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminListPreviewUsers];
GO

CREATE PROCEDURE [tb_app].[AdminListPreviewUsers]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @Login nvarchar(256), @Display nvarchar(160);
    DECLARE @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid=@ActorSid OUTPUT, @LoginName=@Login OUTPUT, @DisplayName=@Display OUTPUT,
        @IsTechnician=@Tech OUTPUT, @IsManager=@Manager OUTPUT,
        @IsAdmin=@Admin OUTPUT, @IsSyncOperator=@Sync OUTPUT;

    IF @Admin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51910, N'Only a currently authorized TechBench Admin may list preview users.', 1;

    SELECT
        [WindowsSid] AS [UserSid], [LoginName], [DisplayName],
        [IsTechnician], [IsManager], [IsAdmin], [IsSyncOperator]
    FROM [tb_security].[Users]
    WHERE [WindowsSid] <> @ActorSid
      AND [IsTechnician] = 1
      AND [IsAdmin] = 0
      AND [LastSeenAtUtc] >= DATEADD(hour, -1, SYSUTCDATETIME())
    ORDER BY [DisplayName], [LoginName];
END;
GO

IF OBJECT_ID(N'tb_app.AdminBeginUserPreview', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminBeginUserPreview];
GO

CREATE PROCEDURE [tb_app].[AdminBeginUserPreview]
    @TargetLoginName nvarchar(256),
    @ClientInstanceId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @Login nvarchar(256), @Display nvarchar(160);
    DECLARE @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid=@ActorSid OUTPUT, @LoginName=@Login OUTPUT, @DisplayName=@Display OUTPUT,
        @IsTechnician=@Tech OUTPUT, @IsManager=@Manager OUTPUT,
        @IsAdmin=@Admin OUTPUT, @IsSyncOperator=@Sync OUTPUT;

    IF @Admin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51911, N'Only a currently authorized TechBench Admin may begin a user preview.', 1;

    SET @TargetLoginName = NULLIF(LTRIM(RTRIM(@TargetLoginName)), N'');
    IF @TargetLoginName IS NULL OR @ClientInstanceId IS NULL
        THROW 51912, N'TargetLoginName and ClientInstanceId are required.', 1;

    DECLARE @TargetSid varbinary(85), @PreviewSessionId uniqueidentifier = NEWID();
    DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @Expires datetime2(3);
    SET @Expires = DATEADD(minute, 30, @Now);

    SELECT @TargetSid = [WindowsSid]
    FROM [tb_security].[Users]
    WHERE [LoginName] = @TargetLoginName
      AND [IsTechnician] = 1
      AND [IsAdmin] = 0
      AND [LastSeenAtUtc] >= DATEADD(hour, -1, @Now);

    IF @TargetSid IS NULL OR @TargetSid = @ActorSid
        THROW 51913, N'The selected non-Admin technician must have opened TechBench V2 within the past hour and still be authorized.', 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        UPDATE [tb_security].[AdminUserPreviewSessions]
        SET [EndedAtUtc] = @Now
        WHERE [ActorWindowsSid] = @ActorSid
          AND [ClientInstanceId] = @ClientInstanceId
          AND [EndedAtUtc] IS NULL;

        INSERT INTO [tb_security].[AdminUserPreviewSessions]
        (
            [PreviewSessionId], [ActorWindowsSid], [TargetWindowsSid],
            [ClientInstanceId], [StartedAtUtc], [ExpiresAtUtc]
        )
        VALUES
        (
            @PreviewSessionId, @ActorSid, @TargetSid,
            @ClientInstanceId, @Now, @Expires
        );

        DECLARE @AuditJson nvarchar(max) =
        (
            SELECT @TargetLoginName AS [targetLoginName], @ClientInstanceId AS [clientInstanceId],
                   @Expires AS [expiresAtUtc]
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );
        EXEC [tb_security].[WriteAuditEvent]
            @Action=N'AdminUserPreviewStarted', @EntityType=N'UserPreview',
            @EntityId=@TargetLoginName, @RequestId=@PreviewSessionId, @DataJson=@AuditJson;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        @PreviewSessionId AS [PreviewSessionId],
        target_user.[WindowsSid] AS [UserSid], target_user.[LoginName], target_user.[DisplayName],
        target_user.[IsTechnician], target_user.[IsManager], target_user.[IsAdmin],
        target_user.[IsSyncOperator], @Expires AS [ExpiresAtUtc]
    FROM [tb_security].[Users] AS target_user
    WHERE target_user.[WindowsSid] = @TargetSid;
END;
GO

IF OBJECT_ID(N'tb_app.ActivateReadOnlyPreview', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ActivateReadOnlyPreview];
GO

CREATE PROCEDURE [tb_app].[ActivateReadOnlyPreview]
    @PreviewSessionId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF SESSION_CONTEXT(N'TechBench.PreviewSessionId') IS NOT NULL
        THROW 51914, N'This SQL connection already has a preview context.', 1;

    DECLARE @ActorSid varbinary(85), @Login nvarchar(256), @Display nvarchar(160);
    DECLARE @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid=@ActorSid OUTPUT, @LoginName=@Login OUTPUT, @DisplayName=@Display OUTPUT,
        @IsTechnician=@Tech OUTPUT, @IsManager=@Manager OUTPUT,
        @IsAdmin=@Admin OUTPUT, @IsSyncOperator=@Sync OUTPUT;

    IF @PreviewSessionId IS NULL OR @Admin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51915, N'Only a currently authorized TechBench Admin may activate a user preview.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_security].[AdminUserPreviewSessions] AS preview_session
        INNER JOIN [tb_security].[Users] AS target_user
            ON target_user.[WindowsSid] = preview_session.[TargetWindowsSid]
        WHERE preview_session.[PreviewSessionId] = @PreviewSessionId
          AND preview_session.[ActorWindowsSid] = @ActorSid
          AND preview_session.[EndedAtUtc] IS NULL
          AND preview_session.[ExpiresAtUtc] > SYSUTCDATETIME()
          AND target_user.[IsTechnician] = 1
          AND target_user.[IsAdmin] = 0
          AND target_user.[LastSeenAtUtc] >= DATEADD(hour, -1, SYSUTCDATETIME())
    )
        THROW 51916, N'The user preview session is missing, expired, or no longer authorized.', 1;

    EXEC sys.sp_set_session_context
        @key=N'TechBench.PreviewSessionId', @value=@PreviewSessionId, @read_only=1;

    SELECT [PreviewSessionId], [TargetWindowsSid] AS [UserSid], [ExpiresAtUtc]
    FROM [tb_security].[AdminUserPreviewSessions]
    WHERE [PreviewSessionId] = @PreviewSessionId;
END;
GO

IF OBJECT_ID(N'tb_app.AdminEndUserPreview', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminEndUserPreview];
GO

CREATE PROCEDURE [tb_app].[AdminEndUserPreview]
    @PreviewSessionId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @Login nvarchar(256), @Display nvarchar(160);
    DECLARE @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid=@ActorSid OUTPUT, @LoginName=@Login OUTPUT, @DisplayName=@Display OUTPUT,
        @IsTechnician=@Tech OUTPUT, @IsManager=@Manager OUTPUT,
        @IsAdmin=@Admin OUTPUT, @IsSyncOperator=@Sync OUTPUT;

    IF @PreviewSessionId IS NULL OR @Admin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51917, N'Only a currently authorized TechBench Admin may end a user preview.', 1;

    DECLARE @TargetLogin nvarchar(256);
    SELECT @TargetLogin = target_user.[LoginName]
    FROM [tb_security].[AdminUserPreviewSessions] AS preview_session
    INNER JOIN [tb_security].[Users] AS target_user
        ON target_user.[WindowsSid] = preview_session.[TargetWindowsSid]
    WHERE preview_session.[PreviewSessionId] = @PreviewSessionId
      AND preview_session.[ActorWindowsSid] = @ActorSid;

    UPDATE [tb_security].[AdminUserPreviewSessions]
    SET [EndedAtUtc] = COALESCE([EndedAtUtc], SYSUTCDATETIME())
    WHERE [PreviewSessionId] = @PreviewSessionId
      AND [ActorWindowsSid] = @ActorSid
      AND [EndedAtUtc] IS NULL;

    IF @TargetLogin IS NOT NULL
        EXEC [tb_security].[WriteAuditEvent]
            @Action=N'AdminUserPreviewEnded', @EntityType=N'UserPreview',
            @EntityId=@TargetLogin, @RequestId=@PreviewSessionId, @DataJson=NULL;

    SELECT @PreviewSessionId AS [PreviewSessionId],
           CONVERT(bit, CASE WHEN @TargetLogin IS NULL THEN 0 ELSE 1 END) AS [Ended];
END;
GO

/* A preview reproduces the target user's shared work view but never exposes
   their personal-note payload, personal-note flag, rowversion, or draft. */
ALTER PROCEDURE [tb_app].[SearchWorkEntries]
    @StartDate date = NULL,
    @EndDate date = NULL,
    @ClientId int = NULL,
    @TicketId int = NULL,
    @ExcludeId int = NULL,
    @TicketText nvarchar(120) = NULL,
    @PostingStatus nvarchar(40) = NULL,
    @Keyword nvarchar(240) = NULL,
    @Tags nvarchar(500) = NULL,
    @FollowUpState nvarchar(30) = NULL,
    @OpenFollowUpsOnly bit = 0,
    @PendingWhdOnly bit = 0,
    @PendingSageOnly bit = 0,
    @PendingAnyOnly bit = 0,
    @IncludeAllUsers bit = 0,
    @Limit int = 500
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @LoginName nvarchar(256), @DisplayName nvarchar(160);
    DECLARE @IsTechnician bit, @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid=@UserSid OUTPUT, @LoginName=@LoginName OUTPUT, @DisplayName=@DisplayName OUTPUT,
        @IsTechnician=@IsTechnician OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;

    DECLARE @IsReadOnlyPreview bit =
        CONVERT(bit, CASE WHEN USER_NAME() = N'tb_preview_reader' THEN 1 ELSE 0 END);

    IF @IncludeAllUsers = 1 AND @IsManager <> 1 AND @IsAdmin <> 1
        THROW 51120, N'Only a Manager or Admin may search other users'' work entries.', 1;

    SET @Limit = CASE WHEN @Limit IS NULL OR @Limit < 1 THEN 1 WHEN @Limit > 2000 THEN 2000 ELSE @Limit END;
    SET @TicketText = NULLIF(LTRIM(RTRIM(@TicketText)), N'');
    SET @PostingStatus = NULLIF(LTRIM(RTRIM(@PostingStatus)), N'');
    SET @Keyword = NULLIF(LTRIM(RTRIM(@Keyword)), N'');
    SET @Tags = NULLIF(LTRIM(RTRIM(@Tags)), N'');
    SET @FollowUpState = NULLIF(LTRIM(RTRIM(@FollowUpState)), N'');

    DECLARE @KeywordPattern nvarchar(500) = CASE WHEN @Keyword IS NULL THEN NULL ELSE N'%' + @Keyword + N'%' END;
    DECLARE @TicketPattern nvarchar(300) = CASE WHEN @TicketText IS NULL THEN NULL ELSE N'%' + @TicketText + N'%' END;
    DECLARE @TagPattern nvarchar(700) = CASE WHEN @Tags IS NULL THEN NULL ELSE N'%' + @Tags + N'%' END;

    SELECT TOP (@Limit)
        work_entry.[Id], work_entry.[OwnerWindowsSid], work_entry.[WorkDate],
        work_entry.[ClientId], work_entry.[ManualClientName], work_entry.[TicketId],
        work_entry.[TicketNumberText], work_entry.[HasTimeRange], work_entry.[StartTime],
        work_entry.[EndTime], work_entry.[DurationMinutes], work_entry.[Billable],
        work_entry.[Note],
        CASE WHEN @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
             THEN personal_note.[Note] ELSE NULL END AS [InternalNote],
        CASE WHEN @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
             THEN personal_note.[Note] ELSE NULL END AS [PersonalNote],
        CASE WHEN @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
             THEN COALESCE(personal_note.[IncludeInWhd], 0)
             ELSE CONVERT(bit, 0) END AS [IncludePersonalNoteInWhd],
        work_entry.[Tags], work_entry.[FollowUpState], work_entry.[FollowUpDueDate],
        work_entry.[WhdPosted], work_entry.[WhdPostedAtUtc] AS [WhdPostedAt],
        work_entry.[SagePosted], work_entry.[SagePostedAtUtc] AS [SagePostedAt],
        work_entry.[SageTicketNumber], work_entry.[PostingStatus],
        CASE WHEN @IsReadOnlyPreview = 1 THEN NULL ELSE work_entry.[LastError] END AS [LastError],
        work_entry.[CreatedAtUtc] AS [CreatedAt], work_entry.[UpdatedAtUtc] AS [UpdatedAt],
        client.[Name] AS [ClientName], ticket.[TicketNumber], ticket.[Subject] AS [TicketSubject],
        work_entry.[RowVersion],
        CASE WHEN @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
             THEN personal_note.[RowVersion] ELSE NULL END AS [PersonalNoteRowVersion]
    FROM [tb_data].[WorkEntries] AS work_entry
    LEFT JOIN [tb_data].[Clients] AS client ON client.[Id] = work_entry.[ClientId]
    LEFT JOIN [tb_data].[Tickets] AS ticket ON ticket.[Id] = work_entry.[TicketId]
    LEFT JOIN [tb_private].[WorkEntryPersonalNotes] AS personal_note
        ON personal_note.[WorkEntryId] = work_entry.[Id]
       AND personal_note.[OwnerWindowsSid] = @UserSid
       AND @IsReadOnlyPreview = 0
    WHERE (@IncludeAllUsers = 1 OR work_entry.[OwnerWindowsSid] = @UserSid)
      AND (@StartDate IS NULL OR work_entry.[WorkDate] >= @StartDate)
      AND (@EndDate IS NULL OR work_entry.[WorkDate] <= @EndDate)
      AND (@ClientId IS NULL OR work_entry.[ClientId] = @ClientId)
      AND (@TicketId IS NULL OR work_entry.[TicketId] = @TicketId)
      AND (@ExcludeId IS NULL OR work_entry.[Id] <> @ExcludeId)
      AND (@TicketPattern IS NULL OR ticket.[TicketNumber] LIKE @TicketPattern OR work_entry.[TicketNumberText] LIKE @TicketPattern)
      AND (@PostingStatus IS NULL OR work_entry.[PostingStatus] = @PostingStatus)
      AND (@TagPattern IS NULL OR work_entry.[Tags] LIKE @TagPattern)
      AND (@FollowUpState IS NULL OR work_entry.[FollowUpState] = @FollowUpState)
      AND (@OpenFollowUpsOnly = 0 OR work_entry.[FollowUpState] IN (N'FollowUp', N'Waiting'))
      AND
      (
          @PendingWhdOnly = 0
          OR
          (
              (work_entry.[TicketId] IS NOT NULL OR NULLIF(LTRIM(RTRIM(work_entry.[TicketNumberText])), N'') IS NOT NULL)
              AND work_entry.[SagePosted] = 0
              AND
              (
                  work_entry.[WhdPosted] = 0 OR work_entry.[WhdPostedAtUtc] IS NULL
                  OR work_entry.[UpdatedAtUtc] > work_entry.[WhdPostedAtUtc]
                  OR work_entry.[LastError] LIKE N'WHD sync conflict:%'
              )
          )
      )
      AND (@PendingSageOnly = 0 OR (work_entry.[Billable] = 1 AND work_entry.[SagePosted] = 0))
      AND
      (
          @PendingAnyOnly = 0
          OR (work_entry.[Billable] = 1 AND work_entry.[SagePosted] = 0)
          OR
          (
              (work_entry.[TicketId] IS NOT NULL OR NULLIF(LTRIM(RTRIM(work_entry.[TicketNumberText])), N'') IS NOT NULL)
              AND work_entry.[SagePosted] = 0
              AND
              (
                  work_entry.[WhdPosted] = 0 OR work_entry.[WhdPostedAtUtc] IS NULL
                  OR work_entry.[UpdatedAtUtc] > work_entry.[WhdPostedAtUtc]
                  OR work_entry.[LastError] LIKE N'WHD sync conflict:%'
              )
          )
      )
      AND
      (
          @KeywordPattern IS NULL
          OR work_entry.[Note] LIKE @KeywordPattern OR work_entry.[Tags] LIKE @KeywordPattern
          OR work_entry.[ManualClientName] LIKE @KeywordPattern OR work_entry.[TicketNumberText] LIKE @KeywordPattern
          OR client.[Name] LIKE @KeywordPattern OR ticket.[TicketNumber] LIKE @KeywordPattern
          OR ticket.[Subject] LIKE @KeywordPattern
          OR
          (
              @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
              AND personal_note.[Note] LIKE @KeywordPattern
          )
      )
    ORDER BY work_entry.[WorkDate] DESC, work_entry.[StartTime] DESC, work_entry.[Id] DESC;
END;
GO

ALTER PROCEDURE [tb_app].[GetWorkEntry]
    @Id int,
    @IncludeAllUsers bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @LoginName nvarchar(256), @DisplayName nvarchar(160);
    DECLARE @IsTechnician bit, @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid=@UserSid OUTPUT, @LoginName=@LoginName OUTPUT, @DisplayName=@DisplayName OUTPUT,
        @IsTechnician=@IsTechnician OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;

    DECLARE @IsReadOnlyPreview bit =
        CONVERT(bit, CASE WHEN USER_NAME() = N'tb_preview_reader' THEN 1 ELSE 0 END);

    IF @IncludeAllUsers = 1 AND @IsManager <> 1 AND @IsAdmin <> 1
        THROW 51120, N'Only a Manager or Admin may read another user''s work entry.', 1;

    DECLARE @CanReadAll bit = CONVERT
    (
        bit,
        CASE WHEN @IncludeAllUsers = 1 AND (@IsManager = 1 OR @IsAdmin = 1) THEN 1 ELSE 0 END
    );

    SELECT
        work_entry.[Id], work_entry.[OwnerWindowsSid], work_entry.[WorkDate],
        work_entry.[ClientId], work_entry.[ManualClientName], work_entry.[TicketId],
        work_entry.[TicketNumberText], work_entry.[HasTimeRange], work_entry.[StartTime],
        work_entry.[EndTime], work_entry.[DurationMinutes], work_entry.[Billable],
        work_entry.[Note],
        CASE WHEN @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
             THEN personal_note.[Note] END AS [InternalNote],
        CASE WHEN @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
             THEN personal_note.[Note] END AS [PersonalNote],
        CASE WHEN @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
             THEN COALESCE(personal_note.[IncludeInWhd], 0)
             ELSE CONVERT(bit, 0) END AS [IncludePersonalNoteInWhd],
        work_entry.[Tags], work_entry.[FollowUpState], work_entry.[FollowUpDueDate],
        work_entry.[WhdPosted], work_entry.[WhdPostedAtUtc] AS [WhdPostedAt],
        work_entry.[SagePosted], work_entry.[SagePostedAtUtc] AS [SagePostedAt],
        work_entry.[SageTicketNumber], work_entry.[PostingStatus],
        CASE WHEN @IsReadOnlyPreview = 1 THEN NULL ELSE work_entry.[LastError] END AS [LastError],
        work_entry.[CreatedAtUtc] AS [CreatedAt], work_entry.[UpdatedAtUtc] AS [UpdatedAt],
        client.[Name] AS [ClientName], ticket.[TicketNumber], ticket.[Subject] AS [TicketSubject],
        work_entry.[RowVersion],
        CASE WHEN @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
             THEN personal_note.[RowVersion] END AS [PersonalNoteRowVersion]
    FROM [tb_data].[WorkEntries] AS work_entry
    LEFT JOIN [tb_data].[Clients] AS client ON client.[Id] = work_entry.[ClientId]
    LEFT JOIN [tb_data].[Tickets] AS ticket ON ticket.[Id] = work_entry.[TicketId]
    LEFT JOIN [tb_private].[WorkEntryPersonalNotes] AS personal_note
        ON personal_note.[WorkEntryId] = work_entry.[Id]
       AND personal_note.[OwnerWindowsSid] = @UserSid
       AND @IsReadOnlyPreview = 0
    WHERE work_entry.[Id] = @Id
      AND (work_entry.[OwnerWindowsSid] = @UserSid OR @CanReadAll = 1);
END;
GO

/* Organization settings remain visible in preview, but target-owned settings
   (including legacy credential-migration values) are never returned. */
ALTER PROCEDURE [tb_app].[GetSettings]
    @ScopeType nvarchar(40) = NULL,
    @DeviceId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@UserSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;
    DECLARE @IsReadOnlyPreview bit =
        CONVERT(bit, CASE WHEN USER_NAME() = N'tb_preview_reader' THEN 1 ELSE 0 END);

    ;WITH settings AS
    (
        SELECT
            CONVERT(nvarchar(20), N'Organization') AS [ScopeType], [SettingKey], [SettingValue],
            [UpdatedAtUtc], [RowVersion], CONVERT(int, 1) AS [ScopePriority]
        FROM [tb_data].[OrganizationSettings]

        UNION ALL

        SELECT
            CONVERT(nvarchar(20), N'User') AS [ScopeType], [SettingKey], [SettingValue],
            [UpdatedAtUtc], [RowVersion], CONVERT(int, 2) AS [ScopePriority]
        FROM [tb_user].[UserSettings]
        WHERE [OwnerWindowsSid] = @UserSid
          AND @IsReadOnlyPreview = 0
    ),
    ranked AS
    (
        SELECT [ScopeType], [SettingKey], [SettingValue], [UpdatedAtUtc], [RowVersion],
               ROW_NUMBER() OVER (PARTITION BY [SettingKey] ORDER BY [ScopePriority] DESC) AS [Rank]
        FROM settings
    )
    SELECT [ScopeType], [SettingKey], [SettingValue], [UpdatedAtUtc] AS [UpdatedAt], [RowVersion]
    FROM ranked
    WHERE [Rank] = 1
    ORDER BY [SettingKey];
END;
GO

/* Posting payloads contain the exact rendered outbound note and can therefore
   contain Personal Notes. Error messages may echo the same content. Preview
   mode returns safe status metadata only and cannot use keyword search as a
   content-existence oracle for either protected field. */
ALTER PROCEDURE [tb_app].[GetPostingLogs]
    @Destination nvarchar(40) = NULL,
    @Success bit = NULL,
    @Keyword nvarchar(240) = NULL,
    @StartDate date = NULL,
    @EndDate date = NULL,
    @Limit int = 250,
    @IncludeAllUsers bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@UserSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;
    DECLARE @IsReadOnlyPreview bit =
        CONVERT(bit, CASE WHEN USER_NAME() = N'tb_preview_reader' THEN 1 ELSE 0 END);

    IF @IncludeAllUsers = 1 AND @IsManager <> 1 AND @IsAdmin <> 1
        THROW 51330, N'Only a Manager or Admin may read other users'' posting logs.', 1;

    SET @Limit = CASE WHEN @Limit IS NULL OR @Limit < 1 THEN 1 WHEN @Limit > 1000 THEN 1000 ELSE @Limit END;
    SET @Destination = NULLIF(LTRIM(RTRIM(@Destination)), N'');
    IF @Destination = N'Any' SET @Destination = NULL;
    SET @Keyword = NULLIF(LTRIM(RTRIM(@Keyword)), N'');
    DECLARE @KeywordPattern nvarchar(500) =
        CASE WHEN @Keyword IS NULL THEN NULL ELSE N'%' + @Keyword + N'%' END;

    SELECT TOP (@Limit)
        posting_log.[Id],
        posting_log.[WorkEntryId],
        posting_log.[Destination],
        CASE WHEN @IsReadOnlyPreview = 1 THEN N'' ELSE posting_log.[Payload] END AS [Payload],
        posting_log.[Success],
        CASE
            WHEN @IsReadOnlyPreview = 0 THEN posting_log.[Message]
            WHEN posting_log.[Success] = 1 THEN N'Posting succeeded.'
            ELSE N'Posting failed.'
        END AS [Message],
        posting_log.[ExternalReference],
        posting_log.[CreatedAtUtc] AS [CreatedAt]
    FROM [tb_ops].[PostingLogs] AS posting_log
    WHERE (@IncludeAllUsers = 1 OR posting_log.[OwnerWindowsSid] = @UserSid)
      AND (@Destination IS NULL OR posting_log.[Destination] = @Destination)
      AND (@Success IS NULL OR posting_log.[Success] = @Success)
      AND (@StartDate IS NULL OR posting_log.[CreatedAtUtc] >= @StartDate)
      AND (@EndDate IS NULL OR posting_log.[CreatedAtUtc] < DATEADD(day, 1, CONVERT(datetime2(3), @EndDate)))
      AND
      (
          @KeywordPattern IS NULL
          OR posting_log.[Destination] LIKE @KeywordPattern
          OR posting_log.[ExternalReference] LIKE @KeywordPattern
          OR CONVERT(nvarchar(30), posting_log.[WorkEntryId]) LIKE @KeywordPattern
          OR
          (
              @IsReadOnlyPreview = 0
              AND
              (
                  posting_log.[Message] LIKE @KeywordPattern
                  OR posting_log.[Payload] LIKE @KeywordPattern
              )
          )
      )
    ORDER BY posting_log.[CreatedAtUtc] DESC, posting_log.[Id] DESC;
END;
GO

IF OBJECT_ID(N'tb_app.AdminRequestSageSync', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminRequestSageSync];
GO

CREATE PROCEDURE [tb_app].[AdminRequestSageSync]
    @RequestId uniqueidentifier = NULL,
    @AllowLargeRemoval bit = 0,
    @ConfirmedRequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51920, N'Only a currently authorized TechBench Admin may request a Sage customer sync.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_data].[OrganizationSettings]
        WHERE [SettingKey] = N'Sage.SyncDsn'
          AND NULLIF(LTRIM(RTRIM([SettingValue])), N'') IS NOT NULL
    )
        THROW 51923, N'Configure the server Sage System DSN before requesting a Sage customer sync.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_data].[OrganizationSettings]
        WHERE [SettingKey] = N'Sage.SyncUsername'
          AND NULLIF(LTRIM(RTRIM([SettingValue])), N'') IS NOT NULL
    )
        THROW 51924, N'Configure the server Sage username before requesting a Sage customer sync.', 1;

    SET @RequestId = COALESCE(@RequestId, NEWID());
    SET @AllowLargeRemoval = COALESCE(@AllowLargeRemoval, 0);
    IF @AllowLargeRemoval = 0 AND @ConfirmedRequestId IS NOT NULL
        THROW 51925, N'ConfirmedRequestId is valid only for an explicit large-removal approval.', 1;
    IF @AllowLargeRemoval = 1 AND @ConfirmedRequestId IS NULL
        THROW 51926, N'Large-removal approval must reference the rejected Sage sync request whose counts were reviewed.', 1;
    DECLARE @Status nvarchar(30), @Now datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRANSACTION;

        DECLARE @QueueLockResult int;
        EXEC @QueueLockResult = sys.sp_getapplock
            @Resource=N'TechBench.Sage.CustomerSyncQueue', @LockMode=N'Exclusive',
            @LockOwner=N'Transaction', @LockTimeout=5000;
        IF @QueueLockResult < 0
            THROW 51921, N'Could not acquire the Sage customer synchronization queue lock.', 1;

        IF @AllowLargeRemoval = 1
           AND NOT EXISTS
           (
               SELECT 1
               FROM [tb_sync].[SageSyncRequests] WITH (UPDLOCK, HOLDLOCK)
               WHERE [RequestId] = @ConfirmedRequestId
                 AND [Status] = N'Failed'
                 AND [RequiresLargeRemovalConfirmation] = 1
                 AND [CompletedAtUtc] >= DATEADD(hour, -1, @Now)
           )
            THROW 51927, N'The referenced Sage removal proposal is missing, no longer eligible, or more than one hour old. Request a new unapproved sync.', 1;

        DECLARE @ExistingRequestId uniqueidentifier, @AuditEntityId nvarchar(120);
        SELECT TOP (1) @ExistingRequestId = [RequestId]
        FROM [tb_sync].[SageSyncRequests] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Status] IN (N'Queued', N'Running')
        ORDER BY [RequestedAtUtc], [RequestId];

        IF @ExistingRequestId IS NOT NULL
        BEGIN
            SET @RequestId = @ExistingRequestId;
            SET @Status = N'AlreadyQueued';
        END
        ELSE
        BEGIN
            INSERT INTO [tb_sync].[SageSyncRequests]
                (
                    [RequestId], [RequestedByWindowsSid], [RequestedAtUtc], [Status],
                    [AllowLargeRemoval], [ConfirmedRequestId]
                )
            VALUES
                (@RequestId, @ActorSid, @Now, N'Queued', @AllowLargeRemoval, @ConfirmedRequestId);
            SET @Status = N'Queued';

            SET @AuditEntityId = CONVERT(nvarchar(36), @RequestId);
            DECLARE @AuditData nvarchar(max) = CASE WHEN @AllowLargeRemoval = 1
                THEN N'{"allowLargeRemoval":true,"confirmedRequestId":"'
                     + CONVERT(nvarchar(36), @ConfirmedRequestId) + N'"}'
                ELSE N'{"allowLargeRemoval":false}' END;
            EXEC [tb_security].[WriteAuditEvent]
                @Action=N'SageCustomerSyncRequested', @EntityType=N'SageSyncRequest',
                @EntityId=@AuditEntityId, @RequestId=@RequestId, @DataJson=@AuditData;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT request_row.[RequestId], @Status AS [Status], CONVERT(int, 1) AS [QueueDepth],
           request_row.[AllowLargeRemoval], request_row.[ConfirmedRequestId]
    FROM [tb_sync].[SageSyncRequests] AS request_row
    WHERE request_row.[RequestId] = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.GetSageSyncStatus', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetSageSyncStatus];
GO

CREATE PROCEDURE [tb_app].[GetSageSyncStatus]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51922, N'Only a currently authorized TechBench Admin may view Sage sync status.', 1;

    DECLARE @QueueDepth int =
    (
        SELECT COUNT(*) FROM [tb_sync].[SageSyncRequests]
        WHERE [Status] IN (N'Queued', N'Running')
    );

    ;WITH latest AS
    (
        SELECT TOP (1)
            [RequestId], [ConfirmedRequestId], [Status], [Message], [RequestedAtUtc], [CompletedAtUtc],
            [AllowLargeRemoval], [RequiresLargeRemovalConfirmation],
            [ExistingCount], [ReadCount], [SavedCount], [StaleCount]
        FROM [tb_sync].[SageSyncRequests]
        ORDER BY [RequestedAtUtc] DESC, [RequestId] DESC
    )
    SELECT
        [RequestId], [ConfirmedRequestId], COALESCE([Status], N'NeverRun') AS [Status], [Message],
        @QueueDepth AS [QueueDepth], [RequestedAtUtc], [CompletedAtUtc],
        COALESCE([AllowLargeRemoval], 0) AS [AllowLargeRemoval],
        COALESCE([RequiresLargeRemovalConfirmation], 0) AS [RequiresLargeRemovalConfirmation],
        COALESCE([ExistingCount], 0) AS [ExistingCount],
        COALESCE([ReadCount], 0) AS [ReadCount],
        COALESCE([SavedCount], 0) AS [SavedCount],
        COALESCE([StaleCount], 0) AS [StaleCount]
    FROM latest
    RIGHT JOIN (SELECT CONVERT(bit, 1) AS [OneRow]) AS singleton ON 1 = 1;

    SELECT [LastAttemptAtUtc], [LastSuccessfulAtUtc], [LastError]
    FROM [tb_sync].[SageSyncHealth]
    WHERE [HealthId] = 1;
END;
GO

IF OBJECT_ID(N'tb_service.GetSageSyncConfiguration', N'P') IS NOT NULL
    DROP PROCEDURE [tb_service].[GetSageSyncConfiguration];
GO

CREATE PROCEDURE [tb_service].[GetSageSyncConfiguration]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SELECT
        COALESCE(MAX(CASE WHEN [SettingKey] = N'Sage.SyncDsn' THEN [SettingValue] END), N'') AS [Dsn],
        COALESCE(MAX(CASE WHEN [SettingKey] = N'Sage.SyncUsername' THEN [SettingValue] END), N'') AS [Username]
    FROM [tb_data].[OrganizationSettings]
    WHERE [SettingKey] IN (N'Sage.SyncDsn', N'Sage.SyncUsername');
END;
GO

IF OBJECT_ID(N'tb_service.ClaimSageSyncWork', N'P') IS NOT NULL
    DROP PROCEDURE [tb_service].[ClaimSageSyncWork];
GO

CREATE PROCEDURE [tb_service].[ClaimSageSyncWork]
    @WorkerId uniqueidentifier,
    @LeaseSeconds int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @WorkerId IS NULL OR @LeaseSeconds NOT BETWEEN 15 AND 3600
        THROW 51930, N'WorkerId and a lease from 15 to 3600 seconds are required.', 1;

    DECLARE @WorkId uniqueidentifier, @LeaseId uniqueidentifier = NEWID();
    DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @Until datetime2(3);
    SET @Until = DATEADD(second, @LeaseSeconds, @Now);

    BEGIN TRY
        BEGIN TRANSACTION;

        DECLARE @QueueLockResult int;
        EXEC @QueueLockResult = sys.sp_getapplock
            @Resource=N'TechBench.Sage.CustomerSyncQueue', @LockMode=N'Exclusive',
            @LockOwner=N'Transaction', @LockTimeout=5000;
        IF @QueueLockResult < 0
            THROW 51931, N'Could not acquire the Sage customer synchronization queue lock.', 1;

        SELECT TOP (1) @WorkId = request_row.[RequestId]
        FROM [tb_sync].[SageSyncRequests] AS request_row WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK, ROWLOCK)
        LEFT JOIN [tb_sync].[SageSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
            ON lease.[RequestId] = request_row.[RequestId]
        WHERE request_row.[Status] = N'Queued'
           OR (request_row.[Status] = N'Running' AND (lease.[RequestId] IS NULL OR lease.[ExpiresAtUtc] <= @Now))
        ORDER BY request_row.[RequestedAtUtc], request_row.[RequestId];

        IF @WorkId IS NOT NULL
        BEGIN
            DELETE FROM [tb_sync].[SageSyncLeases] WHERE [RequestId] = @WorkId;
            INSERT INTO [tb_sync].[SageSyncLeases]
                ([RequestId], [LeaseId], [WorkerId], [AcquiredAtUtc], [ExpiresAtUtc])
            VALUES
                (@WorkId, @LeaseId, @WorkerId, @Now, @Until);

            UPDATE [tb_sync].[SageSyncRequests]
            SET [Status] = N'Running', [StartedAtUtc] = COALESCE([StartedAtUtc], @Now),
                [CompletedAtUtc] = NULL, [AttemptCount] = [AttemptCount] + 1,
                [ExistingCount] = 0, [ReadCount] = 0, [SavedCount] = 0, [StaleCount] = 0,
                [RequiresLargeRemovalConfirmation] = 0, [Message] = NULL
            WHERE [RequestId] = @WorkId;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT request_row.[RequestId] AS [WorkId], lease.[LeaseId], lease.[WorkerId],
           lease.[ExpiresAtUtc], request_row.[AllowLargeRemoval]
    FROM [tb_sync].[SageSyncRequests] AS request_row
    INNER JOIN [tb_sync].[SageSyncLeases] AS lease ON lease.[RequestId] = request_row.[RequestId]
    WHERE request_row.[RequestId] = @WorkId;
END;
GO

IF OBJECT_ID(N'tb_service.RenewSageSyncLease', N'P') IS NOT NULL
    DROP PROCEDURE [tb_service].[RenewSageSyncLease];
GO

CREATE PROCEDURE [tb_service].[RenewSageSyncLease]
    @WorkId uniqueidentifier,
    @LeaseId uniqueidentifier,
    @WorkerId uniqueidentifier,
    @LeaseSeconds int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @WorkId IS NULL OR @LeaseId IS NULL OR @WorkerId IS NULL OR @LeaseSeconds NOT BETWEEN 15 AND 3600
        THROW 51932, N'WorkId, LeaseId, WorkerId, and a lease from 15 to 3600 seconds are required.', 1;

    DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @Until datetime2(3);
    SET @Until = DATEADD(second, @LeaseSeconds, @Now);

    UPDATE lease
    SET [ExpiresAtUtc] = @Until
    FROM [tb_sync].[SageSyncLeases] AS lease
    INNER JOIN [tb_sync].[SageSyncRequests] AS request_row
        ON request_row.[RequestId] = lease.[RequestId]
    WHERE lease.[RequestId] = @WorkId
      AND lease.[LeaseId] = @LeaseId
      AND lease.[WorkerId] = @WorkerId
      AND lease.[ExpiresAtUtc] > @Now
      AND request_row.[Status] = N'Running';

    IF @@ROWCOUNT <> 1
        THROW 51933, N'The Sage sync lease is missing, expired, or owned by another worker.', 1;

    SELECT @WorkId AS [WorkId], @LeaseId AS [LeaseId], @WorkerId AS [WorkerId], @Until AS [ExpiresAtUtc];
END;
GO

IF OBJECT_ID(N'tb_service.ApplySageCustomerSnapshot', N'P') IS NOT NULL
    DROP PROCEDURE [tb_service].[ApplySageCustomerSnapshot];
GO

CREATE PROCEDURE [tb_service].[ApplySageCustomerSnapshot]
    @WorkId uniqueidentifier,
    @LeaseId uniqueidentifier,
    @WorkerId uniqueidentifier,
    @Json nvarchar(max),
    @SyncedAtUtc datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @WorkId IS NULL OR @LeaseId IS NULL OR @WorkerId IS NULL
        THROW 51934, N'WorkId, LeaseId, and WorkerId are required.', 1;
    IF COALESCE(ISJSON(@Json), 0) <> 1
       OR LEFT(LTRIM(@Json), 1) <> N'['
       OR RIGHT(RTRIM(@Json), 1) <> N']'
       OR @SyncedAtUtc IS NULL
        THROW 51935, N'A non-empty Sage customer JSON array and SyncedAtUtc are required.', 1;

    DECLARE @ActorSid varbinary(85) =
    (
        SELECT [WindowsSid]
        FROM [tb_security].[Users]
        WHERE [LoginName] = N'$(SyncServicePrincipal)'
    );
    IF @ActorSid IS NULL
        THROW 51936, N'The configured sync service principal has no TechBench service actor.', 1;

    /* Preserve every array element until it has been validated. OPENJSON WITH
       would truncate over-length values and the old filter/ranking path could
       silently discard malformed or duplicate customers before reconciliation. */
    DECLARE @RawSnapshot TABLE
    (
        [Ordinal] int NOT NULL PRIMARY KEY,
        [JsonText] nvarchar(max) NULL,
        [JsonType] int NOT NULL
    );

    INSERT INTO @RawSnapshot([Ordinal], [JsonText], [JsonType])
    SELECT TRY_CONVERT(int, json_row.[key]), json_row.[value], json_row.[type]
    FROM OPENJSON(@Json) AS json_row;

    DECLARE @ReadCount int = (SELECT COUNT(*) FROM @RawSnapshot);
    IF @ReadCount = 0
        THROW 51937, N'The Sage customer snapshot was empty; no data was changed.', 1;
    IF EXISTS (SELECT 1 FROM @RawSnapshot WHERE [JsonType] <> 5 OR [Ordinal] IS NULL)
        THROW 51942, N'Every Sage customer snapshot element must be a JSON object; no data was changed.', 1;

    DECLARE @ExtractedSnapshot TABLE
    (
        [Ordinal] int NOT NULL PRIMARY KEY,
        [CustomerId] nvarchar(max) NULL,
        [CustomerIdCount] int NOT NULL,
        [CustomerIdType] int NULL,
        [CustomerName] nvarchar(max) NULL,
        [CustomerNameCount] int NOT NULL,
        [CustomerNameType] int NULL,
        [ContactName] nvarchar(max) NULL,
        [ContactNameCount] int NOT NULL,
        [ContactNameType] int NULL,
        [Telephone] nvarchar(max) NULL,
        [TelephoneCount] int NOT NULL,
        [TelephoneType] int NULL,
        [IsActiveText] nvarchar(max) NULL,
        [IsActiveCount] int NOT NULL,
        [IsActiveType] int NULL
    );

    INSERT INTO @ExtractedSnapshot
    (
        [Ordinal], [CustomerId], [CustomerIdCount], [CustomerIdType],
        [CustomerName], [CustomerNameCount], [CustomerNameType],
        [ContactName], [ContactNameCount], [ContactNameType],
        [Telephone], [TelephoneCount], [TelephoneType],
        [IsActiveText], [IsActiveCount], [IsActiveType]
    )
    SELECT
        raw.[Ordinal],
        MAX(CASE WHEN property_row.[key] = N'customerId' THEN property_row.[value] END),
        SUM(CASE WHEN property_row.[key] = N'customerId' THEN 1 ELSE 0 END),
        MAX(CASE WHEN property_row.[key] = N'customerId' THEN property_row.[type] END),
        MAX(CASE WHEN property_row.[key] = N'customerName' THEN property_row.[value] END),
        SUM(CASE WHEN property_row.[key] = N'customerName' THEN 1 ELSE 0 END),
        MAX(CASE WHEN property_row.[key] = N'customerName' THEN property_row.[type] END),
        MAX(CASE WHEN property_row.[key] = N'contactName' THEN property_row.[value] END),
        SUM(CASE WHEN property_row.[key] = N'contactName' THEN 1 ELSE 0 END),
        MAX(CASE WHEN property_row.[key] = N'contactName' THEN property_row.[type] END),
        MAX(CASE WHEN property_row.[key] = N'telephone' THEN property_row.[value] END),
        SUM(CASE WHEN property_row.[key] = N'telephone' THEN 1 ELSE 0 END),
        MAX(CASE WHEN property_row.[key] = N'telephone' THEN property_row.[type] END),
        MAX(CASE WHEN property_row.[key] = N'isActive' THEN property_row.[value] END),
        SUM(CASE WHEN property_row.[key] = N'isActive' THEN 1 ELSE 0 END),
        MAX(CASE WHEN property_row.[key] = N'isActive' THEN property_row.[type] END)
    FROM @RawSnapshot AS raw
    OUTER APPLY OPENJSON(raw.[JsonText]) AS property_row
    GROUP BY raw.[Ordinal];

    IF EXISTS
    (
        SELECT 1
        FROM @ExtractedSnapshot
        WHERE [CustomerIdCount] <> 1 OR [CustomerIdType] <> 1
           OR NULLIF(LTRIM(RTRIM([CustomerId])), N'') IS NULL
           OR LEN(LTRIM(RTRIM([CustomerId]))) > 120
           OR [CustomerNameCount] <> 1 OR [CustomerNameType] <> 1
           OR NULLIF(LTRIM(RTRIM([CustomerName])), N'') IS NULL
           OR LEN(LTRIM(RTRIM([CustomerName]))) > 240
           OR [ContactNameCount] > 1
           OR ([ContactNameCount] = 1 AND [ContactNameType] NOT IN (0, 1))
           OR LEN(LTRIM(RTRIM(COALESCE([ContactName], N'')))) > 240
           OR [TelephoneCount] > 1
           OR ([TelephoneCount] = 1 AND [TelephoneType] NOT IN (0, 1))
           OR LEN(LTRIM(RTRIM(COALESCE([Telephone], N'')))) > 80
           OR [IsActiveCount] <> 1 OR [IsActiveType] <> 3
           OR [IsActiveText] NOT IN (N'true', N'false')
    )
        THROW 51943, N'The Sage customer snapshot contains a missing, malformed, or over-length field; no data was changed.', 1;

    IF EXISTS
    (
        SELECT NULLIF(LTRIM(RTRIM([CustomerId])), N'')
        FROM @ExtractedSnapshot
        GROUP BY NULLIF(LTRIM(RTRIM([CustomerId])), N'')
        HAVING COUNT(*) > 1
    )
        THROW 51944, N'The Sage customer snapshot contains duplicate customer IDs; no data was changed.', 1;

    DECLARE @Snapshot TABLE
    (
        [CustomerId] nvarchar(120) NOT NULL PRIMARY KEY,
        [CustomerName] nvarchar(240) NOT NULL,
        [ContactName] nvarchar(240) NULL,
        [Telephone] nvarchar(80) NULL,
        [IsActive] bit NOT NULL
    );

    INSERT INTO @Snapshot([CustomerId], [CustomerName], [ContactName], [Telephone], [IsActive])
    SELECT
        LTRIM(RTRIM([CustomerId])),
        LTRIM(RTRIM([CustomerName])),
        NULLIF(LTRIM(RTRIM([ContactName])), N''),
        NULLIF(LTRIM(RTRIM([Telephone])), N''),
        CONVERT(bit, CASE WHEN [IsActiveText] = N'true' THEN 1 ELSE 0 END)
    FROM @ExtractedSnapshot;

    DECLARE @ExistingCount int = 0, @SavedCount int = 0, @StaleCount int = 0, @MatchedCount int = 0;
    DECLARE @AllowLargeRemoval bit = 0, @RequiresLargeRemovalConfirmation bit = 0;
    DECLARE @ConfirmedRequestId uniqueidentifier = NULL, @ConfirmationMatches bit = 0;
    DECLARE @ResultMessage nvarchar(2000) = NULL;
    DECLARE @Now datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @AllowLargeRemoval = request_row.[AllowLargeRemoval],
               @ConfirmedRequestId = request_row.[ConfirmedRequestId]
        FROM [tb_sync].[SageSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN [tb_sync].[SageSyncRequests] AS request_row WITH (UPDLOCK, HOLDLOCK)
            ON request_row.[RequestId] = lease.[RequestId]
        WHERE lease.[RequestId] = @WorkId
          AND lease.[LeaseId] = @LeaseId
          AND lease.[WorkerId] = @WorkerId
          AND lease.[ExpiresAtUtc] > @Now
          AND request_row.[Status] = N'Running';

        IF @@ROWCOUNT <> 1
            THROW 51938, N'A valid unexpired Sage sync lease is required to apply a customer snapshot.', 1;

        SELECT @ExistingCount = COUNT(*)
        FROM [tb_data].[ClientExternalIdentities] WITH (UPDLOCK, HOLDLOCK)
        WHERE [SourceSystem] = N'Sage';

        SELECT @StaleCount = COUNT(*)
        FROM [tb_data].[ClientExternalIdentities] AS identity_row WITH (UPDLOCK, HOLDLOCK)
        WHERE identity_row.[SourceSystem] = N'Sage'
          AND NOT EXISTS
          (
              SELECT 1
              FROM @Snapshot AS snapshot
              WHERE snapshot.[CustomerId] = identity_row.[ExternalId]
          );

        IF @AllowLargeRemoval = 1
           AND @ConfirmedRequestId IS NOT NULL
           AND EXISTS
           (
               SELECT 1
               FROM [tb_sync].[SageSyncRequests] AS confirmed_request WITH (UPDLOCK, HOLDLOCK)
               WHERE confirmed_request.[RequestId] = @ConfirmedRequestId
                 AND confirmed_request.[Status] = N'Failed'
                 AND confirmed_request.[RequiresLargeRemovalConfirmation] = 1
                 AND confirmed_request.[CompletedAtUtc] >= DATEADD(hour, -1, @Now)
                 AND confirmed_request.[ExistingCount] = @ExistingCount
                 AND confirmed_request.[ReadCount] = @ReadCount
                 AND confirmed_request.[StaleCount] = @StaleCount
           )
            SET @ConfirmationMatches = 1;

        /* First imports and small cleanups proceed normally. A snapshot that
           would remove at least ten and at least 25 percent of an established
           Sage identity set requires a new, explicitly confirmed Admin request. */
        IF @ConfirmationMatches <> 1
           AND @ExistingCount >= 20
           AND @StaleCount >= 10
           AND CONVERT(bigint, @StaleCount) * 100 >= CONVERT(bigint, @ExistingCount) * 25
        BEGIN
            SET @RequiresLargeRemovalConfirmation = 1;
            SET @ResultMessage =
                N'Sage returned ' + CONVERT(nvarchar(20), @ReadCount)
                + N' active customer(s), which would remove '
                + CONVERT(nvarchar(20), @StaleCount) + N' of '
                + CONVERT(nvarchar(20), @ExistingCount)
                + N' existing Sage customer mapping(s). No customer data was changed. An Admin must explicitly confirm these exact counts; any changed rerun requires a new confirmation.';

            UPDATE [tb_sync].[SageSyncRequests]
            SET [ExistingCount] = @ExistingCount, [ReadCount] = @ReadCount,
                [SavedCount] = 0, [StaleCount] = @StaleCount,
                [RequiresLargeRemovalConfirmation] = 1, [Message] = @ResultMessage
            WHERE [RequestId] = @WorkId AND [Status] = N'Running';

            COMMIT TRANSACTION;

            SELECT @ReadCount AS [ReadCount], CONVERT(int, 0) AS [SavedCount],
                   @StaleCount AS [StaleCount], CONVERT(int, 0) AS [MatchedCount],
                   @ExistingCount AS [ExistingCount],
                   @RequiresLargeRemovalConfirmation AS [RequiresLargeRemovalConfirmation],
                   @ResultMessage AS [Message];
            RETURN;
        END;

        /* Upgrade legacy Sage columns into the canonical identity table before
           matching the server snapshot. One canonical client wins per ID. */
        ;WITH legacy_candidates AS
        (
            SELECT
                client.[Id] AS [ClientId], client.[SageCustomerId],
                ROW_NUMBER() OVER (PARTITION BY client.[SageCustomerId] ORDER BY client.[Id]) AS [RowNumber]
            FROM [tb_data].[Clients] AS client
            INNER JOIN @Snapshot AS snapshot ON snapshot.[CustomerId] = client.[SageCustomerId]
            WHERE NULLIF(LTRIM(RTRIM(client.[SageCustomerId])), N'') IS NOT NULL
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM [tb_data].[ClientExternalIdentities] AS existing WITH (UPDLOCK, HOLDLOCK)
                  WHERE existing.[SourceSystem] = N'Sage'
                    AND existing.[ExternalId] = client.[SageCustomerId]
              )
        )
        INSERT INTO [tb_data].[ClientExternalIdentities]
        (
            [ClientId], [SourceSystem], [ExternalId], [ExternalName], [LastSyncedAtUtc],
            [CreatedByWindowsSid], [UpdatedByWindowsSid], [CreatedAtUtc], [UpdatedAtUtc]
        )
        SELECT
            legacy.[ClientId], N'Sage', snapshot.[CustomerId], snapshot.[CustomerName], @SyncedAtUtc,
            @ActorSid, @ActorSid, @Now, @Now
        FROM legacy_candidates AS legacy
        INNER JOIN @Snapshot AS snapshot ON snapshot.[CustomerId] = legacy.[SageCustomerId]
        WHERE legacy.[RowNumber] = 1;

        UPDATE identity_row
        SET [ExternalName] = snapshot.[CustomerName], [LastSyncedAtUtc] = @SyncedAtUtc,
            [UpdatedByWindowsSid] = @ActorSid, [UpdatedAtUtc] = @Now
        FROM [tb_data].[ClientExternalIdentities] AS identity_row
        INNER JOIN @Snapshot AS snapshot ON snapshot.[CustomerId] = identity_row.[ExternalId]
        WHERE identity_row.[SourceSystem] = N'Sage';

        DECLARE @NewClients TABLE
        (
            [CustomerId] nvarchar(120) NOT NULL PRIMARY KEY,
            [ClientId] int NOT NULL
        );

        INSERT INTO [tb_data].[Clients]
        (
            [Name], [Source], [ExternalId], [IsActive], [LastSyncedAtUtc],
            [SageCustomerId], [SageCustomerName], [SageContactName], [SageTelephone],
            [MatchStatus], [CreatedByWindowsSid], [UpdatedByWindowsSid],
            [CreatedAtUtc], [UpdatedAtUtc]
        )
        OUTPUT inserted.[SageCustomerId], inserted.[Id]
            INTO @NewClients([CustomerId], [ClientId])
        SELECT
            snapshot.[CustomerName], N'Sage', snapshot.[CustomerId], snapshot.[IsActive], @SyncedAtUtc,
            snapshot.[CustomerId], snapshot.[CustomerName], snapshot.[ContactName], snapshot.[Telephone],
            N'Unmatched', @ActorSid, @ActorSid, @Now, @Now
        FROM @Snapshot AS snapshot
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[ClientExternalIdentities] AS existing WITH (UPDLOCK, HOLDLOCK)
            WHERE existing.[SourceSystem] = N'Sage'
              AND existing.[ExternalId] = snapshot.[CustomerId]
        );

        INSERT INTO [tb_data].[ClientExternalIdentities]
        (
            [ClientId], [SourceSystem], [ExternalId], [ExternalName], [LastSyncedAtUtc],
            [CreatedByWindowsSid], [UpdatedByWindowsSid], [CreatedAtUtc], [UpdatedAtUtc]
        )
        SELECT
            new_client.[ClientId], N'Sage', snapshot.[CustomerId], snapshot.[CustomerName], @SyncedAtUtc,
            @ActorSid, @ActorSid, @Now, @Now
        FROM @NewClients AS new_client
        INNER JOIN @Snapshot AS snapshot ON snapshot.[CustomerId] = new_client.[CustomerId];

        UPDATE client
        SET
            [Name] = CASE WHEN whd_identity.[ClientId] IS NULL THEN snapshot.[CustomerName] ELSE client.[Name] END,
            [Source] = CASE WHEN whd_identity.[ClientId] IS NULL THEN N'Sage' ELSE N'Both' END,
            [ExternalId] = CASE WHEN whd_identity.[ClientId] IS NULL THEN snapshot.[CustomerId] ELSE client.[ExternalId] END,
            [IsActive] = CASE WHEN whd_identity.[ClientId] IS NULL THEN snapshot.[IsActive] ELSE client.[IsActive] END,
            [LastSyncedAtUtc] = @SyncedAtUtc,
            [SageCustomerId] = snapshot.[CustomerId], [SageCustomerName] = snapshot.[CustomerName],
            [SageContactName] = snapshot.[ContactName], [SageTelephone] = snapshot.[Telephone],
            [MatchStatus] = CASE WHEN whd_identity.[ClientId] IS NULL THEN N'Unmatched' ELSE N'Matched' END,
            [UpdatedByWindowsSid] = @ActorSid, [UpdatedAtUtc] = @Now
        FROM [tb_data].[Clients] AS client
        INNER JOIN [tb_data].[ClientExternalIdentities] AS sage_identity
            ON sage_identity.[ClientId] = client.[Id] AND sage_identity.[SourceSystem] = N'Sage'
        INNER JOIN @Snapshot AS snapshot ON snapshot.[CustomerId] = sage_identity.[ExternalId]
        OUTER APPLY
        (
            SELECT TOP (1) whd.[ClientId]
            FROM [tb_data].[ClientExternalIdentities] AS whd
            WHERE whd.[ClientId] = client.[Id] AND whd.[SourceSystem] = N'WHD'
        ) AS whd_identity;

        SET @SavedCount = @ReadCount;

        DECLARE @StaleIdentities TABLE
        (
            [IdentityId] bigint NOT NULL PRIMARY KEY,
            [ClientId] int NOT NULL
        );

        INSERT INTO @StaleIdentities([IdentityId], [ClientId])
        SELECT identity_row.[Id], identity_row.[ClientId]
        FROM [tb_data].[ClientExternalIdentities] AS identity_row
        WHERE identity_row.[SourceSystem] = N'Sage'
          AND NOT EXISTS
          (
              SELECT 1 FROM @Snapshot AS snapshot
              WHERE snapshot.[CustomerId] = identity_row.[ExternalId]
          );
        SET @StaleCount = @@ROWCOUNT;

        DELETE identity_row
        FROM [tb_data].[ClientExternalIdentities] AS identity_row
        INNER JOIN @StaleIdentities AS stale ON stale.[IdentityId] = identity_row.[Id];

        ;WITH removed_clients AS
        (
            SELECT DISTINCT stale.[ClientId]
            FROM @StaleIdentities AS stale
            WHERE NOT EXISTS
            (
                SELECT 1 FROM [tb_data].[ClientExternalIdentities] AS remaining_sage
                WHERE remaining_sage.[ClientId] = stale.[ClientId]
                  AND remaining_sage.[SourceSystem] = N'Sage'
            )
        )
        UPDATE client
        SET
            [Source] = CASE WHEN whd_identity.[ClientId] IS NULL THEN N'Sage' ELSE N'WHD' END,
            [IsActive] = CASE WHEN whd_identity.[ClientId] IS NULL THEN CONVERT(bit, 0) ELSE client.[IsActive] END,
            [SageCustomerId] = NULL, [SageCustomerName] = NULL,
            [SageContactName] = NULL, [SageTelephone] = NULL,
            [MatchStatus] = N'Unmatched', [LastSyncedAtUtc] = @SyncedAtUtc,
            [UpdatedByWindowsSid] = @ActorSid, [UpdatedAtUtc] = @Now
        FROM [tb_data].[Clients] AS client
        INNER JOIN removed_clients AS removed ON removed.[ClientId] = client.[Id]
        OUTER APPLY
        (
            SELECT TOP (1) whd.[ClientId]
            FROM [tb_data].[ClientExternalIdentities] AS whd
            WHERE whd.[ClientId] = client.[Id] AND whd.[SourceSystem] = N'WHD'
        ) AS whd_identity;

        SELECT @MatchedCount = COUNT(DISTINCT client.[Id])
        FROM [tb_data].[Clients] AS client
        INNER JOIN [tb_data].[ClientExternalIdentities] AS sage_identity
            ON sage_identity.[ClientId] = client.[Id] AND sage_identity.[SourceSystem] = N'Sage'
        INNER JOIN @Snapshot AS snapshot ON snapshot.[CustomerId] = sage_identity.[ExternalId]
        WHERE client.[Source] = N'Both';

        UPDATE [tb_sync].[SageSyncRequests]
        SET [ExistingCount] = @ExistingCount, [ReadCount] = @ReadCount,
            [SavedCount] = @SavedCount, [StaleCount] = @StaleCount,
            [RequiresLargeRemovalConfirmation] = 0, [Message] = NULL
        WHERE [RequestId] = @WorkId AND [Status] = N'Running';

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT @ReadCount AS [ReadCount], @SavedCount AS [SavedCount],
           @StaleCount AS [StaleCount], @MatchedCount AS [MatchedCount],
           @ExistingCount AS [ExistingCount],
           @RequiresLargeRemovalConfirmation AS [RequiresLargeRemovalConfirmation],
           @ResultMessage AS [Message];
END;
GO

IF OBJECT_ID(N'tb_service.CompleteSageSyncWork', N'P') IS NOT NULL
    DROP PROCEDURE [tb_service].[CompleteSageSyncWork];
GO

CREATE PROCEDURE [tb_service].[CompleteSageSyncWork]
    @WorkId uniqueidentifier,
    @LeaseId uniqueidentifier,
    @WorkerId uniqueidentifier,
    @Succeeded bit,
    @Message nvarchar(2000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @WorkId IS NULL OR @LeaseId IS NULL OR @WorkerId IS NULL OR @Succeeded IS NULL
        THROW 51939, N'WorkId, LeaseId, WorkerId, and Succeeded are required.', 1;

    SET @Message = NULLIF(LTRIM(RTRIM(@Message)), N'');
    DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @ReadCount int;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @ReadCount = request_row.[ReadCount]
        FROM [tb_sync].[SageSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN [tb_sync].[SageSyncRequests] AS request_row WITH (UPDLOCK, HOLDLOCK)
            ON request_row.[RequestId] = lease.[RequestId]
        WHERE lease.[RequestId] = @WorkId
          AND lease.[LeaseId] = @LeaseId
          AND lease.[WorkerId] = @WorkerId
          AND lease.[ExpiresAtUtc] > @Now
          AND request_row.[Status] = N'Running';

        IF @ReadCount IS NULL
            THROW 51940, N'A valid unexpired Sage sync lease is required to complete this work.', 1;
        IF @Succeeded = 1 AND @ReadCount = 0
            THROW 51941, N'A Sage sync cannot succeed before a non-empty customer snapshot is applied.', 1;

        UPDATE [tb_sync].[SageSyncRequests]
        SET [Status] = CASE WHEN @Succeeded = 1 THEN N'Completed' ELSE N'Failed' END,
            [CompletedAtUtc] = @Now,
            [Message] = CASE WHEN @Succeeded = 1 THEN @Message ELSE COALESCE(@Message, N'Sage customer synchronization failed.') END
        WHERE [RequestId] = @WorkId;

        UPDATE [tb_sync].[SageSyncHealth]
        SET [LastAttemptAtUtc] = @Now,
            [LastSuccessfulAtUtc] = CASE WHEN @Succeeded = 1 THEN @Now ELSE [LastSuccessfulAtUtc] END,
            [LastError] = CASE WHEN @Succeeded = 1 THEN NULL ELSE COALESCE(@Message, N'Sage customer synchronization failed.') END,
            [UpdatedAtUtc] = @Now
        WHERE [HealthId] = 1;

        DELETE FROM [tb_sync].[SageSyncLeases]
        WHERE [RequestId] = @WorkId AND [LeaseId] = @LeaseId AND [WorkerId] = @WorkerId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT [RequestId] AS [WorkId], [Status], [Message],
           [ReadCount], [SavedCount], [StaleCount], [CompletedAtUtc]
    FROM [tb_sync].[SageSyncRequests]
    WHERE [RequestId] = @WorkId;
END;
GO

/* Rebuild WHD row-level security so a valid preview is scoped to the target
   technician and the Admin's ordinary bypass cannot win after impersonation.
   All DDL participates in one transaction: any ALTER FUNCTION or CREATE
   POLICY failure rolls the original enabled policy back instead of leaving a
   fail-open interval after DROP SECURITY POLICY. */
IF OBJECT_ID(N'tb_security.FilterWhdTicketAccess', N'IF') IS NULL
    THROW 51950, N'The V0006 WHD ticket access function is missing.', 1;

DECLARE @RlsFunctionSql nvarchar(max) = N'
ALTER FUNCTION [tb_security].[FilterWhdTicketAccess]
(
    @Source nvarchar(40),
    @AssignedTechExternalId nvarchar(120),
    @AssignedGroupExternalId nvarchar(120)
)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
(
    SELECT CONVERT(bit, 1) AS [AccessAllowed]
    WHERE @Source <> N''WHD''
       OR
       (
           USER_NAME() = N''tb_preview_reader''
           AND EXISTS
           (
               SELECT 1
               FROM [tb_security].[AdminUserPreviewSessions] AS preview_session
               INNER JOIN [tb_security].[Users] AS actor_user
                   ON actor_user.[WindowsSid] = preview_session.[ActorWindowsSid]
               INNER JOIN [tb_security].[Users] AS target_user
                   ON target_user.[WindowsSid] = preview_session.[TargetWindowsSid]
               INNER JOIN [tb_whd].[UserTechnicianMappings] AS mapping
                   ON mapping.[WindowsSid] = preview_session.[TargetWindowsSid]
               WHERE preview_session.[PreviewSessionId] = TRY_CONVERT
                     (
                         uniqueidentifier,
                         CONVERT(nvarchar(36), SESSION_CONTEXT(N''TechBench.PreviewSessionId''))
                     )
                 AND preview_session.[ActorWindowsSid] = SUSER_SID(ORIGINAL_LOGIN())
                 AND preview_session.[EndedAtUtc] IS NULL
                 AND preview_session.[ExpiresAtUtc] > SYSUTCDATETIME()
                 AND actor_user.[IsAdmin] = 1
                 AND target_user.[IsTechnician] = 1
                 AND target_user.[IsAdmin] = 0
                 AND target_user.[LastSeenAtUtc] >= DATEADD(hour, -1, SYSUTCDATETIME())
                 AND
                 (
                     mapping.[TechnicianExternalId] = @AssignedTechExternalId
                     OR EXISTS
                     (
                         SELECT 1
                         FROM [tb_whd].[TechnicianGroupMemberships] AS membership
                         WHERE membership.[TechnicianExternalId] = mapping.[TechnicianExternalId]
                           AND membership.[GroupExternalId] = @AssignedGroupExternalId
                     )
                 )
           )
       )
       OR
       (
           USER_NAME() <> N''tb_preview_reader''
           AND SESSION_CONTEXT(N''TechBench.PreviewSessionId'') IS NULL
           AND
           (
               USER_NAME() = N''dbo''
               OR IS_ROLEMEMBER(N''db_owner'') = 1
               OR IS_ROLEMEMBER(N''tb_role_admin'') = 1
               OR IS_ROLEMEMBER(N''tb_role_sync_service'') = 1
               OR EXISTS
               (
                   SELECT 1
                   FROM [tb_whd].[UserTechnicianMappings] AS mapping
                   WHERE mapping.[WindowsSid] = SUSER_SID(ORIGINAL_LOGIN())
                     AND
                     (
                         mapping.[TechnicianExternalId] = @AssignedTechExternalId
                         OR EXISTS
                         (
                             SELECT 1
                             FROM [tb_whd].[TechnicianGroupMemberships] AS membership
                             WHERE membership.[TechnicianExternalId] = mapping.[TechnicianExternalId]
                               AND membership.[GroupExternalId] = @AssignedGroupExternalId
                         )
                     )
               )
           )
       )
);';

DECLARE @RlsPolicySql nvarchar(max) = N'
CREATE SECURITY POLICY [tb_security].[WhdTicketAccessPolicy]
    ADD FILTER PREDICATE [tb_security].[FilterWhdTicketAccess]
        ([Source], [AssignedTechExternalId], [AssignedGroupExternalId])
        ON [tb_data].[Tickets],
    ADD BLOCK PREDICATE [tb_security].[FilterWhdTicketAccess]
        ([Source], [AssignedTechExternalId], [AssignedGroupExternalId])
        ON [tb_data].[Tickets] AFTER INSERT,
    ADD BLOCK PREDICATE [tb_security].[FilterWhdTicketAccess]
        ([Source], [AssignedTechExternalId], [AssignedGroupExternalId])
        ON [tb_data].[Tickets] AFTER UPDATE
    WITH (STATE = ON, SCHEMABINDING = ON);';

BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS
    (
        SELECT 1
        FROM sys.security_policies AS policy
        INNER JOIN sys.schemas AS schema_row ON schema_row.[schema_id] = policy.[schema_id]
        WHERE schema_row.[name] = N'tb_security'
          AND policy.[name] = N'WhdTicketAccessPolicy'
    )
    BEGIN
        EXEC sys.sp_executesql
            N'ALTER SECURITY POLICY [tb_security].[WhdTicketAccessPolicy] WITH (STATE = OFF);';
        EXEC sys.sp_executesql
            N'DROP SECURITY POLICY [tb_security].[WhdTicketAccessPolicy];';
    END;

    EXEC sys.sp_executesql @RlsFunctionSql;
    EXEC sys.sp_executesql @RlsPolicySql;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO

PRINT N'TechBench V0007 server-owned Sage sync and read-only Admin preview procedures created.';
GO

-- ============================================================================
-- END 49-V0007-ServerOwnedSageAndAdminPreviewProcedures.sql
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
-- BEGIN 51-V0002-OperationalGrants.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

GRANT EXECUTE ON OBJECT::[tb_app].[GetRepositoryCapabilities]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[EnsureWorkspaceDefaults]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SearchTickets]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetTicket]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveTicket]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetTicketStatusOptions]
    TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[SearchWorkEntries]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetWorkEntry]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetDistinctTags]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveWorkEntry]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteWorkEntry]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetWorkEntryLinks]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveWorkEntryLink]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteWorkEntryLink]
    TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[GetEditorDraft]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveEditorDraft]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteEditorDraft]
    TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[GetTemplates]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveTemplate]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteTemplate]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveTemplate]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteTemplate]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetCommonLinks]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveCommonLink]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteCommonLink]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveCommonLink]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteCommonLink]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetSettings]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveUserSetting]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteUserSetting]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveSetting]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteSetting]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClientAliases]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveClientAlias]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteClientAlias]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveClientAlias]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteClientAlias]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClientExternalIdentities]
    TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[AddPostingLog]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetLatestVerifiedWhdPostingLog]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[BeginPostingAttempt]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[HeartbeatPostingAttempt]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetOutstandingPostingAttempt]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[CompletePostingAttempt]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[ResolveOutstandingPostingAttempts]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[MarkWorkEntryPosted]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AbandonOutstandingPostingAttempts]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[HasSuccessfulSageDraftLog]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetPostingLogs]
    TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[BeginImportBatch]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AddImportLegacyMapping]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[CompleteImportBatch]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetImportBatches]
    TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[SearchWorkEntries]
    TO [tb_role_manager];
GRANT EXECUTE ON OBJECT::[tb_app].[GetWorkEntry]
    TO [tb_role_manager];
GRANT EXECUTE ON OBJECT::[tb_app].[GetDistinctTags]
    TO [tb_role_manager];
GRANT EXECUTE ON OBJECT::[tb_app].[GetPostingLogs]
    TO [tb_role_manager];
GRANT EXECUTE ON OBJECT::[tb_app].[GetSyncRuns]
    TO [tb_role_manager];
GRANT EXECUTE ON OBJECT::[tb_app].[GetImportBatches]
    TO [tb_role_manager];

GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveOrganizationSetting]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteOrganizationSetting]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveExternalMapping]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminMergeClients]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[ReconcileClientMatches]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[GetSyncRuns]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[GetImportBatches]
    TO [tb_role_admin];

GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertClient]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertSageCustomer]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncRemoveStaleSageCustomers]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertClientExternalIdentity]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertTicketStatusOption]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertTicket]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[AcquireSyncLease]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[ReleaseSyncLease]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[BeginSyncRun]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[CompleteSyncRun]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[GetSyncRuns]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncApplyClientSnapshot]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncApplyTicketSnapshot]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncApplyTicketStatusSnapshot]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncApplySageCustomerSnapshot]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[ReconcileClientMatches]
    TO [tb_role_sync_operator];

PRINT N'TechBench V0002 stored-procedure-only grants applied.';
GO

-- ============================================================================
-- END 51-V0002-OperationalGrants.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 52-V0004-AdminSharedGrants.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Remove legacy shared-mutation authority from non-Admin roles. Admin users
    are also members of tb_role_user, so these revokes are paired with explicit
    tb_role_admin grants below.
*/
REVOKE EXECUTE ON OBJECT::[tb_app].[EnsureWorkspaceDefaults]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveTemplate]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteTemplate]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminSaveTemplate]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminDeleteTemplate]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveCommonLink]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteCommonLink]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminSaveCommonLink]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminDeleteCommonLink]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveClientAlias]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteClientAlias]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminSaveClientAlias]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminDeleteClientAlias]
    FROM [tb_role_user];

REVOKE EXECUTE ON OBJECT::[tb_app].[EnsureWorkspaceDefaults]
    FROM [tb_role_manager];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveTemplate]
    FROM [tb_role_manager];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteTemplate]
    FROM [tb_role_manager];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveCommonLink]
    FROM [tb_role_manager];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteCommonLink]
    FROM [tb_role_manager];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveClientAlias]
    FROM [tb_role_manager];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteClientAlias]
    FROM [tb_role_manager];

REVOKE EXECUTE ON OBJECT::[tb_app].[EnsureWorkspaceDefaults]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveTemplate]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteTemplate]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveCommonLink]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteCommonLink]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveClientAlias]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteClientAlias]
    FROM [tb_role_sync_operator];

REVOKE EXECUTE ON OBJECT::[tb_app].[AdminSaveOrganizationSetting]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminDeleteOrganizationSetting]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminSaveExternalMapping]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminMergeClients]
    FROM [tb_role_user];
REVOKE EXECUTE ON OBJECT::[tb_app].[ReconcileClientMatches]
    FROM [tb_role_user];

REVOKE EXECUTE ON OBJECT::[tb_app].[AdminSaveOrganizationSetting]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminDeleteOrganizationSetting]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminSaveExternalMapping]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminMergeClients]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[ReconcileClientMatches]
    FROM [tb_role_sync_operator];

REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertClient]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertSageCustomer]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncRemoveStaleSageCustomers]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertClientExternalIdentity]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertTicketStatusOption]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertTicket]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[AcquireSyncLease]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[ReleaseSyncLease]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[BeginSyncRun]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[CompleteSyncRun]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplyClientSnapshot]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplyTicketSnapshot]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplyTicketStatusSnapshot]
    FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplySageCustomerSnapshot]
    FROM [tb_role_sync_operator];

/* Read/use contracts remain available to every normal TechBench user. */
GRANT EXECUTE ON OBJECT::[tb_app].[GetRepositoryCapabilities]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetTemplates]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetCommonLinks]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClientAliases]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetDistinctTags]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetSettings]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveUserSetting]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteUserSetting]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveWorkEntry]
    TO [tb_role_user];

/* Organization configuration and shared reference catalogs are Admin-owned. */
GRANT EXECUTE ON OBJECT::[tb_app].[EnsureWorkspaceDefaults]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveTemplate]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteTemplate]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveTemplate]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteTemplate]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveCommonLink]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteCommonLink]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveCommonLink]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteCommonLink]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveClientAlias]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteClientAlias]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveClientAlias]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteClientAlias]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminGetOrganizationTags]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveOrganizationTag]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteOrganizationTag]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveOrganizationSetting]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminDeleteOrganizationSetting]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveExternalMapping]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminMergeClients]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[ReconcileClientMatches]
    TO [tb_role_admin];

/* Shared synchronization is now an Admin action, including Sage sync. */
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertClient]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertSageCustomer]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncRemoveStaleSageCustomers]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertClientExternalIdentity]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertTicketStatusOption]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncUpsertTicket]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AcquireSyncLease]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[ReleaseSyncLease]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[BeginSyncRun]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[CompleteSyncRun]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[GetSyncRuns]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncApplyClientSnapshot]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncApplyTicketSnapshot]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncApplyTicketStatusSnapshot]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[SyncApplySageCustomerSnapshot]
    TO [tb_role_admin];

/* A legacy sync operator may inspect history but cannot initiate or apply sync. */
GRANT EXECUTE ON OBJECT::[tb_app].[GetSyncRuns]
    TO [tb_role_sync_operator];

PRINT N'TechBench V0004 Admin-owned shared-configuration grants applied.';
GO

-- ============================================================================
-- END 52-V0004-AdminSharedGrants.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 53-V0005-TechBenchV1ImportGrants.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Every normal TechBench user may migrate their own V1 history. Each
    procedure derives ownership from ORIGINAL_LOGIN and exposes no owner
    override. No application role receives direct access to import tables.
*/
GRANT EXECUTE ON OBJECT::[tb_app].[BeginTechBenchV1Import]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[ResolveTechBenchV1Reference]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[ImportTechBenchV1WorkEntry]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[ImportTechBenchV1WorkEntryLink]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[ImportTechBenchV1PostingLog]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[CompleteTechBenchV1Import]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AbandonTechBenchV1Import]
    TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[GetRepositoryCapabilities]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetImportBatches]
    TO [tb_role_user];

PRINT N'TechBench V0005 owner-scoped TechBench V1 import grants applied.';
GO

-- ============================================================================
-- END 53-V0005-TechBenchV1ImportGrants.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 54-V0006-WhdServerSyncGrants.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/* Desktop Admins may orchestrate WHD work, not apply untrusted snapshots. */
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplyClientSnapshot] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplyTicketSnapshot] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplyTicketStatusSnapshot] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertTicket] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertTicketStatusOption] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertClient] FROM [tb_role_admin];

GRANT EXECUTE ON OBJECT::[tb_app].[AdminRequestWhdSync] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[GetWhdSyncStatus] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminGetWhdUserMappings] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveWhdUserMapping] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminGetWhdTechnicians] TO [tb_role_admin];

GRANT EXECUTE ON OBJECT::[tb_app].[SearchTickets] TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetTicket] TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_service].[GetWhdSyncConfiguration] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ClaimWhdSyncWork] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[RenewWhdSyncLease] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplyWhdClientSnapshot] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplyWhdTicketBatch] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplyWhdTicketStatusSnapshot] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplyWhdTechnicianSnapshot] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplyWhdTechGroupSnapshot] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[CompleteWhdSyncWork] TO [tb_role_sync_service];

PRINT N'TechBench V0006 WHD server-sync grants applied.';
GO

-- ============================================================================
-- END 54-V0006-WhdServerSyncGrants.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 55-V0007-ServerOwnedSageAndAdminPreviewGrants.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/* Sage ingestion is service-owned in V0007. Admins may enqueue and inspect
   work, but cannot run any legacy workstation-side Sage apply lifecycle. */
REVOKE EXECUTE ON OBJECT::[tb_app].[AcquireSyncLease] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[ReleaseSyncLease] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[BeginSyncRun] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[CompleteSyncRun] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertClient] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertSageCustomer] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncRemoveStaleSageCustomers] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertClientExternalIdentity] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplySageCustomerSnapshot] FROM [tb_role_admin];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplyClientSnapshot] FROM [tb_role_admin];

REVOKE EXECUTE ON OBJECT::[tb_app].[AcquireSyncLease] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[ReleaseSyncLease] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[BeginSyncRun] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[CompleteSyncRun] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertClient] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertSageCustomer] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncRemoveStaleSageCustomers] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncUpsertClientExternalIdentity] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplySageCustomerSnapshot] FROM [tb_role_sync_operator];
REVOKE EXECUTE ON OBJECT::[tb_app].[SyncApplyClientSnapshot] FROM [tb_role_sync_operator];

GRANT EXECUTE ON OBJECT::[tb_app].[AdminRequestSageSync] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[GetSageSyncStatus] TO [tb_role_admin];

GRANT EXECUTE ON OBJECT::[tb_service].[GetSageSyncConfiguration] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ClaimSageSyncWork] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[RenewSageSyncLease] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplySageCustomerSnapshot] TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[CompleteSageSyncWork] TO [tb_role_sync_service];

/* Admin preview is server-issued and activated per physical SQL connection.
   Only the Admin role may impersonate the WITHOUT LOGIN reader principal. */
GRANT EXECUTE ON OBJECT::[tb_app].[AdminListPreviewUsers] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminBeginUserPreview] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[ActivateReadOnlyPreview] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminEndUserPreview] TO [tb_role_admin];
GRANT IMPERSONATE ON USER::[tb_preview_reader] TO [tb_role_admin];

REVOKE IMPERSONATE ON USER::[tb_preview_reader] FROM [tb_role_user];
REVOKE IMPERSONATE ON USER::[tb_preview_reader] FROM [tb_role_manager];
REVOKE IMPERSONATE ON USER::[tb_preview_reader] FROM [tb_role_sync_operator];
REVOKE IMPERSONATE ON USER::[tb_preview_reader] FROM [tb_role_sync_service];

GRANT EXECUTE ON OBJECT::[tb_app].[GetCurrentUserContext] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetRepositoryCapabilities] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[SearchClients] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClient] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[SearchTickets] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetTicket] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetTicketStatusOptions] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[SearchWorkEntries] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetWorkEntry] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetDistinctTags] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetWorkEntryLinks] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetTemplates] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetCommonLinks] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetSettings] TO [tb_preview_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetPostingLogs] TO [tb_preview_reader];

/* Defense in depth: the reader has no private-data or mutation entry point. */
REVOKE EXECUTE ON OBJECT::[tb_app].[GetEditorDraft] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveEditorDraft] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteEditorDraft] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveWorkEntry] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteWorkEntry] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveWorkEntryLink] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteWorkEntryLink] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveTicket] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[SaveUserSetting] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[DeleteUserSetting] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminRequestSageSync] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminBeginUserPreview] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[ActivateReadOnlyPreview] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminEndUserPreview] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminListPreviewUsers] FROM [tb_preview_reader];

PRINT N'TechBench V0007 server-owned Sage sync and read-only Admin preview grants applied.';
GO

-- ============================================================================
-- END 55-V0007-ServerOwnedSageAndAdminPreviewGrants.sql
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

-- ============================================================================
-- BEGIN 91-V0002-OperationalVerify.sql
-- ============================================================================

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
    WHERE [MigrationId] = N'SqlServer2016.OperationalStorage.0002'
      AND [SchemaVersion] = 2
)
BEGIN
    PRINT N'FAIL: OperationalStorage.0002 migration marker is missing.';
    SET @FailureCount += 1;
END;

IF @InstalledSchemaVersion NOT IN (2, 3, 4, 5, 6, 7)
BEGIN
    PRINT N'FAIL: V0002 verification supports installed schema version 2, 3, 4, 5, 6, or 7.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredTables TABLE
(
    [ObjectName] nvarchar(300) NOT NULL PRIMARY KEY
);

INSERT INTO @RequiredTables([ObjectName])
VALUES
    (N'tb_data.TicketStatusOptions'),
    (N'tb_data.Tickets'),
    (N'tb_data.WorkEntries'),
    (N'tb_private.WorkEntryPersonalNotes'),
    (N'tb_data.WorkEntryLinks'),
    (N'tb_user.EditorDrafts'),
    (N'tb_data.Templates'),
    (N'tb_data.CommonLinks'),
    (N'tb_data.OrganizationSettings'),
    (N'tb_user.UserSettings'),
    (N'tb_data.ClientAliases'),
    (N'tb_data.ClientExternalIdentities'),
    (N'tb_ops.PostingLogs'),
    (N'tb_ops.PostingAttempts'),
    (N'tb_ops.PostingLeases'),
    (N'tb_ops.SyncLeases'),
    (N'tb_ops.SyncRuns'),
    (N'tb_ops.ImportBatches'),
    (N'tb_ops.LegacyIdMappings');

DECLARE @MissingTableCount int =
(
    SELECT COUNT(*)
    FROM @RequiredTables AS required_table
    WHERE OBJECT_ID(required_table.[ObjectName], N'U') IS NULL
);

IF @MissingTableCount > 0
BEGIN
    PRINT N'FAIL: One or more V0002 tables are missing.';
    SET @FailureCount += @MissingTableCount;
END;

IF OBJECT_ID(N'tb_user.DeviceSettings', N'U') IS NOT NULL
BEGIN
    PRINT N'FAIL: Device-specific settings must remain workstation-local, not in SQL Server.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'tb_data.CommonLinks')
      AND [name] = N'UrlHash'
      AND [is_computed] = 0
      AND [is_nullable] = 0
      AND TYPE_NAME([user_type_id]) = N'binary'
      AND [max_length] = 32
)
BEGIN
    PRINT N'FAIL: CommonLinks lacks its stored, bounded SHA-256 URL index key.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'[UrlHash]',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.EnsureWorkspaceDefaults'))) = 0
   OR CHARINDEX(
       N'[UrlHash]',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveCommonLink'))) = 0
BEGIN
    PRINT N'FAIL: Common-link writers do not maintain the stored URL hash.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredProcedures TABLE
(
    [ObjectName] nvarchar(300) NOT NULL PRIMARY KEY
);

INSERT INTO @RequiredProcedures([ObjectName])
VALUES
    (N'tb_security.RenewSyncRunLease'),
    (N'tb_app.GetRepositoryCapabilities'),
    (N'tb_app.EnsureWorkspaceDefaults'),
    (N'tb_app.SearchTickets'),
    (N'tb_app.GetTicket'),
    (N'tb_app.SaveTicket'),
    (N'tb_app.GetTicketStatusOptions'),
    (N'tb_app.SyncUpsertTicketStatusOption'),
    (N'tb_app.SyncUpsertTicket'),
    (N'tb_app.SyncUpsertClient'),
    (N'tb_app.SyncUpsertSageCustomer'),
    (N'tb_app.SyncRemoveStaleSageCustomers'),
    (N'tb_app.AdminSaveExternalMapping'),
    (N'tb_app.AdminMergeClients'),
    (N'tb_app.ReconcileClientMatches'),
    (N'tb_app.SearchWorkEntries'),
    (N'tb_app.GetWorkEntry'),
    (N'tb_app.GetDistinctTags'),
    (N'tb_app.SaveWorkEntry'),
    (N'tb_app.DeleteWorkEntry'),
    (N'tb_app.GetWorkEntryLinks'),
    (N'tb_app.SaveWorkEntryLink'),
    (N'tb_app.DeleteWorkEntryLink'),
    (N'tb_app.GetTemplates'),
    (N'tb_app.SaveTemplate'),
    (N'tb_app.DeleteTemplate'),
    (N'tb_app.AdminSaveTemplate'),
    (N'tb_app.AdminDeleteTemplate'),
    (N'tb_app.GetEditorDraft'),
    (N'tb_app.SaveEditorDraft'),
    (N'tb_app.DeleteEditorDraft'),
    (N'tb_app.GetClientAliases'),
    (N'tb_app.SaveClientAlias'),
    (N'tb_app.DeleteClientAlias'),
    (N'tb_app.AdminSaveClientAlias'),
    (N'tb_app.AdminDeleteClientAlias'),
    (N'tb_app.GetCommonLinks'),
    (N'tb_app.SaveCommonLink'),
    (N'tb_app.DeleteCommonLink'),
    (N'tb_app.AdminSaveCommonLink'),
    (N'tb_app.AdminDeleteCommonLink'),
    (N'tb_app.GetSettings'),
    (N'tb_app.SaveUserSetting'),
    (N'tb_app.DeleteUserSetting'),
    (N'tb_app.SaveSetting'),
    (N'tb_app.DeleteSetting'),
    (N'tb_app.AddPostingLog'),
    (N'tb_app.GetLatestVerifiedWhdPostingLog'),
    (N'tb_app.BeginPostingAttempt'),
    (N'tb_app.HeartbeatPostingAttempt'),
    (N'tb_app.GetOutstandingPostingAttempt'),
    (N'tb_app.CompletePostingAttempt'),
    (N'tb_app.ResolveOutstandingPostingAttempts'),
    (N'tb_app.MarkWorkEntryPosted'),
    (N'tb_app.AbandonOutstandingPostingAttempts'),
    (N'tb_app.HasSuccessfulSageDraftLog'),
    (N'tb_app.GetPostingLogs'),
    (N'tb_app.AcquireSyncLease'),
    (N'tb_app.ReleaseSyncLease'),
    (N'tb_app.BeginSyncRun'),
    (N'tb_app.CompleteSyncRun'),
    (N'tb_app.SyncApplyClientSnapshot'),
    (N'tb_app.SyncApplyTicketSnapshot'),
    (N'tb_app.SyncApplyTicketStatusSnapshot'),
    (N'tb_app.SyncApplySageCustomerSnapshot'),
    (N'tb_app.BeginImportBatch'),
    (N'tb_app.AddImportLegacyMapping'),
    (N'tb_app.CompleteImportBatch');

DECLARE @MissingProcedureCount int =
(
    SELECT COUNT(*)
    FROM @RequiredProcedures AS required_procedure
    WHERE OBJECT_ID(required_procedure.[ObjectName], N'P') IS NULL
);

IF @MissingProcedureCount > 0
BEGIN
    PRINT N'FAIL: One or more V0002 stored procedures are missing.';
    SET @FailureCount += @MissingProcedureCount;
END;

DECLARE @RequiredRowVersionTables TABLE
(
    [ObjectName] nvarchar(300) NOT NULL PRIMARY KEY
);

INSERT INTO @RequiredRowVersionTables([ObjectName])
VALUES
    (N'tb_data.TicketStatusOptions'),
    (N'tb_data.Tickets'),
    (N'tb_data.WorkEntries'),
    (N'tb_private.WorkEntryPersonalNotes'),
    (N'tb_data.WorkEntryLinks'),
    (N'tb_user.EditorDrafts'),
    (N'tb_data.Templates'),
    (N'tb_data.CommonLinks'),
    (N'tb_data.OrganizationSettings'),
    (N'tb_user.UserSettings'),
    (N'tb_data.ClientAliases'),
    (N'tb_data.ClientExternalIdentities'),
    (N'tb_ops.PostingAttempts'),
    (N'tb_ops.PostingLeases'),
    (N'tb_ops.SyncLeases'),
    (N'tb_ops.SyncRuns'),
    (N'tb_ops.ImportBatches');

DECLARE @MissingRowVersionCount int =
(
    SELECT COUNT(*)
    FROM @RequiredRowVersionTables AS required_table
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE [object_id] = OBJECT_ID(required_table.[ObjectName])
          AND [name] = N'RowVersion'
          AND [system_type_id] = 189
    )
);

IF @MissingRowVersionCount > 0
BEGIN
    PRINT N'FAIL: One or more mutable V0002 tables lack a rowversion column.';
    SET @FailureCount += @MissingRowVersionCount;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.parameters
    WHERE [object_id] = OBJECT_ID(N'tb_app.SaveWorkEntry')
      AND [name] IN
      (
          N'@WhdPosted',
          N'@WhdPostedAtUtc',
          N'@SagePosted',
          N'@SagePostedAtUtc',
          N'@SageTicketNumber'
      )
)
BEGIN
    PRINT N'FAIL: SaveWorkEntry accepts authoritative posting-state parameters.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.parameters
    WHERE [object_id] = OBJECT_ID(N'tb_app.SaveWorkEntry')
      AND [name] = N'@LastError'
)
BEGIN
    PRINT N'FAIL: SaveWorkEntry cannot persist client-reported posting errors.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'IF @ExistingSagePosted = 1',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveWorkEntry'))) = 0
BEGIN
    PRINT N'FAIL: SaveWorkEntry does not block updates after Sage posting.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'IF @WhdPosted = 1 OR @SagePosted = 1',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteWorkEntry'))) = 0
BEGIN
    PRINT N'FAIL: DeleteWorkEntry does not block deletion after WHD or Sage posting.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'FROM [tb_ops].[PostingAttempts] WITH (UPDLOCK, HOLDLOCK)',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveWorkEntry'))) = 0
   OR CHARINDEX(
       N'FROM [tb_ops].[PostingLeases] WITH (UPDLOCK, HOLDLOCK)',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveWorkEntry'))) = 0
   OR CHARINDEX(
       N'FROM [tb_ops].[PostingAttempts] WITH (UPDLOCK, HOLDLOCK)',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteWorkEntry'))) = 0
   OR CHARINDEX(
       N'FROM [tb_ops].[PostingLeases] WITH (UPDLOCK, HOLDLOCK)',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteWorkEntry'))) = 0
BEGIN
    PRINT N'FAIL: Work entries can be edited or deleted while external posting coordination is active.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'IF @AffectedCount > 0',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.ResolveOutstandingPostingAttempts'))) = 0
BEGIN
    PRINT N'FAIL: Posting reconciliation can update authoritative state without an outstanding attempt.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.parameters
    WHERE [object_id] = OBJECT_ID(N'tb_app.CompletePostingAttempt')
      AND [name] = N'@MarkPosted'
)
BEGIN
    PRINT N'FAIL: CompletePostingAttempt cannot distinguish successful drafts from completed posts.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'IF @Status = N''Succeeded'' AND @MarkPosted = 1',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'AND @MarkPosted = 0',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'WHEN [WhdPosted] = 1 THEN N''PostedToWhd''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'ELSE IF @Status <> N''Succeeded''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
BEGIN
    PRINT N'FAIL: CompletePostingAttempt does not finalize successful unposted Sage draft state safely.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'FROM [tb_ops].[PostingLogs] WITH (UPDLOCK, HOLDLOCK)',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'[Message] = COALESCE(@Message, N'''')',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'[ExternalReference] IS NULL AND @ExternalReference IS NULL',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
BEGIN
    PRINT N'FAIL: CompletePostingAttempt always duplicates a detailed client posting log.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'@NormalizedSageTicketNumber',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'UPPER(LEFT(@ExternalReference, 5)) = N''SAGE-''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'@NormalizedSageTicketNumber',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.ResolveOutstandingPostingAttempts'))) = 0
   OR CHARINDEX(
       N'UPPER(LEFT(@ExternalReference, 5)) = N''SAGE-''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.ResolveOutstandingPostingAttempts'))) = 0
BEGIN
    PRINT N'FAIL: Posting completion or reconciliation does not normalize Sage ticket references.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'FROM [tb_data].[WorkEntries] WITH (UPDLOCK, HOLDLOCK)',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.BeginPostingAttempt'))) = 0
   OR CHARINDEX(
       N'IF @SagePosted = 1',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.BeginPostingAttempt'))) = 0
   OR CHARINDEX(
       N'FROM [tb_data].[WorkEntries] WITH (UPDLOCK, HOLDLOCK)',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'IF @ExistingSagePosted = 1',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'FROM [tb_data].[WorkEntries] WITH (UPDLOCK, HOLDLOCK)',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.ResolveOutstandingPostingAttempts'))) = 0
   OR CHARINDEX(
       N'IF @SagePosted = 1',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.ResolveOutstandingPostingAttempts'))) = 0
BEGIN
    PRINT N'FAIL: A posting workflow can mutate an entry after Sage posting.';
    SET @FailureCount += 1;
END;

DECLARE @ManualPostedParameters TABLE
(
    [ParameterName] sysname NOT NULL PRIMARY KEY
);

INSERT INTO @ManualPostedParameters([ParameterName])
VALUES
    (N'@WorkEntryId'),
    (N'@Destination'),
    (N'@ExpectedRowVersion'),
    (N'@RequestId');

IF EXISTS
(
    SELECT 1
    FROM @ManualPostedParameters AS expected_parameter
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.parameters
        WHERE [object_id] = OBJECT_ID(N'tb_app.MarkWorkEntryPosted')
          AND [name] = expected_parameter.[ParameterName]
    )
)
BEGIN
    PRINT N'FAIL: MarkWorkEntryPosted is missing a required ownership/concurrency contract parameter.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'IF @SagePosted = 1',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.MarkWorkEntryPosted'))) = 0
   OR CHARINDEX(
       N'INSERT INTO [tb_ops].[PostingLogs]',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.MarkWorkEntryPosted'))) = 0
   OR CHARINDEX(
       N'[tb_security].[WriteAuditEvent]',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.MarkWorkEntryPosted'))) = 0
BEGIN
    PRINT N'FAIL: MarkWorkEntryPosted lacks immutable, posting-log, or audit enforcement.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'WHEN @LastError IS NOT NULL THEN N''Failed''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveWorkEntry'))) = 0
   OR CHARINDEX(
       N'WHEN [WhdPosted] = 1 AND [SagePosted] = 1 THEN N''PostedToBoth''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveWorkEntry'))) = 0
BEGIN
    PRINT N'FAIL: SaveWorkEntry does not safely derive PostingStatus from errors and posted flags.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'AND posting_lease.[ExpiresAtUtc] > @NowUtc',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.HeartbeatPostingAttempt'))) = 0
BEGIN
    PRINT N'FAIL: Posting heartbeats can revive an expired lease.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'ISJSON(posting_log.[Payload]) = 1',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetLatestVerifiedWhdPostingLog'))) = 0
   OR CHARINDEX(
       N'OPENJSON',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetLatestVerifiedWhdPostingLog'))) = 0
   OR CHARINDEX(
       N'payload_property.[key] = N''noteText''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetLatestVerifiedWhdPostingLog'))) = 0
BEGIN
    PRINT N'FAIL: Latest verified WHD posting lookup can prefer a completion marker over the JSON note snapshot.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'[SageCustomerId] =',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminMergeClients'))) = 0
   OR CHARINDEX(
       N'[MatchStatus] = N''Matched''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminMergeClients'))) = 0
BEGIN
    PRINT N'FAIL: Client merge does not preserve shared external metadata.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'WHEN [Source] = N''Both''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SyncUpsertClient'))) = 0
BEGIN
    PRINT N'FAIL: Client synchronization does not preserve a merged client match.';
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
    WHERE CHARINDEX(
              expected_default.[Token],
              OBJECT_DEFINITION(OBJECT_ID(N'tb_app.EnsureWorkspaceDefaults'))) = 0
);

IF @MissingWorkspaceDefaultTokenCount > 0
BEGIN
    PRINT N'FAIL: EnsureWorkspaceDefaults does not contain every V1 workspace default.';
    SET @FailureCount += @MissingWorkspaceDefaultTokenCount;
END;

IF @InstalledSchemaVersion < 4
   AND
   (
       CHARINDEX(
           N'AND [BuiltInKey] IS NOT NULL',
           OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveCommonLink'))) = 0
       OR CHARINDEX(
           N'AND [BuiltInKey] IS NOT NULL',
           OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteCommonLink'))) = 0
   )
BEGIN
    PRINT N'FAIL: Built-in Common Links are not protected from edit/delete.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'ELSE [LastSyncedAtUtc]',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveTicket'))) = 0
BEGIN
    PRINT N'FAIL: Technician ticket saves can overwrite synchronization timestamps.';
    SET @FailureCount += 1;
END;

DECLARE @SnapshotLeaseContracts TABLE
(
    [ProcedureName] nvarchar(300) NOT NULL PRIMARY KEY,
    [ExpectedSource] nvarchar(80) NOT NULL
);

INSERT INTO @SnapshotLeaseContracts([ProcedureName], [ExpectedSource])
VALUES
    (N'tb_app.SyncApplyClientSnapshot', N'WHD-Clients'),
    (N'tb_app.SyncApplyTicketStatusSnapshot', N'WHD-TicketStatuses'),
    (N'tb_app.SyncApplyTicketSnapshot', N'WHD-Tickets'),
    (N'tb_app.SyncApplySageCustomerSnapshot', N'Sage-Customers');

DECLARE @MissingSnapshotLeaseContractCount int =
(
    SELECT COUNT(*)
    FROM @SnapshotLeaseContracts AS contract
    WHERE CHARINDEX(
              N'[tb_ops].[SyncLeases]',
              OBJECT_DEFINITION(OBJECT_ID(contract.[ProcedureName]))) = 0
       OR CHARINDEX(
              N'[tb_security].[RenewSyncRunLease]',
              OBJECT_DEFINITION(OBJECT_ID(contract.[ProcedureName]))) = 0
       OR CHARINDEX(
              contract.[ExpectedSource],
              OBJECT_DEFINITION(OBJECT_ID(contract.[ProcedureName]))) = 0
);

IF @MissingSnapshotLeaseContractCount > 0
BEGIN
    PRINT N'FAIL: A snapshot procedure does not enforce its active source-specific lease.';
    SET @FailureCount += @MissingSnapshotLeaseContractCount;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.key_constraints
    WHERE [parent_object_id] = OBJECT_ID(N'tb_ops.PostingLeases')
      AND [type] = N'PK'
)
BEGIN
    PRINT N'FAIL: PostingLeases lacks its one-lease-per-work-entry/destination primary key.';
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
    (N'tb_role_user', N'tb_app.SearchTickets'),
    (N'tb_role_user', N'tb_app.SaveTicket'),
    (N'tb_role_user', N'tb_app.SearchWorkEntries'),
    (N'tb_role_user', N'tb_app.SaveWorkEntry'),
    (N'tb_role_user', N'tb_app.GetEditorDraft'),
    (N'tb_role_user', N'tb_app.SaveUserSetting'),
    (N'tb_role_user', N'tb_app.GetPostingLogs'),
    (N'tb_role_user', N'tb_app.BeginPostingAttempt'),
    (N'tb_role_user', N'tb_app.MarkWorkEntryPosted'),
    (N'tb_role_user', N'tb_app.BeginImportBatch'),
    (N'tb_role_manager', N'tb_app.SearchWorkEntries'),
    (N'tb_role_admin', N'tb_app.AdminMergeClients'),
    (N'tb_role_admin', N'tb_app.AdminSaveOrganizationSetting');

IF @InstalledSchemaVersion < 4
BEGIN
    INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
    VALUES
        (N'tb_role_user', N'tb_app.EnsureWorkspaceDefaults'),
        (N'tb_role_user', N'tb_app.SaveTemplate'),
        (N'tb_role_user', N'tb_app.SaveCommonLink'),
        (N'tb_role_user', N'tb_app.SaveClientAlias'),
        (N'tb_role_sync_operator', N'tb_app.AcquireSyncLease'),
        (N'tb_role_sync_operator', N'tb_app.SyncApplyClientSnapshot'),
        (N'tb_role_sync_operator', N'tb_app.SyncApplyTicketSnapshot'),
        (N'tb_role_sync_operator', N'tb_app.SyncApplyTicketStatusSnapshot'),
        (N'tb_role_sync_operator', N'tb_app.SyncApplySageCustomerSnapshot');
END
ELSE
BEGIN
    INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
    VALUES
        (N'tb_role_admin', N'tb_app.EnsureWorkspaceDefaults'),
        (N'tb_role_admin', N'tb_app.SaveTemplate'),
        (N'tb_role_admin', N'tb_app.SaveCommonLink'),
        (N'tb_role_admin', N'tb_app.SaveClientAlias');

    /* V0007 moves organization-wide Sage snapshot application to the service. */
    IF @InstalledSchemaVersion < 7
    BEGIN
        INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
        VALUES
            (N'tb_role_admin', N'tb_app.AcquireSyncLease'),
            (N'tb_role_admin', N'tb_app.SyncApplySageCustomerSnapshot');
    END;

    /* V0006 moves organization-wide WHD snapshot application to the service. */
    IF @InstalledSchemaVersion < 6
    BEGIN
        INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
        VALUES
            (N'tb_role_admin', N'tb_app.SyncApplyClientSnapshot'),
            (N'tb_role_admin', N'tb_app.SyncApplyTicketSnapshot'),
            (N'tb_role_admin', N'tb_app.SyncApplyTicketStatusSnapshot');
    END;
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
    PRINT N'FAIL: One or more required V0002 role grants are missing.';
    SET @FailureCount += @MissingGrantCount;
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
        (N'tb_data', N'tb_private', N'tb_user', N'tb_ops', N'tb_security', N'tb_audit')
      AND permission.[permission_name] IN
        (N'SELECT', N'INSERT', N'UPDATE', N'DELETE', N'CONTROL', N'ALTER')
      AND permission.[state] IN (N'G', N'W')
)
BEGIN
    PRINT N'FAIL: A TechBench application role has direct table or schema data permission.';
    SET @FailureCount += 1;
END;

IF @FailureCount > 0
BEGIN
    RAISERROR(
        N'TechBench V0002 verification failed with %d issue(s).',
        16,
        1,
        @FailureCount);
    RETURN;
END;

PRINT N'TechBench V0002 operational-storage verification passed.';

SELECT
    DB_NAME() AS [DatabaseName],
    MAX([SchemaVersion]) AS [SchemaVersion],
    MAX(CASE WHEN [MigrationId] = N'SqlServer2016.OperationalStorage.0002'
        THEN [AppliedAtUtc] END) AS [OperationalStorageAppliedAtUtc]
FROM [tb_deploy].[SchemaMigrations];
GO

-- ============================================================================
-- END 91-V0002-OperationalVerify.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 92-V0003-SharedReferenceVerify.sql
-- ============================================================================

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
    WHERE [MigrationId] = N'SqlServer2016.SharedReferenceData.0003'
      AND [SchemaVersion] = 3
      AND [ReleaseVersion] = N'2.0.0-alpha.3'
)
BEGIN
    PRINT N'FAIL: SharedReferenceData.0003 migration marker is missing or invalid.';
    SET @FailureCount += 1;
END;

IF @InstalledSchemaVersion NOT IN (3, 4, 5, 6, 7)
BEGIN
    PRINT N'FAIL: V0003 verification supports installed schema version 3, 4, 5, 6, or 7.';
    SET @FailureCount += 1;
END;

IF OBJECT_ID(N'tb_data.OrganizationTags', N'U') IS NULL
BEGIN
    PRINT N'FAIL: tb_data.OrganizationTags is missing.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'tb_data.OrganizationTags')
      AND [name] = N'Tag'
      AND TYPE_NAME([user_type_id]) = N'nvarchar'
      AND [max_length] = 2000
      AND [is_nullable] = 0
)
BEGIN
    PRINT N'FAIL: OrganizationTags.Tag is not bounded to nvarchar(1000) NOT NULL.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'tb_data.OrganizationTags')
      AND [name] = N'RowVersion'
      AND [system_type_id] = 189
      AND [is_nullable] = 0
)
BEGIN
    PRINT N'FAIL: OrganizationTags lacks its required rowversion concurrency token.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'tb_data.OrganizationTags')
      AND [name] = N'TagHash'
      AND TYPE_NAME([user_type_id]) = N'binary'
      AND [max_length] = 32
      AND [is_nullable] = 0
)
BEGIN
    PRINT N'FAIL: OrganizationTags.TagHash is not binary(32) NOT NULL.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'tb_data.OrganizationTags')
      AND [name] = N'UX_OrganizationTags_TagHash'
      AND [is_unique] = 1
)
BEGIN
    PRINT N'FAIL: The canonical organization-tag hash is not uniquely indexed.';
    SET @FailureCount += 1;
END;

IF @InstalledSchemaVersion = 3
   AND OBJECT_ID(N'tb_data.OrganizationTags', N'U') IS NOT NULL
   AND EXISTS
   (
       SELECT 1
       FROM [tb_data].[OrganizationTags]
       WHERE NULLIF(LTRIM(RTRIM([Tag])), N'') IS NULL
          OR [Tag] <> LTRIM(RTRIM([Tag]))
          OR DATALENGTH([Tag]) > 2000
   )
BEGIN
    PRINT N'FAIL: OrganizationTags contains a blank, untrimmed, or over-length tag.';
    SET @FailureCount += 1;
END;

IF @InstalledSchemaVersion = 3
   AND OBJECT_ID(N'tb_data.OrganizationTags', N'U') IS NOT NULL
   AND EXISTS
   (
       SELECT 1
       FROM [tb_data].[WorkEntries] AS work_entry
       CROSS APPLY STRING_SPLIT(COALESCE(work_entry.[Tags], N''), N',') AS tag
       WHERE NULLIF(LTRIM(RTRIM(tag.[value])), N'') IS NOT NULL
         AND NOT EXISTS
         (
             SELECT 1
             FROM [tb_data].[OrganizationTags] AS organization_tag
             WHERE organization_tag.[TagHash] =
                 CONVERT
                 (
                     binary(32),
                     HASHBYTES
                     (
                         N'SHA2_256',
                         CONVERT
                         (
                             varbinary(2000),
                             UPPER(LTRIM(RTRIM(tag.[value])))
                         )
                     )
                 )
         )
   )
BEGIN
    PRINT N'FAIL: One or more existing work-entry tags were not backfilled.';
    SET @FailureCount += 1;
END;

DECLARE @GetDistinctTagsDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetDistinctTags'));
DECLARE @SaveWorkEntryDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveWorkEntry'));

IF @GetDistinctTagsDefinition IS NULL
   OR CHARINDEX(N'[tb_data].[OrganizationTags]', @GetDistinctTagsDefinition) = 0
   OR CHARINDEX(N'[tb_data].[WorkEntries]', @GetDistinctTagsDefinition) > 0
BEGIN
    PRINT N'FAIL: GetDistinctTags is not isolated to the canonical organization catalog.';
    SET @FailureCount += 1;
END;

IF @SaveWorkEntryDefinition IS NULL
   OR
   (
       @InstalledSchemaVersion = 3
       AND
       (
           CHARINDEX(N'[tb_data].[OrganizationTags]', @SaveWorkEntryDefinition) = 0
           OR CHARINDEX(N'UPDLOCK', @SaveWorkEntryDefinition) = 0
           OR CHARINDEX(N'HOLDLOCK', @SaveWorkEntryDefinition) = 0
           OR CHARINDEX(N'MERGE', @SaveWorkEntryDefinition) > 0
       )
   )
   OR
   (
       @InstalledSchemaVersion >= 4
       AND CHARINDEX(N'[tb_data].[OrganizationTags]', @SaveWorkEntryDefinition) > 0
   )
BEGIN
    PRINT N'FAIL: SaveWorkEntry does not implement the installed schema version tag-catalog boundary.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
    FROM [tb_data].[CommonLinks]
    WHERE [ScopeType] <> N'Organization'
       OR [OwnerWindowsSid] IS NOT NULL
)
BEGIN
    PRINT N'FAIL: A Common Link remains outside organization scope.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
    FROM [tb_data].[ClientAliases]
    WHERE [ScopeType] <> N'Organization'
       OR [OwnerWindowsSid] IS NOT NULL
)
BEGIN
    PRINT N'FAIL: A client alias remains outside organization scope.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.default_constraints AS default_constraint
    INNER JOIN sys.columns AS column_definition
        ON column_definition.[object_id] = default_constraint.[parent_object_id]
       AND column_definition.[column_id] = default_constraint.[parent_column_id]
    WHERE default_constraint.[parent_object_id] = OBJECT_ID(N'tb_data.CommonLinks')
      AND column_definition.[name] = N'ScopeType'
      AND CHARINDEX(N'Organization', default_constraint.[definition]) > 0
)
BEGIN
    PRINT N'FAIL: CommonLinks.ScopeType does not default to Organization.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.default_constraints AS default_constraint
    INNER JOIN sys.columns AS column_definition
        ON column_definition.[object_id] = default_constraint.[parent_object_id]
       AND column_definition.[column_id] = default_constraint.[parent_column_id]
    WHERE default_constraint.[parent_object_id] = OBJECT_ID(N'tb_data.ClientAliases')
      AND column_definition.[name] = N'ScopeType'
      AND CHARINDEX(N'Organization', default_constraint.[definition]) > 0
)
BEGIN
    PRINT N'FAIL: ClientAliases.ScopeType does not default to Organization.';
    SET @FailureCount += 1;
END;

DECLARE @SaveCommonLinkDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveCommonLink'));
DECLARE @DeleteCommonLinkDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteCommonLink'));
DECLARE @GetTemplatesDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetTemplates'));
DECLARE @SaveTemplateDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveTemplate'));
DECLARE @DeleteTemplateDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteTemplate'));
DECLARE @SaveClientAliasDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveClientAlias'));
DECLARE @DeleteClientAliasDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteClientAlias'));

IF @GetTemplatesDefinition IS NULL
   OR CHARINDEX(N'[ScopeType] = N''Organization''', @GetTemplatesDefinition) = 0
   OR CHARINDEX(N'[ScopeType] = N''User''', @GetTemplatesDefinition) = 0
   OR @SaveTemplateDefinition IS NULL
   OR CHARINDEX(N'@ScopeType nvarchar(20) = N''Organization''', @SaveTemplateDefinition) = 0
   OR CHARINDEX(N'@ScopeType <> N''Organization''', @SaveTemplateDefinition) = 0
   OR CHARINDEX(N'@IsAdmin <> 1', @SaveTemplateDefinition) = 0
   OR CHARINDEX(N'[ScopeType] = N''Organization''', @SaveTemplateDefinition) = 0
   OR @DeleteTemplateDefinition IS NULL
   OR CHARINDEX(N'@IsAdmin <> 1', @DeleteTemplateDefinition) = 0
   OR CHARINDEX(N'[ScopeType] = N''Organization''', @DeleteTemplateDefinition) = 0
BEGIN
    PRINT N'FAIL: Templates are not organization/Admin-managed with legacy personal rows read-only.';
    SET @FailureCount += 1;
END;

IF @SaveCommonLinkDefinition IS NULL
   OR CHARINDEX(N'@ScopeType nvarchar(20) = N''Organization''', @SaveCommonLinkDefinition) = 0
   OR CHARINDEX(N'@IsAdmin <> 1', @SaveCommonLinkDefinition) = 0
   OR @DeleteCommonLinkDefinition IS NULL
   OR CHARINDEX(N'@IsAdmin <> 1', @DeleteCommonLinkDefinition) = 0
BEGIN
    PRINT N'FAIL: Common-link writes are not organization-only and Admin-managed.';
    SET @FailureCount += 1;
END;

IF @SaveClientAliasDefinition IS NULL
   OR CHARINDEX(N'@ScopeType nvarchar(20) = N''Organization''', @SaveClientAliasDefinition) = 0
   OR CHARINDEX(N'UPDLOCK', @SaveClientAliasDefinition) = 0
   OR CHARINDEX(N'HOLDLOCK', @SaveClientAliasDefinition) = 0
   OR CHARINDEX(N'@IsAdmin <> 1', @SaveClientAliasDefinition) = 0
   OR @DeleteClientAliasDefinition IS NULL
   OR CHARINDEX(N'@IsAdmin <> 1', @DeleteClientAliasDefinition) = 0
BEGIN
    PRINT N'FAIL: Client aliases do not implement shared idempotent insert and Admin-only mutation.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'@ScopeType = N''Organization''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminSaveTemplate'))) = 0
   OR CHARINDEX(
       N'@ScopeType = N''Organization''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminSaveCommonLink'))) = 0
   OR CHARINDEX(
       N'@ScopeType = N''Organization''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminSaveClientAlias'))) = 0
BEGIN
    PRINT N'FAIL: An administrative shared-data wrapper does not use Organization scope.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
    FROM [tb_user].[UserSettings]
    WHERE [SettingKey] IN
    (
        N'Whd.BaseUrl',
        N'Whd.AuthenticationMode',
        N'Sage.ActivityItemId'
    )
)
BEGIN
    PRINT N'FAIL: A shared connection/configuration key remains user-scoped.';
    SET @FailureCount += 1;
END;

IF @InstalledSchemaVersion = 3
   AND
   (
       CHARINDEX(
           N'Whd.BaseUrl',
           OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveUserSetting'))) = 0
       OR CHARINDEX(
           N'Whd.AuthenticationMode',
           OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveUserSetting'))) = 0
       OR CHARINDEX(
           N'Sage.ActivityItemId',
           OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveUserSetting'))) = 0
   )
BEGIN
    PRINT N'FAIL: SaveUserSetting does not protect organization-scoped keys.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
    FROM [tb_data].[Templates] AS personal_template
    WHERE personal_template.[ScopeType] = N'User'
      AND EXISTS
      (
          SELECT 1
          FROM [tb_data].[Templates] AS organization_template
          WHERE organization_template.[ScopeType] = N'Organization'
            AND organization_template.[Name] = personal_template.[Name]
            AND organization_template.[Category] = personal_template.[Category]
            AND organization_template.[TemplateText] = personal_template.[TemplateText]
      )
)
BEGIN
    PRINT N'FAIL: An exact personal/organization template duplicate was not deduplicated.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'CONVERT(int, ' + CONVERT(nvarchar(10), @InstalledSchemaVersion) + N')',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities'))) = 0
BEGIN
    PRINT N'FAIL: GetRepositoryCapabilities does not report the installed schema version.';
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
    (N'tb_role_user', N'tb_app.GetDistinctTags'),
    (N'tb_role_user', N'tb_app.SaveWorkEntry'),
    (N'tb_role_user', N'tb_app.GetCommonLinks'),
    (N'tb_role_user', N'tb_app.GetClientAliases'),
    (N'tb_role_user', N'tb_app.SaveUserSetting'),
    (N'tb_role_user', N'tb_app.DeleteUserSetting'),
    (N'tb_role_admin', N'tb_app.AdminSaveOrganizationSetting'),
    (N'tb_role_admin', N'tb_app.AdminDeleteOrganizationSetting');

IF @InstalledSchemaVersion = 3
BEGIN
    INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
    VALUES
        (N'tb_role_user', N'tb_app.SaveCommonLink'),
        (N'tb_role_user', N'tb_app.DeleteCommonLink'),
        (N'tb_role_user', N'tb_app.SaveClientAlias'),
        (N'tb_role_user', N'tb_app.DeleteClientAlias');
END
ELSE
BEGIN
    INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
    VALUES
        (N'tb_role_admin', N'tb_app.SaveCommonLink'),
        (N'tb_role_admin', N'tb_app.DeleteCommonLink'),
        (N'tb_role_admin', N'tb_app.SaveClientAlias'),
        (N'tb_role_admin', N'tb_app.DeleteClientAlias');
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
    PRINT N'FAIL: One or more V0003 procedure grants are missing.';
    SET @FailureCount += @MissingGrantCount;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission
    INNER JOIN sys.database_principals AS grantee
        ON grantee.[principal_id] = permission.[grantee_principal_id]
    WHERE permission.[class] = 1
      AND permission.[major_id] = OBJECT_ID(N'tb_data.OrganizationTags')
      AND grantee.[name] IN
      (
          N'tb_role_user',
          N'tb_role_manager',
          N'tb_role_admin',
          N'tb_role_sync_operator'
      )
      AND permission.[permission_name] IN
      (
          N'SELECT', N'INSERT', N'UPDATE', N'DELETE', N'CONTROL', N'ALTER'
      )
      AND permission.[state] IN (N'G', N'W')
)
BEGIN
    PRINT N'FAIL: An application role has direct access to OrganizationTags.';
    SET @FailureCount += 1;
END;

IF @FailureCount > 0
BEGIN
    RAISERROR(
        N'TechBench V0003 shared-reference verification failed with %d issue(s).',
        16,
        1,
        @FailureCount);
    RETURN;
END;

PRINT N'TechBench V0003 shared-reference verification passed.';

SELECT
    DB_NAME() AS [DatabaseName],
    MAX([SchemaVersion]) AS [SchemaVersion],
    MAX
    (
        CASE
            WHEN [MigrationId] = N'SqlServer2016.SharedReferenceData.0003'
                THEN [AppliedAtUtc]
        END
    ) AS [SharedReferenceDataAppliedAtUtc]
FROM [tb_deploy].[SchemaMigrations];
GO

-- ============================================================================
-- END 92-V0003-SharedReferenceVerify.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 93-V0004-AdminSharedVerify.sql
-- ============================================================================

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

IF @InstalledSchemaVersion NOT IN (4, 5, 6, 7)
BEGIN
    PRINT N'FAIL: V0004 verification supports installed schema version 4, 5, 6, or 7.';
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
       N'CONVERT(int, ' + CONVERT(nvarchar(10), @InstalledSchemaVersion) + N')',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities'))) = 0
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

-- ============================================================================
-- END 93-V0004-AdminSharedVerify.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 94-V0005-TechBenchV1ImportVerify.sql
-- ============================================================================

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
    WHERE [MigrationId] = N'SqlServer2016.TechBenchV1Import.0005'
      AND [SchemaVersion] = 5
      AND [ReleaseVersion] = N'2.0.0-alpha.5'
)
BEGIN
    PRINT N'FAIL: TechBenchV1Import.0005 migration marker is missing or invalid.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'tb_ops.ImportBatches')
      AND [name] = N'UX_ImportBatches_ActiveTechBenchV1'
      AND [is_unique] = 1
      AND [has_filter] = 1
)
BEGIN
    PRINT N'FAIL: ImportBatches does not prevent concurrent active V1 imports for one owner.';
    SET @FailureCount += 1;
END;

IF @InstalledSchemaVersion NOT IN (5, 6, 7)
BEGIN
    PRINT N'FAIL: V0005 verification supports installed schema version 5, 6, or 7.';
    SET @FailureCount += 1;
END;

IF OBJECT_ID(N'tb_ops.LegacyEntityMappings', N'U') IS NULL
BEGIN
    PRINT N'FAIL: tb_ops.LegacyEntityMappings is missing.';
    SET @FailureCount += 1;
END;

IF COL_LENGTH(N'tb_ops.ImportBatches', N'ConflictCount') IS NULL
BEGIN
    PRINT N'FAIL: ImportBatches.ConflictCount is missing.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredMappingColumns TABLE
(
    [ColumnName] sysname NOT NULL PRIMARY KEY
);

INSERT INTO @RequiredMappingColumns([ColumnName])
VALUES
    (N'OwnerWindowsSid'),
    (N'SourceSystem'),
    (N'EntityType'),
    (N'LegacyId'),
    (N'NewEntityId'),
    (N'ContentHash'),
    (N'FirstImportBatchId'),
    (N'LastSeenImportBatchId'),
    (N'CreatedAtUtc'),
    (N'LastSeenAtUtc'),
    (N'RowVersion');

DECLARE @MissingMappingColumnCount int =
(
    SELECT COUNT(*)
    FROM @RequiredMappingColumns AS required_column
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.columns AS actual_column
        WHERE actual_column.[object_id] =
            OBJECT_ID(N'tb_ops.LegacyEntityMappings')
          AND actual_column.[name] = required_column.[ColumnName]
    )
);

IF @MissingMappingColumnCount > 0
BEGIN
    PRINT N'FAIL: LegacyEntityMappings is missing one or more required columns.';
    SET @FailureCount += @MissingMappingColumnCount;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'tb_ops.LegacyEntityMappings')
      AND [name] = N'PK_LegacyEntityMappings'
      AND [is_primary_key] = 1
      AND [is_unique] = 1
)
BEGIN
    PRINT N'FAIL: LegacyEntityMappings lacks its owner/source/entity/legacy primary key.';
    SET @FailureCount += 1;
END;

DECLARE @ExpectedPrimaryKeyColumns TABLE
(
    [KeyOrdinal] int NOT NULL PRIMARY KEY,
    [ColumnName] sysname NOT NULL
);

INSERT INTO @ExpectedPrimaryKeyColumns([KeyOrdinal], [ColumnName])
VALUES
    (1, N'OwnerWindowsSid'),
    (2, N'SourceSystem'),
    (3, N'EntityType'),
    (4, N'LegacyId');

DECLARE @WrongPrimaryKeyColumnCount int =
(
    SELECT COUNT(*)
    FROM @ExpectedPrimaryKeyColumns AS expected_column
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes AS primary_index
        INNER JOIN sys.index_columns AS index_column
            ON index_column.[object_id] = primary_index.[object_id]
           AND index_column.[index_id] = primary_index.[index_id]
        INNER JOIN sys.columns AS actual_column
            ON actual_column.[object_id] = index_column.[object_id]
           AND actual_column.[column_id] = index_column.[column_id]
        WHERE primary_index.[object_id] =
            OBJECT_ID(N'tb_ops.LegacyEntityMappings')
          AND primary_index.[name] = N'PK_LegacyEntityMappings'
          AND index_column.[key_ordinal] = expected_column.[KeyOrdinal]
          AND actual_column.[name] = expected_column.[ColumnName]
    )
);

IF @WrongPrimaryKeyColumnCount > 0
BEGIN
    PRINT N'FAIL: LegacyEntityMappings primary-key order does not enforce owner-scoped idempotency.';
    SET @FailureCount += @WrongPrimaryKeyColumnCount;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'tb_ops.LegacyEntityMappings')
      AND [name] = N'IX_LegacyEntityMappings_NewEntity'
      AND [is_unique] = 0
      AND [has_filter] = 0
)
BEGIN
    PRINT N'FAIL: LegacyEntityMappings lacks its nonunique reverse-entity lookup index.';
    SET @FailureCount += 1;
END;

DECLARE @ExpectedReverseLookupColumns TABLE
(
    [KeyOrdinal] int NOT NULL PRIMARY KEY,
    [ColumnName] sysname NOT NULL
);

INSERT INTO @ExpectedReverseLookupColumns([KeyOrdinal], [ColumnName])
VALUES
    (1, N'OwnerWindowsSid'),
    (2, N'SourceSystem'),
    (3, N'EntityType'),
    (4, N'NewEntityId');

IF
(
    SELECT COUNT(*)
    FROM @ExpectedReverseLookupColumns AS expected_column
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes AS reverse_index
        INNER JOIN sys.index_columns AS index_column
            ON index_column.[object_id] = reverse_index.[object_id]
           AND index_column.[index_id] = reverse_index.[index_id]
        INNER JOIN sys.columns AS actual_column
            ON actual_column.[object_id] = index_column.[object_id]
           AND actual_column.[column_id] = index_column.[column_id]
        WHERE reverse_index.[object_id] =
            OBJECT_ID(N'tb_ops.LegacyEntityMappings')
          AND reverse_index.[name] = N'IX_LegacyEntityMappings_NewEntity'
          AND index_column.[key_ordinal] = expected_column.[KeyOrdinal]
          AND actual_column.[name] = expected_column.[ColumnName]
    )
) > 0
OR
(
    SELECT COUNT(*)
    FROM sys.indexes AS reverse_index
    INNER JOIN sys.index_columns AS index_column
        ON index_column.[object_id] = reverse_index.[object_id]
       AND index_column.[index_id] = reverse_index.[index_id]
    WHERE reverse_index.[object_id] =
        OBJECT_ID(N'tb_ops.LegacyEntityMappings')
      AND reverse_index.[name] = N'IX_LegacyEntityMappings_NewEntity'
      AND index_column.[key_ordinal] > 0
) <> 4
BEGIN
    PRINT N'FAIL: LegacyEntityMappings reverse-entity lookup has the wrong key shape.';
    SET @FailureCount += 1;
END;

DECLARE @ExpectedEntityUniquenessIndexes TABLE
(
    [IndexName] sysname NOT NULL PRIMARY KEY,
    [EntityType] nvarchar(40) NOT NULL
);

INSERT INTO @ExpectedEntityUniquenessIndexes([IndexName], [EntityType])
VALUES
    (N'UX_LegacyEntityMappings_WorkEntryNewEntity', N'WorkEntry'),
    (N'UX_LegacyEntityMappings_PostingLogNewEntity', N'PostingLog');

DECLARE @MissingEntityUniquenessIndexCount int =
(
    SELECT COUNT(*)
    FROM @ExpectedEntityUniquenessIndexes AS expected_index
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes AS actual_index
        WHERE actual_index.[object_id] =
            OBJECT_ID(N'tb_ops.LegacyEntityMappings')
          AND actual_index.[name] = expected_index.[IndexName]
          AND actual_index.[is_unique] = 1
          AND actual_index.[has_filter] = 1
          AND CHARINDEX(
                  N'[EntityType]',
                  actual_index.[filter_definition]) > 0
          AND CHARINDEX(
                  N'N''' + expected_index.[EntityType] + N'''',
                  actual_index.[filter_definition]) > 0
    )
);

IF @MissingEntityUniquenessIndexCount > 0
BEGIN
    PRINT N'FAIL: LegacyEntityMappings lacks filtered WorkEntry/PostingLog reverse uniqueness.';
    SET @FailureCount += @MissingEntityUniquenessIndexCount;
END;

IF EXISTS
(
    SELECT 1
    FROM @ExpectedEntityUniquenessIndexes AS expected_index
    INNER JOIN sys.indexes AS actual_index
        ON actual_index.[object_id] =
            OBJECT_ID(N'tb_ops.LegacyEntityMappings')
       AND actual_index.[name] = expected_index.[IndexName]
    WHERE
    (
        SELECT COUNT(*)
        FROM sys.index_columns AS index_column
        WHERE index_column.[object_id] = actual_index.[object_id]
          AND index_column.[index_id] = actual_index.[index_id]
          AND index_column.[key_ordinal] > 0
    ) <> 3
    OR NOT EXISTS
    (
        SELECT 1
        FROM sys.index_columns AS index_column
        INNER JOIN sys.columns AS actual_column
            ON actual_column.[object_id] = index_column.[object_id]
           AND actual_column.[column_id] = index_column.[column_id]
        WHERE index_column.[object_id] = actual_index.[object_id]
          AND index_column.[index_id] = actual_index.[index_id]
          AND index_column.[key_ordinal] = 1
          AND actual_column.[name] = N'OwnerWindowsSid'
    )
    OR NOT EXISTS
    (
        SELECT 1
        FROM sys.index_columns AS index_column
        INNER JOIN sys.columns AS actual_column
            ON actual_column.[object_id] = index_column.[object_id]
           AND actual_column.[column_id] = index_column.[column_id]
        WHERE index_column.[object_id] = actual_index.[object_id]
          AND index_column.[index_id] = actual_index.[index_id]
          AND index_column.[key_ordinal] = 2
          AND actual_column.[name] = N'SourceSystem'
    )
    OR NOT EXISTS
    (
        SELECT 1
        FROM sys.index_columns AS index_column
        INNER JOIN sys.columns AS actual_column
            ON actual_column.[object_id] = index_column.[object_id]
           AND actual_column.[column_id] = index_column.[column_id]
        WHERE index_column.[object_id] = actual_index.[object_id]
          AND index_column.[index_id] = actual_index.[index_id]
          AND index_column.[key_ordinal] = 3
          AND actual_column.[name] = N'NewEntityId'
    )
)
BEGIN
    PRINT N'FAIL: A filtered LegacyEntityMappings reverse-uniqueness index has the wrong key shape.';
    SET @FailureCount += 1;
END;

/*
    A unique reverse index that applies to WorkEntryLink would reject two
    distinct legacy link IDs that describe the same canonical relationship.
*/
IF EXISTS
(
    SELECT 1
    FROM sys.indexes AS unique_index
    WHERE unique_index.[object_id] =
        OBJECT_ID(N'tb_ops.LegacyEntityMappings')
      AND unique_index.[is_unique] = 1
      AND EXISTS
      (
          SELECT 1
          FROM sys.index_columns AS index_column
          INNER JOIN sys.columns AS indexed_column
              ON indexed_column.[object_id] = index_column.[object_id]
             AND indexed_column.[column_id] = index_column.[column_id]
          WHERE index_column.[object_id] = unique_index.[object_id]
            AND index_column.[index_id] = unique_index.[index_id]
            AND index_column.[key_ordinal] > 0
            AND indexed_column.[name] = N'NewEntityId'
      )
      AND NOT EXISTS
      (
          SELECT 1
          FROM sys.index_columns AS index_column
          INNER JOIN sys.columns AS indexed_column
              ON indexed_column.[object_id] = index_column.[object_id]
             AND indexed_column.[column_id] = index_column.[column_id]
          WHERE index_column.[object_id] = unique_index.[object_id]
            AND index_column.[index_id] = unique_index.[index_id]
            AND index_column.[key_ordinal] > 0
            AND indexed_column.[name] = N'LegacyId'
      )
      AND
      (
          unique_index.[has_filter] = 0
          OR REPLACE(
                 REPLACE(
                     REPLACE(unique_index.[filter_definition], N' ', N''),
                     N'(',
                     N''),
                 N')',
                 N'') IN
             (
                 N'[EntityType]=N''WorkEntryLink''',
                 N'N''WorkEntryLink''=[EntityType]'
             )
      )
)
BEGIN
    PRINT N'FAIL: A unique reverse mapping still prevents multiple V1 link IDs from sharing one SQL relationship.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'tb_ops.ImportBatches')
      AND [name] = N'IX_ImportBatches_OwnerSourceFileHash'
)
BEGIN
    PRINT N'FAIL: ImportBatches lacks the owner/source/file-hash lookup index.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredProcedures TABLE
(
    [ObjectName] nvarchar(300) NOT NULL PRIMARY KEY
);

INSERT INTO @RequiredProcedures([ObjectName])
VALUES
    (N'tb_app.BeginTechBenchV1Import'),
    (N'tb_app.ImportTechBenchV1WorkEntry'),
    (N'tb_app.ImportTechBenchV1WorkEntryLink'),
    (N'tb_app.ImportTechBenchV1PostingLog'),
    (N'tb_app.CompleteTechBenchV1Import'),
    (N'tb_app.AbandonTechBenchV1Import'),
    (N'tb_app.ResolveTechBenchV1Reference');

DECLARE @MissingProcedureCount int =
(
    SELECT COUNT(*)
    FROM @RequiredProcedures AS required_procedure
    WHERE OBJECT_ID(required_procedure.[ObjectName], N'P') IS NULL
);

IF @MissingProcedureCount > 0
BEGIN
    PRINT N'FAIL: One or more TechBench V1 import procedures are missing.';
    SET @FailureCount += @MissingProcedureCount;
END;

IF EXISTS
(
    SELECT 1
    FROM @RequiredProcedures AS import_procedure
    INNER JOIN sys.parameters AS procedure_parameter
        ON procedure_parameter.[object_id] = OBJECT_ID(import_procedure.[ObjectName])
    WHERE procedure_parameter.[name] IN
    (
        N'@OwnerWindowsSid',
        N'@UserSid',
        N'@LoginName',
        N'@OwnerLoginName'
    )
)
BEGIN
    PRINT N'FAIL: A TechBench V1 import procedure accepts a client-supplied owner identity.';
    SET @FailureCount += 1;
END;

DECLARE @BeginDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.BeginTechBenchV1Import'));
DECLARE @WorkEntryDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.ImportTechBenchV1WorkEntry'));
DECLARE @LinkDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.ImportTechBenchV1WorkEntryLink'));
DECLARE @PostingLogDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.ImportTechBenchV1PostingLog'));
DECLARE @CompleteDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompleteTechBenchV1Import'));
DECLARE @AbandonDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AbandonTechBenchV1Import'));
DECLARE @ResolverDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.ResolveTechBenchV1Reference'));

DECLARE @LinkSourcePrerequisitePosition int =
    COALESCE(CHARINDEX(
        N'SELECT @SourceWorkEntryId = CONVERT(int, [NewEntityId])',
        @LinkDefinition), 0);
DECLARE @LinkSourceCurrentBatchPosition int =
    COALESCE(CHARINDEX(
        N'AND [LastSeenImportBatchId] = @BatchId',
        @LinkDefinition,
        @LinkSourcePrerequisitePosition), 0);
DECLARE @LinkTargetPrerequisitePosition int =
    COALESCE(CHARINDEX(
        N'SELECT @TargetWorkEntryId = CONVERT(int, [NewEntityId])',
        @LinkDefinition), 0);
DECLARE @LinkTargetCurrentBatchPosition int =
    COALESCE(CHARINDEX(
        N'AND [LastSeenImportBatchId] = @BatchId',
        @LinkDefinition,
        @LinkTargetPrerequisitePosition), 0);
DECLARE @LinkExistingMappingBranchPosition int =
    COALESCE(CHARINDEX(
        N'IF @ExistingNewEntityId IS NOT NULL',
        @LinkDefinition), 0);
DECLARE @LinkExistingMappingUpdatePosition int =
    COALESCE(CHARINDEX(
        N'UPDATE [tb_ops].[LegacyEntityMappings]',
        @LinkDefinition,
        @LinkExistingMappingBranchPosition), 0);

DECLARE @PostingPrerequisitePosition int =
    COALESCE(CHARINDEX(
        N'SELECT @WorkEntryId = CONVERT(int, [NewEntityId])',
        @PostingLogDefinition), 0);
DECLARE @PostingCurrentBatchPosition int =
    COALESCE(CHARINDEX(
        N'AND [LastSeenImportBatchId] = @BatchId',
        @PostingLogDefinition,
        @PostingPrerequisitePosition), 0);
DECLARE @PostingExistingMappingBranchPosition int =
    COALESCE(CHARINDEX(
        N'IF @ExistingNewEntityId IS NOT NULL',
        @PostingLogDefinition), 0);
DECLARE @PostingExistingReconcilePosition int =
    COALESCE(CHARINDEX(
        N'IF @Success = 1',
        @PostingLogDefinition,
        @PostingExistingMappingBranchPosition), 0);

IF @BeginDefinition IS NULL
   OR CHARINDEX(N'[tb_security].[GetCurrentAccess]', @BeginDefinition) = 0
   OR CHARINDEX(N'[OwnerWindowsSid] = @UserSid', @BeginDefinition) = 0
   OR CHARINDEX(N'[FileHash] = @FileHash', @BeginDefinition) = 0
   OR CHARINDEX(N'[ReadCount] = @ExpectedCount', @BeginDefinition) = 0
   OR CHARINDEX(N'[ConflictCount] = 0', @BeginDefinition) = 0
   OR CHARINDEX(N'[ErrorCount] = 0', @BeginDefinition) = 0
BEGIN
    PRINT N'FAIL: BeginTechBenchV1Import lacks its authenticated owner/file-hash resume contract.';
    SET @FailureCount += 1;
END;

IF @WorkEntryDefinition IS NULL
   OR CHARINDEX(N'[tb_security].[GetCurrentAccess]', @WorkEntryDefinition) = 0
   OR CHARINDEX(N'[OwnerWindowsSid] = @UserSid', @WorkEntryDefinition) = 0
   OR CHARINDEX(N'[FileName] IS NOT NULL', @WorkEntryDefinition) = 0
   OR CHARINDEX(N'[FileHash] IS NOT NULL', @WorkEntryDefinition) = 0
   OR CHARINDEX(N'WITH (UPDLOCK, HOLDLOCK)', @WorkEntryDefinition) = 0
   OR CHARINDEX(N'[tb_ops].[LegacyEntityMappings]', @WorkEntryDefinition) = 0
   OR CHARINDEX(N'N''Conflict''', @WorkEntryDefinition) = 0
   OR CHARINDEX(N'INSERT INTO [tb_data].[WorkEntries]', @WorkEntryDefinition) = 0
   OR CHARINDEX(N'INSERT INTO [tb_private].[WorkEntryPersonalNotes]', @WorkEntryDefinition) = 0
   OR CHARINDEX(N'@WhdPostedAtUtc', @WorkEntryDefinition) = 0
   OR CHARINDEX(N'@SagePostedAtUtc', @WorkEntryDefinition) = 0
   OR CHARINDEX(N'@SageTicketNumber', @WorkEntryDefinition) = 0
   OR CHARINDEX(N'@CreatedAtUtc', @WorkEntryDefinition) = 0
   OR CHARINDEX(N'@UpdatedAtUtc', @WorkEntryDefinition) = 0
   OR CHARINDEX(N'N''PostedToBoth''', @WorkEntryDefinition) = 0
BEGIN
    PRINT N'FAIL: ImportTechBenchV1WorkEntry lacks its owner/idempotency/private-note/posting-state contract.';
    SET @FailureCount += 1;
END;

IF @WorkEntryDefinition IS NULL
   OR CHARINDEX(
          N'ISNULL([ClientId], -1) <> ISNULL(@ClientId, -1)',
          @WorkEntryDefinition) = 0
   OR CHARINDEX(
          N'ISNULL([TicketId], -1) <> ISNULL(@TicketId, -1)',
          @WorkEntryDefinition) = 0
   OR CHARINDEX(
          N'The resolved client or ticket changed',
          @WorkEntryDefinition) = 0
BEGIN
    PRINT N'FAIL: ImportTechBenchV1WorkEntry can silently reuse a legacy mapping after its resolved client or ticket changes.';
    SET @FailureCount += 1;
END;

IF @WorkEntryDefinition IS NULL
   OR CHARINDEX(
          N'@ExistingFirstImportBatchId = [FirstImportBatchId]',
          @WorkEntryDefinition) = 0
   OR CHARINDEX(
          N'@ExistingFirstImportBatchId = @BatchId',
          @WorkEntryDefinition) = 0
   OR CHARINDEX(
          N'CASE WHEN @ExistingFirstImportBatchId = @BatchId THEN N''Imported'' ELSE N''Skipped'' END AS [Outcome]',
          @WorkEntryDefinition) = 0
BEGIN
    PRINT N'FAIL: ImportTechBenchV1WorkEntry does not distinguish a same-batch replay from a prior-batch retry.';
    SET @FailureCount += 1;
END;

IF @LinkDefinition IS NULL
   OR CHARINDEX(N'[tb_security].[GetCurrentAccess]', @LinkDefinition) = 0
   OR CHARINDEX(N'[OwnerWindowsSid] = @UserSid', @LinkDefinition) = 0
   OR CHARINDEX(N'[FileName] IS NOT NULL', @LinkDefinition) = 0
   OR CHARINDEX(N'[FileHash] IS NOT NULL', @LinkDefinition) = 0
   OR CHARINDEX(N'[EntityType] = N''WorkEntry''', @LinkDefinition) = 0
   OR CHARINDEX(N'INSERT INTO [tb_data].[WorkEntryLinks]', @LinkDefinition) = 0
   OR CHARINDEX(N'@LinkType = N''Related''', @LinkDefinition) = 0
   OR CHARINDEX(N'N''Conflict''', @LinkDefinition) = 0
BEGIN
    PRINT N'FAIL: ImportTechBenchV1WorkEntryLink lacks its mapped-owner/idempotency contract.';
    SET @FailureCount += 1;
END;

IF @LinkSourcePrerequisitePosition = 0
   OR @LinkSourceCurrentBatchPosition <= @LinkSourcePrerequisitePosition
   OR @LinkTargetPrerequisitePosition <= @LinkSourceCurrentBatchPosition
   OR @LinkTargetCurrentBatchPosition <= @LinkTargetPrerequisitePosition
   OR @LinkExistingMappingBranchPosition <= @LinkTargetCurrentBatchPosition
BEGIN
    PRINT N'FAIL: ImportTechBenchV1WorkEntryLink does not validate both current-batch prerequisites before its existing-mapping replay branch.';
    SET @FailureCount += 1;
END;

IF @LinkExistingMappingBranchPosition = 0
   OR @LinkExistingMappingUpdatePosition <= @LinkExistingMappingBranchPosition
   OR CHARINDEX(
          N'link.[Id] = CONVERT(int, @ExistingNewEntityId)',
          @LinkDefinition,
          @LinkExistingMappingBranchPosition) NOT BETWEEN
              @LinkExistingMappingBranchPosition
              AND @LinkExistingMappingUpdatePosition
   OR CHARINDEX(
          N'link.[LinkType] = @LinkType',
          @LinkDefinition,
          @LinkExistingMappingBranchPosition) NOT BETWEEN
              @LinkExistingMappingBranchPosition
              AND @LinkExistingMappingUpdatePosition
   OR CHARINDEX(
          N'link.[SourceWorkEntryId] = @SourceWorkEntryId',
          @LinkDefinition,
          @LinkExistingMappingBranchPosition) NOT BETWEEN
              @LinkExistingMappingBranchPosition
              AND @LinkExistingMappingUpdatePosition
   OR CHARINDEX(
          N'link.[TargetWorkEntryId] = @TargetWorkEntryId',
          @LinkDefinition,
          @LinkExistingMappingBranchPosition) NOT BETWEEN
              @LinkExistingMappingBranchPosition
              AND @LinkExistingMappingUpdatePosition
   OR CHARINDEX(
          N'@LinkType = N''Related''',
          @LinkDefinition,
          @LinkExistingMappingBranchPosition) NOT BETWEEN
              @LinkExistingMappingBranchPosition
              AND @LinkExistingMappingUpdatePosition
   OR CHARINDEX(
          N'link.[SourceWorkEntryId] = @TargetWorkEntryId',
          @LinkDefinition,
          @LinkExistingMappingBranchPosition) NOT BETWEEN
              @LinkExistingMappingBranchPosition
              AND @LinkExistingMappingUpdatePosition
   OR CHARINDEX(
          N'link.[TargetWorkEntryId] = @SourceWorkEntryId',
          @LinkDefinition,
          @LinkExistingMappingBranchPosition) NOT BETWEEN
              @LinkExistingMappingBranchPosition
              AND @LinkExistingMappingUpdatePosition
BEGIN
    PRINT N'FAIL: ImportTechBenchV1WorkEntryLink does not validate an existing mapping against link type and mapped source/target SQL IDs.';
    SET @FailureCount += 1;
END;

IF @LinkDefinition IS NULL
   OR
   (
       LEN(@LinkDefinition)
       - LEN(REPLACE(
                 @LinkDefinition,
                 N'AND [LastSeenImportBatchId] = @BatchId',
                 N''))
   ) / LEN(N'AND [LastSeenImportBatchId] = @BatchId') < 2
   OR CHARINDEX(
          N'@ExistingFirstImportBatchId = [FirstImportBatchId]',
          @LinkDefinition) = 0
   OR CHARINDEX(
          N'@ExistingFirstImportBatchId = @BatchId',
          @LinkDefinition) = 0
   OR CHARINDEX(
          N'CONVERT(bigint, NULL) AS [NewEntityId]',
          @LinkDefinition) = 0
   OR CHARINDEX(N'stale mapping', @LinkDefinition) = 0
   OR CHARINDEX(
          N'CASE WHEN @ExistingFirstImportBatchId = @BatchId THEN N''Imported'' ELSE N''Skipped'' END AS [Outcome]',
          @LinkDefinition) = 0
BEGIN
    PRINT N'FAIL: ImportTechBenchV1WorkEntryLink can reuse stale prerequisites or misclassify a resumed same-batch mapping.';
    SET @FailureCount += 1;
END;

IF @PostingLogDefinition IS NULL
   OR CHARINDEX(N'[tb_security].[GetCurrentAccess]', @PostingLogDefinition) = 0
   OR CHARINDEX(N'[OwnerWindowsSid] = @UserSid', @PostingLogDefinition) = 0
   OR CHARINDEX(N'[FileName] IS NOT NULL', @PostingLogDefinition) = 0
   OR CHARINDEX(N'[FileHash] IS NOT NULL', @PostingLogDefinition) = 0
   OR CHARINDEX(N'[EntityType] = N''WorkEntry''', @PostingLogDefinition) = 0
   OR CHARINDEX(N'INSERT INTO [tb_ops].[PostingLogs]', @PostingLogDefinition) = 0
   OR CHARINDEX(N'N''Conflict''', @PostingLogDefinition) = 0
BEGIN
    PRINT N'FAIL: ImportTechBenchV1PostingLog lacks its mapped-owner/idempotency contract.';
    SET @FailureCount += 1;
END;

IF @PostingPrerequisitePosition = 0
   OR @PostingCurrentBatchPosition <= @PostingPrerequisitePosition
   OR @PostingExistingMappingBranchPosition <= @PostingCurrentBatchPosition
BEGIN
    PRINT N'FAIL: ImportTechBenchV1PostingLog does not validate its current-batch work-entry prerequisite before its existing-mapping replay branch.';
    SET @FailureCount += 1;
END;

IF @PostingExistingMappingBranchPosition = 0
   OR @PostingExistingReconcilePosition <= @PostingExistingMappingBranchPosition
   OR CHARINDEX(
          N'[Id] = @ExistingNewEntityId',
          @PostingLogDefinition,
          @PostingExistingMappingBranchPosition) NOT BETWEEN
              @PostingExistingMappingBranchPosition
              AND @PostingExistingReconcilePosition
   OR CHARINDEX(
          N'[WorkEntryId] = @WorkEntryId',
          @PostingLogDefinition,
          @PostingExistingMappingBranchPosition) NOT BETWEEN
              @PostingExistingMappingBranchPosition
              AND @PostingExistingReconcilePosition
BEGIN
    PRINT N'FAIL: ImportTechBenchV1PostingLog does not validate an existing posting-log mapping against the current mapped work-entry SQL ID.';
    SET @FailureCount += 1;
END;

IF @PostingLogDefinition IS NULL
   OR CHARINDEX(
          N'AND [LastSeenImportBatchId] = @BatchId',
          @PostingLogDefinition) = 0
   OR CHARINDEX(
          N'@ExistingFirstImportBatchId = [FirstImportBatchId]',
          @PostingLogDefinition) = 0
   OR CHARINDEX(
          N'@ExistingFirstImportBatchId = @BatchId',
          @PostingLogDefinition) = 0
   OR CHARINDEX(
          N'CONVERT(bigint, NULL) AS [NewEntityId]',
          @PostingLogDefinition) = 0
   OR CHARINDEX(N'stale mapping', @PostingLogDefinition) = 0
   OR CHARINDEX(
          N'CASE WHEN @ExistingFirstImportBatchId = @BatchId THEN N''Imported'' ELSE N''Skipped'' END AS [Outcome]',
          @PostingLogDefinition) = 0
BEGIN
    PRINT N'FAIL: ImportTechBenchV1PostingLog can reuse a stale prerequisite or misclassify a resumed same-batch mapping.';
    SET @FailureCount += 1;
END;

IF @PostingLogDefinition IS NULL
   OR CHARINDEX(N'IF @Success = 1', @PostingLogDefinition) = 0
   OR CHARINDEX(
          N'UPDATE [tb_data].[WorkEntries]',
          @PostingLogDefinition) = 0
   OR CHARINDEX(N'[WhdPosted] =', @PostingLogDefinition) = 0
   OR CHARINDEX(N'[WhdPostedAtUtc] =', @PostingLogDefinition) = 0
   OR CHARINDEX(N'[SagePosted] =', @PostingLogDefinition) = 0
   OR CHARINDEX(N'[SagePostedAtUtc] =', @PostingLogDefinition) = 0
   OR CHARINDEX(N'[PostingStatus] =', @PostingLogDefinition) = 0
   OR CHARINDEX(N'@CreatedAtUtc', @PostingLogDefinition) = 0
   OR CHARINDEX(
          N'CASE WHEN @Destination = N''WHD'' THEN CONVERT(bit, 1) ELSE [WhdPosted] END',
          @PostingLogDefinition) = 0
   OR CHARINDEX(
          N'CASE WHEN @Destination = N''Sage'' THEN CONVERT(bit, 1) ELSE [SagePosted] END',
          @PostingLogDefinition) = 0
   OR CHARINDEX(N'N''PostedToWhd''', @PostingLogDefinition) = 0
   OR CHARINDEX(N'N''PostedToSage''', @PostingLogDefinition) = 0
   OR CHARINDEX(N'N''PostedToBoth''', @PostingLogDefinition) = 0
   OR CHARINDEX(
          N'COALESCE([WhdPostedAtUtc], @CreatedAtUtc)',
          @PostingLogDefinition) = 0
   OR CHARINDEX(
          N'COALESCE([SagePostedAtUtc], @CreatedAtUtc)',
          @PostingLogDefinition) = 0
BEGIN
    PRINT N'FAIL: A successful imported posting log does not conservatively reconcile the mapped work-entry posting state.';
    SET @FailureCount += 1;
END;

IF @PostingLogDefinition IS NULL
   OR
   (
       LEN(@PostingLogDefinition)
       - LEN(REPLACE(
                 @PostingLogDefinition,
                 N'IF @Success = 1',
                 N''))
   ) / LEN(N'IF @Success = 1') < 2
   OR
   (
       LEN(@PostingLogDefinition)
       - LEN(REPLACE(
                 @PostingLogDefinition,
                 N'UPDATE [tb_data].[WorkEntries]',
                 N''))
   ) / LEN(N'UPDATE [tb_data].[WorkEntries]') < 2
BEGIN
    PRINT N'FAIL: ImportTechBenchV1PostingLog does not reconcile successful logs on both first import and idempotent replay paths.';
    SET @FailureCount += 1;
END;

IF @CompleteDefinition IS NULL
   OR CHARINDEX(N'[tb_security].[GetCurrentAccess]', @CompleteDefinition) = 0
   OR CHARINDEX(N'[OwnerWindowsSid] = @UserSid', @CompleteDefinition) = 0
   OR CHARINDEX(N'[FileName] IS NOT NULL', @CompleteDefinition) = 0
   OR CHARINDEX(N'[FileHash] IS NOT NULL', @CompleteDefinition) = 0
   OR CHARINDEX(N'[ImportedCount] = @ImportedCount', @CompleteDefinition) = 0
   OR CHARINDEX(N'[SkippedCount] = @SkippedCount', @CompleteDefinition) = 0
   OR CHARINDEX(N'[ConflictCount] = @ConflictCount', @CompleteDefinition) = 0
   OR CHARINDEX(N'[ErrorCount] = @ErrorCount', @CompleteDefinition) = 0
   OR CHARINDEX(N'CONVERT(bigint, @ImportedCount)', @CompleteDefinition) = 0
   OR CHARINDEX(N'CONVERT(bigint, @SkippedCount)', @CompleteDefinition) = 0
   OR CHARINDEX(N'CONVERT(bigint, @ConflictCount)', @CompleteDefinition) = 0
   OR CHARINDEX(N'CONVERT(bigint, @ErrorCount)', @CompleteDefinition) = 0
   OR CHARINDEX(N'@Succeeded = 1 AND @ErrorCount <> 0', @CompleteDefinition) = 0
   OR CHARINDEX(
          N'@Succeeded = 1 AND @OutcomeCount <> CONVERT(bigint, @ReadCount)',
          @CompleteDefinition) = 0
BEGIN
    PRINT N'FAIL: CompleteTechBenchV1Import lacks its authenticated exact successful-outcome contract.';
    SET @FailureCount += 1;
END;

IF @AbandonDefinition IS NULL
   OR CHARINDEX(N'@BatchId uniqueidentifier = NULL', @AbandonDefinition) = 0
   OR CHARINDEX(N'[tb_security].[GetCurrentAccess]', @AbandonDefinition) = 0
   OR CHARINDEX(N'@BatchId IS NULL', @AbandonDefinition) = 0
   OR CHARINDEX(N'WITH (UPDLOCK, HOLDLOCK)', @AbandonDefinition) = 0
   OR CHARINDEX(N'[OwnerWindowsSid] = @UserSid', @AbandonDefinition) = 0
   OR CHARINDEX(N'[SourceSystem] = N''TechBenchV1''', @AbandonDefinition) = 0
   OR CHARINDEX(N'[Status] = N''Started''', @AbandonDefinition) = 0
   OR CHARINDEX(N'[Status] = N''Abandoned''', @AbandonDefinition) = 0
   OR CHARINDEX(N'N''TechBenchV1ImportAbandoned''', @AbandonDefinition) = 0
   OR CHARINDEX(N'BEGIN TRANSACTION', @AbandonDefinition) = 0
BEGIN
    PRINT N'FAIL: AbandonTechBenchV1Import lacks nullable current-batch recovery, owner scope, or audit atomicity.';
    SET @FailureCount += 1;
END;

DECLARE @ExpectedResolverParameters TABLE
(
    [ParameterId] int NOT NULL PRIMARY KEY,
    [ParameterName] sysname NOT NULL,
    [MaxLength] smallint NOT NULL
);

INSERT INTO @ExpectedResolverParameters
(
    [ParameterId],
    [ParameterName],
    [MaxLength]
)
VALUES
    (1, N'@ClientSourceSystem', 80),
    (2, N'@ClientExternalId', 1000),
    (3, N'@SageCustomerId', 240),
    (4, N'@ClientName', 480),
    (5, N'@TicketSourceSystem', 80),
    (6, N'@TicketExternalId', 480),
    (7, N'@TicketNumber', 240);

DECLARE @InvalidResolverParameterCount int =
(
    SELECT COUNT(*)
    FROM @ExpectedResolverParameters AS expected_parameter
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.parameters AS actual_parameter
        INNER JOIN sys.types AS parameter_type
            ON parameter_type.[user_type_id] = actual_parameter.[user_type_id]
        WHERE actual_parameter.[object_id] =
            OBJECT_ID(N'tb_app.ResolveTechBenchV1Reference')
          AND actual_parameter.[parameter_id] = expected_parameter.[ParameterId]
          AND actual_parameter.[name] = expected_parameter.[ParameterName]
          AND parameter_type.[name] = N'nvarchar'
          AND actual_parameter.[max_length] = expected_parameter.[MaxLength]
          AND actual_parameter.[is_output] = 0
    )
);

IF @InvalidResolverParameterCount > 0
   OR
   (
       SELECT COUNT(*)
       FROM sys.parameters
       WHERE [object_id] =
           OBJECT_ID(N'tb_app.ResolveTechBenchV1Reference')
         AND [parameter_id] > 0
   ) <> 7
BEGIN
    PRINT N'FAIL: ResolveTechBenchV1Reference does not expose the expected seven optional exact-reference inputs.';
    SET @FailureCount += CASE
        WHEN @InvalidResolverParameterCount > 0
            THEN @InvalidResolverParameterCount
        ELSE 1
    END;
END;

IF @ResolverDefinition IS NULL
   OR CHARINDEX(
          N'@ClientSourceSystem nvarchar(40) = NULL',
          @ResolverDefinition) = 0
   OR CHARINDEX(
          N'@ClientExternalId nvarchar(500) = NULL',
          @ResolverDefinition) = 0
   OR CHARINDEX(
          N'@SageCustomerId nvarchar(120) = NULL',
          @ResolverDefinition) = 0
   OR CHARINDEX(N'@ClientName nvarchar(240) = NULL', @ResolverDefinition) = 0
   OR CHARINDEX(
          N'@TicketSourceSystem nvarchar(40) = NULL',
          @ResolverDefinition) = 0
   OR CHARINDEX(
          N'@TicketExternalId nvarchar(240) = NULL',
          @ResolverDefinition) = 0
   OR CHARINDEX(N'@TicketNumber nvarchar(120) = NULL', @ResolverDefinition) = 0
BEGIN
    PRINT N'FAIL: ResolveTechBenchV1Reference inputs are not all optional with the expected SQL contract.';
    SET @FailureCount += 1;
END;

IF @ResolverDefinition IS NULL
   OR CHARINDEX(N'[tb_security].[GetCurrentAccess]', @ResolverDefinition) = 0
   OR CHARINDEX(N'[tb_data].[ClientExternalIdentities]', @ResolverDefinition) = 0
   OR CHARINDEX(N'[tb_data].[Clients]', @ResolverDefinition) = 0
   OR CHARINDEX(N'[tb_data].[ClientAliases]', @ResolverDefinition) = 0
   OR CHARINDEX(N'[tb_data].[Tickets]', @ResolverDefinition) = 0
   OR CHARINDEX(N'[ScopeType] = N''Organization''', @ResolverDefinition) = 0
   OR CHARINDEX(N'[ClientResolutionStatus]', @ResolverDefinition) = 0
   OR CHARINDEX(N'[ClientId]', @ResolverDefinition) = 0
   OR CHARINDEX(N'[ClientMatchMethod]', @ResolverDefinition) = 0
   OR CHARINDEX(N'[TicketResolutionStatus]', @ResolverDefinition) = 0
   OR CHARINDEX(N'[TicketId]', @ResolverDefinition) = 0
   OR CHARINDEX(N'[TicketMatchMethod]', @ResolverDefinition) = 0
   OR CHARINDEX(N'N''Matched''', @ResolverDefinition) = 0
   OR CHARINDEX(N'N''NotFound''', @ResolverDefinition) = 0
   OR CHARINDEX(N'N''Ambiguous''', @ResolverDefinition) = 0
   OR CHARINDEX(N'N''Conflict''', @ResolverDefinition) = 0
   OR CHARINDEX(N'N''NotResolved''', @ResolverDefinition) = 0
BEGIN
    PRINT N'FAIL: ResolveTechBenchV1Reference lacks its authenticated direct-table, unambiguous status/result contract.';
    SET @FailureCount += 1;
END;

IF @ResolverDefinition IS NULL
   OR CHARINDEX(
          N'IF @ClientSourceSystem = N''Both''',
          @ResolverDefinition) = 0
   OR CHARINDEX(
          N'SET @ClientSourceSystem = N''WHD''',
          @ResolverDefinition) = 0
   OR CHARINDEX(
          N'IF @TicketSourceSystem = N''Both''',
          @ResolverDefinition) = 0
   OR CHARINDEX(
          N'SET @TicketSourceSystem = N''WHD''',
          @ResolverDefinition) = 0
   OR CHARINDEX(
          N'identity_row.[SourceSystem] = @ClientSourceSystem',
          @ResolverDefinition) = 0
   OR CHARINDEX(
          N'identity_row.[ExternalId] = @ClientExternalId',
          @ResolverDefinition) = 0
   OR CHARINDEX(
          N'client.[SageCustomerId] = @SageCustomerId',
          @ResolverDefinition) = 0
   OR CHARINDEX(
          N'alias_row.[ScopeType] = N''Organization''',
          @ResolverDefinition) = 0
   OR CHARINDEX(
          N'alias_row.[Alias] = @ClientName',
          @ResolverDefinition) = 0
   OR CHARINDEX(N'client.[Name] = @ClientName', @ResolverDefinition) = 0
   OR CHARINDEX(
          N'ticket.[Source] = @TicketSourceSystem',
          @ResolverDefinition) = 0
   OR CHARINDEX(
          N'ticket.[ExternalId] = @TicketExternalId',
          @ResolverDefinition) = 0
   OR CHARINDEX(
          N'ticket.[ClientId] = @ResolvedClientId',
          @ResolverDefinition) = 0
   OR CHARINDEX(
          N'ticket.[TicketNumber] = @TicketNumber',
          @ResolverDefinition) = 0
   OR CHARINDEX(N'COUNT(DISTINCT [ClientId])', @ResolverDefinition) = 0
   OR CHARINDEX(N'COUNT(DISTINCT [TicketId])', @ResolverDefinition) = 0
   OR CHARINDEX(N'N''ClientExternalIdentity''', @ResolverDefinition) = 0
   OR CHARINDEX(N'N''SageCustomerId''', @ResolverDefinition) = 0
   OR CHARINDEX(N'N''OrganizationAlias''', @ResolverDefinition) = 0
   OR CHARINDEX(N'N''ClientName''', @ResolverDefinition) = 0
   OR CHARINDEX(N'N''TicketExternalIdentity''', @ResolverDefinition) = 0
   OR CHARINDEX(N'N''TicketNumber''', @ResolverDefinition) = 0
BEGIN
    PRINT N'FAIL: ResolveTechBenchV1Reference lacks exact source qualification, V1 Both-to-WHD normalization, or unambiguous fallback matching.';
    SET @FailureCount += 1;
END;

IF @ResolverDefinition IS NOT NULL
   AND
   (
       (
           LEN(@ResolverDefinition)
           - LEN(REPLACE(
                     @ResolverDefinition,
                     N'identity_row.[ExternalId] = @ClientExternalId',
                     N''))
       ) / LEN(N'identity_row.[ExternalId] = @ClientExternalId') <> 1
       OR
       (
           LEN(@ResolverDefinition)
           - LEN(REPLACE(
                     @ResolverDefinition,
                     N'ticket.[ExternalId] = @TicketExternalId',
                     N''))
       ) / LEN(N'ticket.[ExternalId] = @TicketExternalId') <> 1
   )
BEGIN
    PRINT N'FAIL: ResolveTechBenchV1Reference contains an unexpected additional external-ID lookup path.';
    SET @FailureCount += 1;
END;

IF @ResolverDefinition IS NOT NULL
   AND
   (
       CHARINDEX(N' LIKE ', UPPER(@ResolverDefinition)) > 0
       OR CHARINDEX(N'SOUNDEX(', UPPER(@ResolverDefinition)) > 0
       OR CHARINDEX(N'DIFFERENCE(', UPPER(@ResolverDefinition)) > 0
       OR CHARINDEX(N'TOP (', UPPER(@ResolverDefinition)) > 0
       OR CHARINDEX(N'TOP(', UPPER(@ResolverDefinition)) > 0
       OR CHARINDEX(N'@LIMIT', UPPER(@ResolverDefinition)) > 0
       OR CHARINDEX(N'OFFSET ', UPPER(@ResolverDefinition)) > 0
       OR CHARINDEX(N'FETCH NEXT', UPPER(@ResolverDefinition)) > 0
       OR CHARINDEX(N'[TB_APP].[SEARCHCLIENTS]', UPPER(@ResolverDefinition)) > 0
       OR CHARINDEX(N'[TB_APP].[GETTICKETS]', UPPER(@ResolverDefinition)) > 0
   )
BEGIN
    PRINT N'FAIL: ResolveTechBenchV1Reference uses fuzzy, capped, paged, or list-procedure lookup behavior.';
    SET @FailureCount += 1;
END;

IF @ResolverDefinition IS NOT NULL
   AND
   (
       CHARINDEX(
           N'INSERT INTO [tb_data].[ClientExternalIdentities]',
           @ResolverDefinition) > 0
       OR CHARINDEX(
              N'UPDATE [tb_data].[ClientExternalIdentities]',
              @ResolverDefinition) > 0
       OR CHARINDEX(
              N'DELETE FROM [tb_data].[ClientExternalIdentities]',
              @ResolverDefinition) > 0
       OR CHARINDEX(N'INSERT INTO [tb_data].[Clients]', @ResolverDefinition) > 0
       OR CHARINDEX(N'UPDATE [tb_data].[Clients]', @ResolverDefinition) > 0
       OR CHARINDEX(N'DELETE FROM [tb_data].[Clients]', @ResolverDefinition) > 0
       OR CHARINDEX(N'INSERT INTO [tb_data].[ClientAliases]', @ResolverDefinition) > 0
       OR CHARINDEX(N'UPDATE [tb_data].[ClientAliases]', @ResolverDefinition) > 0
       OR CHARINDEX(N'DELETE FROM [tb_data].[ClientAliases]', @ResolverDefinition) > 0
       OR CHARINDEX(N'INSERT INTO [tb_data].[Tickets]', @ResolverDefinition) > 0
       OR CHARINDEX(N'UPDATE [tb_data].[Tickets]', @ResolverDefinition) > 0
       OR CHARINDEX(N'DELETE FROM [tb_data].[Tickets]', @ResolverDefinition) > 0
   )
BEGIN
    PRINT N'FAIL: ResolveTechBenchV1Reference is not read-only over authoritative shared tables.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'IF @Source = N''TechBenchV1''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.BeginImportBatch'))) = 0
   OR CHARINDEX(
       N'[SourceSystem] = N''TechBenchV1''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompleteImportBatch'))) = 0
   OR CHARINDEX(
       N'CompleteTechBenchV1Import',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompleteImportBatch'))) = 0
BEGIN
    PRINT N'FAIL: Generic import lifecycle procedures can bypass the V1 file/count contract.';
    SET @FailureCount += 1;
END;

/* Import procedures may read shared clients/tickets, but never mutate catalogs. */
DECLARE @ImportMutationDefinitions TABLE
(
    [DefinitionText] nvarchar(max) NULL
);

INSERT INTO @ImportMutationDefinitions([DefinitionText])
VALUES
    (@BeginDefinition),
    (@WorkEntryDefinition),
    (@LinkDefinition),
    (@PostingLogDefinition),
    (@CompleteDefinition);

IF EXISTS
(
    SELECT 1
    FROM @ImportMutationDefinitions
    WHERE CHARINDEX(N'INSERT INTO [tb_data].[Clients]', [DefinitionText]) > 0
       OR CHARINDEX(N'UPDATE [tb_data].[Clients]', [DefinitionText]) > 0
       OR CHARINDEX(N'DELETE FROM [tb_data].[Clients]', [DefinitionText]) > 0
       OR CHARINDEX(N'INSERT INTO [tb_data].[Tickets]', [DefinitionText]) > 0
       OR CHARINDEX(N'UPDATE [tb_data].[Tickets]', [DefinitionText]) > 0
       OR CHARINDEX(N'DELETE FROM [tb_data].[Tickets]', [DefinitionText]) > 0
       OR CHARINDEX(N'[tb_data].[ClientAliases]', [DefinitionText]) > 0
       OR CHARINDEX(N'[tb_data].[CommonLinks]', [DefinitionText]) > 0
       OR CHARINDEX(N'[tb_data].[Templates]', [DefinitionText]) > 0
       OR CHARINDEX(N'[tb_data].[OrganizationTags]', [DefinitionText]) > 0
       OR CHARINDEX(N'[tb_data].[OrganizationSettings]', [DefinitionText]) > 0
)
BEGIN
    PRINT N'FAIL: A TechBench V1 import procedure can mutate shared catalog/configuration data.';
    SET @FailureCount += 1;
END;

DECLARE @ExpectedGrants TABLE
(
    [RoleName] sysname NOT NULL,
    [ObjectName] nvarchar(300) NOT NULL,
    PRIMARY KEY ([RoleName], [ObjectName])
);

INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
SELECT N'tb_role_user', [ObjectName]
FROM @RequiredProcedures;

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
    PRINT N'FAIL: tb_role_user is missing one or more V1 import procedure grants.';
    SET @FailureCount += @MissingGrantCount;
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

IF CHARINDEX(
       N'CONVERT(int, ' + CONVERT(nvarchar(10), @InstalledSchemaVersion) + N')',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities'))) = 0
   OR CHARINDEX(
       N'[SupportsTechBenchV1Import]',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities'))) = 0
BEGIN
    PRINT N'FAIL: GetRepositoryCapabilities does not report the installed schema version and V1 import support.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'[ConflictCount]',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetImportBatches'))) = 0
BEGIN
    PRINT N'FAIL: GetImportBatches does not return V0005 conflict counts.';
    SET @FailureCount += 1;
END;

IF @FailureCount > 0
BEGIN
    RAISERROR(
        N'TechBench V0005 owner-scoped V1 import verification failed with %d issue(s).',
        16,
        1,
        @FailureCount);
    RETURN;
END;

PRINT N'TechBench V0005 owner-scoped V1 import verification passed.';

SELECT
    DB_NAME() AS [DatabaseName],
    MAX([SchemaVersion]) AS [SchemaVersion],
    MAX
    (
        CASE
            WHEN [MigrationId] = N'SqlServer2016.TechBenchV1Import.0005'
                THEN [AppliedAtUtc]
        END
    ) AS [TechBenchV1ImportAppliedAtUtc]
FROM [tb_deploy].[SchemaMigrations];
GO

-- ============================================================================
-- END 94-V0005-TechBenchV1ImportVerify.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 95-V0006-WhdServerSyncVerify.sql
-- ============================================================================

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
        (N'tb_service.CompleteSageSyncWork');
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
DECLARE @SearchDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SearchTickets'));
DECLARE @GetTicketDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetTicket'));

SELECT @ClaimDefinition = REPLACE(REPLACE(REPLACE(@ClaimDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @CompleteDefinition = REPLACE(REPLACE(REPLACE(@CompleteDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @MappingDefinition = REPLACE(REPLACE(REPLACE(@MappingDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
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
   OR CHARINDEX(N'WriteAuditEvent', @MappingDefinition) = 0
   OR CHARINDEX(N'DELETEFROM[tb_whd].[UserTechnicianMappings]', @MappingDefinition) = 0
BEGIN
    PRINT N'FAIL: WHD user mapping does not support audited removal.';
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

-- ============================================================================
-- END 95-V0006-WhdServerSyncVerify.sql
-- ============================================================================

-- ============================================================================
-- BEGIN 96-V0007-ServerOwnedSageAndAdminPreviewVerify.sql
-- ============================================================================

:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @FailureCount int = 0;

IF NOT EXISTS
(
    SELECT 1 FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.ServerOwnedSageAndAdminPreview.0007'
      AND [SchemaVersion] = 7
      AND [ReleaseVersion] = N'2.0.0-alpha.8'
)
BEGIN
    PRINT N'FAIL: V0007 migration marker is missing or invalid.';
    SET @FailureCount += 1;
END;

IF (SELECT MAX([SchemaVersion]) FROM [tb_deploy].[SchemaMigrations]) <> 7
BEGIN
    PRINT N'FAIL: installed schema version is not 7.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredObjects TABLE
(
    [ObjectName] nvarchar(300) NOT NULL PRIMARY KEY,
    [ObjectType] char(2) NOT NULL
);
INSERT INTO @RequiredObjects([ObjectName], [ObjectType]) VALUES
    (N'tb_sync.SageSyncRequests', N'U'),
    (N'tb_sync.SageSyncLeases', N'U'),
    (N'tb_sync.SageSyncHealth', N'U'),
    (N'tb_security.AdminUserPreviewSessions', N'U'),
    (N'tb_app.AdminRequestSageSync', N'P'),
    (N'tb_app.GetSageSyncStatus', N'P'),
    (N'tb_service.GetSageSyncConfiguration', N'P'),
    (N'tb_service.ClaimSageSyncWork', N'P'),
    (N'tb_service.RenewSageSyncLease', N'P'),
    (N'tb_service.ApplySageCustomerSnapshot', N'P'),
    (N'tb_service.CompleteSageSyncWork', N'P'),
    (N'tb_app.AdminListPreviewUsers', N'P'),
    (N'tb_app.AdminBeginUserPreview', N'P'),
    (N'tb_app.ActivateReadOnlyPreview', N'P'),
    (N'tb_app.AdminEndUserPreview', N'P'),
    (N'tb_security.FilterWhdTicketAccess', N'IF');

IF EXISTS
(
    SELECT 1 FROM @RequiredObjects AS required
    WHERE OBJECT_ID(required.[ObjectName], required.[ObjectType]) IS NULL
)
BEGIN
    PRINT N'FAIL: one or more V0007 objects are missing.';
    SET @FailureCount += 1;
END;

IF DATABASE_PRINCIPAL_ID(N'tb_preview_reader') IS NULL
BEGIN
    PRINT N'FAIL: the WITHOUT LOGIN preview reader is missing.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredColumns TABLE
(
    [ObjectName] nvarchar(300) NOT NULL,
    [ColumnName] sysname NOT NULL,
    PRIMARY KEY ([ObjectName], [ColumnName])
);
INSERT INTO @RequiredColumns([ObjectName], [ColumnName]) VALUES
    (N'tb_sync.SageSyncRequests', N'RequestId'),
    (N'tb_sync.SageSyncRequests', N'RequestedByWindowsSid'),
    (N'tb_sync.SageSyncRequests', N'RequestedAtUtc'),
    (N'tb_sync.SageSyncRequests', N'StartedAtUtc'),
    (N'tb_sync.SageSyncRequests', N'CompletedAtUtc'),
    (N'tb_sync.SageSyncRequests', N'Status'),
    (N'tb_sync.SageSyncRequests', N'AllowLargeRemoval'),
    (N'tb_sync.SageSyncRequests', N'RequiresLargeRemovalConfirmation'),
    (N'tb_sync.SageSyncRequests', N'ConfirmedRequestId'),
    (N'tb_sync.SageSyncRequests', N'ExistingCount'),
    (N'tb_sync.SageSyncRequests', N'ReadCount'),
    (N'tb_sync.SageSyncRequests', N'SavedCount'),
    (N'tb_sync.SageSyncRequests', N'StaleCount'),
    (N'tb_sync.SageSyncRequests', N'AttemptCount'),
    (N'tb_sync.SageSyncRequests', N'Message'),
    (N'tb_sync.SageSyncLeases', N'RequestId'),
    (N'tb_sync.SageSyncLeases', N'LeaseId'),
    (N'tb_sync.SageSyncLeases', N'WorkerId'),
    (N'tb_sync.SageSyncLeases', N'ExpiresAtUtc'),
    (N'tb_sync.SageSyncHealth', N'LastAttemptAtUtc'),
    (N'tb_sync.SageSyncHealth', N'LastSuccessfulAtUtc'),
    (N'tb_sync.SageSyncHealth', N'LastError'),
    (N'tb_security.AdminUserPreviewSessions', N'PreviewSessionId'),
    (N'tb_security.AdminUserPreviewSessions', N'ActorWindowsSid'),
    (N'tb_security.AdminUserPreviewSessions', N'TargetWindowsSid'),
    (N'tb_security.AdminUserPreviewSessions', N'ClientInstanceId'),
    (N'tb_security.AdminUserPreviewSessions', N'ExpiresAtUtc'),
    (N'tb_security.AdminUserPreviewSessions', N'EndedAtUtc');

IF EXISTS
(
    SELECT 1 FROM @RequiredColumns AS required
    WHERE COL_LENGTH(required.[ObjectName], required.[ColumnName]) IS NULL
)
BEGIN
    PRINT N'FAIL: a required V0007 column is missing.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredIndexes TABLE
(
    [ObjectName] nvarchar(300) NOT NULL,
    [IndexName] sysname NOT NULL,
    PRIMARY KEY ([ObjectName], [IndexName])
);
INSERT INTO @RequiredIndexes([ObjectName], [IndexName]) VALUES
    (N'tb_sync.SageSyncRequests', N'IX_SageSyncRequests_StatusRequested'),
    (N'tb_sync.SageSyncRequests', N'IX_SageSyncRequests_RequestedAt'),
    (N'tb_security.AdminUserPreviewSessions', N'IX_AdminUserPreviewSessions_ActorActive'),
    (N'tb_security.AdminUserPreviewSessions', N'IX_AdminUserPreviewSessions_Expires');

IF EXISTS
(
    SELECT 1 FROM @RequiredIndexes AS required
    WHERE NOT EXISTS
    (
        SELECT 1 FROM sys.indexes AS index_row
        WHERE index_row.[object_id] = OBJECT_ID(required.[ObjectName], N'U')
          AND index_row.[name] = required.[IndexName]
          AND index_row.[is_disabled] = 0
    )
)
BEGIN
    PRINT N'FAIL: a required V0007 index is missing or disabled.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredParameters TABLE
(
    [ProcedureName] nvarchar(300) NOT NULL,
    [ParameterName] sysname NOT NULL,
    PRIMARY KEY ([ProcedureName], [ParameterName])
);
INSERT INTO @RequiredParameters([ProcedureName], [ParameterName]) VALUES
    (N'tb_app.AdminRequestSageSync', N'@RequestId'),
    (N'tb_app.AdminRequestSageSync', N'@AllowLargeRemoval'),
    (N'tb_app.AdminRequestSageSync', N'@ConfirmedRequestId'),
    (N'tb_service.ClaimSageSyncWork', N'@WorkerId'),
    (N'tb_service.ClaimSageSyncWork', N'@LeaseSeconds'),
    (N'tb_service.RenewSageSyncLease', N'@WorkId'),
    (N'tb_service.RenewSageSyncLease', N'@LeaseId'),
    (N'tb_service.RenewSageSyncLease', N'@WorkerId'),
    (N'tb_service.RenewSageSyncLease', N'@LeaseSeconds'),
    (N'tb_service.ApplySageCustomerSnapshot', N'@WorkId'),
    (N'tb_service.ApplySageCustomerSnapshot', N'@LeaseId'),
    (N'tb_service.ApplySageCustomerSnapshot', N'@WorkerId'),
    (N'tb_service.ApplySageCustomerSnapshot', N'@Json'),
    (N'tb_service.ApplySageCustomerSnapshot', N'@SyncedAtUtc'),
    (N'tb_service.CompleteSageSyncWork', N'@WorkId'),
    (N'tb_service.CompleteSageSyncWork', N'@LeaseId'),
    (N'tb_service.CompleteSageSyncWork', N'@WorkerId'),
    (N'tb_service.CompleteSageSyncWork', N'@Succeeded'),
    (N'tb_service.CompleteSageSyncWork', N'@Message'),
    (N'tb_app.AdminBeginUserPreview', N'@TargetLoginName'),
    (N'tb_app.AdminBeginUserPreview', N'@ClientInstanceId'),
    (N'tb_app.ActivateReadOnlyPreview', N'@PreviewSessionId'),
    (N'tb_app.AdminEndUserPreview', N'@PreviewSessionId');

IF EXISTS
(
    SELECT 1 FROM @RequiredParameters AS required
    WHERE NOT EXISTS
    (
        SELECT 1 FROM sys.parameters AS parameter_row
        WHERE parameter_row.[object_id] = OBJECT_ID(required.[ProcedureName], N'P')
          AND parameter_row.[name] = required.[ParameterName]
    )
)
BEGIN
    PRINT N'FAIL: the V0007 procedure parameter contract is incomplete.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredAdminProcedures TABLE ([ObjectName] nvarchar(300) NOT NULL PRIMARY KEY);
INSERT INTO @RequiredAdminProcedures([ObjectName]) VALUES
    (N'tb_app.AdminRequestSageSync'),
    (N'tb_app.GetSageSyncStatus'),
    (N'tb_app.AdminListPreviewUsers'),
    (N'tb_app.AdminBeginUserPreview'),
    (N'tb_app.ActivateReadOnlyPreview'),
    (N'tb_app.AdminEndUserPreview');

IF EXISTS
(
    SELECT 1 FROM @RequiredAdminProcedures AS required
    WHERE NOT EXISTS
    (
        SELECT 1 FROM sys.database_permissions AS permission_row
        WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_role_admin')
          AND permission_row.[class] = 1
          AND permission_row.[major_id] = OBJECT_ID(required.[ObjectName], N'P')
          AND permission_row.[permission_name] = N'EXECUTE'
          AND permission_row.[state] IN (N'G', N'W')
    )
)
BEGIN
    PRINT N'FAIL: a required V0007 Admin EXECUTE grant is missing.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1 FROM sys.database_permissions AS permission_row
    WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_role_admin')
      AND permission_row.[class] = 4
      AND permission_row.[major_id] = DATABASE_PRINCIPAL_ID(N'tb_preview_reader')
      AND permission_row.[permission_name] = N'IMPERSONATE'
      AND permission_row.[state] IN (N'G', N'W')
)
BEGIN
    PRINT N'FAIL: the Admin role lacks the narrow preview-reader IMPERSONATE grant.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1 FROM sys.database_permissions AS permission_row
    INNER JOIN sys.database_principals AS grantee
        ON grantee.[principal_id] = permission_row.[grantee_principal_id]
    WHERE permission_row.[class] = 4
      AND permission_row.[major_id] = DATABASE_PRINCIPAL_ID(N'tb_preview_reader')
      AND permission_row.[permission_name] = N'IMPERSONATE'
      AND permission_row.[state] IN (N'G', N'W')
      AND grantee.[name] <> N'tb_role_admin'
)
BEGIN
    PRINT N'FAIL: a principal other than the Admin role can impersonate the preview reader.';
    SET @FailureCount += 1;
END;

DECLARE @LegacySageAdminProcedures TABLE ([ObjectName] nvarchar(300) NOT NULL PRIMARY KEY);
INSERT INTO @LegacySageAdminProcedures([ObjectName]) VALUES
    (N'tb_app.AcquireSyncLease'),
    (N'tb_app.ReleaseSyncLease'),
    (N'tb_app.BeginSyncRun'),
    (N'tb_app.CompleteSyncRun'),
    (N'tb_app.SyncUpsertClient'),
    (N'tb_app.SyncUpsertSageCustomer'),
    (N'tb_app.SyncRemoveStaleSageCustomers'),
    (N'tb_app.SyncUpsertClientExternalIdentity'),
    (N'tb_app.SyncApplySageCustomerSnapshot'),
    (N'tb_app.SyncApplyClientSnapshot');

IF EXISTS
(
    SELECT 1 FROM @LegacySageAdminProcedures AS legacy
    INNER JOIN sys.database_permissions AS permission_row
        ON permission_row.[class] = 1
       AND permission_row.[major_id] = OBJECT_ID(legacy.[ObjectName], N'P')
       AND permission_row.[permission_name] = N'EXECUTE'
       AND permission_row.[state] IN (N'G', N'W')
    WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_role_admin')
)
BEGIN
    PRINT N'FAIL: Admin retains a legacy workstation-side Sage ingestion grant.';
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
    (N'tb_service.CompleteWhdSyncWork'),
    (N'tb_service.GetSageSyncConfiguration'),
    (N'tb_service.ClaimSageSyncWork'),
    (N'tb_service.RenewSageSyncLease'),
    (N'tb_service.ApplySageCustomerSnapshot'),
    (N'tb_service.CompleteSageSyncWork');

IF EXISTS
(
    SELECT 1 FROM @ServiceProcedures AS required
    WHERE NOT EXISTS
    (
        SELECT 1 FROM sys.database_permissions AS permission_row
        WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_role_sync_service')
          AND permission_row.[class] = 1
          AND permission_row.[major_id] = OBJECT_ID(required.[ObjectName], N'P')
          AND permission_row.[permission_name] = N'EXECUTE'
          AND permission_row.[state] IN (N'G', N'W')
    )
)
BEGIN
    PRINT N'FAIL: a required WHD/Sage service EXECUTE grant is missing.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1 FROM sys.database_permissions AS permission_row
    WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_role_sync_service')
      AND permission_row.[state] IN (N'G', N'W')
      AND
      (
          permission_row.[permission_name] IN
              (N'SELECT', N'INSERT', N'UPDATE', N'DELETE', N'ALTER', N'CONTROL', N'TAKE OWNERSHIP', N'IMPERSONATE')
          OR
          (
              permission_row.[permission_name] = N'EXECUTE'
              AND
              (
                  permission_row.[class] <> 1
                  OR NOT EXISTS
                  (
                      SELECT 1 FROM @ServiceProcedures AS allowed
                      WHERE OBJECT_ID(allowed.[ObjectName], N'P') = permission_row.[major_id]
                  )
              )
          )
      )
)
BEGIN
    PRINT N'FAIL: the sync service role has direct data/control or unexpected execution grants.';
    SET @FailureCount += 1;
END;

DECLARE @PreviewReadProcedures TABLE ([ObjectName] nvarchar(300) NOT NULL PRIMARY KEY);
INSERT INTO @PreviewReadProcedures([ObjectName]) VALUES
    (N'tb_app.GetCurrentUserContext'),
    (N'tb_app.GetRepositoryCapabilities'),
    (N'tb_app.SearchClients'),
    (N'tb_app.GetClient'),
    (N'tb_app.SearchTickets'),
    (N'tb_app.GetTicket'),
    (N'tb_app.GetTicketStatusOptions'),
    (N'tb_app.SearchWorkEntries'),
    (N'tb_app.GetWorkEntry'),
    (N'tb_app.GetWorkEntryLinks'),
    (N'tb_app.GetDistinctTags'),
    (N'tb_app.GetTemplates'),
    (N'tb_app.GetCommonLinks'),
    (N'tb_app.GetSettings'),
    (N'tb_app.GetPostingLogs');

IF EXISTS
(
    SELECT 1 FROM @PreviewReadProcedures AS required
    WHERE NOT EXISTS
    (
        SELECT 1 FROM sys.database_permissions AS permission_row
        WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_preview_reader')
          AND permission_row.[class] = 1
          AND permission_row.[major_id] = OBJECT_ID(required.[ObjectName], N'P')
          AND permission_row.[permission_name] = N'EXECUTE'
          AND permission_row.[state] IN (N'G', N'W')
    )
)
BEGIN
    PRINT N'FAIL: a required preview-safe read EXECUTE grant is missing.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1 FROM sys.database_permissions AS permission_row
    WHERE permission_row.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_preview_reader')
      AND permission_row.[state] IN (N'G', N'W')
      AND NOT
      (
          (
              permission_row.[class] = 0
              AND permission_row.[major_id] = 0
              AND permission_row.[minor_id] = 0
              AND permission_row.[permission_name] = N'CONNECT'
          )
          OR
          (
              permission_row.[class] = 1
              AND permission_row.[minor_id] = 0
              AND permission_row.[permission_name] = N'EXECUTE'
              AND EXISTS
              (
                  SELECT 1 FROM @PreviewReadProcedures AS allowed
                  WHERE OBJECT_ID(allowed.[ObjectName], N'P') = permission_row.[major_id]
              )
          )
      )
)
BEGIN
    PRINT N'FAIL: the preview reader has data/control or unexpected execution grants.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1 FROM sys.database_role_members
    WHERE [member_principal_id] = DATABASE_PRINCIPAL_ID(N'tb_preview_reader')
)
BEGIN
    PRINT N'FAIL: the preview reader must not be a member of any database role.';
    SET @FailureCount += 1;
END;

DECLARE @EnsureDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_security.EnsureCurrentUser', N'P'));
DECLARE @ContextDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetCurrentUserContext', N'P'));
DECLARE @ListPreviewDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminListPreviewUsers', N'P'));
DECLARE @BeginPreviewDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminBeginUserPreview', N'P'));
DECLARE @ActivatePreviewDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.ActivateReadOnlyPreview', N'P'));
DECLARE @TicketAccessDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_security.FilterWhdTicketAccess', N'IF'));
DECLARE @SearchWorkDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SearchWorkEntries', N'P'));
DECLARE @GetWorkDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetWorkEntry', N'P'));
DECLARE @SettingsDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetSettings', N'P'));
DECLARE @PostingLogsDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetPostingLogs', N'P'));

SELECT @EnsureDefinition = REPLACE(REPLACE(REPLACE(@EnsureDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @ContextDefinition = REPLACE(REPLACE(REPLACE(@ContextDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @ListPreviewDefinition = REPLACE(REPLACE(REPLACE(@ListPreviewDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @BeginPreviewDefinition = REPLACE(REPLACE(REPLACE(@BeginPreviewDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @ActivatePreviewDefinition = REPLACE(REPLACE(REPLACE(@ActivatePreviewDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @TicketAccessDefinition = REPLACE(REPLACE(REPLACE(@TicketAccessDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @SearchWorkDefinition = REPLACE(REPLACE(REPLACE(@SearchWorkDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @GetWorkDefinition = REPLACE(REPLACE(REPLACE(@GetWorkDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @SettingsDefinition = REPLACE(REPLACE(REPLACE(@SettingsDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @PostingLogsDefinition = REPLACE(REPLACE(REPLACE(@PostingLogsDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');

IF CHARINDEX(N'SESSION_CONTEXT(N''TechBench.PreviewSessionId'')', @EnsureDefinition) = 0
   OR CHARINDEX(N'IFUSER_NAME()=N''tb_preview_reader''', @EnsureDefinition) = 0
   OR CHARINDEX(N'AdminUserPreviewSessions', @EnsureDefinition) = 0
   OR CHARINDEX(N'target_user.[IsAdmin]=0', @EnsureDefinition) = 0
   OR CHARINDEX(N'target_user.[LastSeenAtUtc]>=DATEADD(hour,-1,SYSUTCDATETIME())', @EnsureDefinition) = 0
BEGIN
    PRINT N'FAIL: EnsureCurrentUser does not securely resolve the server-issued preview target.';
    SET @FailureCount += 1;
END;

DECLARE @RoleRefreshPosition int = CHARINDEX(N'UPDATE[tb_security].[Users]WITH(UPDLOCK,HOLDLOCK)', @EnsureDefinition);
DECLARE @ZeroRoleThrowPosition int = CHARINDEX(N'IF@HasApplicationRole=0THROW51002', @EnsureDefinition);
IF @RoleRefreshPosition = 0
   OR @ZeroRoleThrowPosition <= @RoleRefreshPosition
   OR CHARINDEX(N'[IsTechnician]=@IsTechnician', @EnsureDefinition) = 0
   OR CHARINDEX(N'[IsManager]=@IsManager', @EnsureDefinition) = 0
   OR CHARINDEX(N'[IsAdmin]=@IsAdmin', @EnsureDefinition) = 0
   OR CHARINDEX(N'[IsSyncOperator]=@IsSyncOperator', @EnsureDefinition) = 0
BEGIN
    PRINT N'FAIL: EnsureCurrentUser does not persist refreshed zero role flags before denying access.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'AuthenticatedUserSid', @ContextDefinition) = 0
   OR CHARINDEX(N'IsReadOnlyPreview', @ContextDefinition) = 0
   OR CHARINDEX(N'PreviewSessionId', @ContextDefinition) = 0
   OR CHARINDEX(N'PreviewExpiresAtUtc', @ContextDefinition) = 0
BEGIN
    PRINT N'FAIL: GetCurrentUserContext lacks authenticated/preview context fields.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'IS_ROLEMEMBER(N''tb_role_admin'')<>1', @BeginPreviewDefinition) = 0
   OR CHARINDEX(N'[IsTechnician]=1', @BeginPreviewDefinition) = 0
   OR CHARINDEX(N'[IsAdmin]=0', @BeginPreviewDefinition) = 0
   OR CHARINDEX(N'[LastSeenAtUtc]>=DATEADD(hour,-1,@Now)', @BeginPreviewDefinition) = 0
   OR CHARINDEX(N'DATEADD(minute,30,@Now)', @BeginPreviewDefinition) = 0
BEGIN
    PRINT N'FAIL: AdminBeginUserPreview lacks live Admin, target, or expiry validation.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'sp_set_session_context', @ActivatePreviewDefinition) = 0
   OR CHARINDEX(N'@read_only=1', @ActivatePreviewDefinition) = 0
   OR CHARINDEX(N'IS_ROLEMEMBER(N''tb_role_admin'')<>1', @ActivatePreviewDefinition) = 0
   OR CHARINDEX(N'target_user.[LastSeenAtUtc]>=DATEADD(hour,-1,SYSUTCDATETIME())', @ActivatePreviewDefinition) = 0
BEGIN
    PRINT N'FAIL: ActivateReadOnlyPreview does not set a read-only server-issued context after live Admin validation.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'[LastSeenAtUtc]>=DATEADD(hour,-1,SYSUTCDATETIME())', @ListPreviewDefinition) = 0
BEGIN
    PRINT N'FAIL: AdminListPreviewUsers includes authorization records older than one hour.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'USER_NAME()=N''tb_preview_reader''', @TicketAccessDefinition) = 0
   OR CHARINDEX(N'USER_NAME()<>N''tb_preview_reader''', @TicketAccessDefinition) = 0
   OR CHARINDEX(N'SESSION_CONTEXT(N''TechBench.PreviewSessionId'')ISNULL', @TicketAccessDefinition) = 0
   OR CHARINDEX(N'AdminUserPreviewSessions', @TicketAccessDefinition) = 0
   OR CHARINDEX(N'mapping.[WindowsSid]=preview_session.[TargetWindowsSid]', @TicketAccessDefinition) = 0
   OR CHARINDEX(N'target_user.[LastSeenAtUtc]>=DATEADD(hour,-1,SYSUTCDATETIME())', @TicketAccessDefinition) = 0
BEGIN
    PRINT N'FAIL: WHD row security does not prevent the authenticated Admin bypass from winning in preview.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'@IsReadOnlyPreview=0', @SearchWorkDefinition) = 0
   OR CHARINDEX(N'@IsReadOnlyPreview=0', @GetWorkDefinition) = 0
   OR CHARINDEX(N'@IsReadOnlyPreview=0', @SettingsDefinition) = 0
   OR CHARINDEX(N'WHEN@IsReadOnlyPreview=1THENNULLELSEwork_entry.[LastError]ENDAS[LastError]', @SearchWorkDefinition) = 0
   OR CHARINDEX(N'WHEN@IsReadOnlyPreview=1THENNULLELSEwork_entry.[LastError]ENDAS[LastError]', @GetWorkDefinition) = 0
BEGIN
    PRINT N'FAIL: preview-safe reads do not mask personal notes, posting errors, or user-owned settings.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'WHEN@IsReadOnlyPreview=1THENN''''ELSEposting_log.[Payload]', @PostingLogsDefinition) = 0
   OR CHARINDEX(N'WHEN@IsReadOnlyPreview=0THENposting_log.[Message]', @PostingLogsDefinition) = 0
   OR CHARINDEX(N'@IsReadOnlyPreview=0AND(posting_log.[Message]LIKE@KeywordPatternORposting_log.[Payload]LIKE@KeywordPattern)', @PostingLogsDefinition) = 0
BEGIN
    PRINT N'FAIL: preview-safe posting history does not redact payload/message content or block keyword inference.';
    SET @FailureCount += 1;
END;

DECLARE @RequestSageDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminRequestSageSync', N'P'));
DECLARE @ClaimSageDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_service.ClaimSageSyncWork', N'P'));
DECLARE @ApplySageDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_service.ApplySageCustomerSnapshot', N'P'));
DECLARE @CompleteSageDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_service.CompleteSageSyncWork', N'P'));
DECLARE @SageConfigDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_service.GetSageSyncConfiguration', N'P'));
SELECT @RequestSageDefinition = REPLACE(REPLACE(REPLACE(@RequestSageDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @ClaimSageDefinition = REPLACE(REPLACE(REPLACE(@ClaimSageDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @ApplySageDefinition = REPLACE(REPLACE(REPLACE(@ApplySageDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @CompleteSageDefinition = REPLACE(REPLACE(REPLACE(@CompleteSageDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
SELECT @SageConfigDefinition = REPLACE(REPLACE(REPLACE(@SageConfigDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');

IF CHARINDEX(N'IS_ROLEMEMBER(N''tb_role_admin'')<>1', @RequestSageDefinition) = 0
   OR CHARINDEX(N'sp_getapplock', @RequestSageDefinition) = 0
   OR CHARINDEX(N'INSERTINTO[tb_sync].[SageSyncRequests]', @RequestSageDefinition) = 0
   OR CHARINDEX(N'@AllowLargeRemovalbit=0', @RequestSageDefinition) = 0
   OR CHARINDEX(N'@ConfirmedRequestIduniqueidentifier=NULL', @RequestSageDefinition) = 0
   OR CHARINDEX(N'[AllowLargeRemoval]', @RequestSageDefinition) = 0
   OR CHARINDEX(N'[RequiresLargeRemovalConfirmation]=1', @RequestSageDefinition) = 0
   OR CHARINDEX(N'[CompletedAtUtc]>=DATEADD(hour,-1,@Now)', @RequestSageDefinition) = 0
BEGIN
    PRINT N'FAIL: the Admin-only Sage request queue is incomplete.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'sp_getapplock', @ClaimSageDefinition) = 0
   OR CHARINDEX(N'READCOMMITTEDLOCK', @ClaimSageDefinition) = 0
   OR CHARINDEX(N'INSERTINTO[tb_sync].[SageSyncRequests]', @ClaimSageDefinition) > 0
BEGIN
    PRINT N'FAIL: ClaimSageSyncWork is not a manual-queue-only, RCSI-safe lease claim.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'COALESCE(ISJSON(@Json),0)<>1', @ApplySageDefinition) = 0
   OR CHARINDEX(N'@ReadCount=0', @ApplySageDefinition) = 0
   OR CHARINDEX(N'SageSyncLeases', @ApplySageDefinition) = 0
   OR CHARINDEX(N'BEGINTRANSACTION', @ApplySageDefinition) = 0
   OR CHARINDEX(N'ClientExternalIdentities', @ApplySageDefinition) = 0
   OR CHARINDEX(N'@RawSnapshotTABLE', @ApplySageDefinition) = 0
   OR CHARINDEX(N'[JsonType]<>5', @ApplySageDefinition) = 0
   OR CHARINDEX(N'[CustomerIdCount]<>1', @ApplySageDefinition) = 0
   OR CHARINDEX(N'LEN(LTRIM(RTRIM([CustomerId])))>120', @ApplySageDefinition) = 0
   OR CHARINDEX(N'HAVINGCOUNT(*)>1', @ApplySageDefinition) = 0
   OR CHARINDEX(N'@ConfirmationMatches<>1', @ApplySageDefinition) = 0
   OR CHARINDEX(N'@ExistingCount>=20', @ApplySageDefinition) = 0
   OR CHARINDEX(N'@StaleCount>=10', @ApplySageDefinition) = 0
   OR CHARINDEX(N'confirmed_request.[ExistingCount]=@ExistingCount', @ApplySageDefinition) = 0
   OR CHARINDEX(N'confirmed_request.[ReadCount]=@ReadCount', @ApplySageDefinition) = 0
   OR CHARINDEX(N'confirmed_request.[StaleCount]=@StaleCount', @ApplySageDefinition) = 0
   OR CHARINDEX(N'[RequiresLargeRemovalConfirmation]=1', @ApplySageDefinition) = 0
BEGIN
    PRINT N'FAIL: ApplySageCustomerSnapshot lacks lossless validation, destructive-delta confirmation, lease enforcement, or atomic identity reconciliation.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'@Succeeded=1AND@ReadCount=0', @CompleteSageDefinition) = 0
   OR CHARINDEX(N'[tb_sync].[SageSyncHealth]', @CompleteSageDefinition) = 0
   OR CHARINDEX(N'DELETEFROM[tb_sync].[SageSyncLeases]', @CompleteSageDefinition) = 0
BEGIN
    PRINT N'FAIL: CompleteSageSyncWork lacks apply-before-success, health, or lease completion safeguards.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'Sage.SyncDsn', @SageConfigDefinition) = 0
   OR CHARINDEX(N'Sage.SyncUsername', @SageConfigDefinition) = 0
BEGIN
    PRINT N'FAIL: the service-owned Sage configuration contract is incomplete.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'CONVERT(int,7)', REPLACE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities')), N' ', N'')) = 0
BEGIN
    PRINT N'FAIL: GetRepositoryCapabilities does not report schema version 7.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS (SELECT 1 FROM [tb_sync].[SageSyncHealth] WHERE [HealthId] = 1)
BEGIN
    PRINT N'FAIL: the Sage synchronization health singleton is missing.';
    SET @FailureCount += 1;
END;

IF @FailureCount > 0
BEGIN
    RAISERROR(
        N'TechBench V0007 server-owned Sage and Admin-preview verification failed with %d issue(s).',
        16, 1, @FailureCount);
    RETURN;
END;

PRINT N'TechBench V0007 server-owned Sage and Admin-preview verification passed.';
SELECT
    DB_NAME() AS [DatabaseName],
    MAX([SchemaVersion]) AS [SchemaVersion],
    MAX(CASE
        WHEN [MigrationId] = N'SqlServer2016.ServerOwnedSageAndAdminPreview.0007'
            THEN [AppliedAtUtc]
        END) AS [ServerOwnedSageAndAdminPreviewAppliedAtUtc]
FROM [tb_deploy].[SchemaMigrations];
GO

-- ============================================================================
-- END 96-V0007-ServerOwnedSageAndAdminPreviewVerify.sql
-- ============================================================================

PRINT N'TechBench deployment completed successfully on CSRI-SQL.';
GO
