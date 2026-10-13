using System.Globalization;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Imaging;

/// <summary>Sizes of a placed picture as it prints: pixels, inches on the page, dpi.</summary>
public static class ImageSizing
{
    /// <summary>The picture's width and height on the page in inches (its frame with its object's scale and turn applied).</summary>
    public static (double Width, double Height) PrintedInches(ImageObject image)
    {
        var world = image.GetWorldTransform();
        return (world.TransformVector(new Geometry.Vec2(image.Width, 0)).Length / Units.PointsPerInch,
                world.TransformVector(new Geometry.Vec2(0, image.Height)).Length / Units.PointsPerInch);
    }

    /// <summary>Plain info for the Info Line, e.g. "Bitmap: 600 x 300 px, 300 dpi"; null while the pixel size is not known.</summary>
    public static string? InfoText(ImageObject image)
    {
        if (!image.HasPixelSize) return null;
        double dx = image.EffectiveDpiX, dy = image.EffectiveDpiY;
        string dpi = Math.Abs(dx - dy) < 0.5 ? Dpi(dx) + " dpi" : $"{Dpi(dx)} x {Dpi(dy)} dpi";
        return string.Create(CultureInfo.InvariantCulture, $"Bitmap: {image.PixelWidth} x {image.PixelHeight} px, {dpi}");
    }

    /// <summary>Dpi as people say it: whole numbers when it is (nearly) one, else one decimal.</summary>
    public static string Dpi(double dpi) =>
        Math.Abs(dpi - Math.Round(dpi)) < 0.05 ? Math.Round(dpi).ToString(CultureInfo.InvariantCulture) : dpi.ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>
/// The numbers behind Image ▸ Resample: a picture of a given pixel size that prints at a fixed size on the page. Setting the pixel width,
/// the pixel height or the dpi changes the others; the printed size never changes. With <see cref="LockAspect"/> the width and the height
/// keep the picture's current pixel proportions; a dpi applies to both sides, so it also evens out non-square pixels.
/// There is no "too low resolution" judgment anywhere: the dpi is shown, nothing more.
/// </summary>
public sealed class ResampleSizing
{
    /// <summary>The largest side, and the most pixels, a resampled picture may have (the same limit as rasterizing).</summary>
    public const int MaxSide = 32_000;
    public const long MaxPixels = ImagePlacement.MaxRasterPixels;

    public ResampleSizing(int pixelWidth, int pixelHeight, double printedWidthInches, double printedHeightInches)
    {
        if (pixelWidth < 1 || pixelHeight < 1) throw new ArgumentOutOfRangeException(nameof(pixelWidth), "A picture needs at least one pixel each way.");
        if (!(printedWidthInches > 0 && printedHeightInches > 0)) throw new ArgumentOutOfRangeException(nameof(printedWidthInches), "The printed size must be positive.");
        SourceWidth = Width = pixelWidth;
        SourceHeight = Height = pixelHeight;
        PrintedWidthInches = printedWidthInches;
        PrintedHeightInches = printedHeightInches;
    }

    public static ResampleSizing For(ImageObject image)
    {
        var (w, h) = ImageSizing.PrintedInches(image);
        return new ResampleSizing(image.PixelWidth, image.PixelHeight, w, h);
    }

    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public double PrintedWidthInches { get; }
    public double PrintedHeightInches { get; }

    /// <summary>The new pixel size being set up.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Keep the width and height in the proportions of the current pixels (on by default).</summary>
    public bool LockAspect { get; set; } = true;

    public double CurrentDpiX => SourceWidth / PrintedWidthInches;
    public double CurrentDpiY => SourceHeight / PrintedHeightInches;

    /// <summary>The dpi the new pixel size gives across and down the printed size.</summary>
    public double DpiX => Width / PrintedWidthInches;
    public double DpiY => Height / PrintedHeightInches;

    public bool IsChanged => Width != SourceWidth || Height != SourceHeight;
    public long Pixels => (long)Width * Height;

    /// <summary>Memory the new picture takes once decoded (4 bytes per pixel), in megabytes.</summary>
    public double Megabytes => Pixels * 4.0 / 1048576.0;

    /// <summary>Why the size cannot be used (too large), or null.</summary>
    public string? Problem =>
        Width > MaxSide || Height > MaxSide || Pixels > MaxPixels ? "That would make the picture too large." : null;

    public bool CanApply => IsChanged && Problem is null;

    public void SetWidth(int width)
    {
        Width = Math.Max(1, width);
        if (LockAspect) Height = ToInt((double)Width * SourceHeight / SourceWidth);
    }

    public void SetHeight(int height)
    {
        Height = Math.Max(1, height);
        if (LockAspect) Width = ToInt((double)Height * SourceWidth / SourceHeight);
    }

    /// <summary>Sets the dpi: both sides get that many pixels per printed inch.</summary>
    public void SetDpi(double dpi)
    {
        if (!(dpi > 0) || double.IsInfinity(dpi)) return;
        Width = ToInt(dpi * PrintedWidthInches);
        Height = ToInt(dpi * PrintedHeightInches);
    }

    private static int ToInt(double v) => (int)Math.Clamp(Math.Round(v), 1, int.MaxValue);

    /// <summary>Back to the current pixel size.</summary>
    public void Reset()
    {
        Width = SourceWidth;
        Height = SourceHeight;
    }
}
