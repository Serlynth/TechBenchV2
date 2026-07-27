using TechBench.Data;
using TechBench.Models;

namespace TechBench.Tests;

public sealed class ClientUsersWorkspaceTests
{
    [Fact]
    public void SidebarPlacesUsersDirectlyUnderClients()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var clients = xaml.IndexOf(
            "CommandParameter=\"Client List\"",
            StringComparison.Ordinal);
        var users = xaml.IndexOf(
            "CommandParameter=\"Client Users\"",
            StringComparison.Ordinal);
        var tickets = xaml.IndexOf(
            "CommandParameter=\"Ticket List\"",
            StringComparison.Ordinal);

        Assert.True(clients >= 0);
        Assert.True(users > clients);
        Assert.True(tickets > users);
        Assert.Contains(
            "Visibility=\"{Binding CanAccessClientUsers, Converter={StaticResource BooleanToVisibilityConverter}}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ClientUsersWorkspaceSearchesAndRevealsDynamicAccountGroups()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var viewModel = ReadRepositoryFile(
            Path.Combine("ViewModels", "MainWindowViewModel.ClientUsers.cs"));

        Assert.Contains(
            "ConverterParameter=Client Users",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding ClientUsers}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding ClientUserAccountGroups}\"",
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
            "Content=\"Clear\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding ClearClientUserSearchCommand}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "field with { Value = \"***\" }",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "await ClipboardService.TrySetTextAsync(field.Value)",
            viewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "System.Windows.Clipboard",
            viewModel,
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

        var contract = typeof(ITechBenchRepository);
        Assert.NotNull(contract.GetMethod(nameof(ITechBenchRepository.SearchClientUsers)));
        Assert.NotNull(contract.GetMethod(nameof(ITechBenchRepository.RevealClientUser)));

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
        Assert.True(searchStart >= 0);
        Assert.True(revealStart > searchStart);

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
