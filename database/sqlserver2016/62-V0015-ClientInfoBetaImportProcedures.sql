:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'tb_app.BeginClientInfoImport', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[BeginClientInfoImport];
GO

CREATE PROCEDURE [tb_app].[BeginClientInfoImport]
    @ClientId int,
    @TemplateVersion nvarchar(40),
    @WorkbookId uniqueidentifier,
    @ContentSha256 binary(32),
    @SourceDisplayName nvarchar(260),
    @SourceModifiedAtUtc datetime2(3) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85),@IsManager bit,@IsAdmin bit,@IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,@IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,@IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1 AND IS_ROLEMEMBER(N'tb_role_client_migration_operator')<>1
        THROW 52400,N'Client migration operator permission is required.',1;

    SET @TemplateVersion=NULLIF(LTRIM(RTRIM(@TemplateVersion)),N'');
    SET @SourceDisplayName=NULLIF(LTRIM(RTRIM(@SourceDisplayName)),N'');
    IF @TemplateVersion IS NULL OR @SourceDisplayName IS NULL
       OR @WorkbookId IS NULL OR @ContentSha256 IS NULL
        THROW 52401,N'Template version, workbook ID, source name, and content hash are required.',1;
    IF NOT EXISTS
        (SELECT 1 FROM [tb_data].[Clients] WHERE [Id]=@ClientId AND [IsActive]=1)
        THROW 52402,N'The selected client does not exist or is inactive.',1;

    DECLARE @BatchId uniqueidentifier,@SourceDocumentId bigint,
            @NowUtc datetime2(3)=SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @BatchId=[BatchId]
        FROM [tb_import].[ClientInfoBatches] WITH (UPDLOCK,HOLDLOCK)
        WHERE [ClientId]=@ClientId AND [WorkbookId]=@WorkbookId
          AND [ContentSha256]=@ContentSha256
          AND [State] NOT IN (N'Rejected',N'Superseded',N'Failed');

        IF @BatchId IS NULL
        BEGIN
            SELECT @SourceDocumentId=[SourceDocumentId]
            FROM [tb_client].[SourceDocuments] WITH (UPDLOCK,HOLDLOCK)
            WHERE [ClientId]=@ClientId AND [SourceKind]=N'Workbook'
              AND [ContentSha256]=@ContentSha256;

            IF @SourceDocumentId IS NULL
            BEGIN
                INSERT INTO [tb_client].[SourceDocuments]
                (
                    [ClientId],[SourceKind],[DisplayName],[ContentSha256],
                    [SourceModifiedAtUtc],[ObservedAtUtc],[CreatedByWindowsSid]
                )
                VALUES
                (
                    @ClientId,N'Workbook',@SourceDisplayName,@ContentSha256,
                    @SourceModifiedAtUtc,@NowUtc,@ActorSid
                );
                SET @SourceDocumentId=CONVERT(bigint,SCOPE_IDENTITY());
            END;

            SET @BatchId=NEWID();
            INSERT INTO [tb_import].[ClientInfoBatches]
            (
                [BatchId],[ClientId],[SourceDocumentId],[TemplateVersion],
                [WorkbookId],[ContentSha256],[State],[Message],
                [CreatedByWindowsSid],[CreatedAtUtc],[UpdatedAtUtc]
            )
            VALUES
            (
                @BatchId,@ClientId,@SourceDocumentId,@TemplateVersion,
                @WorkbookId,@ContentSha256,N'Draft',N'Workbook accepted for staging.',
                @ActorSid,@NowUtc,@NowUtc
            );

            IF EXISTS
                (SELECT 1 FROM [tb_ops].[ClientInfoCutovers] WHERE [ClientId]=@ClientId)
                UPDATE [tb_ops].[ClientInfoCutovers]
                SET [ActiveBatchId]=@BatchId,[State]=N'Staging',
                    [UpdatedByWindowsSid]=@ActorSid,[UpdatedAtUtc]=@NowUtc
                WHERE [ClientId]=@ClientId;
            ELSE
                INSERT INTO [tb_ops].[ClientInfoCutovers]
                (
                    [ClientId],[ActiveBatchId],[State],
                    [UpdatedByWindowsSid],[UpdatedAtUtc]
                )
                VALUES (@ClientId,@BatchId,N'Staging',@ActorSid,@NowUtc);

            DECLARE @AuditEntityId nvarchar(120) =
                CONVERT(nvarchar(120), @BatchId);
            EXEC [tb_security].[WriteAuditEvent]
                @Action=N'ClientInfoImportStarted',
                @EntityType=N'ClientInfoImportBatch',
                @EntityId=@AuditEntityId,
                @RequestId=@RequestId;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [BatchId],[ClientId],[TemplateVersion],[WorkbookId],[State],
        [Message],[CreatedAtUtc],[UpdatedAtUtc],[RowVersion]
    FROM [tb_import].[ClientInfoBatches]
    WHERE [BatchId]=@BatchId;
END;
GO

IF OBJECT_ID(N'tb_app.StageClientInfoRecord', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[StageClientInfoRecord];
GO

CREATE PROCEDURE [tb_app].[StageClientInfoRecord]
    @BatchId uniqueidentifier,
    @RecordType nvarchar(40),
    @LocalKey nvarchar(120),
    @ParentLocalKey nvarchar(120) = NULL,
    @PayloadJson nvarchar(max),
    @SourceSheet nvarchar(128) = NULL,
    @SourceRow int = NULL,
    @ReviewStatus nvarchar(24) = N'Unverified'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ActorSid varbinary(85),@IsManager bit,@IsAdmin bit,@IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,@IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,@IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1 AND IS_ROLEMEMBER(N'tb_role_client_migration_operator')<>1
        THROW 52400,N'Client migration operator permission is required.',1;

    SET @RecordType=NULLIF(LTRIM(RTRIM(@RecordType)),N'');
    SET @LocalKey=NULLIF(LTRIM(RTRIM(@LocalKey)),N'');
    SET @ParentLocalKey=NULLIF(LTRIM(RTRIM(@ParentLocalKey)),N'');
    IF @RecordType NOT IN
        (N'Profile',N'Location',N'Person',N'Resource',N'ResourceField',
         N'Credential',N'Fact',N'Equipment')
       OR @LocalKey IS NULL OR ISJSON(@PayloadJson)<>1
        THROW 52410,N'The staged Client Info record is invalid.',1;
    IF @ReviewStatus NOT IN
        (N'Unverified',N'Verified',N'AcceptedUnverified',N'NeedsReview',N'Rejected')
        THROW 52411,N'The staged review status is invalid.',1;
    IF NOT EXISTS
    (
        SELECT 1 FROM [tb_import].[ClientInfoBatches]
        WHERE [BatchId]=@BatchId
          AND [State] IN (N'Draft',N'Parsed',N'ValidationFailed',N'InReview')
    )
        THROW 52412,N'This import batch can no longer be changed.',1;

    MERGE [tb_import].[ClientInfoRecords] WITH (HOLDLOCK) AS target_record
    USING
    (
        SELECT @BatchId AS [BatchId],@RecordType AS [RecordType],
               @LocalKey AS [LocalKey]
    ) AS source_record
    ON target_record.[BatchId]=source_record.[BatchId]
       AND target_record.[RecordType]=source_record.[RecordType]
       AND target_record.[LocalKey]=source_record.[LocalKey]
    WHEN MATCHED THEN UPDATE SET
        [ParentLocalKey]=@ParentLocalKey,[PayloadJson]=@PayloadJson,
        [SourceSheet]=NULLIF(@SourceSheet,N''),[SourceRow]=@SourceRow,
        [ReviewStatus]=@ReviewStatus,[CreatedAtUtc]=SYSUTCDATETIME()
    WHEN NOT MATCHED THEN INSERT
    (
        [BatchId],[RecordType],[LocalKey],[ParentLocalKey],[PayloadJson],
        [SourceSheet],[SourceRow],[ReviewStatus]
    )
    VALUES
    (
        @BatchId,@RecordType,@LocalKey,@ParentLocalKey,@PayloadJson,
        NULLIF(@SourceSheet,N''),@SourceRow,@ReviewStatus
    );

    UPDATE [tb_import].[ClientInfoBatches]
    SET [State]=N'Parsed',[Message]=N'Workbook rows staged.',
        [UpdatedAtUtc]=SYSUTCDATETIME()
    WHERE [BatchId]=@BatchId;
END;
GO

IF OBJECT_ID(N'tb_app.StageClientInfoSecret', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[StageClientInfoSecret];
GO

CREATE PROCEDURE [tb_app].[StageClientInfoSecret]
    @BatchId uniqueidentifier,
    @CredentialLocalKey nvarchar(120),
    @SecretType nvarchar(80),
    @SecretLabel nvarchar(200),
    @SecretValue nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ActorSid varbinary(85),@IsManager bit,@IsAdmin bit,@IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,@IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,@IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1
       AND IS_ROLEMEMBER(N'tb_role_client_migration_operator')<>1
       AND IS_ROLEMEMBER(N'tb_role_client_secret_editor')<>1
        THROW 52400,N'Client migration operator permission is required.',1;

    SET @CredentialLocalKey=NULLIF(LTRIM(RTRIM(@CredentialLocalKey)),N'');
    SET @SecretType=NULLIF(LTRIM(RTRIM(@SecretType)),N'');
    SET @SecretLabel=NULLIF(LTRIM(RTRIM(@SecretLabel)),N'');
    IF @CredentialLocalKey IS NULL OR @SecretType IS NULL
       OR @SecretLabel IS NULL OR NULLIF(@SecretValue,N'') IS NULL
        THROW 52420,N'The staged client secret is invalid.',1;
    IF NOT EXISTS
    (
        SELECT 1 FROM [tb_import].[ClientInfoBatches]
        WHERE [BatchId]=@BatchId
          AND [State] IN (N'Draft',N'Parsed',N'ValidationFailed',N'InReview')
    )
        THROW 52412,N'This import batch can no longer be changed.',1;
    IF NOT EXISTS
    (
        SELECT 1 FROM [tb_import].[ClientInfoRecords]
        WHERE [BatchId]=@BatchId AND [RecordType]=N'Credential'
          AND [LocalKey]=@CredentialLocalKey
    )
        THROW 52421,N'The staged secret does not reference a credential row.',1;

    DECLARE @ImportSecretId bigint;
    SELECT @ImportSecretId=[ImportSecretId]
    FROM [tb_import].[ClientInfoSecrets] WITH (UPDLOCK,HOLDLOCK)
    WHERE [BatchId]=@BatchId AND [CredentialLocalKey]=@CredentialLocalKey
      AND [SecretType]=@SecretType AND [SecretLabel]=@SecretLabel;

    IF @ImportSecretId IS NULL
    BEGIN
        INSERT INTO [tb_import].[ClientInfoSecrets]
        (
            [BatchId],[CredentialLocalKey],[SecretType],[SecretLabel],
            [ValueEncrypted],[ComparisonStatus]
        )
        VALUES
        (
            @BatchId,@CredentialLocalKey,@SecretType,@SecretLabel,
            0x,N'NotCompared'
        );
        SET @ImportSecretId=CONVERT(bigint,SCOPE_IDENTITY());
    END;

    DECLARE @Authenticator varbinary(32)=HASHBYTES(
        N'SHA2_256',
        CONVERT(varbinary(max),
            N'ClientImportSecret|' + CONVERT(nvarchar(30),@ImportSecretId)));
    DECLARE @EncryptedValue varbinary(max);
    EXEC [tb_security].[EncryptClientSecretValue]
        @SecretValue=@SecretValue,
        @Authenticator=@Authenticator,
        @EncryptedValue=@EncryptedValue OUTPUT;
    UPDATE [tb_import].[ClientInfoSecrets]
    SET [ValueEncrypted]=@EncryptedValue,
        [ComparisonStatus]=N'NotCompared',
        [Resolution]=NULL
    WHERE [ImportSecretId]=@ImportSecretId;

    IF EXISTS
        (SELECT 1 FROM [tb_import].[ClientInfoSecrets]
         WHERE [ImportSecretId]=@ImportSecretId
           AND ([ValueEncrypted] IS NULL OR DATALENGTH([ValueEncrypted])=0))
        THROW 52422,N'The staged client secret could not be encrypted.',1;
END;
GO

IF OBJECT_ID(N'tb_app.ValidateClientInfoImport', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ValidateClientInfoImport];
GO

CREATE PROCEDURE [tb_app].[ValidateClientInfoImport]
    @BatchId uniqueidentifier,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ActorSid varbinary(85),@IsManager bit,@IsAdmin bit,@IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,@IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,@IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1 AND IS_ROLEMEMBER(N'tb_role_client_migration_operator')<>1
        THROW 52400,N'Client migration operator permission is required.',1;

    IF NOT EXISTS
        (SELECT 1 FROM [tb_import].[ClientInfoBatches] WHERE [BatchId]=@BatchId)
        THROW 52430,N'The import batch was not found.',1;

    BEGIN TRY
        BEGIN TRANSACTION;
        DELETE FROM [tb_import].[ClientInfoIssues]
        WHERE [BatchId]=@BatchId AND [IsResolved]=0;

        IF NOT EXISTS
        (
            SELECT 1 FROM [tb_import].[ClientInfoRecords]
            WHERE [BatchId]=@BatchId AND [RecordType]=N'Profile'
              AND [ReviewStatus]<>N'Rejected'
        )
            INSERT INTO [tb_import].[ClientInfoIssues]
                ([BatchId],[Severity],[IssueCode],[Message])
            VALUES
                (@BatchId,N'Error',N'PROFILE_REQUIRED',
                 N'The workbook must include one Client Info profile row.');

        INSERT INTO [tb_import].[ClientInfoIssues]
            ([BatchId],[ImportRecordId],[Severity],[IssueCode],[Message])
        SELECT [BatchId],[ImportRecordId],N'Error',N'NAME_REQUIRED',
            N'This row requires a non-blank Name value.'
        FROM [tb_import].[ClientInfoRecords]
        WHERE [BatchId]=@BatchId
          AND [RecordType] IN (N'Location',N'Resource',N'Credential',N'Equipment')
          AND NULLIF(LTRIM(RTRIM(JSON_VALUE([PayloadJson],N'$.name'))),N'') IS NULL;

        INSERT INTO [tb_import].[ClientInfoIssues]
            ([BatchId],[ImportRecordId],[Severity],[IssueCode],[Message])
        SELECT [BatchId],[ImportRecordId],N'Error',N'PERSON_NAME_REQUIRED',
            N'This person row requires a non-blank Display Name value.'
        FROM [tb_import].[ClientInfoRecords]
        WHERE [BatchId]=@BatchId AND [RecordType]=N'Person'
          AND NULLIF(LTRIM(RTRIM(JSON_VALUE([PayloadJson],N'$.displayName'))),N'') IS NULL;

        INSERT INTO [tb_import].[ClientInfoIssues]
            ([BatchId],[ImportRecordId],[Severity],[IssueCode],[Message])
        SELECT [BatchId],[ImportRecordId],N'Error',N'FACT_FIELDS_REQUIRED',
            N'This Other Info row requires Section and Field Label values.'
        FROM [tb_import].[ClientInfoRecords]
        WHERE [BatchId]=@BatchId AND [RecordType]=N'Fact'
          AND
          (
              NULLIF(LTRIM(RTRIM(JSON_VALUE([PayloadJson],N'$.sectionName'))),N'') IS NULL
              OR NULLIF(LTRIM(RTRIM(JSON_VALUE([PayloadJson],N'$.fieldLabel'))),N'') IS NULL
          );

        INSERT INTO [tb_import].[ClientInfoIssues]
            ([BatchId],[ImportRecordId],[Severity],[IssueCode],[Message])
        SELECT [BatchId],[ImportRecordId],N'Error',N'RESOURCE_FIELD_INVALID',
            N'Resource field "'
            + COALESCE(NULLIF(LTRIM(RTRIM(JSON_VALUE([PayloadJson],N'$.fieldLabel'))),N''),N'(unnamed)')
            + N'" on '
            + COALESCE(NULLIF(LTRIM(RTRIM([SourceSheet])),N''),N'the workbook')
            + CASE WHEN [SourceRow] IS NULL THEN N''
                   ELSE N' row ' + CONVERT(nvarchar(20),[SourceRow]) END
            + N' requires a parent resource, field key, field label, and valid value type. Supported types are Text, Number, Boolean, Date, URL, IP address, Phone, and Email.'
        FROM [tb_import].[ClientInfoRecords]
        WHERE [BatchId]=@BatchId AND [RecordType]=N'ResourceField'
          AND
          (
              NULLIF(LTRIM(RTRIM([ParentLocalKey])),N'') IS NULL
              OR NULLIF(LTRIM(RTRIM(JSON_VALUE([PayloadJson],N'$.fieldKey'))),N'') IS NULL
              OR NULLIF(LTRIM(RTRIM(JSON_VALUE([PayloadJson],N'$.fieldLabel'))),N'') IS NULL
              OR JSON_VALUE([PayloadJson],N'$.valueType') NOT IN
                  (N'Text',N'Number',N'Boolean',N'Date',N'Url',N'IpAddress',N'Phone',N'Email')
          );

        INSERT INTO [tb_import].[ClientInfoIssues]
            ([BatchId],[ImportRecordId],[Severity],[IssueCode],[Message])
        SELECT field.[BatchId],field.[ImportRecordId],N'Error',
            N'ORPHAN_RESOURCE_FIELD',
            N'This custom or standard field does not reference a staged resource.'
        FROM [tb_import].[ClientInfoRecords] field
        WHERE field.[BatchId]=@BatchId
          AND field.[RecordType]=N'ResourceField'
          AND NOT EXISTS
          (
              SELECT 1
              FROM [tb_import].[ClientInfoRecords] resource
              WHERE resource.[BatchId]=field.[BatchId]
                AND resource.[RecordType]=N'Resource'
                AND resource.[LocalKey]=field.[ParentLocalKey]
                AND resource.[ReviewStatus]<>N'Rejected'
          );

        INSERT INTO [tb_import].[ClientInfoIssues]
            ([BatchId],[Severity],[IssueCode],[Message])
        SELECT @BatchId,N'Error',N'ORPHAN_SECRET',
            N'One or more secret rows do not reference a staged credential.'
        WHERE EXISTS
        (
            SELECT 1
            FROM [tb_import].[ClientInfoSecrets] AS secret
            WHERE secret.[BatchId]=@BatchId
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM [tb_import].[ClientInfoRecords] AS record
                  WHERE record.[BatchId]=secret.[BatchId]
                    AND record.[RecordType]=N'Credential'
                    AND record.[LocalKey]=secret.[CredentialLocalKey]
              )
        );

        INSERT INTO [tb_import].[ClientInfoIssues]
            ([BatchId],[ImportRecordId],[Severity],[IssueCode],[Message])
        SELECT
            record.[BatchId],record.[ImportRecordId],N'Warning',N'UNVERIFIED_RECORD',
            N'Unverified '+LOWER(record.[RecordType])
            +CASE
                WHEN COALESCE(
                        NULLIF(JSON_VALUE(record.[PayloadJson],N'$.displayName'),N''),
                        NULLIF(JSON_VALUE(record.[PayloadJson],N'$.name'),N''),
                        NULLIF(JSON_VALUE(record.[PayloadJson],N'$.fieldLabel'),N''),
                        NULLIF(JSON_VALUE(record.[PayloadJson],N'$.item'),N'')) IS NULL
                    THEN N''
                ELSE N' "'+COALESCE(
                    NULLIF(JSON_VALUE(record.[PayloadJson],N'$.displayName'),N''),
                    NULLIF(JSON_VALUE(record.[PayloadJson],N'$.name'),N''),
                    NULLIF(JSON_VALUE(record.[PayloadJson],N'$.fieldLabel'),N''),
                    NULLIF(JSON_VALUE(record.[PayloadJson],N'$.item'),N''))+N'"'
             END
            +N' on '+COALESCE(NULLIF(record.[SourceSheet],N''),N'the workbook')
            +CASE WHEN record.[SourceRow] IS NULL THEN N''
                  ELSE N' row '+CONVERT(nvarchar(20),record.[SourceRow]) END
            +N'. Set Review Status to Verified or use Accept Remaining as Keep as-is before approval.'
        FROM [tb_import].[ClientInfoRecords] AS record
        WHERE record.[BatchId]=@BatchId AND record.[ReviewStatus]=N'Unverified';

        INSERT INTO [tb_import].[ClientInfoIssues]
            ([BatchId],[ImportRecordId],[Severity],[IssueCode],[Message])
        SELECT
            record.[BatchId],record.[ImportRecordId],N'Warning',N'NEEDS_REVIEW_RECORD',
            N'Record on '+COALESCE(NULLIF(record.[SourceSheet],N''),N'the workbook')
            +CASE WHEN record.[SourceRow] IS NULL THEN N''
                  ELSE N' row '+CONVERT(nvarchar(20),record.[SourceRow]) END
            +N' is marked Needs review. Verify it, accept it, or exclude it in the workbook before approval.'
        FROM [tb_import].[ClientInfoRecords] AS record
        WHERE record.[BatchId]=@BatchId AND record.[ReviewStatus]=N'NeedsReview';

        DECLARE @ErrorCount int=
            (SELECT COUNT(*) FROM [tb_import].[ClientInfoIssues]
             WHERE [BatchId]=@BatchId AND [Severity]=N'Error' AND [IsResolved]=0);
        DECLARE @WarningCount int=
            (SELECT COUNT(*) FROM [tb_import].[ClientInfoIssues]
             WHERE [BatchId]=@BatchId AND [Severity]=N'Warning' AND [IsResolved]=0);

        UPDATE [tb_import].[ClientInfoBatches]
        SET [State]=CASE WHEN @ErrorCount>0 THEN N'ValidationFailed' ELSE N'InReview' END,
            [Message]=CASE
                WHEN @ErrorCount>0
                    THEN CONVERT(nvarchar(20),@ErrorCount)+N' blocking error(s) require attention.'
                WHEN @WarningCount>0
                    THEN N'Validation passed with '+CONVERT(nvarchar(20),@WarningCount)+N' warning(s).'
                ELSE N'Validation passed.' END,
            [UpdatedAtUtc]=SYSUTCDATETIME()
        WHERE [BatchId]=@BatchId;

        UPDATE older_batch
        SET
            [State]=N'Superseded',
            [Message]=N'Replaced by a newer revision of this workbook.',
            [UpdatedAtUtc]=SYSUTCDATETIME()
        FROM [tb_import].[ClientInfoBatches] AS older_batch
        INNER JOIN [tb_import].[ClientInfoBatches] AS current_batch
            ON current_batch.[BatchId]=@BatchId
           AND current_batch.[ClientId]=older_batch.[ClientId]
           AND current_batch.[WorkbookId]=older_batch.[WorkbookId]
        WHERE older_batch.[BatchId]<>@BatchId
          AND older_batch.[State] IN
              (N'Draft',N'Parsed',N'Validated',N'InReview',N'ValidationFailed');

        DECLARE @AuditEntityId nvarchar(120) =
            CONVERT(nvarchar(120), @BatchId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action=N'ClientInfoImportValidated',
            @EntityType=N'ClientInfoImportBatch',
            @EntityId=@AuditEntityId,@RequestId=@RequestId,
            @DataJson=N'{"containsSecretValues":false}';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    EXEC [tb_app].[GetClientInfoImportBatch] @BatchId=@BatchId;
END;
GO

IF OBJECT_ID(N'tb_app.GetClientInfoImportBatch', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetClientInfoImportBatch];
GO

IF OBJECT_ID(N'tb_security.GetClientInfoImportBatchResult', N'P') IS NOT NULL
    DROP PROCEDURE [tb_security].[GetClientInfoImportBatchResult];
GO

CREATE PROCEDURE [tb_security].[GetClientInfoImportBatchResult]
    @BatchId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SELECT
        batch.[BatchId],batch.[ClientId],client.[Name] AS [ClientName],
        batch.[TemplateVersion],batch.[WorkbookId],batch.[State],batch.[Message],
        batch.[CreatedAtUtc],batch.[UpdatedAtUtc],batch.[ApprovedAtUtc],
        batch.[PromotedAtUtc],batch.[RowVersion],
        (SELECT COUNT(*) FROM [tb_import].[ClientInfoRecords] record
         WHERE record.[BatchId]=batch.[BatchId]) AS [RecordCount],
        (SELECT COUNT(*) FROM [tb_import].[ClientInfoSecrets] secret
         WHERE secret.[BatchId]=batch.[BatchId]) AS [SecretCount],
        (SELECT COUNT(*) FROM [tb_import].[ClientInfoSecrets] secret
         WHERE secret.[BatchId]=batch.[BatchId]
           AND secret.[ComparisonStatus]=N'Match') AS [SecretMatchCount],
        (SELECT COUNT(*) FROM [tb_import].[ClientInfoSecrets] secret
         WHERE secret.[BatchId]=batch.[BatchId]
           AND secret.[ComparisonStatus]=N'Mismatch') AS [SecretMismatchCount],
        (SELECT COUNT(*) FROM [tb_import].[ClientInfoSecrets] secret
         WHERE secret.[BatchId]=batch.[BatchId]
           AND secret.[ComparisonStatus]=N'WorkbookOnly') AS [SecretWorkbookOnlyCount],
        (SELECT COUNT(*) FROM [tb_import].[ClientInfoIssues] issue
         WHERE issue.[BatchId]=batch.[BatchId] AND issue.[Severity]=N'Error'
           AND issue.[IsResolved]=0) AS [BlockingIssueCount],
        (SELECT COUNT(*) FROM [tb_import].[ClientInfoIssues] issue
         WHERE issue.[BatchId]=batch.[BatchId] AND issue.[Severity]=N'Warning'
           AND issue.[IsResolved]=0) AS [WarningCount]
    FROM [tb_import].[ClientInfoBatches] batch
    INNER JOIN [tb_data].[Clients] client ON client.[Id]=batch.[ClientId]
    WHERE batch.[BatchId]=@BatchId;

    SELECT
        [IssueId],[ImportRecordId],[Severity],[IssueCode],[Message],
        [IsResolved],[ResolutionNote],[ResolvedAtUtc],[RowVersion]
    FROM [tb_import].[ClientInfoIssues]
    WHERE [BatchId]=@BatchId
    ORDER BY [IsResolved],[Severity],[IssueId];
END;
GO

CREATE PROCEDURE [tb_app].[GetClientInfoImportBatch]
    @BatchId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ActorSid varbinary(85),@IsManager bit,@IsAdmin bit,@IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,@IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,@IsSyncOperator=@IsSyncOperator OUTPUT;
    EXEC [tb_security].[GetClientInfoImportBatchResult] @BatchId=@BatchId;
END;
GO

IF OBJECT_ID(N'tb_app.CompareClientInfoImportToFireDrill', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[CompareClientInfoImportToFireDrill];
GO

CREATE PROCEDURE [tb_app].[CompareClientInfoImportToFireDrill]
    @BatchId uniqueidentifier,
    @RequestId uniqueidentifier = NULL
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ClientId int,@ClientName nvarchar(240),
            @WhdLocationName nvarchar(240),@SageCustomerName nvarchar(240);
    SELECT
        @ClientId=batch.[ClientId],
        @ClientName=client.[Name],
        @WhdLocationName=client.[WhdLocationName],
        @SageCustomerName=client.[SageCustomerName]
    FROM [tb_import].[ClientInfoBatches] batch
    INNER JOIN [tb_data].[Clients] client ON client.[Id]=batch.[ClientId]
    WHERE batch.[BatchId]=@BatchId
      AND batch.[State] IN
          (N'Parsed',N'Validated',N'InReview',N'ValidationFailed');
    IF @ClientId IS NULL
        THROW 52430,N'The selected import batch cannot be compared.',1;

    CREATE TABLE #FireDrillHashes
    (
        [ValueHash] binary(32) NOT NULL,
        [FieldLabel] nvarchar(200) NOT NULL
    );
    CREATE TABLE #WorkbookHashes
    (
        [ImportSecretId] bigint NOT NULL PRIMARY KEY,
        [ValueHash] binary(32) NULL
    );

    BEGIN TRY
        OPEN SYMMETRIC KEY [tb_FireDrillCredentialKey]
            DECRYPTION BY CERTIFICATE [tb_FireDrillCredentialCertificate];
        OPEN SYMMETRIC KEY [tb_ClientSecretKey]
            DECRYPTION BY CERTIFICATE [tb_ClientSecretCertificate];

        INSERT INTO #FireDrillHashes([ValueHash],[FieldLabel])
        SELECT
            HASHBYTES(N'SHA2_256',decrypted.[ValuePlain]),
            field.[FieldLabel]
        FROM [tb_data].[FireDrillCredentials] credential
        INNER JOIN [tb_data].[FireDrillCredentialFields] field
            ON field.[CredentialId]=credential.[CredentialId]
        CROSS APPLY
        (
            SELECT DecryptByKey(
                field.[ValueEncrypted],
                1,
                CONVERT(
                    nvarchar(64),
                    HASHBYTES(
                        N'SHA2_256',
                        CONVERT(
                            varbinary(max),
                            credential.[ClientKey]+N'|'+field.[FieldKey])),
                    2)) AS [ValuePlain]
        ) decrypted
        WHERE credential.[IsCurrent]=1
          AND field.[ValueEncrypted] IS NOT NULL
          AND decrypted.[ValuePlain] IS NOT NULL
          AND
          (
              LTRIM(RTRIM(credential.[ClientName]))=LTRIM(RTRIM(@ClientName))
              OR LTRIM(RTRIM(credential.[ClientName]))=LTRIM(RTRIM(COALESCE(@WhdLocationName,N'')))
              OR LTRIM(RTRIM(credential.[ClientName]))=LTRIM(RTRIM(COALESCE(@SageCustomerName,N'')))
          );

        INSERT INTO #FireDrillHashes([ValueHash],[FieldLabel])
        SELECT
            HASHBYTES(N'SHA2_256',decrypted.[ValuePlain]),
            legacy.[FieldLabel]
        FROM [tb_data].[FireDrillCredentials] credential
        CROSS APPLY
        (
            VALUES
                (credential.[AdminEncrypted],N'Admin'),
                (credential.[CsriAdminEncrypted],N'CSRI Admin'),
                (credential.[FireboxDbCsriEncrypted],N'Firebox DB CSRI'),
                (credential.[AuthpointUserEncrypted],N'AuthPoint User'),
                (credential.[SslVpnPasswordEncrypted],N'SSL VPN Password'),
                (credential.[AdAuthUserEncrypted],N'AD Auth User'),
                (credential.[AdPasswordEncrypted],N'AD Password'),
                (credential.[RustPasswordEncrypted],N'Rust Password')
        ) legacy([ValueEncrypted],[FieldLabel])
        CROSS APPLY
        (
            SELECT DecryptByKey(
                legacy.[ValueEncrypted],
                1,
                CONVERT(
                    nvarchar(64),
                    HASHBYTES(N'SHA2_256',credential.[ClientKey]),
                    2)) AS [ValuePlain]
        ) decrypted
        WHERE credential.[IsCurrent]=1
          AND legacy.[ValueEncrypted] IS NOT NULL
          AND decrypted.[ValuePlain] IS NOT NULL
          AND
          (
              LTRIM(RTRIM(credential.[ClientName]))=LTRIM(RTRIM(@ClientName))
              OR LTRIM(RTRIM(credential.[ClientName]))=LTRIM(RTRIM(COALESCE(@WhdLocationName,N'')))
              OR LTRIM(RTRIM(credential.[ClientName]))=LTRIM(RTRIM(COALESCE(@SageCustomerName,N'')))
          );

        INSERT INTO #WorkbookHashes([ImportSecretId],[ValueHash])
        SELECT
            secret.[ImportSecretId],
            HASHBYTES(
                N'SHA2_256',
                DecryptByKey(
                    secret.[ValueEncrypted],
                    1,
                    HASHBYTES(
                        N'SHA2_256',
                        CONVERT(
                            varbinary(max),
                            N'ClientImportSecret|'
                            +CONVERT(
                                nvarchar(30),
                                secret.[ImportSecretId])))))
        FROM [tb_import].[ClientInfoSecrets] secret
        WHERE secret.[BatchId]=@BatchId;

        DECLARE @HasFireDrillClient bit =
            CASE WHEN EXISTS
            (
                SELECT 1
                FROM [tb_data].[FireDrillCredentials] credential
                WHERE credential.[IsCurrent]=1
                  AND
                  (
                      LTRIM(RTRIM(credential.[ClientName]))=LTRIM(RTRIM(@ClientName))
                      OR LTRIM(RTRIM(credential.[ClientName]))=LTRIM(RTRIM(COALESCE(@WhdLocationName,N'')))
                      OR LTRIM(RTRIM(credential.[ClientName]))=LTRIM(RTRIM(COALESCE(@SageCustomerName,N'')))
                  )
            ) THEN 1 ELSE 0 END;
        DECLARE @HasFireDrillValues bit =
            CASE WHEN EXISTS(SELECT 1 FROM #FireDrillHashes)
                THEN 1 ELSE 0 END;
        UPDATE secret
        SET
            [ComparisonStatus]=
                CASE
                    WHEN workbook_value.[ValueHash] IS NULL THEN N'NotComparable'
                    WHEN @HasFireDrillClient=0 THEN N'WorkbookOnly'
                    WHEN @HasFireDrillValues=0 THEN N'NotComparable'
                    WHEN EXISTS
                    (
                        SELECT 1
                        FROM #FireDrillHashes fire_value
                        WHERE fire_value.[ValueHash]=workbook_value.[ValueHash]
                    )
                        THEN N'Match'
                    ELSE N'Mismatch'
                END
        FROM [tb_import].[ClientInfoSecrets] secret
        INNER JOIN #WorkbookHashes workbook_value
            ON workbook_value.[ImportSecretId]=secret.[ImportSecretId]
        WHERE secret.[BatchId]=@BatchId;

        DELETE FROM [tb_import].[ClientInfoIssues]
        WHERE [BatchId]=@BatchId
          AND [IssueCode] IN
              (N'FIREDRILL_MISMATCH',N'WORKBOOK_ONLY_SECRET',
               N'FIREDRILL_ONLY_SECRET',N'SECRET_NOT_COMPARABLE',
               N'FIREDRILL_VALUES_UNAVAILABLE');

        INSERT INTO [tb_import].[ClientInfoIssues]
            ([BatchId],[Severity],[IssueCode],[Message])
        SELECT @BatchId,N'Warning',N'SECRET_NOT_COMPARABLE',
            CONVERT(nvarchar(20),COUNT(*))
            +N' workbook secret(s) could not be decrypted for comparison. Import review can continue.'
        FROM [tb_import].[ClientInfoSecrets] secret
        INNER JOIN #WorkbookHashes workbook_value
            ON workbook_value.[ImportSecretId]=secret.[ImportSecretId]
        WHERE secret.[BatchId]=@BatchId
          AND workbook_value.[ValueHash] IS NULL
        HAVING COUNT(*)>0;

        INSERT INTO [tb_import].[ClientInfoIssues]
            ([BatchId],[Severity],[IssueCode],[Message])
        SELECT @BatchId,N'Warning',N'FIREDRILL_VALUES_UNAVAILABLE',
            N'The FireDrill client name matched, but none of its stored values could be compared. Import review can continue.'
        WHERE @HasFireDrillClient=1 AND @HasFireDrillValues=0;

        INSERT INTO [tb_import].[ClientInfoIssues]
            ([BatchId],[Severity],[IssueCode],[Message])
        SELECT @BatchId,N'Warning',N'FIREDRILL_MISMATCH',
            CONVERT(nvarchar(20),COUNT(*))
            +N' workbook secret(s) do not match any value in the current FireDrill client.'
        FROM [tb_import].[ClientInfoSecrets]
        WHERE [BatchId]=@BatchId AND [ComparisonStatus]=N'Mismatch'
        HAVING COUNT(*)>0;

        INSERT INTO [tb_import].[ClientInfoIssues]
            ([BatchId],[Severity],[IssueCode],[Message])
        SELECT @BatchId,N'Warning',N'WORKBOOK_ONLY_SECRET',
            CONVERT(nvarchar(20),COUNT(*))
            +N' workbook secret(s) could not be compared because no current FireDrill client match was found.'
        FROM [tb_import].[ClientInfoSecrets]
        WHERE [BatchId]=@BatchId AND [ComparisonStatus]=N'WorkbookOnly'
        HAVING COUNT(*)>0;

        INSERT INTO [tb_import].[ClientInfoIssues]
            ([BatchId],[Severity],[IssueCode],[Message])
        SELECT @BatchId,N'Warning',N'FIREDRILL_ONLY_SECRET',
            N'FireDrill contains one or more values not represented in this workbook.'
        WHERE EXISTS
        (
            SELECT 1 FROM #FireDrillHashes fire_value
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM #WorkbookHashes workbook_value
                WHERE fire_value.[ValueHash]=workbook_value.[ValueHash]
            )
        );

        CLOSE SYMMETRIC KEY [tb_ClientSecretKey];
        CLOSE SYMMETRIC KEY [tb_FireDrillCredentialKey];
    END TRY
    BEGIN CATCH
        IF EXISTS
            (SELECT 1 FROM sys.openkeys WHERE [key_name]=N'tb_ClientSecretKey')
            CLOSE SYMMETRIC KEY [tb_ClientSecretKey];
        IF EXISTS
            (SELECT 1 FROM sys.openkeys WHERE [key_name]=N'tb_FireDrillCredentialKey')
            CLOSE SYMMETRIC KEY [tb_FireDrillCredentialKey];
        THROW;
    END CATCH;

    DECLARE @AuditEntityId nvarchar(120)=
        CONVERT(nvarchar(120),@BatchId);
    EXEC [tb_security].[WriteAuditEvent]
        @Action=N'ClientInfoImportComparedToFireDrill',
        @EntityType=N'ClientInfoImportBatch',
        @EntityId=@AuditEntityId,@RequestId=@RequestId,
        @DataJson=N'{"containsSecretValues":false}';

    EXEC [tb_security].[GetClientInfoImportBatchResult] @BatchId=@BatchId;
END;
GO

IF OBJECT_ID(N'tb_app.ResolveClientInfoImportIssue', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ResolveClientInfoImportIssue];
GO

CREATE PROCEDURE [tb_app].[ResolveClientInfoImportIssue]
    @IssueId bigint,
    @ResolutionNote nvarchar(1000),
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ActorSid varbinary(85),@IsManager bit,@IsAdmin bit,@IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,@IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,@IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1 AND IS_ROLEMEMBER(N'tb_role_client_migration_operator')<>1
        THROW 52400,N'Client migration operator permission is required.',1;
    SET @ResolutionNote=NULLIF(LTRIM(RTRIM(@ResolutionNote)),N'');
    IF @ResolutionNote IS NULL
        THROW 52440,N'A resolution note is required.',1;
    UPDATE [tb_import].[ClientInfoIssues]
    SET [IsResolved]=1,[ResolutionNote]=@ResolutionNote,
        [ResolvedByWindowsSid]=@ActorSid,[ResolvedAtUtc]=SYSUTCDATETIME()
    WHERE [IssueId]=@IssueId AND [RowVersion]=@ExpectedRowVersion;
    IF @@ROWCOUNT<>1
        THROW 52441,N'The import issue changed on another workstation.',1;
    DECLARE @AuditEntityId nvarchar(120) =
        CONVERT(nvarchar(120), @IssueId);
    EXEC [tb_security].[WriteAuditEvent]
        @Action=N'ClientInfoImportIssueResolved',
        @EntityType=N'ClientInfoImportIssue',
        @EntityId=@AuditEntityId,@RequestId=@RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.AcceptClientInfoImportUnverified', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AcceptClientInfoImportUnverified];
GO

CREATE PROCEDURE [tb_app].[AcceptClientInfoImportUnverified]
    @BatchId uniqueidentifier,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ActorSid varbinary(85),@IsManager bit,@IsAdmin bit,@IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,@IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,@IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1 AND IS_ROLEMEMBER(N'tb_role_client_migration_operator')<>1
        THROW 52400,N'Client migration operator permission is required.',1;

    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE [tb_import].[ClientInfoBatches]
        SET [Message]=N'Remaining unverified rows were accepted as Keep as-is.',
            [UpdatedAtUtc]=SYSUTCDATETIME()
        WHERE [BatchId]=@BatchId
          AND [State] IN (N'InReview',N'ValidationFailed',N'Validated')
          AND [RowVersion]=@ExpectedRowVersion;
        IF @@ROWCOUNT<>1
            THROW 52442,N'The import batch changed or cannot accept unverified rows.',1;

        UPDATE [tb_import].[ClientInfoRecords]
        SET [ReviewStatus]=N'AcceptedUnverified'
        WHERE [BatchId]=@BatchId AND [ReviewStatus]=N'Unverified';
        IF @@ROWCOUNT=0
            THROW 52443,N'This import has no remaining unverified rows.',1;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    DECLARE @AuditEntityId nvarchar(120)=CONVERT(nvarchar(120),@BatchId);
    EXEC [tb_security].[WriteAuditEvent]
        @Action=N'ClientInfoImportUnverifiedAccepted',
        @EntityType=N'ClientInfoImportBatch',
        @EntityId=@AuditEntityId,@RequestId=@RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.DiscardClientInfoImport', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DiscardClientInfoImport];
GO

CREATE PROCEDURE [tb_app].[DiscardClientInfoImport]
    @BatchId uniqueidentifier,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ActorSid varbinary(85),@IsManager bit,@IsAdmin bit,@IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,@IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,@IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1 AND IS_ROLEMEMBER(N'tb_role_client_migration_operator')<>1
        THROW 52400,N'Client migration operator permission is required.',1;

    DECLARE @ClientId int,@NextBatchId uniqueidentifier,@NextBatchState nvarchar(24);
    BEGIN TRY
        BEGIN TRANSACTION;
        SELECT @ClientId=[ClientId]
        FROM [tb_import].[ClientInfoBatches] WITH (UPDLOCK,HOLDLOCK)
        WHERE [BatchId]=@BatchId
          AND [State] IN
              (N'Draft',N'Parsed',N'Validated',N'InReview',N'ValidationFailed',N'Approved')
          AND [RowVersion]=@ExpectedRowVersion;
        IF @ClientId IS NULL
            THROW 52454,N'The import batch changed or can no longer be discarded.',1;

        UPDATE [tb_import].[ClientInfoBatches]
        SET [State]=N'Rejected',[Message]=N'Discarded without changing Client Information.',
            [UpdatedAtUtc]=SYSUTCDATETIME()
        WHERE [BatchId]=@BatchId;

        SELECT TOP(1)
            @NextBatchId=[BatchId],@NextBatchState=[State]
        FROM [tb_import].[ClientInfoBatches]
        WHERE [ClientId]=@ClientId AND [BatchId]<>@BatchId
          AND [State] IN
              (N'Draft',N'Parsed',N'Validated',N'InReview',N'ValidationFailed',N'Approved')
        ORDER BY [CreatedAtUtc] DESC,[BatchId] DESC;

        UPDATE [tb_ops].[ClientInfoCutovers]
        SET [ActiveBatchId]=@NextBatchId,
            [State]=CASE
                WHEN @NextBatchId IS NULL THEN N'NotStarted'
                WHEN @NextBatchState=N'Approved' THEN N'Ready'
                ELSE N'Staging' END,
            [UpdatedByWindowsSid]=@ActorSid,[UpdatedAtUtc]=SYSUTCDATETIME()
        WHERE [ClientId]=@ClientId AND [ActiveBatchId]=@BatchId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    DECLARE @AuditEntityId nvarchar(120)=CONVERT(nvarchar(120),@BatchId);
    EXEC [tb_security].[WriteAuditEvent]
        @Action=N'ClientInfoImportDiscarded',
        @EntityType=N'ClientInfoImportBatch',
        @EntityId=@AuditEntityId,@RequestId=@RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.ApproveClientInfoImport', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ApproveClientInfoImport];
GO

CREATE PROCEDURE [tb_app].[ApproveClientInfoImport]
    @BatchId uniqueidentifier,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ActorSid varbinary(85),@IsManager bit,@IsAdmin bit,@IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,@IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,@IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1
        THROW 52450,N'Only a TechBench Admin may approve a Client Info import.',1;
    IF EXISTS
        (SELECT 1 FROM [tb_import].[ClientInfoIssues]
         WHERE [BatchId]=@BatchId AND [Severity]=N'Error' AND [IsResolved]=0)
        THROW 52451,N'Blocking import issues must be resolved before approval.',1;
    IF EXISTS
        (SELECT 1 FROM [tb_import].[ClientInfoRecords]
         WHERE [BatchId]=@BatchId AND [ReviewStatus] IN (N'Unverified',N'NeedsReview'))
        THROW 52452,N'Every unverified row must be verified, accepted, or rejected before approval.',1;

    UPDATE [tb_import].[ClientInfoBatches]
    SET [State]=N'Approved',[Message]=N'Approved and ready for promotion.',
        [ApprovedByWindowsSid]=@ActorSid,[ApprovedAtUtc]=SYSUTCDATETIME(),
        [UpdatedAtUtc]=SYSUTCDATETIME()
    WHERE [BatchId]=@BatchId AND [State]=N'InReview'
      AND [RowVersion]=@ExpectedRowVersion;
    IF @@ROWCOUNT<>1
        THROW 52453,N'The import batch changed or is not ready for approval.',1;

    DECLARE @AuditEntityId nvarchar(120) =
        CONVERT(nvarchar(120), @BatchId);
    EXEC [tb_security].[WriteAuditEvent]
        @Action=N'ClientInfoImportApproved',
        @EntityType=N'ClientInfoImportBatch',
        @EntityId=@AuditEntityId,@RequestId=@RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.PromoteClientInfoImport', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[PromoteClientInfoImport];
GO

CREATE PROCEDURE [tb_app].[PromoteClientInfoImport]
    @BatchId uniqueidentifier,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ClientId int,@ActorSid varbinary(85)=SUSER_SID(ORIGINAL_LOGIN()),
            @NowUtc datetime2(3)=SYSUTCDATETIME();
    SELECT @ClientId=[ClientId]
    FROM [tb_import].[ClientInfoBatches] WITH (UPDLOCK,HOLDLOCK)
    WHERE [BatchId]=@BatchId AND [State]=N'Approved'
      AND [RowVersion]=@ExpectedRowVersion;
    IF @ClientId IS NULL
        THROW 52460,N'The import batch changed or is not approved for promotion.',1;

    BEGIN TRY
        BEGIN TRANSACTION;

        DECLARE @ProfileSummary nvarchar(2000),@ProfileReview nvarchar(24);
        SELECT TOP(1)
            @ProfileSummary=JSON_VALUE([PayloadJson],N'$.summary'),
            @ProfileReview=[ReviewStatus]
        FROM [tb_import].[ClientInfoRecords]
        WHERE [BatchId]=@BatchId AND [RecordType]=N'Profile'
          AND [ReviewStatus]<>N'Rejected'
        ORDER BY [ImportRecordId];

        IF EXISTS (SELECT 1 FROM [tb_client].[ClientProfiles] WHERE [ClientId]=@ClientId)
            UPDATE [tb_client].[ClientProfiles]
            SET [Summary]=NULLIF(@ProfileSummary,N''),
                [ReviewStatus]=COALESCE(@ProfileReview,N'AcceptedUnverified'),
                [UpdatedByWindowsSid]=@ActorSid,[UpdatedAtUtc]=@NowUtc
            WHERE [ClientId]=@ClientId;
        ELSE
            INSERT INTO [tb_client].[ClientProfiles]
            (
                [ClientId],[Summary],[ReviewStatus],[CreatedByWindowsSid],
                [UpdatedByWindowsSid],[CreatedAtUtc],[UpdatedAtUtc]
            )
            VALUES
            (
                @ClientId,NULLIF(@ProfileSummary,N''),
                COALESCE(@ProfileReview,N'AcceptedUnverified'),
                @ActorSid,@ActorSid,@NowUtc,@NowUtc
            );

        DECLARE @ImportRecordId bigint,@RecordType nvarchar(40),
                @LocalKey nvarchar(120),@ParentLocalKey nvarchar(120),
                @PayloadJson nvarchar(max),@ReviewStatus nvarchar(24),
                @EntityId bigint,@LocationId bigint,@PersonId bigint,
                @ResourceId bigint;
        DECLARE record_cursor CURSOR LOCAL FAST_FORWARD FOR
            SELECT [ImportRecordId],[RecordType],[LocalKey],[ParentLocalKey],
                   [PayloadJson],[ReviewStatus]
            FROM [tb_import].[ClientInfoRecords]
            WHERE [BatchId]=@BatchId AND [RecordType]<>N'Profile'
              AND [ReviewStatus]<>N'Rejected'
            ORDER BY CASE [RecordType]
                WHEN N'Location' THEN 1 WHEN N'Person' THEN 2
                WHEN N'Resource' THEN 3 WHEN N'ResourceField' THEN 4
                WHEN N'Credential' THEN 5 WHEN N'Fact' THEN 6
                WHEN N'Equipment' THEN 7 ELSE 9 END,
                [ImportRecordId];
        OPEN record_cursor;
        FETCH NEXT FROM record_cursor INTO
            @ImportRecordId,@RecordType,@LocalKey,@ParentLocalKey,@PayloadJson,@ReviewStatus;
        WHILE @@FETCH_STATUS=0
        BEGIN
            SET @EntityId=NULL;
            IF @RecordType=N'Location'
            BEGIN
                SELECT @EntityId=[LocationId]
                FROM [tb_client].[Locations]
                WHERE [ClientId]=@ClientId AND [LocalKey]=@LocalKey;
                IF @EntityId IS NULL
                BEGIN
                    INSERT INTO [tb_client].[Locations]
                    (
                        [ClientId],[LocalKey],[Name],[LocationType],[Address1],
                        [Address2],[City],[StateProvince],[PostalCode],[MainPhone],
                        [TimeZoneId],[IsPrimary],[ReviewStatus],[CreatedByWindowsSid],
                        [UpdatedByWindowsSid],[CreatedAtUtc],[UpdatedAtUtc]
                    )
                    VALUES
                    (
                        @ClientId,@LocalKey,JSON_VALUE(@PayloadJson,N'$.name'),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.locationType'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.address1'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.address2'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.city'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.stateProvince'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.postalCode'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.mainPhone'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.timeZoneId'),N''),
                        COALESCE(TRY_CONVERT(bit,JSON_VALUE(@PayloadJson,N'$.isPrimary')),0),
                        @ReviewStatus,@ActorSid,@ActorSid,@NowUtc,@NowUtc
                    );
                    SET @EntityId=CONVERT(bigint,SCOPE_IDENTITY());
                END;
                ELSE
                    UPDATE [tb_client].[Locations]
                    SET
                        [Name]=JSON_VALUE(@PayloadJson,N'$.name'),
                        [LocationType]=NULLIF(JSON_VALUE(@PayloadJson,N'$.locationType'),N''),
                        [Address1]=NULLIF(JSON_VALUE(@PayloadJson,N'$.address1'),N''),
                        [Address2]=NULLIF(JSON_VALUE(@PayloadJson,N'$.address2'),N''),
                        [City]=NULLIF(JSON_VALUE(@PayloadJson,N'$.city'),N''),
                        [StateProvince]=NULLIF(JSON_VALUE(@PayloadJson,N'$.stateProvince'),N''),
                        [PostalCode]=NULLIF(JSON_VALUE(@PayloadJson,N'$.postalCode'),N''),
                        [MainPhone]=NULLIF(JSON_VALUE(@PayloadJson,N'$.mainPhone'),N''),
                        [TimeZoneId]=NULLIF(JSON_VALUE(@PayloadJson,N'$.timeZoneId'),N''),
                        [IsPrimary]=COALESCE(
                            TRY_CONVERT(bit,JSON_VALUE(@PayloadJson,N'$.isPrimary')),
                            0),
                        [ReviewStatus]=@ReviewStatus,
                        [IsActive]=COALESCE(
                            TRY_CONVERT(bit,JSON_VALUE(@PayloadJson,N'$.isActive')),
                            1),
                        [LastVerifiedAtUtc]=CASE
                            WHEN @ReviewStatus=N'Verified' THEN @NowUtc
                            ELSE [LastVerifiedAtUtc] END,
                        [UpdatedByWindowsSid]=@ActorSid,
                        [UpdatedAtUtc]=@NowUtc
                    WHERE [LocationId]=@EntityId;
            END
            ELSE IF @RecordType=N'Person'
            BEGIN
                SET @LocationId=NULL;
                IF @ParentLocalKey IS NOT NULL
                    SELECT @LocationId=[EntityId]
                    FROM [tb_import].[ClientInfoPromotionMap] map
                    INNER JOIN [tb_import].[ClientInfoRecords] source_record
                        ON source_record.[ImportRecordId]=map.[ImportRecordId]
                    WHERE source_record.[BatchId]=@BatchId
                      AND source_record.[RecordType]=N'Location'
                      AND source_record.[LocalKey]=@ParentLocalKey;
                SELECT @EntityId=[PersonId] FROM [tb_client].[People]
                WHERE [ClientId]=@ClientId AND [LocalKey]=@LocalKey;
                IF @EntityId IS NULL
                BEGIN
                    INSERT INTO [tb_client].[People]
                    (
                        [ClientId],[LocationId],[LocalKey],[DisplayName],
                        [RoleDepartment],[AdUsername],[Email],[HasMicrosoft365],
                        [Microsoft365License],[PcName],[Phone],[MobilePhone],[ContactType],
                        [IsPrimary],[ReviewStatus],[CreatedByWindowsSid],
                        [UpdatedByWindowsSid],[CreatedAtUtc],[UpdatedAtUtc]
                    )
                    VALUES
                    (
                        @ClientId,@LocationId,@LocalKey,
                        JSON_VALUE(@PayloadJson,N'$.displayName'),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.roleDepartment'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.adUsername'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.email'),N''),
                        COALESCE(TRY_CONVERT(bit,JSON_VALUE(@PayloadJson,N'$.hasMicrosoft365')),0),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.microsoft365License'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.pcName'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.phone'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.mobilePhone'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.contactType'),N''),
                        COALESCE(TRY_CONVERT(bit,JSON_VALUE(@PayloadJson,N'$.isPrimary')),0),
                        @ReviewStatus,@ActorSid,@ActorSid,@NowUtc,@NowUtc
                    );
                    SET @EntityId=CONVERT(bigint,SCOPE_IDENTITY());
                END;
                ELSE
                    UPDATE [tb_client].[People]
                    SET
                        [LocationId]=@LocationId,
                        [DisplayName]=JSON_VALUE(@PayloadJson,N'$.displayName'),
                        [RoleDepartment]=NULLIF(JSON_VALUE(@PayloadJson,N'$.roleDepartment'),N''),
                        [AdUsername]=NULLIF(JSON_VALUE(@PayloadJson,N'$.adUsername'),N''),
                        [Email]=NULLIF(JSON_VALUE(@PayloadJson,N'$.email'),N''),
                        [HasMicrosoft365]=COALESCE(
                            TRY_CONVERT(bit,JSON_VALUE(@PayloadJson,N'$.hasMicrosoft365')),
                            0),
                        [Microsoft365License]=NULLIF(
                            JSON_VALUE(@PayloadJson,N'$.microsoft365License'),N''),
                        [PcName]=NULLIF(JSON_VALUE(@PayloadJson,N'$.pcName'),N''),
                        [Phone]=NULLIF(JSON_VALUE(@PayloadJson,N'$.phone'),N''),
                        [MobilePhone]=NULLIF(JSON_VALUE(@PayloadJson,N'$.mobilePhone'),N''),
                        [ContactType]=NULLIF(JSON_VALUE(@PayloadJson,N'$.contactType'),N''),
                        [IsPrimary]=COALESCE(
                            TRY_CONVERT(bit,JSON_VALUE(@PayloadJson,N'$.isPrimary')),
                            0),
                        [ReviewStatus]=@ReviewStatus,
                        [IsActive]=COALESCE(
                            TRY_CONVERT(bit,JSON_VALUE(@PayloadJson,N'$.isActive')),
                            1),
                        [LastVerifiedAtUtc]=CASE
                            WHEN @ReviewStatus=N'Verified' THEN @NowUtc
                            ELSE [LastVerifiedAtUtc] END,
                        [UpdatedByWindowsSid]=@ActorSid,
                        [UpdatedAtUtc]=@NowUtc
                    WHERE [PersonId]=@EntityId;
            END
            ELSE IF @RecordType=N'Resource'
            BEGIN
                SET @LocationId=NULL; SET @ResourceId=NULL;
                SELECT @LocationId=[LocationId] FROM [tb_client].[Locations]
                WHERE [ClientId]=@ClientId
                  AND [LocalKey]=JSON_VALUE(@PayloadJson,N'$.locationKey');
                IF @ParentLocalKey IS NOT NULL
                    SELECT @ResourceId=[ResourceId] FROM [tb_client].[Resources]
                    WHERE [ClientId]=@ClientId AND [LocalKey]=@ParentLocalKey;
                SELECT @EntityId=[ResourceId] FROM [tb_client].[Resources]
                WHERE [ClientId]=@ClientId AND [LocalKey]=@LocalKey;
                IF @EntityId IS NULL
                BEGIN
                    INSERT INTO [tb_client].[Resources]
                    (
                        [ClientId],[LocationId],[ParentResourceId],[LocalKey],
                        [ResourceType],[Name],[Provider],[AddressOrUrl],[Status],
                        [Notes],[ReviewStatus],[CreatedByWindowsSid],
                        [UpdatedByWindowsSid],[CreatedAtUtc],[UpdatedAtUtc]
                    )
                    VALUES
                    (
                        @ClientId,@LocationId,@ResourceId,@LocalKey,
                        COALESCE(NULLIF(JSON_VALUE(@PayloadJson,N'$.resourceType'),N''),N'Other'),
                        JSON_VALUE(@PayloadJson,N'$.name'),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.provider'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.addressOrUrl'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.status'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.notes'),N''),
                        @ReviewStatus,@ActorSid,@ActorSid,@NowUtc,@NowUtc
                    );
                    SET @EntityId=CONVERT(bigint,SCOPE_IDENTITY());
                END;
                ELSE
                    UPDATE [tb_client].[Resources]
                    SET
                        [LocationId]=@LocationId,
                        [ParentResourceId]=@ResourceId,
                        [ResourceType]=COALESCE(
                            NULLIF(JSON_VALUE(@PayloadJson,N'$.resourceType'),N''),
                            N'Other'),
                        [Name]=JSON_VALUE(@PayloadJson,N'$.name'),
                        [Provider]=NULLIF(JSON_VALUE(@PayloadJson,N'$.provider'),N''),
                        [AddressOrUrl]=NULLIF(JSON_VALUE(@PayloadJson,N'$.addressOrUrl'),N''),
                        [Status]=NULLIF(JSON_VALUE(@PayloadJson,N'$.status'),N''),
                        [Notes]=NULLIF(JSON_VALUE(@PayloadJson,N'$.notes'),N''),
                        [ReviewStatus]=@ReviewStatus,
                        [IsActive]=COALESCE(
                            TRY_CONVERT(bit,JSON_VALUE(@PayloadJson,N'$.isActive')),
                            1),
                        [LastVerifiedAtUtc]=CASE
                            WHEN @ReviewStatus=N'Verified' THEN @NowUtc
                            ELSE [LastVerifiedAtUtc] END,
                        [UpdatedByWindowsSid]=@ActorSid,
                        [UpdatedAtUtc]=@NowUtc
                    WHERE [ResourceId]=@EntityId;
            END
            ELSE IF @RecordType=N'ResourceField'
            BEGIN
                SET @ResourceId=NULL;
                SELECT @ResourceId=[ResourceId]
                FROM [tb_client].[Resources]
                WHERE [ClientId]=@ClientId AND [LocalKey]=@ParentLocalKey;
                IF @ResourceId IS NULL
                    THROW 52463,N'A staged resource field could not be linked to its resource.',1;

                SELECT @EntityId=[ResourceFieldId]
                FROM [tb_client].[ResourceFields]
                WHERE [ResourceId]=@ResourceId
                  AND [FieldKey]=JSON_VALUE(@PayloadJson,N'$.fieldKey');
                IF @EntityId IS NULL
                BEGIN
                    INSERT INTO [tb_client].[ResourceFields]
                    (
                        [ResourceId],[FieldKey],[FieldLabel],[ValueText],
                        [ValueType],[SortOrder],[UpdatedByWindowsSid],[UpdatedAtUtc]
                    )
                    VALUES
                    (
                        @ResourceId,
                        JSON_VALUE(@PayloadJson,N'$.fieldKey'),
                        JSON_VALUE(@PayloadJson,N'$.fieldLabel'),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.valueText'),N''),
                        COALESCE(NULLIF(JSON_VALUE(@PayloadJson,N'$.valueType'),N''),N'Text'),
                        COALESCE(TRY_CONVERT(int,JSON_VALUE(@PayloadJson,N'$.sortOrder')),0),
                        @ActorSid,@NowUtc
                    );
                    SET @EntityId=CONVERT(bigint,SCOPE_IDENTITY());
                END
                ELSE
                    UPDATE [tb_client].[ResourceFields]
                    SET
                        [FieldLabel]=JSON_VALUE(@PayloadJson,N'$.fieldLabel'),
                        [ValueText]=NULLIF(JSON_VALUE(@PayloadJson,N'$.valueText'),N''),
                        [ValueType]=COALESCE(
                            NULLIF(JSON_VALUE(@PayloadJson,N'$.valueType'),N''),
                            N'Text'),
                        [SortOrder]=COALESCE(
                            TRY_CONVERT(int,JSON_VALUE(@PayloadJson,N'$.sortOrder')),
                            0),
                        [UpdatedByWindowsSid]=@ActorSid,
                        [UpdatedAtUtc]=@NowUtc
                    WHERE [ResourceFieldId]=@EntityId;
            END
            ELSE IF @RecordType=N'Credential'
            BEGIN
                SET @ResourceId=NULL; SET @PersonId=NULL;
                SELECT @ResourceId=[ResourceId] FROM [tb_client].[Resources]
                WHERE [ClientId]=@ClientId
                  AND [LocalKey]=JSON_VALUE(@PayloadJson,N'$.resourceKey');
                SELECT @PersonId=[PersonId] FROM [tb_client].[People]
                WHERE [ClientId]=@ClientId
                  AND [LocalKey]=JSON_VALUE(@PayloadJson,N'$.personKey');
                SELECT @EntityId=[CredentialId] FROM [tb_client].[Credentials]
                WHERE [ClientId]=@ClientId AND [LocalKey]=@LocalKey;
                IF @EntityId IS NULL
                BEGIN
                    INSERT INTO [tb_client].[Credentials]
                    (
                        [ClientId],[ResourceId],[PersonId],[LocalKey],[Name],
                        [Category],[Username],[LoginUrl],[Notes],[ReviewStatus],
                        [CreatedByWindowsSid],[UpdatedByWindowsSid],
                        [CreatedAtUtc],[UpdatedAtUtc]
                    )
                    VALUES
                    (
                        @ClientId,@ResourceId,@PersonId,@LocalKey,
                        JSON_VALUE(@PayloadJson,N'$.name'),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.category'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.username'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.loginUrl'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.notes'),N''),
                        @ReviewStatus,@ActorSid,@ActorSid,@NowUtc,@NowUtc
                    );
                    SET @EntityId=CONVERT(bigint,SCOPE_IDENTITY());
                END;
                ELSE
                    UPDATE [tb_client].[Credentials]
                    SET
                        [ResourceId]=@ResourceId,
                        [PersonId]=@PersonId,
                        [Name]=JSON_VALUE(@PayloadJson,N'$.name'),
                        [Category]=NULLIF(JSON_VALUE(@PayloadJson,N'$.category'),N''),
                        [Username]=NULLIF(JSON_VALUE(@PayloadJson,N'$.username'),N''),
                        [LoginUrl]=NULLIF(JSON_VALUE(@PayloadJson,N'$.loginUrl'),N''),
                        [Notes]=NULLIF(JSON_VALUE(@PayloadJson,N'$.notes'),N''),
                        [ReviewStatus]=@ReviewStatus,
                        [IsActive]=1,
                        [LastVerifiedAtUtc]=CASE
                            WHEN @ReviewStatus=N'Verified' THEN @NowUtc
                            ELSE [LastVerifiedAtUtc] END,
                        [UpdatedByWindowsSid]=@ActorSid,
                        [UpdatedAtUtc]=@NowUtc
                    WHERE [CredentialId]=@EntityId;
            END
            ELSE IF @RecordType=N'Fact'
            BEGIN
                SELECT @EntityId=[FactId] FROM [tb_client].[ClientFacts]
                WHERE [ClientId]=@ClientId AND [LocalKey]=@LocalKey;
                IF @EntityId IS NULL
                BEGIN
                    INSERT INTO [tb_client].[ClientFacts]
                    (
                        [ClientId],[LocalKey],[SectionName],[FieldLabel],[ValueText],
                        [ValueType],[ReviewStatus],[SortOrder],[CreatedByWindowsSid],
                        [UpdatedByWindowsSid],[CreatedAtUtc],[UpdatedAtUtc]
                    )
                    VALUES
                    (
                        @ClientId,@LocalKey,
                        JSON_VALUE(@PayloadJson,N'$.sectionName'),
                        JSON_VALUE(@PayloadJson,N'$.fieldLabel'),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.valueText'),N''),
                        COALESCE(NULLIF(JSON_VALUE(@PayloadJson,N'$.valueType'),N''),N'Text'),
                        @ReviewStatus,
                        COALESCE(TRY_CONVERT(int,JSON_VALUE(@PayloadJson,N'$.sortOrder')),0),
                        @ActorSid,@ActorSid,@NowUtc,@NowUtc
                    );
                    SET @EntityId=CONVERT(bigint,SCOPE_IDENTITY());
                END;
                ELSE
                    UPDATE [tb_client].[ClientFacts]
                    SET
                        [SectionName]=JSON_VALUE(@PayloadJson,N'$.sectionName'),
                        [FieldLabel]=JSON_VALUE(@PayloadJson,N'$.fieldLabel'),
                        [ValueText]=NULLIF(JSON_VALUE(@PayloadJson,N'$.valueText'),N''),
                        [ValueType]=COALESCE(
                            NULLIF(JSON_VALUE(@PayloadJson,N'$.valueType'),N''),
                            N'Text'),
                        [ReviewStatus]=@ReviewStatus,
                        [SortOrder]=COALESCE(
                            TRY_CONVERT(int,JSON_VALUE(@PayloadJson,N'$.sortOrder')),
                            0),
                        [IsActive]=COALESCE(
                            TRY_CONVERT(bit,JSON_VALUE(@PayloadJson,N'$.isActive')),
                            1),
                        [LastVerifiedAtUtc]=CASE
                            WHEN @ReviewStatus=N'Verified' THEN @NowUtc
                            ELSE [LastVerifiedAtUtc] END,
                        [UpdatedByWindowsSid]=@ActorSid,
                        [UpdatedAtUtc]=@NowUtc
                    WHERE [FactId]=@EntityId;
            END
            ELSE IF @RecordType=N'Equipment'
            BEGIN
                SELECT @EntityId=[EquipmentId]
                FROM [tb_inventory].[Equipment]
                WHERE [ClientId]=@ClientId
                  AND [ClientInfoLocalKey]=@LocalKey;
                IF @EntityId IS NULL
                BEGIN
                    INSERT INTO [tb_inventory].[Equipment]
                    (
                        [AssetTag],[DeviceType],[Name],[SerialNumber],[PartNumber],
                        [IpAddress],[Manufacturer],[Model],[ClientId],[ClientName],
                        [LocationName],[Notes],[WorkflowStage],[ClientInfoLocalKey],
                        [CreatedByWindowsSid],[UpdatedByWindowsSid],
                        [CreatedAtUtc],[UpdatedAtUtc]
                    )
                    SELECT
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.assetTag'),N''),
                        COALESCE(NULLIF(JSON_VALUE(@PayloadJson,N'$.deviceType'),N''),N'Other'),
                        JSON_VALUE(@PayloadJson,N'$.name'),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.serialNumber'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.partNumber'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.ipAddress'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.manufacturer'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.model'),N''),
                        @ClientId,client.[Name],
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.locationName'),N''),
                        NULLIF(JSON_VALUE(@PayloadJson,N'$.notes'),N''),
                        N'Deployed',@LocalKey,
                        @ActorSid,@ActorSid,@NowUtc,@NowUtc
                    FROM [tb_data].[Clients] client
                    WHERE client.[Id]=@ClientId;
                    SET @EntityId=CONVERT(bigint,SCOPE_IDENTITY());
                END;
                ELSE
                    UPDATE equipment
                    SET
                        [AssetTag]=NULLIF(JSON_VALUE(@PayloadJson,N'$.assetTag'),N''),
                        [DeviceType]=COALESCE(
                            NULLIF(JSON_VALUE(@PayloadJson,N'$.deviceType'),N''),
                            N'Other'),
                        [Name]=JSON_VALUE(@PayloadJson,N'$.name'),
                        [SerialNumber]=NULLIF(JSON_VALUE(@PayloadJson,N'$.serialNumber'),N''),
                        [PartNumber]=NULLIF(JSON_VALUE(@PayloadJson,N'$.partNumber'),N''),
                        [IpAddress]=NULLIF(JSON_VALUE(@PayloadJson,N'$.ipAddress'),N''),
                        [Manufacturer]=NULLIF(JSON_VALUE(@PayloadJson,N'$.manufacturer'),N''),
                        [Model]=NULLIF(JSON_VALUE(@PayloadJson,N'$.model'),N''),
                        [ClientName]=client.[Name],
                        [LocationName]=NULLIF(JSON_VALUE(@PayloadJson,N'$.locationName'),N''),
                        [Notes]=NULLIF(JSON_VALUE(@PayloadJson,N'$.notes'),N''),
                        [UpdatedByWindowsSid]=@ActorSid,
                        [UpdatedAtUtc]=@NowUtc
                    FROM [tb_inventory].[Equipment] equipment
                    INNER JOIN [tb_data].[Clients] client
                        ON client.[Id]=equipment.[ClientId]
                    WHERE equipment.[EquipmentId]=@EntityId;
            END;

            IF @EntityId IS NOT NULL
            BEGIN
                IF NOT EXISTS
                    (SELECT 1 FROM [tb_import].[ClientInfoPromotionMap]
                     WHERE [ImportRecordId]=@ImportRecordId)
                    INSERT INTO [tb_import].[ClientInfoPromotionMap]
                        ([ImportRecordId],[EntityType],[EntityId])
                    VALUES (@ImportRecordId,@RecordType,@EntityId);

                INSERT INTO [tb_client].[RecordProvenance]
                (
                    [ClientId],[EntityType],[EntityId],[SourceDocumentId],
                    [SourceSheet],[SourceAddress],[ReviewStatus],
                    [RecordedAtUtc],[RecordedByWindowsSid]
                )
                SELECT @ClientId,@RecordType,@EntityId,batch.[SourceDocumentId],
                    source_record.[SourceSheet],
                    CASE WHEN source_record.[SourceRow] IS NULL THEN NULL
                        ELSE N'Row '+CONVERT(nvarchar(20),source_record.[SourceRow]) END,
                    @ReviewStatus,@NowUtc,@ActorSid
                FROM [tb_import].[ClientInfoBatches] batch
                INNER JOIN [tb_import].[ClientInfoRecords] source_record
                    ON source_record.[BatchId]=batch.[BatchId]
                WHERE batch.[BatchId]=@BatchId
                  AND source_record.[ImportRecordId]=@ImportRecordId;
            END;

            FETCH NEXT FROM record_cursor INTO
                @ImportRecordId,@RecordType,@LocalKey,@ParentLocalKey,@PayloadJson,@ReviewStatus;
        END;
        CLOSE record_cursor;
        DEALLOCATE record_cursor;

        DECLARE @ImportSecretId bigint,@CredentialLocalKey nvarchar(120),
                @SecretType nvarchar(80),@SecretLabel nvarchar(200),
                @EncryptedValue varbinary(max),@CanonicalCredentialId bigint,
                @CanonicalSecretId bigint,@ClearValue varbinary(max),
                @ImportAuthenticator varbinary(32),@CanonicalAuthenticator varbinary(32);
        OPEN SYMMETRIC KEY [tb_ClientSecretKey]
            DECRYPTION BY CERTIFICATE [tb_ClientSecretCertificate];
        DECLARE secret_cursor CURSOR LOCAL FAST_FORWARD FOR
            SELECT [ImportSecretId],[CredentialLocalKey],[SecretType],
                   [SecretLabel],[ValueEncrypted]
            FROM [tb_import].[ClientInfoSecrets]
            WHERE [BatchId]=@BatchId AND COALESCE([Resolution],N'UseWorkbook')<>N'Rejected';
        OPEN secret_cursor;
        FETCH NEXT FROM secret_cursor INTO
            @ImportSecretId,@CredentialLocalKey,@SecretType,@SecretLabel,@EncryptedValue;
        WHILE @@FETCH_STATUS=0
        BEGIN
            SELECT @CanonicalCredentialId=credential.[CredentialId]
            FROM [tb_client].[Credentials] credential
            WHERE credential.[ClientId]=@ClientId
              AND credential.[LocalKey]=@CredentialLocalKey;
            IF @CanonicalCredentialId IS NULL
                THROW 52461,N'A staged secret could not be linked to its promoted credential.',1;

            SET @ImportAuthenticator=HASHBYTES(
                N'SHA2_256',CONVERT(varbinary(max),
                    N'ClientImportSecret|'+CONVERT(nvarchar(30),@ImportSecretId)));
            SET @ClearValue=DecryptByKey(@EncryptedValue,1,@ImportAuthenticator);
            IF @ClearValue IS NULL
                THROW 52462,N'A staged secret could not be decrypted for promotion.',1;

            SELECT @CanonicalSecretId=[SecretId]
            FROM [tb_client].[CredentialSecrets]
            WHERE [CredentialId]=@CanonicalCredentialId
              AND [SecretType]=@SecretType AND [SecretLabel]=@SecretLabel;
            IF @CanonicalSecretId IS NULL
            BEGIN
                INSERT INTO [tb_client].[CredentialSecrets]
                (
                    [CredentialId],[SecretType],[SecretLabel],[ValueEncrypted],
                    [CreatedByWindowsSid],[UpdatedByWindowsSid],
                    [CreatedAtUtc],[UpdatedAtUtc]
                )
                VALUES
                (
                    @CanonicalCredentialId,@SecretType,@SecretLabel,0x,
                    @ActorSid,@ActorSid,@NowUtc,@NowUtc
                );
                SET @CanonicalSecretId=CONVERT(bigint,SCOPE_IDENTITY());
            END;
            SET @CanonicalAuthenticator=HASHBYTES(
                N'SHA2_256',CONVERT(varbinary(max),
                    N'ClientSecret|'+CONVERT(nvarchar(30),@CanonicalSecretId)));
            UPDATE [tb_client].[CredentialSecrets]
            SET [ValueEncrypted]=EncryptByKey(
                    Key_GUID(N'tb_ClientSecretKey'),@ClearValue,1,@CanonicalAuthenticator),
                [UpdatedByWindowsSid]=@ActorSid,[UpdatedAtUtc]=@NowUtc
            WHERE [SecretId]=@CanonicalSecretId;

            SET @CanonicalCredentialId=NULL; SET @CanonicalSecretId=NULL;
            FETCH NEXT FROM secret_cursor INTO
                @ImportSecretId,@CredentialLocalKey,@SecretType,@SecretLabel,@EncryptedValue;
        END;
        CLOSE secret_cursor;
        DEALLOCATE secret_cursor;
        CLOSE SYMMETRIC KEY [tb_ClientSecretKey];

        UPDATE [tb_client].[ClientProfiles]
        SET [IsLive]=1,[UpdatedByWindowsSid]=@ActorSid,[UpdatedAtUtc]=@NowUtc
        WHERE [ClientId]=@ClientId;
        UPDATE [tb_import].[ClientInfoBatches]
        SET [State]=N'Promoted',[Message]=N'Promoted to canonical Client Info.',
            [PromotedAtUtc]=@NowUtc,[UpdatedAtUtc]=@NowUtc
        WHERE [BatchId]=@BatchId;
        UPDATE [tb_ops].[ClientInfoCutovers]
        SET [State]=N'Complete',[LiveAtUtc]=COALESCE([LiveAtUtc],@NowUtc),
            [HypercareEndsAtUtc]=NULL,
            [UpdatedByWindowsSid]=@ActorSid,[UpdatedAtUtc]=@NowUtc
        WHERE [ClientId]=@ClientId;

        DECLARE @AuditEntityId nvarchar(120) =
            CONVERT(nvarchar(120), @BatchId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action=N'ClientInfoImportPromoted',
            @EntityType=N'ClientInfoImportBatch',
            @EntityId=@AuditEntityId,@RequestId=@RequestId,
            @DataJson=N'{"containsSecretValues":false}';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF EXISTS
            (SELECT 1 FROM sys.openkeys WHERE [key_name]=N'tb_ClientSecretKey')
            CLOSE SYMMETRIC KEY [tb_ClientSecretKey];
        IF CURSOR_STATUS(N'local',N'record_cursor')>=-1
        BEGIN
            IF CURSOR_STATUS(N'local',N'record_cursor')>0 CLOSE record_cursor;
            DEALLOCATE record_cursor;
        END;
        IF CURSOR_STATUS(N'local',N'secret_cursor')>=-1
        BEGIN
            IF CURSOR_STATUS(N'local',N'secret_cursor')>0 CLOSE secret_cursor;
            DEALLOCATE secret_cursor;
        END;
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

PRINT N'Client Info beta import procedures installed.';
GO
