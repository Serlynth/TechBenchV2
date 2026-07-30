:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

GRANT EXECUTE ON OBJECT::[tb_app].[HeartbeatClientSession] TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AcknowledgeClientSessionCommand] TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[CloseClientSession] TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[AdminGetActiveClientSessions] TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminQueueClientSessionCommand] TO [tb_role_admin];

REVOKE EXECUTE ON OBJECT::[tb_app].[HeartbeatClientSession] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AcknowledgeClientSessionCommand] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[CloseClientSession] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminGetActiveClientSessions] FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[AdminQueueClientSessionCommand] FROM [tb_preview_reader];

PRINT N'TechBench V0010 client presence grants applied.';
GO
