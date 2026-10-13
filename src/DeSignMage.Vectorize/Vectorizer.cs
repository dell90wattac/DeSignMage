using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Vectorize;

/// <summary>
/// Raster to vector. A clean-room tracer built on the same ideas as VTracer (color clustering, stacked layers,
/// corner-aware spline fitting) and Potrace's boundary-first view: reduce the colors, find regions of one color, merge
/// noise into neighbors, trace the boundaries once (so neighbors share them), fit lines and cubic Beziers, then
/// assemble one closed path per region.
/// </summary>
public static class Vectorizer
{
    public static VectorizeResult Trace(RasterImage source, VectorizeOptions? options = null, CancellationToken cancel = default)
    {
        var o = (options ?? new VectorizeOptions()).Normalized();

        // ---- Working image ----
        var work = source.LimitSize(o.MaxWorkingSize);
        int longest = Math.Max(work.Width, work.Height);
        if (longest < o.MinWorkingSize)
        {
            double f = Math.Min(4.0, (double)o.MinWorkingSize / longest);
            work = work.Resize((int)Math.Round(work.Width * f), (int)Math.Round(work.Height * f));
        }
        for (int i = 0; i < o.Denoise; i++) work = work.Median3();
        cancel.ThrowIfCancellationRequested();

        double toSource = (double)source.Width / work.Width;   // working pixel -> source pixel
        double areaScale = 1 / (toSource * toSource);          // source pixel area -> working pixel area

        // ---- Colors ----
        QuantizedImage q = o.Mode == VectorizeMode.Color ? Quantizer.Quantize(work, o.Colors) : Threshold(work, o.Threshold);
        cancel.ThrowIfCancellationRequested();
        var palette = q.Palette;
        var index = q.Index;
        if (palette.Length == 0)
            return new VectorizeResult(source.Width, source.Height, [], [], o.OutlineWidth);
        var lab = new Quantizer.PaletteLab(palette);

        // ---- Regions and the noise filter ----
        int minArea = o.SpeckleSize <= 0 ? 0 : Math.Max(1, (int)Math.Round(o.SpeckleSize * areaScale));
        double thinWidth = o.SpeckleSize <= 0 ? 0 : 2.2 * Math.Max(1, 1 / toSource);
        var map = RegionMap.Build(index, work.Width, work.Height);
        for (int pass = 0; pass < 3 && (minArea > 0 || thinWidth > 0); pass++)
        {
            var merged = map.MergeSmall(lab, minArea, thinWidth);
            if (merged.AsSpan().SequenceEqual(index)) break;
            index = merged;
            map = RegionMap.Build(index, work.Width, work.Height);
            cancel.ThrowIfCancellationRequested();
        }

        // ---- Which regions become shapes ----
        var emit = new bool[map.Count];
        for (int r = 0; r < map.Count; r++)
        {
            int c = map.Color[r];
            emit[r] = c >= 0 && (o.Mode == VectorizeMode.Color || c == 0);   // B&W: palette 0 is black, 1 is white
        }
        if (o.OmitBackground && o.Mode == VectorizeMode.Color)
        {
            int bg = BorderBackground(map);
            if (bg >= 0) emit[bg] = false;
        }

        // ---- Boundaries, curves, shapes ----
        var graph = new BoundaryGraph(map.Label, map.Width, map.Height);
        cancel.ThrowIfCancellationRequested();
        var builder = new ShapeBuilder(map, graph, new CurveFitter(o, 1 / toSource), toSource);
        var layering = o.Mode == VectorizeMode.Outline ? VectorizeLayering.CutOut : o.Layering;
        var built = builder.Build(emit, layering);
        cancel.ThrowIfCancellationRequested();

        var shapes = new List<VectorShape>(built.Count);
        var used = new HashSet<int>();
        foreach (var (region, path) in built)
        {
            if (path.IsEmpty) continue;
            var color = palette[map.Color[region]];
            used.Add(map.Color[region]);
            shapes.Add(o.Mode == VectorizeMode.Outline
                ? new VectorShape(path, null, DocColor.Black)
                : new VectorShape(path, color, null));
        }
        return new VectorizeResult(source.Width, source.Height, shapes, palette, o.OutlineWidth);
    }

    private static QuantizedImage Threshold(RasterImage img, int threshold)
    {
        int n = img.Width * img.Height;
        var lum = new byte[n];
        var hist = new int[256];
        for (int p = 0; p < n; p++)
        {
            int o = p * 4;
            double l = 0.2126 * img.Rgba[o] + 0.7152 * img.Rgba[o + 1] + 0.0722 * img.Rgba[o + 2];
            // Transparent pixels count as white paper.
            l = l * (img.Rgba[o + 3] / 255.0) + 255 * (1 - img.Rgba[o + 3] / 255.0);
            lum[p] = (byte)Math.Clamp((int)Math.Round(l), 0, 255);
            hist[lum[p]]++;
        }
        int t = threshold > 0 ? threshold : Otsu(hist, n);
        var index = new int[n];
        for (int p = 0; p < n; p++) index[p] = lum[p] < t ? 0 : 1;
        return new QuantizedImage(img.Width, img.Height, index, [DocColor.Black, DocColor.White]);
    }

    /// <summary>Otsu's method: the threshold that best separates two intensity classes.</summary>
    internal static int Otsu(int[] hist, int total)
    {
        double sum = 0;
        for (int i = 0; i < 256; i++) sum += i * (double)hist[i];
        double sumB = 0, best = -1;
        int wB = 0, threshold = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            int wF = total - wB;
            if (wF == 0) break;
            sumB += t * (double)hist[t];
            double mB = sumB / wB, mF = (sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; threshold = t + 1; }
        }
        return Math.Clamp(threshold, 1, 255);
    }

    /// <summary>Palette colors recomputed as the mean of the pixels that ended up with each color after merging.</summary>
    private static DocColor[] Refine(DocColor[] palette, int[] index, RasterImage work)
    {
        var sr = new double[palette.Length]; var sg = new double[palette.Length]; var sb = new double[palette.Length]; var cnt = new long[palette.Length];
        for (int p = 0; p < index.Length; p++)
        {
            int c = index[p];
            if (c < 0) continue;
            sr[c] += work.Rgba[p * 4]; sg[c] += work.Rgba[p * 4 + 1]; sb[c] += work.Rgba[p * 4 + 2]; cnt[c]++;
        }
        var result = (DocColor[])palette.Clone();
        for (int c = 0; c < palette.Length; c++)
            if (cnt[c] > 0) result[c] = new DocColor((byte)Math.Round(sr[c] / cnt[c]), (byte)Math.Round(sg[c] / cnt[c]), (byte)Math.Round(sb[c] / cnt[c]));
        return result;
    }

    /// <summary>The largest opaque region that touches the image border, or -1.</summary>
    private static int BorderBackground(RegionMap map)
    {
        int best = -1;
        void Check(int x, int y)
        {
            int r = map.Label[y * map.Width + x];
            if (map.Color[r] >= 0 && (best < 0 || map.Area[r] > map.Area[best])) best = r;
        }
        for (int x = 0; x < map.Width; x++) { Check(x, 0); Check(x, map.Height - 1); }
        for (int y = 0; y < map.Height; y++) { Check(0, y); Check(map.Width - 1, y); }
        return best;
    }
}


