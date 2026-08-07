using TechBench.Formatting;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class AnyDeskLauncherTests
{
    [Theory]
    [InlineData("123456789", "123 456 789")]
    [InlineData("123 456 789", "123 456 789")]
    [InlineData("123-456-789", "123 456 789")]
    [InlineData("1-234-567-890", "1 234 567 890")]
    [InlineData("1 234 567 890", "1 234 567 890")]
    public void DisplayFormattingGroupsSupportedNumericIds(
        string input,
        string expected)
    {
        Assert.Equal(
            expected,
            AnyDeskIdFormatter.FormatForDisplay(input));
    }

    [Theory]
    [InlineData("2234567890")]
    [InlineData("123-45")]
    [InlineData("support-pc@ad")]
    [InlineData("  support-pc@ad  ")]
    public void DisplayFormattingPreservesNonstandardAddresses(string input)
    {
        Assert.Equal(
            input,
            AnyDeskIdFormatter.FormatForDisplay(input));
    }

    [Fact]
    public void DisplayFormattingTreatsNullAsEmpty()
    {
        Assert.Equal(
            string.Empty,
            AnyDeskIdFormatter.FormatForDisplay(null));
    }

    [Fact]
    public void EquipmentExposesFormattedIdWithoutChangingStoredValue()
    {
        var equipment = new EquipmentItem
        {
            AnyDeskNumber = "1-234-567-890"
        };

        Assert.Equal("1-234-567-890", equipment.AnyDeskNumber);
        Assert.Equal("1 234 567 890", equipment.AnyDeskDisplayNumber);
    }

    [Theory]
    [InlineData("123 456 789", "123456789")]
    [InlineData("1-234-567-890", "1234567890")]
    [InlineData(" support-pc@ad ", "support-pc@ad")]
    public void AddressNormalizationHandlesDisplayedIdsAndAliases(
        string input,
        string expected)
    {
        Assert.Equal(
            expected,
            AnyDeskLauncher.NormalizeAddress(input));
    }

    [Theory]
    [InlineData("123456789")]
    [InlineData("1234567890")]
    [InlineData("support-pc@ad")]
    [InlineData("server_01@csri.remote")]
    public void DocumentedAddressFormsAreAccepted(string address)
    {
        Assert.True(AnyDeskLauncher.IsValidAddress(address));
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("--set-password")]
    [InlineData("not an alias")]
    public void InvalidOrOptionLikeAddressesAreRejected(string address)
    {
        Assert.False(AnyDeskLauncher.IsValidAddress(address));
    }

    [Fact]
    public void PasswordConnectionUsesStandardInputAndNeverAnArgument()
    {
        const string password = "SuperSecret!42";

        var startInfo = AnyDeskLauncher.CreateStartInfo(
            @"C:\Program Files (x86)\AnyDesk\AnyDesk.exe",
            "123456789",
            submitPassword: true);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.Equal(
            new[] { "123456789", "--with-password" },
            startInfo.ArgumentList);
        Assert.DoesNotContain(
            password,
            startInfo.ArgumentList);

        using var input = new StringWriter();
        AnyDeskLauncher.WritePasswordToStandardInput(
            input,
            password);
        Assert.Equal(
            password + Environment.NewLine,
            input.ToString());
    }

    [Fact]
    public void ConnectionWithoutPasswordStartsTheNormalAnyDeskPrompt()
    {
        var startInfo = AnyDeskLauncher.CreateStartInfo(
            @"C:\AnyDesk.exe",
            "support-pc@ad",
            submitPassword: false);

        Assert.False(startInfo.RedirectStandardInput);
        Assert.Equal(
            new[] { "support-pc@ad" },
            startInfo.ArgumentList);
    }

    [Fact]
    public void ExecutableDiscoveryChoosesTheFirstExistingCandidate()
    {
        var selected = AnyDeskLauncher.SelectExistingExecutable(
            [
                null,
                "missing.exe",
                "  \"C:\\Tools\\AnyDesk.exe\"  ",
                "later.exe"
            ],
            path => path.Equals(
                @"C:\Tools\AnyDesk.exe",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal(
            @"C:\Tools\AnyDesk.exe",
            selected);
    }
}
