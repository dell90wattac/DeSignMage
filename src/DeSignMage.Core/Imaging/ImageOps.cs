using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Imaging;

/// <summary>A pixel operation of the Image menu. The pixels themselves are changed by an <see cref="IImageService"/>.</summary>
public abstract record ImageOp
{
    /// <summary>Rotate the pixels by 90 degrees (lossless).</summary>
    public sealed record Rotate90(bool Clockwise) : ImageOp;

    /// <summary>Mirror the pixels left-right (<paramref name="Horizontal"/>) or top-bottom.</summary>
    public sealed record Flip(bool Horizontal) : ImageOp;

    /// <summary>Photographic negative: R, G and B are inverted, alpha is kept.</summary>
    public sealed record Invert : ImageOp;

    /// <summary>
    /// Rebuild the picture with exactly <paramref name="Width"/> x <paramref name="Height"/> pixels (a smooth, high-quality filter, up or
    /// down). The picture's frame on the page does not change: only how many pixels fill it.
    /// </summary>
    public sealed record Resample(int Width, int Height) : ImageOp;

    /// <summary>Keep only the pixel rectangle (X, Y, W, H), measured from the top-left corner.</summary>
    public sealed record Crop(int X, int Y, int Width, int Height) : ImageOp;
}

/// <summary>An encoded picture stored in the document's <see cref="AssetStore"/>, with its pixel size.</summary>
public sealed record ImagePixels(string AssetId, int PixelWidth, int PixelHeight);

/// <summary>Where and how big a rasterized selection is: the picture and the document-space rectangle it covers.</summary>
public sealed record RasterizeResult(ImagePixels Pixels, Rect2 Bounds);

/// <summary>
/// Does the pixel work for image commands. Core has no imaging library, so the app plugs one in
/// (<c>DeSignMage.Imaging</c>, SkiaSharp). Results are stored as assets, so commands only swap asset ids.
/// </summary>
public interface IImageService
{
    /// <summary>Pixel size of a stored picture, or null if it cannot be decoded.</summary>
    (int Width, int Height)? GetPixelSize(Document doc, string assetId);

    /// <summary>Applies <paramref name="op"/> and stores the result as a new asset. Null when the picture cannot be decoded.</summary>
    ImagePixels? Apply(Document doc, string assetId, ImageOp op);

    /// <summary>
    /// Draws <paramref name="objects"/> into a transparent picture of the given pixel size that covers
    /// <paramref name="bounds"/> (document space), and stores it as an asset.
    /// </summary>
    ImagePixels? Rasterize(Document doc, IReadOnlyList<DocObject> objects, Rect2 bounds, int pixelWidth, int pixelHeight);
}

/// <summary>A picture frame (size in points and the local-to-parent transform) after an operation.</summary>
public readonly record struct ImageFrame(double Width, double Height, Affine2 Transform);

/// <summary>Geometry of the Image menu operations: how the frame changes so the picture stays where it is.</summary>
public static class ImageFrames
{
    /// <summary>
    /// After a 90-degree pixel rotation the frame's width and height swap. The picture keeps its center and its own axes,
    /// and the visual size is swapped too (also for stretched pictures), so it looks rotated about its center.
    /// </summary>
    public static ImageFrame Rotate90(double width, double height, Affine2 t)
    {
        double sx = t.TransformVector(new Vec2(1, 0)).Length, sy = t.TransformVector(new Vec2(0, 1)).Length;
        double a = sx > 1e-12 ? sy / sx : 1, b = sy > 1e-12 ? sx / sy : 1;   // new x scale = old y scale, and vice versa
        var linear = new Affine2(t.M11 * a, t.M12 * a, t.M21 * b, t.M22 * b, 0, 0);
        var center = t.Transform(new Vec2(width / 2, height / 2));
        var nt = Affine2.Translation(-height / 2, -width / 2) * linear * Affine2.Translation(center);
        return new ImageFrame(height, width, nt);
    }

    /// <summary>The frame of the kept pixel rectangle; the kept pixels stay exactly where they were on the page.</summary>
    public static ImageFrame Crop(double width, double height, int pixelWidth, int pixelHeight, Affine2 t, int x, int y, int w, int h)
    {
        double ppx = width / pixelWidth, ppy = height / pixelHeight;   // points per pixel
        return new ImageFrame(w * ppx, h * ppy, Affine2.Translation(x * ppx, y * ppy) * t);
    }

    /// <summary>
    /// The pixel rectangle for a box given in frame points: edges snap to the nearest pixel edge and at least one pixel is kept.
    /// </summary>
    public static (int X, int Y, int Width, int Height) PixelRect(Rect2 box, double width, double height, int pixelWidth, int pixelHeight)
    {
        double kx = pixelWidth / width, ky = pixelHeight / height;
        int x0 = Math.Clamp((int)Math.Round(box.Left * kx), 0, pixelWidth - 1);
        int y0 = Math.Clamp((int)Math.Round(box.Top * ky), 0, pixelHeight - 1);
        int x1 = Math.Clamp((int)Math.Round(box.Right * kx), x0 + 1, pixelWidth);
        int y1 = Math.Clamp((int)Math.Round(box.Bottom * ky), y0 + 1, pixelHeight);
        return (x0, y0, x1 - x0, y1 - y0);
    }
}

/// <summary>Natural size and placement of imported pictures, and the extent of a rasterized selection.</summary>
public static class ImagePlacement
{
    /// <summary>The resolution assumed when a file carries none.</summary>
    public const double DefaultDpi = 300;

    /// <summary>
    /// Frame size in points for a picture of the given pixel size at <paramref name="dpi"/> (default 300 if not usable),
    /// shrunk (keeping the aspect ratio) so it fits within the page.
    /// </summary>
    public static (double Width, double Height) NaturalSize(int pixelWidth, int pixelHeight, double dpi, PageSetup page)
    {
        if (!(dpi >= 1 && dpi <= 100_000)) dpi = DefaultDpi;
        double w = pixelWidth * Units.PointsPerInch / dpi, h = pixelHeight * Units.PointsPerInch / dpi;
        double fit = Math.Min(1, Math.Min(page.Width / w, page.Height / h));
        return (w * fit, h * fit);
    }

    /// <summary>A new image object centered on <paramref name="center"/> at its natural size.</summary>
    public static ImageObject Create(ImagePixels pixels, string? fileName, double dpi, PageSetup page, Vec2 center)
    {
        var (w, h) = NaturalSize(pixels.PixelWidth, pixels.PixelHeight, dpi, page);
        return new ImageObject(pixels.AssetId, w, h, pixels.PixelWidth, pixels.PixelHeight, fileName)
        {
            Name = fileName,
            Transform = Affine2.Translation(center.X - w / 2, center.Y - h / 2),
        };
    }

    /// <summary>The resolution assumed for a picture pasted from the clipboard that carries none: a screen's, so a screenshot keeps the size it had on screen.</summary>
    public const double PasteDpi = 96;

    /// <summary>
    /// A picture pasted from the clipboard: its size from <paramref name="dpi"/> (<see cref="PasteDpi"/> when it has none), shrunk
    /// (aspect kept) to fit the page and to fill at most <paramref name="viewFill"/> of the visible area <paramref name="view"/>,
    /// centered in that area. Pictures without a file name are called "Pasted Picture".
    /// </summary>
    public static ImageObject CreatePasted(ImagePixels pixels, double? dpi, PageSetup page, Rect2 view, double viewFill = 0.9)
    {
        var (w, h) = NaturalSize(pixels.PixelWidth, pixels.PixelHeight, dpi ?? PasteDpi, page);
        if (!view.IsEmpty && view.Width > 0 && view.Height > 0)
        {
            double fit = Math.Min(1, Math.Min(view.Width * viewFill / w, view.Height * viewFill / h));
            w *= fit; h *= fit;
        }
        var center = view.IsEmpty ? page.Bounds.Center : view.Center;
        return new ImageObject(pixels.AssetId, w, h, pixels.PixelWidth, pixels.PixelHeight)
        {
            Name = "Pasted Picture",
            Transform = Affine2.Translation(center.X - w / 2, center.Y - h / 2),
        };
    }

    /// <summary>
    /// The page area covered by <paramref name="objects"/> including their stroke widths; this is what Rasterize turns into a picture.
    /// </summary>
    public static Rect2 VisualBounds(IEnumerable<DocObject> objects)
    {
        var r = Rect2.Empty;
        foreach (var o in objects) r = r.Union(Visual(o));
        return r;

        static Rect2 Visual(DocObject o)
        {
            if (o is GroupObject g)
            {
                var u = Rect2.Empty;
                foreach (var c in g.Children) u = u.Union(Visual(c));
                return u;
            }
            double outset = o is CombinedObject c2 ? c2.Parts.Max(p => p.Style.StrokeOutset) : o.Style.StrokeOutset;
            return o.GetWorldBounds().Inflate(outset);
        }
    }

    /// <summary>Pixel size of a picture covering <paramref name="bounds"/> at <paramref name="dpi"/>; null if it would exceed <paramref name="maxPixels"/>.</summary>
    public static (int Width, int Height)? RasterSize(Rect2 bounds, double dpi, long maxPixels = MaxRasterPixels)
    {
        if (bounds.IsEmpty || dpi <= 0) return null;
        double w = Math.Max(1, Math.Round(bounds.Width / Units.PointsPerInch * dpi));
        double h = Math.Max(1, Math.Round(bounds.Height / Units.PointsPerInch * dpi));
        if (w * h > maxPixels || w > 32_000 || h > 32_000) return null;
        return ((int)w, (int)h);
    }

    /// <summary>100 million pixels: 400 MB as RGBA. More would risk running out of memory.</summary>
    public const long MaxRasterPixels = 100_000_000;
}

/// <summary>The memory figure of the info line ("Bitmaps: 0.00 MB total"), and which stored pictures are in use.</summary>
public static class BitmapStats
{
    /// <summary>
    /// The assets some picture object points at. The asset store also keeps pictures that were replaced by an edit
    /// (undo needs them), so a file writer should save only these.
    /// </summary>
    public static HashSet<string> ReferencedAssetIds(Document doc)
    {
        var ids = new HashSet<string>();
        void Visit(DocObject o)
        {
            if (o is ImageObject img) ids.Add(img.AssetId);
            else if (o is CombinedObject c) foreach (var p in c.Parts) Visit(p);
        }
        foreach (var o in doc.AllObjects()) Visit(o);
        return ids;
    }

    /// <summary>
    /// Bytes the document's pictures take once decoded (4 bytes per pixel; each stored picture counted once).
    /// Pictures whose pixel size is unknown count by their stored size.
    /// </summary>
    public static long TotalBytes(Document doc)
    {
        var seen = new HashSet<string>();
        long total = 0;
        void Count(DocObject o)
        {
            if (o is CombinedObject c) foreach (var p in c.Parts) Count(p);
            if (o is not ImageObject img || !seen.Add(img.AssetId)) return;
            total += img.HasPixelSize ? (long)img.PixelWidth * img.PixelHeight * 4 : doc.Assets.Get(img.AssetId)?.Data.Length ?? 0;
        }
        foreach (var o in doc.AllObjects()) Count(o);
        return total;
    }
}
