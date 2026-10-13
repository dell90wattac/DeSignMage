using System.Buffers.Binary;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using SkiaSharp;

namespace DeSignMage.Rendering.Export;

/// <summary>
/// File â–¸ Export: SVG (<see cref="SvgExporter"/>), PDF (vector, Skia's PDF backend; live text is written as curves) and PNG
/// (any DPI, always with a transparent background: the page has no color of its own, and neither the page color nor the Show Transparency checkerboard is ever drawn). The same <see cref="ExportPlan"/> decides what is drawn: invisible and
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
        ".jpg" or ".jpeg" => ExportFormat.Jpeg,
        ".eps" => ExportFormat.Eps,
        ".ai" => ExportFormat.Ai,
        _ => null,
    };

    public static byte[] Render(Document doc, ExportFormat format, ExportOptions options) => format switch
    {
        ExportFormat.Svg => SvgExporter.RenderBytes(doc, options),
        ExportFormat.Pdf => RenderPdf(doc, options),
        ExportFormat.Png => RenderPng(doc, options),
        ExportFormat.Jpeg => RenderJpeg(doc, options),
        ExportFormat.Eps => EpsExporter.RenderBytes(doc, options),
        ExportFormat.Ai => RenderAi(doc, options),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Writes the export to <paramref name="path"/> (the format comes from the extension).</summary>
    public static void Export(Document doc, string path, ExportOptions options)
    {
        var format = FormatOf(path) ?? throw new ExportException($"Cannot export to '{Path.GetExtension(path)}' files. Use .png, .jpg, .pdf, .svg, .eps or .ai.");
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

    /// <summary>
    /// Adobe Illustrator file. Modern Illustrator files are PDF files that carry Illustrator's private data as extra; Illustrator (and the
    /// other programs that open .ai: Inkscape, Affinity, CorelDRAW) open any PDF-compatible .ai and read its vector content. So the file is the
    /// PDF export (one page, the size of the export area, vectors, curves for text) under the .ai name. No Illustrator-private data is written, so there are no
    /// editable Illustrator layers or artboards: everything arrives as one page of paths, which is what a hand-over file needs.
    /// </summary>
    public static byte[] RenderAi(Document doc, ExportOptions options) => RenderPdf(doc, options);

    // ---------------------------------------------------------------- pictures

    /// <summary>Pixel size of a PNG or JPEG for these options (typed size, or page inches × dpi), checked against the limits.</summary>
    public static (int Width, int Height) PngSize(Document doc, ExportOptions options) => RasterExporter.PixelSize(ExportPlan.Create(doc, options), options);

    public static byte[] RenderPng(Document doc, ExportOptions options) => RasterExporter.RenderPng(doc, options, (canvas, plan) => Draw(canvas, doc, plan));

    public static byte[] RenderJpeg(Document doc, ExportOptions options) => RasterExporter.RenderJpeg(doc, options, (canvas, plan) => Draw(canvas, doc, plan));
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

