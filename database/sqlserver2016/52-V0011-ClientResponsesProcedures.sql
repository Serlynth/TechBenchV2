:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

ALTER PROCEDURE [tb_app].[AcknowledgeClientSessionCommand]
    @SessionId uniqueidentifier,
    @CommandId bigint,
    @Result nvarchar(40),
    @ResponseMessage nvarchar(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@UserSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;

    SET @Result = NULLIF(LTRIM(RTRIM(@Result)), N'');
    SET @ResponseMessage = NULLIF(LTRIM(RTRIM(@ResponseMessage)), N'');
    IF @Result NOT IN
        (
            N'Displayed', N'Acknowledged', N'Dismissed',
            N'SignedOut', N'Ignored', N'Failed', N'SaveFailed'
        )
        THROW 52108, N'The client command acknowledgement result is invalid.', 1;
    IF @Result IN (N'Acknowledged', N'Dismissed', N'SignedOut', N'SaveFailed')
       AND @ResponseMessage IS NULL
        THROW 52110, N'This client command response requires a response message.', 1;

    UPDATE command
    SET
        [DeliveredAtUtc] = COALESCE(command.[DeliveredAtUtc], SYSUTCDATETIME()),
        [AcknowledgedAtUtc] = SYSUTCDATETIME(),
        [AcknowledgementResult] = @Result,
        [ResponseMessage] = @ResponseMessage
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

IF OBJECT_ID(N'tb_app.AdminGetRecentClientSessionResponses', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminGetRecentClientSessionResponses];
GO

CREATE PROCEDURE [tb_app].[AdminGetRecentClientSessionResponses]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 52111, N'Only a TechBench Admin may view client responses.', 1;

    SELECT TOP (100)
        command.[CommandId],
        command.[SessionId],
        target_user.[LoginName],
        target_user.[DisplayName],
        session.[MachineName],
        command.[CommandType],
        command.[Message] AS [OriginalMessage],
        command.[AcknowledgementResult],
        COALESCE(command.[ResponseMessage], N'') AS [ResponseMessage],
        requester.[DisplayName] AS [RequestedBy],
        command.[RequestedAtUtc],
        command.[AcknowledgedAtUtc]
    FROM [tb_security].[ClientSessionCommands] AS command
    INNER JOIN [tb_security].[ClientSessions] AS session
        ON session.[SessionId] = command.[SessionId]
    INNER JOIN [tb_security].[Users] AS target_user
        ON target_user.[WindowsSid] = session.[WindowsSid]
    INNER JOIN [tb_security].[Users] AS requester
        ON requester.[WindowsSid] = command.[RequestedByWindowsSid]
    WHERE command.[AcknowledgedAtUtc] IS NOT NULL
      AND command.[AcknowledgedAtUtc] >= DATEADD(DAY, -7, SYSUTCDATETIME())
    ORDER BY command.[AcknowledgedAtUtc] DESC, command.[CommandId] DESC;
END;
GO

PRINT N'TechBench V0011 client response procedures created.';
GO
