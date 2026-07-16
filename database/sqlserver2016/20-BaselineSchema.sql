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
