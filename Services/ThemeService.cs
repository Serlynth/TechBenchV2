using System.Windows;
using System.Windows.Media;
using WpfApplication = System.Windows.Application;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfSystemColors = System.Windows.SystemColors;

namespace TechBench.Services;

public enum AppTheme
{
    Dark,
    Light
}

public static class ThemeService
{
    public static void Apply(AppTheme theme)
    {
        var resources = WpfApplication.Current.Resources;

        if (theme == AppTheme.Light)
        {
            SetBrush(resources, "WindowBackgroundBrush", "#F4F7FA");
            SetBrush(resources, "PanelBackgroundBrush", "#FFFFFF");
            SetBrush(resources, "PanelAltBackgroundBrush", "#ECF1F5");
            SetBrush(resources, "SidebarBackgroundBrush", "#07101A");
            SetBrush(resources, "HeaderBackgroundBrush", "#FFFFFF");
            SetBrush(resources, "ControlBackgroundBrush", "#FFFFFF");
            SetBrush(resources, "ControlAltBackgroundBrush", "#F2F5F8");
            SetBrush(resources, "BorderBrush", "#CDD6DF");
            SetBrush(resources, "PrimaryTextBrush", "#17212B");
            SetBrush(resources, "SecondaryTextBrush", "#52616F");
            SetBrush(resources, "MutedTextBrush", "#5F6F7E");
            SetBrush(resources, "AccentBrush", "#147CFF");
            SetBrush(resources, "AccentHoverBrush", "#0E66D8");
            SetBrush(resources, "AccentSoftBrush", "#DCEBFF");
            SetBrush(resources, "SurfaceHoverBrush", "#E7EEF5");
            SetBrush(resources, "SidebarHoverBrush", "#111C2A");
            SetBrush(resources, "SidebarActiveBrush", "#14243A");
            SetBrush(resources, "DangerBrush", "#C84646");
            SetBrush(resources, "WarningBrush", "#D89B25");
            SetBrush(resources, "SuccessBrush", "#237348");
            SetBrush(resources, "BillableBadgeBrush", "#86B7FF");
            SetBrush(resources, "NeutralBadgeBrush", "#AAB4BE");
            SetBrush(resources, "NoTicketBadgeBrush", "#D2A3FF");
            SetBrush(resources, "SidebarTextBrush", "#F3F7FA");
            SetBrush(resources, "SidebarMutedTextBrush", "#A9B9C9");
            SetBrush(resources, "SuccessSoftBrush", "#DDF3E7");
            SetBrush(resources, "SuccessSoftHoverBrush", "#C7E8D6");
            SetBrush(resources, "ComboBoxBackgroundBrush", "#F7F9FB");
            SetBrush(resources, "ComboBoxTextBrush", "#101820");
            SetBrush(resources, "ComboBoxBorderBrush", "#9AA8B3");
            SetBrush(resources, "ComboBoxHighlightBrush", "#147CFF");
            SetBrush(resources, "ComboBoxHighlightTextBrush", "#FFFFFF");
            SetBrush(resources, WpfSystemColors.WindowBrushKey, "#F7F9FB");
            SetBrush(resources, WpfSystemColors.WindowTextBrushKey, "#101820");
            SetBrush(resources, WpfSystemColors.ControlTextBrushKey, "#101820");
            SetBrush(resources, WpfSystemColors.HighlightBrushKey, "#147CFF");
            SetBrush(resources, WpfSystemColors.HighlightTextBrushKey, "#FFFFFF");
        }
        else
        {
            SetBrush(resources, "WindowBackgroundBrush", "#080D14");
            SetBrush(resources, "PanelBackgroundBrush", "#101822");
            SetBrush(resources, "PanelAltBackgroundBrush", "#162232");
            SetBrush(resources, "SidebarBackgroundBrush", "#070B11");
            SetBrush(resources, "HeaderBackgroundBrush", "#0B111A");
            SetBrush(resources, "ControlBackgroundBrush", "#0C141F");
            SetBrush(resources, "ControlAltBackgroundBrush", "#182536");
            SetBrush(resources, "BorderBrush", "#223247");
            SetBrush(resources, "PrimaryTextBrush", "#F7FAFC");
            SetBrush(resources, "SecondaryTextBrush", "#B8C6D6");
            SetBrush(resources, "MutedTextBrush", "#75869A");
            SetBrush(resources, "AccentBrush", "#3B82F6");
            SetBrush(resources, "AccentHoverBrush", "#60A5FA");
            SetBrush(resources, "AccentSoftBrush", "#18355F");
            SetBrush(resources, "SurfaceHoverBrush", "#1C2B3E");
            SetBrush(resources, "SidebarHoverBrush", "#111C2A");
            SetBrush(resources, "SidebarActiveBrush", "#14243A");
            SetBrush(resources, "DangerBrush", "#EF6464");
            SetBrush(resources, "WarningBrush", "#F5B942");
            SetBrush(resources, "SuccessBrush", "#55D69E");
            SetBrush(resources, "BillableBadgeBrush", "#8AB7FF");
            SetBrush(resources, "NeutralBadgeBrush", "#A6B2C0");
            SetBrush(resources, "NoTicketBadgeBrush", "#C4A7FF");
            SetBrush(resources, "SidebarTextBrush", "#F4F8FC");
            SetBrush(resources, "SidebarMutedTextBrush", "#93A6BA");
            SetBrush(resources, "SuccessSoftBrush", "#102D25");
            SetBrush(resources, "SuccessSoftHoverBrush", "#194638");
            SetBrush(resources, "ComboBoxBackgroundBrush", "#0C141F");
            SetBrush(resources, "ComboBoxTextBrush", "#F7FAFC");
            SetBrush(resources, "ComboBoxBorderBrush", "#223247");
            SetBrush(resources, "ComboBoxHighlightBrush", "#3B82F6");
            SetBrush(resources, "ComboBoxHighlightTextBrush", "#FFFFFF");
            SetBrush(resources, WpfSystemColors.WindowBrushKey, "#0C141F");
            SetBrush(resources, WpfSystemColors.WindowTextBrushKey, "#F7FAFC");
            SetBrush(resources, WpfSystemColors.ControlTextBrushKey, "#F7FAFC");
            SetBrush(resources, WpfSystemColors.HighlightBrushKey, "#3B82F6");
            SetBrush(resources, WpfSystemColors.HighlightTextBrushKey, "#FFFFFF");
        }
    }

    private static void SetBrush(ResourceDictionary resources, object key, string color)
    {
        resources[key] = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(color));
    }
}
