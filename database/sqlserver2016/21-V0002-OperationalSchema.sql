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
