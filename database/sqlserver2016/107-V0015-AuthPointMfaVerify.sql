:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS
(
    SELECT 1 FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId]=N'SqlServer2016.AuthPointMfa.0015'
      AND [SchemaVersion]=15
)
    THROW 52490,N'The AuthPoint MFA schema-15 extension is not installed.',1;

IF OBJECT_ID(N'tb_security.AuthPointUserMappings',N'U') IS NULL
   OR OBJECT_ID(N'tb_security.MfaChallenges',N'U') IS NULL
   OR OBJECT_ID(N'tb_security.MfaBreakGlassGrants',N'U') IS NULL
   OR OBJECT_ID(N'tb_app.BeginClientSecretMfaChallenge',N'P') IS NULL
   OR OBJECT_ID(N'tb_app.GetClientSecretMfaChallenge',N'P') IS NULL
   OR OBJECT_ID(N'tb_app.CancelClientSecretMfaChallenge',N'P') IS NULL
   OR OBJECT_ID(N'tb_service.GetAuthPointMfaConfiguration',N'P') IS NULL
   OR OBJECT_ID(N'tb_service.ClaimAuthPointMfaChallenge',N'P') IS NULL
   OR OBJECT_ID(N'tb_service.CompleteAuthPointMfaChallenge',N'P') IS NULL
    THROW 52491,N'One or more AuthPoint MFA objects are missing.',1;

IF COL_LENGTH(N'tb_security.MfaChallenges',N'AuthorizationTokenHash') IS NULL
   OR COL_LENGTH(N'tb_security.MfaChallenges',N'AuthorizationTokenEncrypted') IS NULL
   OR COL_LENGTH(N'tb_security.MfaChallenges',N'ChallengeNonceHash') IS NULL
   OR COL_LENGTH(N'tb_security.MfaChallenges',N'ActorWindowsSid') IS NULL
    THROW 52492,N'The AuthPoint challenge binding columns are incomplete.',1;

IF OBJECT_DEFINITION(OBJECT_ID(N'tb_app.RevealClientCredentialSecret'))
        NOT LIKE N'%AuthorizationToken%'
   OR OBJECT_DEFINITION(OBJECT_ID(N'tb_app.RevealClientCredentialSecret'))
        NOT LIKE N'%ActorWindowsSid%'
   OR OBJECT_DEFINITION(OBJECT_ID(N'tb_app.RevealClientCredentialSecret'))
        NOT LIKE N'%Status%Consumed%'
    THROW 52493,N'The canonical secret reveal procedure is not MFA enforcing.',1;

IF OBJECT_DEFINITION(OBJECT_ID(N'tb_app.RevealFireDrillCredential'))
        LIKE N'%AuthPoint%'
   OR OBJECT_DEFINITION(OBJECT_ID(N'tb_app.RevealFireDrillCredential'))
        LIKE N'%MfaChallenge%'
    THROW 52494,N'FireDrill was unexpectedly modified by the AuthPoint extension.',1;

IF EXISTS
(
    SELECT 1 FROM [tb_data].[OrganizationSettings]
    WHERE [SettingKey] IN
        (N'AuthPoint.ApiKey',N'AuthPoint.AccessPassword',N'AuthPoint.BearerToken',
         N'AuthPoint.Secret',N'AuthPoint.SecretKey')
)
    THROW 52495,N'AuthPoint secret material must not be stored in SQL.',1;

IF HAS_PERMS_BY_NAME(N'tb_security.MfaChallenges',N'OBJECT',N'SELECT')=1
   AND IS_ROLEMEMBER(N'tb_role_user')=1
    THROW 52496,N'Desktop users must not directly read MFA challenge storage.',1;

PRINT N'WatchGuard AuthPoint MFA verification passed; schema version remains 15.';
GO
