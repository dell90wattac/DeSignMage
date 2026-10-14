using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Text;

/// <summary>How the letters are spread along the path.</summary>
public enum TextPathSpacing : byte
{
    /// <summary>The text fills the whole path: the first letter at its start, the last at its end, the gaps stretched or squeezed to fit (the legacy default).</summary>
    Fit,

    /// <summary>The letters keep their natural spacing and start where the text's alignment puts them (Left = path start, Center = middle, Right = end).</summary>
    Natural,
}

/// <summary>Which side of the path the letters sit on.</summary>
public enum TextPathSide : byte
{
    /// <summary>The baseline lies on the path and the letters stand on it.</summary>
    Above,

    /// <summary>The letters hang from the path (their ascender line on it), on the other side.</summary>
    Below,
}

/// <summary>
/// "Text on Path" (effects spec <c>text-on-path.md</c>): the text flows along a curve and stays live. Each letter is moved and rotated whole
/// (never bent) so that the middle of its baseline lands on the path; retyping, a font change or a spacing change re-flows it.
/// <list type="bullet">
/// <item><see cref="Path"/> is in the text's own coordinate space (like the legacy effect's stored nodes), so moving, scaling or rotating the text carries the path along.</item>
/// <item><see cref="CurveCompensation"/> evens out the gaps at bends (the spec's formula: a letter past a bend moves along by <c>0.12 · pathLength · tan(turn) · c</c>,
/// summed over the bends before it, positive when the path turns toward the letters' tops; see <see cref="TextPathPlacer.BendFactor"/>). 0 turns it off; the default 0.5
/// is the legacy code's default and, with that factor, the legacy help's "6 %". It does not apply to a single arc.</item>
/// <item><see cref="StartOffset"/> shifts every letter along the path (points, positive = toward the path's end).</item>
/// <item><see cref="Raise"/> lifts the baseline off the path, perpendicular to it (points, positive = toward the letters' tops).</item>
/// <item><see cref="Flip"/> runs the text along the path backwards, which also turns it onto the other side.</item>
/// </list>
/// </summary>
public sealed record TextOnPath(PathGeometry Path)
{
    public const double DefaultCompensation = 0.5;

    public double CurveCompensation { get; init; } = DefaultCompensation;
    public TextPathSpacing Spacing { get; init; } = TextPathSpacing.Fit;
    public double StartOffset { get; init; }
    public TextPathSide Side { get; init; } = TextPathSide.Above;
    public bool Flip { get; init; }
    public double Raise { get; init; }

    /// <summary>
    /// The path the legacy tool starts a text with: from the left end to the right end of the text's baseline as one cubic whose control
    /// points lie 0.3 × the chord above it (at 0.4 of the way) and below it (at 0.6): a gentle S that rises first. Read from the legacy file's own default.
    /// </summary>
    public static TextOnPath DefaultFor(TextObject text)
    {
        var straight = TextEngine.Layout(text.ToLayoutRequest());
        double x0 = straight.Frame.Left, chord = Math.Max(straight.Frame.Width, text.FontSize);
        var b = new PathBuilder().MoveTo(x0, 0);
        b.CubicTo(x0 + 0.4 * chord, -0.3 * chord, x0 + 0.6 * chord, 0.3 * chord, x0 + chord, 0);   // first control above the baseline, second below: it rises, then falls
        return new TextOnPath(b.Build());
    }

    /// <summary>True when the path is one circular arc (two nodes, the second an arc): there the compensation is forced to 0 and its field is hidden.</summary>
    public bool PathIsSingleArc => PathEditing.ToLoops(Path) is [{ Closed: false, Segments: [{ Kind: SegmentKind.Arc }] }];

    /// <summary>The compensation that is actually used (0 for a single arc).</summary>
    public double EffectiveCompensation => PathIsSingleArc ? 0 : CurveCompensation;
}
