using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Effects;

/// <summary>
/// "How far is the pointer from the shape's edge, and on which side?" for the Outline, Inline and Cookie Cutter drag (drag
/// the edge in or out; positive outside, negative inside). The outlines are flattened once; each query walks the segments.
/// For an open path the sign tells the side of the line: positive = screen-left of the direction of travel, the side
/// <see cref="Geometry.OpenPathMode.OneSided"/> takes for a positive offset.
/// </summary>
public sealed class EdgeProbe
{
    private readonly record struct Loop(Vec2[] Points, bool Closed, int Shape);

    private readonly List<Loop> _loops = [];
    private readonly List<FillRule> _rules = [];

    public EdgeProbe(IEnumerable<PathGeometry> shapes, double tolerance = 0.1)
    {
        int index = 0;
        foreach (var g in shapes)
        {
            _rules.Add(g.FillRule);
            foreach (var poly in g.Flatten(tolerance))
            {
                var p = poly.Points;
                bool closed = poly.Closed || (p.Length >= 4 && Vec2.Distance(p[0], p[^1]) < 1e-6);
                if (p.Length >= 2) _loops.Add(new Loop(p, closed, index));
            }
            index++;
        }
    }

    public bool IsEmpty => _loops.Count == 0;

    /// <summary>The nearest point on any edge and the signed distance to it; null when there are no edges.</summary>
    public (Vec2 Nearest, double Signed)? Probe(Vec2 p)
    {
        double best = double.PositiveInfinity;
        Vec2 nearest = default;
        Loop? nearestLoop = null;
        Vec2 a0 = default, b0 = default;
        foreach (var loop in _loops)
        {
            var pts = loop.Points;
            int count = loop.Closed ? pts.Length : pts.Length - 1;
            for (int i = 0; i < count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Length];
                var ab = b - a;
                double len2 = ab.LengthSquared;
                double t = len2 < 1e-18 ? 0 : Math.Clamp(Vec2.Dot(p - a, ab) / len2, 0, 1);
                var q = a + ab * t;
                double d = Vec2.Distance(p, q);
                if (d < best) { best = d; nearest = q; nearestLoop = loop; a0 = a; b0 = b; }
            }
        }
        if (nearestLoop is not { } l) return null;
        double sign;
        if (l.Closed) sign = Inside(p, l.Shape) ? -1 : 1;
        else sign = Vec2.Cross(b0 - a0, p - a0) < 0 ? 1 : -1;
        return (nearest, sign * best);
    }

    private bool Inside(Vec2 p, int shape)
    {
        int winding = 0;
        foreach (var loop in _loops)
        {
            if (loop.Shape != shape || !loop.Closed) continue;
            var pts = loop.Points;
            for (int i = 0; i < pts.Length; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Length];
                if (a.Y <= p.Y)
                {
                    if (b.Y > p.Y && Vec2.Cross(b - a, p - a) > 0) winding++;
                }
                else if (b.Y <= p.Y && Vec2.Cross(b - a, p - a) < 0)
                {
                    winding--;
                }
            }
        }
        return _rules[shape] == FillRule.EvenOdd ? (winding & 1) != 0 : winding != 0;
    }
}
