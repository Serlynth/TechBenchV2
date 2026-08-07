:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'tb_app.SyncUpsertClient', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncUpsertClient];
GO

CREATE PROCEDURE [tb_app].[SyncUpsertClient]
    @Name nvarchar(240),
    @Source nvarchar(80),
    @ExternalId nvarchar(500) = NULL,
    @IsActive bit = 1,
    @SyncedAtUtc datetime2(3) = NULL,
    @WhdLocationName nvarchar(240) = NULL,
    @WhdContactName nvarchar(240) = NULL,
    @SageCustomerId nvarchar(120) = NULL,
    @SageCustomerName nvarchar(240) = NULL,
    @SageContactName nvarchar(240) = NULL,
    @SageTelephone nvarchar(80) = NULL,
    @MatchStatus nvarchar(80) = N'Unmatched'
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

    IF @IsAdmin <> 1 AND @IsSyncOperator <> 1
        THROW 51400, N'Only an Admin or Sync Operator may synchronize clients.', 1;

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
    SET @SyncedAtUtc = COALESCE(@SyncedAtUtc, SYSUTCDATETIME());

    IF @Name IS NULL
        THROW 51401, N'Client name is required.', 1;

    DECLARE @IdentitySource nvarchar(40) =
        CASE
            WHEN @Source = N'WHD' THEN N'WHD'
            WHEN @Source = N'Sage' THEN N'Sage'
            ELSE NULL
        END;
    DECLARE @IdentityExternalId nvarchar(500) =
        CASE
            WHEN @IdentitySource = N'Sage'
                THEN COALESCE(@SageCustomerId, @ExternalId)
            ELSE @ExternalId
        END;
    DECLARE @ClientId int;
    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @IdentitySource IS NOT NULL AND @IdentityExternalId IS NOT NULL
        BEGIN
            SELECT @ClientId = [ClientId]
            FROM [tb_data].[ClientExternalIdentities] WITH (UPDLOCK, HOLDLOCK)
            WHERE [SourceSystem] = @IdentitySource
              AND [ExternalId] = @IdentityExternalId;
        END;

        IF @ClientId IS NULL AND @ExternalId IS NOT NULL
        BEGIN
            SELECT TOP (1) @ClientId = [Id]
            FROM [tb_data].[Clients] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Source] = @Source
              AND [ExternalId] = @ExternalId
            ORDER BY [Id];
        END;

        IF @ClientId IS NULL
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
                @Name,
                @Source,
                @ExternalId,
                @IsActive,
                @SyncedAtUtc,
                @WhdLocationName,
                @WhdContactName,
                @SageCustomerId,
                @SageCustomerName,
                @SageContactName,
                @SageTelephone,
                @MatchStatus,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );
            SET @ClientId = CONVERT(int, SCOPE_IDENTITY());
        END
        ELSE
        BEGIN
            UPDATE [tb_data].[Clients]
            SET
                [Name] =
                    CASE
                        WHEN @Source = N'WHD' AND @WhdLocationName IS NOT NULL
                            THEN @Name
                        WHEN NULLIF(LTRIM(RTRIM([Name])), N'') IS NULL
                            THEN @Name
                        ELSE [Name]
                    END,
                [Source] =
                    CASE
                        WHEN [Source] = @Source OR [Source] = N'Both' THEN [Source]
                        WHEN [Source] IN (N'WHD', N'Sage')
                         AND @Source IN (N'WHD', N'Sage')
                            THEN N'Both'
                        ELSE @Source
                    END,
                [ExternalId] = COALESCE([ExternalId], @ExternalId),
                [IsActive] = @IsActive,
                [LastSyncedAtUtc] = @SyncedAtUtc,
                [WhdLocationName] = COALESCE(@WhdLocationName, [WhdLocationName]),
                [WhdContactName] = COALESCE(@WhdContactName, [WhdContactName]),
                [SageCustomerId] = COALESCE(@SageCustomerId, [SageCustomerId]),
                [SageCustomerName] = COALESCE(@SageCustomerName, [SageCustomerName]),
                [SageContactName] = COALESCE(@SageContactName, [SageContactName]),
                [SageTelephone] = COALESCE(@SageTelephone, [SageTelephone]),
                [MatchStatus] =
                    CASE
                        WHEN [Source] = N'Both'
                            THEN N'Matched'
                        WHEN [Source] IN (N'WHD', N'Sage')
                         AND @Source IN (N'WHD', N'Sage')
                         AND [Source] <> @Source
                            THEN N'Matched'
                        ELSE @MatchStatus
                    END,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @ClientId;
        END;

        IF @IdentitySource IS NOT NULL AND @IdentityExternalId IS NOT NULL
        BEGIN
            IF EXISTS
            (
                SELECT 1
                FROM [tb_data].[ClientExternalIdentities]
                WHERE [SourceSystem] = @IdentitySource
                  AND [ExternalId] = @IdentityExternalId
            )
            BEGIN
                UPDATE [tb_data].[ClientExternalIdentities]
                SET
                    [ClientId] = @ClientId,
                    [ExternalName] = @Name,
                    [LastSyncedAtUtc] = @SyncedAtUtc,
                    [UpdatedByWindowsSid] = @UserSid,
                    [UpdatedAtUtc] = @NowUtc
                WHERE [SourceSystem] = @IdentitySource
                  AND [ExternalId] = @IdentityExternalId;
            END
            ELSE
            BEGIN
                INSERT INTO [tb_data].[ClientExternalIdentities]
                (
                    [ClientId],
                    [SourceSystem],
                    [ExternalId],
                    [ExternalName],
                    [LastSyncedAtUtc],
                    [CreatedByWindowsSid],
                    [UpdatedByWindowsSid],
                    [CreatedAtUtc],
                    [UpdatedAtUtc]
                )
                VALUES
                (
                    @ClientId,
                    @IdentitySource,
                    @IdentityExternalId,
                    @Name,
                    @SyncedAtUtc,
                    @UserSid,
                    @UserSid,
                    @NowUtc,
                    @NowUtc
                );
            END;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        client.[Id],
        client.[Name],
        client.[Source],
        client.[ExternalId],
        client.[IsActive],
        client.[LastSyncedAtUtc] AS [LastSyncedAt],
        client.[WhdLocationName],
        client.[WhdContactName],
        client.[SageCustomerId],
        client.[SageCustomerName],
        client.[SageContactName],
        client.[SageTelephone],
        client.[MatchStatus],
        client.[RowVersion]
    FROM [tb_data].[Clients] AS client
    WHERE client.[Id] = @ClientId;
END;
GO

IF OBJECT_ID(N'tb_app.SyncUpsertSageCustomer', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncUpsertSageCustomer];
GO

CREATE PROCEDURE [tb_app].[SyncUpsertSageCustomer]
    @CustomerId nvarchar(120),
    @CustomerName nvarchar(240),
    @ContactName nvarchar(240) = NULL,
    @Telephone nvarchar(80) = NULL,
    @IsActive bit = 1,
    @SyncedAtUtc datetime2(3) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Result TABLE
    (
        [Id] int,
        [Name] nvarchar(240),
        [Source] nvarchar(80),
        [ExternalId] nvarchar(500),
        [IsActive] bit,
        [LastSyncedAt] datetime2(3),
        [WhdLocationName] nvarchar(240),
        [WhdContactName] nvarchar(240),
        [SageCustomerId] nvarchar(120),
        [SageCustomerName] nvarchar(240),
        [SageContactName] nvarchar(240),
        [SageTelephone] nvarchar(80),
        [MatchStatus] nvarchar(80),
        [RowVersion] binary(8)
    );

    INSERT INTO @Result
    EXEC [tb_app].[SyncUpsertClient]
        @Name = @CustomerName,
        @Source = N'Sage',
        @ExternalId = @CustomerId,
        @IsActive = @IsActive,
        @SyncedAtUtc = @SyncedAtUtc,
        @SageCustomerId = @CustomerId,
        @SageCustomerName = @CustomerName,
        @SageContactName = @ContactName,
        @SageTelephone = @Telephone,
        @MatchStatus = N'Unmatched';

    SELECT
        [Id] AS [ClientId],
        [Id],
        [RowVersion]
    FROM @Result;
END;
GO

IF OBJECT_ID(N'tb_app.SyncRemoveStaleSageCustomers', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncRemoveStaleSageCustomers];
GO

CREATE PROCEDURE [tb_app].[SyncRemoveStaleSageCustomers]
    @ActiveCustomerIdsJson nvarchar(max),
    @SyncedAtUtc datetime2(3) = NULL
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

    IF @IsAdmin <> 1 AND @IsSyncOperator <> 1
        THROW 51410, N'Only an Admin or Sync Operator may reconcile Sage customers.', 1;
    IF ISJSON(@ActiveCustomerIdsJson) <> 1
        THROW 51411, N'ActiveCustomerIdsJson must be a JSON array.', 1;

    DECLARE @ActiveIds TABLE
    (
        [CustomerId] nvarchar(120) NOT NULL PRIMARY KEY
    );

    INSERT INTO @ActiveIds([CustomerId])
    SELECT DISTINCT NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(120), [value]))), N'')
    FROM OPENJSON(@ActiveCustomerIdsJson)
    WHERE NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(120), [value]))), N'') IS NOT NULL;

    DECLARE @StaleClients TABLE ([ClientId] int NOT NULL PRIMARY KEY);

    INSERT INTO @StaleClients([ClientId])
    SELECT DISTINCT identity_row.[ClientId]
    FROM [tb_data].[ClientExternalIdentities] AS identity_row
    WHERE identity_row.[SourceSystem] = N'Sage'
      AND NOT EXISTS
      (
          SELECT 1
          FROM @ActiveIds AS active_id
          WHERE active_id.[CustomerId] = identity_row.[ExternalId]
      );

    DECLARE @StaleCount int = @@ROWCOUNT;

    DELETE identity_row
    FROM [tb_data].[ClientExternalIdentities] AS identity_row
    INNER JOIN @StaleClients AS stale
        ON stale.[ClientId] = identity_row.[ClientId]
    WHERE identity_row.[SourceSystem] = N'Sage';

    UPDATE client
    SET
        [Source] =
            CASE
                WHEN EXISTS
                (
                    SELECT 1
                    FROM [tb_data].[ClientExternalIdentities] AS remaining
                    WHERE remaining.[ClientId] = client.[Id]
                      AND remaining.[SourceSystem] = N'WHD'
                )
                    THEN N'WHD'
                ELSE N'Sage'
            END,
        [IsActive] =
            CASE
                WHEN EXISTS
                (
                    SELECT 1
                    FROM [tb_data].[ClientExternalIdentities] AS remaining
                    WHERE remaining.[ClientId] = client.[Id]
                      AND remaining.[SourceSystem] = N'WHD'
                )
                    THEN client.[IsActive]
                ELSE 0
            END,
        [SageCustomerId] = NULL,
        [SageCustomerName] = NULL,
        [SageContactName] = NULL,
        [SageTelephone] = NULL,
        [MatchStatus] = N'Unmatched',
        [LastSyncedAtUtc] = COALESCE(@SyncedAtUtc, SYSUTCDATETIME()),
        [UpdatedByWindowsSid] = @UserSid,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    FROM [tb_data].[Clients] AS client
    INNER JOIN @StaleClients AS stale
        ON stale.[ClientId] = client.[Id];

    SELECT @StaleCount AS [StaleCount], @StaleCount AS [AffectedCount];
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveExternalMapping', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveExternalMapping];
GO

CREATE PROCEDURE [tb_app].[AdminSaveExternalMapping]
    @ClientId int,
    @Source nvarchar(80),
    @ExternalId nvarchar(240),
    @ExternalName nvarchar(240) = NULL,
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

    IF @IsAdmin <> 1
        THROW 51420, N'Only an Admin may save an external client mapping.', 1;

    DECLARE @Result TABLE
    (
        [Id] bigint,
        [ClientId] int,
        [SourceSystem] nvarchar(40),
        [ExternalId] nvarchar(500),
        [ExternalName] nvarchar(240),
        [LastSyncedAt] datetime2(3),
        [RowVersion] binary(8)
    );

    INSERT INTO @Result
    EXEC [tb_app].[SyncUpsertClientExternalIdentity]
        @ClientId = @ClientId,
        @SourceSystem = @Source,
        @ExternalId = @ExternalId,
        @ExternalName = @ExternalName,
        @LastSyncedAtUtc = NULL;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @ClientId);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'ExternalClientMappingSaved',
        @EntityType = N'Client',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.AcquireSyncLease', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AcquireSyncLease];
GO

CREATE PROCEDURE [tb_app].[AcquireSyncLease]
    @Source nvarchar(120),
    @LeaseSeconds int = 300,
    @DeviceId uniqueidentifier
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

    IF @IsAdmin <> 1 AND @IsSyncOperator <> 1
        THROW 51430, N'Only an Admin or Sync Operator may acquire a sync lease.', 1;

    SET @Source = NULLIF(LTRIM(RTRIM(@Source)), N'');
    SET @LeaseSeconds =
        CASE
            WHEN @LeaseSeconds IS NULL OR @LeaseSeconds < 30 THEN 30
            WHEN @LeaseSeconds > 3600 THEN 3600
            ELSE @LeaseSeconds
        END;
    IF @Source IS NULL OR LEN(@Source) > 40
        THROW 51431, N'Sync source is required and cannot exceed 40 characters.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @LeaseId uniqueidentifier;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @LeaseId = [LeaseId]
        FROM [tb_ops].[SyncLeases] WITH (UPDLOCK, HOLDLOCK)
        WHERE [SourceSystem] = @Source
          AND [ExpiresAtUtc] > @NowUtc;

        IF @LeaseId IS NOT NULL
        BEGIN
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_ops].[SyncLeases]
                WHERE [SourceSystem] = @Source
                  AND [LeaseId] = @LeaseId
                  AND [OwnerWindowsSid] = @UserSid
                  AND [DeviceId] = @DeviceId
            )
                THROW 51432, N'Another workstation currently owns this synchronization lease.', 1;

            UPDATE [tb_ops].[SyncLeases]
            SET
                [ExpiresAtUtc] = DATEADD(second, @LeaseSeconds, @NowUtc),
                [UpdatedAtUtc] = @NowUtc
            WHERE [SourceSystem] = @Source;
        END
        ELSE
        BEGIN
            DELETE FROM [tb_ops].[SyncLeases]
            WHERE [SourceSystem] = @Source;

            SET @LeaseId = NEWID();
            INSERT INTO [tb_ops].[SyncLeases]
            (
                [SourceSystem],
                [LeaseId],
                [OwnerWindowsSid],
                [DeviceId],
                [AcquiredAtUtc],
                [ExpiresAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @Source,
                @LeaseId,
                @UserSid,
                @DeviceId,
                @NowUtc,
                DATEADD(second, @LeaseSeconds, @NowUtc),
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
        [LeaseId],
        [SourceSystem] AS [Source],
        [ExpiresAtUtc],
        [RowVersion]
    FROM [tb_ops].[SyncLeases]
    WHERE [SourceSystem] = @Source;
END;
GO

IF OBJECT_ID(N'tb_app.ReleaseSyncLease', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ReleaseSyncLease];
GO

CREATE PROCEDURE [tb_app].[ReleaseSyncLease]
    @LeaseId uniqueidentifier,
    @DeviceId uniqueidentifier
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

    DELETE FROM [tb_ops].[SyncLeases]
    WHERE [LeaseId] = @LeaseId
      AND [OwnerWindowsSid] = @UserSid
      AND [DeviceId] = @DeviceId;
END;
GO

IF OBJECT_ID(N'tb_app.BeginSyncRun', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[BeginSyncRun];
GO

CREATE PROCEDURE [tb_app].[BeginSyncRun]
    @Source nvarchar(120),
    @LeaseId uniqueidentifier,
    @DeviceId uniqueidentifier
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

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_ops].[SyncLeases]
        WHERE [SourceSystem] = @Source
          AND [LeaseId] = @LeaseId
          AND [OwnerWindowsSid] = @UserSid
          AND [DeviceId] = @DeviceId
          AND [ExpiresAtUtc] > SYSUTCDATETIME()
    )
        THROW 51440, N'The synchronization lease is missing, expired, or owned by another workstation.', 1;

    DECLARE @RunId uniqueidentifier = NEWID();

    INSERT INTO [tb_ops].[SyncRuns]
    (
        [Id],
        [SourceSystem],
        [LeaseId],
        [OwnerWindowsSid],
        [DeviceId],
        [Status]
    )
    VALUES
    (
        @RunId,
        @Source,
        @LeaseId,
        @UserSid,
        @DeviceId,
        N'Started'
    );

    SELECT @RunId AS [RunId];
END;
GO

IF OBJECT_ID(N'tb_app.CompleteSyncRun', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[CompleteSyncRun];
GO

CREATE PROCEDURE [tb_app].[CompleteSyncRun]
    @RunId uniqueidentifier,
    @Succeeded bit,
    @ItemCount int = 0,
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

    UPDATE [tb_ops].[SyncRuns]
    SET
        [Status] = CASE WHEN @Succeeded = 1 THEN N'Succeeded' ELSE N'Failed' END,
        [ReadCount] = CASE WHEN @ItemCount < 0 THEN 0 ELSE @ItemCount END,
        [Message] = COALESCE(@Message, N''),
        [CompletedAtUtc] = SYSUTCDATETIME()
    WHERE [Id] = @RunId
      AND [OwnerWindowsSid] = @UserSid
      AND [Status] = N'Started';

    IF @@ROWCOUNT = 0
        THROW 51441, N'The synchronization run is missing, final, or owned by another user.', 1;
END;
GO

IF OBJECT_ID(N'tb_app.GetSyncRuns', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetSyncRuns];
GO

CREATE PROCEDURE [tb_app].[GetSyncRuns]
    @Source nvarchar(120) = NULL,
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

    IF @IsManager <> 1 AND @IsAdmin <> 1 AND @IsSyncOperator <> 1
        THROW 51442, N'The current user cannot read synchronization history.', 1;

    SET @Limit =
        CASE WHEN @Limit < 1 THEN 1 WHEN @Limit > 1000 THEN 1000 ELSE @Limit END;

    SELECT TOP (@Limit)
        [Id] AS [RunId],
        [SourceSystem] AS [Source],
        [LeaseId],
        [Status],
        [ReadCount],
        [SavedCount],
        [StaleCount],
        [Message],
        [StartedAtUtc],
        [CompletedAtUtc],
        [RowVersion]
    FROM [tb_ops].[SyncRuns]
    WHERE @Source IS NULL OR [SourceSystem] = @Source
    ORDER BY [StartedAtUtc] DESC;
END;
GO

IF OBJECT_ID(N'tb_security.RenewSyncRunLease', N'P') IS NOT NULL
    DROP PROCEDURE [tb_security].[RenewSyncRunLease];
GO

CREATE PROCEDURE [tb_security].[RenewSyncRunLease]
    @RunId uniqueidentifier,
    @ExpectedSource nvarchar(40)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85) = SUSER_SID(ORIGINAL_LOGIN());
    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    UPDATE sync_lease
    SET
        [ExpiresAtUtc] = DATEADD(second, 300, @NowUtc),
        [UpdatedAtUtc] = @NowUtc
    FROM [tb_ops].[SyncLeases] AS sync_lease
    INNER JOIN [tb_ops].[SyncRuns] AS sync_run
        ON sync_run.[SourceSystem] = sync_lease.[SourceSystem]
       AND sync_run.[LeaseId] = sync_lease.[LeaseId]
       AND sync_run.[OwnerWindowsSid] = sync_lease.[OwnerWindowsSid]
       AND sync_run.[DeviceId] = sync_lease.[DeviceId]
    WHERE sync_run.[Id] = @RunId
      AND sync_run.[SourceSystem] = @ExpectedSource
      AND sync_run.[OwnerWindowsSid] = @UserSid
      AND sync_run.[Status] = N'Started'
      AND sync_lease.[ExpiresAtUtc] > @NowUtc;

    IF @@ROWCOUNT = 0
        THROW 51449, N'The source-specific synchronization lease expired or was replaced.', 1;
END;
GO

IF OBJECT_ID(N'tb_app.SyncApplyClientSnapshot', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncApplyClientSnapshot];
GO

CREATE PROCEDURE [tb_app].[SyncApplyClientSnapshot]
    @RunId uniqueidentifier,
    @SnapshotJson nvarchar(max),
    @SyncedAtUtc datetime2(3),
    @ReconcileMissing bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85) = SUSER_SID(ORIGINAL_LOGIN());
    IF ISJSON(@SnapshotJson) <> 1
        THROW 51450, N'SnapshotJson must be a JSON array.', 1;
    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_ops].[SyncRuns] AS sync_run
        INNER JOIN [tb_ops].[SyncLeases] AS sync_lease
            ON sync_lease.[SourceSystem] = sync_run.[SourceSystem]
           AND sync_lease.[LeaseId] = sync_run.[LeaseId]
           AND sync_lease.[OwnerWindowsSid] = sync_run.[OwnerWindowsSid]
           AND sync_lease.[DeviceId] = sync_run.[DeviceId]
        WHERE sync_run.[Id] = @RunId
          AND sync_run.[SourceSystem] = N'WHD-Clients'
          AND sync_run.[OwnerWindowsSid] = @UserSid
          AND sync_run.[Status] = N'Started'
          AND sync_lease.[ExpiresAtUtc] > SYSUTCDATETIME()
    )
        THROW 51451, N'The WHD client synchronization run or lease is not active for this workstation.', 1;

    EXEC [tb_security].[RenewSyncRunLease]
        @RunId = @RunId,
        @ExpectedSource = N'WHD-Clients';

    DECLARE @Snapshot TABLE
    (
        [ExternalId] nvarchar(500) NOT NULL PRIMARY KEY NONCLUSTERED,
        [Name] nvarchar(240) NOT NULL,
        [LocationName] nvarchar(240) NULL,
        [ContactName] nvarchar(240) NULL,
        [IsActive] bit NOT NULL
    );

    INSERT INTO @Snapshot
    (
        [ExternalId],
        [Name],
        [LocationName],
        [ContactName],
        [IsActive]
    )
    SELECT
        [ExternalId],
        [Name],
        [LocationName],
        [ContactName],
        COALESCE([IsActive], 1)
    FROM OPENJSON(@SnapshotJson)
    WITH
    (
        [ExternalId] nvarchar(500) N'$.externalId',
        [Name] nvarchar(240) N'$.name',
        [LocationName] nvarchar(240) N'$.locationName',
        [ContactName] nvarchar(240) N'$.contactName',
        [IsActive] bit N'$.isActive'
    )
    WHERE NULLIF(LTRIM(RTRIM([ExternalId])), N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM([Name])), N'') IS NOT NULL;

    DECLARE @ExternalId nvarchar(500);
    DECLARE @Name nvarchar(240);
    DECLARE @LocationName nvarchar(240);
    DECLARE @ContactName nvarchar(240);
    DECLARE @IsActive bit;
    DECLARE @SavedCount int = 0;

    DECLARE SnapshotCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT [ExternalId], [Name], [LocationName], [ContactName], [IsActive]
    FROM @Snapshot;

    OPEN SnapshotCursor;
    FETCH NEXT FROM SnapshotCursor
    INTO @ExternalId, @Name, @LocationName, @ContactName, @IsActive;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC [tb_security].[RenewSyncRunLease]
            @RunId = @RunId,
            @ExpectedSource = N'WHD-Clients';

        DECLARE @ClientResult TABLE
        (
            [Id] int,
            [Name] nvarchar(240),
            [Source] nvarchar(80),
            [ExternalId] nvarchar(500),
            [IsActive] bit,
            [LastSyncedAt] datetime2(3),
            [WhdLocationName] nvarchar(240),
            [WhdContactName] nvarchar(240),
            [SageCustomerId] nvarchar(120),
            [SageCustomerName] nvarchar(240),
            [SageContactName] nvarchar(240),
            [SageTelephone] nvarchar(80),
            [MatchStatus] nvarchar(80),
            [RowVersion] binary(8)
        );

        INSERT INTO @ClientResult
        EXEC [tb_app].[SyncUpsertClient]
            @Name = @Name,
            @Source = N'WHD',
            @ExternalId = @ExternalId,
            @IsActive = @IsActive,
            @SyncedAtUtc = @SyncedAtUtc,
            @WhdLocationName = @LocationName,
            @WhdContactName = @ContactName,
            @MatchStatus = N'Unmatched';

        SET @SavedCount += 1;

        FETCH NEXT FROM SnapshotCursor
        INTO @ExternalId, @Name, @LocationName, @ContactName, @IsActive;
    END;

    CLOSE SnapshotCursor;
    DEALLOCATE SnapshotCursor;

    EXEC [tb_security].[RenewSyncRunLease]
        @RunId = @RunId,
        @ExpectedSource = N'WHD-Clients';

    DECLARE @StaleCount int = 0;
    IF @ReconcileMissing = 1
    BEGIN
        DECLARE @StaleClients TABLE ([ClientId] int NOT NULL PRIMARY KEY);
        INSERT INTO @StaleClients([ClientId])
        SELECT DISTINCT identity_row.[ClientId]
        FROM [tb_data].[ClientExternalIdentities] AS identity_row
        WHERE identity_row.[SourceSystem] = N'WHD'
          AND NOT EXISTS
          (
              SELECT 1
              FROM @Snapshot AS snapshot
              WHERE snapshot.[ExternalId] = identity_row.[ExternalId]
          );

        SET @StaleCount = @@ROWCOUNT;

        DELETE identity_row
        FROM [tb_data].[ClientExternalIdentities] AS identity_row
        INNER JOIN @StaleClients AS stale
            ON stale.[ClientId] = identity_row.[ClientId]
        WHERE identity_row.[SourceSystem] = N'WHD';

        UPDATE client
        SET
            [Source] =
                CASE
                    WHEN EXISTS
                    (
                        SELECT 1
                        FROM [tb_data].[ClientExternalIdentities] AS remaining
                        WHERE remaining.[ClientId] = client.[Id]
                          AND remaining.[SourceSystem] = N'Sage'
                    )
                        THEN N'Sage'
                    ELSE N'WHD'
                END,
            [IsActive] =
                CASE
                    WHEN EXISTS
                    (
                        SELECT 1
                        FROM [tb_data].[ClientExternalIdentities] AS remaining
                        WHERE remaining.[ClientId] = client.[Id]
                          AND remaining.[SourceSystem] = N'Sage'
                    )
                        THEN client.[IsActive]
                    ELSE 0
                END,
            [WhdLocationName] = NULL,
            [WhdContactName] = NULL,
            [MatchStatus] = N'Unmatched',
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        FROM [tb_data].[Clients] AS client
        INNER JOIN @StaleClients AS stale
            ON stale.[ClientId] = client.[Id];
    END;

    DECLARE @MatchedCount int =
    (
        SELECT COUNT(*)
        FROM [tb_data].[Clients]
        WHERE [Source] = N'Both'
    );

    UPDATE [tb_ops].[SyncRuns]
    SET
        [ReadCount] = (SELECT COUNT(*) FROM @Snapshot),
        [SavedCount] = @SavedCount,
        [StaleCount] = @StaleCount
    WHERE [Id] = @RunId;

    SELECT
        @SavedCount AS [SavedCount],
        @StaleCount AS [StaleCount],
        @MatchedCount AS [MatchedCount];
END;
GO

IF OBJECT_ID(N'tb_app.SyncApplyTicketStatusSnapshot', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncApplyTicketStatusSnapshot];
GO

CREATE PROCEDURE [tb_app].[SyncApplyTicketStatusSnapshot]
    @RunId uniqueidentifier,
    @SnapshotJson nvarchar(max),
    @SyncedAtUtc datetime2(3),
    @ReconcileMissing bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85) = SUSER_SID(ORIGINAL_LOGIN());
    IF ISJSON(@SnapshotJson) <> 1
        THROW 51450, N'SnapshotJson must be a JSON array.', 1;
    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_ops].[SyncRuns] AS sync_run
        INNER JOIN [tb_ops].[SyncLeases] AS sync_lease
            ON sync_lease.[SourceSystem] = sync_run.[SourceSystem]
           AND sync_lease.[LeaseId] = sync_run.[LeaseId]
           AND sync_lease.[OwnerWindowsSid] = sync_run.[OwnerWindowsSid]
           AND sync_lease.[DeviceId] = sync_run.[DeviceId]
        WHERE sync_run.[Id] = @RunId
          AND sync_run.[SourceSystem] = N'WHD-TicketStatuses'
          AND sync_run.[OwnerWindowsSid] = @UserSid
          AND sync_run.[Status] = N'Started'
          AND sync_lease.[ExpiresAtUtc] > SYSUTCDATETIME()
    )
        THROW 51451, N'The WHD ticket-status synchronization run or lease is not active for this workstation.', 1;

    EXEC [tb_security].[RenewSyncRunLease]
        @RunId = @RunId,
        @ExpectedSource = N'WHD-TicketStatuses';

    DECLARE @Snapshot TABLE
    (
        [Id] int NOT NULL PRIMARY KEY,
        [Name] nvarchar(160) NOT NULL,
        [IsClosed] bit NOT NULL
    );

    INSERT INTO @Snapshot([Id], [Name], [IsClosed])
    SELECT [Id], [Name], COALESCE([IsClosed], 0)
    FROM OPENJSON(@SnapshotJson)
    WITH
    (
        [Id] int N'$.id',
        [Name] nvarchar(160) N'$.name',
        [IsClosed] bit N'$.isClosed'
    )
    WHERE [Id] IS NOT NULL
      AND NULLIF(LTRIM(RTRIM([Name])), N'') IS NOT NULL;

    DECLARE @Id int;
    DECLARE @Name nvarchar(160);
    DECLARE @IsClosed bit;
    DECLARE @SavedCount int = 0;
    DECLARE @StatusExternalId nvarchar(240);

    DECLARE StatusCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT [Id], [Name], [IsClosed] FROM @Snapshot;
    OPEN StatusCursor;
    FETCH NEXT FROM StatusCursor INTO @Id, @Name, @IsClosed;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC [tb_security].[RenewSyncRunLease]
            @RunId = @RunId,
            @ExpectedSource = N'WHD-TicketStatuses';

        DECLARE @StatusResult TABLE
        (
            [Id] int,
            [Name] nvarchar(160),
            [Source] nvarchar(40),
            [ExternalId] nvarchar(240),
            [WhdStatusTypeId] int,
            [IsClosed] bit,
            [LastSyncedAt] datetime2(3),
            [RowVersion] binary(8)
        );
        SET @StatusExternalId = CONVERT(nvarchar(240), @Id);
        INSERT INTO @StatusResult
        EXEC [tb_app].[SyncUpsertTicketStatusOption]
            @Name = @Name,
            @Source = N'WHD',
            @ExternalId = @StatusExternalId,
            @WhdStatusTypeId = @Id,
            @IsClosed = @IsClosed,
            @SyncedAtUtc = @SyncedAtUtc;
        SET @SavedCount += 1;
        FETCH NEXT FROM StatusCursor INTO @Id, @Name, @IsClosed;
    END;
    CLOSE StatusCursor;
    DEALLOCATE StatusCursor;

    EXEC [tb_security].[RenewSyncRunLease]
        @RunId = @RunId,
        @ExpectedSource = N'WHD-TicketStatuses';

    DECLARE @StaleCount int = 0;
    IF @ReconcileMissing = 1
    BEGIN
        UPDATE status_option
        SET
            [IsClosed] = 1,
            [LastSyncedAtUtc] = @SyncedAtUtc,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        FROM [tb_data].[TicketStatusOptions] AS status_option
        WHERE status_option.[Source] = N'WHD'
          AND status_option.[WhdStatusTypeId] IS NOT NULL
          AND NOT EXISTS
          (
              SELECT 1
              FROM @Snapshot AS snapshot
              WHERE snapshot.[Id] = status_option.[WhdStatusTypeId]
          );
        SET @StaleCount = @@ROWCOUNT;
    END;

    UPDATE [tb_ops].[SyncRuns]
    SET
        [ReadCount] = (SELECT COUNT(*) FROM @Snapshot),
        [SavedCount] = @SavedCount,
        [StaleCount] = @StaleCount
    WHERE [Id] = @RunId;

    SELECT
        @SavedCount AS [SavedCount],
        @StaleCount AS [StaleCount],
        CONVERT(int, 0) AS [MatchedCount];
END;
GO

IF OBJECT_ID(N'tb_app.SyncApplyTicketSnapshot', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncApplyTicketSnapshot];
GO

CREATE PROCEDURE [tb_app].[SyncApplyTicketSnapshot]
    @RunId uniqueidentifier,
    @SnapshotJson nvarchar(max),
    @SyncedAtUtc datetime2(3),
    @ReconcileMissing bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85) = SUSER_SID(ORIGINAL_LOGIN());
    IF ISJSON(@SnapshotJson) <> 1
        THROW 51450, N'SnapshotJson must be a JSON array.', 1;
    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_ops].[SyncRuns] AS sync_run
        INNER JOIN [tb_ops].[SyncLeases] AS sync_lease
            ON sync_lease.[SourceSystem] = sync_run.[SourceSystem]
           AND sync_lease.[LeaseId] = sync_run.[LeaseId]
           AND sync_lease.[OwnerWindowsSid] = sync_run.[OwnerWindowsSid]
           AND sync_lease.[DeviceId] = sync_run.[DeviceId]
        WHERE sync_run.[Id] = @RunId
          AND sync_run.[SourceSystem] = N'WHD-Tickets'
          AND sync_run.[OwnerWindowsSid] = @UserSid
          AND sync_run.[Status] = N'Started'
          AND sync_lease.[ExpiresAtUtc] > SYSUTCDATETIME()
    )
        THROW 51451, N'The WHD ticket synchronization run or lease is not active for this workstation.', 1;

    EXEC [tb_security].[RenewSyncRunLease]
        @RunId = @RunId,
        @ExpectedSource = N'WHD-Tickets';

    DECLARE @Snapshot TABLE
    (
        [ExternalId] nvarchar(240) NOT NULL PRIMARY KEY,
        [TicketNumber] nvarchar(120) NOT NULL,
        [Subject] nvarchar(500) NOT NULL,
        [Status] nvarchar(160) NOT NULL,
        [StatusTypeId] int NULL,
        [IsClosed] bit NOT NULL,
        [ClientExternalId] nvarchar(500) NOT NULL,
        [ClientName] nvarchar(240) NOT NULL,
        [LocationName] nvarchar(240) NULL,
        [ContactName] nvarchar(240) NULL
    );

    INSERT INTO @Snapshot
    (
        [ExternalId],
        [TicketNumber],
        [Subject],
        [Status],
        [StatusTypeId],
        [IsClosed],
        [ClientExternalId],
        [ClientName],
        [LocationName],
        [ContactName]
    )
    SELECT
        [ExternalId],
        [TicketNumber],
        COALESCE([Subject], N''),
        COALESCE(NULLIF([Status], N''), N'Open'),
        [StatusTypeId],
        COALESCE([IsClosed], 0),
        [ClientExternalId],
        [ClientName],
        [LocationName],
        [ContactName]
    FROM OPENJSON(@SnapshotJson)
    WITH
    (
        [ExternalId] nvarchar(240) N'$.externalId',
        [TicketNumber] nvarchar(120) N'$.ticketNumber',
        [Subject] nvarchar(500) N'$.subject',
        [Status] nvarchar(160) N'$.status',
        [StatusTypeId] int N'$.statusTypeId',
        [IsClosed] bit N'$.isClosed',
        [ClientExternalId] nvarchar(500) N'$.client.externalId',
        [ClientName] nvarchar(240) N'$.client.name',
        [LocationName] nvarchar(240) N'$.client.locationName',
        [ContactName] nvarchar(240) N'$.client.contactName'
    )
    WHERE NULLIF(LTRIM(RTRIM([ExternalId])), N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM([TicketNumber])), N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM([ClientExternalId])), N'') IS NOT NULL;

    DECLARE @ExternalId nvarchar(240);
    DECLARE @TicketNumber nvarchar(120);
    DECLARE @Subject nvarchar(500);
    DECLARE @Status nvarchar(160);
    DECLARE @StatusTypeId int;
    DECLARE @IsClosed bit;
    DECLARE @ClientExternalId nvarchar(500);
    DECLARE @ClientName nvarchar(240);
    DECLARE @LocationName nvarchar(240);
    DECLARE @ContactName nvarchar(240);
    DECLARE @SavedCount int = 0;

    DECLARE TicketCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT
        [ExternalId],
        [TicketNumber],
        [Subject],
        [Status],
        [StatusTypeId],
        [IsClosed],
        [ClientExternalId],
        [ClientName],
        [LocationName],
        [ContactName]
    FROM @Snapshot;

    OPEN TicketCursor;
    FETCH NEXT FROM TicketCursor INTO
        @ExternalId, @TicketNumber, @Subject, @Status, @StatusTypeId, @IsClosed,
        @ClientExternalId, @ClientName, @LocationName, @ContactName;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC [tb_security].[RenewSyncRunLease]
            @RunId = @RunId,
            @ExpectedSource = N'WHD-Tickets';

        DECLARE @ClientResult TABLE
        (
            [Id] int,
            [Name] nvarchar(240),
            [Source] nvarchar(80),
            [ExternalId] nvarchar(500),
            [IsActive] bit,
            [LastSyncedAt] datetime2(3),
            [WhdLocationName] nvarchar(240),
            [WhdContactName] nvarchar(240),
            [SageCustomerId] nvarchar(120),
            [SageCustomerName] nvarchar(240),
            [SageContactName] nvarchar(240),
            [SageTelephone] nvarchar(80),
            [MatchStatus] nvarchar(80),
            [RowVersion] binary(8)
        );

        INSERT INTO @ClientResult
        EXEC [tb_app].[SyncUpsertClient]
            @Name = @ClientName,
            @Source = N'WHD',
            @ExternalId = @ClientExternalId,
            @IsActive = 1,
            @SyncedAtUtc = @SyncedAtUtc,
            @WhdLocationName = @LocationName,
            @WhdContactName = @ContactName,
            @MatchStatus = N'Unmatched';

        DECLARE @ClientId int = (SELECT TOP (1) [Id] FROM @ClientResult);
        DECLARE @TicketResult TABLE
        (
            [Id] int,
            [TicketNumber] nvarchar(120),
            [ClientId] int,
            [Subject] nvarchar(500),
            [Status] nvarchar(160),
            [Source] nvarchar(40),
            [ExternalId] nvarchar(240),
            [WhdStatusTypeId] int,
            [IsClosed] bit,
            [LastSyncedAt] datetime2(3),
            [RowVersion] binary(8)
        );

        INSERT INTO @TicketResult
        EXEC [tb_app].[SyncUpsertTicket]
            @ExternalId = @ExternalId,
            @TicketNumber = @TicketNumber,
            @ClientId = @ClientId,
            @Subject = @Subject,
            @Status = @Status,
            @WhdStatusTypeId = @StatusTypeId,
            @IsClosed = @IsClosed,
            @SyncedAtUtc = @SyncedAtUtc;

        SET @SavedCount += 1;

        FETCH NEXT FROM TicketCursor INTO
            @ExternalId, @TicketNumber, @Subject, @Status, @StatusTypeId, @IsClosed,
            @ClientExternalId, @ClientName, @LocationName, @ContactName;
    END;

    CLOSE TicketCursor;
    DEALLOCATE TicketCursor;

    EXEC [tb_security].[RenewSyncRunLease]
        @RunId = @RunId,
        @ExpectedSource = N'WHD-Tickets';

    DECLARE @StaleCount int = 0;
    IF @ReconcileMissing = 1
    BEGIN
        UPDATE ticket
        SET
            [IsClosed] = 1,
            [LastSyncedAtUtc] = @SyncedAtUtc,
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        FROM [tb_data].[Tickets] AS ticket
        WHERE ticket.[Source] = N'WHD'
          AND ticket.[ExternalId] IS NOT NULL
          AND NOT EXISTS
          (
              SELECT 1
              FROM @Snapshot AS snapshot
              WHERE snapshot.[ExternalId] = ticket.[ExternalId]
          );
        SET @StaleCount = @@ROWCOUNT;
    END;

    UPDATE [tb_ops].[SyncRuns]
    SET
        [ReadCount] = (SELECT COUNT(*) FROM @Snapshot),
        [SavedCount] = @SavedCount,
        [StaleCount] = @StaleCount
    WHERE [Id] = @RunId;

    SELECT
        @SavedCount AS [SavedCount],
        @StaleCount AS [StaleCount],
        CONVERT(int, 0) AS [MatchedCount];
END;
GO

IF OBJECT_ID(N'tb_app.SyncApplySageCustomerSnapshot', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncApplySageCustomerSnapshot];
GO

CREATE PROCEDURE [tb_app].[SyncApplySageCustomerSnapshot]
    @RunId uniqueidentifier,
    @SnapshotJson nvarchar(max),
    @SyncedAtUtc datetime2(3),
    @ReconcileMissing bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85) = SUSER_SID(ORIGINAL_LOGIN());
    IF ISJSON(@SnapshotJson) <> 1
        THROW 51450, N'SnapshotJson must be a JSON array.', 1;
    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_ops].[SyncRuns] AS sync_run
        INNER JOIN [tb_ops].[SyncLeases] AS sync_lease
            ON sync_lease.[SourceSystem] = sync_run.[SourceSystem]
           AND sync_lease.[LeaseId] = sync_run.[LeaseId]
           AND sync_lease.[OwnerWindowsSid] = sync_run.[OwnerWindowsSid]
           AND sync_lease.[DeviceId] = sync_run.[DeviceId]
        WHERE sync_run.[Id] = @RunId
          AND sync_run.[SourceSystem] = N'Sage-Customers'
          AND sync_run.[OwnerWindowsSid] = @UserSid
          AND sync_run.[Status] = N'Started'
          AND sync_lease.[ExpiresAtUtc] > SYSUTCDATETIME()
    )
        THROW 51451, N'The Sage customer synchronization run or lease is not active for this workstation.', 1;

    EXEC [tb_security].[RenewSyncRunLease]
        @RunId = @RunId,
        @ExpectedSource = N'Sage-Customers';

    DECLARE @Snapshot TABLE
    (
        [CustomerId] nvarchar(120) NOT NULL PRIMARY KEY,
        [CustomerName] nvarchar(240) NOT NULL,
        [ContactName] nvarchar(240) NULL,
        [Telephone] nvarchar(80) NULL,
        [IsActive] bit NOT NULL
    );

    INSERT INTO @Snapshot
    SELECT
        [CustomerId],
        [CustomerName],
        [ContactName],
        [Telephone],
        COALESCE([IsActive], 1)
    FROM OPENJSON(@SnapshotJson)
    WITH
    (
        [CustomerId] nvarchar(120) N'$.customerId',
        [CustomerName] nvarchar(240) N'$.customerName',
        [ContactName] nvarchar(240) N'$.contactName',
        [Telephone] nvarchar(80) N'$.telephone',
        [IsActive] bit N'$.isActive'
    )
    WHERE NULLIF(LTRIM(RTRIM([CustomerId])), N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM([CustomerName])), N'') IS NOT NULL;

    DECLARE @CustomerId nvarchar(120);
    DECLARE @CustomerName nvarchar(240);
    DECLARE @ContactName nvarchar(240);
    DECLARE @Telephone nvarchar(80);
    DECLARE @IsActive bit;
    DECLARE @SavedCount int = 0;

    DECLARE SageCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT [CustomerId], [CustomerName], [ContactName], [Telephone], [IsActive]
    FROM @Snapshot;
    OPEN SageCursor;
    FETCH NEXT FROM SageCursor
    INTO @CustomerId, @CustomerName, @ContactName, @Telephone, @IsActive;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC [tb_security].[RenewSyncRunLease]
            @RunId = @RunId,
            @ExpectedSource = N'Sage-Customers';

        DECLARE @SageClientResult TABLE
        (
            [Id] int,
            [Name] nvarchar(240),
            [Source] nvarchar(80),
            [ExternalId] nvarchar(500),
            [IsActive] bit,
            [LastSyncedAt] datetime2(3),
            [WhdLocationName] nvarchar(240),
            [WhdContactName] nvarchar(240),
            [SageCustomerId] nvarchar(120),
            [SageCustomerName] nvarchar(240),
            [SageContactName] nvarchar(240),
            [SageTelephone] nvarchar(80),
            [MatchStatus] nvarchar(80),
            [RowVersion] binary(8)
        );
        INSERT INTO @SageClientResult
        EXEC [tb_app].[SyncUpsertClient]
            @Name = @CustomerName,
            @Source = N'Sage',
            @ExternalId = @CustomerId,
            @IsActive = @IsActive,
            @SyncedAtUtc = @SyncedAtUtc,
            @SageCustomerId = @CustomerId,
            @SageCustomerName = @CustomerName,
            @SageContactName = @ContactName,
            @SageTelephone = @Telephone,
            @MatchStatus = N'Unmatched';
        SET @SavedCount += 1;
        FETCH NEXT FROM SageCursor
        INTO @CustomerId, @CustomerName, @ContactName, @Telephone, @IsActive;
    END;
    CLOSE SageCursor;
    DEALLOCATE SageCursor;

    EXEC [tb_security].[RenewSyncRunLease]
        @RunId = @RunId,
        @ExpectedSource = N'Sage-Customers';

    DECLARE @StaleCount int = 0;
    IF @ReconcileMissing = 1
    BEGIN
        DECLARE @ActiveJson nvarchar(max) =
        (
            SELECT [CustomerId] AS [value]
            FROM @Snapshot
            FOR JSON PATH
        );

        /* Build the scalar-array shape expected by SyncRemoveStaleSageCustomers. */
        SET @ActiveJson =
        (
            SELECT [CustomerId]
            FROM @Snapshot
            FOR JSON PATH
        );

        DECLARE @StaleResult TABLE
        (
            [StaleCount] int,
            [AffectedCount] int
        );

        /* The object array is parsed here directly to avoid a second JSON shape. */
        DECLARE @StaleClients TABLE ([ClientId] int NOT NULL PRIMARY KEY);
        INSERT INTO @StaleClients([ClientId])
        SELECT DISTINCT identity_row.[ClientId]
        FROM [tb_data].[ClientExternalIdentities] AS identity_row
        WHERE identity_row.[SourceSystem] = N'Sage'
          AND NOT EXISTS
          (
              SELECT 1
              FROM @Snapshot AS snapshot
              WHERE snapshot.[CustomerId] = identity_row.[ExternalId]
          );
        SET @StaleCount = @@ROWCOUNT;

        DELETE identity_row
        FROM [tb_data].[ClientExternalIdentities] AS identity_row
        INNER JOIN @StaleClients AS stale
            ON stale.[ClientId] = identity_row.[ClientId]
        WHERE identity_row.[SourceSystem] = N'Sage';

        UPDATE client
        SET
            [Source] =
                CASE
                    WHEN EXISTS
                    (
                        SELECT 1
                        FROM [tb_data].[ClientExternalIdentities] AS remaining
                        WHERE remaining.[ClientId] = client.[Id]
                          AND remaining.[SourceSystem] = N'WHD'
                    )
                        THEN N'WHD'
                    ELSE N'Sage'
                END,
            [IsActive] =
                CASE
                    WHEN EXISTS
                    (
                        SELECT 1
                        FROM [tb_data].[ClientExternalIdentities] AS remaining
                        WHERE remaining.[ClientId] = client.[Id]
                          AND remaining.[SourceSystem] = N'WHD'
                    )
                        THEN client.[IsActive]
                    ELSE 0
                END,
            [SageCustomerId] = NULL,
            [SageCustomerName] = NULL,
            [SageContactName] = NULL,
            [SageTelephone] = NULL,
            [MatchStatus] = N'Unmatched',
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        FROM [tb_data].[Clients] AS client
        INNER JOIN @StaleClients AS stale
            ON stale.[ClientId] = client.[Id];
    END;

    DECLARE @MatchedCount int =
    (
        SELECT COUNT(*)
        FROM [tb_data].[Clients]
        WHERE [Source] = N'Both'
    );

    UPDATE [tb_ops].[SyncRuns]
    SET
        [ReadCount] = (SELECT COUNT(*) FROM @Snapshot),
        [SavedCount] = @SavedCount,
        [StaleCount] = @StaleCount
    WHERE [Id] = @RunId;

    SELECT
        @SavedCount AS [SavedCount],
        @StaleCount AS [StaleCount],
        @MatchedCount AS [MatchedCount];
END;
GO

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

IF OBJECT_ID(N'tb_app.AddImportLegacyMapping', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AddImportLegacyMapping];
GO

CREATE PROCEDURE [tb_app].[AddImportLegacyMapping]
    @BatchId uniqueidentifier,
    @LegacyValue nvarchar(240),
    @EntityType nvarchar(80),
    @EntityId bigint
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

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_ops].[ImportBatches]
        WHERE [Id] = @BatchId
          AND [OwnerWindowsSid] = @UserSid
          AND [Status] = N'Started'
    )
        THROW 51461, N'The import batch is missing, final, or owned by another user.', 1;

    SET @LegacyValue = NULLIF(LTRIM(RTRIM(@LegacyValue)), N'');
    SET @EntityType = NULLIF(LTRIM(RTRIM(@EntityType)), N'');
    IF @LegacyValue IS NULL OR @EntityType IS NULL
        THROW 51462, N'LegacyValue and EntityType are required.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM [tb_ops].[LegacyIdMappings]
        WHERE [ImportBatchId] = @BatchId
          AND [EntityType] = @EntityType
          AND [LegacyId] = @LegacyValue
    )
    BEGIN
        UPDATE [tb_ops].[LegacyIdMappings]
        SET [NewEntityId] = @EntityId
        WHERE [ImportBatchId] = @BatchId
          AND [EntityType] = @EntityType
          AND [LegacyId] = @LegacyValue;
    END
    ELSE
    BEGIN
        INSERT INTO [tb_ops].[LegacyIdMappings]
        (
            [ImportBatchId],
            [EntityType],
            [LegacyId],
            [NewEntityId]
        )
        VALUES
        (
            @BatchId,
            @EntityType,
            @LegacyValue,
            @EntityId
        );
    END;
END;
GO

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

    UPDATE [tb_ops].[ImportBatches]
    SET
        [Status] = CASE WHEN @Succeeded = 1 THEN N'Succeeded' ELSE N'Failed' END,
        [ImportedCount] = CASE WHEN @ImportedCount < 0 THEN 0 ELSE @ImportedCount END,
        [Message] = COALESCE(@Message, N''),
        [CompletedAtUtc] = SYSUTCDATETIME()
    WHERE [Id] = @BatchId
      AND [OwnerWindowsSid] = @UserSid
      AND [Status] = N'Started';

    IF @@ROWCOUNT = 0
        THROW 51463, N'The import batch is missing, final, or owned by another user.', 1;
END;
GO

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

PRINT N'TechBench V0002 client sync, synchronization lease, snapshot, and import procedures created.';
GO
