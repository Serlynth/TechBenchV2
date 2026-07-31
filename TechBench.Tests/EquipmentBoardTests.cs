using TechBench.Controls;
using TechBench.Converters;
using TechBench.Models;
using TechBench.ViewModels;
using System.Xml.Linq;

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
    public void EquipmentBoardKeepsDragWorkflowWhileInventoryOwnsTheEditor()
    {
        var mainWindowXaml = ReadRepositoryFile("MainWindow.xaml");
        var mainWindowCode = ReadRepositoryFile("MainWindow.xaml.cs");
        var equipmentViewModel = ReadRepositoryFile(
            Path.Combine(
                "ViewModels",
                "MainWindowViewModel.Equipment.cs"));

        Assert.Contains("<controls:EquipmentEditorPanel", mainWindowXaml);
        Assert.Contains(
            "Visibility=\"{Binding IsEquipmentInventoryEditorVisible",
            mainWindowXaml);
        Assert.Contains(
            "x:Name=\"InventoryRegistrySurface\"",
            mainWindowXaml);
        var document = XDocument.Parse(mainWindowXaml);
        var xamlNamespace = XNamespace.Get(
            "http://schemas.microsoft.com/winfx/2006/xaml");
        var registrySurface = Assert.Single(
            document.Descendants(),
            element =>
                (string?)element.Attribute(xamlNamespace + "Name")
                == "InventoryRegistrySurface");
        var inventoryWorkspace = Assert.IsType<XElement>(
            registrySurface.Parent);
        Assert.Contains(
            inventoryWorkspace.Elements(),
            element => element.Name.LocalName == "EquipmentEditorPanel");
        Assert.DoesNotContain(
            "x:Name=\"EquipmentDetailsPanel\"",
            mainWindowXaml);
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

    [Fact]
    public void EquipmentPaneClosesWheneverTheWorkspaceScreenChanges()
    {
        var mainViewModel = ReadRepositoryFile(
            Path.Combine("ViewModels", "MainWindowViewModel.cs"));

        var moduleSetter = ReadBlock(
            mainViewModel,
            "public BenchModule ActiveBenchModule",
            "public string ModuleBrandName");
        var sectionSetter = ReadBlock(
            mainViewModel,
            "public string CurrentSection",
            "public string WindowTitle");

        Assert.Contains("CloseEquipmentEditor();", moduleSetter);
        Assert.Contains("CloseEquipmentEditor();", sectionSetter);
        Assert.True(
            sectionSetter.IndexOf(
                "CloseEquipmentEditor();",
                StringComparison.Ordinal)
            > sectionSetter.IndexOf(
                "if (SetProperty(ref _currentSection, value))",
                StringComparison.Ordinal));
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

    private static string ReadBlock(
        string source,
        string startMarker,
        string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(
            endMarker,
            start,
            StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        return source[start..end];
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
    public void DeployedStageIsDistinctFromDeploymentAndRemainsInventoryVisible()
    {
        var equipment = new EquipmentItem
        {
            EquipmentId = 22,
            DeviceType = "Laptop",
            Name = "Installed laptop",
            WorkflowStage = EquipmentWorkflowStages.Deployed,
            ClientName = "Sample Client",
            ClientUserDisplayName = "Dana Brooks"
        };

        Assert.True(equipment.IsDeployed);
        Assert.False(equipment.IsDeployment);
        Assert.False(equipment.IsInStock);
        Assert.Equal("Deployed", equipment.StatusLabel);
        Assert.Equal("Deployed", equipment.InventoryStatusLabel);
        Assert.Equal(
            "Sample Client \u00B7 Dana Brooks",
            equipment.AssignmentLabel);
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
    public void DesktopAndLaptopEditorSupportsProtectedAnyDeskDetails()
    {
        var xaml = ReadRepositoryFile(Path.Combine(
            "Controls",
            "EquipmentEditorPanel.xaml"));
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

    [Fact]
    public void AnyDeskConnectActionIsAvailableFromEveryEquipmentPane()
    {
        var mainWindowXaml = ReadRepositoryFile("MainWindow.xaml");
        var editorXaml = ReadRepositoryFile(Path.Combine(
            "Controls",
            "EquipmentEditorPanel.xaml"));
        var detailsXaml = ReadRepositoryFile(Path.Combine(
            "Controls",
            "EquipmentDetailsContent.xaml"));
        var clientDrawerXaml = ReadRepositoryFile(Path.Combine(
            "Controls",
            "ClientEquipmentDetailsDrawer.xaml"));
        var mainViewModel = ReadRepositoryFile(Path.Combine(
            "ViewModels",
            "MainWindowViewModel.Equipment.cs"));
        var clientViewModel = ReadRepositoryFile(Path.Combine(
            "ViewModels",
            "ClientInfoProfileViewModel.cs"));

        Assert.Contains(
            "Content=\"Connect with AnyDesk\"",
            editorXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"Connect with AnyDesk\"",
            detailsXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "LaunchAnyDeskCommand=\"{Binding LaunchAnyDeskCommand}\"",
            mainWindowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "LaunchAnyDeskCommand=\"{Binding LaunchAnyDeskCommand}\"",
            clientDrawerXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AnyDeskLauncher.Launch(",
            mainViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "AnyDeskLauncher.Launch(",
            clientViewModel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyEquipmentViewsShareOneCanonicalFieldSet()
    {
        var mainWindowXaml = ReadRepositoryFile("MainWindow.xaml");
        var clientDrawerXaml = ReadRepositoryFile(Path.Combine(
            "Controls",
            "ClientEquipmentDetailsDrawer.xaml"));
        var detailsXaml = ReadRepositoryFile(Path.Combine(
            "Controls",
            "EquipmentDetailsContent.xaml"));
        var editorXaml = ReadRepositoryFile(Path.Combine(
            "Controls",
            "EquipmentEditorPanel.xaml"));
        var expectedFieldOrder = new[]
        {
            "Device type",
            "Asset tag",
            "Equipment name",
            "Serial number",
            "Part number",
            "Manufacturer",
            "Model",
            "IP address",
            "AnyDesk number",
            "AnyDesk password",
            "Client",
            "Client user / shared role",
            "Site / room / desk",
            "Notes",
            "Assignment history"
        };

        Assert.Contains(
            "<controls:EquipmentDetailsContent Equipment=\"{Binding SelectedEquipment}\"",
            mainWindowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<local:EquipmentDetailsContent Equipment=\"{Binding SelectedEquipment}\"",
            clientDrawerXaml,
            StringComparison.Ordinal);

        var detailsPosition = 0;
        var editorPosition = 0;
        foreach (var field in expectedFieldOrder)
        {
            detailsPosition = detailsXaml.IndexOf(
                $"Text=\"{field}\"",
                detailsPosition,
                StringComparison.OrdinalIgnoreCase);
            editorPosition = editorXaml.IndexOf(
                $"Text=\"{field}\"",
                editorPosition,
                StringComparison.OrdinalIgnoreCase);

            Assert.True(detailsPosition >= 0, $"Shared details are missing {field}.");
            Assert.True(editorPosition >= 0, $"Inventory editor is missing {field}.");
            detailsPosition++;
            editorPosition++;
        }

        Assert.Contains(
            "StringOrPlaceholderConverter",
            detailsXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SelectedEquipment.AssetTag, Converter={StaticResource StringToVisibilityConverter}",
            clientDrawerXaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyEquipmentValuesUseAStablePlaceholder()
    {
        var converter = new StringOrPlaceholderConverter();

        Assert.Equal(
            "—",
            converter.Convert(
                "  ",
                typeof(string),
                null,
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(
            "TB-100",
            converter.Convert(
                "TB-100",
                typeof(string),
                null,
                System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void InventoryRegistryAndEquipmentBoardHaveDistinctResponsibilities()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var viewModel = ReadRepositoryFile(Path.Combine(
            "ViewModels",
            "MainWindowViewModel.Equipment.cs"));
        var inventoryStart = xaml.IndexOf(
            "<Grid Visibility=\"{Binding CurrentSection, Converter={StaticResource SectionVisibilityConverter}, ConverterParameter=Inventory}\">",
            StringComparison.Ordinal);
        var boardStart = xaml.IndexOf(
            "<Grid Visibility=\"{Binding CurrentSection, Converter={StaticResource SectionVisibilityConverter}, ConverterParameter=Equipment Board}\">",
            StringComparison.Ordinal);

        Assert.True(inventoryStart >= 0);
        Assert.True(boardStart > inventoryStart);
        var inventory = xaml[inventoryStart..boardStart];
        var board = xaml[boardStart..];

        Assert.Contains("Text=\"All Equipment\"", inventory);
        Assert.Contains(
            "ItemsSource=\"{Binding InventoryEquipmentItems}\"",
            inventory);
        Assert.Contains("InventoryEquipmentSearchText", inventory);
        Assert.Contains("InventoryStatusFilterOptions", inventory);
        Assert.Contains("InventoryDeviceTypeFilterOptions", inventory);
        Assert.Contains("InventoryClientFilterOptions", inventory);
        Assert.Contains("InventoryTechnicianFilterOptions", inventory);
        Assert.Contains("InventoryStockOnly", inventory);
        Assert.Contains("ImportEquipmentBuildSheetCommand", inventory);
        Assert.Contains("NewEquipmentCommand", inventory);
        Assert.Contains("<controls:EquipmentEditorPanel", inventory);

        Assert.Contains("ItemsSource=\"{Binding EquipmentLanes}\"", board);
        Assert.Contains("ItemsSource=\"{Binding DeploymentLanes}\"", board);
        Assert.Contains(
            "Drop=\"EquipmentLaneListBox_Drop\"",
            board);
        Assert.DoesNotContain("ImportEquipmentBuildSheetCommand", board);
        Assert.DoesNotContain("NewEquipmentCommand", board);
        Assert.Contains("CanEditEquipmentRecords()", viewModel);
        Assert.Contains(
            "CurrentSection.Equals(\n            \"Inventory\"",
            viewModel.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void InventoryFilterSearchesRequestedEquipmentFields()
    {
        var equipment = new EquipmentItem
        {
            EquipmentId = 12,
            Name = "Accounting Workstation",
            AssetTag = "TB-2048",
            SerialNumber = "SN-9000",
            DeviceType = "Desktop",
            ClientName = "Marrone O'Rourke",
            ClientUserDisplayName = "Licia Marrone",
            ClientUserEmail = "licia@example.test",
            AssignedToDisplayName = "Ryan Skoog",
            WorkflowStage = EquipmentWorkflowStages.Assigned
        };

        foreach (var query in new[]
                 {
                     "Accounting",
                     "TB-2048",
                     "SN-9000",
                     "Marrone",
                     "Licia",
                     "Ryan"
                 })
        {
            Assert.True(EquipmentInventoryFilter.Matches(
                equipment,
                query,
                EquipmentInventoryFilter.AllStatuses,
                EquipmentInventoryFilter.AllDeviceTypes,
                EquipmentInventoryFilter.AllClients,
                EquipmentInventoryFilter.AllTechnicians,
                stockOnly: false));
        }

        Assert.False(EquipmentInventoryFilter.Matches(
            equipment,
            "not present",
            EquipmentInventoryFilter.AllStatuses,
            EquipmentInventoryFilter.AllDeviceTypes,
            EquipmentInventoryFilter.AllClients,
            EquipmentInventoryFilter.AllTechnicians,
            stockOnly: false));
    }

    [Theory]
    [InlineData(EquipmentWorkflowStages.Stock, EquipmentInventoryFilter.StockStatus)]
    [InlineData(EquipmentWorkflowStages.Assigned, EquipmentInventoryFilter.InProgressStatus)]
    [InlineData(EquipmentWorkflowStages.Deployment, EquipmentInventoryFilter.DeploymentStatus)]
    [InlineData(EquipmentWorkflowStages.Deployed, EquipmentInventoryFilter.DeployedStatus)]
    public void InventoryStatusFiltersMapToBoardWorkflowStages(
        string workflowStage,
        string statusFilter)
    {
        var equipment = new EquipmentItem
        {
            EquipmentId = 13,
            Name = "Filtered device",
            DeviceType = "Laptop",
            WorkflowStage = workflowStage
        };

        Assert.True(EquipmentInventoryFilter.Matches(
            equipment,
            searchText: string.Empty,
            statusFilter,
            EquipmentInventoryFilter.AllDeviceTypes,
            EquipmentInventoryFilter.AllClients,
            EquipmentInventoryFilter.AllTechnicians,
            stockOnly: false));
    }

    [Fact]
    public void EquipmentBoardCompletionActionIsVisibleAndKeepsInventoryAsRecordSystem()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var viewModel = ReadRepositoryFile(Path.Combine(
            "ViewModels",
            "MainWindowViewModel.Equipment.cs"));

        Assert.Contains("Content=\"✓  Mark Deployed\"", xaml);
        Assert.Contains("MarkEquipmentDeployedCommand", xaml);
        Assert.Contains("CanMarkSelectedEquipmentDeployed", xaml);
        var markStart = viewModel.IndexOf(
            "private async Task MarkEquipmentDeployedAsync()",
            StringComparison.Ordinal);
        var markEnd = viewModel.IndexOf(
            "public async Task AssignEquipmentAsync(",
            markStart,
            StringComparison.Ordinal);
        Assert.True(markStart >= 0 && markEnd > markStart);
        var markBody = viewModel[markStart..markEnd];
        Assert.Contains(
            "EquipmentDeploymentState.Create",
            markBody);
        Assert.Contains("SaveOrganizationSetting", markBody);
        Assert.DoesNotContain("MoveEquipment", markBody);
        Assert.Contains(
            "item.IsDeployed",
            viewModel);
        Assert.Contains(
            "It remains in Inventory",
            viewModel);
    }

    [Fact]
    public void SharedDeploymentStateRoundTripsAndOverlaysInventoryStatus()
    {
        var equipment = new EquipmentItem
        {
            EquipmentId = 44,
            DeviceType = "Laptop",
            Name = "Field laptop",
            WorkflowStage = EquipmentWorkflowStages.Deployment,
            AssignedToLoginName = @"CSRI\rjs",
            AssignedToDisplayName = "Ryan Skoog",
            ClientId = 7,
            ClientName = "Sample Client",
            ClientUserId = 701,
            ClientUserDisplayName = "Dana Brooks",
            LocationName = "Main office"
        };
        var currentUser = new CurrentUserContext(
            [1],
            @"CSRI\rjs",
            "Ryan Skoog",
            Guid.NewGuid(),
            15,
            DateTime.UtcNow,
            IsTechnician: true,
            IsManager: true,
            IsAdmin: true,
            IsSyncOperator: false);
        var deployedAt = new DateTime(
            2026,
            7,
            30,
            12,
            0,
            0,
            DateTimeKind.Utc);
        var state = EquipmentDeploymentState.Create(
            equipment,
            currentUser,
            deployedAt);
        var settings = new Dictionary<string, string>
        {
            [state.SettingKey] = state.Serialize()
        };

        var states = EquipmentDeploymentState.ReadFromSettings(settings);
        var effective = Assert.Single(
            EquipmentDeploymentState.Apply([equipment], states));
        var history = states[equipment.EquipmentId].ToHistoryEntry();

        Assert.True(effective.IsDeployed);
        Assert.Equal("Deployed", effective.InventoryStatusLabel);
        Assert.Equal(deployedAt, history.AssignedAtUtc);
        Assert.Equal("Deployed", history.EventType);
        Assert.Contains("Ryan Skoog", history.Notes);
    }

    [Fact]
    public void MalformedDeploymentSettingDoesNotHideEquipment()
    {
        var settings = new Dictionary<string, string>
        {
            [EquipmentDeploymentState.BuildSettingKey(55)] = "{not-json"
        };

        Assert.Empty(EquipmentDeploymentState.ReadFromSettings(settings));
    }

    [Fact]
    public void InventoryEditorXamlLoadsIndependentlyOnAnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = new EquipmentEditorPanel();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(failure);
    }

    [Fact]
    public void SharedEquipmentDetailsXamlLoadsIndependentlyOnAnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var details = new EquipmentDetailsContent
                {
                    Equipment = new EquipmentItem
                    {
                        EquipmentId = 12,
                        DeviceType = "Desktop",
                        Name = "Accounting PC",
                        ClientName = "Sample Client"
                    }
                };
                details.UpdateLayout();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(failure);
    }
}
