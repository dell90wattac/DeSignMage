using DeSignMage.Core.Geometry;

namespace DeSignMage.Geometry.Internal;

internal enum SegKind : byte { Line, Arc, Cubic }

/// <summary>
/// One source segment of a path (line, circular arc or cubic) with a parameter t in [0, 1] from start to end.
/// Arcs are kept as centre, radius, start angle and signed sweep so that pieces of them stay exact.
/// </summary>
internal sealed class Seg
{
    public SegKind Kind { get; private init; }
    public Vec2 P0 { get; private init; }
    public Vec2 P1 { get; private init; }
    public Vec2 C1 { get; private init; }
    public Vec2 C2 { get; private init; }
    public Vec2 Center { get; private init; }
    public double Radius { get; private init; }
    public double A0 { get; private init; }
    public double Sweep { get; private init; }

    /// <summary>Index in the owning table (set by <see cref="TagTable"/>).</summary>
    public int Id { get; set; } = -1;

    /// <summary>Angle step (radians) the arc was flattened with; 0 for other kinds.</summary>
    public double FlattenStep { get; set; }

    private Seg() { }

    public static Seg? Line(Vec2 a, Vec2 b, double eps) =>
        Vec2.Distance(a, b) <= eps ? null : new Seg { Kind = SegKind.Line, P0 = a, P1 = b };

    /// <summary>Arc start → through → end. Collinear points give a line; a zero-size arc gives null.</summary>
    public static Seg? Arc(Vec2 s, Vec2 through, Vec2 e, double eps)
    {
        // Solved relative to the start point: the three-point formula loses digits far from the origin.
        if (!Arcs.TryCircle(Vec2.Zero, through - s, e - s, out var lc, out var r) || r <= eps)
            return Line(s, e, eps);
        var c = lc + s;
        var (a0, sweep) = Arcs.Angles(Vec2.Zero, through - s, e - s, lc);
        if (Math.Abs(sweep) * r <= eps) return null;
        return new Seg { Kind = SegKind.Arc, P0 = s, P1 = e, Center = c, Radius = r, A0 = a0, Sweep = sweep };
    }

    public static Seg? Cubic(Vec2 p0, Vec2 c1, Vec2 c2, Vec2 p3, double eps)
    {
        if (Vec2.Distance(p0, p3) <= eps && Vec2.Distance(p0, c1) <= eps && Vec2.Distance(p0, c2) <= eps) return null;
        return new Seg { Kind = SegKind.Cubic, P0 = p0, C1 = c1, C2 = c2, P1 = p3 };
    }

    public bool IsCurve => Kind != SegKind.Line;

    public Vec2 Eval(double t)
    {
        if (t <= 0) return P0;
        if (t >= 1) return P1;
        switch (Kind)
        {
            case SegKind.Line: return Vec2.Lerp(P0, P1, t);
            case SegKind.Arc:
            {
                double a = A0 + Sweep * t;
                return Center + new Vec2(Math.Cos(a), Math.Sin(a)) * Radius;
            }
            default: return PathGeometry.EvalCubic(P0, C1, C2, P1, t);
        }
    }

    /// <summary>Derivative with respect to t (direction of travel, not normalised).</summary>
    public Vec2 Deriv(double t)
    {
        switch (Kind)
        {
            case SegKind.Line: return P1 - P0;
            case SegKind.Arc:
            {
                double a = A0 + Sweep * t;
                return new Vec2(-Math.Sin(a), Math.Cos(a)) * (Radius * Sweep);
            }
            default:
            {
                double mt = 1 - t;
                var d = 3 * mt * mt * (C1 - P0) + 6 * mt * t * (C2 - C1) + 3 * t * t * (P1 - C2);
                if (d.LengthSquared > 1e-24) return d;
                // Coincident control points: use the next non-zero direction.
                var alt = t < 0.5 ? (C2 - P0) : (P1 - C1);
                return alt.LengthSquared > 1e-24 ? alt : P1 - P0;
            }
        }
    }

    private Vec2 Deriv2(double t)
    {
        if (Kind == SegKind.Arc)
        {
            double a = A0 + Sweep * t;
            return new Vec2(-Math.Cos(a), -Math.Sin(a)) * (Radius * Sweep * Sweep);
        }
        if (Kind == SegKind.Line) return Vec2.Zero;
        double mt = 1 - t;
        return 6 * mt * (C2 - 2 * C1 + P0) + 6 * t * (P1 - 2 * C2 + C1);
    }

    /// <summary>Unit tangent at t.</summary>
    public Vec2 Tangent(double t)
    {
        var d = Deriv(t);
        double l = d.Length;
        return l < 1e-300 ? new Vec2(1, 0) : d / l;
    }

    /// <summary>Appends the interior parameters (0 &lt; t &lt; 1) a polyline needs to stay within <paramref name="tol"/>.</summary>
    public void SampleParams(double tol, List<double> ts)
    {
        int n;
        switch (Kind)
        {
            case SegKind.Line: return;
            case SegKind.Arc:
            {
                double c = 1 - tol / Radius;
                double step = c <= -1 ? Math.PI : 2 * Math.Acos(c);
                step = Math.Clamp(step, 1e-4, Math.PI / 12);   // at most 15°: keeps tags dense enough on big, flat arcs
                n = (int)Math.Clamp(Math.Ceiling(Math.Abs(Sweep) / step - 1e-9), 1, 100_000);
                FlattenStep = Math.Abs(Sweep) / n;
                break;
            }
            default:
            {
                double dd = Math.Max((P0 - 2 * C1 + C2).Length, (C1 - 2 * C2 + P1).Length);
                n = (int)Math.Clamp(Math.Ceiling(Math.Sqrt(0.75 * dd / tol)), 1, 4096);
                // Always a few samples on a curve, so tags can tell it from the chord.
                n = Math.Max(n, 2);
                break;
            }
        }
        for (int i = 1; i < n; i++) ts.Add((double)i / n);
    }

    /// <summary>Parameter of the point nearest to <paramref name="p"/> within [lo, hi].</summary>
    public double Project(Vec2 p, double lo, double hi)
    {
        if (hi < lo) (lo, hi) = (hi, lo);
        switch (Kind)
        {
            case SegKind.Line:
            {
                var d = P1 - P0;
                double t = Vec2.Dot(p - P0, d) / d.LengthSquared;
                return Math.Clamp(t, lo, hi);
            }
            case SegKind.Arc:
            {
                double tm = (lo + hi) / 2;
                double am = A0 + Sweep * tm;
                double ap = Math.Atan2(p.Y - Center.Y, p.X - Center.X);
                double delta = Math.IEEERemainder(ap - am, 2 * Math.PI);
                double t = tm + delta / Sweep;
                if (t >= lo && t <= hi) return t;
                return Vec2.Distance(Eval(lo), p) <= Vec2.Distance(Eval(hi), p) ? lo : hi;
            }
            default:
            {
                // Coarse scan, then Newton on (B(t) - p) · B'(t) = 0.
                const int N = 8;
                double best = lo, bestD = double.PositiveInfinity;
                for (int i = 0; i <= N; i++)
                {
                    double t = lo + (hi - lo) * i / N;
                    double dd = (Eval(t) - p).LengthSquared;
                    if (dd < bestD) { bestD = dd; best = t; }
                }
                double tt = best;
                for (int it = 0; it < 12; it++)
                {
                    var q = Eval(tt) - p;
                    var d1 = Deriv(tt);
                    var d2 = Deriv2(tt);
                    double f = Vec2.Dot(q, d1);
                    double df = Vec2.Dot(d1, d1) + Vec2.Dot(q, d2);
                    if (Math.Abs(df) < 1e-300) break;
                    double next = Math.Clamp(tt - f / df, lo, hi);
                    if (Math.Abs(next - tt) < 1e-15) { tt = next; break; }
                    tt = next;
                }
                return tt;
            }
        }
    }

    /// <summary>
    /// The piece from t0 to t1 (t1 &lt; t0 = reversed) as an output segment ending at <paramref name="end"/>; the piece
    /// is moved so that it starts at <paramref name="start"/> (both are the exact output vertices).
    /// </summary>
    public OutSeg Piece(double t0, double t1, Vec2 start, Vec2 end)
    {
        switch (Kind)
        {
            case SegKind.Line:
                return OutSeg.Line(end);
            case SegKind.Arc:
            {
                double tm = (t0 + t1) / 2;
                double sweep = Sweep * (t1 - t0);
                var through = Eval(tm);
                return OutSeg.Arc(through, end, Center, Radius, sweep, Id);
            }
            default:
            {
                Vec2 c1, c2;
                bool forward = t1 >= t0;
                double lo = Math.Min(t0, t1), hi = Math.Max(t0, t1);
                if (lo <= 0 && hi >= 1) { c1 = C1; c2 = C2; }
                else SubCubic(lo, hi, out c1, out c2);
                var a = Eval(lo);
                var b = Eval(hi);
                if (!forward) { (c1, c2) = (c2, c1); (a, b) = (b, a); }
                // Move the controls with their end points (exact when the ends did not move).
                c1 += start - a;
                c2 += end - b;
                return OutSeg.Cubic(c1, c2, end, Id);
            }
        }
    }

    /// <summary>Control points of the cubic restricted to [lo, hi] (blossoming).</summary>
    private void SubCubic(double lo, double hi, out Vec2 c1, out Vec2 c2)
    {
        c1 = Blossom(lo, lo, hi);
        c2 = Blossom(lo, hi, hi);
    }

    private Vec2 Blossom(double u, double v, double w)
    {
        // de Casteljau with three different parameters.
        var a = Vec2.Lerp(P0, C1, u); var b = Vec2.Lerp(C1, C2, u); var c = Vec2.Lerp(C2, P1, u);
        var d = Vec2.Lerp(a, b, v); var e = Vec2.Lerp(b, c, v);
        return Vec2.Lerp(d, e, w);
    }
}
