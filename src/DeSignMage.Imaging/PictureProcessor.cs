using System.Runtime.InteropServices;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using SkiaSharp;

namespace DeSignMage.Imaging;

/// <summary>
/// The pixel work of the Picture Editor: it turns an original picture and a <see cref="PictureRecipe"/> into the finished picture, in the order
/// orientation (flip, quarter turns), rotate, crop, adjust, effects. The same code makes the live preview (from a reduced copy of the original,
/// <c>scale</c> &lt; 1) and the real result at full resolution.
/// </summary>
public static class PictureProcessor
{
    /// <summary>Pictures are previewed from a copy whose long side is at most this many pixels, so the editor stays quick on older computers.</summary>
    public const int PreviewSide = 1800;

    /// <summary>
    /// The intermediate pictures of the last preview, so that changing a late step (a slider of Adjust or Effects) does not redo the early ones
    /// (turning and cropping). Valid for one source picture and scale only; used by one preview at a time.
    /// </summary>
    public sealed class RenderCache
    {
        internal ((bool Mirror, int Quarter, double Angle) Key, PixelBuffer Canvas)? Canvas;
        internal ((bool Mirror, int Quarter, double Angle) Key, RectI? Rect, PixelBuffer Cropped)? Cropped;
        internal (PictureTransparency Transparency, PixelBuffer Input, PixelBuffer Result)? Transparent;
        internal (PictureAdjust Adjust, PixelBuffer Input, PixelBuffer Adjusted)? Adjusted;
        internal ((int NoiseReduction, double Blur, int Sharpen) Key, PixelBuffer Input, PixelBuffer Filtered)? Filtered;
    }

    /// <summary>The picture before the crop (<see cref="Canvas"/>: turned, on its big canvas) and the finished picture after everything.</summary>
    public sealed record Rendered(PixelBuffer Canvas, PixelBuffer Result);

    /// <summary>
    /// The picture after the recipe. <paramref name="source"/> is the original, or a copy of it reduced by <paramref name="scale"/> (the recipe's pixel
    /// numbers are those of the original: the crop is scaled to fit). The result may be <paramref name="source"/> itself when the recipe changes nothing.
    /// </summary>
    public static PixelBuffer Render(PixelBuffer source, PictureRecipe recipe, double scale = 1.0, CancellationToken cancel = default) =>
        RenderParts(source, recipe, scale, cancel).Result;

    /// <summary>Like <see cref="Render"/>, also returning the turned canvas the crop frame sits on, and reusing the steps <paramref name="cache"/> still has.</summary>
    public static Rendered RenderParts(PixelBuffer source, PictureRecipe recipe, double scale = 1.0, CancellationToken cancel = default, RenderCache? cache = null)
    {
        recipe = recipe.Normalized();
        var turnKey = (recipe.Mirror, recipe.Quarter, recipe.Straighten);
        PixelBuffer canvas;
        if (cache?.Canvas is { } cc && cc.Key == turnKey) canvas = cc.Canvas;
        else
        {
            canvas = Straighten(Orient(source, recipe.Mirror, recipe.Quarter), recipe.Straighten);
            if (cache is not null) cache.Canvas = (turnKey, canvas);
        }
        cancel.ThrowIfCancellationRequested();

        PixelBuffer cropped = canvas;
        RectI? rect = null;
        if (recipe.Crop is not null)
        {
            var (fullW, fullH) = recipe.CanvasSize;
            var c = recipe.EffectiveCrop;
            double kx = (double)canvas.Width / fullW, ky = (double)canvas.Height / fullH;   // = scale, up to rounding of the reduced copy's size
            var scaled = new RectI((int)Math.Round(c.X * kx), (int)Math.Round(c.Y * ky), Math.Max(1, (int)Math.Round(c.Width * kx)), Math.Max(1, (int)Math.Round(c.Height * ky)))
                .ClampedTo(canvas.Width, canvas.Height);
            if (scale >= 1) scaled = c;   // full size: exactly the recipe's rectangle
            rect = scaled == new RectI(0, 0, canvas.Width, canvas.Height) ? null : scaled;
        }
        if (rect is { } r)
        {
            if (cache?.Cropped is { } cr && cr.Key == turnKey && cr.Rect == rect) cropped = cr.Cropped;
            else
            {
                cropped = PixelOps.Crop(canvas, r.X, r.Y, r.Width, r.Height);
                if (cache is not null) cache.Cropped = (turnKey, rect, cropped);
            }
        }
        else if (cache is not null) cache.Cropped = (turnKey, null, cropped);
        cancel.ThrowIfCancellationRequested();

        // Transparency: after the crop, before the colors. The cut outlines live in pixels of the original, so they are carried through the turn and the crop.
        PixelBuffer seen = cropped;
        if (!recipe.Transparency.IsIdentity)
        {
            if (cache?.Transparent is { } ct && ct.Transparency == recipe.Transparency && ReferenceEquals(ct.Input, cropped)) seen = ct.Result;
            else
            {
                var (fullW, fullH) = recipe.CanvasSize;
                double kx = (double)canvas.Width / fullW, ky = (double)canvas.Height / fullH;
                int originX = rect?.X ?? 0, originY = rect?.Y ?? 0;
                var r0 = recipe;
                seen = TransparencyOps.Apply(cropped, recipe.Transparency,
                    p => { var c = PictureGeometry.SourceToCanvas(r0, p); return new Vec2(c.X * kx - originX, c.Y * ky - originY); }, scale, cancel);
                if (cache is not null) cache.Transparent = (recipe.Transparency, cropped, seen);
            }
            cancel.ThrowIfCancellationRequested();
        }

        PixelBuffer adjusted;
        if (cache?.Adjusted is { } ca && ca.Adjust == recipe.Adjust && ReferenceEquals(ca.Input, seen)) adjusted = ca.Adjusted;
        else
        {
            adjusted = ApplyAdjust(seen, recipe.Adjust, cancel);
            if (cache is not null) cache.Adjusted = (recipe.Adjust, seen, adjusted);
        }
        cancel.ThrowIfCancellationRequested();

        return new Rendered(canvas, ApplyEffects(adjusted, recipe.Effects, scale, cancel, cache));
    }

    /// <summary>The original flipped left-right (<paramref name="mirror"/>) and then turned clockwise by <paramref name="quarter"/> quarter turns (0..3). Lossless; the same buffer for neither.</summary>
    public static PixelBuffer Orient(PixelBuffer src, bool mirror, int quarter)
    {
        var p = mirror ? PixelOps.Flip(src, true) : src;
        quarter = ((quarter % 4) + 4) % 4;
        return quarter switch
        {
            1 => PixelOps.Rotate90(p, true),
            2 => PixelOps.Flip(PixelOps.Flip(p, true), false),
            3 => PixelOps.Rotate90(p, false),
            _ => p,
        };
    }

    // ---- Effects ----

    /// <summary>
    /// The effects of the Effects tab (<see cref="PictureEffects"/> for the order): the filters, then the per-pixel looks. <paramref name="scale"/> is the factor
    /// <paramref name="src"/> was reduced by (1 at full resolution): filter sizes are given in pixels of the original, so they shrink with it.
    /// Returns <paramref name="src"/> itself when no effect is on.
    /// </summary>
    public static PixelBuffer ApplyEffects(PixelBuffer src, PictureEffects effects, double scale = 1.0, CancellationToken cancel = default, RenderCache? cache = null)
    {
        effects = effects.Clamped();
        var buffer = src;
        if (EffectMath.HasFilter(effects))
        {
            var key = (effects.NoiseReduction, effects.Blur, effects.Sharpen);
            if (cache?.Filtered is { } cf && cf.Key == key && ReferenceEquals(cf.Input, src)) buffer = cf.Filtered;
            else
            {
                if (effects.NoiseReduction > 0) buffer = Despeckle(buffer, effects.NoiseReduction, cancel);
                if (effects.Blur > 0 && effects.Blur * scale >= 0.3) buffer = GaussianBlur(buffer, effects.Blur * scale / 2);
                cancel.ThrowIfCancellationRequested();
                if (effects.Sharpen > 0) buffer = UnsharpMask(buffer, effects.Sharpen, SharpenSigma(Math.Max(src.Width, src.Height) / Math.Max(scale, 1e-9), scale), cancel);
                if (cache is not null) cache.Filtered = (key, src, buffer);
            }
        }
        cancel.ThrowIfCancellationRequested();
        return EffectMath.HasLook(effects) ? ApplyLooks(buffer, effects, cancel) : buffer;
    }

    /// <summary>Radius of the unsharp mask in pixels of the picture it works on: it grows a little with the size of the original so a large photo is not sharpened too finely to see.</summary>
    internal static double SharpenSigma(double originalLongSide, double scale) => Math.Max(0.4, (1 + originalLongSide / 3000) * scale);

    /// <summary>Grayscale, sepia, invert, black and white and posterize (<see cref="EffectMath.Look"/>) over every pixel; alpha is kept.</summary>
    public static PixelBuffer ApplyLooks(PixelBuffer src, PictureEffects effects, CancellationToken cancel = default)
    {
        var result = new PixelBuffer(src.Width, src.Height);
        var from = src.Rgba;
        var to = result.Rgba;
        int stride = src.Width * 4;
        bool perChannel = !effects.Grayscale && effects.Sepia == 0 && effects.Threshold == 0;   // invert and posterize treat each channel on its own: a table does it
        if (perChannel)
        {
            var lut = new byte[256];
            for (int v = 0; v < 256; v++) lut[v] = EffectMath.Look(effects, (byte)v, (byte)v, (byte)v).R;
            Parallel.For(0, src.Height, new ParallelOptions { CancellationToken = cancel }, y =>
            {
                for (int i = y * stride, end = i + stride; i < end; i += 4)
                {
                    to[i] = lut[from[i]]; to[i + 1] = lut[from[i + 1]]; to[i + 2] = lut[from[i + 2]]; to[i + 3] = from[i + 3];
                }
            });
            return result;
        }
        Parallel.For(0, src.Height, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            for (int i = y * stride, end = i + stride; i < end; i += 4)
            {
                var (r, g, b) = EffectMath.Look(effects, from[i], from[i + 1], from[i + 2]);
                to[i] = r; to[i + 1] = g; to[i + 2] = b; to[i + 3] = from[i + 3];
            }
        });
        return result;
    }

    /// <summary>
    /// Despeckle: each color channel replaced by the median of its 3 x 3 neighbors, blended in by <paramref name="strength"/> percent (and done twice above 50 %). Alpha is kept.
    /// </summary>
    public static PixelBuffer Despeckle(PixelBuffer src, int strength, CancellationToken cancel = default)
    {
        if (strength <= 0) return src;
        double k = Math.Clamp(strength, 1, 100) / 100.0;
        var current = src;
        for (int pass = 0; pass < (strength > 50 ? 2 : 1); pass++)
        {
            cancel.ThrowIfCancellationRequested();
            current = MedianPass(current, k, cancel);
        }
        return current;
    }

    private static PixelBuffer MedianPass(PixelBuffer src, double blend, CancellationToken cancel)
    {
        int w = src.Width, h = src.Height;
        var result = new PixelBuffer(w, h);
        var from = src.Rgba;
        var to = result.Rgba;
        float k = (float)blend;
        Parallel.For(0, h, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            int rowAbove = Math.Max(0, y - 1) * w, rowHere = y * w, rowBelow = Math.Min(h - 1, y + 1) * w;   // the picture's edge repeats beyond itself
            for (int x = 0; x < w; x++)
            {
                int xl = Math.Max(0, x - 1), xr = Math.Min(w - 1, x + 1);
                int i = (rowHere + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    int m = Median9(from[(rowAbove + xl) * 4 + c], from[(rowAbove + x) * 4 + c], from[(rowAbove + xr) * 4 + c],
                                    from[(rowHere + xl) * 4 + c], from[i + c], from[(rowHere + xr) * 4 + c],
                                    from[(rowBelow + xl) * 4 + c], from[(rowBelow + x) * 4 + c], from[(rowBelow + xr) * 4 + c]);
                    int v = from[i + c];
                    to[i + c] = (byte)(v + (m - v) * k + 0.5f);   // between v and m, never negative
                }
                to[i + 3] = from[i + 3];
            }
        });
        return result;
    }

    /// <summary>The median of nine values by a fixed network of compare-and-swap steps (no sorting, no branching on the data beyond the swaps).</summary>
    internal static int Median9(int p0, int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8)
    {
        static void S(ref int a, ref int b) { if (a > b) (a, b) = (b, a); }
        S(ref p1, ref p2); S(ref p4, ref p5); S(ref p7, ref p8);
        S(ref p0, ref p1); S(ref p3, ref p4); S(ref p6, ref p7);
        S(ref p1, ref p2); S(ref p4, ref p5); S(ref p7, ref p8);
        S(ref p0, ref p3); S(ref p5, ref p8); S(ref p4, ref p7);
        S(ref p3, ref p6); S(ref p1, ref p4); S(ref p2, ref p5);
        S(ref p4, ref p7); S(ref p4, ref p2); S(ref p6, ref p4);
        S(ref p4, ref p2);
        return p4;
    }

    /// <summary>A Gaussian blur of standard deviation <paramref name="sigma"/> pixels (edges repeat the outermost pixels).</summary>
    public static PixelBuffer GaussianBlur(PixelBuffer src, double sigma)
    {
        if (sigma < 0.05) return src;
        using var image = ToPremulImage(src);
        using var surface = SKSurface.Create(new SKImageInfo(src.Width, src.Height, SKColorType.Rgba8888, SKAlphaType.Premul)) ?? throw new OutOfMemoryException("The picture is too large to blur.");
        using var filter = SKImageFilter.CreateBlur((float)sigma, (float)sigma, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { ImageFilter = filter };
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(image, 0, 0, new SKSamplingOptions(), paint);
        return ReadUnpremul(surface, src.Width, src.Height);
    }

    /// <summary>Unsharp mask: the picture plus <paramref name="amount"/> percent (up to twice) of its difference to a blurred copy of itself. Alpha is kept.</summary>
    public static PixelBuffer UnsharpMask(PixelBuffer src, int amount, double sigma, CancellationToken cancel = default)
    {
        if (amount <= 0) return src;
        var blurred = GaussianBlur(src, sigma);
        cancel.ThrowIfCancellationRequested();
        float k = Math.Clamp(amount, 0, 100) / 100f * 2f;
        var result = new PixelBuffer(src.Width, src.Height);
        var a = src.Rgba;
        var b = blurred.Rgba;
        var to = result.Rgba;
        int stride = src.Width * 4;
        Parallel.For(0, src.Height, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            for (int i = y * stride, end = i + stride; i < end; i += 4)
            {
                for (int c = 0; c < 3; c++)
                {
                    int v = (int)(a[i + c] + k * (a[i + c] - b[i + c]) + 0.5f);
                    to[i + c] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                }
                to[i + 3] = a[i + 3];
            }
        });
        return result;
    }

    /// <summary>
    /// The picture turned clockwise by <paramref name="degrees"/> about its center, on a canvas big enough to hold all of it (<see cref="PictureGeometry.CanvasSize"/>);
    /// the corners that the picture does not reach are transparent. Smooth (bicubic) sampling. Returns <paramref name="src"/> itself for no turn.
    /// </summary>
    public static PixelBuffer Straighten(PixelBuffer src, double degrees)
    {
        if (Math.Abs(degrees) < 1e-6) return src;
        var (cw, ch) = PictureGeometry.CanvasSize(src.Width, src.Height, degrees);
        using var image = ToPremulImage(src);
        using var surface = SKSurface.Create(new SKImageInfo(cw, ch, SKColorType.Rgba8888, SKAlphaType.Premul)) ?? throw new OutOfMemoryException("The picture is too large to straighten.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(cw / 2f, ch / 2f);
        canvas.RotateDegrees((float)degrees);
        canvas.Translate(-src.Width / 2f, -src.Height / 2f);
        using var paint = new SKPaint { IsAntialias = true };
        // Catmull-Rom passes through the original pixel values, so a turn by a right angle (or none) loses nothing; Mitchell would blur even then.
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKCubicResampler.CatmullRom), paint);
        return ReadUnpremul(surface, cw, ch);
    }

    /// <summary>
    /// The colors adjusted (<see cref="AdjustMath"/>): a lookup table per channel, then the saturation/hue matrix when it is not neutral; the alpha channel is untouched.
    /// Rows are done in parallel. Returns <paramref name="src"/> itself when the adjustment is neutral.
    /// </summary>
    public static PixelBuffer ApplyAdjust(PixelBuffer src, PictureAdjust adjust, CancellationToken cancel = default)
    {
        if (adjust.IsIdentity) return src;
        var curveR = AdjustMath.Curve(adjust, 0);
        var curveG = AdjustMath.Curve(adjust, 1);
        var curveB = AdjustMath.Curve(adjust, 2);
        bool matrix = !adjust.ColorIsDefault;
        var m = AdjustMath.ColorMatrix(adjust);
        float m00 = (float)m[0], m01 = (float)m[1], m02 = (float)m[2], m10 = (float)m[3], m11 = (float)m[4], m12 = (float)m[5], m20 = (float)m[6], m21 = (float)m[7], m22 = (float)m[8];

        var result = new PixelBuffer(src.Width, src.Height);
        var from = src.Rgba;
        var to = result.Rgba;
        int stride = src.Width * 4;
        Parallel.For(0, src.Height, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            int i = y * stride, end = i + stride;
            for (; i < end; i += 4)
            {
                byte r = curveR[from[i]], g = curveG[from[i + 1]], b = curveB[from[i + 2]];
                if (matrix)
                {
                    int nr = (int)(m00 * r + m01 * g + m02 * b + 0.5f), ng = (int)(m10 * r + m11 * g + m12 * b + 0.5f), nb = (int)(m20 * r + m21 * g + m22 * b + 0.5f);
                    r = (byte)(nr < 0 ? 0 : nr > 255 ? 255 : nr);
                    g = (byte)(ng < 0 ? 0 : ng > 255 ? 255 : ng);
                    b = (byte)(nb < 0 ? 0 : nb > 255 ? 255 : nb);
                }
                to[i] = r; to[i + 1] = g; to[i + 2] = b; to[i + 3] = from[i + 3];
            }
        });
        return result;
    }

    /// <summary>The histograms (256 counts for red, green and blue) of the pixels that are visible (alpha above 0).</summary>
    public static long[][] Histograms(PixelBuffer p)
    {
        var h = new[] { new long[256], new long[256], new long[256] };
        var d = p.Rgba;
        for (int i = 0; i < d.Length; i += 4)
        {
            if (d[i + 3] == 0) continue;
            h[0][d[i]]++; h[1][d[i + 1]]++; h[2][d[i + 2]]++;
        }
        return h;
    }

    /// <summary>One-click Auto levels: the black and white points per channel from the picture as it is (<see cref="AdjustMath.AutoLevels"/>).</summary>
    public static PictureAdjust AutoLevels(PixelBuffer p, PictureAdjust current) => AdjustMath.AutoLevels(current, Histograms(p));

    /// <summary>
    /// Auto-trim: the rectangle left when plain borders are cut away. An edge is trimmed while its outermost row (or column) is almost all (99.5 %) of one color
    /// (within <paramref name="tolerance"/> per channel) or transparent, provided the edge starts out plain (90 % of one color); edges that carry a picture are
    /// left alone. Null when nothing would be cut or nothing would be left.
    /// </summary>
    public static RectI? AutoTrim(PixelBuffer canvas, int tolerance = 12)
    {
        int w = canvas.Width, h = canvas.Height;
        int top = 0, bottom = h, left = 0, right = w;

        bool Plain(bool row, int index, int from, int to, ref (byte R, byte G, byte B, bool Any)? reference)
        {
            // Is this row (or column) between from and to all one color or transparent?
            int count = to - from, ok = 0;
            var d = canvas.Rgba;
            var refc = reference;
            for (int k = from; k < to; k++)
            {
                int i = row ? (index * w + k) * 4 : (k * w + index) * 4;
                if (d[i + 3] < 16) { ok++; continue; }
                if (refc is null) refc = (d[i], d[i + 1], d[i + 2], true);
                var c = refc.Value;
                if (Math.Abs(d[i] - c.R) <= tolerance && Math.Abs(d[i + 1] - c.G) <= tolerance && Math.Abs(d[i + 2] - c.B) <= tolerance) ok++;
            }
            reference = refc;
            return ok >= count * 0.995;
        }

        // The color of an edge: the one that most of its outermost row/column shares.
        (byte R, byte G, byte B, bool Any)? EdgeColor(bool row, int index, int from, int to)
        {
            var buckets = new Dictionary<int, (int N, long R, long G, long B)>();
            int step = Math.Max(1, (to - from) / 256);
            int total = 0;
            for (int k = from; k < to; k += step)
            {
                int i = row ? (index * w + k) * 4 : (k * w + index) * 4;
                total++;
                if (canvas.Rgba[i + 3] < 16) continue;
                int key = (canvas.Rgba[i] >> 4) << 8 | (canvas.Rgba[i + 1] >> 4) << 4 | (canvas.Rgba[i + 2] >> 4);
                buckets.TryGetValue(key, out var b);
                buckets[key] = (b.N + 1, b.R + canvas.Rgba[i], b.G + canvas.Rgba[i + 1], b.B + canvas.Rgba[i + 2]);
            }
            if (buckets.Count == 0) return (0, 0, 0, false);   // all transparent: plain
            var best = buckets.Values.MaxBy(v => v.N);
            if (best.N < total * 0.9) return null;            // this edge carries picture
            return ((byte)(best.R / best.N), (byte)(best.G / best.N), (byte)(best.B / best.N), true);
        }

        // Top
        if (EdgeColor(true, 0, 0, w) is { } topColor)
        {
            var reference = topColor.Any ? topColor : ((byte, byte, byte, bool Any)?)null;
            while (top < h - 1 && Plain(true, top, 0, w, ref reference)) top++;
        }
        // Bottom
        if (EdgeColor(true, h - 1, 0, w) is { } bottomColor)
        {
            var reference = bottomColor.Any ? bottomColor : ((byte, byte, byte, bool Any)?)null;
            while (bottom - 1 > top && Plain(true, bottom - 1, 0, w, ref reference)) bottom--;
        }
        // Left and right (within the rows that are left)
        if (EdgeColor(false, 0, top, bottom) is { } leftColor)
        {
            var reference = leftColor.Any ? leftColor : ((byte, byte, byte, bool Any)?)null;
            while (left < w - 1 && Plain(false, left, top, bottom, ref reference)) left++;
        }
        if (EdgeColor(false, w - 1, top, bottom) is { } rightColor)
        {
            var reference = rightColor.Any ? rightColor : ((byte, byte, byte, bool Any)?)null;
            while (right - 1 > left && Plain(false, right - 1, top, bottom, ref reference)) right--;
        }

        var result = new RectI(left, top, right - left, bottom - top);
        return result == new RectI(0, 0, w, h) || result.IsEmpty ? null : result;
    }

    /// <summary>A copy of <paramref name="src"/> whose long side is at most <paramref name="maxSide"/> pixels, and the factor it was reduced by (1 when it was small enough: the same buffer is returned).</summary>
    public static (PixelBuffer Copy, double Scale) Reduce(PixelBuffer src, int maxSide = PreviewSide)
    {
        int longSide = Math.Max(src.Width, src.Height);
        if (longSide <= maxSide) return (src, 1);
        double scale = (double)maxSide / longSide;
        int w = Math.Max(1, (int)Math.Round(src.Width * scale)), h = Math.Max(1, (int)Math.Round(src.Height * scale));
        return (PixelResampler.Resample(src, w, h), scale);
    }

    // ---- Skia plumbing ----

    internal static SKImage ToPremulImage(PixelBuffer p)
    {
        var info = new SKImageInfo(p.Width, p.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var premul = PixelResampler.Premultiplied(p.Rgba);
        var handle = GCHandle.Alloc(premul, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            return SKImage.FromPixelCopy(pixmap) ?? throw new InvalidOperationException("The picture could not be prepared.");
        }
        finally { handle.Free(); }
    }

    internal static PixelBuffer ReadUnpremul(SKSurface surface, int width, int height)
    {
        var result = new PixelBuffer(width, height);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(result.Rgba, GCHandleType.Pinned);
        try
        {
            using var snapshot = surface.Snapshot();
            if (!snapshot.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes)) throw new InvalidOperationException("The picture could not be read back.");
        }
        finally { handle.Free(); }
        return result;
    }
}
