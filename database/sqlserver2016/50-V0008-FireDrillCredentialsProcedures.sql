:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

ALTER PROCEDURE [tb_app].[GetRepositoryCapabilities]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @UserSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@UserSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;
    SELECT CONVERT(int, 10) AS [SchemaVersion], CONVERT(bit, 0) AS [FullTextSearchAvailable],
        CONVERT(bit, 1) AS [SupportsTickets], CONVERT(bit, 1) AS [SupportsWorkEntries],
        CONVERT(bit, 1) AS [SupportsPrivateNotes], CONVERT(bit, 1) AS [SupportsPostingLeases],
        CONVERT(bit, 1) AS [SupportsSyncLeases], CONVERT(bit, 1) AS [SupportsImports],
        CONVERT(bit, 1) AS [SupportsTechBenchV1Import], CONVERT(bit, 1) AS [SupportsServerSageSync],
        CONVERT(bit, 1) AS [SupportsAdminUserPreview], CONVERT(bit, 1) AS [SupportsFireDrillCredentials];
END;
GO

/* The server-only workbook location is an Admin setting. Ordinary clients and
   read-only Admin preview sessions must not receive it through GetSettings. */
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
    DECLARE @CanReadServerPaths bit = CONVERT(bit, CASE
        WHEN @IsAdmin = 1 AND @IsReadOnlyPreview = 0 THEN 1 ELSE 0 END);

    ;WITH settings AS
    (
        SELECT CONVERT(nvarchar(20), N'Organization') AS [ScopeType],
            [SettingKey], [SettingValue], [UpdatedAtUtc], [RowVersion],
            CONVERT(int, 1) AS [ScopePriority]
        FROM [tb_data].[OrganizationSettings]

        UNION ALL

        SELECT CONVERT(nvarchar(20), N'User') AS [ScopeType],
            [SettingKey], [SettingValue], [UpdatedAtUtc], [RowVersion],
            CONVERT(int, 2) AS [ScopePriority]
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
      AND ([SettingKey] <> N'FireDrill.SourcePath' OR @CanReadServerPaths = 1)
    ORDER BY [SettingKey];
END;
GO

CREATE OR ALTER PROCEDURE [tb_app].[SearchFireDrillCredentials]
    @Search nvarchar(240) = NULL,
    @Limit int = 250
AS
BEGIN
    SET NOCOUNT ON;
    IF USER_NAME() = N'tb_preview_reader'
        THROW 52000, N'Credentials are unavailable in Admin user-preview mode.', 1;

    DECLARE @Sid varbinary(85), @Login nvarchar(256), @Display nvarchar(160),
            @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser] @Sid OUTPUT, @Login OUTPUT, @Display OUTPUT,
        @Tech OUTPUT, @Manager OUTPUT, @Admin OUTPUT, @Sync OUTPUT;

    SET @Search = NULLIF(LTRIM(RTRIM(@Search)), N'');
    SET @Limit = CASE WHEN @Limit IS NULL OR @Limit < 1 THEN 250 WHEN @Limit > 1000 THEN 1000 ELSE @Limit END;

    SELECT TOP (@Limit)
        [CredentialId], [ClientName], [FireboxIp], [Status], [LastSyncedAtUtc]
    FROM [tb_data].[FireDrillCredentials]
    WHERE [IsCurrent] = 1
      AND (@Search IS NULL OR [ClientName] LIKE N'%' + @Search + N'%'
           OR [FireboxIp] LIKE N'%' + @Search + N'%'
           OR [Status] LIKE N'%' + @Search + N'%')
    ORDER BY [ClientName], [CredentialId];
END;
GO

CREATE OR ALTER PROCEDURE [tb_app].[RevealFireDrillCredential]
    @CredentialId bigint
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF SESSION_CONTEXT(N'TechBench.PreviewSessionId') IS NOT NULL
        THROW 52001, N'Credentials are unavailable in Admin user-preview mode.', 1;

    IF NOT EXISTS (SELECT 1 FROM [tb_data].[FireDrillCredentials] WHERE [CredentialId] = @CredentialId AND [IsCurrent] = 1)
        THROW 52002, N'The credential was not found or is no longer current.', 1;

    SELECT [CredentialId], [ClientName], [FireboxIp], [Status], [LastSyncedAtUtc],
        CONVERT(nvarchar(max), DecryptByKeyAutoCert(CERT_ID(N'tb_FireDrillCredentialCertificate'),NULL,[AdminEncrypted],1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',[ClientKey]),2))) AS [Admin],
        CONVERT(nvarchar(max), DecryptByKeyAutoCert(CERT_ID(N'tb_FireDrillCredentialCertificate'),NULL,[CsriAdminEncrypted],1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',[ClientKey]),2))) AS [CsriAdmin],
        CONVERT(nvarchar(max), DecryptByKeyAutoCert(CERT_ID(N'tb_FireDrillCredentialCertificate'),NULL,[FireboxDbCsriEncrypted],1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',[ClientKey]),2))) AS [FireboxDbCsri],
        CONVERT(nvarchar(max), DecryptByKeyAutoCert(CERT_ID(N'tb_FireDrillCredentialCertificate'),NULL,[AuthpointUserEncrypted],1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',[ClientKey]),2))) AS [AuthpointUser],
        CONVERT(nvarchar(max), DecryptByKeyAutoCert(CERT_ID(N'tb_FireDrillCredentialCertificate'),NULL,[SslVpnPasswordEncrypted],1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',[ClientKey]),2))) AS [SslVpnPassword],
        CONVERT(nvarchar(max), DecryptByKeyAutoCert(CERT_ID(N'tb_FireDrillCredentialCertificate'),NULL,[AdAuthUserEncrypted],1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',[ClientKey]),2))) AS [AdAuthUser],
        CONVERT(nvarchar(max), DecryptByKeyAutoCert(CERT_ID(N'tb_FireDrillCredentialCertificate'),NULL,[AdPasswordEncrypted],1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',[ClientKey]),2))) AS [AdPassword],
        CONVERT(nvarchar(max), DecryptByKeyAutoCert(CERT_ID(N'tb_FireDrillCredentialCertificate'),NULL,[RustPasswordEncrypted],1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',[ClientKey]),2))) AS [RustPassword]
    FROM [tb_data].[FireDrillCredentials]
    WHERE [CredentialId] = @CredentialId AND [IsCurrent] = 1;

END;
GO

IF OBJECT_ID(N'tb_app.AuditFireDrillCredentialCopy', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AuditFireDrillCredentialCopy];
GO

CREATE OR ALTER PROCEDURE [tb_app].[AdminRequestFireDrillSync]
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Sid varbinary(85), @Login nvarchar(256), @Display nvarchar(160),
            @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser] @Sid OUTPUT, @Login OUTPUT, @Display OUTPUT,
        @Tech OUTPUT, @Manager OUTPUT, @Admin OUTPUT, @Sync OUTPUT;
    IF @Admin = 0 THROW 52010, N'Only a TechBench Admin can request Credentials synchronization.', 1;
    IF NOT EXISTS
    (
        SELECT 1 FROM [tb_data].[OrganizationSettings]
        WHERE [SettingKey] = N'FireDrill.SourcePath'
          AND NULLIF(LTRIM(RTRIM([SettingValue])), N'') IS NOT NULL
    )
        THROW 52011, N'Configure the Credentials workbook path in Server Manager before requesting synchronization.', 1;
    SET @RequestId = COALESCE(@RequestId, NEWID());

    BEGIN TRANSACTION;
    IF EXISTS (SELECT 1 FROM [tb_sync].[FireDrillSyncRequests] WITH (UPDLOCK, HOLDLOCK) WHERE [Status] IN (N'Queued', N'Running'))
    BEGIN
        SELECT TOP (1) [RequestId], N'AlreadyQueued' AS [Status]
        FROM [tb_sync].[FireDrillSyncRequests]
        WHERE [Status] IN (N'Queued', N'Running') ORDER BY [RequestedAtUtc];
        COMMIT TRANSACTION;
        RETURN;
    END;
    INSERT INTO [tb_sync].[FireDrillSyncRequests]([RequestId], [RequestedByWindowsSid], [RequestType])
        VALUES (@RequestId, @Sid, N'Manual');
    COMMIT TRANSACTION;
    SELECT @RequestId AS [RequestId], N'Queued' AS [Status];
END;
GO

CREATE OR ALTER PROCEDURE [tb_app].[GetFireDrillSyncStatus]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (1) [RequestId], [Status], [Message], [RequestedAtUtc], [CompletedAtUtc],
        [ReadCount], [SavedCount], [StaleCount],
        (SELECT COUNT(*) FROM [tb_sync].[FireDrillSyncRequests] WHERE [Status] IN (N'Queued', N'Running')) AS [QueueDepth]
    FROM [tb_sync].[FireDrillSyncRequests]
    ORDER BY [RequestedAtUtc] DESC, [RequestId] DESC;

    SELECT [LastAttemptAtUtc], [LastSuccessfulAtUtc], [LastSourceModifiedAtUtc], [LastError]
    FROM [tb_sync].[FireDrillSyncHealth] WHERE [HealthId] = 1;
END;
GO

CREATE OR ALTER PROCEDURE [tb_service].[GetFireDrillSyncConfiguration]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        COALESCE((SELECT [SettingValue] FROM [tb_data].[OrganizationSettings] WHERE [SettingKey]=N'FireDrill.SourcePath'), N'') AS [SourcePath],
        COALESCE(TRY_CONVERT(bit, (SELECT [SettingValue] FROM [tb_data].[OrganizationSettings] WHERE [SettingKey]=N'FireDrill.DailySyncEnabled')), 1) AS [DailySyncEnabled],
        COALESCE((SELECT [SettingValue] FROM [tb_data].[OrganizationSettings] WHERE [SettingKey]=N'FireDrill.DailySyncTime'), N'04:00') AS [DailySyncTime];
END;
GO

CREATE OR ALTER PROCEDURE [tb_service].[ClaimFireDrillSyncWork]
    @WorkerId uniqueidentifier,
    @LeaseSeconds int = 300
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @LeaseSeconds < 120 OR @LeaseSeconds > 3600 THROW 52020, N'Lease seconds must be between 120 and 3600.', 1;

    /* The Windows service is intentionally isolated in tb_role_sync_service
       and is not a member of the older workstation sync-operator role. */
    IF IS_ROLEMEMBER(N'tb_role_sync_service') <> 1
        THROW 52021, N'The current identity is not the TechBench sync service.', 1;

    DECLARE @Sid varbinary(85) = SUSER_SID(ORIGINAL_LOGIN());
    IF @Sid IS NULL
       OR NOT EXISTS
       (
           SELECT 1
           FROM [tb_security].[Users]
           WHERE [WindowsSid] = @Sid
             AND [LoginName] = CONVERT(nvarchar(256), ORIGINAL_LOGIN())
       )
        THROW 52024, N'The TechBench sync service actor is not registered.', 1;

    DECLARE @NowUtc datetime2(3)=SYSUTCDATETIME(), @NowLocal datetime=GETDATE(),
            @SourcePath nvarchar(2000)=NULLIF(LTRIM(RTRIM((SELECT [SettingValue] FROM [tb_data].[OrganizationSettings] WHERE [SettingKey]=N'FireDrill.SourcePath'))),N''),
            @Enabled bit=COALESCE(TRY_CONVERT(bit,(SELECT [SettingValue] FROM [tb_data].[OrganizationSettings] WHERE [SettingKey]=N'FireDrill.DailySyncEnabled')),1),
            @At time(0)=COALESCE(TRY_CONVERT(time(0),(SELECT [SettingValue] FROM [tb_data].[OrganizationSettings] WHERE [SettingKey]=N'FireDrill.DailySyncTime')),CONVERT(time(0),N'04:00')),
            @RequestId uniqueidentifier, @LeaseId uniqueidentifier=NEWID();

    BEGIN TRANSACTION;
    DELETE FROM [tb_sync].[FireDrillSyncLeases] WHERE [ExpiresAtUtc] <= @NowUtc;
    UPDATE request_row SET [Status]=N'Queued', [StartedAtUtc]=NULL,
        [Message]=N'Previous service lease expired; queued for retry.'
    FROM [tb_sync].[FireDrillSyncRequests] request_row
    WHERE request_row.[Status]=N'Running'
      AND NOT EXISTS (SELECT 1 FROM [tb_sync].[FireDrillSyncLeases] lease_row WHERE lease_row.[RequestId]=request_row.[RequestId]);

    IF @Enabled=1 AND @SourcePath IS NOT NULL AND CONVERT(time(0),@NowLocal)>=@At
       AND NOT EXISTS
       (
           SELECT 1 FROM [tb_sync].[FireDrillSyncRequests]
           WHERE [RequestType]=N'Automatic' AND CONVERT(date,[RequestedAtUtc])=CONVERT(date,@NowUtc)
             AND ([Status]<>N'Failed' OR [RequestedAtUtc]>DATEADD(minute,-30,@NowUtc))
       )
    BEGIN
        DECLARE @AutomaticRequestId uniqueidentifier=NEWID();
        INSERT INTO [tb_sync].[FireDrillSyncRequests]([RequestId],[RequestedByWindowsSid],[RequestType])
            VALUES(@AutomaticRequestId,@Sid,N'Automatic');
    END;

    SELECT TOP (1) @RequestId=[RequestId]
    FROM [tb_sync].[FireDrillSyncRequests] WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE [Status]=N'Queued' ORDER BY [RequestedAtUtc], [RequestId];

    IF @RequestId IS NOT NULL
    BEGIN
        UPDATE [tb_sync].[FireDrillSyncRequests]
        SET [Status]=N'Running', [StartedAtUtc]=@NowUtc, [CompletedAtUtc]=NULL,
            [AttemptCount]=[AttemptCount]+1, [Message]=N'Workbook synchronization is running.'
        WHERE [RequestId]=@RequestId;
        INSERT INTO [tb_sync].[FireDrillSyncLeases]([RequestId],[LeaseId],[WorkerId],[ExpiresAtUtc])
            VALUES(@RequestId,@LeaseId,@WorkerId,DATEADD(second,@LeaseSeconds,@NowUtc));
    END;
    COMMIT TRANSACTION;

    IF @RequestId IS NOT NULL
        SELECT @RequestId AS [WorkId], @LeaseId AS [LeaseId], DATEADD(second,@LeaseSeconds,@NowUtc) AS [LeaseExpiresUtc];
END;
GO

CREATE OR ALTER PROCEDURE [tb_service].[RenewFireDrillSyncLease]
    @RequestId uniqueidentifier, @LeaseId uniqueidentifier, @WorkerId uniqueidentifier, @LeaseSeconds int=300
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE [tb_sync].[FireDrillSyncLeases]
    SET [ExpiresAtUtc]=DATEADD(second,@LeaseSeconds,SYSUTCDATETIME())
    WHERE [RequestId]=@RequestId AND [LeaseId]=@LeaseId AND [WorkerId]=@WorkerId AND [ExpiresAtUtc]>SYSUTCDATETIME();
    IF @@ROWCOUNT<>1 THROW 52022, N'The Credentials synchronization lease is no longer valid.', 1;
END;
GO

CREATE OR ALTER PROCEDURE [tb_service].[ApplyFireDrillCredentialSnapshot]
    @RequestId uniqueidentifier,
    @LeaseId uniqueidentifier,
    @WorkerId uniqueidentifier,
    @RowsJson nvarchar(max),
    @SourceModifiedAtUtc datetime2(3),
    @SyncedAtUtc datetime2(3)
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF ISJSON(@RowsJson)<>1 THROW 52030, N'The Credentials snapshot is not valid JSON.', 1;

    CREATE TABLE #Rows
    (
        [ClientKey] nvarchar(200) NOT NULL PRIMARY KEY,
        [ClientName] nvarchar(240) NOT NULL,
        [FireboxIp] nvarchar(120) NULL, [Status] nvarchar(120) NULL,
        [Admin] nvarchar(3000) NULL, [CsriAdmin] nvarchar(3000) NULL,
        [FireboxDbCsri] nvarchar(3000) NULL, [AuthpointUser] nvarchar(3000) NULL,
        [SslVpnPassword] nvarchar(3000) NULL, [AdAuthUser] nvarchar(3000) NULL,
        [AdPassword] nvarchar(3000) NULL, [RustPassword] nvarchar(3000) NULL,
        [RowHash] binary(32) NULL
    );

    INSERT INTO #Rows([ClientKey],[ClientName],[FireboxIp],[Status],[Admin],[CsriAdmin],[FireboxDbCsri],[AuthpointUser],[SslVpnPassword],[AdAuthUser],[AdPassword],[RustPassword])
    SELECT LOWER(LTRIM(RTRIM([ClientName]))), LTRIM(RTRIM([ClientName])), NULLIF(LTRIM(RTRIM([FireboxIp])),N''), NULLIF(LTRIM(RTRIM([Status])),N''),
        [Admin],[CsriAdmin],[FireboxDbCsri],[AuthpointUser],[SslVpnPassword],[AdAuthUser],[AdPassword],[RustPassword]
    FROM OPENJSON(@RowsJson)
    WITH
    (
        [ClientName] nvarchar(240) N'$.clientName', [FireboxIp] nvarchar(120) N'$.fireboxIp', [Status] nvarchar(120) N'$.status',
        [Admin] nvarchar(3000) N'$.admin', [CsriAdmin] nvarchar(3000) N'$.csriAdmin', [FireboxDbCsri] nvarchar(3000) N'$.fireboxDbCsri',
        [AuthpointUser] nvarchar(3000) N'$.authpointUser', [SslVpnPassword] nvarchar(3000) N'$.sslVpnPassword',
        [AdAuthUser] nvarchar(3000) N'$.adAuthUser', [AdPassword] nvarchar(3000) N'$.adPassword', [RustPassword] nvarchar(3000) N'$.rustPassword'
    );

    IF NOT EXISTS(SELECT 1 FROM #Rows) THROW 52031, N'The Credentials snapshot contained no client rows; existing data was not changed.', 1;
    IF EXISTS(SELECT 1 FROM #Rows WHERE LEN([ClientKey])=0) THROW 52032, N'A Credentials row has no client name.', 1;

    UPDATE #Rows SET [RowHash]=HASHBYTES('SHA2_256',
        HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL([ClientName],N'<NULL>'))) +
        HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL([FireboxIp],N'<NULL>'))) +
        HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL([Status],N'<NULL>'))) +
        HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL([Admin],N'<NULL>'))) +
        HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL([CsriAdmin],N'<NULL>'))) +
        HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL([FireboxDbCsri],N'<NULL>'))) +
        HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL([AuthpointUser],N'<NULL>'))) +
        HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL([SslVpnPassword],N'<NULL>'))) +
        HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL([AdAuthUser],N'<NULL>'))) +
        HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL([AdPassword],N'<NULL>'))) +
        HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL([RustPassword],N'<NULL>'))));

    DECLARE @ReadCount int=(SELECT COUNT(*) FROM #Rows), @SavedCount int=0, @StaleCount int=0;
    BEGIN TRY
    BEGIN TRANSACTION;
    IF NOT EXISTS
    (
        SELECT 1 FROM [tb_sync].[FireDrillSyncLeases]
        WHERE [RequestId]=@RequestId AND [LeaseId]=@LeaseId AND [WorkerId]=@WorkerId AND [ExpiresAtUtc]>SYSUTCDATETIME()
    ) THROW 52033, N'The Credentials synchronization lease is no longer valid.', 1;

    OPEN SYMMETRIC KEY [tb_FireDrillCredentialKey] DECRYPTION BY CERTIFICATE [tb_FireDrillCredentialCertificate];

    UPDATE target SET [ClientName]=source_row.[ClientName], [FireboxIp]=source_row.[FireboxIp], [Status]=source_row.[Status],
        [AdminEncrypted]=CASE WHEN source_row.[Admin] IS NULL THEN NULL ELSE EncryptByKey(Key_GUID(N'tb_FireDrillCredentialKey'),CONVERT(varbinary(max),source_row.[Admin]),1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',source_row.[ClientKey]),2)) END,
        [CsriAdminEncrypted]=CASE WHEN source_row.[CsriAdmin] IS NULL THEN NULL ELSE EncryptByKey(Key_GUID(N'tb_FireDrillCredentialKey'),CONVERT(varbinary(max),source_row.[CsriAdmin]),1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',source_row.[ClientKey]),2)) END,
        [FireboxDbCsriEncrypted]=CASE WHEN source_row.[FireboxDbCsri] IS NULL THEN NULL ELSE EncryptByKey(Key_GUID(N'tb_FireDrillCredentialKey'),CONVERT(varbinary(max),source_row.[FireboxDbCsri]),1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',source_row.[ClientKey]),2)) END,
        [AuthpointUserEncrypted]=CASE WHEN source_row.[AuthpointUser] IS NULL THEN NULL ELSE EncryptByKey(Key_GUID(N'tb_FireDrillCredentialKey'),CONVERT(varbinary(max),source_row.[AuthpointUser]),1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',source_row.[ClientKey]),2)) END,
        [SslVpnPasswordEncrypted]=CASE WHEN source_row.[SslVpnPassword] IS NULL THEN NULL ELSE EncryptByKey(Key_GUID(N'tb_FireDrillCredentialKey'),CONVERT(varbinary(max),source_row.[SslVpnPassword]),1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',source_row.[ClientKey]),2)) END,
        [AdAuthUserEncrypted]=CASE WHEN source_row.[AdAuthUser] IS NULL THEN NULL ELSE EncryptByKey(Key_GUID(N'tb_FireDrillCredentialKey'),CONVERT(varbinary(max),source_row.[AdAuthUser]),1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',source_row.[ClientKey]),2)) END,
        [AdPasswordEncrypted]=CASE WHEN source_row.[AdPassword] IS NULL THEN NULL ELSE EncryptByKey(Key_GUID(N'tb_FireDrillCredentialKey'),CONVERT(varbinary(max),source_row.[AdPassword]),1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',source_row.[ClientKey]),2)) END,
        [RustPasswordEncrypted]=CASE WHEN source_row.[RustPassword] IS NULL THEN NULL ELSE EncryptByKey(Key_GUID(N'tb_FireDrillCredentialKey'),CONVERT(varbinary(max),source_row.[RustPassword]),1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',source_row.[ClientKey]),2)) END,
        [SourceRowHash]=source_row.[RowHash], [SourceModifiedAtUtc]=@SourceModifiedAtUtc, [LastSyncedAtUtc]=@SyncedAtUtc, [IsCurrent]=1
    FROM [tb_data].[FireDrillCredentials] target INNER JOIN #Rows source_row ON source_row.[ClientKey]=target.[ClientKey]
    WHERE target.[SourceRowHash]<>source_row.[RowHash] OR target.[IsCurrent]=0;
    SET @SavedCount=@@ROWCOUNT;

    INSERT INTO [tb_data].[FireDrillCredentials]
        ([ClientKey],[ClientName],[FireboxIp],[Status],[AdminEncrypted],[CsriAdminEncrypted],[FireboxDbCsriEncrypted],[AuthpointUserEncrypted],[SslVpnPasswordEncrypted],[AdAuthUserEncrypted],[AdPasswordEncrypted],[RustPasswordEncrypted],[SourceRowHash],[SourceModifiedAtUtc],[LastSyncedAtUtc],[IsCurrent])
    SELECT source_row.[ClientKey],source_row.[ClientName],source_row.[FireboxIp],source_row.[Status],
        CASE WHEN source_row.[Admin] IS NULL THEN NULL ELSE EncryptByKey(Key_GUID(N'tb_FireDrillCredentialKey'),CONVERT(varbinary(max),source_row.[Admin]),1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',source_row.[ClientKey]),2)) END,
        CASE WHEN source_row.[CsriAdmin] IS NULL THEN NULL ELSE EncryptByKey(Key_GUID(N'tb_FireDrillCredentialKey'),CONVERT(varbinary(max),source_row.[CsriAdmin]),1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',source_row.[ClientKey]),2)) END,
        CASE WHEN source_row.[FireboxDbCsri] IS NULL THEN NULL ELSE EncryptByKey(Key_GUID(N'tb_FireDrillCredentialKey'),CONVERT(varbinary(max),source_row.[FireboxDbCsri]),1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',source_row.[ClientKey]),2)) END,
        CASE WHEN source_row.[AuthpointUser] IS NULL THEN NULL ELSE EncryptByKey(Key_GUID(N'tb_FireDrillCredentialKey'),CONVERT(varbinary(max),source_row.[AuthpointUser]),1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',source_row.[ClientKey]),2)) END,
        CASE WHEN source_row.[SslVpnPassword] IS NULL THEN NULL ELSE EncryptByKey(Key_GUID(N'tb_FireDrillCredentialKey'),CONVERT(varbinary(max),source_row.[SslVpnPassword]),1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',source_row.[ClientKey]),2)) END,
        CASE WHEN source_row.[AdAuthUser] IS NULL THEN NULL ELSE EncryptByKey(Key_GUID(N'tb_FireDrillCredentialKey'),CONVERT(varbinary(max),source_row.[AdAuthUser]),1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',source_row.[ClientKey]),2)) END,
        CASE WHEN source_row.[AdPassword] IS NULL THEN NULL ELSE EncryptByKey(Key_GUID(N'tb_FireDrillCredentialKey'),CONVERT(varbinary(max),source_row.[AdPassword]),1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',source_row.[ClientKey]),2)) END,
        CASE WHEN source_row.[RustPassword] IS NULL THEN NULL ELSE EncryptByKey(Key_GUID(N'tb_FireDrillCredentialKey'),CONVERT(varbinary(max),source_row.[RustPassword]),1,CONVERT(nvarchar(64),HASHBYTES('SHA2_256',source_row.[ClientKey]),2)) END,
        source_row.[RowHash],@SourceModifiedAtUtc,@SyncedAtUtc,1
    FROM #Rows source_row
    WHERE NOT EXISTS(SELECT 1 FROM [tb_data].[FireDrillCredentials] target WHERE target.[ClientKey]=source_row.[ClientKey]);
    SET @SavedCount+=@@ROWCOUNT;

    UPDATE target SET [IsCurrent]=0,[LastSyncedAtUtc]=@SyncedAtUtc
    FROM [tb_data].[FireDrillCredentials] target
    WHERE target.[IsCurrent]=1 AND NOT EXISTS(SELECT 1 FROM #Rows source_row WHERE source_row.[ClientKey]=target.[ClientKey]);
    SET @StaleCount=@@ROWCOUNT;
    CLOSE SYMMETRIC KEY [tb_FireDrillCredentialKey];

    UPDATE [tb_sync].[FireDrillSyncRequests]
    SET [ReadCount]=@ReadCount,[SavedCount]=@SavedCount,[StaleCount]=@StaleCount
    WHERE [RequestId]=@RequestId;
    COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF EXISTS (SELECT 1 FROM sys.openkeys WHERE [key_name]=N'tb_FireDrillCredentialKey')
            CLOSE SYMMETRIC KEY [tb_FireDrillCredentialKey];
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
    SELECT @ReadCount AS [ReadCount],@SavedCount AS [SavedCount],@StaleCount AS [StaleCount];
END;
GO

CREATE OR ALTER PROCEDURE [tb_service].[CompleteFireDrillSyncWork]
    @RequestId uniqueidentifier, @LeaseId uniqueidentifier, @WorkerId uniqueidentifier,
    @Succeeded bit, @Message nvarchar(2000), @SourceModifiedAtUtc datetime2(3)=NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRY
    BEGIN TRANSACTION;
    IF NOT EXISTS(SELECT 1 FROM [tb_sync].[FireDrillSyncLeases] WHERE [RequestId]=@RequestId AND [LeaseId]=@LeaseId AND [WorkerId]=@WorkerId)
        THROW 52040, N'The Credentials synchronization lease is no longer valid.', 1;
    UPDATE [tb_sync].[FireDrillSyncRequests]
    SET [Status]=CASE WHEN @Succeeded=1 THEN N'Completed' ELSE N'Failed' END,
        [CompletedAtUtc]=SYSUTCDATETIME(), [Message]=LEFT(@Message,2000)
    WHERE [RequestId]=@RequestId;
    DELETE FROM [tb_sync].[FireDrillSyncLeases] WHERE [RequestId]=@RequestId;
    UPDATE [tb_sync].[FireDrillSyncHealth]
    SET [LastAttemptAtUtc]=SYSUTCDATETIME(),
        [LastSuccessfulAtUtc]=CASE WHEN @Succeeded=1 THEN SYSUTCDATETIME() ELSE [LastSuccessfulAtUtc] END,
        [LastSourceModifiedAtUtc]=CASE WHEN @Succeeded=1 THEN @SourceModifiedAtUtc ELSE [LastSourceModifiedAtUtc] END,
        [LastError]=CASE WHEN @Succeeded=1 THEN NULL ELSE LEFT(@Message,2000) END,
        [UpdatedAtUtc]=SYSUTCDATETIME()
    WHERE [HealthId]=1;
    COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

PRINT N'TechBench V0008 Credentials procedures created.';
GO
