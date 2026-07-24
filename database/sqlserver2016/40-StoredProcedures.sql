:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'tb_security.EnsureCurrentUser', N'P') IS NOT NULL
    DROP PROCEDURE [tb_security].[EnsureCurrentUser];
GO

CREATE PROCEDURE [tb_security].[EnsureCurrentUser]
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

    SET @UserSid = SUSER_SID(ORIGINAL_LOGIN());
    SET @LoginName = CONVERT(nvarchar(256), ORIGINAL_LOGIN());
    SET @IsTechnician =
        CONVERT(bit, CASE WHEN IS_ROLEMEMBER(N'tb_role_user') = 1 THEN 1 ELSE 0 END);
    SET @IsManager =
        CONVERT(bit, CASE WHEN IS_ROLEMEMBER(N'tb_role_manager') = 1 THEN 1 ELSE 0 END);
    SET @IsAdmin =
        CONVERT(bit, CASE WHEN IS_ROLEMEMBER(N'tb_role_admin') = 1 THEN 1 ELSE 0 END);
    SET @IsSyncOperator =
        CONVERT(
            bit,
            CASE WHEN IS_ROLEMEMBER(N'tb_role_sync_operator') = 1 THEN 1 ELSE 0 END);

    IF @UserSid IS NULL
       OR DATALENGTH(@UserSid) NOT BETWEEN 8 AND 85
       OR NULLIF(LTRIM(RTRIM(@LoginName)), N'') IS NULL
    BEGIN
        THROW 51000, N'SQL Server did not provide a valid authenticated Windows identity.', 1;
    END;

    IF @IsTechnician = 0
       AND @IsManager = 0
       AND @IsAdmin = 0
       AND @IsSyncOperator = 0
    BEGIN
        THROW 51002, N'The Windows login is not assigned to a TechBench application role.', 1;
    END;

    IF @IsAdmin = 1
    BEGIN
        SET @IsManager = 1;
        SET @IsTechnician = 1;
    END
    ELSE IF @IsManager = 1
    BEGIN
        SET @IsTechnician = 1;
    END;

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
                                    THEN RIGHT(
                                        [LoginName],
                                        LEN([LoginName]) - CHARINDEX(N'\', [LoginName]))
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

        IF @@ROWCOUNT = 0
        BEGIN
            INSERT INTO [tb_security].[Users]
            (
                [WindowsSid],
                [LoginName],
                [DisplayName],
                [IsTechnician],
                [IsManager],
                [IsAdmin],
                [IsSyncOperator]
            )
            VALUES
            (
                @UserSid,
                @LoginName,
                @DisplayName,
                @IsTechnician,
                @IsManager,
                @IsAdmin,
                @IsSyncOperator
            );
        END;

        SELECT
            @DisplayName = [DisplayName]
        FROM [tb_security].[Users]
        WHERE [WindowsSid] = @UserSid;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_app.GetCurrentUserContext', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetCurrentUserContext];
GO

CREATE PROCEDURE [tb_app].[GetCurrentUserContext]
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
    DECLARE @DatabaseInstanceId uniqueidentifier;
    DECLARE @SchemaVersion int;

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

    IF @DatabaseInstanceId IS NULL OR @SchemaVersion IS NULL
    BEGIN
        THROW 51020, N'The TechBench database metadata is incomplete.', 1;
    END;

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
        @IsSyncOperator AS [IsSyncOperator];
END;
GO

IF OBJECT_ID(N'tb_app.SearchClients', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SearchClients];
GO

CREATE PROCEDURE [tb_app].[SearchClients]
    @IncludeInactive bit = 0,
    @Search nvarchar(240) = NULL,
    @Limit int = 250
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
            WHEN @Limit > 1000 THEN 1000
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
        client.[Id],
        client.[Name],
        client.[Source],
        client.[ExternalId],
        client.[IsActive],
        client.[LastSyncedAtUtc] AS [LastSyncedAt],
        client.[WhdLocationName],
        client.[WhdContactName],
        client.[WhdContactEmail],
        client.[WhdPhone],
        client.[WhdAddress],
        client.[SageCustomerId],
        client.[SageCustomerName],
        client.[SageContactName],
        client.[SageTelephone],
        client.[MatchStatus],
        client.[RowVersion]
    FROM [tb_data].[Clients] AS client
    WHERE (@IncludeInactive = 1 OR client.[IsActive] = 1)
      AND
      (
          @Pattern IS NULL
          OR client.[Name] LIKE @Pattern ESCAPE N'~'
          OR client.[WhdLocationName] LIKE @Pattern ESCAPE N'~'
          OR client.[WhdContactName] LIKE @Pattern ESCAPE N'~'
          OR client.[WhdContactEmail] LIKE @Pattern ESCAPE N'~'
          OR client.[WhdPhone] LIKE @Pattern ESCAPE N'~'
          OR client.[WhdAddress] LIKE @Pattern ESCAPE N'~'
          OR client.[SageCustomerId] LIKE @Pattern ESCAPE N'~'
          OR client.[SageCustomerName] LIKE @Pattern ESCAPE N'~'
          OR client.[SageContactName] LIKE @Pattern ESCAPE N'~'
      )
    ORDER BY client.[Name], client.[Id];
END;
GO

IF OBJECT_ID(N'tb_app.GetClient', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetClient];
GO

CREATE PROCEDURE [tb_app].[GetClient]
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
        client.[Id],
        client.[Name],
        client.[Source],
        client.[ExternalId],
        client.[IsActive],
        client.[LastSyncedAtUtc] AS [LastSyncedAt],
        client.[WhdLocationName],
        client.[WhdContactName],
        client.[WhdContactEmail],
        client.[WhdPhone],
        client.[WhdAddress],
        client.[SageCustomerId],
        client.[SageCustomerName],
        client.[SageContactName],
        client.[SageTelephone],
        client.[MatchStatus],
        client.[RowVersion],
        client.[CreatedAtUtc],
        client.[UpdatedAtUtc]
    FROM [tb_data].[Clients] AS client
    WHERE client.[Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveClient', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveClient];
GO

CREATE PROCEDURE [tb_app].[AdminSaveClient]
    @Id int = NULL,
    @Name nvarchar(max),
    @Source nvarchar(max) = N'Manual',
    @ExternalId nvarchar(max) = NULL,
    @IsActive bit = 1,
    @LastSyncedAtUtc datetime2(3) = NULL,
    @WhdLocationName nvarchar(max) = NULL,
    @WhdContactName nvarchar(max) = NULL,
    @SageCustomerId nvarchar(max) = NULL,
    @SageCustomerName nvarchar(max) = NULL,
    @SageContactName nvarchar(max) = NULL,
    @SageTelephone nvarchar(max) = NULL,
    @MatchStatus nvarchar(max) = N'Unmatched',
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorWindowsSid varbinary(85);
    DECLARE @ActorLoginName nvarchar(256);
    DECLARE @DisplayName nvarchar(160);
    DECLARE @IsTechnician bit;
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @ActorWindowsSid OUTPUT,
        @LoginName = @ActorLoginName OUTPUT,
        @DisplayName = @DisplayName OUTPUT,
        @IsTechnician = @IsTechnician OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
    BEGIN
        THROW 51003, N'Only a current TechBench Admin may save shared clients.', 1;
    END;

    SET @Name = NULLIF(LTRIM(RTRIM(@Name)), N'');
    SET @Source = COALESCE(NULLIF(LTRIM(RTRIM(@Source)), N''), N'Manual');
    SET @ExternalId = NULLIF(LTRIM(RTRIM(@ExternalId)), N'');
    SET @WhdLocationName = NULLIF(LTRIM(RTRIM(@WhdLocationName)), N'');
    SET @WhdContactName = NULLIF(LTRIM(RTRIM(@WhdContactName)), N'');
    SET @SageCustomerId = NULLIF(LTRIM(RTRIM(@SageCustomerId)), N'');
    SET @SageCustomerName = NULLIF(LTRIM(RTRIM(@SageCustomerName)), N'');
    SET @SageContactName = NULLIF(LTRIM(RTRIM(@SageContactName)), N'');
    SET @SageTelephone = NULLIF(LTRIM(RTRIM(@SageTelephone)), N'');
    SET @MatchStatus =
        COALESCE(NULLIF(LTRIM(RTRIM(@MatchStatus)), N''), N'Unmatched');
    SET @RequestId = COALESCE(@RequestId, NEWID());

    IF @Name IS NULL
        THROW 51010, N'Client name is required.', 1;
    IF LEN(@Name) > 240
        THROW 51010, N'Client name exceeds 240 characters.', 1;
    IF LEN(@Source) > 80
        THROW 51010, N'Client source exceeds 80 characters.', 1;
    IF @Source NOT IN (N'Manual', N'WHD', N'Sage', N'Both')
        THROW 51010, N'Client source must be Manual, WHD, Sage, or Both.', 1;
    IF LEN(@ExternalId) > 500
        THROW 51010, N'External client ID exceeds 500 characters.', 1;
    IF LEN(@WhdLocationName) > 240
        THROW 51010, N'WHD location name exceeds 240 characters.', 1;
    IF LEN(@WhdContactName) > 240
        THROW 51010, N'WHD contact name exceeds 240 characters.', 1;
    IF LEN(@SageCustomerId) > 120
        THROW 51010, N'Sage customer ID exceeds 120 characters.', 1;
    IF LEN(@SageCustomerName) > 240
        THROW 51010, N'Sage customer name exceeds 240 characters.', 1;
    IF LEN(@SageContactName) > 240
        THROW 51010, N'Sage contact name exceeds 240 characters.', 1;
    IF LEN(@SageTelephone) > 80
        THROW 51010, N'Sage telephone exceeds 80 characters.', 1;
    IF LEN(@MatchStatus) > 80
        THROW 51010, N'Client match status exceeds 80 characters.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51010, N'ExpectedRowVersion is required when updating a client.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @Action nvarchar(120);
    DECLARE @DataJson nvarchar(max);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[Clients]
            (
                [Name],
                [Source],
                [ExternalId],
                [IsActive],
                [LastSyncedAtUtc],
                [WhdLocationName],
                [WhdContactName],
                [SageCustomerId],
                [SageCustomerName],
                [SageContactName],
                [SageTelephone],
                [MatchStatus],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                CONVERT(nvarchar(240), @Name),
                CONVERT(nvarchar(80), @Source),
                CONVERT(nvarchar(500), @ExternalId),
                @IsActive,
                @LastSyncedAtUtc,
                CONVERT(nvarchar(240), @WhdLocationName),
                CONVERT(nvarchar(240), @WhdContactName),
                CONVERT(nvarchar(120), @SageCustomerId),
                CONVERT(nvarchar(240), @SageCustomerName),
                CONVERT(nvarchar(240), @SageContactName),
                CONVERT(nvarchar(80), @SageTelephone),
                CONVERT(nvarchar(80), @MatchStatus),
                @ActorWindowsSid,
                @ActorWindowsSid,
                @NowUtc,
                @NowUtc
            );

            SET @Id = CONVERT(int, SCOPE_IDENTITY());
            SET @Action = N'ClientCreated';
        END
        ELSE
        BEGIN
            UPDATE [tb_data].[Clients]
            SET
                [Name] = CONVERT(nvarchar(240), @Name),
                [Source] = CONVERT(nvarchar(80), @Source),
                [ExternalId] = CONVERT(nvarchar(500), @ExternalId),
                [IsActive] = @IsActive,
                [LastSyncedAtUtc] = @LastSyncedAtUtc,
                [WhdLocationName] = CONVERT(nvarchar(240), @WhdLocationName),
                [WhdContactName] = CONVERT(nvarchar(240), @WhdContactName),
                [SageCustomerId] = CONVERT(nvarchar(120), @SageCustomerId),
                [SageCustomerName] = CONVERT(nvarchar(240), @SageCustomerName),
                [SageContactName] = CONVERT(nvarchar(240), @SageContactName),
                [SageTelephone] = CONVERT(nvarchar(80), @SageTelephone),
                [MatchStatus] = CONVERT(nvarchar(80), @MatchStatus),
                [UpdatedByWindowsSid] = @ActorWindowsSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
            BEGIN
                IF NOT EXISTS
                (
                    SELECT 1
                    FROM [tb_data].[Clients]
                    WHERE [Id] = @Id
                )
                    THROW 51011, N'The shared client no longer exists.', 1;

                THROW 51012, N'The shared client changed after it was loaded.', 1;
            END;

            SET @Action = N'ClientUpdated';
        END;

        SELECT @DataJson =
        (
            SELECT
                client.[Name] AS [name],
                client.[Source] AS [source],
                client.[IsActive] AS [isActive],
                client.[MatchStatus] AS [matchStatus],
                CONVERT(varchar(18), client.[RowVersion], 1) AS [rowVersion]
            FROM [tb_data].[Clients] AS client
            WHERE client.[Id] = @Id
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );

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
            N'Client',
            CONVERT(nvarchar(120), @Id),
            @RequestId,
            @DataJson,
            @NowUtc
        );

        SELECT
            client.[Id],
            client.[Name],
            client.[Source],
            client.[ExternalId],
            client.[IsActive],
            client.[LastSyncedAtUtc] AS [LastSyncedAt],
            client.[WhdLocationName],
            client.[WhdContactName],
            client.[WhdContactEmail],
            client.[WhdPhone],
            client.[WhdAddress],
            client.[SageCustomerId],
            client.[SageCustomerName],
            client.[SageContactName],
            client.[SageTelephone],
            client.[MatchStatus],
            client.[RowVersion],
            client.[CreatedAtUtc],
            client.[UpdatedAtUtc]
        FROM [tb_data].[Clients] AS client
        WHERE client.[Id] = @Id;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_app.ReadAuditEvents', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ReadAuditEvents];
GO

CREATE PROCEDURE [tb_app].[ReadAuditEvents]
    @SinceUtc datetime2(3) = NULL,
    @Limit int = 250
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF IS_ROLEMEMBER(N'tb_role_admin') <> 1
    BEGIN
        THROW 51003, N'Only a current TechBench Admin may read TechBench audit events.', 1;
    END;

    SET @Limit =
        CASE
            WHEN @Limit IS NULL OR @Limit < 1 THEN 1
            WHEN @Limit > 1000 THEN 1000
            ELSE @Limit
        END;

    SELECT TOP (@Limit)
        audit_event.[Id],
        audit_event.[ActorWindowsSid],
        audit_event.[ActorLoginName],
        audit_event.[Action],
        audit_event.[EntityType],
        audit_event.[EntityId],
        audit_event.[RequestId],
        audit_event.[DataJson],
        audit_event.[OccurredAtUtc]
    FROM [tb_audit].[AuditEvents] AS audit_event
    WHERE @SinceUtc IS NULL
       OR audit_event.[OccurredAtUtc] >= @SinceUtc
    ORDER BY audit_event.[OccurredAtUtc] DESC, audit_event.[Id] DESC;
END;
GO

PRINT N'TechBench stored procedures created.';
GO
