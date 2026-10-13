using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using SkiaSharp;

namespace DeSignMage.Rendering;

/// <summary>
/// The soft-paint renderer (R8): draws what a <see cref="BlurStyle"/> (Soft Shadow, Glow) or a <see cref="FeatherStyle"/> asks for, from the
/// object's own vector shape. Nothing is stored in the document; the soft part is a mask, made when it is drawn and kept per object.
/// <para>
/// A mask is the object's silhouette (its outline transformed by the object's world transform, <b>without</b> the translation), optionally
/// eroded by a centered stroke (feather inset), blurred with a Gaussian, as an 8-bit alpha bitmap. Because a blurred shape is smooth,
/// the bitmap is made at the lower of the screen's scale and the scale where the blur is 3 pixels wide; whatever is drawn at a higher
/// resolution is the bitmap stretched smoothly, which is exactly as good as the blur itself. So the cost of a mask follows the size of
/// the <i>blur</i>, not the size of the object or the zoom: a huge soft shadow is a small bitmap.
/// </para>
/// <para>
/// Masks are kept per object, keyed on (version, dependency stamp, linear part of the world transform, blur, erode, raster scale), so
/// moving an object, or its group, reuses its mask; zooming to another half octave, editing the object or changing the parameters makes a
/// new one. They are the "render-time layer cache" of <c>raster-effects-pipeline.md</c> section 8, per object and zoom bucket.
/// </para>
/// Used from one thread (the one that owns the <see cref="SceneRenderer"/>).
/// </summary>
internal sealed class SoftLayers : IDisposable
{
    /// <summary>The blur is made this many pixels wide (sigma) at most: more would be a bigger bitmap for nothing.</summary>
    public const double MaxSigmaPixels = 3;

    /// <summary>Longest side of a mask bitmap in pixels (the legacy program caps its effect bitmaps at 4,000).</summary>
    public const int MaxEdge = 4000;

    /// <summary>Pixels per point when drawing for export and the blur is smaller than <see cref="MaxSigmaPixels"/> pixels at that scale (about 576 dpi).</summary>
    public const double ExportScale = 8;

    private sealed class Entry : IDisposable
    {
        public required MaskKey Key;
        public required SKImage Image;
        public required SKRect Relative;   // where the bitmap lies, relative to the object's world translation, in points
        public int LastUsed;
        public void Dispose() => Image.Dispose();
    }

    private readonly record struct MaskKey(int Version, long Stamp, float M11, float M12, float M21, float M22, float Sigma, float Erode, int ScaleKey);

    private readonly Dictionary<DocObject, Entry> _masks = [];

    /// <summary>How many masks were made (tests, profiling).</summary>
    public int MasksMade { get; private set; }

    public int Cached => _masks.Count;

    /// <summary>
    /// Pixels per point of the mask for a blur of <paramref name="sigmaPoints"/> when the page is shown at <paramref name="viewScale"/> screen
    /// pixels per point (0 = export). The scale is quantized to half octaves, like the renderer's picture buckets, so a wheel zoom does not make a mask per step.
    /// </summary>
    public static (double PixelsPerPoint, int Key) RasterScale(double sigmaPoints, double viewScale)
    {
        double device = viewScale > 0 ? Math.Pow(2, (Math.Floor(Math.Log2(viewScale) * 2) + 1) / 2) : ExportScale;
        double k = sigmaPoints > 1e-9 ? Math.Min(device, MaxSigmaPixels / sigmaPoints) : device;
        k = Math.Max(k, 1e-3);
        // Quantize (a quarter octave) so that two almost equal blurs share a scale; the key is the quantized exponent.
        int key = (int)Math.Round(Math.Log2(k) * 4);
        return (Math.Pow(2, key / 4.0), key);
    }

    private Entry? GetMask(DocObject o, Affine2 world, double sigmaPoints, double erodePoints, double viewScale, int generation)
    {
        var (k, scaleKey) = RasterScale(sigmaPoints, viewScale);
        var key = new MaskKey(o.Version, o.DependencyStamp, (float)world.M11, (float)world.M12, (float)world.M21, (float)world.M22,
            (float)sigmaPoints, (float)erodePoints, scaleKey);
        if (_masks.TryGetValue(o, out var hit) && hit.Key == key)
        {
            hit.LastUsed = generation;
            return hit;
        }

        var made = Build(o, world, sigmaPoints, erodePoints, k, key);
        if (made is null) return null;
        made.LastUsed = generation;
        if (_masks.Remove(o, out var old)) old.Dispose();
        _masks[o] = made;
        return made;
    }

    private Entry? Build(DocObject o, Affine2 world, double sigma, double erode, double k, MaskKey key)
    {
        if (o.GetLocalGeometry() is not { IsEmpty: false } local) return null;
        var linear = new Affine2(world.M11, world.M12, world.M21, world.M22, 0, 0);
        var shape = local.Transform(linear);
        var b = shape.GetBounds();
        if (b.IsEmpty) return null;

        double margin = 3 * sigma + 1;
        var rect = new Rect2(b.Left - margin, b.Top - margin, b.Right + margin, b.Bottom + margin);
        k = Math.Min(k, MaxEdge / Math.Max(Math.Max(rect.Width, rect.Height), 1e-9));
        int w = Math.Max(1, (int)Math.Ceiling(rect.Width * k)), h = Math.Max(1, (int)Math.Ceiling(rect.Height * k));
        // the bitmap covers whole pixels: let the rectangle it is drawn into be exactly that big, or the mask would be stretched a little
        rect = new Rect2(rect.Left, rect.Top, rect.Left + w / k, rect.Top + h / k);

        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul));
        if (surface is null) return null;
        var canvas = surface.Canvas;
        canvas.Scale((float)k);
        canvas.Translate((float)-rect.Left, (float)-rect.Top);

        using var path = shape.ToSKPath();
        // an image filter's sigma is in the canvas's local units (here points: the canvas is scaled by k), not in pixels
        float sigmaLocal = (float)sigma;
        using var blur = sigma * k > 0.05 ? SKImageFilter.CreateBlur(sigmaLocal, sigmaLocal, SKShaderTileMode.Decal) : null;
        using var layerPaint = blur is null ? null : new SKPaint { ImageFilter = blur };
        if (layerPaint is not null) canvas.SaveLayer(layerPaint);
        using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.White })
            canvas.DrawPath(path, fill);
        if (erode > 1e-9)
        {
            // The inset of a feather: a stroke centered on the outline, erased (the legacy program does the same with a stroke of the background color).
            using var erase = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)erode, BlendMode = SKBlendMode.Clear, StrokeJoin = SKStrokeJoin.Round };
            canvas.DrawPath(path, erase);
        }
        if (layerPaint is not null) canvas.Restore();

        MasksMade++;
        return new Entry
        {
            Key = key,
            Image = surface.Snapshot(),
            Relative = SKRect.Create((float)rect.Left, (float)rect.Top, (float)rect.Width, (float)rect.Height),
        };
    }

    // ---- Soft Shadow and Glow ----

    /// <summary>Draws the object's shape blurred, in <paramref name="color"/> times the opacity. False when there is nothing to draw.</summary>
    public bool DrawBlurred(SKCanvas canvas, DocObject o, Affine2 world, BlurStyle blur, SKColor color, double viewScale, int generation, out bool usedMask)
    {
        usedMask = false;
        double scale = Math.Sqrt(Math.Abs(world.Determinant));
        double sigma = blur.Sigma * scale;
        byte alpha = (byte)Math.Round(Math.Clamp(blur.Opacity, 0, 1) * color.Alpha);
        if (alpha == 0) return false;
        using var paint = new SKPaint { Color = color.WithAlpha(alpha), IsAntialias = true };

        if (o.GetLocalGeometry() is not { IsEmpty: false } geometry) return false;
        if (sigma < 0.02)
        {
            // No softness: the plain shape at the opacity.
            using var plain = geometry.Transform(world).ToSKPath();
            canvas.DrawPath(plain, paint);
            return true;
        }

        var entry = GetMask(o, world, sigma, 0, viewScale, generation);
        if (entry is null) return false;
        usedMask = true;
        canvas.DrawImage(entry.Image, Dest(entry, world), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
        return true;
    }

    private static SKRect Dest(Entry e, Affine2 world) =>
        SKRect.Create(e.Relative.Left + (float)world.OffsetX, e.Relative.Top + (float)world.OffsetY, e.Relative.Width, e.Relative.Height);

    // ---- Feather ----

    /// <summary>The numbers a feather works with, from the object's bounds (<paramref name="bounds"/> in document points).</summary>
    public static (double Sigma, double Erode) FeatherSizes(DocObject o, Rect2 bounds, FeatherStyle f)
    {
        double basis = o is TextObject ? bounds.Height : Math.Min(bounds.Width, bounds.Height);
        double px = Math.Max(basis, 0) * Math.Max(f.Amount, 0);
        return (FeatherStyle.SigmaPerPixel * px, px * Math.Clamp(f.Inset, 0, 2) * FeatherStyle.ErodeWidthPerPixel);
    }

    /// <summary>
    /// Draws the object with <paramref name="drawInner"/> (its normal paint, into whatever canvas it is given) and fades its edge: the object goes into a layer and
    /// the mask is multiplied in. With <paramref name="pixelDpi"/> above 0 the layer is a bitmap at that resolution that is drawn as one picture instead (what a PDF
    /// can hold: it has no way to say "multiply by this mask").
    /// </summary>
    public bool DrawFeathered(SKCanvas canvas, DocObject o, Affine2 world, FeatherStyle feather, double viewScale, int generation, double pixelDpi, Action<SKCanvas> drawInner)
    {
        if (o.GetLocalGeometry() is not { IsEmpty: false } local) return false;
        var bounds = local.Transform(world).GetBounds();
        if (bounds.IsEmpty) return false;
        var (sigma, erode) = FeatherSizes(o, bounds, feather);
        if (sigma < 1e-6 && erode < 1e-6) return false;

        var entry = GetMask(o, world, sigma, erode, viewScale, generation);
        if (entry is null) return false;
        var dest = Dest(entry, world);
        using var multiply = new SKPaint { BlendMode = SKBlendMode.DstIn, Color = SKColors.White };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        if (pixelDpi <= 0)
        {
            canvas.SaveLayer(dest, null);
            drawInner(canvas);
            canvas.DrawImage(entry.Image, dest, sampling, multiply);
            canvas.Restore();
            return true;
        }

        double k = Math.Min(pixelDpi / 72.0, MaxEdge / Math.Max(Math.Max(dest.Width, dest.Height), 1e-9));
        int w = Math.Max(1, (int)Math.Ceiling(dest.Width * k)), h = Math.Max(1, (int)Math.Ceiling(dest.Height * k));
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null) return false;
        var raster = surface.Canvas;
        raster.Scale((float)k);
        raster.Translate(-dest.Left, -dest.Top);
        drawInner(raster);
        raster.DrawImage(entry.Image, dest, sampling, multiply);
        using var picture = surface.Snapshot();
        canvas.DrawImage(picture, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        return true;
    }

    // ---- Housekeeping ----

    /// <summary>Releases masks that were not used for the last two records (a dragged object's mask survives the drag).</summary>
    public void Sweep(int generation)
    {
        List<DocObject>? stale = null;
        foreach (var (o, e) in _masks)
            if (e.LastUsed < generation - 1) (stale ??= []).Add(o);
        if (stale is null) return;
        foreach (var o in stale)
        {
            _masks[o].Dispose();
            _masks.Remove(o);
        }
    }

    public void Dispose()
    {
        foreach (var e in _masks.Values) e.Dispose();
        _masks.Clear();
    }
}
