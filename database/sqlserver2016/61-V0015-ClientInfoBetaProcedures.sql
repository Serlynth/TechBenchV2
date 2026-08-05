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
        @UserSid=@UserSid OUTPUT,
        @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;

    SELECT
        CONVERT(int, 15) AS [SchemaVersion],
        CONVERT(bit, 0) AS [FullTextSearchAvailable],
        CONVERT(bit, 1) AS [SupportsTickets],
        CONVERT(bit, 1) AS [SupportsWorkEntries],
        CONVERT(bit, 1) AS [SupportsPrivateNotes],
        CONVERT(bit, 1) AS [SupportsPostingLeases],
        CONVERT(bit, 1) AS [SupportsSyncLeases],
        CONVERT(bit, 1) AS [SupportsImports],
        CONVERT(bit, 1) AS [SupportsTechBenchV1Import],
        CONVERT(bit, 1) AS [SupportsServerSageSync],
        CONVERT(bit, 1) AS [SupportsAdminUserPreview],
        CONVERT(bit, 1) AS [SupportsFireDrillCredentials],
        CONVERT(bit, 1) AS [EquipmentBoardAvailable],
        CONVERT(bit, 1) AS [ClientInfoBetaAvailable];
END;
GO

IF OBJECT_ID(N'tb_client.ReparentClientGraph', N'P') IS NOT NULL
    DROP PROCEDURE [tb_client].[ReparentClientGraph];
GO

CREATE PROCEDURE [tb_client].[ReparentClientGraph]
    @SourceClientId int,
    @TargetClientId int,
    @ActorWindowsSid varbinary(85)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @SourceClientId IS NULL OR @TargetClientId IS NULL
       OR @SourceClientId = @TargetClientId
        THROW 52300, N'A distinct source and target client are required.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_data].[Clients] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Id] = @SourceClientId
    )
        THROW 52301, N'The source client no longer exists.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_data].[Clients] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Id] = @TargetClientId
    )
        THROW 52302, N'The target client no longer exists.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    IF EXISTS
    (
        SELECT 1 FROM [tb_client].[ClientProfiles]
        WHERE [ClientId] = @SourceClientId
    )
    BEGIN
        IF EXISTS
        (
            SELECT 1 FROM [tb_client].[ClientProfiles]
            WHERE [ClientId] = @TargetClientId
        )
        BEGIN
            UPDATE target_profile
            SET
                [Summary] = COALESCE(
                    NULLIF(target_profile.[Summary], N''),
                    source_profile.[Summary]),
                [ReviewStatus] =
                    CASE
                        WHEN target_profile.[ReviewStatus] = N'Verified'
                            THEN target_profile.[ReviewStatus]
                        ELSE source_profile.[ReviewStatus]
                    END,
                [IsLive] =
                    CONVERT(bit, CASE
                        WHEN target_profile.[IsLive] = 1 OR source_profile.[IsLive] = 1
                            THEN 1 ELSE 0 END),
                [LastVerifiedAtUtc] = COALESCE(
                    target_profile.[LastVerifiedAtUtc],
                    source_profile.[LastVerifiedAtUtc]),
                [LastVerifiedByWindowsSid] = COALESCE(
                    target_profile.[LastVerifiedByWindowsSid],
                    source_profile.[LastVerifiedByWindowsSid]),
                [UpdatedByWindowsSid] = @ActorWindowsSid,
                [UpdatedAtUtc] = @NowUtc
            FROM [tb_client].[ClientProfiles] AS target_profile
            CROSS JOIN [tb_client].[ClientProfiles] AS source_profile
            WHERE target_profile.[ClientId] = @TargetClientId
              AND source_profile.[ClientId] = @SourceClientId;

            DELETE FROM [tb_client].[ClientProfiles]
            WHERE [ClientId] = @SourceClientId;
        END
        ELSE
        BEGIN
            UPDATE [tb_client].[ClientProfiles]
            SET
                [ClientId] = @TargetClientId,
                [UpdatedByWindowsSid] = @ActorWindowsSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [ClientId] = @SourceClientId;
        END;
    END;

    UPDATE [tb_inventory].[ClientUsers]
    SET
        [ClientId] = @TargetClientId,
        [UpdatedByWindowsSid] = @ActorWindowsSid,
        [UpdatedAtUtc] = @NowUtc
    WHERE [ClientId] = @SourceClientId;

    UPDATE source_equipment
    SET
        [ClientId] = @TargetClientId,
        [UpdatedByWindowsSid] = @ActorWindowsSid,
        [UpdatedAtUtc] = @NowUtc,
        [ClientInfoLocalKey] =
            CASE
                WHEN source_equipment.[ClientInfoLocalKey] IS NOT NULL
                 AND EXISTS
                    (
                        SELECT 1
                        FROM [tb_inventory].[Equipment] target_equipment
                        WHERE target_equipment.[ClientId]=@TargetClientId
                          AND target_equipment.[ClientInfoLocalKey]=
                              source_equipment.[ClientInfoLocalKey]
                    )
                    THEN LEFT(
                        source_equipment.[ClientInfoLocalKey]
                        +N'-merged-'
                        +CONVERT(
                            nvarchar(20),
                            source_equipment.[EquipmentId]),
                        120)
                ELSE source_equipment.[ClientInfoLocalKey]
            END
    FROM [tb_inventory].[Equipment] source_equipment
    WHERE source_equipment.[ClientId] = @SourceClientId;

    UPDATE [tb_inventory].[EquipmentAssignmentHistory]
    SET [ClientId] = @TargetClientId
    WHERE [ClientId] = @SourceClientId;

    UPDATE [tb_client].[Locations]
    SET
        [ClientId] = @TargetClientId,
        [UpdatedByWindowsSid] = @ActorWindowsSid,
        [UpdatedAtUtc] = @NowUtc,
        [LocalKey] =
            CASE
                WHEN [LocalKey] IS NOT NULL
                 AND EXISTS
                    (
                        SELECT 1
                        FROM [tb_client].[Locations] AS target_record
                        WHERE target_record.[ClientId] = @TargetClientId
                          AND target_record.[LocalKey] = [tb_client].[Locations].[LocalKey]
                    )
                    THEN LEFT(
                        [LocalKey] + N'-merged-' + CONVERT(nvarchar(20), [LocationId]),
                        120)
                ELSE [LocalKey]
            END
    WHERE [ClientId] = @SourceClientId;

    UPDATE [tb_client].[People]
    SET
        [ClientId] = @TargetClientId,
        [UpdatedByWindowsSid] = @ActorWindowsSid,
        [UpdatedAtUtc] = @NowUtc,
        [LocalKey] =
            CASE
                WHEN [LocalKey] IS NOT NULL
                 AND EXISTS
                    (
                        SELECT 1
                        FROM [tb_client].[People] AS target_record
                        WHERE target_record.[ClientId] = @TargetClientId
                          AND target_record.[LocalKey] = [tb_client].[People].[LocalKey]
                    )
                    THEN LEFT(
                        [LocalKey] + N'-merged-' + CONVERT(nvarchar(20), [PersonId]),
                        120)
                ELSE [LocalKey]
            END
    WHERE [ClientId] = @SourceClientId;

    UPDATE [tb_client].[Resources]
    SET
        [ClientId] = @TargetClientId,
        [UpdatedByWindowsSid] = @ActorWindowsSid,
        [UpdatedAtUtc] = @NowUtc,
        [LocalKey] =
            CASE
                WHEN [LocalKey] IS NOT NULL
                 AND EXISTS
                    (
                        SELECT 1
                        FROM [tb_client].[Resources] AS target_record
                        WHERE target_record.[ClientId] = @TargetClientId
                          AND target_record.[LocalKey] = [tb_client].[Resources].[LocalKey]
                    )
                    THEN LEFT(
                        [LocalKey] + N'-merged-' + CONVERT(nvarchar(20), [ResourceId]),
                        120)
                ELSE [LocalKey]
            END
    WHERE [ClientId] = @SourceClientId;

    UPDATE [tb_client].[Credentials]
    SET
        [ClientId] = @TargetClientId,
        [UpdatedByWindowsSid] = @ActorWindowsSid,
        [UpdatedAtUtc] = @NowUtc,
        [LocalKey] =
            CASE
                WHEN [LocalKey] IS NOT NULL
                 AND EXISTS
                    (
                        SELECT 1
                        FROM [tb_client].[Credentials] AS target_record
                        WHERE target_record.[ClientId] = @TargetClientId
                          AND target_record.[LocalKey] = [tb_client].[Credentials].[LocalKey]
                    )
                    THEN LEFT(
                        [LocalKey] + N'-merged-' + CONVERT(nvarchar(20), [CredentialId]),
                        120)
                ELSE [LocalKey]
            END
    WHERE [ClientId] = @SourceClientId;

    UPDATE [tb_client].[ClientFacts]
    SET
        [ClientId] = @TargetClientId,
        [UpdatedByWindowsSid] = @ActorWindowsSid,
        [UpdatedAtUtc] = @NowUtc,
        [LocalKey] =
            CASE
                WHEN [LocalKey] IS NOT NULL
                 AND EXISTS
                    (
                        SELECT 1
                        FROM [tb_client].[ClientFacts] AS target_record
                        WHERE target_record.[ClientId] = @TargetClientId
                          AND target_record.[LocalKey] = [tb_client].[ClientFacts].[LocalKey]
                    )
                    THEN LEFT(
                        [LocalKey] + N'-merged-' + CONVERT(nvarchar(20), [FactId]),
                        120)
                ELSE [LocalKey]
            END
    WHERE [ClientId] = @SourceClientId;

    /* Attachment metadata follows a merged client. Relative file paths stay
       unchanged so the files never need to be moved during a SQL transaction. */
    IF OBJECT_ID(N'tb_client.ClientAttachments', N'U') IS NOT NULL
        EXEC sys.sp_executesql
            N'UPDATE [tb_client].[ClientAttachments]
              SET [ClientId]=@TargetClientId
              WHERE [ClientId]=@SourceClientId;',
            N'@SourceClientId int,@TargetClientId int',
            @SourceClientId=@SourceClientId,
            @TargetClientId=@TargetClientId;

    UPDATE provenance
    SET [SourceDocumentId] = target_document.[SourceDocumentId]
    FROM [tb_client].[RecordProvenance] AS provenance
    INNER JOIN [tb_client].[SourceDocuments] AS source_document
        ON source_document.[SourceDocumentId] = provenance.[SourceDocumentId]
       AND source_document.[ClientId] = @SourceClientId
    INNER JOIN [tb_client].[SourceDocuments] AS target_document
        ON target_document.[ClientId] = @TargetClientId
       AND target_document.[SourceKind] = source_document.[SourceKind]
       AND target_document.[ContentSha256] = source_document.[ContentSha256];

    UPDATE batch
    SET [SourceDocumentId] = target_document.[SourceDocumentId]
    FROM [tb_import].[ClientInfoBatches] AS batch
    INNER JOIN [tb_client].[SourceDocuments] AS source_document
        ON source_document.[SourceDocumentId] = batch.[SourceDocumentId]
       AND source_document.[ClientId] = @SourceClientId
    INNER JOIN [tb_client].[SourceDocuments] AS target_document
        ON target_document.[ClientId] = @TargetClientId
       AND target_document.[SourceKind] = source_document.[SourceKind]
       AND target_document.[ContentSha256] = source_document.[ContentSha256];

    DELETE source_document
    FROM [tb_client].[SourceDocuments] AS source_document
    WHERE source_document.[ClientId] = @SourceClientId
      AND EXISTS
      (
          SELECT 1
          FROM [tb_client].[SourceDocuments] AS target_document
          WHERE target_document.[ClientId] = @TargetClientId
            AND target_document.[SourceKind] = source_document.[SourceKind]
            AND target_document.[ContentSha256] = source_document.[ContentSha256]
      );

    UPDATE [tb_client].[SourceDocuments]
    SET [ClientId] = @TargetClientId
    WHERE [ClientId] = @SourceClientId;

    UPDATE [tb_client].[RecordProvenance]
    SET [ClientId] = @TargetClientId
    WHERE [ClientId] = @SourceClientId;

    UPDATE source_batch
    SET
        [WorkbookId] = NEWID(),
        [Message] = LEFT(
            COALESCE(NULLIF(source_batch.[Message], N'') + N' ', N'')
            + N'Workbook identity changed while merging duplicate client records.',
            2000),
        [UpdatedAtUtc] = @NowUtc
    FROM [tb_import].[ClientInfoBatches] AS source_batch
    WHERE source_batch.[ClientId] = @SourceClientId
      AND EXISTS
      (
          SELECT 1
          FROM [tb_import].[ClientInfoBatches] AS target_batch
          WHERE target_batch.[ClientId] = @TargetClientId
            AND target_batch.[WorkbookId] = source_batch.[WorkbookId]
            AND target_batch.[ContentSha256] = source_batch.[ContentSha256]
      );

    UPDATE [tb_import].[ClientInfoBatches]
    SET
        [ClientId] = @TargetClientId,
        [UpdatedAtUtc] = @NowUtc
    WHERE [ClientId] = @SourceClientId;

    IF EXISTS
    (
        SELECT 1 FROM [tb_ops].[ClientInfoCutovers]
        WHERE [ClientId] = @SourceClientId
    )
    BEGIN
        IF EXISTS
        (
            SELECT 1 FROM [tb_ops].[ClientInfoCutovers]
            WHERE [ClientId] = @TargetClientId
        )
            DELETE FROM [tb_ops].[ClientInfoCutovers]
            WHERE [ClientId] = @SourceClientId;
        ELSE
            UPDATE [tb_ops].[ClientInfoCutovers]
            SET
                [ClientId] = @TargetClientId,
                [UpdatedByWindowsSid] = @ActorWindowsSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [ClientId] = @SourceClientId;
    END;

END;
GO

IF OBJECT_ID(N'tb_app.SearchClientInfoClients', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SearchClientInfoClients];
GO

CREATE PROCEDURE [tb_app].[SearchClientInfoClients]
    @Search nvarchar(240) = NULL,
    @IncludeInactive bit = 0,
    @Limit int = 500
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@UserSid OUTPUT,
        @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;

    SET @Search = NULLIF(LTRIM(RTRIM(@Search)), N'');
    SET @Limit = CASE
        WHEN @Limit IS NULL OR @Limit < 1 THEN 1
        WHEN @Limit > 2000 THEN 2000
        ELSE @Limit END;

    DECLARE @Pattern nvarchar(500) =
        CASE WHEN @Search IS NULL THEN NULL ELSE N'%' + @Search + N'%' END;

    SELECT TOP (@Limit)
        client.[Id] AS [ClientId],
        client.[Name] AS [ClientName],
        client.[IsActive],
        COALESCE(profile.[ReviewStatus], N'Unverified') AS [ReviewStatus],
        COALESCE(cutover.[State], N'NotStarted') AS [CutoverState],
        COALESCE(profile.[IsLive], CONVERT(bit, 0)) AS [IsLive],
        profile.[UpdatedAtUtc],
        profile.[RowVersion],
        (
            SELECT COUNT_BIG(*)
            FROM [tb_client].[Locations] AS location
            WHERE location.[ClientId] = client.[Id]
              AND location.[IsActive] = 1
        ) AS [LocationCount],
        (
            SELECT COUNT_BIG(*)
            FROM [tb_client].[People] AS person
            WHERE person.[ClientId] = client.[Id]
              AND person.[IsActive] = 1
        ) AS [PersonCount],
        (
            SELECT COUNT_BIG(*)
            FROM [tb_client].[Resources] AS resource
            WHERE resource.[ClientId] = client.[Id]
              AND resource.[IsActive] = 1
        ) AS [ResourceCount],
        (
            SELECT COUNT_BIG(*)
            FROM [tb_client].[Credentials] AS credential
            WHERE credential.[ClientId] = client.[Id]
              AND credential.[IsActive] = 1
        ) AS [CredentialCount]
    FROM [tb_data].[Clients] AS client
    LEFT JOIN [tb_client].[ClientProfiles] AS profile
        ON profile.[ClientId] = client.[Id]
    LEFT JOIN [tb_ops].[ClientInfoCutovers] AS cutover
        ON cutover.[ClientId] = client.[Id]
    WHERE (@IncludeInactive = 1 OR client.[IsActive] = 1)
      AND
      (
          @Pattern IS NULL
          OR client.[Name] LIKE @Pattern
          OR CONVERT(nvarchar(20), client.[Id]) = @Search
          OR client.[WhdLocationName] LIKE @Pattern
          OR client.[SageCustomerName] LIKE @Pattern
      )
    ORDER BY client.[IsActive] DESC, client.[Name], client.[Id];
END;
GO

IF OBJECT_ID(N'tb_app.GetClientInfoSnapshot', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetClientInfoSnapshot];
GO

CREATE PROCEDURE [tb_app].[GetClientInfoSnapshot]
    @ClientId int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@UserSid OUTPUT,
        @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;

    IF NOT EXISTS
    (
        SELECT 1 FROM [tb_data].[Clients]
        WHERE [Id] = @ClientId
    )
        THROW 52310, N'The selected client no longer exists.', 1;

    SELECT
        client.[Id] AS [ClientId],
        client.[Name] AS [ClientName],
        client.[IsActive],
        client.[WhdContactName],
        client.[WhdContactEmail],
        client.[WhdPhone],
        client.[WhdAddress],
        profile.[Summary],
        COALESCE(profile.[ReviewStatus], N'Unverified') AS [ReviewStatus],
        COALESCE(profile.[IsLive], CONVERT(bit, 0)) AS [IsLive],
        profile.[LastVerifiedAtUtc],
        profile.[UpdatedAtUtc],
        profile.[RowVersion],
        COALESCE(cutover.[State], N'NotStarted') AS [CutoverState],
        cutover.[RowVersion] AS [CutoverRowVersion]
    FROM [tb_data].[Clients] AS client
    LEFT JOIN [tb_client].[ClientProfiles] AS profile
        ON profile.[ClientId] = client.[Id]
    LEFT JOIN [tb_ops].[ClientInfoCutovers] AS cutover
        ON cutover.[ClientId] = client.[Id]
    WHERE client.[Id] = @ClientId;

    SELECT
        [LocationId], [ClientId], [LocalKey], [Name], [LocationType],
        [Address1], [Address2], [City], [StateProvince], [PostalCode],
        [MainPhone], [TimeZoneId], [IsPrimary], [ReviewStatus], [IsActive],
        [LastVerifiedAtUtc], [UpdatedAtUtc], [RowVersion]
    FROM [tb_client].[Locations]
    WHERE [ClientId] = @ClientId
    ORDER BY [IsActive] DESC, [IsPrimary] DESC, [Name], [LocationId];

    SELECT
        person.[PersonId], person.[ClientId], person.[LocationId],
        location.[Name] AS [LocationName], person.[LocalKey],
        person.[DisplayName], person.[RoleDepartment], person.[Email],
        person.[Phone], person.[MobilePhone], person.[ContactType],
        person.[IsPrimary], person.[ReviewStatus], person.[IsActive],
        person.[LastVerifiedAtUtc], person.[UpdatedAtUtc], person.[RowVersion]
    FROM [tb_client].[People] AS person
    LEFT JOIN [tb_client].[Locations] AS location
        ON location.[LocationId] = person.[LocationId]
    WHERE person.[ClientId] = @ClientId
    ORDER BY person.[IsActive] DESC, person.[IsPrimary] DESC,
        person.[DisplayName], person.[PersonId];

    SELECT
        resource.[ResourceId], resource.[ClientId], resource.[LocationId],
        location.[Name] AS [LocationName], resource.[ParentResourceId],
        resource.[EquipmentId], resource.[LocalKey], resource.[ResourceType],
        resource.[Name], resource.[Provider], resource.[AddressOrUrl],
        resource.[Status], resource.[Notes], resource.[ReviewStatus],
        resource.[IsActive], resource.[LastVerifiedAtUtc],
        resource.[UpdatedAtUtc], resource.[RowVersion]
    FROM [tb_client].[Resources] AS resource
    LEFT JOIN [tb_client].[Locations] AS location
        ON location.[LocationId] = resource.[LocationId]
    WHERE resource.[ClientId] = @ClientId
    ORDER BY resource.[IsActive] DESC, resource.[ResourceType],
        resource.[Name], resource.[ResourceId];

    SELECT
        field.[ResourceFieldId], field.[ResourceId], field.[FieldKey],
        field.[FieldLabel], field.[ValueText], field.[ValueType],
        field.[SortOrder], field.[UpdatedAtUtc], field.[RowVersion]
    FROM [tb_client].[ResourceFields] AS field
    INNER JOIN [tb_client].[Resources] AS resource
        ON resource.[ResourceId] = field.[ResourceId]
    WHERE resource.[ClientId] = @ClientId
    ORDER BY field.[ResourceId], field.[SortOrder],
        field.[FieldLabel], field.[ResourceFieldId];

    SELECT
        credential.[CredentialId], credential.[ClientId],
        credential.[ResourceId], credential.[PersonId], credential.[LocalKey],
        credential.[Name], credential.[Category], credential.[Username],
        credential.[LoginUrl], credential.[Notes], credential.[ReviewStatus],
        credential.[IsActive], credential.[LastVerifiedAtUtc],
        credential.[UpdatedAtUtc], credential.[RowVersion],
        COUNT(secret.[SecretId]) AS [SecretCount]
    FROM [tb_client].[Credentials] AS credential
    LEFT JOIN [tb_client].[CredentialSecrets] AS secret
        ON secret.[CredentialId] = credential.[CredentialId]
       AND secret.[IsCurrent] = 1
    WHERE credential.[ClientId] = @ClientId
    GROUP BY
        credential.[CredentialId], credential.[ClientId],
        credential.[ResourceId], credential.[PersonId], credential.[LocalKey],
        credential.[Name], credential.[Category], credential.[Username],
        credential.[LoginUrl], credential.[Notes], credential.[ReviewStatus],
        credential.[IsActive], credential.[LastVerifiedAtUtc],
        credential.[UpdatedAtUtc], credential.[RowVersion]
    ORDER BY credential.[IsActive] DESC, credential.[Category],
        credential.[Name], credential.[CredentialId];

    SELECT
        secret.[SecretId], secret.[CredentialId], secret.[SecretType],
        secret.[SecretLabel], secret.[IsCurrent], secret.[LastVerifiedAtUtc],
        secret.[UpdatedAtUtc], secret.[RowVersion]
    FROM [tb_client].[CredentialSecrets] AS secret
    INNER JOIN [tb_client].[Credentials] AS credential
        ON credential.[CredentialId] = secret.[CredentialId]
    WHERE credential.[ClientId] = @ClientId
      AND secret.[IsCurrent] = 1
    ORDER BY secret.[CredentialId], secret.[SecretType],
        secret.[SecretLabel], secret.[SecretId];

    SELECT
        [FactId], [ClientId], [LocalKey], [SectionName], [FieldLabel],
        [ValueText], [ValueType], [ReviewStatus], [SortOrder], [IsActive],
        [LastVerifiedAtUtc], [UpdatedAtUtc], [RowVersion]
    FROM [tb_client].[ClientFacts]
    WHERE [ClientId] = @ClientId
    ORDER BY [IsActive] DESC, [SectionName], [SortOrder],
        [FieldLabel], [FactId];

    SELECT TOP (50)
        batch.[BatchId], batch.[ClientId], client.[Name] AS [ClientName],
        batch.[TemplateVersion], batch.[WorkbookId], batch.[State],
        batch.[Message], batch.[CreatedAtUtc], batch.[UpdatedAtUtc],
        batch.[ApprovedAtUtc], batch.[PromotedAtUtc], batch.[RowVersion],
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
    INNER JOIN [tb_data].[Clients] client
        ON client.[Id]=batch.[ClientId]
    WHERE batch.[ClientId] = @ClientId
    ORDER BY batch.[CreatedAtUtc] DESC, batch.[BatchId];
END;
GO

IF OBJECT_ID(N'tb_app.SaveClientInfoProfile', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveClientInfoProfile];
GO

CREATE PROCEDURE [tb_app].[SaveClientInfoProfile]
    @ClientId int,
    @Summary nvarchar(2000) = NULL,
    @ReviewStatus nvarchar(24) = N'Unverified',
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,
        @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
       AND IS_ROLEMEMBER(N'tb_role_client_info_editor') <> 1
        THROW 52320, N'Client Info editor permission is required.', 1;

    SET @Summary = NULLIF(LTRIM(RTRIM(@Summary)), N'');
    SET @ReviewStatus = COALESCE(NULLIF(LTRIM(RTRIM(@ReviewStatus)), N''), N'Unverified');
    IF @ReviewStatus NOT IN
        (N'Unverified', N'Verified', N'AcceptedUnverified', N'NeedsReview')
        THROW 52321, N'The Client Info review status is invalid.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM [tb_data].[Clients]
        WHERE [Id] = @ClientId
    )
        THROW 52322, N'The selected client no longer exists.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @Action nvarchar(120);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF EXISTS
        (
            SELECT 1 FROM [tb_client].[ClientProfiles]
            WHERE [ClientId] = @ClientId
        )
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 52323, N'ExpectedRowVersion is required when updating Client Info.', 1;

            UPDATE [tb_client].[ClientProfiles]
            SET
                [Summary] = @Summary,
                [ReviewStatus] = @ReviewStatus,
                [LastVerifiedAtUtc] =
                    CASE WHEN @ReviewStatus = N'Verified'
                        THEN @NowUtc ELSE [LastVerifiedAtUtc] END,
                [LastVerifiedByWindowsSid] =
                    CASE WHEN @ReviewStatus = N'Verified'
                        THEN @ActorSid ELSE [LastVerifiedByWindowsSid] END,
                [UpdatedByWindowsSid] = @ActorSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [ClientId] = @ClientId
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT <> 1
                THROW 52324, N'Client Info changed on another workstation. Refresh and resolve the conflict.', 1;
            SET @Action = N'ClientInfoProfileUpdated';
        END
        ELSE
        BEGIN
            IF @ExpectedRowVersion IS NOT NULL
                THROW 52324, N'Client Info changed on another workstation. Refresh and resolve the conflict.', 1;

            INSERT INTO [tb_client].[ClientProfiles]
            (
                [ClientId], [Summary], [ReviewStatus],
                [LastVerifiedAtUtc], [LastVerifiedByWindowsSid],
                [CreatedByWindowsSid], [UpdatedByWindowsSid],
                [CreatedAtUtc], [UpdatedAtUtc]
            )
            VALUES
            (
                @ClientId, @Summary, @ReviewStatus,
                CASE WHEN @ReviewStatus = N'Verified' THEN @NowUtc END,
                CASE WHEN @ReviewStatus = N'Verified' THEN @ActorSid END,
                @ActorSid, @ActorSid, @NowUtc, @NowUtc
            );
            SET @Action = N'ClientInfoProfileCreated';
        END;

        DECLARE @AuditEntityId nvarchar(120) =
            CONVERT(nvarchar(120), @ClientId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action=@Action,
            @EntityType=N'ClientInfoProfile',
            @EntityId=@AuditEntityId,
            @RequestId=@RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [ClientId], [Summary], [ReviewStatus], [IsLive],
        [LastVerifiedAtUtc], [UpdatedAtUtc], [RowVersion]
    FROM [tb_client].[ClientProfiles]
    WHERE [ClientId] = @ClientId;
END;
GO

IF OBJECT_ID(N'tb_app.SaveClientInfoLocation', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveClientInfoLocation];
GO

CREATE PROCEDURE [tb_app].[SaveClientInfoLocation]
    @LocationId bigint = NULL,
    @ClientId int,
    @LocalKey nvarchar(120) = NULL,
    @Name nvarchar(240),
    @LocationType nvarchar(80) = NULL,
    @Address1 nvarchar(240) = NULL,
    @Address2 nvarchar(240) = NULL,
    @City nvarchar(120) = NULL,
    @StateProvince nvarchar(80) = NULL,
    @PostalCode nvarchar(40) = NULL,
    @MainPhone nvarchar(80) = NULL,
    @TimeZoneId nvarchar(120) = NULL,
    @IsPrimary bit = 0,
    @ReviewStatus nvarchar(24) = N'Unverified',
    @IsActive bit = 1,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,
        @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin <> 1
       AND IS_ROLEMEMBER(N'tb_role_client_info_editor') <> 1
        THROW 52320, N'Client Info editor permission is required.', 1;

    SET @Name = NULLIF(LTRIM(RTRIM(@Name)), N'');
    SET @LocalKey = NULLIF(LTRIM(RTRIM(@LocalKey)), N'');
    IF @Name IS NULL
        THROW 52330, N'Location name is required.', 1;
    IF @ReviewStatus NOT IN
        (N'Unverified', N'Verified', N'AcceptedUnverified', N'NeedsReview')
        THROW 52321, N'The Client Info review status is invalid.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @Action nvarchar(120);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @IsPrimary = 1
            UPDATE [tb_client].[Locations]
            SET
                [IsPrimary] = 0,
                [UpdatedByWindowsSid] = @ActorSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [ClientId] = @ClientId
              AND [IsPrimary] = 1
              AND (@LocationId IS NULL OR [LocationId] <> @LocationId);

        IF @LocationId IS NULL
        BEGIN
            INSERT INTO [tb_client].[Locations]
            (
                [ClientId], [LocalKey], [Name], [LocationType], [Address1],
                [Address2], [City], [StateProvince], [PostalCode], [MainPhone],
                [TimeZoneId], [IsPrimary], [ReviewStatus], [IsActive],
                [LastVerifiedAtUtc], [CreatedByWindowsSid], [UpdatedByWindowsSid],
                [CreatedAtUtc], [UpdatedAtUtc]
            )
            VALUES
            (
                @ClientId, @LocalKey, @Name, NULLIF(@LocationType, N''),
                NULLIF(@Address1, N''), NULLIF(@Address2, N''), NULLIF(@City, N''),
                NULLIF(@StateProvince, N''), NULLIF(@PostalCode, N''),
                NULLIF(@MainPhone, N''), NULLIF(@TimeZoneId, N''), @IsPrimary,
                @ReviewStatus, @IsActive,
                CASE WHEN @ReviewStatus = N'Verified' THEN @NowUtc END,
                @ActorSid, @ActorSid, @NowUtc, @NowUtc
            );
            SET @LocationId = CONVERT(bigint, SCOPE_IDENTITY());
            SET @Action = N'ClientInfoLocationCreated';
        END
        ELSE
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 52331, N'ExpectedRowVersion is required when updating a location.', 1;

            UPDATE [tb_client].[Locations]
            SET
                [LocalKey]=@LocalKey, [Name]=@Name,
                [LocationType]=NULLIF(@LocationType, N''),
                [Address1]=NULLIF(@Address1, N''),
                [Address2]=NULLIF(@Address2, N''),
                [City]=NULLIF(@City, N''),
                [StateProvince]=NULLIF(@StateProvince, N''),
                [PostalCode]=NULLIF(@PostalCode, N''),
                [MainPhone]=NULLIF(@MainPhone, N''),
                [TimeZoneId]=NULLIF(@TimeZoneId, N''),
                [IsPrimary]=@IsPrimary, [ReviewStatus]=@ReviewStatus,
                [IsActive]=@IsActive,
                [LastVerifiedAtUtc]=CASE WHEN @ReviewStatus=N'Verified'
                    THEN @NowUtc ELSE [LastVerifiedAtUtc] END,
                [UpdatedByWindowsSid]=@ActorSid, [UpdatedAtUtc]=@NowUtc
            WHERE [LocationId]=@LocationId
              AND [ClientId]=@ClientId
              AND [RowVersion]=@ExpectedRowVersion;
            IF @@ROWCOUNT <> 1
                THROW 52332, N'The location changed on another workstation. Refresh and resolve the conflict.', 1;
            SET @Action = N'ClientInfoLocationUpdated';
        END;

        DECLARE @AuditEntityId nvarchar(120) =
            CONVERT(nvarchar(120), @LocationId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action=@Action, @EntityType=N'ClientInfoLocation',
            @EntityId=@AuditEntityId, @RequestId=@RequestId;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT * FROM [tb_client].[Locations]
    WHERE [LocationId] = @LocationId;
END;
GO

IF OBJECT_ID(N'tb_app.SaveClientInfoPerson', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveClientInfoPerson];
GO

CREATE PROCEDURE [tb_app].[SaveClientInfoPerson]
    @PersonId bigint = NULL,
    @ClientId int,
    @LocationId bigint = NULL,
    @LocalKey nvarchar(120) = NULL,
    @DisplayName nvarchar(240),
    @RoleDepartment nvarchar(240) = NULL,
    @Email nvarchar(320) = NULL,
    @Phone nvarchar(80) = NULL,
    @MobilePhone nvarchar(80) = NULL,
    @ContactType nvarchar(80) = NULL,
    @IsPrimary bit = 0,
    @ReviewStatus nvarchar(24) = N'Unverified',
    @IsActive bit = 1,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin <> 1
       AND IS_ROLEMEMBER(N'tb_role_client_info_editor') <> 1
        THROW 52320, N'Client Info editor permission is required.', 1;

    SET @DisplayName = NULLIF(LTRIM(RTRIM(@DisplayName)), N'');
    SET @LocalKey = NULLIF(LTRIM(RTRIM(@LocalKey)), N'');
    IF @DisplayName IS NULL
        THROW 52340, N'Person name is required.', 1;
    IF @LocationId IS NOT NULL AND NOT EXISTS
    (
        SELECT 1 FROM [tb_client].[Locations]
        WHERE [LocationId]=@LocationId AND [ClientId]=@ClientId
    )
        THROW 52341, N'The selected location does not belong to this client.', 1;
    IF @ReviewStatus NOT IN
        (N'Unverified', N'Verified', N'AcceptedUnverified', N'NeedsReview')
        THROW 52321, N'The Client Info review status is invalid.', 1;

    DECLARE @NowUtc datetime2(3)=SYSUTCDATETIME();
    DECLARE @Action nvarchar(120);
    BEGIN TRY
        BEGIN TRANSACTION;
        IF @IsPrimary=1
            UPDATE [tb_client].[People]
            SET [IsPrimary]=0, [UpdatedByWindowsSid]=@ActorSid, [UpdatedAtUtc]=@NowUtc
            WHERE [ClientId]=@ClientId AND [IsPrimary]=1
              AND (@PersonId IS NULL OR [PersonId]<>@PersonId);

        IF @PersonId IS NULL
        BEGIN
            INSERT INTO [tb_client].[People]
            (
                [ClientId], [LocationId], [LocalKey], [DisplayName],
                [RoleDepartment], [Email], [Phone], [MobilePhone],
                [ContactType], [IsPrimary], [ReviewStatus], [IsActive],
                [LastVerifiedAtUtc], [CreatedByWindowsSid], [UpdatedByWindowsSid],
                [CreatedAtUtc], [UpdatedAtUtc]
            )
            VALUES
            (
                @ClientId, @LocationId, @LocalKey, @DisplayName,
                NULLIF(@RoleDepartment,N''), NULLIF(@Email,N''),
                NULLIF(@Phone,N''), NULLIF(@MobilePhone,N''),
                NULLIF(@ContactType,N''), @IsPrimary, @ReviewStatus, @IsActive,
                CASE WHEN @ReviewStatus=N'Verified' THEN @NowUtc END,
                @ActorSid, @ActorSid, @NowUtc, @NowUtc
            );
            SET @PersonId=CONVERT(bigint,SCOPE_IDENTITY());
            SET @Action=N'ClientInfoPersonCreated';
        END
        ELSE
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 52342, N'ExpectedRowVersion is required when updating a person.', 1;
            UPDATE [tb_client].[People]
            SET [LocationId]=@LocationId, [LocalKey]=@LocalKey,
                [DisplayName]=@DisplayName,
                [RoleDepartment]=NULLIF(@RoleDepartment,N''),
                [Email]=NULLIF(@Email,N''), [Phone]=NULLIF(@Phone,N''),
                [MobilePhone]=NULLIF(@MobilePhone,N''),
                [ContactType]=NULLIF(@ContactType,N''),
                [IsPrimary]=@IsPrimary, [ReviewStatus]=@ReviewStatus,
                [IsActive]=@IsActive,
                [LastVerifiedAtUtc]=CASE WHEN @ReviewStatus=N'Verified'
                    THEN @NowUtc ELSE [LastVerifiedAtUtc] END,
                [UpdatedByWindowsSid]=@ActorSid, [UpdatedAtUtc]=@NowUtc
            WHERE [PersonId]=@PersonId AND [ClientId]=@ClientId
              AND [RowVersion]=@ExpectedRowVersion;
            IF @@ROWCOUNT<>1
                THROW 52343, N'The person changed on another workstation. Refresh and resolve the conflict.', 1;
            SET @Action=N'ClientInfoPersonUpdated';
        END;
        DECLARE @AuditEntityId nvarchar(120) =
            CONVERT(nvarchar(120), @PersonId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action=@Action, @EntityType=N'ClientInfoPerson',
            @EntityId=@AuditEntityId, @RequestId=@RequestId;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
    SELECT * FROM [tb_client].[People] WHERE [PersonId]=@PersonId;
END;
GO

IF OBJECT_ID(N'tb_app.SaveClientInfoResource', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveClientInfoResource];
GO

CREATE PROCEDURE [tb_app].[SaveClientInfoResource]
    @ResourceId bigint = NULL,
    @ClientId int,
    @LocationId bigint = NULL,
    @ParentResourceId bigint = NULL,
    @EquipmentId bigint = NULL,
    @LocalKey nvarchar(120) = NULL,
    @ResourceType nvarchar(80),
    @Name nvarchar(240),
    @Provider nvarchar(160) = NULL,
    @AddressOrUrl nvarchar(1000) = NULL,
    @Status nvarchar(80) = NULL,
    @Notes nvarchar(max) = NULL,
    @ReviewStatus nvarchar(24) = N'Unverified',
    @IsActive bit = 1,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1 AND IS_ROLEMEMBER(N'tb_role_client_info_editor')<>1
        THROW 52320, N'Client Info editor permission is required.', 1;

    SET @ResourceType=NULLIF(LTRIM(RTRIM(@ResourceType)),N'');
    SET @Name=NULLIF(LTRIM(RTRIM(@Name)),N'');
    SET @LocalKey=NULLIF(LTRIM(RTRIM(@LocalKey)),N'');
    IF @ResourceType IS NULL OR @Name IS NULL
        THROW 52350, N'Resource type and name are required.', 1;
    IF @ReviewStatus NOT IN
        (N'Unverified',N'Verified',N'AcceptedUnverified',N'NeedsReview')
        THROW 52321, N'The Client Info review status is invalid.', 1;
    IF @LocationId IS NOT NULL AND NOT EXISTS
        (SELECT 1 FROM [tb_client].[Locations]
         WHERE [LocationId]=@LocationId AND [ClientId]=@ClientId)
        THROW 52351, N'The selected location does not belong to this client.', 1;
    IF @ParentResourceId IS NOT NULL AND NOT EXISTS
        (SELECT 1 FROM [tb_client].[Resources]
         WHERE [ResourceId]=@ParentResourceId AND [ClientId]=@ClientId)
        THROW 52352, N'The parent resource does not belong to this client.', 1;

    DECLARE @NowUtc datetime2(3)=SYSUTCDATETIME();
    DECLARE @Action nvarchar(120);
    BEGIN TRY
        BEGIN TRANSACTION;
        IF @ResourceId IS NULL
        BEGIN
            INSERT INTO [tb_client].[Resources]
            (
                [ClientId],[LocationId],[ParentResourceId],[EquipmentId],
                [LocalKey],[ResourceType],[Name],[Provider],[AddressOrUrl],
                [Status],[Notes],[ReviewStatus],[IsActive],[LastVerifiedAtUtc],
                [CreatedByWindowsSid],[UpdatedByWindowsSid],[CreatedAtUtc],[UpdatedAtUtc]
            )
            VALUES
            (
                @ClientId,@LocationId,@ParentResourceId,@EquipmentId,@LocalKey,
                @ResourceType,@Name,NULLIF(@Provider,N''),NULLIF(@AddressOrUrl,N''),
                NULLIF(@Status,N''),NULLIF(@Notes,N''),@ReviewStatus,@IsActive,
                CASE WHEN @ReviewStatus=N'Verified' THEN @NowUtc END,
                @ActorSid,@ActorSid,@NowUtc,@NowUtc
            );
            SET @ResourceId=CONVERT(bigint,SCOPE_IDENTITY());
            SET @Action=N'ClientInfoResourceCreated';
        END
        ELSE
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 52353, N'ExpectedRowVersion is required when updating a resource.',1;
            UPDATE [tb_client].[Resources]
            SET [LocationId]=@LocationId,[ParentResourceId]=@ParentResourceId,
                [EquipmentId]=@EquipmentId,[LocalKey]=@LocalKey,
                [ResourceType]=@ResourceType,[Name]=@Name,
                [Provider]=NULLIF(@Provider,N''),
                [AddressOrUrl]=NULLIF(@AddressOrUrl,N''),
                [Status]=NULLIF(@Status,N''),[Notes]=NULLIF(@Notes,N''),
                [ReviewStatus]=@ReviewStatus,[IsActive]=@IsActive,
                [LastVerifiedAtUtc]=CASE WHEN @ReviewStatus=N'Verified'
                    THEN @NowUtc ELSE [LastVerifiedAtUtc] END,
                [UpdatedByWindowsSid]=@ActorSid,[UpdatedAtUtc]=@NowUtc
            WHERE [ResourceId]=@ResourceId AND [ClientId]=@ClientId
              AND [RowVersion]=@ExpectedRowVersion;
            IF @@ROWCOUNT<>1
                THROW 52354,N'The resource changed on another workstation. Refresh and resolve the conflict.',1;
            SET @Action=N'ClientInfoResourceUpdated';
        END;
        DECLARE @AuditEntityId nvarchar(120) =
            CONVERT(nvarchar(120), @ResourceId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action=@Action,@EntityType=N'ClientInfoResource',
            @EntityId=@AuditEntityId,@RequestId=@RequestId;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
    SELECT * FROM [tb_client].[Resources] WHERE [ResourceId]=@ResourceId;
END;
GO

IF OBJECT_ID(N'tb_app.SaveClientInfoResourceField', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveClientInfoResourceField];
GO

CREATE PROCEDURE [tb_app].[SaveClientInfoResourceField]
    @ResourceFieldId bigint = NULL,
    @ResourceId bigint,
    @FieldKey nvarchar(120),
    @FieldLabel nvarchar(200),
    @ValueText nvarchar(max) = NULL,
    @ValueType nvarchar(24) = N'Text',
    @SortOrder int = 0,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1 AND IS_ROLEMEMBER(N'tb_role_client_info_editor')<>1
        THROW 52320, N'Client Info editor permission is required.', 1;

    SET @FieldKey=NULLIF(LTRIM(RTRIM(@FieldKey)),N'');
    SET @FieldLabel=NULLIF(LTRIM(RTRIM(@FieldLabel)),N'');
    IF @FieldKey IS NULL OR @FieldLabel IS NULL
        THROW 52355,N'Resource field key and label are required.',1;
    IF @ValueType NOT IN (N'Text',N'Number',N'Boolean',N'Date',N'Url',N'IpAddress')
        THROW 52355,N'The resource field value type is invalid.',1;
    IF NOT EXISTS (SELECT 1 FROM [tb_client].[Resources] WHERE [ResourceId]=@ResourceId)
        THROW 52356,N'The resource for this field no longer exists.',1;

    DECLARE @NowUtc datetime2(3)=SYSUTCDATETIME(), @Action nvarchar(120);
    BEGIN TRY
        BEGIN TRANSACTION;
        IF @ResourceFieldId IS NULL
        BEGIN
            INSERT INTO [tb_client].[ResourceFields]
            (
                [ResourceId],[FieldKey],[FieldLabel],[ValueText],[ValueType],
                [SortOrder],[UpdatedByWindowsSid],[UpdatedAtUtc]
            )
            VALUES
            (
                @ResourceId,@FieldKey,@FieldLabel,NULLIF(@ValueText,N''),
                @ValueType,@SortOrder,@ActorSid,@NowUtc
            );
            SET @ResourceFieldId=CONVERT(bigint,SCOPE_IDENTITY());
            SET @Action=N'ClientInfoResourceFieldCreated';
        END
        ELSE
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 52357,N'ExpectedRowVersion is required when updating a resource field.',1;
            UPDATE [tb_client].[ResourceFields]
            SET [FieldKey]=@FieldKey,[FieldLabel]=@FieldLabel,
                [ValueText]=NULLIF(@ValueText,N''),[ValueType]=@ValueType,
                [SortOrder]=@SortOrder,[UpdatedByWindowsSid]=@ActorSid,
                [UpdatedAtUtc]=@NowUtc
            WHERE [ResourceFieldId]=@ResourceFieldId
              AND [ResourceId]=@ResourceId
              AND [RowVersion]=@ExpectedRowVersion;
            IF @@ROWCOUNT<>1
                THROW 52358,N'The resource field changed on another workstation. Refresh and resolve the conflict.',1;
            SET @Action=N'ClientInfoResourceFieldUpdated';
        END;

        DECLARE @AuditEntityId nvarchar(120)=
            CONVERT(nvarchar(120),@ResourceFieldId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action=@Action,@EntityType=N'ClientInfoResourceField',
            @EntityId=@AuditEntityId,@RequestId=@RequestId;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT * FROM [tb_client].[ResourceFields]
    WHERE [ResourceFieldId]=@ResourceFieldId;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteClientInfoResourceField', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteClientInfoResourceField];
GO

CREATE PROCEDURE [tb_app].[DeleteClientInfoResourceField]
    @ResourceFieldId bigint,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT, @IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1 AND IS_ROLEMEMBER(N'tb_role_client_info_editor')<>1
        THROW 52320, N'Client Info editor permission is required.', 1;
    IF @ExpectedRowVersion IS NULL
        THROW 52357,N'ExpectedRowVersion is required when deleting a resource field.',1;

    BEGIN TRY
        BEGIN TRANSACTION;
        DELETE FROM [tb_client].[ResourceFields]
        WHERE [ResourceFieldId]=@ResourceFieldId
          AND [RowVersion]=@ExpectedRowVersion;
        IF @@ROWCOUNT<>1
            THROW 52359,N'The resource field changed on another workstation. Refresh and resolve the conflict.',1;

        DECLARE @AuditEntityId nvarchar(120)=
            CONVERT(nvarchar(120),@ResourceFieldId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action=N'ClientInfoResourceFieldDeleted',
            @EntityType=N'ClientInfoResourceField',
            @EntityId=@AuditEntityId,@RequestId=@RequestId;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_app.SaveClientInfoFact', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveClientInfoFact];
GO

CREATE PROCEDURE [tb_app].[SaveClientInfoFact]
    @FactId bigint = NULL,
    @ClientId int,
    @LocalKey nvarchar(120) = NULL,
    @SectionName nvarchar(120),
    @FieldLabel nvarchar(200),
    @ValueText nvarchar(max) = NULL,
    @ValueType nvarchar(24) = N'Text',
    @ReviewStatus nvarchar(24) = N'Unverified',
    @SortOrder int = 0,
    @IsActive bit = 1,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,@IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,@IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1 AND IS_ROLEMEMBER(N'tb_role_client_info_editor')<>1
        THROW 52320,N'Client Info editor permission is required.',1;

    SET @SectionName=NULLIF(LTRIM(RTRIM(@SectionName)),N'');
    SET @FieldLabel=NULLIF(LTRIM(RTRIM(@FieldLabel)),N'');
    SET @LocalKey=NULLIF(LTRIM(RTRIM(@LocalKey)),N'');
    IF @SectionName IS NULL OR @FieldLabel IS NULL
        THROW 52360,N'Fact section and label are required.',1;
    IF @ValueType NOT IN (N'Text',N'Number',N'Boolean',N'Date',N'Url',N'IpAddress')
        THROW 52361,N'The fact value type is invalid.',1;
    IF @ReviewStatus NOT IN
        (N'Unverified',N'Verified',N'AcceptedUnverified',N'NeedsReview')
        THROW 52321,N'The Client Info review status is invalid.',1;

    DECLARE @NowUtc datetime2(3)=SYSUTCDATETIME(),@Action nvarchar(120);
    BEGIN TRY
        BEGIN TRANSACTION;
        IF @FactId IS NULL
        BEGIN
            INSERT INTO [tb_client].[ClientFacts]
            (
                [ClientId],[LocalKey],[SectionName],[FieldLabel],[ValueText],
                [ValueType],[ReviewStatus],[SortOrder],[IsActive],[LastVerifiedAtUtc],
                [CreatedByWindowsSid],[UpdatedByWindowsSid],[CreatedAtUtc],[UpdatedAtUtc]
            )
            VALUES
            (
                @ClientId,@LocalKey,@SectionName,@FieldLabel,NULLIF(@ValueText,N''),
                @ValueType,@ReviewStatus,@SortOrder,@IsActive,
                CASE WHEN @ReviewStatus=N'Verified' THEN @NowUtc END,
                @ActorSid,@ActorSid,@NowUtc,@NowUtc
            );
            SET @FactId=CONVERT(bigint,SCOPE_IDENTITY());
            SET @Action=N'ClientInfoFactCreated';
        END
        ELSE
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 52362,N'ExpectedRowVersion is required when updating a fact.',1;
            UPDATE [tb_client].[ClientFacts]
            SET [LocalKey]=@LocalKey,[SectionName]=@SectionName,
                [FieldLabel]=@FieldLabel,[ValueText]=NULLIF(@ValueText,N''),
                [ValueType]=@ValueType,[ReviewStatus]=@ReviewStatus,
                [SortOrder]=@SortOrder,[IsActive]=@IsActive,
                [LastVerifiedAtUtc]=CASE WHEN @ReviewStatus=N'Verified'
                    THEN @NowUtc ELSE [LastVerifiedAtUtc] END,
                [UpdatedByWindowsSid]=@ActorSid,[UpdatedAtUtc]=@NowUtc
            WHERE [FactId]=@FactId AND [ClientId]=@ClientId
              AND [RowVersion]=@ExpectedRowVersion;
            IF @@ROWCOUNT<>1
                THROW 52363,N'The fact changed on another workstation. Refresh and resolve the conflict.',1;
            SET @Action=N'ClientInfoFactUpdated';
        END;
        DECLARE @AuditEntityId nvarchar(120) =
            CONVERT(nvarchar(120), @FactId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action=@Action,@EntityType=N'ClientInfoFact',
            @EntityId=@AuditEntityId,@RequestId=@RequestId;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
    SELECT * FROM [tb_client].[ClientFacts] WHERE [FactId]=@FactId;
END;
GO

IF OBJECT_ID(N'tb_app.SaveClientCredential', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveClientCredential];
GO

CREATE PROCEDURE [tb_app].[SaveClientCredential]
    @CredentialId bigint = NULL,
    @ClientId int,
    @ResourceId bigint = NULL,
    @PersonId bigint = NULL,
    @LocalKey nvarchar(120) = NULL,
    @Name nvarchar(240),
    @Category nvarchar(120) = NULL,
    @Username nvarchar(500) = NULL,
    @LoginUrl nvarchar(1000) = NULL,
    @Notes nvarchar(1000) = NULL,
    @ReviewStatus nvarchar(24) = N'Unverified',
    @IsActive bit = 1,
    @ExpectedRowVersion binary(8) = NULL,
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
       AND IS_ROLEMEMBER(N'tb_role_client_secret_editor')<>1
       AND IS_ROLEMEMBER(N'tb_role_client_info_editor')<>1
        THROW 52370,N'Client credential editor permission is required.',1;

    SET @Name=NULLIF(LTRIM(RTRIM(@Name)),N'');
    SET @LocalKey=NULLIF(LTRIM(RTRIM(@LocalKey)),N'');
    IF @Name IS NULL THROW 52371,N'Credential name is required.',1;
    IF @ReviewStatus NOT IN
        (N'Unverified',N'Verified',N'AcceptedUnverified',N'NeedsReview')
        THROW 52321,N'The Client Info review status is invalid.',1;
    IF @ResourceId IS NOT NULL AND NOT EXISTS
        (SELECT 1 FROM [tb_client].[Resources]
         WHERE [ResourceId]=@ResourceId AND [ClientId]=@ClientId)
        THROW 52372,N'The linked resource does not belong to this client.',1;
    IF @PersonId IS NOT NULL AND NOT EXISTS
        (SELECT 1 FROM [tb_client].[People]
         WHERE [PersonId]=@PersonId AND [ClientId]=@ClientId)
        THROW 52373,N'The linked person does not belong to this client.',1;

    DECLARE @NowUtc datetime2(3)=SYSUTCDATETIME(),@Action nvarchar(120);
    BEGIN TRY
        BEGIN TRANSACTION;
        IF @CredentialId IS NULL
        BEGIN
            INSERT INTO [tb_client].[Credentials]
            (
                [ClientId],[ResourceId],[PersonId],[LocalKey],[Name],[Category],
                [Username],[LoginUrl],[Notes],[ReviewStatus],[IsActive],
                [LastVerifiedAtUtc],[CreatedByWindowsSid],[UpdatedByWindowsSid],
                [CreatedAtUtc],[UpdatedAtUtc]
            )
            VALUES
            (
                @ClientId,@ResourceId,@PersonId,@LocalKey,@Name,
                NULLIF(@Category,N''),NULLIF(@Username,N''),
                NULLIF(@LoginUrl,N''),NULLIF(@Notes,N''),@ReviewStatus,@IsActive,
                CASE WHEN @ReviewStatus=N'Verified' THEN @NowUtc END,
                @ActorSid,@ActorSid,@NowUtc,@NowUtc
            );
            SET @CredentialId=CONVERT(bigint,SCOPE_IDENTITY());
            SET @Action=N'ClientCredentialCreated';
        END
        ELSE
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 52374,N'ExpectedRowVersion is required when updating a credential.',1;
            UPDATE [tb_client].[Credentials]
            SET [ResourceId]=@ResourceId,[PersonId]=@PersonId,[LocalKey]=@LocalKey,
                [Name]=@Name,[Category]=NULLIF(@Category,N''),
                [Username]=NULLIF(@Username,N''),[LoginUrl]=NULLIF(@LoginUrl,N''),
                [Notes]=NULLIF(@Notes,N''),[ReviewStatus]=@ReviewStatus,
                [IsActive]=@IsActive,
                [LastVerifiedAtUtc]=CASE WHEN @ReviewStatus=N'Verified'
                    THEN @NowUtc ELSE [LastVerifiedAtUtc] END,
                [UpdatedByWindowsSid]=@ActorSid,[UpdatedAtUtc]=@NowUtc
            WHERE [CredentialId]=@CredentialId AND [ClientId]=@ClientId
              AND [RowVersion]=@ExpectedRowVersion;
            IF @@ROWCOUNT<>1
                THROW 52375,N'The credential changed on another workstation. Refresh and resolve the conflict.',1;
            SET @Action=N'ClientCredentialUpdated';
        END;
        DECLARE @AuditEntityId nvarchar(120) =
            CONVERT(nvarchar(120), @CredentialId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action=@Action,@EntityType=N'ClientCredential',
            @EntityId=@AuditEntityId,@RequestId=@RequestId;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
    SELECT * FROM [tb_client].[Credentials] WHERE [CredentialId]=@CredentialId;
END;
GO

IF OBJECT_ID(N'tb_app.SetClientCredentialSecret', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SetClientCredentialSecret];
GO

CREATE PROCEDURE [tb_app].[SetClientCredentialSecret]
    @SecretId bigint = NULL,
    @CredentialId bigint,
    @SecretType nvarchar(80),
    @SecretLabel nvarchar(200),
    @SecretValue nvarchar(max),
    @IsVerified bit = 0,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ActorSid varbinary(85),@IsManager bit,@IsAdmin bit,@IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,@IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,@IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1 AND IS_ROLEMEMBER(N'tb_role_client_secret_editor')<>1
        THROW 52380,N'Client secret editor permission is required.',1;

    SET @SecretType=NULLIF(LTRIM(RTRIM(@SecretType)),N'');
    SET @SecretLabel=NULLIF(LTRIM(RTRIM(@SecretLabel)),N'');
    IF @SecretType IS NULL OR @SecretLabel IS NULL
       OR NULLIF(@SecretValue,N'') IS NULL
        THROW 52381,N'Secret type, label, and value are required.',1;
    IF NOT EXISTS
        (SELECT 1 FROM [tb_client].[Credentials]
         WHERE [CredentialId]=@CredentialId AND [IsActive]=1)
        THROW 52382,N'The credential no longer exists.',1;

    DECLARE @NowUtc datetime2(3)=SYSUTCDATETIME(),@Action nvarchar(120);
    BEGIN TRY
        BEGIN TRANSACTION;
        IF @SecretId IS NULL
        BEGIN
            INSERT INTO [tb_client].[CredentialSecrets]
            (
                [CredentialId],[SecretType],[SecretLabel],[ValueEncrypted],
                [LastVerifiedAtUtc],[CreatedByWindowsSid],[UpdatedByWindowsSid],
                [CreatedAtUtc],[UpdatedAtUtc]
            )
            VALUES
            (
                @CredentialId,@SecretType,@SecretLabel,0x,
                CASE WHEN @IsVerified=1 THEN @NowUtc END,
                @ActorSid,@ActorSid,@NowUtc,@NowUtc
            );
            SET @SecretId=CONVERT(bigint,SCOPE_IDENTITY());
            SET @Action=N'ClientSecretCreated';
        END
        ELSE
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 52383,N'ExpectedRowVersion is required when replacing a secret.',1;
            UPDATE [tb_client].[CredentialSecrets]
            SET [SecretType]=@SecretType,[SecretLabel]=@SecretLabel,
                [LastVerifiedAtUtc]=CASE WHEN @IsVerified=1
                    THEN @NowUtc ELSE [LastVerifiedAtUtc] END,
                [UpdatedByWindowsSid]=@ActorSid,[UpdatedAtUtc]=@NowUtc
            WHERE [SecretId]=@SecretId AND [CredentialId]=@CredentialId
              AND [RowVersion]=@ExpectedRowVersion;
            IF @@ROWCOUNT<>1
                THROW 52384,N'The secret changed on another workstation. Refresh before replacing it.',1;
            SET @Action=N'ClientSecretReplaced';
        END;

        DECLARE @Authenticator varbinary(32)=HASHBYTES(
            N'SHA2_256',
            CONVERT(varbinary(max),
                N'ClientSecret|' + CONVERT(nvarchar(30),@SecretId)));

        OPEN SYMMETRIC KEY [tb_ClientSecretKey]
            DECRYPTION BY CERTIFICATE [tb_ClientSecretCertificate];
        UPDATE [tb_client].[CredentialSecrets]
        SET [ValueEncrypted]=EncryptByKey(
                Key_GUID(N'tb_ClientSecretKey'),
                CONVERT(varbinary(max),@SecretValue),
                1,
                @Authenticator),
            [UpdatedByWindowsSid]=@ActorSid,
            [UpdatedAtUtc]=@NowUtc
        WHERE [SecretId]=@SecretId;
        CLOSE SYMMETRIC KEY [tb_ClientSecretKey];

        IF EXISTS
        (
            SELECT 1 FROM [tb_client].[CredentialSecrets]
            WHERE [SecretId]=@SecretId
              AND ([ValueEncrypted] IS NULL OR DATALENGTH([ValueEncrypted])=0)
        )
            THROW 52385,N'The client secret could not be encrypted.',1;

        DECLARE @AuditEntityId nvarchar(120) =
            CONVERT(nvarchar(120), @SecretId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action=@Action,@EntityType=N'ClientCredentialSecret',
            @EntityId=@AuditEntityId,@RequestId=@RequestId;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF EXISTS
            (SELECT 1 FROM sys.openkeys WHERE [key_name]=N'tb_ClientSecretKey')
            CLOSE SYMMETRIC KEY [tb_ClientSecretKey];
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [SecretId],[CredentialId],[SecretType],[SecretLabel],[IsCurrent],
        [LastVerifiedAtUtc],[UpdatedAtUtc],[RowVersion]
    FROM [tb_client].[CredentialSecrets]
    WHERE [SecretId]=@SecretId;
END;
GO

IF OBJECT_ID(N'tb_app.RevealClientCredentialSecret', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[RevealClientCredentialSecret];
GO

CREATE PROCEDURE [tb_app].[RevealClientCredentialSecret]
    @SecretId bigint,
    @AccessAction nvarchar(12) = N'Reveal',
    @RequestId uniqueidentifier = NULL
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @AccessAction NOT IN (N'Reveal',N'Copy')
        THROW 52392,N'The secret access action is invalid.',1;

    DECLARE @Authenticator varbinary(32)=HASHBYTES(
        N'SHA2_256',
        CONVERT(varbinary(max),
            N'ClientSecret|' + CONVERT(nvarchar(30),@SecretId)));
    DECLARE @CredentialId bigint,@ClientId int,
            @CredentialName nvarchar(240),@SecretType nvarchar(80),
            @SecretLabel nvarchar(200),@SecretValue nvarchar(max),
            @SecretRowVersion binary(8);

    OPEN SYMMETRIC KEY [tb_ClientSecretKey]
        DECRYPTION BY CERTIFICATE [tb_ClientSecretCertificate];
    SELECT
        @CredentialId=secret.[CredentialId],
        @ClientId=credential.[ClientId],
        @CredentialName=credential.[Name],
        @SecretType=secret.[SecretType],
        @SecretLabel=secret.[SecretLabel],
        @SecretValue=CONVERT(nvarchar(max),DecryptByKey(
            secret.[ValueEncrypted],1,@Authenticator)),
        @SecretRowVersion=secret.[RowVersion]
    FROM [tb_client].[CredentialSecrets] AS secret
    INNER JOIN [tb_client].[Credentials] AS credential
        ON credential.[CredentialId]=secret.[CredentialId]
    WHERE secret.[SecretId]=@SecretId
      AND secret.[IsCurrent]=1
      AND credential.[IsActive]=1;
    IF @@ROWCOUNT<>1
    BEGIN
        CLOSE SYMMETRIC KEY [tb_ClientSecretKey];
        THROW 52393,N'The requested client secret was not found.',1;
    END;
    CLOSE SYMMETRIC KEY [tb_ClientSecretKey];

    DECLARE @AuditAction nvarchar(80) =
        CASE WHEN @AccessAction=N'Copy'
            THEN N'ClientSecretCopied' ELSE N'ClientSecretRevealed' END;
    DECLARE @AuditEntityId nvarchar(120) =
        CONVERT(nvarchar(120), @SecretId);
    EXEC [tb_security].[WriteAuditEvent]
        @Action=@AuditAction,
        @EntityType=N'ClientCredentialSecret',
        @EntityId=@AuditEntityId,
        @RequestId=@RequestId;

    SELECT
        @SecretId AS [SecretId],@CredentialId AS [CredentialId],
        @ClientId AS [ClientId],@CredentialName AS [CredentialName],
        @SecretType AS [SecretType],@SecretLabel AS [SecretLabel],
        @SecretValue AS [SecretValue],@SecretRowVersion AS [RowVersion];
END;
GO

PRINT N'Client Info beta application procedures installed.';
GO
