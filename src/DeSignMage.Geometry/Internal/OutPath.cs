using DeSignMage.Core.Geometry;

namespace DeSignMage.Geometry.Internal;

/// <summary>One segment of a result loop: a line, an arc (with its circle) or a cubic, ending at <see cref="End"/>.</summary>
internal readonly record struct OutSeg(
    SegKind Kind, Vec2 End, Vec2 Through, Vec2 C1, Vec2 C2, Vec2 Center, double Radius, double Sweep, int Source)
{
    public static OutSeg Line(Vec2 end) => new(SegKind.Line, end, default, default, default, default, 0, 0, -1);

    public static OutSeg Arc(Vec2 through, Vec2 end, Vec2 center, double radius, double sweep, int source) =>
        new(SegKind.Arc, end, through, default, default, center, radius, sweep, source);

    public static OutSeg Cubic(Vec2 c1, Vec2 c2, Vec2 end, int source) =>
        new(SegKind.Cubic, end, default, c1, c2, default, 0, 0, source);
}

/// <summary>A result loop under construction.</summary>
internal sealed class OutLoop
{
    public Vec2 Start;
    public readonly List<OutSeg> Segs = [];
    public bool Closed = true;

    public Vec2 Current => Segs.Count == 0 ? Start : Segs[^1].End;
}

internal static class OutPaths
{
    /// <summary>
    /// Tidies a loop: drops zero-length segments, merges consecutive collinear lines and consecutive arcs of one circle.
    /// <paramref name="eps"/> is the geometric epsilon (points).
    /// </summary>
    public static void Simplify(OutLoop loop, double eps)
    {
        var segs = loop.Segs;
        // 1. Zero-length segments.
        var list = new List<OutSeg>(segs.Count);
        var cur = loop.Start;
        foreach (var s in segs)
        {
            bool tiny = Vec2.Distance(s.End, cur) <= eps && s.Kind switch
            {
                SegKind.Line => true,
                SegKind.Arc => Math.Abs(s.Sweep) < Math.PI,
                _ => Vec2.Distance(s.C1, cur) <= eps && Vec2.Distance(s.C2, cur) <= eps,
            };
            if (tiny) continue;
            list.Add(s);
            cur = s.End;
        }
        if (loop.Closed && list.Count > 0 && Vec2.Distance(list[^1].End, loop.Start) > eps)
            list.Add(OutSeg.Line(loop.Start));          // closing line made explicit while tidying
        else if (loop.Closed && list.Count > 0)
            list[^1] = list[^1] with { End = loop.Start };

        // 2. Merge neighbours (also across the start of a closed loop, by rotating).
        bool changed = true;
        int guard = 0;
        while (changed && guard++ < 4)
        {
            changed = false;
            var merged = new List<OutSeg>(list.Count);
            var prevStart = loop.Start;     // start of merged[^1]
            foreach (var s in list)
            {
                if (merged.Count > 0 && TryMerge(prevStart, merged[^1], s, eps, out var m))
                {
                    merged[^1] = m;
                    changed = true;
                }
                else
                {
                    if (merged.Count > 0) prevStart = merged[^1].End;
                    merged.Add(s);
                }
            }
            list = merged;
            if (loop.Closed && list.Count >= 3)
            {
                // Last segment (lastStart → Start) and first (Start → ...) merged: the loop then starts at lastStart.
                var lastStart = list[^2].End;
                if (TryMerge(lastStart, list[^1], list[0], eps, out var wrap))
                {
                    loop.Start = lastStart;
                    list[0] = wrap;
                    list.RemoveAt(list.Count - 1);
                    changed = true;
                }
            }
        }
        segs.Clear();
        segs.AddRange(list);
    }

    private static bool TryMerge(Vec2 aStart, OutSeg a, OutSeg b, double eps, out OutSeg merged)
    {
        merged = default;
        if (a.Kind == SegKind.Line && b.Kind == SegKind.Line)
        {
            var d1 = a.End - aStart;
            var d2 = b.End - a.End;
            if (Vec2.Dot(d1, d2) <= 0) return false;
            var chord = b.End - aStart;
            double len = chord.Length;
            if (len <= eps) return false;
            double dev = Math.Abs(Vec2.Cross(chord, a.End - aStart)) / len;
            if (dev > eps) return false;
            merged = OutSeg.Line(b.End);
            return true;
        }
        if (a.Kind == SegKind.Arc && b.Kind == SegKind.Arc)
        {
            if (Vec2.Distance(a.Center, b.Center) > eps || Math.Abs(a.Radius - b.Radius) > eps) return false;
            if (Math.Sign(a.Sweep) != Math.Sign(b.Sweep)) return false;
            double sum = a.Sweep + b.Sweep;
            bool sameSource = a.Source >= 0 && a.Source == b.Source;
            if (Math.Abs(sum) >= 2 * Math.PI - 1e-6) return false;
            if (!sameSource && Math.Abs(sum) > Math.PI + 1e-9) return false;
            double a0 = Math.Atan2(aStart.Y - a.Center.Y, aStart.X - a.Center.X);
            double am = a0 + sum / 2;
            var through = a.Center + new Vec2(Math.Cos(am), Math.Sin(am)) * a.Radius;
            merged = OutSeg.Arc(through, b.End, a.Center, a.Radius, sum, sameSource ? a.Source : -1);
            return true;
        }
        return false;
    }

    /// <summary>Builds the path; a closed loop's final line back to its start is left to the Close verb.</summary>
    public static PathGeometry Build(IEnumerable<OutLoop> loops)
    {
        var b = new PathBuilder { FillRule = FillRule.NonZero };
        foreach (var loop in loops)
        {
            if (loop.Segs.Count == 0) continue;
            b.MoveTo(loop.Start);
            int n = loop.Segs.Count;
            for (int i = 0; i < n; i++)
            {
                var s = loop.Segs[i];
                bool last = i == n - 1;
                switch (s.Kind)
                {
                    case SegKind.Line:
                        if (last && loop.Closed && n > 1) break;   // Close draws it
                        b.LineTo(s.End);
                        break;
                    case SegKind.Arc: b.ArcTo(s.Through, s.End); break;
                    default: b.CubicTo(s.C1, s.C2, s.End); break;
                }
            }
            if (loop.Closed) b.Close();
        }
        return b.Build();
    }
}
