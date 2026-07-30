:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

GRANT EXECUTE ON OBJECT::[tb_app].[GetCurrentUserContext]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SearchClients]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClient]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetCurrentUserContext]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[SearchClients]
    TO [tb_role_sync_operator];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClient]
    TO [tb_role_sync_operator];

GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveClient]
    TO [tb_role_admin];

GRANT EXECUTE ON OBJECT::[tb_app].[ReadAuditEvents]
    TO [tb_role_admin];

PRINT N'TechBench stored-procedure-only grants applied.';
GO
