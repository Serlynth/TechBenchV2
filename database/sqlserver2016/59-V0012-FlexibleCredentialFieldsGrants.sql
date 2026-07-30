:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

GRANT EXECUTE ON OBJECT::[tb_app].[SearchFireDrillCredentials] TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[RevealFireDrillCredential] TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_service].[ApplyFireDrillCredentialSnapshot] TO [tb_role_sync_service];

REVOKE EXECUTE ON OBJECT::[tb_app].[SearchFireDrillCredentials] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[RevealFireDrillCredential] FROM [tb_preview_reader];

PRINT N'TechBench V0012 flexible Credentials grants applied.';
GO
