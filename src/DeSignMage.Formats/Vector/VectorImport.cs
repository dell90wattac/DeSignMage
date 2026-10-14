using System.Text;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Vector.Eps;
using DeSignMage.Formats.Vector.Pdf;
using DeSignMage.Formats.Vector.Svg;

namespace DeSignMage.Formats.Vector;

/// <summary>The vector file formats <see cref="VectorImport"/> reads.</summary>
public enum VectorFormat
{
    Pdf,

    /// <summary>A PDF-compatible Illustrator file (read through the PDF reader), or a legacy PostScript one (read as EPS).</summary>
    Ai,

    Eps,
    Svg,
}

/// <summary>The file cannot be read at all (not a file of its format, damaged, locked with a password). The message is fit to show the user.</summary>
public sealed class VectorImportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Settings of <see cref="VectorImport"/>. Every property has a working default.</summary>
public sealed record VectorImportOptions
{
    /// <summary>Which page of a PDF / AI file to read, counting from 0; clamped to the pages there are. Other formats have one page.</summary>
    public int PageIndex { get; init; }

    /// <summary>Keep text as <see cref="TextObject"/> when its font is an installed font; false (or any other font) gives outlines.</summary>
    public bool LiveText { get; init; } = true;

    /// <summary>
    /// Clips shapes to their clipping path when given (a path boolean). Without it clipping is ignored, except for clips that do not touch the shape,
    /// and a warning says how many shapes were not clipped.
    /// </summary>
    public IBooleanService? BooleanService { get; init; }
}

/// <summary>What <see cref="VectorImport"/> read. The document's page is the source page in points; objects are in document points (y down).</summary>
public sealed class VectorImportResult(Document document, VectorFormat format, int pageCount, int pageIndex, IReadOnlyList<string> warnings)
{
    public Document Document { get; } = document;
    public VectorFormat Format { get; } = format;

    /// <summary>Pages in the file (1 for SVG and EPS).</summary>
    public int PageCount { get; } = pageCount;

    /// <summary>The page that was read (counting from 0).</summary>
    public int PageIndex { get; } = pageIndex;

    /// <summary>What was simplified or left out; shown to the user, never fatal.</summary>
    public IReadOnlyList<string> Warnings { get; } = warnings;

    /// <summary>Objects (not groups) in the document.</summary>
    public int ObjectCount => Document.Layers.Sum(l => l.Children.Count);
}

/// <summary>
/// Reads other programs' vector files into a <see cref="Document"/>: PDF (paths, colors incl. CMYK and spot colors, gradients, text, pictures,
/// layers), Illustrator files (PDF-compatible ones through the PDF reader), SVG (shapes, paths, groups, transforms, styles, gradients, text,
/// pictures) and EPS (the path-and-color subset of PostScript that drawing programs write; an embedded PDF is read as a PDF). Groups follow the
/// source (PDF form XObjects, SVG groups) as membership over the flat drawing order; stacking order is kept; units are points.
/// </summary>
public static class VectorImport
{
    public static IReadOnlyList<string> Extensions { get; } = [".pdf", ".ai", ".eps", ".svg"];

    /// <summary>True for a file name with one of <see cref="Extensions"/> (the cheap test for open / drop routing).</summary>
    public static bool CanRead(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The format of a file: by its extension, checked against the first bytes (a ".ai" that starts with "%PDF" is PDF-compatible).</summary>
    public static VectorFormat? Detect(string path)
    {
        if (!CanRead(path)) return null;
        try
        {
            using var s = File.OpenRead(path);
            var head = new byte[1024];
            int n = s.Read(head, 0, head.Length);
            return Detect(head.AsSpan(0, n), Path.GetExtension(path));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static VectorFormat? Detect(ReadOnlySpan<byte> head, string extension)
    {
        string ext = extension.ToLowerInvariant();
        bool pdfMagic = Find(head, "%PDF-"u8) is >= 0 and < 1024;
        bool psMagic = head.Length >= 4 && ((head[0] == '%' && head[1] == '!') || (head[0] == 0xC5 && head[1] == 0xD0 && head[2] == 0xD3 && head[3] == 0xC6));
        if (pdfMagic) return ext == ".ai" ? VectorFormat.Ai : VectorFormat.Pdf;
        if (psMagic) return ext == ".ai" ? VectorFormat.Ai : VectorFormat.Eps;
        if (ext == ".svg" || ext == ".svgz") return VectorFormat.Svg;
        string text = Encoding.UTF8.GetString(head);
        if (text.Contains("<svg", StringComparison.OrdinalIgnoreCase)) return VectorFormat.Svg;
        return ext switch { ".pdf" => VectorFormat.Pdf, ".ai" => VectorFormat.Ai, ".eps" => VectorFormat.Eps, _ => null };
    }

    private static int Find(ReadOnlySpan<byte> data, ReadOnlySpan<byte> what) => data.IndexOf(what);

    /// <summary>The number of pages: PDF and AI files can have several, everything else has 1. A file that cannot be read has 1.</summary>
    public static int GetPageCount(string path)
    {
        try
        {
            if (Detect(path) is VectorFormat.Pdf or VectorFormat.Ai)
            {
                var bytes = File.ReadAllBytes(path);
                if (PdfImporter.LooksLikePdf(bytes)) return Math.Max(1, PdfImporter.PageCount(bytes));
            }
        }
        catch (Exception) { /* a file that cannot be read has one page as far as the picker is concerned */ }
        return 1;
    }

    public static VectorImportResult Read(string path, VectorImportOptions? options = null)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new VectorImportException($"The file '{Path.GetFileName(path)}' could not be read: {e.Message}", e);
        }
        var format = Detect(bytes.AsSpan(0, Math.Min(bytes.Length, 1024)), Path.GetExtension(path))
            ?? throw new VectorImportException($"'{Path.GetFileName(path)}' is not a PDF, Illustrator, EPS or SVG file.");
        return Read(bytes, format, options, Path.GetDirectoryName(Path.GetFullPath(path)));
    }

    public static VectorImportResult Read(Stream stream, VectorFormat format, VectorImportOptions? options = null)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Read(ms.ToArray(), format, options, null);
    }

    private static VectorImportResult Read(byte[] bytes, VectorFormat declared, VectorImportOptions? options, string? baseDirectory)
    {
        options ??= new VectorImportOptions();
        var warnings = new List<string>();
        try
        {
            switch (declared)
            {
                case VectorFormat.Svg:
                    return SvgImporter.Read(bytes, options, baseDirectory);
                case VectorFormat.Pdf:
                case VectorFormat.Ai:
                case VectorFormat.Eps:
                    if (PdfImporter.LooksLikePdf(bytes))
                        return PdfImporter.Read(bytes, declared == VectorFormat.Eps ? VectorFormat.Eps : declared, options);
                    if (declared == VectorFormat.Pdf)
                        throw new VectorImportException("This is not a PDF file (it does not start with %PDF).");
                    return EpsImporter.Read(bytes, declared, options);
                default:
                    throw new VectorImportException("Unknown vector format.");
            }
        }
        catch (VectorImportException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            warnings.Add(e.Message);
            throw new VectorImportException($"The {declared.ToString().ToUpperInvariant()} file could not be read: {e.Message}", e);
        }
    }
}
