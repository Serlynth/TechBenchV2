:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

GRANT EXECUTE ON OBJECT::[tb_app].[BeginClientSecretMfaChallenge]
    TO [tb_role_client_secret_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[GetClientSecretMfaChallenge]
    TO [tb_role_client_secret_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[CancelClientSecretMfaChallenge]
    TO [tb_role_client_secret_reader];
GRANT EXECUTE ON OBJECT::[tb_app].[RevealClientCredentialSecret]
    TO [tb_role_client_secret_reader];

GRANT EXECUTE ON OBJECT::[tb_app].[AdminGetAuthPointUserMappings]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminSaveAuthPointUserMapping]
    TO [tb_role_admin];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminIssueMfaBreakGlassGrant]
    TO [tb_role_mfa_break_glass];
GRANT EXECUTE ON OBJECT::[tb_app].[AdminRevokeMfaBreakGlassGrant]
    TO [tb_role_mfa_break_glass];

GRANT EXECUTE ON OBJECT::[tb_service].[GetAuthPointMfaConfiguration]
    TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[ClaimAuthPointMfaChallenge]
    TO [tb_role_sync_service];
GRANT EXECUTE ON OBJECT::[tb_service].[CompleteAuthPointMfaChallenge]
    TO [tb_role_sync_service];

DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::[tb_security].[AuthPointUserMappings]
    TO [tb_role_user];
DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::[tb_security].[MfaChallenges]
    TO [tb_role_user];
DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::[tb_security].[MfaBreakGlassGrants]
    TO [tb_role_user];
DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::[tb_security].[AuthPointUserMappings]
    TO [tb_role_sync_service];
DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::[tb_security].[MfaChallenges]
    TO [tb_role_sync_service];
DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::[tb_security].[MfaBreakGlassGrants]
    TO [tb_role_sync_service];

REVOKE EXECUTE ON OBJECT::[tb_app].[BeginClientSecretMfaChallenge]
    FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[GetClientSecretMfaChallenge]
    FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[CancelClientSecretMfaChallenge]
    FROM [tb_preview_reader];
REVOKE EXECUTE ON OBJECT::[tb_app].[RevealClientCredentialSecret]
    FROM [tb_preview_reader];
GO

PRINT N'WatchGuard AuthPoint MFA permissions installed.';
GO
