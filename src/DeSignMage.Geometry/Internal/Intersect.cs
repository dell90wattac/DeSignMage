using DeSignMage.Core.Geometry;

namespace DeSignMage.Geometry.Internal;

/// <summary>Exact intersections of lines and circles, and of source segments, near a known approximate point.</summary>
internal static class Intersect
{
    /// <summary>
    /// The exact crossing of segments <paramref name="a"/> and <paramref name="b"/> nearest to <paramref name="guess"/>
    /// (a crossing found on the flattened polylines). False when there is none within <paramref name="maxMove"/>.
    /// Near-tangent curves give their point of closest approach.
    /// </summary>
    public static bool Refine(Seg a, Seg b, Vec2 guess, double maxMove, out double ta, out double tb, out Vec2 p)
    {
        ta = tb = 0;
        p = guess;
        Vec2? best = null;
        if (a.Kind != SegKind.Cubic && b.Kind != SegKind.Cubic)
        {
            best = (a.Kind, b.Kind) switch
            {
                (SegKind.Line, SegKind.Line) => LineLine(a.P0, a.P1 - a.P0, b.P0, b.P1 - b.P0),
                (SegKind.Line, SegKind.Arc) => LineCircle(a.P0, a.P1 - a.P0, b.Center, b.Radius, guess),
                (SegKind.Arc, SegKind.Line) => LineCircle(b.P0, b.P1 - b.P0, a.Center, a.Radius, guess),
                _ => CircleCircle(a.Center, a.Radius, b.Center, b.Radius, guess),
            };
            if (best is null) return false;
            p = best.Value;
            ta = a.Project(p, 0, 1);
            tb = b.Project(p, 0, 1);
        }
        else
        {
            ta = a.Project(guess, 0, 1);
            tb = b.Project(guess, 0, 1);
            ClosestApproach(a, b, ref ta, ref tb);
            p = (a.Eval(ta) + b.Eval(tb)) / 2;
        }
        // The curves must really meet (or touch within tolerance) at the point, on both segments.
        if (Vec2.Distance(a.Eval(ta), p) > maxMove || Vec2.Distance(b.Eval(tb), p) > maxMove) return false;
        // How far the flattened crossing may sit from the exact one grows as the crossing gets shallower: two polylines
        // within tolerance of each other can cross anywhere along a near-tangent stretch (capped at the stretch's length).
        double sin = Math.Abs(Vec2.Cross(a.Tangent(ta), b.Tangent(tb)));
        double reach = Math.Min(LocalRadius(a), LocalRadius(b));
        double allowed = Math.Min(maxMove / Math.Max(sin, 1e-6), maxMove + 8 * Math.Sqrt(maxMove * reach));
        return Vec2.Distance(p, guess) <= Math.Max(allowed, maxMove);
    }

    /// <summary>Radius of curvature scale of a segment (lines: their length, cubics: their chord).</summary>
    private static double LocalRadius(Seg s) => s.Kind switch
    {
        SegKind.Arc => s.Radius,
        _ => Math.Max(Vec2.Distance(s.P0, s.P1), 1e-300),
    };

    /// <summary>
    /// Damped Gauss–Newton on A(ta) − B(tb): converges to the crossing when there is one and to the point of closest
    /// approach of near-tangent curves otherwise.
    /// </summary>
    private static void ClosestApproach(Seg a, Seg b, ref double ta, ref double tb)
    {
        double lambda = 1e-9;
        var f = a.Eval(ta) - b.Eval(tb);
        double cost = f.LengthSquared;
        for (int it = 0; it < 60 && cost > 1e-32; it++)
        {
            var da = a.Deriv(ta);
            var db = -b.Deriv(tb);
            // Normal equations (J^T J + λ diag) δ = −J^T f, J = [da db].
            double a11 = Vec2.Dot(da, da), a12 = Vec2.Dot(da, db), a22 = Vec2.Dot(db, db);
            double g1 = Vec2.Dot(da, f), g2 = Vec2.Dot(db, f);
            bool improved = false;
            for (int tries = 0; tries < 12 && !improved; tries++)
            {
                double m11 = a11 * (1 + lambda) + 1e-300, m22 = a22 * (1 + lambda) + 1e-300;
                double det = m11 * m22 - a12 * a12;
                if (Math.Abs(det) < 1e-300) { lambda *= 10; continue; }
                double d1 = -(g1 * m22 - g2 * a12) / det;
                double d2 = -(m11 * g2 - a12 * g1) / det;
                double na = Math.Clamp(ta + d1, 0, 1), nb = Math.Clamp(tb + d2, 0, 1);
                var nf = a.Eval(na) - b.Eval(nb);
                double nc = nf.LengthSquared;
                if (nc < cost)
                {
                    bool done = Math.Abs(na - ta) < 1e-15 && Math.Abs(nb - tb) < 1e-15;
                    ta = na; tb = nb; f = nf; cost = nc;
                    lambda = Math.Max(lambda / 10, 1e-12);
                    improved = true;
                    if (done) return;
                }
                else lambda *= 10;
            }
            if (!improved) return;
        }
    }

    /// <summary>Crossing of two infinite lines (point + direction), or null if parallel.</summary>
    public static Vec2? LineLine(Vec2 p, Vec2 d, Vec2 q, Vec2 e)
    {
        double det = Vec2.Cross(d, e);
        if (Math.Abs(det) <= 1e-14 * d.Length * e.Length) return null;
        double s = Vec2.Cross(q - p, e) / det;
        return p + d * s;
    }

    /// <summary>Line/circle crossing nearest to <paramref name="guess"/>; a near-miss gives the closest point.</summary>
    public static Vec2? LineCircle(Vec2 p, Vec2 d, Vec2 c, double r, Vec2 guess)
    {
        double len = d.Length;
        if (len < 1e-300) return null;
        var u = d / len;
        var f = p - c;
        double b = Vec2.Dot(f, u);
        double disc = b * b - (f.LengthSquared - r * r);
        if (disc < 0)
        {
            // Closest approach: the foot of the perpendicular from the centre, pushed onto the circle.
            var foot = p - u * b;
            var dir = foot - c;
            double dl = dir.Length;
            return dl < 1e-300 ? null : (foot + (c + dir / dl * r)) / 2;
        }
        double sq = Math.Sqrt(disc);
        var p1 = p + u * (-b + sq);
        var p2 = p + u * (-b - sq);
        return Vec2.Distance(p1, guess) <= Vec2.Distance(p2, guess) ? p1 : p2;
    }

    /// <summary>Circle/circle crossing nearest to <paramref name="guess"/>; tangent or near-miss circles give the closest point.</summary>
    public static Vec2? CircleCircle(Vec2 c1, double r1, Vec2 c2, double r2, Vec2 guess)
    {
        var d = c2 - c1;
        double dist = d.Length;
        if (dist < 1e-12 * Math.Max(r1, r2)) return null;     // concentric
        var u = d / dist;
        double a = (r1 * r1 - r2 * r2 + dist * dist) / (2 * dist);
        double h2 = r1 * r1 - a * a;
        var mid = c1 + u * a;
        if (h2 <= 0) return mid;
        double h = Math.Sqrt(h2);
        var perp = new Vec2(-u.Y, u.X) * h;
        var q1 = mid + perp;
        var q2 = mid - perp;
        return Vec2.Distance(q1, guess) <= Vec2.Distance(q2, guess) ? q1 : q2;
    }
}
