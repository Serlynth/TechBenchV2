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
