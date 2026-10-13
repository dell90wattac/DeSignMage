using System.Buffers.Binary;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using SkiaSharp;

namespace DeSignMage.Rendering.Export;

/// <summary>
/// Picture exports (PNG, JPEG): the size in pixels, drawing the plan into a bitmap and encoding it.
/// A PNG keeps the alpha channel (where nothing is drawn it has no color at all, R27); a JPEG cannot hold alpha, so it gets a white background.
/// </summary>
internal static class RasterExporter
{
    /// <summary>Pixel size from the options: typed width and/or height (the missing one follows the area's aspect ratio), or the area in inches times the dpi.</summary>
    public static (int Width, int Height) PixelSize(ExportPlan plan, ExportOptions options)
    {
        double areaW = plan.Area.Width, areaH = plan.Area.Height;
        double w, h;
        if (options.PixelWidth is not null || options.PixelHeight is not null)
        {
            if (options.PixelWidth is <= 0 || options.PixelHeight is <= 0)
                throw new ExportException("The picture size must be at least 1 pixel in each direction.");
            if (options.PixelWidth is { } pw && options.PixelHeight is { } ph) { w = pw; h = ph; }
            else if (options.PixelWidth is { } onlyW) { w = onlyW; h = Math.Round(onlyW * areaH / Math.Max(areaW, 1e-9)); }
            else { h = options.PixelHeight!.Value; w = Math.Round(h * areaW / Math.Max(areaH, 1e-9)); }
        }
        else
        {
            double dpi = options.Dpi;
            if (!(dpi > 0) || double.IsInfinity(dpi)) throw new ExportException("The resolution must be a positive number of dots per inch.");
            double scale = dpi / Units.PointsPerInch;
            w = Math.Ceiling(areaW * scale - 1e-6);
            h = Math.Ceiling(areaH * scale - 1e-6);
        }
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        if (w > DocumentExporter.MaxPixelEdge || h > DocumentExporter.MaxPixelEdge || w * h > DocumentExporter.MaxPixels)
            throw new ExportException($"A {w:0} x {h:0} pixel picture is too large. The most is {DocumentExporter.MaxPixelEdge:N0} pixels on a side and {DocumentExporter.MaxPixels / 1_000_000:N0} million in all; use a smaller size.");
        return ((int)w, (int)h);
    }

    /// <summary>Dots per inch of a picture of this width for this area (written into the file so other programs know the physical size).</summary>
    public static double DpiOf(ExportPlan plan, int pixelWidth) =>
        pixelWidth / Math.Max(plan.Area.Width / Units.PointsPerInch, 1e-9);

    public static byte[] RenderPng(Document doc, ExportOptions options, Action<SKCanvas, ExportPlan> draw)
    {
        if (options.PngCompression is < 0 or > 9) throw new ExportException("The PNG compression must be between 0 and 9.");
        var plan = ExportPlan.Create(doc, options);
        using var surface = Render(plan, options, white: options.WhiteBackground, draw);
        using var pixmap = surface.PeekPixels();
        using var stream = new SKDynamicMemoryWStream();
        var png = new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, options.PngCompression);
        if (!pixmap.Encode(stream, png)) throw new ExportException("The PNG could not be encoded.");
        using var data = stream.DetachAsData();
        return DocumentExporter.WithDpi(data.ToArray(), DpiOf(plan, pixmap.Width));
    }

    public static byte[] RenderJpeg(Document doc, ExportOptions options, Action<SKCanvas, ExportPlan> draw)
    {
        if (options.JpegQuality is < 1 or > 100) throw new ExportException("The JPEG quality must be between 1 and 100.");
        var plan = ExportPlan.Create(doc, options);
        using var surface = Render(plan, options, white: true, draw);   // JPEG has no alpha: a solid white page
        using var pixmap = surface.PeekPixels();
        using var stream = new SKDynamicMemoryWStream();
        // Sharp colored edges (sign artwork) smear with the usual 4:2:0 color reduction, so a good quality keeps full color resolution.
        var jpeg = new SKJpegEncoderOptions(options.JpegQuality, options.JpegQuality >= 85 ? SKJpegEncoderDownsample.Downsample444 : SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore);
        if (!pixmap.Encode(stream, jpeg)) throw new ExportException("The JPEG could not be encoded.");
        using var data = stream.DetachAsData();
        return WithJfifDensity(data.ToArray(), DpiOf(plan, pixmap.Width));
    }

    private static SKSurface Render(ExportPlan plan, ExportOptions options, bool white, Action<SKCanvas, ExportPlan> draw)
    {
        var (w, h) = PixelSize(plan, options);
        var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new ExportException("The picture could not be created (not enough memory?).");
        var canvas = surface.Canvas;
        canvas.Clear(white ? SKColors.White : SKColors.Transparent);
        canvas.Scale((float)(w / Math.Max(plan.Area.Width, 1e-9)), (float)(h / Math.Max(plan.Area.Height, 1e-9)));
        canvas.Translate((float)-plan.Area.Left, (float)-plan.Area.Top);
        draw(canvas, plan);
        return surface;
    }

    /// <summary>Sets the density in the JFIF header (dots per inch) so the picture opens at its physical size.</summary>
    internal static byte[] WithJfifDensity(byte[] jpeg, double dpi)
    {
        // SOI (2 bytes), then APP0 marker FFE0, length (2), "JFIF\0", version (2), units (1), Xdensity (2), Ydensity (2)
        if (jpeg.Length < 20 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 || jpeg[2] != 0xFF || jpeg[3] != 0xE0) return jpeg;
        if (jpeg[6] != (byte)'J' || jpeg[7] != (byte)'F' || jpeg[8] != (byte)'I' || jpeg[9] != (byte)'F' || jpeg[10] != 0) return jpeg;
        ushort d = (ushort)Math.Clamp(Math.Round(dpi), 1, 65535);
        jpeg[13] = 1;   // dots per inch
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(14), d);
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(16), d);
        return jpeg;
    }
}
