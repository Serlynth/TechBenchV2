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
    WHERE [MigrationId] = N'SqlServer2016.WhdMissingNoteRecovery.0009'
      AND [SchemaVersion] = 9
      AND [ReleaseVersion] = N'0.5.20'
)
BEGIN
    PRINT N'FAIL: V0009 migration marker is missing or invalid.';
    SET @FailureCount += 1;
END;

IF (SELECT MAX([SchemaVersion]) FROM [tb_deploy].[SchemaMigrations]) NOT IN (9, 10)
BEGIN
    PRINT N'FAIL: installed schema version is not 9.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.parameters
    WHERE [object_id] = OBJECT_ID(N'tb_app.DeleteWorkEntry', N'P')
      AND [name] = N'@ConfirmMissingWhdTechNote'
      AND [system_type_id] = TYPE_ID(N'bit')
)
BEGIN
    PRINT N'FAIL: DeleteWorkEntry does not require explicit missing-TechNote confirmation.';
    SET @FailureCount += 1;
END;

DECLARE @DeleteDefinition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteWorkEntry', N'P'));
IF CHARINDEX(N'@SagePosted = 1', @DeleteDefinition) = 0
   OR CHARINDEX(N'@ConfirmMissingWhdTechNote <> 1', @DeleteDefinition) = 0
   OR CHARINDEX(N'WHD sync pending:%TechNote #%was not found.%', @DeleteDefinition) = 0
   OR CHARINDEX(N'posting_log.[ExternalReference]', @DeleteDefinition) = 0
   OR CHARINDEX(N'WHD-TECHNOTE-%', @DeleteDefinition) = 0
   OR CHARINDEX(N'posting_log.[Success] = 0', @DeleteDefinition) = 0
BEGIN
    PRINT N'FAIL: DeleteWorkEntry does not preserve the verified missing-TechNote recovery boundary.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(N'CONVERT(int, 10) AS [SchemaVersion]', OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities', N'P'))) = 0
BEGIN
    PRINT N'FAIL: GetRepositoryCapabilities does not report the final schema version.';
    SET @FailureCount += 1;
END;

IF @FailureCount > 0
BEGIN
    RAISERROR(N'TechBench V0009 WHD missing-TechNote recovery verification failed with %d issue(s).', 16, 1, @FailureCount);
    RETURN;
END;

PRINT N'TechBench V0009 WHD missing-TechNote recovery verification passed.';
GO
