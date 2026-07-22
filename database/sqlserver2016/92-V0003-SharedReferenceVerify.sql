:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @FailureCount int = 0;
DECLARE @InstalledSchemaVersion int =
(
    SELECT MAX([SchemaVersion])
    FROM [tb_deploy].[SchemaMigrations]
);

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.SharedReferenceData.0003'
      AND [SchemaVersion] = 3
      AND [ReleaseVersion] = N'2.0.0-alpha.3'
)
BEGIN
    PRINT N'FAIL: SharedReferenceData.0003 migration marker is missing or invalid.';
    SET @FailureCount += 1;
END;

IF @InstalledSchemaVersion NOT IN (3, 4, 5, 6, 7, 8, 9)
BEGIN
    PRINT N'FAIL: V0003 verification supports installed schema version 3, 4, 5, 6, 7, 8, or 9.';
    SET @FailureCount += 1;
END;

IF OBJECT_ID(N'tb_data.OrganizationTags', N'U') IS NULL
BEGIN
    PRINT N'FAIL: tb_data.OrganizationTags is missing.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'tb_data.OrganizationTags')
      AND [name] = N'Tag'
      AND TYPE_NAME([user_type_id]) = N'nvarchar'
      AND [max_length] = 2000
      AND [is_nullable] = 0
)
BEGIN
    PRINT N'FAIL: OrganizationTags.Tag is not bounded to nvarchar(1000) NOT NULL.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'tb_data.OrganizationTags')
      AND [name] = N'RowVersion'
      AND [system_type_id] = 189
      AND [is_nullable] = 0
)
BEGIN
    PRINT N'FAIL: OrganizationTags lacks its required rowversion concurrency token.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'tb_data.OrganizationTags')
      AND [name] = N'TagHash'
      AND TYPE_NAME([user_type_id]) = N'binary'
      AND [max_length] = 32
      AND [is_nullable] = 0
)
BEGIN
    PRINT N'FAIL: OrganizationTags.TagHash is not binary(32) NOT NULL.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'tb_data.OrganizationTags')
      AND [name] = N'UX_OrganizationTags_TagHash'
      AND [is_unique] = 1
)
BEGIN
    PRINT N'FAIL: The canonical organization-tag hash is not uniquely indexed.';
    SET @FailureCount += 1;
END;

IF @InstalledSchemaVersion = 3
   AND OBJECT_ID(N'tb_data.OrganizationTags', N'U') IS NOT NULL
   AND EXISTS
   (
       SELECT 1
       FROM [tb_data].[OrganizationTags]
       WHERE NULLIF(LTRIM(RTRIM([Tag])), N'') IS NULL
          OR [Tag] <> LTRIM(RTRIM([Tag]))
          OR DATALENGTH([Tag]) > 2000
   )
BEGIN
    PRINT N'FAIL: OrganizationTags contains a blank, untrimmed, or over-length tag.';
    SET @FailureCount += 1;
END;

IF @InstalledSchemaVersion = 3
   AND OBJECT_ID(N'tb_data.OrganizationTags', N'U') IS NOT NULL
   AND EXISTS
   (
       SELECT 1
       FROM [tb_data].[WorkEntries] AS work_entry
       CROSS APPLY STRING_SPLIT(COALESCE(work_entry.[Tags], N''), N',') AS tag
       WHERE NULLIF(LTRIM(RTRIM(tag.[value])), N'') IS NOT NULL
         AND NOT EXISTS
         (
             SELECT 1
             FROM [tb_data].[OrganizationTags] AS organization_tag
             WHERE organization_tag.[TagHash] =
                 CONVERT
                 (
                     binary(32),
                     HASHBYTES
                     (
                         N'SHA2_256',
                         CONVERT
                         (
                             varbinary(2000),
                             UPPER(LTRIM(RTRIM(tag.[value])))
                         )
                     )
                 )
         )
   )
BEGIN
    PRINT N'FAIL: One or more existing work-entry tags were not backfilled.';
    SET @FailureCount += 1;
END;

DECLARE @GetDistinctTagsDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetDistinctTags'));
DECLARE @SaveWorkEntryDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveWorkEntry'));

IF @GetDistinctTagsDefinition IS NULL
   OR CHARINDEX(N'[tb_data].[WorkEntries]', @GetDistinctTagsDefinition) = 0
   OR CHARINDEX(N'work_entry.[OwnerWindowsSid] = @UserSid', @GetDistinctTagsDefinition) = 0
   OR CHARINDEX(N'STRING_SPLIT', @GetDistinctTagsDefinition) = 0
   OR CHARINDEX(N'[tb_data].[OrganizationTags]', @GetDistinctTagsDefinition) > 0
BEGIN
    PRINT N'FAIL: GetDistinctTags is not isolated to tags used by the effective user.';
    SET @FailureCount += 1;
END;

IF @SaveWorkEntryDefinition IS NULL
   OR
   (
       @InstalledSchemaVersion = 3
       AND
       (
           CHARINDEX(N'[tb_data].[OrganizationTags]', @SaveWorkEntryDefinition) = 0
           OR CHARINDEX(N'UPDLOCK', @SaveWorkEntryDefinition) = 0
           OR CHARINDEX(N'HOLDLOCK', @SaveWorkEntryDefinition) = 0
           OR CHARINDEX(N'MERGE', @SaveWorkEntryDefinition) > 0
       )
   )
   OR
   (
       @InstalledSchemaVersion >= 4
       AND CHARINDEX(N'[tb_data].[OrganizationTags]', @SaveWorkEntryDefinition) > 0
   )
BEGIN
    PRINT N'FAIL: SaveWorkEntry does not implement the installed schema version tag-catalog boundary.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
    FROM [tb_data].[CommonLinks]
    WHERE [ScopeType] <> N'Organization'
       OR [OwnerWindowsSid] IS NOT NULL
)
BEGIN
    PRINT N'FAIL: A Common Link remains outside organization scope.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
    FROM [tb_data].[ClientAliases]
    WHERE [ScopeType] <> N'Organization'
       OR [OwnerWindowsSid] IS NOT NULL
)
BEGIN
    PRINT N'FAIL: A client alias remains outside organization scope.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.default_constraints AS default_constraint
    INNER JOIN sys.columns AS column_definition
        ON column_definition.[object_id] = default_constraint.[parent_object_id]
       AND column_definition.[column_id] = default_constraint.[parent_column_id]
    WHERE default_constraint.[parent_object_id] = OBJECT_ID(N'tb_data.CommonLinks')
      AND column_definition.[name] = N'ScopeType'
      AND CHARINDEX(N'Organization', default_constraint.[definition]) > 0
)
BEGIN
    PRINT N'FAIL: CommonLinks.ScopeType does not default to Organization.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.default_constraints AS default_constraint
    INNER JOIN sys.columns AS column_definition
        ON column_definition.[object_id] = default_constraint.[parent_object_id]
       AND column_definition.[column_id] = default_constraint.[parent_column_id]
    WHERE default_constraint.[parent_object_id] = OBJECT_ID(N'tb_data.ClientAliases')
      AND column_definition.[name] = N'ScopeType'
      AND CHARINDEX(N'Organization', default_constraint.[definition]) > 0
)
BEGIN
    PRINT N'FAIL: ClientAliases.ScopeType does not default to Organization.';
    SET @FailureCount += 1;
END;

DECLARE @SaveCommonLinkDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveCommonLink'));
DECLARE @DeleteCommonLinkDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteCommonLink'));
DECLARE @GetTemplatesDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetTemplates'));
DECLARE @SaveTemplateDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveTemplate'));
DECLARE @DeleteTemplateDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteTemplate'));
DECLARE @SaveClientAliasDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveClientAlias'));
DECLARE @DeleteClientAliasDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteClientAlias'));

IF @GetTemplatesDefinition IS NULL
   OR CHARINDEX(N'[ScopeType] = N''Organization''', @GetTemplatesDefinition) = 0
   OR CHARINDEX(N'[ScopeType] = N''User''', @GetTemplatesDefinition) = 0
   OR @SaveTemplateDefinition IS NULL
   OR CHARINDEX(N'@ScopeType nvarchar(20) = N''Organization''', @SaveTemplateDefinition) = 0
   OR CHARINDEX(N'@ScopeType <> N''Organization''', @SaveTemplateDefinition) = 0
   OR CHARINDEX(N'@IsAdmin <> 1', @SaveTemplateDefinition) = 0
   OR CHARINDEX(N'[ScopeType] = N''Organization''', @SaveTemplateDefinition) = 0
   OR @DeleteTemplateDefinition IS NULL
   OR CHARINDEX(N'@IsAdmin <> 1', @DeleteTemplateDefinition) = 0
   OR CHARINDEX(N'[ScopeType] = N''Organization''', @DeleteTemplateDefinition) = 0
BEGIN
    PRINT N'FAIL: Templates are not organization/Admin-managed with legacy personal rows read-only.';
    SET @FailureCount += 1;
END;

IF @SaveCommonLinkDefinition IS NULL
   OR CHARINDEX(N'@ScopeType nvarchar(20) = N''Organization''', @SaveCommonLinkDefinition) = 0
   OR CHARINDEX(N'@IsAdmin <> 1', @SaveCommonLinkDefinition) = 0
   OR @DeleteCommonLinkDefinition IS NULL
   OR CHARINDEX(N'@IsAdmin <> 1', @DeleteCommonLinkDefinition) = 0
BEGIN
    PRINT N'FAIL: Common-link writes are not organization-only and Admin-managed.';
    SET @FailureCount += 1;
END;

IF @SaveClientAliasDefinition IS NULL
   OR CHARINDEX(N'@ScopeType nvarchar(20) = N''Organization''', @SaveClientAliasDefinition) = 0
   OR CHARINDEX(N'UPDLOCK', @SaveClientAliasDefinition) = 0
   OR CHARINDEX(N'HOLDLOCK', @SaveClientAliasDefinition) = 0
   OR CHARINDEX(N'@IsAdmin <> 1', @SaveClientAliasDefinition) = 0
   OR @DeleteClientAliasDefinition IS NULL
   OR CHARINDEX(N'@IsAdmin <> 1', @DeleteClientAliasDefinition) = 0
BEGIN
    PRINT N'FAIL: Client aliases do not implement shared idempotent insert and Admin-only mutation.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'@ScopeType = N''Organization''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminSaveTemplate'))) = 0
   OR CHARINDEX(
       N'@ScopeType = N''Organization''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminSaveCommonLink'))) = 0
   OR CHARINDEX(
       N'@ScopeType = N''Organization''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminSaveClientAlias'))) = 0
BEGIN
    PRINT N'FAIL: An administrative shared-data wrapper does not use Organization scope.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
    FROM [tb_user].[UserSettings]
    WHERE [SettingKey] IN
    (
        N'Whd.BaseUrl',
        N'Whd.AuthenticationMode'
    )
)
BEGIN
    PRINT N'FAIL: A shared connection/configuration key remains user-scoped.';
    SET @FailureCount += 1;
END;

IF @InstalledSchemaVersion = 3
   AND
   (
       CHARINDEX(
           N'Whd.BaseUrl',
           OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveUserSetting'))) = 0
       OR CHARINDEX(
           N'Whd.AuthenticationMode',
           OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveUserSetting'))) = 0
   )
BEGIN
    PRINT N'FAIL: SaveUserSetting does not protect organization-scoped keys.';
    SET @FailureCount += 1;
END;

IF EXISTS
(
    SELECT 1
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
      )
)
BEGIN
    PRINT N'FAIL: An exact personal/organization template duplicate was not deduplicated.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'CONVERT(int,' + CONVERT(nvarchar(10), @InstalledSchemaVersion) + N')',
       REPLACE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities')), N' ', N'')) = 0
BEGIN
    PRINT N'FAIL: GetRepositoryCapabilities does not report the installed schema version.';
    SET @FailureCount += 1;
END;

DECLARE @ExpectedGrants TABLE
(
    [RoleName] sysname NOT NULL,
    [ObjectName] nvarchar(300) NOT NULL,
    PRIMARY KEY ([RoleName], [ObjectName])
);

INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
VALUES
    (N'tb_role_user', N'tb_app.GetRepositoryCapabilities'),
    (N'tb_role_user', N'tb_app.GetDistinctTags'),
    (N'tb_role_user', N'tb_app.SaveWorkEntry'),
    (N'tb_role_user', N'tb_app.GetCommonLinks'),
    (N'tb_role_user', N'tb_app.GetClientAliases'),
    (N'tb_role_user', N'tb_app.SaveUserSetting'),
    (N'tb_role_user', N'tb_app.DeleteUserSetting'),
    (N'tb_role_admin', N'tb_app.AdminSaveOrganizationSetting'),
    (N'tb_role_admin', N'tb_app.AdminDeleteOrganizationSetting');

IF @InstalledSchemaVersion = 3
BEGIN
    INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
    VALUES
        (N'tb_role_user', N'tb_app.SaveCommonLink'),
        (N'tb_role_user', N'tb_app.DeleteCommonLink'),
        (N'tb_role_user', N'tb_app.SaveClientAlias'),
        (N'tb_role_user', N'tb_app.DeleteClientAlias');
END
ELSE
BEGIN
    INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
    VALUES
        (N'tb_role_admin', N'tb_app.SaveCommonLink'),
        (N'tb_role_admin', N'tb_app.DeleteCommonLink'),
        (N'tb_role_admin', N'tb_app.SaveClientAlias'),
        (N'tb_role_admin', N'tb_app.DeleteClientAlias');
END;

DECLARE @MissingGrantCount int =
(
    SELECT COUNT(*)
    FROM @ExpectedGrants AS expected_grant
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.database_permissions AS permission
        INNER JOIN sys.database_principals AS grantee
            ON grantee.[principal_id] = permission.[grantee_principal_id]
        WHERE grantee.[name] = expected_grant.[RoleName]
          AND permission.[class] = 1
          AND permission.[major_id] = OBJECT_ID(expected_grant.[ObjectName])
          AND permission.[permission_name] = N'EXECUTE'
          AND permission.[state] IN (N'G', N'W')
    )
);

IF @MissingGrantCount > 0
BEGIN
    PRINT N'FAIL: One or more V0003 procedure grants are missing.';
    SET @FailureCount += @MissingGrantCount;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission
    INNER JOIN sys.database_principals AS grantee
        ON grantee.[principal_id] = permission.[grantee_principal_id]
    WHERE permission.[class] = 1
      AND permission.[major_id] = OBJECT_ID(N'tb_data.OrganizationTags')
      AND grantee.[name] IN
      (
          N'tb_role_user',
          N'tb_role_manager',
          N'tb_role_admin',
          N'tb_role_sync_operator'
      )
      AND permission.[permission_name] IN
      (
          N'SELECT', N'INSERT', N'UPDATE', N'DELETE', N'CONTROL', N'ALTER'
      )
      AND permission.[state] IN (N'G', N'W')
)
BEGIN
    PRINT N'FAIL: An application role has direct access to OrganizationTags.';
    SET @FailureCount += 1;
END;

IF @FailureCount > 0
BEGIN
    RAISERROR(
        N'TechBench V0003 shared-reference verification failed with %d issue(s).',
        16,
        1,
        @FailureCount);
    RETURN;
END;

PRINT N'TechBench V0003 shared-reference verification passed.';

SELECT
    DB_NAME() AS [DatabaseName],
    MAX([SchemaVersion]) AS [SchemaVersion],
    MAX
    (
        CASE
            WHEN [MigrationId] = N'SqlServer2016.SharedReferenceData.0003'
                THEN [AppliedAtUtc]
        END
    ) AS [SharedReferenceDataAppliedAtUtc]
FROM [tb_deploy].[SchemaMigrations];
GO
