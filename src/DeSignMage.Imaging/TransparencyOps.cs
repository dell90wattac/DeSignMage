using System.Runtime.InteropServices;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using SkiaSharp;

namespace DeSignMage.Imaging;

/// <summary>
/// The pixel work of the Picture Editor's Transparency tab (<see cref="PictureTransparency"/>): it builds a mask (255 = stays, 0 = gone) from the
/// knocked-out colors and the cut outlines, shrinks and softens its border, and multiplies it into the picture's alpha. Only alpha changes; the colors stay.
/// </summary>
public static class TransparencyOps
{
    /// <summary>How far past the tolerance a color is only partly removed, in percent of the black-to-white distance: this makes the edge of a knocked-out color smooth instead of jagged.</summary>
    public const double RampPercent = 6;

    private const double MaxDistance = 441.6729559300637;   // sqrt(3) * 255: black to white

    /// <summary>
    /// The picture with the recipe's transparency applied; <paramref name="src"/> itself when it changes nothing.
    /// </summary>
    /// <param name="cutToBuffer">Maps a point of the original picture to a pixel position of <paramref name="src"/> (the cut outlines are stored in pixels of the original).</param>
    /// <param name="scale">The factor <paramref name="src"/> was reduced by (1 at full size): shrink and softness are given in pixels of the original.</param>
    public static PixelBuffer Apply(PixelBuffer src, PictureTransparency t, Func<Vec2, Vec2> cutToBuffer, double scale = 1.0, CancellationToken cancel = default)
    {
        t = t.Clamped();
        if (t.IsIdentity) return src;
        int w = src.Width, h = src.Height;
        var mask = new byte[w * h];
        Array.Fill(mask, (byte)255);

        if (t.HasColors) ColorMask(src, t, mask, cancel);
        cancel.ThrowIfCancellationRequested();
        if (t.HasCuts) CutMask(t, cutToBuffer, mask, w, h);
        cancel.ThrowIfCancellationRequested();

        int shrink = (int)Math.Round(t.Shrink * scale);
        if (shrink > 0 && (t.HasColors || t.HasCuts)) mask = Erode(mask, w, h, shrink);
        double sigma = t.Softness * scale / 2;
        if (sigma >= 0.3 && (t.HasColors || t.HasCuts)) mask = Blur(mask, w, h, sigma);
        cancel.ThrowIfCancellationRequested();

        var result = new PixelBuffer(w, h);
        var from = src.Rgba;
        var to = result.Rgba;
        int opacity = t.Opacity;
        Parallel.For(0, h, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            for (int x = 0, p = y * w, i = p * 4; x < w; x++, p++, i += 4)
            {
                to[i] = from[i]; to[i + 1] = from[i + 1]; to[i + 2] = from[i + 2];
                int a = from[i + 3] * mask[p] / 255;
                to[i + 3] = (byte)(opacity >= 100 ? a : (a * opacity + 50) / 100);
            }
        });
        return result;
    }

    // ---------------------------------------------------------------- colors

    /// <summary>Distance of a pixel from the nearest chosen color, as a squared RGB distance.</summary>
    private static int NearestDistance2(byte r, byte g, byte b, int[] colors)
    {
        int best = int.MaxValue;
        foreach (int c in colors)
        {
            int dr = r - ((c >> 16) & 255), dg = g - ((c >> 8) & 255), db = b - (c & 255);
            int d = dr * dr + dg * dg + db * db;
            if (d < best) best = d;
        }
        return best;
    }

    private static void ColorMask(PixelBuffer src, PictureTransparency t, byte[] mask, CancellationToken cancel)
    {
        int w = src.Width, h = src.Height;
        var colors = t.Colors.ToArray();
        double limit = t.Tolerance / 100.0 * MaxDistance;
        double ramp = RampPercent / 100.0 * MaxDistance;
        double limit2 = limit * limit, outer2 = (limit + ramp) * (limit + ramp);
        var from = src.Rgba;

        // The mask value of a pixel at distance d2 from the chosen colors: 0 inside the tolerance, rising to 255 over the ramp.
        byte Ramp(int d2) => d2 <= limit2 ? (byte)0 : d2 >= outer2 ? (byte)255 : (byte)Math.Round((Math.Sqrt(d2) - limit) / ramp * 255);

        if (!t.EdgeOnly)
        {
            Parallel.For(0, h, new ParallelOptions { CancellationToken = cancel }, y =>
            {
                for (int x = 0, p = y * w, i = p * 4; x < w; x++, p++, i += 4)
                    if (from[i + 3] > 0) mask[p] = Ramp(NearestDistance2(from[i], from[i + 1], from[i + 2], colors));
            });
            return;
        }

        // Only the part of the background that touches the edge goes: a flood fill from every edge pixel that is in the tolerance.
        // Pixels that are already clear (the corners of a turned picture) count as background, so the fill passes through them.
        var near = new bool[w * h];
        var d2s = new int[w * h];
        Parallel.For(0, h, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            for (int x = 0, p = y * w, i = p * 4; x < w; x++, p++, i += 4)
            {
                int d2 = from[i + 3] == 0 ? 0 : NearestDistance2(from[i], from[i + 1], from[i + 2], colors);
                d2s[p] = d2;
                near[p] = d2 <= limit2;
            }
        });
        var gone = new bool[w * h];
        var stack = new Stack<int>();
        void Seed(int p) { if (near[p] && !gone[p]) { gone[p] = true; stack.Push(p); } }
        for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
        while (stack.Count > 0)
        {
            int p = stack.Pop(), x = p % w, y = p / w;
            if (x > 0) Seed(p - 1);
            if (x < w - 1) Seed(p + 1);
            if (y > 0) Seed(p - w);
            if (y < h - 1) Seed(p + w);
        }
        cancel.ThrowIfCancellationRequested();
        for (int p = 0; p < mask.Length; p++)
        {
            if (gone[p]) { mask[p] = 0; continue; }
            // the ramp: pixels next to the removed background that are only a little more different than the tolerance
            int x = p % w, y = p / w;
            bool beside = x > 0 && gone[p - 1] || x < w - 1 && gone[p + 1] || y > 0 && gone[p - w] || y < h - 1 && gone[p + w];
            if (beside && d2s[p] < outer2) mask[p] = Ramp(d2s[p]);
        }
    }

    /// <summary>
    /// The color a plain background has: the most common color around the edge of the picture (pixels that are already clear are ignored).
    /// Null when the edge has no color most of it shares (a picture all the way to its borders).
    /// </summary>
    public static int? BackgroundColor(PixelBuffer p, double minShare = 0.5)
    {
        int w = p.Width, h = p.Height;
        var buckets = new Dictionary<int, (int N, long R, long G, long B)>();
        int total = 0;
        void Take(int x, int y)
        {
            int i = (y * w + x) * 4;
            total++;
            if (p.Rgba[i + 3] < 16) return;
            int key = (p.Rgba[i] >> 4) << 8 | (p.Rgba[i + 1] >> 4) << 4 | (p.Rgba[i + 2] >> 4);
            buckets.TryGetValue(key, out var b);
            buckets[key] = (b.N + 1, b.R + p.Rgba[i], b.G + p.Rgba[i + 1], b.B + p.Rgba[i + 2]);
        }
        int stepX = Math.Max(1, w / 512), stepY = Math.Max(1, h / 512);
        for (int x = 0; x < w; x += stepX) { Take(x, 0); Take(x, h - 1); }
        for (int y = 0; y < h; y += stepY) { Take(0, y); Take(w - 1, y); }
        if (buckets.Count == 0) return null;
        var best = buckets.Values.MaxBy(v => v.N);
        if (best.N < total * minShare) return null;
        return PictureTransparency.Pack((byte)(best.R / best.N), (byte)(best.G / best.N), (byte)(best.B / best.N));
    }

    // ---------------------------------------------------------------- cuts

    private static void CutMask(PictureTransparency t, Func<Vec2, Vec2> toBuffer, byte[] mask, int w, int h)
    {
        var keep = Coverage(t.Cuts.Where(c => c.IsUsable && c.Keep), toBuffer, w, h);
        var remove = Coverage(t.Cuts.Where(c => c.IsUsable && !c.Keep), toBuffer, w, h);
        for (int p = 0; p < mask.Length; p++)
        {
            int m = mask[p];
            if (keep is not null) m = m * keep[p] / 255;
            if (remove is not null) m = m * (255 - remove[p]) / 255;
            mask[p] = (byte)m;
        }
    }

    /// <summary>The union of the outlines as anti-aliased coverage, 0..255 per pixel; null when there are none.</summary>
    private static byte[]? Coverage(IEnumerable<CutShape> shapes, Func<Vec2, Vec2> toBuffer, int w, int h)
    {
        var list = shapes.ToList();
        if (list.Count == 0) return null;
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul)) ?? throw new OutOfMemoryException("The picture is too large to cut.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };
        foreach (var shape in list)
        {
            using var path = new SKPath();
            for (int i = 0; i < shape.Points.Count; i++)
            {
                var q = toBuffer(shape.Points[i]);
                if (i == 0) path.MoveTo((float)q.X, (float)q.Y); else path.LineTo((float)q.X, (float)q.Y);
            }
            path.Close();
            canvas.DrawPath(path, paint);
        }
        var bytes = new byte[w * h];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            using var snapshot = surface.Snapshot();
            if (!snapshot.ReadPixels(new SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul), handle.AddrOfPinnedObject(), w)) throw new InvalidOperationException("The cut could not be read back.");
        }
        finally { handle.Free(); }
        return bytes;
    }

    // ---------------------------------------------------------------- the border of the mask

    /// <summary>The mask with every clear (0) area growing by <paramref name="radius"/> pixels, so the kept picture shrinks away from its cut border (a minimum filter, square window); the picture's own edge does not retreat.</summary>
    public static byte[] Erode(byte[] mask, int w, int h, int radius)
    {
        var horizontal = new byte[mask.Length];
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                byte min = 255;
                for (int k = Math.Max(0, x - radius), end = Math.Min(w - 1, x + radius); k <= end; k++) if (mask[row + k] < min) min = mask[row + k];
                horizontal[row + x] = min;
            }
        });
        var result = new byte[mask.Length];
        Parallel.For(0, h, y =>
        {
            int y0 = Math.Max(0, y - radius), y1 = Math.Min(h - 1, y + radius);
            for (int x = 0; x < w; x++)
            {
                byte min = 255;
                for (int k = y0; k <= y1; k++) if (horizontal[k * w + x] < min) min = horizontal[k * w + x];
                result[y * w + x] = min;
            }
        });
        return result;
    }

    /// <summary>A soft blur of the mask (three box blurs approximate a Gaussian of standard deviation <paramref name="sigma"/>); the picture's edge repeats.</summary>
    public static byte[] Blur(byte[] mask, int w, int h, double sigma)
    {
        // box width for three passes: sqrt(12 sigma^2 / 3 + 1)
        int box = Math.Max(1, (int)Math.Round(Math.Sqrt(4 * sigma * sigma + 1)));
        int radius = box / 2;
        var a = new float[mask.Length];
        for (int i = 0; i < mask.Length; i++) a[i] = mask[i];
        var b = new float[mask.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxHorizontal(a, b, w, h, radius);
            BoxVertical(b, a, w, h, radius);
        }
        var result = new byte[mask.Length];
        for (int i = 0; i < mask.Length; i++) result[i] = (byte)Math.Clamp((int)(a[i] + 0.5f), 0, 255);
        return result;
    }

    private static void BoxHorizontal(float[] from, float[] to, int w, int h, int r)
    {
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            float sum = 0, size = 2 * r + 1;
            for (int k = -r; k <= r; k++) sum += from[row + Math.Clamp(k, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                to[row + x] = sum / size;
                sum += from[row + Math.Min(w - 1, x + r + 1)] - from[row + Math.Max(0, x - r)];
            }
        });
    }

    private static void BoxVertical(float[] from, float[] to, int w, int h, int r)
    {
        Parallel.For(0, w, x =>
        {
            float sum = 0, size = 2 * r + 1;
            for (int k = -r; k <= r; k++) sum += from[Math.Clamp(k, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                to[y * w + x] = sum / size;
                sum += from[Math.Min(h - 1, y + r + 1) * w + x] - from[Math.Max(0, y - r) * w + x];
            }
        });
    }
}

/// <summary>Decodes a stored picture into the coarse <see cref="AlphaMask"/> that picking uses.</summary>
public static class AlphaMasks
{
    /// <summary>Longest side of the mask, in cells; a click resolves to about 1/256 of the picture.</summary>
    public const int MaxSide = 256;

    /// <summary>
    /// The mask of an encoded picture, or null when it has no see-through parts that matter (a JPEG, an opaque PNG, or one that cannot be read).
    /// A cell is visible when any pixel in it is, so thin lines are not lost.
    /// </summary>
    public static AlphaMask? Load(byte[] data)
    {
        try
        {
            // A picture whose header says it has no alpha channel (every JPEG, most photos) is never decoded just for picking.
            using (var codec = SKCodec.Create(new SKMemoryStream(data)))
                if (codec is null || codec.Info.AlphaType == SKAlphaType.Opaque) return null;
            return FromPixels(ImageCodec.Decode(data));
        }
        catch (InvalidDataException) { return null; }
    }

    public static AlphaMask? FromPixels(PixelBuffer p)
    {
        int w = p.Width, h = p.Height;
        double k = Math.Min(1.0, (double)MaxSide / Math.Max(w, h));
        int mw = Math.Max(1, (int)Math.Ceiling(w * k)), mh = Math.Max(1, (int)Math.Ceiling(h * k));
        var cells = new byte[mw * mh];
        bool anyClear = false;
        var d = p.Rgba;
        for (int y = 0; y < h; y++)
        {
            int cy = Math.Min(mh - 1, (int)(y * k));
            for (int x = 0; x < w; x++)
            {
                byte a = d[(y * w + x) * 4 + 3];
                int c = cy * mw + Math.Min(mw - 1, (int)(x * k));
                if (a > cells[c]) cells[c] = a;
                if (a < 250) anyClear = true;
            }
        }
        if (!anyClear) return null;
        // A mask that says "all clear" is no use for picking either: such a picture keeps its frame.
        return cells.All(c => c < AlphaMask.VisibleAlpha) ? null : new AlphaMask(mw, mh, cells);
    }
}
