using TechBench.Controls;
using TechBench.Models;
using TechBench.Services;
using TechBench.ViewModels;

namespace TechBench.Tests;

public sealed class EquipmentBoardTests
{
    [Fact]
    public void LiveInventoryBoardLaunchesAnIsolatedLocalDemo()
    {
        var mainWindowXaml = ReadRepositoryFile("MainWindow.xaml");
        var mainWindowCode = ReadRepositoryFile("MainWindow.xaml.cs");

        Assert.Contains("Open Demo Mode", mainWindowXaml);
        Assert.Contains(
            "Click=\"OpenEquipmentBoardDemo_Click\"",
            mainWindowXaml);
        Assert.Contains(
            "new EquipmentBoardDemoWindow",
            mainWindowCode);
        Assert.Contains("demoWindow.ShowDialog();", mainWindowCode);
    }

    [Fact]
    public void LiveInventoryBoardUsesTransientEditorAndDragOnlyTechnicianControls()
    {
        var mainWindowXaml = ReadRepositoryFile("MainWindow.xaml");
        var mainWindowCode = ReadRepositoryFile("MainWindow.xaml.cs");
        var equipmentViewModel = ReadRepositoryFile(
            Path.Combine(
                "ViewModels",
                "MainWindowViewModel.Equipment.cs"));

        Assert.Contains(
            "Visibility=\"{Binding IsEquipmentInventoryEditorVisible, Converter={StaticResource BooleanToVisibilityConverter}}\"",
            mainWindowXaml);
        Assert.Contains("x:Name=\"InventoryEquipmentEditorPanel\"", mainWindowXaml);
        Assert.Contains("x:Name=\"EquipmentQuickViewPanel\"", mainWindowXaml);
        Assert.Contains(
            "CanEditEquipmentInCurrentSection()",
            equipmentViewModel);
        Assert.Contains(
            "CurrentSection.Equals(",
            equipmentViewModel);
        Assert.Contains(
            "\"Inventory\"",
            equipmentViewModel);
        Assert.Contains(
            "Data=\"M1,8 C3.8,3.5 12.2,3.5 15,8 C12.2,12.5 3.8,12.5 1,8 Z\"",
            mainWindowXaml);
        Assert.Contains(
            "PreviewMouseLeftButtonUp=\"EquipmentLaneHeader_PreviewMouseLeftButtonUp\"",
            mainWindowXaml);
        Assert.DoesNotContain(
            "Click=\"MoveEquipmentLaneLeft_Click\"",
            mainWindowXaml);
        Assert.DoesNotContain(
            "Click=\"MoveEquipmentLaneRight_Click\"",
            mainWindowXaml);
        Assert.DoesNotContain("EquipmentDetailsRailButton", mainWindowXaml);
        Assert.DoesNotContain("ToggleEquipmentDetailsButton", mainWindowXaml);
        Assert.DoesNotContain("Content=\"&#xE890;\"", mainWindowXaml);
        Assert.Contains("EquipmentLaneDragPreview", mainWindowCode);
        Assert.Contains(
            "sourceElement.CaptureMouse();",
            mainWindowCode);
        Assert.Contains(
            "UpdateEquipmentLaneDragTarget();",
            mainWindowCode);
        Assert.Contains(
            "FindEquipmentLaneElementAncestor(hit)",
            mainWindowCode);
        Assert.Contains(
            "EquipmentLaneDragPlacement.ResolveTargetIndex",
            mainWindowCode);
        Assert.DoesNotContain(
            "EquipmentLaneHeader_DragOver",
            mainWindowCode);
        Assert.DoesNotContain(
            "typeof(EquipmentLane), lane",
            mainWindowCode);
        Assert.Contains(
            "EquipmentLanes.Move(sourceIndex, targetIndex);",
            equipmentViewModel);
        Assert.DoesNotContain("insertAfterTarget", equipmentViewModel);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "TechBenchV2.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }

    [Theory]
    [InlineData(1, 336, 5, 2)]
    [InlineData(2, -336, 5, 1)]
    [InlineData(1, 672, 5, 3)]
    [InlineData(3, -672, 5, 1)]
    [InlineData(1, 120, 5, 1)]
    [InlineData(3, 900, 5, 4)]
    public void TechnicianDragDistanceResolvesAStableDestination(
        int sourceIndex,
        double horizontalDelta,
        int laneCount,
        int expectedTargetIndex)
    {
        Assert.Equal(
            expectedTargetIndex,
            EquipmentLaneDragPlacement.ResolveTargetIndex(
                sourceIndex,
                horizontalDelta,
                laneCount));
    }

    [Theory]
    [InlineData(350, 5, 1)]
    [InlineData(500, 5, 1)]
    [InlineData(700, 5, 2)]
    [InlineData(1050, 5, 3)]
    [InlineData(1600, 5, 4)]
    public void TechnicianBoardPositionResolvesTheColumnUnderThePointer(
        double boardPositionX,
        int laneCount,
        int expectedTargetIndex)
    {
        Assert.Equal(
            expectedTargetIndex,
            EquipmentLaneDragPlacement
                .ResolveTargetIndexFromBoardPosition(
                    boardPositionX,
                    laneCount));
    }

    [Fact]
    public void StockLaneReportsItsAssignmentAndLiveItemCount()
    {
        var lane = new EquipmentLane("Stock", null);
        var changedProperties = new List<string>();
        lane.PropertyChanged += (_, args) =>
            changedProperties.Add(args.PropertyName ?? string.Empty);

        lane.Items.Add(new EquipmentItem
        {
            EquipmentId = 1,
            DeviceType = "Firewall",
            Name = "Stock firewall"
        });

        Assert.True(lane.IsStock);
        Assert.Equal("1 item", lane.CountLabel);
        Assert.Contains(nameof(EquipmentLane.CountLabel), changedProperties);
    }

    [Fact]
    public void EquipmentCardSummariesUseOnlyPopulatedDetails()
    {
        var equipment = new EquipmentItem
        {
            DeviceType = "Switch",
            Name = "Core replacement",
            Manufacturer = "Example",
            Model = "SW-48",
            SerialNumber = string.Empty,
            ClientName = "Sample Client",
            IpAddress = "192.0.2.10"
        };

        Assert.Equal("Example \u00B7 SW-48", equipment.IdentityLine);
        Assert.Equal(
            "Sample Client \u00B7 192.0.2.10",
            equipment.LocationLine);
        Assert.Equal("Stock", equipment.AssignmentLabel);
    }

    [Fact]
    public void DeploymentStageIsDistinctAndRetainsAReadyStatus()
    {
        var lane = new EquipmentLane(
            "Deployment",
            null,
            EquipmentWorkflowStages.Deployment);
        var equipment = new EquipmentItem
        {
            EquipmentId = 2,
            DeviceType = "Laptop",
            Name = "Ready laptop",
            WorkflowStage = EquipmentWorkflowStages.Deployment,
            AssignedToLoginName = @"CSRI\technician",
            AssignedToDisplayName = "Technician"
        };

        lane.Items.Add(equipment);

        Assert.True(lane.IsDeployment);
        Assert.False(lane.IsStock);
        Assert.Equal("Awaiting deployment owner", lane.Subtitle);
        Assert.Equal("Ready", equipment.StatusLabel);
        Assert.Equal("Ready for deployment", equipment.AssignmentLabel);
    }

    [Fact]
    public void DeploymentTechnicianLaneKeepsTechnicianIdentity()
    {
        var lane = new EquipmentLane(
            "Alex Morgan",
            @"DEMO\amorgan",
            EquipmentWorkflowStages.Deployment);

        Assert.True(lane.IsDeployment);
        Assert.Equal("Deploying technician", lane.Subtitle);
        Assert.Equal("AM", lane.Initials);
        Assert.Equal("Segoe UI", lane.AvatarFontFamily);
    }

    [Fact]
    public void DeploymentRepeatsTechnicianColumnsAndReassignsTheDeployingTechnician()
    {
        var board = new EquipmentBoardDemoViewModel();

        Assert.Equal(
            new[] { "Unassigned", "Alex Morgan", "Jordan Lee", "Taylor Rivera", "Casey Patel" },
            board.DeploymentLanes.Select(static lane => lane.Title));

        var alexLane = board.DeploymentLanes.Single(static lane =>
            lane.AssignedToLoginName == @"DEMO\amorgan");
        var jordanLane = board.DeploymentLanes.Single(static lane =>
            lane.AssignedToLoginName == @"DEMO\jlee");
        var equipment = Assert.Single(alexLane.Items);

        board.AssignEquipment(equipment, jordanLane, 0);

        Assert.Empty(alexLane.Items);
        var moved = Assert.Single(jordanLane.Items);
        Assert.Equal(EquipmentWorkflowStages.Deployment, moved.WorkflowStage);
        Assert.Equal(@"DEMO\jlee", moved.AssignedToLoginName);
        Assert.Equal("Jordan Lee", moved.AssignedToDisplayName);
    }

    [Fact]
    public void DeployedEquipmentIdentifiesItsClientUserAndAssetTag()
    {
        var equipment = new EquipmentItem
        {
            EquipmentId = 3,
            AssetTag = "TB-0042",
            DeviceType = "Laptop",
            Name = "Accounting laptop",
            WorkflowStage = EquipmentWorkflowStages.Deployment,
            ClientId = 12,
            ClientName = "Sample Client",
            ClientUserId = 1201,
            ClientUserDisplayName = "Dana Brooks",
            LocationName = "Main office"
        };

        Assert.True(equipment.HasAssetTag);
        Assert.Equal("TB-0042", equipment.AssetTagChipLabel);
        Assert.Equal(
            "Sample Client \u00B7 Dana Brooks",
            equipment.ClientChipLabel);
        Assert.Equal(
            "Sample Client \u00B7 Dana Brooks",
            equipment.AssignmentLabel);
        Assert.Equal("User assigned", equipment.StatusLabel);
    }

    [Fact]
    public void ClientUserAndAssignmentHistoryExposeReadableLabels()
    {
        var clientUser = new InventoryClientUser
        {
            DisplayName = "Dana Brooks",
            RoleDepartment = "Accounting"
        };
        var history = new EquipmentAssignmentHistoryEntry
        {
            ClientName = "Sample Client",
            ClientUserDisplayName = "Dana Brooks",
            LocationName = "Main office"
        };

        Assert.Equal(
            "Dana Brooks \u2014 Accounting",
            clientUser.DisplayLabel);
        Assert.Equal(
            "Sample Client \u00B7 Dana Brooks \u00B7 Main office",
            history.ClientChipLabel);
    }

    [Fact]
    public void EquipmentDetailsCanBeCopiedWithoutIncludingThePassword()
    {
        var equipment = new EquipmentItem
        {
            Name = "Reception PC",
            DeviceType = "Desktop",
            AssetTag = "TB-0007",
            SerialNumber = "SERIAL-7",
            AnyDeskNumber = "123 456 789",
            AnyDeskPassword = "private-password",
            ClientName = "Acme Test",
            ClientUserDisplayName = "Dana Brooks"
        };

        var text = EquipmentClipboardFormatter.Format(equipment);

        Assert.Contains("Equipment: Reception PC", text);
        Assert.Contains("Asset tag: TB-0007", text);
        Assert.Contains("AnyDesk number: 123 456 789", text);
        Assert.Contains("Client user: Dana Brooks", text);
        Assert.DoesNotContain("private-password", text);
    }

    [Fact]
    public void DesktopAndLaptopEditorSupportsProtectedAnyDeskDetails()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var equipmentViewModel = ReadRepositoryFile(Path.Combine(
            "ViewModels",
            "MainWindowViewModel.Equipment.cs"));
        var repository = ReadRepositoryFile(Path.Combine(
            "Data",
            "SqlServerTechBenchRepository.Equipment.cs"));

        Assert.Contains("Text=\"AnyDesk remote access\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"AnyDesk number\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"AnyDesk password\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"••••••••\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"Show and edit AnyDesk password\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "EquipmentDeviceType.Equals(\"Desktop\"",
            equipmentViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "EquipmentDeviceType.Equals(\"Laptop\"",
            equipmentViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddMaxText(command, \"@AnyDeskPassword\", equipment.AnyDeskPassword)",
            repository,
            StringComparison.Ordinal);
    }
}
