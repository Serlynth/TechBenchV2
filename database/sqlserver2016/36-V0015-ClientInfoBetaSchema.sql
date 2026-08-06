:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Client Info beta is deliberately an additive schema-15 extension.
    Stable 0.6.1 clients reject schema versions above 15, so this migration
    records its own ID while leaving the database's maximum schema version at
    15. Stable clients ignore these objects and continue to use FireDrill.
*/
IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.EquipmentAnyDesk.0015'
      AND [SchemaVersion] = 15
)
BEGIN
    RAISERROR(N'Schema version 15 must be installed before the Client Info beta extension.', 16, 1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    IF SCHEMA_ID(N'tb_client') IS NULL
        EXEC(N'CREATE SCHEMA [tb_client] AUTHORIZATION [dbo];');

    IF SCHEMA_ID(N'tb_import') IS NULL
        EXEC(N'CREATE SCHEMA [tb_import] AUTHORIZATION [dbo];');

    IF DATABASE_PRINCIPAL_ID(N'tb_role_client_info_editor') IS NULL
        CREATE ROLE [tb_role_client_info_editor] AUTHORIZATION [dbo];

    IF DATABASE_PRINCIPAL_ID(N'tb_role_client_secret_reader') IS NULL
        CREATE ROLE [tb_role_client_secret_reader] AUTHORIZATION [dbo];

    IF DATABASE_PRINCIPAL_ID(N'tb_role_client_secret_editor') IS NULL
        CREATE ROLE [tb_role_client_secret_editor] AUTHORIZATION [dbo];

    IF DATABASE_PRINCIPAL_ID(N'tb_role_client_migration_operator') IS NULL
        CREATE ROLE [tb_role_client_migration_operator] AUTHORIZATION [dbo];

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.certificates
        WHERE [name] = N'tb_ClientSecretCertificate'
    )
        CREATE CERTIFICATE [tb_ClientSecretCertificate]
            WITH SUBJECT = N'TechBench canonical client secret encryption';

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.symmetric_keys
        WHERE [name] = N'tb_ClientSecretKey'
    )
        CREATE SYMMETRIC KEY [tb_ClientSecretKey]
            WITH ALGORITHM = AES_256
            ENCRYPTION BY CERTIFICATE [tb_ClientSecretCertificate];

    IF COL_LENGTH(N'tb_inventory.Equipment', N'ClientInfoLocalKey') IS NULL
        EXEC sys.sp_executesql N'
            ALTER TABLE [tb_inventory].[Equipment]
            ADD [ClientInfoLocalKey] nvarchar(120) NULL;';

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE [object_id]=OBJECT_ID(N'tb_inventory.Equipment')
          AND [name]=N'UX_Equipment_ClientInfoLocalKey'
    )
        EXEC sys.sp_executesql N'
            CREATE UNIQUE INDEX [UX_Equipment_ClientInfoLocalKey]
            ON [tb_inventory].[Equipment]([ClientId],[ClientInfoLocalKey])
            WHERE [ClientId] IS NOT NULL
              AND [ClientInfoLocalKey] IS NOT NULL;';

    IF OBJECT_ID(N'tb_client.ClientProfiles', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_client].[ClientProfiles]
        (
            [ClientId] int NOT NULL,
            [Summary] nvarchar(2000) NULL,
            [ClientFolderPath] nvarchar(2048) NULL,
            [LegacyClientInfoSheetPath] nvarchar(2048) NULL,
            [ReviewStatus] nvarchar(24) NOT NULL
                CONSTRAINT [DF_ClientProfiles_ReviewStatus] DEFAULT (N'Unverified'),
            [IsLive] bit NOT NULL
                CONSTRAINT [DF_ClientProfiles_IsLive] DEFAULT (0),
            [LastVerifiedAtUtc] datetime2(3) NULL,
            [LastVerifiedByWindowsSid] varbinary(85) NULL,
            [CreatedByWindowsSid] varbinary(85) NOT NULL,
            [UpdatedByWindowsSid] varbinary(85) NOT NULL,
            [CreatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientProfiles_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientProfiles_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ClientProfiles] PRIMARY KEY CLUSTERED ([ClientId]),
            CONSTRAINT [FK_ClientProfiles_Client]
                FOREIGN KEY ([ClientId]) REFERENCES [tb_data].[Clients]([Id]),
            CONSTRAINT [FK_ClientProfiles_VerifiedBy]
                FOREIGN KEY ([LastVerifiedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_ClientProfiles_CreatedBy]
                FOREIGN KEY ([CreatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_ClientProfiles_UpdatedBy]
                FOREIGN KEY ([UpdatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_ClientProfiles_ReviewStatus]
                CHECK ([ReviewStatus] IN
                    (N'Unverified', N'Verified', N'AcceptedUnverified', N'NeedsReview'))
        );
    END;

    IF COL_LENGTH(N'tb_client.ClientProfiles', N'ClientFolderPath') IS NULL
        ALTER TABLE [tb_client].[ClientProfiles]
            ADD [ClientFolderPath] nvarchar(2048) NULL;

    IF COL_LENGTH(N'tb_client.ClientProfiles', N'LegacyClientInfoSheetPath') IS NULL
        ALTER TABLE [tb_client].[ClientProfiles]
            ADD [LegacyClientInfoSheetPath] nvarchar(2048) NULL;

    IF OBJECT_ID(N'tb_client.Locations', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_client].[Locations]
        (
            [LocationId] bigint IDENTITY(1,1) NOT NULL,
            [ClientId] int NOT NULL,
            [LocalKey] nvarchar(120) NULL,
            [Name] nvarchar(240) NOT NULL,
            [LocationType] nvarchar(80) NULL,
            [Address1] nvarchar(240) NULL,
            [Address2] nvarchar(240) NULL,
            [City] nvarchar(120) NULL,
            [StateProvince] nvarchar(80) NULL,
            [PostalCode] nvarchar(40) NULL,
            [MainPhone] nvarchar(80) NULL,
            [TimeZoneId] nvarchar(120) NULL,
            [IsPrimary] bit NOT NULL
                CONSTRAINT [DF_ClientLocations_IsPrimary] DEFAULT (0),
            [ReviewStatus] nvarchar(24) NOT NULL
                CONSTRAINT [DF_ClientLocations_ReviewStatus] DEFAULT (N'Unverified'),
            [IsActive] bit NOT NULL
                CONSTRAINT [DF_ClientLocations_IsActive] DEFAULT (1),
            [LastVerifiedAtUtc] datetime2(3) NULL,
            [CreatedByWindowsSid] varbinary(85) NOT NULL,
            [UpdatedByWindowsSid] varbinary(85) NOT NULL,
            [CreatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientLocations_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientLocations_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ClientLocations] PRIMARY KEY CLUSTERED ([LocationId]),
            CONSTRAINT [FK_ClientLocations_Client]
                FOREIGN KEY ([ClientId]) REFERENCES [tb_data].[Clients]([Id]),
            CONSTRAINT [FK_ClientLocations_CreatedBy]
                FOREIGN KEY ([CreatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_ClientLocations_UpdatedBy]
                FOREIGN KEY ([UpdatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_ClientLocations_Name]
                CHECK (LEN(LTRIM(RTRIM([Name]))) > 0),
            CONSTRAINT [CK_ClientLocations_ReviewStatus]
                CHECK ([ReviewStatus] IN
                    (N'Unverified', N'Verified', N'AcceptedUnverified', N'NeedsReview'))
        );

        CREATE INDEX [IX_ClientLocations_Client]
            ON [tb_client].[Locations]
                ([ClientId], [IsActive], [IsPrimary] DESC, [Name], [LocationId]);

        CREATE UNIQUE INDEX [UX_ClientLocations_LocalKey]
            ON [tb_client].[Locations]([ClientId], [LocalKey])
            WHERE [LocalKey] IS NOT NULL;
    END;

    IF OBJECT_ID(N'tb_client.People', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_client].[People]
        (
            [PersonId] bigint IDENTITY(1,1) NOT NULL,
            [ClientId] int NOT NULL,
            [LocationId] bigint NULL,
            [LocalKey] nvarchar(120) NULL,
            [DisplayName] nvarchar(240) NOT NULL,
            [RoleDepartment] nvarchar(240) NULL,
            [AdUsername] nvarchar(256) NULL,
            [Email] nvarchar(320) NULL,
            [HasMicrosoft365] bit NOT NULL
                CONSTRAINT [DF_ClientPeople_HasMicrosoft365] DEFAULT (0),
            [Microsoft365License] nvarchar(240) NULL,
            [PcName] nvarchar(240) NULL,
            [Phone] nvarchar(80) NULL,
            [MobilePhone] nvarchar(80) NULL,
            [ContactType] nvarchar(80) NULL,
            [IsPrimary] bit NOT NULL
                CONSTRAINT [DF_ClientPeople_IsPrimary] DEFAULT (0),
            [ReviewStatus] nvarchar(24) NOT NULL
                CONSTRAINT [DF_ClientPeople_ReviewStatus] DEFAULT (N'Unverified'),
            [IsActive] bit NOT NULL
                CONSTRAINT [DF_ClientPeople_IsActive] DEFAULT (1),
            [LastVerifiedAtUtc] datetime2(3) NULL,
            [CreatedByWindowsSid] varbinary(85) NOT NULL,
            [UpdatedByWindowsSid] varbinary(85) NOT NULL,
            [CreatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientPeople_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientPeople_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ClientPeople] PRIMARY KEY CLUSTERED ([PersonId]),
            CONSTRAINT [FK_ClientPeople_Client]
                FOREIGN KEY ([ClientId]) REFERENCES [tb_data].[Clients]([Id]),
            CONSTRAINT [FK_ClientPeople_Location]
                FOREIGN KEY ([LocationId]) REFERENCES [tb_client].[Locations]([LocationId]),
            CONSTRAINT [FK_ClientPeople_CreatedBy]
                FOREIGN KEY ([CreatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_ClientPeople_UpdatedBy]
                FOREIGN KEY ([UpdatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_ClientPeople_Name]
                CHECK (LEN(LTRIM(RTRIM([DisplayName]))) > 0),
            CONSTRAINT [CK_ClientPeople_ReviewStatus]
                CHECK ([ReviewStatus] IN
                    (N'Unverified', N'Verified', N'AcceptedUnverified', N'NeedsReview'))
        );

        CREATE INDEX [IX_ClientPeople_Client]
            ON [tb_client].[People]
                ([ClientId], [IsActive], [IsPrimary] DESC, [DisplayName], [PersonId]);

        CREATE UNIQUE INDEX [UX_ClientPeople_LocalKey]
            ON [tb_client].[People]([ClientId], [LocalKey])
            WHERE [LocalKey] IS NOT NULL;
    END;

    IF COL_LENGTH(N'tb_client.People', N'AdUsername') IS NULL
        ALTER TABLE [tb_client].[People]
            ADD [AdUsername] nvarchar(256) NULL;

    IF COL_LENGTH(N'tb_client.People', N'HasMicrosoft365') IS NULL
        ALTER TABLE [tb_client].[People]
            ADD [HasMicrosoft365] bit NOT NULL
                CONSTRAINT [DF_ClientPeople_HasMicrosoft365] DEFAULT (0);

    IF COL_LENGTH(N'tb_client.People', N'Microsoft365License') IS NULL
        ALTER TABLE [tb_client].[People]
            ADD [Microsoft365License] nvarchar(240) NULL;

    IF COL_LENGTH(N'tb_client.People', N'PcName') IS NULL
        ALTER TABLE [tb_client].[People]
            ADD [PcName] nvarchar(240) NULL;

    IF OBJECT_ID(N'tb_client.Resources', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_client].[Resources]
        (
            [ResourceId] bigint IDENTITY(1,1) NOT NULL,
            [ClientId] int NOT NULL,
            [LocationId] bigint NULL,
            [ParentResourceId] bigint NULL,
            [EquipmentId] bigint NULL,
            [LocalKey] nvarchar(120) NULL,
            [ResourceType] nvarchar(80) NOT NULL,
            [Name] nvarchar(240) NOT NULL,
            [Provider] nvarchar(160) NULL,
            [AddressOrUrl] nvarchar(1000) NULL,
            [Status] nvarchar(80) NULL,
            [Notes] nvarchar(max) NULL,
            [ReviewStatus] nvarchar(24) NOT NULL
                CONSTRAINT [DF_ClientResources_ReviewStatus] DEFAULT (N'Unverified'),
            [IsActive] bit NOT NULL
                CONSTRAINT [DF_ClientResources_IsActive] DEFAULT (1),
            [LastVerifiedAtUtc] datetime2(3) NULL,
            [CreatedByWindowsSid] varbinary(85) NOT NULL,
            [UpdatedByWindowsSid] varbinary(85) NOT NULL,
            [CreatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientResources_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientResources_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ClientResources] PRIMARY KEY CLUSTERED ([ResourceId]),
            CONSTRAINT [FK_ClientResources_Client]
                FOREIGN KEY ([ClientId]) REFERENCES [tb_data].[Clients]([Id]),
            CONSTRAINT [FK_ClientResources_Location]
                FOREIGN KEY ([LocationId]) REFERENCES [tb_client].[Locations]([LocationId]),
            CONSTRAINT [FK_ClientResources_Parent]
                FOREIGN KEY ([ParentResourceId]) REFERENCES [tb_client].[Resources]([ResourceId]),
            CONSTRAINT [FK_ClientResources_Equipment]
                FOREIGN KEY ([EquipmentId]) REFERENCES [tb_inventory].[Equipment]([EquipmentId]),
            CONSTRAINT [FK_ClientResources_CreatedBy]
                FOREIGN KEY ([CreatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_ClientResources_UpdatedBy]
                FOREIGN KEY ([UpdatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_ClientResources_Type]
                CHECK (LEN(LTRIM(RTRIM([ResourceType]))) > 0),
            CONSTRAINT [CK_ClientResources_Name]
                CHECK (LEN(LTRIM(RTRIM([Name]))) > 0),
            CONSTRAINT [CK_ClientResources_ReviewStatus]
                CHECK ([ReviewStatus] IN
                    (N'Unverified', N'Verified', N'AcceptedUnverified', N'NeedsReview'))
        );

        CREATE INDEX [IX_ClientResources_Client]
            ON [tb_client].[Resources]
                ([ClientId], [IsActive], [ResourceType], [Name], [ResourceId]);

        CREATE UNIQUE INDEX [UX_ClientResources_LocalKey]
            ON [tb_client].[Resources]([ClientId], [LocalKey])
            WHERE [LocalKey] IS NOT NULL;
    END;

    IF OBJECT_ID(N'tb_client.ResourceFields', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_client].[ResourceFields]
        (
            [ResourceFieldId] bigint IDENTITY(1,1) NOT NULL,
            [ResourceId] bigint NOT NULL,
            [FieldKey] nvarchar(120) NOT NULL,
            [FieldLabel] nvarchar(200) NOT NULL,
            [ValueText] nvarchar(max) NULL,
            [ValueType] nvarchar(24) NOT NULL
                CONSTRAINT [DF_ClientResourceFields_ValueType] DEFAULT (N'Text'),
            [SortOrder] int NOT NULL
                CONSTRAINT [DF_ClientResourceFields_SortOrder] DEFAULT (0),
            [UpdatedByWindowsSid] varbinary(85) NOT NULL,
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientResourceFields_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ClientResourceFields] PRIMARY KEY CLUSTERED ([ResourceFieldId]),
            CONSTRAINT [FK_ClientResourceFields_Resource]
                FOREIGN KEY ([ResourceId])
                REFERENCES [tb_client].[Resources]([ResourceId]) ON DELETE CASCADE,
            CONSTRAINT [FK_ClientResourceFields_UpdatedBy]
                FOREIGN KEY ([UpdatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [UX_ClientResourceFields_Key]
                UNIQUE ([ResourceId], [FieldKey]),
            CONSTRAINT [CK_ClientResourceFields_Key]
                CHECK (LEN(LTRIM(RTRIM([FieldKey]))) > 0),
            CONSTRAINT [CK_ClientResourceFields_Label]
                CHECK (LEN(LTRIM(RTRIM([FieldLabel]))) > 0),
            CONSTRAINT [CK_ClientResourceFields_Type]
                CHECK ([ValueType] IN
                    (N'Text', N'Number', N'Boolean', N'Date', N'Url', N'IpAddress', N'Phone', N'Email'))
        );
    END;

    -- Phone and email are first-class workbook field types. Repair the
    -- original schema-15 constraint on existing databases as well as new
    -- installs so generated Support Phone and Support Email columns validate.
    IF OBJECT_ID(N'tb_client.ResourceFields', N'U') IS NOT NULL
    BEGIN
        IF OBJECT_ID(N'tb_client.CK_ClientResourceFields_Type', N'C') IS NOT NULL
            ALTER TABLE [tb_client].[ResourceFields]
                DROP CONSTRAINT [CK_ClientResourceFields_Type];

        ALTER TABLE [tb_client].[ResourceFields] WITH CHECK
            ADD CONSTRAINT [CK_ClientResourceFields_Type]
            CHECK ([ValueType] IN
                (N'Text', N'Number', N'Boolean', N'Date', N'Url', N'IpAddress', N'Phone', N'Email'));
        ALTER TABLE [tb_client].[ResourceFields]
            CHECK CONSTRAINT [CK_ClientResourceFields_Type];
    END;

    IF OBJECT_ID(N'tb_client.Credentials', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_client].[Credentials]
        (
            [CredentialId] bigint IDENTITY(1,1) NOT NULL,
            [ClientId] int NOT NULL,
            [ResourceId] bigint NULL,
            [PersonId] bigint NULL,
            [LocalKey] nvarchar(120) NULL,
            [Name] nvarchar(240) NOT NULL,
            [Category] nvarchar(120) NULL,
            [Username] nvarchar(500) NULL,
            [LoginUrl] nvarchar(1000) NULL,
            [Notes] nvarchar(1000) NULL,
            [ReviewStatus] nvarchar(24) NOT NULL
                CONSTRAINT [DF_ClientCredentials_ReviewStatus] DEFAULT (N'Unverified'),
            [IsActive] bit NOT NULL
                CONSTRAINT [DF_ClientCredentials_IsActive] DEFAULT (1),
            [LastVerifiedAtUtc] datetime2(3) NULL,
            [CreatedByWindowsSid] varbinary(85) NOT NULL,
            [UpdatedByWindowsSid] varbinary(85) NOT NULL,
            [CreatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientCredentials_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientCredentials_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ClientCredentials] PRIMARY KEY CLUSTERED ([CredentialId]),
            CONSTRAINT [FK_ClientCredentials_Client]
                FOREIGN KEY ([ClientId]) REFERENCES [tb_data].[Clients]([Id]),
            CONSTRAINT [FK_ClientCredentials_Resource]
                FOREIGN KEY ([ResourceId]) REFERENCES [tb_client].[Resources]([ResourceId]),
            CONSTRAINT [FK_ClientCredentials_Person]
                FOREIGN KEY ([PersonId]) REFERENCES [tb_client].[People]([PersonId]),
            CONSTRAINT [FK_ClientCredentials_CreatedBy]
                FOREIGN KEY ([CreatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_ClientCredentials_UpdatedBy]
                FOREIGN KEY ([UpdatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_ClientCredentials_Name]
                CHECK (LEN(LTRIM(RTRIM([Name]))) > 0),
            CONSTRAINT [CK_ClientCredentials_ReviewStatus]
                CHECK ([ReviewStatus] IN
                    (N'Unverified', N'Verified', N'AcceptedUnverified', N'NeedsReview'))
        );

        CREATE INDEX [IX_ClientCredentials_Client]
            ON [tb_client].[Credentials]
                ([ClientId], [IsActive], [Category], [Name], [CredentialId]);

        CREATE UNIQUE INDEX [UX_ClientCredentials_LocalKey]
            ON [tb_client].[Credentials]([ClientId], [LocalKey])
            WHERE [LocalKey] IS NOT NULL;
    END;

    IF OBJECT_ID(N'tb_client.CredentialSecrets', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_client].[CredentialSecrets]
        (
            [SecretId] bigint IDENTITY(1,1) NOT NULL,
            [CredentialId] bigint NOT NULL,
            [SecretType] nvarchar(80) NOT NULL,
            [SecretLabel] nvarchar(200) NOT NULL,
            [ValueEncrypted] varbinary(max) NOT NULL,
            [IsCurrent] bit NOT NULL
                CONSTRAINT [DF_ClientCredentialSecrets_IsCurrent] DEFAULT (1),
            [LastVerifiedAtUtc] datetime2(3) NULL,
            [CreatedByWindowsSid] varbinary(85) NOT NULL,
            [UpdatedByWindowsSid] varbinary(85) NOT NULL,
            [CreatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientCredentialSecrets_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientCredentialSecrets_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ClientCredentialSecrets] PRIMARY KEY CLUSTERED ([SecretId]),
            CONSTRAINT [FK_ClientCredentialSecrets_Credential]
                FOREIGN KEY ([CredentialId])
                REFERENCES [tb_client].[Credentials]([CredentialId]) ON DELETE CASCADE,
            CONSTRAINT [FK_ClientCredentialSecrets_CreatedBy]
                FOREIGN KEY ([CreatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_ClientCredentialSecrets_UpdatedBy]
                FOREIGN KEY ([UpdatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [UX_ClientCredentialSecrets_Type]
                UNIQUE ([CredentialId], [SecretType], [SecretLabel]),
            CONSTRAINT [CK_ClientCredentialSecrets_Type]
                CHECK (LEN(LTRIM(RTRIM([SecretType]))) > 0),
            CONSTRAINT [CK_ClientCredentialSecrets_Label]
                CHECK (LEN(LTRIM(RTRIM([SecretLabel]))) > 0)
        );
    END;

    IF OBJECT_ID(N'tb_client.ClientFacts', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_client].[ClientFacts]
        (
            [FactId] bigint IDENTITY(1,1) NOT NULL,
            [ClientId] int NOT NULL,
            [LocalKey] nvarchar(120) NULL,
            [SectionName] nvarchar(120) NOT NULL,
            [FieldLabel] nvarchar(200) NOT NULL,
            [ValueText] nvarchar(max) NULL,
            [ValueType] nvarchar(24) NOT NULL
                CONSTRAINT [DF_ClientFacts_ValueType] DEFAULT (N'Text'),
            [ReviewStatus] nvarchar(24) NOT NULL
                CONSTRAINT [DF_ClientFacts_ReviewStatus] DEFAULT (N'Unverified'),
            [SortOrder] int NOT NULL
                CONSTRAINT [DF_ClientFacts_SortOrder] DEFAULT (0),
            [IsActive] bit NOT NULL
                CONSTRAINT [DF_ClientFacts_IsActive] DEFAULT (1),
            [LastVerifiedAtUtc] datetime2(3) NULL,
            [CreatedByWindowsSid] varbinary(85) NOT NULL,
            [UpdatedByWindowsSid] varbinary(85) NOT NULL,
            [CreatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientFacts_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientFacts_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ClientFacts] PRIMARY KEY CLUSTERED ([FactId]),
            CONSTRAINT [FK_ClientFacts_Client]
                FOREIGN KEY ([ClientId]) REFERENCES [tb_data].[Clients]([Id]),
            CONSTRAINT [FK_ClientFacts_CreatedBy]
                FOREIGN KEY ([CreatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_ClientFacts_UpdatedBy]
                FOREIGN KEY ([UpdatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_ClientFacts_Section]
                CHECK (LEN(LTRIM(RTRIM([SectionName]))) > 0),
            CONSTRAINT [CK_ClientFacts_Label]
                CHECK (LEN(LTRIM(RTRIM([FieldLabel]))) > 0),
            CONSTRAINT [CK_ClientFacts_Type]
                CHECK ([ValueType] IN
                    (N'Text', N'Number', N'Boolean', N'Date', N'Url', N'IpAddress', N'Phone', N'Email')),
            CONSTRAINT [CK_ClientFacts_ReviewStatus]
                CHECK ([ReviewStatus] IN
                    (N'Unverified', N'Verified', N'AcceptedUnverified', N'NeedsReview'))
        );

        CREATE INDEX [IX_ClientFacts_Client]
            ON [tb_client].[ClientFacts]
                ([ClientId], [IsActive], [SectionName], [SortOrder], [FactId]);

        CREATE UNIQUE INDEX [UX_ClientFacts_LocalKey]
            ON [tb_client].[ClientFacts]([ClientId], [LocalKey])
            WHERE [LocalKey] IS NOT NULL;
    END;

    IF OBJECT_ID(N'tb_client.ClientFacts', N'U') IS NOT NULL
    BEGIN
        IF OBJECT_ID(N'tb_client.CK_ClientFacts_Type', N'C') IS NOT NULL
            ALTER TABLE [tb_client].[ClientFacts]
                DROP CONSTRAINT [CK_ClientFacts_Type];

        ALTER TABLE [tb_client].[ClientFacts] WITH CHECK
            ADD CONSTRAINT [CK_ClientFacts_Type]
            CHECK ([ValueType] IN
                (N'Text', N'Number', N'Boolean', N'Date', N'Url', N'IpAddress', N'Phone', N'Email'));
        ALTER TABLE [tb_client].[ClientFacts]
            CHECK CONSTRAINT [CK_ClientFacts_Type];
    END;

    IF OBJECT_ID(N'tb_client.SourceDocuments', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_client].[SourceDocuments]
        (
            [SourceDocumentId] bigint IDENTITY(1,1) NOT NULL,
            [ClientId] int NOT NULL,
            [SourceKind] nvarchar(40) NOT NULL,
            [DisplayName] nvarchar(260) NOT NULL,
            [ContentSha256] binary(32) NOT NULL,
            [SourceModifiedAtUtc] datetime2(3) NULL,
            [ObservedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientSourceDocuments_ObservedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [CreatedByWindowsSid] varbinary(85) NOT NULL,
            CONSTRAINT [PK_ClientSourceDocuments] PRIMARY KEY CLUSTERED ([SourceDocumentId]),
            CONSTRAINT [FK_ClientSourceDocuments_Client]
                FOREIGN KEY ([ClientId]) REFERENCES [tb_data].[Clients]([Id]),
            CONSTRAINT [FK_ClientSourceDocuments_CreatedBy]
                FOREIGN KEY ([CreatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [UX_ClientSourceDocuments_Hash]
                UNIQUE ([ClientId], [SourceKind], [ContentSha256]),
            CONSTRAINT [CK_ClientSourceDocuments_Kind]
                CHECK ([SourceKind] IN
                    (N'Workbook', N'FireDrill', N'Manual', N'WHD', N'Sage', N'LegacySql'))
        );
    END;

    IF OBJECT_ID(N'tb_client.RecordProvenance', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_client].[RecordProvenance]
        (
            [RecordProvenanceId] bigint IDENTITY(1,1) NOT NULL,
            [ClientId] int NOT NULL,
            [EntityType] nvarchar(80) NOT NULL,
            [EntityId] bigint NOT NULL,
            [FieldKey] nvarchar(120) NULL,
            [SourceDocumentId] bigint NULL,
            [SourceSheet] nvarchar(128) NULL,
            [SourceAddress] nvarchar(40) NULL,
            [ReviewStatus] nvarchar(24) NOT NULL,
            [RecordedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientRecordProvenance_RecordedAtUtc]
                DEFAULT (SYSUTCDATETIME()),
            [RecordedByWindowsSid] varbinary(85) NOT NULL,
            CONSTRAINT [PK_ClientRecordProvenance]
                PRIMARY KEY CLUSTERED ([RecordProvenanceId]),
            CONSTRAINT [FK_ClientRecordProvenance_Client]
                FOREIGN KEY ([ClientId]) REFERENCES [tb_data].[Clients]([Id]),
            CONSTRAINT [FK_ClientRecordProvenance_Document]
                FOREIGN KEY ([SourceDocumentId])
                REFERENCES [tb_client].[SourceDocuments]([SourceDocumentId]),
            CONSTRAINT [FK_ClientRecordProvenance_RecordedBy]
                FOREIGN KEY ([RecordedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_ClientRecordProvenance_ReviewStatus]
                CHECK ([ReviewStatus] IN
                    (N'Unverified', N'Verified', N'AcceptedUnverified', N'NeedsReview', N'Rejected'))
        );

        CREATE INDEX [IX_ClientRecordProvenance_Entity]
            ON [tb_client].[RecordProvenance]
                ([ClientId], [EntityType], [EntityId], [RecordProvenanceId]);
    END;

    IF OBJECT_ID(N'tb_import.ClientInfoBatches', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_import].[ClientInfoBatches]
        (
            [BatchId] uniqueidentifier NOT NULL,
            [ClientId] int NOT NULL,
            [SourceDocumentId] bigint NULL,
            [TemplateVersion] nvarchar(40) NOT NULL,
            [WorkbookId] uniqueidentifier NOT NULL,
            [ContentSha256] binary(32) NOT NULL,
            [State] nvarchar(24) NOT NULL,
            [Message] nvarchar(2000) NULL,
            [CreatedByWindowsSid] varbinary(85) NOT NULL,
            [ApprovedByWindowsSid] varbinary(85) NULL,
            [CreatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientInfoBatches_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientInfoBatches_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [ApprovedAtUtc] datetime2(3) NULL,
            [PromotedAtUtc] datetime2(3) NULL,
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ClientInfoBatches] PRIMARY KEY CLUSTERED ([BatchId]),
            CONSTRAINT [FK_ClientInfoBatches_Client]
                FOREIGN KEY ([ClientId]) REFERENCES [tb_data].[Clients]([Id]),
            CONSTRAINT [FK_ClientInfoBatches_Document]
                FOREIGN KEY ([SourceDocumentId])
                REFERENCES [tb_client].[SourceDocuments]([SourceDocumentId]),
            CONSTRAINT [FK_ClientInfoBatches_CreatedBy]
                FOREIGN KEY ([CreatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_ClientInfoBatches_ApprovedBy]
                FOREIGN KEY ([ApprovedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [UX_ClientInfoBatches_Idempotency]
                UNIQUE ([ClientId], [WorkbookId], [ContentSha256]),
            CONSTRAINT [CK_ClientInfoBatches_State]
                CHECK ([State] IN
                    (N'Draft', N'Parsed', N'Validated', N'InReview', N'Approved',
                     N'Promoted', N'ValidationFailed', N'Rejected', N'Superseded', N'Failed'))
        );
    END;

    IF OBJECT_ID(N'tb_import.ClientInfoRecords', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_import].[ClientInfoRecords]
        (
            [ImportRecordId] bigint IDENTITY(1,1) NOT NULL,
            [BatchId] uniqueidentifier NOT NULL,
            [RecordType] nvarchar(40) NOT NULL,
            [LocalKey] nvarchar(120) NOT NULL,
            [ParentLocalKey] nvarchar(120) NULL,
            [PayloadJson] nvarchar(max) NOT NULL,
            [SourceSheet] nvarchar(128) NULL,
            [SourceRow] int NULL,
            [ReviewStatus] nvarchar(24) NOT NULL,
            [CreatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientInfoRecords_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT [PK_ClientInfoRecords] PRIMARY KEY CLUSTERED ([ImportRecordId]),
            CONSTRAINT [FK_ClientInfoRecords_Batch]
                FOREIGN KEY ([BatchId])
                REFERENCES [tb_import].[ClientInfoBatches]([BatchId]) ON DELETE CASCADE,
            CONSTRAINT [UX_ClientInfoRecords_Key]
                UNIQUE ([BatchId], [RecordType], [LocalKey]),
            CONSTRAINT [CK_ClientInfoRecords_Type]
                CHECK ([RecordType] IN
                    (N'Profile', N'Location', N'Person', N'Resource', N'ResourceField', N'Credential', N'Fact', N'Equipment')),
            CONSTRAINT [CK_ClientInfoRecords_Payload]
                CHECK (ISJSON([PayloadJson]) = 1),
            CONSTRAINT [CK_ClientInfoRecords_ReviewStatus]
                CHECK ([ReviewStatus] IN
                    (N'Unverified', N'Verified', N'AcceptedUnverified', N'NeedsReview', N'Rejected'))
        );
    END;

    -- ResourceField staging was added after the original schema-15 table was
    -- deployed. Repair the constraint on existing databases as well as new
    -- installs so the additive Client Info extension remains installer-safe.
    IF OBJECT_ID(N'tb_import.ClientInfoRecords', N'U') IS NOT NULL
    BEGIN
        IF OBJECT_ID(N'tb_import.CK_ClientInfoRecords_Type', N'C') IS NOT NULL
            ALTER TABLE [tb_import].[ClientInfoRecords]
                DROP CONSTRAINT [CK_ClientInfoRecords_Type];

        ALTER TABLE [tb_import].[ClientInfoRecords] WITH CHECK
            ADD CONSTRAINT [CK_ClientInfoRecords_Type]
            CHECK ([RecordType] IN
                (N'Profile', N'Location', N'Person', N'Resource', N'ResourceField', N'Credential', N'Fact', N'Equipment'));
        ALTER TABLE [tb_import].[ClientInfoRecords]
            CHECK CONSTRAINT [CK_ClientInfoRecords_Type];
    END;

    IF OBJECT_ID(N'tb_import.ClientInfoSecrets', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_import].[ClientInfoSecrets]
        (
            [ImportSecretId] bigint IDENTITY(1,1) NOT NULL,
            [BatchId] uniqueidentifier NOT NULL,
            [CredentialLocalKey] nvarchar(120) NOT NULL,
            [SecretType] nvarchar(80) NOT NULL,
            [SecretLabel] nvarchar(200) NOT NULL,
            [ValueEncrypted] varbinary(max) NOT NULL,
            [ComparisonStatus] nvarchar(24) NOT NULL
                CONSTRAINT [DF_ClientInfoSecrets_ComparisonStatus] DEFAULT (N'NotCompared'),
            [Resolution] nvarchar(24) NULL,
            [CreatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientInfoSecrets_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ClientInfoSecrets] PRIMARY KEY CLUSTERED ([ImportSecretId]),
            CONSTRAINT [FK_ClientInfoSecrets_Batch]
                FOREIGN KEY ([BatchId])
                REFERENCES [tb_import].[ClientInfoBatches]([BatchId]) ON DELETE CASCADE,
            CONSTRAINT [UX_ClientInfoSecrets_Key]
                UNIQUE ([BatchId], [CredentialLocalKey], [SecretType], [SecretLabel]),
            CONSTRAINT [CK_ClientInfoSecrets_Comparison]
                CHECK ([ComparisonStatus] IN
                    (N'NotCompared', N'Match', N'Mismatch', N'WorkbookOnly', N'FireDrillOnly', N'NotComparable')),
            CONSTRAINT [CK_ClientInfoSecrets_Resolution]
                CHECK ([Resolution] IS NULL OR [Resolution] IN
                    (N'UseWorkbook', N'UseFireDrill', N'VerifiedValue', N'Rejected'))
        );
    END;

    IF OBJECT_ID(N'tb_import.ClientInfoIssues', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_import].[ClientInfoIssues]
        (
            [IssueId] bigint IDENTITY(1,1) NOT NULL,
            [BatchId] uniqueidentifier NOT NULL,
            [ImportRecordId] bigint NULL,
            [Severity] nvarchar(12) NOT NULL,
            [IssueCode] nvarchar(80) NOT NULL,
            [Message] nvarchar(1000) NOT NULL,
            [IsResolved] bit NOT NULL
                CONSTRAINT [DF_ClientInfoIssues_IsResolved] DEFAULT (0),
            [ResolutionNote] nvarchar(1000) NULL,
            [ResolvedByWindowsSid] varbinary(85) NULL,
            [ResolvedAtUtc] datetime2(3) NULL,
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ClientInfoIssues] PRIMARY KEY CLUSTERED ([IssueId]),
            CONSTRAINT [FK_ClientInfoIssues_Batch]
                FOREIGN KEY ([BatchId])
                REFERENCES [tb_import].[ClientInfoBatches]([BatchId]) ON DELETE CASCADE,
            CONSTRAINT [FK_ClientInfoIssues_Record]
                FOREIGN KEY ([ImportRecordId])
                REFERENCES [tb_import].[ClientInfoRecords]([ImportRecordId]),
            CONSTRAINT [FK_ClientInfoIssues_ResolvedBy]
                FOREIGN KEY ([ResolvedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_ClientInfoIssues_Severity]
                CHECK ([Severity] IN (N'Error', N'Warning', N'Info'))
        );
    END;

    -- A revised copy of the same workbook replaces its earlier unfinished
    -- review. Keep the historical batch for audit purposes, but remove it
    -- from the active migration workflow.
    ;WITH active_workbook_reviews AS
    (
        SELECT
            [BatchId],
            ROW_NUMBER() OVER
            (
                PARTITION BY [ClientId], [WorkbookId]
                ORDER BY [CreatedAtUtc] DESC, [BatchId] DESC
            ) AS [ReviewOrder]
        FROM [tb_import].[ClientInfoBatches]
        WHERE [State] IN
            (N'Draft',N'Parsed',N'Validated',N'InReview',N'ValidationFailed')
    )
    UPDATE batch
    SET
        [State]=N'Superseded',
        [Message]=N'Replaced by a newer revision of this workbook.',
        [UpdatedAtUtc]=SYSUTCDATETIME()
    FROM [tb_import].[ClientInfoBatches] AS batch
    INNER JOIN active_workbook_reviews AS review
        ON review.[BatchId]=batch.[BatchId]
    WHERE review.[ReviewOrder]>1;

    IF OBJECT_ID(N'tb_import.ClientInfoPromotionMap', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_import].[ClientInfoPromotionMap]
        (
            [ImportRecordId] bigint NOT NULL,
            [EntityType] nvarchar(80) NOT NULL,
            [EntityId] bigint NOT NULL,
            [PromotedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientInfoPromotionMap_PromotedAtUtc] DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT [PK_ClientInfoPromotionMap] PRIMARY KEY CLUSTERED ([ImportRecordId]),
            CONSTRAINT [FK_ClientInfoPromotionMap_Record]
                FOREIGN KEY ([ImportRecordId])
                REFERENCES [tb_import].[ClientInfoRecords]([ImportRecordId])
        );
    END;

    IF OBJECT_ID(N'tb_ops.ClientInfoCutovers', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_ops].[ClientInfoCutovers]
        (
            [ClientId] int NOT NULL,
            [ActiveBatchId] uniqueidentifier NULL,
            [State] nvarchar(24) NOT NULL
                CONSTRAINT [DF_ClientInfoCutovers_State] DEFAULT (N'NotStarted'),
            [LegacyFrozenAtUtc] datetime2(3) NULL,
            [LiveAtUtc] datetime2(3) NULL,
            [HypercareEndsAtUtc] datetime2(3) NULL,
            [CompletedAtUtc] datetime2(3) NULL,
            [UpdatedByWindowsSid] varbinary(85) NOT NULL,
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_ClientInfoCutovers_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ClientInfoCutovers] PRIMARY KEY CLUSTERED ([ClientId]),
            CONSTRAINT [FK_ClientInfoCutovers_Client]
                FOREIGN KEY ([ClientId]) REFERENCES [tb_data].[Clients]([Id]),
            CONSTRAINT [FK_ClientInfoCutovers_Batch]
                FOREIGN KEY ([ActiveBatchId])
                REFERENCES [tb_import].[ClientInfoBatches]([BatchId]),
            CONSTRAINT [FK_ClientInfoCutovers_UpdatedBy]
                FOREIGN KEY ([UpdatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_ClientInfoCutovers_State]
                CHECK ([State] IN
                    (N'NotStarted', N'Staging', N'Ready', N'Frozen', N'Live',
                     N'Hypercare', N'Complete', N'RolledBack'))
        );
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_deploy].[SchemaMigrations]
        WHERE [MigrationId] = N'SqlServer2016.ClientInfoBeta.0015'
    )
        INSERT INTO [tb_deploy].[SchemaMigrations]
            ([MigrationId], [SchemaVersion], [ReleaseVersion], [ScriptChecksum])
        VALUES
            (N'SqlServer2016.ClientInfoBeta.0015', 15, N'0.6.2-beta.1', NULL);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'Schema-15-compatible Client Info beta extension installed.';
GO
