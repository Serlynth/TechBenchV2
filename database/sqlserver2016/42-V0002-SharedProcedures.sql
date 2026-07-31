:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'tb_security.GetCurrentAccess', N'P') IS NOT NULL
    DROP PROCEDURE [tb_security].[GetCurrentAccess];
GO

CREATE PROCEDURE [tb_security].[GetCurrentAccess]
    @UserSid varbinary(85) OUTPUT,
    @IsManager bit OUTPUT,
    @IsAdmin bit OUTPUT,
    @IsSyncOperator bit OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @LoginName nvarchar(256);
    DECLARE @DisplayName nvarchar(160);
    DECLARE @IsTechnician bit;

    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @UserSid OUTPUT,
        @LoginName = @LoginName OUTPUT,
        @DisplayName = @DisplayName OUTPUT,
        @IsTechnician = @IsTechnician OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;
END;
GO

IF OBJECT_ID(N'tb_app.GetRepositoryCapabilities', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetRepositoryCapabilities];
GO

CREATE PROCEDURE [tb_app].[GetRepositoryCapabilities]
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

    SELECT
        CONVERT(int, 2) AS [SchemaVersion],
        CONVERT(bit, 0) AS [FullTextSearchAvailable],
        CONVERT(bit, 1) AS [SupportsTickets],
        CONVERT(bit, 1) AS [SupportsWorkEntries],
        CONVERT(bit, 1) AS [SupportsPrivateNotes],
        CONVERT(bit, 1) AS [SupportsPostingLeases],
        CONVERT(bit, 1) AS [SupportsSyncLeases],
        CONVERT(bit, 1) AS [SupportsImports];
END;
GO

IF OBJECT_ID(N'tb_app.EnsureWorkspaceDefaults', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[EnsureWorkspaceDefaults];
GO

CREATE PROCEDURE [tb_app].[EnsureWorkspaceDefaults]
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

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @ChangedCount int = 0;

    DECLARE @DefaultLinks TABLE
    (
        [BuiltInKey] nvarchar(120) NOT NULL PRIMARY KEY,
        [Name] nvarchar(160) NOT NULL,
        [Url] nvarchar(2048) NOT NULL,
        [SortOrder] int NOT NULL
    );

    INSERT INTO @DefaultLinks([BuiltInKey], [Name], [Url], [SortOrder])
    VALUES
        (N'watchguard-cloud', N'WatchGuard Cloud', N'https://cloud.watchguard.com/', 0),
        (N'microsoft-365-admin', N'Microsoft 365 Admin Center', N'https://admin.microsoft.com/', 1),
        (N'barracuda-cloud-control', N'Barracuda Cloud Control', N'https://login.barracuda.com/', 2),
        (N'eset-protect', N'ESET PROTECT Console', N'https://protect.eset.com/', 3),
        (N'email2phone', N'Email2Phone', N'https://user.email2phone.net/client/#/authentication/signin', 4),
        (N'godaddy-dns', N'GoDaddy', N'https://dcc.godaddy.com/control/portfolio', 10),
        (N'network-solutions-dns', N'Network Solutions', N'https://www.networksolutions.com/my-account/login', 11);

    DECLARE @DefaultTemplates TABLE
    (
        [Name] nvarchar(160) NOT NULL PRIMARY KEY,
        [Category] nvarchar(160) NOT NULL,
        [TemplateText] nvarchar(max) NOT NULL
    );

    INSERT INTO @DefaultTemplates([Name], [Category], [TemplateText])
    VALUES
        (
            N'Exchange certificate update',
            N'Microsoft 365',
            N'Updated Exchange certificate binding, verified mail flow, and confirmed Outlook connectivity.'
        ),
        (
            N'VPN troubleshooting',
            N'Network',
            N'Investigated VPN connection failure, validated credentials and MFA status, reviewed client logs, and confirmed successful reconnect.'
        ),
        (
            N'Microsoft 365 licensing',
            N'Microsoft 365',
            N'Reviewed Microsoft 365 license assignment, adjusted user licensing, and confirmed service availability.'
        ),
        (
            N'Firewall rule change',
            N'Network',
            N'Reviewed requested firewall rule change, validated source and destination scope, applied the rule, and confirmed expected traffic.'
        ),
        (
            N'Password reset',
            N'Help Desk',
            N'Reset user password, confirmed MFA status, and verified successful sign-in with the user.'
        ),
        (
            N'Backup verification',
            N'Infrastructure',
            N'Reviewed backup job status, checked warnings or failures, and documented restore-point availability.'
        ),
        (
            N'Server reboot/maintenance',
            N'Infrastructure',
            N'Performed scheduled server maintenance, rebooted services as needed, and verified post-maintenance availability.'
        );

    BEGIN TRY
        BEGIN TRANSACTION;

        /* Range-update locking serializes concurrent first-run initialization. */
        DECLARE @ExistingOrganizationLinkCount int;
        SELECT @ExistingOrganizationLinkCount = COUNT(*)
        FROM [tb_data].[CommonLinks] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ScopeType] = N'Organization';

        DECLARE @BuiltInKey nvarchar(120);
        DECLARE @Name nvarchar(160);
        DECLARE @Url nvarchar(2048);
        DECLARE @SortOrder int;
        DECLARE @LinkId int;

        DECLARE DefaultLinkCursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT [BuiltInKey], [Name], [Url], [SortOrder]
        FROM @DefaultLinks
        ORDER BY [SortOrder], [BuiltInKey];

        OPEN DefaultLinkCursor;
        FETCH NEXT FROM DefaultLinkCursor
        INTO @BuiltInKey, @Name, @Url, @SortOrder;

        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @LinkId = NULL;

            SELECT TOP (1) @LinkId = [Id]
            FROM [tb_data].[CommonLinks] WITH (UPDLOCK, HOLDLOCK)
            WHERE [BuiltInKey] = @BuiltInKey
               OR
               (
                   [ScopeType] = N'Organization'
                   AND [Url] = @Url
               )
            ORDER BY
                CASE WHEN [BuiltInKey] = @BuiltInKey THEN 0 ELSE 1 END,
                [Id];

            IF @LinkId IS NULL
            BEGIN
                INSERT INTO [tb_data].[CommonLinks]
                (
                    [ScopeType],
                    [OwnerWindowsSid],
                    [Name],
                    [Url],
                    [UrlHash],
                    [SortOrder],
                    [BuiltInKey],
                    [CreatedByWindowsSid],
                    [UpdatedByWindowsSid],
                    [CreatedAtUtc],
                    [UpdatedAtUtc]
                )
                VALUES
                (
                    N'Organization',
                    NULL,
                    @Name,
                    @Url,
                    CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), @Url))),
                    @SortOrder,
                    @BuiltInKey,
                    @UserSid,
                    @UserSid,
                    @NowUtc,
                    @NowUtc
                );

                SET @ChangedCount += 1;
            END
            ELSE IF EXISTS
            (
                SELECT 1
                FROM [tb_data].[CommonLinks]
                WHERE [Id] = @LinkId
                  AND
                  (
                      [ScopeType] <> N'Organization'
                      OR [OwnerWindowsSid] IS NOT NULL
                      OR [Name] <> @Name
                      OR [Url] <> @Url
                      OR [UrlHash] <>
                         CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), @Url)))
                      OR [SortOrder] <> @SortOrder
                      OR [BuiltInKey] IS NULL
                      OR [BuiltInKey] <> @BuiltInKey
                  )
            )
            BEGIN
                UPDATE [tb_data].[CommonLinks]
                SET
                    [ScopeType] = N'Organization',
                    [OwnerWindowsSid] = NULL,
                    [Name] = @Name,
                    [Url] = @Url,
                    [UrlHash] =
                        CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), @Url))),
                    [SortOrder] = @SortOrder,
                    [BuiltInKey] = @BuiltInKey,
                    [UpdatedByWindowsSid] = @UserSid,
                    [UpdatedAtUtc] = @NowUtc
                WHERE [Id] = @LinkId;

                SET @ChangedCount += 1;
            END;

            FETCH NEXT FROM DefaultLinkCursor
            INTO @BuiltInKey, @Name, @Url, @SortOrder;
        END;

        CLOSE DefaultLinkCursor;
        DEALLOCATE DefaultLinkCursor;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[Templates] WITH (UPDLOCK, HOLDLOCK)
            WHERE [ScopeType] = N'Organization'
        )
        BEGIN
            INSERT INTO [tb_data].[Templates]
            (
                [ScopeType],
                [OwnerWindowsSid],
                [Name],
                [Category],
                [TemplateText],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            SELECT
                N'Organization',
                NULL,
                default_template.[Name],
                default_template.[Category],
                default_template.[TemplateText],
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            FROM @DefaultTemplates AS default_template;

            SET @ChangedCount += @@ROWCOUNT;
        END;

        IF @ChangedCount > 0
        BEGIN
            EXEC [tb_security].[WriteAuditEvent]
                @Action = N'WorkspaceDefaultsEnsured',
                @EntityType = N'WorkspaceDefaults',
                @EntityId = N'Organization';
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF CURSOR_STATUS(N'local', N'DefaultLinkCursor') >= 0
            CLOSE DefaultLinkCursor;
        IF CURSOR_STATUS(N'local', N'DefaultLinkCursor') > -3
            DEALLOCATE DefaultLinkCursor;
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_app.GetTemplates', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetTemplates];
GO

CREATE PROCEDURE [tb_app].[GetTemplates]
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

    SELECT
        [Id],
        [ScopeType],
        [Name],
        [Category],
        [TemplateText],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[Templates]
    WHERE [ScopeType] = N'Organization'
       OR ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
    ORDER BY
        CASE WHEN [ScopeType] = N'User' THEN 0 ELSE 1 END,
        [Category],
        [Name],
        [Id];
END;
GO

IF OBJECT_ID(N'tb_app.SaveTemplate', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveTemplate];
GO

CREATE PROCEDURE [tb_app].[SaveTemplate]
    @Id int = NULL,
    @ScopeType nvarchar(20) = N'User',
    @Name nvarchar(160),
    @Category nvarchar(160) = N'',
    @TemplateText nvarchar(max) = N'',
    @ExpectedRowVersion binary(8) = NULL,
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

    SET @ScopeType =
        COALESCE(NULLIF(LTRIM(RTRIM(@ScopeType)), N''), N'User');
    SET @Name = NULLIF(LTRIM(RTRIM(@Name)), N'');
    SET @Category = COALESCE(LTRIM(RTRIM(@Category)), N'');
    SET @TemplateText = COALESCE(@TemplateText, N'');

    IF @ScopeType NOT IN (N'User', N'Organization')
        THROW 51200, N'Template ScopeType must be User or Organization.', 1;
    IF @ScopeType = N'Organization' AND @IsAdmin <> 1
        THROW 51201, N'Only an Admin may save organization templates.', 1;
    IF @Name IS NULL
        THROW 51200, N'Template name is required.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51202, N'ExpectedRowVersion is required when updating a template.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @Action nvarchar(120);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[Templates]
            (
                [ScopeType],
                [OwnerWindowsSid],
                [Name],
                [Category],
                [TemplateText],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @ScopeType,
                CASE WHEN @ScopeType = N'User' THEN @UserSid ELSE NULL END,
                @Name,
                @Category,
                @TemplateText,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );

            SET @Id = CONVERT(int, SCOPE_IDENTITY());
            SET @Action = N'TemplateCreated';
        END
        ELSE
        BEGIN
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[Templates]
                WHERE [Id] = @Id
                  AND
                  (
                      ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
                      OR ([ScopeType] = N'Organization' AND @IsAdmin = 1)
                  )
            )
                THROW 51203, N'The template does not exist or cannot be edited by the current user.', 1;

            UPDATE [tb_data].[Templates]
            SET
                [ScopeType] = @ScopeType,
                [OwnerWindowsSid] =
                    CASE WHEN @ScopeType = N'User' THEN @UserSid ELSE NULL END,
                [Name] = @Name,
                [Category] = @Category,
                [TemplateText] = @TemplateText,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51204, N'The template changed after it was loaded.', 1;
            SET @Action = N'TemplateUpdated';
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
            @EntityType = N'Template',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [ScopeType],
        [Name],
        [Category],
        [TemplateText],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[Templates]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteTemplate', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteTemplate];
GO

CREATE PROCEDURE [tb_app].[DeleteTemplate]
    @Id int,
    @ExpectedRowVersion binary(8),
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

    DELETE FROM [tb_data].[Templates]
    WHERE [Id] = @Id
      AND [RowVersion] = @ExpectedRowVersion
      AND
      (
          ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
          OR ([ScopeType] = N'Organization' AND @IsAdmin = 1)
      );

    IF @@ROWCOUNT = 0
        THROW 51205, N'The template was not found, changed, or cannot be deleted by the current user.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'TemplateDeleted',
        @EntityType = N'Template',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.GetCommonLinks', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetCommonLinks];
GO

CREATE PROCEDURE [tb_app].[GetCommonLinks]
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

    SELECT
        [Id],
        [ScopeType],
        [Name],
        [Url],
        [SortOrder],
        [BuiltInKey],
        [CreatedAtUtc] AS [CreatedAt],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[CommonLinks]
    WHERE [ScopeType] = N'Organization'
       OR ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
    ORDER BY
        CASE WHEN [ScopeType] = N'Organization' THEN 0 ELSE 1 END,
        [SortOrder],
        [Name],
        [Id];
END;
GO

IF OBJECT_ID(N'tb_app.SaveCommonLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveCommonLink];
GO

CREATE PROCEDURE [tb_app].[SaveCommonLink]
    @Id int = NULL,
    @ScopeType nvarchar(20) = N'User',
    @Name nvarchar(160),
    @Url nvarchar(2048),
    @SortOrder int = 0,
    @BuiltInKey nvarchar(120) = NULL,
    @ExpectedRowVersion binary(8) = NULL,
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

    SET @ScopeType =
        COALESCE(NULLIF(LTRIM(RTRIM(@ScopeType)), N''), N'User');
    SET @Name = NULLIF(LTRIM(RTRIM(@Name)), N'');
    SET @Url = NULLIF(LTRIM(RTRIM(@Url)), N'');
    SET @BuiltInKey = NULLIF(LTRIM(RTRIM(@BuiltInKey)), N'');

    IF @ScopeType NOT IN (N'User', N'Organization')
        THROW 51210, N'Common-link ScopeType must be User or Organization.', 1;
    IF @ScopeType = N'Organization' AND @IsAdmin <> 1
        THROW 51211, N'Only an Admin may save organization Common Links.', 1;
    IF @BuiltInKey IS NOT NULL AND @IsAdmin <> 1
        THROW 51211, N'Only an Admin may assign a built-in Common Link key.', 1;
    IF @Name IS NULL OR @Url IS NULL
        THROW 51210, N'Common-link name and URL are required.', 1;
    IF @Id IS NOT NULL
       AND EXISTS
       (
           SELECT 1
           FROM [tb_data].[CommonLinks]
           WHERE [Id] = @Id
             AND [BuiltInKey] IS NOT NULL
       )
        THROW 51216, N'Built-in Common Links cannot be changed.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51212, N'ExpectedRowVersion is required when updating a Common Link.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @UrlHash binary(32) =
        CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), @Url)));

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[CommonLinks]
            (
                [ScopeType],
                [OwnerWindowsSid],
                [Name],
                [Url],
                [UrlHash],
                [SortOrder],
                [BuiltInKey],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @ScopeType,
                CASE WHEN @ScopeType = N'User' THEN @UserSid ELSE NULL END,
                @Name,
                @Url,
                @UrlHash,
                @SortOrder,
                @BuiltInKey,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );
            SET @Id = CONVERT(int, SCOPE_IDENTITY());
        END
        ELSE
        BEGIN
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[CommonLinks]
                WHERE [Id] = @Id
                  AND
                  (
                      ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
                      OR ([ScopeType] = N'Organization' AND @IsAdmin = 1)
                  )
            )
                THROW 51213, N'The Common Link does not exist or cannot be edited by the current user.', 1;

            UPDATE [tb_data].[CommonLinks]
            SET
                [ScopeType] = @ScopeType,
                [OwnerWindowsSid] =
                    CASE WHEN @ScopeType = N'User' THEN @UserSid ELSE NULL END,
                [Name] = @Name,
                [Url] = @Url,
                [UrlHash] = @UrlHash,
                [SortOrder] = @SortOrder,
                [BuiltInKey] = @BuiltInKey,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51214, N'The Common Link changed after it was loaded.', 1;
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'CommonLinkSaved',
            @EntityType = N'CommonLink',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [ScopeType],
        [Name],
        [Url],
        [SortOrder],
        [BuiltInKey],
        [CreatedAtUtc] AS [CreatedAt],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[CommonLinks]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteCommonLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteCommonLink];
GO

CREATE PROCEDURE [tb_app].[DeleteCommonLink]
    @Id int,
    @ExpectedRowVersion binary(8),
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

    IF EXISTS
    (
        SELECT 1
        FROM [tb_data].[CommonLinks]
        WHERE [Id] = @Id
          AND [BuiltInKey] IS NOT NULL
    )
        THROW 51216, N'Built-in Common Links cannot be removed.', 1;

    DELETE FROM [tb_data].[CommonLinks]
    WHERE [Id] = @Id
      AND [RowVersion] = @ExpectedRowVersion
      AND
      (
          ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
          OR ([ScopeType] = N'Organization' AND @IsAdmin = 1)
      );

    IF @@ROWCOUNT = 0
        THROW 51215, N'The Common Link was not found, changed, or cannot be deleted by the current user.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'CommonLinkDeleted',
        @EntityType = N'CommonLink',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.GetSettings', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetSettings];
GO

CREATE PROCEDURE [tb_app].[GetSettings]
    @ScopeType nvarchar(40) = NULL,
    @DeviceId uniqueidentifier = NULL
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

    ;WITH settings AS
    (
        SELECT
            CONVERT(nvarchar(20), N'Organization') AS [ScopeType],
            [SettingKey],
            [SettingValue],
            [UpdatedAtUtc],
            [RowVersion],
            CONVERT(int, 1) AS [ScopePriority]
        FROM [tb_data].[OrganizationSettings]

        UNION ALL

        SELECT
            CONVERT(nvarchar(20), N'User') AS [ScopeType],
            [SettingKey],
            [SettingValue],
            [UpdatedAtUtc],
            [RowVersion],
            CONVERT(int, 2) AS [ScopePriority]
        FROM [tb_user].[UserSettings]
        WHERE [OwnerWindowsSid] = @UserSid
    ),
    ranked AS
    (
        SELECT
            [ScopeType],
            [SettingKey],
            [SettingValue],
            [UpdatedAtUtc],
            [RowVersion],
            ROW_NUMBER() OVER
            (
                PARTITION BY [SettingKey]
                ORDER BY [ScopePriority] DESC
            ) AS [Rank]
        FROM settings
    )
    SELECT
        [ScopeType],
        [SettingKey],
        [SettingValue],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM ranked
    WHERE [Rank] = 1
    ORDER BY [SettingKey];
END;
GO

IF OBJECT_ID(N'tb_app.SaveUserSetting', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveUserSetting];
GO

CREATE PROCEDURE [tb_app].[SaveUserSetting]
    @SettingKey nvarchar(200),
    @SettingValue nvarchar(max),
    @ExpectedRowVersion binary(8) = NULL
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

    SET @SettingKey = NULLIF(LTRIM(RTRIM(@SettingKey)), N'');
    SET @SettingValue = COALESCE(@SettingValue, N'');
    IF @SettingKey IS NULL
        THROW 51220, N'SettingKey is required.', 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF EXISTS
        (
            SELECT 1
            FROM [tb_user].[UserSettings] WITH (UPDLOCK, HOLDLOCK)
            WHERE [OwnerWindowsSid] = @UserSid
              AND [SettingKey] = @SettingKey
        )
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 51221, N'ExpectedRowVersion is required for an existing user setting.', 1;

            UPDATE [tb_user].[UserSettings]
            SET
                [SettingValue] = @SettingValue,
                [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [OwnerWindowsSid] = @UserSid
              AND [SettingKey] = @SettingKey
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51222, N'The user setting changed after it was loaded.', 1;
        END
        ELSE
        BEGIN
            IF @ExpectedRowVersion IS NOT NULL
                THROW 51222, N'The user setting changed after it was loaded.', 1;

            INSERT INTO [tb_user].[UserSettings]
            (
                [OwnerWindowsSid],
                [SettingKey],
                [SettingValue]
            )
            VALUES
            (
                @UserSid,
                @SettingKey,
                @SettingValue
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
        CONVERT(nvarchar(20), N'User') AS [ScopeType],
        [SettingKey],
        [SettingValue],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_user].[UserSettings]
    WHERE [OwnerWindowsSid] = @UserSid
      AND [SettingKey] = @SettingKey;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteUserSetting', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteUserSetting];
GO

CREATE PROCEDURE [tb_app].[DeleteUserSetting]
    @SettingKey nvarchar(200),
    @ExpectedRowVersion binary(8) = NULL
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

    DELETE FROM [tb_user].[UserSettings]
    WHERE [OwnerWindowsSid] = @UserSid
      AND [SettingKey] = @SettingKey
      AND (@ExpectedRowVersion IS NULL OR [RowVersion] = @ExpectedRowVersion);
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveOrganizationSetting', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveOrganizationSetting];
GO

CREATE PROCEDURE [tb_app].[AdminSaveOrganizationSetting]
    @SettingKey nvarchar(200),
    @SettingValue nvarchar(max),
    @ExpectedRowVersion binary(8) = NULL,
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
        THROW 51223, N'Only an Admin may save organization settings.', 1;

    SET @SettingKey = NULLIF(LTRIM(RTRIM(@SettingKey)), N'');
    SET @SettingValue = COALESCE(@SettingValue, N'');
    IF @SettingKey IS NULL
        THROW 51220, N'SettingKey is required.', 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF EXISTS
        (
            SELECT 1
            FROM [tb_data].[OrganizationSettings] WITH (UPDLOCK, HOLDLOCK)
            WHERE [SettingKey] = @SettingKey
        )
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 51221, N'ExpectedRowVersion is required for an existing organization setting.', 1;

            UPDATE [tb_data].[OrganizationSettings]
            SET
                [SettingValue] = @SettingValue,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [SettingKey] = @SettingKey
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51222, N'The organization setting changed after it was loaded.', 1;
        END
        ELSE
        BEGIN
            IF @ExpectedRowVersion IS NOT NULL
                THROW 51222, N'The organization setting changed after it was loaded.', 1;

            INSERT INTO [tb_data].[OrganizationSettings]
            (
                [SettingKey],
                [SettingValue],
                [UpdatedByWindowsSid]
            )
            VALUES
            (
                @SettingKey,
                @SettingValue,
                @UserSid
            );
        END;

        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'OrganizationSettingSaved',
            @EntityType = N'OrganizationSetting',
            @EntityId = @SettingKey,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        CONVERT(nvarchar(20), N'Organization') AS [ScopeType],
        [SettingKey],
        [SettingValue],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[OrganizationSettings]
    WHERE [SettingKey] = @SettingKey;
END;
GO

IF OBJECT_ID(N'tb_app.AdminDeleteOrganizationSetting', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminDeleteOrganizationSetting];
GO

CREATE PROCEDURE [tb_app].[AdminDeleteOrganizationSetting]
    @SettingKey nvarchar(200),
    @ExpectedRowVersion binary(8) = NULL,
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
        THROW 51223, N'Only an Admin may delete organization settings.', 1;

    DELETE FROM [tb_data].[OrganizationSettings]
    WHERE [SettingKey] = @SettingKey
      AND (@ExpectedRowVersion IS NULL OR [RowVersion] = @ExpectedRowVersion);

    IF @@ROWCOUNT > 0
    BEGIN
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'OrganizationSettingDeleted',
            @EntityType = N'OrganizationSetting',
            @EntityId = @SettingKey,
            @RequestId = @RequestId;
    END;
END;
GO

IF OBJECT_ID(N'tb_app.GetClientAliases', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetClientAliases];
GO

CREATE PROCEDURE [tb_app].[GetClientAliases]
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

    ;WITH aliases AS
    (
        SELECT
            [Id],
            [ScopeType],
            [Alias],
            [ClientId],
            [UpdatedAtUtc],
            [RowVersion],
            CASE WHEN [ScopeType] = N'User' THEN 2 ELSE 1 END AS [ScopePriority]
        FROM [tb_data].[ClientAliases]
        WHERE [ScopeType] = N'Organization'
           OR ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
    ),
    ranked AS
    (
        SELECT
            [Id],
            [ScopeType],
            [Alias],
            [ClientId],
            [UpdatedAtUtc],
            [RowVersion],
            ROW_NUMBER() OVER
            (
                PARTITION BY [Alias]
                ORDER BY [ScopePriority] DESC, [Id] DESC
            ) AS [Rank]
        FROM aliases
    )
    SELECT
        [Id],
        [ScopeType],
        [Alias],
        [ClientId],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM ranked
    WHERE [Rank] = 1
    ORDER BY [Alias];
END;
GO

IF OBJECT_ID(N'tb_app.SaveClientAlias', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveClientAlias];
GO

CREATE PROCEDURE [tb_app].[SaveClientAlias]
    @Id bigint = NULL,
    @ScopeType nvarchar(20) = N'User',
    @Alias nvarchar(240),
    @ClientId int,
    @ExpectedRowVersion binary(8) = NULL,
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

    SET @ScopeType =
        COALESCE(NULLIF(LTRIM(RTRIM(@ScopeType)), N''), N'User');
    SET @Alias = NULLIF(LTRIM(RTRIM(@Alias)), N'');

    IF @ScopeType NOT IN (N'User', N'Organization')
        THROW 51230, N'Client-alias ScopeType must be User or Organization.', 1;
    IF @ScopeType = N'Organization' AND @IsAdmin <> 1
        THROW 51231, N'Only an Admin may save organization client aliases.', 1;
    IF @Alias IS NULL
        THROW 51230, N'Client alias is required.', 1;
    IF NOT EXISTS (SELECT 1 FROM [tb_data].[Clients] WHERE [Id] = @ClientId)
        THROW 51230, N'The selected client does not exist.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51232, N'ExpectedRowVersion is required when updating a client alias.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[ClientAliases]
            (
                [ScopeType],
                [OwnerWindowsSid],
                [Alias],
                [ClientId],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @ScopeType,
                CASE WHEN @ScopeType = N'User' THEN @UserSid ELSE NULL END,
                @Alias,
                @ClientId,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );
            SET @Id = CONVERT(bigint, SCOPE_IDENTITY());
        END
        ELSE
        BEGIN
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[ClientAliases]
                WHERE [Id] = @Id
                  AND
                  (
                      ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
                      OR ([ScopeType] = N'Organization' AND @IsAdmin = 1)
                  )
            )
                THROW 51233, N'The client alias does not exist or cannot be edited by the current user.', 1;

            UPDATE [tb_data].[ClientAliases]
            SET
                [ScopeType] = @ScopeType,
                [OwnerWindowsSid] =
                    CASE WHEN @ScopeType = N'User' THEN @UserSid ELSE NULL END,
                [Alias] = @Alias,
                [ClientId] = @ClientId,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51234, N'The client alias changed after it was loaded.', 1;
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'ClientAliasSaved',
            @EntityType = N'ClientAlias',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [ScopeType],
        [Alias],
        [ClientId],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[ClientAliases]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteClientAlias', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteClientAlias];
GO

CREATE PROCEDURE [tb_app].[DeleteClientAlias]
    @Id bigint,
    @ExpectedRowVersion binary(8),
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

    DELETE FROM [tb_data].[ClientAliases]
    WHERE [Id] = @Id
      AND [RowVersion] = @ExpectedRowVersion
      AND
      (
          ([ScopeType] = N'User' AND [OwnerWindowsSid] = @UserSid)
          OR ([ScopeType] = N'Organization' AND @IsAdmin = 1)
      );

    IF @@ROWCOUNT = 0
        THROW 51235, N'The client alias was not found, changed, or cannot be deleted by the current user.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'ClientAliasDeleted',
        @EntityType = N'ClientAlias',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.GetClientExternalIdentities', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetClientExternalIdentities];
GO

CREATE PROCEDURE [tb_app].[GetClientExternalIdentities]
    @ClientId int = NULL,
    @SourceSystem nvarchar(40) = NULL
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

    SELECT
        [Id],
        [ClientId],
        [SourceSystem],
        [ExternalId],
        [ExternalName],
        [LastSyncedAtUtc] AS [LastSyncedAt],
        [RowVersion]
    FROM [tb_data].[ClientExternalIdentities]
    WHERE (@ClientId IS NULL OR [ClientId] = @ClientId)
      AND (@SourceSystem IS NULL OR [SourceSystem] = @SourceSystem)
    ORDER BY [ClientId], [SourceSystem], [ExternalId];
END;
GO

IF OBJECT_ID(N'tb_app.SyncUpsertClientExternalIdentity', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncUpsertClientExternalIdentity];
GO

CREATE PROCEDURE [tb_app].[SyncUpsertClientExternalIdentity]
    @ClientId int,
    @SourceSystem nvarchar(40),
    @ExternalId nvarchar(500),
    @ExternalName nvarchar(240) = NULL,
    @LastSyncedAtUtc datetime2(3) = NULL
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
        THROW 51240, N'Only an Admin or Sync Operator may save external client identities.', 1;

    SET @SourceSystem = NULLIF(LTRIM(RTRIM(@SourceSystem)), N'');
    SET @ExternalId = NULLIF(LTRIM(RTRIM(@ExternalId)), N'');
    SET @ExternalName = NULLIF(LTRIM(RTRIM(@ExternalName)), N'');
    IF @SourceSystem IS NULL OR @ExternalId IS NULL
        THROW 51241, N'SourceSystem and ExternalId are required.', 1;
    IF NOT EXISTS (SELECT 1 FROM [tb_data].[Clients] WHERE [Id] = @ClientId)
        THROW 51241, N'The selected client does not exist.', 1;

    DECLARE @Id bigint;
    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @Id = [Id]
        FROM [tb_data].[ClientExternalIdentities] WITH (UPDLOCK, HOLDLOCK)
        WHERE [SourceSystem] = @SourceSystem
          AND [ExternalId] = @ExternalId;

        IF @Id IS NULL
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
                @SourceSystem,
                @ExternalId,
                @ExternalName,
                @LastSyncedAtUtc,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );
            SET @Id = CONVERT(bigint, SCOPE_IDENTITY());
        END
        ELSE
        BEGIN
            UPDATE [tb_data].[ClientExternalIdentities]
            SET
                [ClientId] = @ClientId,
                [ExternalName] = @ExternalName,
                [LastSyncedAtUtc] = @LastSyncedAtUtc,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [ClientId],
        [SourceSystem],
        [ExternalId],
        [ExternalName],
        [LastSyncedAtUtc] AS [LastSyncedAt],
        [RowVersion]
    FROM [tb_data].[ClientExternalIdentities]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.SyncUpsertTicketStatusOption', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncUpsertTicketStatusOption];
GO

CREATE PROCEDURE [tb_app].[SyncUpsertTicketStatusOption]
    @Name nvarchar(160),
    @Source nvarchar(40) = N'WHD',
    @ExternalId nvarchar(240) = NULL,
    @WhdStatusTypeId int = NULL,
    @IsClosed bit = 0,
    @SyncedAtUtc datetime2(3) = NULL,
    @LastSyncedAtUtc datetime2(3) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    /* @LastSyncedAtUtc is the desktop repository contract. Keep
       @SyncedAtUtc as the snapshot/deployment compatibility alias. */
    SET @SyncedAtUtc = COALESCE(@LastSyncedAtUtc, @SyncedAtUtc);

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
        THROW 51250, N'Only an Admin or Sync Operator may synchronize ticket statuses.', 1;

    SET @Name = NULLIF(LTRIM(RTRIM(@Name)), N'');
    SET @Source = COALESCE(NULLIF(LTRIM(RTRIM(@Source)), N''), N'WHD');
    SET @ExternalId = NULLIF(LTRIM(RTRIM(@ExternalId)), N'');
    IF @Name IS NULL
        THROW 51251, N'Ticket-status name is required.', 1;

    DECLARE @Id int;
    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @Id = [Id]
        FROM [tb_data].[TicketStatusOptions] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Source] = @Source
          AND
          (
              ([ExternalId] = @ExternalId)
              OR (@ExternalId IS NULL AND [ExternalId] IS NULL AND [Name] = @Name)
          );

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[TicketStatusOptions]
            (
                [Name],
                [Source],
                [ExternalId],
                [WhdStatusTypeId],
                [IsClosed],
                [LastSyncedAtUtc],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @Name,
                @Source,
                @ExternalId,
                @WhdStatusTypeId,
                @IsClosed,
                @SyncedAtUtc,
                @NowUtc,
                @NowUtc
            );
            SET @Id = CONVERT(int, SCOPE_IDENTITY());
        END
        ELSE
        BEGIN
            UPDATE [tb_data].[TicketStatusOptions]
            SET
                [Name] = @Name,
                [WhdStatusTypeId] = @WhdStatusTypeId,
                [IsClosed] = @IsClosed,
                [LastSyncedAtUtc] = @SyncedAtUtc,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [Name],
        [Source],
        [ExternalId],
        [WhdStatusTypeId],
        [IsClosed],
        [LastSyncedAtUtc] AS [LastSyncedAt],
        [RowVersion]
    FROM [tb_data].[TicketStatusOptions]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.SyncUpsertTicket', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SyncUpsertTicket];
GO

CREATE PROCEDURE [tb_app].[SyncUpsertTicket]
    @TicketNumber nvarchar(120),
    @ClientId int,
    @Subject nvarchar(500) = N'',
    @Status nvarchar(160) = N'Open',
    @Source nvarchar(40) = N'WHD',
    @ExternalId nvarchar(240) = NULL,
    @WhdStatusTypeId int = NULL,
    @IsClosed bit = 0,
    @SyncedAtUtc datetime2(3) = NULL,
    @LastSyncedAtUtc datetime2(3) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    /* @LastSyncedAtUtc is the desktop repository contract. Keep
       @SyncedAtUtc as the snapshot/deployment compatibility alias. */
    SET @SyncedAtUtc = COALESCE(@LastSyncedAtUtc, @SyncedAtUtc);

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
        THROW 51260, N'Only an Admin or Sync Operator may synchronize tickets.', 1;

    SET @TicketNumber = NULLIF(LTRIM(RTRIM(@TicketNumber)), N'');
    SET @Subject = COALESCE(LTRIM(RTRIM(@Subject)), N'');
    SET @Status = COALESCE(NULLIF(LTRIM(RTRIM(@Status)), N''), N'Open');
    SET @Source = COALESCE(NULLIF(LTRIM(RTRIM(@Source)), N''), N'WHD');
    SET @ExternalId = NULLIF(LTRIM(RTRIM(@ExternalId)), N'');

    IF @TicketNumber IS NULL
        THROW 51261, N'TicketNumber is required.', 1;
    IF NOT EXISTS (SELECT 1 FROM [tb_data].[Clients] WHERE [Id] = @ClientId)
        THROW 51261, N'The selected client does not exist.', 1;

    DECLARE @Id int;
    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @Id = [Id]
        FROM [tb_data].[Tickets] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Source] = @Source
          AND
          (
              ([ExternalId] = @ExternalId)
              OR (@ExternalId IS NULL AND [ExternalId] IS NULL
                  AND [TicketNumber] = @TicketNumber)
          );

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[Tickets]
            (
                [TicketNumber],
                [ClientId],
                [Subject],
                [Status],
                [Source],
                [ExternalId],
                [WhdStatusTypeId],
                [IsClosed],
                [LastSyncedAtUtc],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @TicketNumber,
                @ClientId,
                @Subject,
                @Status,
                @Source,
                @ExternalId,
                @WhdStatusTypeId,
                @IsClosed,
                @SyncedAtUtc,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );
            SET @Id = CONVERT(int, SCOPE_IDENTITY());
        END
        ELSE
        BEGIN
            UPDATE [tb_data].[Tickets]
            SET
                [TicketNumber] = @TicketNumber,
                [ClientId] = @ClientId,
                [Subject] = @Subject,
                [Status] = @Status,
                [WhdStatusTypeId] = @WhdStatusTypeId,
                [IsClosed] = @IsClosed,
                [LastSyncedAtUtc] = @SyncedAtUtc,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        [Id],
        [TicketNumber],
        [ClientId],
        [Subject],
        [Status],
        [Source],
        [ExternalId],
        [WhdStatusTypeId],
        [IsClosed],
        [LastSyncedAtUtc] AS [LastSyncedAt],
        [RowVersion]
    FROM [tb_data].[Tickets]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.AdminMergeClients', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminMergeClients];
GO

CREATE PROCEDURE [tb_app].[AdminMergeClients]
    @TargetClientId int = NULL,
    @SourceClientId int = NULL,
    @ExpectedTargetRowVersion binary(8) = NULL,
    @ExpectedSourceRowVersion binary(8) = NULL,
    @WhdClientId int = NULL,
    @SageClientId int = NULL,
    @ExpectedWhdRowVersion binary(8) = NULL,
    @ExpectedSageRowVersion binary(8) = NULL,
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
        THROW 51270, N'Only an Admin may merge clients.', 1;

    SET @WhdClientId = COALESCE(@TargetClientId, @WhdClientId);
    SET @SageClientId = COALESCE(@SourceClientId, @SageClientId);
    SET @ExpectedWhdRowVersion =
        COALESCE(@ExpectedTargetRowVersion, @ExpectedWhdRowVersion);
    SET @ExpectedSageRowVersion =
        COALESCE(@ExpectedSourceRowVersion, @ExpectedSageRowVersion);

    IF @WhdClientId IS NULL
       OR @SageClientId IS NULL
       OR @ExpectedWhdRowVersion IS NULL
       OR @ExpectedSageRowVersion IS NULL
        THROW 51271, N'Both client IDs and expected row versions are required.', 1;
    IF @WhdClientId = @SageClientId
        THROW 51271, N'WhdClientId and SageClientId must be different.', 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[Clients] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @WhdClientId
              AND [RowVersion] = @ExpectedWhdRowVersion
        )
            THROW 51272, N'The target client changed or no longer exists.', 1;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[Clients] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @SageClientId
              AND [RowVersion] = @ExpectedSageRowVersion
        )
            THROW 51273, N'The source client changed or no longer exists.', 1;

        UPDATE target_client
        SET
            [Source] =
                CASE
                    WHEN target_client.[Source] = N'Both'
                      OR source_client.[Source] = N'Both'
                      OR
                      (
                          target_client.[Source] IN (N'WHD', N'Sage')
                          AND source_client.[Source] IN (N'WHD', N'Sage')
                          AND target_client.[Source] <> source_client.[Source]
                      )
                        THEN N'Both'
                    WHEN target_client.[Source] = N'Manual'
                        THEN source_client.[Source]
                    ELSE target_client.[Source]
                END,
            [IsActive] =
                CONVERT(
                    bit,
                    CASE
                        WHEN target_client.[IsActive] = 1
                          OR source_client.[IsActive] = 1
                            THEN 1
                        ELSE 0
                    END),
            [LastSyncedAtUtc] =
                CASE
                    WHEN target_client.[LastSyncedAtUtc] IS NULL
                        THEN source_client.[LastSyncedAtUtc]
                    WHEN source_client.[LastSyncedAtUtc] IS NULL
                        THEN target_client.[LastSyncedAtUtc]
                    WHEN source_client.[LastSyncedAtUtc] > target_client.[LastSyncedAtUtc]
                        THEN source_client.[LastSyncedAtUtc]
                    ELSE target_client.[LastSyncedAtUtc]
                END,
            [WhdLocationName] =
                COALESCE(target_client.[WhdLocationName], source_client.[WhdLocationName]),
            [WhdContactName] =
                COALESCE(target_client.[WhdContactName], source_client.[WhdContactName]),
            [SageCustomerId] =
                COALESCE(target_client.[SageCustomerId], source_client.[SageCustomerId]),
            [SageCustomerName] =
                COALESCE(target_client.[SageCustomerName], source_client.[SageCustomerName]),
            [SageContactName] =
                COALESCE(target_client.[SageContactName], source_client.[SageContactName]),
            [SageTelephone] =
                COALESCE(target_client.[SageTelephone], source_client.[SageTelephone]),
            [MatchStatus] = N'Matched',
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        FROM [tb_data].[Clients] AS target_client
        CROSS JOIN [tb_data].[Clients] AS source_client
        WHERE target_client.[Id] = @WhdClientId
          AND source_client.[Id] = @SageClientId;

        UPDATE [tb_data].[Tickets]
        SET
            [ClientId] = @WhdClientId,
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ClientId] = @SageClientId;

        UPDATE [tb_data].[WorkEntries]
        SET
            [ClientId] = @WhdClientId,
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ClientId] = @SageClientId;

        UPDATE [tb_data].[ClientAliases]
        SET
            [ClientId] = @WhdClientId,
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ClientId] = @SageClientId;

        DELETE source_identity
        FROM [tb_data].[ClientExternalIdentities] AS source_identity
        WHERE source_identity.[ClientId] = @SageClientId
          AND EXISTS
          (
              SELECT 1
              FROM [tb_data].[ClientExternalIdentities] AS target_identity
              WHERE target_identity.[ClientId] = @WhdClientId
                AND target_identity.[SourceSystem] = source_identity.[SourceSystem]
                AND target_identity.[ExternalId] = source_identity.[ExternalId]
          );

        UPDATE [tb_data].[ClientExternalIdentities]
        SET
            [ClientId] = @WhdClientId,
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ClientId] = @SageClientId;

        IF OBJECT_ID(N'tb_client.ReparentClientGraph', N'P') IS NOT NULL
            EXEC [tb_client].[ReparentClientGraph]
                @SourceClientId = @SageClientId,
                @TargetClientId = @WhdClientId,
                @ActorWindowsSid = @UserSid;

        DELETE FROM [tb_data].[Clients]
        WHERE [Id] = @SageClientId
          AND [RowVersion] = @ExpectedSageRowVersion;

        IF @@ROWCOUNT = 0
            THROW 51273, N'The source client changed during the merge.', 1;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @WhdClientId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'ClientMerged',
            @EntityType = N'Client',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    EXEC [tb_app].[GetClient] @Id = @WhdClientId;
END;
GO

IF OBJECT_ID(N'tb_app.ReconcileClientMatches', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ReconcileClientMatches];
GO

CREATE PROCEDURE [tb_app].[ReconcileClientMatches]
    @Mode nvarchar(20) = N'Exact'
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
        THROW 51280, N'Only an Admin or Sync Operator may reconcile client matches.', 1;

    SET @Mode = COALESCE(NULLIF(LTRIM(RTRIM(@Mode)), N''), N'Exact');
    IF @Mode NOT IN (N'Exact', N'Strong', N'Safe')
        THROW 51281, N'Mode must be Exact, Strong, or Safe.', 1;

    DECLARE @MatchedCount int = 0;

    IF @Mode = N'Exact'
    BEGIN
        UPDATE client
        SET
            [MatchStatus] = N'Matched',
            [UpdatedByWindowsSid] = @UserSid,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        FROM [tb_data].[Clients] AS client
        WHERE client.[MatchStatus] <> N'Matched'
          AND EXISTS
          (
              SELECT 1
              FROM [tb_data].[ClientExternalIdentities] AS whd_identity
              WHERE whd_identity.[ClientId] = client.[Id]
                AND whd_identity.[SourceSystem] = N'WHD'
          )
          AND EXISTS
          (
              SELECT 1
              FROM [tb_data].[ClientExternalIdentities] AS sage_identity
              WHERE sage_identity.[ClientId] = client.[Id]
                AND sage_identity.[SourceSystem] = N'Sage'
          );

        SET @MatchedCount = @@ROWCOUNT;
    END;

    SELECT
        @Mode AS [Mode],
        @MatchedCount AS [MatchedCount],
        CONVERT(bit, CASE WHEN @Mode = N'Exact' THEN 1 ELSE 0 END)
            AS [AutomaticChangesApplied];
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveTemplate', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveTemplate];
GO

CREATE PROCEDURE [tb_app].[AdminSaveTemplate]
    @Id int = NULL,
    @Name nvarchar(160),
    @Category nvarchar(160) = N'',
    @TemplateText nvarchar(max) = N'',
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    EXEC [tb_app].[SaveTemplate]
        @Id = @Id,
        @ScopeType = N'User',
        @Name = @Name,
        @Category = @Category,
        @TemplateText = @TemplateText,
        @ExpectedRowVersion = @ExpectedRowVersion,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.AdminDeleteTemplate', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminDeleteTemplate];
GO

CREATE PROCEDURE [tb_app].[AdminDeleteTemplate]
    @Id int,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    EXEC [tb_app].[DeleteTemplate]
        @Id = @Id,
        @ExpectedRowVersion = @ExpectedRowVersion,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveCommonLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveCommonLink];
GO

CREATE PROCEDURE [tb_app].[AdminSaveCommonLink]
    @Id int = NULL,
    @Name nvarchar(160),
    @Url nvarchar(2048),
    @SortOrder int = 0,
    @BuiltInKey nvarchar(120) = NULL,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    EXEC [tb_app].[SaveCommonLink]
        @Id = @Id,
        @ScopeType = N'User',
        @Name = @Name,
        @Url = @Url,
        @SortOrder = @SortOrder,
        @BuiltInKey = @BuiltInKey,
        @ExpectedRowVersion = @ExpectedRowVersion,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.AdminDeleteCommonLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminDeleteCommonLink];
GO

CREATE PROCEDURE [tb_app].[AdminDeleteCommonLink]
    @Id int,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    EXEC [tb_app].[DeleteCommonLink]
        @Id = @Id,
        @ExpectedRowVersion = @ExpectedRowVersion,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveClientAlias', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveClientAlias];
GO

CREATE PROCEDURE [tb_app].[AdminSaveClientAlias]
    @Alias nvarchar(240),
    @ClientId int,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;
    DECLARE @Id bigint;
    DECLARE @ExpectedRowVersion binary(8);

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT
        @Id = [Id],
        @ExpectedRowVersion = [RowVersion]
    FROM [tb_data].[ClientAliases]
    WHERE [ScopeType] = N'User'
      AND [OwnerWindowsSid] = @UserSid
      AND [Alias] = @Alias;

    EXEC [tb_app].[SaveClientAlias]
        @Id = @Id,
        @ScopeType = N'User',
        @Alias = @Alias,
        @ClientId = @ClientId,
        @ExpectedRowVersion = @ExpectedRowVersion,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.AdminDeleteClientAlias', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminDeleteClientAlias];
GO

CREATE PROCEDURE [tb_app].[AdminDeleteClientAlias]
    @Alias nvarchar(240),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;
    DECLARE @Id bigint;

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SELECT @Id = [Id]
    FROM [tb_data].[ClientAliases]
    WHERE [ScopeType] = N'User'
      AND [OwnerWindowsSid] = @UserSid
      AND [Alias] = @Alias;

    IF @Id IS NOT NULL
    BEGIN
        DELETE FROM [tb_data].[ClientAliases]
        WHERE [Id] = @Id
          AND [OwnerWindowsSid] = @UserSid;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'ClientAliasDeleted',
            @EntityType = N'ClientAlias',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;
    END;
END;
GO

IF OBJECT_ID(N'tb_app.SaveSetting', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveSetting];
GO

CREATE PROCEDURE [tb_app].[SaveSetting]
    @ScopeType nvarchar(40) = N'User',
    @SettingKey nvarchar(200),
    @SettingValue nvarchar(max),
    @DeviceId uniqueidentifier = NULL,
    @ExpectedRowVersion binary(8) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @ScopeType <> N'User'
        THROW 51224, N'The desktop SaveSetting contract supports user-scoped settings only.', 1;

    EXEC [tb_app].[SaveUserSetting]
        @SettingKey = @SettingKey,
        @SettingValue = @SettingValue,
        @ExpectedRowVersion = @ExpectedRowVersion;
END;
GO

IF OBJECT_ID(N'tb_app.DeleteSetting', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[DeleteSetting];
GO

CREATE PROCEDURE [tb_app].[DeleteSetting]
    @ScopeType nvarchar(40) = N'User',
    @SettingKey nvarchar(200),
    @DeviceId uniqueidentifier = NULL,
    @ExpectedRowVersion binary(8) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @ScopeType <> N'User'
        THROW 51224, N'The desktop DeleteSetting contract supports user-scoped settings only.', 1;

    EXEC [tb_app].[DeleteUserSetting]
        @SettingKey = @SettingKey,
        @ExpectedRowVersion = @ExpectedRowVersion;
END;
GO

PRINT N'TechBench V0002 templates, links, settings, aliases, mapping, and shared-data procedures created.';
GO
