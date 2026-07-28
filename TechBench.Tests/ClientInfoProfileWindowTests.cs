using TechBench.Models;
using TechBench.ViewModels;

namespace TechBench.Tests;

public sealed class ClientInfoProfileWindowTests
{
    [Fact]
    public void ProfileStartsProtectedAndRevealLoadsTheCompleteClient()
    {
        var summary = new FireDrillCredentialSummary(
            42,
            "Acme Test",
            string.Empty,
            string.Empty,
            DateTime.UtcNow,
            [
                Field("Wireless SSID", 0, string.Empty),
                Field("Microsoft 365 Password", 1, string.Empty)
            ]);
        var revealed = new FireDrillCredential(
            42,
            "Acme Test",
            string.Empty,
            string.Empty,
            summary.LastSyncedAtUtc,
            [
                Field("Wireless SSID", 0, "Acme Staff"),
                Field("Microsoft 365 Password", 1, "private-value")
            ]);
        var profile = new ClientInfoProfileViewModel(summary, _ => revealed);

        Assert.False(profile.IsRevealed);
        Assert.All(
            profile.CredentialGroups.SelectMany(group => group.Fields),
            field => Assert.Equal("***", field.Value));

        profile.RevealCommand.Execute(null);

        Assert.True(profile.IsRevealed);
        Assert.Equal(2, profile.CredentialGroups.Count);
        Assert.Contains(
            profile.CredentialGroups.SelectMany(group => group.Fields),
            field => field.Value == "Acme Staff");
        Assert.Contains(
            profile.CredentialGroups.SelectMany(group => group.Fields),
            field => field.Value == "private-value");

        profile.HideCommand.Execute(null);

        Assert.False(profile.IsRevealed);
        Assert.All(
            profile.CredentialGroups.SelectMany(group => group.Fields),
            field => Assert.Equal("***", field.Value));
    }

    [Fact]
    public void CredentialResultDoubleClickOpensProfessionalCompleteProfileWindow()
    {
        var mainWindowXaml = ReadRepositoryFile("MainWindow.xaml");
        var mainWindowCode = ReadRepositoryFile("MainWindow.xaml.cs");
        var profileXaml = ReadRepositoryFile("ClientInfoWindow.xaml");

        Assert.Contains(
            "MouseDoubleClick=\"FireDrillCredentialsListBox_MouseDoubleClick\"",
            mainWindowXaml,
            StringComparison.Ordinal);
        Assert.Contains("new ClientInfoWindow", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("COMPLETE CLIENT PROFILE", profileXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding CredentialGroups}\"", profileXaml, StringComparison.Ordinal);
        Assert.Contains("<WrapPanel />", profileXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Reveal All\"", profileXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Hide\"", profileXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Copy\"", profileXaml, StringComparison.Ordinal);
        Assert.Contains("WEB HELP DESK MATCH", profileXaml, StringComparison.Ordinal);
        Assert.Contains("MAIN CONTACT", profileXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"ADDRESS\"", profileXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"CLIENT INVENTORY\"", profileXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Equipment}\"", profileXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"EquipmentCard_Click\"", profileXaml, StringComparison.Ordinal);
        Assert.Contains(
            "EquipmentOpenRequested += async",
            mainWindowCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FullProfileDisplaysTheClientsAssignedInventory()
    {
        var summary = new FireDrillCredentialSummary(
            42,
            "Acme Test",
            string.Empty,
            string.Empty,
            DateTime.UtcNow,
            []);
        var equipment = new EquipmentItem
        {
            EquipmentId = 7,
            Name = "Reception PC",
            DeviceType = "Desktop",
            AssetTag = "TB-0007",
            ClientId = 12,
            ClientName = "Acme Test",
            ClientUserId = 99,
            ClientUserDisplayName = "Jamie Rivera"
        };

        var profile = new ClientInfoProfileViewModel(
            summary,
            _ => null,
            equipment: [equipment]);

        Assert.True(profile.HasEquipment);
        Assert.True(profile.HasProfileContent);
        Assert.False(profile.HasFields);
        Assert.Equal("1 inventory item", profile.EquipmentCountLabel);
        Assert.Same(equipment, Assert.Single(profile.Equipment));
    }

    [Fact]
    public void FullProfileConfidentlyMatchesWHDContactDetails()
    {
        var clients = new[]
        {
            new Client
            {
                Id = 7,
                Name = "Acme Services LLC",
                WhdLocationName = "Acme Services",
                WhdContactName = "Jamie Rivera",
                WhdContactEmail = "jamie@example.test",
                WhdPhone = "610-555-0188",
                WhdAddress = "100 Main Street, Malvern, PA 19355"
            },
            new Client { Id = 8, Name = "Another Client" }
        };

        var match = ClientProfileWhdMatcher.FindConfidentMatch("Acme Services, Inc.", clients);
        var profile = new ClientInfoProfileViewModel(
            new FireDrillCredentialSummary(1, "Acme Services, Inc.", "", "", DateTime.UtcNow, []),
            _ => null,
            match);

        Assert.NotNull(match);
        Assert.True(profile.HasWhdMatch);
        Assert.Equal("Jamie Rivera", profile.WhdContactName);
        Assert.Equal("jamie@example.test", profile.WhdContactEmail);
        Assert.Equal("610-555-0188", profile.WhdPhone);
        Assert.Equal("100 Main Street, Malvern, PA 19355", profile.WhdAddress);
    }

    [Fact]
    public void FullProfileDoesNotGuessWhenANameMatchIsAmbiguous()
    {
        var clients = new[]
        {
            new Client { Id = 1, Name = "North Campus", Source = "WHD" },
            new Client { Id = 2, Name = "North Campus", Source = "WHD" }
        };

        var match = ClientProfileWhdMatcher.FindConfidentMatch("North Campus", clients);

        Assert.Null(match);
    }

    private static FireDrillCredentialField Field(
        string label,
        int order,
        string value) =>
        new()
        {
            Label = label,
            FieldName = label.ToLowerInvariant(),
            SortOrder = order,
            Value = value
        };

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
}
