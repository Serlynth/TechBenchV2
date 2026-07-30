using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class CredentialWorkspaceLayoutTests
{
    [Fact]
    public void SidebarGroupsClientWorkspacesUnderOneExpandableArea()
    {
        var navigation = ReadRepositoryFile(
            Path.Combine("Controls", "WorkspaceNavigation.xaml"));
        var xaml = ReadRepositoryFile("MainWindow.xaml");

        Assert.Contains(
            "Header=\"FIREDRILL\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter=\"Client Info\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding FireDrillWorkspaceSections}\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"{Binding DisplayName}\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter=\"{Binding SectionKey}\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding IsCredentialWorkspaceSection, Converter={StaticResource BooleanToVisibilityConverter}}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CredentialWorkspaceTitle}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandParameter=\"Client Credentials\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SidebarNavigationUsesScrollableExpandableGroups()
    {
        var mainWindow = ReadRepositoryFile("MainWindow.xaml");
        var navigation = ReadRepositoryFile(
            Path.Combine("Controls", "WorkspaceNavigation.xaml"));

        Assert.Contains(
            "<controls:WorkspaceNavigation Grid.Column=\"0\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"SidebarNavigationScrollViewer\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "VerticalScrollBarVisibility=\"Auto\"",
            navigation,
            StringComparison.Ordinal);
        foreach (var group in new[] { "NOTES", "FIREDRILL", "EQUIPMENT", "SYSTEM" })
        {
            Assert.Contains(
                $"Header=\"{group}\"",
                navigation,
                StringComparison.Ordinal);
        }
        Assert.DoesNotContain(
            "Header=\"SERVICE\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains("x:Key=\"WorkspaceNavItemStyle\"", navigation, StringComparison.Ordinal);
        Assert.Contains(
            "Converter={StaticResource SectionActiveConverter}",
            navigation,
            StringComparison.Ordinal);
        Assert.DoesNotContain("x:Key=\"NavTodayStyle\"", navigation, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialWorkspaceSearchHasSearchAndClearActions()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var viewModel = ReadRepositoryFile(Path.Combine("ViewModels", "MainWindowViewModel.FireDrill.cs"));

        Assert.Contains("Command=\"{Binding SearchFireDrillCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "<KeyBinding Key=\"Enter\" Command=\"{Binding SearchFireDrillCommand}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Content=\"Clear\" Command=\"{Binding ClearFireDrillSearchCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FireDrillSearchText = string.Empty;", viewModel, StringComparison.Ordinal);
        Assert.Contains("RefreshFireDrillCredentials();", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultCardsExposeOnlyClientName()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var itemsSource = xaml.IndexOf(
            "ItemsSource=\"{Binding FireDrillCredentials}\"",
            StringComparison.Ordinal);
        var start = xaml.LastIndexOf("<ListBox", itemsSource, StringComparison.Ordinal);
        var end = xaml.IndexOf("</ListBox>", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var list = xaml[start..end];

        Assert.Contains("{Binding ClientName}", list, StringComparison.Ordinal);
        Assert.DoesNotContain("FireboxIp", list, StringComparison.Ordinal);
        Assert.DoesNotContain("Status", list, StringComparison.Ordinal);
        Assert.DoesNotContain("LastSyncedLabel", list, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailPanelShowsMaskedFieldsBeforeReveal()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var viewModel = ReadRepositoryFile(Path.Combine("ViewModels", "MainWindowViewModel.FireDrill.cs"));
        var clipboard = ReadRepositoryFile(Path.Combine("Services", "ClipboardService.cs"));

        Assert.Contains("ItemsSource=\"{Binding FireDrillCredentialGroups}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Fields}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Name}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding HasSelectedFireDrillCredential", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedFireDrillCredential?.Fields", viewModel, StringComparison.Ordinal);
        Assert.Contains("field with { Value = \"***\" }", viewModel, StringComparison.Ordinal);
        Assert.Contains("PopulateRevealedFireDrillFields", viewModel, StringComparison.Ordinal);
        Assert.Contains("await ClipboardService.TrySetTextAsync(value)", viewModel, StringComparison.Ordinal);
        Assert.Contains("_isCopyingFireDrillCredential", viewModel, StringComparison.Ordinal);
        Assert.Contains("string.Equals(candidate.FieldName, field", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("selected credential field is invalid", viewModel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Clipboard.SetText", viewModel, StringComparison.Ordinal);
        Assert.Contains("thread.SetApartmentState(ApartmentState.STA)", clipboard, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.Run()", clipboard, StringComparison.Ordinal);
        Assert.Contains("Clipboard.SetDataObject(value, copy: false)", clipboard, StringComparison.Ordinal);
        Assert.DoesNotContain("copy: true", clipboard, StringComparison.Ordinal);
        Assert.DoesNotContain("AddFireDrillFields", viewModel, StringComparison.Ordinal);
        Assert.Contains("credential.Fields", viewModel, StringComparison.Ordinal);
        Assert.Contains("CredentialFieldGrouper.Group", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialFieldsAreGroupedByProviderWithFallback()
    {
        FireDrillCredentialField[] fields =
        [
            Field("Firebox IP", 0),
            Field("Status", 1),
            Field("Admin", 2),
            Field("csriadmin", 3),
            Field("*if enabled -Firebox-DB\\csri", 4),
            Field("Authpoint User", 5),
            Field("sslvpnpassword", 6),
            Field("Microsoft 365 Username", 7),
            Field("365 Password", 8),
            Field("ESET User", 9),
            Field("ESET Password", 10),
            Field("Barracuda Login", 11),
            Field("Barracuda Password", 12),
            Field("Veeam Username", 13),
            Field("Veeam Password", 14),
            Field("SonicWall Username", 15),
            Field("SonicWall Password", 16),
            Field("Unclassified note", 17),
            Field("Wireless SSID", 18),
            Field("Wireless Password", 19)
        ];

        var groups = CredentialFieldGrouper.Group(fields);

        Assert.Equal(
            ["Wireless", "WatchGuard", "Microsoft 365", "ESET", "Barracuda", "Veeam", "SonicWall", "Other"],
            groups.Select(group => group.Name));
        Assert.Equal(2, groups.Single(group => group.Name == "Wireless").Fields.Count);
        Assert.Equal(7, groups.Single(group => group.Name == "WatchGuard").Fields.Count);
        Assert.Equal(2, groups.Single(group => group.Name == "Veeam").Fields.Count);
        Assert.Equal(2, groups.Single(group => group.Name == "SonicWall").Fields.Count);
        Assert.Equal(
            "Unclassified note",
            groups.Single(group => group.Name == "Other").Fields.Single().Label);
    }

    [Fact]
    public void ClientWifiUsesOnlyWirelessPrefixedFields()
    {
        Assert.True(CredentialFieldGrouper.IsWirelessField(Field("Wireless SSID", 0)));
        Assert.True(CredentialFieldGrouper.IsWirelessField(Field(" wireless password", 1)));
        Assert.False(CredentialFieldGrouper.IsWirelessField(Field("Microsoft 365 Password", 2)));

        var source = Field("Wireless Guest Password", 3);
        var display = CredentialFieldGrouper.CreateWirelessDisplayField(source);
        Assert.Equal("Guest Password", display.Label);
        Assert.Equal(source.FieldName, display.FieldName);
        Assert.Equal(source.Value, display.Value);
        Assert.Equal(
            "Admin Password",
            CredentialFieldGrouper.CreateWirelessDisplayField(
                Field("Wireless - Admin Password", 4)).Label);

        var viewModel = ReadRepositoryFile(Path.Combine("ViewModels", "MainWindowViewModel.FireDrill.cs"));
        Assert.Contains("item.Fields.Any(IsFieldVisibleInCurrentCredentialSection)", viewModel, StringComparison.Ordinal);
        Assert.Contains("CredentialFieldGrouper.IsFieldInWorkspaceSection", viewModel, StringComparison.Ordinal);
        Assert.Contains("CredentialFieldGrouper.CreateWirelessSectionGroup(", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientWifiKeepsWirelessAdminInWirelessGroupAfterPrefixRemoval()
    {
        FireDrillCredentialField[] fields =
        [
            Field("Wireless Admin", 0),
            Field("Wireless Admin Password", 1),
            Field("Wireless Management Url", 2),
            Field("Firebox IP", 3)
        ];

        var group = CredentialFieldGrouper.CreateWirelessSectionGroup(fields);

        Assert.NotNull(group);
        Assert.Equal("Wireless", group.Name);
        Assert.Equal(
            ["Admin", "Admin Password", "Management Url"],
            group.Fields.Select(field => field.Label));
        Assert.DoesNotContain(
            group.Fields,
            field => field.Label.StartsWith("Wireless", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            group.Fields,
            field => field.Label.Equals("Firebox IP", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CredentialSubsectionsClassifyFieldsWithoutOverlap()
    {
        var wireless = Field("Wireless Guest Password", 0);
        var domain = Field("AD Password", 1);
        var localDomain = Field("Local Domain", 2);
        var connection = Field("Firebox IP", 3);
        var authPoint = Field("AuthPoint User", 4);
        var veeam = Field("Veeam Password", 5);
        var misc = Field("Microsoft 365 Password", 6);

        Assert.True(CredentialFieldGrouper.IsWirelessField(wireless));
        Assert.True(CredentialFieldGrouper.IsDomainOrAdField(domain));
        Assert.True(CredentialFieldGrouper.IsDomainOrAdField(localDomain));
        Assert.True(CredentialFieldGrouper.IsConnectionField(connection));
        Assert.True(CredentialFieldGrouper.IsConnectionField(authPoint));
        Assert.True(CredentialFieldGrouper.IsVeeamField(veeam));
        Assert.False(CredentialFieldGrouper.IsVeeamField(misc));
        Assert.True(CredentialFieldGrouper.IsMiscInfoField(misc));

        foreach (var field in new[] { wireless, domain, localDomain, connection, authPoint, veeam })
            Assert.False(CredentialFieldGrouper.IsMiscInfoField(field));
    }

    [Fact]
    public void RepeatedLeadingKeywordsCreateFutureWorkspaceSections()
    {
        FireDrillCredentialField[] fields =
        [
            Field("ILO Host 1 User", 0),
            Field("ILO Host 1 PW", 1),
            Field("ILO Host 2 IP", 2),
            Field("UPS 1 User", 3),
            Field("UPS 1 PW", 4),
            Field("Rack 1 User", 5),
            Field("Rack 2 Password", 6),
            Field("One-off note", 7)
        ];

        var groups = CredentialFieldGrouper.Group(fields);
        var sections =
            CredentialFieldGrouper.DiscoverWorkspaceSections(fields);

        Assert.Equal(
            ["ILO", "UPS", "Rack", "Other"],
            groups.Select(group => group.Name));
        Assert.Equal(
            ["ILO", "UPS", "Rack", "Miscellaneous"],
            sections.Select(section => section.DisplayName));
        Assert.Equal(
            3,
            sections.Single(section =>
                section.DisplayName == "ILO").FieldKeys.Count);
        Assert.Equal(
            2,
            sections.Single(section =>
                section.DisplayName == "UPS").FieldKeys.Count);
        Assert.All(
            sections,
            section => Assert.StartsWith(
                CredentialFieldGrouper.WorkspaceSectionPrefix,
                section.SectionKey,
                StringComparison.Ordinal));
    }

    [Fact]
    public void OneRepeatedClientRowDoesNotCreateASection()
    {
        var repeatedColumn = Enumerable.Range(0, 5)
            .Select(index => new FireDrillCredentialField
            {
                Label = "Single Appliance Token",
                FieldName = "single_appliance_token",
                SortOrder = index,
                Value = "***"
            });

        var sections =
            CredentialFieldGrouper.DiscoverWorkspaceSections(
                repeatedColumn);

        var miscellaneous = Assert.Single(sections);
        Assert.Equal("Miscellaneous", miscellaneous.DisplayName);
        Assert.Single(miscellaneous.FieldKeys);
    }

    [Fact]
    public void ViewModelFiltersDiscoveredFireDrillWorkspaces()
    {
        var viewModel = ReadRepositoryFile(Path.Combine("ViewModels", "MainWindowViewModel.FireDrill.cs"));
        var navigation = ReadRepositoryFile(Path.Combine("ViewModels", "MainWindowViewModel.cs"));

        Assert.Contains("\"Client Info\"", viewModel + navigation, StringComparison.Ordinal);
        Assert.Contains("item.Fields.Any(IsFieldVisibleInCurrentCredentialSection)", viewModel, StringComparison.Ordinal);
        Assert.Contains("DiscoverWorkspaceSections", viewModel, StringComparison.Ordinal);
        Assert.Contains("FireDrillWorkspaceSections", viewModel + navigation, StringComparison.Ordinal);
        Assert.Contains("IsWorkspaceSectionKey(CurrentSection)", viewModel, StringComparison.Ordinal);
        Assert.Contains("CurrentFireDrillWorkspaceSection", viewModel, StringComparison.Ordinal);
    }

    private static FireDrillCredentialField Field(string label, int order) =>
        new()
        {
            Label = label,
            FieldName = label.ToLowerInvariant(),
            SortOrder = order,
            Value = "***"
        };

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TechBenchV2.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }
}
