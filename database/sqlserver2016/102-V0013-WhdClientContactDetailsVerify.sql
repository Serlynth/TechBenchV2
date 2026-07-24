:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @FailureCount int = 0;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.WhdClientContactDetails.0013'
      AND [SchemaVersion] = 13
)
BEGIN
    PRINT N'FAIL: V0013 WHD client contact migration is not installed.';
    SET @FailureCount += 1;
END;

IF COL_LENGTH(N'tb_data.Clients', N'WhdContactEmail') IS NULL
   OR COL_LENGTH(N'tb_data.Clients', N'WhdPhone') IS NULL
   OR COL_LENGTH(N'tb_data.Clients', N'WhdAddress') IS NULL
BEGIN
    PRINT N'FAIL: one or more WHD client contact columns are missing.';
    SET @FailureCount += 1;
END;

DECLARE @CapabilitiesDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities', N'P'));
DECLARE @SearchDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SearchClients', N'P'));
DECLARE @GetDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetClient', N'P'));
DECLARE @ApplyDefinition nvarchar(max) =
    OBJECT_DEFINITION(OBJECT_ID(N'tb_service.ApplyWhdClientSnapshot', N'P'));

IF CHARINDEX(N'CONVERT(int, 13) AS [SchemaVersion]', COALESCE(@CapabilitiesDefinition, N'')) = 0
BEGIN
    PRINT N'FAIL: repository capabilities do not report schema version 13.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'client.[WhdContactEmail]', COALESCE(@SearchDefinition, N'')) = 0
   OR CHARINDEX(N'client.[WhdPhone]', COALESCE(@SearchDefinition, N'')) = 0
   OR CHARINDEX(N'client.[WhdAddress]', COALESCE(@SearchDefinition, N'')) = 0
   OR CHARINDEX(N'client.[WhdContactEmail]', COALESCE(@GetDefinition, N'')) = 0
BEGIN
    PRINT N'FAIL: approved client readers do not return WHD contact details.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'[ContactEmail] nvarchar(255) ''$.contactEmail''', COALESCE(@ApplyDefinition, N'')) = 0
   OR CHARINDEX(N'[WhdContactEmail]', COALESCE(@ApplyDefinition, N'')) = 0
   OR CHARINDEX(N'[WhdPhone]', COALESCE(@ApplyDefinition, N'')) = 0
   OR CHARINDEX(N'[WhdAddress]', COALESCE(@ApplyDefinition, N'')) = 0
BEGIN
    PRINT N'FAIL: WHD synchronization does not persist the new contact details.';
    SET @FailureCount += 1;
END;

IF @FailureCount > 0
    THROW 52100, N'TechBench V0013 WHD client contact verification failed.', 1;

PRINT N'TechBench V0013 WHD client contact verification passed.';
GO
