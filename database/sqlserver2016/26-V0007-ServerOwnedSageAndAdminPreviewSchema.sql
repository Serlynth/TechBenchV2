:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.WhdServerSync.0006'
      AND [SchemaVersion] = 6
)
BEGIN
    RAISERROR(N'V0006 must be installed before ServerOwnedSageAndAdminPreview.0007.', 16, 1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    IF DATABASE_PRINCIPAL_ID(N'tb_preview_reader') IS NULL
        CREATE USER [tb_preview_reader] WITHOUT LOGIN;

    IF OBJECT_ID(N'tb_sync.SageSyncRequests', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_sync].[SageSyncRequests]
        (
            [RequestId] uniqueidentifier NOT NULL,
            [RequestedByWindowsSid] varbinary(85) NOT NULL,
            [RequestedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_SageSyncRequests_Requested] DEFAULT (SYSUTCDATETIME()),
            [StartedAtUtc] datetime2(3) NULL,
            [CompletedAtUtc] datetime2(3) NULL,
            [Status] nvarchar(30) NOT NULL
                CONSTRAINT [DF_SageSyncRequests_Status] DEFAULT (N'Queued'),
            [AllowLargeRemoval] bit NOT NULL
                CONSTRAINT [DF_SageSyncRequests_AllowLargeRemoval] DEFAULT (0),
            [RequiresLargeRemovalConfirmation] bit NOT NULL
                CONSTRAINT [DF_SageSyncRequests_RequiresLargeRemovalConfirmation] DEFAULT (0),
            [ConfirmedRequestId] uniqueidentifier NULL,
            [ExistingCount] int NOT NULL
                CONSTRAINT [DF_SageSyncRequests_ExistingCount] DEFAULT (0),
            [ReadCount] int NOT NULL
                CONSTRAINT [DF_SageSyncRequests_ReadCount] DEFAULT (0),
            [SavedCount] int NOT NULL
                CONSTRAINT [DF_SageSyncRequests_SavedCount] DEFAULT (0),
            [StaleCount] int NOT NULL
                CONSTRAINT [DF_SageSyncRequests_StaleCount] DEFAULT (0),
            [AttemptCount] int NOT NULL
                CONSTRAINT [DF_SageSyncRequests_AttemptCount] DEFAULT (0),
            [Message] nvarchar(2000) NULL,
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_SageSyncRequests] PRIMARY KEY CLUSTERED ([RequestId]),
            CONSTRAINT [FK_SageSyncRequests_Requester]
                FOREIGN KEY ([RequestedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_SageSyncRequests_ConfirmedRequest]
                FOREIGN KEY ([ConfirmedRequestId])
                REFERENCES [tb_sync].[SageSyncRequests]([RequestId]),
            CONSTRAINT [CK_SageSyncRequests_Status]
                CHECK ([Status] IN (N'Queued', N'Running', N'Completed', N'Failed')),
            CONSTRAINT [CK_SageSyncRequests_ConfirmationBinding]
                CHECK
                (
                    ([AllowLargeRemoval] = 0 AND [ConfirmedRequestId] IS NULL)
                    OR ([AllowLargeRemoval] = 1 AND [ConfirmedRequestId] IS NOT NULL)
                ),
            CONSTRAINT [CK_SageSyncRequests_Counts]
                CHECK
                (
                    [ExistingCount] >= 0
                    AND [ReadCount] >= 0
                    AND [SavedCount] >= 0
                    AND [StaleCount] >= 0
                    AND [AttemptCount] >= 0
                ),
            CONSTRAINT [CK_SageSyncRequests_Times]
                CHECK
                (
                    ([StartedAtUtc] IS NULL OR [StartedAtUtc] >= [RequestedAtUtc])
                    AND
                    (
                        [CompletedAtUtc] IS NULL
                        OR
                        (
                            [StartedAtUtc] IS NOT NULL
                            AND [CompletedAtUtc] >= [StartedAtUtc]
                        )
                    )
                )
        );
    END;

    /* Keep the stage rerunnable across pre-release V7 database rehearsals. */
    IF COL_LENGTH(N'tb_sync.SageSyncRequests', N'AllowLargeRemoval') IS NULL
        ALTER TABLE [tb_sync].[SageSyncRequests]
            ADD [AllowLargeRemoval] bit NOT NULL
                CONSTRAINT [DF_SageSyncRequests_AllowLargeRemoval] DEFAULT (0) WITH VALUES;

    IF COL_LENGTH(N'tb_sync.SageSyncRequests', N'RequiresLargeRemovalConfirmation') IS NULL
        ALTER TABLE [tb_sync].[SageSyncRequests]
            ADD [RequiresLargeRemovalConfirmation] bit NOT NULL
                CONSTRAINT [DF_SageSyncRequests_RequiresLargeRemovalConfirmation] DEFAULT (0) WITH VALUES;

    IF COL_LENGTH(N'tb_sync.SageSyncRequests', N'ExistingCount') IS NULL
        ALTER TABLE [tb_sync].[SageSyncRequests]
            ADD [ExistingCount] int NOT NULL
                CONSTRAINT [DF_SageSyncRequests_ExistingCount] DEFAULT (0) WITH VALUES;

    IF COL_LENGTH(N'tb_sync.SageSyncRequests', N'ConfirmedRequestId') IS NULL
        ALTER TABLE [tb_sync].[SageSyncRequests]
            ADD [ConfirmedRequestId] uniqueidentifier NULL;

    IF OBJECT_ID(N'tb_sync.CK_SageSyncRequests_ConfirmationBinding', N'C') IS NULL
    BEGIN
        UPDATE [tb_sync].[SageSyncRequests]
        SET [AllowLargeRemoval] = 0
        WHERE [ConfirmedRequestId] IS NULL AND [AllowLargeRemoval] <> 0;

        ALTER TABLE [tb_sync].[SageSyncRequests] WITH CHECK
            ADD CONSTRAINT [CK_SageSyncRequests_ConfirmationBinding]
                CHECK
                (
                    ([AllowLargeRemoval] = 0 AND [ConfirmedRequestId] IS NULL)
                    OR ([AllowLargeRemoval] = 1 AND [ConfirmedRequestId] IS NOT NULL)
                );
    END;

    IF OBJECT_ID(N'tb_sync.FK_SageSyncRequests_ConfirmedRequest', N'F') IS NULL
        ALTER TABLE [tb_sync].[SageSyncRequests] WITH CHECK
            ADD CONSTRAINT [FK_SageSyncRequests_ConfirmedRequest]
                FOREIGN KEY ([ConfirmedRequestId])
                REFERENCES [tb_sync].[SageSyncRequests]([RequestId]);

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_sync.SageSyncRequests')
          AND [name] = N'IX_SageSyncRequests_StatusRequested'
    )
        CREATE INDEX [IX_SageSyncRequests_StatusRequested]
            ON [tb_sync].[SageSyncRequests]([Status], [RequestedAtUtc], [RequestId])
            INCLUDE
            (
                [StartedAtUtc], [CompletedAtUtc], [AllowLargeRemoval],
                [RequiresLargeRemovalConfirmation], [ConfirmedRequestId],
                [ExistingCount], [ReadCount],
                [SavedCount], [StaleCount], [AttemptCount]
            );

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_sync.SageSyncRequests')
          AND [name] = N'IX_SageSyncRequests_RequestedAt'
    )
        CREATE INDEX [IX_SageSyncRequests_RequestedAt]
            ON [tb_sync].[SageSyncRequests]([RequestedAtUtc] DESC, [RequestId])
            INCLUDE
            (
                [Status], [CompletedAtUtc], [AllowLargeRemoval],
                [RequiresLargeRemovalConfirmation], [ConfirmedRequestId], [ExistingCount],
                [ReadCount], [SavedCount], [StaleCount]
            );

    IF OBJECT_ID(N'tb_sync.SageSyncLeases', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_sync].[SageSyncLeases]
        (
            [RequestId] uniqueidentifier NOT NULL,
            [LeaseId] uniqueidentifier NOT NULL,
            [WorkerId] uniqueidentifier NOT NULL,
            [AcquiredAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_SageSyncLeases_Acquired] DEFAULT (SYSUTCDATETIME()),
            [ExpiresAtUtc] datetime2(3) NOT NULL,
            CONSTRAINT [PK_SageSyncLeases] PRIMARY KEY CLUSTERED ([RequestId]),
            CONSTRAINT [UQ_SageSyncLeases_LeaseId] UNIQUE ([LeaseId]),
            CONSTRAINT [FK_SageSyncLeases_Request]
                FOREIGN KEY ([RequestId])
                REFERENCES [tb_sync].[SageSyncRequests]([RequestId]),
            CONSTRAINT [CK_SageSyncLeases_Expiry]
                CHECK ([ExpiresAtUtc] > [AcquiredAtUtc])
        );
    END;

    IF OBJECT_ID(N'tb_sync.SageSyncHealth', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_sync].[SageSyncHealth]
        (
            [HealthId] tinyint NOT NULL
                CONSTRAINT [PK_SageSyncHealth] PRIMARY KEY
                CONSTRAINT [CK_SageSyncHealth_OneRow] CHECK ([HealthId] = 1),
            [LastAttemptAtUtc] datetime2(3) NULL,
            [LastSuccessfulAtUtc] datetime2(3) NULL,
            [LastError] nvarchar(2000) NULL,
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_SageSyncHealth_Updated] DEFAULT (SYSUTCDATETIME())
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM [tb_sync].[SageSyncHealth] WHERE [HealthId] = 1)
        INSERT INTO [tb_sync].[SageSyncHealth]([HealthId]) VALUES (1);

    IF OBJECT_ID(N'tb_security.AdminUserPreviewSessions', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_security].[AdminUserPreviewSessions]
        (
            [PreviewSessionId] uniqueidentifier NOT NULL,
            [ActorWindowsSid] varbinary(85) NOT NULL,
            [TargetWindowsSid] varbinary(85) NOT NULL,
            [ClientInstanceId] uniqueidentifier NOT NULL,
            [StartedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_AdminUserPreviewSessions_Started] DEFAULT (SYSUTCDATETIME()),
            [ExpiresAtUtc] datetime2(3) NOT NULL,
            [EndedAtUtc] datetime2(3) NULL,
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_AdminUserPreviewSessions]
                PRIMARY KEY CLUSTERED ([PreviewSessionId]),
            CONSTRAINT [FK_AdminUserPreviewSessions_Actor]
                FOREIGN KEY ([ActorWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_AdminUserPreviewSessions_Target]
                FOREIGN KEY ([TargetWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_AdminUserPreviewSessions_DifferentUsers]
                CHECK ([ActorWindowsSid] <> [TargetWindowsSid]),
            CONSTRAINT [CK_AdminUserPreviewSessions_Expiry]
                CHECK ([ExpiresAtUtc] > [StartedAtUtc]),
            CONSTRAINT [CK_AdminUserPreviewSessions_Ended]
                CHECK ([EndedAtUtc] IS NULL OR [EndedAtUtc] >= [StartedAtUtc])
        );
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_security.AdminUserPreviewSessions')
          AND [name] = N'IX_AdminUserPreviewSessions_ActorActive'
    )
        CREATE INDEX [IX_AdminUserPreviewSessions_ActorActive]
            ON [tb_security].[AdminUserPreviewSessions]
                ([ActorWindowsSid], [EndedAtUtc], [ExpiresAtUtc] DESC)
            INCLUDE ([TargetWindowsSid], [ClientInstanceId]);

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'tb_security.AdminUserPreviewSessions')
          AND [name] = N'IX_AdminUserPreviewSessions_Expires'
    )
        CREATE INDEX [IX_AdminUserPreviewSessions_Expires]
            ON [tb_security].[AdminUserPreviewSessions]([ExpiresAtUtc], [PreviewSessionId])
            INCLUDE ([EndedAtUtc]);

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_deploy].[SchemaMigrations]
        WHERE [MigrationId] = N'SqlServer2016.ServerOwnedSageAndAdminPreview.0007'
    )
    BEGIN
        INSERT INTO [tb_deploy].[SchemaMigrations]
            ([MigrationId], [SchemaVersion], [ReleaseVersion], [ScriptChecksum])
        VALUES
            (N'SqlServer2016.ServerOwnedSageAndAdminPreview.0007', 7, N'2.0.0-alpha.8', NULL);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'SqlServer2016.ServerOwnedSageAndAdminPreview.0007 is installed.';
GO
