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
