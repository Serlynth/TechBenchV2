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
                CHECK ([RequestType] IN (N'Full', N'Incremental', N'Technicians')),
            CONSTRAINT [CK_WhdRequests_Status]
                CHECK ([Status] IN (N'Queued', N'Running', N'Completed', N'Failed'))
        );
    END;

    IF OBJECT_ID(N'tb_sync.WhdSyncRequests', N'U') IS NOT NULL
       AND EXISTS
       (
           SELECT 1
           FROM sys.check_constraints
           WHERE [parent_object_id] = OBJECT_ID(N'tb_sync.WhdSyncRequests')
             AND [name] = N'CK_WhdRequests_Type'
             AND [definition] NOT LIKE N'%Technicians%'
       )
    BEGIN
        ALTER TABLE [tb_sync].[WhdSyncRequests]
            DROP CONSTRAINT [CK_WhdRequests_Type];
        ALTER TABLE [tb_sync].[WhdSyncRequests] WITH CHECK
            ADD CONSTRAINT [CK_WhdRequests_Type]
            CHECK ([RequestType] IN (N'Full', N'Incremental', N'Technicians'));
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

    /*
        Keep the last canonical owner of a WHD location after that location
        disappears from a complete snapshot. The active identity row can then
        be removed without losing the deterministic path back to the same live
        TechBench client if WHD later returns the location again.
    */
    IF OBJECT_ID(N'tb_sync.WhdClientIdentityHistory', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_sync].[WhdClientIdentityHistory]
        (
            [ExternalId] nvarchar(500) NOT NULL,
            [ClientId] int NOT NULL,
            [ExternalName] nvarchar(240) NULL,
            [LastSeenAtUtc] datetime2(3) NULL,
            [RetiredAtUtc] datetime2(3) NOT NULL,
            [UpdatedByWindowsSid] varbinary(85) NOT NULL,
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_WhdClientIdentityHistory_Updated]
                DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT [PK_WhdClientIdentityHistory]
                PRIMARY KEY NONCLUSTERED ([ExternalId]),
            CONSTRAINT [FK_WhdClientIdentityHistory_Client]
                FOREIGN KEY ([ClientId]) REFERENCES [tb_data].[Clients]([Id])
                ON DELETE CASCADE,
            CONSTRAINT [FK_WhdClientIdentityHistory_UpdatedBy]
                FOREIGN KEY ([UpdatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid])
        );
    END;

    /* SQL Server 2016 limits clustered keys to 900 bytes. ExternalId keeps
       its established nvarchar(500) contract, while the 1,000-byte key uses
       the SQL Server 2016 nonclustered-key limit instead. Upgrade databases
       that received the original clustered definition before a later batch
       stopped the deployment. */
    IF EXISTS
    (
        SELECT 1
        FROM sys.key_constraints AS key_constraint
        INNER JOIN sys.indexes AS key_index
            ON key_index.[object_id] = key_constraint.[parent_object_id]
           AND key_index.[index_id] = key_constraint.[unique_index_id]
        WHERE key_constraint.[parent_object_id]
                = OBJECT_ID(N'tb_sync.WhdClientIdentityHistory', N'U')
          AND key_constraint.[name] = N'PK_WhdClientIdentityHistory'
          AND key_index.[type_desc] = N'CLUSTERED'
    )
    BEGIN
        ALTER TABLE [tb_sync].[WhdClientIdentityHistory]
            DROP CONSTRAINT [PK_WhdClientIdentityHistory];
        ALTER TABLE [tb_sync].[WhdClientIdentityHistory]
            ADD CONSTRAINT [PK_WhdClientIdentityHistory]
                PRIMARY KEY NONCLUSTERED ([ExternalId]);
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_sync.WhdClientIdentityHistory')
          AND [name] = N'IX_WhdClientIdentityHistory_Client'
    )
        CREATE INDEX [IX_WhdClientIdentityHistory_Client]
            ON [tb_sync].[WhdClientIdentityHistory]([ClientId]);

    /*
        An unexpectedly destructive complete WHD client snapshot must be
        observed twice with the same missing-location set before identities
        are retired. Keeping the pending set in SQL makes that confirmation
        durable across separate sync work items and service restarts.
    */
    IF OBJECT_ID(N'tb_sync.WhdPendingClientRemovals', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_sync].[WhdPendingClientRemovals]
        (
            [ExternalId] nvarchar(500) NOT NULL,
            [ExistingCount] int NOT NULL,
            [IncomingCount] int NOT NULL,
            [StaleCount] int NOT NULL,
            [FirstObservedAtUtc] datetime2(3) NOT NULL,
            [UpdatedByWindowsSid] varbinary(85) NOT NULL,
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_WhdPendingClientRemovals_Updated]
                DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT [PK_WhdPendingClientRemovals]
                PRIMARY KEY NONCLUSTERED ([ExternalId]),
            CONSTRAINT [CK_WhdPendingClientRemovals_Counts]
                CHECK
                (
                    [ExistingCount] > 0
                    AND [IncomingCount] >= 0
                    AND [StaleCount] > 0
                    AND [StaleCount] <= [ExistingCount]
                ),
            CONSTRAINT [FK_WhdPendingClientRemovals_UpdatedBy]
                FOREIGN KEY ([UpdatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid])
        );
    END;

    IF EXISTS
    (
        SELECT 1
        FROM sys.key_constraints AS key_constraint
        INNER JOIN sys.indexes AS key_index
            ON key_index.[object_id] = key_constraint.[parent_object_id]
           AND key_index.[index_id] = key_constraint.[unique_index_id]
        WHERE key_constraint.[parent_object_id]
                = OBJECT_ID(N'tb_sync.WhdPendingClientRemovals', N'U')
          AND key_constraint.[name] = N'PK_WhdPendingClientRemovals'
          AND key_index.[type_desc] = N'CLUSTERED'
    )
    BEGIN
        ALTER TABLE [tb_sync].[WhdPendingClientRemovals]
            DROP CONSTRAINT [PK_WhdPendingClientRemovals];
        ALTER TABLE [tb_sync].[WhdPendingClientRemovals]
            ADD CONSTRAINT [PK_WhdPendingClientRemovals]
                PRIMARY KEY NONCLUSTERED ([ExternalId]);
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
