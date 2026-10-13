using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Lyt;

/// <summary>
/// Turns the gradient and transparency fields of a legacy fill block (<see cref="LytBlend"/>) into a <see cref="GradientPaint"/>:
/// <list type="bullet">
/// <item><b>Gradient</b> (GradType linear, circle, ellipse, horizontal, vertical): the first two nodes are the ends of the control line, the
/// others sit between them (ordered by where their point falls on the line); highlight nodes keep their flag.</item>
/// <item><b>Transparency over a plain color</b> (GradType none, TransType linear, circle, ellipse, horizontal, vertical): the object's color
/// fading to nothing. It becomes a gradient of that one color whose stop alpha follows the legacy rule: opacity at the start (or center)
/// <c>1 - Max</c>, at the end (or radius) <c>1 - Min·Max</c>, exchanged when Swap is on. Flat transparency, and a transparency on top of
/// a color gradient, are not imported (there is no opacity ramp on top of another paint yet).</item>
/// </list>
/// Control points are stored in the part's node space (y up); they become the object's local space (y negated) and then relative to its bounds.
/// </summary>
internal static class LytGradients
{
    private const uint GradLinear = 1, GradCircle = 2, GradEllipse = 3, GradHorizontal = 5, GradVertical = 6;
    private const uint TransLinear = 2, TransCircle = 3, TransEllipse = 4, TransHorizontal = 0xC, TransVertical = 0xD;

    /// <summary>The paint for a part, or null when the block holds neither a usable gradient nor a fade.</summary>
    public static GradientPaint? TryCreate(LytBlend blend, DocColor baseColor, PathGeometry outline)
    {
        var bounds = outline.GetBounds();
        if (bounds.IsEmpty || bounds.Width <= 1e-9 || bounds.Height <= 1e-9) return null;

        if (blend.GradType is GradLinear or GradCircle or GradEllipse or GradHorizontal or GradVertical) return Blended(blend, baseColor, bounds);
        if (blend.GradType == 0 && blend.TransType is TransLinear or TransCircle or TransEllipse or TransHorizontal or TransVertical) return Fade(blend, baseColor, bounds);
        return null;
    }

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

        // An ellipse takes its two axis handles from the first three nodes; the colors between its ends start after them.
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
        return new GradientPaint(kind, points, stops);
    }

    // ---- fade ----

    private static GradientPaint? Fade(LytBlend b, DocColor color, Rect2 bounds)
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

        byte Alpha(double a) => (byte)Math.Round(Math.Clamp(a, 0, 1) * color.A);
        return new GradientPaint(kind, points, [new GradientNode(0, color with { A = Alpha(start) }), new GradientNode(1, color with { A = Alpha(end) })]);
    }
}
