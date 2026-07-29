using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TechBench.Tests;

public sealed class BenchModuleLogoRenderingTests
{
    private const double LogoWidth = 252;
    private const double ViewportWidth = 270;
    private const double ViewportHeight = 92;

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void LogosRenderAtTheSameSizeAndPositionAcrossDpiScales(
        double dpiScale)
    {
        var bounds = RunOnSta(() =>
            new[]
            {
                RenderLogo("csri-techbench-logo.png", dpiScale),
                RenderLogo("csri-salesbench-logo.png", dpiScale),
                RenderLogo("csri-adminbench-logo.png", dpiScale)
            });

        var widthRange = bounds.Max(item => item.Width)
            - bounds.Min(item => item.Width);
        var heightRange = bounds.Max(item => item.Height)
            - bounds.Min(item => item.Height);
        var horizontalCenterRange = bounds.Max(item => item.HorizontalCenterTwice)
            - bounds.Min(item => item.HorizontalCenterTwice);
        var verticalCenterRange = bounds.Max(item => item.VerticalCenterTwice)
            - bounds.Min(item => item.VerticalCenterTwice);

        Assert.True(
            widthRange == 0,
            $"Rendered logo widths differ at {dpiScale:P0}: {Describe(bounds)}");
        Assert.True(
            heightRange == 0,
            $"Rendered logo heights differ at {dpiScale:P0}: {Describe(bounds)}");
        Assert.True(
            horizontalCenterRange == 0,
            $"Rendered horizontal centers differ at {dpiScale:P0}: {Describe(bounds)}");
        Assert.True(
            verticalCenterRange == 0,
            $"Rendered vertical centers differ at {dpiScale:P0}: {Describe(bounds)}");
    }

    private static PixelBounds RenderLogo(string fileName, double dpiScale)
    {
        var sourcePath = FindRepositoryFile("Assets", fileName);
        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.UriSource = new Uri(sourcePath, UriKind.Absolute);
        source.EndInit();
        source.Freeze();

        var image = new Image
        {
            Source = source,
            Width = LogoWidth,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(
            image,
            BitmapScalingMode.HighQuality);

        var viewport = new Grid
        {
            Width = ViewportWidth,
            Height = ViewportHeight,
            Background = Brushes.Black,
            ClipToBounds = true
        };
        viewport.Children.Add(image);
        viewport.Measure(new Size(ViewportWidth, ViewportHeight));
        viewport.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));
        viewport.UpdateLayout();

        var pixelWidth = (int)Math.Round(
            ViewportWidth * dpiScale,
            MidpointRounding.AwayFromZero);
        var pixelHeight = (int)Math.Round(
            ViewportHeight * dpiScale,
            MidpointRounding.AwayFromZero);
        var rendered = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96 * dpiScale,
            96 * dpiScale,
            PixelFormats.Pbgra32);
        rendered.Render(viewport);

        var stride = pixelWidth * 4;
        var pixels = new byte[stride * pixelHeight];
        rendered.CopyPixels(pixels, stride, 0);
        return FindVisibleBounds(pixels, pixelWidth, pixelHeight, stride);
    }

    private static PixelBounds FindVisibleBounds(
        byte[] pixels,
        int width,
        int height,
        int stride)
    {
        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * stride) + (x * 4);
                var brightness = Math.Max(
                    pixels[offset],
                    Math.Max(pixels[offset + 1], pixels[offset + 2]));
                if (brightness < 60)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        Assert.True(maxX >= minX && maxY >= minY, "No logo pixels were rendered.");
        return new PixelBounds(minX, minY, maxX, maxY);
    }

    private static T RunOnSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
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
            throw failure;
        }

        return result!;
    }

    private static string Describe(IEnumerable<PixelBounds> bounds) =>
        string.Join(", ", bounds);

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "TechBenchV2.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(
            new[] { directory.FullName }.Concat(relativeSegments).ToArray());
    }

    private sealed record PixelBounds(
        int MinX,
        int MinY,
        int MaxX,
        int MaxY)
    {
        public int Width => MaxX - MinX + 1;
        public int Height => MaxY - MinY + 1;
        public int HorizontalCenterTwice => MinX + MaxX;
        public int VerticalCenterTwice => MinY + MaxY;
    }
}
