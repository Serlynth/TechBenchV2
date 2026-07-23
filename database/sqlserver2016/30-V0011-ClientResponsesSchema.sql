:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.ClientPresence.0010'
      AND [SchemaVersion] = 10
)
BEGIN
    RAISERROR(N'V0010 must be installed before client response schema version 11.', 16, 1);
    RETURN;
END;

IF COL_LENGTH(N'tb_security.ClientSessionCommands', N'ResponseMessage') IS NULL
BEGIN
    ALTER TABLE [tb_security].[ClientSessionCommands]
        ADD [ResponseMessage] nvarchar(500) NULL;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE [parent_object_id] =
        OBJECT_ID(N'tb_security.ClientSessionCommands', N'U')
      AND [name] = N'CK_ClientSessionCommands_AcknowledgementResult'
)
BEGIN
    ALTER TABLE [tb_security].[ClientSessionCommands]
        DROP CONSTRAINT [CK_ClientSessionCommands_AcknowledgementResult];
END;

ALTER TABLE [tb_security].[ClientSessionCommands] WITH CHECK
    ADD CONSTRAINT [CK_ClientSessionCommands_AcknowledgementResult]
        CHECK
        (
            [AcknowledgementResult] IS NULL
            OR [AcknowledgementResult] IN
                (
                    N'Displayed', N'Acknowledged', N'Dismissed',
                    N'SignedOut', N'Ignored', N'Failed', N'SaveFailed'
                )
        );

ALTER TABLE [tb_security].[ClientSessionCommands]
    CHECK CONSTRAINT [CK_ClientSessionCommands_AcknowledgementResult];

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.ClientResponses.0011'
)
BEGIN
    INSERT INTO [tb_deploy].[SchemaMigrations]
        ([MigrationId], [SchemaVersion], [ReleaseVersion], [ScriptChecksum])
    VALUES
        (N'SqlServer2016.ClientResponses.0011', 11, N'0.5.24', NULL);
END;

PRINT N'SqlServer2016.ClientResponses.0011 installed.';
GO
