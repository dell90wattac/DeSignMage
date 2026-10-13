namespace DeSignMage.Imaging;

/// <summary>
/// Decoded pixels: 8-bit R, G, B, A per pixel (not premultiplied), rows top to bottom. Exact, so lossless operations
/// (rotate, flip, crop, invert) can be checked pixel by pixel.
/// </summary>
public sealed class PixelBuffer(int width, int height, byte[] rgba)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Rgba { get; } = rgba.Length == width * height * 4 ? rgba : throw new ArgumentException("Pixel data does not match the size.");

    public PixelBuffer(int width, int height) : this(width, height, new byte[checked(width * height * 4)]) { }

    public (byte R, byte G, byte B, byte A) Get(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3]);
    }

    public void Set(int x, int y, byte r, byte g, byte b, byte a = 255)
    {
        int i = (y * Width + x) * 4;
        Rgba[i] = r; Rgba[i + 1] = g; Rgba[i + 2] = b; Rgba[i + 3] = a;
    }
}

/// <summary>The lossless pixel operations behind the Image menu.</summary>
public static class PixelOps
{
    /// <summary>Rotates by 90 degrees. Clockwise: the top-left pixel ends up top-right and the width and height swap.</summary>
    public static PixelBuffer Rotate90(PixelBuffer src, bool clockwise)
    {
        int w = src.Width, h = src.Height;
        var dst = new PixelBuffer(h, w);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int dx = clockwise ? h - 1 - y : y;
                int dy = clockwise ? x : w - 1 - x;
                CopyPixel(src.Rgba, (y * w + x) * 4, dst.Rgba, (dy * h + dx) * 4);
            }
        return dst;
    }

    public static PixelBuffer Flip(PixelBuffer src, bool horizontal)
    {
        int w = src.Width, h = src.Height;
        var dst = new PixelBuffer(w, h);
        if (horizontal)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) CopyPixel(src.Rgba, (y * w + x) * 4, dst.Rgba, (y * w + (w - 1 - x)) * 4);
        }
        else
        {
            for (int y = 0; y < h; y++) Buffer.BlockCopy(src.Rgba, y * w * 4, dst.Rgba, (h - 1 - y) * w * 4, w * 4);
        }
        return dst;
    }

    /// <summary>Inverts R, G and B (a photographic negative); the alpha channel is untouched.</summary>
    public static PixelBuffer Invert(PixelBuffer src)
    {
        var dst = new PixelBuffer(src.Width, src.Height, (byte[])src.Rgba.Clone());
        var d = dst.Rgba;
        for (int i = 0; i < d.Length; i += 4)
        {
            d[i] = (byte)(255 - d[i]);
            d[i + 1] = (byte)(255 - d[i + 1]);
            d[i + 2] = (byte)(255 - d[i + 2]);
        }
        return dst;
    }

    /// <summary>The pixel rectangle (x, y, width, height) as a new picture. The rectangle is clamped to the picture.</summary>
    public static PixelBuffer Crop(PixelBuffer src, int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, src.Width - 1);
        y = Math.Clamp(y, 0, src.Height - 1);
        width = Math.Clamp(width, 1, src.Width - x);
        height = Math.Clamp(height, 1, src.Height - y);
        var dst = new PixelBuffer(width, height);
        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(src.Rgba, ((y + row) * src.Width + x) * 4, dst.Rgba, row * width * 4, width * 4);
        return dst;
    }

    private static void CopyPixel(byte[] from, int fi, byte[] to, int ti)
    {
        to[ti] = from[fi]; to[ti + 1] = from[fi + 1]; to[ti + 2] = from[fi + 2]; to[ti + 3] = from[fi + 3];
    }
}
