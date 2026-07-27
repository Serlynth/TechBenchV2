using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using TechBench.Models;

namespace TechBench.Controls;

/// <summary>
/// A lightweight card that follows the pointer during WPF drag/drop.
/// It is visual-only and never participates in hit testing.
/// </summary>
internal sealed class EquipmentDragPreview : IDisposable
{
    private readonly Popup _popup;
    private readonly double _dpiScaleX;
    private readonly double _dpiScaleY;
    private readonly double _previewWidth;
    private readonly double _previewHeight;

    internal EquipmentDragPreview(Visual dpiSource, EquipmentItem equipment)
    {
        ArgumentNullException.ThrowIfNull(dpiSource);
        ArgumentNullException.ThrowIfNull(equipment);

        var dpi = VisualTreeHelper.GetDpi(dpiSource);
        _dpiScaleX = dpi.DpiScaleX;
        _dpiScaleY = dpi.DpiScaleY;

        var glyph = new TextBlock
        {
            Text = equipment.DeviceGlyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 23,
            Foreground = System.Windows.Media.Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        var glyphBadge = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(12),
            Background = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(
                equipment.DeviceBadgeColor)!,
            Child = glyph
        };

        var text = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        text.Children.Add(new TextBlock
        {
            Text = equipment.DeviceType.ToUpperInvariant(),
            Foreground = FindBrush("AccentHoverBrush", "#6EA8FF"),
            FontSize = 10,
            FontWeight = FontWeights.Bold
        });
        text.Children.Add(new TextBlock
        {
            Text = equipment.Name,
            Foreground = FindBrush("PrimaryTextBrush", "#FFFFFF"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = $"From {equipment.AssignmentLabel}",
            Foreground = FindBrush("SecondaryTextBrush", "#A8B3C2"),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0)
        });

        var layout = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal
        };
        layout.Children.Add(glyphBadge);
        layout.Children.Add(text);

        var card = new Border
        {
            Background = FindBrush("PanelBackgroundBrush", "#172231"),
            BorderBrush = FindBrush("AccentBrush", "#3F87F5"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13),
            Opacity = 0.96,
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 7,
                Opacity = 0.42,
                Color = Colors.Black
            },
            Child = layout,
            IsHitTestVisible = false
        };
        card.Measure(new System.Windows.Size(
            double.PositiveInfinity,
            double.PositiveInfinity));
        _previewWidth = card.DesiredSize.Width;
        _previewHeight = card.DesiredSize.Height;

        _popup = new Popup
        {
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Placement = PlacementMode.AbsolutePoint,
            StaysOpen = true,
            Child = card
        };
    }

    internal void Show()
    {
        UpdatePosition();
        _popup.IsOpen = true;
    }

    internal void UpdatePosition()
    {
        if (!GetCursorPos(out var point))
        {
            return;
        }

        _popup.HorizontalOffset = (point.X / _dpiScaleX) - (_previewWidth / 2);
        _popup.VerticalOffset = (point.Y / _dpiScaleY) - (_previewHeight / 2);
    }

    public void Dispose()
    {
        _popup.IsOpen = false;
        _popup.Child = null;
    }

    private static System.Windows.Media.Brush FindBrush(string resourceKey, string fallback) =>
        System.Windows.Application.Current.TryFindResource(resourceKey) as System.Windows.Media.Brush
        ?? (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(fallback)!;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }
}
