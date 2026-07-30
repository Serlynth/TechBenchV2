:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.WhdMissingNoteRecovery.0009'
      AND [SchemaVersion] = 9
)
BEGIN
    RAISERROR(N'V0009 must be installed before client presence schema version 10.', 16, 1);
    RETURN;
END;

IF OBJECT_ID(N'tb_security.ClientSessions', N'U') IS NULL
BEGIN
    CREATE TABLE [tb_security].[ClientSessions]
    (
        [SessionId] uniqueidentifier NOT NULL,
        [WindowsSid] varbinary(85) NOT NULL,
        [DeviceId] uniqueidentifier NOT NULL,
        [MachineName] nvarchar(128) NOT NULL,
        [ClientVersion] nvarchar(40) NOT NULL,
        [CurrentSection] nvarchar(80) NULL,
        [HasUnsavedChanges] bit NOT NULL
            CONSTRAINT [DF_ClientSessions_HasUnsavedChanges] DEFAULT (0),
        [IsBusy] bit NOT NULL
            CONSTRAINT [DF_ClientSessions_IsBusy] DEFAULT (0),
        [StartedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_ClientSessions_StartedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [LastSeenAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_ClientSessions_LastSeenAtUtc] DEFAULT (SYSUTCDATETIME()),
        [ClosedAtUtc] datetime2(3) NULL,
        CONSTRAINT [PK_ClientSessions] PRIMARY KEY CLUSTERED ([SessionId]),
        CONSTRAINT [FK_ClientSessions_User]
            FOREIGN KEY ([WindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_ClientSessions_MachineName]
            CHECK (LEN(LTRIM(RTRIM([MachineName]))) BETWEEN 1 AND 128),
        CONSTRAINT [CK_ClientSessions_ClientVersion]
            CHECK (LEN(LTRIM(RTRIM([ClientVersion]))) BETWEEN 1 AND 40)
    );

    CREATE INDEX [IX_ClientSessions_Active]
        ON [tb_security].[ClientSessions]([ClosedAtUtc], [LastSeenAtUtc])
        INCLUDE
        (
            [WindowsSid], [MachineName], [ClientVersion], [CurrentSection],
            [HasUnsavedChanges], [IsBusy], [StartedAtUtc]
        );
END;

IF OBJECT_ID(N'tb_security.ClientSessionCommands', N'U') IS NULL
BEGIN
    CREATE TABLE [tb_security].[ClientSessionCommands]
    (
        [CommandId] bigint IDENTITY(1,1) NOT NULL,
        [SessionId] uniqueidentifier NOT NULL,
        [CommandType] nvarchar(30) NOT NULL,
        [Message] nvarchar(500) NOT NULL,
        [RequestedByWindowsSid] varbinary(85) NOT NULL,
        [RequestId] uniqueidentifier NOT NULL,
        [RequestedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_ClientSessionCommands_RequestedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [DeliveredAtUtc] datetime2(3) NULL,
        [AcknowledgedAtUtc] datetime2(3) NULL,
        [AcknowledgementResult] nvarchar(40) NULL,
        CONSTRAINT [PK_ClientSessionCommands] PRIMARY KEY CLUSTERED ([CommandId]),
        CONSTRAINT [FK_ClientSessionCommands_Session]
            FOREIGN KEY ([SessionId])
            REFERENCES [tb_security].[ClientSessions]([SessionId]),
        CONSTRAINT [FK_ClientSessionCommands_RequestedBy]
            FOREIGN KEY ([RequestedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [UX_ClientSessionCommands_RequestId] UNIQUE ([RequestId]),
        CONSTRAINT [CK_ClientSessionCommands_CommandType]
            CHECK ([CommandType] IN (N'UpdateNotice', N'SignOut')),
        CONSTRAINT [CK_ClientSessionCommands_Message]
            CHECK (LEN(LTRIM(RTRIM([Message]))) BETWEEN 1 AND 500),
        CONSTRAINT [CK_ClientSessionCommands_AcknowledgementResult]
            CHECK
            (
                [AcknowledgementResult] IS NULL
                OR [AcknowledgementResult] IN
                    (N'Displayed', N'SignedOut', N'Ignored', N'Failed')
            )
    );

    CREATE INDEX [IX_ClientSessionCommands_Pending]
        ON [tb_security].[ClientSessionCommands]
            ([SessionId], [AcknowledgedAtUtc], [CommandId])
        INCLUDE
            ([CommandType], [Message], [RequestedByWindowsSid], [RequestedAtUtc], [DeliveredAtUtc]);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.ClientPresence.0010'
)
BEGIN
    INSERT INTO [tb_deploy].[SchemaMigrations]
        ([MigrationId], [SchemaVersion], [ReleaseVersion], [ScriptChecksum])
    VALUES
        (N'SqlServer2016.ClientPresence.0010', 10, N'0.5.23', NULL);
END;

PRINT N'SqlServer2016.ClientPresence.0010 installed.';
GO
