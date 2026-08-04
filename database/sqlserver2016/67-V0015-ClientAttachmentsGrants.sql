:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

GRANT EXECUTE ON OBJECT::[tb_app].[GetClientAttachmentStorageConfiguration]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClientInfoAttachments]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[SaveClientInfoAttachment]
    TO [tb_role_client_info_editor];
GRANT EXECUTE ON OBJECT::[tb_app].[SetClientInfoAttachmentEquipmentLink]
    TO [tb_role_client_info_editor];
GRANT EXECUTE ON OBJECT::[tb_app].[SetClientInfoAttachmentArchived]
    TO [tb_role_client_info_editor];
GO

DENY SELECT, INSERT, UPDATE, DELETE
    ON OBJECT::[tb_client].[ClientAttachments] TO [tb_role_user];
DENY SELECT, INSERT, UPDATE, DELETE
    ON OBJECT::[tb_client].[ClientAttachments]
    TO [tb_role_client_info_editor];
GO

PRINT N'Client Attachments permissions installed.';
GO
