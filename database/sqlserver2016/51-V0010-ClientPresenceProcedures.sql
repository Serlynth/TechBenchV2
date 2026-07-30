:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'tb_app.HeartbeatClientSession', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[HeartbeatClientSession];
GO

CREATE PROCEDURE [tb_app].[HeartbeatClientSession]
    @SessionId uniqueidentifier,
    @DeviceId uniqueidentifier,
    @MachineName nvarchar(128),
    @ClientVersion nvarchar(40),
    @CurrentSection nvarchar(80) = NULL,
    @HasUnsavedChanges bit = 0,
    @IsBusy bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@UserSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;

    SET @MachineName = NULLIF(LTRIM(RTRIM(@MachineName)), N'');
    SET @ClientVersion = NULLIF(LTRIM(RTRIM(@ClientVersion)), N'');
    SET @CurrentSection = NULLIF(LTRIM(RTRIM(@CurrentSection)), N'');
    IF @SessionId IS NULL OR @DeviceId IS NULL
       OR @MachineName IS NULL OR @ClientVersion IS NULL
        THROW 52100, N'Session, device, machine, and client version are required.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    UPDATE [tb_security].[ClientSessions]
    SET
        [DeviceId] = @DeviceId,
        [MachineName] = @MachineName,
        [ClientVersion] = @ClientVersion,
        [CurrentSection] = @CurrentSection,
        [HasUnsavedChanges] = @HasUnsavedChanges,
        [IsBusy] = @IsBusy,
        [LastSeenAtUtc] = @NowUtc,
        [ClosedAtUtc] = NULL
    WHERE [SessionId] = @SessionId
      AND [WindowsSid] = @UserSid;

    IF @@ROWCOUNT = 0
    BEGIN
        IF EXISTS
        (
            SELECT 1
            FROM [tb_security].[ClientSessions]
            WHERE [SessionId] = @SessionId
              AND [WindowsSid] <> @UserSid
        )
            THROW 52101, N'This TechBench client session belongs to another user.', 1;

        INSERT INTO [tb_security].[ClientSessions]
        (
            [SessionId], [WindowsSid], [DeviceId], [MachineName],
            [ClientVersion], [CurrentSection], [HasUnsavedChanges],
            [IsBusy], [StartedAtUtc], [LastSeenAtUtc], [ClosedAtUtc]
        )
        VALUES
        (
            @SessionId, @UserSid, @DeviceId, @MachineName,
            @ClientVersion, @CurrentSection, @HasUnsavedChanges,
            @IsBusy, @NowUtc, @NowUtc, NULL
        );
    END;

    DECLARE
        @CommandId bigint,
        @CommandType nvarchar(30),
        @Message nvarchar(500),
        @RequestedBy nvarchar(256),
        @RequestedAtUtc datetime2(3);

    SELECT TOP (1)
        @CommandId = command.[CommandId],
        @CommandType = command.[CommandType],
        @Message = command.[Message],
        @RequestedBy = requester.[DisplayName],
        @RequestedAtUtc = command.[RequestedAtUtc]
    FROM [tb_security].[ClientSessionCommands] AS command
    INNER JOIN [tb_security].[Users] AS requester
        ON requester.[WindowsSid] = command.[RequestedByWindowsSid]
    WHERE command.[SessionId] = @SessionId
      AND command.[AcknowledgedAtUtc] IS NULL
    ORDER BY command.[CommandId];

    IF @CommandId IS NOT NULL
    BEGIN
        UPDATE [tb_security].[ClientSessionCommands]
        SET [DeliveredAtUtc] = COALESCE([DeliveredAtUtc], @NowUtc)
        WHERE [CommandId] = @CommandId;
    END;

    SELECT
        @NowUtc AS [ServerUtc],
        @CommandId AS [CommandId],
        @SessionId AS [SessionId],
        @CommandType AS [CommandType],
        @Message AS [Message],
        @RequestedBy AS [RequestedBy],
        @RequestedAtUtc AS [RequestedAtUtc];
END;
GO

IF OBJECT_ID(N'tb_app.AdminGetActiveClientSessions', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminGetActiveClientSessions];
GO

CREATE PROCEDURE [tb_app].[AdminGetActiveClientSessions]
    @CurrentSessionId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 52102, N'Only a TechBench Admin may view active client sessions.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    SELECT
        session.[SessionId],
        user_row.[LoginName],
        user_row.[DisplayName],
        user_row.[IsAdmin],
        session.[MachineName],
        session.[ClientVersion],
        COALESCE(session.[CurrentSection], N'') AS [CurrentSection],
        session.[HasUnsavedChanges],
        session.[IsBusy],
        session.[StartedAtUtc],
        session.[LastSeenAtUtc],
        CONVERT(bit, CASE WHEN session.[SessionId] = @CurrentSessionId THEN 1 ELSE 0 END)
            AS [IsCurrentSession]
    FROM [tb_security].[ClientSessions] AS session
    INNER JOIN [tb_security].[Users] AS user_row
        ON user_row.[WindowsSid] = session.[WindowsSid]
    WHERE session.[ClosedAtUtc] IS NULL
      AND session.[LastSeenAtUtc] >= DATEADD(SECOND, -90, @NowUtc)
    ORDER BY user_row.[DisplayName], session.[MachineName], session.[StartedAtUtc];
END;
GO

IF OBJECT_ID(N'tb_app.AdminQueueClientSessionCommand', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminQueueClientSessionCommand];
GO

CREATE PROCEDURE [tb_app].[AdminQueueClientSessionCommand]
    @RequesterSessionId uniqueidentifier,
    @TargetSessionId uniqueidentifier,
    @CommandType nvarchar(30),
    @Message nvarchar(500),
    @RequestId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 52103, N'Only a TechBench Admin may send client session commands.', 1;

    SET @CommandType = NULLIF(LTRIM(RTRIM(@CommandType)), N'');
    SET @Message = NULLIF(LTRIM(RTRIM(@Message)), N'');
    IF @RequesterSessionId IS NULL OR @TargetSessionId IS NULL OR @RequestId IS NULL
       OR @CommandType NOT IN (N'UpdateNotice', N'SignOut')
       OR @Message IS NULL
        THROW 52104, N'A valid requester, target, command type, message, and request ID are required.', 1;
    IF @RequesterSessionId = @TargetSessionId
        THROW 52105, N'Use the normal Exit command to close your own TechBench session.', 1;
    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_security].[ClientSessions]
        WHERE [SessionId] = @RequesterSessionId
          AND [WindowsSid] = @ActorSid
          AND [ClosedAtUtc] IS NULL
          AND [LastSeenAtUtc] >= DATEADD(SECOND, -90, SYSUTCDATETIME())
    )
        THROW 52106, N'The requesting Admin client session is no longer active.', 1;
    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_security].[ClientSessions]
        WHERE [SessionId] = @TargetSessionId
          AND [ClosedAtUtc] IS NULL
          AND [LastSeenAtUtc] >= DATEADD(SECOND, -90, SYSUTCDATETIME())
    )
        THROW 52107, N'The selected TechBench client is no longer active.', 1;

    DECLARE @CommandId bigint;
    SELECT @CommandId = [CommandId]
    FROM [tb_security].[ClientSessionCommands]
    WHERE [RequestId] = @RequestId;

    IF @CommandId IS NULL
    BEGIN
        INSERT INTO [tb_security].[ClientSessionCommands]
        (
            [SessionId], [CommandType], [Message],
            [RequestedByWindowsSid], [RequestId]
        )
        VALUES
        (
            @TargetSessionId, @CommandType, @Message,
            @ActorSid, @RequestId
        );
        SET @CommandId = SCOPE_IDENTITY();
    END;

    SELECT
        command.[CommandId],
        command.[SessionId],
        command.[CommandType],
        command.[Message],
        requester.[DisplayName] AS [RequestedBy],
        command.[RequestedAtUtc]
    FROM [tb_security].[ClientSessionCommands] AS command
    INNER JOIN [tb_security].[Users] AS requester
        ON requester.[WindowsSid] = command.[RequestedByWindowsSid]
    WHERE command.[CommandId] = @CommandId;
END;
GO

IF OBJECT_ID(N'tb_app.AcknowledgeClientSessionCommand', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AcknowledgeClientSessionCommand];
GO

CREATE PROCEDURE [tb_app].[AcknowledgeClientSessionCommand]
    @SessionId uniqueidentifier,
    @CommandId bigint,
    @Result nvarchar(40)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@UserSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;
    SET @Result = NULLIF(LTRIM(RTRIM(@Result)), N'');
    IF @Result NOT IN (N'Displayed', N'SignedOut', N'Ignored', N'Failed')
        THROW 52108, N'The client command acknowledgement result is invalid.', 1;

    UPDATE command
    SET
        [DeliveredAtUtc] = COALESCE(command.[DeliveredAtUtc], SYSUTCDATETIME()),
        [AcknowledgedAtUtc] = SYSUTCDATETIME(),
        [AcknowledgementResult] = @Result
    FROM [tb_security].[ClientSessionCommands] AS command
    INNER JOIN [tb_security].[ClientSessions] AS session
        ON session.[SessionId] = command.[SessionId]
    WHERE command.[CommandId] = @CommandId
      AND command.[SessionId] = @SessionId
      AND session.[WindowsSid] = @UserSid;

    IF @@ROWCOUNT = 0
        THROW 52109, N'The client command was not found for the current user session.', 1;
END;
GO

IF OBJECT_ID(N'tb_app.CloseClientSession', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[CloseClientSession];
GO

CREATE PROCEDURE [tb_app].[CloseClientSession]
    @SessionId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@UserSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;

    UPDATE [tb_security].[ClientSessions]
    SET
        [ClosedAtUtc] = COALESCE([ClosedAtUtc], SYSUTCDATETIME()),
        [LastSeenAtUtc] = SYSUTCDATETIME(),
        [IsBusy] = 0
    WHERE [SessionId] = @SessionId
      AND [WindowsSid] = @UserSid;
END;
GO

PRINT N'TechBench V0010 client presence procedures created.';
GO
