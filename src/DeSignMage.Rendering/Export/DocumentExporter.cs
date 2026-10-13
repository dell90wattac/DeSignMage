using System.Buffers.Binary;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using SkiaSharp;

namespace DeSignMage.Rendering.Export;

/// <summary>
/// File â–¸ Export: SVG (<see cref="SvgExporter"/>), PDF (vector, Skia's PDF backend; live text is written as curves) and PNG
/// (any DPI, optional transparent background). The same <see cref="ExportPlan"/> decides what is drawn: invisible and
/// non-printable layers, guides and (by default) pasteboard objects are left out.
/// </summary>
public static class DocumentExporter
{
    /// <summary>Largest PNG edge in pixels and the largest total (Skia's raster limits are lower in practice; this keeps a typo from eating all memory).</summary>
    public const int MaxPixelEdge = 30000;
    public const long MaxPixels = 250_000_000;

    public static ExportFormat? FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".svg" => ExportFormat.Svg,
        ".pdf" => ExportFormat.Pdf,
        ".png" => ExportFormat.Png,
        _ => null,
    };

    public static byte[] Render(Document doc, ExportFormat format, ExportOptions options) => format switch
    {
        ExportFormat.Svg => SvgExporter.RenderBytes(doc, options),
        ExportFormat.Pdf => RenderPdf(doc, options),
        ExportFormat.Png => RenderPng(doc, options),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Writes the export to <paramref name="path"/> (the format comes from the extension).</summary>
    public static void Export(Document doc, string path, ExportOptions options)
    {
        var format = FormatOf(path) ?? throw new ExportException($"Cannot export to '{Path.GetExtension(path)}' files. Use .svg, .pdf or .png.");
        File.WriteAllBytes(path, Render(doc, format, options));
    }

    // ---------------------------------------------------------------- PDF

    public static byte[] RenderPdf(Document doc, ExportOptions options)
    {
        var plan = ExportPlan.Create(doc, options);
        using var ms = new MemoryStream();
        var metadata = new SKDocumentPdfMetadata
        {
            Title = options.Title ?? "DeSign Mage export",
            Creator = "DeSign Mage",
            Producer = "DeSign Mage (Skia/PDF)",
            RasterDpi = 300,
        };
        using (var pdf = SKDocument.CreatePdf(ms, metadata))
        {
            using (var canvas = pdf.BeginPage((float)plan.Area.Width, (float)plan.Area.Height))
            {
                canvas.Translate((float)-plan.Area.Left, (float)-plan.Area.Top);
                Draw(canvas, doc, plan, featherDpi: 300);   // a PDF cannot multiply by a mask: a feathered object goes in as a 300 dpi picture
            }
            pdf.Close();
        }
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- PNG

    /// <summary>Pixel size of the PNG for these options (page inches Ã— DPI, rounded up).</summary>
    public static (int Width, int Height) PngSize(Document doc, ExportOptions options) => PngSize(ExportPlan.Create(doc, options), options.Dpi);

    private static (int Width, int Height) PngSize(ExportPlan plan, double dpi)
    {
        if (!(dpi > 0) || double.IsInfinity(dpi)) throw new ExportException("The resolution must be a positive number of dots per inch.");
        double scale = dpi / Units.PointsPerInch;
        double w = Math.Ceiling(plan.Area.Width * scale - 1e-6), h = Math.Ceiling(plan.Area.Height * scale - 1e-6);
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        if (w > MaxPixelEdge || h > MaxPixelEdge || w * h > MaxPixels)
            throw new ExportException($"A {w:0} x {h:0} pixel image is too large. Use a lower resolution.");
        return ((int)w, (int)h);
    }

    public static byte[] RenderPng(Document doc, ExportOptions options)
    {
        var plan = ExportPlan.Create(doc, options);
        var (w, h) = PngSize(plan, options.Dpi);
        double scale = options.Dpi / Units.PointsPerInch;

        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new ExportException("The image could not be created (not enough memory?).");
        var canvas = surface.Canvas;
        canvas.Clear(options.TransparentBackground ? SKColors.Transparent : SKColors.White);
        canvas.Scale((float)scale);
        canvas.Translate((float)-plan.Area.Left, (float)-plan.Area.Top);
        Draw(canvas, doc, plan);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return WithDpi(data.ToArray(), options.Dpi);
    }

    // ---------------------------------------------------------------- shared

    private static void Draw(SKCanvas canvas, Document doc, ExportPlan plan, double featherDpi = 0)
    {
        using var renderer = new SceneRenderer(doc) { FeatherPixelDpi = featherDpi };
        canvas.ClipRect(plan.Area.ToSK());
        foreach (var layer in plan.Layers)
            foreach (var o in layer.Objects)
                renderer.DrawObject(canvas, o, Affine2.Identity, null, cache: false);
    }

    /// <summary>Inserts a pHYs chunk so other programs know the physical size (Skia writes none).</summary>
    internal static byte[] WithDpi(byte[] png, double dpi)
    {
        const int afterIhdr = 8 + 25;   // signature + IHDR chunk (length, type, 13 data bytes, crc)
        if (png.Length < afterIhdr) return png;
        uint perMeter = (uint)Math.Round(dpi / 0.0254);
        var chunk = new byte[4 + 4 + 9 + 4];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, 9);
        "pHYs"u8.CopyTo(chunk.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8), perMeter);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12), perMeter);
        chunk[16] = 1;   // unit: metre
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(17), Crc32(chunk.AsSpan(4, 4 + 9)));

        var result = new byte[png.Length + chunk.Length];
        png.AsSpan(0, afterIhdr).CopyTo(result);
        chunk.CopyTo(result, afterIhdr);
        png.AsSpan(afterIhdr).CopyTo(result.AsSpan(afterIhdr + chunk.Length));
        return result;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

