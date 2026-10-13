using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using DeSignMage.Feedback;
using SkiaSharp;

namespace DeSignMage.App.Reporting;

/// <summary>The picture of the DeSign window for a bug report, and shrinking it to fit the size budget.</summary>
internal static class ScreenshotTools
{
    /// <summary>Renders <paramref name="window"/> (the window itself, not the screen) to a PNG. Null if it has no size (minimized) or rendering fails.</summary>
    public static byte[]? CapturePng(Window window)
    {
        try
        {
            double scale = window.RenderScaling;
            var size = new PixelSize((int)(window.Bounds.Width * scale), (int)(window.Bounds.Height * scale));
            if (size.Width < 16 || size.Height < 16) return null;
            using var rtb = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            rtb.Render(window);
            using var ms = new MemoryStream();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
            rtb.Save(ms);
#pragma warning restore CS0618
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Error("screenshot", "Could not capture the window", ex);
            return null;
        }
    }

    /// <summary>
    /// Re-encodes a PNG as a smaller JPEG (full size first, then scaled down) until it is at most <paramref name="maxBytes"/>.
    /// Null if even the smallest attempt is too big.
    /// </summary>
    public static ShrunkImage? Shrink(byte[] png, long maxBytes)
    {
        using var source = SKBitmap.Decode(png);
        if (source is null) return null;
        foreach (var (scale, quality) in new[] { (1.0, 85), (0.75, 80), (0.5, 75), (0.35, 70), (0.25, 60) })
        {
            using var bitmap = scale >= 1 ? null : source.Resize(new SKImageInfo(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale))), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            using var image = SKImage.FromBitmap(bitmap ?? source);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            if (data.Size <= maxBytes) return new ShrunkImage(data.ToArray(), ".jpg", "image/jpeg");
        }
        return null;
    }

    /// <summary>One line per monitor: size in pixels, scaling, primary.</summary>
    public static IEnumerable<string> DescribeDisplays(Window window)
    {
        var screens = window.Screens;
        if (screens is null) yield break;
        foreach (var s in screens.All)
            yield return $"{s.Bounds.Width}x{s.Bounds.Height} px, scaling {s.Scaling * 100:0}%{(s.IsPrimary ? " (primary)" : "")}";
        yield return $"This window: {window.Bounds.Width:0}x{window.Bounds.Height:0} (scaling {window.RenderScaling * 100:0}%)";
    }
}
