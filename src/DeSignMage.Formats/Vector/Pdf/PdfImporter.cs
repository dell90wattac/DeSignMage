using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;

namespace DeSignMage.Formats.Vector.Pdf;

/// <summary>
/// Reads a page of a PDF (or of a PDF-compatible Illustrator file). The structure, the filters and the fonts come from PdfPig; the drawing
/// operators are run by <see cref="PdfInterpreter"/> so that the stacking order of paths, text and pictures is kept.
/// </summary>
internal static class PdfImporter
{
    public static bool LooksLikePdf(byte[] bytes) => bytes.AsSpan(0, Math.Min(bytes.Length, 1024)).IndexOf("%PDF-"u8) >= 0;

    private static ParsingOptions Options => new() { UseLenientParsing = true, SkipMissingFonts = true };

    public static int PageCount(byte[] bytes)
    {
        using var pdf = Open(bytes);
        return pdf.NumberOfPages;
    }

    private static PdfDocument Open(byte[] bytes)
    {
        try { return PdfDocument.Open(bytes, Options); }
        catch (Exception e) when (e.GetType().Name.Contains("Encrypt", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new VectorImportException("This PDF is locked with a password and cannot be read.", e);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new VectorImportException($"This PDF could not be read: {e.Message}", e);
        }
    }

    public static VectorImportResult Read(byte[] bytes, VectorFormat format, VectorImportOptions options)
    {
        using var pdfDoc = Open(bytes);
        using var file = new PdfFile(pdfDoc);
        int pageCount = pdfDoc.NumberOfPages;
        if (pageCount == 0) throw new VectorImportException("This PDF has no pages.");
        int index = Math.Clamp(options.PageIndex, 0, pageCount - 1);

        Page page;
        try { page = pdfDoc.GetPage(index + 1); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new VectorImportException($"Page {index + 1} of this PDF could not be read: {e.Message}", e);
        }

        // the visible box and where its corners go: document points, y down, a turned page turned back
        var box = page.CropBox.Bounds;
        double left = Math.Min(box.Left, box.Right), right = Math.Max(box.Left, box.Right), bottom = Math.Min(box.Bottom, box.Top), top = Math.Max(box.Bottom, box.Top);
        double w = right - left, h = top - bottom;
        int rotation = ((page.Rotation.Value % 360) + 360) % 360;
        var pageBase = rotation switch
        {
            90 => new Affine2(0, 1, 1, 0, -bottom, -left),
            180 => new Affine2(-1, 0, 0, 1, right, -bottom),
            270 => new Affine2(0, -1, -1, 0, top, right),
            _ => new Affine2(1, 0, 0, -1, -left, top),
        };
        var setup = rotation is 90 or 270 ? new PageSetup(h, w) : new PageSetup(w, h);

        var builder = new VectorBuilder(setup, options);
        var hidden = HiddenGroups(file);
        var interpreter = new PdfInterpreter(file, builder, options, pageBase, page.Letters, hidden);

        var resources = InheritedResources(file, page.Dictionary);
        var content = PageContent(file, page.Dictionary);
        if (content.All(b => b is 0 or 9 or 10 or 12 or 13 or 32))
        {
            builder.Warn(format == VectorFormat.Ai
                ? "This Illustrator file has no PDF-compatible drawing in it (it was saved without \"Create PDF Compatible File\"), so nothing could be read."
                : "This page has no drawing on it.");
        }
        else interpreter.RunPage(resources, content);

        var document = builder.Build();
        var warnings = builder.Warnings.ToList();
        if (document.Layers.All(l => l.Children.Count == 0) && IllustratorScript(file, page.Dictionary) is { } script)
        {
            // an Illustrator file saved without "Create PDF Compatible File": the page is empty and the art is in the document data
            var fromData = Eps.EpsImporter.RunScript(script.Bytes, script.Header, privateData: true, aiMode: true, useArtboard: true, format, options);
            if (fromData.ObjectCount > 0)
            {
                warnings = ["The PDF part of this Illustrator file is empty (it was saved without PDF compatibility); the drawing was read from the Illustrator data in it.", .. fromData.Warnings];
                return new VectorImportResult(fromData.Document, format, pageCount, index, warnings);
            }
        }
        if (page.GetAnnotations().Any(a => a.Type != UglyToad.PdfPig.Annotations.AnnotationType.Link))
            warnings.Add("Annotations and form fields of the PDF are not read.");
        if (format == VectorFormat.Ai && document.Layers.All(l => l.Children.Count == 0) && !warnings.Any(w => w.Contains("PDF")))
            warnings.Add("This Illustrator file has no PDF-compatible drawing in it (it was saved without \"Create PDF Compatible File\"), so nothing could be read.");
        return new VectorImportResult(document, format, pageCount, index, warnings);
    }

    /// <summary>The document data an Illustrator file keeps next to its PDF page (<c>PieceInfo/Illustrator/Private</c>): a plain header, then deflated text in blocks.</summary>
    private static (byte[] Bytes, string Header)? IllustratorScript(PdfFile file, DictionaryToken page)
    {
        var priv = file.GetDict(file.GetDict(file.GetDict(page, "PieceInfo"), "Illustrator"), "Private");
        if (priv is null) return null;
        int blocks = (int)file.GetNumber(priv, "NumBlock", 0);
        if (blocks < 2 || blocks > 10_000) return null;
        var parts = new List<byte[]>();
        for (int i = 1; i <= blocks; i++)
        {
            if (file.Get(priv, "AIPrivateData" + i) is not StreamToken s) return null;
            try { parts.Add(file.Decode(s, out _)); }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
        }
        string header = System.Text.Encoding.Latin1.GetString(parts[0]);
        using var rest = new MemoryStream();
        for (int i = 1; i < parts.Count; i++) rest.Write(parts[i]);
        var data = rest.ToArray();
        const string marker = "%AI12_CompressedData";
        int at = data.AsSpan(0, Math.Min(data.Length, 100)).IndexOf(System.Text.Encoding.ASCII.GetBytes(marker));
        byte[] text;
        if (at >= 0)
        {
            using var output = new MemoryStream();
            try
            {
                using var z = new System.IO.Compression.ZLibStream(new MemoryStream(data, at + marker.Length, data.Length - at - marker.Length), System.IO.Compression.CompressionMode.Decompress);
                z.CopyTo(output);
            }
            catch (InvalidDataException) { /* what could be read is used */ }
            text = output.ToArray();
        }
        else text = data;
        if (text.Length == 0) return null;
        var all = new byte[parts[0].Length + 1 + text.Length];
        parts[0].CopyTo(all, 0);
        all[parts[0].Length] = (byte)'\n';
        text.CopyTo(all, parts[0].Length + 1);
        return (all, header);
    }
    // ---------------------------------------------------------------- page parts

    private static DictionaryToken? InheritedResources(PdfFile file, DictionaryToken page)
    {
        var d = page;
        for (int i = 0; i < 32 && d is not null; i++)
        {
            if (file.GetDict(d, "Resources") is { } r) return r;
            d = file.GetDict(d, "Parent");
        }
        return null;
    }

    private static byte[] PageContent(PdfFile file, DictionaryToken page)
    {
        var contents = file.Get(page, "Contents");
        var streams = new List<StreamToken>();
        if (contents is StreamToken one) streams.Add(one);
        else if (contents is ArrayToken arr)
            foreach (var item in arr.Data) if (file.Stream(item) is { } s) streams.Add(s);
        using var ms = new MemoryStream();
        foreach (var s in streams)
        {
            try { ms.Write(file.Decode(s, out _)); ms.WriteByte((byte)'\n'); }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or NotSupportedException) { }
        }
        return ms.ToArray();
    }

    /// <summary>The object numbers of the optional content groups (layers) that are off when the file opens.</summary>
    private static HashSet<long> HiddenGroups(PdfFile file)
    {
        var hidden = new HashSet<long>();
        var catalog = file.Document.Structure.Catalog.CatalogDictionary;
        var props = file.GetDict(catalog, "OCProperties");
        var d = file.GetDict(props, "D");
        if (props is null || d is null) return hidden;
        if (file.GetName(d, "BaseState") == "OFF" && file.Array(file.Get(props, "OCGs")) is { } all)
            foreach (var g in all.Data.OfType<IndirectReferenceToken>()) hidden.Add(g.Data.ObjectNumber);
        if (file.Array(file.Get(d, "OFF")) is { } off)
            foreach (var g in off.Data.OfType<IndirectReferenceToken>()) hidden.Add(g.Data.ObjectNumber);
        if (file.Array(file.Get(d, "ON")) is { } on)
            foreach (var g in on.Data.OfType<IndirectReferenceToken>()) hidden.Remove(g.Data.ObjectNumber);
        return hidden;
    }
}
