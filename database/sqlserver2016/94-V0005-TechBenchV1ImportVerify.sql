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

IF @InstalledSchemaVersion NOT IN (5, 6, 7, 8, 9, 10, 11, 12)
BEGIN
    PRINT N'FAIL: V0005 verification supports installed schema version 5, 6, 7, 8, or 9.';
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
       N'CONVERT(int,' + CONVERT(nvarchar(10), @InstalledSchemaVersion) + N')',
       REPLACE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities')), N' ', N'')) = 0
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
