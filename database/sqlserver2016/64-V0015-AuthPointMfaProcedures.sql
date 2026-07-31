:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'tb_app.AdminGetAuthPointUserMappings', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminGetAuthPointUserMappings];
GO

CREATE PROCEDURE [tb_app].[AdminGetAuthPointUserMappings]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit,
            @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin <> 1
        THROW 52400, N'Only a TechBench Admin may manage AuthPoint mappings.', 1;

    SELECT
        users.[LoginName], users.[DisplayName], users.[WindowsSid],
        mapping.[AuthPointLogin], COALESCE(mapping.[IsEnabled], 0) AS [IsEnabled],
        mapping.[UpdatedAtUtc], mapping.[RowVersion]
    FROM [tb_security].[Users] AS users
    LEFT JOIN [tb_security].[AuthPointUserMappings] AS mapping
        ON mapping.[WindowsSid]=users.[WindowsSid]
    WHERE users.[IsTechnician]=1 OR users.[IsManager]=1 OR users.[IsAdmin]=1
    ORDER BY users.[DisplayName], users.[LoginName];
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveAuthPointUserMapping', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveAuthPointUserMapping];
GO

CREATE PROCEDURE [tb_app].[AdminSaveAuthPointUserMapping]
    @LoginName nvarchar(256),
    @AuthPointLogin nvarchar(256),
    @IsEnabled bit = 1,
    @ExpectedRowVersion binary(8) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit,
            @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin <> 1
        THROW 52400, N'Only a TechBench Admin may manage AuthPoint mappings.', 1;

    SET @LoginName=NULLIF(LTRIM(RTRIM(@LoginName)),N'');
    SET @AuthPointLogin=NULLIF(LTRIM(RTRIM(@AuthPointLogin)),N'');
    IF @LoginName IS NULL OR @AuthPointLogin IS NULL
        THROW 52401, N'Windows login and AuthPoint login are required.', 1;

    DECLARE @TargetSid varbinary(85)=
        (SELECT [WindowsSid] FROM [tb_security].[Users]
         WHERE [LoginName]=@LoginName);
    IF @TargetSid IS NULL
        THROW 52402, N'The selected TechBench user no longer exists.', 1;

    BEGIN TRY
        BEGIN TRANSACTION;
        IF EXISTS
        (
            SELECT 1 FROM [tb_security].[AuthPointUserMappings]
            WHERE [WindowsSid]=@TargetSid
        )
        BEGIN
            IF @ExpectedRowVersion IS NULL
                THROW 52403, N'The mapping changed after it was loaded. Refresh and try again.', 1;
            UPDATE [tb_security].[AuthPointUserMappings]
            SET [AuthPointLogin]=@AuthPointLogin, [IsEnabled]=@IsEnabled,
                [UpdatedByWindowsSid]=@ActorSid,
                [UpdatedAtUtc]=SYSUTCDATETIME()
            WHERE [WindowsSid]=@TargetSid
              AND [RowVersion]=@ExpectedRowVersion;
            IF @@ROWCOUNT<>1
                THROW 52403, N'The mapping changed after it was loaded. Refresh and try again.', 1;
        END
        ELSE
        BEGIN
            INSERT INTO [tb_security].[AuthPointUserMappings]
                ([WindowsSid],[AuthPointLogin],[IsEnabled],[UpdatedByWindowsSid])
            VALUES
                (@TargetSid,@AuthPointLogin,@IsEnabled,@ActorSid);
        END;

        EXEC [tb_security].[WriteAuditEvent]
            @Action=N'AuthPointUserMappingSaved',
            @EntityType=N'AuthPointUserMapping',
            @EntityId=@LoginName,@RequestId=@RequestId;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        users.[LoginName], users.[DisplayName], users.[WindowsSid],
        mapping.[AuthPointLogin], mapping.[IsEnabled], mapping.[UpdatedAtUtc],
        mapping.[RowVersion]
    FROM [tb_security].[AuthPointUserMappings] AS mapping
    INNER JOIN [tb_security].[Users] AS users
        ON users.[WindowsSid]=mapping.[WindowsSid]
    WHERE mapping.[WindowsSid]=@TargetSid;
END;
GO

IF OBJECT_ID(N'tb_app.BeginClientSecretMfaChallenge', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[BeginClientSecretMfaChallenge];
GO

CREATE PROCEDURE [tb_app].[BeginClientSecretMfaChallenge]
    @SecretId bigint,
    @ActionScope nvarchar(16),
    @ClientMachine nvarchar(128) = NULL,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit,
            @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT, @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;

    IF @ActionScope NOT IN (N'Reveal',N'Copy')
        THROW 52410, N'The MFA action is invalid.', 1;
    IF COALESCE(TRY_CONVERT(bit,
        (SELECT [SettingValue] FROM [tb_data].[OrganizationSettings]
         WHERE [SettingKey]=N'AuthPoint.Enabled')),0)<>1
    BEGIN
        SELECT CONVERT(uniqueidentifier,NULL) AS [ChallengeId],
               CONVERT(varbinary(32),NULL) AS [ChallengeNonce],
               CONVERT(nvarchar(24),N'NotRequired') AS [Status],
               SYSUTCDATETIME() AS [ExpiresAtUtc],
               CONVERT(nvarchar(256),N'') AS [ProviderLogin];
        RETURN;
    END;
    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_client].[CredentialSecrets] AS secret
        INNER JOIN [tb_client].[Credentials] AS credential
            ON credential.[CredentialId]=secret.[CredentialId]
        WHERE secret.[SecretId]=@SecretId AND secret.[IsCurrent]=1
          AND credential.[IsActive]=1
    )
        THROW 52393, N'The requested client secret was not found.', 1;

    DECLARE @ProviderLogin nvarchar(256)=
        (SELECT [AuthPointLogin]
         FROM [tb_security].[AuthPointUserMappings]
         WHERE [WindowsSid]=@ActorSid AND [IsEnabled]=1);
    IF @ProviderLogin IS NULL
        THROW 52412, N'Your Windows account is not mapped to an enabled AuthPoint user.', 1;

    DECLARE @NowUtc datetime2(3)=SYSUTCDATETIME();
    IF (SELECT COUNT_BIG(*) FROM [tb_security].[MfaChallenges]
        WHERE [ActorWindowsSid]=@ActorSid
          AND [CreatedAtUtc]>=DATEADD(minute,-2,@NowUtc))>=3
        THROW 52413, N'Too many AuthPoint requests were started. Wait two minutes and try again.', 1;
    IF (SELECT COUNT_BIG(*) FROM [tb_security].[MfaChallenges]
        WHERE [ActorWindowsSid]=@ActorSid
          AND [CreatedAtUtc]>=DATEADD(minute,-15,@NowUtc))>=10
        THROW 52413, N'Too many AuthPoint requests were started. Wait before trying again.', 1;
    IF EXISTS
    (
        SELECT 1 FROM [tb_security].[MfaChallenges]
        WHERE [ActorWindowsSid]=@ActorSid AND [SecretId]=@SecretId
          AND [ActionScope]=@ActionScope
          AND [Status] IN (N'Queued',N'Processing')
          AND [ExpiresAtUtc]>@NowUtc
    )
        THROW 52414, N'An AuthPoint request for this action is already in progress.', 1;

    DECLARE @ChallengeId uniqueidentifier=NEWID(),
            @ChallengeNonce varbinary(32)=CRYPT_GEN_RANDOM(32),
            @EffectiveRequestId uniqueidentifier=COALESCE(@RequestId,NEWID()),
            @ActorLogin nvarchar(256)=CONVERT(nvarchar(256),ORIGINAL_LOGIN());
    SET @ClientMachine=LEFT(NULLIF(LTRIM(RTRIM(@ClientMachine)),N''),128);

    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE [tb_security].[MfaChallenges]
        SET [Status]=N'Expired',[CompletedAtUtc]=@NowUtc,
            [OutcomeCode]=N'CLIENT_EXPIRED',
            [OutcomeMessage]=N'The AuthPoint request expired.'
        WHERE [Status] IN (N'Queued',N'Processing',N'Approved')
          AND [ExpiresAtUtc]<=@NowUtc;

        INSERT INTO [tb_security].[MfaChallenges]
        (
            [ChallengeId],[RequestId],[ActorWindowsSid],[ActorLoginName],
            [ProviderLogin],[ActionScope],[SecretId],[ClientMachine],
            [ChallengeNonceHash],[Status],[ExpiresAtUtc]
        )
        VALUES
        (
            @ChallengeId,@EffectiveRequestId,@ActorSid,@ActorLogin,
            @ProviderLogin,@ActionScope,@SecretId,@ClientMachine,
            HASHBYTES(N'SHA2_256',@ChallengeNonce),N'Queued',
            DATEADD(minute,2,@NowUtc)
        );

        DECLARE @EntityId nvarchar(120)=CONVERT(nvarchar(36),@ChallengeId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action=N'ClientSecretMfaRequested',@EntityType=N'MfaChallenge',
            @EntityId=@EntityId,@RequestId=@EffectiveRequestId;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT @ChallengeId AS [ChallengeId],@ChallengeNonce AS [ChallengeNonce],
           CONVERT(nvarchar(24),N'Queued') AS [Status],
           DATEADD(minute,2,@NowUtc) AS [ExpiresAtUtc],
           @ProviderLogin AS [ProviderLogin];
END;
GO

IF OBJECT_ID(N'tb_app.GetClientSecretMfaChallenge', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetClientSecretMfaChallenge];
GO

CREATE PROCEDURE [tb_app].[GetClientSecretMfaChallenge]
    @ChallengeId uniqueidentifier,
    @ChallengeNonce varbinary(32)
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit,
            @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,@IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,@IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @ChallengeNonce IS NULL OR DATALENGTH(@ChallengeNonce)<>32
        THROW 52415, N'The AuthPoint challenge proof is invalid.', 1;

    DECLARE @NowUtc datetime2(3)=SYSUTCDATETIME(),
            @Status nvarchar(24),@OutcomeCode nvarchar(80),
            @OutcomeMessage nvarchar(500),@ExpiresAtUtc datetime2(3),
            @TokenEncrypted varbinary(8000),@AuthorizationToken varbinary(32),
            @Authenticator varbinary(32)=HASHBYTES(N'SHA2_256',
                CONVERT(varbinary(max),N'MfaAuthorization|'
                    +CONVERT(nvarchar(36),@ChallengeId)));

    BEGIN TRY
        BEGIN TRANSACTION;
        IF NOT EXISTS
        (
            SELECT 1 FROM [tb_security].[MfaChallenges] WITH (UPDLOCK,HOLDLOCK)
            WHERE [ChallengeId]=@ChallengeId
              AND [ActorWindowsSid]=@ActorSid
              AND [ChallengeNonceHash]=HASHBYTES(N'SHA2_256',@ChallengeNonce)
        )
            THROW 52416, N'The AuthPoint challenge is unavailable for this Windows account.', 1;

        UPDATE [tb_security].[MfaChallenges]
        SET [Status]=N'Expired',[CompletedAtUtc]=@NowUtc,
            [OutcomeCode]=N'CLIENT_EXPIRED',
            [OutcomeMessage]=N'The AuthPoint request expired.',
            [AuthorizationTokenEncrypted]=NULL,
            [AuthorizationTokenHash]=NULL,
            [AuthorizationExpiresAtUtc]=NULL
        WHERE [ChallengeId]=@ChallengeId
          AND [Status] IN (N'Queued',N'Processing',N'Approved')
          AND ([ExpiresAtUtc]<=@NowUtc
               OR ([AuthorizationExpiresAtUtc] IS NOT NULL
                   AND [AuthorizationExpiresAtUtc]<=@NowUtc));

        SELECT @Status=[Status],@OutcomeCode=[OutcomeCode],
               @OutcomeMessage=[OutcomeMessage],@ExpiresAtUtc=[ExpiresAtUtc],
               @TokenEncrypted=[AuthorizationTokenEncrypted]
        FROM [tb_security].[MfaChallenges]
        WHERE [ChallengeId]=@ChallengeId;

        IF @Status=N'Approved' AND @TokenEncrypted IS NOT NULL
        BEGIN
            OPEN SYMMETRIC KEY [tb_ClientSecretKey]
                DECRYPTION BY CERTIFICATE [tb_ClientSecretCertificate];
            SET @AuthorizationToken=CONVERT(varbinary(32),DecryptByKey(
                @TokenEncrypted,1,@Authenticator));
            CLOSE SYMMETRIC KEY [tb_ClientSecretKey];
            IF @AuthorizationToken IS NULL OR DATALENGTH(@AuthorizationToken)<>32
                THROW 52417, N'The AuthPoint authorization could not be recovered.', 1;
            UPDATE [tb_security].[MfaChallenges]
            SET [AuthorizationTokenEncrypted]=NULL
            WHERE [ChallengeId]=@ChallengeId;
        END;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        BEGIN TRY
            CLOSE SYMMETRIC KEY [tb_ClientSecretKey];
        END TRY BEGIN CATCH END CATCH;
        THROW;
    END CATCH;

    SELECT @ChallengeId AS [ChallengeId],@Status AS [Status],
           COALESCE(@OutcomeCode,N'') AS [OutcomeCode],
           COALESCE(@OutcomeMessage,N'') AS [OutcomeMessage],
           @ExpiresAtUtc AS [ExpiresAtUtc],
           @AuthorizationToken AS [AuthorizationToken];
END;
GO

IF OBJECT_ID(N'tb_app.CancelClientSecretMfaChallenge', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[CancelClientSecretMfaChallenge];
GO

CREATE PROCEDURE [tb_app].[CancelClientSecretMfaChallenge]
    @ChallengeId uniqueidentifier,
    @ChallengeNonce varbinary(32),
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ActorSid varbinary(85),@IsManager bit,@IsAdmin bit,
            @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,@IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,@IsSyncOperator=@IsSyncOperator OUTPUT;

    UPDATE [tb_security].[MfaChallenges]
    SET [Status]=N'Cancelled',[CompletedAtUtc]=SYSUTCDATETIME(),
        [OutcomeCode]=N'CLIENT_CANCELLED',
        [OutcomeMessage]=N'The user cancelled the AuthPoint request.',
        [AuthorizationTokenEncrypted]=NULL,
        [AuthorizationTokenHash]=NULL,
        [AuthorizationExpiresAtUtc]=NULL
    WHERE [ChallengeId]=@ChallengeId AND [ActorWindowsSid]=@ActorSid
      AND [ChallengeNonceHash]=HASHBYTES(N'SHA2_256',@ChallengeNonce)
      AND [Status] IN (N'Queued',N'Processing',N'Approved');
    IF @@ROWCOUNT=1
    BEGIN
        DECLARE @EntityId nvarchar(120)=CONVERT(nvarchar(36),@ChallengeId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action=N'ClientSecretMfaCancelled',@EntityType=N'MfaChallenge',
            @EntityId=@EntityId,@RequestId=@RequestId;
    END;
END;
GO

IF OBJECT_ID(N'tb_service.GetAuthPointMfaConfiguration', N'P') IS NOT NULL
    DROP PROCEDURE [tb_service].[GetAuthPointMfaConfiguration];
GO

CREATE PROCEDURE [tb_service].[GetAuthPointMfaConfiguration]
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    IF IS_ROLEMEMBER(N'tb_role_sync_service')<>1
        THROW 52420,N'The Sync Service role is required.',1;
    SELECT
        COALESCE(TRY_CONVERT(bit,(SELECT [SettingValue]
            FROM [tb_data].[OrganizationSettings]
            WHERE [SettingKey]=N'AuthPoint.Enabled')),0) AS [Enabled],
        COALESCE((SELECT [SettingValue] FROM [tb_data].[OrganizationSettings]
            WHERE [SettingKey]=N'AuthPoint.BaseApiUrl'),N'') AS [BaseApiUrl],
        COALESCE((SELECT [SettingValue] FROM [tb_data].[OrganizationSettings]
            WHERE [SettingKey]=N'AuthPoint.AccountId'),N'') AS [AccountId],
        COALESCE((SELECT [SettingValue] FROM [tb_data].[OrganizationSettings]
            WHERE [SettingKey]=N'AuthPoint.ResourceId'),N'') AS [ResourceId],
        COALESCE((SELECT [SettingValue] FROM [tb_data].[OrganizationSettings]
            WHERE [SettingKey]=N'AuthPoint.AccessId'),N'') AS [AccessId];
END;
GO

IF OBJECT_ID(N'tb_service.ClaimAuthPointMfaChallenge', N'P') IS NOT NULL
    DROP PROCEDURE [tb_service].[ClaimAuthPointMfaChallenge];
GO

CREATE PROCEDURE [tb_service].[ClaimAuthPointMfaChallenge]
    @WorkerId uniqueidentifier,
    @LeaseSeconds int = 150
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF IS_ROLEMEMBER(N'tb_role_sync_service')<>1
        THROW 52420,N'The Sync Service role is required.',1;
    IF @WorkerId IS NULL THROW 52421,N'WorkerId is required.',1;
    SET @LeaseSeconds=CASE WHEN @LeaseSeconds<90 THEN 90
        WHEN @LeaseSeconds>240 THEN 240 ELSE @LeaseSeconds END;

    DECLARE @NowUtc datetime2(3)=SYSUTCDATETIME(),
            @ChallengeId uniqueidentifier,@LeaseId uniqueidentifier=NEWID();
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE [tb_security].[MfaChallenges]
        SET [Status]=N'Expired',[CompletedAtUtc]=@NowUtc,
            [OutcomeCode]=N'CHALLENGE_EXPIRED',
            [OutcomeMessage]=N'The AuthPoint request expired.'
        WHERE [Status] IN (N'Queued',N'Processing') AND [ExpiresAtUtc]<=@NowUtc;

        UPDATE [tb_security].[MfaChallenges]
        SET [Status]=CASE WHEN [AttemptCount]>=3 THEN N'Error' ELSE N'Queued' END,
            [OutcomeCode]=CASE WHEN [AttemptCount]>=3 THEN N'LEASE_RETRY_LIMIT' END,
            [OutcomeMessage]=CASE WHEN [AttemptCount]>=3
                THEN N'The AuthPoint request could not be completed.' END,
            [CompletedAtUtc]=CASE WHEN [AttemptCount]>=3 THEN @NowUtc END,
            [WorkerId]=NULL,[LeaseId]=NULL,[LeaseExpiresAtUtc]=NULL
        WHERE [Status]=N'Processing' AND [LeaseExpiresAtUtc]<=@NowUtc;

        SELECT TOP (1) @ChallengeId=[ChallengeId]
        FROM [tb_security].[MfaChallenges] WITH (UPDLOCK,READPAST,ROWLOCK)
        WHERE [Status]=N'Queued' AND [ExpiresAtUtc]>@NowUtc
          AND [AttemptCount]<3
        ORDER BY [CreatedAtUtc],[ChallengeId];

        IF @ChallengeId IS NOT NULL
        BEGIN
            UPDATE [tb_security].[MfaChallenges]
            SET [Status]=N'Processing',[AttemptCount]=[AttemptCount]+1,
                [WorkerId]=@WorkerId,[LeaseId]=@LeaseId,
                [LeaseExpiresAtUtc]=DATEADD(second,@LeaseSeconds,@NowUtc)
            WHERE [ChallengeId]=@ChallengeId AND [Status]=N'Queued';
            IF @@ROWCOUNT<>1 SET @ChallengeId=NULL;
        END;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT [ChallengeId],[LeaseId],[ProviderLogin],[ClientMachine],
           [ActionScope],[SecretId],[ExpiresAtUtc]
    FROM [tb_security].[MfaChallenges]
    WHERE [ChallengeId]=@ChallengeId AND [WorkerId]=@WorkerId
      AND [LeaseId]=@LeaseId AND [Status]=N'Processing';
END;
GO

IF OBJECT_ID(N'tb_service.CompleteAuthPointMfaChallenge', N'P') IS NOT NULL
    DROP PROCEDURE [tb_service].[CompleteAuthPointMfaChallenge];
GO

CREATE PROCEDURE [tb_service].[CompleteAuthPointMfaChallenge]
    @ChallengeId uniqueidentifier,
    @WorkerId uniqueidentifier,
    @LeaseId uniqueidentifier,
    @Result nvarchar(16),
    @OutcomeCode nvarchar(80) = NULL,
    @OutcomeMessage nvarchar(500) = NULL,
    @ProviderTransactionId nvarchar(120) = NULL
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF IS_ROLEMEMBER(N'tb_role_sync_service')<>1
        THROW 52420,N'The Sync Service role is required.',1;
    IF @Result NOT IN (N'Approved',N'Denied',N'Error')
        THROW 52422,N'The AuthPoint result is invalid.',1;

    DECLARE @NowUtc datetime2(3)=SYSUTCDATETIME(),
            @AuthorizationToken varbinary(32),
            @TokenHash binary(64),@TokenEncrypted varbinary(8000),
            @ActorSid varbinary(85),@ActorLogin nvarchar(256),
            @Authenticator varbinary(32)=HASHBYTES(N'SHA2_256',
                CONVERT(varbinary(max),N'MfaAuthorization|'
                    +CONVERT(nvarchar(36),@ChallengeId)));
    IF @Result=N'Approved'
    BEGIN
        SET @AuthorizationToken=CRYPT_GEN_RANDOM(32);
        SET @TokenHash=HASHBYTES(N'SHA2_512',@AuthorizationToken);
        OPEN SYMMETRIC KEY [tb_ClientSecretKey]
            DECRYPTION BY CERTIFICATE [tb_ClientSecretCertificate];
        SET @TokenEncrypted=EncryptByKey(
            Key_GUID(N'tb_ClientSecretKey'),@AuthorizationToken,1,@Authenticator);
        CLOSE SYMMETRIC KEY [tb_ClientSecretKey];
        IF @TokenEncrypted IS NULL THROW 52423,N'AuthPoint authorization encryption failed.',1;
    END;

    BEGIN TRY
        BEGIN TRANSACTION;
        SELECT @ActorSid=[ActorWindowsSid],@ActorLogin=[ActorLoginName]
        FROM [tb_security].[MfaChallenges] WITH (UPDLOCK,HOLDLOCK)
        WHERE [ChallengeId]=@ChallengeId AND [Status]=N'Processing'
          AND [WorkerId]=@WorkerId AND [LeaseId]=@LeaseId
          AND [LeaseExpiresAtUtc]>@NowUtc AND [ExpiresAtUtc]>@NowUtc;
        IF @ActorSid IS NULL
            THROW 52424,N'The AuthPoint challenge lease is no longer valid.',1;

        UPDATE [tb_security].[MfaChallenges]
        SET [Status]=@Result,[OutcomeCode]=LEFT(NULLIF(@OutcomeCode,N''),80),
            [OutcomeMessage]=LEFT(NULLIF(@OutcomeMessage,N''),500),
            [ProviderTransactionId]=LEFT(NULLIF(@ProviderTransactionId,N''),120),
            [AuthorizationTokenHash]=@TokenHash,
            [AuthorizationTokenEncrypted]=@TokenEncrypted,
            [AuthorizationExpiresAtUtc]=CASE WHEN @Result=N'Approved'
                THEN DATEADD(second,60,@NowUtc) END,
            [CompletedAtUtc]=@NowUtc,[WorkerId]=NULL,[LeaseId]=NULL,
            [LeaseExpiresAtUtc]=NULL
        WHERE [ChallengeId]=@ChallengeId;

        INSERT INTO [tb_audit].[AuditEvents]
        (
            [ActorWindowsSid],[ActorLoginName],[Action],[EntityType],
            [EntityId],[RequestId],[DataJson],[OccurredAtUtc]
        )
        SELECT [ActorWindowsSid],[ActorLoginName],
               CASE @Result WHEN N'Approved' THEN N'ClientSecretMfaApproved'
                    WHEN N'Denied' THEN N'ClientSecretMfaDenied'
                    ELSE N'ClientSecretMfaError' END,
               N'MfaChallenge',CONVERT(nvarchar(36),[ChallengeId]),
               [RequestId],NULL,@NowUtc
        FROM [tb_security].[MfaChallenges]
        WHERE [ChallengeId]=@ChallengeId;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        BEGIN TRY
            CLOSE SYMMETRIC KEY [tb_ClientSecretKey];
        END TRY BEGIN CATCH END CATCH;
        THROW;
    END CATCH;
END;
GO

IF OBJECT_ID(N'tb_app.AdminIssueMfaBreakGlassGrant', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminIssueMfaBreakGlassGrant];
GO

CREATE PROCEDURE [tb_app].[AdminIssueMfaBreakGlassGrant]
    @TargetLoginName nvarchar(256),
    @SecretId bigint,
    @ActionScope nvarchar(16),
    @Reason nvarchar(500),
    @ValidMinutes int = 10,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ApproverSid varbinary(85),@IsManager bit,@IsAdmin bit,
            @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ApproverSid OUTPUT,@IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,@IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1 OR IS_ROLEMEMBER(N'tb_role_mfa_break_glass')<>1
        THROW 52430,N'The dedicated MFA break-glass role and Admin permission are required.',1;
    SET @TargetLoginName=NULLIF(LTRIM(RTRIM(@TargetLoginName)),N'');
    SET @Reason=NULLIF(LTRIM(RTRIM(@Reason)),N'');
    IF @ActionScope NOT IN (N'Reveal',N'Copy') OR LEN(COALESCE(@Reason,N''))<12
        THROW 52431,N'A valid action and a specific break-glass reason are required.',1;
    IF @ValidMinutes<1 OR @ValidMinutes>10
        THROW 52432,N'Break-glass access must expire within ten minutes.',1;
    DECLARE @TargetSid varbinary(85)=(SELECT [WindowsSid]
        FROM [tb_security].[Users] WHERE [LoginName]=@TargetLoginName);
    IF @TargetSid IS NULL THROW 52402,N'The selected TechBench user no longer exists.',1;
    IF @TargetSid=@ApproverSid
        THROW 52433,N'A second administrator must approve break-glass access.',1;
    IF NOT EXISTS (SELECT 1 FROM [tb_client].[CredentialSecrets]
        WHERE [SecretId]=@SecretId AND [IsCurrent]=1)
        THROW 52393,N'The requested client secret was not found.',1;

    DECLARE @GrantId uniqueidentifier=NEWID(),@NowUtc datetime2(3)=SYSUTCDATETIME();
    INSERT INTO [tb_security].[MfaBreakGlassGrants]
    (
        [GrantId],[TargetWindowsSid],[ActionScope],[SecretId],[Reason],
        [ApprovedByWindowsSid],[ApprovedByLoginName],[ExpiresAtUtc]
    )
    VALUES
    (
        @GrantId,@TargetSid,@ActionScope,@SecretId,@Reason,@ApproverSid,
        CONVERT(nvarchar(256),ORIGINAL_LOGIN()),DATEADD(minute,@ValidMinutes,@NowUtc)
    );
    DECLARE @EntityId nvarchar(120)=CONVERT(nvarchar(36),@GrantId);
    EXEC [tb_security].[WriteAuditEvent]
        @Action=N'MfaBreakGlassIssued',@EntityType=N'MfaBreakGlassGrant',
        @EntityId=@EntityId,@RequestId=@RequestId;
    SELECT @GrantId AS [GrantId],DATEADD(minute,@ValidMinutes,@NowUtc) AS [ExpiresAtUtc];
END;
GO

IF OBJECT_ID(N'tb_app.AdminRevokeMfaBreakGlassGrant', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminRevokeMfaBreakGlassGrant];
GO

CREATE PROCEDURE [tb_app].[AdminRevokeMfaBreakGlassGrant]
    @GrantId uniqueidentifier,
    @RequestId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ActorSid varbinary(85),@IsManager bit,@IsAdmin bit,
            @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,@IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,@IsSyncOperator=@IsSyncOperator OUTPUT;
    IF @IsAdmin<>1 OR IS_ROLEMEMBER(N'tb_role_mfa_break_glass')<>1
        THROW 52430,N'The dedicated MFA break-glass role and Admin permission are required.',1;
    UPDATE [tb_security].[MfaBreakGlassGrants]
    SET [RevokedAtUtc]=SYSUTCDATETIME(),[RevokedByWindowsSid]=@ActorSid,
        [RemainingUses]=0
    WHERE [GrantId]=@GrantId AND [RevokedAtUtc] IS NULL
      AND [ConsumedAtUtc] IS NULL;
    DECLARE @EntityId nvarchar(120)=CONVERT(nvarchar(36),@GrantId);
    EXEC [tb_security].[WriteAuditEvent]
        @Action=N'MfaBreakGlassRevoked',@EntityType=N'MfaBreakGlassGrant',
        @EntityId=@EntityId,@RequestId=@RequestId;
END;
GO

/* Replace only the canonical Client Info secret procedure. FireDrill remains unchanged. */
IF OBJECT_ID(N'tb_app.RevealClientCredentialSecret', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[RevealClientCredentialSecret];
GO

CREATE PROCEDURE [tb_app].[RevealClientCredentialSecret]
    @SecretId bigint,
    @AccessAction nvarchar(12) = N'Reveal',
    @AuthorizationToken varbinary(32) = NULL,
    @RequestId uniqueidentifier = NULL
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @AccessAction NOT IN (N'Reveal',N'Copy')
        THROW 52392,N'The secret access action is invalid.',1;

    DECLARE @ActorSid varbinary(85),@IsManager bit,@IsAdmin bit,
            @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,@IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,@IsSyncOperator=@IsSyncOperator OUTPUT;
    DECLARE @NowUtc datetime2(3)=SYSUTCDATETIME(),@Authorized bit=0,
            @UsedBreakGlass bit=0,
            @MfaEnabled bit=COALESCE(TRY_CONVERT(bit,
                (SELECT [SettingValue] FROM [tb_data].[OrganizationSettings]
                 WHERE [SettingKey]=N'AuthPoint.Enabled')),0);

    DECLARE @Authenticator varbinary(32)=HASHBYTES(N'SHA2_256',
        CONVERT(varbinary(max),N'ClientSecret|'+CONVERT(nvarchar(30),@SecretId)));
    DECLARE @CredentialId bigint,@ClientId int,@CredentialName nvarchar(240),
            @SecretType nvarchar(80),@SecretLabel nvarchar(200),
            @SecretValue nvarchar(max),@SecretRowVersion binary(8);

    BEGIN TRY
        BEGIN TRANSACTION;
        IF @MfaEnabled=0
            SET @Authorized=1;
        ELSE IF @AuthorizationToken IS NOT NULL
             AND DATALENGTH(@AuthorizationToken)=32
        BEGIN
            UPDATE [tb_security].[MfaChallenges]
            SET [Status]=N'Consumed',[ConsumedAtUtc]=@NowUtc,
                [AuthorizationTokenHash]=NULL,
                [AuthorizationTokenEncrypted]=NULL,
                [AuthorizationExpiresAtUtc]=NULL
            WHERE [ActorWindowsSid]=@ActorSid AND [SecretId]=@SecretId
              AND [ActionScope]=@AccessAction AND [Status]=N'Approved'
              AND [AuthorizationExpiresAtUtc]>@NowUtc
              AND [AuthorizationTokenHash]=HASHBYTES(N'SHA2_512',@AuthorizationToken);
            IF @@ROWCOUNT=1 SET @Authorized=1;
        END;

        IF @Authorized=0 AND @MfaEnabled=1
        BEGIN
            DECLARE @GrantId uniqueidentifier;
            SELECT TOP (1) @GrantId=[GrantId]
            FROM [tb_security].[MfaBreakGlassGrants] WITH (UPDLOCK,READPAST,ROWLOCK)
            WHERE [TargetWindowsSid]=@ActorSid AND [SecretId]=@SecretId
              AND [ActionScope]=@AccessAction AND [RemainingUses]=1
              AND [RevokedAtUtc] IS NULL AND [ConsumedAtUtc] IS NULL
              AND [ExpiresAtUtc]>@NowUtc
            ORDER BY [CreatedAtUtc];
            IF @GrantId IS NOT NULL
            BEGIN
                UPDATE [tb_security].[MfaBreakGlassGrants]
                SET [RemainingUses]=0,[ConsumedAtUtc]=@NowUtc
                WHERE [GrantId]=@GrantId AND [RemainingUses]=1;
                IF @@ROWCOUNT=1
                BEGIN
                    SET @Authorized=1;
                    SET @UsedBreakGlass=1;
                END;
            END;
        END;

        IF @Authorized<>1
            THROW 52440,N'AuthPoint approval is required. Start a new request from the Client Info beta.',1;

        OPEN SYMMETRIC KEY [tb_ClientSecretKey]
            DECRYPTION BY CERTIFICATE [tb_ClientSecretCertificate];
        SELECT
            @CredentialId=secret.[CredentialId],@ClientId=credential.[ClientId],
            @CredentialName=credential.[Name],@SecretType=secret.[SecretType],
            @SecretLabel=secret.[SecretLabel],
            @SecretValue=CONVERT(nvarchar(max),DecryptByKey(
                secret.[ValueEncrypted],1,@Authenticator)),
            @SecretRowVersion=secret.[RowVersion]
        FROM [tb_client].[CredentialSecrets] AS secret
        INNER JOIN [tb_client].[Credentials] AS credential
            ON credential.[CredentialId]=secret.[CredentialId]
        WHERE secret.[SecretId]=@SecretId AND secret.[IsCurrent]=1
          AND credential.[IsActive]=1;
        IF @@ROWCOUNT<>1
        BEGIN
            CLOSE SYMMETRIC KEY [tb_ClientSecretKey];
            THROW 52393,N'The requested client secret was not found.',1;
        END;
        CLOSE SYMMETRIC KEY [tb_ClientSecretKey];

        DECLARE @AuditAction nvarchar(80)=CASE
            WHEN @UsedBreakGlass=1 THEN N'ClientSecretBreakGlassUsed'
            WHEN @AccessAction=N'Copy' THEN N'ClientSecretCopied'
            ELSE N'ClientSecretRevealed' END;
        DECLARE @AuditEntityId nvarchar(120)=CONVERT(nvarchar(120),@SecretId);
        EXEC [tb_security].[WriteAuditEvent]
            @Action=@AuditAction,@EntityType=N'ClientCredentialSecret',
            @EntityId=@AuditEntityId,@RequestId=@RequestId;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        BEGIN TRY
            CLOSE SYMMETRIC KEY [tb_ClientSecretKey];
        END TRY BEGIN CATCH END CATCH;
        THROW;
    END CATCH;

    SELECT @SecretId AS [SecretId],@CredentialId AS [CredentialId],
           @ClientId AS [ClientId],@CredentialName AS [CredentialName],
           @SecretType AS [SecretType],@SecretLabel AS [SecretLabel],
           @SecretValue AS [SecretValue],@SecretRowVersion AS [RowVersion];
END;
GO

PRINT N'WatchGuard AuthPoint MFA procedures installed.';
GO
