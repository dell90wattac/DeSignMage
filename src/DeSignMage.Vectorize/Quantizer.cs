using DeSignMage.Core.Model;

namespace DeSignMage.Vectorize;

/// <summary>Palette index per pixel (-1 = transparent) plus the palette.</summary>
internal sealed record QuantizedImage(int Width, int Height, int[] Index, DocColor[] Palette);

/// <summary>
/// Color reduction: a weighted k-means in CIE Lab over a 6-bit-per-channel histogram (so it sees every pixel, not a
/// sample, and is fast), seeded with k-means++ using a fixed seed so results are repeatable.
/// </summary>
internal static class Quantizer
{
    private const int AlphaCutoff = 128;

    public static QuantizedImage Quantize(RasterImage img, int colors)
    {
        int n = img.Width * img.Height;
        var rgba = img.Rgba;

        // Histogram of opaque pixels on a 64x64x64 grid.
        // Edge pixels (anti-aliasing blends) carry almost no weight, so the palette is made of the image's true flat
        // colors instead of dozens of in-between grays.
        var count = new int[1 << 18];
        var sumR = new double[1 << 18];
        var sumG = new double[1 << 18];
        var sumB = new double[1 << 18];
        var wt = new double[1 << 18];
        var maxw = new double[1 << 18];
        var bin = new int[n];
        for (int p = 0; p < n; p++)
        {
            int o = p * 4;
            if (rgba[o + 3] < AlphaCutoff) { bin[p] = -1; continue; }
            int key = ((rgba[o] >> 2) << 12) | ((rgba[o + 1] >> 2) << 6) | (rgba[o + 2] >> 2);
            bin[p] = key;
            double fw = Flatness(img, p % img.Width, p / img.Width);
            count[key]++; wt[key] += fw; if (fw > maxw[key]) maxw[key] = fw; sumR[key] += rgba[o] * fw; sumG[key] += rgba[o + 1] * fw; sumB[key] += rgba[o + 2] * fw;
        }

        var keys = new List<int>();
        for (int k = 0; k < count.Length; k++) if (count[k] > 0) keys.Add(k);
        var index = new int[n];
        if (keys.Count == 0)
        {
            Array.Fill(index, -1);
            return new QuantizedImage(img.Width, img.Height, index, []);
        }

        int m = keys.Count;
        var w = new double[m];
        var solid = new bool[m];
        var lab = new (double L, double A, double B)[m];
        for (int i = 0; i < m; i++)
        {
            int k = keys[i];
            w[i] = wt[k];
            solid[i] = maxw[k] >= 0.5;
            lab[i] = ToLab(sumR[k] / wt[k], sumG[k] / wt[k], sumB[k] / wt[k]);
        }

        int kc = Math.Min(colors, m);
        var centers = SeedPlusPlus(lab, w, solid, kc);
        kc = centers.Length;
        var assign = new int[m];
        for (int iter = 0; iter < 12; iter++)
        {
            bool changed = false;
            for (int i = 0; i < m; i++)
            {
                int best = Nearest(lab[i], centers);
                if (best != assign[i]) { assign[i] = best; changed = true; }
            }
            var acc = new (double L, double A, double B, double W)[kc];
            for (int i = 0; i < m; i++)
            {
                ref var a = ref acc[assign[i]];
                a.L += lab[i].L * w[i]; a.A += lab[i].A * w[i]; a.B += lab[i].B * w[i]; a.W += w[i];
            }
            for (int c = 0; c < kc; c++)
                if (acc[c].W > 0) centers[c] = (acc[c].L / acc[c].W, acc[c].A / acc[c].W, acc[c].B / acc[c].W);
            if (!changed && iter > 0) break;
        }
        for (int i = 0; i < m; i++) assign[i] = Nearest(lab[i], centers);

        // Final palette colors: the true mean RGB of the pixels assigned to each center. Drop empty centers.
        var rs = new double[kc]; var gs = new double[kc]; var bs = new double[kc]; var ws = new double[kc];
        for (int i = 0; i < m; i++)
        {
            int k = keys[i], c = assign[i];
            rs[c] += sumR[k]; gs[c] += sumG[k]; bs[c] += sumB[k]; ws[c] += wt[k];
        }
        var remap = new int[kc];
        var palette = new List<DocColor>();
        for (int c = 0; c < kc; c++)
        {
            if (ws[c] <= 0) { remap[c] = -1; continue; }
            remap[c] = palette.Count;
            palette.Add(new DocColor((byte)Math.Round(rs[c] / ws[c]), (byte)Math.Round(gs[c] / ws[c]), (byte)Math.Round(bs[c] / ws[c])));
        }

        var binToPalette = new int[1 << 18];
        for (int i = 0; i < m; i++) binToPalette[keys[i]] = remap[assign[i]];
        for (int p = 0; p < n; p++) index[p] = bin[p] < 0 ? -1 : binToPalette[bin[p]];
        return new QuantizedImage(img.Width, img.Height, index, palette.ToArray());
    }

    /// <summary>
    /// How much a pixel should count when choosing the palette: 1 in flat areas and for thin lines or dots (whose color
    /// is more extreme than anything around them), falling toward 0 for in-between colors across an edge, the
    /// anti-aliasing blends that are not colors of the artwork.
    /// </summary>
    private static double Flatness(RasterImage img, int x, int y)
    {
        var d = img.Rgba;
        int w = img.Width, h = img.Height;
        int o = (y * w + x) * 4;
        int g = 0;
        int loR = 255, loG = 255, loB = 255, hiR = 0, hiG = 0, hiB = 0;
        Span<int> nb = stackalloc int[4];
        int n = 0;
        if (x > 0) nb[n++] = o - 4;
        if (x + 1 < w) nb[n++] = o + 4;
        if (y > 0) nb[n++] = o - w * 4;
        if (y + 1 < h) nb[n++] = o + w * 4;
        for (int i = 0; i < n; i++)
        {
            int q = nb[i];
            g = Math.Max(g, Math.Max(Math.Abs(d[o] - d[q]), Math.Max(Math.Abs(d[o + 1] - d[q + 1]), Math.Abs(d[o + 2] - d[q + 2]))));
            loR = Math.Min(loR, d[q]); hiR = Math.Max(hiR, d[q]);
            loG = Math.Min(loG, d[q + 1]); hiG = Math.Max(hiG, d[q + 1]);
            loB = Math.Min(loB, d[q + 2]); hiB = Math.Max(hiB, d[q + 2]);
        }
        if (g <= 14) return 1;
        // A thin line or dot: its color is more extreme than anything around it, so it is real artwork, not a blend.
        if (d[o] > hiR + 10 || d[o] < loR - 10 || d[o + 1] > hiG + 10 || d[o + 1] < loG - 10 || d[o + 2] > hiB + 10 || d[o + 2] < loB - 10) return 1;
        double t = g / 14.0;
        return 1.0 / (1.0 + t * t * t);
    }
    private static int Nearest((double L, double A, double B) p, (double L, double A, double B)[] centers)
    {
        int best = 0;
        double bd = double.MaxValue;
        for (int c = 0; c < centers.Length; c++)
        {
            double dl = p.L - centers[c].L, da = p.A - centers[c].A, db = p.B - centers[c].B;
            double d = dl * dl + da * da + db * db;
            if (d < bd) { bd = d; best = c; }
        }
        return best;
    }

    /// <summary>
    /// Greedy seeding (farthest, weighted) with an early stop: once every color that has flat pixels is within a few Lab
    /// units of some center there is nothing left to separate, so a flat 3-color logo gets 3 palette entries even if 16
    /// were allowed. Colors that exist only as anti-aliasing blends (<paramref name="solid"/> false) never get a center.
    /// </summary>
    private static (double L, double A, double B)[] SeedPlusPlus((double L, double A, double B)[] pts, double[] w, bool[] solid, int k)
    {
        var centers = new (double L, double A, double B)[k];
        int m = pts.Length;

        // First center: the heaviest bin (the dominant color), so a flat logo's background is always a palette entry.
        int first = 0;
        for (int i = 1; i < m; i++) if (w[i] > w[first]) first = i;
        centers[0] = pts[first];

        var dist = new double[m];
        for (int i = 0; i < m; i++) dist[i] = Dist2(pts[i], centers[0]);
        for (int c = 1; c < k; c++)
        {
            int pick = -1;
            double best = -1;
            for (int i = 0; i < m; i++)
            {
                if (!solid[i] || dist[i] < 64) continue;   // within ~8 Lab units of an existing center, or only a blend
                double score = dist[i] * w[i];
                if (score > best) { best = score; pick = i; }
            }
            if (pick < 0) return centers[..c];
            centers[c] = pts[pick];
            for (int i = 0; i < m; i++) dist[i] = Math.Min(dist[i], Dist2(pts[i], centers[c]));
        }
        return centers;
    }
    private static double Dist2((double L, double A, double B) a, (double L, double A, double B) b)
    {
        double dl = a.L - b.L, da = a.A - b.A, db = a.B - b.B;
        return dl * dl + da * da + db * db;
    }

    // ---- Color space ----

    private static readonly double[] SrgbToLinear = BuildSrgbTable();

    private static double[] BuildSrgbTable()
    {
        var t = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            t[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return t;
    }

    private static double Linear(double v)
    {
        int i = (int)v;
        if (i >= 255) return SrgbToLinear[255];
        double f = v - i;
        return SrgbToLinear[i] * (1 - f) + SrgbToLinear[i + 1] * f;
    }

    /// <summary>sRGB (0-255) to CIE Lab (D65).</summary>
    public static (double L, double A, double B) ToLab(double r, double g, double b)
    {
        double lr = Linear(r), lg = Linear(g), lb = Linear(b);
        double x = (0.4124564 * lr + 0.3575761 * lg + 0.1804375 * lb) / 0.95047;
        double y = 0.2126729 * lr + 0.7151522 * lg + 0.0721750 * lb;
        double z = (0.0193339 * lr + 0.1191920 * lg + 0.9503041 * lb) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>Palette colors in Lab for fast color comparisons between palette indices.</summary>
    public sealed class PaletteLab
    {
        private readonly (double L, double A, double B)[] _lab;

        public PaletteLab(DocColor[] palette) => _lab = palette.Select(c => ToLab(c.R, c.G, c.B)).ToArray();

        /// <summary>Lab distance between two palette entries; transparent (-1) is far from everything.</summary>
        public double Distance(int a, int b)
        {
            if (a < 0 || b < 0) return a == b ? 0 : 1000;
            return Math.Sqrt(Dist2(_lab[a], _lab[b]));
        }

        /// <summary>True if color <paramref name="c"/> lies close to the straight line between colors <paramref name="a"/> and <paramref name="b"/>
        /// (and between them): the signature of an anti-aliasing or blending fringe.</summary>
        public bool IsBetween(int c, int a, int b)
        {
            var pc = _lab[c]; var pa = _lab[a]; var pb = _lab[b];
            double vx = pb.L - pa.L, vy = pb.A - pa.A, vz = pb.B - pa.B;
            double len2 = vx * vx + vy * vy + vz * vz;
            if (len2 < 1e-9) return false;
            double t = ((pc.L - pa.L) * vx + (pc.A - pa.A) * vy + (pc.B - pa.B) * vz) / len2;
            if (t < 0.05 || t > 0.95) return false;
            double qx = pa.L + t * vx - pc.L, qy = pa.A + t * vy - pc.A, qz = pa.B + t * vz - pc.B;
            double off = Math.Sqrt(qx * qx + qy * qy + qz * qz);
            return off <= 4 + 0.15 * Math.Sqrt(len2);
        }
    }

    public static double LabDistance(DocColor a, DocColor b)
    {
        var la = ToLab(a.R, a.G, a.B);
        var lb = ToLab(b.R, b.G, b.B);
        return Math.Sqrt(Dist2(la, lb));
    }
}






