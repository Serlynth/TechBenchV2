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
