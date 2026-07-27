using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace TechBench.Controls;

/// <summary>
/// A snapshot of a technician header that follows the pointer during lane reordering.
/// </summary>
internal sealed class EquipmentLaneDragPreview : IDisposable
{
    private readonly Popup _popup;
    private readonly double _dpiScaleX;
    private readonly double _dpiScaleY;
    private readonly double _previewWidth;
    private readonly double _previewHeight;

    internal EquipmentLaneDragPreview(FrameworkElement source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var dpi = VisualTreeHelper.GetDpi(source);
        _dpiScaleX = dpi.DpiScaleX;
        _dpiScaleY = dpi.DpiScaleY;
        _previewWidth = Math.Max(source.ActualWidth, 1);
        _previewHeight = Math.Max(source.ActualHeight, 1);

        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(_previewWidth * _dpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(_previewHeight * _dpiScaleY)),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(source);
        bitmap.Freeze();

        var image = new System.Windows.Controls.Image
        {
            Source = bitmap,
            Width = _previewWidth,
            Height = _previewHeight,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };
        var preview = new Border
        {
            Background = FindBrush("PanelBackgroundBrush", "#172231"),
            BorderBrush = FindBrush("AccentBrush", "#3F87F5"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            Opacity = 0.96,
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 7,
                Opacity = 0.42,
                Color = Colors.Black
            },
            Child = image,
            IsHitTestVisible = false
        };
        preview.Measure(new System.Windows.Size(
            double.PositiveInfinity,
            double.PositiveInfinity));
        _previewWidth = preview.DesiredSize.Width;
        _previewHeight = preview.DesiredSize.Height;

        _popup = new Popup
        {
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Placement = PlacementMode.AbsolutePoint,
            StaysOpen = true,
            Child = preview
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

        _popup.HorizontalOffset =
            (point.X / _dpiScaleX) - (_previewWidth / 2);
        _popup.VerticalOffset =
            (point.Y / _dpiScaleY) - (_previewHeight / 2);
    }

    public void Dispose()
    {
        _popup.IsOpen = false;
        _popup.Child = null;
    }

    private static System.Windows.Media.Brush FindBrush(
        string resourceKey,
        string fallback) =>
        System.Windows.Application.Current.TryFindResource(resourceKey)
            as System.Windows.Media.Brush
        ?? (System.Windows.Media.Brush)new BrushConverter()
            .ConvertFromString(fallback)!;

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
