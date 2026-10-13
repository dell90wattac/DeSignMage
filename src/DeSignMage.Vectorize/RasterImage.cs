namespace DeSignMage.Vectorize;

/// <summary>A decoded bitmap: straight (non-premultiplied) RGBA, 4 bytes per pixel, rows top to bottom.</summary>
public sealed class RasterImage
{
    public RasterImage(int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image must not be empty.");
        if (rgba.Length != checked(width * height * 4)) throw new ArgumentException("Pixel buffer size does not match the dimensions.", nameof(rgba));
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }

    public static RasterImage Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var buf = new byte[width * height * 4];
        for (int i = 0; i < buf.Length; i += 4) { buf[i] = r; buf[i + 1] = g; buf[i + 2] = b; buf[i + 3] = a; }
        return new RasterImage(width, height, buf);
    }

    public (byte R, byte G, byte B, byte A) GetPixel(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3]);
    }

    public void SetPixel(int x, int y, byte r, byte g, byte b, byte a = 255)
    {
        int i = (y * Width + x) * 4;
        Rgba[i] = r; Rgba[i + 1] = g; Rgba[i + 2] = b; Rgba[i + 3] = a;
    }

    /// <summary>Area-average resize to the given size (box filter when shrinking, bilinear when enlarging).</summary>
    public RasterImage Resize(int newWidth, int newHeight)
    {
        if (newWidth == Width && newHeight == Height) return this;
        return newWidth < Width || newHeight < Height ? Shrink(newWidth, newHeight) : Enlarge(newWidth, newHeight);
    }

    /// <summary>Shrinks so the longest side is at most <paramref name="maxSide"/>; returns this if already small enough.</summary>
    public RasterImage LimitSize(int maxSide)
    {
        int longest = Math.Max(Width, Height);
        if (longest <= maxSide) return this;
        double s = (double)maxSide / longest;
        return Resize(Math.Max(1, (int)Math.Round(Width * s)), Math.Max(1, (int)Math.Round(Height * s)));
    }

    private RasterImage Shrink(int nw, int nh)
    {
        var dst = new byte[nw * nh * 4];
        double sx = (double)Width / nw, sy = (double)Height / nh;
        for (int y = 0; y < nh; y++)
        {
            double y0 = y * sy, y1 = (y + 1) * sy;
            int iy0 = (int)y0, iy1 = Math.Min(Height - 1, (int)Math.Ceiling(y1) - 1);
            for (int x = 0; x < nw; x++)
            {
                double x0 = x * sx, x1 = (x + 1) * sx;
                int ix0 = (int)x0, ix1 = Math.Min(Width - 1, (int)Math.Ceiling(x1) - 1);
                double sr = 0, sg = 0, sb = 0, sa = 0, sumW = 0;
                for (int py = iy0; py <= iy1; py++)
                {
                    double wy = Math.Min(y1, py + 1) - Math.Max(y0, py);
                    for (int px = ix0; px <= ix1; px++)
                    {
                        double w = wy * (Math.Min(x1, px + 1) - Math.Max(x0, px));
                        int i = (py * Width + px) * 4;
                        double wa = w * Rgba[i + 3];   // color weighted by alpha so transparent pixels don't darken edges
                        sr += Rgba[i] * wa; sg += Rgba[i + 1] * wa; sb += Rgba[i + 2] * wa;
                        sa += wa; sumW += w;
                    }
                }
                int o = (y * nw + x) * 4;
                if (sa > 0)
                {
                    dst[o] = (byte)Math.Round(sr / sa); dst[o + 1] = (byte)Math.Round(sg / sa); dst[o + 2] = (byte)Math.Round(sb / sa);
                }
                dst[o + 3] = (byte)Math.Round(sumW > 0 ? sa / sumW : 0);
            }
        }
        return new RasterImage(nw, nh, dst);
    }

    private RasterImage Enlarge(int nw, int nh)
    {
        var dst = new byte[nw * nh * 4];
        double sx = (double)Width / nw, sy = (double)Height / nh;
        for (int y = 0; y < nh; y++)
        {
            double fy = (y + 0.5) * sy - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(fy), 0, Height - 1), y1 = Math.Min(Height - 1, y0 + 1);
            double ty = Math.Clamp(fy - y0, 0, 1);
            for (int x = 0; x < nw; x++)
            {
                double fx = (x + 0.5) * sx - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(fx), 0, Width - 1), x1 = Math.Min(Width - 1, x0 + 1);
                double tx = Math.Clamp(fx - x0, 0, 1);
                int o = (y * nw + x) * 4;
                double wa00 = (1 - tx) * (1 - ty), wa10 = tx * (1 - ty), wa01 = (1 - tx) * ty, wa11 = tx * ty;
                int i00 = (y0 * Width + x0) * 4, i10 = (y0 * Width + x1) * 4, i01 = (y1 * Width + x0) * 4, i11 = (y1 * Width + x1) * 4;
                double a00 = wa00 * Rgba[i00 + 3], a10 = wa10 * Rgba[i10 + 3], a01 = wa01 * Rgba[i01 + 3], a11 = wa11 * Rgba[i11 + 3];
                double sa = a00 + a10 + a01 + a11;
                for (int c = 0; c < 3; c++)
                    dst[o + c] = sa > 0 ? (byte)Math.Round((Rgba[i00 + c] * a00 + Rgba[i10 + c] * a10 + Rgba[i01 + c] * a01 + Rgba[i11 + c] * a11) / sa) : (byte)0;
                dst[o + 3] = (byte)Math.Round(wa00 * Rgba[i00 + 3] + wa10 * Rgba[i10 + 3] + wa01 * Rgba[i01 + 3] + wa11 * Rgba[i11 + 3]);
            }
        }
        return new RasterImage(nw, nh, dst);
    }

    /// <summary>3×3 per-channel median filter: removes salt-and-pepper noise and JPEG speckle but keeps edges.</summary>
    public RasterImage Median3()
    {
        var dst = new byte[Rgba.Length];
        Span<byte> win = stackalloc byte[9];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int o = (y * Width + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = Math.Clamp(y + dy, 0, Height - 1);
                        for (int dx = -1; dx <= 1; dx++)
                            win[n++] = Rgba[(yy * Width + Math.Clamp(x + dx, 0, Width - 1)) * 4 + c];
                    }
                    win.Sort();
                    dst[o + c] = win[4];
                }
                dst[o + 3] = Rgba[o + 3];
            }
        }
        return new RasterImage(Width, Height, dst);
    }
}
