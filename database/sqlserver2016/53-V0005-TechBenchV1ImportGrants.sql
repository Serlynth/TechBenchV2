:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Every normal TechBench user may migrate their own V1 history. Each
    procedure derives ownership from ORIGINAL_LOGIN and exposes no owner
    override. No application role receives direct access to import tables.
*/
GRANT EXECUTE ON OBJECT::[tb_app].[BeginTechBenchV1Import]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[ResolveTechBenchV1Reference]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[ImportTechBenchV1WorkEntry]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[ImportTechBenchV1WorkEntryLink]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[ImportTechBenchV1PostingLog]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[CompleteTechBenchV1Import]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[AbandonTechBenchV1Import]
    TO [tb_role_user];

GRANT EXECUTE ON OBJECT::[tb_app].[GetRepositoryCapabilities]
    TO [tb_role_user];
GRANT EXECUTE ON OBJECT::[tb_app].[GetImportBatches]
    TO [tb_role_user];

PRINT N'TechBench V0005 owner-scoped TechBench V1 import grants applied.';
GO
