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
    SELECT CONVERT(int, 13) AS [SchemaVersion], CONVERT(bit, 0) AS [FullTextSearchAvailable],
        CONVERT(bit, 1) AS [SupportsTickets], CONVERT(bit, 1) AS [SupportsWorkEntries],
        CONVERT(bit, 1) AS [SupportsPrivateNotes], CONVERT(bit, 1) AS [SupportsPostingLeases],
        CONVERT(bit, 1) AS [SupportsSyncLeases], CONVERT(bit, 1) AS [SupportsImports],
        CONVERT(bit, 1) AS [SupportsTechBenchV1Import], CONVERT(bit, 1) AS [SupportsServerSageSync],
        CONVERT(bit, 1) AS [SupportsAdminUserPreview], CONVERT(bit, 1) AS [SupportsFireDrillCredentials];
END;
GO

ALTER PROCEDURE [tb_app].[SearchFireDrillCredentials]
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
        credential.[CredentialId], credential.[ClientName], credential.[FireboxIp],
        credential.[Status], credential.[LastSyncedAtUtc],
        COALESCE
        (
            (
                SELECT field.[FieldLabel] AS [label],
                    field.[FieldKey] AS [fieldName],
                    field.[SortOrder] AS [sortOrder],
                    CONVERT(nvarchar(1), N'') AS [value]
                FROM [tb_data].[FireDrillCredentialFields] field
                WHERE field.[CredentialId] = credential.[CredentialId]
                ORDER BY field.[SortOrder], field.[FieldKey]
                FOR JSON PATH
            ),
            N'[]'
        ) AS [FieldsJson]
    FROM [tb_data].[FireDrillCredentials] credential
    WHERE credential.[IsCurrent] = 1
      AND (@Search IS NULL OR credential.[ClientName] LIKE N'%' + @Search + N'%'
           OR credential.[FireboxIp] LIKE N'%' + @Search + N'%'
           OR credential.[Status] LIKE N'%' + @Search + N'%')
    ORDER BY credential.[ClientName], credential.[CredentialId];
END;
GO

ALTER PROCEDURE [tb_app].[RevealFireDrillCredential]
    @CredentialId bigint
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF SESSION_CONTEXT(N'TechBench.PreviewSessionId') IS NOT NULL
        THROW 52001, N'Credentials are unavailable in Admin user-preview mode.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_data].[FireDrillCredentials]
        WHERE [CredentialId] = @CredentialId AND [IsCurrent] = 1
    )
        THROW 52002, N'The credential was not found or is no longer current.', 1;

    SELECT credential.[CredentialId], credential.[ClientName], credential.[FireboxIp],
        credential.[Status], credential.[LastSyncedAtUtc],
        COALESCE
        (
            (
                SELECT field.[FieldLabel] AS [label],
                    field.[FieldKey] AS [fieldName],
                    field.[SortOrder] AS [sortOrder],
                    COALESCE
                    (
                        CONVERT
                        (
                            nvarchar(max),
                            DecryptByKeyAutoCert
                            (
                                CERT_ID(N'tb_FireDrillCredentialCertificate'),
                                NULL,
                                field.[ValueEncrypted],
                                1,
                                CONVERT
                                (
                                    nvarchar(64),
                                    HASHBYTES
                                    (
                                        'SHA2_256',
                                        CONVERT
                                        (
                                            varbinary(max),
                                            credential.[ClientKey] + N'|' + field.[FieldKey]
                                        )
                                    ),
                                    2
                                )
                            )
                        ),
                        N''
                    ) AS [value]
                FROM [tb_data].[FireDrillCredentialFields] field
                WHERE field.[CredentialId] = credential.[CredentialId]
                ORDER BY field.[SortOrder], field.[FieldKey]
                FOR JSON PATH
            ),
            N'[]'
        ) AS [FieldsJson]
    FROM [tb_data].[FireDrillCredentials] credential
    WHERE credential.[CredentialId] = @CredentialId
      AND credential.[IsCurrent] = 1;
END;
GO

ALTER PROCEDURE [tb_service].[ApplyFireDrillCredentialSnapshot]
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
        [FireboxIp] nvarchar(120) NULL,
        [Status] nvarchar(120) NULL,
        [RowHash] binary(32) NULL,
        [FieldsJson] nvarchar(max) NULL
    );

    INSERT INTO #Rows
        ([ClientKey], [ClientName], [FireboxIp], [Status], [RowHash], [FieldsJson])
    SELECT LOWER(LTRIM(RTRIM(source_row.[ClientName]))),
        LTRIM(RTRIM(source_row.[ClientName])),
        NULLIF(LTRIM(RTRIM(source_row.[FireboxIp])), N''),
        NULLIF(LTRIM(RTRIM(source_row.[Status])), N''),
        TRY_CONVERT(binary(32), source_row.[RowHashHex], 2),
        source_row.[FieldsJson]
    FROM OPENJSON(@RowsJson)
    WITH
    (
        [ClientName] nvarchar(240) N'$.clientName',
        [FireboxIp] nvarchar(120) N'$.fireboxIp',
        [Status] nvarchar(120) N'$.status',
        [RowHashHex] nvarchar(64) N'$.rowHashHex',
        [FieldsJson] nvarchar(max) N'$.fields' AS JSON
    ) source_row;

    IF NOT EXISTS(SELECT 1 FROM #Rows)
        THROW 52031, N'The Credentials snapshot contained no client rows; existing data was not changed.', 1;
    IF EXISTS(SELECT 1 FROM #Rows WHERE LEN([ClientKey])=0)
        THROW 52032, N'A Credentials row has no client name.', 1;
    IF EXISTS(SELECT 1 FROM #Rows WHERE [RowHash] IS NULL OR ISJSON([FieldsJson])<>1)
        THROW 52034, N'A Credentials row has invalid flexible field data.', 1;

    CREATE TABLE #Fields
    (
        [ClientKey] nvarchar(200) NOT NULL,
        [FieldKey] nvarchar(200) NOT NULL,
        [FieldLabel] nvarchar(200) NOT NULL,
        [SortOrder] int NOT NULL,
        [FieldValue] nvarchar(3000) NULL,
        CONSTRAINT [PK_FlexibleCredentialFields] PRIMARY KEY ([ClientKey], [FieldKey]),
        CONSTRAINT [UQ_FlexibleCredentialFieldOrder] UNIQUE ([ClientKey], [SortOrder])
    );

    INSERT INTO #Fields
        ([ClientKey], [FieldKey], [FieldLabel], [SortOrder], [FieldValue])
    SELECT row_data.[ClientKey],
        LTRIM(RTRIM(field_data.[FieldKey])),
        LTRIM(RTRIM(field_data.[FieldLabel])),
        field_data.[SortOrder],
        field_data.[FieldValue]
    FROM #Rows row_data
    CROSS APPLY OPENJSON(row_data.[FieldsJson])
    WITH
    (
        [FieldKey] nvarchar(200) N'$.fieldKey',
        [FieldLabel] nvarchar(200) N'$.label',
        [SortOrder] int N'$.sortOrder',
        [FieldValue] nvarchar(3000) N'$.value'
    ) field_data;

    IF EXISTS
    (
        SELECT 1
        FROM #Fields
        WHERE LEN([FieldKey])=0 OR LEN([FieldLabel])=0 OR [SortOrder] < 1
    )
        THROW 52035, N'A Credentials field has an invalid header or order.', 1;
    IF EXISTS
    (
        SELECT 1
        FROM #Rows row_data
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM #Fields field_data
            WHERE field_data.[ClientKey] = row_data.[ClientKey]
        )
    )
        THROW 52036, N'A Credentials row has no flexible fields.', 1;

    CREATE TABLE #Changed
    (
        [ClientKey] nvarchar(200) NOT NULL PRIMARY KEY
    );
    INSERT INTO #Changed([ClientKey])
    SELECT source_row.[ClientKey]
    FROM #Rows source_row
    LEFT JOIN [tb_data].[FireDrillCredentials] target
        ON target.[ClientKey] = source_row.[ClientKey]
    WHERE target.[CredentialId] IS NULL
       OR target.[SourceRowHash] <> source_row.[RowHash]
       OR target.[IsCurrent] = 0;

    DECLARE @ReadCount int=(SELECT COUNT(*) FROM #Rows),
            @SavedCount int=(SELECT COUNT(*) FROM #Changed),
            @StaleCount int=0;

    BEGIN TRY
    BEGIN TRANSACTION;
    IF NOT EXISTS
    (
        SELECT 1 FROM [tb_sync].[FireDrillSyncLeases]
        WHERE [RequestId]=@RequestId AND [LeaseId]=@LeaseId
          AND [WorkerId]=@WorkerId AND [ExpiresAtUtc]>SYSUTCDATETIME()
    )
        THROW 52033, N'The Credentials synchronization lease is no longer valid.', 1;

    OPEN SYMMETRIC KEY [tb_FireDrillCredentialKey]
        DECRYPTION BY CERTIFICATE [tb_FireDrillCredentialCertificate];

    UPDATE target
    SET [ClientName]=source_row.[ClientName],
        [FireboxIp]=source_row.[FireboxIp],
        [Status]=source_row.[Status],
        [SourceRowHash]=source_row.[RowHash],
        [SourceModifiedAtUtc]=@SourceModifiedAtUtc,
        [LastSyncedAtUtc]=@SyncedAtUtc,
        [IsCurrent]=1
    FROM [tb_data].[FireDrillCredentials] target
    INNER JOIN #Rows source_row ON source_row.[ClientKey]=target.[ClientKey]
    INNER JOIN #Changed changed_row ON changed_row.[ClientKey]=source_row.[ClientKey];

    INSERT INTO [tb_data].[FireDrillCredentials]
        ([ClientKey], [ClientName], [FireboxIp], [Status], [SourceRowHash],
         [SourceModifiedAtUtc], [LastSyncedAtUtc], [IsCurrent])
    SELECT source_row.[ClientKey], source_row.[ClientName], source_row.[FireboxIp],
        source_row.[Status], source_row.[RowHash], @SourceModifiedAtUtc,
        @SyncedAtUtc, 1
    FROM #Rows source_row
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM [tb_data].[FireDrillCredentials] target
        WHERE target.[ClientKey]=source_row.[ClientKey]
    );

    DELETE stored_field
    FROM [tb_data].[FireDrillCredentialFields] stored_field
    INNER JOIN [tb_data].[FireDrillCredentials] credential
        ON credential.[CredentialId]=stored_field.[CredentialId]
    INNER JOIN #Changed changed_row
        ON changed_row.[ClientKey]=credential.[ClientKey];

    INSERT INTO [tb_data].[FireDrillCredentialFields]
        ([CredentialId], [FieldKey], [FieldLabel], [SortOrder], [ValueEncrypted])
    SELECT credential.[CredentialId], source_field.[FieldKey],
        source_field.[FieldLabel], source_field.[SortOrder],
        CASE WHEN source_field.[FieldValue] IS NULL THEN NULL ELSE
            EncryptByKey
            (
                Key_GUID(N'tb_FireDrillCredentialKey'),
                CONVERT(varbinary(max), source_field.[FieldValue]),
                1,
                CONVERT
                (
                    nvarchar(64),
                    HASHBYTES
                    (
                        'SHA2_256',
                        CONVERT
                        (
                            varbinary(max),
                            credential.[ClientKey] + N'|' + source_field.[FieldKey]
                        )
                    ),
                    2
                )
            )
        END
    FROM #Fields source_field
    INNER JOIN #Changed changed_row
        ON changed_row.[ClientKey]=source_field.[ClientKey]
    INNER JOIN [tb_data].[FireDrillCredentials] credential
        ON credential.[ClientKey]=source_field.[ClientKey];

    IF EXISTS
    (
        SELECT 1
        FROM #Fields source_field
        INNER JOIN #Changed changed_row
            ON changed_row.[ClientKey]=source_field.[ClientKey]
        INNER JOIN [tb_data].[FireDrillCredentials] credential
            ON credential.[ClientKey]=source_field.[ClientKey]
        INNER JOIN [tb_data].[FireDrillCredentialFields] stored_field
            ON stored_field.[CredentialId]=credential.[CredentialId]
           AND stored_field.[FieldKey]=source_field.[FieldKey]
        WHERE source_field.[FieldValue] IS NOT NULL
          AND stored_field.[ValueEncrypted] IS NULL
    )
        THROW 52037, N'A Credentials field could not be encrypted.', 1;

    UPDATE target
    SET [IsCurrent]=0, [LastSyncedAtUtc]=@SyncedAtUtc
    FROM [tb_data].[FireDrillCredentials] target
    WHERE target.[IsCurrent]=1
      AND NOT EXISTS
      (
          SELECT 1
          FROM #Rows source_row
          WHERE source_row.[ClientKey]=target.[ClientKey]
      );
    SET @StaleCount=@@ROWCOUNT;

    CLOSE SYMMETRIC KEY [tb_FireDrillCredentialKey];

    UPDATE [tb_sync].[FireDrillSyncRequests]
    SET [ReadCount]=@ReadCount, [SavedCount]=@SavedCount, [StaleCount]=@StaleCount
    WHERE [RequestId]=@RequestId;

    COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF EXISTS
        (
            SELECT 1 FROM sys.openkeys
            WHERE [key_name]=N'tb_FireDrillCredentialKey'
        )
            CLOSE SYMMETRIC KEY [tb_FireDrillCredentialKey];
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT @ReadCount AS [ReadCount],
        @SavedCount AS [SavedCount],
        @StaleCount AS [StaleCount];
END;
GO

PRINT N'TechBench V0012 flexible Credentials procedures created.';
GO
