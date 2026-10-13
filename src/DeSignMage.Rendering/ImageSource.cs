using DeSignMage.Core.Model;
using SkiaSharp;

namespace DeSignMage.Rendering;

/// <summary>
/// The pixels of one stored picture, ready to draw. Normal pictures are decoded once; a big photo also gets a small copy
/// (long side <see cref="PreviewSize"/>) that is drawn instead when it is zoomed far out, so panning a 40-megapixel
/// photo costs about as much as a thumbnail. Pictures above <see cref="MaxDecodedPixels"/> stay encoded and are decoded
/// by Skia on demand (its cache drops them under memory pressure) instead of being held decoded.
/// </summary>
internal sealed class ImageSource : IDisposable
{
    public const int PreviewSize = 1024;
    public const int MaxDecodedPixels = 16_000_000;

    public SKImage? Full { get; private set; }
    public SKImage? Small { get; private set; }

    /// <summary>The last record generation that drew this picture (cache sweeping).</summary>
    public int LastUsed;

    public int PixelWidth => Full?.Width ?? 0;
    public int PixelHeight => Full?.Height ?? 0;

    public static ImageSource Load(Asset? asset)
    {
        var s = new ImageSource();
        if (asset is null) return s;
        var lazy = SKImage.FromEncodedData(asset.Data);
        if (lazy is null) return s;

        long pixels = (long)lazy.Width * lazy.Height;
        if (pixels <= MaxDecodedPixels)
        {
            s.Full = lazy.ToRasterImage(true) ?? lazy;
            if (!ReferenceEquals(s.Full, lazy)) lazy.Dispose();
        }
        else s.Full = lazy;

        if (Math.Max(s.Full.Width, s.Full.Height) > 2 * PreviewSize) s.Small = MakePreview(s.Full);
        return s;
    }

    private static SKImage? MakePreview(SKImage full)
    {
        double k = (double)PreviewSize / Math.Max(full.Width, full.Height);
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(full.Width * k)), Math.Max(1, (int)Math.Round(full.Height * k)), SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var pixmap = bitmap.PeekPixels();
        if (pixmap is null || !full.ScalePixels(pixmap, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))) return null;
        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>Picks the image and the sampling for a given zoom; <paramref name="devicePixelsPerImagePixel"/> is 0 when drawing for export.</summary>
    public (SKImage Image, SKSamplingOptions Sampling)? Choose(double devicePixelsPerImagePixel)
    {
        if (Full is null) return null;
        double r = devicePixelsPerImagePixel;
        if (r <= 0) return (Full, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));          // export: best quality
        if (r >= NearestAbove) return (Full, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)); // zoomed in: crisp pixels
        if (r >= 1) return (Full, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        // Zoomed out: the small copy when it still has at least one pixel per screen pixel, else mipmapped full size.
        if (Small is not null && r <= (double)Small.Width / Full.Width)
            return (Small, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return (Full, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    /// <summary>From this many screen pixels per picture pixel, pixels are drawn as crisp squares instead of smoothed.</summary>
    public const double NearestAbove = 4;

    public void Dispose()
    {
        Small?.Dispose();
        Full?.Dispose();
        Small = Full = null;
    }
}
