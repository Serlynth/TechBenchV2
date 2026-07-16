:ON ERROR EXIT

USE [master];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Principal sysname;
DECLARE @Sql nvarchar(max);

DECLARE PrincipalCursor CURSOR LOCAL FAST_FORWARD FOR
SELECT [PrincipalName]
FROM
(
    VALUES
        (N'$(UserGroup)'),
        (N'$(AdminGroup)')
) AS Principals([PrincipalName]);

OPEN PrincipalCursor;
FETCH NEXT FROM PrincipalCursor INTO @Principal;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF SUSER_ID(@Principal) IS NULL
    BEGIN
        SET @Sql =
            N'CREATE LOGIN ' + QUOTENAME(@Principal)
            + N' FROM WINDOWS WITH DEFAULT_DATABASE = '
            + QUOTENAME(N'$(DatabaseName)') + N';';
        EXEC sys.sp_executesql @Sql;
    END;

    FETCH NEXT FROM PrincipalCursor INTO @Principal;
END;

CLOSE PrincipalCursor;
DEALLOCATE PrincipalCursor;
GO

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @LegacyRoleName sysname;
DECLARE @LegacyMemberName sysname;
DECLARE @LegacySql nvarchar(max);

DECLARE LegacyMembershipCursor CURSOR LOCAL FAST_FORWARD FOR
SELECT
    role_principal.[name],
    member_principal.[name]
FROM sys.database_role_members AS drm
INNER JOIN sys.database_principals AS role_principal
    ON role_principal.[principal_id] = drm.[role_principal_id]
INNER JOIN sys.database_principals AS member_principal
    ON member_principal.[principal_id] = drm.[member_principal_id]
WHERE role_principal.[name] IN
    (N'tb_role_auditor', N'tb_role_deployer');

OPEN LegacyMembershipCursor;
FETCH NEXT FROM LegacyMembershipCursor
INTO @LegacyRoleName, @LegacyMemberName;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @LegacySql =
        N'ALTER ROLE ' + QUOTENAME(@LegacyRoleName)
        + N' DROP MEMBER ' + QUOTENAME(@LegacyMemberName) + N';';
    EXEC sys.sp_executesql @LegacySql;

    FETCH NEXT FROM LegacyMembershipCursor
    INTO @LegacyRoleName, @LegacyMemberName;
END;

CLOSE LegacyMembershipCursor;
DEALLOCATE LegacyMembershipCursor;

IF DATABASE_PRINCIPAL_ID(N'tb_role_auditor') IS NOT NULL
    DROP ROLE [tb_role_auditor];
IF DATABASE_PRINCIPAL_ID(N'tb_role_deployer') IS NOT NULL
    DROP ROLE [tb_role_deployer];

IF DATABASE_PRINCIPAL_ID(N'tb_role_user') IS NULL
    CREATE ROLE [tb_role_user] AUTHORIZATION [dbo];
IF DATABASE_PRINCIPAL_ID(N'tb_role_manager') IS NULL
    CREATE ROLE [tb_role_manager] AUTHORIZATION [dbo];
IF DATABASE_PRINCIPAL_ID(N'tb_role_admin') IS NULL
    CREATE ROLE [tb_role_admin] AUTHORIZATION [dbo];
IF DATABASE_PRINCIPAL_ID(N'tb_role_sync_operator') IS NULL
    CREATE ROLE [tb_role_sync_operator] AUTHORIZATION [dbo];

DECLARE @Principal sysname;
DECLARE @DefaultSchema sysname;
DECLARE @Sql nvarchar(max);

DECLARE UserCursor CURSOR LOCAL FAST_FORWARD FOR
SELECT [PrincipalName], [DefaultSchema]
FROM
(
    VALUES
        (N'$(UserGroup)', N'tb_app'),
        (N'$(AdminGroup)', N'tb_app')
) AS Principals([PrincipalName], [DefaultSchema]);

OPEN UserCursor;
FETCH NEXT FROM UserCursor INTO @Principal, @DefaultSchema;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF DATABASE_PRINCIPAL_ID(@Principal) IS NULL
    BEGIN
        SET @Sql =
            N'CREATE USER ' + QUOTENAME(@Principal)
            + N' FOR LOGIN ' + QUOTENAME(@Principal)
            + N' WITH DEFAULT_SCHEMA = ' + QUOTENAME(@DefaultSchema) + N';';
        EXEC sys.sp_executesql @Sql;
    END;

    FETCH NEXT FROM UserCursor INTO @Principal, @DefaultSchema;
END;

CLOSE UserCursor;
DEALLOCATE UserCursor;

DECLARE @Membership TABLE
(
    [RoleName] sysname NOT NULL,
    [MemberName] sysname NOT NULL
);

INSERT INTO @Membership([RoleName], [MemberName])
VALUES
    (N'tb_role_user', N'$(UserGroup)'),
    (N'tb_role_user', N'$(AdminGroup)'),
    (N'tb_role_manager', N'$(AdminGroup)'),
    (N'tb_role_admin', N'$(AdminGroup)'),
    (N'tb_role_sync_operator', N'$(AdminGroup)');

DECLARE @RoleName sysname;
DECLARE @MemberName sysname;

DECLARE MembershipCursor CURSOR LOCAL FAST_FORWARD FOR
SELECT [RoleName], [MemberName]
FROM @Membership;

OPEN MembershipCursor;
FETCH NEXT FROM MembershipCursor INTO @RoleName, @MemberName;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.database_role_members AS drm
        INNER JOIN sys.database_principals AS role_principal
            ON role_principal.principal_id = drm.role_principal_id
        INNER JOIN sys.database_principals AS member_principal
            ON member_principal.principal_id = drm.member_principal_id
        WHERE role_principal.name = @RoleName
          AND member_principal.name = @MemberName
    )
    BEGIN
        SET @Sql =
            N'ALTER ROLE ' + QUOTENAME(@RoleName)
            + N' ADD MEMBER ' + QUOTENAME(@MemberName) + N';';
        EXEC sys.sp_executesql @Sql;
    END;

    FETCH NEXT FROM MembershipCursor INTO @RoleName, @MemberName;
END;

CLOSE MembershipCursor;
DEALLOCATE MembershipCursor;

PRINT N'TechBench AD logins, database users, and role memberships are configured.';
GO
