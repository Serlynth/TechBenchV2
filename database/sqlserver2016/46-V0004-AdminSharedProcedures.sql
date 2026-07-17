:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/*
    V0004 centralizes organization-wide mutation authority in TechBench_Admins.
    Existing signatures remain stable for the desktop client.
*/

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

    /*
        The legacy Sync Operator role remains visible in the user context for
        upgrade compatibility, but it no longer authorizes a shared mutation.
        Existing sync procedures therefore enforce Admin-only access without
        changing any public procedure signature.
    */
    IF @IsAdmin <> 1
        SET @IsSyncOperator = 0;
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
        CONVERT(int, 4) AS [SchemaVersion],
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

    IF @IsAdmin <> 1
        THROW 51500, N'Only an Admin may initialize shared workspace defaults.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @ChangedCount int = 0;

    DECLARE @DefaultLinks TABLE
    (
        [BuiltInKey] nvarchar(120) NOT NULL PRIMARY KEY,
        [Name] nvarchar(160) NOT NULL,
        [Url] nvarchar(2048) NOT NULL,
        [UrlHash] binary(32) NOT NULL,
        [SortOrder] int NOT NULL
    );

    INSERT INTO @DefaultLinks([BuiltInKey], [Name], [Url], [UrlHash], [SortOrder])
    VALUES
        (N'watchguard-cloud', N'WatchGuard Cloud', N'https://cloud.watchguard.com/',
            CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), N'https://cloud.watchguard.com/'))), 0),
        (N'microsoft-365-admin', N'Microsoft 365 Admin Center', N'https://admin.microsoft.com/',
            CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), N'https://admin.microsoft.com/'))), 1),
        (N'barracuda-cloud-control', N'Barracuda Cloud Control', N'https://login.barracuda.com/',
            CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), N'https://login.barracuda.com/'))), 2),
        (N'eset-protect', N'ESET PROTECT Console', N'https://protect.eset.com/',
            CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), N'https://protect.eset.com/'))), 3),
        (N'email2phone', N'Email2Phone', N'https://user.email2phone.net/client/#/authentication/signin',
            CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), N'https://user.email2phone.net/client/#/authentication/signin'))), 4),
        (N'godaddy-dns', N'GoDaddy', N'https://dcc.godaddy.com/control/portfolio',
            CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), N'https://dcc.godaddy.com/control/portfolio'))), 10),
        (N'network-solutions-dns', N'Network Solutions', N'https://www.networksolutions.com/my-account/login',
            CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), N'https://www.networksolutions.com/my-account/login'))), 11);

    DECLARE @DefaultTemplates TABLE
    (
        [Name] nvarchar(160) NOT NULL PRIMARY KEY,
        [Category] nvarchar(160) NOT NULL,
        [TemplateText] nvarchar(max) NOT NULL
    );

    INSERT INTO @DefaultTemplates([Name], [Category], [TemplateText])
    VALUES
        (N'Exchange certificate update', N'Microsoft 365',
            N'Updated Exchange certificate binding, verified mail flow, and confirmed Outlook connectivity.'),
        (N'VPN troubleshooting', N'Network',
            N'Investigated VPN connection failure, validated credentials and MFA status, reviewed client logs, and confirmed successful reconnect.'),
        (N'Microsoft 365 licensing', N'Microsoft 365',
            N'Reviewed Microsoft 365 license assignment, adjusted user licensing, and confirmed service availability.'),
        (N'Firewall rule change', N'Network',
            N'Reviewed requested firewall rule change, validated source and destination scope, applied the rule, and confirmed expected traffic.'),
        (N'Password reset', N'Help Desk',
            N'Reset user password, confirmed MFA status, and verified successful sign-in with the user.'),
        (N'Backup verification', N'Infrastructure',
            N'Reviewed backup job status, checked warnings or failures, and documented restore-point availability.'),
        (N'Server reboot/maintenance', N'Infrastructure',
            N'Performed scheduled server maintenance, rebooted services as needed, and verified post-maintenance availability.');

    DECLARE @DefaultOrganizationSettings TABLE
    (
        [SettingKey] nvarchar(200) NOT NULL PRIMARY KEY,
        [SettingValue] nvarchar(max) NOT NULL
    );

    INSERT INTO @DefaultOrganizationSettings([SettingKey], [SettingValue])
    VALUES
        (N'Whd.AutoSyncEnabled', N'true'),
        (N'Whd.AutoSyncMinutes', N'5');

    BEGIN TRY
        BEGIN TRANSACTION;

        /*
            Auto-sync settings are required runtime configuration, so repair a
            missing row on every Admin initialization without overwriting an
            existing Admin value.
        */
        INSERT INTO [tb_data].[OrganizationSettings]
        (
            [SettingKey],
            [SettingValue],
            [UpdatedByWindowsSid],
            [UpdatedAtUtc]
        )
        SELECT
            default_setting.[SettingKey],
            default_setting.[SettingValue],
            @UserSid,
            @NowUtc
        FROM @DefaultOrganizationSettings AS default_setting
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[OrganizationSettings] WITH (UPDLOCK, HOLDLOCK)
            WHERE [SettingKey] = default_setting.[SettingKey]
        );

        SET @ChangedCount += @@ROWCOUNT;

        DECLARE @InitializeWorkspaceCatalogs bit = 0;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_data].[OrganizationSettings] WITH (UPDLOCK, HOLDLOCK)
            WHERE [SettingKey] = N'WorkspaceDefaults.Initialized'
        )
            SET @InitializeWorkspaceCatalogs = 1;

        IF @InitializeWorkspaceCatalogs = 1
        BEGIN
            /*
                The original link/template catalogs are a one-time V0004 seed.
                Once marked initialized, later Admin rename/delete operations
                remain authoritative and are never recreated on app startup.
            */
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
            SELECT
                N'Organization',
                NULL,
                default_link.[Name],
                default_link.[Url],
                default_link.[UrlHash],
                default_link.[SortOrder],
                default_link.[BuiltInKey],
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            FROM @DefaultLinks AS default_link
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[CommonLinks] WITH (UPDLOCK, HOLDLOCK)
                WHERE [BuiltInKey] = default_link.[BuiltInKey]
                   OR
                   (
                       [ScopeType] = N'Organization'
                       AND [UrlHash] = default_link.[UrlHash]
                   )
            );

            SET @ChangedCount += @@ROWCOUNT;

            /*
                Template names identify defaults during the one-time seed.
                Existing organization templates keep their category and text.
            */
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
            FROM @DefaultTemplates AS default_template
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[Templates] WITH (UPDLOCK, HOLDLOCK)
                WHERE [ScopeType] = N'Organization'
                  AND [Name] = default_template.[Name]
            );

            SET @ChangedCount += @@ROWCOUNT;

            INSERT INTO [tb_data].[OrganizationSettings]
            (
                [SettingKey],
                [SettingValue],
                [UpdatedByWindowsSid],
                [UpdatedAtUtc]
            )
            VALUES
            (
                N'WorkspaceDefaults.Initialized',
                N'4',
                @UserSid,
                @NowUtc
            );

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
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

/*
    Built-in links are ordinary Admin-managed catalog rows in V0004. Their key
    still prevents duplicate defaults; it no longer makes the row immutable.
*/
IF OBJECT_ID(N'tb_app.SaveCommonLink', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveCommonLink];
GO

CREATE PROCEDURE [tb_app].[SaveCommonLink]
    @Id int = NULL,
    @ScopeType nvarchar(20) = N'Organization',
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
        COALESCE(NULLIF(LTRIM(RTRIM(@ScopeType)), N''), N'Organization');
    SET @Name = NULLIF(LTRIM(RTRIM(@Name)), N'');
    SET @Url = NULLIF(LTRIM(RTRIM(@Url)), N'');
    SET @BuiltInKey = NULLIF(LTRIM(RTRIM(@BuiltInKey)), N'');

    IF @ScopeType <> N'Organization'
        THROW 51300, N'Common Links are organization-scoped in schema version 4.', 1;
    IF @IsAdmin <> 1
        THROW 51301, N'Only an Admin may save organization Common Links.', 1;
    IF @Name IS NULL OR @Url IS NULL
        THROW 51300, N'Common-link name and URL are required.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51302, N'ExpectedRowVersion is required when updating a Common Link.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @UrlHash binary(32) =
        CONVERT(binary(32), HASHBYTES(N'SHA2_256', CONVERT(varbinary(8000), @Url)));
    DECLARE @Action nvarchar(120);

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
                N'Organization',
                NULL,
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
            SET @Action = N'CommonLinkCreated';
        END
        ELSE
        BEGIN
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[CommonLinks] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = @Id
                  AND [ScopeType] = N'Organization'
            )
                THROW 51304, N'The organization Common Link does not exist.', 1;

            UPDATE [tb_data].[CommonLinks]
            SET
                [ScopeType] = N'Organization',
                [OwnerWindowsSid] = NULL,
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
                THROW 51305, N'The Common Link changed after it was loaded.', 1;

            SET @Action = N'CommonLinkUpdated';
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
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

    IF @IsAdmin <> 1
        THROW 51306, N'Only an Admin may delete organization Common Links.', 1;
    IF EXISTS
    (
        SELECT 1
        FROM [tb_data].[CommonLinks]
        WHERE [Id] = @Id
          AND [BuiltInKey] IS NOT NULL
    )
        THROW 51303, N'Built-in Common Links may be edited but cannot be removed.', 1;

    DELETE FROM [tb_data].[CommonLinks]
    WHERE [Id] = @Id
      AND [ScopeType] = N'Organization'
      AND [RowVersion] = @ExpectedRowVersion;

    IF @@ROWCOUNT = 0
        THROW 51307, N'The Common Link was not found or changed after it was loaded.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'CommonLinkDeleted',
        @EntityType = N'CommonLink',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.SaveClientAlias', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveClientAlias];
GO

CREATE PROCEDURE [tb_app].[SaveClientAlias]
    @Id bigint = NULL,
    @ScopeType nvarchar(20) = N'Organization',
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

    IF @IsAdmin <> 1
        THROW 51311, N'Only an Admin may save organization client aliases.', 1;

    SET @ScopeType =
        COALESCE(NULLIF(LTRIM(RTRIM(@ScopeType)), N''), N'Organization');
    SET @Alias = NULLIF(LTRIM(RTRIM(@Alias)), N'');

    IF @ScopeType <> N'Organization'
        THROW 51310, N'Client aliases are organization-scoped in schema version 4.', 1;
    IF @Alias IS NULL
        THROW 51310, N'Client alias is required.', 1;
    IF NOT EXISTS (SELECT 1 FROM [tb_data].[Clients] WHERE [Id] = @ClientId)
        THROW 51310, N'The selected client does not exist.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @StoredAlias nvarchar(240);
    DECLARE @StoredClientId int;
    DECLARE @StoredRowVersion binary(8);
    DECLARE @Action nvarchar(120);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            SELECT
                @Id = [Id],
                @StoredAlias = [Alias],
                @StoredClientId = [ClientId],
                @StoredRowVersion = [RowVersion]
            FROM [tb_data].[ClientAliases] WITH (UPDLOCK, HOLDLOCK)
            WHERE [ScopeType] = N'Organization'
              AND [Alias] = @Alias;

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
                    N'Organization',
                    NULL,
                    @Alias,
                    @ClientId,
                    @UserSid,
                    @UserSid,
                    @NowUtc,
                    @NowUtc
                );

                SET @Id = CONVERT(bigint, SCOPE_IDENTITY());
                SET @Action = N'ClientAliasCreated';
            END
            ELSE IF @StoredClientId <> @ClientId
            BEGIN
                UPDATE [tb_data].[ClientAliases]
                SET
                    [ClientId] = @ClientId,
                    [UpdatedByWindowsSid] = @UserSid,
                    [UpdatedAtUtc] = @NowUtc
                WHERE [Id] = @Id
                  AND [RowVersion] = @StoredRowVersion;

                IF @@ROWCOUNT = 0
                    THROW 51312, N'The client alias changed while it was being saved.', 1;

                SET @Action = N'ClientAliasUpdated';
            END;
        END
        ELSE
        BEGIN
            SELECT
                @StoredAlias = [Alias],
                @StoredClientId = [ClientId],
                @StoredRowVersion = [RowVersion]
            FROM [tb_data].[ClientAliases] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @Id
              AND [ScopeType] = N'Organization';

            IF @StoredAlias IS NULL
                THROW 51313, N'The organization client alias does not exist.', 1;

            IF @StoredAlias <> @Alias OR @StoredClientId <> @ClientId
            BEGIN
                IF @ExpectedRowVersion IS NULL
                    THROW 51314, N'ExpectedRowVersion is required when changing a client alias.', 1;

                UPDATE [tb_data].[ClientAliases]
                SET
                    [Alias] = @Alias,
                    [ClientId] = @ClientId,
                    [UpdatedByWindowsSid] = @UserSid,
                    [UpdatedAtUtc] = @NowUtc
                WHERE [Id] = @Id
                  AND [RowVersion] = @ExpectedRowVersion;

                IF @@ROWCOUNT = 0
                    THROW 51312, N'The client alias changed after it was loaded.', 1;

                SET @Action = N'ClientAliasUpdated';
            END;
        END;

        IF @Action IS NOT NULL
        BEGIN
            DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
            EXEC [tb_security].[WriteAuditEvent]
                @Action = @Action,
                @EntityType = N'ClientAlias',
                @EntityId = @AuditEntityId,
                @RequestId = @RequestId;
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
        [ScopeType],
        [Alias],
        [ClientId],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[ClientAliases]
    WHERE [Id] = @Id;
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
    IF @SettingKey NOT IN
       (
           N'Whd.Username',
           N'Sage.Username',
           N'Sage.EmployeeId'
       )
        THROW 51510, N'Only approved per-user identity settings may be saved.', 1;

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

    SET @SettingKey = NULLIF(LTRIM(RTRIM(@SettingKey)), N'');

    IF @SettingKey IS NULL
        THROW 51220, N'SettingKey is required.', 1;
    IF @SettingKey NOT IN
       (
           N'Whd.Username',
           N'Sage.Username',
           N'Sage.EmployeeId',
           N'Whd.ApiToken',
           N'Sage.Password',
           N'Sage.DefaultCustomerId'
       )
        THROW 51511, N'Only approved per-user settings may be deleted.', 1;

    DELETE FROM [tb_user].[UserSettings]
    WHERE [OwnerWindowsSid] = @UserSid
      AND [SettingKey] = @SettingKey
      AND (@ExpectedRowVersion IS NULL OR [RowVersion] = @ExpectedRowVersion);
END;
GO

IF OBJECT_ID(N'tb_app.AdminGetOrganizationTags', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminGetOrganizationTags];
GO

CREATE PROCEDURE [tb_app].[AdminGetOrganizationTags]
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
        THROW 51520, N'Only an Admin may manage organization tags.', 1;

    SELECT
        [Id],
        [Tag],
        [CreatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[OrganizationTags]
    ORDER BY [Tag], [Id];
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveOrganizationTag', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveOrganizationTag];
GO

CREATE PROCEDURE [tb_app].[AdminSaveOrganizationTag]
    @Id int = NULL,
    @Tag nvarchar(1000),
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
        THROW 51520, N'Only an Admin may manage organization tags.', 1;

    SET @Tag = NULLIF(LTRIM(RTRIM(@Tag)), N'');
    IF @Tag IS NULL
        THROW 51521, N'Tag is required.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51522, N'ExpectedRowVersion is required when updating an organization tag.', 1;

    DECLARE @TagHash binary(32) =
        CONVERT
        (
            binary(32),
            HASHBYTES
            (
                N'SHA2_256',
                CONVERT(varbinary(2000), UPPER(@Tag))
            )
        );
    DECLARE @Action nvarchar(120);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF EXISTS
        (
            SELECT 1
            FROM [tb_data].[OrganizationTags] WITH (UPDLOCK, HOLDLOCK)
            WHERE [TagHash] = @TagHash
              AND (@Id IS NULL OR [Id] <> @Id)
        )
            THROW 51523, N'An organization tag with the same normalized value already exists.', 1;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[OrganizationTags]
            (
                [Tag],
                [TagHash],
                [CreatedByWindowsSid],
                [CreatedAtUtc]
            )
            VALUES
            (
                @Tag,
                @TagHash,
                @UserSid,
                SYSUTCDATETIME()
            );

            SET @Id = CONVERT(int, SCOPE_IDENTITY());
            SET @Action = N'OrganizationTagCreated';
        END
        ELSE
        BEGIN
            UPDATE [tb_data].[OrganizationTags]
            SET
                [Tag] = @Tag,
                [TagHash] = @TagHash
            WHERE [Id] = @Id
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51524, N'The organization tag was not found or changed after it was loaded.', 1;

            SET @Action = N'OrganizationTagUpdated';
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
            @EntityType = N'OrganizationTag',
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
        [Tag],
        [CreatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[OrganizationTags]
    WHERE [Id] = @Id;
END;
GO

IF OBJECT_ID(N'tb_app.AdminDeleteOrganizationTag', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminDeleteOrganizationTag];
GO

CREATE PROCEDURE [tb_app].[AdminDeleteOrganizationTag]
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

    IF @IsAdmin <> 1
        THROW 51520, N'Only an Admin may manage organization tags.', 1;

    DELETE FROM [tb_data].[OrganizationTags]
    WHERE [Id] = @Id
      AND [RowVersion] = @ExpectedRowVersion;

    IF @@ROWCOUNT = 0
        THROW 51525, N'The organization tag was not found or changed after it was loaded.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'OrganizationTagDeleted',
        @EntityType = N'OrganizationTag',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

/*
    Work-entry tags remain the user's own comma-separated entry data. Saving a
    work entry no longer changes the Admin-managed organization tag catalog.
*/
IF OBJECT_ID(N'tb_app.SaveWorkEntry', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveWorkEntry];
GO

CREATE PROCEDURE [tb_app].[SaveWorkEntry]
    @Id int = NULL,
    @WorkDate date,
    @ClientId int = NULL,
    @ManualClientName nvarchar(240) = NULL,
    @TicketId int = NULL,
    @TicketNumberText nvarchar(120) = NULL,
    @HasTimeRange bit = 1,
    @StartTime time(0) = '00:00',
    @EndTime time(0) = '00:00',
    @DurationMinutes int,
    @Billable bit = 1,
    @Note nvarchar(max) = N'',
    @PersonalNote nvarchar(max) = NULL,
    @IncludePersonalNoteInWhd bit = 0,
    @Tags nvarchar(1000) = N'',
    @FollowUpState nvarchar(30) = N'None',
    @FollowUpDueDate date = NULL,
    @PostingStatus nvarchar(40) = N'Draft',
    @LastError nvarchar(max) = NULL,
    @ExpectedRowVersion binary(8) = NULL,
    @ExpectedPersonalNoteRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @LoginName nvarchar(256);
    DECLARE @DisplayName nvarchar(160);
    DECLARE @IsTechnician bit;
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;

    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @UserSid OUTPUT,
        @LoginName = @LoginName OUTPUT,
        @DisplayName = @DisplayName OUTPUT,
        @IsTechnician = @IsTechnician OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    SET @ManualClientName = NULLIF(LTRIM(RTRIM(@ManualClientName)), N'');
    SET @TicketNumberText = NULLIF(LTRIM(RTRIM(@TicketNumberText)), N'');
    SET @Note = COALESCE(@Note, N'');
    SET @PersonalNote = NULLIF(LTRIM(RTRIM(@PersonalNote)), N'');
    SET @Tags = COALESCE(LTRIM(RTRIM(@Tags)), N'');
    SET @FollowUpState =
        COALESCE(NULLIF(LTRIM(RTRIM(@FollowUpState)), N''), N'None');
    SET @PostingStatus =
        COALESCE(NULLIF(LTRIM(RTRIM(@PostingStatus)), N''), N'Draft');
    SET @LastError = NULLIF(LTRIM(RTRIM(@LastError)), N'');

    IF @ClientId IS NULL AND @ManualClientName IS NULL
        THROW 51130, N'A client or manual client name is required.', 1;
    IF @ClientId IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM [tb_data].[Clients] WHERE [Id] = @ClientId)
        THROW 51130, N'The selected client does not exist.', 1;
    IF @TicketId IS NOT NULL
       AND NOT EXISTS
       (
           SELECT 1
           FROM [tb_data].[Tickets]
           WHERE [Id] = @TicketId
             AND (@ClientId IS NULL OR [ClientId] = @ClientId)
       )
        THROW 51130, N'The selected ticket does not exist for the selected client.', 1;
    IF @DurationMinutes < 0 OR @DurationMinutes > 1440
        THROW 51130, N'DurationMinutes must be between 0 and 1440.', 1;
    IF @FollowUpState NOT IN (N'None', N'FollowUp', N'Waiting', N'Completed')
        THROW 51130, N'FollowUpState is invalid.', 1;
    IF @PostingStatus NOT IN (N'Draft', N'Ready')
        THROW 51130, N'PostingStatus may be only Draft or Ready in SaveWorkEntry.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51131, N'ExpectedRowVersion is required when updating a work entry.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @Action nvarchar(120);
    DECLARE @ExistingWhdPosted bit;
    DECLARE @ExistingSagePosted bit;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @Id IS NULL
        BEGIN
            INSERT INTO [tb_data].[WorkEntries]
            (
                [OwnerWindowsSid],
                [WorkDate],
                [ClientId],
                [ManualClientName],
                [TicketId],
                [TicketNumberText],
                [HasTimeRange],
                [StartTime],
                [EndTime],
                [DurationMinutes],
                [Billable],
                [Note],
                [Tags],
                [FollowUpState],
                [FollowUpDueDate],
                [WhdPosted],
                [WhdPostedAtUtc],
                [SagePosted],
                [SagePostedAtUtc],
                [SageTicketNumber],
                [PostingStatus],
                [LastError],
                [CreatedByWindowsSid],
                [UpdatedByWindowsSid],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            VALUES
            (
                @UserSid,
                @WorkDate,
                @ClientId,
                @ManualClientName,
                @TicketId,
                @TicketNumberText,
                @HasTimeRange,
                @StartTime,
                @EndTime,
                @DurationMinutes,
                @Billable,
                @Note,
                @Tags,
                @FollowUpState,
                @FollowUpDueDate,
                0,
                NULL,
                0,
                NULL,
                NULL,
                CASE WHEN @LastError IS NOT NULL THEN N'Failed' ELSE @PostingStatus END,
                @LastError,
                @UserSid,
                @UserSid,
                @NowUtc,
                @NowUtc
            );

            SET @Id = CONVERT(int, SCOPE_IDENTITY());
            SET @Action = N'WorkEntryCreated';
        END
        ELSE
        BEGIN
            SELECT
                @ExistingWhdPosted = [WhdPosted],
                @ExistingSagePosted = [SagePosted]
            FROM [tb_data].[WorkEntries] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @Id
              AND [OwnerWindowsSid] = @UserSid
              AND [RowVersion] = @ExpectedRowVersion;

            IF @ExistingWhdPosted IS NULL
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM [tb_data].[WorkEntries] WHERE [Id] = @Id)
                    THROW 51132, N'The work entry no longer exists.', 1;
                IF NOT EXISTS
                (
                    SELECT 1
                    FROM [tb_data].[WorkEntries]
                    WHERE [Id] = @Id
                      AND [OwnerWindowsSid] = @UserSid
                )
                    THROW 51133, N'Only the work-entry owner may update it.', 1;
                THROW 51134, N'The work entry changed after it was loaded.', 1;
            END;

            IF @ExistingSagePosted = 1
                THROW 51137, N'A work entry already posted to Sage cannot be changed.', 1;

            IF EXISTS
            (
                SELECT 1
                FROM [tb_ops].[PostingAttempts] WITH (UPDLOCK, HOLDLOCK)
                WHERE [WorkEntryId] = @Id
                  AND [OwnerWindowsSid] = @UserSid
                  AND [Status] IN (N'Started', N'Unknown')
            )
            OR EXISTS
            (
                SELECT 1
                FROM [tb_ops].[PostingLeases] WITH (UPDLOCK, HOLDLOCK)
                WHERE [WorkEntryId] = @Id
                  AND [OwnerWindowsSid] = @UserSid
            )
                THROW 51139, N'A work entry cannot be changed while an external posting attempt is active.', 1;

            UPDATE [tb_data].[WorkEntries]
            SET
                [WorkDate] = @WorkDate,
                [ClientId] = @ClientId,
                [ManualClientName] = @ManualClientName,
                [TicketId] = @TicketId,
                [TicketNumberText] = @TicketNumberText,
                [HasTimeRange] = @HasTimeRange,
                [StartTime] = @StartTime,
                [EndTime] = @EndTime,
                [DurationMinutes] = @DurationMinutes,
                [Billable] = @Billable,
                [Note] = @Note,
                [Tags] = @Tags,
                [FollowUpState] = @FollowUpState,
                [FollowUpDueDate] = @FollowUpDueDate,
                [PostingStatus] =
                    CASE
                        WHEN @LastError IS NOT NULL THEN N'Failed'
                        WHEN [WhdPosted] = 1 AND [SagePosted] = 1 THEN N'PostedToBoth'
                        WHEN [SagePosted] = 1 THEN N'PostedToSage'
                        WHEN [WhdPosted] = 1 THEN N'PostedToWhd'
                        ELSE @PostingStatus
                    END,
                [LastError] = @LastError,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id
              AND [OwnerWindowsSid] = @UserSid
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM [tb_data].[WorkEntries] WHERE [Id] = @Id)
                    THROW 51132, N'The work entry no longer exists.', 1;
                IF NOT EXISTS
                (
                    SELECT 1
                    FROM [tb_data].[WorkEntries]
                    WHERE [Id] = @Id
                      AND [OwnerWindowsSid] = @UserSid
                )
                    THROW 51133, N'Only the work-entry owner may update it.', 1;
                THROW 51134, N'The work entry changed after it was loaded.', 1;
            END;

            SET @Action = N'WorkEntryUpdated';
        END;

        IF EXISTS
        (
            SELECT 1
            FROM [tb_private].[WorkEntryPersonalNotes]
            WHERE [WorkEntryId] = @Id
              AND [OwnerWindowsSid] = @UserSid
        )
        BEGIN
            IF @ExpectedPersonalNoteRowVersion IS NULL
                THROW 51135, N'ExpectedPersonalNoteRowVersion is required for an existing personal note.', 1;

            IF @PersonalNote IS NULL AND @IncludePersonalNoteInWhd = 0
            BEGIN
                DELETE FROM [tb_private].[WorkEntryPersonalNotes]
                WHERE [WorkEntryId] = @Id
                  AND [OwnerWindowsSid] = @UserSid
                  AND [RowVersion] = @ExpectedPersonalNoteRowVersion;
            END
            ELSE
            BEGIN
                UPDATE [tb_private].[WorkEntryPersonalNotes]
                SET
                    [Note] = COALESCE(@PersonalNote, N''),
                    [IncludeInWhd] = @IncludePersonalNoteInWhd,
                    [UpdatedAtUtc] = @NowUtc
                WHERE [WorkEntryId] = @Id
                  AND [OwnerWindowsSid] = @UserSid
                  AND [RowVersion] = @ExpectedPersonalNoteRowVersion;
            END;

            IF @@ROWCOUNT = 0
                THROW 51136, N'The personal note changed after it was loaded.', 1;
        END
        ELSE
        BEGIN
            IF @ExpectedPersonalNoteRowVersion IS NOT NULL
                THROW 51136, N'The personal note changed after it was loaded.', 1;

            IF @PersonalNote IS NOT NULL OR @IncludePersonalNoteInWhd = 1
            BEGIN
                INSERT INTO [tb_private].[WorkEntryPersonalNotes]
                (
                    [WorkEntryId],
                    [OwnerWindowsSid],
                    [Note],
                    [IncludeInWhd],
                    [CreatedAtUtc],
                    [UpdatedAtUtc]
                )
                VALUES
                (
                    @Id,
                    @UserSid,
                    COALESCE(@PersonalNote, N''),
                    @IncludePersonalNoteInWhd,
                    @NowUtc,
                    @NowUtc
                );
            END;
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = @Action,
            @EntityType = N'WorkEntry',
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
        work_entry.[Id],
        work_entry.[OwnerWindowsSid],
        work_entry.[WorkDate],
        work_entry.[ClientId],
        work_entry.[ManualClientName],
        work_entry.[TicketId],
        work_entry.[TicketNumberText],
        work_entry.[HasTimeRange],
        work_entry.[StartTime],
        work_entry.[EndTime],
        work_entry.[DurationMinutes],
        work_entry.[Billable],
        work_entry.[Note],
        personal_note.[Note] AS [InternalNote],
        personal_note.[Note] AS [PersonalNote],
        COALESCE(personal_note.[IncludeInWhd], 0) AS [IncludePersonalNoteInWhd],
        work_entry.[Tags],
        work_entry.[FollowUpState],
        work_entry.[FollowUpDueDate],
        work_entry.[WhdPosted],
        work_entry.[WhdPostedAtUtc] AS [WhdPostedAt],
        work_entry.[SagePosted],
        work_entry.[SagePostedAtUtc] AS [SagePostedAt],
        work_entry.[SageTicketNumber],
        work_entry.[PostingStatus],
        work_entry.[LastError],
        work_entry.[CreatedAtUtc] AS [CreatedAt],
        work_entry.[UpdatedAtUtc] AS [UpdatedAt],
        client.[Name] AS [ClientName],
        ticket.[TicketNumber],
        ticket.[Subject] AS [TicketSubject],
        work_entry.[RowVersion],
        personal_note.[RowVersion] AS [PersonalNoteRowVersion]
    FROM [tb_data].[WorkEntries] AS work_entry
    LEFT JOIN [tb_data].[Clients] AS client
        ON client.[Id] = work_entry.[ClientId]
    LEFT JOIN [tb_data].[Tickets] AS ticket
        ON ticket.[Id] = work_entry.[TicketId]
    LEFT JOIN [tb_private].[WorkEntryPersonalNotes] AS personal_note
        ON personal_note.[WorkEntryId] = work_entry.[Id]
       AND personal_note.[OwnerWindowsSid] = @UserSid
    WHERE work_entry.[Id] = @Id;
END;
GO

/*
    These lifecycle procedures previously relied only on the lease owner and
    their EXECUTE grants. Keep those ownership checks and add an explicit
    runtime Admin boundary so a future accidental grant cannot restore legacy
    Sync Operator write authority.
*/
IF OBJECT_ID(N'tb_app.ReleaseSyncLease', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[ReleaseSyncLease];
GO

CREATE PROCEDURE [tb_app].[ReleaseSyncLease]
    @LeaseId uniqueidentifier,
    @DeviceId uniqueidentifier
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
        THROW 51540, N'Only an Admin may release a synchronization lease.', 1;

    DELETE FROM [tb_ops].[SyncLeases]
    WHERE [LeaseId] = @LeaseId
      AND [OwnerWindowsSid] = @UserSid
      AND [DeviceId] = @DeviceId;
END;
GO

IF OBJECT_ID(N'tb_app.BeginSyncRun', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[BeginSyncRun];
GO

CREATE PROCEDURE [tb_app].[BeginSyncRun]
    @Source nvarchar(120),
    @LeaseId uniqueidentifier,
    @DeviceId uniqueidentifier
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
        THROW 51540, N'Only an Admin may begin a synchronization run.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_ops].[SyncLeases]
        WHERE [SourceSystem] = @Source
          AND [LeaseId] = @LeaseId
          AND [OwnerWindowsSid] = @UserSid
          AND [DeviceId] = @DeviceId
          AND [ExpiresAtUtc] > SYSUTCDATETIME()
    )
        THROW 51440, N'The synchronization lease is missing, expired, or owned by another workstation.', 1;

    DECLARE @RunId uniqueidentifier = NEWID();

    INSERT INTO [tb_ops].[SyncRuns]
    (
        [Id],
        [SourceSystem],
        [LeaseId],
        [OwnerWindowsSid],
        [DeviceId],
        [Status]
    )
    VALUES
    (
        @RunId,
        @Source,
        @LeaseId,
        @UserSid,
        @DeviceId,
        N'Started'
    );

    SELECT @RunId AS [RunId];
END;
GO

IF OBJECT_ID(N'tb_app.CompleteSyncRun', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[CompleteSyncRun];
GO

CREATE PROCEDURE [tb_app].[CompleteSyncRun]
    @RunId uniqueidentifier,
    @Succeeded bit,
    @ItemCount int = 0,
    @Message nvarchar(max) = NULL
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
        THROW 51540, N'Only an Admin may complete a synchronization run.', 1;

    UPDATE [tb_ops].[SyncRuns]
    SET
        [Status] = CASE WHEN @Succeeded = 1 THEN N'Succeeded' ELSE N'Failed' END,
        [ReadCount] = CASE WHEN @ItemCount < 0 THEN 0 ELSE @ItemCount END,
        [Message] = COALESCE(@Message, N''),
        [CompletedAtUtc] = SYSUTCDATETIME()
    WHERE [Id] = @RunId
      AND [OwnerWindowsSid] = @UserSid
      AND [Status] = N'Started';

    IF @@ROWCOUNT = 0
        THROW 51441, N'The synchronization run is missing, final, or owned by another user.', 1;
END;
GO

IF OBJECT_ID(N'tb_security.RenewSyncRunLease', N'P') IS NOT NULL
    DROP PROCEDURE [tb_security].[RenewSyncRunLease];
GO

CREATE PROCEDURE [tb_security].[RenewSyncRunLease]
    @RunId uniqueidentifier,
    @ExpectedSource nvarchar(40)
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
        THROW 51540, N'Only an Admin may renew a synchronization run lease.', 1;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();

    UPDATE sync_lease
    SET
        [ExpiresAtUtc] = DATEADD(second, 300, @NowUtc),
        [UpdatedAtUtc] = @NowUtc
    FROM [tb_ops].[SyncLeases] AS sync_lease
    INNER JOIN [tb_ops].[SyncRuns] AS sync_run
        ON sync_run.[SourceSystem] = sync_lease.[SourceSystem]
       AND sync_run.[LeaseId] = sync_lease.[LeaseId]
       AND sync_run.[OwnerWindowsSid] = sync_lease.[OwnerWindowsSid]
       AND sync_run.[DeviceId] = sync_lease.[DeviceId]
    WHERE sync_run.[Id] = @RunId
      AND sync_run.[SourceSystem] = @ExpectedSource
      AND sync_run.[OwnerWindowsSid] = @UserSid
      AND sync_run.[Status] = N'Started'
      AND sync_lease.[ExpiresAtUtc] > @NowUtc;

    IF @@ROWCOUNT = 0
        THROW 51449, N'The source-specific synchronization lease expired or was replaced.', 1;
END;
GO
