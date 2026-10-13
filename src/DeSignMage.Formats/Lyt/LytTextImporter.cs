using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;

namespace DeSignMage.Formats.Lyt;

/// <summary>Why a text record stayed a plain shape instead of becoming a <see cref="TextObject"/>.</summary>
public enum LytTextKeptAs
{
    /// <summary>It became live text.</summary>
    None,

    /// <summary>No character markers: the layout had already converted this text to vectors (or it is an "Outlined" copy).</summary>
    ConvertedToVectors,

    /// <summary>A legacy <c>.FNT</c> outline font: not an installed font, so there is nothing to draw live text with yet.</summary>
    SignFont,

    /// <summary>The record carries something live text cannot reproduce (gradient or picture fill, slant, markers that do not match the string).</summary>
    Unmappable,
}

/// <summary>
/// Turns a layout text record into a live <see cref="TextObject"/> that lands where the stored outlines were drawn.
/// <list type="bullet">
/// <item><b>Font:</b> <c>"_Lato Semibold.ptt"</c> is the GDI face name <c>Lato Semibold</c> (the name the legacy font table lists), which the text
/// engine resolves itself; a font that is not installed keeps its name and is drawn in the generic fallback (R6).</item>
/// <item><b>Size:</b> Text is sized by its capital height: the node space is 10 units from baseline to the top of a capital "H"
/// (measured against Lato, Olivia and Corbel to 0.03 %). The em is therefore <c>10 / capHeight</c> node units, times 72Â·scale points.</item>
/// <item><b>Letter positions:</b> each character marker's x is the ink-left edge of that letter (a space's marker is its pen position),
/// counted from the first letter's ink-left. Pen = marker âˆ’ the letter's left side bearing; <see cref="TextObject.KerningForLetterPositions"/>
/// turns those pens into per-gap offsets. The median gap becomes <see cref="TextObject.Tracking"/>, the rest per-gap kerning, so
/// the tracking handle behaves, and every letter still lands where it was.</item>
/// <item><b>Alignment:</b> The nodes are moved so that center/right text is centered on / ends at the object's origin; the transform here
/// puts the first letter exactly where outline starts.</item>
/// </list>
/// </summary>
internal static class LytTextImporter
{
    /// <summary>Node-space height of a capital letter (text is normalised to 10 units).</summary>
    public const double CapHeightUnits = 10;

    /// <summary>The GDI family/face name of a TrueType text font file (<c>_Lato Semibold.ptt</c>), or null for legacy <c>.FNT</c> fonts.</summary>
    public static string? FamilyOf(string fontFile)
    {
        var f = fontFile.Trim();
        if (!f.EndsWith(".ptt", StringComparison.OrdinalIgnoreCase)) return null;
        f = f[..^4].TrimStart('_', '@').Trim();   // a leading '@' marks a vertical-writing face; we use the horizontal one
        return f.Length == 0 ? null : f;
    }

    /// <summary>The per-character markers split into glyph groups: marker x and the ink bounds (node space, y negated) of the glyph that follows.</summary>
    private static List<(double X, Rect2? Bounds)> GlyphGroups(LytObject o)
    {
        var groups = new List<(double X, List<LytNode> Nodes)>();
        foreach (var n in o.Nodes)
        {
            if (n.Kind == LytNodeKind.Char) groups.Add((n.X, []));
            else if (groups.Count > 0) groups[^1].Nodes.Add(n);
        }
        return groups.Select(g => (g.X, g.Nodes.Count == 0 ? (Rect2?)null : LytImporter.BuildPath(g.Nodes).GetBounds())).ToList();
    }

    /// <summary>
    /// Builds the text object (placed like <see cref="LytImporter.PartTransform"/> places the record's outline), or says why it stays a shape.
    /// </summary>
    public static LytTextKeptAs TryCreate(LytObject o, double pageHeightPoints, ObjectStyle style, out TextObject? text)
    {
        text = null;
        var s = o.Style!;
        var groups = GlyphGroups(o);
        if (groups.Count == 0) return LytTextKeptAs.ConvertedToVectors;
        if (FamilyOf(s.FontFile) is not { } family) return LytTextKeptAs.SignFont;

        var elements = TextElements.Split(o.Name);
        bool plain = !o.Name.Contains('\n') && !o.Name.Contains('\r') && elements.Count == groups.Count;
        // Fills other than a plain color, and slanted text (the italic field equals the rotation when text is merely rotated), cannot be reproduced.
        bool fillOk = s.Fill is null || s.Fill.Type == 0;
        bool slantOk = o.Shear == 0 && (o.Italic == 0 || Math.Abs(o.Italic - o.Rotation) < 1e-4);
        bool geometryOk = o.Scale > 0 && o.XScale > 0 && double.IsFinite(o.Scale + o.XScale + o.Rotation + o.OrgX + o.OrgY);
        if (!plain || !fillOk || !slantOk || !geometryOk) return LytTextKeptAs.Unmappable;

        double k = o.Scale * Units.PointsPerInch;                       // points per node unit
        var fonts = TextEngine.Fonts;
        var resolved = fonts.Resolve(family, 400, false);
        double cap = CapHeightOf(fonts, resolved);
        double emPoints = CapHeightUnits / cap * k;

        var t = new TextObject(o.Name, family, emPoints)
        {
            Style = style,
            Alignment = s.Align switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, _ => TextAlignment.Left },
        };

        // Where each letter's pen has to be (points, relative to the node origin).
        var plainLayout = t.Layout;
        if (plainLayout.Letters.Count != groups.Count) return LytTextKeptAs.Unmappable;
        int firstInk = groups.FindIndex(g => g.Bounds is not null);
        double nodeOffset = firstInk < 0 ? 0 : groups[firstInk].Bounds!.Value.Left - groups[firstInk].X;   // center/right shift applied to the line
        var pens = new double[groups.Count];
        for (int i = 0; i < pens.Length; i++)
        {
            var letter = plainLayout.Letters[i];
            double lsb = letter.Outline.IsEmpty ? 0 : letter.Outline.GetBounds().Left - letter.X;
            pens[i] = (groups[i].X + nodeOffset) * k - lsb;
        }

        var gaps = t.KerningForLetterPositions(pens).ToList();
        while (gaps.Count < pens.Length - 1) gaps.Add(0);
        double tracking = gaps.Count == 0 ? 0 : gaps.OrderBy(g => g).ElementAt(gaps.Count / 2);
        t.Tracking = tracking;
        t.Kerning = gaps.Select(g => Math.Abs(g - tracking) < 1e-6 ? 0 : g - tracking).ToArray();

        double dx = pens[0] - t.Layout.Letters[0].X;
        t.Transform = Affine2.Translation(dx, 0)
                    * Affine2.Scale(o.XScale, 1)
                    * Affine2.Rotation(-o.Rotation)
                    * Affine2.Translation(o.OrgX * Units.PointsPerInch, pageHeightPoints - o.OrgY * Units.PointsPerInch);
        text = t;
        return LytTextKeptAs.None;
    }

    /// <summary>Height of a capital H in ems, for the font actually used (a fallback's own, if the font is missing).</summary>
    internal static double CapHeightOf(IFontService fonts, ResolvedFont font)
    {
        try
        {
            var h = fonts.Shape(font, ["H"], 1000)[0].Outline;
            double cap = h.IsEmpty ? 0 : h.GetBounds().Height / 1000;
            if (cap > 0.2 && cap < 1.5) return cap;
        }
        catch (Exception) { /* fall through to a typical value */ }
        return 0.7;
    }
}
