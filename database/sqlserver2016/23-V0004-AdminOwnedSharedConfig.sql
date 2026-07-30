:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    V0004 makes every organization-wide setting and reference catalog an
    Admin-owned boundary. It is additive and upgrades an installed V0003
    database in place without changing or deleting shared templates or links.
*/

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.SharedReferenceData.0003'
      AND [SchemaVersion] = 3
)
BEGIN
    RAISERROR(
        N'The TechBench V0003 shared-reference schema must be installed before AdminOwnedSharedConfig.0004.',
        16,
        1);
    RETURN;
END;

IF EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.AdminOwnedSharedConfig.0004'
      AND [SchemaVersion] = 4
)
BEGIN
    PRINT N'SqlServer2016.AdminOwnedSharedConfig.0004 is already installed.';
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    /*
        Only per-user external identities remain saveable. Legacy secrets are
        retained temporarily so the desktop migration can delete them after
        transferring them to Windows Credential Manager; they cannot be saved
        again under V0004.
    */
    DELETE FROM [tb_user].[UserSettings]
    WHERE [SettingKey] NOT IN
    (
        N'Whd.Username',
        N'Sage.Username',
        N'Sage.EmployeeId',
        N'Sage.ActivityItemId',
        N'Whd.ApiToken',
        N'Sage.Password',
        N'Sage.DefaultCustomerId'
    );

    INSERT INTO [tb_deploy].[SchemaMigrations]
    (
        [MigrationId],
        [SchemaVersion],
        [ReleaseVersion],
        [ScriptChecksum]
    )
    VALUES
    (
        N'SqlServer2016.AdminOwnedSharedConfig.0004',
        4,
        N'2.0.0-alpha.4',
        NULL
    );

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'SqlServer2016.AdminOwnedSharedConfig.0004 installed.';
GO
