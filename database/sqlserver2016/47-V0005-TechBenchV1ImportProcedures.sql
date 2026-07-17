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
