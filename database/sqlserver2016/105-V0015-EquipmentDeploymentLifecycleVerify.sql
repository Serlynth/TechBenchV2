:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;

DECLARE @Failures int = 0;

DECLARE @EquipmentConstraint nvarchar(max) =
    COALESCE
    (
        (
            SELECT [definition]
            FROM sys.check_constraints
            WHERE [parent_object_id] =
                OBJECT_ID(N'tb_inventory.Equipment', N'U')
              AND [name] = N'CK_Equipment_WorkflowStage'
        ),
        N''
    );
DECLARE @HistoryConstraint nvarchar(max) =
    COALESCE
    (
        (
            SELECT [definition]
            FROM sys.check_constraints
            WHERE [parent_object_id] =
                OBJECT_ID(N'tb_inventory.EquipmentAssignmentHistory', N'U')
              AND [name] =
                N'CK_EquipmentAssignmentHistory_WorkflowStage'
        ),
        N''
    );

IF CHARINDEX(N'Deployed', @EquipmentConstraint) = 0
BEGIN
    PRINT N'FAIL: Equipment workflow constraint does not allow Deployed.';
    SET @Failures += 1;
END;

IF CHARINDEX(N'Deployed', @HistoryConstraint) = 0
BEGIN
    PRINT N'FAIL: Equipment history workflow constraint does not allow Deployed.';
    SET @Failures += 1;
END;

DECLARE @CapabilitiesDefinition nvarchar(max) =
    COALESCE
    (
        OBJECT_DEFINITION(
            OBJECT_ID(N'tb_app.GetRepositoryCapabilities', N'P')),
        N''
    );
IF CHARINDEX(
       N'CONVERT(int, 15) AS [SchemaVersion]',
       @CapabilitiesDefinition) = 0
BEGIN
    PRINT N'FAIL: Repository capabilities do not preserve schema version 15.';
    SET @Failures += 1;
END;

DECLARE @MoveDefinition nvarchar(max) =
    COALESCE
    (
        OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminMoveEquipment', N'P')),
        N''
    );
IF CHARINDEX(N'N''Deployed''', @MoveDefinition) = 0
   OR CHARINDEX(N'N''Equipment deployment completed.''', @MoveDefinition) = 0
BEGIN
    PRINT N'FAIL: Equipment move procedure does not persist and log deployed completion.';
    SET @Failures += 1;
END;

DECLARE @BoardDefinition nvarchar(max) =
    COALESCE
    (
        OBJECT_DEFINITION(OBJECT_ID(N'tb_app.AdminGetEquipmentBoard', N'P')),
        N''
    );
DECLARE @SecureBoardDefinition nvarchar(max) =
    COALESCE
    (
        OBJECT_DEFINITION(
            OBJECT_ID(N'tb_app.AdminGetEquipmentBoardSecure', N'P')),
        N''
    );
IF CHARINDEX(N'@IncludeDeployed', @BoardDefinition) = 0
   OR CHARINDEX(N'@IncludeDeployed', @SecureBoardDefinition) = 0
   OR CHARINDEX(N'[WorkflowStage] <> N''Deployed''', @SecureBoardDefinition) = 0
BEGIN
    PRINT N'FAIL: Stable-compatible equipment board filtering is not installed.';
    SET @Failures += 1;
END;

IF @Failures > 0
    THROW 52222, N'TechBench equipment deployment lifecycle verification failed.', 1;

PRINT N'TechBench schema-15-compatible equipment deployment lifecycle verification passed.';
GO
