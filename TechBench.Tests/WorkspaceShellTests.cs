namespace TechBench.Tests;

public sealed class WorkspaceShellTests
{
    [Fact]
    public void AppExposesDatabaseFreeShellPreview()
    {
        var app = ReadRepositoryFile("App.xaml.cs");
        var shell = ReadRepositoryFile("WorkspaceShellDemoWindow.xaml");

        Assert.Contains("\"--shell-demo\"", app, StringComparison.Ordinal);
        Assert.Contains("new WorkspaceShellDemoWindow()", app, StringComparison.Ordinal);
        Assert.Contains(
            "<controls:WorkspaceNavigation",
            shell,
            StringComparison.Ordinal);
        Assert.Contains(
            "<controls:WorkspaceHeader",
            shell,
            StringComparison.Ordinal);
        Assert.Contains(
            "Nothing is saved",
            shell,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowUsesReusableShellControls()
    {
        var mainWindow = ReadRepositoryFile("MainWindow.xaml");

        Assert.Contains(
            "<controls:WorkspaceNavigation Grid.Column=\"0\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "<controls:WorkspaceHeader Grid.Row=\"0\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "x:Key=\"NavTodayStyle\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "x:Key=\"HeaderUpdateButtonStyle\"",
            mainWindow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationThemesAllScrollBars()
    {
        var app = ReadRepositoryFile("App.xaml");

        Assert.Contains(
            "<Style TargetType=\"{x:Type ScrollBar}\">",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"ScrollThumb\"",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "ScrollBar.PageLeftCommand",
            app,
            StringComparison.Ordinal);
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
}
