:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.EquipmentAnyDesk.0015'
      AND [SchemaVersion] = 15
)
BEGIN
    RAISERROR(N'V0015 must be installed before the WHD local-delete extension.', 16, 1);
    RETURN;
END;

IF OBJECT_ID(N'tb_app.DeleteWorkEntry', N'P') IS NULL
BEGIN
    RAISERROR(N'tb_app.DeleteWorkEntry must exist before the WHD local-delete extension.', 16, 1);
    RETURN;
END;
GO

ALTER PROCEDURE [tb_app].[DeleteWorkEntry]
    @Id int,
    @ExpectedRowVersion binary(8),
    @RequestId uniqueidentifier = NULL,
    @ConfirmMissingWhdTechNote bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85);
    DECLARE @LoginName nvarchar(256);
    DECLARE @DisplayName nvarchar(160);
    DECLARE @IsTechnician bit;
    DECLARE @IsManager bit;
    DECLARE @IsAdmin bit;
    DECLARE @IsSyncOperator bit;
    DECLARE @WhdPosted bit;
    DECLARE @SagePosted bit;

    EXEC [tb_security].[EnsureCurrentUser]
        @UserSid = @UserSid OUTPUT,
        @LoginName = @LoginName OUTPUT,
        @DisplayName = @DisplayName OUTPUT,
        @IsTechnician = @IsTechnician OUTPUT,
        @IsManager = @IsManager OUTPUT,
        @IsAdmin = @IsAdmin OUTPUT,
        @IsSyncOperator = @IsSyncOperator OUTPUT;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT
            @WhdPosted = [WhdPosted],
            @SagePosted = [SagePosted]
        FROM [tb_data].[WorkEntries] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Id] = @Id
          AND [OwnerWindowsSid] = @UserSid
          AND [RowVersion] = @ExpectedRowVersion;

        IF @WhdPosted IS NULL
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM [tb_data].[WorkEntries] WHERE [Id] = @Id)
                THROW 51132, N'The work entry no longer exists.', 1;
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[WorkEntries]
                WHERE [Id] = @Id
                  AND [OwnerWindowsSid] = @UserSid
            )
                THROW 51133, N'Only the work-entry owner may delete it.', 1;
            THROW 51134, N'The work entry changed after it was loaded.', 1;
        END;

        IF @SagePosted = 1
            THROW 51138, N'A work entry posted to Sage cannot be deleted.', 1;

        IF @WhdPosted = 1
           AND @ConfirmMissingWhdTechNote <> 1
            THROW 51140, N'A WHD-posted entry requires explicit confirmation before its TechBench copy can be deleted.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM [tb_ops].[PostingAttempts] WITH (UPDLOCK, HOLDLOCK)
            WHERE [WorkEntryId] = @Id
              AND [OwnerWindowsSid] = @UserSid
              AND [Status] IN (N'Started', N'Unknown')
        )
        OR EXISTS
        (
            SELECT 1
            FROM [tb_ops].[PostingLeases] WITH (UPDLOCK, HOLDLOCK)
            WHERE [WorkEntryId] = @Id
              AND [OwnerWindowsSid] = @UserSid
        )
            THROW 51139, N'A work entry cannot be deleted while an external posting attempt is active.', 1;

        DELETE FROM [tb_data].[WorkEntryLinks]
        WHERE [SourceWorkEntryId] = @Id
           OR [TargetWorkEntryId] = @Id;

        DELETE FROM [tb_data].[WorkEntries]
        WHERE [Id] = @Id
          AND [OwnerWindowsSid] = @UserSid
          AND [RowVersion] = @ExpectedRowVersion;

        IF @@ROWCOUNT = 0
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM [tb_data].[WorkEntries] WHERE [Id] = @Id)
                THROW 51132, N'The work entry no longer exists.', 1;
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_data].[WorkEntries]
                WHERE [Id] = @Id
                  AND [OwnerWindowsSid] = @UserSid
            )
                THROW 51133, N'Only the work-entry owner may delete it.', 1;
            THROW 51134, N'The work entry changed after it was loaded.', 1;
        END;

        DECLARE @AuditEntityId nvarchar(120) = CONVERT(nvarchar(120), @Id);
        EXEC [tb_security].[WriteAuditEvent]
            @Action = N'WorkEntryDeleted',
            @EntityType = N'WorkEntry',
            @EntityId = @AuditEntityId,
            @RequestId = @RequestId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

PRINT N'Schema-15-compatible WHD local-delete extension installed.';
GO
