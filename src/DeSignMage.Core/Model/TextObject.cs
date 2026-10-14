using DeSignMage.Core.Geometry;
using DeSignMage.Core.Text;

namespace DeSignMage.Core.Model;

/// <summary>
/// Where the text sits relative to its origin (local x = 0): the line starts there (Left), is centered on it, or ends
/// there (Right). Justify behaves like Left until paragraph text exists.
/// </summary>
public enum TextAlignment : byte { Left, Center, Right, Justify }

/// <summary>
/// Live text (requirement R6): a string drawn from a font every time it is needed, so the font can change at any
/// time, until its vectors are edited (then it becomes ordinary paths, see <c>ConvertTextToCurvesCommand</c>).
/// <list type="bullet">
/// <item>Local origin (0,0) is the start of the first line's baseline; y grows downward, so letters extend into negative y.</item>
/// <item><see cref="FontFamily"/> is the name that was asked for. If it isn't installed the text still renders in a generic
/// fallback and <see cref="IsFontMissing"/> is true; the requested name stays so the font can be resolved later.</item>
/// <item><see cref="Tracking"/> (every letter) and <see cref="Kerning"/> (per gap) are in ems, i.e. fractions of
/// <see cref="FontSize"/>, so they follow size changes. Kerning index g is the gap after letter g; everything to the right shifts.</item>
/// <item>Letters are grapheme clusters (<see cref="TextElements"/>); a '\n' makes a new line (and counts as a letter for kerning indices).</item>
/// </list>
/// </summary>
public sealed class TextObject : DocObject
{
    private string _text;
    private string _fontFamily;
    private int _fontWeight = 400;
    private bool _italic;
    private double _fontSize;
    private double _tracking;
    private IReadOnlyList<double> _kerning = [];
    private TextAlignment _alignment;
    private double _lineSpacing = 1;
    private TextOnPath? _onPath;

    private sealed record LayoutCache(int Stamp, int Generation, TextLayout Layout);
    private LayoutCache? _layout;
    private int _layoutStamp;

    /// <summary>
    /// Counts only the changes that alter the laid-out glyphs (text, font, size, spacing, kerning, alignment). Moving,
    /// recoloring or renaming the object bumps <see cref="DocObject.Version"/> but not this, so the layout survives.
    /// </summary>
    public int LayoutStamp => _layoutStamp;

    private void TouchLayout() { _layoutStamp++; Touch(); }

    public TextObject(string text, string fontFamily, double fontSize)
    {
        _text = text; _fontFamily = fontFamily; _fontSize = fontSize;
        Style = ObjectStyle.FillOnly(DocColor.Black);
    }

    public string Text { get => _text; set { _text = value; TouchLayout(); } }

    /// <summary>The requested font family, kept even if it is not installed.</summary>
    public string FontFamily { get => _fontFamily; set { _fontFamily = value; TouchLayout(); } }

    /// <summary>CSS-style weight: 400 regular, 700 bold, 600 semibold, ...</summary>
    public int FontWeight { get => _fontWeight; set { _fontWeight = value; TouchLayout(); } }
    public bool Italic { get => _italic; set { _italic = value; TouchLayout(); } }

    /// <summary>Em size in points.</summary>
    public double FontSize { get => _fontSize; set { _fontSize = value; TouchLayout(); } }

    /// <summary>Extra space after every letter, in ems.</summary>
    public double Tracking { get => _tracking; set { _tracking = value; TouchLayout(); } }

    /// <summary>Per-gap offsets in ems (see class remarks). Trailing zeros are dropped; empty means none.</summary>
    public IReadOnlyList<double> Kerning
    {
        get => _kerning;
        set { _kerning = TextElements.NormalizeKerning(value); TouchLayout(); }
    }

    public TextAlignment Alignment { get => _alignment; set { _alignment = value; TouchLayout(); } }

    /// <summary>Line distance as a multiple of the font's natural line height (multi-line text only).</summary>
    public double LineSpacing { get => _lineSpacing; set { _lineSpacing = value; TouchLayout(); } }

    /// <summary>
    /// Text on a Path (null = ordinary straight text): the text follows this path, still live (retyping or a font change re-flows it).
    /// See <see cref="TextOnPath"/>. The path is in this object's own coordinate space.
    /// </summary>
    public TextOnPath? OnPath { get => _onPath; set { _onPath = value; TouchLayout(); } }

    public bool IsOnPath => _onPath is not null;

    public double KerningAt(int gap) => TextEngine.KerningAt(_kerning, gap);

    /// <summary>A kerning list with <paramref name="gap"/> set to <paramref name="value"/> ems (does not change the object).</summary>
    public IReadOnlyList<double> KerningWith(int gap, double value)
    {
        var list = _kerning.ToList();
        while (list.Count <= gap) list.Add(0);
        list[gap] = value;
        return TextElements.NormalizeKerning(list);
    }

    public override string KindName => "Text";

    /// <summary>Everything that decides how this text looks, as a value.</summary>
    public TextLayoutRequest ToLayoutRequest() =>
        new(_text, _fontFamily, _fontWeight, _italic, _fontSize, _tracking, _kerning, _alignment, _lineSpacing);

    /// <summary>
    /// The kerning that makes this text's letters sit at the given pen positions (letter i at <paramref name="letterX"/>[i];
    /// only differences matter), using the current font, size and tracking. Assign the result to <see cref="Kerning"/>.
    /// </summary>
    public IReadOnlyList<double> KerningForLetterPositions(IReadOnlyList<double> letterX) =>
        TextEngine.KerningForLetterPositions(ToLayoutRequest(), letterX);

    /// <summary>The laid-out text. Cached per object version; recomputed after any change or when the font service changes.</summary>
    public TextLayout Layout
    {
        get
        {
            int stamp = _layoutStamp, generation = TextEngine.Generation;
            if (_layout is { } cached && cached.Stamp == stamp && cached.Generation == generation) return cached.Layout;
            var layout = TextEngine.Layout(ToLayoutRequest());
            if (_onPath is { } onPath) layout = TextPathPlacer.Place(layout, onPath);
            _layout = new LayoutCache(stamp, generation, layout);   // one reference: worker threads (export, save) may race the UI thread here
            return layout;
        }
    }

    /// <summary>True when the requested font isn't installed and a fallback is drawn instead.</summary>
    public bool IsFontMissing => Layout.FontMissing;

    /// <summary>The font actually used (equals <see cref="FontFamily"/> unless it is missing).</summary>
    public string ResolvedFontFamily => Layout.ResolvedFamily;

    protected override DocObject CloneCore() => new TextObject(_text, _fontFamily, _fontSize)
    {
        _fontWeight = _fontWeight, _italic = _italic, _tracking = _tracking, _kerning = _kerning,
        _alignment = _alignment, _lineSpacing = _lineSpacing, _onPath = _onPath,
    };

    /// <summary>The glyph outlines in local coordinates (what is filled and stroked). Empty for blank text.</summary>
    public override PathGeometry GetLocalGeometry() => Layout.Outline;

    /// <summary>The clickable box in local coordinates: the advance box, at least a few points wide so empty text can be hit.</summary>
    public Rect2 GetPickFrame()
    {
        var f = Layout.Frame;
        return f.Width < 2 ? new Rect2(f.Left - 1, f.Top, f.Right + 1, f.Bottom) : f;
    }

    /// <summary>
    /// What a click on this text hits, in local coordinates: its advance box, or for text on a path the boxes of the placed letters
    /// (so a click between two letters on a curve still picks the text, and a click far from the curve does not).
    /// </summary>
    public PathGeometry GetPickGeometry()
    {
        var layout = Layout;
        if (layout.Placements is null) return PathGeometry.Rectangle(GetPickFrame());
        var b = new PathBuilder { FillRule = FillRule.NonZero };
        for (int i = 0; i < layout.Letters.Count; i++)
        {
            if (layout.Letters[i].IsBreak) continue;
            var q = layout.LetterQuad(i);
            b.MoveTo(q[0]).LineTo(q[1]).LineTo(q[2]).LineTo(q[3]).Close();
        }
        var g = b.Build();
        return g.IsEmpty ? PathGeometry.Rectangle(GetPickFrame()) : g;
    }

    /// <summary>Tight bounds of the glyphs; the pick frame for text that draws nothing (empty or only spaces).</summary>
    public override long DependencyStamp => TextEngine.Generation;

    protected override Rect2 ComputeWorldBounds(Affine2 w)
    {
        var outline = Layout.Outline;
        if (outline.IsEmpty) return GetPickFrame().Transform(w);
        // Without rotation or skew the cached local bounds map exactly; otherwise the glyphs themselves are transformed.
        return Math.Abs(w.M12) < 1e-12 && Math.Abs(w.M21) < 1e-12
            ? outline.GetBounds().Transform(w)
            : outline.Transform(w).GetBounds();
    }
}

