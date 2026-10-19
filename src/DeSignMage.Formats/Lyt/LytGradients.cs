using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Lyt;

/// <summary>
/// Turns the gradient and transparency fields of a legacy fill block (<see cref="LytBlend"/>) into a <see cref="GradientPaint"/>:
/// <list type="bullet">
/// <item><b>Gradient</b> (GradType linear, circle, ellipse, horizontal, vertical): the first two nodes are the ends of the control line, the
/// others sit between them (ordered by where their point falls on the line); highlight nodes keep their flag, Alternate Colors is read from the block.</item>
/// <item><b>Transparency over a plain color</b> (GradType none, TransType linear, circle, ellipse, horizontal, vertical): the object's color
/// fading to nothing. It becomes a gradient of that one color whose stop alpha follows the legacy rule: opacity at the start (or center)
/// <c>1 - Max</c>, at the end (or radius) <c>1 - Min·Max</c>, exchanged when Swap is on.</item>
/// <item><b>Transparency over a color gradient</b>: when the fade runs along the same control line as the colors (same kind and ends, as when the two were
/// made together), the fade is multiplied into the alpha of the gradient's stops, so the result is one gradient with see-through colors. A fade
/// along a different line cannot be written into the stops of a gradient: the gradient is kept as it is and the caller is told (<c>rampDropped</c>).</item>
/// <item><b>Flat transparency</b> (TransType 1): one opacity for the whole fill, <c>1 - Min</c> (<see cref="FlatOpacity"/>), which the caller multiplies into
/// the fill's color or into every stop of the gradient (<see cref="WithOpacity"/>).</item>
/// </list>
/// Control points are stored in the part's node space (y up); they become the object's local space (y negated) and then relative to its bounds.
/// </summary>
internal static class LytGradients
{
    private const uint GradLinear = 1, GradCircle = 2, GradEllipse = 3, GradHorizontal = 5, GradVertical = 6;
    private const uint TransFlat = 1, TransLinear = 2, TransCircle = 3, TransEllipse = 4, TransHorizontal = 0xC, TransVertical = 0xD;

    /// <summary>How far (as a fraction of the object's size) two control points may be apart and still count as the same line.</summary>
    private const double SameLineTolerance = 0.02;

    /// <summary>The paint for a part, or null when the block holds neither a usable gradient nor a fade (flat transparency is not a gradient: see <see cref="FlatOpacity"/>).</summary>
    public static GradientPaint? TryCreate(LytBlend blend, DocColor baseColor, PathGeometry outline) => Create(blend, baseColor, outline, out _);

    /// <summary><see cref="TryCreate"/>, also saying when a transparency ramp over a gradient could not be kept.</summary>
    public static GradientPaint? Create(LytBlend blend, DocColor baseColor, PathGeometry outline, out bool rampDropped)
    {
        rampDropped = false;
        var bounds = outline.GetBounds();
        if (bounds.IsEmpty || bounds.Width <= 1e-9 || bounds.Height <= 1e-9) return null;

        if (blend.GradType is GradLinear or GradCircle or GradEllipse or GradHorizontal or GradVertical)
        {
            var gradient = Blended(blend, baseColor, bounds);
            if (gradient is not null && IsRamp(blend.TransType) && Ramp(blend, bounds) is { } ramp)
            {
                var merged = MultiplyRamp(gradient, ramp);
                if (merged is null) rampDropped = true; else gradient = merged;
            }
            return gradient;
        }
        if (blend.GradType is 0 or 7 && IsRamp(blend.TransType) && Ramp(blend, bounds) is { } fade)
            return new GradientPaint(fade.Kind, fade.Points, [new GradientNode(0, baseColor with { A = Scale(baseColor.A, fade.Start) }), new GradientNode(1, baseColor with { A = Scale(baseColor.A, fade.End) })]);
        return null;
    }

    /// <summary>The opacity of a flat transparency, <c>1 - Min</c> (Max is not used by a flat one); 1 when the block has none.</summary>
    public static double FlatOpacity(LytBlend blend)
    {
        if (blend.TransType != TransFlat) return 1;
        double min = double.IsFinite(blend.TransMin) ? blend.TransMin : 0;
        return Math.Clamp(1 - min, 0, 1);
    }

    /// <summary>The fill with its colors' opacity multiplied by <paramref name="opacity"/> (a gradient keeps its geometry; any other paint of a plain color too).</summary>
    public static Paint? WithOpacity(Paint? paint, double opacity)
    {
        if (opacity >= 0.9995) return paint;
        return paint switch
        {
            SolidPaint s => new SolidPaint(s.Color with { A = Scale(s.Color.A, opacity) }),
            GradientPaint g => g.WithStops(g.Stops.Select(n => n with { Color = n.Color with { A = Scale(n.Color.A, opacity) } }).ToList()),
            _ => paint,
        };
    }

    private static byte Scale(byte alpha, double factor) => (byte)Math.Round(Math.Clamp(alpha * factor, 0, 255));

    private static bool IsRamp(uint transType) => transType is TransLinear or TransCircle or TransEllipse or TransHorizontal or TransVertical;

    private static Vec2 Relative(float x, float y, Rect2 bounds) => GradientPaint.ToRelative(new Vec2(x, -y), bounds);

    private static bool Finite(params float[] values) => values.All(float.IsFinite);

    // ---- gradient ----

    private static GradientPaint? Blended(LytBlend b, DocColor baseColor, Rect2 bounds)
    {
        int count = Math.Clamp(b.NodeCount, 2, GradientPaint.MaxStops);
        var nodes = new List<(Vec2 Point, DocColor Color, bool Highlight)>();
        for (int i = 0; i < count; i++)
        {
            float x, y; LytColor color; ushort flag = 0;
            if (i < 4) { x = b.Points[2 * i]; y = b.Points[2 * i + 1]; color = b.Colors[i]; }
            else
            {
                if (i - 4 >= b.ExtraNodes.Count) break;
                var n = b.ExtraNodes[i - 4];
                x = n.X; y = n.Y; color = n.Color; flag = n.Flag;
            }
            if (!Finite(x, y)) return null;
            nodes.Add((new Vec2(x, -y), color.IsValid ? color.ToDocColor() : baseColor, flag == 1));
        }
        if (nodes.Count < 2) return null;

        GradientKind kind = b.GradType switch
        {
            GradCircle => GradientKind.Circle, GradEllipse => GradientKind.Ellipse,
            GradHorizontal => GradientKind.Horizontal, GradVertical => GradientKind.Vertical, _ => GradientKind.Linear,
        };
        var p0 = nodes[0].Point; var p1 = nodes[1].Point;
        if ((p1 - p0).LengthSquared < 1e-12) return null;

        // An ellipse takes its two axis handles from the first three nodes (center, end of the first axis, end of the second, as the legacy program
        // draws it: the first axis gives the angle and the radius, the third point only the other half-axis); the colors between its ends start after them.
        int firstInner = 2;
        var points = new List<Vec2> { GradientPaint.ToRelative(p0, bounds), GradientPaint.ToRelative(p1, bounds) };
        if (kind == GradientKind.Ellipse)
        {
            if (nodes.Count < 3) return null;
            points.Add(GradientPaint.ToRelative(nodes[2].Point, bounds));
            firstInner = 3;
        }

        var axis = p1 - p0;
        var stops = new List<GradientNode> { new(0, nodes[0].Color), new(1, nodes[1].Color) };
        for (int i = firstInner; i < nodes.Count; i++)
        {
            double t = Vec2.Dot(nodes[i].Point - p0, axis) / axis.LengthSquared;
            stops.Add(new GradientNode(Math.Clamp(t, 0.001, 0.999), nodes[i].Color, nodes[i].Highlight));
        }
        return new GradientPaint(kind, points, stops, alternateColors: b.AlternateColors);
    }

    // ---- fade ----

    /// <summary>A transparency ramp: which way it runs and the opacity (0..1) at its start (or center) and at its end (or radius).</summary>
    private readonly record struct Fade(GradientKind Kind, Vec2[] Points, double Start, double End);

    private static Fade? Ramp(LytBlend b, Rect2 bounds)
    {
        double min = Math.Clamp(double.IsFinite(b.TransMin) ? b.TransMin : 0, 0, 1);
        double max = Math.Clamp(double.IsFinite(b.TransMax) ? b.TransMax : 1, 0, 1);
        // Opacity runs from 1 - Max at one end of the ramp to 1 - Min·Max at the other. In a linear fade the start is the low end. In a circle or
        // ellipse fade the center is the HIGH end (checked against the pin design: the red layer of its title is strongest in the middle of its
        // ellipse and fades out toward the rim; the older spec text had it the other way round). Swap turns the opacity upside down (1 - opacity)
        // for those, and exchanges the two ends of a linear one.
        double low = 1 - max, high = 1 - min * max;
        bool radial = b.TransType is TransCircle or TransEllipse;
        double start = radial ? high : low, end = radial ? low : high;
        if (b.TransSwap)
        {
            if (radial) (start, end) = (1 - start, 1 - end);
            else (start, end) = (end, start);
        }
        if (Math.Abs(start - 1) < 1e-3 && Math.Abs(end - 1) < 1e-3) return null;   // no fade at all

        var t = b.TransPoints;
        if (!Finite(t)) return null;
        GradientKind kind;
        Vec2[] points;
        switch (b.TransType)
        {
            case TransLinear:
                kind = GradientKind.Linear;
                points = [Relative(t[0], t[1], bounds), Relative(t[2], t[3], bounds)];
                break;
            case TransCircle:
                kind = GradientKind.Circle;
                points = [Relative(t[0], t[1], bounds), Relative(t[2], t[3], bounds)];
                break;
            case TransEllipse:
                kind = GradientKind.Ellipse;
                points = [Relative(t[0], t[1], bounds), Relative(t[2], t[3], bounds), Relative(t[4], t[5], bounds)];
                break;
            case TransHorizontal:
                kind = GradientKind.Horizontal;
                points = GradientPaint.DefaultPoints(kind);
                break;
            default:
                kind = GradientKind.Vertical;
                points = GradientPaint.DefaultPoints(kind);
                break;
        }
        if (kind != GradientKind.Horizontal && kind != GradientKind.Vertical && (points[1] - points[0]).LengthSquared < 1e-12) return null;
        return new Fade(kind, points, start, end);
    }

    /// <summary>
    /// The fade multiplied into the gradient's stops, or null when they do not run along the same line (then the colors alone are kept). A circle or
    /// ellipse fade measures from the center like the colors do, a linear one along the line, so the opacity at a stop is a plain interpolation of the two ends.
    /// </summary>
    private static GradientPaint? MultiplyRamp(GradientPaint g, Fade fade)
    {
        bool sameKind = g.Kind == fade.Kind;
        // a locked horizontal or vertical fade covers the whole object; the colors cover it too when they are locked the same way
        if (!sameKind || g.Points.Count != fade.Points.Length) return null;
        for (int i = 0; i < g.Points.Count; i++)
            if (Math.Abs(g.Points[i].X - fade.Points[i].X) > SameLineTolerance || Math.Abs(g.Points[i].Y - fade.Points[i].Y) > SameLineTolerance) return null;

        var stops = g.Stops.Select(n => n with { Color = n.Color with { A = Scale(n.Color.A, fade.Start + (fade.End - fade.Start) * n.Offset) } }).ToList();
        return g.WithStops(stops);
    }
}
