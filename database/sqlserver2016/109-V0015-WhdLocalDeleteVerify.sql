:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;

DECLARE @Failures int = 0;
DECLARE @DeleteDefinition nvarchar(max) =
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteWorkEntry', N'P')), N'');

IF CHARINDEX(N'@ConfirmMissingWhdTechNote bit = 0', @DeleteDefinition) = 0
   OR CHARINDEX(N'IF @SagePosted = 1', @DeleteDefinition) = 0
   OR CHARINDEX(N'IF @WhdPosted = 1', @DeleteDefinition) = 0
   OR CHARINDEX(N'@ConfirmMissingWhdTechNote <> 1', @DeleteDefinition) = 0
BEGIN
    PRINT N'FAIL: DeleteWorkEntry does not require explicit local-delete confirmation while preserving the Sage lock.';
    SET @Failures += 1;
END;

IF CHARINDEX(N'WHD sync pending:%TechNote #%was not found.%', @DeleteDefinition) > 0
   OR CHARINDEX(N'posting_log.[ExternalReference]', @DeleteDefinition) > 0
BEGIN
    PRINT N'FAIL: DeleteWorkEntry still depends on obsolete WHD note synchronization evidence.';
    SET @Failures += 1;
END;

IF CHARINDEX(
       N'FROM [tb_ops].[PostingAttempts] WITH (UPDLOCK, HOLDLOCK)',
       @DeleteDefinition) = 0
   OR CHARINDEX(
       N'FROM [tb_ops].[PostingLeases] WITH (UPDLOCK, HOLDLOCK)',
       @DeleteDefinition) = 0
BEGIN
    PRINT N'FAIL: DeleteWorkEntry no longer protects active external posting operations.';
    SET @Failures += 1;
END;

IF @Failures > 0
    THROW 52226, N'TechBench WHD local-delete verification failed.', 1;

PRINT N'TechBench schema-15-compatible WHD local-delete verification passed.';
GO
