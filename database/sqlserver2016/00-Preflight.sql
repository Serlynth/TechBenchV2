:ON ERROR EXIT

USE [master];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DatabaseName sysname = N'$(DatabaseName)';
DECLARE @DatabaseOwnerLogin sysname = N'$(DatabaseOwnerLogin)';
DECLARE @DeploymentGroup sysname = N'$(DeploymentGroup)';
DECLARE @TechnicianGroup sysname = N'$(TechnicianGroup)';
DECLARE @ManagerGroup sysname = N'$(ManagerGroup)';
DECLARE @AdminGroup sysname = N'$(AdminGroup)';
DECLARE @SyncOperatorGroup sysname = N'$(SyncOperatorGroup)';
DECLARE @AuditReaderGroup sysname = N'$(AuditReaderGroup)';
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
        N'The initial TechBench deployment must run under a sysadmin Windows login.',
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

IF NULLIF(LTRIM(RTRIM(@DatabaseOwnerLogin)), N'') IS NULL
   OR NULLIF(LTRIM(RTRIM(@DeploymentGroup)), N'') IS NULL
   OR NULLIF(LTRIM(RTRIM(@TechnicianGroup)), N'') IS NULL
   OR NULLIF(LTRIM(RTRIM(@ManagerGroup)), N'') IS NULL
   OR NULLIF(LTRIM(RTRIM(@AdminGroup)), N'') IS NULL
   OR NULLIF(LTRIM(RTRIM(@SyncOperatorGroup)), N'') IS NULL
   OR NULLIF(LTRIM(RTRIM(@AuditReaderGroup)), N'') IS NULL
BEGIN
    RAISERROR(N'Every AD-principal SQLCMD variable must be supplied.', 16, 1);
    RETURN;
END;

IF @DatabaseOwnerLogin NOT LIKE N'%\%'
   OR @DeploymentGroup NOT LIKE N'%\%'
   OR @TechnicianGroup NOT LIKE N'%\%'
   OR @ManagerGroup NOT LIKE N'%\%'
   OR @AdminGroup NOT LIKE N'%\%'
   OR @SyncOperatorGroup NOT LIKE N'%\%'
   OR @AuditReaderGroup NOT LIKE N'%\%'
BEGIN
    RAISERROR(
        N'AD principals must use DOMAIN\name format. SQL logins are not accepted by this package.',
        16,
        1);
    RETURN;
END;

IF @TechnicianGroup = @AdminGroup
BEGIN
    RAISERROR(
        N'TechnicianGroup and AdminGroup must be distinct so ordinary users do not receive administration rights.',
        16,
        1);
    RETURN;
END;

IF @DatabaseOwnerLogin IN
       (@DeploymentGroup, @TechnicianGroup, @ManagerGroup, @AdminGroup,
        @SyncOperatorGroup, @AuditReaderGroup)
   OR @DeploymentGroup IN
       (@TechnicianGroup, @ManagerGroup, @AdminGroup,
        @SyncOperatorGroup, @AuditReaderGroup)
BEGIN
    RAISERROR(
        N'DatabaseOwnerLogin and DeploymentGroup must be dedicated DBA principals, separate from each other and the application groups.',
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
PRINT N'  Database owner: ' + @DatabaseOwnerLogin;
PRINT N'  Deployment group: ' + @DeploymentGroup;
PRINT N'  Technician group: ' + @TechnicianGroup;
PRINT N'  Manager group: ' + @ManagerGroup;
PRINT N'  Admin group: ' + @AdminGroup;
PRINT N'  Sync Operator group: ' + @SyncOperatorGroup;
PRINT N'  Audit Reader group: ' + @AuditReaderGroup;
PRINT N'AD principal resolution is performed by the create/security scripts.';
GO
