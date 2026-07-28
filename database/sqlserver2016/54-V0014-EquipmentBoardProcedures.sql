:ON ERROR EXIT

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

ALTER PROCEDURE [tb_app].[GetRepositoryCapabilities]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@UserSid OUTPUT,
        @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;

    SELECT
        CONVERT(int, 15) AS [SchemaVersion],
        CONVERT(bit, 0) AS [FullTextSearchAvailable],
        CONVERT(bit, 1) AS [SupportsTickets],
        CONVERT(bit, 1) AS [SupportsWorkEntries],
        CONVERT(bit, 1) AS [SupportsPrivateNotes],
        CONVERT(bit, 1) AS [SupportsPostingLeases],
        CONVERT(bit, 1) AS [SupportsSyncLeases],
        CONVERT(bit, 1) AS [SupportsImports],
        CONVERT(bit, 1) AS [SupportsTechBenchV1Import],
        CONVERT(bit, 1) AS [SupportsServerSageSync],
        CONVERT(bit, 1) AS [SupportsAdminUserPreview],
        CONVERT(bit, 1) AS [SupportsFireDrillCredentials],
        CONVERT(bit, 1) AS [EquipmentBoardAvailable];
END;
GO

IF OBJECT_ID(N'tb_app.AdminGetInventoryClients', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminGetInventoryClients];
GO

CREATE PROCEDURE [tb_app].[AdminGetInventoryClients]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,
        @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 52213, N'Only a TechBench Admin may view inventory clients and users.', 1;

    SELECT
        client.[Id] AS [ClientId],
        client.[Name] AS [ClientName],
        COALESCE(
            NULLIF(client_user.[LocationName], N''),
            NULLIF(client.[WhdLocationName], N''),
            client.[Name]) AS [PrimaryLocation],
        client_user.[ClientUserId],
        client_user.[DisplayName] AS [ClientUserDisplayName],
        client_user.[RoleDepartment],
        client_user.[Email],
        client_user.[Phone],
        client_user.[LocationName],
        client_user.[IsActive]
    FROM [tb_data].[Clients] AS client
    LEFT JOIN [tb_inventory].[ClientUsers] AS client_user
        ON client_user.[ClientId] = client.[Id]
       AND client_user.[IsActive] = 1
    WHERE client.[IsActive] = 1
    ORDER BY
        client.[Name],
        client_user.[DisplayName],
        client_user.[ClientUserId];
END;
GO

IF OBJECT_ID(N'tb_app.GetEquipmentInventory', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[GetEquipmentInventory];
GO

CREATE PROCEDURE [tb_app].[GetEquipmentInventory]
    @ClientId int = NULL,
    @ClientUserId bigint = NULL,
    @ClientName nvarchar(240) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF USER_NAME() = N'tb_preview_reader'
        THROW 52250, N'Equipment inventory is unavailable in Admin user-preview mode.', 1;

    DECLARE @Sid varbinary(85), @Login nvarchar(256), @Display nvarchar(160),
            @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser] @Sid OUTPUT, @Login OUTPUT, @Display OUTPUT,
        @Tech OUTPUT, @Manager OUTPUT, @Admin OUTPUT, @Sync OUTPUT;

    SET @ClientId = NULLIF(@ClientId, 0);
    SET @ClientUserId = NULLIF(@ClientUserId, 0);
    SET @ClientName = NULLIF(LTRIM(RTRIM(@ClientName)), N'');

    IF @ClientId IS NULL AND @ClientUserId IS NULL AND @ClientName IS NULL
        THROW 52251, N'A client or client user is required to read equipment inventory.', 1;

    SELECT
        equipment.[EquipmentId],
        equipment.[AssetTag],
        equipment.[DeviceType],
        equipment.[Name],
        equipment.[SerialNumber],
        equipment.[PartNumber],
        equipment.[IpAddress],
        equipment.[Manufacturer],
        equipment.[Model],
        equipment.[AnyDeskNumber],
        CAST(N'' AS nvarchar(max)) AS [AnyDeskPassword],
        equipment.[ClientId],
        COALESCE(client.[Name], equipment.[ClientName]) AS [ClientName],
        equipment.[ClientUserId],
        client_user.[DisplayName] AS [ClientUserDisplayName],
        client_user.[Email] AS [ClientUserEmail],
        equipment.[LocationName],
        equipment.[Notes],
        equipment.[WorkflowStage],
        user_row.[LoginName] AS [AssignedToLoginName],
        user_row.[DisplayName] AS [AssignedToDisplayName],
        equipment.[SortOrder],
        equipment.[AssignedAtUtc],
        equipment.[CreatedAtUtc],
        equipment.[UpdatedAtUtc],
        equipment.[RowVersion]
    FROM [tb_inventory].[Equipment] AS equipment
    LEFT JOIN [tb_security].[Users] AS user_row
        ON user_row.[WindowsSid] = equipment.[AssignedToWindowsSid]
    LEFT JOIN [tb_data].[Clients] AS client
        ON client.[Id] = equipment.[ClientId]
    LEFT JOIN [tb_inventory].[ClientUsers] AS client_user
        ON client_user.[ClientUserId] = equipment.[ClientUserId]
    WHERE equipment.[IsArchived] = 0
      AND (@ClientId IS NULL OR equipment.[ClientId] = @ClientId)
      AND (@ClientUserId IS NULL OR equipment.[ClientUserId] = @ClientUserId)
      AND
      (
          @ClientName IS NULL
          OR COALESCE(client.[Name], equipment.[ClientName]) = @ClientName
      )
    ORDER BY
        equipment.[DeviceType],
        equipment.[Name],
        equipment.[AssetTag],
        equipment.[EquipmentId];
END;
GO

IF OBJECT_ID(N'tb_app.AdminGetEquipmentBoard', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminGetEquipmentBoard];
GO

IF OBJECT_ID(N'tb_app.AdminGetEquipmentBoardSecure', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminGetEquipmentBoardSecure];
GO

CREATE PROCEDURE [tb_app].[AdminGetEquipmentBoardSecure]
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SELECT
        equipment.[EquipmentId],
        equipment.[AssetTag],
        equipment.[DeviceType],
        equipment.[Name],
        equipment.[SerialNumber],
        equipment.[PartNumber],
        equipment.[IpAddress],
        equipment.[Manufacturer],
        equipment.[Model],
        equipment.[AnyDeskNumber],
        CONVERT(nvarchar(max), DecryptByKeyAutoCert(
            CERT_ID(N'tb_FireDrillCredentialCertificate'),
            NULL,
            equipment.[AnyDeskPasswordEncrypted],
            1,
            CONVERT(nvarchar(20), equipment.[EquipmentId]))) AS [AnyDeskPassword],
        equipment.[ClientId],
        COALESCE(client.[Name], equipment.[ClientName]) AS [ClientName],
        equipment.[ClientUserId],
        client_user.[DisplayName] AS [ClientUserDisplayName],
        client_user.[Email] AS [ClientUserEmail],
        equipment.[LocationName],
        equipment.[Notes],
        equipment.[WorkflowStage],
        user_row.[LoginName] AS [AssignedToLoginName],
        user_row.[DisplayName] AS [AssignedToDisplayName],
        equipment.[SortOrder],
        equipment.[AssignedAtUtc],
        equipment.[CreatedAtUtc],
        equipment.[UpdatedAtUtc],
        equipment.[RowVersion]
    FROM [tb_inventory].[Equipment] AS equipment
    LEFT JOIN [tb_security].[Users] AS user_row
        ON user_row.[WindowsSid] = equipment.[AssignedToWindowsSid]
    LEFT JOIN [tb_data].[Clients] AS client
        ON client.[Id] = equipment.[ClientId]
    LEFT JOIN [tb_inventory].[ClientUsers] AS client_user
        ON client_user.[ClientUserId] = equipment.[ClientUserId]
    WHERE equipment.[IsArchived] = 0
    ORDER BY
        CASE equipment.[WorkflowStage]
            WHEN N'Stock' THEN 0
            WHEN N'Assigned' THEN 1
            ELSE 2
        END,
        user_row.[DisplayName],
        user_row.[LoginName],
        equipment.[SortOrder],
        equipment.[EquipmentId];
END;
GO

CREATE PROCEDURE [tb_app].[AdminGetEquipmentBoard]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,
        @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 52200, N'Only a TechBench Admin may view the equipment board.', 1;

    EXEC [tb_app].[AdminGetEquipmentBoardSecure];
END;
GO

IF OBJECT_ID(N'tb_security.EncryptEquipmentAnyDeskPassword', N'P') IS NOT NULL
    DROP PROCEDURE [tb_security].[EncryptEquipmentAnyDeskPassword];
GO

CREATE PROCEDURE [tb_security].[EncryptEquipmentAnyDeskPassword]
    @EquipmentId bigint,
    @PlainText nvarchar(max),
    @EncryptedValue varbinary(max) OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @EncryptedValue = NULL;
    IF @PlainText IS NULL
        RETURN;

    DECLARE @OpenedHere bit = CONVERT(bit, CASE WHEN EXISTS
    (
        SELECT 1
        FROM sys.openkeys
        WHERE [key_name] = N'tb_FireDrillCredentialKey'
    ) THEN 0 ELSE 1 END);

    BEGIN TRY
        IF @OpenedHere = 1
            OPEN SYMMETRIC KEY [tb_FireDrillCredentialKey]
                DECRYPTION BY CERTIFICATE [tb_FireDrillCredentialCertificate];

        SET @EncryptedValue = EncryptByKey(
            Key_GUID(N'tb_FireDrillCredentialKey'),
            CONVERT(varbinary(max), @PlainText),
            1,
            CONVERT(nvarchar(20), @EquipmentId));

        IF @OpenedHere = 1
            CLOSE SYMMETRIC KEY [tb_FireDrillCredentialKey];
    END TRY
    BEGIN CATCH
        IF EXISTS
        (
            SELECT 1
            FROM sys.openkeys
            WHERE [key_name] = N'tb_FireDrillCredentialKey'
        ) AND @OpenedHere = 1
            CLOSE SYMMETRIC KEY [tb_FireDrillCredentialKey];
        THROW;
    END CATCH;

    IF @EncryptedValue IS NULL
        THROW 52219, N'The AnyDesk password could not be encrypted.', 1;
END;
GO

IF OBJECT_ID(N'tb_app.AdminSaveEquipment', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminSaveEquipment];
GO

CREATE PROCEDURE [tb_app].[AdminSaveEquipment]
    @EquipmentId bigint = NULL,
    @AssetTag nvarchar(80) = NULL,
    @DeviceType nvarchar(80),
    @Name nvarchar(180),
    @SerialNumber nvarchar(120) = NULL,
    @PartNumber nvarchar(120) = NULL,
    @IpAddress nvarchar(80) = NULL,
    @Manufacturer nvarchar(120) = NULL,
    @Model nvarchar(120) = NULL,
    @AnyDeskNumber nvarchar(80) = NULL,
    @AnyDeskPassword nvarchar(max) = NULL,
    @ClientId int = NULL,
    @ClientUserId bigint = NULL,
    @LocationName nvarchar(240) = NULL,
    @Notes nvarchar(max) = NULL,
    @ExpectedRowVersion binary(8) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,
        @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 52201, N'Only a TechBench Admin may save equipment.', 1;

    SET @EquipmentId = NULLIF(@EquipmentId, 0);
    SET @AssetTag = NULLIF(LTRIM(RTRIM(@AssetTag)), N'');
    SET @DeviceType = NULLIF(LTRIM(RTRIM(@DeviceType)), N'');
    SET @Name = NULLIF(LTRIM(RTRIM(@Name)), N'');
    SET @SerialNumber = NULLIF(LTRIM(RTRIM(@SerialNumber)), N'');
    SET @PartNumber = NULLIF(LTRIM(RTRIM(@PartNumber)), N'');
    SET @IpAddress = NULLIF(LTRIM(RTRIM(@IpAddress)), N'');
    SET @Manufacturer = NULLIF(LTRIM(RTRIM(@Manufacturer)), N'');
    SET @Model = NULLIF(LTRIM(RTRIM(@Model)), N'');
    SET @AnyDeskNumber = NULLIF(LTRIM(RTRIM(@AnyDeskNumber)), N'');
    SET @AnyDeskPassword = NULLIF(@AnyDeskPassword, N'');
    SET @ClientId = NULLIF(@ClientId, 0);
    SET @ClientUserId = NULLIF(@ClientUserId, 0);
    SET @LocationName = NULLIF(LTRIM(RTRIM(@LocationName)), N'');
    SET @Notes = NULLIF(LTRIM(RTRIM(@Notes)), N'');

    IF @DeviceType IS NULL OR @Name IS NULL
        THROW 52202, N'Device type and equipment name are required.', 1;

    IF @ClientId IS NOT NULL
       AND NOT EXISTS
       (
           SELECT 1
           FROM [tb_data].[Clients]
           WHERE [Id] = @ClientId AND [IsActive] = 1
       )
        THROW 52214, N'The selected client is not available for inventory assignment.', 1;

    IF @ClientUserId IS NOT NULL
       AND NOT EXISTS
       (
           SELECT 1
           FROM [tb_inventory].[ClientUsers]
           WHERE [ClientUserId] = @ClientUserId
             AND [ClientId] = @ClientId
             AND [IsActive] = 1
       )
        THROW 52215, N'The selected client user does not belong to the selected client.', 1;

    DECLARE @ClientName nvarchar(240) =
    (
        SELECT [Name]
        FROM [tb_data].[Clients]
        WHERE [Id] = @ClientId
    );
    DECLARE @AnyDeskPasswordEncrypted varbinary(max);

    BEGIN TRY
    BEGIN TRANSACTION;

    IF @EquipmentId IS NULL
    BEGIN
        DECLARE @NextStockOrder int =
        (
            SELECT COALESCE(MAX([SortOrder]), -10) + 10
            FROM [tb_inventory].[Equipment] WITH (UPDLOCK, HOLDLOCK)
            WHERE [IsArchived] = 0
              AND [WorkflowStage] = N'Stock'
        );

        INSERT INTO [tb_inventory].[Equipment]
        (
            [AssetTag], [DeviceType], [Name], [SerialNumber], [PartNumber], [IpAddress],
            [Manufacturer], [Model], [ClientId], [ClientName], [ClientUserId],
            [LocationName], [Notes], [WorkflowStage], [SortOrder],
            [CreatedByWindowsSid], [UpdatedByWindowsSid]
        )
        VALUES
        (
            @AssetTag, @DeviceType, @Name, @SerialNumber, @PartNumber, @IpAddress,
            @Manufacturer, @Model, @ClientId, @ClientName, @ClientUserId,
            @LocationName, @Notes, N'Stock', @NextStockOrder,
            @ActorSid, @ActorSid
        );

        SET @EquipmentId = SCOPE_IDENTITY();

        EXEC [tb_security].[EncryptEquipmentAnyDeskPassword]
            @EquipmentId=@EquipmentId,
            @PlainText=@AnyDeskPassword,
            @EncryptedValue=@AnyDeskPasswordEncrypted OUTPUT;

        UPDATE [tb_inventory].[Equipment]
        SET
            [AnyDeskNumber] = @AnyDeskNumber,
            [AnyDeskPasswordEncrypted] = @AnyDeskPasswordEncrypted
        WHERE [EquipmentId] = @EquipmentId;

        INSERT INTO [tb_inventory].[EquipmentAssignmentHistory]
        (
            [EquipmentId], [EventType], [WorkflowStage],
            [AssignedToWindowsSid], [ClientId], [ClientUserId],
            [LocationName], [Notes], [ChangedByWindowsSid]
        )
        VALUES
        (
            @EquipmentId, N'Created', N'Stock',
            NULL, @ClientId, @ClientUserId,
            @LocationName, N'Equipment record created.', @ActorSid
        );
    END
    ELSE
    BEGIN
        DECLARE
            @PreviousClientId int,
            @PreviousClientUserId bigint,
            @PreviousLocationName nvarchar(240),
            @PreviousWorkflowStage nvarchar(24),
            @PreviousAssignedToWindowsSid varbinary(85);

        SELECT
            @PreviousClientId = [ClientId],
            @PreviousClientUserId = [ClientUserId],
            @PreviousLocationName = [LocationName],
            @PreviousWorkflowStage = [WorkflowStage],
            @PreviousAssignedToWindowsSid = [AssignedToWindowsSid]
        FROM [tb_inventory].[Equipment]
        WHERE [EquipmentId] = @EquipmentId
          AND [IsArchived] = 0;

        EXEC [tb_security].[EncryptEquipmentAnyDeskPassword]
            @EquipmentId=@EquipmentId,
            @PlainText=@AnyDeskPassword,
            @EncryptedValue=@AnyDeskPasswordEncrypted OUTPUT;

        UPDATE [tb_inventory].[Equipment]
        SET
            [AssetTag]=@AssetTag,
            [DeviceType]=@DeviceType,
            [Name]=@Name,
            [SerialNumber]=@SerialNumber,
            [PartNumber]=@PartNumber,
            [IpAddress]=@IpAddress,
            [Manufacturer]=@Manufacturer,
            [Model]=@Model,
            [AnyDeskNumber]=@AnyDeskNumber,
            [AnyDeskPasswordEncrypted]=@AnyDeskPasswordEncrypted,
            [ClientId]=@ClientId,
            [ClientName]=@ClientName,
            [ClientUserId]=@ClientUserId,
            [LocationName]=@LocationName,
            [Notes]=@Notes,
            [UpdatedByWindowsSid]=@ActorSid,
            [UpdatedAtUtc]=SYSUTCDATETIME()
        WHERE [EquipmentId]=@EquipmentId
          AND [IsArchived]=0
          AND (@ExpectedRowVersion IS NULL OR [RowVersion]=@ExpectedRowVersion);

        IF @@ROWCOUNT = 0
        BEGIN
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_inventory].[Equipment]
                WHERE [EquipmentId]=@EquipmentId AND [IsArchived]=0
            )
                THROW 52203, N'The equipment record no longer exists.', 1;
            THROW 52204, N'The equipment record changed on another workstation. Refresh and try again.', 1;
        END;

        IF ISNULL(@PreviousClientId, -1) <> ISNULL(@ClientId, -1)
           OR ISNULL(@PreviousClientUserId, -1) <> ISNULL(@ClientUserId, -1)
           OR ISNULL(@PreviousLocationName, N'') <> ISNULL(@LocationName, N'')
        BEGIN
            INSERT INTO [tb_inventory].[EquipmentAssignmentHistory]
            (
                [EquipmentId], [EventType], [WorkflowStage],
                [AssignedToWindowsSid], [ClientId], [ClientUserId],
                [LocationName], [Notes], [ChangedByWindowsSid]
            )
            VALUES
            (
                @EquipmentId, N'ClientAssignmentChanged', @PreviousWorkflowStage,
                @PreviousAssignedToWindowsSid, @ClientId, @ClientUserId,
                @LocationName, N'Client, user, or deployment location changed.', @ActorSid
            );
        END;
    END;

    COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT
        equipment.[EquipmentId],
        equipment.[AssetTag],
        equipment.[DeviceType],
        equipment.[Name],
        equipment.[SerialNumber],
        equipment.[PartNumber],
        equipment.[IpAddress],
        equipment.[Manufacturer],
        equipment.[Model],
        equipment.[AnyDeskNumber],
        @AnyDeskPassword AS [AnyDeskPassword],
        equipment.[ClientId],
        COALESCE(client.[Name], equipment.[ClientName]) AS [ClientName],
        equipment.[ClientUserId],
        client_user.[DisplayName] AS [ClientUserDisplayName],
        client_user.[Email] AS [ClientUserEmail],
        equipment.[LocationName],
        equipment.[Notes],
        equipment.[WorkflowStage],
        user_row.[LoginName] AS [AssignedToLoginName],
        user_row.[DisplayName] AS [AssignedToDisplayName],
        equipment.[SortOrder],
        equipment.[AssignedAtUtc],
        equipment.[CreatedAtUtc],
        equipment.[UpdatedAtUtc],
        equipment.[RowVersion]
    FROM [tb_inventory].[Equipment] AS equipment
    LEFT JOIN [tb_security].[Users] AS user_row
        ON user_row.[WindowsSid] = equipment.[AssignedToWindowsSid]
    LEFT JOIN [tb_data].[Clients] AS client
        ON client.[Id] = equipment.[ClientId]
    LEFT JOIN [tb_inventory].[ClientUsers] AS client_user
        ON client_user.[ClientUserId] = equipment.[ClientUserId]
    WHERE equipment.[EquipmentId] = @EquipmentId;
END;
GO

IF OBJECT_ID(N'tb_app.AdminGetEquipmentAssignmentHistory', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminGetEquipmentAssignmentHistory];
GO

CREATE PROCEDURE [tb_app].[AdminGetEquipmentAssignmentHistory]
    @EquipmentId bigint
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,
        @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 52216, N'Only a TechBench Admin may view equipment assignment history.', 1;

    SELECT
        history.[EquipmentAssignmentHistoryId],
        history.[EquipmentId],
        history.[EventType],
        history.[WorkflowStage],
        assigned_user.[LoginName] AS [AssignedToLoginName],
        assigned_user.[DisplayName] AS [AssignedToDisplayName],
        history.[ClientId],
        history.[ClientUserId],
        client.[Name] AS [ClientName],
        client_user.[DisplayName] AS [ClientUserDisplayName],
        history.[LocationName],
        history.[Notes],
        history.[ChangedAtUtc]
    FROM [tb_inventory].[EquipmentAssignmentHistory] AS history
    LEFT JOIN [tb_security].[Users] AS assigned_user
        ON assigned_user.[WindowsSid] = history.[AssignedToWindowsSid]
    LEFT JOIN [tb_data].[Clients] AS client
        ON client.[Id] = history.[ClientId]
    LEFT JOIN [tb_inventory].[ClientUsers] AS client_user
        ON client_user.[ClientUserId] = history.[ClientUserId]
    WHERE history.[EquipmentId] = @EquipmentId
    ORDER BY
        history.[ChangedAtUtc] DESC,
        history.[EquipmentAssignmentHistoryId] DESC;
END;
GO

IF OBJECT_ID(N'tb_app.AdminMoveEquipment', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminMoveEquipment];
GO

CREATE PROCEDURE [tb_app].[AdminMoveEquipment]
    @EquipmentId bigint,
    @TargetWindowsLoginName nvarchar(256) = NULL,
    @TargetWorkflowStage nvarchar(24),
    @TargetIndex int = NULL,
    @ExpectedRowVersion binary(8) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,
        @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 52205, N'Only a TechBench Admin may assign equipment.', 1;

    SET @TargetWindowsLoginName =
        NULLIF(LTRIM(RTRIM(@TargetWindowsLoginName)), N'');
    SET @TargetWorkflowStage =
        NULLIF(LTRIM(RTRIM(@TargetWorkflowStage)), N'');

    IF @TargetWorkflowStage IS NULL
       OR @TargetWorkflowStage NOT IN (N'Stock', N'Assigned', N'Deployment')
        THROW 52211, N'The selected equipment workflow stage is invalid.', 1;

    SET @TargetWorkflowStage =
        CASE
            WHEN @TargetWorkflowStage = N'Stock' THEN N'Stock'
            WHEN @TargetWorkflowStage = N'Assigned' THEN N'Assigned'
            ELSE N'Deployment'
        END;

    IF @TargetWorkflowStage = N'Assigned'
       AND @TargetWindowsLoginName IS NULL
        THROW 52212, N'A technician is required for the Assigned equipment stage.', 1;

    DECLARE @TargetSid varbinary(85) = NULL;
    IF @TargetWorkflowStage IN (N'Assigned', N'Deployment')
       AND @TargetWindowsLoginName IS NOT NULL
    BEGIN
        SELECT @TargetSid=[WindowsSid]
        FROM [tb_security].[Users]
        WHERE [LoginName]=@TargetWindowsLoginName
          AND [IsTechnician]=1;

        IF @TargetSid IS NULL
            THROW 52206, N'The selected TechBench user is not available for equipment assignment.', 1;
    END;

    BEGIN TRY
        BEGIN TRANSACTION;

        DECLARE
            @SourceSid varbinary(85),
            @SourceStage nvarchar(24),
            @SourceAssignedAtUtc datetime2(3),
            @SourceClientId int,
            @SourceClientUserId bigint,
            @SourceLocationName nvarchar(240);

        SELECT
            @SourceSid=[AssignedToWindowsSid],
            @SourceStage=[WorkflowStage],
            @SourceAssignedAtUtc=[AssignedAtUtc],
            @SourceClientId=[ClientId],
            @SourceClientUserId=[ClientUserId],
            @SourceLocationName=[LocationName]
        FROM [tb_inventory].[Equipment] WITH (UPDLOCK, HOLDLOCK)
        WHERE [EquipmentId]=@EquipmentId
          AND [IsArchived]=0
          AND (@ExpectedRowVersion IS NULL OR [RowVersion]=@ExpectedRowVersion);

        IF @@ROWCOUNT = 0
        BEGIN
            IF NOT EXISTS
            (
                SELECT 1
                FROM [tb_inventory].[Equipment]
                WHERE [EquipmentId]=@EquipmentId AND [IsArchived]=0
            )
                THROW 52207, N'The equipment record no longer exists.', 1;
            THROW 52208, N'The equipment assignment changed on another workstation. Refresh and try again.', 1;
        END;

        DECLARE @SameLane bit =
            CASE
                WHEN @SourceStage=@TargetWorkflowStage
                 AND
                 (
                     @SourceStage NOT IN (N'Assigned', N'Deployment')
                     OR @SourceSid=@TargetSid
                     OR (@SourceSid IS NULL AND @TargetSid IS NULL)
                 )
                    THEN 1
                ELSE 0
            END;

        DECLARE @TargetCount int =
        (
            SELECT COUNT(*)
            FROM [tb_inventory].[Equipment] WITH (UPDLOCK, HOLDLOCK)
            WHERE [IsArchived]=0
              AND [EquipmentId]<>@EquipmentId
              AND [WorkflowStage]=@TargetWorkflowStage
              AND
                  (
                      @TargetWorkflowStage NOT IN (N'Assigned', N'Deployment')
                      OR [AssignedToWindowsSid]=@TargetSid
                      OR ([AssignedToWindowsSid] IS NULL AND @TargetSid IS NULL)
                  )
        );

        SET @TargetIndex = COALESCE(@TargetIndex, @TargetCount);
        IF @TargetIndex < 0 SET @TargetIndex = 0;
        IF @TargetIndex > @TargetCount SET @TargetIndex = @TargetCount;

        ;WITH TargetPriority AS
        (
            SELECT
                [EquipmentId],
                [SortOrder],
                (ROW_NUMBER() OVER (ORDER BY [SortOrder], [EquipmentId]) - 1)
                    AS [ExistingIndex]
            FROM [tb_inventory].[Equipment]
            WHERE [IsArchived]=0
              AND [EquipmentId]<>@EquipmentId
              AND [WorkflowStage]=@TargetWorkflowStage
              AND
                  (
                      @TargetWorkflowStage NOT IN (N'Assigned', N'Deployment')
                      OR [AssignedToWindowsSid]=@TargetSid
                      OR ([AssignedToWindowsSid] IS NULL AND @TargetSid IS NULL)
                  )
        ),
        TargetPriorityWithGap AS
        (
            SELECT
                [EquipmentId],
                CASE
                    WHEN [ExistingIndex] >= @TargetIndex
                        THEN ([ExistingIndex] + 1) * 10
                    ELSE [ExistingIndex] * 10
                END AS [NewSortOrder]
            FROM TargetPriority
        )
        UPDATE equipment
        SET
            [SortOrder]=priority.[NewSortOrder],
            [UpdatedByWindowsSid]=@ActorSid,
            [UpdatedAtUtc]=SYSUTCDATETIME()
        FROM [tb_inventory].[Equipment] equipment
        INNER JOIN TargetPriorityWithGap priority
            ON priority.[EquipmentId]=equipment.[EquipmentId]
        WHERE equipment.[SortOrder]<>priority.[NewSortOrder];

        UPDATE [tb_inventory].[Equipment]
        SET
            [WorkflowStage]=@TargetWorkflowStage,
            [AssignedToWindowsSid]=
                CASE
                    WHEN @TargetWorkflowStage=N'Stock' THEN NULL
                    ELSE @TargetSid
                END,
            [SortOrder]=@TargetIndex * 10,
            [AssignedAtUtc]=
                CASE
                    WHEN @TargetWorkflowStage=N'Stock' THEN NULL
                    WHEN @SourceStage IN (N'Assigned', N'Deployment')
                     AND
                     (
                         @SourceSid=@TargetSid
                         OR (@SourceSid IS NULL AND @TargetSid IS NULL)
                     )
                        THEN @SourceAssignedAtUtc
                    ELSE SYSUTCDATETIME()
                END,
            [UpdatedByWindowsSid]=@ActorSid,
            [UpdatedAtUtc]=SYSUTCDATETIME()
        WHERE [EquipmentId]=@EquipmentId
          AND [IsArchived]=0;

        IF @SameLane = 0
           OR @SourceStage <> @TargetWorkflowStage
           OR ISNULL(@SourceSid, 0x) <> ISNULL(@TargetSid, 0x)
        BEGIN
            INSERT INTO [tb_inventory].[EquipmentAssignmentHistory]
            (
                [EquipmentId], [EventType], [WorkflowStage],
                [AssignedToWindowsSid], [ClientId], [ClientUserId],
                [LocationName], [Notes], [ChangedByWindowsSid]
            )
            VALUES
            (
                @EquipmentId, N'WorkflowMoved', @TargetWorkflowStage,
                CASE
                    WHEN @TargetWorkflowStage = N'Stock' THEN NULL
                    ELSE @TargetSid
                END,
                @SourceClientId, @SourceClientUserId,
                @SourceLocationName,
                N'Equipment moved between inventory workflow lanes.',
                @ActorSid
            );
        END;

        IF @SameLane=0
        BEGIN
            ;WITH SourcePriority AS
            (
                SELECT
                    [EquipmentId],
                    [SortOrder],
                    (ROW_NUMBER() OVER (ORDER BY [SortOrder], [EquipmentId]) - 1) * 10
                        AS [NewSortOrder]
                FROM [tb_inventory].[Equipment]
                WHERE [IsArchived]=0
                  AND [WorkflowStage]=@SourceStage
                  AND
                      (
                          @SourceStage NOT IN (N'Assigned', N'Deployment')
                          OR [AssignedToWindowsSid]=@SourceSid
                          OR ([AssignedToWindowsSid] IS NULL AND @SourceSid IS NULL)
                      )
            )
            UPDATE equipment
            SET
                [SortOrder]=priority.[NewSortOrder],
                [UpdatedByWindowsSid]=@ActorSid,
                [UpdatedAtUtc]=SYSUTCDATETIME()
            FROM [tb_inventory].[Equipment] equipment
            INNER JOIN SourcePriority priority
                ON priority.[EquipmentId]=equipment.[EquipmentId]
            WHERE equipment.[SortOrder]<>priority.[NewSortOrder];
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    EXEC [tb_app].[AdminGetEquipmentBoard];
END;
GO

IF OBJECT_ID(N'tb_app.AdminArchiveEquipment', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[AdminArchiveEquipment];
GO

CREATE PROCEDURE [tb_app].[AdminArchiveEquipment]
    @EquipmentId bigint,
    @ExpectedRowVersion binary(8) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActorSid varbinary(85), @IsManager bit, @IsAdmin bit, @IsSyncOperator bit;
    EXEC [tb_security].[GetCurrentAccess]
        @UserSid=@ActorSid OUTPUT,
        @IsManager=@IsManager OUTPUT,
        @IsAdmin=@IsAdmin OUTPUT,
        @IsSyncOperator=@IsSyncOperator OUTPUT;

    IF @IsAdmin <> 1 OR IS_ROLEMEMBER(N'tb_role_admin') <> 1
        THROW 52209, N'Only a TechBench Admin may archive equipment.', 1;

    INSERT INTO [tb_inventory].[EquipmentAssignmentHistory]
    (
        [EquipmentId], [EventType], [WorkflowStage],
        [AssignedToWindowsSid], [ClientId], [ClientUserId],
        [LocationName], [Notes], [ChangedByWindowsSid]
    )
    SELECT
        [EquipmentId], N'Archived', [WorkflowStage],
        [AssignedToWindowsSid], [ClientId], [ClientUserId],
        [LocationName], N'Equipment record archived.', @ActorSid
    FROM [tb_inventory].[Equipment]
    WHERE [EquipmentId]=@EquipmentId
      AND [IsArchived]=0
      AND (@ExpectedRowVersion IS NULL OR [RowVersion]=@ExpectedRowVersion);

    UPDATE [tb_inventory].[Equipment]
    SET
        [IsArchived]=1,
        [UpdatedByWindowsSid]=@ActorSid,
        [UpdatedAtUtc]=SYSUTCDATETIME()
    WHERE [EquipmentId]=@EquipmentId
      AND [IsArchived]=0
      AND (@ExpectedRowVersion IS NULL OR [RowVersion]=@ExpectedRowVersion);

    IF @@ROWCOUNT = 0
        THROW 52210, N'The equipment record changed or no longer exists. Refresh and try again.', 1;
END;
GO

IF OBJECT_ID(N'tb_service.ApplyCredentialsClientUserSnapshot', N'P') IS NOT NULL
    DROP PROCEDURE [tb_service].[ApplyCredentialsClientUserSnapshot];
GO

CREATE PROCEDURE [tb_service].[ApplyCredentialsClientUserSnapshot]
    @RequestId uniqueidentifier,
    @LeaseId uniqueidentifier,
    @WorkerId uniqueidentifier,
    @RowsJson nvarchar(max),
    @SourceModifiedAtUtc datetime2(3),
    @SyncedAtUtc datetime2(3)
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF ISJSON(@RowsJson) <> 1
        THROW 52220, N'The Credentials client-user snapshot is not valid JSON.', 1;

    CREATE TABLE #People
    (
        [SourceKey] nvarchar(500) NOT NULL PRIMARY KEY,
        [ClientName] nvarchar(240) NOT NULL,
        [DisplayName] nvarchar(240) NOT NULL,
        [RoleDepartment] nvarchar(240) NULL,
        [Email] nvarchar(320) NULL,
        [LocationName] nvarchar(240) NULL,
        [IsActive] bit NOT NULL,
        [RowHash] binary(32) NULL,
        [AccountsJson] nvarchar(max) NOT NULL,
        [ClientId] int NULL
    );

    INSERT INTO #People
    (
        [SourceKey], [ClientName], [DisplayName], [RoleDepartment],
        [Email], [LocationName], [IsActive], [RowHash], [AccountsJson]
    )
    SELECT
        LTRIM(RTRIM(source_row.[SourceKey])),
        LTRIM(RTRIM(source_row.[ClientName])),
        LTRIM(RTRIM(source_row.[DisplayName])),
        NULLIF(LTRIM(RTRIM(source_row.[RoleDepartment])), N''),
        NULLIF(LTRIM(RTRIM(source_row.[Email])), N''),
        NULLIF(LTRIM(RTRIM(source_row.[LocationName])), N''),
        source_row.[IsActive],
        TRY_CONVERT(binary(32), source_row.[RowHashHex], 2),
        source_row.[AccountsJson]
    FROM OPENJSON(@RowsJson)
    WITH
    (
        [SourceKey] nvarchar(500) N'$.sourceKey',
        [ClientName] nvarchar(240) N'$.clientName',
        [DisplayName] nvarchar(240) N'$.displayName',
        [RoleDepartment] nvarchar(240) N'$.roleDepartment',
        [Email] nvarchar(320) N'$.email',
        [LocationName] nvarchar(240) N'$.locationName',
        [IsActive] bit N'$.isActive',
        [RowHashHex] nvarchar(64) N'$.rowHashHex',
        [AccountsJson] nvarchar(max) N'$.accounts' AS JSON
    ) source_row;

    IF NOT EXISTS (SELECT 1 FROM #People)
        THROW 52221, N'The Client Users worksheet contained no people; existing data was not changed.', 1;
    IF EXISTS
    (
        SELECT 1
        FROM #People
        WHERE LEN([SourceKey])=0 OR LEN([ClientName])=0
           OR LEN([DisplayName])=0 OR [RowHash] IS NULL
           OR ISJSON([AccountsJson])<>1
    )
        THROW 52222, N'A Client Users person row is invalid.', 1;

    UPDATE person_row
    SET [ClientId]=client_match.[Id]
    FROM #People person_row
    CROSS APPLY
    (
        SELECT TOP (1) client.[Id]
        FROM [tb_data].[Clients] client
        WHERE client.[IsActive]=1
          AND
          (
              LOWER(LTRIM(RTRIM(client.[Name])))
                  = LOWER(LTRIM(RTRIM(person_row.[ClientName])))
              OR LOWER(LTRIM(RTRIM(ISNULL(client.[WhdLocationName],N''))))
                  = LOWER(LTRIM(RTRIM(person_row.[ClientName])))
              OR LOWER(LTRIM(RTRIM(ISNULL(client.[SageCustomerName],N''))))
                  = LOWER(LTRIM(RTRIM(person_row.[ClientName])))
              OR EXISTS
              (
                  SELECT 1
                  FROM [tb_data].[ClientAliases] alias
                  WHERE alias.[ClientId]=client.[Id]
                    AND alias.[ScopeType]=N'Organization'
                    AND LOWER(LTRIM(RTRIM(alias.[Alias])))
                        = LOWER(LTRIM(RTRIM(person_row.[ClientName])))
              )
          )
        ORDER BY
            CASE
                WHEN LOWER(LTRIM(RTRIM(client.[Name])))
                     = LOWER(LTRIM(RTRIM(person_row.[ClientName]))) THEN 0
                WHEN LOWER(LTRIM(RTRIM(ISNULL(client.[WhdLocationName],N''))))
                     = LOWER(LTRIM(RTRIM(person_row.[ClientName]))) THEN 1
                WHEN LOWER(LTRIM(RTRIM(ISNULL(client.[SageCustomerName],N''))))
                     = LOWER(LTRIM(RTRIM(person_row.[ClientName]))) THEN 2
                ELSE 3
            END,
            client.[Id]
    ) client_match;

    IF EXISTS (SELECT 1 FROM #People WHERE [ClientId] IS NULL)
        THROW 52223, N'One or more Client Users rows could not be matched to a TechBench client. Add an organization client alias or correct the Client value; no data was changed.', 1;

    CREATE TABLE #Accounts
    (
        [SourceKey] nvarchar(500) NOT NULL PRIMARY KEY,
        [PersonSourceKey] nvarchar(500) NOT NULL,
        [AccountSystem] nvarchar(240) NOT NULL,
        [RowHash] binary(32) NULL,
        [FieldsJson] nvarchar(max) NOT NULL
    );

    INSERT INTO #Accounts
        ([SourceKey], [PersonSourceKey], [AccountSystem], [RowHash], [FieldsJson])
    SELECT
        LTRIM(RTRIM(account_row.[SourceKey])),
        person_row.[SourceKey],
        LTRIM(RTRIM(account_row.[AccountSystem])),
        TRY_CONVERT(binary(32), account_row.[RowHashHex], 2),
        account_row.[FieldsJson]
    FROM #People person_row
    CROSS APPLY OPENJSON(person_row.[AccountsJson])
    WITH
    (
        [SourceKey] nvarchar(500) N'$.sourceKey',
        [AccountSystem] nvarchar(240) N'$.accountSystem',
        [RowHashHex] nvarchar(64) N'$.rowHashHex',
        [FieldsJson] nvarchar(max) N'$.fields' AS JSON
    ) account_row;

    IF EXISTS
    (
        SELECT 1
        FROM #Accounts
        WHERE LEN([SourceKey])=0 OR LEN([AccountSystem])=0
           OR [RowHash] IS NULL OR ISJSON([FieldsJson])<>1
    )
        THROW 52224, N'A Client Users account row is invalid.', 1;

    CREATE TABLE #AccountFields
    (
        [AccountSourceKey] nvarchar(500) NOT NULL,
        [FieldKey] nvarchar(200) NOT NULL,
        [FieldLabel] nvarchar(200) NOT NULL,
        [SortOrder] int NOT NULL,
        [FieldValue] nvarchar(3000) NULL,
        CONSTRAINT [PK_CredentialsClientUserFields]
            PRIMARY KEY ([AccountSourceKey], [FieldKey]),
        CONSTRAINT [UQ_CredentialsClientUserFieldOrder]
            UNIQUE ([AccountSourceKey], [SortOrder])
    );

    INSERT INTO #AccountFields
        ([AccountSourceKey], [FieldKey], [FieldLabel], [SortOrder], [FieldValue])
    SELECT account_row.[SourceKey],
        LTRIM(RTRIM(field_row.[FieldKey])),
        LTRIM(RTRIM(field_row.[FieldLabel])),
        field_row.[SortOrder],
        field_row.[FieldValue]
    FROM #Accounts account_row
    CROSS APPLY OPENJSON(account_row.[FieldsJson])
    WITH
    (
        [FieldKey] nvarchar(200) N'$.fieldKey',
        [FieldLabel] nvarchar(200) N'$.label',
        [SortOrder] int N'$.sortOrder',
        [FieldValue] nvarchar(3000) N'$.value'
    ) field_row;

    IF EXISTS
    (
        SELECT 1 FROM #AccountFields
        WHERE LEN([FieldKey])=0 OR LEN([FieldLabel])=0 OR [SortOrder]<1
    )
        THROW 52225, N'A Client Users account field is invalid.', 1;

    DECLARE @ActorSid varbinary(85)=SUSER_SID(ORIGINAL_LOGIN());
    IF @ActorSid IS NULL
       OR NOT EXISTS
       (
           SELECT 1
           FROM [tb_security].[Users]
           WHERE [WindowsSid]=@ActorSid
             AND [LoginName]=CONVERT(nvarchar(256),ORIGINAL_LOGIN())
       )
        THROW 52226, N'The TechBench sync service actor is not registered.', 1;

    DECLARE @UserReadCount int=(SELECT COUNT(*) FROM #People),
            @UserSavedCount int=0,
            @UserStaleCount int=0,
            @AccountReadCount int=(SELECT COUNT(*) FROM #Accounts),
            @AccountSavedCount int=0,
            @AccountStaleCount int=0;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [tb_sync].[FireDrillSyncLeases]
            WHERE [RequestId]=@RequestId
              AND [LeaseId]=@LeaseId
              AND [WorkerId]=@WorkerId
              AND [ExpiresAtUtc]>SYSUTCDATETIME()
        )
            THROW 52227, N'The Credentials synchronization lease is no longer valid.', 1;

        UPDATE target
        SET [ClientId]=source_row.[ClientId],
            [DisplayName]=source_row.[DisplayName],
            [RoleDepartment]=source_row.[RoleDepartment],
            [Email]=source_row.[Email],
            [LocationName]=source_row.[LocationName],
            [SourceRowHash]=source_row.[RowHash],
            [IsActive]=source_row.[IsActive],
            [LastSyncedAtUtc]=@SyncedAtUtc,
            [UpdatedByWindowsSid]=@ActorSid,
            [UpdatedAtUtc]=SYSUTCDATETIME()
        FROM [tb_inventory].[ClientUsers] target
        INNER JOIN #People source_row
            ON target.[SourceSystem]=N'CredentialsWorkbook'
           AND target.[SourceKey]=source_row.[SourceKey]
        WHERE target.[SourceRowHash]<>source_row.[RowHash]
           OR target.[SourceRowHash] IS NULL
           OR target.[IsActive]<>source_row.[IsActive]
           OR target.[ClientId]<>source_row.[ClientId];
        SET @UserSavedCount=@@ROWCOUNT;

        INSERT INTO [tb_inventory].[ClientUsers]
        (
            [ClientId], [DisplayName], [RoleDepartment], [Email],
            [LocationName], [SourceSystem], [SourceKey], [SourceRowHash],
            [IsActive], [LastSyncedAtUtc],
            [CreatedByWindowsSid], [UpdatedByWindowsSid]
        )
        SELECT source_row.[ClientId], source_row.[DisplayName],
            source_row.[RoleDepartment], source_row.[Email],
            source_row.[LocationName], N'CredentialsWorkbook',
            source_row.[SourceKey], source_row.[RowHash],
            source_row.[IsActive], @SyncedAtUtc, @ActorSid, @ActorSid
        FROM #People source_row
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM [tb_inventory].[ClientUsers] target
            WHERE target.[SourceSystem]=N'CredentialsWorkbook'
              AND target.[SourceKey]=source_row.[SourceKey]
        );
        SET @UserSavedCount+=@@ROWCOUNT;

        UPDATE target
        SET [IsActive]=0,
            [LastSyncedAtUtc]=@SyncedAtUtc,
            [UpdatedByWindowsSid]=@ActorSid,
            [UpdatedAtUtc]=SYSUTCDATETIME()
        FROM [tb_inventory].[ClientUsers] target
        WHERE target.[SourceSystem]=N'CredentialsWorkbook'
          AND target.[IsActive]=1
          AND NOT EXISTS
          (
              SELECT 1 FROM #People source_row
              WHERE source_row.[SourceKey]=target.[SourceKey]
          );
        SET @UserStaleCount=@@ROWCOUNT;

        CREATE TABLE #ChangedAccounts
        (
            [SourceKey] nvarchar(500) NOT NULL PRIMARY KEY
        );

        INSERT INTO #ChangedAccounts([SourceKey])
        SELECT source_row.[SourceKey]
        FROM #Accounts source_row
        LEFT JOIN [tb_inventory].[ClientUserAccounts] target
            ON target.[SourceKey]=source_row.[SourceKey]
        WHERE target.[ClientUserAccountId] IS NULL
           OR target.[SourceRowHash]<>source_row.[RowHash]
           OR target.[IsCurrent]=0;
        SET @AccountSavedCount=@@ROWCOUNT;

        UPDATE target
        SET [ClientUserId]=client_user.[ClientUserId],
            [AccountSystem]=source_row.[AccountSystem],
            [SourceRowHash]=source_row.[RowHash],
            [SourceModifiedAtUtc]=@SourceModifiedAtUtc,
            [LastSyncedAtUtc]=@SyncedAtUtc,
            [IsCurrent]=1,
            [UpdatedByWindowsSid]=@ActorSid,
            [UpdatedAtUtc]=SYSUTCDATETIME()
        FROM [tb_inventory].[ClientUserAccounts] target
        INNER JOIN #Accounts source_row
            ON source_row.[SourceKey]=target.[SourceKey]
        INNER JOIN #ChangedAccounts changed_row
            ON changed_row.[SourceKey]=source_row.[SourceKey]
        INNER JOIN [tb_inventory].[ClientUsers] client_user
            ON client_user.[SourceSystem]=N'CredentialsWorkbook'
           AND client_user.[SourceKey]=source_row.[PersonSourceKey];

        INSERT INTO [tb_inventory].[ClientUserAccounts]
        (
            [ClientUserId], [AccountSystem], [SourceKey], [SourceRowHash],
            [SourceModifiedAtUtc], [LastSyncedAtUtc], [IsCurrent],
            [CreatedByWindowsSid], [UpdatedByWindowsSid]
        )
        SELECT client_user.[ClientUserId], source_row.[AccountSystem],
            source_row.[SourceKey], source_row.[RowHash],
            @SourceModifiedAtUtc, @SyncedAtUtc, 1, @ActorSid, @ActorSid
        FROM #Accounts source_row
        INNER JOIN [tb_inventory].[ClientUsers] client_user
            ON client_user.[SourceSystem]=N'CredentialsWorkbook'
           AND client_user.[SourceKey]=source_row.[PersonSourceKey]
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM [tb_inventory].[ClientUserAccounts] target
            WHERE target.[SourceKey]=source_row.[SourceKey]
        );

        DELETE stored_field
        FROM [tb_inventory].[ClientUserAccountFields] stored_field
        INNER JOIN [tb_inventory].[ClientUserAccounts] account
            ON account.[ClientUserAccountId]=stored_field.[ClientUserAccountId]
        INNER JOIN #ChangedAccounts changed_row
            ON changed_row.[SourceKey]=account.[SourceKey];

        OPEN SYMMETRIC KEY [tb_FireDrillCredentialKey]
            DECRYPTION BY CERTIFICATE [tb_FireDrillCredentialCertificate];

        INSERT INTO [tb_inventory].[ClientUserAccountFields]
            ([ClientUserAccountId], [FieldKey], [FieldLabel], [SortOrder], [ValueEncrypted])
        SELECT account.[ClientUserAccountId], source_field.[FieldKey],
            source_field.[FieldLabel], source_field.[SortOrder],
            CASE WHEN source_field.[FieldValue] IS NULL THEN NULL ELSE
                EncryptByKey
                (
                    Key_GUID(N'tb_FireDrillCredentialKey'),
                    CONVERT(varbinary(max),source_field.[FieldValue]),
                    1,
                    CONVERT
                    (
                        nvarchar(64),
                        HASHBYTES
                        (
                            'SHA2_256',
                            CONVERT
                            (
                                varbinary(max),
                                CONVERT(nvarchar(30),account.[ClientUserAccountId])
                                    + N'|' + source_field.[FieldKey]
                            )
                        ),
                        2
                    )
                )
            END
        FROM #AccountFields source_field
        INNER JOIN #ChangedAccounts changed_row
            ON changed_row.[SourceKey]=source_field.[AccountSourceKey]
        INNER JOIN [tb_inventory].[ClientUserAccounts] account
            ON account.[SourceKey]=source_field.[AccountSourceKey];

        IF EXISTS
        (
            SELECT 1
            FROM #AccountFields source_field
            INNER JOIN #ChangedAccounts changed_row
                ON changed_row.[SourceKey]=source_field.[AccountSourceKey]
            INNER JOIN [tb_inventory].[ClientUserAccounts] account
                ON account.[SourceKey]=source_field.[AccountSourceKey]
            INNER JOIN [tb_inventory].[ClientUserAccountFields] stored_field
                ON stored_field.[ClientUserAccountId]=account.[ClientUserAccountId]
               AND stored_field.[FieldKey]=source_field.[FieldKey]
            WHERE source_field.[FieldValue] IS NOT NULL
              AND stored_field.[ValueEncrypted] IS NULL
        )
            THROW 52228, N'A Client Users account field could not be encrypted.', 1;

        CLOSE SYMMETRIC KEY [tb_FireDrillCredentialKey];

        UPDATE target
        SET [IsCurrent]=0,
            [LastSyncedAtUtc]=@SyncedAtUtc,
            [UpdatedByWindowsSid]=@ActorSid,
            [UpdatedAtUtc]=SYSUTCDATETIME()
        FROM [tb_inventory].[ClientUserAccounts] target
        INNER JOIN [tb_inventory].[ClientUsers] client_user
            ON client_user.[ClientUserId]=target.[ClientUserId]
           AND client_user.[SourceSystem]=N'CredentialsWorkbook'
        WHERE target.[IsCurrent]=1
          AND NOT EXISTS
          (
              SELECT 1 FROM #Accounts source_row
              WHERE source_row.[SourceKey]=target.[SourceKey]
          );
        SET @AccountStaleCount=@@ROWCOUNT;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF EXISTS
        (
            SELECT 1 FROM sys.openkeys
            WHERE [key_name]=N'tb_FireDrillCredentialKey'
        )
            CLOSE SYMMETRIC KEY [tb_FireDrillCredentialKey];
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT @UserReadCount AS [UserReadCount],
        @UserSavedCount AS [UserSavedCount],
        @UserStaleCount AS [UserStaleCount],
        @AccountReadCount AS [AccountReadCount],
        @AccountSavedCount AS [AccountSavedCount],
        @AccountStaleCount AS [AccountStaleCount];
END;
GO

IF OBJECT_ID(N'tb_app.SearchClientUsers', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[SearchClientUsers];
GO

CREATE PROCEDURE [tb_app].[SearchClientUsers]
    @ClientId int = NULL,
    @Search nvarchar(240) = NULL,
    @Limit int = 500
AS
BEGIN
    SET NOCOUNT ON;
    IF USER_NAME() = N'tb_preview_reader'
        THROW 52240, N'Client users are unavailable in Admin user-preview mode.', 1;

    DECLARE @Sid varbinary(85), @Login nvarchar(256), @Display nvarchar(160),
            @Tech bit, @Manager bit, @Admin bit, @Sync bit;
    EXEC [tb_security].[EnsureCurrentUser] @Sid OUTPUT, @Login OUTPUT, @Display OUTPUT,
        @Tech OUTPUT, @Manager OUTPUT, @Admin OUTPUT, @Sync OUTPUT;

    SET @Search=NULLIF(LTRIM(RTRIM(@Search)), N'');
    SET @Limit=CASE WHEN @Limit IS NULL OR @Limit<1 THEN 500
                    WHEN @Limit>1000 THEN 1000 ELSE @Limit END;

    SELECT TOP (@Limit)
        client_user.[ClientUserId],
        client_user.[ClientId],
        client.[Name] AS [ClientName],
        client_user.[DisplayName],
        COALESCE(client_user.[RoleDepartment], N'') AS [RoleDepartment],
        COALESCE(client_user.[Email], N'') AS [Email],
        COALESCE(client_user.[Phone], N'') AS [Phone],
        COALESCE(client_user.[LocationName], N'') AS [LocationName],
        COALESCE(client_user.[LastSyncedAtUtc], client_user.[UpdatedAtUtc]) AS [LastSyncedAtUtc],
        (
            SELECT COUNT(*)
            FROM [tb_inventory].[ClientUserAccounts] account
            WHERE account.[ClientUserId]=client_user.[ClientUserId]
              AND account.[IsCurrent]=1
        ) AS [AccountCount],
        COALESCE
        (
            (
                SELECT account.[AccountSystem] AS [name],
                    CONVERT(int, ROW_NUMBER() OVER
                        (ORDER BY account.[AccountSystem], account.[ClientUserAccountId]))
                        AS [sortOrder],
                    JSON_QUERY
                    (
                        COALESCE
                        (
                            (
                                SELECT field.[FieldLabel] AS [label],
                                    field.[FieldKey] AS [fieldName],
                                    field.[SortOrder] AS [sortOrder],
                                    CONVERT(nvarchar(1), N'') AS [value]
                                FROM [tb_inventory].[ClientUserAccountFields] field
                                WHERE field.[ClientUserAccountId]=account.[ClientUserAccountId]
                                ORDER BY field.[SortOrder], field.[FieldKey]
                                FOR JSON PATH
                            ),
                            N'[]'
                        )
                    ) AS [fields]
                FROM [tb_inventory].[ClientUserAccounts] account
                WHERE account.[ClientUserId]=client_user.[ClientUserId]
                  AND account.[IsCurrent]=1
                ORDER BY account.[AccountSystem], account.[ClientUserAccountId]
                FOR JSON PATH
            ),
            N'[]'
        ) AS [AccountsJson]
    FROM [tb_inventory].[ClientUsers] client_user
    INNER JOIN [tb_data].[Clients] client
        ON client.[Id]=client_user.[ClientId]
       AND client.[IsActive]=1
    WHERE client_user.[IsActive]=1
      AND (@ClientId IS NULL OR client_user.[ClientId]=@ClientId)
      AND
      (
          @Search IS NULL
          OR client.[Name] LIKE N'%' + @Search + N'%'
          OR client_user.[DisplayName] LIKE N'%' + @Search + N'%'
          OR client_user.[Email] LIKE N'%' + @Search + N'%'
          OR client_user.[RoleDepartment] LIKE N'%' + @Search + N'%'
          OR client_user.[LocationName] LIKE N'%' + @Search + N'%'
      )
    ORDER BY client.[Name], client_user.[DisplayName], client_user.[ClientUserId];
END;
GO

IF OBJECT_ID(N'tb_app.RevealClientUser', N'P') IS NOT NULL
    DROP PROCEDURE [tb_app].[RevealClientUser];
GO

CREATE PROCEDURE [tb_app].[RevealClientUser]
    @ClientUserId bigint
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF SESSION_CONTEXT(N'TechBench.PreviewSessionId') IS NOT NULL
        THROW 52241, N'Client users are unavailable in Admin user-preview mode.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [tb_inventory].[ClientUsers]
        WHERE [ClientUserId]=@ClientUserId AND [IsActive]=1
    )
        THROW 52242, N'The client user was not found or is no longer current.', 1;

    SELECT client_user.[ClientUserId],
        client_user.[ClientId],
        client.[Name] AS [ClientName],
        client_user.[DisplayName],
        COALESCE(client_user.[RoleDepartment], N'') AS [RoleDepartment],
        COALESCE(client_user.[Email], N'') AS [Email],
        COALESCE(client_user.[Phone], N'') AS [Phone],
        COALESCE(client_user.[LocationName], N'') AS [LocationName],
        COALESCE(client_user.[LastSyncedAtUtc], client_user.[UpdatedAtUtc]) AS [LastSyncedAtUtc],
        (
            SELECT COUNT(*)
            FROM [tb_inventory].[ClientUserAccounts] account
            WHERE account.[ClientUserId]=client_user.[ClientUserId]
              AND account.[IsCurrent]=1
        ) AS [AccountCount],
        COALESCE
        (
            (
                SELECT account.[AccountSystem] AS [name],
                    CONVERT(int, ROW_NUMBER() OVER
                        (ORDER BY account.[AccountSystem], account.[ClientUserAccountId]))
                        AS [sortOrder],
                    JSON_QUERY
                    (
                        COALESCE
                        (
                            (
                                SELECT field.[FieldLabel] AS [label],
                                    field.[FieldKey] AS [fieldName],
                                    field.[SortOrder] AS [sortOrder],
                                    COALESCE
                                    (
                                        CONVERT
                                        (
                                            nvarchar(max),
                                            DecryptByKeyAutoCert
                                            (
                                                CERT_ID(N'tb_FireDrillCredentialCertificate'),
                                                NULL,
                                                field.[ValueEncrypted],
                                                1,
                                                CONVERT
                                                (
                                                    nvarchar(64),
                                                    HASHBYTES
                                                    (
                                                        'SHA2_256',
                                                        CONVERT
                                                        (
                                                            varbinary(max),
                                                            CONVERT(nvarchar(30),account.[ClientUserAccountId])
                                                                + N'|' + field.[FieldKey]
                                                        )
                                                    ),
                                                    2
                                                )
                                            )
                                        ),
                                        N''
                                    ) AS [value]
                                FROM [tb_inventory].[ClientUserAccountFields] field
                                WHERE field.[ClientUserAccountId]=account.[ClientUserAccountId]
                                ORDER BY field.[SortOrder], field.[FieldKey]
                                FOR JSON PATH
                            ),
                            N'[]'
                        )
                    ) AS [fields]
                FROM [tb_inventory].[ClientUserAccounts] account
                WHERE account.[ClientUserId]=client_user.[ClientUserId]
                  AND account.[IsCurrent]=1
                ORDER BY account.[AccountSystem], account.[ClientUserAccountId]
                FOR JSON PATH
            ),
            N'[]'
        ) AS [AccountsJson]
    FROM [tb_inventory].[ClientUsers] client_user
    INNER JOIN [tb_data].[Clients] client
        ON client.[Id]=client_user.[ClientId]
       AND client.[IsActive]=1
    WHERE client_user.[ClientUserId]=@ClientUserId
      AND client_user.[IsActive]=1;
END;
GO

PRINT N'TechBench V0014 equipment-board procedures created.';
GO
