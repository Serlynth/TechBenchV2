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

        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
            @EntityType = N'Ticket',
            @EntityId = CONVERT(nvarchar(120), @Id),
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

    IF @IncludeAllUsers = 1 AND @IsManager <> 1 AND @IsAdmin <> 1
        THROW 51120, N'Only a Manager or Admin may read organization-wide tags.', 1;

    SELECT DISTINCT
        LTRIM(RTRIM(tag.[value])) AS [Tag]
    FROM [tb_data].[WorkEntries] AS work_entry
    CROSS APPLY STRING_SPLIT(work_entry.[Tags], N',') AS tag
    WHERE (@IncludeAllUsers = 1 OR work_entry.[OwnerWindowsSid] = @UserSid)
      AND NULLIF(LTRIM(RTRIM(tag.[value])), N'') IS NOT NULL
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

        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
            @EntityType = N'WorkEntry',
            @EntityId = CONVERT(nvarchar(120), @Id),
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

        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'WorkEntryDeleted',
            @EntityType = N'WorkEntry',
            @EntityId = CONVERT(nvarchar(120), @Id),
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

        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'WorkEntryLinkSaved',
            @EntityType = N'WorkEntryLink',
            @EntityId = CONVERT(nvarchar(120), @Id),
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

    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'WorkEntryLinkDeleted',
        @EntityType = N'WorkEntryLink',
        @EntityId = CONVERT(nvarchar(120), @Id),
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

        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
            @EntityType = N'Template',
            @EntityId = CONVERT(nvarchar(120), @Id),
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

    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'TemplateDeleted',
        @EntityType = N'Template',
        @EntityId = CONVERT(nvarchar(120), @Id),
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

        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'CommonLinkSaved',
            @EntityType = N'CommonLink',
            @EntityId = CONVERT(nvarchar(120), @Id),
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

    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'CommonLinkDeleted',
        @EntityType = N'CommonLink',
        @EntityId = CONVERT(nvarchar(120), @Id),
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

        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'ClientAliasSaved',
            @EntityType = N'ClientAlias',
            @EntityId = CONVERT(nvarchar(120), @Id),
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

    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'ClientAliasDeleted',
        @EntityType = N'ClientAlias',
        @EntityId = CONVERT(nvarchar(120), @Id),
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

        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'ClientMerged',
            @EntityType = N'Client',
            @EntityId = CONVERT(nvarchar(120), @WhdClientId),
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

        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'ClientAliasDeleted',
            @EntityType = N'ClientAlias',
            @EntityId = CONVERT(nvarchar(120), @Id),
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

        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'PostingAttemptCompleted',
            @EntityType = N'PostingAttempt',
            @EntityId = CONVERT(nvarchar(120), @AttemptId);

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

        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'WorkEntryManuallyMarkedPosted',
            @EntityType = N'WorkEntry',
            @EntityId = CONVERT(nvarchar(120), @WorkEntryId),
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

    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'ExternalClientMappingSaved',
        @EntityType = N'Client',
        @EntityId = CONVERT(nvarchar(120), @ClientId),
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
        INSERT INTO @StatusResult
        EXEC [tb_app].[SyncUpsertTicketStatusOption]
            @Name = @Name,
            @Source = N'WHD',
            @ExternalId = CONVERT(nvarchar(240), @Id),
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

    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'ImportBatchStarted',
        @EntityType = N'ImportBatch',
        @EntityId = CONVERT(nvarchar(120), @BatchId),
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

IF
(
    SELECT MAX([SchemaVersion])
    FROM [tb_deploy].[SchemaMigrations]
) <> 2
BEGIN
    PRINT N'FAIL: The installed TechBench schema version is not 2.';
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

IF CHARINDEX(
       N'AND [BuiltInKey] IS NOT NULL',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveCommonLink'))) = 0
   OR CHARINDEX(
       N'AND [BuiltInKey] IS NOT NULL',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteCommonLink'))) = 0
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
    (N'tb_role_user', N'tb_app.EnsureWorkspaceDefaults'),
    (N'tb_role_user', N'tb_app.SearchTickets'),
    (N'tb_role_user', N'tb_app.SaveTicket'),
    (N'tb_role_user', N'tb_app.SearchWorkEntries'),
    (N'tb_role_user', N'tb_app.SaveWorkEntry'),
    (N'tb_role_user', N'tb_app.GetEditorDraft'),
    (N'tb_role_user', N'tb_app.SaveTemplate'),
    (N'tb_role_user', N'tb_app.SaveCommonLink'),
    (N'tb_role_user', N'tb_app.SaveClientAlias'),
    (N'tb_role_user', N'tb_app.SaveUserSetting'),
    (N'tb_role_user', N'tb_app.GetPostingLogs'),
    (N'tb_role_user', N'tb_app.BeginPostingAttempt'),
    (N'tb_role_user', N'tb_app.MarkWorkEntryPosted'),
    (N'tb_role_user', N'tb_app.BeginImportBatch'),
    (N'tb_role_manager', N'tb_app.SearchWorkEntries'),
    (N'tb_role_admin', N'tb_app.AdminMergeClients'),
    (N'tb_role_admin', N'tb_app.AdminSaveOrganizationSetting'),
    (N'tb_role_sync_operator', N'tb_app.AcquireSyncLease'),
    (N'tb_role_sync_operator', N'tb_app.SyncApplyClientSnapshot'),
    (N'tb_role_sync_operator', N'tb_app.SyncApplyTicketSnapshot'),
    (N'tb_role_sync_operator', N'tb_app.SyncApplyTicketStatusSnapshot'),
    (N'tb_role_sync_operator', N'tb_app.SyncApplySageCustomerSnapshot');

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

PRINT N'TechBench deployment completed successfully on CSRI-SQL.';
GO
