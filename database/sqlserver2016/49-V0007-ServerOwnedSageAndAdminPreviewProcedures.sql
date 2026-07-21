:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* Activity Item ID is personal posting identity, not shared sync configuration. */
DELETE FROM [tb_data].[OrganizationSettings]
WHERE [SettingKey] = N'Sage.ActivityItemId';
GO

/*
    V0007 moves Sage customer synchronization behind the Windows service and
    adds short-lived, server-issued, read-only Admin preview sessions.
*/

ALTER PROCEDURE [tb_security].[EnsureCurrentUser]
    @UserSid varbinary(85) OUTPUT,
    @LoginName nvarchar(256) OUTPUT,
    @DisplayName nvarchar(160) OUTPUT,
    @IsTechnician bit OUTPUT,
    @IsManager bit OUTPUT,
    @IsAdmin bit OUTPUT,
    @IsSyncOperator bit OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @PreviewContext sql_variant = SESSION_CONTEXT(N'TechBench.PreviewSessionId');

    IF @PreviewContext IS NOT NULL
    BEGIN
        DECLARE @PreviewSessionId uniqueidentifier =
            TRY_CONVERT(uniqueidentifier, CONVERT(nvarchar(36), @PreviewContext));

        IF @PreviewSessionId IS NULL OR USER_NAME() <> N'tb_preview_reader'
            THROW 51900, N'The read-only user preview context is invalid.', 1;

        SELECT
            @UserSid = target_user.[WindowsSid],
            @LoginName = target_user.[LoginName],
            @DisplayName = target_user.[DisplayName],
            @IsTechnician = target_user.[IsTechnician],
            @IsManager = target_user.[IsManager],
            @IsAdmin = target_user.[IsAdmin],
            @IsSyncOperator = target_user.[IsSyncOperator]
        FROM [tb_security].[AdminUserPreviewSessions] AS preview_session
        INNER JOIN [tb_security].[Users] AS actor_user
            ON actor_user.[WindowsSid] = preview_session.[ActorWindowsSid]
        INNER JOIN [tb_security].[Users] AS target_user
            ON target_user.[WindowsSid] = preview_session.[TargetWindowsSid]
        WHERE preview_session.[PreviewSessionId] = @PreviewSessionId
          AND preview_session.[ActorWindowsSid] = SUSER_SID(ORIGINAL_LOGIN())
          AND preview_session.[EndedAtUtc] IS NULL
          AND preview_session.[ExpiresAtUtc] > SYSUTCDATETIME()
          AND actor_user.[IsAdmin] = 1
          AND target_user.[IsTechnician] = 1
          AND target_user.[IsAdmin] = 0
          AND target_user.[LastSeenAtUtc] >= DATEADD(hour, -1, SYSUTCDATETIME());

        IF @UserSid IS NULL
            THROW 51901, N'The read-only user preview session is missing, expired, or no longer authorized.', 1;

        RETURN;
    END;

    IF USER_NAME() = N'tb_preview_reader'
        THROW 51902, N'The preview reader cannot be used without a valid server-issued session.', 1;

    SET @UserSid = SUSER_SID(ORIGINAL_LOGIN());
    SET @LoginName = CONVERT(nvarchar(256), ORIGINAL_LOGIN());
    SET @IsTechnician =
        CONVERT(bit, CASE WHEN IS_ROLEMEMBER(N'tb_role_user') = 1 THEN 1 ELSE 0 END);
    SET @IsManager =
        CONVERT(bit, CASE WHEN IS_ROLEMEMBER(N'tb_role_manager') = 1 THEN 1 ELSE 0 END);
    SET @IsAdmin =
        CONVERT(bit, CASE WHEN IS_ROLEMEMBER(N'tb_role_admin') = 1 THEN 1 ELSE 0 END);
    SET @IsSyncOperator =
        CONVERT(bit, CASE WHEN IS_ROLEMEMBER(N'tb_role_sync_operator') = 1 THEN 1 ELSE 0 END);

    IF @UserSid IS NULL
       OR DATALENGTH(@UserSid) NOT BETWEEN 8 AND 85
       OR NULLIF(LTRIM(RTRIM(@LoginName)), N'') IS NULL
        THROW 51000, N'SQL Server did not provide a valid authenticated Windows identity.', 1;

    DECLARE @HasApplicationRole bit = CONVERT
    (
        bit,
        CASE
            WHEN @IsTechnician = 1 OR @IsManager = 1 OR @IsAdmin = 1 OR @IsSyncOperator = 1
                THEN 1
            ELSE 0
        END
    );

    IF @IsAdmin = 1
    BEGIN
        SET @IsManager = 1;
        SET @IsTechnician = 1;
    END
    ELSE IF @IsManager = 1
        SET @IsTechnician = 1;

    SET @DisplayName =
        CASE
            WHEN CHARINDEX(N'\', @LoginName) > 0
                THEN RIGHT(@LoginName, LEN(@LoginName) - CHARINDEX(N'\', @LoginName))
            ELSE @LoginName
        END;
    SET @DisplayName = LEFT(@DisplayName, 160);

    BEGIN TRY
        BEGIN TRANSACTION;

        UPDATE [tb_security].[Users] WITH (UPDLOCK, HOLDLOCK)
        SET
            [LoginName] = @LoginName,
            [DisplayName] =
                CASE
                    WHEN NULLIF(LTRIM(RTRIM([DisplayName])), N'') IS NULL
                      OR [DisplayName] = [LoginName]
                      OR [DisplayName] =
                         CASE
                             WHEN CHARINDEX(N'\', [LoginName]) > 0
                                 THEN RIGHT([LoginName], LEN([LoginName]) - CHARINDEX(N'\', [LoginName]))
                             ELSE [LoginName]
                         END
                        THEN @DisplayName
                    ELSE [DisplayName]
                END,
            [IsTechnician] = @IsTechnician,
            [IsManager] = @IsManager,
            [IsAdmin] = @IsAdmin,
            [IsSyncOperator] = @IsSyncOperator,
            [LastSeenAtUtc] = SYSUTCDATETIME()
        WHERE [WindowsSid] = @UserSid;

        IF @@ROWCOUNT = 0 AND @HasApplicationRole = 1
        BEGIN
            INSERT INTO [tb_security].[Users]
            (
                [WindowsSid], [LoginName], [DisplayName], [IsTechnician],
                [IsManager], [IsAdmin], [IsSyncOperator]
            )
            VALUES
            (
                @UserSid, @LoginName, @DisplayName, @IsTechnician,
                @IsManager, @IsAdmin, @IsSyncOperator
            );
        END;

        /* A role refresh is authoritative. Immediately terminate sessions
           whose actor/target is no longer eligible, including the all-zero
           role state that is persisted before the access-denied THROW. */
        UPDATE [tb_security].[AdminUserPreviewSessions]
        SET [EndedAtUtc] = COALESCE([EndedAtUtc], SYSUTCDATETIME())
        WHERE [EndedAtUtc] IS NULL
          AND
          (
              ([ActorWindowsSid] = @UserSid AND @IsAdmin = 0)
              OR
              (
                  [TargetWindowsSid] = @UserSid
                  AND (@IsTechnician = 0 OR @IsAdmin = 1)
              )
          );

        SELECT @DisplayName = [DisplayName]
        FROM [tb_security].[Users]
        WHERE [WindowsSid] = @UserSid;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    IF @HasApplicationRole = 0
        THROW 51002, N'The Windows login is not assigned to a TechBench application role.', 1;
END;
GO

ALTER PROCEDURE [tb_app].[GetCurrentUserContext]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @LoginName nvarchar(256), @DisplayName nvarchar(160);
    DECLARE @IsTechnician bit, @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    DECLARE @DatabaseInstanceId uniqueidentifier, @SchemaVersion int;
    DECLARE @AuthenticatedUserSid varbinary(85) = SUSER_SID(ORIGINAL_LOGIN());
    DECLARE @AuthenticatedLoginName nvarchar(256) = CONVERT(nvarchar(256), ORIGINAL_LOGIN());
    DECLARE @AuthenticatedDisplayName nvarchar(160);
    DECLARE @PreviewSessionId uniqueidentifier = TRY_CONVERT
    (
        uniqueidentifier,
        CONVERT(nvarchar(36), SESSION_CONTEXT(N'TechBench.PreviewSessionId'))
    );
    DECLARE @PreviewExpiresAtUtc datetime2(3);

    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @UserSid OUTPUT,
        @LoginName = @LoginName OUTPUT,
        @DisplayName = @DisplayName OUTPUT,
        @IsTechnician = @IsTechnician OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT @DatabaseInstanceId = TRY_CONVERT(uniqueidentifier, [Value])
    FROM [tb_data].[ServerMetadata]
    WHERE [Key] = N'Server.InstanceId';

    SELECT @SchemaVersion = MAX([SchemaVersion])
    FROM [tb_deploy].[SchemaMigrations];

    SELECT @AuthenticatedDisplayName = [DisplayName]
    FROM [tb_security].[Users]
    WHERE [WindowsSid] = @AuthenticatedUserSid;

    SET @AuthenticatedDisplayName = COALESCE
    (
        NULLIF(LTRIM(RTRIM(@AuthenticatedDisplayName)), N''),
        CASE
            WHEN CHARINDEX(N'\', @AuthenticatedLoginName) > 0
                THEN RIGHT(@AuthenticatedLoginName, LEN(@AuthenticatedLoginName) - CHARINDEX(N'\', @AuthenticatedLoginName))
            ELSE @AuthenticatedLoginName
        END
    );

    IF USER_NAME() = N'tb_preview_reader'
    BEGIN
        SELECT @PreviewExpiresAtUtc = [ExpiresAtUtc]
        FROM [tb_security].[AdminUserPreviewSessions]
        WHERE [PreviewSessionId] = @PreviewSessionId
          AND [ActorWindowsSid] = @AuthenticatedUserSid
          AND [TargetWindowsSid] = @UserSid
          AND [EndedAtUtc] IS NULL
          AND [ExpiresAtUtc] > SYSUTCDATETIME();
    END
    ELSE
        SET @PreviewSessionId = NULL;

    IF @DatabaseInstanceId IS NULL OR @SchemaVersion IS NULL
        THROW 51020, N'The TechBench database metadata is incomplete.', 1;

    SELECT
        @UserSid AS [UserSid],
        @LoginName AS [LoginName],
        @DisplayName AS [DisplayName],
        @DatabaseInstanceId AS [DatabaseInstanceId],
        @SchemaVersion AS [SchemaVersion],
        SYSUTCDATETIME() AS [ServerUtc],
        @IsTechnician AS [IsTechnician],
        @IsManager AS [IsManager],
        @IsAdmin AS [IsAdmin],
        @IsSyncOperator AS [IsSyncOperator],
        @AuthenticatedUserSid AS [AuthenticatedUserSid],
        @AuthenticatedLoginName AS [AuthenticatedLoginName],
        @AuthenticatedDisplayName AS [AuthenticatedDisplayName],
        CONVERT(bit, CASE WHEN @PreviewSessionId IS NULL THEN 0 ELSE 1 END) AS [IsReadOnlyPreview],
        @PreviewSessionId AS [PreviewSessionId],
        @PreviewExpiresAtUtc AS [PreviewExpiresAtUtc];
END;
GO

ALTER PROCEDURE [tb_app].[GetRepositoryCapabilities]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT
        CONVERT(int, 7) AS [SchemaVersion],
        CONVERT(bit, 0) AS [FullTextSearchAvailable],
        CONVERT(bit, 1) AS [SupportsTickets],
        CONVERT(bit, 1) AS [SupportsWorkEntries],
        CONVERT(bit, 1) AS [SupportsPrivateNotes],
        CONVERT(bit, 1) AS [SupportsPostingLeases],
        CONVERT(bit, 1) AS [SupportsSyncLeases],
        CONVERT(bit, 1) AS [SupportsImports],
        CONVERT(bit, 1) AS [SupportsTechBenchV1Import],
        CONVERT(bit, 1) AS [SupportsServerSageSync],
        CONVERT(bit, 1) AS [SupportsAdminUserPreview];
END;
GO

IF OBJECT_ID(N'tb_app.AdminListPreviewUsers', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminListPreviewUsers];
GO

CREATE PROCEDURE [tb_app].[AdminListPreviewUsers]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @Login nvarchar(256), @Display nvarchar(160);
    DECLARE @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid=@ActorSid OUTPUT, @LoginName=@Login OUTPUT, @DisplayName=@Display OUTPUT,
        @IsTechnician=@Tech OUTPUT, @IsManager=@Manager OUTPUT,
        @IsAdmin=@Admin OUTPUT, @IsSyncOperator=@Sync OUTPUT;

    IF @Admin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51910, N'Only a currently authorized TechBench Admin may list preview users.', 1;

    SELECT
        [WindowsSid] AS [UserSid], [LoginName], [DisplayName],
        [IsTechnician], [IsManager], [IsAdmin], [IsSyncOperator]
    FROM [tb_security].[Users]
    WHERE [WindowsSid] <> @ActorSid
      AND [IsTechnician] = 1
      AND [IsAdmin] = 0
      AND [LastSeenAtUtc] >= DATEADD(hour, -1, SYSUTCDATETIME())
    ORDER BY [DisplayName], [LoginName];
END;
GO

IF OBJECT_ID(N'tb_app.AdminBeginUserPreview', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminBeginUserPreview];
GO

CREATE PROCEDURE [tb_app].[AdminBeginUserPreview]
    @TargetLoginName nvarchar(256),
    @ClientInstanceId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @Login nvarchar(256), @Display nvarchar(160);
    DECLARE @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid=@ActorSid OUTPUT, @LoginName=@Login OUTPUT, @DisplayName=@Display OUTPUT,
        @IsTechnician=@Tech OUTPUT, @IsManager=@Manager OUTPUT,
        @IsAdmin=@Admin OUTPUT, @IsSyncOperator=@Sync OUTPUT;

    IF @Admin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51911, N'Only a currently authorized TechBench Admin may begin a user preview.', 1;

    SET @TargetLoginName = NULLIF(LTRIM(RTRIM(@TargetLoginName)), N'');
    IF @TargetLoginName IS NULL OR @ClientInstanceId IS NULL
        THROW 51912, N'TargetLoginName and ClientInstanceId are required.', 1;

    DECLARE @TargetSid varbinary(85), @PreviewSessionId uniqueidentifier = NEWID();
    DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @Expires datetime2(3);
    SET @Expires = DATEADD(minute, 30, @Now);

    SELECT @TargetSid = [WindowsSid]
    FROM [tb_security].[Users]
    WHERE [LoginName] = @TargetLoginName
      AND [IsTechnician] = 1
      AND [IsAdmin] = 0
      AND [LastSeenAtUtc] >= DATEADD(hour, -1, @Now);

    IF @TargetSid IS NULL OR @TargetSid = @ActorSid
        THROW 51913, N'The selected non-Admin technician must have opened TechBench V2 within the past hour and still be authorized.', 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        UPDATE [tb_security].[AdminUserPreviewSessions]
        SET [EndedAtUtc] = @Now
        WHERE [ActorWindowsSid] = @ActorSid
          AND [ClientInstanceId] = @ClientInstanceId
          AND [EndedAtUtc] IS NULL;

        INSERT INTO [tb_security].[AdminUserPreviewSessions]
        (
            [PreviewSessionId], [ActorWindowsSid], [TargetWindowsSid],
            [ClientInstanceId], [StartedAtUtc], [ExpiresAtUtc]
        )
        VALUES
        (
            @PreviewSessionId, @ActorSid, @TargetSid,
            @ClientInstanceId, @Now, @Expires
        );

        DECLARE @AuditJson nvarchar(max) =
        (
            SELECT @TargetLoginName AS [targetLoginName], @ClientInstanceId AS [clientInstanceId],
                   @Expires AS [expiresAtUtc]
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );
        EXEC [tb_security].[WriteAuditEvent]
            @Action=N'AdminUserPreviewStarted', @EntityType=N'UserPreview',
            @EntityId=@TargetLoginName, @RequestId=@PreviewSessionId, @DataJson=@AuditJson;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        @PreviewSessionId AS [PreviewSessionId],
        target_user.[WindowsSid] AS [UserSid], target_user.[LoginName], target_user.[DisplayName],
        target_user.[IsTechnician], target_user.[IsManager], target_user.[IsAdmin],
        target_user.[IsSyncOperator], @Expires AS [ExpiresAtUtc]
    FROM [tb_security].[Users] AS target_user
    WHERE target_user.[WindowsSid] = @TargetSid;
END;
GO

IF OBJECT_ID(N'tb_app.ActivateReadOnlyPreview', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ActivateReadOnlyPreview];
GO

CREATE PROCEDURE [tb_app].[ActivateReadOnlyPreview]
    @PreviewSessionId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF SESSION_CONTEXT(N'TechBench.PreviewSessionId') IS NOT NULL
        THROW 51914, N'This SQL connection already has a preview context.', 1;

    DECLARE @ActorSid varbinary(85), @Login nvarchar(256), @Display nvarchar(160);
    DECLARE @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid=@ActorSid OUTPUT, @LoginName=@Login OUTPUT, @DisplayName=@Display OUTPUT,
        @IsTechnician=@Tech OUTPUT, @IsManager=@Manager OUTPUT,
        @IsAdmin=@Admin OUTPUT, @IsSyncOperator=@Sync OUTPUT;

    IF @PreviewSessionId IS NULL OR @Admin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51915, N'Only a currently authorized TechBench Admin may activate a user preview.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_security].[AdminUserPreviewSessions] AS preview_session
        INNER JOIN [tb_security].[Users] AS target_user
            ON target_user.[WindowsSid] = preview_session.[TargetWindowsSid]
        WHERE preview_session.[PreviewSessionId] = @PreviewSessionId
          AND preview_session.[ActorWindowsSid] = @ActorSid
          AND preview_session.[EndedAtUtc] IS NULL
          AND preview_session.[ExpiresAtUtc] > SYSUTCDATETIME()
          AND target_user.[IsTechnician] = 1
          AND target_user.[IsAdmin] = 0
          AND target_user.[LastSeenAtUtc] >= DATEADD(hour, -1, SYSUTCDATETIME())
    )
        THROW 51916, N'The user preview session is missing, expired, or no longer authorized.', 1;

    EXEC sys.sp_set_session_context
        @key=N'TechBench.PreviewSessionId', @value=@PreviewSessionId, @read_only=1;

    SELECT [PreviewSessionId], [TargetWindowsSid] AS [UserSid], [ExpiresAtUtc]
    FROM [tb_security].[AdminUserPreviewSessions]
    WHERE [PreviewSessionId] = @PreviewSessionId;
END;
GO

IF OBJECT_ID(N'tb_app.AdminEndUserPreview', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminEndUserPreview];
GO

CREATE PROCEDURE [tb_app].[AdminEndUserPreview]
    @PreviewSessionId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @Login nvarchar(256), @Display nvarchar(160);
    DECLARE @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid=@ActorSid OUTPUT, @LoginName=@Login OUTPUT, @DisplayName=@Display OUTPUT,
        @IsTechnician=@Tech OUTPUT, @IsManager=@Manager OUTPUT,
        @IsAdmin=@Admin OUTPUT, @IsSyncOperator=@Sync OUTPUT;

    IF @PreviewSessionId IS NULL OR @Admin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51917, N'Only a currently authorized TechBench Admin may end a user preview.', 1;

    DECLARE @TargetLogin nvarchar(256);
    SELECT @TargetLogin = target_user.[LoginName]
    FROM [tb_security].[AdminUserPreviewSessions] AS preview_session
    INNER JOIN [tb_security].[Users] AS target_user
        ON target_user.[WindowsSid] = preview_session.[TargetWindowsSid]
    WHERE preview_session.[PreviewSessionId] = @PreviewSessionId
      AND preview_session.[ActorWindowsSid] = @ActorSid;

    UPDATE [tb_security].[AdminUserPreviewSessions]
    SET [EndedAtUtc] = COALESCE([EndedAtUtc], SYSUTCDATETIME())
    WHERE [PreviewSessionId] = @PreviewSessionId
      AND [ActorWindowsSid] = @ActorSid
      AND [EndedAtUtc] IS NULL;

    IF @TargetLogin IS NOT NULL
        EXEC [tb_security].[WriteAuditEvent]
            @Action=N'AdminUserPreviewEnded', @EntityType=N'UserPreview',
            @EntityId=@TargetLogin, @RequestId=@PreviewSessionId, @DataJson=NULL;

    SELECT @PreviewSessionId AS [PreviewSessionId],
           CONVERT(bit, CASE WHEN @TargetLogin IS NULL THEN 0 ELSE 1 END) AS [Ended];
END;
GO

/* A preview reproduces the target user's shared work view but never exposes
   their personal-note payload, personal-note flag, rowversion, or draft. */
ALTER PROCEDURE [tb_app].[SearchWorkEntries]
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

    DECLARE @UserSid varbinary(85), @LoginName nvarchar(256), @DisplayName nvarchar(160);
    DECLARE @IsTechnician bit, @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid=@UserSid OUTPUT, @LoginName=@LoginName OUTPUT, @DisplayName=@DisplayName OUTPUT,
        @IsTechnician=@IsTechnician OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;

    DECLARE @IsReadOnlyPreview bit =
        CONVERT(bit, CASE WHEN USER_NAME() = N'tb_preview_reader' THEN 1 ELSE 0 END);

    IF @IncludeAllUsers = 1 AND @IsManager <> 1 AND @IsAdmin <> 1
        THROW 51120, N'Only a Manager or Admin may search other users'' work entries.', 1;

    SET @Limit = CASE WHEN @Limit IS NULL OR @Limit < 1 THEN 1 WHEN @Limit > 2000 THEN 2000 ELSE @Limit END;
    SET @TicketText = NULLIF(LTRIM(RTRIM(@TicketText)), N'');
    SET @PostingStatus = NULLIF(LTRIM(RTRIM(@PostingStatus)), N'');
    SET @Keyword = NULLIF(LTRIM(RTRIM(@Keyword)), N'');
    SET @Tags = NULLIF(LTRIM(RTRIM(@Tags)), N'');
    SET @FollowUpState = NULLIF(LTRIM(RTRIM(@FollowUpState)), N'');

    DECLARE @KeywordPattern nvarchar(500) = CASE WHEN @Keyword IS NULL THEN NULL ELSE N'%' + @Keyword + N'%' END;
    DECLARE @TicketPattern nvarchar(300) = CASE WHEN @TicketText IS NULL THEN NULL ELSE N'%' + @TicketText + N'%' END;
    DECLARE @TagPattern nvarchar(700) = CASE WHEN @Tags IS NULL THEN NULL ELSE N'%' + @Tags + N'%' END;

    SELECT TOP (@Limit)
        work_entry.[Id], work_entry.[OwnerWindowsSid], work_entry.[WorkDate],
        work_entry.[ClientId], work_entry.[ManualClientName], work_entry.[TicketId],
        work_entry.[TicketNumberText], work_entry.[HasTimeRange], work_entry.[StartTime],
        work_entry.[EndTime], work_entry.[DurationMinutes], work_entry.[Billable],
        work_entry.[Note],
        CASE WHEN @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
             THEN personal_note.[Note] ELSE NULL END AS [InternalNote],
        CASE WHEN @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
             THEN personal_note.[Note] ELSE NULL END AS [PersonalNote],
        CASE WHEN @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
             THEN COALESCE(personal_note.[IncludeInWhd], 0)
             ELSE CONVERT(bit, 0) END AS [IncludePersonalNoteInWhd],
        work_entry.[Tags], work_entry.[FollowUpState], work_entry.[FollowUpDueDate],
        work_entry.[WhdPosted], work_entry.[WhdPostedAtUtc] AS [WhdPostedAt],
        work_entry.[SagePosted], work_entry.[SagePostedAtUtc] AS [SagePostedAt],
        work_entry.[SageTicketNumber], work_entry.[PostingStatus],
        CASE WHEN @IsReadOnlyPreview = 1 THEN NULL ELSE work_entry.[LastError] END AS [LastError],
        work_entry.[CreatedAtUtc] AS [CreatedAt], work_entry.[UpdatedAtUtc] AS [UpdatedAt],
        client.[Name] AS [ClientName], ticket.[TicketNumber], ticket.[Subject] AS [TicketSubject],
        work_entry.[RowVersion],
        CASE WHEN @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
             THEN personal_note.[RowVersion] ELSE NULL END AS [PersonalNoteRowVersion]
    FROM [tb_data].[WorkEntries] AS work_entry
    LEFT JOIN [tb_data].[Clients] AS client ON client.[Id] = work_entry.[ClientId]
    LEFT JOIN [tb_data].[Tickets] AS ticket ON ticket.[Id] = work_entry.[TicketId]
    LEFT JOIN [tb_private].[WorkEntryPersonalNotes] AS personal_note
        ON personal_note.[WorkEntryId] = work_entry.[Id]
       AND personal_note.[OwnerWindowsSid] = @UserSid
       AND @IsReadOnlyPreview = 0
    WHERE (@IncludeAllUsers = 1 OR work_entry.[OwnerWindowsSid] = @UserSid)
      AND (@StartDate IS NULL OR work_entry.[WorkDate] >= @StartDate)
      AND (@EndDate IS NULL OR work_entry.[WorkDate] <= @EndDate)
      AND (@ClientId IS NULL OR work_entry.[ClientId] = @ClientId)
      AND (@TicketId IS NULL OR work_entry.[TicketId] = @TicketId)
      AND (@ExcludeId IS NULL OR work_entry.[Id] <> @ExcludeId)
      AND (@TicketPattern IS NULL OR ticket.[TicketNumber] LIKE @TicketPattern OR work_entry.[TicketNumberText] LIKE @TicketPattern)
      AND (@PostingStatus IS NULL OR work_entry.[PostingStatus] = @PostingStatus)
      AND (@TagPattern IS NULL OR work_entry.[Tags] LIKE @TagPattern)
      AND (@FollowUpState IS NULL OR work_entry.[FollowUpState] = @FollowUpState)
      AND (@OpenFollowUpsOnly = 0 OR work_entry.[FollowUpState] IN (N'FollowUp', N'Waiting'))
      AND
      (
          @PendingWhdOnly = 0
          OR
          (
              (work_entry.[TicketId] IS NOT NULL OR NULLIF(LTRIM(RTRIM(work_entry.[TicketNumberText])), N'') IS NOT NULL)
              AND work_entry.[SagePosted] = 0
              AND
              (
                  work_entry.[WhdPosted] = 0 OR work_entry.[WhdPostedAtUtc] IS NULL
                  OR work_entry.[UpdatedAtUtc] > work_entry.[WhdPostedAtUtc]
                  OR work_entry.[LastError] LIKE N'WHD sync conflict:%'
              )
          )
      )
      AND (@PendingSageOnly = 0 OR (work_entry.[Billable] = 1 AND work_entry.[SagePosted] = 0))
      AND
      (
          @PendingAnyOnly = 0
          OR (work_entry.[Billable] = 1 AND work_entry.[SagePosted] = 0)
          OR
          (
              (work_entry.[TicketId] IS NOT NULL OR NULLIF(LTRIM(RTRIM(work_entry.[TicketNumberText])), N'') IS NOT NULL)
              AND work_entry.[SagePosted] = 0
              AND
              (
                  work_entry.[WhdPosted] = 0 OR work_entry.[WhdPostedAtUtc] IS NULL
                  OR work_entry.[UpdatedAtUtc] > work_entry.[WhdPostedAtUtc]
                  OR work_entry.[LastError] LIKE N'WHD sync conflict:%'
              )
          )
      )
      AND
      (
          @KeywordPattern IS NULL
          OR work_entry.[Note] LIKE @KeywordPattern OR work_entry.[Tags] LIKE @KeywordPattern
          OR work_entry.[ManualClientName] LIKE @KeywordPattern OR work_entry.[TicketNumberText] LIKE @KeywordPattern
          OR client.[Name] LIKE @KeywordPattern OR ticket.[TicketNumber] LIKE @KeywordPattern
          OR ticket.[Subject] LIKE @KeywordPattern
          OR
          (
              @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
              AND personal_note.[Note] LIKE @KeywordPattern
          )
      )
    ORDER BY work_entry.[WorkDate] DESC, work_entry.[StartTime] DESC, work_entry.[Id] DESC;
END;
GO

ALTER PROCEDURE [tb_app].[GetWorkEntry]
    @Id int,
    @IncludeAllUsers bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @LoginName nvarchar(256), @DisplayName nvarchar(160);
    DECLARE @IsTechnician bit, @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid=@UserSid OUTPUT, @LoginName=@LoginName OUTPUT, @DisplayName=@DisplayName OUTPUT,
        @IsTechnician=@IsTechnician OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;

    DECLARE @IsReadOnlyPreview bit =
        CONVERT(bit, CASE WHEN USER_NAME() = N'tb_preview_reader' THEN 1 ELSE 0 END);

    IF @IncludeAllUsers = 1 AND @IsManager <> 1 AND @IsAdmin <> 1
        THROW 51120, N'Only a Manager or Admin may read another user''s work entry.', 1;

    DECLARE @CanReadAll bit = CONVERT
    (
        bit,
        CASE WHEN @IncludeAllUsers = 1 AND (@IsManager = 1 OR @IsAdmin = 1) THEN 1 ELSE 0 END
    );

    SELECT
        work_entry.[Id], work_entry.[OwnerWindowsSid], work_entry.[WorkDate],
        work_entry.[ClientId], work_entry.[ManualClientName], work_entry.[TicketId],
        work_entry.[TicketNumberText], work_entry.[HasTimeRange], work_entry.[StartTime],
        work_entry.[EndTime], work_entry.[DurationMinutes], work_entry.[Billable],
        work_entry.[Note],
        CASE WHEN @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
             THEN personal_note.[Note] END AS [InternalNote],
        CASE WHEN @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
             THEN personal_note.[Note] END AS [PersonalNote],
        CASE WHEN @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
             THEN COALESCE(personal_note.[IncludeInWhd], 0)
             ELSE CONVERT(bit, 0) END AS [IncludePersonalNoteInWhd],
        work_entry.[Tags], work_entry.[FollowUpState], work_entry.[FollowUpDueDate],
        work_entry.[WhdPosted], work_entry.[WhdPostedAtUtc] AS [WhdPostedAt],
        work_entry.[SagePosted], work_entry.[SagePostedAtUtc] AS [SagePostedAt],
        work_entry.[SageTicketNumber], work_entry.[PostingStatus],
        CASE WHEN @IsReadOnlyPreview = 1 THEN NULL ELSE work_entry.[LastError] END AS [LastError],
        work_entry.[CreatedAtUtc] AS [CreatedAt], work_entry.[UpdatedAtUtc] AS [UpdatedAt],
        client.[Name] AS [ClientName], ticket.[TicketNumber], ticket.[Subject] AS [TicketSubject],
        work_entry.[RowVersion],
        CASE WHEN @IsReadOnlyPreview = 0 AND work_entry.[OwnerWindowsSid] = @UserSid
             THEN personal_note.[RowVersion] END AS [PersonalNoteRowVersion]
    FROM [tb_data].[WorkEntries] AS work_entry
    LEFT JOIN [tb_data].[Clients] AS client ON client.[Id] = work_entry.[ClientId]
    LEFT JOIN [tb_data].[Tickets] AS ticket ON ticket.[Id] = work_entry.[TicketId]
    LEFT JOIN [tb_private].[WorkEntryPersonalNotes] AS personal_note
        ON personal_note.[WorkEntryId] = work_entry.[Id]
       AND personal_note.[OwnerWindowsSid] = @UserSid
       AND @IsReadOnlyPreview = 0
    WHERE work_entry.[Id] = @Id
      AND (work_entry.[OwnerWindowsSid] = @UserSid OR @CanReadAll = 1);
END;
GO

/* Organization settings remain visible in preview, but target-owned settings
   (including legacy credential-migration values) are never returned. */
ALTER PROCEDURE [tb_app].[GetSettings]
    @ScopeType nvarchar(40) = NULL,
    @DeviceId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@UserSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;
    DECLARE @IsReadOnlyPreview bit =
        CONVERT(bit, CASE WHEN USER_NAME() = N'tb_preview_reader' THEN 1 ELSE 0 END);

    ;WITH settings AS
    (
        SELECT
            CONVERT(nvarchar(20), N'Organization') AS [ScopeType], [SettingKey], [SettingValue],
            [UpdatedAtUtc], [RowVersion], CONVERT(int, 1) AS [ScopePriority]
        FROM [tb_data].[OrganizationSettings]

        UNION ALL

        SELECT
            CONVERT(nvarchar(20), N'User') AS [ScopeType], [SettingKey], [SettingValue],
            [UpdatedAtUtc], [RowVersion], CONVERT(int, 2) AS [ScopePriority]
        FROM [tb_user].[UserSettings]
        WHERE [OwnerWindowsSid] = @UserSid
          AND @IsReadOnlyPreview = 0
    ),
    ranked AS
    (
        SELECT [ScopeType], [SettingKey], [SettingValue], [UpdatedAtUtc], [RowVersion],
               ROW_NUMBER() OVER (PARTITION BY [SettingKey] ORDER BY [ScopePriority] DESC) AS [Rank]
        FROM settings
    )
    SELECT [ScopeType], [SettingKey], [SettingValue], [UpdatedAtUtc] AS [UpdatedAt], [RowVersion]
    FROM ranked
    WHERE [Rank] = 1
    ORDER BY [SettingKey];
END;
GO

/* Posting payloads contain the exact rendered outbound note and can therefore
   contain Personal Notes. Error messages may echo the same content. Preview
   mode returns safe status metadata only and cannot use keyword search as a
   content-existence oracle for either protected field. */
ALTER PROCEDURE [tb_app].[GetPostingLogs]
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

    DECLARE @UserSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@UserSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;
    DECLARE @IsReadOnlyPreview bit =
        CONVERT(bit, CASE WHEN USER_NAME() = N'tb_preview_reader' THEN 1 ELSE 0 END);

    IF @IncludeAllUsers = 1 AND @IsManager <> 1 AND @IsAdmin <> 1
        THROW 51330, N'Only a Manager or Admin may read other users'' posting logs.', 1;

    SET @Limit = CASE WHEN @Limit IS NULL OR @Limit < 1 THEN 1 WHEN @Limit > 1000 THEN 1000 ELSE @Limit END;
    SET @Destination = NULLIF(LTRIM(RTRIM(@Destination)), N'');
    IF @Destination = N'Any' SET @Destination = NULL;
    SET @Keyword = NULLIF(LTRIM(RTRIM(@Keyword)), N'');
    DECLARE @KeywordPattern nvarchar(500) =
        CASE WHEN @Keyword IS NULL THEN NULL ELSE N'%' + @Keyword + N'%' END;

    SELECT TOP (@Limit)
        posting_log.[Id],
        posting_log.[WorkEntryId],
        posting_log.[Destination],
        CASE WHEN @IsReadOnlyPreview = 1 THEN N'' ELSE posting_log.[Payload] END AS [Payload],
        posting_log.[Success],
        CASE
            WHEN @IsReadOnlyPreview = 0 THEN posting_log.[Message]
            WHEN posting_log.[Success] = 1 THEN N'Posting succeeded.'
            ELSE N'Posting failed.'
        END AS [Message],
        posting_log.[ExternalReference],
        posting_log.[CreatedAtUtc] AS [CreatedAt]
    FROM [tb_ops].[PostingLogs] AS posting_log
    WHERE (@IncludeAllUsers = 1 OR posting_log.[OwnerWindowsSid] = @UserSid)
      AND (@Destination IS NULL OR posting_log.[Destination] = @Destination)
      AND (@Success IS NULL OR posting_log.[Success] = @Success)
      AND (@StartDate IS NULL OR posting_log.[CreatedAtUtc] >= @StartDate)
      AND (@EndDate IS NULL OR posting_log.[CreatedAtUtc] < DATEADD(day, 1, CONVERT(datetime2(3), @EndDate)))
      AND
      (
          @KeywordPattern IS NULL
          OR posting_log.[Destination] LIKE @KeywordPattern
          OR posting_log.[ExternalReference] LIKE @KeywordPattern
          OR CONVERT(nvarchar(30), posting_log.[WorkEntryId]) LIKE @KeywordPattern
          OR
          (
              @IsReadOnlyPreview = 0
              AND
              (
                  posting_log.[Message] LIKE @KeywordPattern
                  OR posting_log.[Payload] LIKE @KeywordPattern
              )
          )
      )
    ORDER BY posting_log.[CreatedAtUtc] DESC, posting_log.[Id] DESC;
END;
GO

IF OBJECT_ID(N'tb_app.AdminRequestSageSync', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminRequestSageSync];
GO

CREATE PROCEDURE [tb_app].[AdminRequestSageSync]
    @RequestId uniqueidentifier = NULL,
    @AllowLargeRemoval bit = 0,
    @ConfirmedRequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51920, N'Only a currently authorized TechBench Admin may request a Sage customer sync.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_data].[OrganizationSettings]
        WHERE [SettingKey] = N'Sage.SyncDsn'
          AND NULLIF(LTRIM(RTRIM([SettingValue])), N'') IS NOT NULL
    )
        THROW 51923, N'Configure the server Sage System DSN before requesting a Sage customer sync.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_data].[OrganizationSettings]
        WHERE [SettingKey] = N'Sage.SyncUsername'
          AND NULLIF(LTRIM(RTRIM([SettingValue])), N'') IS NOT NULL
    )
        THROW 51924, N'Configure the server Sage username before requesting a Sage customer sync.', 1;

    SET @RequestId = COALESCE(@RequestId, NEWID());
    SET @AllowLargeRemoval = COALESCE(@AllowLargeRemoval, 0);
    IF @AllowLargeRemoval = 0 AND @ConfirmedRequestId IS NOT NULL
        THROW 51925, N'ConfirmedRequestId is valid only for an explicit large-removal approval.', 1;
    IF @AllowLargeRemoval = 1 AND @ConfirmedRequestId IS NULL
        THROW 51926, N'Large-removal approval must reference the rejected Sage sync request whose counts were reviewed.', 1;
    DECLARE @Status nvarchar(30), @Now datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRANSACTION;

        DECLARE @QueueLockResult int;
        EXEC @QueueLockResult = sys.sp_getapplock
            @Resource=N'TechBench.Sage.CustomerSyncQueue', @LockMode=N'Exclusive',
            @LockOwner=N'Transaction', @LockTimeout=5000;
        IF @QueueLockResult < 0
            THROW 51921, N'Could not acquire the Sage customer synchronization queue lock.', 1;

        IF @AllowLargeRemoval = 1
           AND NOT EXISTS
           (
               SELECT 1
               FROM [tb_sync].[SageSyncRequests] WITH (UPDLOCK, HOLDLOCK)
               WHERE [RequestId] = @ConfirmedRequestId
                 AND [Status] = N'Failed'
                 AND [RequiresLargeRemovalConfirmation] = 1
                 AND [CompletedAtUtc] >= DATEADD(hour, -1, @Now)
           )
            THROW 51927, N'The referenced Sage removal proposal is missing, no longer eligible, or more than one hour old. Request a new unapproved sync.', 1;

        DECLARE @ExistingRequestId uniqueidentifier, @AuditEntityId nvarchar(120);
        SELECT TOP (1) @ExistingRequestId = [RequestId]
        FROM [tb_sync].[SageSyncRequests] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Status] IN (N'Queued', N'Running')
        ORDER BY [RequestedAtUtc], [RequestId];

        IF @ExistingRequestId IS NOT NULL
        BEGIN
            SET @RequestId = @ExistingRequestId;
            SET @Status = N'AlreadyQueued';
        END
        ELSE
        BEGIN
            INSERT INTO [tb_sync].[SageSyncRequests]
                (
                    [RequestId], [RequestedByWindowsSid], [RequestedAtUtc], [Status],
                    [AllowLargeRemoval], [ConfirmedRequestId]
                )
            VALUES
                (@RequestId, @ActorSid, @Now, N'Queued', @AllowLargeRemoval, @ConfirmedRequestId);
            SET @Status = N'Queued';

            SET @AuditEntityId = CONVERT(nvarchar(36), @RequestId);
            DECLARE @AuditData nvarchar(max) = CASE WHEN @AllowLargeRemoval = 1
                THEN N'{"allowLargeRemoval":true,"confirmedRequestId":"'
                     + CONVERT(nvarchar(36), @ConfirmedRequestId) + N'"}'
                ELSE N'{"allowLargeRemoval":false}' END;
            EXEC [tb_security].[WriteAuditEvent]
                @Action=N'SageCustomerSyncRequested', @EntityType=N'SageSyncRequest',
                @EntityId=@AuditEntityId, @RequestId=@RequestId, @DataJson=@AuditData;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT request_row.[RequestId], @Status AS [Status], CONVERT(int, 1) AS [QueueDepth],
           request_row.[AllowLargeRemoval], request_row.[ConfirmedRequestId]
    FROM [tb_sync].[SageSyncRequests] AS request_row
    WHERE request_row.[RequestId] = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.GetSageSyncStatus', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetSageSyncStatus];
GO

CREATE PROCEDURE [tb_app].[GetSageSyncStatus]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51922, N'Only a currently authorized TechBench Admin may view Sage sync status.', 1;

    DECLARE @QueueDepth int =
    (
        SELECT COUNT(*) FROM [tb_sync].[SageSyncRequests]
        WHERE [Status] IN (N'Queued', N'Running')
    );

    ;WITH latest AS
    (
        SELECT TOP (1)
            [RequestId], [ConfirmedRequestId], [Status], [Message], [RequestedAtUtc], [CompletedAtUtc],
            [AllowLargeRemoval], [RequiresLargeRemovalConfirmation],
            [ExistingCount], [ReadCount], [SavedCount], [StaleCount]
        FROM [tb_sync].[SageSyncRequests]
        ORDER BY [RequestedAtUtc] DESC, [RequestId] DESC
    )
    SELECT
        [RequestId], [ConfirmedRequestId], COALESCE([Status], N'NeverRun') AS [Status], [Message],
        @QueueDepth AS [QueueDepth], [RequestedAtUtc], [CompletedAtUtc],
        COALESCE([AllowLargeRemoval], 0) AS [AllowLargeRemoval],
        COALESCE([RequiresLargeRemovalConfirmation], 0) AS [RequiresLargeRemovalConfirmation],
        COALESCE([ExistingCount], 0) AS [ExistingCount],
        COALESCE([ReadCount], 0) AS [ReadCount],
        COALESCE([SavedCount], 0) AS [SavedCount],
        COALESCE([StaleCount], 0) AS [StaleCount]
    FROM latest
    RIGHT JOIN (SELECT CONVERT(bit, 1) AS [OneRow]) AS singleton ON 1 = 1;

    SELECT [LastAttemptAtUtc], [LastSuccessfulAtUtc], [LastError]
    FROM [tb_sync].[SageSyncHealth]
    WHERE [HealthId] = 1;
END;
GO

IF OBJECT_ID(N'tb_service.GetSageSyncConfiguration', N'P') IS NOT NULL
    DROP PROCEDURE [tb_service].[GetSageSyncConfiguration];
GO

CREATE PROCEDURE [tb_service].[GetSageSyncConfiguration]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SELECT
        COALESCE(MAX(CASE WHEN [SettingKey] = N'Sage.SyncDsn' THEN [SettingValue] END), N'') AS [Dsn],
        COALESCE(MAX(CASE WHEN [SettingKey] = N'Sage.SyncUsername' THEN [SettingValue] END), N'') AS [Username]
    FROM [tb_data].[OrganizationSettings]
    WHERE [SettingKey] IN (N'Sage.SyncDsn', N'Sage.SyncUsername');
END;
GO

IF OBJECT_ID(N'tb_service.ClaimSageSyncWork', N'P') IS NOT NULL
    DROP PROCEDURE [tb_service].[ClaimSageSyncWork];
GO

CREATE PROCEDURE [tb_service].[ClaimSageSyncWork]
    @WorkerId uniqueidentifier,
    @LeaseSeconds int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @WorkerId IS NULL OR @LeaseSeconds NOT BETWEEN 15 AND 3600
        THROW 51930, N'WorkerId and a lease from 15 to 3600 seconds are required.', 1;

    DECLARE @WorkId uniqueidentifier, @LeaseId uniqueidentifier = NEWID();
    DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @Until datetime2(3);
    SET @Until = DATEADD(second, @LeaseSeconds, @Now);

    BEGIN TRY
        BEGIN TRANSACTION;

        DECLARE @QueueLockResult int;
        EXEC @QueueLockResult = sys.sp_getapplock
            @Resource=N'TechBench.Sage.CustomerSyncQueue', @LockMode=N'Exclusive',
            @LockOwner=N'Transaction', @LockTimeout=5000;
        IF @QueueLockResult < 0
            THROW 51931, N'Could not acquire the Sage customer synchronization queue lock.', 1;

        SELECT TOP (1) @WorkId = request_row.[RequestId]
        FROM [tb_sync].[SageSyncRequests] AS request_row WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK, ROWLOCK)
        LEFT JOIN [tb_sync].[SageSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
            ON lease.[RequestId] = request_row.[RequestId]
        WHERE request_row.[Status] = N'Queued'
           OR (request_row.[Status] = N'Running' AND (lease.[RequestId] IS NULL OR lease.[ExpiresAtUtc] <= @Now))
        ORDER BY request_row.[RequestedAtUtc], request_row.[RequestId];

        IF @WorkId IS NOT NULL
        BEGIN
            DELETE FROM [tb_sync].[SageSyncLeases] WHERE [RequestId] = @WorkId;
            INSERT INTO [tb_sync].[SageSyncLeases]
                ([RequestId], [LeaseId], [WorkerId], [AcquiredAtUtc], [ExpiresAtUtc])
            VALUES
                (@WorkId, @LeaseId, @WorkerId, @Now, @Until);

            UPDATE [tb_sync].[SageSyncRequests]
            SET [Status] = N'Running', [StartedAtUtc] = COALESCE([StartedAtUtc], @Now),
                [CompletedAtUtc] = NULL, [AttemptCount] = [AttemptCount] + 1,
                [ExistingCount] = 0, [ReadCount] = 0, [SavedCount] = 0, [StaleCount] = 0,
                [RequiresLargeRemovalConfirmation] = 0, [Message] = NULL
            WHERE [RequestId] = @WorkId;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT request_row.[RequestId] AS [WorkId], lease.[LeaseId], lease.[WorkerId],
           lease.[ExpiresAtUtc], request_row.[AllowLargeRemoval]
    FROM [tb_sync].[SageSyncRequests] AS request_row
    INNER JOIN [tb_sync].[SageSyncLeases] AS lease ON lease.[RequestId] = request_row.[RequestId]
    WHERE request_row.[RequestId] = @WorkId;
END;
GO

IF OBJECT_ID(N'tb_service.RenewSageSyncLease', N'P') IS NOT NULL
    DROP PROCEDURE [tb_service].[RenewSageSyncLease];
GO

CREATE PROCEDURE [tb_service].[RenewSageSyncLease]
    @WorkId uniqueidentifier,
    @LeaseId uniqueidentifier,
    @WorkerId uniqueidentifier,
    @LeaseSeconds int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @WorkId IS NULL OR @LeaseId IS NULL OR @WorkerId IS NULL OR @LeaseSeconds NOT BETWEEN 15 AND 3600
        THROW 51932, N'WorkId, LeaseId, WorkerId, and a lease from 15 to 3600 seconds are required.', 1;

    DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @Until datetime2(3);
    SET @Until = DATEADD(second, @LeaseSeconds, @Now);

    UPDATE lease
    SET [ExpiresAtUtc] = @Until
    FROM [tb_sync].[SageSyncLeases] AS lease
    INNER JOIN [tb_sync].[SageSyncRequests] AS request_row
        ON request_row.[RequestId] = lease.[RequestId]
    WHERE lease.[RequestId] = @WorkId
      AND lease.[LeaseId] = @LeaseId
      AND lease.[WorkerId] = @WorkerId
      AND lease.[ExpiresAtUtc] > @Now
      AND request_row.[Status] = N'Running';

    IF @@ROWCOUNT <> 1
        THROW 51933, N'The Sage sync lease is missing, expired, or owned by another worker.', 1;

    SELECT @WorkId AS [WorkId], @LeaseId AS [LeaseId], @WorkerId AS [WorkerId], @Until AS [ExpiresAtUtc];
END;
GO

IF OBJECT_ID(N'tb_service.ApplySageCustomerSnapshot', N'P') IS NOT NULL
    DROP PROCEDURE [tb_service].[ApplySageCustomerSnapshot];
GO

CREATE PROCEDURE [tb_service].[ApplySageCustomerSnapshot]
    @WorkId uniqueidentifier,
    @LeaseId uniqueidentifier,
    @WorkerId uniqueidentifier,
    @Json nvarchar(max),
    @SyncedAtUtc datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @WorkId IS NULL OR @LeaseId IS NULL OR @WorkerId IS NULL
        THROW 51934, N'WorkId, LeaseId, and WorkerId are required.', 1;
    IF COALESCE(ISJSON(@Json), 0) <> 1
       OR LEFT(LTRIM(@Json), 1) <> N'['
       OR RIGHT(RTRIM(@Json), 1) <> N']'
       OR @SyncedAtUtc IS NULL
        THROW 51935, N'A non-empty Sage customer JSON array and SyncedAtUtc are required.', 1;

    DECLARE @ActorSid varbinary(85) =
    (
        SELECT [WindowsSid]
        FROM [tb_security].[Users]
        WHERE [LoginName] = N'$(SyncServicePrincipal)'
    );
    IF @ActorSid IS NULL
        THROW 51936, N'The configured sync service principal has no TechBench service actor.', 1;

    /* Preserve every array element until it has been validated. OPENJSON WITH
       would truncate over-length values and the old filter/ranking path could
       silently discard malformed or duplicate customers before reconciliation. */
    DECLARE @RawSnapshot TABLE
    (
        [Ordinal] int NOT NULL PRIMARY KEY,
        [JsonText] nvarchar(max) NULL,
        [JsonType] int NOT NULL
    );

    INSERT INTO @RawSnapshot([Ordinal], [JsonText], [JsonType])
    SELECT TRY_CONVERT(int, json_row.[key]), json_row.[value], json_row.[type]
    FROM OPENJSON(@Json) AS json_row;

    DECLARE @ReadCount int = (SELECT COUNT(*) FROM @RawSnapshot);
    IF @ReadCount = 0
        THROW 51937, N'The Sage customer snapshot was empty; no data was changed.', 1;
    IF EXISTS (SELECT 1 FROM @RawSnapshot WHERE [JsonType] <> 5 OR [Ordinal] IS NULL)
        THROW 51942, N'Every Sage customer snapshot element must be a JSON object; no data was changed.', 1;

    DECLARE @ExtractedSnapshot TABLE
    (
        [Ordinal] int NOT NULL PRIMARY KEY,
        [CustomerId] nvarchar(max) NULL,
        [CustomerIdCount] int NOT NULL,
        [CustomerIdType] int NULL,
        [CustomerName] nvarchar(max) NULL,
        [CustomerNameCount] int NOT NULL,
        [CustomerNameType] int NULL,
        [ContactName] nvarchar(max) NULL,
        [ContactNameCount] int NOT NULL,
        [ContactNameType] int NULL,
        [Telephone] nvarchar(max) NULL,
        [TelephoneCount] int NOT NULL,
        [TelephoneType] int NULL,
        [IsActiveText] nvarchar(max) NULL,
        [IsActiveCount] int NOT NULL,
        [IsActiveType] int NULL
    );

    INSERT INTO @ExtractedSnapshot
    (
        [Ordinal], [CustomerId], [CustomerIdCount], [CustomerIdType],
        [CustomerName], [CustomerNameCount], [CustomerNameType],
        [ContactName], [ContactNameCount], [ContactNameType],
        [Telephone], [TelephoneCount], [TelephoneType],
        [IsActiveText], [IsActiveCount], [IsActiveType]
    )
    SELECT
        raw.[Ordinal],
        MAX(CASE WHEN property_row.[key] = N'customerId' THEN property_row.[value] END),
        SUM(CASE WHEN property_row.[key] = N'customerId' THEN 1 ELSE 0 END),
        MAX(CASE WHEN property_row.[key] = N'customerId' THEN property_row.[type] END),
        MAX(CASE WHEN property_row.[key] = N'customerName' THEN property_row.[value] END),
        SUM(CASE WHEN property_row.[key] = N'customerName' THEN 1 ELSE 0 END),
        MAX(CASE WHEN property_row.[key] = N'customerName' THEN property_row.[type] END),
        MAX(CASE WHEN property_row.[key] = N'contactName' THEN property_row.[value] END),
        SUM(CASE WHEN property_row.[key] = N'contactName' THEN 1 ELSE 0 END),
        MAX(CASE WHEN property_row.[key] = N'contactName' THEN property_row.[type] END),
        MAX(CASE WHEN property_row.[key] = N'telephone' THEN property_row.[value] END),
        SUM(CASE WHEN property_row.[key] = N'telephone' THEN 1 ELSE 0 END),
        MAX(CASE WHEN property_row.[key] = N'telephone' THEN property_row.[type] END),
        MAX(CASE WHEN property_row.[key] = N'isActive' THEN property_row.[value] END),
        SUM(CASE WHEN property_row.[key] = N'isActive' THEN 1 ELSE 0 END),
        MAX(CASE WHEN property_row.[key] = N'isActive' THEN property_row.[type] END)
    FROM @RawSnapshot AS raw
    OUTER APPLY OPENJSON(raw.[JsonText]) AS property_row
    GROUP BY raw.[Ordinal];

    IF EXISTS
    (
        SELECT 1
        FROM @ExtractedSnapshot
        WHERE [CustomerIdCount] <> 1 OR [CustomerIdType] <> 1
           OR NULLIF(LTRIM(RTRIM([CustomerId])), N'') IS NULL
           OR LEN(LTRIM(RTRIM([CustomerId]))) > 120
           OR [CustomerNameCount] <> 1 OR [CustomerNameType] <> 1
           OR NULLIF(LTRIM(RTRIM([CustomerName])), N'') IS NULL
           OR LEN(LTRIM(RTRIM([CustomerName]))) > 240
           OR [ContactNameCount] > 1
           OR ([ContactNameCount] = 1 AND [ContactNameType] NOT IN (0, 1))
           OR LEN(LTRIM(RTRIM(COALESCE([ContactName], N'')))) > 240
           OR [TelephoneCount] > 1
           OR ([TelephoneCount] = 1 AND [TelephoneType] NOT IN (0, 1))
           OR LEN(LTRIM(RTRIM(COALESCE([Telephone], N'')))) > 80
           OR [IsActiveCount] <> 1 OR [IsActiveType] <> 3
           OR [IsActiveText] NOT IN (N'true', N'false')
    )
        THROW 51943, N'The Sage customer snapshot contains a missing, malformed, or over-length field; no data was changed.', 1;

    IF EXISTS
    (
        SELECT NULLIF(LTRIM(RTRIM([CustomerId])), N'')
        FROM @ExtractedSnapshot
        GROUP BY NULLIF(LTRIM(RTRIM([CustomerId])), N'')
        HAVING COUNT(*) > 1
    )
        THROW 51944, N'The Sage customer snapshot contains duplicate customer IDs; no data was changed.', 1;

    DECLARE @Snapshot TABLE
    (
        [CustomerId] nvarchar(120) NOT NULL PRIMARY KEY,
        [CustomerName] nvarchar(240) NOT NULL,
        [ContactName] nvarchar(240) NULL,
        [Telephone] nvarchar(80) NULL,
        [IsActive] bit NOT NULL
    );

    INSERT INTO @Snapshot([CustomerId], [CustomerName], [ContactName], [Telephone], [IsActive])
    SELECT
        LTRIM(RTRIM([CustomerId])),
        LTRIM(RTRIM([CustomerName])),
        NULLIF(LTRIM(RTRIM([ContactName])), N''),
        NULLIF(LTRIM(RTRIM([Telephone])), N''),
        CONVERT(bit, CASE WHEN [IsActiveText] = N'true' THEN 1 ELSE 0 END)
    FROM @ExtractedSnapshot;

    DECLARE @ExistingCount int = 0, @SavedCount int = 0, @StaleCount int = 0, @MatchedCount int = 0;
    DECLARE @AllowLargeRemoval bit = 0, @RequiresLargeRemovalConfirmation bit = 0;
    DECLARE @ConfirmedRequestId uniqueidentifier = NULL, @ConfirmationMatches bit = 0;
    DECLARE @ResultMessage nvarchar(2000) = NULL;
    DECLARE @Now datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @AllowLargeRemoval = request_row.[AllowLargeRemoval],
               @ConfirmedRequestId = request_row.[ConfirmedRequestId]
        FROM [tb_sync].[SageSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN [tb_sync].[SageSyncRequests] AS request_row WITH (UPDLOCK, HOLDLOCK)
            ON request_row.[RequestId] = lease.[RequestId]
        WHERE lease.[RequestId] = @WorkId
          AND lease.[LeaseId] = @LeaseId
          AND lease.[WorkerId] = @WorkerId
          AND lease.[ExpiresAtUtc] > @Now
          AND request_row.[Status] = N'Running';

        IF @@ROWCOUNT <> 1
            THROW 51938, N'A valid unexpired Sage sync lease is required to apply a customer snapshot.', 1;

        SELECT @ExistingCount = COUNT(*)
        FROM [tb_data].[ClientExternalIdentities] WITH (UPDLOCK, HOLDLOCK)
        WHERE [SourceSystem] = N'Sage';

        SELECT @StaleCount = COUNT(*)
        FROM [tb_data].[ClientExternalIdentities] AS identity_row WITH (UPDLOCK, HOLDLOCK)
        WHERE identity_row.[SourceSystem] = N'Sage'
          AND NOT EXISTS
          (
              SELECT 1
              FROM @Snapshot AS snapshot
              WHERE snapshot.[CustomerId] = identity_row.[ExternalId]
          );

        IF @AllowLargeRemoval = 1
           AND @ConfirmedRequestId IS NOT NULL
           AND EXISTS
           (
               SELECT 1
               FROM [tb_sync].[SageSyncRequests] AS confirmed_request WITH (UPDLOCK, HOLDLOCK)
               WHERE confirmed_request.[RequestId] = @ConfirmedRequestId
                 AND confirmed_request.[Status] = N'Failed'
                 AND confirmed_request.[RequiresLargeRemovalConfirmation] = 1
                 AND confirmed_request.[CompletedAtUtc] >= DATEADD(hour, -1, @Now)
                 AND confirmed_request.[ExistingCount] = @ExistingCount
                 AND confirmed_request.[ReadCount] = @ReadCount
                 AND confirmed_request.[StaleCount] = @StaleCount
           )
            SET @ConfirmationMatches = 1;

        /* First imports and small cleanups proceed normally. A snapshot that
           would remove at least ten and at least 25 percent of an established
           Sage identity set requires a new, explicitly confirmed Admin request. */
        IF @ConfirmationMatches <> 1
           AND @ExistingCount >= 20
           AND @StaleCount >= 10
           AND CONVERT(bigint, @StaleCount) * 100 >= CONVERT(bigint, @ExistingCount) * 25
        BEGIN
            SET @RequiresLargeRemovalConfirmation = 1;
            SET @ResultMessage =
                N'Sage returned ' + CONVERT(nvarchar(20), @ReadCount)
                + N' active customer(s), which would remove '
                + CONVERT(nvarchar(20), @StaleCount) + N' of '
                + CONVERT(nvarchar(20), @ExistingCount)
                + N' existing Sage customer mapping(s). No customer data was changed. An Admin must explicitly confirm these exact counts; any changed rerun requires a new confirmation.';

            UPDATE [tb_sync].[SageSyncRequests]
            SET [ExistingCount] = @ExistingCount, [ReadCount] = @ReadCount,
                [SavedCount] = 0, [StaleCount] = @StaleCount,
                [RequiresLargeRemovalConfirmation] = 1, [Message] = @ResultMessage
            WHERE [RequestId] = @WorkId AND [Status] = N'Running';

            COMMIT TRANSACTION;

            SELECT @ReadCount AS [ReadCount], CONVERT(int, 0) AS [SavedCount],
                   @StaleCount AS [StaleCount], CONVERT(int, 0) AS [MatchedCount],
                   @ExistingCount AS [ExistingCount],
                   @RequiresLargeRemovalConfirmation AS [RequiresLargeRemovalConfirmation],
                   @ResultMessage AS [Message];
            RETURN;
        END;

        /* Upgrade legacy Sage columns into the canonical identity table before
           matching the server snapshot. One canonical client wins per ID. */
        ;WITH legacy_candidates AS
        (
            SELECT
                client.[Id] AS [ClientId], client.[SageCustomerId],
                ROW_NUMBER() OVER (PARTITION BY client.[SageCustomerId] ORDER BY client.[Id]) AS [RowNumber]
            FROM [tb_data].[Clients] AS client
            INNER JOIN @Snapshot AS snapshot ON snapshot.[CustomerId] = client.[SageCustomerId]
            WHERE NULLIF(LTRIM(RTRIM(client.[SageCustomerId])), N'') IS NOT NULL
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM [tb_data].[ClientExternalIdentities] AS existing WITH (UPDLOCK, HOLDLOCK)
                  WHERE existing.[SourceSystem] = N'Sage'
                    AND existing.[ExternalId] = client.[SageCustomerId]
              )
        )
        INSERT INTO [tb_data].[ClientExternalIdentities]
        (
            [ClientId], [SourceSystem], [ExternalId], [ExternalName], [LastSyncedAtUtc],
            [CreatedByWindowsSid], [UpdatedByWindowsSid], [CreatedAtUtc], [UpdatedAtUtc]
        )
        SELECT
            legacy.[ClientId], N'Sage', snapshot.[CustomerId], snapshot.[CustomerName], @SyncedAtUtc,
            @ActorSid, @ActorSid, @Now, @Now
        FROM legacy_candidates AS legacy
        INNER JOIN @Snapshot AS snapshot ON snapshot.[CustomerId] = legacy.[SageCustomerId]
        WHERE legacy.[RowNumber] = 1;

        UPDATE identity_row
        SET [ExternalName] = snapshot.[CustomerName], [LastSyncedAtUtc] = @SyncedAtUtc,
            [UpdatedByWindowsSid] = @ActorSid, [UpdatedAtUtc] = @Now
        FROM [tb_data].[ClientExternalIdentities] AS identity_row
        INNER JOIN @Snapshot AS snapshot ON snapshot.[CustomerId] = identity_row.[ExternalId]
        WHERE identity_row.[SourceSystem] = N'Sage';

        DECLARE @NewClients TABLE
        (
            [CustomerId] nvarchar(120) NOT NULL PRIMARY KEY,
            [ClientId] int NOT NULL
        );

        INSERT INTO [tb_data].[Clients]
        (
            [Name], [Source], [ExternalId], [IsActive], [LastSyncedAtUtc],
            [SageCustomerId], [SageCustomerName], [SageContactName], [SageTelephone],
            [MatchStatus], [CreatedByWindowsSid], [UpdatedByWindowsSid],
            [CreatedAtUtc], [UpdatedAtUtc]
        )
        OUTPUT inserted.[SageCustomerId], inserted.[Id]
            INTO @NewClients([CustomerId], [ClientId])
        SELECT
            snapshot.[CustomerName], N'Sage', snapshot.[CustomerId], snapshot.[IsActive], @SyncedAtUtc,
            snapshot.[CustomerId], snapshot.[CustomerName], snapshot.[ContactName], snapshot.[Telephone],
            N'Unmatched', @ActorSid, @ActorSid, @Now, @Now
        FROM @Snapshot AS snapshot
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[ClientExternalIdentities] AS existing WITH (UPDLOCK, HOLDLOCK)
            WHERE existing.[SourceSystem] = N'Sage'
              AND existing.[ExternalId] = snapshot.[CustomerId]
        );

        INSERT INTO [tb_data].[ClientExternalIdentities]
        (
            [ClientId], [SourceSystem], [ExternalId], [ExternalName], [LastSyncedAtUtc],
            [CreatedByWindowsSid], [UpdatedByWindowsSid], [CreatedAtUtc], [UpdatedAtUtc]
        )
        SELECT
            new_client.[ClientId], N'Sage', snapshot.[CustomerId], snapshot.[CustomerName], @SyncedAtUtc,
            @ActorSid, @ActorSid, @Now, @Now
        FROM @NewClients AS new_client
        INNER JOIN @Snapshot AS snapshot ON snapshot.[CustomerId] = new_client.[CustomerId];

        UPDATE client
        SET
            [Name] = CASE WHEN whd_identity.[ClientId] IS NULL THEN snapshot.[CustomerName] ELSE client.[Name] END,
            [Source] = CASE WHEN whd_identity.[ClientId] IS NULL THEN N'Sage' ELSE N'Both' END,
            [ExternalId] = CASE WHEN whd_identity.[ClientId] IS NULL THEN snapshot.[CustomerId] ELSE client.[ExternalId] END,
            [IsActive] = CASE WHEN whd_identity.[ClientId] IS NULL THEN snapshot.[IsActive] ELSE client.[IsActive] END,
            [LastSyncedAtUtc] = @SyncedAtUtc,
            [SageCustomerId] = snapshot.[CustomerId], [SageCustomerName] = snapshot.[CustomerName],
            [SageContactName] = snapshot.[ContactName], [SageTelephone] = snapshot.[Telephone],
            [MatchStatus] = CASE WHEN whd_identity.[ClientId] IS NULL THEN N'Unmatched' ELSE N'Matched' END,
            [UpdatedByWindowsSid] = @ActorSid, [UpdatedAtUtc] = @Now
        FROM [tb_data].[Clients] AS client
        INNER JOIN [tb_data].[ClientExternalIdentities] AS sage_identity
            ON sage_identity.[ClientId] = client.[Id] AND sage_identity.[SourceSystem] = N'Sage'
        INNER JOIN @Snapshot AS snapshot ON snapshot.[CustomerId] = sage_identity.[ExternalId]
        OUTER APPLY
        (
            SELECT TOP (1) whd.[ClientId]
            FROM [tb_data].[ClientExternalIdentities] AS whd
            WHERE whd.[ClientId] = client.[Id] AND whd.[SourceSystem] = N'WHD'
        ) AS whd_identity;

        SET @SavedCount = @ReadCount;

        DECLARE @StaleIdentities TABLE
        (
            [IdentityId] bigint NOT NULL PRIMARY KEY,
            [ClientId] int NOT NULL
        );

        INSERT INTO @StaleIdentities([IdentityId], [ClientId])
        SELECT identity_row.[Id], identity_row.[ClientId]
        FROM [tb_data].[ClientExternalIdentities] AS identity_row
        WHERE identity_row.[SourceSystem] = N'Sage'
          AND NOT EXISTS
          (
              SELECT 1 FROM @Snapshot AS snapshot
              WHERE snapshot.[CustomerId] = identity_row.[ExternalId]
          );
        SET @StaleCount = @@ROWCOUNT;

        DELETE identity_row
        FROM [tb_data].[ClientExternalIdentities] AS identity_row
        INNER JOIN @StaleIdentities AS stale ON stale.[IdentityId] = identity_row.[Id];

        ;WITH removed_clients AS
        (
            SELECT DISTINCT stale.[ClientId]
            FROM @StaleIdentities AS stale
            WHERE NOT EXISTS
            (
                SELECT 1 FROM [tb_data].[ClientExternalIdentities] AS remaining_sage
                WHERE remaining_sage.[ClientId] = stale.[ClientId]
                  AND remaining_sage.[SourceSystem] = N'Sage'
            )
        )
        UPDATE client
        SET
            [Source] = CASE WHEN whd_identity.[ClientId] IS NULL THEN N'Sage' ELSE N'WHD' END,
            [IsActive] = CASE WHEN whd_identity.[ClientId] IS NULL THEN CONVERT(bit, 0) ELSE client.[IsActive] END,
            [SageCustomerId] = NULL, [SageCustomerName] = NULL,
            [SageContactName] = NULL, [SageTelephone] = NULL,
            [MatchStatus] = N'Unmatched', [LastSyncedAtUtc] = @SyncedAtUtc,
            [UpdatedByWindowsSid] = @ActorSid, [UpdatedAtUtc] = @Now
        FROM [tb_data].[Clients] AS client
        INNER JOIN removed_clients AS removed ON removed.[ClientId] = client.[Id]
        OUTER APPLY
        (
            SELECT TOP (1) whd.[ClientId]
            FROM [tb_data].[ClientExternalIdentities] AS whd
            WHERE whd.[ClientId] = client.[Id] AND whd.[SourceSystem] = N'WHD'
        ) AS whd_identity;

        SELECT @MatchedCount = COUNT(DISTINCT client.[Id])
        FROM [tb_data].[Clients] AS client
        INNER JOIN [tb_data].[ClientExternalIdentities] AS sage_identity
            ON sage_identity.[ClientId] = client.[Id] AND sage_identity.[SourceSystem] = N'Sage'
        INNER JOIN @Snapshot AS snapshot ON snapshot.[CustomerId] = sage_identity.[ExternalId]
        WHERE client.[Source] = N'Both';

        UPDATE [tb_sync].[SageSyncRequests]
        SET [ExistingCount] = @ExistingCount, [ReadCount] = @ReadCount,
            [SavedCount] = @SavedCount, [StaleCount] = @StaleCount,
            [RequiresLargeRemovalConfirmation] = 0, [Message] = NULL
        WHERE [RequestId] = @WorkId AND [Status] = N'Running';

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT @ReadCount AS [ReadCount], @SavedCount AS [SavedCount],
           @StaleCount AS [StaleCount], @MatchedCount AS [MatchedCount],
           @ExistingCount AS [ExistingCount],
           @RequiresLargeRemovalConfirmation AS [RequiresLargeRemovalConfirmation],
           @ResultMessage AS [Message];
END;
GO

IF OBJECT_ID(N'tb_service.CompleteSageSyncWork', N'P') IS NOT NULL
    DROP PROCEDURE [tb_service].[CompleteSageSyncWork];
GO

CREATE PROCEDURE [tb_service].[CompleteSageSyncWork]
    @WorkId uniqueidentifier,
    @LeaseId uniqueidentifier,
    @WorkerId uniqueidentifier,
    @Succeeded bit,
    @Message nvarchar(2000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @WorkId IS NULL OR @LeaseId IS NULL OR @WorkerId IS NULL OR @Succeeded IS NULL
        THROW 51939, N'WorkId, LeaseId, WorkerId, and Succeeded are required.', 1;

    SET @Message = NULLIF(LTRIM(RTRIM(@Message)), N'');
    DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @ReadCount int;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @ReadCount = request_row.[ReadCount]
        FROM [tb_sync].[SageSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN [tb_sync].[SageSyncRequests] AS request_row WITH (UPDLOCK, HOLDLOCK)
            ON request_row.[RequestId] = lease.[RequestId]
        WHERE lease.[RequestId] = @WorkId
          AND lease.[LeaseId] = @LeaseId
          AND lease.[WorkerId] = @WorkerId
          AND lease.[ExpiresAtUtc] > @Now
          AND request_row.[Status] = N'Running';

        IF @ReadCount IS NULL
            THROW 51940, N'A valid unexpired Sage sync lease is required to complete this work.', 1;
        IF @Succeeded = 1 AND @ReadCount = 0
            THROW 51941, N'A Sage sync cannot succeed before a non-empty customer snapshot is applied.', 1;

        UPDATE [tb_sync].[SageSyncRequests]
        SET [Status] = CASE WHEN @Succeeded = 1 THEN N'Completed' ELSE N'Failed' END,
            [CompletedAtUtc] = @Now,
            [Message] = CASE WHEN @Succeeded = 1 THEN @Message ELSE COALESCE(@Message, N'Sage customer synchronization failed.') END
        WHERE [RequestId] = @WorkId;

        UPDATE [tb_sync].[SageSyncHealth]
        SET [LastAttemptAtUtc] = @Now,
            [LastSuccessfulAtUtc] = CASE WHEN @Succeeded = 1 THEN @Now ELSE [LastSuccessfulAtUtc] END,
            [LastError] = CASE WHEN @Succeeded = 1 THEN NULL ELSE COALESCE(@Message, N'Sage customer synchronization failed.') END,
            [UpdatedAtUtc] = @Now
        WHERE [HealthId] = 1;

        DELETE FROM [tb_sync].[SageSyncLeases]
        WHERE [RequestId] = @WorkId AND [LeaseId] = @LeaseId AND [WorkerId] = @WorkerId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT [RequestId] AS [WorkId], [Status], [Message],
           [ReadCount], [SavedCount], [StaleCount], [CompletedAtUtc]
    FROM [tb_sync].[SageSyncRequests]
    WHERE [RequestId] = @WorkId;
END;
GO

/* Rebuild WHD row-level security so a valid preview is scoped to the target
   technician and the Admin's ordinary bypass cannot win after impersonation.
   All DDL participates in one transaction: any ALTER FUNCTION or CREATE
   POLICY failure rolls the original enabled policy back instead of leaving a
   fail-open interval after DROP SECURITY POLICY. */
IF OBJECT_ID(N'tb_security.FilterWhdTicketAccess', N'IF') IS NULL
    THROW 51950, N'The V0006 WHD ticket access function is missing.', 1;

DECLARE @RlsFunctionSql nvarchar(max) = N'
ALTER FUNCTION [tb_security].[FilterWhdTicketAccess]
(
    @Source nvarchar(40),
    @AssignedTechExternalId nvarchar(120),
    @AssignedGroupExternalId nvarchar(120)
)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
(
    SELECT CONVERT(bit, 1) AS [AccessAllowed]
    WHERE @Source <> N''WHD''
       OR
       (
           USER_NAME() = N''tb_preview_reader''
           AND EXISTS
           (
               SELECT 1
               FROM [tb_security].[AdminUserPreviewSessions] AS preview_session
               INNER JOIN [tb_security].[Users] AS actor_user
                   ON actor_user.[WindowsSid] = preview_session.[ActorWindowsSid]
               INNER JOIN [tb_security].[Users] AS target_user
                   ON target_user.[WindowsSid] = preview_session.[TargetWindowsSid]
               INNER JOIN [tb_whd].[UserTechnicianMappings] AS mapping
                   ON mapping.[WindowsSid] = preview_session.[TargetWindowsSid]
               WHERE preview_session.[PreviewSessionId] = TRY_CONVERT
                     (
                         uniqueidentifier,
                         CONVERT(nvarchar(36), SESSION_CONTEXT(N''TechBench.PreviewSessionId''))
                     )
                 AND preview_session.[ActorWindowsSid] = SUSER_SID(ORIGINAL_LOGIN())
                 AND preview_session.[EndedAtUtc] IS NULL
                 AND preview_session.[ExpiresAtUtc] > SYSUTCDATETIME()
                 AND actor_user.[IsAdmin] = 1
                 AND target_user.[IsTechnician] = 1
                 AND target_user.[IsAdmin] = 0
                 AND target_user.[LastSeenAtUtc] >= DATEADD(hour, -1, SYSUTCDATETIME())
                 AND
                 (
                     mapping.[TechnicianExternalId] = @AssignedTechExternalId
                     OR EXISTS
                     (
                         SELECT 1
                         FROM [tb_whd].[TechnicianGroupMemberships] AS membership
                         WHERE membership.[TechnicianExternalId] = mapping.[TechnicianExternalId]
                           AND membership.[GroupExternalId] = @AssignedGroupExternalId
                     )
                 )
           )
       )
       OR
       (
           USER_NAME() <> N''tb_preview_reader''
           AND SESSION_CONTEXT(N''TechBench.PreviewSessionId'') IS NULL
           AND
           (
               USER_NAME() = N''dbo''
               OR IS_ROLEMEMBER(N''db_owner'') = 1
               OR IS_ROLEMEMBER(N''tb_role_admin'') = 1
               OR IS_ROLEMEMBER(N''tb_role_sync_service'') = 1
               OR EXISTS
               (
                   SELECT 1
                   FROM [tb_whd].[UserTechnicianMappings] AS mapping
                   WHERE mapping.[WindowsSid] = SUSER_SID(ORIGINAL_LOGIN())
                     AND
                     (
                         mapping.[TechnicianExternalId] = @AssignedTechExternalId
                         OR EXISTS
                         (
                             SELECT 1
                             FROM [tb_whd].[TechnicianGroupMemberships] AS membership
                             WHERE membership.[TechnicianExternalId] = mapping.[TechnicianExternalId]
                               AND membership.[GroupExternalId] = @AssignedGroupExternalId
                         )
                     )
               )
           )
       )
);';

DECLARE @RlsPolicySql nvarchar(max) = N'
CREATE SECURITY POLICY [tb_security].[WhdTicketAccessPolicy]
    ADD FILTER PREDICATE [tb_security].[FilterWhdTicketAccess]
        ([Source], [AssignedTechExternalId], [AssignedGroupExternalId])
        ON [tb_data].[Tickets],
    ADD BLOCK PREDICATE [tb_security].[FilterWhdTicketAccess]
        ([Source], [AssignedTechExternalId], [AssignedGroupExternalId])
        ON [tb_data].[Tickets] AFTER INSERT,
    ADD BLOCK PREDICATE [tb_security].[FilterWhdTicketAccess]
        ([Source], [AssignedTechExternalId], [AssignedGroupExternalId])
        ON [tb_data].[Tickets] AFTER UPDATE
    WITH (STATE = ON, SCHEMABINDING = ON);';

BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS
    (
        SELECT 1
        FROM sys.security_policies AS policy
        INNER JOIN sys.schemas AS schema_row ON schema_row.[schema_id] = policy.[schema_id]
        WHERE schema_row.[name] = N'tb_security'
          AND policy.[name] = N'WhdTicketAccessPolicy'
    )
    BEGIN
        EXEC sys.sp_executesql
            N'ALTER SECURITY POLICY [tb_security].[WhdTicketAccessPolicy] WITH (STATE = OFF);';
        EXEC sys.sp_executesql
            N'DROP SECURITY POLICY [tb_security].[WhdTicketAccessPolicy];';
    END;

    EXEC sys.sp_executesql @RlsFunctionSql;
    EXEC sys.sp_executesql @RlsPolicySql;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO

PRINT N'TechBench V0007 server-owned Sage sync and read-only Admin preview procedures created.';
GO
