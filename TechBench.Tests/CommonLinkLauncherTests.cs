using TechBench.Services;

namespace TechBench.Tests;

public sealed class CommonLinkLauncherTests
{
    [Fact]
    public void ChromeIncognitoLaunchUsesSeparateArguments()
    {
        const string chromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        const string url = "https://admin.microsoft.com/?source=techbench&mode=test";

        var startInfo = CommonLinkLauncher.CreateChromeIncognitoStartInfo(chromePath, url);

        Assert.Equal(chromePath, startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(new[] { "--incognito", url }, startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
    }

    [Fact]
    public void ChromeDiscoverySelectsFirstExistingExecutable()
    {
        var candidates = new[]
        {
            @"C:\missing\chrome.exe",
            @"C:\Chrome\chrome.exe",
            @"C:\Other\chrome.exe"
        };

        var result = CommonLinkLauncher.SelectExistingChromeExecutable(
            candidates,
            path => path == @"C:\Chrome\chrome.exe" || path == @"C:\Other\chrome.exe");

        Assert.Equal(@"C:\Chrome\chrome.exe", result);
    }
}
