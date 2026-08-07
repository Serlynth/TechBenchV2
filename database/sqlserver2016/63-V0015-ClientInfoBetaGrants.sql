:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DATABASE_PRINCIPAL_ID(N'$(AdminGroup)') IS NOT NULL
BEGIN
    IF NOT EXISTS
    (
        SELECT 1 FROM sys.database_role_members
        WHERE [role_principal_id]=DATABASE_PRINCIPAL_ID(N'tb_role_client_info_editor')
          AND [member_principal_id]=DATABASE_PRINCIPAL_ID(N'$(AdminGroup)')
    )
        ALTER ROLE [tb_role_client_info_editor] ADD MEMBER [$(AdminGroup)];

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.database_role_members
        WHERE [role_principal_id]=DATABASE_PRINCIPAL_ID(N'tb_role_client_secret_reader')
          AND [member_principal_id]=DATABASE_PRINCIPAL_ID(N'$(AdminGroup)')
    )
        ALTER ROLE [tb_role_client_secret_reader] ADD MEMBER [$(AdminGroup)];

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.database_role_members
        WHERE [role_principal_id]=DATABASE_PRINCIPAL_ID(N'tb_role_client_secret_editor')
          AND [member_principal_id]=DATABASE_PRINCIPAL_ID(N'$(AdminGroup)')
    )
        ALTER ROLE [tb_role_client_secret_editor] ADD MEMBER [$(AdminGroup)];

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.database_role_members
        WHERE [role_principal_id]=DATABASE_PRINCIPAL_ID(N'tb_role_client_migration_operator')
          AND [member_principal_id]=DATABASE_PRINCIPAL_ID(N'$(AdminGroup)')
    )
        ALTER ROLE [tb_role_client_migration_operator] ADD MEMBER [$(AdminGroup)];
END;
GO

IF DATABASE_PRINCIPAL_ID(N'$(UserGroup)') IS NOT NULL
BEGIN
    IF NOT EXISTS
    (
        SELECT 1 FROM sys.database_role_members
        WHERE [role_principal_id]=DATABASE_PRINCIPAL_ID(N'tb_role_client_secret_reader')
          AND [member_principal_id]=DATABASE_PRINCIPAL_ID(N'$(UserGroup)')
    )
        ALTER ROLE [tb_role_client_secret_reader] ADD MEMBER [$(UserGroup)];

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.database_role_members
        WHERE [role_principal_id]=DATABASE_PRINCIPAL_ID(N'tb_role_client_info_editor')
          AND [member_principal_id]=DATABASE_PRINCIPAL_ID(N'$(UserGroup)')
    )
        ALTER ROLE [tb_role_client_info_editor] ADD MEMBER [$(UserGroup)];

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.database_role_members
        WHERE [role_principal_id]=DATABASE_PRINCIPAL_ID(N'tb_role_client_secret_editor')
          AND [member_principal_id]=DATABASE_PRINCIPAL_ID(N'$(UserGroup)')
    )
        ALTER ROLE [tb_role_client_secret_editor] ADD MEMBER [$(UserGroup)];
END;
GO

GRANT EXECUTE ON OBJECT::[tb_app].[SearchClientInfoClients]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClientInfoSnapshot]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminCreateManualClientInfoClient]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminLinkClientSources]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClientInfoImportBatch]
    TO [tb_role_client_migration_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClientInfoImportBatch]
    TO [tb_role_admin];
GO

GRANT EXECUTE ON OBJECT::[tb_app].[SaveClientInfoProfile]
    TO [tb_role_client_info_editor];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveClientInfoLocation]
    TO [tb_role_client_info_editor];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveClientInfoPerson]
    TO [tb_role_client_info_editor];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveClientInfoResource]
    TO [tb_role_client_info_editor];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveClientInfoResourceField]
    TO [tb_role_client_info_editor];
GRANT EXECUTE ON OBJECT::[tb_app].[DeleteClientInfoResourceField]
    TO [tb_role_client_info_editor];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveClientInfoFact]
    TO [tb_role_client_info_editor];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveClientCredential]
    TO [tb_role_client_info_editor];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveClientCredential]
    TO [tb_role_client_secret_editor];
GRANT EXECUTE ON OBJECT::[tb_app].[SetClientCredentialSecret]
    TO [tb_role_client_secret_editor];
GRANT EXECUTE ON OBJECT::[tb_app].[RevealClientCredentialSecret]
    TO [tb_role_client_secret_reader];
GO

GRANT EXECUTE ON OBJECT::[tb_app].[BeginClientInfoImport]
    TO [tb_role_client_migration_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[StageClientInfoRecord]
    TO [tb_role_client_migration_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[StageClientInfoSecret]
    TO [tb_role_client_migration_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[ValidateClientInfoImport]
    TO [tb_role_client_migration_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[CompareClientInfoImportToFireDrill]
    TO [tb_role_client_migration_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[ResolveClientInfoImportIssue]
    TO [tb_role_client_migration_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[AcceptClientInfoImportUnverified]
    TO [tb_role_client_migration_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[DiscardClientInfoImport]
    TO [tb_role_client_migration_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[ApproveClientInfoImport]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[PromoteClientInfoImport]
    TO [tb_role_admin];
GO

DENY SELECT, INSERT, UPDATE, DELETE
    ON SCHEMA::[tb_client] TO [tb_role_user];
DENY SELECT, INSERT, UPDATE, DELETE
    ON SCHEMA::[tb_import] TO [tb_role_user];
DENY SELECT, INSERT, UPDATE, DELETE
    ON SCHEMA::[tb_client] TO [tb_role_client_info_editor];
DENY SELECT, INSERT, UPDATE, DELETE
    ON SCHEMA::[tb_import] TO [tb_role_client_migration_operator];
GO

REVOKE EXECUTE ON OBJECT::[tb_app].[RevealClientCredentialSecret]
    FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[SetClientCredentialSecret]
    FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[PromoteClientInfoImport]
    FROM [tb_preview_reader];
GO

PRINT N'Client Info beta permissions installed.';
GO
