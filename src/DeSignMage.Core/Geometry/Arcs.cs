namespace DeSignMage.Core.Geometry;

/// <summary>
/// Circular-arc math for arcs given by three points: start, a point on the arc ("through", normally the midpoint)
/// and end. Collinear points degrade to a straight line.
/// </summary>
public static class Arcs
{
    /// <summary>True if the transform maps circles to circles (rotation, uniform scale, mirror, translation).</summary>
    public static bool IsSimilarity(Affine2 m, double eps = 1e-9) =>
        (Math.Abs(m.M11 - m.M22) < eps && Math.Abs(m.M12 + m.M21) < eps) ||
        (Math.Abs(m.M11 + m.M22) < eps && Math.Abs(m.M12 - m.M21) < eps);

    /// <summary>Circle through three points. False if they are (nearly) collinear.</summary>
    public static bool TryCircle(Vec2 a, Vec2 b, Vec2 c, out Vec2 center, out double radius)
    {
        double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        double scale = Math.Max((b - a).LengthSquared, (c - a).LengthSquared);
        if (Math.Abs(d) <= 1e-9 * Math.Max(scale, 1e-12))
        {
            center = default;
            radius = 0;
            return false;
        }
        double a2 = a.LengthSquared, b2 = b.LengthSquared, c2 = c.LengthSquared;
        center = new Vec2(
            (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d,
            (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d);
        radius = (a - center).Length;
        return radius < 1e9;
    }

    /// <summary>Start angle and signed sweep (radians) of the arc start → through → end around <paramref name="center"/>.</summary>
    public static (double Start, double Sweep) Angles(Vec2 s, Vec2 through, Vec2 e, Vec2 center)
    {
        double a0 = Math.Atan2(s.Y - center.Y, s.X - center.X);
        double am = Math.Atan2(through.Y - center.Y, through.X - center.X);
        double a1 = Math.Atan2(e.Y - center.Y, e.X - center.X);
        double d1 = Norm(a1 - a0), dm = Norm(am - a0);
        double sweep = dm <= d1 ? d1 : d1 - 2 * Math.PI;
        if (Vec2.Distance(s, e) < 1e-12) sweep = dm <= Math.PI ? 2 * Math.PI : -2 * Math.PI;   // full circle
        return (a0, sweep);
    }

    private static double Norm(double a)
    {
        a %= 2 * Math.PI;
        return a < 0 ? a + 2 * Math.PI : a;
    }

    /// <summary>Point at parameter <paramref name="t"/> (0..1 along the sweep).</summary>
    public static Vec2 PointAt(Vec2 s, Vec2 through, Vec2 e, double t)
    {
        if (!TryCircle(s, through, e, out var c, out var r)) return Vec2.Lerp(s, e, t);
        var (a0, sweep) = Angles(s, through, e, c);
        double a = a0 + sweep * t;
        return c + new Vec2(Math.Cos(a), Math.Sin(a)) * r;
    }

    /// <summary>Unit tangent at parameter <paramref name="t"/>, in the direction of travel.</summary>
    public static Vec2 TangentAt(Vec2 s, Vec2 through, Vec2 e, double t)
    {
        if (!TryCircle(s, through, e, out var c, out _)) return Unit(e - s);
        var (a0, sweep) = Angles(s, through, e, c);
        double a = a0 + sweep * t;
        var tan = new Vec2(-Math.Sin(a), Math.Cos(a));
        return sweep >= 0 ? tan : -tan;
    }

    /// <summary>Cubic Béziers (at most 90° each) approximating the arc.</summary>
    public static IEnumerable<(Vec2 C1, Vec2 C2, Vec2 End)> ToCubics(Vec2 s, Vec2 through, Vec2 e)
    {
        if (!TryCircle(s, through, e, out var c, out var r))
        {
            yield return (Vec2.Lerp(s, e, 1 / 3.0), Vec2.Lerp(s, e, 2 / 3.0), e);
            yield break;
        }
        var (a0, sweep) = Angles(s, through, e, c);
        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2) - 1e-9));
        double step = sweep / n;
        double k = 4.0 / 3.0 * Math.Tan(step / 4);
        for (int i = 0; i < n; i++)
        {
            double a = a0 + step * i, b = a + step;
            var p0 = c + new Vec2(Math.Cos(a), Math.Sin(a)) * r;
            var p3 = i == n - 1 ? e : c + new Vec2(Math.Cos(b), Math.Sin(b)) * r;
            var c1 = p0 + new Vec2(-Math.Sin(a), Math.Cos(a)) * (k * r);
            var c2 = p3 - new Vec2(-Math.Sin(b), Math.Cos(b)) * (k * r);
            yield return (c1, c2, p3);
        }
    }

    /// <summary>Signed bulge of an arc: midpoint offset from the chord, as a fraction of the chord length.</summary>
    public static double Bulge(Vec2 s, Vec2 through, Vec2 e)
    {
        var chord = e - s;
        double len = chord.Length;
        if (len < 1e-12) return 0;
        var normal = new Vec2(-chord.Y, chord.X) / len;
        var mid = PointAt(s, through, e, 0.5);
        return Vec2.Dot(mid - (s + e) / 2, normal) / len;
    }

    /// <summary>The arc midpoint for a chord <paramref name="s"/>→<paramref name="e"/> with the given bulge.</summary>
    public static Vec2 ThroughFromBulge(Vec2 s, Vec2 e, double bulge)
    {
        var chord = e - s;
        double len = chord.Length;
        if (len < 1e-12) return s;
        var normal = new Vec2(-chord.Y, chord.X) / len;
        return (s + e) / 2 + normal * (bulge * len);
    }

    private static Vec2 Unit(Vec2 v) => v.Length < 1e-12 ? new Vec2(1, 0) : v / v.Length;
}
