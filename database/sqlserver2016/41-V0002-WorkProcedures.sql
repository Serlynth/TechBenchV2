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

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
            @EntityType = N'Ticket',
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

    /*
        @IncludeAllUsers is retained for desktop contract compatibility, but tag
        suggestions are always private to the effective user's saved work. This
        also gives an Admin read-only preview the same suggestions as its target.
    */
    SELECT parsed_tag.[Tag]
    FROM
    (
        SELECT MIN(LTRIM(RTRIM(tag.[value]))) AS [Tag]
        FROM [tb_data].[WorkEntries] AS work_entry
        CROSS APPLY STRING_SPLIT(COALESCE(work_entry.[Tags], N''), N',') AS tag
        WHERE work_entry.[OwnerWindowsSid] = @UserSid
          AND NULLIF(LTRIM(RTRIM(tag.[value])), N'') IS NOT NULL
        GROUP BY UPPER(LTRIM(RTRIM(tag.[value])))
    ) AS parsed_tag
    ORDER BY parsed_tag.[Tag];
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

        /*
            Publish newly entered tags to the organization catalog in the same
            transaction as the work-entry save. Range locks serialize first use
            of a tag across workstations; no direct table grant is required.
        */
        ;WITH parsed_tags AS
        (
            SELECT DISTINCT
                LTRIM(RTRIM(tag.[value])) AS [Tag],
                CONVERT
                (
                    binary(32),
                    HASHBYTES
                    (
                        N'SHA2_256',
                        CONVERT
                        (
                            varbinary(2000),
                            UPPER(LTRIM(RTRIM(tag.[value])))
                        )
                    )
                ) AS [TagHash]
            FROM STRING_SPLIT(@Tags, N',') AS tag
            WHERE NULLIF(LTRIM(RTRIM(tag.[value])), N'') IS NOT NULL
        )
        INSERT INTO [tb_data].[OrganizationTags]
        (
            [Tag],
            [TagHash],
            [CreatedByWindowsSid],
            [CreatedAtUtc]
        )
        SELECT
            parsed_tag.[Tag],
            parsed_tag.[TagHash],
            @UserSid,
            @NowUtc
        FROM parsed_tags AS parsed_tag
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[OrganizationTags] WITH (UPDLOCK, HOLDLOCK)
            WHERE [TagHash] = parsed_tag.[TagHash]
        );

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
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

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'WorkEntryDeleted',
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

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'WorkEntryLinkSaved',
            @EntityType = N'WorkEntryLink',
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

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'WorkEntryLinkDeleted',
        @EntityType = N'WorkEntryLink',
        @EntityId = @AuditEntityId,
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
