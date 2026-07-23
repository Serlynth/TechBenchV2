:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

GRANT EXECUTE ON OBJECT::[tb_app].[AdminGetRecentClientSessionResponses]
    TO [tb_role_admin];

REVOKE EXECUTE ON OBJECT::[tb_app].[AdminGetRecentClientSessionResponses]
    FROM [tb_preview_reader];

PRINT N'TechBench V0011 client response grants applied.';
GO
