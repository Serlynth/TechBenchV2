:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.ServerOwnedSageAndAdminPreview.0007'
      AND [SchemaVersion] = 7
)
BEGIN
    RAISERROR(N'V0007 must be installed before FireDrillCredentials.0008.', 16, 1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE [name] = N'##MS_DatabaseMasterKey##')
    BEGIN
        DECLARE @MasterKeyPassword nvarchar(128) =
            N'TB-' + CONVERT(nvarchar(36), NEWID()) + N'-' + CONVERT(nvarchar(36), NEWID());
        DECLARE @CreateMasterKeySql nvarchar(max) =
            N'CREATE MASTER KEY ENCRYPTION BY PASSWORD = N''' + REPLACE(@MasterKeyPassword, N'''', N'''''') + N''';';
        EXEC sys.sp_executesql @CreateMasterKeySql;

        PRINT N'IMPORTANT: A database master key was created for FireDrill credential encryption.';
        SELECT @MasterKeyPassword AS [DatabaseMasterKeyRecoveryPassword];
        PRINT N'Store the recovery password shown in the Results grid in your protected administrative password vault.';
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.certificates WHERE [name] = N'tb_FireDrillCredentialCertificate')
        CREATE CERTIFICATE [tb_FireDrillCredentialCertificate]
            WITH SUBJECT = N'TechBench FireDrill credential encryption';

    IF NOT EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE [name] = N'tb_FireDrillCredentialKey')
        CREATE SYMMETRIC KEY [tb_FireDrillCredentialKey]
            WITH ALGORITHM = AES_256
            ENCRYPTION BY CERTIFICATE [tb_FireDrillCredentialCertificate];

    IF OBJECT_ID(N'tb_data.FireDrillCredentials', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_data].[FireDrillCredentials]
        (
            [CredentialId] bigint IDENTITY(1,1) NOT NULL,
            [ClientKey] nvarchar(200) NOT NULL,
            [ClientName] nvarchar(240) NOT NULL,
            [FireboxIp] nvarchar(120) NULL,
            [Status] nvarchar(120) NULL,
            [AdminEncrypted] varbinary(max) NULL,
            [CsriAdminEncrypted] varbinary(max) NULL,
            [FireboxDbCsriEncrypted] varbinary(max) NULL,
            [AuthpointUserEncrypted] varbinary(max) NULL,
            [SslVpnPasswordEncrypted] varbinary(max) NULL,
            [AdAuthUserEncrypted] varbinary(max) NULL,
            [AdPasswordEncrypted] varbinary(max) NULL,
            [RustPasswordEncrypted] varbinary(max) NULL,
            [SourceRowHash] binary(32) NOT NULL,
            [SourceModifiedAtUtc] datetime2(3) NOT NULL,
            [LastSyncedAtUtc] datetime2(3) NOT NULL,
            [IsCurrent] bit NOT NULL CONSTRAINT [DF_FireDrillCredentials_IsCurrent] DEFAULT (1),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_FireDrillCredentials] PRIMARY KEY CLUSTERED ([CredentialId]),
            CONSTRAINT [UQ_FireDrillCredentials_ClientKey] UNIQUE ([ClientKey]),
            CONSTRAINT [CK_FireDrillCredentials_ClientKey] CHECK (LEN(LTRIM(RTRIM([ClientKey]))) > 0),
            CONSTRAINT [CK_FireDrillCredentials_ClientName] CHECK (LEN(LTRIM(RTRIM([ClientName]))) > 0)
        );

        CREATE INDEX [IX_FireDrillCredentials_Search]
            ON [tb_data].[FireDrillCredentials]([IsCurrent], [ClientName], [CredentialId])
            INCLUDE ([FireboxIp], [Status], [LastSyncedAtUtc]);
    END;

    IF OBJECT_ID(N'tb_sync.FireDrillSyncRequests', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_sync].[FireDrillSyncRequests]
        (
            [RequestId] uniqueidentifier NOT NULL,
            [RequestedByWindowsSid] varbinary(85) NOT NULL,
            [RequestType] nvarchar(20) NOT NULL,
            [RequestedAtUtc] datetime2(3) NOT NULL CONSTRAINT [DF_FireDrillSyncRequests_Requested] DEFAULT (SYSUTCDATETIME()),
            [StartedAtUtc] datetime2(3) NULL,
            [CompletedAtUtc] datetime2(3) NULL,
            [Status] nvarchar(30) NOT NULL CONSTRAINT [DF_FireDrillSyncRequests_Status] DEFAULT (N'Queued'),
            [ReadCount] int NOT NULL CONSTRAINT [DF_FireDrillSyncRequests_ReadCount] DEFAULT (0),
            [SavedCount] int NOT NULL CONSTRAINT [DF_FireDrillSyncRequests_SavedCount] DEFAULT (0),
            [StaleCount] int NOT NULL CONSTRAINT [DF_FireDrillSyncRequests_StaleCount] DEFAULT (0),
            [AttemptCount] int NOT NULL CONSTRAINT [DF_FireDrillSyncRequests_AttemptCount] DEFAULT (0),
            [Message] nvarchar(2000) NULL,
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_FireDrillSyncRequests] PRIMARY KEY CLUSTERED ([RequestId]),
            CONSTRAINT [FK_FireDrillSyncRequests_Requester] FOREIGN KEY ([RequestedByWindowsSid]) REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_FireDrillSyncRequests_Type] CHECK ([RequestType] IN (N'Automatic', N'Manual')),
            CONSTRAINT [CK_FireDrillSyncRequests_Status] CHECK ([Status] IN (N'Queued', N'Running', N'Completed', N'Failed')),
            CONSTRAINT [CK_FireDrillSyncRequests_Counts] CHECK ([ReadCount] >= 0 AND [SavedCount] >= 0 AND [StaleCount] >= 0 AND [AttemptCount] >= 0)
        );

        CREATE INDEX [IX_FireDrillSyncRequests_StatusRequested]
            ON [tb_sync].[FireDrillSyncRequests]([Status], [RequestedAtUtc], [RequestId])
            INCLUDE ([StartedAtUtc], [CompletedAtUtc], [RequestType], [AttemptCount]);
    END;

    IF OBJECT_ID(N'tb_sync.FireDrillSyncLeases', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_sync].[FireDrillSyncLeases]
        (
            [RequestId] uniqueidentifier NOT NULL,
            [LeaseId] uniqueidentifier NOT NULL,
            [WorkerId] uniqueidentifier NOT NULL,
            [AcquiredAtUtc] datetime2(3) NOT NULL CONSTRAINT [DF_FireDrillSyncLeases_Acquired] DEFAULT (SYSUTCDATETIME()),
            [ExpiresAtUtc] datetime2(3) NOT NULL,
            CONSTRAINT [PK_FireDrillSyncLeases] PRIMARY KEY CLUSTERED ([RequestId]),
            CONSTRAINT [UQ_FireDrillSyncLeases_LeaseId] UNIQUE ([LeaseId]),
            CONSTRAINT [FK_FireDrillSyncLeases_Request] FOREIGN KEY ([RequestId]) REFERENCES [tb_sync].[FireDrillSyncRequests]([RequestId]),
            CONSTRAINT [CK_FireDrillSyncLeases_Expiry] CHECK ([ExpiresAtUtc] > [AcquiredAtUtc])
        );
    END;

    IF OBJECT_ID(N'tb_sync.FireDrillSyncHealth', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_sync].[FireDrillSyncHealth]
        (
            [HealthId] tinyint NOT NULL CONSTRAINT [PK_FireDrillSyncHealth] PRIMARY KEY CONSTRAINT [CK_FireDrillSyncHealth_OneRow] CHECK ([HealthId] = 1),
            [LastAttemptAtUtc] datetime2(3) NULL,
            [LastSuccessfulAtUtc] datetime2(3) NULL,
            [LastSourceModifiedAtUtc] datetime2(3) NULL,
            [LastError] nvarchar(2000) NULL,
            [UpdatedAtUtc] datetime2(3) NOT NULL CONSTRAINT [DF_FireDrillSyncHealth_Updated] DEFAULT (SYSUTCDATETIME())
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM [tb_sync].[FireDrillSyncHealth] WHERE [HealthId] = 1)
        INSERT INTO [tb_sync].[FireDrillSyncHealth]([HealthId]) VALUES (1);

    IF NOT EXISTS
    (
        SELECT 1 FROM [tb_deploy].[SchemaMigrations]
        WHERE [MigrationId] = N'SqlServer2016.FireDrillCredentials.0008'
    )
        INSERT INTO [tb_deploy].[SchemaMigrations]
            ([MigrationId], [SchemaVersion], [ReleaseVersion], [ScriptChecksum])
        VALUES
            (N'SqlServer2016.FireDrillCredentials.0008', 8, N'0.5.6', NULL);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'SqlServer2016.FireDrillCredentials.0008 installed.';
GO
