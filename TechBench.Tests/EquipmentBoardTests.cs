using TechBench.Models;
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

        Assert.Contains(
            "Visibility=\"{Binding IsEquipmentEditorVisible, Converter={StaticResource BooleanToVisibilityConverter}}\"",
            mainWindowXaml);
        Assert.Contains("Content=\"&#xE890;\"", mainWindowXaml);
        Assert.DoesNotContain(
            "Click=\"MoveEquipmentLaneLeft_Click\"",
            mainWindowXaml);
        Assert.DoesNotContain(
            "Click=\"MoveEquipmentLaneRight_Click\"",
            mainWindowXaml);
        Assert.DoesNotContain("EquipmentDetailsRailButton", mainWindowXaml);
        Assert.DoesNotContain("ToggleEquipmentDetailsButton", mainWindowXaml);
        Assert.Contains("EquipmentLaneDragPreview", mainWindowCode);
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
}
