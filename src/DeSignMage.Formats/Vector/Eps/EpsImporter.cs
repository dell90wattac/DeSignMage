using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Vector.Pdf;

namespace DeSignMage.Formats.Vector.Eps;

/// <summary>
/// Reads Encapsulated PostScript and the PostScript-based Illustrator files (the older ".ai" format): paths, colors (RGB, CMYK, gray, custom/spot colors),
/// groups, compound paths, clipping, layers, dashes' presence, gradients through <c>shfill</c>, pictures through <c>image</c>, and plain <c>show</c> text. A PDF inside the file
/// is read as a PDF; the editable data an Illustrator EPS carries (the document itself, compressed) is read in preference to the PostScript that draws it.
/// A file that needs a real PostScript interpreter (its own prolog does the drawing) is not drawn; the warning says so.
/// </summary>
internal static partial class EpsImporter
{
    public static VectorImportResult Read(byte[] bytes, VectorFormat format, VectorImportOptions options)
    {
        // the binary header some programs put in front (preview pictures follow the PostScript)
        int start = 0, length = bytes.Length;
        int tiffLength = 0, wmfLength = 0;
        if (bytes.Length > 30 && bytes[0] == 0xC5 && bytes[1] == 0xD0 && bytes[2] == 0xD3 && bytes[3] == 0xC6)
        {
            int psOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
            int psLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
            wmfLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
            tiffLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24));
            if (psOffset >= 0 && psLength > 0 && psOffset + (long)psLength <= bytes.Length) { start = psOffset; length = psLength; }
        }
        var ps = bytes.AsSpan(start, length).ToArray();

        // a PDF inside the file
        int pdf = ps.AsSpan().IndexOf("%PDF-"u8);
        if (pdf > 0)
        {
            try
            {
                var slice = ps[pdf..];
                var r = PdfImporter.Read(slice, format, options);
                if (r.ObjectCount > 0) return r;
            }
            catch (VectorImportException) { /* fall through to the PostScript */ }
        }

        string head = Encoding.Latin1.GetString(ps, 0, Math.Min(ps.Length, 200_000));
        bool illustrator = head.Contains("Adobe Illustrator", StringComparison.Ordinal) || head.Contains("%AI", StringComparison.Ordinal);
        byte[] script = ps;
        string headForBox = head;
        bool privateData = false;
        if (AiPrivateData.TryExtract(ps, out var ai))
        {
            script = ai;
            headForBox = Encoding.Latin1.GetString(ai, 0, Math.Min(ai.Length, 200_000));
            privateData = true;
            illustrator = true;
        }

        var result = RunScript(script, headForBox, privateData, illustrator || !Regex.IsMatch(head, @"\bmoveto\b"), useArtboard: false, format, options);
        if (result.ObjectCount == 0)
        {
            string preview = tiffLength > 0 ? " The file has a preview picture (TIFF), which is not imported." : wmfLength > 0 ? " The file has a preview picture (Windows metafile), which is not imported." : "";
            return new VectorImportResult(result.Document, format, 1, 0,
                [.. result.Warnings, "No drawing could be read from this file: it needs a full PostScript interpreter." + preview]);
        }
        return result;
    }

    /// <summary>
    /// Runs a drawing script: PostScript, or the document data of an Illustrator file (the same operators). The page is the bounding box of the drawing, or
    /// for the data inside a PDF the artboard (<c>%AI3_Cropmarks</c>) when <paramref name="useArtboard"/>.
    /// </summary>
    internal static VectorImportResult RunScript(byte[] script, string header, bool privateData, bool aiMode, bool useArtboard, VectorFormat format, VectorImportOptions options)
    {
        var (llx, lly, urx, ury, haveBox) = useArtboard && Artboard(header) is { } art ? (art.Llx, art.Lly, art.Urx, art.Ury, true) : BoundingBox(header);
        var warnings = new List<string>();
        if (!haveBox || urx - llx < 1e-6 || ury - lly < 1e-6)
        {
            llx = 0; lly = 0; urx = 612; ury = 792;
            warnings.Add("The file does not say how big its drawing is; a letter-size page was used.");
        }
        var page = new PageSetup(urx - llx, ury - lly);
        var builder = new VectorBuilder(page, options);
        var baseMatrix = new Affine2(1, 0, 0, -1, -llx, ury);

        int from = ScriptStart(script, privateData, aiMode);
        PsInterpreter.Run(script, from, script.Length, builder, options, baseMatrix, aiMode);

        var document = builder.Build();
        return new VectorImportResult(document, format, 1, 0, [.. warnings, .. builder.Warnings]);
    }

    private static (double Llx, double Lly, double Urx, double Ury)? Artboard(string header)
    {
        int i = header.IndexOf("%AI3_Cropmarks:", StringComparison.Ordinal);
        if (i < 0) return null;
        int end = header.IndexOfAny(['\r', '\n'], i);
        var parts = header[(i + "%AI3_Cropmarks:".Length)..(end < 0 ? header.Length : end)].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !parts.Take(4).All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) return null;
        double[] v = parts.Take(4).Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return v[2] > v[0] && v[3] > v[1] ? (v[0], v[1], v[2], v[3]) : null;
    }
    /// <summary>Where the drawing starts: after the setup of an Illustrator file (its prolog is built in), else at the beginning.</summary>
    private static int ScriptStart(byte[] data, bool privateData, bool aiMode)
    {
        string text = Encoding.Latin1.GetString(data, 0, Math.Min(data.Length, 4_000_000));
        if (privateData)
        {
            int layer = text.IndexOf("%AI5_BeginLayer", StringComparison.Ordinal);
            if (layer >= 0) return layer;
        }
        if (!aiMode) return 0;
        foreach (var marker in new[] { "%%EndSetup", "%%EndProlog" })
        {
            int i = text.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0) return i + marker.Length;
        }
        return 0;
    }

    private static (double Llx, double Lly, double Urx, double Ury, bool Found) BoundingBox(string head)
    {
        foreach (var key in new[] { "%%HiResBoundingBox:", "%%BoundingBox:" })
        {
            int i = head.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) continue;
            int end = head.IndexOfAny(['\r', '\n'], i);
            string line = head[(i + key.Length)..(end < 0 ? head.Length : end)];
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4 && parts.Take(4).All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                double[] v = parts.Take(4).Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                if (v[2] > v[0] && v[3] > v[1]) return (v[0], v[1], v[2], v[3], true);
            }
        }
        return (0, 0, 0, 0, false);
    }
}

/// <summary>The document data an Illustrator EPS keeps as comment lines: ASCII85 text of a deflated copy of the document in the older Illustrator operator format.</summary>
internal static class AiPrivateData
{
    public static bool TryExtract(byte[] ps, out byte[] script)
    {
        script = [];
        var text = Encoding.Latin1.GetString(ps);
        int begin = text.IndexOf("%AI9_PrivateDataBegin", StringComparison.Ordinal);
        if (begin < 0) return false;
        int stream = text.IndexOf("%AI9_DataStream", begin, StringComparison.Ordinal);
        int end = text.LastIndexOf("%AI9_PrivateDataEnd", StringComparison.Ordinal);
        if (stream < 0 || end <= stream) return false;

        var sb = new StringBuilder();
        foreach (var raw in text[stream..end].Split('\n', '\r'))
        {
            if (raw.Length == 0 || raw[0] != '%' || raw.StartsWith("%AI9_DataStream", StringComparison.Ordinal)) continue;
            sb.Append(raw, 1, raw.Length - 1);
        }
        var chars = Encoding.Latin1.GetBytes(sb.ToString());
        int p = 0;
        var compressed = Ascii85.Decode(chars, ref p, chars.Length);
        try
        {
            using var z = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            script = ms.ToArray();
            return script.Length > 0;
        }
        catch (InvalidDataException) { return false; }
    }
}
