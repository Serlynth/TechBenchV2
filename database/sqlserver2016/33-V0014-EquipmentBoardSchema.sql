:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.WhdClientContactDetails.0013'
      AND [SchemaVersion] = 13
)
BEGIN
    RAISERROR(N'V0013 must be installed before equipment-board schema version 14.', 16, 1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    IF SCHEMA_ID(N'tb_inventory') IS NULL
        EXEC(N'CREATE SCHEMA [tb_inventory] AUTHORIZATION [dbo];');

    IF OBJECT_ID(N'tb_inventory.ClientUsers', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_inventory].[ClientUsers]
        (
            [ClientUserId] bigint IDENTITY(1,1) NOT NULL,
            [ClientId] int NOT NULL,
            [DisplayName] nvarchar(240) NOT NULL,
            [RoleDepartment] nvarchar(240) NULL,
            [Email] nvarchar(320) NULL,
            [Phone] nvarchar(80) NULL,
            [LocationName] nvarchar(240) NULL,
            [SourceSystem] nvarchar(80) NOT NULL
                CONSTRAINT [DF_ClientUsers_SourceSystem]
                DEFAULT (N'CredentialsWorkbook'),
            [SourceKey] nvarchar(500) NULL,
            [SourceRowHash] binary(32) NULL,
            [IsActive] bit NOT NULL
                CONSTRAINT [DF_ClientUsers_IsActive] DEFAULT (1),
            [LastSyncedAtUtc] datetime2(3) NULL,
            [CreatedByWindowsSid] varbinary(85) NOT NULL,
            [UpdatedByWindowsSid] varbinary(85) NOT NULL,
            [CreatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientUsers_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientUsers_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ClientUsers]
                PRIMARY KEY CLUSTERED ([ClientUserId]),
            CONSTRAINT [FK_ClientUsers_Client]
                FOREIGN KEY ([ClientId])
                REFERENCES [tb_data].[Clients]([Id]),
            CONSTRAINT [FK_ClientUsers_CreatedBy]
                FOREIGN KEY ([CreatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_ClientUsers_UpdatedBy]
                FOREIGN KEY ([UpdatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_ClientUsers_DisplayName_NotBlank]
                CHECK (LEN(LTRIM(RTRIM([DisplayName]))) > 0)
        );

        CREATE INDEX [IX_ClientUsers_ClientActiveName]
            ON [tb_inventory].[ClientUsers]
                ([ClientId], [IsActive], [DisplayName], [ClientUserId])
            INCLUDE ([RoleDepartment], [Email], [Phone], [LocationName]);

        CREATE UNIQUE INDEX [UX_ClientUsers_SourceKey]
            ON [tb_inventory].[ClientUsers]([SourceSystem], [SourceKey])
            WHERE [SourceKey] IS NOT NULL;
    END;

    IF COL_LENGTH(N'tb_inventory.ClientUsers', N'SourceRowHash') IS NULL
        ALTER TABLE [tb_inventory].[ClientUsers]
            ADD [SourceRowHash] binary(32) NULL;

    IF OBJECT_ID(N'tb_inventory.ClientUserAccounts', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_inventory].[ClientUserAccounts]
        (
            [ClientUserAccountId] bigint IDENTITY(1,1) NOT NULL,
            [ClientUserId] bigint NOT NULL,
            [AccountSystem] nvarchar(240) NOT NULL,
            [SourceKey] nvarchar(500) NOT NULL,
            [SourceRowHash] binary(32) NOT NULL,
            [SourceModifiedAtUtc] datetime2(3) NULL,
            [LastSyncedAtUtc] datetime2(3) NULL,
            [IsCurrent] bit NOT NULL
                CONSTRAINT [DF_ClientUserAccounts_IsCurrent] DEFAULT (1),
            [CreatedByWindowsSid] varbinary(85) NOT NULL,
            [UpdatedByWindowsSid] varbinary(85) NOT NULL,
            [CreatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientUserAccounts_CreatedAtUtc]
                DEFAULT (SYSUTCDATETIME()),
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientUserAccounts_UpdatedAtUtc]
                DEFAULT (SYSUTCDATETIME()),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ClientUserAccounts]
                PRIMARY KEY CLUSTERED ([ClientUserAccountId]),
            CONSTRAINT [UX_ClientUserAccounts_SourceKey]
                UNIQUE ([SourceKey]),
            CONSTRAINT [FK_ClientUserAccounts_ClientUser]
                FOREIGN KEY ([ClientUserId])
                REFERENCES [tb_inventory].[ClientUsers]([ClientUserId]),
            CONSTRAINT [FK_ClientUserAccounts_CreatedBy]
                FOREIGN KEY ([CreatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_ClientUserAccounts_UpdatedBy]
                FOREIGN KEY ([UpdatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_ClientUserAccounts_System_NotBlank]
                CHECK (LEN(LTRIM(RTRIM([AccountSystem]))) > 0),
            CONSTRAINT [CK_ClientUserAccounts_SourceKey_NotBlank]
                CHECK (LEN(LTRIM(RTRIM([SourceKey]))) > 0)
        );

        CREATE INDEX [IX_ClientUserAccounts_UserCurrent]
            ON [tb_inventory].[ClientUserAccounts]
                ([ClientUserId], [IsCurrent], [AccountSystem], [ClientUserAccountId]);
    END;

    IF OBJECT_ID(N'tb_inventory.ClientUserAccountFields', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_inventory].[ClientUserAccountFields]
        (
            [ClientUserAccountId] bigint NOT NULL,
            [FieldKey] nvarchar(200) NOT NULL,
            [FieldLabel] nvarchar(200) NOT NULL,
            [SortOrder] int NOT NULL,
            [ValueEncrypted] varbinary(max) NULL,
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ClientUserAccountFields]
                PRIMARY KEY CLUSTERED ([ClientUserAccountId], [FieldKey]),
            CONSTRAINT [UQ_ClientUserAccountFields_Order]
                UNIQUE ([ClientUserAccountId], [SortOrder]),
            CONSTRAINT [FK_ClientUserAccountFields_Account]
                FOREIGN KEY ([ClientUserAccountId])
                REFERENCES [tb_inventory].[ClientUserAccounts]([ClientUserAccountId])
                ON DELETE CASCADE,
            CONSTRAINT [CK_ClientUserAccountFields_Key]
                CHECK (LEN(LTRIM(RTRIM([FieldKey]))) > 0),
            CONSTRAINT [CK_ClientUserAccountFields_Label]
                CHECK (LEN(LTRIM(RTRIM([FieldLabel]))) > 0),
            CONSTRAINT [CK_ClientUserAccountFields_Order]
                CHECK ([SortOrder] > 0)
        );
    END;

    IF OBJECT_ID(N'tb_inventory.Equipment', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_inventory].[Equipment]
        (
            [EquipmentId] bigint IDENTITY(1,1) NOT NULL,
            [AssetTag] nvarchar(80) NULL,
            [DeviceType] nvarchar(80) NOT NULL,
            [Name] nvarchar(180) NOT NULL,
            [SerialNumber] nvarchar(120) NULL,
            [PartNumber] nvarchar(120) NULL,
            [IpAddress] nvarchar(80) NULL,
            [Manufacturer] nvarchar(120) NULL,
            [Model] nvarchar(120) NULL,
            [ClientId] int NULL,
            [ClientName] nvarchar(240) NULL,
            [ClientUserId] bigint NULL,
            [LocationName] nvarchar(240) NULL,
            [Notes] nvarchar(max) NULL,
            [WorkflowStage] nvarchar(24) NOT NULL
                CONSTRAINT [DF_Equipment_WorkflowStage] DEFAULT (N'Stock'),
            [AssignedToWindowsSid] varbinary(85) NULL,
            [SortOrder] int NOT NULL
                CONSTRAINT [DF_Equipment_SortOrder] DEFAULT (0),
            [AssignedAtUtc] datetime2(3) NULL,
            [IsArchived] bit NOT NULL
                CONSTRAINT [DF_Equipment_IsArchived] DEFAULT (0),
            [CreatedByWindowsSid] varbinary(85) NOT NULL,
            [UpdatedByWindowsSid] varbinary(85) NOT NULL,
            [CreatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_Equipment_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_Equipment_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_Equipment] PRIMARY KEY CLUSTERED ([EquipmentId]),
            CONSTRAINT [CK_Equipment_DeviceType_NotBlank]
                CHECK (LEN(LTRIM(RTRIM([DeviceType]))) > 0),
            CONSTRAINT [CK_Equipment_Name_NotBlank]
                CHECK (LEN(LTRIM(RTRIM([Name]))) > 0),
            CONSTRAINT [CK_Equipment_WorkflowStage]
                CHECK ([WorkflowStage] IN
                    (N'Stock', N'Assigned', N'Deployment', N'Deployed')),
            CONSTRAINT [FK_Equipment_AssignedUser]
                FOREIGN KEY ([AssignedToWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_Equipment_Client]
                FOREIGN KEY ([ClientId])
                REFERENCES [tb_data].[Clients]([Id]),
            CONSTRAINT [FK_Equipment_ClientUser]
                FOREIGN KEY ([ClientUserId])
                REFERENCES [tb_inventory].[ClientUsers]([ClientUserId])
        );
    END;

    IF COL_LENGTH(N'tb_inventory.Equipment', N'AssetTag') IS NULL
        ALTER TABLE [tb_inventory].[Equipment] ADD [AssetTag] nvarchar(80) NULL;

    IF COL_LENGTH(N'tb_inventory.Equipment', N'ClientId') IS NULL
        ALTER TABLE [tb_inventory].[Equipment] ADD [ClientId] int NULL;

    IF COL_LENGTH(N'tb_inventory.Equipment', N'ClientUserId') IS NULL
        ALTER TABLE [tb_inventory].[Equipment] ADD [ClientUserId] bigint NULL;

    IF COL_LENGTH(N'tb_inventory.Equipment', N'LocationName') IS NULL
        ALTER TABLE [tb_inventory].[Equipment] ADD [LocationName] nvarchar(240) NULL;

    IF COL_LENGTH(N'tb_inventory.Equipment', N'WorkflowStage') IS NULL
        ALTER TABLE [tb_inventory].[Equipment]
            ADD [WorkflowStage] nvarchar(24) NOT NULL
                CONSTRAINT [DF_Equipment_WorkflowStage] DEFAULT (N'Stock')
                WITH VALUES;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.check_constraints
        WHERE [parent_object_id] = OBJECT_ID(N'tb_inventory.Equipment', N'U')
          AND [name] = N'CK_Equipment_WorkflowStage'
    )
        ALTER TABLE [tb_inventory].[Equipment] WITH CHECK
            ADD CONSTRAINT [CK_Equipment_WorkflowStage]
                CHECK ([WorkflowStage] IN
                    (N'Stock', N'Assigned', N'Deployment', N'Deployed'));

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys
        WHERE [parent_object_id] = OBJECT_ID(N'tb_inventory.Equipment', N'U')
          AND [name] = N'FK_Equipment_Client'
    )
        ALTER TABLE [tb_inventory].[Equipment] WITH CHECK
            ADD CONSTRAINT [FK_Equipment_Client]
                FOREIGN KEY ([ClientId])
                REFERENCES [tb_data].[Clients]([Id]);

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys
        WHERE [parent_object_id] = OBJECT_ID(N'tb_inventory.Equipment', N'U')
          AND [name] = N'FK_Equipment_ClientUser'
    )
        ALTER TABLE [tb_inventory].[Equipment] WITH CHECK
            ADD CONSTRAINT [FK_Equipment_ClientUser]
                FOREIGN KEY ([ClientUserId])
                REFERENCES [tb_inventory].[ClientUsers]([ClientUserId]);

    IF EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_inventory.Equipment', N'U')
          AND [name] = N'IX_Equipment_Board'
    )
        DROP INDEX [IX_Equipment_Board] ON [tb_inventory].[Equipment];

    CREATE INDEX [IX_Equipment_Board]
        ON [tb_inventory].[Equipment]
            ([IsArchived], [WorkflowStage], [AssignedToWindowsSid], [SortOrder], [EquipmentId])
        INCLUDE
        (
            [AssetTag], [DeviceType], [Name], [SerialNumber], [IpAddress],
            [ClientId], [ClientUserId], [ClientName], [LocationName]
        );

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_inventory.Equipment', N'U')
          AND [name] = N'UX_Equipment_AssetTag'
    )
        CREATE UNIQUE INDEX [UX_Equipment_AssetTag]
            ON [tb_inventory].[Equipment]([AssetTag])
            WHERE [AssetTag] IS NOT NULL AND [IsArchived] = 0;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_inventory.Equipment', N'U')
          AND [name] = N'IX_Equipment_SerialNumber'
    )
        CREATE INDEX [IX_Equipment_SerialNumber]
            ON [tb_inventory].[Equipment]([SerialNumber])
            WHERE [SerialNumber] IS NOT NULL;

    IF OBJECT_ID(N'tb_inventory.EquipmentAssignmentHistory', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_inventory].[EquipmentAssignmentHistory]
        (
            [EquipmentAssignmentHistoryId] bigint IDENTITY(1,1) NOT NULL,
            [EquipmentId] bigint NOT NULL,
            [EventType] nvarchar(80) NOT NULL,
            [WorkflowStage] nvarchar(24) NOT NULL,
            [AssignedToWindowsSid] varbinary(85) NULL,
            [ClientId] int NULL,
            [ClientUserId] bigint NULL,
            [LocationName] nvarchar(240) NULL,
            [Notes] nvarchar(1000) NULL,
            [ChangedByWindowsSid] varbinary(85) NOT NULL,
            [ChangedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_EquipmentAssignmentHistory_ChangedAtUtc]
                DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT [PK_EquipmentAssignmentHistory]
                PRIMARY KEY CLUSTERED ([EquipmentAssignmentHistoryId]),
            CONSTRAINT [FK_EquipmentAssignmentHistory_Equipment]
                FOREIGN KEY ([EquipmentId])
                REFERENCES [tb_inventory].[Equipment]([EquipmentId]),
            CONSTRAINT [FK_EquipmentAssignmentHistory_AssignedUser]
                FOREIGN KEY ([AssignedToWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_EquipmentAssignmentHistory_Client]
                FOREIGN KEY ([ClientId])
                REFERENCES [tb_data].[Clients]([Id]),
            CONSTRAINT [FK_EquipmentAssignmentHistory_ClientUser]
                FOREIGN KEY ([ClientUserId])
                REFERENCES [tb_inventory].[ClientUsers]([ClientUserId]),
            CONSTRAINT [FK_EquipmentAssignmentHistory_ChangedBy]
                FOREIGN KEY ([ChangedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_EquipmentAssignmentHistory_WorkflowStage]
                CHECK ([WorkflowStage] IN
                    (N'Stock', N'Assigned', N'Deployment', N'Deployed'))
        );

        CREATE INDEX [IX_EquipmentAssignmentHistory_Equipment]
            ON [tb_inventory].[EquipmentAssignmentHistory]
                ([EquipmentId], [ChangedAtUtc] DESC, [EquipmentAssignmentHistoryId] DESC)
            INCLUDE
                ([EventType], [WorkflowStage], [AssignedToWindowsSid],
                 [ClientId], [ClientUserId], [LocationName]);
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_deploy].[SchemaMigrations]
        WHERE [MigrationId] = N'SqlServer2016.EquipmentBoard.0014'
    )
        INSERT INTO [tb_deploy].[SchemaMigrations]
            ([MigrationId], [SchemaVersion], [ReleaseVersion], [ScriptChecksum])
        VALUES
            (N'SqlServer2016.EquipmentBoard.0014', 14, N'0.5.57', NULL);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'SqlServer2016.EquipmentBoard.0014 installed.';
GO
