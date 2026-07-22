:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @FailureCount int = 0;
DECLARE @InstalledSchemaVersion int =
(
    SELECT MAX([SchemaVersion])
    FROM [tb_deploy].[SchemaMigrations]
);

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.OperationalStorage.0002'
      AND [SchemaVersion] = 2
)
BEGIN
    PRINT N'FAIL: OperationalStorage.0002 migration marker is missing.';
    SET @FailureCount += 1;
END;

IF @InstalledSchemaVersion NOT IN (2, 3, 4, 5, 6, 7, 8)
BEGIN
    PRINT N'FAIL: V0002 verification supports installed schema version 2, 3, 4, 5, 6, 7, or 8.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredTables TABLE
(
    [ObjectName] nvarchar(300) NOT NULL PRIMARY KEY
);

INSERT INTO @RequiredTables([ObjectName])
VALUES
    (N'tb_data.TicketStatusOptions'),
    (N'tb_data.Tickets'),
    (N'tb_data.WorkEntries'),
    (N'tb_private.WorkEntryPersonalNotes'),
    (N'tb_data.WorkEntryLinks'),
    (N'tb_user.EditorDrafts'),
    (N'tb_data.Templates'),
    (N'tb_data.CommonLinks'),
    (N'tb_data.OrganizationSettings'),
    (N'tb_user.UserSettings'),
    (N'tb_data.ClientAliases'),
    (N'tb_data.ClientExternalIdentities'),
    (N'tb_ops.PostingLogs'),
    (N'tb_ops.PostingAttempts'),
    (N'tb_ops.PostingLeases'),
    (N'tb_ops.SyncLeases'),
    (N'tb_ops.SyncRuns'),
    (N'tb_ops.ImportBatches'),
    (N'tb_ops.LegacyIdMappings');

DECLARE @MissingTableCount int =
(
    SELECT COUNT(*)
    FROM @RequiredTables AS required_table
    WHERE OBJECT_ID(required_table.[ObjectName], N'U') IS NULL
);

IF @MissingTableCount > 0
BEGIN
    PRINT N'FAIL: One or more V0002 tables are missing.';
    SET @FailureCount += @MissingTableCount;
END;

IF OBJECT_ID(N'tb_user.DeviceSettings', N'U') IS NOT NULL
BEGIN
    PRINT N'FAIL: Device-specific settings must remain workstation-local, not in SQL Server.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'tb_data.CommonLinks')
      AND [name] = N'UrlHash'
      AND [is_computed] = 0
      AND [is_nullable] = 0
      AND TYPE_NAME([user_type_id]) = N'binary'
      AND [max_length] = 32
)
BEGIN
    PRINT N'FAIL: CommonLinks lacks its stored, bounded SHA-256 URL index key.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'[UrlHash]',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.EnsureWorkspaceDefaults'))) = 0
   OR CHARINDEX(
       N'[UrlHash]',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveCommonLink'))) = 0
BEGIN
    PRINT N'FAIL: Common-link writers do not maintain the stored URL hash.';
    SET @FailureCount += 1;
END;

DECLARE @RequiredProcedures TABLE
(
    [ObjectName] nvarchar(300) NOT NULL PRIMARY KEY
);

INSERT INTO @RequiredProcedures([ObjectName])
VALUES
    (N'tb_security.RenewSyncRunLease'),
    (N'tb_app.GetRepositoryCapabilities'),
    (N'tb_app.EnsureWorkspaceDefaults'),
    (N'tb_app.SearchTickets'),
    (N'tb_app.GetTicket'),
    (N'tb_app.SaveTicket'),
    (N'tb_app.GetTicketStatusOptions'),
    (N'tb_app.SyncUpsertTicketStatusOption'),
    (N'tb_app.SyncUpsertTicket'),
    (N'tb_app.SyncUpsertClient'),
    (N'tb_app.SyncUpsertSageCustomer'),
    (N'tb_app.SyncRemoveStaleSageCustomers'),
    (N'tb_app.AdminSaveExternalMapping'),
    (N'tb_app.AdminMergeClients'),
    (N'tb_app.ReconcileClientMatches'),
    (N'tb_app.SearchWorkEntries'),
    (N'tb_app.GetWorkEntry'),
    (N'tb_app.GetDistinctTags'),
    (N'tb_app.SaveWorkEntry'),
    (N'tb_app.DeleteWorkEntry'),
    (N'tb_app.GetWorkEntryLinks'),
    (N'tb_app.SaveWorkEntryLink'),
    (N'tb_app.DeleteWorkEntryLink'),
    (N'tb_app.GetTemplates'),
    (N'tb_app.SaveTemplate'),
    (N'tb_app.DeleteTemplate'),
    (N'tb_app.AdminSaveTemplate'),
    (N'tb_app.AdminDeleteTemplate'),
    (N'tb_app.GetEditorDraft'),
    (N'tb_app.SaveEditorDraft'),
    (N'tb_app.DeleteEditorDraft'),
    (N'tb_app.GetClientAliases'),
    (N'tb_app.SaveClientAlias'),
    (N'tb_app.DeleteClientAlias'),
    (N'tb_app.AdminSaveClientAlias'),
    (N'tb_app.AdminDeleteClientAlias'),
    (N'tb_app.GetCommonLinks'),
    (N'tb_app.SaveCommonLink'),
    (N'tb_app.DeleteCommonLink'),
    (N'tb_app.AdminSaveCommonLink'),
    (N'tb_app.AdminDeleteCommonLink'),
    (N'tb_app.GetSettings'),
    (N'tb_app.SaveUserSetting'),
    (N'tb_app.DeleteUserSetting'),
    (N'tb_app.SaveSetting'),
    (N'tb_app.DeleteSetting'),
    (N'tb_app.AddPostingLog'),
    (N'tb_app.GetLatestVerifiedWhdPostingLog'),
    (N'tb_app.BeginPostingAttempt'),
    (N'tb_app.HeartbeatPostingAttempt'),
    (N'tb_app.GetOutstandingPostingAttempt'),
    (N'tb_app.CompletePostingAttempt'),
    (N'tb_app.ResolveOutstandingPostingAttempts'),
    (N'tb_app.MarkWorkEntryPosted'),
    (N'tb_app.AbandonOutstandingPostingAttempts'),
    (N'tb_app.HasSuccessfulSageDraftLog'),
    (N'tb_app.GetPostingLogs'),
    (N'tb_app.AcquireSyncLease'),
    (N'tb_app.ReleaseSyncLease'),
    (N'tb_app.BeginSyncRun'),
    (N'tb_app.CompleteSyncRun'),
    (N'tb_app.SyncApplyClientSnapshot'),
    (N'tb_app.SyncApplyTicketSnapshot'),
    (N'tb_app.SyncApplyTicketStatusSnapshot'),
    (N'tb_app.SyncApplySageCustomerSnapshot'),
    (N'tb_app.BeginImportBatch'),
    (N'tb_app.AddImportLegacyMapping'),
    (N'tb_app.CompleteImportBatch');

DECLARE @MissingProcedureCount int =
(
    SELECT COUNT(*)
    FROM @RequiredProcedures AS required_procedure
    WHERE OBJECT_ID(required_procedure.[ObjectName], N'P') IS NULL
);

IF @MissingProcedureCount > 0
BEGIN
    PRINT N'FAIL: One or more V0002 stored procedures are missing.';
    SET @FailureCount += @MissingProcedureCount;
END;

DECLARE @RequiredRowVersionTables TABLE
(
    [ObjectName] nvarchar(300) NOT NULL PRIMARY KEY
);

INSERT INTO @RequiredRowVersionTables([ObjectName])
VALUES
    (N'tb_data.TicketStatusOptions'),
    (N'tb_data.Tickets'),
    (N'tb_data.WorkEntries'),
    (N'tb_private.WorkEntryPersonalNotes'),
    (N'tb_data.WorkEntryLinks'),
    (N'tb_user.EditorDrafts'),
    (N'tb_data.Templates'),
    (N'tb_data.CommonLinks'),
    (N'tb_data.OrganizationSettings'),
    (N'tb_user.UserSettings'),
    (N'tb_data.ClientAliases'),
    (N'tb_data.ClientExternalIdentities'),
    (N'tb_ops.PostingAttempts'),
    (N'tb_ops.PostingLeases'),
    (N'tb_ops.SyncLeases'),
    (N'tb_ops.SyncRuns'),
    (N'tb_ops.ImportBatches');

DECLARE @MissingRowVersionCount int =
(
    SELECT COUNT(*)
    FROM @RequiredRowVersionTables AS required_table
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE [object_id] = OBJECT_ID(required_table.[ObjectName])
          AND [name] = N'RowVersion'
          AND [system_type_id] = 189
    )
);

IF @MissingRowVersionCount > 0
BEGIN
    PRINT N'FAIL: One or more mutable V0002 tables lack a rowversion column.';
    SET @FailureCount += @MissingRowVersionCount;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.parameters
    WHERE [object_id] = OBJECT_ID(N'tb_app.SaveWorkEntry')
      AND [name] IN
      (
          N'@WhdPosted',
          N'@WhdPostedAtUtc',
          N'@SagePosted',
          N'@SagePostedAtUtc',
          N'@SageTicketNumber'
      )
)
BEGIN
    PRINT N'FAIL: SaveWorkEntry accepts authoritative posting-state parameters.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.parameters
    WHERE [object_id] = OBJECT_ID(N'tb_app.SaveWorkEntry')
      AND [name] = N'@LastError'
)
BEGIN
    PRINT N'FAIL: SaveWorkEntry cannot persist client-reported posting errors.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'IF @ExistingSagePosted = 1',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveWorkEntry'))) = 0
BEGIN
    PRINT N'FAIL: SaveWorkEntry does not block updates after Sage posting.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'IF @WhdPosted = 1 OR @SagePosted = 1',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteWorkEntry'))) = 0
BEGIN
    PRINT N'FAIL: DeleteWorkEntry does not block deletion after WHD or Sage posting.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'FROM [tb_ops].[PostingAttempts] WITH (UPDLOCK, HOLDLOCK)',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveWorkEntry'))) = 0
   OR CHARINDEX(
       N'FROM [tb_ops].[PostingLeases] WITH (UPDLOCK, HOLDLOCK)',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveWorkEntry'))) = 0
   OR CHARINDEX(
       N'FROM [tb_ops].[PostingAttempts] WITH (UPDLOCK, HOLDLOCK)',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteWorkEntry'))) = 0
   OR CHARINDEX(
       N'FROM [tb_ops].[PostingLeases] WITH (UPDLOCK, HOLDLOCK)',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteWorkEntry'))) = 0
BEGIN
    PRINT N'FAIL: Work entries can be edited or deleted while external posting coordination is active.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'IF @AffectedCount > 0',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.ResolveOutstandingPostingAttempts'))) = 0
BEGIN
    PRINT N'FAIL: Posting reconciliation can update authoritative state without an outstanding attempt.';
    SET @FailureCount += 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.parameters
    WHERE [object_id] = OBJECT_ID(N'tb_app.CompletePostingAttempt')
      AND [name] = N'@MarkPosted'
)
BEGIN
    PRINT N'FAIL: CompletePostingAttempt cannot distinguish successful drafts from completed posts.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'IF @Status = N''Succeeded'' AND @MarkPosted = 1',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'AND @MarkPosted = 0',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'WHEN [WhdPosted] = 1 THEN N''PostedToWhd''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'ELSE IF @Status <> N''Succeeded''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
BEGIN
    PRINT N'FAIL: CompletePostingAttempt does not finalize successful unposted Sage draft state safely.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'FROM [tb_ops].[PostingLogs] WITH (UPDLOCK, HOLDLOCK)',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'[Message] = COALESCE(@Message, N'''')',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'[ExternalReference] IS NULL AND @ExternalReference IS NULL',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
BEGIN
    PRINT N'FAIL: CompletePostingAttempt always duplicates a detailed client posting log.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'@NormalizedSageTicketNumber',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'UPPER(LEFT(@ExternalReference, 5)) = N''SAGE-''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'@NormalizedSageTicketNumber',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.ResolveOutstandingPostingAttempts'))) = 0
   OR CHARINDEX(
       N'UPPER(LEFT(@ExternalReference, 5)) = N''SAGE-''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.ResolveOutstandingPostingAttempts'))) = 0
BEGIN
    PRINT N'FAIL: Posting completion or reconciliation does not normalize Sage ticket references.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'FROM [tb_data].[WorkEntries] WITH (UPDLOCK, HOLDLOCK)',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.BeginPostingAttempt'))) = 0
   OR CHARINDEX(
       N'IF @SagePosted = 1',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.BeginPostingAttempt'))) = 0
   OR CHARINDEX(
       N'FROM [tb_data].[WorkEntries] WITH (UPDLOCK, HOLDLOCK)',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'IF @ExistingSagePosted = 1',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.CompletePostingAttempt'))) = 0
   OR CHARINDEX(
       N'FROM [tb_data].[WorkEntries] WITH (UPDLOCK, HOLDLOCK)',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.ResolveOutstandingPostingAttempts'))) = 0
   OR CHARINDEX(
       N'IF @SagePosted = 1',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.ResolveOutstandingPostingAttempts'))) = 0
BEGIN
    PRINT N'FAIL: A posting workflow can mutate an entry after Sage posting.';
    SET @FailureCount += 1;
END;

DECLARE @ManualPostedParameters TABLE
(
    [ParameterName] sysname NOT NULL PRIMARY KEY
);

INSERT INTO @ManualPostedParameters([ParameterName])
VALUES
    (N'@WorkEntryId'),
    (N'@Destination'),
    (N'@ExpectedRowVersion'),
    (N'@RequestId');

IF EXISTS
(
    SELECT 1
    FROM @ManualPostedParameters AS expected_parameter
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.parameters
        WHERE [object_id] = OBJECT_ID(N'tb_app.MarkWorkEntryPosted')
          AND [name] = expected_parameter.[ParameterName]
    )
)
BEGIN
    PRINT N'FAIL: MarkWorkEntryPosted is missing a required ownership/concurrency contract parameter.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'IF @SagePosted = 1',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.MarkWorkEntryPosted'))) = 0
   OR CHARINDEX(
       N'INSERT INTO [tb_ops].[PostingLogs]',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.MarkWorkEntryPosted'))) = 0
   OR CHARINDEX(
       N'[tb_security].[WriteAuditEvent]',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.MarkWorkEntryPosted'))) = 0
BEGIN
    PRINT N'FAIL: MarkWorkEntryPosted lacks immutable, posting-log, or audit enforcement.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'WHEN @LastError IS NOT NULL THEN N''Failed''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveWorkEntry'))) = 0
   OR CHARINDEX(
       N'WHEN [WhdPosted] = 1 AND [SagePosted] = 1 THEN N''PostedToBoth''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveWorkEntry'))) = 0
BEGIN
    PRINT N'FAIL: SaveWorkEntry does not safely derive PostingStatus from errors and posted flags.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'AND posting_lease.[ExpiresAtUtc] > @NowUtc',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.HeartbeatPostingAttempt'))) = 0
BEGIN
    PRINT N'FAIL: Posting heartbeats can revive an expired lease.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'ISJSON(posting_log.[Payload]) = 1',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetLatestVerifiedWhdPostingLog'))) = 0
   OR CHARINDEX(
       N'OPENJSON',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetLatestVerifiedWhdPostingLog'))) = 0
   OR CHARINDEX(
       N'payload_property.[key] = N''noteText''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.GetLatestVerifiedWhdPostingLog'))) = 0
BEGIN
    PRINT N'FAIL: Latest verified WHD posting lookup can prefer a completion marker over the JSON note snapshot.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'[SageCustomerId] =',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminMergeClients'))) = 0
   OR CHARINDEX(
       N'[MatchStatus] = N''Matched''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminMergeClients'))) = 0
BEGIN
    PRINT N'FAIL: Client merge does not preserve shared external metadata.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'WHEN [Source] = N''Both''',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SyncUpsertClient'))) = 0
BEGIN
    PRINT N'FAIL: Client synchronization does not preserve a merged client match.';
    SET @FailureCount += 1;
END;

DECLARE @WorkspaceDefaultTokens TABLE
(
    [Token] nvarchar(200) NOT NULL PRIMARY KEY
);

INSERT INTO @WorkspaceDefaultTokens([Token])
VALUES
    (N'watchguard-cloud'),
    (N'microsoft-365-admin'),
    (N'barracuda-cloud-control'),
    (N'eset-protect'),
    (N'email2phone'),
    (N'godaddy-dns'),
    (N'network-solutions-dns'),
    (N'Exchange certificate update'),
    (N'VPN troubleshooting'),
    (N'Microsoft 365 licensing'),
    (N'Firewall rule change'),
    (N'Password reset'),
    (N'Backup verification'),
    (N'Server reboot/maintenance');

DECLARE @MissingWorkspaceDefaultTokenCount int =
(
    SELECT COUNT(*)
    FROM @WorkspaceDefaultTokens AS expected_default
    WHERE CHARINDEX(
              expected_default.[Token],
              OBJECT_DEFINITION(OBJECT_ID(N'tb_app.EnsureWorkspaceDefaults'))) = 0
);

IF @MissingWorkspaceDefaultTokenCount > 0
BEGIN
    PRINT N'FAIL: EnsureWorkspaceDefaults does not contain every V1 workspace default.';
    SET @FailureCount += @MissingWorkspaceDefaultTokenCount;
END;

IF @InstalledSchemaVersion < 4
   AND
   (
       CHARINDEX(
           N'AND [BuiltInKey] IS NOT NULL',
           OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveCommonLink'))) = 0
       OR CHARINDEX(
           N'AND [BuiltInKey] IS NOT NULL',
           OBJECT_DEFINITION(OBJECT_ID(N'tb_app.DeleteCommonLink'))) = 0
   )
BEGIN
    PRINT N'FAIL: Built-in Common Links are not protected from edit/delete.';
    SET @FailureCount += 1;
END;

IF CHARINDEX(
       N'ELSE [LastSyncedAtUtc]',
       OBJECT_DEFINITION(OBJECT_ID(N'tb_app.SaveTicket'))) = 0
BEGIN
    PRINT N'FAIL: Technician ticket saves can overwrite synchronization timestamps.';
    SET @FailureCount += 1;
END;

DECLARE @SnapshotLeaseContracts TABLE
(
    [ProcedureName] nvarchar(300) NOT NULL PRIMARY KEY,
    [ExpectedSource] nvarchar(80) NOT NULL
);

INSERT INTO @SnapshotLeaseContracts([ProcedureName], [ExpectedSource])
VALUES
    (N'tb_app.SyncApplyClientSnapshot', N'WHD-Clients'),
    (N'tb_app.SyncApplyTicketStatusSnapshot', N'WHD-TicketStatuses'),
    (N'tb_app.SyncApplyTicketSnapshot', N'WHD-Tickets'),
    (N'tb_app.SyncApplySageCustomerSnapshot', N'Sage-Customers');

DECLARE @MissingSnapshotLeaseContractCount int =
(
    SELECT COUNT(*)
    FROM @SnapshotLeaseContracts AS contract
    WHERE CHARINDEX(
              N'[tb_ops].[SyncLeases]',
              OBJECT_DEFINITION(OBJECT_ID(contract.[ProcedureName]))) = 0
       OR CHARINDEX(
              N'[tb_security].[RenewSyncRunLease]',
              OBJECT_DEFINITION(OBJECT_ID(contract.[ProcedureName]))) = 0
       OR CHARINDEX(
              contract.[ExpectedSource],
              OBJECT_DEFINITION(OBJECT_ID(contract.[ProcedureName]))) = 0
);

IF @MissingSnapshotLeaseContractCount > 0
BEGIN
    PRINT N'FAIL: A snapshot procedure does not enforce its active source-specific lease.';
    SET @FailureCount += @MissingSnapshotLeaseContractCount;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.key_constraints
    WHERE [parent_object_id] = OBJECT_ID(N'tb_ops.PostingLeases')
      AND [type] = N'PK'
)
BEGIN
    PRINT N'FAIL: PostingLeases lacks its one-lease-per-work-entry/destination primary key.';
    SET @FailureCount += 1;
END;

DECLARE @ExpectedGrants TABLE
(
    [RoleName] sysname NOT NULL,
    [ObjectName] nvarchar(300) NOT NULL,
    PRIMARY KEY ([RoleName], [ObjectName])
);

INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
VALUES
    (N'tb_role_user', N'tb_app.SearchTickets'),
    (N'tb_role_user', N'tb_app.SaveTicket'),
    (N'tb_role_user', N'tb_app.SearchWorkEntries'),
    (N'tb_role_user', N'tb_app.SaveWorkEntry'),
    (N'tb_role_user', N'tb_app.GetEditorDraft'),
    (N'tb_role_user', N'tb_app.SaveUserSetting'),
    (N'tb_role_user', N'tb_app.GetPostingLogs'),
    (N'tb_role_user', N'tb_app.BeginPostingAttempt'),
    (N'tb_role_user', N'tb_app.MarkWorkEntryPosted'),
    (N'tb_role_user', N'tb_app.BeginImportBatch'),
    (N'tb_role_manager', N'tb_app.SearchWorkEntries'),
    (N'tb_role_admin', N'tb_app.AdminMergeClients'),
    (N'tb_role_admin', N'tb_app.AdminSaveOrganizationSetting');

IF @InstalledSchemaVersion < 4
BEGIN
    INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
    VALUES
        (N'tb_role_user', N'tb_app.EnsureWorkspaceDefaults'),
        (N'tb_role_user', N'tb_app.SaveTemplate'),
        (N'tb_role_user', N'tb_app.SaveCommonLink'),
        (N'tb_role_user', N'tb_app.SaveClientAlias'),
        (N'tb_role_sync_operator', N'tb_app.AcquireSyncLease'),
        (N'tb_role_sync_operator', N'tb_app.SyncApplyClientSnapshot'),
        (N'tb_role_sync_operator', N'tb_app.SyncApplyTicketSnapshot'),
        (N'tb_role_sync_operator', N'tb_app.SyncApplyTicketStatusSnapshot'),
        (N'tb_role_sync_operator', N'tb_app.SyncApplySageCustomerSnapshot');
END
ELSE
BEGIN
    INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
    VALUES
        (N'tb_role_admin', N'tb_app.EnsureWorkspaceDefaults'),
        (N'tb_role_admin', N'tb_app.SaveTemplate'),
        (N'tb_role_admin', N'tb_app.SaveCommonLink'),
        (N'tb_role_admin', N'tb_app.SaveClientAlias');

    /* V0007 moves organization-wide Sage snapshot application to the service. */
    IF @InstalledSchemaVersion < 7
    BEGIN
        INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
        VALUES
            (N'tb_role_admin', N'tb_app.AcquireSyncLease'),
            (N'tb_role_admin', N'tb_app.SyncApplySageCustomerSnapshot');
    END;

    /* V0006 moves organization-wide WHD snapshot application to the service. */
    IF @InstalledSchemaVersion < 6
    BEGIN
        INSERT INTO @ExpectedGrants([RoleName], [ObjectName])
        VALUES
            (N'tb_role_admin', N'tb_app.SyncApplyClientSnapshot'),
            (N'tb_role_admin', N'tb_app.SyncApplyTicketSnapshot'),
            (N'tb_role_admin', N'tb_app.SyncApplyTicketStatusSnapshot');
    END;
END;

DECLARE @MissingGrantCount int =
(
    SELECT COUNT(*)
    FROM @ExpectedGrants AS expected_grant
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.database_permissions AS permission
        INNER JOIN sys.database_principals AS grantee
            ON grantee.[principal_id] = permission.[grantee_principal_id]
        WHERE grantee.[name] = expected_grant.[RoleName]
          AND permission.[class] = 1
          AND permission.[major_id] = OBJECT_ID(expected_grant.[ObjectName])
          AND permission.[permission_name] = N'EXECUTE'
          AND permission.[state] IN (N'G', N'W')
    )
);

IF @MissingGrantCount > 0
BEGIN
    PRINT N'FAIL: One or more required V0002 role grants are missing.';
    SET @FailureCount += @MissingGrantCount;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.database_permissions AS permission
    INNER JOIN sys.database_principals AS grantee
        ON grantee.[principal_id] = permission.[grantee_principal_id]
    LEFT JOIN sys.objects AS secured_object
        ON permission.[class] = 1
       AND secured_object.[object_id] = permission.[major_id]
    LEFT JOIN sys.schemas AS secured_schema
        ON
        (
            permission.[class] = 3
            AND secured_schema.[schema_id] = permission.[major_id]
        )
        OR
        (
            permission.[class] = 1
            AND secured_schema.[schema_id] = secured_object.[schema_id]
        )
    WHERE grantee.[name] IN
        (
            N'tb_role_user',
            N'tb_role_manager',
            N'tb_role_admin',
            N'tb_role_sync_operator'
        )
      AND secured_schema.[name] IN
        (N'tb_data', N'tb_private', N'tb_user', N'tb_ops', N'tb_security', N'tb_audit')
      AND permission.[permission_name] IN
        (N'SELECT', N'INSERT', N'UPDATE', N'DELETE', N'CONTROL', N'ALTER')
      AND permission.[state] IN (N'G', N'W')
)
BEGIN
    PRINT N'FAIL: A TechBench application role has direct table or schema data permission.';
    SET @FailureCount += 1;
END;

IF @FailureCount > 0
BEGIN
    RAISERROR(
        N'TechBench V0002 verification failed with %d issue(s).',
        16,
        1,
        @FailureCount);
    RETURN;
END;

PRINT N'TechBench V0002 operational-storage verification passed.';

SELECT
    DB_NAME() AS [DatabaseName],
    MAX([SchemaVersion]) AS [SchemaVersion],
    MAX(CASE WHEN [MigrationId] = N'SqlServer2016.OperationalStorage.0002'
        THEN [AppliedAtUtc] END) AS [OperationalStorageAppliedAtUtc]
FROM [tb_deploy].[SchemaMigrations];
GO
