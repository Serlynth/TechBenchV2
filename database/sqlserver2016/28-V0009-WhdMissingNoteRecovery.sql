:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.FireDrillCredentials.0008'
      AND [SchemaVersion] = 8
)
BEGIN
    RAISERROR(N'V0008 must be installed before WHD missing-TechNote recovery schema version 9.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.WhdMissingNoteRecovery.0009'
)
BEGIN
    INSERT INTO [tb_deploy].[SchemaMigrations]
        ([MigrationId], [SchemaVersion], [ReleaseVersion], [ScriptChecksum])
    VALUES
        (N'SqlServer2016.WhdMissingNoteRecovery.0009', 9, N'0.5.20', NULL);
END;

PRINT N'SqlServer2016.WhdMissingNoteRecovery.0009 installed.';
GO
