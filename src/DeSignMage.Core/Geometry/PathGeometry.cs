namespace DeSignMage.Core.Geometry;

public enum PathVerb : byte { MoveTo, LineTo, CubicTo, Close, ArcTo }

public enum FillRule : byte { NonZero, EvenOdd }

/// <summary>A flattened subpath: a polyline, closed or open.</summary>
public readonly record struct Polyline(Vec2[] Points, bool Closed);

/// <summary>
/// Immutable path: subpaths of lines, cubic Béziers and circular arcs. Points per verb: MoveTo 1, LineTo 1,
/// CubicTo 3 (control1, control2, end), ArcTo 2 (a point on the arc, normally its midpoint; end), Close 0.
/// Arcs are native because the legacy layout format stores all curves as arcs. Quadratics are elevated to
/// cubics by <see cref="PathBuilder"/>.
/// </summary>
public sealed class PathGeometry
{
    /// <summary>Circle approximation constant for a quarter arc.</summary>
    public const double Kappa = 0.5522847498307936;

    private readonly PathVerb[] _verbs;
    private readonly Vec2[] _points;
    private sealed class BoundsBox(Rect2 value) { public readonly Rect2 Value = value; }
    private BoundsBox? _bounds;   // a reference, so worker threads (export, save) racing the UI thread never read a half-written box

    public PathGeometry(PathVerb[] verbs, Vec2[] points, FillRule fillRule = FillRule.NonZero)
    {
        _verbs = verbs;
        _points = points;
        FillRule = fillRule;
    }

    public static readonly PathGeometry EmptyPath = new([], []);

    public IReadOnlyList<PathVerb> Verbs => _verbs;
    public IReadOnlyList<Vec2> Points => _points;
    public FillRule FillRule { get; }
    public bool IsEmpty => _verbs.Length == 0;

    private byte _arcState;               // 0 = not looked yet, 1 = no arcs, 2 = has arcs (a benign race: every thread computes the same value)
    private PathGeometry? _cubicForm;     // the arcs-as-cubics copy, made on first use (hit testing asks for it on every mouse move)

    /// <summary>True if any segment is a circular arc.</summary>
    public bool HasArcs
    {
        get
        {
            if (_arcState == 0) _arcState = (byte)(Array.IndexOf(_verbs, PathVerb.ArcTo) >= 0 ? 2 : 1);
            return _arcState == 2;
        }
    }

    /// <summary>The same shape with every arc replaced by cubic Béziers (renderers, non-similarity transforms). Made once and kept.</summary>
    public PathGeometry WithArcsAsCubics()
    {
        if (!HasArcs) return this;
        return _cubicForm ??= BuildCubicForm();
    }

    private PathGeometry BuildCubicForm()
    {
        var b = new PathBuilder { FillRule = FillRule };
        int pi = 0;
        foreach (var v in _verbs)
        {
            switch (v)
            {
                case PathVerb.MoveTo: b.MoveTo(_points[pi++]); break;
                case PathVerb.LineTo: b.LineTo(_points[pi++]); break;
                case PathVerb.CubicTo: b.CubicTo(_points[pi], _points[pi + 1], _points[pi + 2]); pi += 3; break;
                case PathVerb.ArcTo:
                    foreach (var (c1, c2, end) in Arcs.ToCubics(b.Current, _points[pi], _points[pi + 1])) b.CubicTo(c1, c2, end);
                    pi += 2;
                    break;
                case PathVerb.Close: b.Close(); break;
            }
        }
        return b.Build();
    }

    public PathGeometry Transform(Affine2 m)
    {
        if (m.IsIdentity) return this;
        // A circle stays a circle only under similarity transforms; otherwise arcs become ellipse pieces.
        if (HasArcs && !Arcs.IsSimilarity(m)) return WithArcsAsCubics().Transform(m);
        var pts = new Vec2[_points.Length];
        for (int i = 0; i < pts.Length; i++) pts[i] = m.Transform(_points[i]);
        return new PathGeometry(_verbs, pts, FillRule);
    }

    /// <summary>Tight bounds (cubic extrema are solved, not just control points).</summary>
    public Rect2 GetBounds() => (_bounds ??= new BoundsBox(ComputeBounds())).Value;

    private Rect2 ComputeBounds()
    {
        if (HasArcs) return WithArcsAsCubics().GetBounds();
        var r = Rect2.Empty;
        int pi = 0;
        Vec2 cur = Vec2.Zero;
        foreach (var v in _verbs)
        {
            switch (v)
            {
                case PathVerb.MoveTo:
                case PathVerb.LineTo:
                    cur = _points[pi++];
                    r = r.Union(cur);
                    break;
                case PathVerb.CubicTo:
                    var c1 = _points[pi]; var c2 = _points[pi + 1]; var end = _points[pi + 2];
                    pi += 3;
                    r = r.Union(end);
                    foreach (var t in CubicExtrema(cur.X, c1.X, c2.X, end.X)) r = r.Union(EvalCubic(cur, c1, c2, end, t));
                    foreach (var t in CubicExtrema(cur.Y, c1.Y, c2.Y, end.Y)) r = r.Union(EvalCubic(cur, c1, c2, end, t));
                    cur = end;
                    break;
            }
        }
        return r;
    }

    public static Vec2 EvalCubic(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, double t)
    {
        double mt = 1 - t;
        double a = mt * mt * mt, b = 3 * mt * mt * t, c = 3 * mt * t * t, d = t * t * t;
        return new(a * p0.X + b * p1.X + c * p2.X + d * p3.X, a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y);
    }

    /// <summary>Parameters in (0,1) where the derivative of a 1D cubic is zero.</summary>
    private static IEnumerable<double> CubicExtrema(double p0, double p1, double p2, double p3)
    {
        // B'(t)/3 = A t^2 + B t + C
        double a = p1 - p0, b = p2 - p1, c = p3 - p2;
        double qa = a - 2 * b + c, qb = 2 * (b - a), qc = a;
        if (Math.Abs(qa) < 1e-12)
        {
            if (Math.Abs(qb) > 1e-12)
            {
                double t = -qc / qb;
                if (t > 0 && t < 1) yield return t;
            }
            yield break;
        }
        double disc = qb * qb - 4 * qa * qc;
        if (disc < 0) yield break;
        double sq = Math.Sqrt(disc);
        double t1 = (-qb + sq) / (2 * qa), t2 = (-qb - sq) / (2 * qa);
        if (t1 > 0 && t1 < 1) yield return t1;
        if (t2 > 0 && t2 < 1) yield return t2;
    }

    /// <summary>Converts curves to polylines with at most <paramref name="tolerance"/> deviation (approximately).</summary>
    public List<Polyline> Flatten(double tolerance)
    {
        if (HasArcs) return WithArcsAsCubics().Flatten(tolerance);
        tolerance = Math.Max(tolerance, 1e-6);
        var result = new List<Polyline>();
        List<Vec2>? current = null;
        int pi = 0;
        Vec2 cur = Vec2.Zero;

        void Finish(bool closed)
        {
            if (current is { Count: > 0 }) result.Add(new Polyline(current.ToArray(), closed));
            current = null;
        }

        foreach (var v in _verbs)
        {
            switch (v)
            {
                case PathVerb.MoveTo:
                    Finish(false);
                    cur = _points[pi++];
                    current = [cur];
                    break;
                case PathVerb.LineTo:
                    current ??= [cur];
                    cur = _points[pi++];
                    current.Add(cur);
                    break;
                case PathVerb.CubicTo:
                {
                    current ??= [cur];
                    var c1 = _points[pi]; var c2 = _points[pi + 1]; var end = _points[pi + 2];
                    pi += 3;
                    double dd = Math.Max((cur - 2 * c1 + c2).Length, (c1 - 2 * c2 + end).Length);
                    int n = (int)Math.Clamp(Math.Ceiling(Math.Sqrt(0.75 * dd / tolerance)), 1, 512);
                    for (int i = 1; i <= n; i++) current.Add(EvalCubic(cur, c1, c2, end, (double)i / n));
                    cur = end;
                    break;
                }
                case PathVerb.Close:
                    var start = current is { Count: > 0 } ? current[0] : cur;
                    Finish(true);
                    cur = start;
                    break;
            }
        }
        Finish(false);
        return result;
    }

    /// <summary>Fill containment test. Open subpaths are implicitly closed, as when filling.</summary>
    public bool Contains(Vec2 p, double tolerance = 0.1) => Contains(p, tolerance, includeOpen: true);

    /// <summary>
    /// Containment test where open subpaths may be left out (<paramref name="includeOpen"/> false): an open line encloses
    /// nothing, so only closed subpaths (a Close verb, or an end point that returns to the start) have an inside.
    /// </summary>
    public bool Contains(Vec2 p, double tolerance, bool includeOpen)
    {
        if (!GetBounds().Contains(p)) return false;
        int winding = 0;
        foreach (var poly in Flatten(tolerance))
        {
            var pts = poly.Points;
            if (!includeOpen && !poly.Closed && !(pts.Length >= 4 && Vec2.Distance(pts[0], pts[^1]) < 1e-6)) continue;
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
        return FillRule == FillRule.EvenOdd ? (winding & 1) != 0 : winding != 0;
    }

    /// <summary>Shortest distance from <paramref name="p"/> to the outline (closing segments only for closed subpaths).</summary>
    public double DistanceToOutline(Vec2 p, double tolerance = 0.1)
    {
        double best = double.PositiveInfinity;
        foreach (var poly in Flatten(tolerance))
        {
            var pts = poly.Points;
            if (pts.Length == 1) best = Math.Min(best, Vec2.Distance(p, pts[0]));
            int segs = poly.Closed ? pts.Length : pts.Length - 1;
            for (int i = 0; i < segs; i++)
                best = Math.Min(best, DistanceToSegment(p, pts[i], pts[(i + 1) % pts.Length]));
        }
        return best;
    }

    private static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-18) return Vec2.Distance(p, a);
        double t = Math.Clamp(Vec2.Dot(p - a, ab) / len2, 0, 1);
        return Vec2.Distance(p, a + ab * t);
    }

    // ---- Picking in another space (hit testing) ----
    //
    // Hit testing runs on every mouse move. Asking the shape moved into world space first (Transform, then Flatten) costs a copy of
    // the whole outline and a polyline for every curve, which for a text of 3,000 nodes was 2 to 5 ms per call. These overloads give
    // the same answers as `Transform(world)` followed by the plain method, but walk this geometry's own segments, move only the
    // control points they look at, and flatten just the curves that can matter: for a distance the ones whose box is near the point,
    // for containment the ones that cross the point's horizontal line to its right.

    private static int FlattenSteps(Vec2 p0, Vec2 c1, Vec2 c2, Vec2 p3, double tolerance)
    {
        double dd = Math.Max((p0 - 2 * c1 + c2).Length, (c1 - 2 * c2 + p3).Length);
        return (int)Math.Clamp(Math.Ceiling(Math.Sqrt(0.75 * dd / tolerance)), 1, 512);
    }

    /// <summary>
    /// <c>Transform(world).DistanceToOutline(p, tolerance)</c> without building the moved copy. The result is exact when it is at most
    /// <paramref name="maxDistance"/>; farther outlines may be reported as infinity.
    /// </summary>
    public double DistanceToOutline(Vec2 p, double tolerance, Affine2 world, double maxDistance = double.PositiveInfinity)
    {
        var g = WithArcsAsCubics();
        tolerance = Math.Max(tolerance, 1e-6);
        double best = double.PositiveInfinity;
        int pi = 0;
        Vec2 cur = default, start = default;
        bool open = false;            // a polyline is under way (MoveTo, or a segment after a Close)
        int segments = 0;             // segments of that polyline so far

        void Finish()
        {
            if (open && segments == 0) best = Math.Min(best, Vec2.Distance(p, cur));   // a lone point
            open = false;
            segments = 0;
        }

        for (int vi = 0; vi < g._verbs.Length; vi++)
        {
            switch (g._verbs[vi])
            {
                case PathVerb.MoveTo:
                    Finish();
                    cur = start = world.Transform(g._points[pi++]);
                    open = true;
                    break;
                case PathVerb.LineTo:
                {
                    if (!open) { open = true; start = cur; }
                    var b = world.Transform(g._points[pi++]);
                    best = Math.Min(best, DistanceToSegment(p, cur, b));
                    cur = b;
                    segments++;
                    break;
                }
                case PathVerb.CubicTo:
                {
                    if (!open) { open = true; start = cur; }
                    var c1 = world.Transform(g._points[pi]); var c2 = world.Transform(g._points[pi + 1]); var end = world.Transform(g._points[pi + 2]);
                    pi += 3;
                    double reach = Math.Min(best, maxDistance);
                    double minX = Math.Min(Math.Min(cur.X, c1.X), Math.Min(c2.X, end.X)), maxX = Math.Max(Math.Max(cur.X, c1.X), Math.Max(c2.X, end.X));
                    double minY = Math.Min(Math.Min(cur.Y, c1.Y), Math.Min(c2.Y, end.Y)), maxY = Math.Max(Math.Max(cur.Y, c1.Y), Math.Max(c2.Y, end.Y));
                    double dx = Math.Max(Math.Max(minX - p.X, p.X - maxX), 0), dy = Math.Max(Math.Max(minY - p.Y, p.Y - maxY), 0);
                    if (dx * dx + dy * dy <= reach * reach)   // the curve stays inside its control box, so a far box means a far curve
                    {
                        int n = FlattenSteps(cur, c1, c2, end, tolerance);
                        var a = cur;
                        for (int i = 1; i <= n; i++)
                        {
                            var b = EvalCubic(cur, c1, c2, end, (double)i / n);
                            best = Math.Min(best, DistanceToSegment(p, a, b));
                            a = b;
                        }
                    }
                    cur = end;
                    segments++;
                    break;
                }
                case PathVerb.Close:
                    if (open)
                    {
                        best = Math.Min(best, DistanceToSegment(p, cur, start));   // the closing edge only exists for closed subpaths
                        Finish();
                    }
                    cur = start;
                    break;
            }
        }
        Finish();
        return best;
    }

    /// <summary><c>Transform(world).Contains(p, tolerance, includeOpen)</c> without building the moved copy.</summary>
    public bool Contains(Vec2 p, double tolerance, bool includeOpen, Affine2 world)
    {
        var g = WithArcsAsCubics();
        tolerance = Math.Max(tolerance, 1e-6);
        int winding = 0;
        int pi = 0;
        Vec2 cur = default, start = default;
        bool open = false;
        int sub = 0;                  // winding of the polyline under way, added when it is known to count
        int points = 0;               // points the flattened polyline has (the open-path rule asks for at least four)

        void Edge(Vec2 a, Vec2 b)
        {
            if (a.Y <= p.Y)
            {
                if (b.Y > p.Y && Vec2.Cross(b - a, p - a) > 0) sub++;
            }
            else if (b.Y <= p.Y && Vec2.Cross(b - a, p - a) < 0)
            {
                sub--;
            }
        }

        void Finish(bool closed)
        {
            if (!open) return;
            Edge(cur, start);                                             // filling closes every subpath
            bool counts = includeOpen || closed || (points >= 4 && Vec2.Distance(start, cur) < 1e-6);
            if (counts) winding += sub;
            open = false;
            sub = 0;
            points = 0;
        }

        for (int vi = 0; vi < g._verbs.Length; vi++)
        {
            switch (g._verbs[vi])
            {
                case PathVerb.MoveTo:
                    Finish(false);
                    cur = start = world.Transform(g._points[pi++]);
                    open = true;
                    points = 1;
                    break;
                case PathVerb.LineTo:
                {
                    if (!open) { open = true; start = cur; points = 1; }
                    var b = world.Transform(g._points[pi++]);
                    Edge(cur, b);
                    cur = b;
                    points++;
                    break;
                }
                case PathVerb.CubicTo:
                {
                    if (!open) { open = true; start = cur; points = 1; }
                    var c1 = world.Transform(g._points[pi]); var c2 = world.Transform(g._points[pi + 1]); var end = world.Transform(g._points[pi + 2]);
                    pi += 3;
                    // Only edges that cross the point's horizontal line, to its right, change the winding.
                    double minY = Math.Min(Math.Min(cur.Y, c1.Y), Math.Min(c2.Y, end.Y)), maxY = Math.Max(Math.Max(cur.Y, c1.Y), Math.Max(c2.Y, end.Y));
                    double maxX = Math.Max(Math.Max(cur.X, c1.X), Math.Max(c2.X, end.X));
                    bool relevant = !(minY > p.Y || maxY <= p.Y || maxX < p.X);
                    if (relevant || !includeOpen)
                    {
                        int n = FlattenSteps(cur, c1, c2, end, tolerance);
                        points += n;
                        if (relevant)
                        {
                            var a = cur;
                            for (int i = 1; i <= n; i++)
                            {
                                var b = EvalCubic(cur, c1, c2, end, (double)i / n);
                                Edge(a, b);
                                a = b;
                            }
                        }
                    }
                    else points++;   // never read: with includeOpen the count does not matter
                    cur = end;
                    break;
                }
                case PathVerb.Close:
                    if (open) Finish(true);
                    cur = start;
                    break;
            }
        }
        Finish(false);
        return FillRule == FillRule.EvenOdd ? (winding & 1) != 0 : winding != 0;
    }

    // ---- Standard shapes ----

    public static PathGeometry Rectangle(Rect2 r, double cornerRadius = 0)
    {
        var b = new PathBuilder();
        double rad = Math.Min(Math.Max(cornerRadius, 0), Math.Min(Math.Abs(r.Width), Math.Abs(r.Height)) / 2);
        if (rad <= 0)
        {
            b.MoveTo(r.Left, r.Top).LineTo(r.Right, r.Top).LineTo(r.Right, r.Bottom).LineTo(r.Left, r.Bottom).Close();
            return b.Build();
        }
        double k = rad * (1 - Kappa);
        b.MoveTo(r.Left + rad, r.Top)
         .LineTo(r.Right - rad, r.Top)
         .CubicTo(r.Right - k, r.Top, r.Right, r.Top + k, r.Right, r.Top + rad)
         .LineTo(r.Right, r.Bottom - rad)
         .CubicTo(r.Right, r.Bottom - k, r.Right - k, r.Bottom, r.Right - rad, r.Bottom)
         .LineTo(r.Left + rad, r.Bottom)
         .CubicTo(r.Left + k, r.Bottom, r.Left, r.Bottom - k, r.Left, r.Bottom - rad)
         .LineTo(r.Left, r.Top + rad)
         .CubicTo(r.Left, r.Top + k, r.Left + k, r.Top, r.Left + rad, r.Top)
         .Close();
        return b.Build();
    }

    public static PathGeometry Ellipse(Rect2 r)
    {
        double cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        double rx = r.Width / 2, ry = r.Height / 2;
        double kx = rx * Kappa, ky = ry * Kappa;
        return new PathBuilder()
            .MoveTo(cx + rx, cy)
            .CubicTo(cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry)
            .CubicTo(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy)
            .CubicTo(cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry)
            .CubicTo(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy)
            .Close()
            .Build();
    }
}

/// <summary>Fluent builder for <see cref="PathGeometry"/>.</summary>
public sealed class PathBuilder
{
    private readonly List<PathVerb> _verbs = [];
    private readonly List<Vec2> _points = [];
    private Vec2 _current;

    public FillRule FillRule { get; set; } = FillRule.NonZero;

    public PathBuilder MoveTo(double x, double y) => MoveTo(new Vec2(x, y));
    public PathBuilder LineTo(double x, double y) => LineTo(new Vec2(x, y));

    public PathBuilder MoveTo(Vec2 p)
    {
        _verbs.Add(PathVerb.MoveTo); _points.Add(p); _current = p;
        return this;
    }

    public PathBuilder LineTo(Vec2 p)
    {
        _verbs.Add(PathVerb.LineTo); _points.Add(p); _current = p;
        return this;
    }

    public PathBuilder CubicTo(double c1x, double c1y, double c2x, double c2y, double x, double y) =>
        CubicTo(new Vec2(c1x, c1y), new Vec2(c2x, c2y), new Vec2(x, y));

    public PathBuilder CubicTo(Vec2 c1, Vec2 c2, Vec2 end)
    {
        _verbs.Add(PathVerb.CubicTo); _points.Add(c1); _points.Add(c2); _points.Add(end); _current = end;
        return this;
    }

    /// <summary>Circular arc from the current point through <paramref name="through"/> to <paramref name="end"/>.</summary>
    public PathBuilder ArcTo(Vec2 through, Vec2 end)
    {
        _verbs.Add(PathVerb.ArcTo); _points.Add(through); _points.Add(end); _current = end;
        return this;
    }

    /// <summary>The current point (end of the last segment).</summary>
    public Vec2 Current => _current;

    /// <summary>Quadratic curve, stored as the equivalent cubic (exact).</summary>
    public PathBuilder QuadTo(Vec2 c, Vec2 end) =>
        CubicTo(_current + (c - _current) * (2.0 / 3), end + (c - end) * (2.0 / 3), end);

    public PathBuilder Close()
    {
        _verbs.Add(PathVerb.Close);
        return this;
    }

    public PathGeometry Build() => new(_verbs.ToArray(), _points.ToArray(), FillRule);
}
