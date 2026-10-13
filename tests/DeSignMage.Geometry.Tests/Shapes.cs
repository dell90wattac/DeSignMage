using DeSignMage.Core.Geometry;

namespace DeSignMage.Geometry.Tests;

/// <summary>Test shapes and exact measurements (areas are analytic for lines, arcs and cubics).</summary>
internal static class Shapes
{
    public static PathGeometry Rect(double x, double y, double w, double h, bool clockwiseOnScreen = true)
    {
        var b = new PathBuilder();
        if (clockwiseOnScreen) b.MoveTo(x, y).LineTo(x + w, y).LineTo(x + w, y + h).LineTo(x, y + h).Close();
        else b.MoveTo(x, y).LineTo(x, y + h).LineTo(x + w, y + h).LineTo(x + w, y).Close();
        return b.Build();
    }

    public static PathGeometry Polygon(params (double X, double Y)[] pts)
    {
        var b = new PathBuilder().MoveTo(pts[0].X, pts[0].Y);
        for (int i = 1; i < pts.Length; i++) b.LineTo(pts[i].X, pts[i].Y);
        return b.Close().Build();
    }

    /// <summary>Circle as <paramref name="arcs"/> native arcs, starting at angle <paramref name="startDeg"/>.</summary>
    public static PathGeometry Circle(Vec2 c, double r, int arcs = 4, double startDeg = 0, bool clockwiseOnScreen = true)
    {
        var b = new PathBuilder();
        double a0 = startDeg * Math.PI / 180;
        double step = 2 * Math.PI / arcs * (clockwiseOnScreen ? 1 : -1);
        Vec2 At(double a) => c + new Vec2(Math.Cos(a), Math.Sin(a)) * r;
        b.MoveTo(At(a0));
        for (int i = 0; i < arcs; i++)
            b.ArcTo(At(a0 + step * (i + 0.5)), i == arcs - 1 ? At(a0) : At(a0 + step * (i + 1)));
        return b.Close().Build();
    }

    public static PathGeometry Concat(params PathGeometry[] parts)
    {
        var verbs = new List<PathVerb>();
        var pts = new List<Vec2>();
        foreach (var p in parts) { verbs.AddRange(p.Verbs); pts.AddRange(p.Points); }
        return new PathGeometry(verbs.ToArray(), pts.ToArray(), parts.Length > 0 ? parts[0].FillRule : FillRule.NonZero);
    }

    public static PathGeometry WithRule(PathGeometry p, FillRule rule) => new([.. p.Verbs], [.. p.Points], rule);

    /// <summary>Signed area (shoelace via Green's theorem, exact per segment kind). Implicit closing lines included.</summary>
    public static double SignedArea(PathGeometry path)
    {
        double sum = 0;
        int pi = 0;
        Vec2 cur = Vec2.Zero, start = Vec2.Zero;
        var pts = path.Points;
        foreach (var v in path.Verbs)
        {
            switch (v)
            {
                case PathVerb.MoveTo:
                    start = cur = pts[pi++];
                    break;
                case PathVerb.LineTo:
                    sum += Vec2.Cross(cur, pts[pi]);
                    cur = pts[pi++];
                    break;
                case PathVerb.ArcTo:
                {
                    var t = pts[pi]; var e = pts[pi + 1];
                    pi += 2;
                    if (Arcs.TryCircle(Vec2.Zero, t - cur, e - cur, out var lc, out var r))
                    {
                        var (_, sweep) = Arcs.Angles(Vec2.Zero, t - cur, e - cur, lc);
                        sum += Vec2.Cross(lc + cur, e - cur) + r * r * sweep;
                    }
                    else sum += Vec2.Cross(cur, e);
                    cur = e;
                    break;
                }
                case PathVerb.CubicTo:
                {
                    var c1 = pts[pi]; var c2 = pts[pi + 1]; var e = pts[pi + 2];
                    pi += 3;
                    sum += CubicTerm(cur, c1, c2, e);
                    cur = e;
                    break;
                }
                case PathVerb.Close:
                    sum += Vec2.Cross(cur, start);
                    cur = start;
                    break;
            }
        }
        return sum / 2;
    }

    private static double CubicTerm(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3)
    {
        // Integral of (x y' - y x') dt with 4-point Gauss-Legendre (exact: the integrand is a degree-5 polynomial).
        double[] xs = [-0.8611363115940526, -0.3399810435848563, 0.3399810435848563, 0.8611363115940526];
        double[] ws = [0.3478548451374538, 0.6521451548625461, 0.6521451548625461, 0.3478548451374538];
        double s = 0;
        for (int i = 0; i < 4; i++)
        {
            double t = (xs[i] + 1) / 2, mt = 1 - t;
            var p = PathGeometry.EvalCubic(p0, p1, p2, p3, t);
            var d = 3 * mt * mt * (p1 - p0) + 6 * mt * t * (p2 - p1) + 3 * t * t * (p3 - p2);
            s += ws[i] * Vec2.Cross(p, d);
        }
        return s / 2;
    }

    public static double Area(PathGeometry path) => SignedArea(path);

    public static int Count(PathGeometry p, PathVerb v) => p.Verbs.Count(x => x == v);

    public static int Loops(PathGeometry p) => Count(p, PathVerb.MoveTo);

    /// <summary>Every arc of the path as (centre, radius, sweep).</summary>
    public static List<(Vec2 Center, double Radius, double Sweep)> ArcList(PathGeometry path)
    {
        var list = new List<(Vec2, double, double)>();
        int pi = 0;
        Vec2 cur = Vec2.Zero;
        foreach (var v in path.Verbs)
        {
            switch (v)
            {
                case PathVerb.MoveTo: case PathVerb.LineTo: cur = path.Points[pi++]; break;
                case PathVerb.CubicTo: cur = path.Points[pi + 2]; pi += 3; break;
                case PathVerb.ArcTo:
                {
                    var t = path.Points[pi]; var e = path.Points[pi + 1];
                    pi += 2;
                    if (Arcs.TryCircle(Vec2.Zero, t - cur, e - cur, out var c, out var r))
                        list.Add((c + cur, r, Arcs.Angles(Vec2.Zero, t - cur, e - cur, c).Sweep));
                    cur = e;
                    break;
                }
            }
        }
        return list;
    }

    /// <summary>Largest distance from points along <paramref name="a"/>'s outline to <paramref name="b"/>'s outline.</summary>
    public static double OneWayDistance(PathGeometry a, PathGeometry b, double step = 0.05)
    {
        double max = 0;
        foreach (var poly in a.Flatten(step / 10))
            foreach (var p in poly.Points)
                max = Math.Max(max, b.DistanceToOutline(p, step / 10));
        return max;
    }

    /// <summary>Symmetric Hausdorff distance between the outlines (approximate, by sampling).</summary>
    public static double Hausdorff(PathGeometry a, PathGeometry b, double step = 0.05) =>
        Math.Max(OneWayDistance(a, b, step), OneWayDistance(b, a, step));

    /// <summary>Area of two overlapping circles' union (lens formula).</summary>
    public static double TwoCircleUnion(double r1, double r2, double d)
    {
        if (d >= r1 + r2) return Math.PI * (r1 * r1 + r2 * r2);
        if (d <= Math.Abs(r1 - r2)) return Math.PI * Math.Pow(Math.Max(r1, r2), 2);
        double a1 = r1 * r1 * Math.Acos((d * d + r1 * r1 - r2 * r2) / (2 * d * r1));
        double a2 = r2 * r2 * Math.Acos((d * d + r2 * r2 - r1 * r1) / (2 * d * r2));
        double k = 0.5 * Math.Sqrt((-d + r1 + r2) * (d + r1 - r2) * (d - r1 + r2) * (d + r1 + r2));
        double lens = a1 + a2 - k;
        return Math.PI * (r1 * r1 + r2 * r2) - lens;
    }
}
