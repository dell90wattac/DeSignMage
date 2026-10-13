namespace DeSignMage.Core.Model;

/// <summary>
/// The colors of a gradient along its control line, as the legacy program blends them: every color is converted to HSV and each segment is
/// interpolated linearly in hue, saturation and value (alpha linearly), converted back to RGB. The hue takes the short way round the wheel;
/// <see cref="GradientPaint.AlternateColors"/> takes the long way, segment by segment. A white or gray end (and a black one) has no hue of its
/// own and takes the other end's, so blue to white never sweeps through other colors. The result is a dense list of plain stops that any
/// renderer (Skia, SVG, PDF) can use unchanged.
/// </summary>
public static class GradientRamp
{
    /// <summary>One color of the ramp at <paramref name="T"/> (0..1 along the control line).</summary>
    public readonly record struct Entry(double T, byte R, byte G, byte B, byte A);

    /// <summary>The ramp for a gradient, ascending in T, from 0 to 1.</summary>
    public static IReadOnlyList<Entry> Build(GradientPaint g)
    {
        var forward = BuildForward(g);
        if (!g.Reflect) return forward;

        // Reflect: the ramp runs from the start to the middle of the control line and back (t -> 1 - |2t - 1|).
        var mirrored = new List<Entry>(forward.Count * 2);
        foreach (var e in forward) mirrored.Add(e with { T = e.T / 2 });
        for (int i = forward.Count - 2; i >= 0; i--) mirrored.Add(forward[i] with { T = 1 - forward[i].T / 2 });
        return mirrored;
    }

    private static List<Entry> BuildForward(GradientPaint g)
    {
        var stops = g.Stops;
        var list = new List<Entry>(stops.Count * 12);
        list.Add(Of(stops[0].Offset, stops[0].Color));
        for (int i = 0; i + 1 < stops.Count; i++)
            AddSegment(list, stops[i], stops[i + 1], g.AlternateColors);
        return list;
    }

    private static Entry Of(double t, DocColor c) => new(t, c.R, c.G, c.B, c.A);

    private static void AddSegment(List<Entry> list, GradientNode a, GradientNode b, bool alternate)
    {
        var ca = a.Color; var cb = b.Color;
        if (a.Offset >= b.Offset || (ca.R == cb.R && ca.G == cb.G && ca.B == cb.B && ca.A == cb.A))
        {
            list.Add(Of(b.Offset, cb));   // equal colors or a zero-length segment: nothing in between
            return;
        }

        var (ha, sa, va) = ca.ToHsv();
        var (hb, sb, vb) = cb.ToHsv();
        bool achromaticA = Achromatic(sa, va), achromaticB = Achromatic(sb, vb);
        if (achromaticA && !achromaticB) ha = hb;
        else if (achromaticB && !achromaticA) hb = ha;
        // Black has no saturation of its own either: black to red only darkens the red instead of washing out through gray.
        if (va < 0.5 && vb >= 0.5) sa = sb;
        else if (vb < 0.5 && va >= 0.5) sb = sa;

        // The two ways round the wheel: the short one (|d| <= 0.5) and the other.
        double d = (hb - ha) / 360;
        if (d > 0.5) d -= 1; else if (d < -0.5) d += 1;
        if (alternate && d != 0) d = d > 0 ? d - 1 : d + 1;

        int steps = 8 + (int)Math.Min(32, Math.Ceiling(Math.Abs(d) * 360 / 6));
        for (int i = 1; i < steps; i++)
        {
            double u = (double)i / steps;
            var (r, g, bl) = ColorMath.HsvToRgb(ha + 360 * d * u, sa + (sb - sa) * u, va + (vb - va) * u);
            byte alpha = (byte)Math.Round(ca.A + (cb.A - ca.A) * u);
            list.Add(new Entry(a.Offset + (b.Offset - a.Offset) * u, r, g, bl, alpha));
        }
        list.Add(Of(b.Offset, cb));
    }

    /// <summary>Gray, white and black have no hue worth keeping.</summary>
    private static bool Achromatic(double s, double v) => s < 0.5 || v < 0.5;

    /// <summary>The ramp color at <paramref name="t"/> (clamped to 0..1), for showing a stop's neighbor colors or a new stop's color.</summary>
    public static DocColor ColorAt(GradientPaint g, double t)
    {
        var ramp = BuildForward(g);
        t = Math.Clamp(t, 0, 1);
        for (int i = 1; i < ramp.Count; i++)
        {
            if (ramp[i].T < t) continue;
            var a = ramp[i - 1]; var b = ramp[i];
            double span = b.T - a.T;
            double u = span <= 1e-12 ? 1 : (t - a.T) / span;
            byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * u);
            return new DocColor(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B), L(a.A, b.A));
        }
        var last = ramp[^1];
        return new DocColor(last.R, last.G, last.B, last.A);
    }
}
