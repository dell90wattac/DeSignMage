using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Effects;

/// <summary>
/// "How far is the pointer from the shape's edge, and on which side?" for the Outline, Inline and Cookie Cutter drag (outside positive, inside negative).
/// The outlines are flattened once and cut into short runs of segments with their bounding boxes, so a query only looks at the runs that can still hold
/// a nearer point: a pointer move costs a fraction of a millisecond on a text outline of tens of thousands of segments.
/// For an open path the sign tells the side of the line: positive = screen-left of the direction of travel, the side
/// <see cref="Geometry.OpenPathMode.OneSided"/> takes for a positive offset.
/// </summary>
public sealed class EdgeProbe
{
    /// <summary>Segments per run (a bounding box is kept for each).</summary>
    private const int RunLength = 24;

    private readonly record struct Loop(Vec2[] Points, bool Closed, int Shape, Rect2 Bounds);

    private readonly record struct Run(int Loop, int First, int Count, Rect2 Bounds);

    private readonly List<Loop> _loops = [];
    private readonly List<Run> _runs = [];
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
                if (p.Length < 2) continue;
                var bounds = p.Aggregate(Rect2.Empty, (r, q) => r.Union(q));
                int loop = _loops.Count;
                _loops.Add(new Loop(p, closed, index, bounds));
                int segments = closed ? p.Length : p.Length - 1;
                for (int first = 0; first < segments; first += RunLength)
                {
                    int count = Math.Min(RunLength, segments - first);
                    var rb = Rect2.Empty;
                    for (int i = first; i <= first + count; i++) rb = rb.Union(p[i % p.Length]);
                    _runs.Add(new Run(loop, first, count, rb));
                }
            }
            index++;
        }
    }

    public bool IsEmpty => _loops.Count == 0;

    private static double DistanceToRect(Vec2 p, Rect2 r)
    {
        double dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
        double dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private int _lastRun;   // the run that held the nearest point last time: pointer moves are close together, so it is the best place to start

    /// <summary>The nearest point on any edge and the signed distance to it; null when there are no edges. Not thread safe (it remembers where it looked last).</summary>
    public (Vec2 Nearest, double Signed)? Probe(Vec2 p)
    {
        if (_runs.Count == 0) return null;
        double best = double.PositiveInfinity;
        Vec2 nearest = default, a0 = default, b0 = default;
        int nearestRun = -1;

        void Scan(int index)
        {
            var run = _runs[index];
            if (DistanceToRect(p, run.Bounds) >= best) return;
            var pts = _loops[run.Loop].Points;
            for (int i = run.First; i < run.First + run.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Length];
                var ab = b - a;
                double len2 = ab.LengthSquared;
                double t = len2 < 1e-18 ? 0 : Math.Clamp(Vec2.Dot(p - a, ab) / len2, 0, 1);
                var q = a + ab * t;
                double d = Vec2.Distance(p, q);
                if (d < best) { best = d; nearest = q; nearestRun = index; a0 = a; b0 = b; }
            }
        }

        int start = Math.Clamp(_lastRun, 0, _runs.Count - 1);
        Scan(start);
        for (int r = 0; r < _runs.Count; r++)
            if (r != start) Scan(r);
        if (nearestRun < 0) return null;
        _lastRun = nearestRun;
        var l = _loops[_runs[nearestRun].Loop];
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
            // a loop whose box does not hold the point contributes nothing
            if (loop.Shape != shape || !loop.Closed || !loop.Bounds.Contains(p)) continue;
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
