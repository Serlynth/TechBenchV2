using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using System.Text.Json;
using TechBench.Data;
using TechBench.Models;
using TechBench.Services;
using TechBench.ViewModels;

namespace TechBench.Tests;

public sealed class ClientInfoBetaTests
{
    [Fact]
    public void LiveClientStatusDoesNotExposeLegacyHypercareStage()
    {
        var summary = new ClientInfoClientSummary
        {
            IsLive = true,
            CutoverState = "Hypercare"
        };
        var profile = new ClientInfoProfile
        {
            IsLive = true,
            CutoverState = "Hypercare"
        };
        var staging = new ClientInfoClientSummary
        {
            IsLive = false,
            CutoverState = "Staging"
        };

        Assert.Equal("Live", summary.DisplayStatus);
        Assert.Equal("Live", profile.DisplayStatus);
        Assert.Equal("Staging", staging.DisplayStatus);
    }

    [Fact]
    public void NewManualClientIsCreatedLiveWithoutAWorkbookImport()
    {
        var procedures = Read(
            "database",
            "sqlserver2016",
            "61-V0015-ClientInfoBetaProcedures.sql");
        var grants = Read(
            "database",
            "sqlserver2016",
            "63-V0015-ClientInfoBetaGrants.sql");
        var verifier = Read(
            "database",
            "sqlserver2016",
            "106-V0015-ClientInfoBetaVerify.sql");
        var repository = Read(
            "Data",
            "SqlServerTechBenchRepository.ClientInfo.cs");
        var sharedRepository = Read(
            "Data",
            "SqlServerTechBenchRepository.cs");
        var workspace = Read(
            "ViewModels",
            "MainWindowViewModel.ClientInfoBeta.cs");
        var mainWindow = Read("MainWindow.xaml");
        var mainWindowCode = Read("MainWindow.xaml.cs");

        var procedureStart = procedures.IndexOf(
            "CREATE PROCEDURE [tb_app].[AdminCreateManualClientInfoClient]",
            StringComparison.Ordinal);
        var procedureEnd = procedures.IndexOf(
            "\nGO",
            procedureStart,
            StringComparison.Ordinal);
        Assert.True(procedureStart >= 0 && procedureEnd > procedureStart);
        var procedure = procedures[procedureStart..procedureEnd];

        Assert.Contains("BEGIN TRANSACTION", procedure, StringComparison.Ordinal);
        Assert.Contains("IS_ROLEMEMBER(N'tb_role_admin')", procedure, StringComparison.Ordinal);
        Assert.Contains("@Name, N'Manual'", procedure, StringComparison.Ordinal);
        Assert.Contains("N'Unmatched'", procedure, StringComparison.Ordinal);
        Assert.Contains("N'Unverified', 1", procedure, StringComparison.Ordinal);
        Assert.Contains("@ClientId, NULL, N'Complete'", procedure, StringComparison.Ordinal);
        Assert.Contains("[LiveAtUtc]", procedure, StringComparison.Ordinal);
        Assert.Contains("[CompletedAtUtc]", procedure, StringComparison.Ordinal);
        Assert.Contains("ManualClientInfoCreated", procedure, StringComparison.Ordinal);
        Assert.Contains("[tb_security].[WriteAuditEvent]", procedure, StringComparison.Ordinal);
        Assert.Contains("exact name already exists", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "GRANT EXECUTE ON OBJECT::[tb_app].[AdminCreateManualClientInfoClient]\n    TO [tb_role_admin]",
            grants.Replace("\r\n", "\n"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[tb_app].[AdminCreateManualClientInfoClient]\n    TO [tb_role_user]",
            grants.Replace("\r\n", "\n"),
            StringComparison.Ordinal);
        Assert.Contains("AdminCreateManualClientInfoClient", verifier, StringComparison.Ordinal);
        Assert.Contains("ManualClientInfoCreationAvailable", verifier, StringComparison.Ordinal);

        Assert.Contains("CreateManualClientInfoClient", repository, StringComparison.Ordinal);
        Assert.Contains("@RequestId", repository, StringComparison.Ordinal);
        Assert.Contains(
            "ManualClientInfoCreationAvailable",
            sharedRepository,
            StringComparison.Ordinal);
        Assert.Contains(
            "ManualClientInfoCreation: false",
            sharedRepository,
            StringComparison.Ordinal);

        Assert.Contains("Content=\"+ New client\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"{Binding CanCreateClientInfoClient}\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateManualClientInfoClient_Click",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "OpenClientInfoBetaWindow(created)",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains("_currentUser.CanManageClients", workspace, StringComparison.Ordinal);
        Assert.Contains(
            "if (!isImportWorkspace || !client.IsLive)",
            workspace,
            StringComparison.Ordinal);

        var created = new ClientInfoClientSummary
        {
            ClientId = 42,
            ClientName = "New Client",
            IsActive = true,
            ReviewStatus = "Unverified",
            CutoverState = "Complete",
            IsLive = true
        };
        Assert.Equal("Live", created.DisplayStatus);
        Assert.Equal("Unverified", created.ReviewStatus);
    }

    [Fact]
    public void Microsoft365LicenseCatalogUsesOnlyCsriSoldLicenses()
    {
        Assert.Equal(
            [
                "Microsoft 365 Business Basic",
                "Microsoft 365 Business Standard",
                "Microsoft 365 Business Premium",
                "Exchange Online Plan 1",
                "Exchange Online Plan 2"
            ],
            Microsoft365LicenseCatalog.All);
        Assert.Equal(
            Microsoft365LicenseCatalog.BusinessPremium,
            Microsoft365LicenseCatalog.Normalize("Business Premium"));
        Assert.Equal(
            Microsoft365LicenseCatalog.ExchangeOnlinePlan1,
            Microsoft365LicenseCatalog.Normalize("Exchange Online P1"));
        Assert.Empty(Microsoft365LicenseCatalog.Normalize("Office 365 E3"));
    }

    [Fact]
    public void DemoClientIsCompleteDeterministicAndClearlyIsolated()
    {
        var demo = ClientInfoDemoData.Create();

        Assert.True(demo.Summary.IsDemo);
        Assert.Equal("Demo Client", demo.Summary.ClientName);
        Assert.True(demo.Summary.ClientId < 0);
        Assert.Equal("DEMO", demo.Summary.InternalIdLabel);
        Assert.Equal("Demo Client", demo.Snapshot.Profile.ClientName);
        Assert.Equal(2, demo.Snapshot.Locations.Count);
        Assert.Equal(3, demo.Snapshot.People.Count);
        Assert.Equal(15, demo.Snapshot.Resources.Count);
        Assert.Equal(22, demo.Snapshot.Credentials.Count);
        Assert.Equal(22, demo.Summary.CredentialCount);
        Assert.Equal(22, demo.SecretValues.Count);
        Assert.Equal(3, demo.Equipment.Count);
        Assert.Equal(
            ClientInfoResourceCategories.All,
            demo.Snapshot.Resources.Select(resource => resource.Category).Distinct());
        Assert.All(
            ClientInfoResourceCategories.All,
            category => Assert.NotEmpty(ClientInfoCategoryOverviewBuilder.Build(
                category,
                demo.Snapshot.Resources.Where(resource => resource.Category == category).ToArray(),
                demo.Snapshot.Credentials.Where(credential =>
                    ClientInfoResourceCategories.ClassifyCredential(credential) == category).ToArray())));
    }

    [Fact]
    public void BlankResourceAccessDoesNotCreateCredentialOrSecret()
    {
        var resource = NewSavedResource();

        var result = ClientInfoBetaViewModel.SaveLinkedResourceAccess(
            42,
            resource,
            resource.Category,
            " ",
            "",
            _ => throw new InvalidOperationException("Credential should not be saved."),
            (_, _, _) => throw new InvalidOperationException("Secret should not be saved."));

        Assert.Null(result);
    }

    [Fact]
    public void UsernameOnlyResourceAccessLinksTheExactSavedResource()
    {
        var resource = NewSavedResource();
        ClientInfoCredential? requestedCredential = null;
        var secretSaveCount = 0;

        var result = ClientInfoBetaViewModel.SaveLinkedResourceAccess(
            42,
            resource,
            resource.Category,
            " apc-admin ",
            "",
            credential =>
            {
                requestedCredential = credential;
                return credential with { CredentialId = 6123 };
            },
            (_, _, _) =>
            {
                secretSaveCount++;
                return new ClientInfoSecretSummary();
            });

        Assert.NotNull(result);
        Assert.NotNull(requestedCredential);
        Assert.Equal(resource.ResourceId, requestedCredential!.ResourceId);
        Assert.Equal("apc-admin", requestedCredential.Username);
        Assert.Equal("UPS", requestedCredential.Category);
        Assert.Equal(resource.AddressOrUrl, requestedCredential.LoginUrl);
        Assert.Equal("Server Room UPS login", requestedCredential.Name);
        Assert.Equal(
            $"resource-primary-access-{resource.ResourceId}",
            requestedCredential.LocalKey);
        Assert.Equal(0, secretSaveCount);
    }

    [Fact]
    public void PasswordOnlyResourceAccessUsesReturnedCredentialIdAndSecretPath()
    {
        var resource = NewSavedResource() with
        {
            ReviewStatus = "Verified"
        };
        var calls = new List<string>();
        ClientInfoCredential? requestedCredential = null;
        ClientInfoSecretSummary? requestedSecret = null;
        string? requestedPassword = null;
        bool? requestedVerified = null;

        ClientInfoBetaViewModel.SaveLinkedResourceAccess(
            42,
            resource,
            resource.Category,
            "",
            "correct horse battery staple",
            credential =>
            {
                calls.Add("credential");
                requestedCredential = credential;
                return credential with { CredentialId = 7331 };
            },
            (secret, password, verified) =>
            {
                calls.Add("secret");
                requestedSecret = secret;
                requestedPassword = password;
                requestedVerified = verified;
                return secret;
            });

        Assert.Equal(["credential", "secret"], calls);
        Assert.Equal(7331, requestedSecret!.CredentialId);
        Assert.Equal("Password", requestedSecret.SecretType);
        Assert.Equal("Password", requestedSecret.SecretLabel);
        Assert.Equal("correct horse battery staple", requestedPassword);
        Assert.True(requestedVerified);
        Assert.DoesNotContain(
            "correct horse battery staple",
            $"{requestedCredential!.Name} {requestedCredential.Username} "
            + $"{requestedCredential.LoginUrl} {requestedCredential.Notes}",
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResourceAddUsesTabbedAccessAndPasswordSideKeepsManualLinking()
    {
        var editor = Read("ClientInfoRecordEditorWindow.cs");
        var viewModel = Read("ViewModels", "ClientInfoBetaViewModel.cs");

        Assert.Contains("new TabControl", editor, StringComparison.Ordinal);
        Assert.Contains("PasswordBox", editor, StringComparison.Ordinal);
        Assert.Contains("Tab = \"Details\"", viewModel, StringComparison.Ordinal);
        Assert.Contains(
            "Tab: \"Passwords\"",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains("Tab: \"AD login\"", viewModel, StringComparison.Ordinal);
        Assert.Contains(
            "Tab: \"Microsoft 365 login\"",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains("IsBoolean: true", viewModel, StringComparison.Ordinal);
        Assert.Contains(
            "VisibleWhenKey: \"m365sameasad\"",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "Options: Microsoft365LicenseCatalog.All",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "AllowBlankSelection: true",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "SaveUserMicrosoft365Credential",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "RefreshConditionalVisibility",
            editor,
            StringComparison.Ordinal);
        Assert.Contains("access_new_name", viewModel, StringComparison.Ordinal);
        Assert.Contains("BuildResourceAccessSlots", viewModel, StringComparison.Ordinal);
        Assert.Contains(
            "ResourceAccessEditorKey(slot.Key, \"username\")",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResourceAccessEditorKey(slot.Key, \"password\")",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains("New additional login", viewModel, StringComparison.Ordinal);
        Assert.Contains("SaveResourceAccessEdits", viewModel, StringComparison.Ordinal);
        Assert.Contains(
            "ResourceId = savedResource.ResourceId",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "new(\"resource\", \"System / service\"",
            viewModel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ClientProfileWindowsDoNotStayAboveTheMainWorkspace()
    {
        var mainWindow = Read("MainWindow.xaml.cs");
        var legacyStart = mainWindow.IndexOf(
            "var profileWindow = new ClientInfoWindow",
            StringComparison.Ordinal);
        var legacyEnd = mainWindow.IndexOf(
            "private void ClientInfoClientListBox_MouseDoubleClick",
            legacyStart,
            StringComparison.Ordinal);
        var canonicalStart = mainWindow.IndexOf(
            "var canonicalWindow = new ClientInfoBetaWindow",
            StringComparison.Ordinal);
        var canonicalEnd = mainWindow.IndexOf(
            "private void ClientInfoImportClientListBox_MouseDoubleClick",
            canonicalStart,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "Owner = this",
            mainWindow[legacyStart..legacyEnd],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Owner = this",
            mainWindow[canonicalStart..canonicalEnd],
            StringComparison.Ordinal);
        Assert.Contains(
            "profileWindow.Show();",
            mainWindow[legacyStart..legacyEnd],
            StringComparison.Ordinal);
        Assert.Contains(
            "canonicalWindow.Show();",
            mainWindow[canonicalStart..canonicalEnd],
            StringComparison.Ordinal);
    }

    private static ClientInfoResource NewSavedResource() => new()
    {
        ResourceId = 9001,
        ClientId = 42,
        ResourceType = ClientInfoResourceCategories.Encode(
            ClientInfoResourceCategories.ServersInfrastructure,
            "UPS"),
        Name = "Server Room UPS",
        AddressOrUrl = "https://10.20.0.18",
        IsActive = true,
        ReviewStatus = "Unverified"
    };

    [Fact]
    public void ResourceCategoriesProvideContextualFormsAndCompactViews()
    {
        foreach (var category in ClientInfoResourceCategories.All)
        {
            Assert.NotEmpty(
                ClientInfoResourceFieldDefinitions.TypeOptionsForCategory(category));
            Assert.Contains(
                category,
                ClientInfoResourceFieldDefinitions.EditorDescriptionForCategory(category),
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "Backup Schedule",
            ClientInfoResourceFieldDefinitions
                .ForEditorCategory(ClientInfoResourceCategories.Backup)
                .Select(field => field.FieldLabel));
        Assert.Contains(
            "Policy / Monitoring",
            ClientInfoResourceFieldDefinitions
                .ForEditorCategory(ClientInfoResourceCategories.Security)
                .Select(field => field.FieldLabel));
        Assert.DoesNotContain(
            "Antivirus / EDR",
            ClientInfoResourceFieldDefinitions.TypeOptionsForCategory(
                ClientInfoResourceCategories.Backup));
        Assert.Contains(
            "Antivirus / EDR",
            ClientInfoResourceFieldDefinitions.TypeOptionsForCategory(
                ClientInfoResourceCategories.Security));
        Assert.Contains(
            "Email Security / Spam Filtering",
            ClientInfoResourceFieldDefinitions.TypeOptionsForCategory(
                ClientInfoResourceCategories.DomainsEmail));
        Assert.DoesNotContain(
            "Spam Filtering",
            ClientInfoResourceFieldDefinitions.TypeOptionsForCategory(
                ClientInfoResourceCategories.Security));
        Assert.Contains(
            "Account Number",
            ClientInfoResourceFieldDefinitions
                .ForEditorCategory(ClientInfoResourceCategories.ConnectionInternet)
                .Select(field => field.FieldLabel));
        Assert.Contains(
            "Bandwidth",
            ClientInfoResourceFieldDefinitions
                .ForEditorCategory(ClientInfoResourceCategories.ConnectionInternet)
                .Select(field => field.FieldLabel));
        var connectionFields = ClientInfoResourceFieldDefinitions
            .ForEditorCategory(ClientInfoResourceCategories.ConnectionInternet);
        var assignmentField = Assert.Single(
            connectionFields,
            field => field.FieldKey == "ip_assignment_type");
        Assert.Contains("Static block", assignmentField.Options ?? []);
        Assert.True(assignmentField.AllowCustomValue);
        Assert.Equal(
            "Number",
            Assert.Single(
                connectionFields,
                field => field.FieldKey == "usable_static_ip_count").ValueType);
        Assert.True(Assert.Single(
            connectionFields,
            field => field.FieldKey == "static_ip_addresses").IsMultiline);
        Assert.Contains(
            connectionFields,
            field => field.FieldKey == "static_ip_range_start");
        Assert.Contains(
            connectionFields,
            field => field.FieldKey == "static_ip_range_end");
        Assert.Contains(
            "Email Security Service",
            ClientInfoResourceFieldDefinitions
                .ForEditorCategory(ClientInfoResourceCategories.DomainsEmail)
                .Select(field => field.FieldLabel));
        var applicationFields = ClientInfoResourceFieldDefinitions
            .ForEditorCategory(ClientInfoResourceCategories.ApplicationsCloud)
            .Select(field => field.FieldKey)
            .ToArray();
        Assert.DoesNotContain("primary_ip", applicationFields);
        Assert.DoesNotContain("admin_portal", applicationFields);
        Assert.DoesNotContain("support_contact", applicationFields);
        Assert.True(ClientInfoResourceFieldDefinitions.IsStandardField(
            ClientInfoResourceCategories.ApplicationsCloud,
            "primary_ip"));
        Assert.True(ClientInfoResourceFieldDefinitions.IsStandardField(
            ClientInfoResourceCategories.ApplicationsCloud,
            "admin_portal"));
        Assert.True(ClientInfoResourceFieldDefinitions.IsStandardField(
            ClientInfoResourceCategories.ApplicationsCloud,
            "support_contact"));
        Assert.Equal(
            "Domains & Email / Spam Filtering",
            ClientInfoResourceCategories.Encode(
                ClientInfoResourceCategories.Security,
                "Spam Filtering"));

        var xaml = Read("ClientInfoBetaWindow.xaml");
        var viewModel = Read("ViewModels", "ClientInfoBetaViewModel.cs");
        var resourceGrid = Read("Controls", "ClientInfoResourceDataGrid.cs");
        Assert.Contains("Text=\"Overview\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"CLIENT DESCRIPTION\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"SERVER LINKS\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"CLIENT FOLDER\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"LEGACY CLIENT INFO SHEET\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Read-only reference after migration\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ClientFolderPath", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding LegacyClientInfoSheetPath", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenClientFolderCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenLegacyClientInfoSheetCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"72\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenServerLink", viewModel, StringComparison.Ordinal);
        Assert.Contains("Text=\"Full list\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ResizeDirection=\"Rows\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"At-a-glance\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("UseCompactView", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Move\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OverviewSections", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Selected record\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedOverviewLabel}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Key=\"LinkedCredentialCardTemplate\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Passwords &amp; access\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding SelectedCredentials}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Secrets}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding LinkedObjectDisplay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<TabItem Header=\"Passwords\">", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Grid Width=\"500\" MinHeight=\"170\"", xaml, StringComparison.Ordinal);
        Assert.True(
            xaml.IndexOf("Header=\"Needs Sorting\"", StringComparison.Ordinal)
            > xaml.IndexOf("Header=\"Other Information\"", StringComparison.Ordinal));
        var selectedRecordMarkup = xaml[
            xaml.IndexOf("ItemsSource=\"{Binding OverviewSections}\"", StringComparison.Ordinal)..
            xaml.IndexOf("<GridSplitter Grid.Row=\"2\"", StringComparison.Ordinal)];
        Assert.DoesNotContain(
            "Text=\"{Binding Description}\"",
            selectedRecordMarkup,
            StringComparison.Ordinal);
        Assert.Contains("controls:ClientInfoResourceDataGrid", xaml, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource {x:Type DataGrid}}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsReadOnly=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Select text and press Ctrl+C to copy", xaml, StringComparison.Ordinal);
        Assert.Contains("ClipboardCopyMode=\"IncludeHeader\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Technician quick reference", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding QuickReferenceSections}\"", xaml, StringComparison.Ordinal);
        var quickReferenceMarkup = xaml[
            xaml.IndexOf("ItemsSource=\"{Binding QuickReferenceSections}\"", StringComparison.Ordinal)..
            xaml.IndexOf("<TabItem Header=\"Locations\">", StringComparison.Ordinal)];
        Assert.DoesNotContain("Text=\"{Binding Description}\"", quickReferenceMarkup, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"OverviewFieldTemplate\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DataContext.RevealSecretCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("DataContext.CopySecretCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("InlineDisplayValue", xaml, StringComparison.Ordinal);
        Assert.Contains("InlineRevealLabel", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("new ClientInfoSecretRevealWindow", viewModel, StringComparison.Ordinal);
        Assert.Contains("<TabItem Header=\"Locations\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<TabItem Header=\"Users\">", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"AD Username\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"AD Password\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Microsoft 365\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"365 License\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"365 uses AD login\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"365 Username\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"365 Password\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Binding=\"{Binding Microsoft365UsesAdLogin, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Microsoft365Password", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"PC Name\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PART_RightHeaderGripper", xaml, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibility\" Value=\"Auto", xaml, StringComparison.Ordinal);
        Assert.Contains("ApplyColumnWidths", Read("ClientInfoBetaWindow.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("SaveColumnWidths", Read("ClientInfoBetaWindow.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("QuickReferenceSections", viewModel, StringComparison.Ordinal);
        Assert.Contains("SelectMany(group => group.AllOverviewSections)", viewModel, StringComparison.Ordinal);
        Assert.Contains("QuickReferencePriority", viewModel, StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(group, NeedsSortingGroup)", viewModel, StringComparison.Ordinal);
        Assert.Contains("or \"Core infrastructure\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("or \"Other backup\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("or \"Other security\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("or \"Remote Access\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("or \"Veeam\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("or \"Vendors & services\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("<TabItem Header=\"Backup\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding BackupGroup}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<TabItem Header=\"Security\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding SecurityGroup}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Backup &amp; Security\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ForEditorCategory(group.CategoryName)", resourceGrid, StringComparison.Ordinal);
        Assert.DoesNotContain(".Where(field => field.ShowInGrid)", resourceGrid, StringComparison.Ordinal);
        Assert.Contains("TextColumn(\"Notes\", \"Notes\"", resourceGrid, StringComparison.Ordinal);
        Assert.Contains("TextColumn(\"Active\", \"IsActive\"", resourceGrid, StringComparison.Ordinal);
        Assert.Contains("TextColumn(\"Last verified\", \"LastVerifiedAtUtc\"", resourceGrid, StringComparison.Ordinal);
        Assert.Contains("TextColumn(\"Updated\", \"UpdatedAtUtc\"", resourceGrid, StringComparison.Ordinal);
        Assert.True(
            xaml.Split("ClipboardCopyMode=\"IncludeHeader\"", StringSplitOptions.None).Length - 1 >= 7);
        Assert.DoesNotContain("MouseDoubleClick=\"ResourceOverview_DoubleClick\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("\"category\",\n                \"Category\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("TypeOptionsForCategory", viewModel, StringComparison.Ordinal);
        Assert.Contains("MoveResourceCommand", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretSummaryRevealsAndHidesOnlyInMemoryForTheCurrentView()
    {
        var secret = new ClientInfoSecretSummary
        {
            SecretId = 91,
            CredentialId = 9,
            SecretLabel = "Admin password",
            SecretType = "Password",
            IsCurrent = true
        };

        Assert.False(secret.IsRevealedInline);
        Assert.Equal(new string('\u2022', 8), secret.InlineDisplayValue);
        Assert.Equal("Reveal", secret.InlineRevealLabel);

        secret.RevealInline("temporary-secret");

        Assert.True(secret.IsRevealedInline);
        Assert.Equal("temporary-secret", secret.InlineDisplayValue);
        Assert.Equal("Hide", secret.InlineRevealLabel);

        secret.HideInline();

        Assert.False(secret.IsRevealedInline);
        Assert.Equal(new string('\u2022', 8), secret.InlineDisplayValue);
        Assert.Equal("Reveal", secret.InlineRevealLabel);
    }

    [Fact]
    public void CategoryOverviewShowsImportantFactsWithoutGrowingOneCardPerItem()
    {
        var resources = Enumerable.Range(1, 8).Select(index => new ClientInfoResource
        {
            ResourceId = index,
            ResourceType = ClientInfoResourceCategories.Encode(
                ClientInfoResourceCategories.ConnectionInternet,
                "WatchGuard Firewall"),
            Name = $"Firebox {index}",
            Provider = "WatchGuard",
            LocationName = "Main Office",
            AddressOrUrl = $"https://10.0.0.{index}",
            Notes = $"Unimportant item note {index}",
            ReviewStatus = "Verified",
            Fields =
            [
                new ClientInfoResourceField
                {
                    FieldKey = "public_wan_ip",
                    FieldLabel = "Public / WAN IP",
                    ValueText = $"203.0.113.{index}",
                    SortOrder = 10
                },
                new ClientInfoResourceField
                {
                    FieldKey = "custom.authpoint_tenant",
                    FieldLabel = "AuthPoint Tenant",
                    ValueText = $"Unimportant custom value {index}",
                    SortOrder = 150
                }
            ]
        }).ToArray();
        var sections = ClientInfoCategoryOverviewBuilder.Build(
            ClientInfoResourceCategories.ConnectionInternet,
            resources,
            [new ClientInfoCredential
            {
                CredentialId = 88,
                ResourceId = 1,
                Name = "WatchGuard Admin",
                Category = "WatchGuard",
                Username = "csriadmin",
                LoginUrl = "https://10.0.0.1",
                SecretCount = 2,
                Secrets =
                [
                    new ClientInfoSecretSummary
                    {
                        SecretId = 880,
                        CredentialId = 88,
                        SecretType = "Password",
                        SecretLabel = "Password",
                        IsCurrent = true
                    },
                    new ClientInfoSecretSummary
                    {
                        SecretId = 881,
                        CredentialId = 88,
                        SecretType = "RecoveryCode",
                        SecretLabel = "Recovery code",
                        IsCurrent = true
                    }
                ]
            }]);

        var watchGuard = Assert.Single(sections);
        Assert.Equal("Connection", watchGuard.Title);
        Assert.DoesNotContain(sections.SelectMany(section => section.Fields),
            field => field.Label == "Notes" || field.Label == "AuthPoint Tenant");
        Assert.Contains(sections.SelectMany(section => section.Fields),
            field => field.Label == "External IP"
                     && field.Value.Contains("+5 more in full list"));
        var access = Assert.Single(sections.SelectMany(section => section.Fields),
            field => field.Label == "Admin password");
        Assert.Equal(2, access.Secrets.Count);
        Assert.DoesNotContain(sections.SelectMany(section => section.Fields),
            field => field.Label == "Protected values");
    }

    [Fact]
    public void CategoryOverviewTracksTheSelectedRecordWhileQuickReferenceSourceStaysComplete()
    {
        ClientInfoResource Connection(long id, string name, string address) => new()
        {
            ResourceId = id,
            ResourceType = ClientInfoResourceCategories.Encode(
                ClientInfoResourceCategories.ConnectionInternet,
                "WatchGuard Firewall"),
            Name = name,
            Fields =
            [
                new ClientInfoResourceField
                {
                    FieldKey = "public_wan_ip",
                    FieldLabel = "Public / WAN IP",
                    ValueText = address
                }
            ]
        };

        var primary = Connection(201, "Primary Firebox", "203.0.113.10");
        var backup = Connection(202, "Backup Firebox", "198.51.100.20");
        var group = new ClientInfoResourceGroup(
            ClientInfoResourceCategories.ConnectionInternet,
            "Connections");

        group.Replace([primary, backup], [], []);

        Assert.Same(primary, group.SelectedResource);
        Assert.Equal(
            "203.0.113.10",
            Assert.Single(group.OverviewSections)
                .Fields.Single(field => field.Label == "External IP").Value);
        var completeExternalIps = Assert.Single(group.AllOverviewSections)
            .Fields.Single(field => field.Label == "External IP").Value;
        Assert.Contains("203.0.113.10", completeExternalIps, StringComparison.Ordinal);
        Assert.Contains("198.51.100.20", completeExternalIps, StringComparison.Ordinal);

        group.SelectedResource = backup;

        Assert.Equal(
            "198.51.100.20",
            Assert.Single(group.OverviewSections)
                .Fields.Single(field => field.Label == "External IP").Value);
        Assert.Equal(completeExternalIps, Assert.Single(group.AllOverviewSections)
            .Fields.Single(field => field.Label == "External IP").Value);
    }

    [Fact]
    public void ObjectPasswordsFollowOnlyTheExplicitlyLinkedSelectedRecord()
    {
        ClientInfoResource Resource(long id, string name) => new()
        {
            ResourceId = id,
            ResourceType = ClientInfoResourceCategories.Encode(
                ClientInfoResourceCategories.ApplicationsCloud,
                "Cloud Application"),
            Name = name
        };

        ClientInfoCredential Credential(
            long id,
            long? resourceId,
            bool active = true,
            long? personId = null) => new()
        {
            CredentialId = id,
            ResourceId = resourceId,
            PersonId = personId,
            Name = $"Login {id}",
            Category = "Applications & Cloud",
            IsActive = active,
            Secrets =
            [
                new ClientInfoSecretSummary
                {
                    SecretId = id * 10,
                    CredentialId = id,
                    SecretLabel = "Password",
                    IsCurrent = true
                }
            ]
        };

        var primary = Resource(401, "Primary portal");
        var secondary = Resource(402, "Secondary portal");
        var primaryLogin = Credential(501, primary.ResourceId);
        var primarySecondLogin = Credential(506, primary.ResourceId);
        var primaryThirdLogin = Credential(507, primary.ResourceId);
        var secondaryLogin = Credential(502, secondary.ResourceId);
        var inactiveLogin = Credential(503, primary.ResourceId, active: false);
        var unlinkedLogin = Credential(504, null);
        var personOnlyLogin = Credential(505, null, personId: 77);
        var group = new ClientInfoResourceGroup(
            ClientInfoResourceCategories.ApplicationsCloud,
            "Applications");

        group.Replace(
            [primary, secondary],
            [
                primaryLogin,
                primarySecondLogin,
                primaryThirdLogin,
                secondaryLogin,
                inactiveLogin,
                unlinkedLogin,
                personOnlyLogin
            ],
            [unlinkedLogin]);

        Assert.Same(primary, group.SelectedResource);
        Assert.Equal(
            [primaryLogin, primarySecondLogin, primaryThirdLogin],
            group.SelectedCredentials);
        Assert.DoesNotContain(inactiveLogin, group.SelectedCredentials);
        Assert.DoesNotContain(unlinkedLogin, group.SelectedCredentials);
        Assert.DoesNotContain(personOnlyLogin, group.SelectedCredentials);
        Assert.DoesNotContain(
            group.OverviewSections.SelectMany(section => section.Fields)
                .SelectMany(field => field.Secrets),
            secret => secret.CredentialId == unlinkedLogin.CredentialId);
        Assert.Equal(
            [501L, 506L, 507L],
            group.OverviewSections.SelectMany(section => section.Fields)
                .SelectMany(field => field.CredentialIds)
                .Distinct()
                .OrderBy(id => id));
        Assert.DoesNotContain(
            group.OverviewSections.SelectMany(section => section.Fields),
            field => field.Label == "More access");

        group.SelectedResource = secondary;

        Assert.Same(secondaryLogin, Assert.Single(group.SelectedCredentials));
        Assert.Same(
            secondaryLogin.Secrets[0],
            Assert.Single(group.SelectedCredentials).Secrets[0]);
    }

    [Fact]
    public void MasterPasswordRowsIdentifyTheirLinkedSystemOrUser()
    {
        var systemCredential = new ClientInfoCredential
        {
            CredentialId = 601,
            ResourceId = 701,
            Name = "Firewall admin"
        };
        var userCredential = new ClientInfoCredential
        {
            CredentialId = 602,
            PersonId = 801,
            Name = "User AD"
        };
        var unlinkedCredential = new ClientInfoCredential
        {
            CredentialId = 603,
            Name = "Legacy login"
        };

        var decorated = ClientInfoBetaViewModel.AddCredentialDisplayLinks(
            [systemCredential, userCredential, unlinkedCredential],
            [new ClientInfoResource { ResourceId = 701, Name = "Main Firebox" }],
            [new ClientInfoPerson { PersonId = 801, DisplayName = "Alex Morgan" }]);

        Assert.Equal("Main Firebox", decorated[0].LinkedObjectDisplay);
        Assert.Equal("Alex Morgan", decorated[1].LinkedObjectDisplay);
        Assert.Equal("(Unlinked)", decorated[2].LinkedObjectDisplay);
        Assert.Same(systemCredential.Secrets, decorated[0].Secrets);
    }

    [Fact]
    public void WifiOverviewContainsOnlyManagementAdminAndEverySsid()
    {
        ClientInfoSecretSummary Secret(long secretId, long credentialId) => new()
        {
            SecretId = secretId,
            CredentialId = credentialId,
            SecretType = "Password",
            SecretLabel = "Password",
            IsCurrent = true
        };
        ClientInfoCredential Credential(
            long id,
            string name,
            string username,
            string loginUrl) => new()
        {
            CredentialId = id,
            ResourceId = 41,
            Name = name,
            Category = "Wireless",
            Username = username,
            LoginUrl = loginUrl,
            IsActive = true,
            Secrets = [Secret(id * 10, id)]
        };

        var sections = ClientInfoCategoryOverviewBuilder.Build(
            ClientInfoResourceCategories.Wifi,
            [new ClientInfoResource
            {
                ResourceId = 41,
                ResourceType = ClientInfoResourceCategories.Encode(
                    ClientInfoResourceCategories.Wifi,
                    "Wireless Controller"),
                Name = "Main Wi-Fi",
                AddressOrUrl = "https://wifi.example.test",
                Fields =
                [
                    new ClientInfoResourceField
                    {
                        FieldKey = "ssid",
                        FieldLabel = "SSID",
                        ValueText = "Example-Staff"
                    },
                    new ClientInfoResourceField
                    {
                        FieldKey = "guest_ssid",
                        FieldLabel = "Guest SSID",
                        ValueText = "Example-Guest"
                    },
                    new ClientInfoResourceField
                    {
                        FieldKey = "vlan",
                        FieldLabel = "VLAN",
                        ValueText = "20"
                    }
                ]
            }],
            [
                Credential(501, "Wireless Admin", "wifi-admin", "https://wifi.example.test"),
                Credential(502, "Staff SSID Password", "", ""),
                Credential(503, "Guest SSID Password", "", "")
            ]);

        var fields = Assert.Single(sections).Fields;
        Assert.Equal(
            ["Type", "Management URL", "Admin username / password", "SSID", "Guest SSID"],
            fields.Select(field => field.Label));
        Assert.Equal("wifi-admin", fields.Single(field =>
            field.Label == "Admin username / password").Value);
        Assert.Single(fields.Single(field => field.Label == "SSID").Secrets);
        Assert.Equal(502, fields.Single(field => field.Label == "SSID").Secrets[0].CredentialId);
        Assert.Single(fields.Single(field => field.Label == "Guest SSID").Secrets);
        Assert.Equal(503, fields.Single(field => field.Label == "Guest SSID").Secrets[0].CredentialId);
        Assert.DoesNotContain(fields, field => field.Label is "VLAN" or "Security" or "Management IP");
    }

    [Fact]
    public void ConnectionOverviewUsesTheExactRequestedFieldsAndConditionalCsriAccess()
    {
        ClientInfoCredential Credential(long id, string name, string username = "") => new()
        {
            CredentialId = id,
            ResourceId = 61,
            Name = name,
            Category = "WatchGuard",
            Username = username,
            IsActive = true,
            Secrets =
            [
                new ClientInfoSecretSummary
                {
                    SecretId = id * 10,
                    CredentialId = id,
                    SecretType = "Password",
                    SecretLabel = "Password",
                    IsCurrent = true
                }
            ]
        };
        var resource = new ClientInfoResource
        {
            ResourceId = 61,
            ResourceType = ClientInfoResourceCategories.Encode(
                ClientInfoResourceCategories.ConnectionInternet,
                "WatchGuard Firewall"),
            Name = "Main Firebox",
            Fields =
            [
                new ClientInfoResourceField
                {
                    FieldKey = "public_wan_ip",
                    FieldLabel = "Public / WAN IP",
                    ValueText = "203.0.113.44"
                },
                new ClientInfoResourceField
                {
                    FieldKey = "device_model",
                    FieldLabel = "Device Model",
                    ValueText = "Firebox M390"
                },
                new ClientInfoResourceField
                {
                    FieldKey = "ssl_vpn_port",
                    FieldLabel = "SSL VPN Port",
                    ValueText = "4443"
                },
                new ClientInfoResourceField
                {
                    FieldKey = "gateway",
                    FieldLabel = "Gateway",
                    ValueText = "203.0.113.41"
                }
            ]
        };
        var credentials = new[]
        {
            Credential(601, "Status"),
            Credential(602, "WatchGuard Admin"),
            Credential(603, "Firebox Database CSRI"),
            Credential(604, "AuthPoint User", "authpoint-user@example.test"),
            Credential(605, "SSLVPN Password"),
            Credential(606, "WatchGuard Cloud", "cloud-user@example.test"),
            Credential(607, "WatchGuard AD Auth", "EXAMPLE\\wg-auth")
        };

        var fields = Assert.Single(ClientInfoCategoryOverviewBuilder.Build(
            ClientInfoResourceCategories.ConnectionInternet,
            [resource],
            credentials)).Fields;

        Assert.Equal(
            [
                "External IP",
                "SSL VPN port",
                "Model",
                "Status password",
                "Admin password",
                "Firebox-DB\\csri",
                "AuthPoint user",
                "SSL VPN password",
                "WatchGuard Cloud user / password",
                "WatchGuard AD auth user / password"
            ],
            fields.Select(field => field.Label));
        Assert.Equal("4443", fields[1].Value);
        Assert.DoesNotContain(fields, field => field.Label is "Gateway" or "Firmware" or "Subnet / CIDR");
        Assert.All(fields.Skip(3), field => Assert.NotEmpty(field.Secrets));

        var fallbackFields = Assert.Single(ClientInfoCategoryOverviewBuilder.Build(
            ClientInfoResourceCategories.ConnectionInternet,
            [resource],
            [Credential(608, "CSRIAdmin AuthPoint", "csriadmin")])).Fields;
        Assert.Contains(fallbackFields, field => field.Label == "CSRIAdmin AuthPoint");
        Assert.DoesNotContain(fallbackFields, field => field.Label == "Firebox-DB\\csri");
    }

    [Fact]
    public void ExplicitResourceLinksKeepCredentialsOnTheCorrectConnectionCard()
    {
        ClientInfoResource Resource(long id, string type, string name) => new()
        {
            ResourceId = id,
            ResourceType = ClientInfoResourceCategories.Encode(
                ClientInfoResourceCategories.ConnectionInternet,
                type),
            Name = name,
            Provider = type == "Internet Circuit" ? "Comcast Business" : "WatchGuard",
            IsActive = true
        };
        ClientInfoCredential Credential(long id, long resourceId, string name) => new()
        {
            CredentialId = id,
            ResourceId = resourceId,
            Name = name,
            Username = $"user-{id}",
            IsActive = true,
            Secrets =
            [
                new ClientInfoSecretSummary
                {
                    SecretId = id * 10,
                    CredentialId = id,
                    SecretType = "Password",
                    SecretLabel = "Password",
                    IsCurrent = true
                }
            ]
        };

        var sections = ClientInfoCategoryOverviewBuilder.Build(
            ClientInfoResourceCategories.ConnectionInternet,
            [
                Resource(71, "WatchGuard Firewall", "Main WatchGuard"),
                Resource(72, "Internet Circuit", "Primary Comcast Circuit")
            ],
            [
                Credential(701, 71, "Comcast-managed WatchGuard Admin"),
                Credential(702, 72, "WatchGuard Comcast ISP Portal")
            ]);

        var connection = Assert.Single(
            sections,
            section => section.Title == "Connection");
        var internet = Assert.Single(
            sections,
            section => section.Title == "Internet & circuits");
        Assert.Contains(
            connection.Fields.Single(field => field.Label == "Admin password").CredentialIds,
            id => id == 701);
        Assert.DoesNotContain(
            connection.Fields.SelectMany(field => field.CredentialIds),
            id => id == 702);
        Assert.Equal(
            [702L],
            internet.Fields.Single(field =>
                field.Label == "Account username / password").CredentialIds);
    }

    [Fact]
    public void StaticIpBlocksUseAnExplicitListOrRangeWithoutClutteringSingleStatics()
    {
        static ClientInfoResource Circuit(
            long id,
            params (string Key, string Value)[] values) => new()
            {
                ResourceId = id,
                ResourceType = ClientInfoResourceCategories.Encode(
                    ClientInfoResourceCategories.ConnectionInternet,
                    "Internet Circuit"),
                Name = $"Circuit {id}",
                Provider = "Example ISP",
                IsActive = true,
                Fields = values.Select((value, index) =>
                    new ClientInfoResourceField
                    {
                        FieldKey = value.Key,
                        FieldLabel = value.Key,
                        ValueText = value.Value,
                        SortOrder = index
                    }).ToArray()
            };

        static IReadOnlyList<ClientInfoOverviewField> Fields(
            ClientInfoResource resource) =>
            Assert.Single(ClientInfoCategoryOverviewBuilder.BuildSelected(
                ClientInfoResourceCategories.ConnectionInternet,
                resource,
                [])).Fields;

        var listed = Fields(Circuit(
            73,
            ("ip_assignment_type", "Static block"),
            ("static_ip_addresses", "not-an-ip, 203.0.113.26,\r\n203.0.113.27;203.0.113.26"),
            ("static_ip_range_start", "203.0.113.28"),
            ("static_ip_range_end", "203.0.113.30")));
        Assert.Equal(
            "Static block",
            listed.Single(field => field.Label == "IP assignment").Value);
        Assert.Equal(
            "2",
            listed.Single(field => field.Label == "Usable static IPs").Value);
        Assert.Equal(
            $"not-an-ip{Environment.NewLine}203.0.113.26{Environment.NewLine}203.0.113.27",
            listed.Single(field => field.Label == "Static IPs / range").Value);

        var ranged = Fields(Circuit(
            74,
            ("static_ip_range_start", "203.0.113.26"),
            ("static_ip_range_end", "203.0.113.30")));
        Assert.Equal(
            "Static block",
            ranged.Single(field => field.Label == "IP assignment").Value);
        Assert.Equal(
            "5",
            ranged.Single(field => field.Label == "Usable static IPs").Value);
        Assert.Equal(
            "203.0.113.26 – 203.0.113.30",
            ranged.Single(field => field.Label == "Static IPs / range").Value);

        var partial = Fields(Circuit(
            75,
            ("static_ip_range_start", "not-yet-assigned")));
        Assert.Equal(
            "Not entered",
            partial.Single(field => field.Label == "Usable static IPs").Value);
        Assert.Equal(
            "not-yet-assigned",
            partial.Single(field => field.Label == "Static IPs / range").Value);

        var single = Fields(Circuit(
            76,
            ("ip_assignment_type", "Single static"),
            ("public_wan_ip", "198.51.100.44")));
        Assert.DoesNotContain(single, field => field.Label == "IP assignment");
        Assert.DoesNotContain(single, field => field.Label == "Usable static IPs");
        Assert.DoesNotContain(single, field => field.Label == "Static IPs / range");

        var dynamic = Fields(Circuit(
            77,
            ("ip_assignment_type", "Dynamic"),
            ("usable_static_ip_count", "5"),
            ("static_ip_range_start", "203.0.113.26"),
            ("static_ip_range_end", "203.0.113.30")));
        Assert.Equal(
            "Dynamic",
            dynamic.Single(field => field.Label == "IP assignment").Value);
        Assert.DoesNotContain(dynamic, field => field.Label == "Usable static IPs");
        Assert.DoesNotContain(dynamic, field => field.Label == "Static IPs / range");
    }

    [Fact]
    public void RequestedNetworkRowsStayVisibleWhenInformationIsMissing()
    {
        var wifi = Assert.Single(ClientInfoCategoryOverviewBuilder.Build(
            ClientInfoResourceCategories.Wifi,
            [],
            [])).Fields;
        Assert.Equal(
            ["Type", "Management URL", "Admin username / password", "SSID / password"],
            wifi.Select(field => field.Label));
        Assert.All(wifi, field => Assert.Equal("Not entered", field.Value));

        var connection = Assert.Single(ClientInfoCategoryOverviewBuilder.Build(
            ClientInfoResourceCategories.ConnectionInternet,
            [],
            [])).Fields;
        Assert.Equal(
            [
                "External IP",
                "SSL VPN port",
                "Model",
                "Status password",
                "Admin password",
                "CSRIAdmin AuthPoint",
                "AuthPoint user",
                "SSL VPN password",
                "WatchGuard Cloud user / password",
                "WatchGuard AD auth user / password"
            ],
            connection.Select(field => field.Label));
        Assert.All(connection, field => Assert.Equal("Not entered", field.Value));
    }

    [Fact]
    public void DemoClientPopulatesEveryRequestedNetworkOverviewValueAndSecret()
    {
        var demo = ClientInfoDemoData.Create();
        var wifiResources = demo.Snapshot.Resources.Where(resource =>
            resource.Category == ClientInfoResourceCategories.Wifi).ToArray();
        var wifiResourceIds = wifiResources.Select(resource => resource.ResourceId).ToHashSet();
        var wifiCredentials = demo.Snapshot.Credentials.Where(credential =>
            credential.ResourceId.HasValue
            && wifiResourceIds.Contains(credential.ResourceId.Value)).ToArray();
        var wifi = Assert.Single(ClientInfoCategoryOverviewBuilder.Build(
            ClientInfoResourceCategories.Wifi,
            wifiResources,
            wifiCredentials)).Fields;
        Assert.Equal(
            ["Type", "Management URL", "Admin username / password", "SSID", "Guest SSID"],
            wifi.Select(field => field.Label));
        Assert.NotEmpty(wifi.Single(field => field.Label == "Admin username / password").Secrets);
        Assert.NotEmpty(wifi.Single(field => field.Label == "SSID").Secrets);
        Assert.NotEmpty(wifi.Single(field => field.Label == "Guest SSID").Secrets);

        var connectionResources = demo.Snapshot.Resources.Where(resource =>
            resource.Category == ClientInfoResourceCategories.ConnectionInternet).ToArray();
        var connectionResourceIds = connectionResources.Select(resource => resource.ResourceId).ToHashSet();
        var connectionCredentials = demo.Snapshot.Credentials.Where(credential =>
            credential.ResourceId.HasValue
            && connectionResourceIds.Contains(credential.ResourceId.Value)).ToArray();
        var connectionSections = ClientInfoCategoryOverviewBuilder.Build(
            ClientInfoResourceCategories.ConnectionInternet,
            connectionResources,
            connectionCredentials);
        var connection = Assert.Single(
            connectionSections,
            section => section.Title == "Connection").Fields;
        Assert.Equal(
            [
                "External IP",
                "SSL VPN port",
                "Model",
                "Status password",
                "Admin password",
                "Firebox-DB\\csri",
                "AuthPoint user",
                "SSL VPN password",
                "WatchGuard Cloud user / password",
                "WatchGuard AD auth user / password"
            ],
            connection.Select(field => field.Label));
        Assert.Equal("443", connection[1].Value);
        Assert.All(connection.Skip(3), field => Assert.NotEmpty(field.Secrets));
        var internet = Assert.Single(
            connectionSections,
            section => section.Title == "Internet & circuits").Fields;
        Assert.Equal(
            [
                "Provider",
                "Circuit ID",
                "Account number",
                "Service type",
                "Bandwidth",
                "Public IP",
                "Gateway",
                "Subnet / CIDR",
                "IP assignment",
                "Usable static IPs",
                "Static IPs / range",
                "Support contact",
                "Support phone",
                "Location",
                "Status",
                "Account username / password"
            ],
            internet.Select(field => field.Label));
        Assert.Equal("Comcast Business", internet[0].Value);
        Assert.Equal("DEMO-COMCAST-4821", internet[1].Value);
        Assert.Equal("DEMO-ACCOUNT-1048", internet[2].Value);
        Assert.Equal("1.25 Gbps down / 35 Mbps up", internet[4].Value);
        Assert.Equal("203.0.113.26", internet[5].Value);
        Assert.Equal("203.0.113.25", internet[6].Value);
        Assert.Equal("Static block", internet[8].Value);
        Assert.Equal("5", internet[9].Value);
        Assert.Equal("203.0.113.26 – 203.0.113.30", internet[10].Value);
        Assert.Equal("isp-admin@example.test", internet[^1].Value);
        Assert.NotEmpty(internet[^1].Secrets);
        Assert.DoesNotContain(internet, field => field.Label == "SSL VPN port");

        var ispResource = Assert.Single(
            connectionResources,
            resource => resource.TypeLabel == "Internet Circuit");
        var selectedIsp = Assert.Single(ClientInfoCategoryOverviewBuilder.BuildSelected(
            ClientInfoResourceCategories.ConnectionInternet,
            ispResource,
            connectionCredentials.Where(credential =>
                credential.ResourceId == ispResource.ResourceId).ToArray()));
        Assert.Equal("Internet & circuits", selectedIsp.Title);
        Assert.Equal(internet.Select(field => field.Label), selectedIsp.Fields.Select(field => field.Label));
        Assert.All(
            wifi.Concat(connection).Concat(internet).SelectMany(field => field.Secrets),
            secret => Assert.True(demo.SecretValues.ContainsKey(secret.SecretId)));
    }

    [Fact]
    public void Microsoft365AndDomainQuickReferenceStayFocusedOnDailyUse()
    {
        ClientInfoResource Resource(string category, string type, params (string Key, string Value)[] fields) =>
            new()
            {
                ResourceId = category == ClientInfoResourceCategories.ApplicationsCloud ? 1 : 2,
                ResourceType = ClientInfoResourceCategories.Encode(category, type),
                Name = type,
                AddressOrUrl = "https://admin.example.test",
                Fields = fields.Select((field, index) => new ClientInfoResourceField
                {
                    FieldKey = field.Key,
                    FieldLabel = field.Key,
                    ValueText = field.Value,
                    SortOrder = index
                }).ToArray()
            };

        var applications = ClientInfoCategoryOverviewBuilder.Build(
            ClientInfoResourceCategories.ApplicationsCloud,
            [
                Resource(ClientInfoResourceCategories.ApplicationsCloud, "Microsoft 365", ("tenant_instance", "example.onmicrosoft.com"), ("primary_ip", "198.51.100.12"), ("admin_portal", "https://admin.example.test"), ("plan", "Business Premium"), ("support_contact", "Support"), ("renewal_date", "2030-01-01")),
                Resource(ClientInfoResourceCategories.ApplicationsCloud, "ScreenConnect Remote Access", ("tenant_instance", "example"), ("primary_ip", "198.51.100.44"), ("admin_portal", "https://remote.example.test"), ("version", "25"), ("support_contact", "Help Desk"))
            ],
            []);
        var domain = ClientInfoCategoryOverviewBuilder.Build(
            ClientInfoResourceCategories.DomainsEmail,
            [
                Resource(ClientInfoResourceCategories.DomainsEmail, "Active Directory", ("domain_name", "example.local"), ("domain controller", "DC01")),
                Resource(ClientInfoResourceCategories.DomainsEmail, "Email Domain", ("domain_name", "example.test"), ("registrar", "Registrar"), ("dns_provider", "DNS host"), ("mail_provider", "Mail host"), ("expiration_date", "2030-01-01"))
            ],
            [
                new ClientInfoCredential
                {
                    CredentialId = 75,
                    Name = "Domain Admin",
                    Category = "Active Directory",
                    Username = "EXAMPLE\\admin",
                    LoginUrl = "https://dc01.example.local",
                    IsActive = true,
                    Secrets =
                    [
                        new ClientInfoSecretSummary
                        {
                            SecretId = 750,
                            CredentialId = 75,
                            SecretType = "Password",
                            SecretLabel = "Password",
                            IsCurrent = true
                        }
                    ]
                }
            ]);

        var microsoftFields = Assert.Single(applications, section => section.Title == "Microsoft 365").Fields;
        Assert.Equal(["Tenant / instance"], microsoftFields.Select(field => field.Label));
        var remoteFields = Assert.Single(applications, section => section.Title == "Remote Access").Fields;
        Assert.Equal(
            ["Services", "Portal / URL", "Tenant / instance", "Version"],
            remoteFields.Select(field => field.Label));
        Assert.DoesNotContain(
            applications.SelectMany(section => section.Fields),
            field => field.Label is "Admin portal" or "Primary IP" or "Support");
        var domainFields = Assert.Single(domain).Fields;
        Assert.Equal(
            ["AD domain", "Email domain", "Domain admin username / password"],
            domainFields.Select(field => field.Label));
        Assert.Equal("example.local", domainFields[0].Value);
        Assert.Equal("example.test", domainFields[1].Value);
        Assert.Equal("EXAMPLE\\admin", domainFields[2].Value);
        Assert.Single(domainFields[2].Secrets);
        Assert.DoesNotContain(
            domainFields,
            field => field.Value.Contains("https://", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BarracudaMailProviderDoesNotTurnAnEmailDomainIntoAnEmailSecurityObject()
    {
        ClientInfoResource Resource(long id, string type, string name) => new()
        {
            ResourceId = id,
            ResourceType = ClientInfoResourceCategories.Encode(
                ClientInfoResourceCategories.DomainsEmail,
                type),
            Name = name,
            Provider = "Barracuda",
            IsActive = true,
            Fields =
            [
                new ClientInfoResourceField
                {
                    FieldKey = "mail_provider",
                    FieldLabel = "Mail Provider",
                    ValueText = "Barracuda"
                }
            ]
        };

        var emailDomain = Assert.Single(ClientInfoCategoryOverviewBuilder.BuildSelected(
            ClientInfoResourceCategories.DomainsEmail,
            Resource(81, "Email Tenant", "Primary Email Domain"),
            []));
        var emailSecurity = Assert.Single(ClientInfoCategoryOverviewBuilder.BuildSelected(
            ClientInfoResourceCategories.DomainsEmail,
            Resource(82, "Email Security / Spam Filtering", "Barracuda Email Security"),
            []));

        Assert.Equal("Domain & AD", emailDomain.Title);
        Assert.Equal("Email Security", emailSecurity.Title);
    }

    [Fact]
    public void CloudAccountsQuickReferenceCombinesOnlyTheRequestedLogins()
    {
        ClientInfoCredential Credential(
            long id,
            string name,
            string username,
            string loginUrl) => new()
        {
            CredentialId = id,
            Name = name,
            Category = "Cloud",
            Username = username,
            LoginUrl = loginUrl,
            IsActive = true,
            Secrets =
            [
                new ClientInfoSecretSummary
                {
                    SecretId = id * 10,
                    CredentialId = id,
                    SecretType = "Password",
                    SecretLabel = "Password",
                    IsCurrent = true
                }
            ]
        };

        var section = ClientInfoCategoryOverviewBuilder.BuildCloudAccounts(
        [
            Credential(701, "Barracuda Admin", "barracuda-admin", "https://barracuda.example.test"),
            Credential(702, "ESET Protect Admin", "eset-admin", "https://eset.example.test"),
            Credential(703, "M365 Global Admin", "m365-admin@example.test", "https://admin.microsoft.com"),
            Credential(704, "Unrelated Cloud App", "other-admin", "https://other.example.test")
        ]);

        Assert.Equal("Cloud Accounts", section.Title);
        Assert.Equal(
            ["Barracuda", "ESET", "Microsoft 365"],
            section.Fields.Select(field => field.Label));
        Assert.Equal("barracuda-admin", section.Fields[0].Value);
        Assert.Equal("eset-admin", section.Fields[1].Value);
        Assert.Equal("m365-admin@example.test", section.Fields[2].Value);
        Assert.All(section.Fields, field => Assert.Single(field.Secrets));
        Assert.DoesNotContain(
            section.Fields,
            field => field.Value.Contains("https://", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DemoOverviewPlacesTheActualFireDrillGroupsInTheirCanonicalCategories()
    {
        var demo = ClientInfoDemoData.Create().Snapshot;

        string[] Titles(string category) => ClientInfoCategoryOverviewBuilder.Build(
                category,
                demo.Resources.Where(resource => resource.Category == category).ToArray(),
                demo.Credentials.Where(credential =>
                    ClientInfoResourceCategories.ClassifyCredential(credential) == category).ToArray())
            .Select(section => section.Title)
            .ToArray();

        Assert.Contains("ILO", Titles(ClientInfoResourceCategories.ServersInfrastructure));
        Assert.Contains("UPS", Titles(ClientInfoResourceCategories.ServersInfrastructure));
        Assert.Contains("Connection", Titles(ClientInfoResourceCategories.ConnectionInternet));
        Assert.Equal(["WiFi"], Titles(ClientInfoResourceCategories.Wifi));
        Assert.Contains("Microsoft 365", Titles(ClientInfoResourceCategories.ApplicationsCloud));
        Assert.Contains("Remote Access", Titles(ClientInfoResourceCategories.ApplicationsCloud));
        Assert.Equal(
            ["Domain & AD", "Email Security"],
            Titles(ClientInfoResourceCategories.DomainsEmail));
        Assert.Contains("Veeam", Titles(ClientInfoResourceCategories.Backup));
        Assert.DoesNotContain("ESET", Titles(ClientInfoResourceCategories.Backup));
        Assert.Contains("ESET", Titles(ClientInfoResourceCategories.Security));
        Assert.DoesNotContain("Barracuda", Titles(ClientInfoResourceCategories.Security));
        var barracuda = Assert.Single(
            demo.Resources,
            resource => resource.Name == "Barracuda Email Security");
        Assert.Equal(ClientInfoResourceCategories.DomainsEmail, barracuda.Category);
        var barracudaOverview = Assert.Single(
            ClientInfoCategoryOverviewBuilder.BuildSelected(
                ClientInfoResourceCategories.DomainsEmail,
                barracuda,
                demo.Credentials.Where(credential =>
                    credential.ResourceId == barracuda.ResourceId).ToArray()));
        Assert.Equal("Email Security", barracudaOverview.Title);
        Assert.Equal(
            [
                "Provider",
                "Protected domain",
                "Service",
                "Protected mailboxes",
                "Message retention",
                "Filtering / monitoring",
                "Renewal",
                "Admin username / password"
            ],
            barracudaOverview.Fields.Select(field => field.Label));
        Assert.Equal("Barracuda", barracudaOverview.Fields[0].Value);
        Assert.Equal("mail-admin@example.test", barracudaOverview.Fields[^1].Value);
        Assert.NotEmpty(barracudaOverview.Fields[^1].Secrets);

        var builder = Read("Models", "ClientInfoCategoryOverviewBuilder.cs");
        Assert.Contains("CredentialFieldGrouper.Group", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("DiscoverWorkspaceSections", builder, StringComparison.Ordinal);
        var viewModel = Read("ViewModels", "ClientInfoBetaViewModel.cs");
        Assert.Contains("BuildCloudAccounts", viewModel, StringComparison.Ordinal);
        Assert.Contains("\"Microsoft 365\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("or \"ESET\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("or \"Barracuda\"", viewModel, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-104, ClientInfoResourceCategories.ApplicationsCloud, "m365-admin@example.test")]
    [InlineData(-113, ClientInfoResourceCategories.Security, "security-admin@example.test")]
    public void SelectedApplicationAndSecurityCardsShowExplicitUsernameAndPassword(
        long resourceId,
        string category,
        string expectedUsername)
    {
        var demo = ClientInfoDemoData.Create().Snapshot;
        var resource = Assert.Single(
            demo.Resources,
            item => item.ResourceId == resourceId);
        var credentials = demo.Credentials
            .Where(credential => credential.ResourceId == resourceId)
            .ToArray();

        var section = Assert.Single(
            ClientInfoCategoryOverviewBuilder.BuildSelected(
                category,
                resource,
                credentials));

        var username = Assert.Single(
            section.Fields,
            field => field.Label == "Username");
        Assert.Equal(expectedUsername, username.Value);
        var password = Assert.Single(
            section.Fields,
            field => field.Label == "Password");
        Assert.Equal("Password available", password.Value);
        Assert.NotEmpty(password.Secrets);
        Assert.DoesNotContain(
            section.Fields,
            field => credentials.Any(credential => field.Label.Equals(
                credential.Name,
                StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void CategoryOverviewRecognizesFireDrillStyleLabelsWithoutReadingFireDrillData()
    {
        var sections = ClientInfoCategoryOverviewBuilder.Build(
            ClientInfoResourceCategories.ServersInfrastructure,
            [new ClientInfoResource
            {
                ResourceId = 9,
                ResourceType = ClientInfoResourceCategories.ServersInfrastructure,
                Name = "ILO Host 1",
                Fields =
                [
                    new ClientInfoResourceField
                    {
                        FieldKey = "custom.ilo_host_1_ip",
                        FieldLabel = "ILO Host 1 IP",
                        ValueText = "10.2.0.15"
                    }
                ]
            }],
            []);

        Assert.Contains(sections.SelectMany(section => section.Fields),
            field => field.Label == "Host IP" && field.Value == "10.2.0.15");
        Assert.Contains(sections, section => section.Title == "ILO");
        Assert.DoesNotContain(
            "FireDrillRepository",
            Read("Models", "ClientInfoCategoryOverviewBuilder.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ServerQuickReferenceUsesIloLabelAndOnlyRequestedUpsFields()
    {
        var demo = ClientInfoDemoData.Create().Snapshot;
        var resources = demo.Resources.Where(resource =>
            resource.Category == ClientInfoResourceCategories.ServersInfrastructure).ToArray();
        var resourceIds = resources.Select(resource => resource.ResourceId).ToHashSet();
        var credentials = demo.Credentials.Where(credential =>
            credential.ResourceId.HasValue
            && resourceIds.Contains(credential.ResourceId.Value)).ToArray();

        var sections = ClientInfoCategoryOverviewBuilder.Build(
            ClientInfoResourceCategories.ServersInfrastructure,
            resources,
            credentials);

        Assert.Contains(sections, section => section.Title == "ILO");
        Assert.DoesNotContain(sections, section => section.Title.Contains("iDRAC", StringComparison.OrdinalIgnoreCase));
        var ilo = Assert.Single(sections, section => section.Title == "ILO");
        Assert.Equal(
            ["Host name", "Host IP", "Username", "Password"],
            ilo.Fields.Select(field => field.Label));
        Assert.Equal("ILO Host 1", ilo.Fields[0].Value);
        Assert.Equal("10.20.0.12", ilo.Fields[1].Value);
        Assert.Equal("Administrator", ilo.Fields[2].Value);
        Assert.Equal("Password available", ilo.Fields[3].Value);
        Assert.Single(ilo.Fields[3].Secrets);
        var ups = Assert.Single(sections, section => section.Title == "UPS");
        Assert.Equal(
            ["Location", "Model", "IP", "Username", "Password"],
            ups.Fields.Select(field => field.Label));
        Assert.Equal("Main Office", ups.Fields[0].Value);
        Assert.Equal("APC Smart-UPS 2200", ups.Fields[1].Value);
        Assert.Equal("10.20.0.18", ups.Fields[2].Value);
        Assert.Equal("apc-admin", ups.Fields[3].Value);
        Assert.Equal("Password available", ups.Fields[4].Value);
        Assert.Single(ups.Fields[4].Secrets);
        Assert.DoesNotContain(
            ups.Fields,
            field => field.Value.Contains("https://", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IloQuickReferenceBuildsOneCredentialMatchedCardPerServer()
    {
        ClientInfoResource Server(long id, string name, string ip) => new()
        {
            ResourceId = id,
            ResourceType = ClientInfoResourceCategories.Encode(
                ClientInfoResourceCategories.ServersInfrastructure,
                "ILO"),
            Name = name,
            Fields =
            [
                new ClientInfoResourceField
                {
                    FieldKey = "management_ip",
                    FieldLabel = "Host IP",
                    ValueText = ip
                }
            ]
        };

        ClientInfoCredential Access(long id, long resourceId, string username) => new()
        {
            CredentialId = id,
            ResourceId = resourceId,
            Name = $"ILO {resourceId} Admin",
            Category = "ILO",
            Username = username,
            IsActive = true,
            Secrets =
            [
                new ClientInfoSecretSummary
                {
                    SecretId = id * 10,
                    CredentialId = id,
                    SecretType = "Password",
                    SecretLabel = "Password",
                    IsCurrent = true
                }
            ]
        };

        var sections = ClientInfoCategoryOverviewBuilder.Build(
            ClientInfoResourceCategories.ServersInfrastructure,
            [Server(501, "APP-SRV-01 ILO", "10.0.0.11"), Server(502, "FILE-SRV-01 ILO", "10.0.0.12")],
            [Access(601, 501, "app-admin"), Access(602, 502, "file-admin")]);

        var ilo = sections.Where(section => section.Title == "ILO").ToArray();
        Assert.Equal(2, ilo.Length);
        Assert.All(
            ilo,
            section => Assert.Equal(
                ["Host name", "Host IP", "Username", "Password"],
                section.Fields.Select(field => field.Label)));
        Assert.Equal("APP-SRV-01 ILO", ilo[0].Fields[0].Value);
        Assert.Equal("10.0.0.11", ilo[0].Fields[1].Value);
        Assert.Equal("app-admin", ilo[0].Fields[2].Value);
        Assert.Equal(6010, Assert.Single(ilo[0].Fields[3].Secrets).SecretId);
        Assert.Equal("FILE-SRV-01 ILO", ilo[1].Fields[0].Value);
        Assert.Equal("10.0.0.12", ilo[1].Fields[1].Value);
        Assert.Equal("file-admin", ilo[1].Fields[2].Value);
        Assert.Equal(6020, Assert.Single(ilo[1].Fields[3].Secrets).SecretId);
    }

    [Theory]
    [InlineData("WatchGuard", "Admin", ClientInfoResourceCategories.ConnectionInternet)]
    [InlineData("Wireless", "Aruba Central", ClientInfoResourceCategories.Wifi)]
    [InlineData("Veeam", "Backup Console", ClientInfoResourceCategories.Backup)]
    [InlineData("ESET", "Endpoint Security", ClientInfoResourceCategories.Security)]
    [InlineData("Email Security", "Barracuda Admin", ClientInfoResourceCategories.DomainsEmail)]
    [InlineData("Barracuda", "Barracuda Admin", ClientInfoResourceCategories.Security)]
    [InlineData("Backup & Security", "Veeam Admin", ClientInfoResourceCategories.Backup)]
    [InlineData("Backup & Security", "ESET Admin", ClientInfoResourceCategories.Security)]
    [InlineData("Active Directory", "Domain Admin", ClientInfoResourceCategories.DomainsEmail)]
    [InlineData("Remote Access", "ScreenConnect", ClientInfoResourceCategories.ApplicationsCloud)]
    [InlineData("ILO", "ILO Host 1", ClientInfoResourceCategories.ServersInfrastructure)]
    public void StandaloneFireDrillStyleAccessIsClassifiedIntoTheCorrectOverview(
        string category,
        string name,
        string expected)
    {
        Assert.Equal(
            expected,
            ClientInfoResourceCategories.ClassifyCredential(new ClientInfoCredential
            {
                Category = category,
                Name = name
            }));
    }

    [Fact]
    public void GeneratedWorkbookRoundTripsTheInternalClientIdentity()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Acme Client Info.xlsx");
            var service = new ClientInfoWorkbookService();

            service.CreateTemplate(path, 477, "Acme Legal");
            var package = service.Read(path);

            Assert.Equal(ClientInfoWorkbookService.TemplateVersion, package.TemplateVersion);
            Assert.Equal(477, package.ClientId);
            Assert.Equal("Acme Legal", package.ClientName);
            Assert.NotEqual(Guid.Empty, package.WorkbookId);
            Assert.Equal(32, package.ContentSha256.Length);
            var profile = Assert.Single(package.Records);
            Assert.Equal("Profile", profile.RecordType);
            Assert.Equal("Verified", profile.ReviewStatus);
            Assert.Empty(package.Secrets);

            using var workbook = SpreadsheetDocument.Open(path, false);
            var names = workbook.WorkbookPart!.Workbook.Sheets!
                .Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
                .Select(sheet => sheet.Name?.Value ?? string.Empty)
                .ToArray();
            Assert.Equal(
                [
                    "Start Here",
                    "Locations",
                    "Users",
                    "FireDrill",
                    "Servers & Infrastructure",
                    "Connection & Internet",
                    "Wi-Fi",
                    "Applications & Cloud",
                    "Domains & Email",
                    "Backup",
                    "Security",
                    "Vendors & Services",
                    "Equipment",
                    "Passwords",
                    "Other Info",
                    "Needs Sorting"
                ],
                names);
            Assert.Equal(
                [
                    "Firebox IP", "Status", "Admin", "csriadmin",
                    "*if enabled -Firebox-DB\\csri", "Authpoint User",
                    "sslvpnpassword", "AD Auth User", "AD Password", "RustPW",
                    "Review Status"
                ],
                ReadHeaderRow(workbook, "FireDrill"));
            Assert.Contains("AD Password", ReadHeaderRow(workbook, "Users"));
            Assert.Contains(
                "Microsoft 365 Uses AD Login",
                ReadHeaderRow(workbook, "Users"));
            Assert.Contains(
                "Microsoft 365 Username",
                ReadHeaderRow(workbook, "Users"));
            Assert.Contains(
                "Microsoft 365 Password",
                ReadHeaderRow(workbook, "Users"));
            var usersSheet = workbook.WorkbookPart.Workbook.Sheets!
                .Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
                .Single(sheet => string.Equals(
                    sheet.Name?.Value,
                    "Users",
                    StringComparison.Ordinal));
            var usersPart = (WorksheetPart)workbook.WorkbookPart.GetPartById(
                usersSheet.Id!.Value!);
            var licenseValidation = Assert.Single(
                usersPart.Worksheet.Descendants<DataValidation>(),
                validation => validation.SequenceOfReferences?.InnerText
                    == "G2:G500");
            Assert.Equal(
                $"\"{string.Join(',', Microsoft365LicenseCatalog.All)}\"",
                licenseValidation.Formula1?.Text);
            Assert.True(licenseValidation.ShowErrorMessage?.Value);
            Assert.Contains(
                "Primary IP",
                ReadHeaderRow(workbook, "Servers & Infrastructure"));
            Assert.Contains(
                "Password",
                ReadHeaderRow(workbook, "Servers & Infrastructure"));
            Assert.Contains(
                "Public / WAN IP",
                ReadHeaderRow(workbook, "Connection & Internet"));
            Assert.Contains(
                "SSL VPN Port",
                ReadHeaderRow(workbook, "Connection & Internet"));
            Assert.Contains(
                "Status Username",
                ReadHeaderRow(workbook, "Connection & Internet"));
            Assert.Contains(
                "Status Password",
                ReadHeaderRow(workbook, "Connection & Internet"));
            Assert.Contains(
                "Admin Username",
                ReadHeaderRow(workbook, "Connection & Internet"));
            Assert.Contains(
                "Admin Password",
                ReadHeaderRow(workbook, "Connection & Internet"));
            Assert.Contains(
                "SSID",
                ReadHeaderRow(workbook, "Wi-Fi"));
            Assert.Contains(
                "Tenant / Instance",
                ReadHeaderRow(workbook, "Applications & Cloud"));
            Assert.Contains(
                "Username",
                ReadHeaderRow(workbook, "Applications & Cloud"));
            Assert.Contains(
                "Password",
                ReadHeaderRow(workbook, "Applications & Cloud"));
            Assert.Contains(
                "Username",
                ReadHeaderRow(workbook, "Security"));
            Assert.Contains(
                "Password",
                ReadHeaderRow(workbook, "Security"));

            Assert.Equal(
                $"\"{string.Join(',', ClientInfoResourceFieldDefinitions.TypeOptionsForCategory(ClientInfoResourceCategories.ConnectionInternet))}\"",
                ReadListValidation(workbook, "Connection & Internet", "Type")
                    .Formula1?.Text);
            Assert.Equal(
                "\"Dynamic,Single static,Static block\"",
                ReadListValidation(workbook, "Connection & Internet", "IP assignment")
                    .Formula1?.Text);
            Assert.False(
                ReadListValidation(workbook, "Connection & Internet", "IP assignment")
                    .ShowErrorMessage?.Value);
            Assert.Equal(
                "\"Desktop,Laptop,Server,Switch,Firewall,Access Point,Printer,UPS,Phone,Other\"",
                ReadListValidation(workbook, "Equipment", "Device Type")
                    .Formula1?.Text);
            Assert.Equal(
                "\"Password,API Key,Token,PIN,Recovery Code,Other\"",
                ReadListValidation(workbook, "Passwords", "Secret Type")
                    .Formula1?.Text);
            Assert.Equal(
                4U,
                ReadHeaderCell(workbook, "Users", "Name").StyleIndex?.Value);
            Assert.Equal(
                4U,
                ReadHeaderCell(workbook, "Users", "Review Status").StyleIndex?.Value);
            Assert.Equal(
                4U,
                ReadHeaderCell(workbook, "Other Info", "Item").StyleIndex?.Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GeneratedWorkbookUsesCurrentFireDrillFieldLabelsAndImportsTheirValues()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "FireDrill Migration.xlsx");
            var service = new ClientInfoWorkbookService();
            service.CreateTemplate(
                path,
                477,
                "Acme Legal",
                [
                    "Firebox IP", "Status", "Admin", "Barracuda Password",
                    "ILO Host 1 PW", "Custom Future Field", "Admin"
                ]);

            using (var workbook = SpreadsheetDocument.Open(path, false))
            {
                Assert.Equal(
                    [
                        "Firebox IP", "Status", "Admin", "Barracuda Password",
                        "ILO Host 1 PW", "Custom Future Field", "Review Status"
                    ],
                    ReadHeaderRow(workbook, "FireDrill"));
            }

            AppendRowByHeader(
                path,
                "FireDrill",
                ("Firebox IP", "203.0.113.24"),
                ("Status", "status-secret"),
                ("Barracuda Password", "barracuda-secret"),
                ("ILO Host 1 PW", "ilo-secret"),
                ("Custom Future Field", "future-secret"),
                ("Review Status", "Verified"));

            var package = service.Read(path);
            var fireDrillCredentials = package.Records
                .Where(record => record.RecordType == "Credential"
                                 && record.SourceSheet == "FireDrill")
                .ToArray();

            Assert.Equal(5, fireDrillCredentials.Length);
            Assert.Equal(
                [
                    "203.0.113.24", "barracuda-secret", "future-secret",
                    "ilo-secret", "status-secret"
                ],
                package.Secrets
                    .Select(secret => secret.SecretValue)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray());
            Assert.Contains(
                fireDrillCredentials,
                record => JsonDocument.Parse(record.PayloadJson)
                    .RootElement.GetProperty("category").GetString()
                    == ClientInfoResourceCategories.ConnectionInternet);
            Assert.Contains(
                fireDrillCredentials,
                record => JsonDocument.Parse(record.PayloadJson)
                    .RootElement.GetProperty("category").GetString()
                    == ClientInfoResourceCategories.DomainsEmail);
            Assert.Contains(
                fireDrillCredentials,
                record => JsonDocument.Parse(record.PayloadJson)
                    .RootElement.GetProperty("category").GetString()
                    == ClientInfoResourceCategories.ServersInfrastructure);
            Assert.All(
                fireDrillCredentials,
                record => Assert.Equal("Verified", record.ReviewStatus));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PreviousFireDrillTransferTemplateVersionRemainsImportable()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Previous Migration.xlsx");
            var service = new ClientInfoWorkbookService();
            service.CreateTemplate(path, 477, "Acme Legal");
            using (var workbook = SpreadsheetDocument.Open(path, true))
            {
                var workbookPart = workbook.WorkbookPart!;
                var sheets = workbookPart.Workbook.Sheets!
                    .Elements<Sheet>()
                    .ToList();
                SetTemplateVersion(
                    workbookPart,
                    sheets,
                    ClientInfoWorkbookService.FireDrillTransferTemplateVersion);
                workbookPart.Workbook.Save();
            }
            AppendRowByHeader(
                path,
                "Applications & Cloud",
                ("Type", "Microsoft 365 Tenant"),
                ("Name", "Microsoft 365"),
                ("Review Status", "Verified"));

            var package = service.Read(path);

            Assert.Equal(
                ClientInfoWorkbookService.FireDrillTransferTemplateVersion,
                package.TemplateVersion);
            Assert.Contains(
                package.Records,
                record => record.RecordType == "Resource"
                          && record.PayloadJson.Contains(
                              "Microsoft 365",
                              StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CurrentMigrationSheetsKeepBackupAndSecuritySeparate()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Protection Migration.xlsx");
            var service = new ClientInfoWorkbookService();
            service.CreateTemplate(path, 477, "Acme Legal");
            AppendRowByHeader(
                path,
                "Backup",
                ("Type", "Veeam Backup"),
                ("Name", "Veeam"),
                ("Provider", "Veeam"),
                ("Console / Portal URL", "https://backup.example.test"),
                ("Status", "Active"),
                ("Review Status", "Verified"));
            AppendRowByHeader(
                path,
                "Security",
                ("Type", "Antivirus / EDR"),
                ("Name", "ESET Protect"),
                ("Provider", "ESET"),
                ("Console / Portal URL", "https://security.example.test"),
                ("Status", "Active"),
                ("Review Status", "Verified"));

            var resources = service.Read(path).Records
                .Where(record => record.RecordType == "Resource")
                .ToArray();

            Assert.Equal(2, resources.Length);
            Assert.Contains(resources, record => record.PayloadJson.Contains(
                "\"resourceType\":\"Backup / Veeam Backup\"",
                StringComparison.Ordinal));
            Assert.Contains(resources, record => record.PayloadJson.Contains(
                "\"resourceType\":\"Security / Antivirus / EDR\"",
                StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PreviousCombinedProtectionSheetRemainsImportable()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Previous Protection Migration.xlsx");
            var service = new ClientInfoWorkbookService();
            service.CreateTemplate(path, 477, "Acme Legal");
            ConvertGeneratedWorkbookToPreviousTemplateVersion(path);
            AppendRow(
                path,
                "Backup & Security",
                "Veeam Backup", "Veeam", "Veeam",
                "https://backup.example.test", "", "Active", "", "Verified");

            var package = service.Read(path);

            Assert.Equal(
                ClientInfoWorkbookService.PreviousTemplateVersion,
                package.TemplateVersion);
            Assert.Contains(
                package.Records,
                record => record.RecordType == "Resource"
                    && record.PayloadJson.Contains(
                        "\"resourceType\":\"Backup / Veeam Backup\"",
                        StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FriendlyWorkbookRowsMapToCanonicalRecordsWithoutTechnicalKeys()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Friendly Migration.xlsx");
            var service = new ClientInfoWorkbookService();
            service.CreateTemplate(path, 477, "Acme Legal");

            AppendRow(
                path,
                "Locations",
                "Main Office", "Office", "100 Main Street", "", "Malvern",
                "PA", "19355", "610-555-0100", "Eastern Standard Time",
                "Yes", "Verified");
            AppendRowByHeader(
                path,
                "Users",
                ("Name", "Jamie Rivera"),
                ("Role/Department", "Office Manager"),
                ("AD Username", "ACME\\jrivera"),
                ("Email", "jamie@example.test"),
                ("Has Microsoft 365", "Yes"),
                ("Microsoft 365 License", "Business Premium"),
                ("PC Name", "ACME-PC-01"),
                ("Phone", "610-555-0101"),
                ("Location", "Main Office"),
                ("Contact Type", "Primary"),
                ("Review Status", "Verified"));
            AppendRowByHeader(
                path,
                "Connection & Internet",
                ("Type", "Firewall"),
                ("Name", "WatchGuard"),
                ("Provider", "WatchGuard"),
                ("Hostname / URL", "https://firewall.test"),
                ("Location", "Main Office"),
                ("Status", "Active"),
                ("Notes", "Primary firewall"),
                ("Review Status", "Verified"));
            AppendRow(
                path,
                "Equipment",
                "Desktop", "Reception PC", "Dell", "OptiPlex", "SN-100", "",
                "TB-100", "10.0.0.10", "Main Office", "Front desk", "Verified");
            AppendRow(
                path,
                "Passwords",
                "Firewall Admin", "Firewall", "admin", "P@ss word",
                "https://firewall.test", "WatchGuard", "Jamie Rivera", "Emergency admin",
                "Password", "Admin password", "Verified");
            AppendRow(
                path,
                "Other Info",
                "Operations", "After-hours procedure", "Call the office manager",
                "Keep as-is");

            var package = service.Read(path);

            Assert.Equal(8, package.Records.Count);
            Assert.Contains(package.Records, record => record.RecordType == "Profile");
            var location = Assert.Single(
                package.Records,
                record => record.RecordType == "Location");
            var person = Assert.Single(
                package.Records,
                record => record.RecordType == "Person");
            var resource = Assert.Single(
                package.Records,
                record => record.RecordType == "Resource");
            var credentials = package.Records
                .Where(record => record.RecordType == "Credential")
                .ToArray();
            Assert.Equal(2, credentials.Length);
            Assert.Single(
                package.Records,
                record => record.RecordType == "Equipment");
            var fact = Assert.Single(
                package.Records,
                record => record.RecordType == "Fact");
            Assert.Equal("AcceptedUnverified", fact.ReviewStatus);
            Assert.Equal(location.LocalKey, person.ParentLocalKey);
            Assert.Contains("ACME\\\\jrivera", person.PayloadJson, StringComparison.Ordinal);
            Assert.Contains("Business Premium", person.PayloadJson, StringComparison.Ordinal);
            Assert.Contains("ACME-PC-01", person.PayloadJson, StringComparison.Ordinal);
            Assert.Contains(location.LocalKey, resource.PayloadJson, StringComparison.Ordinal);
            using var resourcePayload =
                System.Text.Json.JsonDocument.Parse(resource.PayloadJson);
            Assert.Equal(
                "Connection & Internet / Firewall",
                resourcePayload.RootElement
                    .GetProperty("resourceType")
                    .GetString());
            var credential = Assert.Single(
                credentials,
                item => item.PayloadJson.Contains(
                    resource.LocalKey,
                    StringComparison.Ordinal));
            Assert.Contains(person.LocalKey, credential.PayloadJson, StringComparison.Ordinal);
            var secret = Assert.Single(package.Secrets);
            Assert.Equal(credential.LocalKey, secret.CredentialLocalKey);
            Assert.Equal("P@ss word", secret.SecretValue);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CurrentWorkbookImportsPasswordsBesideUsersAndSystems()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Inline Credentials.xlsx");
            var service = new ClientInfoWorkbookService();
            service.CreateTemplate(path, 477, "Acme Legal");
            AppendRowByHeader(
                path,
                "Users",
                ("Name", "Jamie Rivera"),
                ("AD Username", "ACME\\jrivera"),
                ("AD Password", "user secret"),
                ("Email", "jamie@example.test"),
                ("Has Microsoft 365", "Yes"),
                ("Microsoft 365 Uses AD Login", "No"),
                ("Microsoft 365 Username", "jamie.365@example.test"),
                ("Microsoft 365 Password", "m365 secret"),
                ("Review Status", "Verified"));
            AppendRowByHeader(
                path,
                "Servers & Infrastructure",
                ("Type", "UPS"),
                ("Name", "Server Room UPS"),
                ("Hostname / URL", "https://10.20.0.18"),
                ("Login Name", "UPS administrator"),
                ("Username", "apc-admin"),
                ("Password", "ups secret"),
                ("Review Status", "Verified"));

            var package = service.Read(path);

            var person = Assert.Single(
                package.Records,
                record => record.RecordType == "Person");
            var resource = Assert.Single(
                package.Records,
                record => record.RecordType == "Resource");
            var credentials = package.Records
                .Where(record => record.RecordType == "Credential")
                .ToArray();
            Assert.Equal(3, credentials.Length);
            Assert.Contains(
                credentials,
                credential => credential.PayloadJson.Contains(
                        $"\"personKey\":\"{person.LocalKey}\"",
                        StringComparison.Ordinal)
                    && credential.PayloadJson.Contains(
                        "ACME\\\\jrivera",
                        StringComparison.Ordinal));
            Assert.Contains(
                credentials,
                credential => credential.PayloadJson.Contains(
                        $"\"personKey\":\"{person.LocalKey}\"",
                        StringComparison.Ordinal)
                    && credential.PayloadJson.Contains(
                        "jamie.365@example.test",
                        StringComparison.Ordinal)
                    && credential.PayloadJson.Contains(
                        "Microsoft 365 User",
                        StringComparison.Ordinal));
            Assert.Contains(
                credentials,
                credential => credential.PayloadJson.Contains(
                        $"\"resourceKey\":\"{resource.LocalKey}\"",
                        StringComparison.Ordinal)
                    && credential.PayloadJson.Contains(
                        "apc-admin",
                        StringComparison.Ordinal));
            Assert.Equal(
                ["m365 secret", "ups secret", "user secret"],
                package.Secrets
                    .Select(secret => secret.SecretValue)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CurrentWorkbookLinksApplicationSecurityAndFirewallLoginsToTheirResources()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Linked Resource Logins.xlsx");
            var service = new ClientInfoWorkbookService();
            service.CreateTemplate(path, 477, "Acme Legal");
            AppendRowByHeader(
                path,
                "Applications & Cloud",
                ("Type", "Microsoft 365 Tenant"),
                ("Name", "Microsoft 365"),
                ("Login Name", "Microsoft 365 Global Admin"),
                ("Username", "m365-admin@example.test"),
                ("Password", "m365 secret"),
                ("Review Status", "Verified"));
            AppendRowByHeader(
                path,
                "Security",
                ("Type", "Antivirus / EDR"),
                ("Name", "ESET Protect"),
                ("Login Name", "ESET Protect Admin"),
                ("Username", "eset-admin@example.test"),
                ("Password", "eset secret"),
                ("Review Status", "Verified"));
            AppendRowByHeader(
                path,
                "Connection & Internet",
                ("Type", "WatchGuard Firewall"),
                ("Name", "Main WatchGuard"),
                ("Status Username", "status"),
                ("Status Password", "status secret"),
                ("Admin Username", "admin"),
                ("Admin Password", "admin secret"),
                ("Review Status", "Verified"));

            var package = service.Read(path);
            var resources = package.Records
                .Where(record => record.RecordType == "Resource")
                .ToArray();
            var credentials = package.Records
                .Where(record => record.RecordType == "Credential")
                .ToArray();
            Assert.Equal(3, resources.Length);
            Assert.Equal(4, credentials.Length);

            void AssertLinkedLogin(
                string resourceName,
                string credentialName,
                string username)
            {
                var resource = Assert.Single(
                    resources,
                    record => record.PayloadJson.Contains(
                        $"\"name\":\"{resourceName}\"",
                        StringComparison.Ordinal));
                Assert.Contains(
                    credentials,
                    credential => credential.PayloadJson.Contains(
                            $"\"resourceKey\":\"{resource.LocalKey}\"",
                            StringComparison.Ordinal)
                        && credential.PayloadJson.Contains(
                            $"\"name\":\"{credentialName}\"",
                            StringComparison.Ordinal)
                        && credential.PayloadJson.Contains(
                            username,
                            StringComparison.Ordinal));
            }

            AssertLinkedLogin(
                "Microsoft 365",
                "Microsoft 365 Global Admin",
                "m365-admin@example.test");
            AssertLinkedLogin(
                "ESET Protect",
                "ESET Protect Admin",
                "eset-admin@example.test");
            AssertLinkedLogin("Main WatchGuard", "Main WatchGuard status", "status");
            AssertLinkedLogin("Main WatchGuard", "Main WatchGuard admin", "admin");
            Assert.Equal(
                ["admin secret", "eset secret", "m365 secret", "status secret"],
                package.Secrets
                    .Select(secret => secret.SecretValue)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray());
            Assert.All(
                package.Records,
                record => Assert.Equal("Verified", record.ReviewStatus));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CurrentWorkbookReusesAdLoginWhenMicrosoft365SyncIsYes()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Synced Microsoft 365 Login.xlsx");
            var service = new ClientInfoWorkbookService();
            service.CreateTemplate(path, 477, "Acme Legal");
            AppendRowByHeader(
                path,
                "Users",
                ("Name", "Jamie Rivera"),
                ("AD Username", "ACME\\jrivera"),
                ("AD Password", "ad secret"),
                ("Has Microsoft 365", "Yes"),
                ("Microsoft 365 Uses AD Login", "Yes"),
                ("Microsoft 365 Username", "ignored@example.test"),
                ("Microsoft 365 Password", "ignored secret"),
                ("Review Status", "Verified"));

            var package = service.Read(path);
            var credential = Assert.Single(
                package.Records,
                record => record.RecordType == "Credential");

            Assert.Contains(
                "Active Directory User",
                credential.PayloadJson,
                StringComparison.Ordinal);
            var secret = Assert.Single(package.Secrets);
            Assert.Equal("ad secret", secret.SecretValue);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CurrentWorkbookWifiSheetMapsToTheWifiCategory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Wi-Fi Migration.xlsx");
            var service = new ClientInfoWorkbookService();
            service.CreateTemplate(path, 477, "Acme Legal");
            AppendRowByHeader(
                path,
                "Wi-Fi",
                ("Type", "Access Point"),
                ("Name", "Lobby AP"),
                ("Provider", "Ubiquiti"),
                ("Controller / URL", "https://controller.test"),
                ("Management IP", "10.0.20.10"),
                ("SSID", "Lobby Wi-Fi"),
                ("VLAN", "20"),
                ("Security", "WPA2"),
                ("Status", "Active"),
                ("Notes", "Main wireless access point"),
                ("Review Status", "Verified"));

            var package = service.Read(path);

            var resource = Assert.Single(
                package.Records,
                record => record.RecordType == "Resource");
            Assert.Contains(
                "\"resourceType\":\"Wi-Fi / Access Point\"",
                resource.PayloadJson,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WorkbookStandardAndCustomColumnsStageResourceFields()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Custom Fields.xlsx");
            var service = new ClientInfoWorkbookService();
            service.CreateTemplate(path, 477, "Acme Legal");
            AppendHeader(path, "Servers & Infrastructure", "Custom: Rack");
            AppendRowByHeader(
                path,
                "Servers & Infrastructure",
                ("Type", "Server"),
                ("Name", "DC01"),
                ("Provider", "Dell"),
                ("Hostname / URL", "dc01.example.test"),
                ("Primary IP", "10.0.0.5"),
                ("Management IP", "10.0.0.6"),
                ("Additional IPs / Subnet", "10.0.0.7; 10.0.0.0/24"),
                ("Status", "Active"),
                ("Notes", "Domain controller"),
                ("Review Status", "Verified"),
                ("Custom: Rack", "Rack A / U12"));

            var package = service.Read(path);

            var resource = Assert.Single(
                package.Records,
                record => record.RecordType == "Resource");
            var fields = package.Records
                .Where(record => record.RecordType == "ResourceField")
                .ToArray();
            Assert.Equal(4, fields.Length);
            Assert.All(fields, field => Assert.Equal(
                resource.LocalKey,
                field.ParentLocalKey));
            Assert.Contains(
                fields,
                field => field.PayloadJson.Contains(
                    "\"fieldKey\":\"primary_ip\"",
                    StringComparison.Ordinal));
            Assert.Contains(
                fields,
                field => field.PayloadJson.Contains(
                    "\"fieldKey\":\"custom.rack\"",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("Veeam Backup", "Backup")]
    [InlineData("Barracuda Backup", "Backup")]
    [InlineData("Backup & Security / Veeam Backup", "Backup")]
    [InlineData("Antivirus / EDR", "Security")]
    [InlineData("Barracuda Email Security", "Domains & Email")]
    [InlineData("Barracuda WAF", "Security")]
    [InlineData("Spam Filtering", "Domains & Email")]
    [InlineData("Security / Spam Filtering", "Domains & Email")]
    [InlineData("Backup & Security / Spam Filtering", "Domains & Email")]
    [InlineData("CrowdStrike", "Security")]
    [InlineData("Backup & Security / Antivirus / EDR", "Security")]
    [InlineData("Backup & Security", "Needs Sorting")]
    [InlineData("Backup & Security / Other", "Needs Sorting")]
    [InlineData("Firewall", "Connection & Internet")]
    [InlineData("Switch", "Servers & Infrastructure")]
    [InlineData("Network Switch", "Servers & Infrastructure")]
    [InlineData("Network Appliance", "Servers & Infrastructure")]
    [InlineData("Connection & Internet / Network Appliance", "Servers & Infrastructure")]
    [InlineData("Network & Internet / Network Appliance", "Servers & Infrastructure")]
    [InlineData("Network & Internet / Switch", "Servers & Infrastructure")]
    [InlineData("Network & Internet / Firewall", "Connection & Internet")]
    [InlineData("Wi-Fi", "Wi-Fi")]
    [InlineData("Wireless Network", "Wi-Fi")]
    [InlineData("Access Point", "Wi-Fi")]
    [InlineData("Connection & Internet / Wi-Fi", "Wi-Fi")]
    [InlineData("Network & Internet / Access Point", "Wi-Fi")]
    [InlineData("Microsoft 365", "Applications & Cloud")]
    [InlineData("Domain Registrar", "Domains & Email")]
    [InlineData("Hyper-V Host", "Servers & Infrastructure")]
    [InlineData("Copier Contract", "Vendors & Services")]
    public void ExistingResourceTypesArePlacedInUsefulCategories(
        string resourceType,
        string expectedCategory)
    {
        Assert.Equal(
            expectedCategory,
            TechBench.Models.ClientInfoResourceCategories.Classify(resourceType));
    }

    [Theory]
    [InlineData(
        "Connection & Internet",
        "Network Appliance",
        "Servers & Infrastructure / Network Appliance")]
    [InlineData(
        "Connection & Internet",
        "Wireless Access Point",
        "Wi-Fi / Wireless Access Point")]
    public void FriendlyWorkbookCategoriesAreCorrectedForInfrastructureAndWifi(
        string selectedCategory,
        string type,
        string expectedResourceType)
    {
        Assert.Equal(
            expectedResourceType,
            TechBench.Models.ClientInfoResourceCategories.Encode(
                selectedCategory,
                type));
    }

    [Fact]
    public void PreviousFriendlyWorkbookVersionRemainsImportable()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Previous Migration.xlsx");
            var service = new ClientInfoWorkbookService();
            service.CreateTemplate(path, 477, "Acme Legal");
            ConvertGeneratedWorkbookToFriendlyVersion(path);
            AppendRow(
                path,
                "Systems & Services",
                "Firewall", "WatchGuard", "WatchGuard", "", "", "Active",
                "Previous beta workbook", "Verified");

            var package = service.Read(path);

            Assert.Equal(
                ClientInfoWorkbookService.FriendlyTemplateVersion,
                package.TemplateVersion);
            var resource = Assert.Single(
                package.Records,
                record => record.RecordType == "Resource");
            Assert.Contains(
                "\"resourceType\":\"Firewall\"",
                resource.PayloadJson,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PreviousCategorizedWorkbookVersionRemainsImportable()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Previous Categorized.xlsx");
            var service = new ClientInfoWorkbookService();
            service.CreateTemplate(path, 477, "Acme Legal");
            ConvertGeneratedWorkbookToPreviousCategorizedVersion(path);
            AppendRow(
                path,
                "Network & Internet",
                "Switch", "Core Switch", "Cisco", "", "", "Active",
                "Previous beta workbook", "Verified");

            var package = service.Read(path);

            Assert.Equal(
                ClientInfoWorkbookService.CategorizedTemplateVersion,
                package.TemplateVersion);
            var resource = Assert.Single(
                package.Records,
                record => record.RecordType == "Resource");
            Assert.Equal(
                "Servers & Infrastructure",
                TechBench.Models.ClientInfoResourceCategories.Classify(
                    System.Text.Json.JsonDocument.Parse(resource.PayloadJson)
                        .RootElement.GetProperty("resourceType").GetString()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PreviousConnectionWorkbookVersionMovesWifiIntoItsOwnCategory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Previous Connection.xlsx");
            var service = new ClientInfoWorkbookService();
            service.CreateTemplate(path, 477, "Acme Legal");
            ConvertGeneratedWorkbookToPreviousConnectionVersion(path);
            AppendRow(
                path,
                "Connection & Internet",
                "Wireless Access Point", "Lobby Wi-Fi", "Ubiquiti", "", "",
                "Active", "Previous beta workbook", "Verified");

            var package = service.Read(path);

            Assert.Equal(
                ClientInfoWorkbookService.ConnectionTemplateVersion,
                package.TemplateVersion);
            var resource = Assert.Single(
                package.Records,
                record => record.RecordType == "Resource");
            Assert.Equal(
                "Wi-Fi",
                TechBench.Models.ClientInfoResourceCategories.Classify(
                    System.Text.Json.JsonDocument.Parse(resource.PayloadJson)
                        .RootElement.GetProperty("resourceType").GetString()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PreviousWifiWorkbookVersionRemainsImportable()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "TechBenchClientInfoTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Previous Wi-Fi.xlsx");
            var service = new ClientInfoWorkbookService();
            service.CreateTemplate(path, 477, "Acme Legal");
            ConvertGeneratedWorkbookToPreviousWifiVersion(path);
            AppendRow(
                path,
                "Wi-Fi",
                "Wireless Access Point", "Lobby Wi-Fi", "Ubiquiti", "", "",
                "Active", "Previous beta workbook", "Verified");

            var package = service.Read(path);

            Assert.Equal(
                ClientInfoWorkbookService.WifiTemplateVersion,
                package.TemplateVersion);
            var resource = Assert.Single(
                package.Records,
                record => record.RecordType == "Resource");
            Assert.Equal(
                "Wi-Fi",
                TechBench.Models.ClientInfoResourceCategories.Classify(
                    System.Text.Json.JsonDocument.Parse(resource.PayloadJson)
                        .RootElement.GetProperty("resourceType").GetString()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BetaSchemaIsAdditiveAndKeepsStableClientsAtSchemaFifteen()
    {
        var schema = Read("database", "sqlserver2016", "36-V0015-ClientInfoBetaSchema.sql");
        var procedures = Read(
            "database",
            "sqlserver2016",
            "61-V0015-ClientInfoBetaProcedures.sql");
        var imports = Read(
            "database",
            "sqlserver2016",
            "62-V0015-ClientInfoBetaImportProcedures.sql");
        var verifier = Read(
            "database",
            "sqlserver2016",
            "106-V0015-ClientInfoBetaVerify.sql");
        var grants = Read(
            "database",
            "sqlserver2016",
            "63-V0015-ClientInfoBetaGrants.sql");

        Assert.Contains(
            "SqlServer2016.ClientInfoBeta.0015",
            schema,
            StringComparison.Ordinal);
        Assert.Contains(
            "N'SqlServer2016.ClientInfoBeta.0015', 15",
            schema,
            StringComparison.Ordinal);
        foreach (var userColumn in new[]
                 {
                     "AdUsername",
                     "HasMicrosoft365",
                     "Microsoft365License",
                     "PcName"
                 })
        {
            Assert.Contains(userColumn, schema, StringComparison.Ordinal);
            Assert.Contains(userColumn, procedures, StringComparison.Ordinal);
            Assert.Contains(userColumn, imports, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(userColumn, verifier, StringComparison.Ordinal);
        }
        foreach (var serverLinkColumn in new[]
                 {
                     "ClientFolderPath",
                     "LegacyClientInfoSheetPath"
                 })
        {
            Assert.Contains(serverLinkColumn, schema, StringComparison.Ordinal);
            Assert.Contains(serverLinkColumn, procedures, StringComparison.Ordinal);
            Assert.Contains(serverLinkColumn, verifier, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(
            "AdPassword",
            schema,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "CONVERT(int, 15) AS [SchemaVersion]",
            procedures,
            StringComparison.Ordinal);
        Assert.Contains(
            "CONVERT(bit, 1) AS [ClientInfoBetaAvailable]",
            procedures,
            StringComparison.Ordinal);
        Assert.Equal(15, SqlServerConnectionFactory.SupportedSchemaVersion);
        Assert.Contains(
            "CompareClientInfoImportToFireDrill",
            imports,
            StringComparison.Ordinal);
        Assert.Contains(
            "SET [State]=N'Complete'",
            imports,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SET [State]=N'Hypercare'",
            imports,
            StringComparison.Ordinal);
        var encryptStart = procedures.IndexOf(
            "CREATE PROCEDURE [tb_security].[EncryptClientSecretValue]",
            StringComparison.Ordinal);
        var encryptEnd = procedures.IndexOf("\nGO", encryptStart, StringComparison.Ordinal);
        var setStart = procedures.IndexOf(
            "CREATE PROCEDURE [tb_app].[SetClientCredentialSecret]",
            StringComparison.Ordinal);
        var setEnd = procedures.IndexOf("\nGO", setStart, StringComparison.Ordinal);
        var stageStart = imports.IndexOf(
            "CREATE PROCEDURE [tb_app].[StageClientInfoSecret]",
            StringComparison.Ordinal);
        var stageEnd = imports.IndexOf("\nGO", stageStart, StringComparison.Ordinal);
        Assert.True(encryptStart >= 0 && encryptEnd > encryptStart);
        Assert.True(setStart >= 0 && setEnd > setStart);
        Assert.True(stageStart >= 0 && stageEnd > stageStart);
        var encryptBody = procedures[encryptStart..encryptEnd];
        var setBody = procedures[setStart..setEnd];
        var stageBody = imports[stageStart..stageEnd];
        Assert.Contains("WITH EXECUTE AS OWNER", encryptBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OPEN SYMMETRIC KEY [tb_ClientSecretKey]", encryptBody, StringComparison.Ordinal);
        Assert.Contains("EXEC [tb_security].[EncryptClientSecretValue]", setBody, StringComparison.Ordinal);
        Assert.Contains("EXEC [tb_security].[EncryptClientSecretValue]", stageBody, StringComparison.Ordinal);
        Assert.DoesNotContain("WITH EXECUTE AS OWNER", setBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WITH EXECUTE AS OWNER", stageBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OPEN SYMMETRIC KEY", setBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OPEN SYMMETRIC KEY", stageBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Client Info secret writes do not use the protected encryption boundary.",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE PROCEDURE [tb_security].[GetClientInfoImportBatchResult]",
            imports,
            StringComparison.Ordinal);
        var compareStart = imports.IndexOf(
            "CREATE PROCEDURE [tb_app].[CompareClientInfoImportToFireDrill]",
            StringComparison.Ordinal);
        var compareEnd = imports.IndexOf("\nGO", compareStart, StringComparison.Ordinal);
        Assert.True(compareStart >= 0 && compareEnd > compareStart);
        var compareBody = imports[compareStart..compareEnd];
        Assert.Contains(
            "EXEC [tb_security].[GetClientInfoImportBatchResult]",
            compareBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EXEC [tb_app].[GetClientInfoImportBatch]",
            compareBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE #WorkbookHashes",
            compareBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "decrypted.[ValuePlain] IS NOT NULL",
            compareBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "credential.[ClientKey]+N'|'+field.[FieldKey]",
            compareBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "DECLARE @HasFireDrillClient bit",
            compareBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHEN @HasFireDrillClient=0 THEN N'WorkbookOnly'",
            compareBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "The FireDrill client name matched, but none of its stored values could be compared.",
            compareBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHEN workbook_value.[ValueHash] IS NULL THEN N'NotComparable'",
            compareBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "Import review can continue.",
            compareBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "Client Info import results do not preserve the caller security context.",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE PROCEDURE [tb_app].[AcceptClientInfoImportUnverified]",
            imports,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE PROCEDURE [tb_app].[DiscardClientInfoImport]",
            imports,
            StringComparison.Ordinal);
        Assert.Contains(
            "[State]=N'Superseded'",
            imports,
            StringComparison.Ordinal);
        var beginImportStart = imports.IndexOf(
            "CREATE PROCEDURE [tb_app].[BeginClientInfoImport]",
            StringComparison.Ordinal);
        var beginImportEnd = imports.IndexOf(
            "\nGO",
            beginImportStart,
            StringComparison.Ordinal);
        Assert.True(beginImportStart >= 0 && beginImportEnd > beginImportStart);
        var beginImportBody = imports[beginImportStart..beginImportEnd];
        Assert.Contains(
            "[State] NOT IN (N'Rejected',N'Superseded',N'Failed')",
            beginImportBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "UX_ClientInfoBatches_ActiveIdempotency",
            schema,
            StringComparison.Ordinal);
        Assert.Contains(
            "DROP CONSTRAINT [UX_ClientInfoBatches_Idempotency]",
            schema,
            StringComparison.Ordinal);
        Assert.Contains(
            "[State]<>N'Rejected'",
            schema,
            StringComparison.Ordinal);
        Assert.Contains(
            "Closed Client Info workbook reviews still block an identical reimport.",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "[State] NOT IN (N'Rejected',N'Superseded')",
            procedures,
            StringComparison.Ordinal);
        Assert.Contains(
            "Accept Remaining as Keep as-is before approval.",
            imports,
            StringComparison.Ordinal);
        Assert.Contains(
            "record.[SourceSheet]",
            imports,
            StringComparison.Ordinal);
        Assert.Contains(
            "record.[SourceRow]",
            imports,
            StringComparison.Ordinal);
        Assert.Contains(
            "tb_app.AcceptClientInfoImportUnverified",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "tb_app.DiscardClientInfoImport",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "GRANT EXECUTE ON OBJECT::[tb_app].[AcceptClientInfoImportUnverified]",
            grants,
            StringComparison.Ordinal);
        Assert.Contains(
            "GRANT EXECUTE ON OBJECT::[tb_app].[DiscardClientInfoImport]",
            grants,
            StringComparison.Ordinal);
        Assert.Contains(
            "N'ResourceField'",
            imports,
            StringComparison.Ordinal);
        Assert.Contains(
            "DROP CONSTRAINT [CK_ClientInfoRecords_Type]",
            schema,
            StringComparison.Ordinal);
        Assert.Contains(
            "N'ResourceField'",
            schema,
            StringComparison.Ordinal);
        Assert.Contains(
            "CHARINDEX(N'ResourceField', @RecordTypeConstraint)=0",
            verifier,
            StringComparison.Ordinal);
        foreach (var valueType in new[] { "N'Phone'", "N'Email'" })
        {
            Assert.Contains(valueType, schema, StringComparison.Ordinal);
            Assert.Contains(valueType, procedures, StringComparison.Ordinal);
            Assert.Contains(valueType, imports, StringComparison.Ordinal);
        }
        Assert.Contains(
            "Client Info does not support phone and email field types.",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "Supported types are Text, Number, Boolean, Date, URL, IP address, Phone, and Email.",
            imports,
            StringComparison.Ordinal);
        Assert.Contains("[SourceSheet]", imports, StringComparison.Ordinal);
        Assert.Contains("[SourceRow]", imports, StringComparison.Ordinal);
        Assert.Contains(
            "SaveClientInfoResourceField",
            procedures,
            StringComparison.Ordinal);
        Assert.Contains(
            "DeleteClientInfoResourceField",
            procedures,
            StringComparison.Ordinal);
        Assert.Contains(
            "GRANT EXECUTE ON OBJECT::[tb_app].[SaveClientInfoResourceField]",
            grants,
            StringComparison.Ordinal);
        Assert.Contains(
            "tb_app.SaveClientInfoResourceField",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "HASHBYTES(",
            imports,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SELECT secret.[ValueEncrypted]",
            imports,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "CHARINDEX(N'CONVERT(int, 15) AS [SchemaVersion]', @Capabilities) = 0",
            verifier,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "@Capabilities NOT LIKE",
            verifier,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryClientMergePathReparentsCanonicalClientInfo()
    {
        var manual = Read(
            "database",
            "sqlserver2016",
            "42-V0002-SharedProcedures.sql");
        var automatic = Read(
            "database",
            "sqlserver2016",
            "49-V0007-ServerOwnedSageAndAdminPreviewProcedures.sql");

        Assert.Contains(
            "EXEC [tb_client].[ReparentClientGraph]",
            manual,
            StringComparison.Ordinal);
        Assert.True(
            automatic.Split(
                "EXEC [tb_client].[ReparentClientGraph]",
                StringSplitOptions.None).Length - 1 >= 2);
    }

    [Fact]
    public void BetaWindowUsesSmallRecordEditorsAndAnExplicitMigrationGate()
    {
        var xaml = Read("ClientInfoBetaWindow.xaml");
        var viewModel = Read(
            "ViewModels",
            "ClientInfoBetaViewModel.cs");
        var mainWindow = Read("MainWindow.xaml.cs");
        var importWindow = Read("ClientInfoImportWindow.xaml");
        var resourceGrid = Read(
            "Controls",
            "ClientInfoResourceDataGrid.cs");

        foreach (var tab in new[]
                 {
                     "Overview",
                     "Locations",
                     "Users",
                     "Equipment",
                     "Servers &amp; Infrastructure",
                     "Connection &amp; Internet",
                     "Wi-Fi",
                     "Applications &amp; Cloud",
                     "Domains &amp; Email",
                     "Backup",
                     "Security",
                     "Vendors &amp; Services",
                     "Needs Sorting",
                     "Passwords",
                     "Other Information"
                 })
        {
            Assert.Contains($"Header=\"{tab}\"", xaml, StringComparison.Ordinal);
        }

        Assert.Contains("SaveClientInfoLocation", viewModel, StringComparison.Ordinal);
        Assert.Contains("SaveClientInfoPerson", viewModel, StringComparison.Ordinal);
        Assert.Contains("SaveClientInfoResource", viewModel, StringComparison.Ordinal);
        Assert.Contains("SaveClientInfoResourceField", viewModel, StringComparison.Ordinal);
        Assert.Contains("ManageResourceFieldsCommand", viewModel, StringComparison.Ordinal);
        Assert.Contains("Content=\"Custom Fields\"", xaml, StringComparison.Ordinal);
        Assert.Contains("customFields", resourceGrid, StringComparison.Ordinal);
        Assert.Contains("RefreshResourceGroups", viewModel, StringComparison.Ordinal);
        Assert.Contains("Computers, printers", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Computers, servers", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "network appliances",
            xaml,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SaveClientInfoCredential", viewModel, StringComparison.Ordinal);
        Assert.Contains("CompareClientInfoImportToFireDrill", viewModel, StringComparison.Ordinal);
        Assert.Contains("Another editor saved first", viewModel, StringComparison.Ordinal);
        Assert.Contains("new ClientInfoBetaWindow", mainWindow, StringComparison.Ordinal);
        Assert.Contains("new ClientInfoImportWindow", mainWindow, StringComparison.Ordinal);
        Assert.Contains("new ClientInfoWindow", mainWindow, StringComparison.Ordinal);
        Assert.Contains(
            "Header=\"Migration\" Visibility=\"Collapsed\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"Create Migration Workbook\"",
            importWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"Import Completed Workbook\"",
            importWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"Add to Client Information\"",
            importWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"Accept Remaining as Keep as-is\"",
            importWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"Discard Review\"",
            importWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "AcceptUnverifiedImportCommand",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "DiscardImportCommand",
            viewModel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalClientInfoHasItsOwnWorkspaceAndDoesNotReplaceFireDrill()
    {
        var navigation = Read("Controls", "WorkspaceNavigation.xaml");
        var mainWindowXaml = Read("MainWindow.xaml");
        var mainWindowCode = Read("MainWindow.xaml.cs");
        var workspaceViewModel = Read(
            "ViewModels",
            "MainWindowViewModel.ClientInfoBeta.cs");

        Assert.Contains("Header=\"CLIENTS\"", navigation, StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"Client Information\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"Workbook Imports\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter=\"Client Database\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter=\"Workbook Imports\"",
            navigation,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IsClientInfoBetaBuild",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "ConverterParameter=Client Database",
            mainWindowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding ClientInfoClients}\"",
            mainWindowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"View / Edit\"",
            mainWindowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "MouseDoubleClick=\"ClientInfoClientListBox_MouseDoubleClick\"",
            mainWindowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "DataContext = viewModel.CreateClientInfoProfile(summary)",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "#if TECHBENCH_CLIENT_INFO_BETA",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateCanonicalClientInfoProfile(\n        ClientInfoClientSummary summary)",
            workspaceViewModel.Replace("\r\n", "\n"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FireDrillCredentialSummary",
            workspaceViewModel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BetaWindowOwnsReadableThemeAwareTabAndGridTemplates()
    {
        var xaml = Read("ClientInfoBetaWindow.xaml");

        Assert.Contains(
            "<Style TargetType=\"TabItem\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<ControlTemplate TargetType=\"TabControl\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "TextElement.Foreground=\"{TemplateBinding Foreground}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Style TargetType=\"DataGridColumnHeader\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<ControlTemplate TargetType=\"DataGridColumnHeader\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Key=\"ColumnHeaderGripperStyle\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"ResizeIndicator\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Trigger Property=\"IsDragging\" Value=\"True\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Style=\"{StaticResource ColumnHeaderGripperStyle}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            xaml.Split(
                "Style=\"{StaticResource ColumnHeaderGripperStyle}\"",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "Value=\"{DynamicResource ControlAltBackgroundBrush}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Value=\"{DynamicResource PrimaryTextBrush}\"",
            xaml,
            StringComparison.Ordinal);
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

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static void AppendRow(
        string path,
        string sheetName,
        params string[] values)
    {
        using var workbook = SpreadsheetDocument.Open(path, true);
        var sheet = workbook.WorkbookPart!.Workbook.Sheets!
            .Elements<Sheet>()
            .Single(item => string.Equals(
                item.Name?.Value,
                sheetName,
                StringComparison.Ordinal));
        var part = (WorksheetPart)workbook.WorkbookPart.GetPartById(sheet.Id!);
        var sheetData = part.Worksheet.GetFirstChild<SheetData>()!;
        var rowIndex = (uint)(sheetData.Elements<Row>()
            .Select(row => row.RowIndex?.Value ?? 0U)
            .DefaultIfEmpty(0U)
            .Max() + 1U);
        var row = new Row { RowIndex = rowIndex };
        for (var index = 0; index < values.Length; index++)
        {
            row.Append(new Cell
            {
                CellReference = $"{TestColumnName(index + 1)}{rowIndex}",
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(values[index]))
            });
        }

        sheetData.Append(row);
        part.Worksheet.Save();
    }

    private static void AppendRowByHeader(
        string path,
        string sheetName,
        params (string Header, string Value)[] values)
    {
        using var workbook = SpreadsheetDocument.Open(path, true);
        var part = GetWorksheetPart(workbook, sheetName);
        var sheetData = part.Worksheet.GetFirstChild<SheetData>()!;
        var headerRow = sheetData.Elements<Row>()
            .Single(row => row.RowIndex?.Value == 1U);
        var columns = headerRow.Elements<Cell>()
            .Select((cell, index) => new
            {
                Header = cell.InlineString?.Text?.Text ?? string.Empty,
                Column = index + 1
            })
            .ToDictionary(
                item => item.Header,
                item => item.Column,
                StringComparer.OrdinalIgnoreCase);
        var rowIndex = (uint)(sheetData.Elements<Row>()
            .Select(row => row.RowIndex?.Value ?? 0U)
            .DefaultIfEmpty(0U)
            .Max() + 1U);
        var row = new Row { RowIndex = rowIndex };
        foreach (var (header, value) in values)
        {
            Assert.True(columns.TryGetValue(header, out var column),
                $"Worksheet '{sheetName}' did not contain header '{header}'.");
            row.Append(new Cell
            {
                CellReference = $"{TestColumnName(column)}{rowIndex}",
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(value))
            });
        }

        sheetData.Append(row);
        part.Worksheet.Save();
    }

    private static void AppendHeader(
        string path,
        string sheetName,
        string header)
    {
        using var workbook = SpreadsheetDocument.Open(path, true);
        var part = GetWorksheetPart(workbook, sheetName);
        var headerRow = part.Worksheet.GetFirstChild<SheetData>()!
            .Elements<Row>()
            .Single(row => row.RowIndex?.Value == 1U);
        var column = headerRow.Elements<Cell>().Count() + 1;
        headerRow.Append(new Cell
        {
            CellReference = $"{TestColumnName(column)}1",
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(header))
        });
        part.Worksheet.Save();
    }

    private static string[] ReadHeaderRow(
        SpreadsheetDocument workbook,
        string sheetName) =>
        GetWorksheetPart(workbook, sheetName)
            .Worksheet.GetFirstChild<SheetData>()!
            .Elements<Row>()
            .Single(row => row.RowIndex?.Value == 1U)
            .Elements<Cell>()
            .Select(cell => cell.InlineString?.Text?.Text ?? string.Empty)
            .ToArray();

    private static Cell ReadHeaderCell(
        SpreadsheetDocument workbook,
        string sheetName,
        string header) =>
        GetWorksheetPart(workbook, sheetName)
            .Worksheet.GetFirstChild<SheetData>()!
            .Elements<Row>()
            .Single(row => row.RowIndex?.Value == 1U)
            .Elements<Cell>()
            .Single(cell => string.Equals(
                cell.InlineString?.Text?.Text,
                header,
                StringComparison.OrdinalIgnoreCase));

    private static DataValidation ReadListValidation(
        SpreadsheetDocument workbook,
        string sheetName,
        string header)
    {
        var part = GetWorksheetPart(workbook, sheetName);
        var headerCells = part.Worksheet.GetFirstChild<SheetData>()!
            .Elements<Row>()
            .Single(row => row.RowIndex?.Value == 1U)
            .Elements<Cell>()
            .ToArray();
        var column = Array.FindIndex(
            headerCells,
            cell => string.Equals(
                cell.InlineString?.Text?.Text,
                header,
                StringComparison.OrdinalIgnoreCase)) + 1;
        Assert.True(column > 0, $"Worksheet '{sheetName}' did not contain header '{header}'.");
        var range = $"{TestColumnName(column)}2:{TestColumnName(column)}500";
        return Assert.Single(
            part.Worksheet.Descendants<DataValidation>(),
            validation => validation.SequenceOfReferences?.InnerText == range);
    }

    private static WorksheetPart GetWorksheetPart(
        SpreadsheetDocument workbook,
        string sheetName)
    {
        var sheet = workbook.WorkbookPart!.Workbook.Sheets!
            .Elements<Sheet>()
            .Single(item => string.Equals(
                item.Name?.Value,
                sheetName,
                StringComparison.Ordinal));
        return (WorksheetPart)workbook.WorkbookPart.GetPartById(sheet.Id!);
    }

    private static void SetLegacyResourceHeaders(
        WorkbookPart workbookPart,
        IEnumerable<Sheet> sheets)
    {
        foreach (var sheet in sheets)
        {
            var part = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
            var headerRow = part.Worksheet.GetFirstChild<SheetData>()!
                .Elements<Row>()
                .Single(row => row.RowIndex?.Value == 1U);
            headerRow.RemoveAllChildren<Cell>();
            var headers = new[]
            {
                "Type", "Name", "Provider", "Address/URL", "Location",
                "Status", "Notes", "Review Status"
            };
            for (var index = 0; index < headers.Length; index++)
            {
                headerRow.Append(new Cell
                {
                    CellReference = $"{TestColumnName(index + 1)}1",
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new Text(headers[index]))
                });
            }

            part.Worksheet.Save();
        }
    }

    private static void ConvertGeneratedWorkbookToFriendlyVersion(string path)
    {
        using var workbook = SpreadsheetDocument.Open(path, true);
        var workbookPart = workbook.WorkbookPart!;
        var sheets = workbookPart.Workbook.Sheets!.Elements<Sheet>().ToList();
        SetTemplateVersion(
            workbookPart,
            sheets,
            ClientInfoWorkbookService.FriendlyTemplateVersion);
        ConvertSplitProtectionToLegacy(sheets);

        sheets.Single(sheet => sheet.Name?.Value == "Connection & Internet").Name =
            "Systems & Services";
        SetLegacyResourceHeaders(
            workbookPart,
            sheets.Where(sheet => sheet.Name?.Value == "Systems & Services"));
        foreach (var name in new[]
                 {
                     "Servers & Infrastructure",
                     "Wi-Fi",
                     "Applications & Cloud",
                     "Domains & Email",
                     "Backup & Security",
                     "Vendors & Services",
                     "Needs Sorting"
                 })
        {
            sheets.Single(sheet => sheet.Name?.Value == name).Remove();
        }

        workbookPart.Workbook.Save();
    }

    private static void ConvertGeneratedWorkbookToPreviousCategorizedVersion(
        string path)
    {
        using var workbook = SpreadsheetDocument.Open(path, true);
        var workbookPart = workbook.WorkbookPart!;
        var sheets = workbookPart.Workbook.Sheets!.Elements<Sheet>().ToList();
        SetTemplateVersion(
            workbookPart,
            sheets,
            ClientInfoWorkbookService.CategorizedTemplateVersion);
        ConvertSplitProtectionToLegacy(sheets);
        sheets.Single(sheet => sheet.Name?.Value == "Connection & Internet").Name =
            "Network & Internet";
        sheets.Single(sheet => sheet.Name?.Value == "Wi-Fi").Remove();
        sheets.Single(sheet => sheet.Name?.Value == "Needs Sorting").Remove();
        SetLegacyResourceHeaders(
            workbookPart,
            sheets.Where(sheet => sheet.Name?.Value is
                "Servers & Infrastructure" or
                "Network & Internet" or
                "Applications & Cloud" or
                "Domains & Email" or
                "Backup & Security" or
                "Vendors & Services"));
        workbookPart.Workbook.Save();
    }

    private static void ConvertGeneratedWorkbookToPreviousConnectionVersion(
        string path)
    {
        using var workbook = SpreadsheetDocument.Open(path, true);
        var workbookPart = workbook.WorkbookPart!;
        var sheets = workbookPart.Workbook.Sheets!.Elements<Sheet>().ToList();
        SetTemplateVersion(
            workbookPart,
            sheets,
            ClientInfoWorkbookService.ConnectionTemplateVersion);
        ConvertSplitProtectionToLegacy(sheets);
        sheets.Single(sheet => sheet.Name?.Value == "Wi-Fi").Remove();
        sheets.Single(sheet => sheet.Name?.Value == "Needs Sorting").Remove();
        SetLegacyResourceHeaders(
            workbookPart,
            sheets.Where(sheet => sheet.Name?.Value is
                "Servers & Infrastructure" or
                "Connection & Internet" or
                "Applications & Cloud" or
                "Domains & Email" or
                "Backup & Security" or
                "Vendors & Services"));
        workbookPart.Workbook.Save();
    }

    private static void ConvertGeneratedWorkbookToPreviousWifiVersion(
        string path)
    {
        using var workbook = SpreadsheetDocument.Open(path, true);
        var workbookPart = workbook.WorkbookPart!;
        var sheets = workbookPart.Workbook.Sheets!.Elements<Sheet>().ToList();
        SetTemplateVersion(
            workbookPart,
            sheets,
            ClientInfoWorkbookService.WifiTemplateVersion);
        ConvertSplitProtectionToLegacy(sheets);
        sheets.Single(sheet => sheet.Name?.Value == "Needs Sorting").Remove();
        var usersSheet = sheets.SingleOrDefault(sheet =>
            string.Equals(
                sheet.Name?.Value,
                "Users",
                StringComparison.OrdinalIgnoreCase));
        if (usersSheet is not null)
        {
            usersSheet.Name = "People";
        }
        SetLegacyResourceHeaders(
            workbookPart,
            sheets.Where(sheet => sheet.Name?.Value is
                "Servers & Infrastructure" or
                "Connection & Internet" or
                "Wi-Fi" or
                "Applications & Cloud" or
                "Domains & Email" or
                "Backup & Security" or
                "Vendors & Services"));
        workbookPart.Workbook.Save();
    }

    private static void ConvertGeneratedWorkbookToPreviousTemplateVersion(
        string path)
    {
        using var workbook = SpreadsheetDocument.Open(path, true);
        var workbookPart = workbook.WorkbookPart!;
        var sheets = workbookPart.Workbook.Sheets!.Elements<Sheet>().ToList();
        SetTemplateVersion(
            workbookPart,
            sheets,
            ClientInfoWorkbookService.PreviousTemplateVersion);
        ConvertSplitProtectionToLegacy(sheets);
        sheets.Single(sheet => sheet.Name?.Value == "Needs Sorting").Remove();
        SetLegacyResourceHeaders(
            workbookPart,
            sheets.Where(sheet => sheet.Name?.Value is
                "Servers & Infrastructure" or
                "Connection & Internet" or
                "Wi-Fi" or
                "Applications & Cloud" or
                "Domains & Email" or
                "Backup & Security" or
                "Vendors & Services"));
        workbookPart.Workbook.Save();
    }

    private static void ConvertSplitProtectionToLegacy(
        IReadOnlyList<Sheet> sheets)
    {
        sheets.Single(sheet => sheet.Name?.Value == "Backup").Name =
            "Backup & Security";
        sheets.Single(sheet => sheet.Name?.Value == "Security").Remove();
    }

    private static void SetTemplateVersion(
        WorkbookPart workbookPart,
        IReadOnlyList<Sheet> sheets,
        string version)
    {
        var startHere = sheets.Single(sheet => sheet.Name?.Value == "Start Here");
        var startPart = (WorksheetPart)workbookPart.GetPartById(startHere.Id!);
        var versionCell = startPart.Worksheet.GetFirstChild<SheetData>()!
            .Elements<Row>()
            .Single(row => row.RowIndex?.Value == 5U)
            .Elements<Cell>()
            .Single(cell => cell.CellReference?.Value == "B5");
        versionCell.DataType = CellValues.InlineString;
        versionCell.InlineString = new InlineString(new Text(version));
        startPart.Worksheet.Save();
    }

    private static string TestColumnName(int column)
    {
        var result = string.Empty;
        while (column > 0)
        {
            column--;
            result = (char)('A' + column % 26) + result;
            column /= 26;
        }

        return result;
    }
}
