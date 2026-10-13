using System.Runtime.InteropServices;
using SkiaSharp;

namespace DeSignMage.Imaging;

/// <summary>Changing a picture's pixel count with Skia's best filters.</summary>
public static class PixelResampler
{
    /// <summary>
    /// A copy of <paramref name="src"/> with <paramref name="width"/> x <paramref name="height"/> pixels. Making it smaller averages with
    /// a mipmapped (trilinear) filter, which keeps fine detail from turning into jagged lines; making it larger uses a bicubic filter
    /// (Mitchell), which is smooth without much blur. Color is blended with alpha taken into account, so the edge of a cut-out
    /// picture does not get a dark or light fringe. The same size gives an exact copy.
    /// </summary>
    public static PixelBuffer Resample(PixelBuffer src, int width, int height)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "A picture needs at least one pixel each way.");
        if (width == src.Width && height == src.Height) return new PixelBuffer(width, height, (byte[])src.Rgba.Clone());

        // Filter premultiplied pixels: filtering plain color next to a transparent pixel would blend in its (black) color and darken the edge.
        var premul = Premultiplied(src.Rgba);
        var srcInfo = new SKImageInfo(src.Width, src.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var handle = GCHandle.Alloc(premul, GCHandleType.Pinned);
        SKImage? image;
        try
        {
            using var pixmap = new SKPixmap(srcInfo, handle.AddrOfPinnedObject(), srcInfo.RowBytes);
            image = SKImage.FromPixelCopy(pixmap);
        }
        finally { handle.Free(); }
        if (image is null) throw new InvalidOperationException("The picture could not be prepared for resampling.");

        using (image)
        {
            var dstInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(dstInfo) ?? throw new OutOfMemoryException("The resampled picture is too large.");
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            bool smaller = (long)width * height < (long)src.Width * src.Height;
            var sampling = smaller
                ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
                : new SKSamplingOptions(SKCubicResampler.Mitchell);
            canvas.DrawImage(image, SKRect.Create(width, height), sampling);

            var result = new PixelBuffer(width, height);
            var outInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var outHandle = GCHandle.Alloc(result.Rgba, GCHandleType.Pinned);
            try
            {
                using var snapshot = surface.Snapshot();
                if (!snapshot.ReadPixels(outInfo, outHandle.AddrOfPinnedObject(), outInfo.RowBytes))
                    throw new InvalidOperationException("The resampled picture could not be read back.");
            }
            finally { outHandle.Free(); }
            return result;
        }
    }

    private static byte[] Premultiplied(byte[] rgba)
    {
        var p = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            if (a == 255) { p[i] = rgba[i]; p[i + 1] = rgba[i + 1]; p[i + 2] = rgba[i + 2]; p[i + 3] = 255; continue; }
            p[i] = (byte)((rgba[i] * a + 127) / 255);
            p[i + 1] = (byte)((rgba[i + 1] * a + 127) / 255);
            p[i + 2] = (byte)((rgba[i + 2] * a + 127) / 255);
            p[i + 3] = (byte)a;
        }
        return p;
    }
}
