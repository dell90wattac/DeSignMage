using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Lyt;

/// <summary>
/// Writes a <see cref="Document"/> as a legacy layout (.lyt, format generation 0x4A), the file <see cref="LytParser"/> and
/// <see cref="LytImporter"/> read. What is written, and what a feature falls back to:
/// <list type="bullet">
/// <item><b>Shapes and paths</b>: one record per drawn object with all its contours as nodes: lines, circular arcs (centre and sweep) and
/// cubic Béziers (flag 0x400), the outer / hole flag of every contour, and the object's color (vinyl name, vendor, RGB or CMYK values).
/// Transforms are baked into the node coordinates (a rotated rectangle is a rectangle with rotated corners).</item>
/// <item><b>Groups</b>: membership over the flat drawing order, as in the document: parts are written in drawing order, each naming the
/// group it is in; the group records follow the parts and name their parent group.</item>
/// <item><b>Text</b>: live TrueType text (the font file name, the string, one marker node per letter followed by that letter's outline)
/// when the text is one line of regular weight, a plain color and an upright similarity transform; otherwise its outlines.</item>
/// <item><b>Pictures</b>: a rectangle with an embedded picture fill (JPEG or PNG bytes as they are).</item>
/// <item><b>Gradients and fades</b>: legacy gradient fills (linear, circle, ellipse, horizontal, vertical) and transparency ramps over a
/// color.</item>
/// <item><b>Strokes</b>: the legacy objects have no stroke; with the geometry services in the options a stroke becomes a ring shape in the
/// stroke color above the fill. <b>Soft shadows and glows</b> become plain objects.</item>
/// </list>
/// Everything that is simplified is listed in <see cref="LytWriteResult.Warnings"/>.
/// </summary>
public static class LytWriter
{
    /// <summary>Writes the layout to <paramref name="output"/> (the whole file in one go).</summary>
    public static LytWriteResult Write(Document document, Stream output, LytWriteOptions? options = null)
    {
        var result = new LytWriteResult();
        var bytes = Build(document, options ?? new LytWriteOptions(), result);
        output.Write(bytes);
        result.Length = bytes.Length;
        return result;
    }

    /// <summary>Writes the layout to a file, replacing it.</summary>
    public static LytWriteResult Write(Document document, string path, LytWriteOptions? options = null)
    {
        var result = new LytWriteResult();
        var bytes = Build(document, options ?? new LytWriteOptions(), result);
        File.WriteAllBytes(path, bytes);
        result.Length = bytes.Length;
        return result;
    }

    /// <summary>The layout as bytes.</summary>
    public static byte[] ToBytes(Document document, LytWriteOptions? options, out LytWriteResult result)
    {
        result = new LytWriteResult();
        var bytes = Build(document, options ?? new LytWriteOptions(), result);
        result.Length = bytes.Length;
        return bytes;
    }

    private static byte[] Build(Document document, LytWriteOptions options, LytWriteResult result) =>
        new LytWriteSession(document, options, result).Run();
}
