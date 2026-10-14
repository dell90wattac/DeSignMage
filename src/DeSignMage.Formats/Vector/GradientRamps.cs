using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Vector;

/// <summary>
/// Axial and radial shadings (PDF and PostScript have the same two) as <see cref="GradientPaint"/>: a color ramp that is a function of one
/// parameter, sampled and thinned out to the stops that matter, on a control line or circle given in the shading's own space.
/// </summary>
internal static class GradientRamps
{
    private const int Samples = 32;
    private const double Tolerance = 2.5;   // largest color difference (0..255) a dropped stop may make

    /// <param name="type">2 axial (coords x0 y0 x1 y1), 3 radial (x0 y0 r0 x1 y1 r1).</param>
    /// <param name="colorAt">The color at a parameter of [t0, t1]; null = no color (drawn black).</param>
    /// <param name="breakpoints">Parameters where the ramp changes its formula; sampled on both sides.</param>
    /// <param name="toDoc">The shading's space to document points.</param>
    /// <param name="bounds">Bounds of the object, in document points: the control points are relative to them.</param>
    public static Paint Build(int type, double[] coords, double t0, double t1, Func<double, DocColor?> colorAt, IEnumerable<double> breakpoints,
        Affine2 toDoc, Rect2 bounds, double alpha, out string? note)
    {
        note = null;
        DocColor Scaled(DocColor c) => alpha >= 1 ? c : c with { A = (byte)Math.Round(c.A * alpha) };
        DocColor Color(double t) => Scaled(colorAt(t) ?? DocColor.Black);
        bool valid = (type == 2 && coords.Length >= 4) || (type == 3 && coords.Length >= 6);
        if (!valid || bounds.IsEmpty || bounds.Width < 1e-9 || bounds.Height < 1e-9 || Math.Abs(t1 - t0) < 1e-12)
        {
            if (!valid) note = $"A shading of type {type} (mesh or function based) is drawn as a flat color.";
            return new SolidPaint(Color((t0 + t1) / 2));
        }

        Vec2 Rel(Vec2 p) => GradientPaint.ToRelative(p, bounds);
        double span = t1 - t0;
        var breaks = breakpoints.ToList();
        if (type == 2)
        {
            var p0 = toDoc.Transform(new Vec2(coords[0], coords[1]));
            var p1 = toDoc.Transform(new Vec2(coords[2], coords[3]));
            if (Vec2.Distance(p0, p1) < 1e-9) return new SolidPaint(Color(t1));
            return new GradientPaint(GradientKind.Linear, [Rel(p0), Rel(p1)], Stops(t0, t1, Color, breaks, t => (t - t0) / span));
        }

        double x0 = coords[0], y0 = coords[1], r0 = Math.Abs(coords[2]), x1 = coords[3], y1 = coords[4], r1 = Math.Abs(coords[5]);
        double radius = Math.Max(r0, r1);
        if (radius < 1e-12) return new SolidPaint(Color((t0 + t1) / 2));
        if (Math.Abs(x0 - x1) > 1e-6 * Math.Max(1, radius) || Math.Abs(y0 - y1) > 1e-6 * Math.Max(1, radius))
            note = "A radial gradient whose circles have different centers is drawn around the end circle.";
        var c = toDoc.Transform(new Vec2(x1, y1));
        var e1 = toDoc.Transform(new Vec2(x1 + radius, y1)) - c;
        var e2 = toDoc.Transform(new Vec2(x1, y1 + radius)) - c;
        // the offset along the radius of the stop at parameter t; inside the smaller circle the color of its end carries on
        var stops = Stops(t0, t1, Color, breaks, t => (r0 + (t - t0) / span * (r1 - r0)) / radius);
        if (stops[0].Offset > 1e-6) stops.Insert(0, new GradientNode(0, stops[0].Color));
        bool circular = Math.Abs(e1.Length - e2.Length) < 1e-6 * Math.Max(e1.Length, 1) && Math.Abs(Vec2.Dot(e1, e2)) < 1e-6 * Math.Max(e1.LengthSquared, 1);
        return circular
            ? new GradientPaint(GradientKind.Circle, [Rel(c), Rel(c + e1)], stops)
            : new GradientPaint(GradientKind.Ellipse, [Rel(c), Rel(c + e1), Rel(c + e2)], stops);
    }

    private static List<GradientNode> Stops(double t0, double t1, Func<double, DocColor> color, List<double> breakpoints, Func<double, double> offsetOf)
    {
        var ts = new SortedSet<double>();
        for (int i = 0; i <= Samples; i++) ts.Add(t0 + (t1 - t0) * i / Samples);
        double lo = Math.Min(t0, t1), hi = Math.Max(t0, t1), eps = 1e-6 * (hi - lo);
        foreach (double b in breakpoints)
        {
            if (b <= lo || b >= hi) continue;
            ts.Add(b); ts.Add(b - eps); ts.Add(b + eps);
        }
        var points = ts.Select(t => (Offset: offsetOf(t), Color: color(t))).ToList();

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        Thin(points, 0, points.Count - 1, keep);
        var nodes = new List<GradientNode>();
        for (int i = 0; i < points.Count; i++)
            if (keep[i]) nodes.Add(new GradientNode(Math.Clamp(points[i].Offset, 0, 1), points[i].Color));
        nodes.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        if (nodes.Count < 2) nodes.Add(nodes[0]);
        if (nodes.Count > GradientPaint.MaxStops)
        {
            var thinned = new List<GradientNode>();
            for (int i = 0; i < GradientPaint.MaxStops; i++) thinned.Add(nodes[(int)Math.Round(i * (nodes.Count - 1.0) / (GradientPaint.MaxStops - 1))]);
            nodes = thinned;
        }
        return nodes;
    }

    private static void Thin(List<(double Offset, DocColor Color)> p, int lo, int hi, bool[] keep)
    {
        if (hi - lo < 2) return;
        double worst = 0; int at = -1;
        double span = p[hi].Offset - p[lo].Offset;
        for (int i = lo + 1; i < hi; i++)
        {
            double f = Math.Abs(span) < 1e-12 ? 0 : (p[i].Offset - p[lo].Offset) / span;
            double d = Diff(p[i].Color, p[lo].Color, p[hi].Color, f);
            if (d > worst) { worst = d; at = i; }
        }
        if (worst <= Tolerance) return;
        keep[at] = true;
        Thin(p, lo, at, keep);
        Thin(p, at, hi, keep);
    }

    private static double Diff(DocColor c, DocColor a, DocColor b, double f)
    {
        double R(double x, double y) => x + (y - x) * f;
        return Math.Max(Math.Max(Math.Abs(c.R - R(a.R, b.R)), Math.Abs(c.G - R(a.G, b.G))), Math.Max(Math.Abs(c.B - R(a.B, b.B)), Math.Abs(c.A - R(a.A, b.A))));
    }
}
