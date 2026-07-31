:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/*
    Additive schema-15 extension for server-enforced WatchGuard AuthPoint MFA.
    No provider credential is stored in SQL. The API password and API key stay
    in a LocalMachine-DPAPI protected file on the Sync Service host.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF DATABASE_PRINCIPAL_ID(N'tb_role_mfa_break_glass') IS NULL
        CREATE ROLE [tb_role_mfa_break_glass] AUTHORIZATION [dbo];

    IF OBJECT_ID(N'tb_security.AuthPointUserMappings', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_security].[AuthPointUserMappings]
        (
            [WindowsSid] varbinary(85) NOT NULL,
            [AuthPointLogin] nvarchar(256) NOT NULL,
            [IsEnabled] bit NOT NULL
                CONSTRAINT [DF_AuthPointUserMappings_IsEnabled] DEFAULT (1),
            [UpdatedByWindowsSid] varbinary(85) NOT NULL,
            [UpdatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_AuthPointUserMappings_UpdatedAtUtc]
                DEFAULT (SYSUTCDATETIME()),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_AuthPointUserMappings]
                PRIMARY KEY CLUSTERED ([WindowsSid]),
            CONSTRAINT [FK_AuthPointUserMappings_User]
                FOREIGN KEY ([WindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_AuthPointUserMappings_UpdatedBy]
                FOREIGN KEY ([UpdatedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [CK_AuthPointUserMappings_Login]
                CHECK (LEN(LTRIM(RTRIM([AuthPointLogin]))) > 0)
        );

        CREATE UNIQUE INDEX [UX_AuthPointUserMappings_Login]
            ON [tb_security].[AuthPointUserMappings]([AuthPointLogin]);
    END;

    IF OBJECT_ID(N'tb_security.MfaChallenges', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_security].[MfaChallenges]
        (
            [ChallengeId] uniqueidentifier NOT NULL,
            [RequestId] uniqueidentifier NOT NULL,
            [ActorWindowsSid] varbinary(85) NOT NULL,
            [ActorLoginName] nvarchar(256) NOT NULL,
            [ProviderLogin] nvarchar(256) NOT NULL,
            [ActionScope] nvarchar(16) NOT NULL,
            [SecretId] bigint NOT NULL,
            [ClientMachine] nvarchar(128) NULL,
            [ChallengeNonceHash] binary(32) NOT NULL,
            [Status] nvarchar(24) NOT NULL,
            [AttemptCount] int NOT NULL
                CONSTRAINT [DF_MfaChallenges_AttemptCount] DEFAULT (0),
            [WorkerId] uniqueidentifier NULL,
            [LeaseId] uniqueidentifier NULL,
            [LeaseExpiresAtUtc] datetime2(3) NULL,
            [ProviderTransactionId] nvarchar(120) NULL,
            [OutcomeCode] nvarchar(80) NULL,
            [OutcomeMessage] nvarchar(500) NULL,
            [AuthorizationTokenHash] binary(64) NULL,
            [AuthorizationTokenEncrypted] varbinary(8000) NULL,
            [AuthorizationExpiresAtUtc] datetime2(3) NULL,
            [CreatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_MfaChallenges_CreatedAtUtc]
                DEFAULT (SYSUTCDATETIME()),
            [ExpiresAtUtc] datetime2(3) NOT NULL,
            [CompletedAtUtc] datetime2(3) NULL,
            [ConsumedAtUtc] datetime2(3) NULL,
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_MfaChallenges]
                PRIMARY KEY CLUSTERED ([ChallengeId]),
            CONSTRAINT [UX_MfaChallenges_RequestId] UNIQUE ([RequestId]),
            CONSTRAINT [FK_MfaChallenges_User]
                FOREIGN KEY ([ActorWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_MfaChallenges_Secret]
                FOREIGN KEY ([SecretId])
                REFERENCES [tb_client].[CredentialSecrets]([SecretId]),
            CONSTRAINT [CK_MfaChallenges_Action]
                CHECK ([ActionScope] IN (N'Reveal', N'Copy')),
            CONSTRAINT [CK_MfaChallenges_Status]
                CHECK ([Status] IN
                    (N'Queued', N'Processing', N'Approved', N'Denied',
                     N'Error', N'Cancelled', N'Expired', N'Consumed')),
            CONSTRAINT [CK_MfaChallenges_Attempts]
                CHECK ([AttemptCount] BETWEEN 0 AND 3)
        );

        CREATE INDEX [IX_MfaChallenges_WorkQueue]
            ON [tb_security].[MfaChallenges]
                ([Status], [ExpiresAtUtc], [LeaseExpiresAtUtc], [CreatedAtUtc])
            INCLUDE ([AttemptCount], [WorkerId], [LeaseId]);

        CREATE INDEX [IX_MfaChallenges_ActorRate]
            ON [tb_security].[MfaChallenges]
                ([ActorWindowsSid], [CreatedAtUtc] DESC)
            INCLUDE ([Status], [ActionScope], [SecretId]);

        CREATE INDEX [IX_MfaChallenges_Secret]
            ON [tb_security].[MfaChallenges]
                ([SecretId], [CreatedAtUtc] DESC);
    END;

    IF OBJECT_ID(N'tb_security.MfaBreakGlassGrants', N'U') IS NULL
    BEGIN
        CREATE TABLE [tb_security].[MfaBreakGlassGrants]
        (
            [GrantId] uniqueidentifier NOT NULL,
            [TargetWindowsSid] varbinary(85) NOT NULL,
            [ActionScope] nvarchar(16) NOT NULL,
            [SecretId] bigint NOT NULL,
            [Reason] nvarchar(500) NOT NULL,
            [ApprovedByWindowsSid] varbinary(85) NOT NULL,
            [ApprovedByLoginName] nvarchar(256) NOT NULL,
            [CreatedAtUtc] datetime2(3) NOT NULL
                CONSTRAINT [DF_MfaBreakGlassGrants_CreatedAtUtc]
                DEFAULT (SYSUTCDATETIME()),
            [ExpiresAtUtc] datetime2(3) NOT NULL,
            [RemainingUses] tinyint NOT NULL
                CONSTRAINT [DF_MfaBreakGlassGrants_RemainingUses] DEFAULT (1),
            [ConsumedAtUtc] datetime2(3) NULL,
            [RevokedAtUtc] datetime2(3) NULL,
            [RevokedByWindowsSid] varbinary(85) NULL,
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_MfaBreakGlassGrants]
                PRIMARY KEY CLUSTERED ([GrantId]),
            CONSTRAINT [FK_MfaBreakGlassGrants_Target]
                FOREIGN KEY ([TargetWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_MfaBreakGlassGrants_Approver]
                FOREIGN KEY ([ApprovedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_MfaBreakGlassGrants_Revoker]
                FOREIGN KEY ([RevokedByWindowsSid])
                REFERENCES [tb_security].[Users]([WindowsSid]),
            CONSTRAINT [FK_MfaBreakGlassGrants_Secret]
                FOREIGN KEY ([SecretId])
                REFERENCES [tb_client].[CredentialSecrets]([SecretId]),
            CONSTRAINT [CK_MfaBreakGlassGrants_Action]
                CHECK ([ActionScope] IN (N'Reveal', N'Copy')),
            CONSTRAINT [CK_MfaBreakGlassGrants_Reason]
                CHECK (LEN(LTRIM(RTRIM([Reason]))) >= 12),
            CONSTRAINT [CK_MfaBreakGlassGrants_Uses]
                CHECK ([RemainingUses] BETWEEN 0 AND 1),
            CONSTRAINT [CK_MfaBreakGlassGrants_TwoPerson]
                CHECK ([TargetWindowsSid] <> [ApprovedByWindowsSid])
        );

        CREATE INDEX [IX_MfaBreakGlassGrants_Consume]
            ON [tb_security].[MfaBreakGlassGrants]
                ([TargetWindowsSid], [SecretId], [ActionScope], [ExpiresAtUtc])
            INCLUDE ([RemainingUses], [RevokedAtUtc]);
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_deploy].[SchemaMigrations]
        WHERE [MigrationId] = N'SqlServer2016.AuthPointMfa.0015'
    )
        INSERT INTO [tb_deploy].[SchemaMigrations]
            ([MigrationId], [SchemaVersion], [ReleaseVersion], [ScriptChecksum])
        VALUES
            (N'SqlServer2016.AuthPointMfa.0015', 15, N'0.6.6-beta.1', NULL);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'Schema-15-compatible WatchGuard AuthPoint MFA extension installed.';
GO
