:ON ERROR EXIT

USE [master];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DatabaseName sysname = N'$(DatabaseName)';
DECLARE @UserGroup sysname = N'$(UserGroup)';
DECLARE @AdminGroup sysname = N'$(AdminGroup)';
DECLARE @SyncServicePrincipal sysname = N'$(SyncServicePrincipal)';
DECLARE @ProductMajorVersion int =
    TRY_CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion'));
DECLARE @ProductVersion nvarchar(128) =
    CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductVersion'));
DECLARE @ProductBuild int =
    TRY_CONVERT(int, PARSENAME(@ProductVersion, 2));

IF @ProductMajorVersion IS NULL OR @ProductMajorVersion < 13
BEGIN
    RAISERROR(
        N'TechBench V2 requires SQL Server 2016 (13.x) or newer. Detected version: %s.',
        16,
        1,
        @ProductVersion);
    RETURN;
END;

IF IS_SRVROLEMEMBER(N'sysadmin') <> 1
BEGIN
    RAISERROR(
        N'The initial TechBench deployment must run under a SQL Server sysadmin login.',
        16,
        1);
    RETURN;
END;

IF NULLIF(LTRIM(RTRIM(@DatabaseName)), N'') IS NULL
   OR LEN(@DatabaseName) > 128
   OR CHARINDEX(N']', @DatabaseName) > 0
BEGIN
    RAISERROR(
        N'DatabaseName must be a nonempty SQL identifier of at most 128 characters and cannot contain ].',
        16,
        1);
    RETURN;
END;

IF NULLIF(LTRIM(RTRIM(@UserGroup)), N'') IS NULL
   OR NULLIF(LTRIM(RTRIM(@AdminGroup)), N'') IS NULL
   OR NULLIF(LTRIM(RTRIM(@SyncServicePrincipal)), N'') IS NULL
BEGIN
    RAISERROR(N'UserGroup, AdminGroup, and SyncServicePrincipal must all be supplied.', 16, 1);
    RETURN;
END;

IF @UserGroup NOT LIKE N'%\%'
   OR @AdminGroup NOT LIKE N'%\%'
   OR @SyncServicePrincipal NOT LIKE N'%\%'
BEGIN
    RAISERROR(
        N'Application groups and SyncServicePrincipal must use DOMAIN\name format.',
        16,
        1);
    RETURN;
END;

IF @UserGroup = @AdminGroup
   OR @UserGroup = @SyncServicePrincipal
   OR @AdminGroup = @SyncServicePrincipal
BEGIN
    RAISERROR(
        N'UserGroup, AdminGroup, and SyncServicePrincipal must be three distinct principals.',
        16,
        1);
    RETURN;
END;

BEGIN TRY
    EXEC master.dbo.xp_logininfo
        @acctname = @UserGroup,
        @option = N'members';

    EXEC master.dbo.xp_logininfo
        @acctname = @AdminGroup,
        @option = N'members';

    /* Resolves the dedicated service account without enumerating members. */
    EXEC master.dbo.xp_logininfo
        @acctname = @SyncServicePrincipal;
END TRY
BEGIN CATCH
    DECLARE @GroupError nvarchar(2048) = ERROR_MESSAGE();
    RAISERROR(
        N'SQL Server could not resolve one of the TechBench AD groups: %s',
        16,
        1,
        @GroupError);
    RETURN;
END CATCH;

IF SUSER_SNAME(0x01) IS NULL
BEGIN
    RAISERROR(
        N'The built-in SQL Server owner principal (SID 0x01) could not be resolved.',
        16,
        1);
    RETURN;
END;

PRINT N'TechBench SQL Server preflight passed.';
PRINT N'  Server version: ' + CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductVersion'));
IF @ProductMajorVersion = 13 AND ISNULL(@ProductBuild, 0) < 6300
BEGIN
    PRINT N'WARNING: SQL Server 2016 SP3 starts at build 13.0.6300.2. Patch and vendor-test this instance before production deployment.';
END;
IF @ProductMajorVersion = 13
BEGIN
    PRINT N'WARNING: SQL Server 2016 normal extended support ended July 14, 2026. Confirm ESU coverage or an upgrade plan.';
END;
PRINT N'  Database: ' + @DatabaseName;
PRINT N'  Database owner: ' + SUSER_SNAME(0x01) + N' (built-in SID 0x01)';
PRINT N'  User group: ' + @UserGroup;
PRINT N'  Admin group: ' + @AdminGroup;
PRINT N'  WHD sync service principal: ' + @SyncServicePrincipal;
PRINT N'AD group resolution passed.';
GO
