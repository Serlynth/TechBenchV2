:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.FlexibleCredentialFields.0012'
      AND [SchemaVersion] = 12
)
BEGIN
    RAISERROR(N'V0012 must be installed before WHD client contact schema version 13.', 16, 1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'tb_data.Clients', N'WhdContactEmail') IS NULL
        ALTER TABLE [tb_data].[Clients]
            ADD [WhdContactEmail] nvarchar(255) NULL;

    IF COL_LENGTH(N'tb_data.Clients', N'WhdPhone') IS NULL
        ALTER TABLE [tb_data].[Clients]
            ADD [WhdPhone] nvarchar(80) NULL;

    IF COL_LENGTH(N'tb_data.Clients', N'WhdAddress') IS NULL
        ALTER TABLE [tb_data].[Clients]
            ADD [WhdAddress] nvarchar(600) NULL;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_deploy].[SchemaMigrations]
        WHERE [MigrationId] = N'SqlServer2016.WhdClientContactDetails.0013'
    )
        INSERT INTO [tb_deploy].[SchemaMigrations]
            ([MigrationId], [SchemaVersion], [ReleaseVersion], [ScriptChecksum])
        VALUES
            (N'SqlServer2016.WhdClientContactDetails.0013', 13, N'0.5.54', NULL);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'SqlServer2016.WhdClientContactDetails.0013 installed.';
GO
