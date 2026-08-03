:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'tb_app.GetClientAttachmentStorageConfiguration', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetClientAttachmentStorageConfiguration];
GO

CREATE PROCEDURE [tb_app].[GetClientAttachmentStorageConfiguration]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @IsManager bit,
            @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@UserSid OUTPUT,
        @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;

    DECLARE @RootPath nvarchar(max) =
        (SELECT [SettingValue] FROM [tb_data].[OrganizationSettings]
         WHERE [SettingKey]=N'ClientAttachments.RootPath');
    DECLARE @MaximumText nvarchar(max) =
        (SELECT [SettingValue] FROM [tb_data].[OrganizationSettings]
         WHERE [SettingKey]=N'ClientAttachments.MaximumFileSizeMegabytes');
    DECLARE @AllowedExtensions nvarchar(max) =
        (SELECT [SettingValue] FROM [tb_data].[OrganizationSettings]
         WHERE [SettingKey]=N'ClientAttachments.AllowedExtensions');
    DECLARE @Maximum int = TRY_CONVERT(int, @MaximumText);

    SELECT
        CONVERT(nvarchar(1000), COALESCE(@RootPath, N'')) AS [RootPath],
        CONVERT(int, CASE
            WHEN @Maximum BETWEEN 1 AND 2048 THEN @Maximum
            ELSE 50 END) AS [MaximumFileSizeMegabytes],
        CONVERT(nvarchar(max), COALESCE(
            NULLIF(LTRIM(RTRIM(@AllowedExtensions)), N''),
            N'.jpg,.jpeg,.png,.gif,.bmp,.webp,.tif,.tiff,.pdf,.doc,.docx,.xls,.xlsx,.csv,.txt,.rtf,.ppt,.pptx,.zip'))
            AS [AllowedExtensions];
END;
GO

IF OBJECT_ID(N'tb_app.GetClientInfoAttachments', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetClientInfoAttachments];
GO

CREATE PROCEDURE [tb_app].[GetClientInfoAttachments]
    @ClientId int,
    @IncludeArchived bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @IsManager bit,
            @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@UserSid OUTPUT,
        @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;

    IF NOT EXISTS (SELECT 1 FROM [tb_data].[Clients] WHERE [Id]=@ClientId)
        THROW 52600, N'The selected client no longer exists.', 1;

    SELECT
        attachment.[AttachmentId], attachment.[ClientId],
        attachment.[RelativePath], attachment.[OriginalFileName],
        attachment.[ContentType], attachment.[Category], attachment.[Caption],
        attachment.[FileSizeBytes], attachment.[ContentSha256],
        COALESCE(NULLIF(uploader.[DisplayName],N''), uploader.[LoginName], N'Unknown')
            AS [UploadedBy],
        attachment.[UploadedAtUtc], attachment.[IsArchived],
        COALESCE(NULLIF(archiver.[DisplayName],N''), archiver.[LoginName], N'')
            AS [ArchivedBy],
        attachment.[ArchivedAtUtc], attachment.[RowVersion]
    FROM [tb_client].[ClientAttachments] AS attachment
    LEFT JOIN [tb_security].[Users] AS uploader
        ON uploader.[WindowsSid]=attachment.[UploadedByWindowsSid]
    LEFT JOIN [tb_security].[Users] AS archiver
        ON archiver.[WindowsSid]=attachment.[ArchivedByWindowsSid]
    WHERE attachment.[ClientId]=@ClientId
      AND (@IncludeArchived=1 OR attachment.[IsArchived]=0)
    ORDER BY attachment.[IsArchived], attachment.[UploadedAtUtc] DESC,
        attachment.[OriginalFileName], attachment.[AttachmentId];
END;
GO

IF OBJECT_ID(N'tb_app.SaveClientInfoAttachment', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveClientInfoAttachment];
GO

CREATE PROCEDURE [tb_app].[SaveClientInfoAttachment]
    @AttachmentId uniqueidentifier,
    @ClientId int,
    @RelativePath nvarchar(400),
    @OriginalFileName nvarchar(260),
    @ContentType nvarchar(160),
    @Category nvarchar(80),
    @Caption nvarchar(500) = NULL,
    @FileSizeBytes bigint,
    @ContentSha256 binary(32),
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit,
            @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,
        @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1 AND IS_ROLEMEMBER(N'tb_role_client_info_editor')<>1
        THROW 52601, N'Client Info editor permission is required.', 1;

    SET @RelativePath=NULLIF(LTRIM(RTRIM(@RelativePath)),N'');
    SET @OriginalFileName=NULLIF(LTRIM(RTRIM(@OriginalFileName)),N'');
    SET @ContentType=COALESCE(NULLIF(LTRIM(RTRIM(@ContentType)),N''),
        N'application/octet-stream');
    SET @Category=COALESCE(NULLIF(LTRIM(RTRIM(@Category)),N''),N'Other');
    SET @Caption=NULLIF(LTRIM(RTRIM(@Caption)),N'');
    SET @RequestId=COALESCE(@RequestId,NEWID());

    IF @AttachmentId IS NULL OR @AttachmentId='00000000-0000-0000-0000-000000000000'
        THROW 52602, N'A generated attachment ID is required.', 1;
    IF NOT EXISTS (SELECT 1 FROM [tb_data].[Clients] WHERE [Id]=@ClientId)
        THROW 52600, N'The selected client no longer exists.', 1;
    IF @RelativePath IS NULL OR @RelativePath LIKE N'%..%'
       OR @RelativePath LIKE N':%' OR LEFT(@RelativePath,1) IN (N'\',N'/')
        THROW 52603, N'The attachment relative path is invalid.', 1;
    DECLARE @ClientIdText nvarchar(20)=CONVERT(nvarchar(20),@ClientId);
    DECLARE @ExpectedClientFolder nvarchar(40)=N'Client-'+CASE
        WHEN LEN(@ClientIdText)>=6 THEN @ClientIdText
        ELSE RIGHT(N'000000'+@ClientIdText,6) END+N'\';
    DECLARE @AttachmentExists bit=CONVERT(bit,CASE WHEN EXISTS
        (SELECT 1 FROM [tb_client].[ClientAttachments]
         WHERE [AttachmentId]=@AttachmentId) THEN 1 ELSE 0 END);
    IF @AttachmentExists=0
       AND @RelativePath NOT LIKE @ExpectedClientFolder + N'%'
        THROW 52604, N'The attachment path does not belong to the selected client folder.', 1;
    IF @OriginalFileName IS NULL OR CHARINDEX(N'\',@OriginalFileName)>0
       OR CHARINDEX(N'/',@OriginalFileName)>0
        THROW 52605, N'The original attachment filename is invalid.', 1;
    IF @FileSizeBytes<0 OR @ContentSha256 IS NULL
        THROW 52606, N'Attachment size and SHA-256 are required.', 1;

    DECLARE @NowUtc datetime2(3)=SYSUTCDATETIME(),
            @Action nvarchar(120);
    BEGIN TRY
        BEGIN TRANSACTION;

        IF @AttachmentExists=0
        BEGIN
            INSERT INTO [tb_client].[ClientAttachments]
            (
                [AttachmentId],[ClientId],[RelativePath],[OriginalFileName],
                [ContentType],[Category],[Caption],[FileSizeBytes],
                [ContentSha256],[UploadedByWindowsSid],[UploadedAtUtc],
                [IsArchived]
            )
            VALUES
            (
                @AttachmentId,@ClientId,@RelativePath,@OriginalFileName,
                @ContentType,@Category,@Caption,@FileSizeBytes,
                @ContentSha256,@ActorSid,@NowUtc,0
            );
            SET @Action=N'ClientInfoAttachmentUploaded';
        END
        ELSE
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 52607, N'ExpectedRowVersion is required when editing an attachment.', 1;

            UPDATE [tb_client].[ClientAttachments]
            SET [Category]=@Category,[Caption]=@Caption
            WHERE [AttachmentId]=@AttachmentId
              AND [ClientId]=@ClientId
              AND [RelativePath]=@RelativePath
              AND [OriginalFileName]=@OriginalFileName
              AND [ContentType]=@ContentType
              AND [FileSizeBytes]=@FileSizeBytes
              AND [ContentSha256]=@ContentSha256
              AND [RowVersion]=@ExpectedRowVersion;
            IF @@ROWCOUNT<>1
                THROW 52608, N'The attachment changed on another workstation. Refresh and resolve the conflict.', 1;
            SET @Action=N'ClientInfoAttachmentMetadataUpdated';
        END;

        DECLARE @AuditEntityId nvarchar(120)=
            CONVERT(nvarchar(120),@AttachmentId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action=@Action,
            @EntityType=N'ClientInfoAttachment',
            @EntityId=@AuditEntityId,
            @RequestId=@RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        attachment.[AttachmentId],attachment.[ClientId],attachment.[RelativePath],
        attachment.[OriginalFileName],attachment.[ContentType],attachment.[Category],
        attachment.[Caption],attachment.[FileSizeBytes],attachment.[ContentSha256],
        COALESCE(NULLIF(uploader.[DisplayName],N''),uploader.[LoginName],N'Unknown') AS [UploadedBy],
        attachment.[UploadedAtUtc],attachment.[IsArchived],N'' AS [ArchivedBy],
        attachment.[ArchivedAtUtc],attachment.[RowVersion]
    FROM [tb_client].[ClientAttachments] AS attachment
    LEFT JOIN [tb_security].[Users] AS uploader
        ON uploader.[WindowsSid]=attachment.[UploadedByWindowsSid]
    WHERE attachment.[AttachmentId]=@AttachmentId;
END;
GO

IF OBJECT_ID(N'tb_app.SetClientInfoAttachmentArchived', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SetClientInfoAttachmentArchived];
GO

CREATE PROCEDURE [tb_app].[SetClientInfoAttachmentArchived]
    @AttachmentId uniqueidentifier,
    @ClientId int,
    @IsArchived bit,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit,
            @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,
        @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1 AND IS_ROLEMEMBER(N'tb_role_client_info_editor')<>1
        THROW 52601, N'Client Info editor permission is required.', 1;
    IF @ExpectedRowVersion IS NULL
        THROW 52607, N'ExpectedRowVersion is required when archiving an attachment.', 1;
    SET @RequestId=COALESCE(@RequestId,NEWID());

    DECLARE @NowUtc datetime2(3)=SYSUTCDATETIME();
    BEGIN TRY
        BEGIN TRANSACTION;

        UPDATE [tb_client].[ClientAttachments]
        SET [IsArchived]=@IsArchived,
            [ArchivedByWindowsSid]=CASE WHEN @IsArchived=1 THEN @ActorSid END,
            [ArchivedAtUtc]=CASE WHEN @IsArchived=1 THEN @NowUtc END
        WHERE [AttachmentId]=@AttachmentId
          AND [ClientId]=@ClientId
          AND [RowVersion]=@ExpectedRowVersion;
        IF @@ROWCOUNT<>1
            THROW 52608, N'The attachment changed on another workstation. Refresh and resolve the conflict.', 1;

        DECLARE @AuditAction nvarchar(120)=CASE WHEN @IsArchived=1
            THEN N'ClientInfoAttachmentArchived'
            ELSE N'ClientInfoAttachmentRestored' END;
        DECLARE @AuditEntityId nvarchar(120)=
            CONVERT(nvarchar(120),@AttachmentId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action=@AuditAction,
            @EntityType=N'ClientInfoAttachment',
            @EntityId=@AuditEntityId,
            @RequestId=@RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        attachment.[AttachmentId],attachment.[ClientId],attachment.[RelativePath],
        attachment.[OriginalFileName],attachment.[ContentType],attachment.[Category],
        attachment.[Caption],attachment.[FileSizeBytes],attachment.[ContentSha256],
        COALESCE(NULLIF(uploader.[DisplayName],N''),uploader.[LoginName],N'Unknown') AS [UploadedBy],
        attachment.[UploadedAtUtc],attachment.[IsArchived],
        COALESCE(NULLIF(archiver.[DisplayName],N''),archiver.[LoginName],N'') AS [ArchivedBy],
        attachment.[ArchivedAtUtc],attachment.[RowVersion]
    FROM [tb_client].[ClientAttachments] AS attachment
    LEFT JOIN [tb_security].[Users] AS uploader
        ON uploader.[WindowsSid]=attachment.[UploadedByWindowsSid]
    LEFT JOIN [tb_security].[Users] AS archiver
        ON archiver.[WindowsSid]=attachment.[ArchivedByWindowsSid]
    WHERE attachment.[AttachmentId]=@AttachmentId;
END;
GO

PRINT N'Client Attachments procedures installed.';
GO
