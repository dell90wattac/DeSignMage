using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Text;

/// <summary>One style of a font family as offered in the style picker ("Regular", "Bold Italic", ...).</summary>
public readonly record struct FontStyleInfo(string Name, int Weight, bool Italic);

/// <summary>
/// The outcome of looking up a requested font. When the font is not installed, <see cref="Missing"/> is set and
/// <see cref="Family"/> names the generic fallback that is used instead (the object keeps the requested name).
/// Metrics are fractions of the font size.
/// </summary>
public sealed record ResolvedFont(string Family, bool Missing, double Ascent, double Descent, double LineGap, object? Handle = null);

/// <summary>One letter (text element) after shaping: its advance, and its outline with the origin at the pen position, baseline y = 0.</summary>
public readonly record struct ShapedElement(double Advance, PathGeometry Outline);

/// <summary>
/// Where fonts come from. Core defines the contract; the HarfBuzz/Skia implementation lives in
/// <c>DeSignMage.Text</c> so Core has no dependencies. All outlines are in document units (points), y down.
/// </summary>
public interface IFontService
{
    /// <summary>Installed family names, sorted.</summary>
    IReadOnlyList<string> Families { get; }

    bool IsInstalled(string family);

    /// <summary>The styles an installed family offers (empty for a missing family).</summary>
    IReadOnlyList<FontStyleInfo> StylesOf(string family);

    ResolvedFont Resolve(string family, int weight, bool italic);

    /// <summary>
    /// Shapes one line (kerning pairs applied, ligatures off so every letter keeps its own dot). Returns exactly one
    /// entry per element, in order. No tracking or per-gap kerning is included; that is added by <see cref="TextEngine"/>.
    /// </summary>
    IReadOnlyList<ShapedElement> Shape(ResolvedFont font, IReadOnlyList<string> elements, double size);
}

/// <summary>Everything that affects how a text object looks, as a value.</summary>
public sealed record TextLayoutRequest(
    string Text, string Family, int Weight, bool Italic, double Size,
    double Tracking, IReadOnlyList<double> Kerning, TextAlignment Alignment, double LineSpacing);

/// <summary>One letter of a laid-out text (a grapheme cluster), or a line break.</summary>
public sealed class TextLetter
{
    public required int Index { get; init; }
    /// <summary>Offset of this letter in the string, and its length in UTF-16 units.</summary>
    public required int Start { get; init; }
    public required int Length { get; init; }
    public required int Line { get; init; }
    public required bool IsBreak { get; init; }
    /// <summary>Pen position of the letter and the baseline of its line, in the text's local coordinates.</summary>
    public required double X { get; init; }
    public required double Baseline { get; init; }
    public required double Advance { get; init; }
    /// <summary>Outline in local coordinates (already placed); empty for spaces and breaks.</summary>
    public required PathGeometry Outline { get; init; }
}

public sealed record TextLine(int FirstLetter, int LetterCount, double Baseline, double Left, double Width);

/// <summary>The result of laying a text out. Immutable. Coordinates are local to the text object; baseline of line 0 is y = 0.</summary>
public sealed class TextLayout
{
    public required IReadOnlyList<TextLetter> Letters { get; init; }
    public required IReadOnlyList<TextLine> Lines { get; init; }
    /// <summary>All glyph outlines as one path.</summary>
    public required PathGeometry Outline { get; init; }
    /// <summary>The advance box: widest line × (ascent + descent) per line. Never empty.</summary>
    public required Rect2 Frame { get; init; }
    public required string ResolvedFamily { get; init; }
    public required bool FontMissing { get; init; }
    public required double Ascent { get; init; }
    public required double Descent { get; init; }
    /// <summary>Distance between baselines of consecutive lines (also where a new line below this text would start).</summary>
    public required double LineHeight { get; init; }

    /// <summary>Number of letters, line breaks included. Caret positions run 0..LetterCount.</summary>
    public int LetterCount => Letters.Count;

    /// <summary>
    /// The dot between letter <paramref name="gap"/> and the next one, or null when there is none (end of line,
    /// line break). It sits at the next letter's pen position, so it moves exactly with the letters that follow it.
    /// </summary>
    public Vec2? GapPoint(int gap)
    {
        if (gap < 0 || gap + 1 >= Letters.Count) return null;
        var a = Letters[gap];
        var b = Letters[gap + 1];
        if (a.IsBreak || b.IsBreak || a.Line != b.Line) return null;
        return new Vec2(b.X, a.Baseline);
    }

    public IEnumerable<(int Gap, Vec2 Point)> GapPoints()
    {
        for (int i = 0; i + 1 < Letters.Count; i++)
            if (GapPoint(i) is { } p) yield return (i, p);
    }

    /// <summary>Where the caret sits before letter <paramref name="index"/> (0..LetterCount): the x and the baseline.</summary>
    public Vec2 CaretPosition(int index)
    {
        index = Math.Clamp(index, 0, Letters.Count);
        foreach (var line in Lines)
        {
            if (index > line.FirstLetter + line.LetterCount) continue;
            double x = index == line.FirstLetter + line.LetterCount ? line.Left + line.Width : Letters[index].X;
            return new Vec2(x, line.Baseline);
        }
        var last = Lines[^1];
        return new Vec2(last.Left + last.Width, last.Baseline);
    }

    /// <summary>The caret index closest to a point in local coordinates.</summary>
    public int IndexAt(Vec2 local)
    {
        var line = Lines[0];
        double bestDy = double.PositiveInfinity;
        foreach (var l in Lines)
        {
            double dy = Math.Abs(local.Y - (l.Baseline - (Ascent - Descent) / 2));
            if (dy < bestDy) { bestDy = dy; line = l; }
        }
        int best = line.FirstLetter;
        double bestDx = double.PositiveInfinity;
        for (int k = 0; k <= line.LetterCount; k++)
        {
            int idx = line.FirstLetter + k;
            double x = k == line.LetterCount ? line.Left + line.Width : Letters[idx].X;
            double dx = Math.Abs(local.X - x);
            if (dx < bestDx) { bestDx = dx; best = idx; }
        }
        return best;
    }
}

/// <summary>
/// Text layout in Core: splits lines and letters, asks the installed <see cref="IFontService"/> to shape, then applies
/// tracking, the per-gap kerning offsets, alignment and line spacing. Tracking and kerning are in ems (fractions of the font size).
/// </summary>
public static class TextEngine
{
    private static IFontService _fonts = new EstimatedFontService();

    /// <summary>The font source. Replaced once at startup by the HarfBuzz/Skia service; changing it invalidates cached layouts.</summary>
    public static IFontService Fonts
    {
        get => _fonts;
        set { _fonts = value; Generation++; }
    }

    /// <summary>Bumped whenever <see cref="Fonts"/> changes; text objects re-lay-out when it differs from their cached layout's.</summary>
    public static int Generation { get; private set; }

    public static TextLayout Layout(TextLayoutRequest r)
    {
        var fonts = _fonts;
        var font = fonts.Resolve(r.Family, r.Weight, r.Italic);
        double size = Math.Max(r.Size, 0.001);
        double lineHeight = (font.Ascent + font.Descent + font.LineGap) * size * (r.LineSpacing <= 0 ? 1 : r.LineSpacing);

        var letters = new List<TextLetter>();
        var lines = new List<TextLine>();
        var outlines = new List<PathGeometry>();
        var text = r.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        var textLines = text.Split('\n');

        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        int offset = 0;
        for (int li = 0; li < textLines.Length; li++)
        {
            var elements = TextElements.Split(textLines[li]);
            var shaped = elements.Count == 0 ? [] : fonts.Shape(font, elements, size);
            double baseline = li * lineHeight;
            int first = letters.Count;

            // Pen positions before alignment.
            var xs = new double[elements.Count];
            var advances = new double[elements.Count];
            double pen = 0;
            for (int k = 0; k < elements.Count; k++)
            {
                xs[k] = pen;
                advances[k] = shaped[k].Advance;
                int gap = first + k;   // the gap after this letter; a line's last letter has none, so its offset does not count
                double extra = k + 1 < elements.Count ? (r.Tracking + KerningAt(r.Kerning, gap)) * size : 0;
                pen += advances[k] + extra;
            }
            double width = elements.Count == 0 ? 0 : xs[^1] + advances[^1];
            double left = r.Alignment switch
            {
                TextAlignment.Center => -width / 2,
                TextAlignment.Right => -width,
                _ => 0,
            };

            int start = offset;
            for (int k = 0; k < elements.Count; k++)
            {
                double x = xs[k] + left;
                var outline = shaped[k].Outline.IsEmpty ? PathGeometry.EmptyPath : shaped[k].Outline.Transform(Affine2.Translation(x, baseline));
                if (!outline.IsEmpty) outlines.Add(outline);
                letters.Add(new TextLetter
                {
                    Index = letters.Count, Start = start, Length = elements[k].Length, Line = li, IsBreak = false,
                    X = x, Baseline = baseline, Advance = advances[k], Outline = outline,
                });
                start += elements[k].Length;
            }
            lines.Add(new TextLine(first, elements.Count, baseline, left, width));
            minX = Math.Min(minX, left);
            maxX = Math.Max(maxX, left + width);
            offset += textLines[li].Length;

            if (li + 1 < textLines.Length)
            {
                letters.Add(new TextLetter
                {
                    Index = letters.Count, Start = offset, Length = 1, Line = li, IsBreak = true,
                    X = left + width, Baseline = baseline, Advance = 0, Outline = PathGeometry.EmptyPath,
                });
                offset += 1;   // the '\n'
            }
        }

        double ascent = font.Ascent * size, descent = font.Descent * size;
        var frame = new Rect2(minX, -ascent, maxX, (textLines.Length - 1) * lineHeight + descent);
        return new TextLayout
        {
            Letters = letters, Lines = lines, Outline = Join(outlines), Frame = frame,
            ResolvedFamily = font.Family, FontMissing = font.Missing, Ascent = ascent, Descent = descent, LineHeight = lineHeight,
        };
    }

    /// <summary>
    /// The per-gap kerning (ems) that puts letter i at <paramref name="letterX"/>[i] (pen positions along the line, in the same
    /// units as the text, only the differences matter), given the request's font, size and tracking. For importing text whose
    /// letter positions are known (per-letter markers) without losing its spacing. Single-line text; the request's own
    /// kerning is ignored.
    /// </summary>
    public static IReadOnlyList<double> KerningForLetterPositions(TextLayoutRequest r, IReadOnlyList<double> letterX)
    {
        var letters = Layout(r with { Kerning = [] }).Letters;
        var k = new List<double>();
        for (int i = 0; i + 1 < Math.Min(letters.Count, letterX.Count); i++)
        {
            if (letters[i].IsBreak || letters[i + 1].IsBreak || letters[i].Line != letters[i + 1].Line) { k.Add(0); continue; }
            k.Add(((letterX[i + 1] - letterX[i]) - letters[i].Advance) / Math.Max(r.Size, 0.001) - r.Tracking);
        }
        return TextElements.NormalizeKerning(k);
    }

    /// <summary>
    /// Height of a capital letter (a capital H) in ems for the font that would actually be used: what sign lettering is measured by.
    /// 0.7 when the font gives no usable H.
    /// </summary>
    public static double CapHeightEm(string family, int weight, bool italic)
    {
        try
        {
            var font = _fonts.Resolve(family, weight, italic);
            var h = _fonts.Shape(font, ["H"], 1000)[0].Outline;
            double cap = h.IsEmpty ? 0 : h.GetBounds().Height / 1000;
            if (cap > 0.2 && cap < 1.5) return cap;
        }
        catch (Exception) { /* fall through to a typical value */ }
        return 0.7;
    }

    /// <summary>The kerning offset (in ems) at a gap, 0 when none is stored.</summary>
    public static double KerningAt(IReadOnlyList<double> kerning, int gap) => gap >= 0 && gap < kerning.Count ? kerning[gap] : 0;

    private static PathGeometry Join(List<PathGeometry> parts)
    {
        if (parts.Count == 0) return PathGeometry.EmptyPath;
        if (parts.Count == 1) return parts[0];
        var verbs = new List<PathVerb>();
        var points = new List<Vec2>();
        foreach (var p in parts) { verbs.AddRange(p.Verbs); points.AddRange(p.Points); }
        return new PathGeometry(verbs.ToArray(), points.ToArray());
    }
}

/// <summary>
/// Stand-in used until a real font service is installed (and by tests that do not care about glyph shapes):
/// every letter is a box of fixed width. <see cref="TextEngine.Fonts"/> is replaced at application startup.
/// </summary>
public sealed class EstimatedFontService : IFontService
{
    private static readonly string[] Names = ["Arial", "Segoe UI"];

    public IReadOnlyList<string> Families => Names;
    public bool IsInstalled(string family) => Names.Contains(family, StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<FontStyleInfo> StylesOf(string family) =>
        IsInstalled(family) ? [new("Regular", 400, false), new("Italic", 400, true), new("Bold", 700, false), new("Bold Italic", 700, true)] : [];

    public ResolvedFont Resolve(string family, int weight, bool italic) =>
        IsInstalled(family) ? new ResolvedFont(family, false, 0.8, 0.2, 0) : new ResolvedFont("Arial", true, 0.8, 0.2, 0);

    public IReadOnlyList<ShapedElement> Shape(ResolvedFont font, IReadOnlyList<string> elements, double size) =>
        elements.Select(e => string.IsNullOrWhiteSpace(e)
            ? new ShapedElement(size * 0.3, PathGeometry.EmptyPath)
            : new ShapedElement(size * 0.55, PathGeometry.Rectangle(new Rect2(size * 0.05, -size * 0.7, size * 0.5, 0)))).ToArray();
}
