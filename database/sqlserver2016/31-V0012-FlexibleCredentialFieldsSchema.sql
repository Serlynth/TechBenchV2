:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.ClientResponses.0011'
      AND [SchemaVersion] = 11
)
BEGIN
    RAISERROR(N'V0011 must be installed before flexible Credentials schema version 12.', 16, 1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'tb_data.FireDrillCredentialFields', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_data].[FireDrillCredentialFields]
        (
            [CredentialId] bigint NOT NULL,
            [FieldKey] nvarchar(200) NOT NULL,
            [FieldLabel] nvarchar(200) NOT NULL,
            [SortOrder] int NOT NULL,
            [ValueEncrypted] varbinary(max) NULL,
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_FireDrillCredentialFields]
                PRIMARY KEY CLUSTERED ([CredentialId], [FieldKey]),
            CONSTRAINT [UQ_FireDrillCredentialFields_Order]
                UNIQUE ([CredentialId], [SortOrder]),
            CONSTRAINT [FK_FireDrillCredentialFields_Credential]
                FOREIGN KEY ([CredentialId])
                REFERENCES [tb_data].[FireDrillCredentials]([CredentialId])
                ON DELETE CASCADE,
            CONSTRAINT [CK_FireDrillCredentialFields_Key]
                CHECK (LEN(LTRIM(RTRIM([FieldKey]))) > 0),
            CONSTRAINT [CK_FireDrillCredentialFields_Label]
                CHECK (LEN(LTRIM(RTRIM([FieldLabel]))) > 0),
            CONSTRAINT [CK_FireDrillCredentialFields_Order]
                CHECK ([SortOrder] > 0)
        );
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_deploy].[SchemaMigrations]
        WHERE [MigrationId] = N'SqlServer2016.FlexibleCredentialFields.0012'
    )
        INSERT INTO [tb_deploy].[SchemaMigrations]
            ([MigrationId], [SchemaVersion], [ReleaseVersion], [ScriptChecksum])
        VALUES
            (N'SqlServer2016.FlexibleCredentialFields.0012', 12, N'0.5.33', NULL);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'SqlServer2016.FlexibleCredentialFields.0012 installed.';
GO
