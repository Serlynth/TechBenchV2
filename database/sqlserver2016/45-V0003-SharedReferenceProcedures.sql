:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
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
        CONVERT(int, 3) AS [SchemaVersion],
        CONVERT(bit, 0) AS [FullTextSearchAvailable],
        CONVERT(bit, 1) AS [SupportsTickets],
        CONVERT(bit, 1) AS [SupportsWorkEntries],
        CONVERT(bit, 1) AS [SupportsPrivateNotes],
        CONVERT(bit, 1) AS [SupportsPostingLeases],
        CONVERT(bit, 1) AS [SupportsSyncLeases],
        CONVERT(bit, 1) AS [SupportsImports];
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
    ORDER BY [SortOrder], [Name], [Id];
END;
GO

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
        THROW 51300, N'Common Links are organization-scoped in schema version 3.', 1;
    IF @IsAdmin <> 1
        THROW 51301, N'Only an Admin may save organization Common Links.', 1;
    IF @Name IS NULL OR @Url IS NULL
        THROW 51300, N'Common-link name and URL are required.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51302, N'ExpectedRowVersion is required when updating a Common Link.', 1;
    IF @Id IS NOT NULL
       AND EXISTS
       (
           SELECT 1
           FROM [tb_data].[CommonLinks]
           WHERE [Id] = @Id
             AND [BuiltInKey] IS NOT NULL
       )
        THROW 51303, N'Built-in Common Links cannot be changed.', 1;

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
        THROW 51303, N'Built-in Common Links cannot be removed.', 1;

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

    SELECT
        [Id],
        [ScopeType],
        [Alias],
        [ClientId],
        [UpdatedAtUtc] AS [UpdatedAt],
        [RowVersion]
    FROM [tb_data].[ClientAliases]
    WHERE [ScopeType] = N'Organization'
    ORDER BY [Alias];
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

    SET @ScopeType =
        COALESCE(NULLIF(LTRIM(RTRIM(@ScopeType)), N''), N'Organization');
    SET @Alias = NULLIF(LTRIM(RTRIM(@Alias)), N'');

    IF @ScopeType <> N'Organization'
        THROW 51310, N'Client aliases are organization-scoped in schema version 3.', 1;
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
                IF @IsAdmin <> 1
                    THROW 51311, N'Only an Admin may change an existing organization client alias.', 1;

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
            /* Same alias + client is intentionally an idempotent success. */
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
                IF @IsAdmin <> 1
                    THROW 51311, N'Only an Admin may change an existing organization client alias.', 1;
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
            /* Reusing the same loaded mapping is also idempotent for all users. */
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

    IF @IsAdmin <> 1
        THROW 51315, N'Only an Admin may delete organization client aliases.', 1;

    DELETE FROM [tb_data].[ClientAliases]
    WHERE [Id] = @Id
      AND [ScopeType] = N'Organization'
      AND [RowVersion] = @ExpectedRowVersion;

    IF @@ROWCOUNT = 0
        THROW 51316, N'The client alias was not found or changed after it was loaded.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'ClientAliasDeleted',
        @EntityType = N'ClientAlias',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
END;
GO

IF OBJECT_ID(N'tb_app.SaveTemplate', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SaveTemplate];
GO

CREATE PROCEDURE [tb_app].[SaveTemplate]
    @Id int = NULL,
    @ScopeType nvarchar(20) = N'Organization',
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
        COALESCE(NULLIF(LTRIM(RTRIM(@ScopeType)), N''), N'Organization');
    SET @Name = NULLIF(LTRIM(RTRIM(@Name)), N'');
    SET @Category = COALESCE(LTRIM(RTRIM(@Category)), N'');
    SET @TemplateText = COALESCE(@TemplateText, N'');

    IF @ScopeType <> N'Organization'
        THROW 51330, N'Templates are organization-scoped in schema version 3; legacy personal templates are read-only.', 1;
    IF @IsAdmin <> 1
        THROW 51331, N'Only an Admin may save organization templates.', 1;
    IF @Name IS NULL
        THROW 51330, N'Template name is required.', 1;
    IF @Id IS NOT NULL AND @ExpectedRowVersion IS NULL
        THROW 51332, N'ExpectedRowVersion is required when updating a template.', 1;

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
                N'Organization',
                NULL,
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
                FROM [tb_data].[Templates] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = @Id
                  AND [ScopeType] = N'Organization'
                  AND [OwnerWindowsSid] IS NULL
            )
                THROW 51333, N'The organization template does not exist; legacy personal templates are read-only.', 1;

            UPDATE [tb_data].[Templates]
            SET
                [Name] = @Name,
                [Category] = @Category,
                [TemplateText] = @TemplateText,
                [UpdatedByWindowsSid] = @UserSid,
                [UpdatedAtUtc] = @NowUtc
            WHERE [Id] = @Id
              AND [ScopeType] = N'Organization'
              AND [OwnerWindowsSid] IS NULL
              AND [RowVersion] = @ExpectedRowVersion;

            IF @@ROWCOUNT = 0
                THROW 51334, N'The template changed after it was loaded.', 1;

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

    IF @IsAdmin <> 1
        THROW 51335, N'Only an Admin may delete organization templates.', 1;

    DELETE FROM [tb_data].[Templates]
    WHERE [Id] = @Id
      AND [ScopeType] = N'Organization'
      AND [OwnerWindowsSid] IS NULL
      AND [RowVersion] = @ExpectedRowVersion;

    IF @@ROWCOUNT = 0
        THROW 51336, N'The organization template was not found or changed; legacy personal templates are read-only.', 1;

    DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
    EXEC [tb_security].[WriteAuditEvent]
        @Action = N'TemplateDeleted',
        @EntityType = N'Template',
        @EntityId = @AuditEntityId,
        @RequestId = @RequestId;
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
    IF @SettingKey IN
       (
           N'Whd.BaseUrl',
           N'Whd.AuthenticationMode',
           N'Sage.ActivityItemId'
       )
        THROW 51320, N'This setting is organization-scoped and may be changed only through organization settings.', 1;

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

    IF @SettingKey IN
       (
           N'Whd.BaseUrl',
           N'Whd.AuthenticationMode',
           N'Sage.ActivityItemId'
       )
        THROW 51320, N'This setting is organization-scoped and may be changed only through organization settings.', 1;

    DELETE FROM [tb_user].[UserSettings]
    WHERE [OwnerWindowsSid] = @UserSid
      AND [SettingKey] = @SettingKey
      AND (@ExpectedRowVersion IS NULL OR [RowVersion] = @ExpectedRowVersion);
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
        @ScopeType = N'Organization',
        @Name = @Name,
        @Category = @Category,
        @TemplateText = @TemplateText,
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
        @ScopeType = N'Organization',
        @Name = @Name,
        @Url = @Url,
        @SortOrder = @SortOrder,
        @BuiltInKey = @BuiltInKey,
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

    IF @IsAdmin <> 1
        THROW 51317, N'Only an Admin may use the administrative client-alias operation.', 1;

    SELECT
        @Id = [Id],
        @ExpectedRowVersion = [RowVersion]
    FROM [tb_data].[ClientAliases]
    WHERE [ScopeType] = N'Organization'
      AND [Alias] = @Alias;

    EXEC [tb_app].[SaveClientAlias]
        @Id = @Id,
        @ScopeType = N'Organization',
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
    DECLARE @ExpectedRowVersion binary(8);

    EXEC [tb_security].[GetCurrentAccess]
        @UserSid = @UserSid OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1
        THROW 51317, N'Only an Admin may use the administrative client-alias operation.', 1;

    SELECT
        @Id = [Id],
        @ExpectedRowVersion = [RowVersion]
    FROM [tb_data].[ClientAliases]
    WHERE [ScopeType] = N'Organization'
      AND [Alias] = @Alias;

    IF @Id IS NOT NULL
    BEGIN
        EXEC [tb_app].[DeleteClientAlias]
            @Id = @Id,
            @ExpectedRowVersion = @ExpectedRowVersion,
            @RequestId = @RequestId;
    END;
END;
GO

PRINT N'TechBench V0003 shared-reference procedures created.';
GO
