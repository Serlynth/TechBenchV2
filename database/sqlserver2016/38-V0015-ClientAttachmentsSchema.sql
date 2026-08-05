:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/* Additive schema-15 extension. Existing stable clients remain compatible and
   simply ignore the attachment metadata and storage configuration. */
IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.ClientInfoBeta.0015'
      AND [SchemaVersion] = 15
)
BEGIN
    RAISERROR(N'The Client Info schema-15 extension must be installed before client attachments.', 16, 1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'tb_client.ClientAttachments', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_client].[ClientAttachments]
        (
            [AttachmentId] uniqueidentifier NOT NULL,
            [ClientId] int NOT NULL,
            [RelativePath] nvarchar(400) NOT NULL,
            [OriginalFileName] nvarchar(260) NOT NULL,
            [ContentType] nvarchar(160) NOT NULL,
            [Category] nvarchar(80) NOT NULL,
            [Caption] nvarchar(500) NULL,
            [FileSizeBytes] bigint NOT NULL,
            [ContentSha256] binary(32) NOT NULL,
            [UploadedByWindowsSid] varbinary(85) NOT NULL,
            [UploadedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientAttachments_UploadedAtUtc]
                DEFAULT (SYSUTCDATETIME()),
            [IsArchived] bit NOT NULL
                CONSTRAINT [DF_ClientAttachments_IsArchived] DEFAULT (0),
            [ArchivedByWindowsSid] varbinary(85) NULL,
            [ArchivedAtUtc] datetime2(3) NULL,
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ClientAttachments]
                PRIMARY KEY CLUSTERED ([AttachmentId]),
            CONSTRAINT [FK_ClientAttachments_Client]
                FOREIGN KEY ([ClientId])
                REFERENCES [tb_data].[Clients]([Id]),
            CONSTRAINT [FK_ClientAttachments_UploadedBy]
                FOREIGN KEY ([UploadedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_ClientAttachments_ArchivedBy]
                FOREIGN KEY ([ArchivedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [UQ_ClientAttachments_RelativePath]
                UNIQUE ([RelativePath]),
            CONSTRAINT [CK_ClientAttachments_FileName]
                CHECK
                (
                    LEN(LTRIM(RTRIM([OriginalFileName]))) > 0
                    AND CHARINDEX(N'\', [OriginalFileName]) = 0
                    AND CHARINDEX(N'/', [OriginalFileName]) = 0
                ),
            CONSTRAINT [CK_ClientAttachments_RelativePath]
                CHECK
                (
                    LEN(LTRIM(RTRIM([RelativePath]))) > 0
                    AND [RelativePath] NOT LIKE N'%..%'
                    AND [RelativePath] NOT LIKE N':%'
                    AND LEFT([RelativePath], 1) <> N'\'
                    AND LEFT([RelativePath], 1) <> N'/'
                ),
            CONSTRAINT [CK_ClientAttachments_Category]
                CHECK (LEN(LTRIM(RTRIM([Category]))) > 0),
            CONSTRAINT [CK_ClientAttachments_FileSize]
                CHECK ([FileSizeBytes] >= 0),
            CONSTRAINT [CK_ClientAttachments_ArchiveState]
                CHECK
                (
                    ([IsArchived] = 0
                     AND [ArchivedByWindowsSid] IS NULL
                     AND [ArchivedAtUtc] IS NULL)
                    OR
                    ([IsArchived] = 1
                     AND [ArchivedByWindowsSid] IS NOT NULL
                     AND [ArchivedAtUtc] IS NOT NULL)
                )
        );

        CREATE INDEX [IX_ClientAttachments_ClientStatusDate]
            ON [tb_client].[ClientAttachments]
                ([ClientId], [IsArchived], [UploadedAtUtc] DESC)
            INCLUDE
                ([OriginalFileName], [Category], [FileSizeBytes], [ContentType]);
    END;

    IF COL_LENGTH(N'tb_client.ClientAttachments', N'EquipmentId') IS NULL
        ALTER TABLE [tb_client].[ClientAttachments]
            ADD [EquipmentId] bigint NULL;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys
        WHERE [name] = N'FK_ClientAttachments_Equipment'
          AND [parent_object_id] = OBJECT_ID(N'tb_client.ClientAttachments')
    )
        EXEC sys.sp_executesql N'
            ALTER TABLE [tb_client].[ClientAttachments] WITH CHECK
                ADD CONSTRAINT [FK_ClientAttachments_Equipment]
                    FOREIGN KEY ([EquipmentId])
                    REFERENCES [tb_inventory].[Equipment]([EquipmentId]);';

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE [name] = N'IX_ClientAttachments_EquipmentStatusDate'
          AND [object_id] = OBJECT_ID(N'tb_client.ClientAttachments')
    )
        EXEC sys.sp_executesql N'
            CREATE INDEX [IX_ClientAttachments_EquipmentStatusDate]
                ON [tb_client].[ClientAttachments]
                    ([EquipmentId], [IsArchived], [UploadedAtUtc] DESC)
                INCLUDE ([OriginalFileName], [Category], [ContentType])
                WHERE [EquipmentId] IS NOT NULL;';

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_deploy].[SchemaMigrations]
        WHERE [MigrationId] = N'SqlServer2016.ClientAttachments.0015'
    )
        INSERT INTO [tb_deploy].[SchemaMigrations]
            ([MigrationId], [SchemaVersion], [ReleaseVersion], [ScriptChecksum])
        VALUES
            (N'SqlServer2016.ClientAttachments.0015', 15, N'0.6.6-beta.4', NULL);

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_deploy].[SchemaMigrations]
        WHERE [MigrationId] = N'SqlServer2016.ClientAttachmentEquipmentLinks.0015'
    )
        INSERT INTO [tb_deploy].[SchemaMigrations]
            ([MigrationId], [SchemaVersion], [ReleaseVersion], [ScriptChecksum])
        VALUES
            (N'SqlServer2016.ClientAttachmentEquipmentLinks.0015', 15,
             N'0.6.6-beta.26', NULL);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'Schema-15-compatible Client Attachments and equipment links installed.';
GO
