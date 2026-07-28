using TechBench.Data;
using TechBench.Models;

namespace TechBench.Tests;

public sealed class ClientUsersWorkspaceTests
{
    [Fact]
    public void SidebarPlacesUsersUnderClientInfoAndClientMatchUnderSystem()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var directory = xaml.IndexOf(
            "Text=\"DIRECTORY\"",
            StringComparison.Ordinal);
        var clientInfo = xaml.IndexOf(
            "CommandParameter=\"Client Info\"",
            StringComparison.Ordinal);
        var users = xaml.IndexOf(
            "CommandParameter=\"Client Users\"",
            StringComparison.Ordinal);
        var clientWifi = xaml.IndexOf(
            "CommandParameter=\"Client WiFi\"",
            StringComparison.Ordinal);
        var system = xaml.IndexOf(
            "Text=\"SYSTEM\"",
            StringComparison.Ordinal);
        var clientMatch = xaml.IndexOf(
            "Content=\"Client Match\" Command=\"{Binding NavigateCommand}\" CommandParameter=\"Client Match\"",
            StringComparison.Ordinal);
        var postingHistory = xaml.IndexOf(
            "CommandParameter=\"Posting History\"",
            StringComparison.Ordinal);

        Assert.True(directory >= 0);
        Assert.True(clientInfo > directory);
        Assert.True(users > clientInfo);
        Assert.True(clientWifi > users);
        Assert.True(system > clientWifi);
        Assert.True(clientMatch > system);
        Assert.True(postingHistory > clientMatch);
        Assert.DoesNotContain(
            "CommandParameter=\"Client List\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding CanAccessClientUsers, Converter={StaticResource BooleanToVisibilityConverter}}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ClientUsersWorkspaceSelectsClientThenOpensUserDetailsDrawer()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var viewModel = ReadRepositoryFile(
            Path.Combine("ViewModels", "MainWindowViewModel.ClientUsers.cs"));
        var models = ReadRepositoryFile(
            Path.Combine("Models", "ClientUserInfo.cs"));

        Assert.Contains(
            "ConverterParameter=Client Users",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding ClientUserClients}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedItem=\"{Binding SelectedClientUserClient}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding ClientUsers}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedItem=\"{Binding SelectedClientUser}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding ClientUserAccountGroups}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding SelectedClientUserEquipment}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Click=\"OpenEquipmentFromInventory_Click\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"Assigned Equipment\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding Fields}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding RevealClientUserCommand}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding CloseClientUserDetailsCommand}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "TranslateTransform X=\"760\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"Clear\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding ClearClientUserSearchCommand}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<KeyBinding Key=\"Enter\" Command=\"{Binding SearchClientUsersCommand}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "field with { Value = \"***\" }",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            ".GroupBy(user => new { user.ClientId, user.ClientName })",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "PopulateClientUsers(value?.ClientId)",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetEquipmentInventory(clientUserId: clientUserId)",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "record ClientUserClientSummary",
            models,
            StringComparison.Ordinal);
        Assert.Contains(
            "await ClipboardService.TrySetTextAsync(field.Value)",
            viewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "System.Windows.Clipboard",
            viewModel,
            StringComparison.Ordinal);

        var mainWindowCode = ReadRepositoryFile("MainWindow.xaml.cs");
        Assert.Contains(
            "OpenEquipmentFromInventoryAsync(equipment)",
            mainWindowCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryUsesStoredProceduresForMaskedSearchAndReveal()
    {
        Assert.Equal(
            "[tb_app].[SearchClientUsers]",
            SqlServerTechBenchRepository.Procedures.SearchClientUsers);
        Assert.Equal(
            "[tb_app].[RevealClientUser]",
            SqlServerTechBenchRepository.Procedures.RevealClientUser);
        Assert.Equal(
            "[tb_app].[GetEquipmentInventory]",
            SqlServerTechBenchRepository.Procedures.GetEquipmentInventory);

        var contract = typeof(ITechBenchRepository);
        Assert.NotNull(contract.GetMethod(nameof(ITechBenchRepository.SearchClientUsers)));
        Assert.NotNull(contract.GetMethod(nameof(ITechBenchRepository.RevealClientUser)));
        Assert.NotNull(contract.GetMethod(nameof(ITechBenchRepository.GetEquipmentInventory)));

        var model = new ClientUserSummary(
            1,
            2,
            "Example Client",
            "Pat Example",
            "Accounting",
            "pat@example.com",
            "555-0100",
            "Main Office",
            DateTime.UtcNow,
            3,
            []);
        Assert.Equal("Pat Example", model.DisplayName);
        Assert.Equal(3, model.AccountCount);
    }

    [Fact]
    public void SqlContractMasksSearchAndDecryptsOnlyOnExplicitReveal()
    {
        var procedures = ReadRepositoryFile(Path.Combine(
            "database",
            "sqlserver2016",
            "54-V0014-EquipmentBoardProcedures.sql"));
        var grants = ReadRepositoryFile(Path.Combine(
            "database",
            "sqlserver2016",
            "60-V0014-EquipmentBoardGrants.sql"));
        var verifier = ReadRepositoryFile(Path.Combine(
            "database",
            "sqlserver2016",
            "103-V0014-EquipmentBoardVerify.sql"));

        var searchStart = procedures.IndexOf(
            "CREATE PROCEDURE [tb_app].[SearchClientUsers]",
            StringComparison.OrdinalIgnoreCase);
        var revealStart = procedures.IndexOf(
            "CREATE PROCEDURE [tb_app].[RevealClientUser]",
            StringComparison.OrdinalIgnoreCase);
        var inventoryStart = procedures.IndexOf(
            "CREATE PROCEDURE [tb_app].[GetEquipmentInventory]",
            StringComparison.OrdinalIgnoreCase);
        Assert.True(searchStart >= 0);
        Assert.True(revealStart > searchStart);
        Assert.True(inventoryStart >= 0);

        var search = procedures[searchStart..revealStart];
        var reveal = procedures[revealStart..];
        Assert.DoesNotContain(
            "DecryptByKeyAutoCert",
            search,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "CONVERT(nvarchar(1), N'') AS [value]",
            search,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "DecryptByKeyAutoCert",
            reveal,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "ClientUserAccountId",
            reveal,
            StringComparison.OrdinalIgnoreCase);

        foreach (var role in new[] { "tb_role_user", "tb_role_admin" })
        {
            Assert.Contains(
                $"GRANT EXECUTE ON OBJECT::[tb_app].[SearchClientUsers] TO [{role}]",
                grants,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                $"GRANT EXECUTE ON OBJECT::[tb_app].[RevealClientUser] TO [{role}]",
                grants,
                StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(
            "client-user read procedures are missing",
            verifier,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "preview or Sync Service can execute a client-user read procedure",
            verifier,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "GRANT EXECUTE ON OBJECT::[tb_app].[GetEquipmentInventory] TO [tb_role_user]",
            grants,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "GRANT EXECUTE ON OBJECT::[tb_app].[GetEquipmentInventory] TO [tb_role_admin]",
            grants,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "shared equipment inventory read is not client-scoped and archive-safe",
            verifier,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "TechBenchV2.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }
}
