:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId]=N'SqlServer2016.ClientInfoBeta.0015'
      AND [SchemaVersion]=15
)
    THROW 52500,N'Client Info beta migration record is missing.',1;

IF (SELECT MAX([SchemaVersion]) FROM [tb_deploy].[SchemaMigrations])<>15
    THROW 52501,N'Client Info beta must remain compatible with stable schema version 15.',1;

DECLARE @MissingObjects TABLE([ObjectName] nvarchar(256) NOT NULL);
INSERT INTO @MissingObjects([ObjectName])
SELECT required.[ObjectName]
FROM
(
    VALUES
        (N'tb_client.ClientProfiles',N'U'),
        (N'tb_client.Locations',N'U'),
        (N'tb_client.People',N'U'),
        (N'tb_client.Resources',N'U'),
        (N'tb_client.ResourceFields',N'U'),
        (N'tb_client.Credentials',N'U'),
        (N'tb_client.CredentialSecrets',N'U'),
        (N'tb_client.ClientFacts',N'U'),
        (N'tb_client.SourceDocuments',N'U'),
        (N'tb_client.RecordProvenance',N'U'),
        (N'tb_import.ClientInfoBatches',N'U'),
        (N'tb_import.ClientInfoRecords',N'U'),
        (N'tb_import.ClientInfoSecrets',N'U'),
        (N'tb_import.ClientInfoIssues',N'U'),
        (N'tb_import.ClientInfoPromotionMap',N'U'),
        (N'tb_ops.ClientInfoCutovers',N'U'),
        (N'tb_app.SearchClientInfoClients',N'P'),
        (N'tb_app.GetClientInfoSnapshot',N'P'),
        (N'tb_app.SaveClientInfoProfile',N'P'),
        (N'tb_app.SaveClientInfoLocation',N'P'),
        (N'tb_app.SaveClientInfoPerson',N'P'),
        (N'tb_app.SaveClientInfoResource',N'P'),
        (N'tb_app.SaveClientInfoFact',N'P'),
        (N'tb_app.SaveClientCredential',N'P'),
        (N'tb_app.SetClientCredentialSecret',N'P'),
        (N'tb_app.RevealClientCredentialSecret',N'P'),
        (N'tb_app.BeginClientInfoImport',N'P'),
        (N'tb_app.StageClientInfoRecord',N'P'),
        (N'tb_app.StageClientInfoSecret',N'P'),
        (N'tb_app.ValidateClientInfoImport',N'P'),
        (N'tb_app.CompareClientInfoImportToFireDrill',N'P'),
        (N'tb_app.GetClientInfoImportBatch',N'P'),
        (N'tb_app.ResolveClientInfoImportIssue',N'P'),
        (N'tb_app.ApproveClientInfoImport',N'P'),
        (N'tb_app.PromoteClientInfoImport',N'P'),
        (N'tb_client.ReparentClientGraph',N'P')
) required([ObjectName],[ObjectType])
WHERE OBJECT_ID(required.[ObjectName],required.[ObjectType]) IS NULL;

IF EXISTS(SELECT 1 FROM @MissingObjects)
BEGIN
    SELECT [ObjectName] FROM @MissingObjects ORDER BY [ObjectName];
    THROW 52502,N'One or more Client Info beta objects are missing.',1;
END;

IF CERT_ID(N'tb_ClientSecretCertificate') IS NULL
    THROW 52503,N'The canonical client-secret certificate is missing.',1;

IF NOT EXISTS
    (SELECT 1 FROM sys.symmetric_keys WHERE [name]=N'tb_ClientSecretKey')
    THROW 52504,N'The canonical client-secret key is missing.',1;

DECLARE @Capabilities nvarchar(max)=
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities'));
IF CHARINDEX(N'[ClientInfoBetaAvailable]', @Capabilities) = 0
   OR CHARINDEX(N'CONVERT(int, 15) AS [SchemaVersion]', @Capabilities) = 0
    THROW 52505,N'Repository capabilities do not expose the schema-15-compatible Client Info beta.',1;

DECLARE @MergeManual nvarchar(max)=
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminMergeClients'));
DECLARE @MergeAuto nvarchar(max)=
    OBJECT_DEFINITION(OBJECT_ID(N'tb_service.ApplyAutomaticClientMatch'));
DECLARE @MergeFamily nvarchar(max)=
    OBJECT_DEFINITION(OBJECT_ID(N'tb_service.ApplyAutomaticWhdFamilyMember'));
IF @MergeManual NOT LIKE N'%ReparentClientGraph%'
   OR @MergeAuto NOT LIKE N'%ReparentClientGraph%'
   OR @MergeFamily NOT LIKE N'%ReparentClientGraph%'
    THROW 52506,N'Every client merge path must reparent the canonical Client Info graph.',1;

IF EXISTS
(
    SELECT 1
    FROM sys.database_permissions permission
    WHERE permission.[grantee_principal_id]=DATABASE_PRINCIPAL_ID(N'tb_role_user')
      AND permission.[major_id] IN
        (OBJECT_ID(N'tb_client.CredentialSecrets'),
         OBJECT_ID(N'tb_import.ClientInfoSecrets'))
      AND permission.[state] IN (N'G',N'W')
)
    THROW 52507,N'Ordinary users received direct secret-table permission.',1;

PRINT N'PASS: Client Info beta schema-15 compatibility and security verified.';
GO
