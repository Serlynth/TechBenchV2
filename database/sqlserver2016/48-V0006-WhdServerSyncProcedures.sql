:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF SCHEMA_ID(N'tb_service') IS NULL EXEC(N'CREATE SCHEMA [tb_service] AUTHORIZATION [dbo];');
GO

/* The service contract intentionally uses leases rather than caller identity. */
IF OBJECT_ID(N'tb_service.GetWhdSyncConfiguration', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[GetWhdSyncConfiguration];
GO
CREATE PROCEDURE [tb_service].[GetWhdSyncConfiguration]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        COALESCE(MAX(CASE WHEN s.[SettingKey] = N'Whd.BaseUrl' THEN s.[SettingValue] END), N'') AS [BaseUrl],
        COALESCE(MAX(CASE WHEN s.[SettingKey] = N'Whd.ServiceUsername' THEN s.[SettingValue] END), N'') AS [Username],
        COALESCE(MAX(CASE WHEN s.[SettingKey] = N'Whd.AuthenticationMode' THEN s.[SettingValue] END), N'Auto') AS [AuthenticationMode],
        COALESCE(
            TRY_CONVERT(bit, MAX(CASE WHEN s.[SettingKey] = N'Whd.AutoSyncEnabled' THEN s.[SettingValue] END)),
            CONVERT(bit, 1)) AS [AutoSyncEnabled],
        COALESCE(
            TRY_CONVERT(int, MAX(CASE WHEN s.[SettingKey] = N'Whd.AutoSyncMinutes' THEN s.[SettingValue] END)),
            5) AS [AutoSyncMinutes],
        c.[CursorValue],
        h.[LastSuccessfulAtUtc],
        h.[LastAttemptAtUtc],
        h.[LastError]
    FROM [tb_sync].[WhdSyncHealth] AS h
    LEFT JOIN [tb_sync].[WhdSyncCursors] AS c
        ON c.[CursorName] = N'WhdTickets'
    LEFT JOIN [tb_data].[OrganizationSettings] AS s
        ON s.[SettingKey] IN
           (
               N'Whd.BaseUrl',
               N'Whd.ServiceUsername',
               N'Whd.AuthenticationMode',
               N'Whd.AutoSyncEnabled',
               N'Whd.AutoSyncMinutes'
           )
    WHERE h.[HealthId] = 1
    GROUP BY
        c.[CursorValue],
        h.[LastSuccessfulAtUtc],
        h.[LastAttemptAtUtc],
        h.[LastError];
END;
GO

IF OBJECT_ID(N'tb_service.ClaimWhdSyncWork', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[ClaimWhdSyncWork];
GO
CREATE PROCEDURE [tb_service].[ClaimWhdSyncWork]
    @WorkerId uniqueidentifier,
    @LeaseSeconds int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF @WorkerId IS NULL OR @LeaseSeconds NOT BETWEEN 15 AND 3600 THROW 51800, N'WorkerId and a lease from 15 to 3600 seconds are required.', 1;
    DECLARE @WorkId uniqueidentifier, @LeaseId uniqueidentifier = NEWID(), @Now datetime2(3) = SYSUTCDATETIME(), @Until datetime2(3);
    DECLARE @ServiceSid varbinary(85) =
    (
        SELECT [WindowsSid]
        FROM [tb_security].[Users]
        WHERE [LoginName] = N'$(SyncServicePrincipal)'
    );
    IF @ServiceSid IS NULL
        THROW 51814, N'The configured WHD sync service principal has no TechBench service actor.', 1;
    SET @Until = DATEADD(second, @LeaseSeconds, @Now);
    BEGIN TRANSACTION;

    DECLARE @QueueLockResult int;
    EXEC @QueueLockResult = sys.sp_getapplock
        @Resource = N'TechBench.WHD.SyncQueue',
        @LockMode = N'Exclusive',
        @LockOwner = N'Transaction',
        @LockTimeout = 5000;
    IF @QueueLockResult < 0
        THROW 51817, N'Could not acquire the WHD synchronization queue lock.', 1;

    DECLARE @AutoEnabled bit = COALESCE
    (
        TRY_CONVERT(bit, (SELECT [SettingValue] FROM [tb_data].[OrganizationSettings] WHERE [SettingKey] = N'Whd.AutoSyncEnabled')),
        1
    );
    DECLARE @AutoMinutes int = COALESCE
    (
        TRY_CONVERT(int, (SELECT [SettingValue] FROM [tb_data].[OrganizationSettings] WHERE [SettingKey] = N'Whd.AutoSyncMinutes')),
        5
    );
    SET @AutoMinutes = CASE WHEN @AutoMinutes < 1 THEN 1 WHEN @AutoMinutes > 1440 THEN 1440 ELSE @AutoMinutes END;

    IF @AutoEnabled = 1
       AND NOT EXISTS
       (
           SELECT 1
           FROM [tb_sync].[WhdSyncWork]
           WHERE [State] IN (N'Queued', N'Leased')
       )
       AND NOT EXISTS
       (
           SELECT 1
           FROM [tb_sync].[WhdSyncHealth]
           WHERE [HealthId] = 1
             AND [LastAttemptAtUtc] > DATEADD(minute, -@AutoMinutes, @Now)
       )
    BEGIN
        DECLARE @AutoRequestId uniqueidentifier = NEWID();
        DECLARE @AutoRequestType nvarchar(40) = CASE
            WHEN EXISTS
            (
                SELECT 1 FROM [tb_sync].[WhdSyncCursors]
                WHERE [CursorName] = N'WhdTickets'
                  AND NULLIF([CursorValue], N'') IS NOT NULL
            ) THEN N'Incremental'
            ELSE N'Full'
        END;
        DECLARE @IncludeReferenceWork bit = CASE
            WHEN @AutoRequestType = N'Full' THEN 1
            WHEN
            (
                SELECT COUNT(DISTINCT [WorkType])
                FROM [tb_sync].[WhdSyncWork]
                WHERE [State] = N'Completed'
                  AND [WorkType] IN (N'Clients', N'Statuses', N'Technicians', N'Groups')
                  AND [CompletedAtUtc] >= DATEADD(day, -1, @Now)
            ) < 4 THEN 1
            ELSE 0
        END;

        INSERT INTO [tb_sync].[WhdSyncRequests]
            ([RequestId], [RequestedByWindowsSid], [RequestType])
        VALUES
            (@AutoRequestId, @ServiceSid, @AutoRequestType);

        INSERT INTO [tb_sync].[WhdSyncWork]
            ([WorkId], [RequestId], [WorkType])
        SELECT NEWID(), @AutoRequestId, work_type.[WorkType]
        FROM
        (
            VALUES
                (N'Clients'),
                (N'Statuses'),
                (N'Technicians'),
                (N'Groups'),
                (N'Tickets')
        ) AS work_type([WorkType])
        WHERE work_type.[WorkType] = N'Tickets'
           OR @IncludeReferenceWork = 1;
    END;

    SELECT TOP (1) @WorkId = w.[WorkId]
    FROM [tb_sync].[WhdSyncWork] AS w WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK, ROWLOCK)
    LEFT JOIN [tb_sync].[WhdSyncLeases] AS l WITH (UPDLOCK, HOLDLOCK) ON l.[WorkId] = w.[WorkId]
    WHERE w.[State] = N'Queued' OR (w.[State] = N'Leased' AND l.[ExpiresAtUtc] <= @Now)
    ORDER BY
        w.[CreatedAtUtc],
        CASE w.[WorkType]
            WHEN N'Clients' THEN 1
            WHEN N'Statuses' THEN 2
            WHEN N'Technicians' THEN 3
            WHEN N'Groups' THEN 4
            WHEN N'Tickets' THEN 5
            ELSE 6
        END,
        w.[WorkId];
    IF @WorkId IS NOT NULL
    BEGIN
        DELETE FROM [tb_sync].[WhdSyncLeases] WHERE [WorkId] = @WorkId;
        INSERT INTO [tb_sync].[WhdSyncLeases]([WorkId], [LeaseId], [WorkerId], [AcquiredAtUtc], [ExpiresAtUtc]) VALUES (@WorkId, @LeaseId, @WorkerId, @Now, @Until);
        UPDATE [tb_sync].[WhdSyncWork] SET [State] = N'Leased' WHERE [WorkId] = @WorkId;
        UPDATE r SET [Status] = N'Running' FROM [tb_sync].[WhdSyncRequests] AS r JOIN [tb_sync].[WhdSyncWork] AS w ON w.[RequestId] = r.[RequestId] WHERE w.[WorkId] = @WorkId AND r.[Status] = N'Queued';
    END;
    COMMIT TRANSACTION;
    SELECT w.[WorkId], l.[LeaseId], l.[WorkerId], l.[ExpiresAtUtc], w.[RequestId], r.[RequestType], w.[WorkType], w.[PayloadJson], c.[CursorValue]
    FROM [tb_sync].[WhdSyncWork] AS w JOIN [tb_sync].[WhdSyncLeases] AS l ON l.[WorkId] = w.[WorkId]
    JOIN [tb_sync].[WhdSyncRequests] AS r ON r.[RequestId] = w.[RequestId]
    LEFT JOIN [tb_sync].[WhdSyncCursors] AS c ON c.[CursorName] = N'WhdTickets'
    WHERE w.[WorkId] = @WorkId;
END;
GO

IF OBJECT_ID(N'tb_service.RenewWhdSyncLease', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[RenewWhdSyncLease];
GO
CREATE PROCEDURE [tb_service].[RenewWhdSyncLease]
    @WorkId uniqueidentifier, @LeaseId uniqueidentifier, @WorkerId uniqueidentifier, @LeaseSeconds int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF @LeaseSeconds NOT BETWEEN 15 AND 3600 THROW 51801, N'LeaseSeconds must be from 15 to 3600.', 1;
    DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @Until datetime2(3) = DATEADD(second, @LeaseSeconds, SYSUTCDATETIME());
    UPDATE [tb_sync].[WhdSyncLeases] SET [ExpiresAtUtc] = @Until
    WHERE [WorkId] = @WorkId AND [LeaseId] = @LeaseId AND [WorkerId] = @WorkerId AND [ExpiresAtUtc] > @Now;
    IF @@ROWCOUNT <> 1 THROW 51802, N'WHD sync lease is missing, expired, or owned by another worker.', 1;
    SELECT @WorkId AS [WorkId], @LeaseId AS [LeaseId], @Until AS [ExpiresAtUtc];
END;
GO

/* Every apply path validates the same unexpired lease before modifying data. */
IF OBJECT_ID(N'tb_service.ApplyWhdClientSnapshot', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[ApplyWhdClientSnapshot];
GO
CREATE PROCEDURE [tb_service].[ApplyWhdClientSnapshot]
    @WorkId uniqueidentifier, @LeaseId uniqueidentifier, @WorkerId uniqueidentifier, @Json nvarchar(max), @SyncedAtUtc datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF COALESCE(ISJSON(@Json), 0) <> 1
       OR LEFT(LTRIM(@Json), 1) <> N'['
       OR RIGHT(RTRIM(@Json), 1) <> N']'
       OR @SyncedAtUtc IS NULL
        THROW 51803, N'Valid JSON and SyncedAtUtc are required.', 1;

    DECLARE @ActorSid varbinary(85) =
    (
        SELECT [WindowsSid]
        FROM [tb_security].[Users]
        WHERE [LoginName] = N'$(SyncServicePrincipal)'
    );
    IF @ActorSid IS NULL
        THROW 51814, N'The WHD sync service actor is missing.', 1;

    DECLARE @Snapshot TABLE
    (
        [ExternalId] nvarchar(500) NOT NULL PRIMARY KEY,
        [Name] nvarchar(240) NOT NULL,
        [LocationName] nvarchar(240) NULL,
        [ContactName] nvarchar(240) NULL,
        [IsActive] bit NOT NULL
    );

    ;WITH parsed AS
    (
        SELECT
            NULLIF(LTRIM(RTRIM([ExternalId])), N'') AS [ExternalId],
            NULLIF(LTRIM(RTRIM([Name])), N'') AS [Name],
            NULLIF(LTRIM(RTRIM([LocationName])), N'') AS [LocationName],
            NULLIF(LTRIM(RTRIM([ContactName])), N'') AS [ContactName],
            COALESCE([IsActive], 1) AS [IsActive]
        FROM OPENJSON(@Json)
        WITH
        (
            [ExternalId] nvarchar(500) '$.externalId',
            [Name] nvarchar(240) '$.name',
            [LocationName] nvarchar(240) '$.locationName',
            [ContactName] nvarchar(240) '$.contactName',
            [IsActive] bit '$.isActive'
        )
    ),
    ranked AS
    (
        SELECT *, ROW_NUMBER() OVER
            (PARTITION BY [ExternalId] ORDER BY [ExternalId]) AS [RowNumber]
        FROM parsed
        WHERE [ExternalId] IS NOT NULL AND [Name] IS NOT NULL
    )
    INSERT INTO @Snapshot([ExternalId], [Name], [LocationName], [ContactName], [IsActive])
    SELECT [ExternalId], [Name], [LocationName], [ContactName], [IsActive]
    FROM ranked
    WHERE [RowNumber] = 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        DECLARE @ClientWorkType nvarchar(40);
        SELECT @ClientWorkType = work_item.[WorkType]
        FROM [tb_sync].[WhdSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN [tb_sync].[WhdSyncWork] AS work_item
            ON work_item.[WorkId] = lease.[WorkId]
        WHERE lease.[WorkId] = @WorkId
          AND lease.[LeaseId] = @LeaseId
          AND lease.[WorkerId] = @WorkerId
          AND lease.[ExpiresAtUtc] > SYSUTCDATETIME()
          AND work_item.[State] = N'Leased'
          AND work_item.[WorkType] IN (N'Clients', N'Tickets');

        IF @ClientWorkType IS NULL
            THROW 51804, N'Valid WHD client/ticket-work lease required.', 1;

        /* Preserve the shared customer match: WHD identities may point at a
           Sage/Both client rather than a standalone WHD client row. */
        UPDATE client
        SET
            [Name] = CASE WHEN client.[Source] = N'WHD' THEN snapshot.[Name] ELSE client.[Name] END,
            [WhdLocationName] = snapshot.[LocationName],
            [WhdContactName] = snapshot.[ContactName],
            [IsActive] = CASE WHEN client.[Source] = N'WHD' THEN snapshot.[IsActive] ELSE client.[IsActive] END,
            [LastSyncedAtUtc] = @SyncedAtUtc,
            [UpdatedAtUtc] = SYSUTCDATETIME(),
            [UpdatedByWindowsSid] = @ActorSid
        FROM [tb_data].[Clients] AS client
        INNER JOIN [tb_data].[ClientExternalIdentities] AS identity_row
            ON identity_row.[ClientId] = client.[Id]
           AND identity_row.[SourceSystem] = N'WHD'
        INNER JOIN @Snapshot AS snapshot
            ON snapshot.[ExternalId] = identity_row.[ExternalId];

        UPDATE identity_row
        SET
            [ExternalName] = snapshot.[Name],
            [LastSyncedAtUtc] = @SyncedAtUtc,
            [UpdatedByWindowsSid] = @ActorSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        FROM [tb_data].[ClientExternalIdentities] AS identity_row
        INNER JOIN @Snapshot AS snapshot
            ON snapshot.[ExternalId] = identity_row.[ExternalId]
        WHERE identity_row.[SourceSystem] = N'WHD';

        /* Seed identity rows for databases upgraded from the original
           Source/ExternalId representation. */
        INSERT INTO [tb_data].[ClientExternalIdentities]
        (
            [ClientId], [SourceSystem], [ExternalId], [ExternalName],
            [LastSyncedAtUtc], [CreatedByWindowsSid], [UpdatedByWindowsSid]
        )
        SELECT
            legacy.[Id], N'WHD', snapshot.[ExternalId], snapshot.[Name],
            @SyncedAtUtc, @ActorSid, @ActorSid
        FROM @Snapshot AS snapshot
        CROSS APPLY
        (
            SELECT TOP (1) client.[Id]
            FROM [tb_data].[Clients] AS client WITH (UPDLOCK, HOLDLOCK)
            WHERE client.[ExternalId] = snapshot.[ExternalId]
              AND client.[Source] IN (N'WHD', N'Both')
            ORDER BY CASE WHEN client.[Source] = N'Both' THEN 0 ELSE 1 END, client.[Id]
        ) AS legacy
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[ClientExternalIdentities] AS existing WITH (UPDLOCK, HOLDLOCK)
            WHERE existing.[SourceSystem] = N'WHD'
              AND existing.[ExternalId] = snapshot.[ExternalId]
        );

        DECLARE @NewClients TABLE
        (
            [ExternalId] nvarchar(500) NOT NULL PRIMARY KEY,
            [ClientId] int NOT NULL
        );

        INSERT INTO [tb_data].[Clients]
        (
            [Name], [Source], [ExternalId], [IsActive], [LastSyncedAtUtc],
            [WhdLocationName], [WhdContactName], [MatchStatus],
            [CreatedByWindowsSid], [UpdatedByWindowsSid]
        )
        OUTPUT inserted.[ExternalId], inserted.[Id]
            INTO @NewClients([ExternalId], [ClientId])
        SELECT
            snapshot.[Name], N'WHD', snapshot.[ExternalId], snapshot.[IsActive], @SyncedAtUtc,
            snapshot.[LocationName], snapshot.[ContactName], N'Unmatched', @ActorSid, @ActorSid
        FROM @Snapshot AS snapshot
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[ClientExternalIdentities] AS existing WITH (UPDLOCK, HOLDLOCK)
            WHERE existing.[SourceSystem] = N'WHD'
              AND existing.[ExternalId] = snapshot.[ExternalId]
        );

        INSERT INTO [tb_data].[ClientExternalIdentities]
        (
            [ClientId], [SourceSystem], [ExternalId], [ExternalName],
            [LastSyncedAtUtc], [CreatedByWindowsSid], [UpdatedByWindowsSid]
        )
        SELECT
            new_client.[ClientId], N'WHD', snapshot.[ExternalId], snapshot.[Name],
            @SyncedAtUtc, @ActorSid, @ActorSid
        FROM @NewClients AS new_client
        INNER JOIN @Snapshot AS snapshot
            ON snapshot.[ExternalId] = new_client.[ExternalId];

        /* Refresh legacy rows immediately after their WHD identity is seeded,
           rather than waiting for the next synchronization cycle. */
        UPDATE client
        SET
            [Name] = CASE WHEN client.[Source] = N'WHD' THEN snapshot.[Name] ELSE client.[Name] END,
            [WhdLocationName] = snapshot.[LocationName],
            [WhdContactName] = snapshot.[ContactName],
            [IsActive] = CASE WHEN client.[Source] = N'WHD' THEN snapshot.[IsActive] ELSE client.[IsActive] END,
            [LastSyncedAtUtc] = @SyncedAtUtc,
            [UpdatedAtUtc] = SYSUTCDATETIME(),
            [UpdatedByWindowsSid] = @ActorSid
        FROM [tb_data].[Clients] AS client
        INNER JOIN [tb_data].[ClientExternalIdentities] AS identity_row
            ON identity_row.[ClientId] = client.[Id]
           AND identity_row.[SourceSystem] = N'WHD'
        INNER JOIN @Snapshot AS snapshot
            ON snapshot.[ExternalId] = identity_row.[ExternalId];

        /* The Clients work is a complete active-location snapshot. Embedded
           client batches during Tickets work are deliberately upsert-only. */
        IF @ClientWorkType = N'Clients'
        BEGIN
            UPDATE client
            SET
                [IsActive] = 0,
                [UpdatedAtUtc] = SYSUTCDATETIME(),
                [UpdatedByWindowsSid] = @ActorSid
            FROM [tb_data].[Clients] AS client
            INNER JOIN [tb_data].[ClientExternalIdentities] AS identity_row
                ON identity_row.[ClientId] = client.[Id]
               AND identity_row.[SourceSystem] = N'WHD'
               AND identity_row.[ExternalId] LIKE N'WHD-LOCATION-%'
            WHERE client.[Source] = N'WHD'
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM @Snapshot AS active_location
                  WHERE active_location.[ExternalId] = identity_row.[ExternalId]
              );
        END;

        DECLARE @SavedCount int = (SELECT COUNT(*) FROM @Snapshot);
        DECLARE @InsertedCount int = (SELECT COUNT(*) FROM @NewClients);

        COMMIT TRANSACTION;

        SELECT
            @SavedCount AS [SavedCount],
            @InsertedCount AS [InsertedCount],
            @SyncedAtUtc AS [SyncedAtUtc];
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_service.ApplyWhdTicketBatch', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[ApplyWhdTicketBatch];
GO
CREATE PROCEDURE [tb_service].[ApplyWhdTicketBatch]
    @WorkId uniqueidentifier, @LeaseId uniqueidentifier, @WorkerId uniqueidentifier, @Json nvarchar(max), @SyncedAtUtc datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF COALESCE(ISJSON(@Json), 0) <> 1
       OR LEFT(LTRIM(@Json), 1) <> N'['
       OR RIGHT(RTRIM(@Json), 1) <> N']'
       OR @SyncedAtUtc IS NULL
        THROW 51805, N'Valid JSON and SyncedAtUtc are required.', 1;

    DECLARE @ActorSid varbinary(85) =
    (
        SELECT [WindowsSid]
        FROM [tb_security].[Users]
        WHERE [LoginName] = N'$(SyncServicePrincipal)'
    );
    IF @ActorSid IS NULL
        THROW 51814, N'The WHD sync service actor is missing.', 1;

    DECLARE @Tickets TABLE
    (
        [ExternalId] nvarchar(240) NOT NULL PRIMARY KEY,
        [TicketNumber] nvarchar(120) NOT NULL,
        [Subject] nvarchar(500) NULL,
        [Status] nvarchar(160) NULL,
        [StatusTypeId] int NULL,
        [ClientExternalId] nvarchar(500) NOT NULL,
        [IsClosed] bit NOT NULL,
        [IsDeleted] bit NOT NULL,
        [LastUpdatedUtc] datetime2(3) NULL,
        [AssignedTechExternalId] nvarchar(120) NULL,
        [AssignedTechName] nvarchar(240) NULL,
        [AssignedGroupExternalId] nvarchar(120) NULL,
        [AssignedGroupName] nvarchar(240) NULL
    );

    ;WITH parsed AS
    (
        SELECT
            NULLIF(LTRIM(RTRIM([ExternalId])), N'') AS [ExternalId],
            NULLIF(LTRIM(RTRIM([TicketNumber])), N'') AS [TicketNumber],
            NULLIF(LTRIM(RTRIM([Subject])), N'') AS [Subject],
            NULLIF(LTRIM(RTRIM([Status])), N'') AS [Status],
            [StatusTypeId],
            NULLIF(LTRIM(RTRIM([ClientExternalId])), N'') AS [ClientExternalId],
            COALESCE([IsClosed], 0) AS [IsClosed],
            COALESCE([IsDeleted], 0) AS [IsDeleted],
            [LastUpdatedUtc],
            NULLIF(LTRIM(RTRIM([AssignedTechExternalId])), N'') AS [AssignedTechExternalId],
            NULLIF(LTRIM(RTRIM([AssignedTechName])), N'') AS [AssignedTechName],
            NULLIF(LTRIM(RTRIM([AssignedGroupExternalId])), N'') AS [AssignedGroupExternalId],
            NULLIF(LTRIM(RTRIM([AssignedGroupName])), N'') AS [AssignedGroupName]
        FROM OPENJSON(@Json)
        WITH
        (
            [ExternalId] nvarchar(240) '$.externalId',
            [TicketNumber] nvarchar(120) '$.ticketNumber',
            [Subject] nvarchar(500) '$.subject',
            [Status] nvarchar(160) '$.status',
            [StatusTypeId] int '$.statusTypeId',
            [ClientExternalId] nvarchar(500) '$.clientExternalId',
            [IsClosed] bit '$.isClosed',
            [IsDeleted] bit '$.isDeleted',
            [LastUpdatedUtc] datetime2(3) '$.lastUpdatedUtc',
            [AssignedTechExternalId] nvarchar(120) '$.assignedTechnicianExternalId',
            [AssignedTechName] nvarchar(240) '$.assignedTechnicianName',
            [AssignedGroupExternalId] nvarchar(120) '$.assignedGroupExternalId',
            [AssignedGroupName] nvarchar(240) '$.assignedGroupName'
        )
    ),
    ranked AS
    (
        SELECT *, ROW_NUMBER() OVER
            (PARTITION BY [ExternalId] ORDER BY [LastUpdatedUtc] DESC, [TicketNumber]) AS [RowNumber]
        FROM parsed
        WHERE [ExternalId] IS NOT NULL
          AND [TicketNumber] IS NOT NULL
          AND [ClientExternalId] IS NOT NULL
    )
    INSERT INTO @Tickets
    (
        [ExternalId], [TicketNumber], [Subject], [Status], [StatusTypeId],
        [ClientExternalId], [IsClosed], [IsDeleted], [LastUpdatedUtc],
        [AssignedTechExternalId], [AssignedTechName],
        [AssignedGroupExternalId], [AssignedGroupName]
    )
    SELECT
        [ExternalId], [TicketNumber], [Subject], [Status], [StatusTypeId],
        [ClientExternalId], [IsClosed], [IsDeleted], [LastUpdatedUtc],
        [AssignedTechExternalId], [AssignedTechName],
        [AssignedGroupExternalId], [AssignedGroupName]
    FROM ranked
    WHERE [RowNumber] = 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_sync].[WhdSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [tb_sync].[WhdSyncWork] AS work_item
                ON work_item.[WorkId] = lease.[WorkId]
            WHERE lease.[WorkId] = @WorkId
              AND lease.[LeaseId] = @LeaseId
              AND lease.[WorkerId] = @WorkerId
              AND lease.[ExpiresAtUtc] > SYSUTCDATETIME()
              AND work_item.[State] = N'Leased'
              AND work_item.[WorkType] = N'Tickets'
        )
            THROW 51806, N'Valid WHD ticket-work lease required.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM @Tickets AS incoming
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[ClientExternalIdentities] AS identity_row
                WHERE identity_row.[SourceSystem] = N'WHD'
                  AND identity_row.[ExternalId] = incoming.[ClientExternalId]
            )
        )
            THROW 51815, N'A WHD ticket referenced a client identity that was not durably applied.', 1;

        UPDATE ticket
        SET
            [TicketNumber] = incoming.[TicketNumber],
            [ClientId] = identity_row.[ClientId],
            [Subject] = COALESCE(incoming.[Subject], ticket.[Subject]),
            [Status] = COALESCE(incoming.[Status], ticket.[Status]),
            [WhdStatusTypeId] = incoming.[StatusTypeId],
            [IsWhdDeleted] = incoming.[IsDeleted],
            [IsClosed] = CASE WHEN incoming.[IsDeleted] = 1 THEN 1 ELSE incoming.[IsClosed] END,
            [WhdLastUpdatedUtc] = COALESCE(incoming.[LastUpdatedUtc], ticket.[WhdLastUpdatedUtc]),
            [AssignedTechExternalId] = incoming.[AssignedTechExternalId],
            [AssignedTechName] = incoming.[AssignedTechName],
            [AssignedGroupExternalId] = incoming.[AssignedGroupExternalId],
            [AssignedGroupName] = incoming.[AssignedGroupName],
            [LastSyncedAtUtc] = @SyncedAtUtc,
            [UpdatedAtUtc] = SYSUTCDATETIME(),
            [UpdatedByWindowsSid] = @ActorSid
        FROM [tb_data].[Tickets] AS ticket
        INNER JOIN @Tickets AS incoming
            ON ticket.[Source] = N'WHD'
           AND ticket.[ExternalId] = incoming.[ExternalId]
        INNER JOIN [tb_data].[ClientExternalIdentities] AS identity_row
            ON identity_row.[SourceSystem] = N'WHD'
           AND identity_row.[ExternalId] = incoming.[ClientExternalId]
        WHERE incoming.[LastUpdatedUtc] IS NULL
           OR ticket.[WhdLastUpdatedUtc] IS NULL
           OR incoming.[LastUpdatedUtc] >= ticket.[WhdLastUpdatedUtc];

        INSERT INTO [tb_data].[Tickets]
        (
            [TicketNumber], [ClientId], [Subject], [Status], [Source], [ExternalId],
            [WhdStatusTypeId], [IsClosed], [LastSyncedAtUtc], [WhdLastUpdatedUtc],
            [IsWhdDeleted], [AssignedTechExternalId], [AssignedTechName],
            [AssignedGroupExternalId], [AssignedGroupName],
            [CreatedByWindowsSid], [UpdatedByWindowsSid]
        )
        SELECT
            incoming.[TicketNumber], identity_row.[ClientId],
            COALESCE(incoming.[Subject], N''), COALESCE(incoming.[Status], N'Open'),
            N'WHD', incoming.[ExternalId], incoming.[StatusTypeId],
            CASE WHEN incoming.[IsDeleted] = 1 THEN 1 ELSE incoming.[IsClosed] END,
            @SyncedAtUtc, incoming.[LastUpdatedUtc], incoming.[IsDeleted],
            incoming.[AssignedTechExternalId], incoming.[AssignedTechName],
            incoming.[AssignedGroupExternalId], incoming.[AssignedGroupName],
            @ActorSid, @ActorSid
        FROM @Tickets AS incoming
        INNER JOIN [tb_data].[ClientExternalIdentities] AS identity_row
            ON identity_row.[SourceSystem] = N'WHD'
           AND identity_row.[ExternalId] = incoming.[ClientExternalId]
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[Tickets] AS existing WITH (UPDLOCK, HOLDLOCK)
            WHERE existing.[Source] = N'WHD'
              AND existing.[ExternalId] = incoming.[ExternalId]
        );

        DECLARE @InsertedCount int = @@ROWCOUNT;
        DECLARE @SavedCount int = (SELECT COUNT(*) FROM @Tickets);

        COMMIT TRANSACTION;

        SELECT
            @SavedCount AS [SavedCount],
            @InsertedCount AS [InsertedCount],
            @SyncedAtUtc AS [SyncedAtUtc];
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_service.ApplyWhdTicketStatusSnapshot', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[ApplyWhdTicketStatusSnapshot];
GO
CREATE PROCEDURE [tb_service].[ApplyWhdTicketStatusSnapshot]
    @WorkId uniqueidentifier, @LeaseId uniqueidentifier, @WorkerId uniqueidentifier, @Json nvarchar(max), @SyncedAtUtc datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF COALESCE(ISJSON(@Json), 0) <> 1
       OR LEFT(LTRIM(@Json), 1) <> N'['
       OR RIGHT(RTRIM(@Json), 1) <> N']'
       OR @SyncedAtUtc IS NULL
        THROW 51807, N'Valid JSON and SyncedAtUtc are required.', 1;

    DECLARE @Snapshot TABLE
    (
        [ExternalId] nvarchar(240) NOT NULL PRIMARY KEY,
        [WhdStatusTypeId] int NULL,
        [Name] nvarchar(160) NOT NULL,
        [IsClosed] bit NOT NULL
    );

    ;WITH parsed AS
    (
        SELECT
            NULLIF(LTRIM(RTRIM([ExternalId])), N'') AS [ExternalId],
            [WhdStatusTypeId],
            NULLIF(LTRIM(RTRIM([Name])), N'') AS [Name],
            COALESCE([IsClosed], 0) AS [IsClosed]
        FROM OPENJSON(@Json)
        WITH
        (
            [ExternalId] nvarchar(240) '$.externalId',
            [WhdStatusTypeId] int '$.whdStatusTypeId',
            [Name] nvarchar(160) '$.name',
            [IsClosed] bit '$.isClosed'
        )
    ),
    ranked AS
    (
        SELECT *, ROW_NUMBER() OVER
            (PARTITION BY [ExternalId] ORDER BY [WhdStatusTypeId]) AS [RowNumber]
        FROM parsed
        WHERE [ExternalId] IS NOT NULL AND [Name] IS NOT NULL
    )
    INSERT INTO @Snapshot([ExternalId], [WhdStatusTypeId], [Name], [IsClosed])
    SELECT [ExternalId], [WhdStatusTypeId], [Name], [IsClosed]
    FROM ranked
    WHERE [RowNumber] = 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_sync].[WhdSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [tb_sync].[WhdSyncWork] AS work_item
                ON work_item.[WorkId] = lease.[WorkId]
            WHERE lease.[WorkId] = @WorkId
              AND lease.[LeaseId] = @LeaseId
              AND lease.[WorkerId] = @WorkerId
              AND lease.[ExpiresAtUtc] > SYSUTCDATETIME()
              AND work_item.[State] = N'Leased'
              AND work_item.[WorkType] = N'Statuses'
        )
            THROW 51808, N'Valid WHD status-work lease required.', 1;

        MERGE [tb_data].[TicketStatusOptions] AS target
        USING @Snapshot AS source
            ON target.[Source] = N'WHD'
           AND target.[ExternalId] = source.[ExternalId]
        WHEN MATCHED THEN
            UPDATE SET
                [Name] = source.[Name],
                [WhdStatusTypeId] = source.[WhdStatusTypeId],
                [IsClosed] = source.[IsClosed],
                [LastSyncedAtUtc] = @SyncedAtUtc,
                [UpdatedAtUtc] = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT
                ([Name], [Source], [ExternalId], [WhdStatusTypeId], [IsClosed], [LastSyncedAtUtc])
            VALUES
                (source.[Name], N'WHD', source.[ExternalId], source.[WhdStatusTypeId], source.[IsClosed], @SyncedAtUtc);

        COMMIT TRANSACTION;
        SELECT (SELECT COUNT(*) FROM @Snapshot) AS [SavedCount], @SyncedAtUtc AS [SyncedAtUtc];
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_service.ApplyWhdTechnicianSnapshot', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[ApplyWhdTechnicianSnapshot];
GO
CREATE PROCEDURE [tb_service].[ApplyWhdTechnicianSnapshot]
    @WorkId uniqueidentifier, @LeaseId uniqueidentifier, @WorkerId uniqueidentifier, @Json nvarchar(max), @SyncedAtUtc datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF COALESCE(ISJSON(@Json), 0) <> 1
       OR LEFT(LTRIM(@Json), 1) <> N'['
       OR RIGHT(RTRIM(@Json), 1) <> N']'
       OR @SyncedAtUtc IS NULL
        THROW 51809, N'Valid JSON and SyncedAtUtc are required.', 1;

    DECLARE @Snapshot TABLE
    (
        [ExternalId] nvarchar(120) NOT NULL PRIMARY KEY,
        [DisplayName] nvarchar(240) NOT NULL,
        [Username] nvarchar(240) NULL,
        [Email] nvarchar(320) NULL,
        [IsActive] bit NOT NULL,
        [LastUpdatedUtc] datetime2(3) NULL
    );

    ;WITH parsed AS
    (
        SELECT
            NULLIF(LTRIM(RTRIM([ExternalId])), N'') AS [ExternalId],
            NULLIF(LTRIM(RTRIM([DisplayName])), N'') AS [DisplayName],
            NULLIF(LTRIM(RTRIM([Username])), N'') AS [Username],
            NULLIF(LTRIM(RTRIM([Email])), N'') AS [Email],
            COALESCE([IsActive], 1) AS [IsActive],
            [LastUpdatedUtc]
        FROM OPENJSON(@Json)
        WITH
        (
            [ExternalId] nvarchar(120) '$.externalId',
            [DisplayName] nvarchar(240) '$.displayName',
            [Username] nvarchar(240) '$.username',
            [Email] nvarchar(320) '$.email',
            [IsActive] bit '$.isActive',
            [LastUpdatedUtc] datetime2(3) '$.lastUpdatedUtc'
        )
    ),
    ranked AS
    (
        SELECT *, ROW_NUMBER() OVER
            (PARTITION BY [ExternalId] ORDER BY [LastUpdatedUtc] DESC, [DisplayName]) AS [RowNumber]
        FROM parsed
        WHERE [ExternalId] IS NOT NULL AND [DisplayName] IS NOT NULL
    )
    INSERT INTO @Snapshot
        ([ExternalId], [DisplayName], [Username], [Email], [IsActive], [LastUpdatedUtc])
    SELECT [ExternalId], [DisplayName], [Username], [Email], [IsActive], [LastUpdatedUtc]
    FROM ranked
    WHERE [RowNumber] = 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_sync].[WhdSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [tb_sync].[WhdSyncWork] AS work_item
                ON work_item.[WorkId] = lease.[WorkId]
            WHERE lease.[WorkId] = @WorkId
              AND lease.[LeaseId] = @LeaseId
              AND lease.[WorkerId] = @WorkerId
              AND lease.[ExpiresAtUtc] > SYSUTCDATETIME()
              AND work_item.[State] = N'Leased'
              AND work_item.[WorkType] = N'Technicians'
        )
            THROW 51810, N'Valid WHD technician-work lease required.', 1;

        MERGE [tb_whd].[Technicians] AS target
        USING @Snapshot AS source
            ON target.[ExternalId] = source.[ExternalId]
        WHEN MATCHED THEN
            UPDATE SET
                [DisplayName] = source.[DisplayName],
                [Username] = source.[Username],
                [Email] = source.[Email],
                [IsActive] = source.[IsActive],
                [WhdLastUpdatedUtc] = source.[LastUpdatedUtc],
                [LastSyncedAtUtc] = @SyncedAtUtc
        WHEN NOT MATCHED BY TARGET THEN
            INSERT
                ([ExternalId], [DisplayName], [Username], [Email], [IsActive],
                 [WhdLastUpdatedUtc], [LastSyncedAtUtc])
            VALUES
                (source.[ExternalId], source.[DisplayName], source.[Username], source.[Email],
                 source.[IsActive], source.[LastUpdatedUtc], @SyncedAtUtc)
        WHEN NOT MATCHED BY SOURCE THEN
            UPDATE SET [IsActive] = 0, [LastSyncedAtUtc] = @SyncedAtUtc;

        COMMIT TRANSACTION;
        SELECT (SELECT COUNT(*) FROM @Snapshot) AS [SavedCount], @SyncedAtUtc AS [SyncedAtUtc];
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_service.ApplyWhdTechGroupSnapshot', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[ApplyWhdTechGroupSnapshot];
GO
CREATE PROCEDURE [tb_service].[ApplyWhdTechGroupSnapshot]
    @WorkId uniqueidentifier, @LeaseId uniqueidentifier, @WorkerId uniqueidentifier, @Json nvarchar(max), @SyncedAtUtc datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF COALESCE(ISJSON(@Json), 0) <> 1
       OR LEFT(LTRIM(@Json), 1) <> N'['
       OR RIGHT(RTRIM(@Json), 1) <> N']'
       OR @SyncedAtUtc IS NULL
        THROW 51811, N'Valid JSON and SyncedAtUtc are required.', 1;

    DECLARE @Groups TABLE
    (
        [ExternalId] nvarchar(120) NOT NULL PRIMARY KEY,
        [DisplayName] nvarchar(240) NOT NULL,
        [IsActive] bit NOT NULL,
        [LastUpdatedUtc] datetime2(3) NULL
    );
    DECLARE @Memberships TABLE
    (
        [TechnicianExternalId] nvarchar(120) NOT NULL,
        [GroupExternalId] nvarchar(120) NOT NULL,
        PRIMARY KEY ([TechnicianExternalId], [GroupExternalId])
    );

    ;WITH parsed AS
    (
        SELECT
            NULLIF(LTRIM(RTRIM([ExternalId])), N'') AS [ExternalId],
            NULLIF(LTRIM(RTRIM([DisplayName])), N'') AS [DisplayName],
            COALESCE([IsActive], 1) AS [IsActive],
            [LastUpdatedUtc]
        FROM OPENJSON(@Json)
        WITH
        (
            [ExternalId] nvarchar(120) '$.externalId',
            [DisplayName] nvarchar(240) '$.name',
            [IsActive] bit '$.isActive',
            [LastUpdatedUtc] datetime2(3) '$.lastUpdatedUtc'
        )
    ),
    ranked AS
    (
        SELECT *, ROW_NUMBER() OVER
            (PARTITION BY [ExternalId] ORDER BY [LastUpdatedUtc] DESC, [DisplayName]) AS [RowNumber]
        FROM parsed
        WHERE [ExternalId] IS NOT NULL AND [DisplayName] IS NOT NULL
    )
    INSERT INTO @Groups([ExternalId], [DisplayName], [IsActive], [LastUpdatedUtc])
    SELECT [ExternalId], [DisplayName], [IsActive], [LastUpdatedUtc]
    FROM ranked
    WHERE [RowNumber] = 1;

    INSERT INTO @Memberships([TechnicianExternalId], [GroupExternalId])
    SELECT DISTINCT
        NULLIF(LTRIM(RTRIM(member.[TechnicianExternalId])), N''),
        NULLIF(LTRIM(RTRIM(group_row.[ExternalId])), N'')
    FROM OPENJSON(@Json)
    WITH
    (
        [ExternalId] nvarchar(120) '$.externalId',
        [Technicians] nvarchar(max) '$.technicianExternalIds' AS JSON
    ) AS group_row
    CROSS APPLY OPENJSON(group_row.[Technicians])
    WITH ([TechnicianExternalId] nvarchar(120) '$') AS member
    WHERE NULLIF(LTRIM(RTRIM(member.[TechnicianExternalId])), N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(group_row.[ExternalId])), N'') IS NOT NULL;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_sync].[WhdSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [tb_sync].[WhdSyncWork] AS work_item
                ON work_item.[WorkId] = lease.[WorkId]
            WHERE lease.[WorkId] = @WorkId
              AND lease.[LeaseId] = @LeaseId
              AND lease.[WorkerId] = @WorkerId
              AND lease.[ExpiresAtUtc] > SYSUTCDATETIME()
              AND work_item.[State] = N'Leased'
              AND work_item.[WorkType] = N'Groups'
        )
            THROW 51812, N'Valid WHD group-work lease required.', 1;

        MERGE [tb_whd].[TechnicianGroups] AS target
        USING @Groups AS source
            ON target.[ExternalId] = source.[ExternalId]
        WHEN MATCHED THEN
            UPDATE SET
                [DisplayName] = source.[DisplayName],
                [IsActive] = source.[IsActive],
                [WhdLastUpdatedUtc] = source.[LastUpdatedUtc],
                [LastSyncedAtUtc] = @SyncedAtUtc
        WHEN NOT MATCHED BY TARGET THEN
            INSERT
                ([ExternalId], [DisplayName], [IsActive], [WhdLastUpdatedUtc], [LastSyncedAtUtc])
            VALUES
                (source.[ExternalId], source.[DisplayName], source.[IsActive],
                 source.[LastUpdatedUtc], @SyncedAtUtc)
        WHEN NOT MATCHED BY SOURCE THEN
            UPDATE SET [IsActive] = 0, [LastSyncedAtUtc] = @SyncedAtUtc;

        /* Membership is a complete snapshot. Replacing it atomically prevents
           removed group access from remaining visible to former members. */
        DELETE FROM [tb_whd].[TechnicianGroupMemberships];

        INSERT INTO [tb_whd].[TechnicianGroupMemberships]
            ([TechnicianExternalId], [GroupExternalId], [LastSyncedAtUtc])
        SELECT membership.[TechnicianExternalId], membership.[GroupExternalId], @SyncedAtUtc
        FROM @Memberships AS membership
        INNER JOIN [tb_whd].[Technicians] AS technician
            ON technician.[ExternalId] = membership.[TechnicianExternalId]
        INNER JOIN [tb_whd].[TechnicianGroups] AS group_row
            ON group_row.[ExternalId] = membership.[GroupExternalId]
        WHERE technician.[IsActive] = 1 AND group_row.[IsActive] = 1;

        COMMIT TRANSACTION;
        SELECT
            (SELECT COUNT(*) FROM @Groups) AS [SavedGroupCount],
            (SELECT COUNT(*) FROM @Memberships) AS [ReadMembershipCount],
            @SyncedAtUtc AS [SyncedAtUtc];
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_service.CompleteWhdSyncWork', N'P') IS NOT NULL DROP PROCEDURE [tb_service].[CompleteWhdSyncWork];
GO
CREATE PROCEDURE [tb_service].[CompleteWhdSyncWork]
    @WorkId uniqueidentifier, @LeaseId uniqueidentifier, @WorkerId uniqueidentifier, @Succeeded bit, @CursorValue nvarchar(400) = NULL, @Message nvarchar(2000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetime2(3) = SYSUTCDATETIME();
    DECLARE @RequestId uniqueidentifier;
    DECLARE @WorkType nvarchar(40);

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT
            @RequestId = work_item.[RequestId],
            @WorkType = work_item.[WorkType]
        FROM [tb_sync].[WhdSyncLeases] AS lease WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN [tb_sync].[WhdSyncWork] AS work_item WITH (UPDLOCK, HOLDLOCK)
            ON work_item.[WorkId] = lease.[WorkId]
        WHERE lease.[WorkId] = @WorkId
          AND lease.[LeaseId] = @LeaseId
          AND lease.[WorkerId] = @WorkerId
          AND lease.[ExpiresAtUtc] > @Now
          AND work_item.[State] = N'Leased';

        IF @RequestId IS NULL
            THROW 51813, N'Valid WHD work lease required.', 1;

        IF @CursorValue IS NOT NULL
           AND
           (
               @Succeeded <> 1
               OR @WorkType <> N'Tickets'
               OR TRY_CONVERT(datetimeoffset(3), @CursorValue) IS NULL
           )
            THROW 51816, N'Only successful Tickets work with a valid UTC cursor may advance WHD state.', 1;

        UPDATE [tb_sync].[WhdSyncWork]
        SET
            [State] = CASE WHEN @Succeeded = 1 THEN N'Completed' ELSE N'Failed' END,
            [CompletedAtUtc] = @Now,
            [ErrorMessage] = CASE WHEN @Succeeded = 1 THEN NULL ELSE @Message END
        WHERE [WorkId] = @WorkId;

        /* Cursor changes only after successful, durably applied Tickets work. */
        IF @Succeeded = 1 AND @WorkType = N'Tickets' AND @CursorValue IS NOT NULL
        BEGIN
            MERGE [tb_sync].[WhdSyncCursors] AS target
            USING
            (
                SELECT N'WhdTickets' AS [CursorName], @CursorValue AS [CursorValue]
            ) AS source
                ON target.[CursorName] = source.[CursorName]
            WHEN MATCHED AND
            (
                TRY_CONVERT(datetimeoffset(3), target.[CursorValue]) IS NULL
                OR TRY_CONVERT(datetimeoffset(3), source.[CursorValue])
                   > TRY_CONVERT(datetimeoffset(3), target.[CursorValue])
            ) THEN
                UPDATE SET
                    [CursorValue] = source.[CursorValue],
                    [UpdatedAtUtc] = @Now
            WHEN NOT MATCHED THEN
                INSERT ([CursorName], [CursorValue])
                VALUES (source.[CursorName], source.[CursorValue]);
        END;

        DELETE FROM [tb_sync].[WhdSyncLeases]
        WHERE [WorkId] = @WorkId;

        DECLARE @HasPendingWork bit = CASE WHEN EXISTS
        (
            SELECT 1
            FROM [tb_sync].[WhdSyncWork]
            WHERE [RequestId] = @RequestId
              AND [State] IN (N'Queued', N'Leased')
        ) THEN 1 ELSE 0 END;
        DECLARE @HasFailedWork bit = CASE WHEN EXISTS
        (
            SELECT 1
            FROM [tb_sync].[WhdSyncWork]
            WHERE [RequestId] = @RequestId
              AND [State] = N'Failed'
        ) THEN 1 ELSE 0 END;
        DECLARE @FailureMessage nvarchar(2000) =
        (
            SELECT TOP (1) [ErrorMessage]
            FROM [tb_sync].[WhdSyncWork]
            WHERE [RequestId] = @RequestId
              AND [State] = N'Failed'
            ORDER BY [CompletedAtUtc] DESC, [WorkId]
        );

        UPDATE [tb_sync].[WhdSyncRequests]
        SET
            [Status] = CASE
                WHEN @HasPendingWork = 1 THEN N'Running'
                WHEN @HasFailedWork = 1 THEN N'Failed'
                ELSE N'Completed'
            END,
            [CompletedAtUtc] = CASE WHEN @HasPendingWork = 0 THEN @Now ELSE NULL END,
            [Message] = LEFT
            (
                CASE
                    WHEN @HasFailedWork = 1 THEN COALESCE(@FailureMessage, N'WHD synchronization failed.')
                    WHEN @HasPendingWork = 0 THEN @Message
                    ELSE NULL
                END,
                1000
            )
        WHERE [RequestId] = @RequestId;

        /* Health is request-level: a successful sibling cannot hide a failed
           work item or claim the whole synchronization succeeded. */
        IF @HasPendingWork = 0
        BEGIN
            UPDATE [tb_sync].[WhdSyncHealth]
            SET
                [LastAttemptAtUtc] = @Now,
                [LastSuccessfulAtUtc] = CASE
                    WHEN @HasFailedWork = 0 THEN @Now
                    ELSE [LastSuccessfulAtUtc]
                END,
                [LastError] = CASE
                    WHEN @HasFailedWork = 1
                        THEN COALESCE(@FailureMessage, N'WHD synchronization failed.')
                    ELSE NULL
                END,
                [UpdatedAtUtc] = @Now
            WHERE [HealthId] = 1;
        END;

        COMMIT TRANSACTION;

        SELECT @WorkId AS [WorkId], @Succeeded AS [Succeeded];
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

/* Admin endpoints may request, monitor, and map users, but never submit snapshots. */
IF OBJECT_ID(N'tb_app.AdminRequestWhdSync', N'P') IS NOT NULL DROP PROCEDURE [tb_app].[AdminRequestWhdSync];
GO
CREATE PROCEDURE [tb_app].[AdminRequestWhdSync] @RequestType nvarchar(40)=N'Incremental', @RequestId uniqueidentifier=NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Sid varbinary(85), @Login nvarchar(256), @Name nvarchar(160);
    DECLARE @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @Sid OUTPUT,
        @LoginName = @Login OUTPUT,
        @DisplayName = @Name OUTPUT,
        @IsTechnician = @Tech OUTPUT,
        @IsManager = @Manager OUTPUT,
        @IsAdmin = @Admin OUTPUT,
        @IsSyncOperator = @Sync OUTPUT;

    IF @Admin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51820, N'Only a TechBench Admin may request WHD sync.', 1;
    IF @RequestType NOT IN (N'Full', N'Incremental')
        THROW 51821, N'RequestType must be Full or Incremental.', 1;

    IF @RequestType = N'Incremental'
       AND NOT EXISTS
       (
           SELECT 1
           FROM [tb_sync].[WhdSyncCursors]
           WHERE [CursorName] = N'WhdTickets'
             AND NULLIF([CursorValue], N'') IS NOT NULL
       )
        SET @RequestType = N'Full';

    SET @RequestId = COALESCE(@RequestId, NEWID());

    BEGIN TRY
        BEGIN TRANSACTION;

        DECLARE @QueueLockResult int;
        EXEC @QueueLockResult = sys.sp_getapplock
            @Resource = N'TechBench.WHD.SyncQueue',
            @LockMode = N'Exclusive',
            @LockOwner = N'Transaction',
            @LockTimeout = 5000;
        IF @QueueLockResult < 0
            THROW 51817, N'Could not acquire the WHD synchronization queue lock.', 1;

        DECLARE @ExistingRequestId uniqueidentifier =
        (
            SELECT TOP (1) request_row.[RequestId]
            FROM [tb_sync].[WhdSyncRequests] AS request_row
            INNER JOIN [tb_sync].[WhdSyncWork] AS work_item
                ON work_item.[RequestId] = request_row.[RequestId]
            WHERE work_item.[State] IN (N'Queued', N'Leased')
            ORDER BY request_row.[RequestedAtUtc], request_row.[RequestId]
        );
        IF @ExistingRequestId IS NOT NULL
        BEGIN
            COMMIT TRANSACTION;
            SELECT @ExistingRequestId AS [RequestId], N'AlreadyQueued' AS [Status];
            RETURN;
        END;

        INSERT INTO [tb_sync].[WhdSyncRequests]
            ([RequestId], [RequestedByWindowsSid], [RequestType])
        VALUES
            (@RequestId, @Sid, @RequestType);

        INSERT INTO [tb_sync].[WhdSyncWork]([WorkId], [RequestId], [WorkType])
        SELECT NEWID(), @RequestId, work_type.[WorkType]
        FROM
        (
            VALUES
                (N'Clients'),
                (N'Statuses'),
                (N'Technicians'),
                (N'Groups'),
                (N'Tickets')
        ) AS work_type([WorkType])
        WHERE @RequestType = N'Full'
           OR work_type.[WorkType] = N'Tickets';

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT @RequestId AS [RequestId], N'Queued' AS [Status];
END;
GO

IF OBJECT_ID(N'tb_app.GetWhdSyncStatus', N'P') IS NOT NULL DROP PROCEDURE [tb_app].[GetWhdSyncStatus];
GO
CREATE PROCEDURE [tb_app].[GetWhdSyncStatus]
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Sid varbinary(85), @Login nvarchar(256), @Name nvarchar(160);
    DECLARE @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @Sid OUTPUT,
        @LoginName = @Login OUTPUT,
        @DisplayName = @Name OUTPUT,
        @IsTechnician = @Tech OUTPUT,
        @IsManager = @Manager OUTPUT,
        @IsAdmin = @Admin OUTPUT,
        @IsSyncOperator = @Sync OUTPUT;

    IF @Admin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51822, N'Only a TechBench Admin may monitor WHD sync.', 1;

    SELECT TOP (1)
        request_row.[RequestId],
        request_row.[RequestType],
        request_row.[Status],
        request_row.[RequestedAtUtc],
        request_row.[CompletedAtUtc],
        request_row.[Message],
        SUM(CASE WHEN work_item.[State] = N'Completed' THEN 1 ELSE 0 END) AS [CompletedWorkCount],
        SUM(CASE WHEN work_item.[State] = N'Failed' THEN 1 ELSE 0 END) AS [FailedWorkCount],
        SUM(CASE WHEN work_item.[State] IN (N'Queued', N'Leased') THEN 1 ELSE 0 END) AS [QueueDepth]
    FROM [tb_sync].[WhdSyncRequests] AS request_row
    INNER JOIN [tb_sync].[WhdSyncWork] AS work_item
        ON work_item.[RequestId] = request_row.[RequestId]
    GROUP BY
        request_row.[RequestId], request_row.[RequestType], request_row.[Status],
        request_row.[RequestedAtUtc], request_row.[CompletedAtUtc], request_row.[Message]
    ORDER BY request_row.[RequestedAtUtc] DESC, request_row.[RequestId] DESC;

    SELECT [LastSuccessfulAtUtc], [LastAttemptAtUtc], [LastError], [UpdatedAtUtc]
    FROM [tb_sync].[WhdSyncHealth]
    WHERE [HealthId] = 1;
END;
GO

IF OBJECT_ID(N'tb_app.AdminGetWhdUserMappings', N'P') IS NOT NULL DROP PROCEDURE [tb_app].[AdminGetWhdUserMappings];
GO
CREATE PROCEDURE [tb_app].[AdminGetWhdUserMappings]
AS
BEGIN
 SET NOCOUNT ON; IF IS_ROLEMEMBER(N'tb_role_admin')<>1 THROW 51823,N'Only a TechBench Admin may manage WHD user mappings.',1; SELECT COALESCE(m.[Id],0) [Id],CONVERT(varchar(170),u.[WindowsSid],1) [UserSid],u.[LoginName],u.[DisplayName],m.[TechnicianExternalId],t.[DisplayName] [TechnicianDisplayName],m.[UpdatedAtUtc] FROM [tb_security].[Users] u LEFT JOIN [tb_whd].[UserTechnicianMappings] m ON m.[WindowsSid]=u.[WindowsSid] LEFT JOIN [tb_whd].[Technicians] t ON t.[ExternalId]=m.[TechnicianExternalId] WHERE u.[IsTechnician]=1 ORDER BY u.[LoginName]; END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveWhdUserMapping', N'P') IS NOT NULL DROP PROCEDURE [tb_app].[AdminSaveWhdUserMapping];
GO
CREATE PROCEDURE [tb_app].[AdminSaveWhdUserMapping]
    @WindowsLoginName nvarchar(256),
    @TechnicianExternalId nvarchar(120) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Actor varbinary(85), @Sid varbinary(85), @Login nvarchar(256), @Name nvarchar(160);
    DECLARE @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @Actor OUTPUT,
        @LoginName = @Login OUTPUT,
        @DisplayName = @Name OUTPUT,
        @IsTechnician = @Tech OUTPUT,
        @IsManager = @Manager OUTPUT,
        @IsAdmin = @Admin OUTPUT,
        @IsSyncOperator = @Sync OUTPUT;

    IF @Admin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 51824, N'Only a TechBench Admin may manage WHD user mappings.', 1;

    SET @WindowsLoginName = NULLIF(LTRIM(RTRIM(@WindowsLoginName)), N'');
    SET @TechnicianExternalId = NULLIF(LTRIM(RTRIM(@TechnicianExternalId)), N'');
    SET @Sid = SUSER_SID(@WindowsLoginName);

    IF @Sid IS NULL
       OR NOT EXISTS (SELECT 1 FROM [tb_security].[Users] WHERE [WindowsSid] = @Sid)
        THROW 51825, N'The mapped Windows user must have signed in to TechBench.', 1;

    IF @TechnicianExternalId IS NOT NULL
       AND NOT EXISTS
       (
           SELECT 1
           FROM [tb_whd].[Technicians]
           WHERE [ExternalId] = @TechnicianExternalId
             AND [IsActive] = 1
       )
        THROW 51826, N'Unknown or inactive WHD technician.', 1;

    DECLARE @PreviousTechnicianExternalId nvarchar(120) =
    (
        SELECT [TechnicianExternalId]
        FROM [tb_whd].[UserTechnicianMappings]
        WHERE [WindowsSid] = @Sid
    );

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @TechnicianExternalId IS NULL
        BEGIN
            DELETE FROM [tb_whd].[UserTechnicianMappings]
            WHERE [WindowsSid] = @Sid;
        END
        ELSE
        BEGIN
            MERGE [tb_whd].[UserTechnicianMappings] AS target
            USING
            (
                SELECT @Sid AS [WindowsSid], @TechnicianExternalId AS [TechnicianExternalId]
            ) AS source
                ON target.[WindowsSid] = source.[WindowsSid]
            WHEN MATCHED THEN
                UPDATE SET
                    [TechnicianExternalId] = source.[TechnicianExternalId],
                    [UpdatedByWindowsSid] = @Actor,
                    [UpdatedAtUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT ([WindowsSid], [TechnicianExternalId], [UpdatedByWindowsSid])
                VALUES (source.[WindowsSid], source.[TechnicianExternalId], @Actor);
        END;

        DECLARE @AuditJson nvarchar(max) =
        (
            SELECT
                @WindowsLoginName AS [windowsLoginName],
                @PreviousTechnicianExternalId AS [previousTechnicianExternalId],
                @TechnicianExternalId AS [technicianExternalId]
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );
        DECLARE @AuditAction nvarchar(120) = CASE
            WHEN @TechnicianExternalId IS NULL THEN N'WhdUserMappingRemoved'
            ELSE N'WhdUserMappingSaved'
        END;
        DECLARE @AuditEntityId nvarchar(120) = LEFT(@WindowsLoginName, 120);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = @AuditAction,
            @EntityType = N'WhdUserMapping',
            @EntityId = @AuditEntityId,
            @RequestId = NULL,
            @DataJson = @AuditJson;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        COALESCE(mapping.[Id], 0) AS [Id],
        CONVERT(varchar(170), user_row.[WindowsSid], 1) AS [UserSid],
        user_row.[LoginName],
        user_row.[DisplayName],
        mapping.[TechnicianExternalId],
        technician.[DisplayName] AS [TechnicianDisplayName]
    FROM [tb_security].[Users] AS user_row
    LEFT JOIN [tb_whd].[UserTechnicianMappings] AS mapping
        ON mapping.[WindowsSid] = user_row.[WindowsSid]
    LEFT JOIN [tb_whd].[Technicians] AS technician
        ON technician.[ExternalId] = mapping.[TechnicianExternalId]
    WHERE user_row.[WindowsSid] = @Sid;
END;
GO

IF OBJECT_ID(N'tb_app.AdminGetWhdTechnicians', N'P') IS NOT NULL DROP PROCEDURE [tb_app].[AdminGetWhdTechnicians];
GO
CREATE PROCEDURE [tb_app].[AdminGetWhdTechnicians]
AS
BEGIN
 SET NOCOUNT ON; IF IS_ROLEMEMBER(N'tb_role_admin')<>1 THROW 51827,N'Only a TechBench Admin may view WHD technicians.',1; SELECT [ExternalId],[DisplayName],[Username],[Email],[IsActive],[WhdLastUpdatedUtc],[LastSyncedAtUtc] FROM [tb_whd].[Technicians] ORDER BY [DisplayName],[ExternalId]; END;
GO

/* WHD is access-scoped for ordinary users; non-WHD tickets retain V0002 behavior. */
IF OBJECT_ID(N'tb_app.SearchTickets', N'P') IS NOT NULL DROP PROCEDURE [tb_app].[SearchTickets];
GO
CREATE PROCEDURE [tb_app].[SearchTickets] @ClientId int=NULL,@Search nvarchar(240)=NULL,@IncludeClosed bit=0,@Limit int=500
AS
BEGIN
 SET NOCOUNT ON; DECLARE @Sid varbinary(85),@Login nvarchar(256),@Name nvarchar(160),@Tech bit,@Manager bit,@Admin bit,@Sync bit; EXEC [tb_security].[EnsureCurrentUser] @UserSid=@Sid OUTPUT,@LoginName=@Login OUTPUT,@DisplayName=@Name OUTPUT,@IsTechnician=@Tech OUTPUT,@IsManager=@Manager OUTPUT,@IsAdmin=@Admin OUTPUT,@IsSyncOperator=@Sync OUTPUT; SET @Limit=CASE WHEN @Limit IS NULL OR @Limit<1 THEN 1 WHEN @Limit>2000 THEN 2000 ELSE @Limit END; SET @Search=NULLIF(LTRIM(RTRIM(@Search)),N''); DECLARE @Pattern nvarchar(500)=CASE WHEN @Search IS NULL THEN NULL ELSE N'%'+REPLACE(REPLACE(REPLACE(REPLACE(@Search,N'~',N'~~'),N'%',N'~%'),N'_',N'~_'),N'[',N'~[')+N'%' END; SELECT TOP(@Limit) t.[Id],t.[TicketNumber],t.[ClientId],t.[Subject],t.[Status],t.[Source],t.[ExternalId],t.[WhdStatusTypeId],t.[IsClosed],t.[LastSyncedAtUtc] [LastSyncedAt],t.[WhdLastUpdatedUtc],t.[IsWhdDeleted],t.[AssignedTechExternalId],t.[AssignedTechName],t.[AssignedGroupExternalId],t.[AssignedGroupName],t.[RowVersion] FROM [tb_data].[Tickets] t WHERE (@ClientId IS NULL OR t.[ClientId]=@ClientId) AND (@IncludeClosed=1 OR t.[IsClosed]=0) AND (@Pattern IS NULL OR t.[TicketNumber] LIKE @Pattern ESCAPE N'~' OR t.[Subject] LIKE @Pattern ESCAPE N'~' OR t.[Status] LIKE @Pattern ESCAPE N'~' OR t.[ExternalId] LIKE @Pattern ESCAPE N'~') AND (t.[Source]<>N'WHD' OR @Admin=1 OR EXISTS(SELECT 1 FROM [tb_whd].[UserTechnicianMappings] m WHERE m.[WindowsSid]=@Sid AND (m.[TechnicianExternalId]=t.[AssignedTechExternalId] OR EXISTS(SELECT 1 FROM [tb_whd].[TechnicianGroupMemberships] gm WHERE gm.[TechnicianExternalId]=m.[TechnicianExternalId] AND gm.[GroupExternalId]=t.[AssignedGroupExternalId])))) ORDER BY t.[IsClosed],t.[TicketNumber],t.[Id]; END;
GO

IF OBJECT_ID(N'tb_app.GetTicket', N'P') IS NOT NULL DROP PROCEDURE [tb_app].[GetTicket];
GO
CREATE PROCEDURE [tb_app].[GetTicket] @Id int
AS
BEGIN
 SET NOCOUNT ON; DECLARE @Sid varbinary(85),@Login nvarchar(256),@Name nvarchar(160),@Tech bit,@Manager bit,@Admin bit,@Sync bit; EXEC [tb_security].[EnsureCurrentUser] @UserSid=@Sid OUTPUT,@LoginName=@Login OUTPUT,@DisplayName=@Name OUTPUT,@IsTechnician=@Tech OUTPUT,@IsManager=@Manager OUTPUT,@IsAdmin=@Admin OUTPUT,@IsSyncOperator=@Sync OUTPUT; SELECT t.[Id],t.[TicketNumber],t.[ClientId],t.[Subject],t.[Status],t.[Source],t.[ExternalId],t.[WhdStatusTypeId],t.[IsClosed],t.[LastSyncedAtUtc] [LastSyncedAt],t.[WhdLastUpdatedUtc],t.[IsWhdDeleted],t.[AssignedTechExternalId],t.[AssignedTechName],t.[AssignedGroupExternalId],t.[AssignedGroupName],t.[RowVersion] FROM [tb_data].[Tickets] t WHERE t.[Id]=@Id AND (t.[Source]<>N'WHD' OR @Admin=1 OR EXISTS(SELECT 1 FROM [tb_whd].[UserTechnicianMappings] m WHERE m.[WindowsSid]=@Sid AND (m.[TechnicianExternalId]=t.[AssignedTechExternalId] OR EXISTS(SELECT 1 FROM [tb_whd].[TechnicianGroupMemberships] gm WHERE gm.[TechnicianExternalId]=m.[TechnicianExternalId] AND gm.[GroupExternalId]=t.[AssignedGroupExternalId])))); END;
GO

/* Enforce the same WHD assignment boundary at the table, not only in ticket
   search procedures. This also protects SaveTicket, SaveWorkEntry, work-entry
   joins, and any future procedure that touches tb_data.Tickets. */
IF EXISTS
(
    SELECT 1
    FROM sys.security_policies AS policy
    INNER JOIN sys.schemas AS schema_row
        ON schema_row.[schema_id] = policy.[schema_id]
    WHERE schema_row.[name] = N'tb_security'
      AND policy.[name] = N'WhdTicketAccessPolicy'
)
BEGIN
    EXEC sys.sp_executesql
        N'ALTER SECURITY POLICY [tb_security].[WhdTicketAccessPolicy] WITH (STATE = OFF);';
    EXEC sys.sp_executesql
        N'DROP SECURITY POLICY [tb_security].[WhdTicketAccessPolicy];';
END;
GO

IF OBJECT_ID(N'tb_security.FilterWhdTicketAccess', N'IF') IS NOT NULL
    DROP FUNCTION [tb_security].[FilterWhdTicketAccess];
GO

CREATE FUNCTION [tb_security].[FilterWhdTicketAccess]
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
    WHERE @Source <> N'WHD'
       OR USER_NAME() = N'dbo'
       OR IS_ROLEMEMBER(N'db_owner') = 1
       OR IS_ROLEMEMBER(N'tb_role_admin') = 1
       OR IS_ROLEMEMBER(N'tb_role_sync_service') = 1
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
);
GO

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
    WITH (STATE = ON, SCHEMABINDING = ON);
GO
