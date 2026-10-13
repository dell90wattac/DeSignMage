using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Vectorize;

namespace DeSignMage.Vectorize.Tests;

/// <summary>Synthetic bitmaps and a tiny software rasterizer so tests can compare traced output to the source.</summary>
internal static class TestImages
{
    public static readonly (byte R, byte G, byte B) Red = (220, 30, 30), Blue = (30, 60, 200), Green = (30, 160, 60), Black = (0, 0, 0), White = (255, 255, 255);

    public static RasterImage Canvas(int w, int h, (byte R, byte G, byte B) bg) => RasterImage.Solid(w, h, bg.R, bg.G, bg.B);

    public static void FillRect(RasterImage img, int x0, int y0, int x1, int y1, (byte R, byte G, byte B) c)
    {
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) img.SetPixel(x, y, c.R, c.G, c.B);
    }

    /// <summary>Anti-aliased disc (4x4 supersampling) blended over what is already there.</summary>
    public static void FillCircle(RasterImage img, double cx, double cy, double r, (byte R, byte G, byte B) c, double innerR = 0)
    {
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                int hit = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                    {
                        double dx = x + (sx + 0.5) / 4 - cx, dy = y + (sy + 0.5) / 4 - cy, d2 = dx * dx + dy * dy;
                        if (d2 <= r * r && d2 >= innerR * innerR) hit++;
                    }
                if (hit == 0) continue;
                var (pr, pg, pb, _) = img.GetPixel(x, y);
                double a = hit / 16.0;
                img.SetPixel(x, y, (byte)Math.Round(pr + (c.R - pr) * a), (byte)Math.Round(pg + (c.G - pg) * a), (byte)Math.Round(pb + (c.B - pb) * a));
            }
        }
    }

    /// <summary>Red square and blue disc on white, 96x96.</summary>
    public static RasterImage SquareAndDisc()
    {
        var img = Canvas(96, 96, White);
        FillRect(img, 12, 12, 44, 44, Red);
        FillCircle(img, 66, 62, 20, Blue);
        return img;
    }

    /// <summary>Paints the result back with the painter's algorithm (even-odd), sampling pixel centers; null where nothing covers.</summary>
    public static (byte R, byte G, byte B)?[,] Rasterize(VectorizeResult r)
    {
        var grid = new (byte, byte, byte)?[r.Width, r.Height];
        foreach (var s in r.Shapes)
        {
            if (s.Fill is not { } f) continue;
            var b = s.Path.GetBounds();
            int x0 = Math.Max(0, (int)Math.Floor(b.Left)), x1 = Math.Min(r.Width - 1, (int)Math.Ceiling(b.Right));
            int y0 = Math.Max(0, (int)Math.Floor(b.Top)), y1 = Math.Min(r.Height - 1, (int)Math.Ceiling(b.Bottom));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (s.Path.Contains(new Vec2(x + 0.5, y + 0.5), 0.05)) grid[x, y] = (f.R, f.G, f.B);
        }
        return grid;
    }

    public static double Distance((byte R, byte G, byte B) a, (byte R, byte G, byte B) b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    /// <summary>Fraction of source pixels (excluding a 2 px band around color edges) whose traced color is within <paramref name="tolerance"/>.</summary>
    public static (double Match, int Uncovered) Compare(RasterImage src, VectorizeResult r, double tolerance = 40)
    {
        var grid = Rasterize(r);
        int total = 0, ok = 0, uncovered = 0;
        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                if (NearEdge(src, x, y)) continue;
                total++;
                var (sr, sg, sb, _) = src.GetPixel(x, y);
                if (grid[x, y] is not { } t) { uncovered++; continue; }
                if (Distance((sr, sg, sb), t) <= tolerance) ok++;
            }
        }
        return ((double)ok / Math.Max(1, total), uncovered);
    }

    private static bool NearEdge(RasterImage img, int x, int y)
    {
        var (r, g, b, _) = img.GetPixel(x, y);
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= img.Width || yy >= img.Height) continue;
                var (r2, g2, b2, _) = img.GetPixel(xx, yy);
                if (Distance((r, g, b), (r2, g2, b2)) > 30) return true;
            }
        return false;
    }

    /// <summary>Default options for the small test pictures: no enlargement, no speckle merging unless a test asks.</summary>
    public static VectorizeOptions Small(VectorizeOptions? o = null) => (o ?? new VectorizeOptions()) with { MinWorkingSize = 0 };

    /// <summary>Counts subpaths (MoveTo verbs).</summary>
    public static int Subpaths(PathGeometry g) => g.Verbs.Count(v => v == PathVerb.MoveTo);
}
