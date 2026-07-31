:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.EquipmentBoard.0014'
      AND [SchemaVersion] = 14
)
BEGIN
    RAISERROR(N'V0014 must be installed before equipment AnyDesk schema version 15.', 16, 1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'tb_inventory.Equipment', N'AnyDeskNumber') IS NULL
        ALTER TABLE [tb_inventory].[Equipment]
            ADD [AnyDeskNumber] nvarchar(80) NULL;

    IF COL_LENGTH(N'tb_inventory.Equipment', N'AnyDeskPasswordEncrypted') IS NULL
        ALTER TABLE [tb_inventory].[Equipment]
            ADD [AnyDeskPasswordEncrypted] varbinary(max) NULL;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_deploy].[SchemaMigrations]
        WHERE [MigrationId] = N'SqlServer2016.EquipmentAnyDesk.0015'
    )
        INSERT INTO [tb_deploy].[SchemaMigrations]
            ([MigrationId], [SchemaVersion], [ReleaseVersion], [ScriptChecksum])
        VALUES
            (N'SqlServer2016.EquipmentAnyDesk.0015', 15, N'0.5.76', NULL);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'SqlServer2016.EquipmentAnyDesk.0015 installed.';
GO
