using System.Runtime.InteropServices;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using SkiaSharp;

namespace DeSignMage.Vectorize.Imaging;

/// <summary>SkiaSharp helpers: decode image files into <see cref="RasterImage"/>, and draw traced results (for the dialog preview and sample renders).</summary>
public static class SkiaImaging
{
    public static RasterImage Decode(string path) => Decode(File.ReadAllBytes(path));

    public static RasterImage Decode(byte[] encoded)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(encoded)) ?? throw new InvalidDataException("Not a supported image file.");
        using var bmp = SKBitmap.Decode(codec) ?? throw new InvalidDataException("The image could not be decoded.");
        using var oriented = ApplyOrigin(bmp, codec.EncodedOrigin);
        return ToRaster(oriented);
    }

    public static RasterImage ToRaster(SKBitmap bmp)
    {
        var info = new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var buf = new byte[bmp.Width * bmp.Height * 4];
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            using var img = SKImage.FromBitmap(bmp);
            if (!img.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes))
                throw new InvalidDataException("The image pixels could not be read.");
        }
        finally { handle.Free(); }
        return new RasterImage(bmp.Width, bmp.Height, buf);
    }

    /// <summary>Draws the image as an SKBitmap (premultiplied, BGRA) so it can be shown or saved.</summary>
    public static SKBitmap ToBitmap(RasterImage img)
    {
        var info = new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var src = new SKBitmap(info);
        Marshal.Copy(img.Rgba, 0, src.GetPixels(), img.Rgba.Length);
        var dst = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }

    private static SKBitmap ApplyOrigin(SKBitmap bmp, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft || origin == SKEncodedOrigin.Default) return bmp.Copy();
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = swap ? bmp.Height : bmp.Width, h = swap ? bmp.Width : bmp.Height;
        var result = new SKBitmap(new SKImageInfo(w, h, bmp.ColorType, bmp.AlphaType));
        using var c = new SKCanvas(result);
        c.Clear(SKColors.Transparent);
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, bmp.Width, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, bmp.Width, 0, -1, bmp.Height, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, bmp.Height, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, bmp.Height, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, bmp.Height, -1, 0, bmp.Width, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, bmp.Width, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        c.SetMatrix(m);
        c.DrawBitmap(bmp, 0, 0);
        return result;
    }

    public static SKPath ToSkPath(PathGeometry g, double scale = 1)
    {
        g = g.WithArcsAsCubics();
        var path = new SKPath { FillType = g.FillRule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding };
        var pts = g.Points;
        int pi = 0;
        SKPoint P(Vec2 v) => new((float)(v.X * scale), (float)(v.Y * scale));
        foreach (var verb in g.Verbs)
        {
            switch (verb)
            {
                case PathVerb.MoveTo: path.MoveTo(P(pts[pi++])); break;
                case PathVerb.LineTo: path.LineTo(P(pts[pi++])); break;
                case PathVerb.CubicTo: path.CubicTo(P(pts[pi]), P(pts[pi + 1]), P(pts[pi + 2])); pi += 3; break;
                case PathVerb.Close: path.Close(); break;
            }
        }
        return path;
    }

    /// <summary>Draws a traced result at <paramref name="scale"/> pixels per source pixel on the given background.</summary>
    public static SKBitmap Render(VectorizeResult r, double scale, SKColor background)
    {
        int w = Math.Max(1, (int)Math.Round(r.Width * scale)), h = Math.Max(1, (int)Math.Round(r.Height * scale));
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(background);
        Draw(canvas, r, scale);
        return bmp;
    }

    public static void Draw(SKCanvas canvas, VectorizeResult r, double scale)
    {
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)Math.Max(0.5, r.StrokeWidth * scale), StrokeJoin = SKStrokeJoin.Round };
        foreach (var s in r.Shapes)
        {
            using var path = ToSkPath(s.Path, scale);
            if (s.Fill is { } f) { fill.Color = new SKColor(f.R, f.G, f.B, f.A); canvas.DrawPath(path, fill); }
            if (s.Stroke is { } st) { stroke.Color = new SKColor(st.R, st.G, st.B, st.A); canvas.DrawPath(path, stroke); }
        }
    }

    public static byte[] EncodePng(SKBitmap bmp)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
