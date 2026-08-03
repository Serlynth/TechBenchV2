:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;

IF NOT EXISTS
(
    SELECT 1 FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId]=N'SqlServer2016.ClientAttachments.0015'
      AND [SchemaVersion]=15
)
    THROW 52650,N'Client Attachments migration record is missing.',1;

IF (SELECT MAX([SchemaVersion]) FROM [tb_deploy].[SchemaMigrations])<>15
    THROW 52651,N'Client Attachments must remain compatible with schema version 15.',1;

DECLARE @MissingObjects TABLE([ObjectName] nvarchar(256) NOT NULL);
INSERT INTO @MissingObjects([ObjectName])
SELECT required.[ObjectName]
FROM
(
    VALUES
        (N'tb_client.ClientAttachments',N'U'),
        (N'tb_app.GetClientAttachmentStorageConfiguration',N'P'),
        (N'tb_app.GetClientInfoAttachments',N'P'),
        (N'tb_app.SaveClientInfoAttachment',N'P'),
        (N'tb_app.SetClientInfoAttachmentArchived',N'P')
) required([ObjectName],[ObjectType])
WHERE OBJECT_ID(required.[ObjectName],required.[ObjectType]) IS NULL;

IF EXISTS(SELECT 1 FROM @MissingObjects)
BEGIN
    SELECT [ObjectName] FROM @MissingObjects ORDER BY [ObjectName];
    THROW 52652,N'One or more Client Attachments objects are missing.',1;
END;

IF EXISTS
(
    SELECT 1 FROM sys.database_permissions permission
    WHERE permission.[grantee_principal_id]=DATABASE_PRINCIPAL_ID(N'tb_role_user')
      AND permission.[major_id]=OBJECT_ID(N'tb_client.ClientAttachments')
      AND permission.[state] IN (N'G',N'W')
)
    THROW 52653,N'Ordinary users received direct attachment-table permission.',1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.database_permissions permission
    WHERE permission.[grantee_principal_id]=DATABASE_PRINCIPAL_ID(N'tb_role_user')
      AND permission.[major_id]=OBJECT_ID(N'tb_app.GetClientInfoAttachments')
      AND permission.[permission_name]=N'EXECUTE'
      AND permission.[state] IN (N'G',N'W')
)
    THROW 52654,N'Ordinary users cannot list attachment metadata.',1;

DECLARE @SaveDefinition nvarchar(max)=
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveClientInfoAttachment'));
IF @SaveDefinition NOT LIKE N'%@ContentSha256 binary(32)%'
   OR @SaveDefinition NOT LIKE N'%@ExpectedRowVersion binary(8)%'
   OR @SaveDefinition NOT LIKE N'%WriteAuditEvent%'
    THROW 52655,N'Attachment writes are missing integrity, concurrency, or audit controls.',1;

PRINT N'PASS: Client Attachments schema-15 compatibility and security verified.';
GO
