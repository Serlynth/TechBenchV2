using TechBench.Models;

namespace TechBench.Tests;

public sealed class ClientInfoLayoutContractTests
{
    [Fact]
    public void EquipmentTabIsHiddenWithoutRemovingItsExistingContent()
    {
        var xaml = Read("ClientInfoBetaWindow.xaml");

        Assert.Contains(
            "<TabItem Header=\"Equipment\" Visibility=\"Collapsed\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding Equipment}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Microsoft 365 Business Basic", "365 Business Basic")]
    [InlineData("Microsoft 365 Business Premium", "365 Business Premium")]
    [InlineData("Exchange Online Plan 1", "Exchange Online Plan 1")]
    public void UserLicenseDisplayDropsOnlyTheLeadingMicrosoftPrefix(
        string storedValue,
        string expected)
    {
        var person = new ClientInfoPerson
        {
            Microsoft365License = storedValue
        };

        Assert.Equal(expected, person.Microsoft365LicenseDisplay);

        var xaml = Read("ClientInfoBetaWindow.xaml");
        Assert.Contains("Header=\"License\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Binding=\"{Binding Microsoft365LicenseDisplay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding Microsoft365LicenseDisplay, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Header=\"365 License\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UsersGridKeepsTheNameColumnVisibleWhileScrollingHorizontally()
    {
        var xaml = Read("ClientInfoBetaWindow.xaml");
        var usersGridStart = xaml.IndexOf(
            "x:Name=\"PeopleDataGrid\"",
            StringComparison.Ordinal);
        var usersColumnsEnd = xaml.IndexOf(
            "</DataGrid.Columns>",
            usersGridStart,
            StringComparison.Ordinal);

        Assert.True(usersGridStart >= 0);
        Assert.True(usersColumnsEnd > usersGridStart);

        var usersGrid = xaml[usersGridStart..usersColumnsEnd];
        var columnsStart = usersGrid.IndexOf(
            "<DataGrid.Columns>",
            StringComparison.Ordinal);
        Assert.True(columnsStart >= 0);

        var firstHeader = usersGrid.IndexOf(
            "Header=\"",
            columnsStart,
            StringComparison.Ordinal);

        Assert.Contains("FrozenColumnCount=\"1\"", usersGrid, StringComparison.Ordinal);
        Assert.Equal(
            usersGrid.IndexOf("Header=\"Name\"", StringComparison.Ordinal),
            firstHeader);
        Assert.Contains(
            "Text=\"{Binding DisplayName, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", usersGrid + xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Azure Sync\"", usersGrid, StringComparison.Ordinal);
        Assert.DoesNotContain("365 uses AD login", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectingAUserDrivesProfileAndAssignedEquipmentCards()
    {
        var xaml = Read("ClientInfoBetaWindow.xaml");

        Assert.Contains(
            "SelectedItem=\"{Binding SelectedPerson, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectionChanged=\"Users_SelectionChanged\"",
            xaml,
            StringComparison.Ordinal);
        var codeBehind = Read("ClientInfoBetaWindow.xaml.cs");
        Assert.Contains(
            "viewModel.SelectedPerson = person;",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "DataContext=\"{Binding SelectedItem, ElementName=PeopleDataGrid}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DisplayInitials}\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding AzureSyncLabel, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"Assigned equipment\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding DataContext.SelectedPersonEquipment, RelativeSource={RelativeSource AncestorType=Window}}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DeviceGlyph}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ModelLine}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SerialNumber", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding StatusLabel}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenEquipmentDetailsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "<controls:ClientEquipmentDetailsDrawer Grid.Row=\"0\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding IsEquipmentDetailsVisible, Converter={StaticResource BooleanToVisibilityConverter}}\"",
            xaml,
            StringComparison.Ordinal);

        var viewModel = Read("ViewModels", "ClientInfoBetaViewModel.cs");
        Assert.Contains("?? People.FirstOrDefault();", viewModel, StringComparison.Ordinal);
        Assert.Contains(
            "public RelayCommand OpenEquipmentDetailsCommand { get; }",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedEquipment = equipment;",
            viewModel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MissingAdPasswordsDoNotLeaveEmptyRevealOrCopyControls()
    {
        var xaml = Read("ClientInfoBetaWindow.xaml");
        var adPasswordTriggers = xaml.Split(
            "<DataTrigger Binding=\"{Binding AdPassword}\" Value=\"{x:Null}\">",
            StringSplitOptions.None);

        Assert.True(
            adPasswordTriggers.Length >= 3,
            "Both the user summary and the full Users grid must hide empty AD password controls.");
        Assert.Contains(
            "<Setter Property=\"Visibility\" Value=\"Collapsed\" />",
            adPasswordTriggers[1],
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"Visibility\" Value=\"Collapsed\" />",
            adPasswordTriggers[2],
            StringComparison.Ordinal);
    }

    [Fact]
    public void PasswordListUsesReadableResizableRememberedColumns()
    {
        var xaml = Read("ClientInfoBetaWindow.xaml");
        var codeBehind = Read("ClientInfoBetaWindow.xaml.cs");
        var preferences = Read("Services", "LocalPreferenceStore.cs");

        Assert.Contains("x:Name=\"CredentialsDataGrid\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CredentialDetailsColumn\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FrozenColumnCount=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<GridSplitter Grid.Column=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"280\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AccessGridColumnWidths", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AccessDetailsPaneWidth", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AccessGridColumnWidths", preferences, StringComparison.Ordinal);
        Assert.Contains("AccessDetailsPaneWidth", preferences, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveClientInformationHidesReviewControlsAndUsesMakeLiveWorkflow()
    {
        var xaml = Read("ClientInfoBetaWindow.xaml");
        var importXaml = Read("ClientInfoImportWindow.xaml");
        var codeBehind = Read("ClientInfoBetaWindow.xaml.cs");
        var resourceGrid = Read("Controls", "ClientInfoResourceDataGrid.cs");
        var viewModel = Read("ViewModels", "ClientInfoBetaViewModel.cs");

        Assert.Contains("ShowReviewWorkflow", xaml, StringComparison.Ordinal);
        Assert.Contains("LocationsReviewColumn.Visibility", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PeopleReviewColumn.Visibility", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CredentialsReviewColumn.Visibility", codeBehind, StringComparison.Ordinal);
        Assert.Contains("FactsReviewColumn.Visibility", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ShowReviewColumn", resourceGrid, StringComparison.Ordinal);
        Assert.Contains("if (ShowReviewColumn)", resourceGrid, StringComparison.Ordinal);
        Assert.Contains("Content=\"Make Live\"", importXaml, StringComparison.Ordinal);
        Assert.Contains("Make Client Information live", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void ResourceSelectedRecordAreaAndDenseHeadersRemainReadable()
    {
        var xaml = Read("ClientInfoBetaWindow.xaml");
        var grid = Read("Controls", "ClientInfoResourceDataGrid.cs");

        Assert.Contains(
            "ScrollViewer.VerticalScrollBarVisibility=\"Auto\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ScrollViewer.CanContentScroll=\"False\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Key=\"WrappedResourceColumnHeaderTemplate\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ColumnHeaderHeight = 54;", grid, StringComparison.Ordinal);
        Assert.Contains("RowHeight = 42;", grid, StringComparison.Ordinal);
        Assert.Contains("Width = new DataGridLength(width, DataGridLengthUnitType.Pixel)", grid, StringComparison.Ordinal);
        Assert.Contains("? 120", grid, StringComparison.Ordinal);
        Assert.Contains("? 95", grid, StringComparison.Ordinal);
        Assert.Contains("ResourceGrid_Sorting", grid, StringComparison.Ordinal);
        Assert.Contains("$field:", grid, StringComparison.Ordinal);
        Assert.Contains("Width=\"620\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"Width\" Value=\"650\" />",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"HorizontalAlignment\" Value=\"Left\" />",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<ContentPresenter HorizontalAlignment=\"Left\" />",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment\" Value=\"Left\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionGridUsesConciseUiOnlyColumnAliases()
    {
        var grid = Read("Controls", "ClientInfoResourceDataGrid.cs");
        var aliases = new Dictionary<string, string>
        {
            ["public_wan_ip"] = "WAN IP",
            ["ssl_vpn_port"] = "VPN Port",
            ["subnet_cidr"] = "Subnet",
            ["ip_assignment_type"] = "IP Type",
            ["usable_static_ip_count"] = "Usable #",
            ["static_ip_addresses"] = "Static IPs",
            ["static_ip_range_start"] = "First IP",
            ["static_ip_range_end"] = "Last IP",
            ["device_model"] = "Model",
            ["firmware_version"] = "Firmware",
            ["isp_provider"] = "ISP",
            ["support_phone"] = "Support",
            ["account_number"] = "Account #",
            ["service_type"] = "Service",
            ["support_contact"] = "Contact"
        };

        foreach (var (fieldKey, header) in aliases)
        {
            Assert.Contains(
                $"\"{fieldKey}\" => \"{header}\"",
                grid,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "categoryName == ClientInfoResourceCategories.ConnectionInternet",
            grid,
            StringComparison.Ordinal);
        Assert.Contains(
            "ForEditorCategory(group.CategoryName)",
            grid,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PasswordLoginUrlsHaveAnInlineCopyAction()
    {
        var xaml = Read("ClientInfoBetaWindow.xaml");
        var viewModel = Read("ViewModels", "ClientInfoBetaViewModel.cs");

        Assert.Contains(
            "<DataGridTemplateColumn Header=\"Login URL\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("CopyLoginUrlCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"{Binding}\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "public RelayCommand CopyLoginUrlCommand { get; }",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains("WpfClipboard.SetText(loginUrl);", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryClientInformationFullListCanBeSearched()
    {
        var xaml = Read("ClientInfoBetaWindow.xaml");
        var viewModel = Read("ViewModels", "ClientInfoBetaViewModel.cs");
        var mainXaml = Read("MainWindow.xaml");
        var mainViewModel = Read(
            "ViewModels",
            "MainWindowViewModel.ClientInfoBeta.cs");

        Assert.Contains("ItemsSource=\"{Binding FilteredResources}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SearchText, UpdateSourceTrigger=PropertyChanged}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding LocationsView}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding PeopleView}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding CredentialsView}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding FactsView}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MatchesResourceSearch", viewModel, StringComparison.Ordinal);
        Assert.Contains("ClientInfoStatusFilterOptions", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ClientInfoSortOptions", mainXaml, StringComparison.Ordinal);
        Assert.Contains("MatchesClientInfoStatusFilter", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("Recently updated", mainViewModel, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(
            new[] { RepositoryRoot() }.Concat(parts).ToArray()));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "TechBenchV2.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException(
                "Could not locate the TechBench repository root.");
    }
}
