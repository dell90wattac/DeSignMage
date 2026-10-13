using DeSignMage.Core.Geometry;

namespace DeSignMage.Vectorize;

/// <summary>One path segment ending at <see cref="End"/>: a straight line, or a cubic Bezier with two control points.</summary>
internal readonly record struct Seg(bool IsLine, Vec2 C1, Vec2 C2, Vec2 End);

/// <summary>The fitted curve of one chain, walked in the chain's canonical direction.</summary>
internal sealed class ChainCurve(Vec2 start, Seg[] segs)
{
    public Vec2 Start { get; } = start;
    public Seg[] Segs { get; } = segs;
    public Vec2 End => Segs.Length == 0 ? Start : Segs[^1].End;

    /// <summary>The same curve walked the other way.</summary>
    public ChainCurve Reversed()
    {
        var rev = new Seg[Segs.Length];
        var from = Start;
        for (int i = 0; i < Segs.Length; i++)
        {
            var s = Segs[i];
            // Segment i runs from (i == 0 ? Start : Segs[i-1].End) to s.End; reversed it runs back and sits at index n-1-i.
            rev[Segs.Length - 1 - i] = new Seg(s.IsLine, s.C2, s.C1, from);
            from = s.End;
        }
        return new ChainCurve(End, rev);
    }
}

/// <summary>
/// Turns a staircase of pixel-corner points into a short list of lines and cubic Beziers: finds real corners, lightly
/// smooths the runs between them, and fits each run with Schneider's least-squares Bezier fit ("An Algorithm for
/// Automatically Fitting Digitized Curves", Graphics Gems, 1990).
/// </summary>
internal sealed class CurveFitter
{
    private readonly int _smoothPasses;
    private readonly double _fitTol, _lineTol, _cornerCos, _rdpEps, _cornerWindow, _minLine;

    /// <param name="o">Options (smoothness, corner angle).</param>
    /// <param name="scale">Working pixels per source pixel (only values above 1 matter). Edge noise from the source pixel grid
    /// grows with it, so smoothing, tolerances and feature sizes are all stated in source pixels.</param>
    public CurveFitter(VectorizeOptions o, double scale)
    {
        double s = Math.Max(1, scale);
        _smoothPasses = (int)Math.Clamp(Math.Round((0.5 + 3.5 * o.Smoothness) * s * s), 1, 40);
        _fitTol = (0.2 + 0.6 * o.Smoothness) * s;
        _lineTol = (0.55 + 0.6 * o.Smoothness) * s;
        _cornerCos = Math.Cos(o.CornerAngle * Math.PI / 180);
        _rdpEps = 0.9 * s;
        _cornerWindow = 4.0 * s;
        _minLine = 20 * s;
    }

    /// <summary>Fits one chain. For a closed chain the points are distinct (the first is not repeated at the end).</summary>
    public ChainCurve Fit(ReadOnlySpan<int> xs, ReadOnlySpan<int> ys, bool closed)
    {
        int n = xs.Length;
        var pts = new Vec2[n];
        for (int i = 0; i < n; i++) pts[i] = new Vec2(xs[i], ys[i]);
        return closed ? FitClosed(pts) : FitOpen(pts);
    }

    private ChainCurve FitOpen(Vec2[] pts)
    {
        int n = pts.Length;
        var segs = new List<Seg>();
        if (n < 2) return new ChainCurve(pts[0], []);

        var corners = new List<int> { 0 };
        foreach (int v in Rdp(pts, 0, n - 1, _rdpEps))
        {
            if (v == 0 || v == n - 1) continue;
            if (IsCorner(pts, v, closed: false)) corners.Add(v);
        }
        corners.Add(n - 1);

        var pos = new Vec2[corners.Count];
        for (int k = 0; k < corners.Count; k++)
            pos[k] = k == 0 || k == corners.Count - 1 ? pts[corners[k]] : RefineCorner(pts, corners[k - 1], corners[k], corners[k + 1], closed: false);

        for (int c = 0; c + 1 < corners.Count; c++)
        {
            var section = Slice(pts, corners[c], corners[c + 1], closed: false);
            section[0] = pos[c]; section[^1] = pos[c + 1];
            FitSection(section, segs);
        }
        return new ChainCurve(pos[0], Finish(pos[0], segs));
    }

    private ChainCurve FitClosed(Vec2[] pts)
    {
        int n = pts.Length;
        if (n < 4) return Polygon(pts);

        // Corners, evaluated on every polygon vertex of the loop (the loop's start is arbitrary).
        var closedPts = new Vec2[n + 1];
        Array.Copy(pts, closedPts, n);
        closedPts[n] = pts[0];

        // A loop only a few pixels around is a polygon, not a curve (a single pixel is a square, not a blob).
        if (n < 16)
        {
            var corner = Rdp(closedPts, 0, n, 0.5).Where(i => i < n).Select(i => pts[i]).ToArray();
            return Polygon(corner.Length >= 3 ? corner : pts);
        }
        var corners = new List<int>();
        foreach (int v in Rdp(closedPts, 0, n, _rdpEps))
        {
            if (v == n) continue;
            if (IsCorner(pts, v, closed: true)) corners.Add(v);
        }
        corners.Sort();

        var segs = new List<Seg>();
        if (corners.Count == 0)
        {
            // A loop with no corners may still have long straight sides (rounded rectangle): start at one and fit piecewise.
            int lineStart = FirstLineStart(closedPts);
            if (lineStart >= 0)
            {
                var rot = new Vec2[n + 1];
                for (int i = 0; i <= n; i++) rot[i] = pts[(lineStart + i) % n];
                FitPieces(rot, cyclic: true, segs);
                return new ChainCurve(rot[0], Finish(rot[0], segs));
            }

            // Smooth loop: smooth cyclically, then split in two with matching tangents at the joints.
            var sm = SmoothCyclic(pts, _smoothPasses);
            int half = n / 2;
            var a = sm[0];
            var tA = CenterTangent(sm, 0, true);
            var tB = CenterTangent(sm, half, true);
            var s1 = Slice(sm, 0, half, closed: true);
            var s2 = Slice(sm, half, n, closed: true);
            FitSmooth(s1, tA, -tB, segs);
            FitSmooth(s2, tB, -tA, segs);
            return new ChainCurve(a, Finish(a, segs));
        }

        // Walk the loop corner to corner.
        var pos = new Vec2[corners.Count];
        for (int k = 0; k < corners.Count; k++)
        {
            int prev = corners[(k + corners.Count - 1) % corners.Count], next = corners[(k + 1) % corners.Count];
            pos[k] = corners.Count < 2 ? pts[corners[k]] : RefineCorner(pts, prev, corners[k], next, closed: true);
        }
        for (int c = 0; c < corners.Count; c++)
        {
            int from = corners[c], to = corners[(c + 1) % corners.Count];
            if (to <= from) to += n;
            var section = Slice(pts, from, to, closed: true);
            section[0] = pos[c]; section[^1] = pos[(c + 1) % corners.Count];
            FitSection(section, segs);
        }
        return new ChainCurve(pos[0], Finish(pos[0], segs));
    }

    /// <summary>
    /// Sharp corners of a pixel outline are blurred over a pixel or two. This moves a corner to where the straight
    /// runs on either side, fitted by least squares, would meet; if either side is not straight it stays put.
    /// </summary>
    private Vec2 RefineCorner(Vec2[] pts, int prevBreak, int corner, int nextBreak, bool closed)
    {
        var armA = CollectArm(pts, corner, prevBreak, forward: false, closed);
        var armB = CollectArm(pts, corner, nextBreak, forward: true, closed);
        if (armA is null || armB is null) return pts[corner];
        if (!FitLine(armA, out var ca, out var da, out double devA) || !FitLine(armB, out var cb, out var db, out double devB)) return pts[corner];
        if (devA > 0.7 * Math.Max(1, _rdpEps / 0.9) || devB > 0.7 * Math.Max(1, _rdpEps / 0.9)) return pts[corner];
        double cross = Vec2.Cross(da, db);
        if (Math.Abs(cross) < Math.Sin(20 * Math.PI / 180)) return pts[corner];
        double t = Vec2.Cross(cb - ca, db) / cross;
        var p = ca + da * t;
        return Vec2.Distance(p, pts[corner]) <= 1.5 * _rdpEps / 0.9 * 1.5 ? p : pts[corner];
    }

    /// <summary>Points on one side of a corner, skipping the blurred stretch next to it and the one next to the far break.</summary>
    private List<Vec2>? CollectArm(Vec2[] pts, int corner, int stop, bool forward, bool closed)
    {
        double s = _rdpEps / 0.9;
        double skip = 2.0 * s, maxLen = 60 * s;
        int n = pts.Length;
        // Path length to the far break.
        double total = 0;
        int cur = corner;
        for (int steps = 0; steps < n; steps++)
        {
            int next = forward ? cur + 1 : cur - 1;
            if (closed) next = (next % n + n) % n; else if (next < 0 || next >= n) break;
            total += Vec2.Distance(pts[cur], pts[next]);
            cur = next;
            if (cur == stop) break;
        }
        if (total < 2 * skip + 4 * s) return null;
        var arm = new List<Vec2>();
        double acc = 0;
        cur = corner;
        for (int steps = 0; steps < n; steps++)
        {
            int next = forward ? cur + 1 : cur - 1;
            if (closed) next = (next % n + n) % n; else if (next < 0 || next >= n) break;
            acc += Vec2.Distance(pts[cur], pts[next]);
            cur = next;
            if (acc >= skip && acc <= total - skip && acc <= maxLen) arm.Add(pts[cur]);
            if (cur == stop || acc > maxLen) break;
        }
        return arm.Count >= 4 ? arm : null;
    }

    /// <summary>Total-least-squares line through the points: a point on it, a unit direction, and the largest distance of any point from it.</summary>
    private static bool FitLine(List<Vec2> p, out Vec2 center, out Vec2 dir, out double maxDev)
    {
        double cx = 0, cy = 0;
        foreach (var v in p) { cx += v.X; cy += v.Y; }
        cx /= p.Count; cy /= p.Count;
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var v in p) { double dx = v.X - cx, dy = v.Y - cy; sxx += dx * dx; sxy += dx * dy; syy += dy * dy; }
        double theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        center = new Vec2(cx, cy);
        dir = new Vec2(Math.Cos(theta), Math.Sin(theta));
        maxDev = 0;
        foreach (var v in p) maxDev = Math.Max(maxDev, Math.Abs(Vec2.Cross(dir, v - center)));
        return true;
    }
    /// <summary>Index of the start of a long straight edge of the loop (closed point array, first == last), or -1.</summary>
    private int FirstLineStart(Vec2[] closedPts)
    {
        var verts = Rdp(closedPts, 0, closedPts.Length - 1, _rdpEps);
        for (int k = 0; k + 1 < verts.Count; k++)
        {
            int a = verts[k], b = verts[k + 1];
            double len = Vec2.Distance(closedPts[a], closedPts[b]);
            if (len >= _minLine && MaxDeviation(closedPts, a, b) <= Math.Min(_lineTol, 0.6 * _rdpEps / 0.9)) return a % (closedPts.Length - 1);
        }
        return -1;
    }

    private static ChainCurve Polygon(Vec2[] pts)
    {
        var segs = new Seg[pts.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            var e = pts[(i + 1) % pts.Length];
            segs[i] = new Seg(true, default, default, e);
        }
        return new ChainCurve(pts[0], segs);
    }

    /// <summary>Points from index a to b inclusive; for closed chains indices wrap around.</summary>
    private static Vec2[] Slice(Vec2[] pts, int a, int b, bool closed)
    {
        int len = b - a + 1;
        var r = new Vec2[len];
        for (int i = 0; i < len; i++) r[i] = pts[closed ? (a + i) % pts.Length : a + i];
        return r;
    }

    private bool IsCorner(Vec2[] pts, int i, bool closed)
    {
        double window = _cornerWindow;
        int n = pts.Length;
        var before = WalkBack(pts, i, window, closed, forward: false);
        var after = WalkBack(pts, i, window, closed, forward: true);
        if (before.Dist < window * 0.4 || after.Dist < window * 0.4) return false;
        var v1 = pts[i] - before.Point;
        var v2 = after.Point - pts[i];
        double l1 = v1.Length, l2 = v2.Length;
        if (l1 < 1e-9 || l2 < 1e-9) return false;
        double cos = Vec2.Dot(v1, v2) / (l1 * l2);
        return cos <= _cornerCos;   // turning angle at least CornerAngle
    }

    /// <summary>Point reached after walking <paramref name="dist"/> of path length (or as far as the chain allows).</summary>
    private static (Vec2 Point, double Dist) WalkBack(Vec2[] pts, int i, double dist, bool closed, bool forward)
    {
        int n = pts.Length;
        double acc = 0;
        int cur = i;
        for (int steps = 0; steps < n; steps++)
        {
            int next = forward ? cur + 1 : cur - 1;
            if (closed) next = (next % n + n) % n;
            else if (next < 0 || next >= n) break;
            acc += Vec2.Distance(pts[cur], pts[next]);
            cur = next;
            if (acc >= dist) break;
        }
        return (pts[cur], acc);
    }

    /// <summary>
    /// Fits one section between two hard end points (corners or chain ends). Long straight runs become exact lines;
    /// what lies between them is fitted with Béziers whose end tangents match the neighboring lines, so a rounded
    /// rectangle comes out as four lines and four smooth arcs rather than one lumpy loop.
    /// </summary>
    private void FitSection(Vec2[] raw, List<Seg> output) => FitPieces(raw, cyclic: false, output);

    

    /// <summary>
    /// <paramref name="raw"/> is a run of staircase points. With <paramref name="cyclic"/> the run starts and ends at the
    /// same point (a loop) and the pieces on either side of that point count as neighbors.
    /// </summary>
    private void FitPieces(Vec2[] raw, bool cyclic, List<Seg> output)
    {
        int n = raw.Length;
        if (n < 2) return;
        var verts = Rdp(raw, 0, n - 1, _rdpEps);
        if (verts.Count == 2 && !cyclic)
        {
            // One edge. A straight section between corners is a line, however short.
            if (MaxDeviation(raw, 0, n - 1) <= _lineTol + 0.15) { output.Add(new Seg(true, default, default, raw[^1])); return; }
        }

        // Classify polygon edges: long and straight enough = line piece.
        var pieces = new List<(int A, int B, bool Line)>();
        for (int k = 0; k + 1 < verts.Count; k++)
        {
            int a = verts[k], b = verts[k + 1];
            double len = Vec2.Distance(raw[a], raw[b]);
            bool line = len >= _minLine && MaxDeviation(raw, a, b) <= Math.Min(_lineTol, 0.6 * _rdpEps / 0.9);
            if (!line && pieces.Count > 0 && !pieces[^1].Line) pieces[^1] = (pieces[^1].A, b, false);   // merge adjacent curve edges
            else pieces.Add((a, b, line));
        }

        for (int k = 0; k < pieces.Count; k++)
        {
            var (a, b, line) = pieces[k];
            if (line) { output.Add(new Seg(true, default, default, raw[b])); continue; }

            var prev = k > 0 ? pieces[k - 1] : cyclic ? pieces[^1] : default;
            var next = k + 1 < pieces.Count ? pieces[k + 1] : cyclic ? pieces[0] : default;
            bool prevLine = (k > 0 || cyclic) && prev.Line;
            bool nextLine = (k + 1 < pieces.Count || cyclic) && next.Line;

            var sm = SmoothOpen(raw[a..(b + 1)], _smoothPasses);
            var tl = prevLine ? Normalize(raw[prev.B] - raw[prev.A]) : EndTangent(sm, atStart: true);
            var tr = nextLine ? -Normalize(raw[next.B] - raw[next.A]) : EndTangent(sm, atStart: false);
            FitSmooth(sm, tl, tr, output);
        }
    }

    private static double MaxDeviation(Vec2[] p, int a, int b)
    {
        var chord = p[b] - p[a];
        double cl = chord.Length;
        double max = 0;
        for (int i = a + 1; i < b; i++)
            max = Math.Max(max, cl < 1e-9 ? Vec2.Distance(p[i], p[a]) : Math.Abs(Vec2.Cross(chord, p[i] - p[a])) / cl);
        return max;
    }
    private void FitSmooth(Vec2[] pts, Vec2 tl, Vec2 tr, List<Seg> output)
    {
        if (pts.Length < 2) return;
        FitCubic(pts, 0, pts.Length - 1, tl, tr, _fitTol, output, 0);
    }

    // ---- Smoothing and tangents ----

    private static Vec2[] SmoothOpen(Vec2[] p, int passes)
    {
        var a = (Vec2[])p.Clone();
        var b = new Vec2[a.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            b[0] = a[0]; b[^1] = a[^1];
            for (int i = 1; i < a.Length - 1; i++) b[i] = (a[i - 1] + a[i] * 2 + a[i + 1]) * 0.25;
            (a, b) = (b, a);
        }
        return a;
    }

    private static Vec2[] SmoothCyclic(Vec2[] p, int passes)
    {
        int n = p.Length;
        var a = (Vec2[])p.Clone();
        var b = new Vec2[n];
        for (int pass = 0; pass < passes; pass++)
        {
            for (int i = 0; i < n; i++) b[i] = (a[(i + n - 1) % n] + a[i] * 2 + a[(i + 1) % n]) * 0.25;
            (a, b) = (b, a);
        }
        return a;
    }

    private static Vec2 Normalize(Vec2 v)
    {
        double l = v.Length;
        return l < 1e-12 ? new Vec2(1, 0) : v / l;
    }

    /// <summary>Unit tangent pointing into the curve from its start (or from its end, walking back).</summary>
    private static Vec2 EndTangent(Vec2[] p, bool atStart)
    {
        int n = p.Length;
        var origin = atStart ? p[0] : p[n - 1];
        for (int k = 1; k < n; k++)
        {
            var q = atStart ? p[k] : p[n - 1 - k];
            if (Vec2.Distance(origin, q) >= 2.0 || k == n - 1) return Normalize(q - origin);
        }
        return new Vec2(1, 0);
    }

    private static Vec2 CenterTangent(Vec2[] p, int i, bool closed)
    {
        int n = p.Length, k = Math.Clamp(n / 8, 1, 4);
        var a = p[(i - k + n * 2) % n];
        var b = p[(i + k) % n];
        return Normalize(b - a);
    }

    // ---- Schneider's Bezier fit ----

    private void FitCubic(Vec2[] d, int first, int last, Vec2 t1, Vec2 t2, double tol, List<Seg> output, int depth)
    {
        int nPts = last - first + 1;
        if (nPts == 2)
        {
            double dist = Vec2.Distance(d[first], d[last]) / 3;
            output.Add(new Seg(false, d[first] + t1 * dist, d[last] + t2 * dist, d[last]));
            return;
        }

        var u = ChordLength(d, first, last);
        var bez = GenerateBezier(d, first, last, u, t1, t2);
        double maxErr = MaxError(d, first, last, bez, u, out int split);
        if (maxErr <= tol * tol)
        {
            output.Add(new Seg(false, bez[1], bez[2], bez[3]));
            return;
        }
        if (maxErr <= 9 * tol * tol)
        {
            for (int i = 0; i < 8; i++)
            {
                var up = Reparameterize(d, first, last, u, bez);
                bez = GenerateBezier(d, first, last, up, t1, t2);
                maxErr = MaxError(d, first, last, bez, up, out split);
                if (maxErr <= tol * tol)
                {
                    output.Add(new Seg(false, bez[1], bez[2], bez[3]));
                    return;
                }
                u = up;
            }
        }

        if (depth > 40 || split <= first || split >= last)
        {
            output.Add(new Seg(false, bez[1], bez[2], bez[3]));
            return;
        }
        var tc = Normalize(d[split - 1] - d[split + 1]);
        FitCubic(d, first, split, t1, tc, tol, output, depth + 1);
        FitCubic(d, split, last, -tc, t2, tol, output, depth + 1);
    }

    private static double[] ChordLength(Vec2[] d, int first, int last)
    {
        var u = new double[last - first + 1];
        for (int i = first + 1; i <= last; i++) u[i - first] = u[i - first - 1] + Vec2.Distance(d[i], d[i - 1]);
        double total = u[^1];
        if (total < 1e-12) { for (int i = 1; i < u.Length; i++) u[i] = (double)i / (u.Length - 1); return u; }
        for (int i = 1; i < u.Length; i++) u[i] /= total;
        return u;
    }

    private static Vec2[] GenerateBezier(Vec2[] d, int first, int last, double[] u, Vec2 t1, Vec2 t2)
    {
        var p0 = d[first]; var p3 = d[last];
        double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
        for (int i = 0; i < u.Length; i++)
        {
            double t = u[i], mt = 1 - t;
            double b0 = mt * mt * mt, b1 = 3 * t * mt * mt, b2 = 3 * t * t * mt, b3 = t * t * t;
            var a1 = t1 * b1; var a2 = t2 * b2;
            c00 += Vec2.Dot(a1, a1); c01 += Vec2.Dot(a1, a2); c11 += Vec2.Dot(a2, a2);
            var tmp = d[first + i] - (p0 * (b0 + b1) + p3 * (b2 + b3));
            x0 += Vec2.Dot(a1, tmp); x1 += Vec2.Dot(a2, tmp);
        }
        double det = c00 * c11 - c01 * c01;
        double segLen = Vec2.Distance(p0, p3);
        double al = 0, ar = 0;
        if (Math.Abs(det) > 1e-12)
        {
            al = (x0 * c11 - x1 * c01) / det;
            ar = (c00 * x1 - c01 * x0) / det;
        }
        double eps = 1e-6 * segLen;
        if (al < eps || ar < eps) al = ar = segLen / 3;
        return [p0, p0 + t1 * al, p3 + t2 * ar, p3];
    }

    private static Vec2 Eval(Vec2[] b, double t) => PathGeometry.EvalCubic(b[0], b[1], b[2], b[3], t);

    private static double MaxError(Vec2[] d, int first, int last, Vec2[] bez, double[] u, out int split)
    {
        double max = 0;
        split = (first + last) / 2;
        for (int i = first + 1; i < last; i++)
        {
            double e = (Eval(bez, u[i - first]) - d[i]).LengthSquared;
            if (e >= max) { max = e; split = i; }
        }
        return max;
    }

    private static double[] Reparameterize(Vec2[] d, int first, int last, double[] u, Vec2[] b)
    {
        var r = new double[u.Length];
        for (int i = 0; i < u.Length; i++)
        {
            double t = u[i], mt = 1 - t;
            var q = Eval(b, t);
            // first and second derivatives
            var q1 = ((b[1] - b[0]) * (mt * mt) + (b[2] - b[1]) * (2 * mt * t) + (b[3] - b[2]) * (t * t)) * 3;
            var q2 = ((b[2] - b[1] * 2 + b[0]) * mt + (b[3] - b[2] * 2 + b[1]) * t) * 6;
            var diff = q - d[first + i];
            double num = Vec2.Dot(diff, q1);
            double den = Vec2.Dot(q1, q1) + Vec2.Dot(diff, q2);
            r[i] = Math.Abs(den) < 1e-12 ? t : Math.Clamp(t - num / den, 0, 1);
        }
        // Parameters must stay ordered.
        for (int i = 1; i < r.Length; i++) if (r[i] < r[i - 1]) r[i] = r[i - 1];
        return r;
    }

    // ---- Cleanup ----

    /// <summary>Turns cubics whose control points lie on the chord into lines.</summary>
    private static Seg[] Finish(Vec2 start, List<Seg> segs)
    {
        var result = new Seg[segs.Count];
        var from = start;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (!s.IsLine)
            {
                var chord = s.End - from;
                double cl = chord.Length;
                if (cl < 1e-9) s = new Seg(true, default, default, s.End);
                else
                {
                    double d1 = Math.Abs(Vec2.Cross(chord, s.C1 - from)) / cl;
                    double d2 = Math.Abs(Vec2.Cross(chord, s.C2 - from)) / cl;
                    if (d1 < 0.08 && d2 < 0.08) s = new Seg(true, default, default, s.End);
                }
            }
            result[i] = s;
            from = s.End;
        }
        return result;
    }

    /// <summary>Indices of the Ramer-Douglas-Peucker polygon vertices between <paramref name="a"/> and <paramref name="b"/> inclusive, in order.</summary>
    private static List<int> Rdp(Vec2[] pts, int a, int b, double eps)
    {
        var keep = new List<int> { a };
        var stack = new Stack<(int, int)>();
        stack.Push((a, b));
        var marks = new bool[pts.Length];
        marks[a] = marks[b] = true;
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            if (e <= s + 1) continue;
            var chord = pts[e] - pts[s];
            double cl = chord.Length;
            double best = -1; int bi = -1;
            for (int i = s + 1; i < e; i++)
            {
                double dd = cl < 1e-12 ? Vec2.Distance(pts[i], pts[s]) : Math.Abs(Vec2.Cross(chord, pts[i] - pts[s])) / cl;
                if (dd > best) { best = dd; bi = i; }
            }
            if (best > eps)
            {
                marks[bi] = true;
                stack.Push((s, bi));
                stack.Push((bi, e));
            }
        }
        keep.Clear();
        for (int i = a; i <= b; i++) if (marks[i]) keep.Add(i);
        return keep;
    }
}





