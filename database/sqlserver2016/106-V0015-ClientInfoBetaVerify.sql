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
        (N'tb_app.AdminCreateManualClientInfoClient',N'P'),
        (N'tb_app.GetClientInfoSnapshot',N'P'),
        (N'tb_app.SaveClientInfoProfile',N'P'),
        (N'tb_app.SaveClientInfoLocation',N'P'),
        (N'tb_app.SaveClientInfoPerson',N'P'),
        (N'tb_app.SaveClientInfoResource',N'P'),
        (N'tb_app.SaveClientInfoResourceField',N'P'),
        (N'tb_app.DeleteClientInfoResourceField',N'P'),
        (N'tb_app.SaveClientInfoFact',N'P'),
        (N'tb_app.SaveClientCredential',N'P'),
        (N'tb_security.EncryptClientSecretValue',N'P'),
        (N'tb_app.SetClientCredentialSecret',N'P'),
        (N'tb_app.RevealClientCredentialSecret',N'P'),
        (N'tb_app.BeginClientInfoImport',N'P'),
        (N'tb_app.StageClientInfoRecord',N'P'),
        (N'tb_app.StageClientInfoSecret',N'P'),
        (N'tb_app.ValidateClientInfoImport',N'P'),
        (N'tb_app.CompareClientInfoImportToFireDrill',N'P'),
        (N'tb_security.GetClientInfoImportBatchResult',N'P'),
        (N'tb_app.GetClientInfoImportBatch',N'P'),
        (N'tb_app.ResolveClientInfoImportIssue',N'P'),
        (N'tb_app.AcceptClientInfoImportUnverified',N'P'),
        (N'tb_app.DiscardClientInfoImport',N'P'),
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

IF COL_LENGTH(N'tb_client.People', N'AdUsername') IS NULL
   OR COL_LENGTH(N'tb_client.People', N'HasMicrosoft365') IS NULL
   OR COL_LENGTH(N'tb_client.People', N'Microsoft365License') IS NULL
   OR COL_LENGTH(N'tb_client.People', N'PcName') IS NULL
    THROW 52508,N'One or more Client Info user identity columns are missing.',1;

IF COL_LENGTH(N'tb_client.ClientProfiles', N'ClientFolderPath') IS NULL
   OR COL_LENGTH(N'tb_client.ClientProfiles', N'LegacyClientInfoSheetPath') IS NULL
    THROW 52509,N'One or more Client Info server-link columns are missing.',1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id]=OBJECT_ID(N'tb_import.ClientInfoBatches')
      AND [name]=N'UX_ClientInfoBatches_ActiveIdempotency'
      AND [is_unique]=1
      AND [has_filter]=1
      AND [filter_definition] LIKE N'%Rejected%'
      AND [filter_definition] LIKE N'%Superseded%'
      AND [filter_definition] LIKE N'%Failed%'
)
    THROW 52514,N'Closed Client Info workbook reviews still block an identical reimport.',1;

DECLARE @RecordTypeConstraint nvarchar(max)=
    (SELECT [definition]
     FROM sys.check_constraints
     WHERE [parent_object_id]=OBJECT_ID(N'tb_import.ClientInfoRecords')
       AND [name]=N'CK_ClientInfoRecords_Type');
IF @RecordTypeConstraint IS NULL
   OR CHARINDEX(N'ResourceField', @RecordTypeConstraint)=0
    THROW 52510,N'Client Info staging does not allow resource-field records.',1;

DECLARE @ResourceFieldTypeConstraint nvarchar(max)=
    (SELECT [definition]
     FROM sys.check_constraints
     WHERE [parent_object_id]=OBJECT_ID(N'tb_client.ResourceFields')
       AND [name]=N'CK_ClientResourceFields_Type');
DECLARE @FactTypeConstraint nvarchar(max)=
    (SELECT [definition]
     FROM sys.check_constraints
     WHERE [parent_object_id]=OBJECT_ID(N'tb_client.ClientFacts')
       AND [name]=N'CK_ClientFacts_Type');
IF @ResourceFieldTypeConstraint IS NULL
   OR CHARINDEX(N'Phone', @ResourceFieldTypeConstraint)=0
   OR CHARINDEX(N'Email', @ResourceFieldTypeConstraint)=0
   OR @FactTypeConstraint IS NULL
   OR CHARINDEX(N'Phone', @FactTypeConstraint)=0
   OR CHARINDEX(N'Email', @FactTypeConstraint)=0
    THROW 52513,N'Client Info does not support phone and email field types.',1;

IF CERT_ID(N'tb_ClientSecretCertificate') IS NULL
    THROW 52503,N'The canonical client-secret certificate is missing.',1;

IF NOT EXISTS
    (SELECT 1 FROM sys.symmetric_keys WHERE [name]=N'tb_ClientSecretKey')
    THROW 52504,N'The canonical client-secret key is missing.',1;

DECLARE @EncryptClientSecretDefinition nvarchar(max)=
    OBJECT_DEFINITION(OBJECT_ID(N'tb_security.EncryptClientSecretValue'));
DECLARE @SetClientSecretDefinition nvarchar(max)=
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SetClientCredentialSecret'));
DECLARE @StageClientSecretDefinition nvarchar(max)=
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.StageClientInfoSecret'));
IF @EncryptClientSecretDefinition IS NULL
   OR CHARINDEX(N'WITH EXECUTE AS OWNER',@EncryptClientSecretDefinition)=0
   OR CHARINDEX(N'OPEN SYMMETRIC KEY [tb_ClientSecretKey]',@EncryptClientSecretDefinition)=0
   OR CHARINDEX(N'[tb_security].[EncryptClientSecretValue]',@SetClientSecretDefinition)=0
   OR CHARINDEX(N'[tb_security].[EncryptClientSecretValue]',@StageClientSecretDefinition)=0
   OR CHARINDEX(N'WITH EXECUTE AS OWNER',@SetClientSecretDefinition)>0
   OR CHARINDEX(N'WITH EXECUTE AS OWNER',@StageClientSecretDefinition)>0
    THROW 52511,N'Client Info secret writes do not use the protected encryption boundary.',1;

DECLARE @CompareClientInfoDefinition nvarchar(max)=
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompareClientInfoImportToFireDrill'));
DECLARE @GetClientInfoBatchDefinition nvarchar(max)=
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetClientInfoImportBatch'));
IF @CompareClientInfoDefinition IS NULL
   OR CHARINDEX(N'WITH EXECUTE AS OWNER',@CompareClientInfoDefinition)=0
   OR CHARINDEX(N'[tb_security].[GetClientInfoImportBatchResult]',@CompareClientInfoDefinition)=0
   OR CHARINDEX(N'[tb_app].[GetClientInfoImportBatch]',@CompareClientInfoDefinition)>0
   OR CHARINDEX(N'[tb_security].[GetCurrentAccess]',@GetClientInfoBatchDefinition)=0
   OR CHARINDEX(N'[tb_security].[GetClientInfoImportBatchResult]',@GetClientInfoBatchDefinition)=0
    THROW 52512,N'Client Info import results do not preserve the caller security context.',1;

DECLARE @Capabilities nvarchar(max)=
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetRepositoryCapabilities'));
IF CHARINDEX(N'[ClientInfoBetaAvailable]', @Capabilities) = 0
   OR CHARINDEX(N'[ManualClientInfoCreationAvailable]', @Capabilities) = 0
   OR CHARINDEX(N'CONVERT(int, 15) AS [SchemaVersion]', @Capabilities) = 0
    THROW 52505,N'Repository capabilities do not expose the schema-15-compatible Client Info beta.',1;

DECLARE @ManualClientCreation nvarchar(max)=
    OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminCreateManualClientInfoClient'));
IF @ManualClientCreation IS NULL
   OR CHARINDEX(N'IS_ROLEMEMBER(N''tb_role_admin'')', @ManualClientCreation) = 0
   OR CHARINDEX(N'N''Manual''', @ManualClientCreation) = 0
   OR CHARINDEX(N'[IsLive]', @ManualClientCreation) = 0
   OR CHARINDEX(N'N''Complete''', @ManualClientCreation) = 0
   OR CHARINDEX(N'[tb_security].[WriteAuditEvent]', @ManualClientCreation) = 0
    THROW 52515,N'Manual client creation is not admin-only, live, complete, and audited.',1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission
    WHERE permission.[grantee_principal_id] =
            DATABASE_PRINCIPAL_ID(N'tb_role_admin')
      AND permission.[major_id] =
            OBJECT_ID(N'tb_app.AdminCreateManualClientInfoClient')
      AND permission.[permission_name] = N'EXECUTE'
      AND permission.[state] IN (N'G',N'W')
)
    THROW 52516,N'The Admin role cannot create live manual clients.',1;

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
