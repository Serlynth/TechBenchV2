:ON ERROR EXIT

USE [master];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DatabaseName sysname = N'$(DatabaseName)';
DECLARE @DatabaseOwnerLogin sysname = N'$(DatabaseOwnerLogin)';
DECLARE @Sql nvarchar(max);

IF SUSER_ID(@DatabaseOwnerLogin) IS NULL
BEGIN
    SET @Sql =
        N'CREATE LOGIN ' + QUOTENAME(@DatabaseOwnerLogin)
        + N' FROM WINDOWS WITH DEFAULT_DATABASE = [master];';
    EXEC sys.sp_executesql @Sql;
END;

IF DB_ID(@DatabaseName) IS NULL
BEGIN
    SET @Sql = N'CREATE DATABASE ' + QUOTENAME(@DatabaseName) + N';';
    EXEC sys.sp_executesql @Sql;
END;

SET @Sql =
    N'ALTER AUTHORIZATION ON DATABASE::' + QUOTENAME(@DatabaseName)
    + N' TO ' + QUOTENAME(@DatabaseOwnerLogin) + N';';
EXEC sys.sp_executesql @Sql;
GO

ALTER DATABASE [$(DatabaseName)] SET COMPATIBILITY_LEVEL = 130;
ALTER DATABASE [$(DatabaseName)] SET AUTO_CLOSE OFF;
ALTER DATABASE [$(DatabaseName)] SET AUTO_SHRINK OFF;
ALTER DATABASE [$(DatabaseName)] SET PAGE_VERIFY CHECKSUM;
ALTER DATABASE [$(DatabaseName)] SET TRUSTWORTHY OFF;
ALTER DATABASE [$(DatabaseName)] SET DB_CHAINING OFF;
ALTER DATABASE [$(DatabaseName)] SET ALLOW_SNAPSHOT_ISOLATION ON;

IF EXISTS
(
    SELECT 1
    FROM sys.databases
    WHERE name = N'$(DatabaseName)'
      AND is_read_committed_snapshot_on = 0
)
BEGIN
    ALTER DATABASE [$(DatabaseName)]
        SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
END;
GO

PRINT N'TechBench database exists, is DBA-owned, and uses compatibility level 130.';
GO
