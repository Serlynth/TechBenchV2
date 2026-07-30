using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class SidebarThemeTests
{
    [Fact]
    public void LightThemeSidebarLabelsUseReadableBrushes()
    {
        RunInSta(() =>
        {
            var app = new App();
            app.InitializeComponent();
            ThemeService.Apply(AppTheme.Light);

            var button = new Button
            {
                Content = "Settings",
                Style = Assert.IsType<Style>(app.Resources["SidebarButtonStyle"])
            };

            button.ApplyTemplate();
            button.Measure(new Size(220, 50));
            button.Arrange(new Rect(0, 0, 220, 50));
            button.UpdateLayout();

            var label = FindVisualChild<TextBlock>(button, "NavLabel");
            Assert.NotNull(label);
            Assert.Equal(Color.FromRgb(0xF3, 0xF7, 0xFA), Assert.IsType<SolidColorBrush>(label.Foreground).Color);

            button.Tag = "Active";
            button.UpdateLayout();
            Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(label.Foreground).Color);

            ThemeService.Apply(AppTheme.Dark, BenchModule.SalesBench);
            Assert.Equal(
                Color.FromRgb(0x22, 0xC5, 0x5E),
                Assert.IsType<SolidColorBrush>(app.Resources["AccentBrush"]).Color);

            ThemeService.Apply(AppTheme.Dark, BenchModule.AdminBench);
            Assert.Equal(
                Color.FromRgb(0xEF, 0x44, 0x44),
                Assert.IsType<SolidColorBrush>(app.Resources["AccentBrush"]).Color);
        });
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && match.Name == name)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
