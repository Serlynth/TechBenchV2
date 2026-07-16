:ON ERROR EXIT

USE [master];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DatabaseName sysname = N'$(DatabaseName)';
DECLARE @DatabaseOwnerLogin sysname = SUSER_SNAME(0x01);
DECLARE @Sql nvarchar(max);

IF @DatabaseOwnerLogin IS NULL
BEGIN
    RAISERROR(
        N'The built-in SQL Server owner principal (SID 0x01) could not be resolved.',
        16,
        1);
    RETURN;
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
ALTER DATABASE [$(DatabaseName)] SET RECOVERY SIMPLE;
ALTER DATABASE [$(DatabaseName)] SET AUTO_CLOSE OFF;
ALTER DATABASE [$(DatabaseName)] SET AUTO_SHRINK OFF;
ALTER DATABASE [$(DatabaseName)] SET PAGE_VERIFY CHECKSUM;
ALTER DATABASE [$(DatabaseName)] SET TRUSTWORTHY OFF;
ALTER DATABASE [$(DatabaseName)] SET DB_CHAINING OFF;
ALTER DATABASE [$(DatabaseName)] SET ALLOW_SNAPSHOT_ISOLATION ON;
GO

USE [master];
GO

DECLARE @DatabaseName sysname = N'$(DatabaseName)';
DECLARE @DataLogicalName sysname;
DECLARE @LogLogicalName sysname;
DECLARE @DataSizePages int;
DECLARE @LogSizePages int;
DECLARE @Sql nvarchar(max);

SELECT TOP (1)
    @DataLogicalName = [name],
    @DataSizePages = [size]
FROM sys.master_files
WHERE [database_id] = DB_ID(@DatabaseName)
  AND [type] = 0
ORDER BY [file_id];

SELECT TOP (1)
    @LogLogicalName = [name],
    @LogSizePages = [size]
FROM sys.master_files
WHERE [database_id] = DB_ID(@DatabaseName)
  AND [type] = 1
ORDER BY [file_id];

IF @DataLogicalName IS NULL OR @LogLogicalName IS NULL
BEGIN
    RAISERROR(
        N'TechBench database data or log file metadata could not be resolved.',
        16,
        1);
    RETURN;
END;

SET @Sql =
    N'ALTER DATABASE ' + QUOTENAME(@DatabaseName)
    + N' MODIFY FILE (NAME = N' + QUOTENAME(@DataLogicalName, N'''')
    + CASE
        WHEN @DataSizePages < 32768 THEN N', SIZE = 256MB'
        ELSE N''
      END
    + N', FILEGROWTH = 64MB);';
EXEC sys.sp_executesql @Sql;

SET @Sql =
    N'ALTER DATABASE ' + QUOTENAME(@DatabaseName)
    + N' MODIFY FILE (NAME = N' + QUOTENAME(@LogLogicalName, N'''')
    + CASE
        WHEN @LogSizePages < 16384 THEN N', SIZE = 128MB'
        ELSE N''
      END
    + N', FILEGROWTH = 64MB);';
EXEC sys.sp_executesql @Sql;
GO

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

PRINT N'TechBench database exists with compatibility level 130, SIMPLE recovery, and fixed-MB file growth.';
GO
