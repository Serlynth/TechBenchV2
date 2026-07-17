:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    V0003 promotes shared reference data to an organization-wide boundary.
    It is intentionally additive so an installed V0002 database upgrades in place.
*/

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.OperationalStorage.0002'
      AND [SchemaVersion] = 2
)
BEGIN
    RAISERROR(
        N'The TechBench V0002 operational schema must be installed before SharedReferenceData.0003.',
        16,
        1);
    RETURN;
END;

IF EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.SharedReferenceData.0003'
      AND [SchemaVersion] = 3
)
BEGIN
    PRINT N'SqlServer2016.SharedReferenceData.0003 is already installed.';
    RETURN;
END;

IF OBJECT_ID(N'tb_data.OrganizationTags', N'U') IS NOT NULL
BEGIN
    RAISERROR(
        N'OrganizationTags exists without the V0003 migration marker. Stop and investigate the partial deployment.',
        16,
        1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    CREATE TABLE [tb_data].[OrganizationTags]
    (
        [Id] int IDENTITY(1,1) NOT NULL,
        [Tag] nvarchar(1000) NOT NULL,
        [TagHash] binary(32) NOT NULL,
        [CreatedByWindowsSid] varbinary(85) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL
            CONSTRAINT [DF_OrganizationTags_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_OrganizationTags] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_OrganizationTags_CreatedBy]
            FOREIGN KEY ([CreatedByWindowsSid])
            REFERENCES [tb_security].[Users]([WindowsSid]),
        CONSTRAINT [CK_OrganizationTags_Canonical]
            CHECK
            (
                LEN([Tag]) > 0
                AND [Tag] = LTRIM(RTRIM([Tag]))
                AND DATALENGTH([Tag]) <= 2000
            )
    );

    CREATE UNIQUE INDEX [UX_OrganizationTags_TagHash]
        ON [tb_data].[OrganizationTags]([TagHash]);

    ;WITH parsed_tags AS
    (
        SELECT
            LTRIM(RTRIM(tag.[value])) AS [Tag],
            CONVERT(
                binary(32),
                HASHBYTES
                (
                    N'SHA2_256',
                    CONVERT(
                        varbinary(2000),
                        UPPER(LTRIM(RTRIM(tag.[value])))
                    )
                )
            ) AS [TagHash],
            work_entry.[CreatedByWindowsSid],
            work_entry.[Id] AS [WorkEntryId]
        FROM [tb_data].[WorkEntries] AS work_entry
        CROSS APPLY STRING_SPLIT(COALESCE(work_entry.[Tags], N''), N',') AS tag
        WHERE NULLIF(LTRIM(RTRIM(tag.[value])), N'') IS NOT NULL
    ),
    ranked_tags AS
    (
        SELECT
            [Tag],
            [TagHash],
            [CreatedByWindowsSid],
            ROW_NUMBER() OVER
            (
                PARTITION BY [TagHash]
                ORDER BY [WorkEntryId], [Tag]
            ) AS [TagRank]
        FROM parsed_tags
    )
    INSERT INTO [tb_data].[OrganizationTags]
    (
        [Tag],
        [TagHash],
        [CreatedByWindowsSid]
    )
    SELECT
        [Tag],
        [TagHash],
        [CreatedByWindowsSid]
    FROM ranked_tags
    WHERE [TagRank] = 1;

    /*
        Common Links are one organization catalog. Existing organization rows win;
        otherwise the newest row wins, with built-ins preferred. Identical URLs are
        already interchangeable under the catalog's URL uniqueness contract.
    */
    ;WITH ranked_links AS
    (
        SELECT
            [Id],
            ROW_NUMBER() OVER
            (
                PARTITION BY [UrlHash]
                ORDER BY
                    CASE WHEN [ScopeType] = N'Organization' THEN 0 ELSE 1 END,
                    CASE WHEN [BuiltInKey] IS NOT NULL THEN 0 ELSE 1 END,
                    [UpdatedAtUtc] DESC,
                    [Id] DESC
            ) AS [LinkRank]
        FROM [tb_data].[CommonLinks]
    )
    DELETE common_link
    FROM [tb_data].[CommonLinks] AS common_link
    INNER JOIN ranked_links AS ranked_link
        ON ranked_link.[Id] = common_link.[Id]
    WHERE ranked_link.[LinkRank] > 1;

    UPDATE [tb_data].[CommonLinks]
    SET
        [ScopeType] = N'Organization',
        [OwnerWindowsSid] = NULL
    WHERE [ScopeType] <> N'Organization'
       OR [OwnerWindowsSid] IS NOT NULL;

    DECLARE @CommonLinkDefault sysname;
    SELECT @CommonLinkDefault = default_constraint.[name]
    FROM sys.default_constraints AS default_constraint
    INNER JOIN sys.columns AS column_definition
        ON column_definition.[object_id] = default_constraint.[parent_object_id]
       AND column_definition.[column_id] = default_constraint.[parent_column_id]
    WHERE default_constraint.[parent_object_id] = OBJECT_ID(N'tb_data.CommonLinks')
      AND column_definition.[name] = N'ScopeType';

    IF @CommonLinkDefault IS NOT NULL
    BEGIN
        DECLARE @DropCommonLinkDefaultSql nvarchar(max) =
            N'ALTER TABLE [tb_data].[CommonLinks] DROP CONSTRAINT '
            + QUOTENAME(@CommonLinkDefault)
            + N';';
        EXEC (@DropCommonLinkDefaultSql);
    END;

    ALTER TABLE [tb_data].[CommonLinks]
        ADD CONSTRAINT [DF_CommonLinks_ScopeType]
        DEFAULT (N'Organization') FOR [ScopeType];

    /*
        Client aliases are shared matching knowledge. Preserve an existing
        organization mapping when present; otherwise choose the most recently
        updated mapping deterministically before promoting it.
    */
    ;WITH ranked_aliases AS
    (
        SELECT
            [Id],
            ROW_NUMBER() OVER
            (
                PARTITION BY [Alias]
                ORDER BY
                    CASE WHEN [ScopeType] = N'Organization' THEN 0 ELSE 1 END,
                    [UpdatedAtUtc] DESC,
                    [Id] DESC
            ) AS [AliasRank]
        FROM [tb_data].[ClientAliases]
    )
    DELETE client_alias
    FROM [tb_data].[ClientAliases] AS client_alias
    INNER JOIN ranked_aliases AS ranked_alias
        ON ranked_alias.[Id] = client_alias.[Id]
    WHERE ranked_alias.[AliasRank] > 1;

    UPDATE [tb_data].[ClientAliases]
    SET
        [ScopeType] = N'Organization',
        [OwnerWindowsSid] = NULL
    WHERE [ScopeType] <> N'Organization'
       OR [OwnerWindowsSid] IS NOT NULL;

    DECLARE @ClientAliasDefault sysname;
    SELECT @ClientAliasDefault = default_constraint.[name]
    FROM sys.default_constraints AS default_constraint
    INNER JOIN sys.columns AS column_definition
        ON column_definition.[object_id] = default_constraint.[parent_object_id]
       AND column_definition.[column_id] = default_constraint.[parent_column_id]
    WHERE default_constraint.[parent_object_id] = OBJECT_ID(N'tb_data.ClientAliases')
      AND column_definition.[name] = N'ScopeType';

    IF @ClientAliasDefault IS NOT NULL
    BEGIN
        DECLARE @DropClientAliasDefaultSql nvarchar(max) =
            N'ALTER TABLE [tb_data].[ClientAliases] DROP CONSTRAINT '
            + QUOTENAME(@ClientAliasDefault)
            + N';';
        EXEC (@DropClientAliasDefaultSql);
    END;

    ALTER TABLE [tb_data].[ClientAliases]
        ADD CONSTRAINT [DF_ClientAliases_ScopeType]
        DEFAULT (N'Organization') FOR [ScopeType];

    /*
        Promote a personal template only when its name/category has no competing
        text. Exact duplicates are collapsed deterministically. Conflicting
        personal variants remain personal so the migration never discards or
        guesses between different note content.
    */
    DELETE personal_template
    FROM [tb_data].[Templates] AS personal_template
    WHERE personal_template.[ScopeType] = N'User'
      AND EXISTS
      (
          SELECT 1
          FROM [tb_data].[Templates] AS organization_template
          WHERE organization_template.[ScopeType] = N'Organization'
            AND organization_template.[Name] = personal_template.[Name]
            AND organization_template.[Category] = personal_template.[Category]
            AND organization_template.[TemplateText] = personal_template.[TemplateText]
      );

    UPDATE candidate
    SET
        [ScopeType] = N'Organization',
        [OwnerWindowsSid] = NULL
    FROM [tb_data].[Templates] AS candidate
    WHERE candidate.[ScopeType] = N'User'
      AND NOT EXISTS
      (
          SELECT 1
          FROM [tb_data].[Templates] AS conflict
          WHERE conflict.[Name] = candidate.[Name]
            AND conflict.[Category] = candidate.[Category]
            AND conflict.[TemplateText] <> candidate.[TemplateText]
      )
      AND NOT EXISTS
      (
          SELECT 1
          FROM [tb_data].[Templates] AS earlier_duplicate
          WHERE earlier_duplicate.[ScopeType] = N'User'
            AND earlier_duplicate.[Name] = candidate.[Name]
            AND earlier_duplicate.[Category] = candidate.[Category]
            AND earlier_duplicate.[TemplateText] = candidate.[TemplateText]
            AND earlier_duplicate.[Id] < candidate.[Id]
      );

    DELETE personal_template
    FROM [tb_data].[Templates] AS personal_template
    WHERE personal_template.[ScopeType] = N'User'
      AND EXISTS
      (
          SELECT 1
          FROM [tb_data].[Templates] AS organization_template
          WHERE organization_template.[ScopeType] = N'Organization'
            AND organization_template.[Name] = personal_template.[Name]
            AND organization_template.[Category] = personal_template.[Category]
            AND organization_template.[TemplateText] = personal_template.[TemplateText]
      );

    /* Promote shared connection/configuration keys; retain identity settings. */
    DECLARE @SharedSettingKeys TABLE
    (
        [SettingKey] nvarchar(200) NOT NULL PRIMARY KEY
    );

    INSERT INTO @SharedSettingKeys([SettingKey])
    VALUES
        (N'Whd.BaseUrl'),
        (N'Whd.AuthenticationMode'),
        (N'Sage.ActivityItemId');

    INSERT INTO [tb_data].[OrganizationSettings]
    (
        [SettingKey],
        [SettingValue],
        [UpdatedByWindowsSid],
        [UpdatedAtUtc]
    )
    SELECT
        shared_key.[SettingKey],
        latest_setting.[SettingValue],
        latest_setting.[OwnerWindowsSid],
        latest_setting.[UpdatedAtUtc]
    FROM @SharedSettingKeys AS shared_key
    CROSS APPLY
    (
        SELECT TOP (1)
            user_setting.[SettingValue],
            user_setting.[OwnerWindowsSid],
            user_setting.[UpdatedAtUtc]
        FROM [tb_user].[UserSettings] AS user_setting
        WHERE user_setting.[SettingKey] = shared_key.[SettingKey]
        ORDER BY
            user_setting.[UpdatedAtUtc] DESC,
            user_setting.[OwnerWindowsSid] DESC
    ) AS latest_setting
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM [tb_data].[OrganizationSettings] AS organization_setting
        WHERE organization_setting.[SettingKey] = shared_key.[SettingKey]
    );

    DELETE user_setting
    FROM [tb_user].[UserSettings] AS user_setting
    INNER JOIN @SharedSettingKeys AS shared_key
        ON shared_key.[SettingKey] = user_setting.[SettingKey];

    INSERT INTO [tb_deploy].[SchemaMigrations]
    (
        [MigrationId],
        [SchemaVersion],
        [ReleaseVersion],
        [ScriptChecksum]
    )
    VALUES
    (
        N'SqlServer2016.SharedReferenceData.0003',
        3,
        N'2.0.0-alpha.3',
        NULL
    );

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'SqlServer2016.SharedReferenceData.0003 installed.';
GO
