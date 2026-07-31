:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS
(
    SELECT 1
    FROM [tb_deploy].[SchemaMigrations]
    WHERE [MigrationId] = N'SqlServer2016.EquipmentAnyDesk.0015'
      AND [SchemaVersion] = 15
)
BEGIN
    RAISERROR(N'V0015 must be installed before the equipment deployment lifecycle extension.', 16, 1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS
    (
        SELECT 1
        FROM sys.check_constraints
        WHERE [parent_object_id] = OBJECT_ID(N'tb_inventory.Equipment', N'U')
          AND [name] = N'CK_Equipment_WorkflowStage'
    )
        ALTER TABLE [tb_inventory].[Equipment]
            DROP CONSTRAINT [CK_Equipment_WorkflowStage];

    ALTER TABLE [tb_inventory].[Equipment] WITH CHECK
        ADD CONSTRAINT [CK_Equipment_WorkflowStage]
            CHECK ([WorkflowStage] IN
                (N'Stock', N'Assigned', N'Deployment', N'Deployed'));

    IF EXISTS
    (
        SELECT 1
        FROM sys.check_constraints
        WHERE [parent_object_id] =
            OBJECT_ID(N'tb_inventory.EquipmentAssignmentHistory', N'U')
          AND [name] = N'CK_EquipmentAssignmentHistory_WorkflowStage'
    )
        ALTER TABLE [tb_inventory].[EquipmentAssignmentHistory]
            DROP CONSTRAINT [CK_EquipmentAssignmentHistory_WorkflowStage];

    ALTER TABLE [tb_inventory].[EquipmentAssignmentHistory] WITH CHECK
        ADD CONSTRAINT [CK_EquipmentAssignmentHistory_WorkflowStage]
            CHECK ([WorkflowStage] IN
                (N'Stock', N'Assigned', N'Deployment', N'Deployed'));

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'Schema-15-compatible equipment deployment lifecycle extension installed.';
GO
