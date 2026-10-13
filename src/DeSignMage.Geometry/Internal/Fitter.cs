using DeSignMage.Core.Geometry;

namespace DeSignMage.Geometry.Internal;

/// <summary>
/// An exact line or circle a run of polyline points is known to lie on (from source tags: the offset of a source line or
/// arc, or a round corner around a source vertex), with the distance within which a point counts as on it.
/// </summary>
internal readonly record struct Carrier(bool IsCircle, Vec2 P, Vec2 Dir, double R, double Tol)
{
    public static Carrier Circle(Vec2 center, double r, double tol) => new(true, center, default, r, tol);

    public static Carrier Line(Vec2 p, Vec2 unitDir, double tol) => new(false, p, unitDir, 0, tol);

    public double Distance(Vec2 q) => IsCircle ? Math.Abs(Vec2.Distance(q, P) - R) : Math.Abs(Vec2.Cross(Dir, q - P));

    public Vec2 Project(Vec2 q)
    {
        if (!IsCircle) return P + Dir * Vec2.Dot(q - P, Dir);
        var d = q - P;
        double l = d.Length;
        return l < 1e-300 ? q : P + d / l * R;
    }
}

/// <summary>
/// Polyline → lines, arcs and (optionally) cubics within a tolerance. Greedy: from each point, the primitive that reaches
/// furthest wins, and exact carriers (hints) beat free fits. Corners (sharp turns, or points flagged hard) are kept as
/// vertices. Where two exact carriers meet, the vertex moves onto their exact crossing, also across a few points that no
/// carrier explains (an offset corner cut back by the flattening).
/// </summary>
internal static class Fitter
{
    private enum Kind : byte { Line, Arc, Cubic }

    private struct Prim
    {
        public Kind Kind;
        public int From, To;              // point indices
        public bool Hinted;
        public Carrier Carrier;           // hinted carrier, or the fitted line/circle (free fits)
        public Vec2 C1, C2;               // cubic controls (before end-point moves)
        public double ApproxSweep;        // arcs: signed sweep measured along the points
        public Vec2? ForcedStart;         // start moved onto an exact corner by a bridge
    }

    private const double MaxSweep = Math.PI + 1e-6;
    private const double HintBonus = 3;  // a free fit must reach this many points further to beat an exact carrier

    public static OutLoop? Fit(IReadOnlyList<Vec2> input, bool closed, Carrier[]?[]? hints, bool[]? hard,
        double tol, double cornerAngleRad, bool preferCubics, double eps)
    {
        // Drop repeated points (and the repeated end of a closed loop).
        var idx = new List<int>(input.Count);
        for (int i = 0; i < input.Count; i++)
            if (idx.Count == 0 || Vec2.Distance(input[idx[^1]], input[i]) > eps) idx.Add(i);
        if (closed && idx.Count > 1 && Vec2.Distance(input[idx[0]], input[idx[^1]]) <= eps) idx.RemoveAt(idx.Count - 1);
        int n = idx.Count;
        if (n < 2 || (closed && n < 3)) return null;

        // Turn angles and corners.
        var turn = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (!closed && (i == 0 || i == n - 1)) { turn[i] = Math.PI; continue; }
            var a = input[idx[(i + n - 1) % n]]; var b = input[idx[i]]; var c = input[idx[(i + 1) % n]];
            turn[i] = Math.Abs(Math.Atan2(Vec2.Cross(b - a, c - b), Vec2.Dot(b - a, c - b)));
        }
        var isHard = new bool[n];
        var isCorner = new bool[n];
        for (int i = 0; i < n; i++)
        {
            isHard[i] = (hard is not null && hard[idx[i]]) || (!closed && (i == 0 || i == n - 1));
            isCorner[i] = isHard[i] || turn[i] > cornerAngleRad;
        }

        // Closed: start at a hard corner, else the sharpest corner, else anywhere. If exact carriers are found, start
        // again in the middle of the longest one, so that no corner bridge straddles the start.
        int rot = 0;
        if (closed)
        {
            int h = Array.IndexOf(isHard, true);
            if (h >= 0) rot = h;
            else
            {
                double best = -1;
                for (int i = 0; i < n; i++) if (isCorner[i] && turn[i] > best) { best = turn[i]; rot = i; }
            }
        }
        var prims = Cover(rot, out var pts, out int m);
        if (closed)
        {
            int longest = -1, len = 0;
            for (int p = 0; p < prims.Count; p++)
                if (prims[p].Hinted && prims[p].To - prims[p].From > len) { len = prims[p].To - prims[p].From; longest = p; }
            if (longest >= 0 && len >= 2)
            {
                int mid = (prims[longest].From + prims[longest].To) / 2;
                prims = Cover((rot + mid) % n, out pts, out m);
            }
        }
        return Emit(prims, pts, m);

        // Greedy cover of the sequence starting at point `start` (closed: and back to it).
        List<Prim> Cover(int start, out Vec2[] pts, out int m)
        {
            m = closed ? n : n - 1;
            pts = new Vec2[m + 1];
            var hintOf = new Carrier[]?[m + 1];
            var hardAt = new bool[m + 1];
            var cornerAt = new bool[m + 1];
            for (int k = 0; k <= m; k++)
            {
                int i = (start + k) % n;
                pts[k] = input[idx[i]];
                hintOf[k] = hints?[idx[i]];
                hardAt[k] = isHard[i];
                cornerAt[k] = isCorner[i];
            }
            // Where a solid exact run begins (three points on one carrier, or a long edge along an exact line): free fits
            // stop there, so they cannot swallow it.
            var strong = new bool[m + 1];
            for (int k = 0; k + 1 <= m; k++)
                foreach (var c in Candidates(hintOf[k], hintOf[k + 1]))
                {
                    if (c.Distance(pts[k]) > c.Tol || c.Distance(pts[k + 1]) > c.Tol) continue;
                    if ((k + 2 <= m && c.Distance(pts[k + 2]) <= c.Tol) || (!c.IsCircle && Vec2.Distance(pts[k], pts[k + 1]) > 4 * tol))
                    { strong[k] = true; break; }
                }

            var prims = new List<Prim>();
            var P = pts;
            int at = 0;
            while (at < m)
            {
                int limitHard = at + 1, limitCorner = at + 1;
                while (limitHard < m && !hardAt[limitHard]) limitHard++;
                while (limitCorner < m && !cornerAt[limitCorner]) limitCorner++;
                int limitFree = limitCorner;
                for (int k = at + 1; k < limitFree; k++) if (strong[k]) { limitFree = k; break; }

                var best = new Prim { Kind = Kind.Line, From = at, To = at + 1 };
                double bestScore = 1;

                // a. exact carriers
                foreach (var c in Candidates(hintOf[at], hintOf[at + 1]))
                {
                    int j = ExtendCarrier(P, at, limitHard, c, tol, out double sweep);
                    if (j <= at) continue;
                    double score = j - at + HintBonus;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = new Prim { Kind = c.IsCircle ? Kind.Arc : Kind.Line, From = at, To = j, Hinted = true, Carrier = c, ApproxSweep = sweep };
                    }
                }
                // b. free line
                {
                    int j = Gallop(at, limitFree, j2 => LineFits(P, at, j2, tol));
                    double score = j - at;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = new Prim { Kind = Kind.Line, From = at, To = j };
                    }
                }
                // c. free arc
                if (limitFree - at >= 2)
                {
                    int j = Gallop(at + 1, limitFree, j2 => j2 - at >= 2 && ArcFits(P, at, j2, tol, out _, out _));
                    if (j - at >= 2 && ArcFits(P, at, j, tol, out var circ, out double sweep) && j - at - 0.25 > bestScore)
                    {
                        bestScore = j - at - 0.25;
                        best = new Prim { Kind = Kind.Arc, From = at, To = j, Carrier = circ, ApproxSweep = sweep };
                    }
                }
                // d. free cubic
                if (preferCubics && limitFree - at >= 3)
                {
                    var t0 = StartTangent(P, at, cornerAt[at]);
                    int mm = m;
                    int j = Gallop(at + 2, limitFree, j2 => j2 - at >= 3 &&
                        CubicFits(P, at, j2, t0, EndTangent(P, j2, cornerAt[j2] || j2 == mm, mm), tol, out _, out _));
                    // Asked for: cubics beat arcs of the same reach.
                    if (j - at >= 3 && CubicFits(P, at, j, t0, EndTangent(P, j, cornerAt[j] || j == m, m), tol, out var c1, out var c2)
                        && j - at - 0.125 > bestScore)
                    {
                        bestScore = j - at - 0.125;
                        best = new Prim { Kind = Kind.Cubic, From = at, To = j, C1 = c1, C2 = c2 };
                    }
                }
                if (best.Kind == Kind.Line && !best.Hinted)
                {
                    var dir = P[best.To] - P[at];
                    best.Carrier = Carrier.Line(P[at], dir / dir.Length, tol);
                }
                prims.Add(best);
                at = best.To;
            }

            // Bridges: a few unexplained points between two exact carriers (a corner cut back by flattening) are dropped
            // and the carriers meet at their exact crossing, if the dropped points hug the corner the carriers make
            // (a bevel or a real chamfer does not).
            for (int p = 0; p < prims.Count; p++)
            {
                if (!prims[p].Hinted) continue;
                int q = p + 1;
                while (q < prims.Count && !prims[q].Hinted) q++;
                if (q >= prims.Count || q == p + 1 || q - p > 6) continue;
                int i1 = prims[p].To, i2 = prims[q].From;
                var a = prims[p].Carrier; var b = prims[q].Carrier;
                if (SameCarrier(a, b) || Cross(a, b, P[i1]) is not { } x) continue;
                // The corner must lie ahead of the first run (lines): no doubling back.
                if (!a.IsCircle && Vec2.Dot(x - P[i1], P[i1] - P[prims[p].From]) < -tol * Vec2.Distance(P[i1], P[prims[p].From])) continue;
                bool hugs = true;
                for (int k = i1; k < i2 && hugs; k++)
                {
                    var mid = (P[k] + P[k + 1]) / 2;
                    hugs = DistanceToCorner(P[k + 1], P[i1], x, P[i2]) <= 2 * tol && DistanceToCorner(mid, P[i1], x, P[i2]) <= 2 * tol;
                }
                if (!hugs) continue;
                prims.RemoveRange(p + 1, q - p - 1);
                var next = prims[p + 1];
                next.From = i1;
                next.ForcedStart = x;
                prims[p + 1] = next;
            }
            return prims;
        }

        OutLoop? Emit(List<Prim> prims, Vec2[] pts, int m)
        {
            // Junctions: exact crossing of two exact carriers; otherwise snap onto the exact one.
            var joint = new Vec2[prims.Count + 1];
            for (int p = 0; p <= prims.Count; p++) joint[p] = pts[p == prims.Count ? m : prims[p].From];
            for (int p = 0; p <= prims.Count; p++)
            {
                if (p < prims.Count && prims[p].ForcedStart is { } forced) { joint[p] = forced; continue; }
                bool wrap = closed && (p == 0 || p == prims.Count);
                Prim? before = p > 0 ? prims[p - 1] : wrap ? prims[^1] : null;
                Prim? after = p < prims.Count ? prims[p] : wrap ? prims[0] : null;
                var q = joint[p];
                var moved = q;
                if (before is { Hinted: true } b && after is { Hinted: true } a)
                {
                    double maxMove = 2 * Math.Max(b.Carrier.Tol, a.Carrier.Tol) + 2 * tol;
                    var x = SameCarrier(b.Carrier, a.Carrier) ? b.Carrier.Project(q) : Cross(b.Carrier, a.Carrier, q);
                    if (x is { } xx && Vec2.Distance(xx, q) <= maxMove) moved = xx;
                    else if (b.Carrier.Distance(q) <= b.Carrier.Tol) moved = b.Carrier.Project(q);
                    else if (a.Carrier.Distance(q) <= a.Carrier.Tol) moved = a.Carrier.Project(q);
                }
                else if (before is { Hinted: true } b2 && b2.Carrier.Distance(q) <= b2.Carrier.Tol) moved = b2.Carrier.Project(q);
                else if (after is { Hinted: true } a2 && a2.Carrier.Distance(q) <= a2.Carrier.Tol) moved = a2.Carrier.Project(q);
                joint[p] = moved;
            }
            if (closed) joint[prims.Count] = joint[0];

            var loop = new OutLoop { Start = joint[0], Closed = closed };
            for (int p = 0; p < prims.Count; p++)
            {
                var pr = prims[p];
                var s = joint[p];
                var e = joint[p + 1];
                switch (pr.Kind)
                {
                    case Kind.Line:
                        loop.Segs.Add(OutSeg.Line(e));
                        break;
                    case Kind.Arc:
                    {
                        var c = pr.Carrier.P;
                        double r = pr.Carrier.R;
                        double a0 = Math.Atan2(s.Y - c.Y, s.X - c.X);
                        double a1 = Math.Atan2(e.Y - c.Y, e.X - c.X);
                        double sweep = pr.ApproxSweep + Math.IEEERemainder(a1 - a0 - pr.ApproxSweep, 2 * Math.PI);
                        double am = a0 + sweep / 2;
                        var through = c + new Vec2(Math.Cos(am), Math.Sin(am)) * r;
                        loop.Segs.Add(OutSeg.Arc(through, e, c, r, sweep, -1));
                        break;
                    }
                    default:
                    {
                        var c1 = pr.C1 + (s - pts[pr.From]);
                        var c2 = pr.C2 + (e - pts[pr.To]);
                        loop.Segs.Add(OutSeg.Cubic(c1, c2, e, -1));
                        break;
                    }
                }
            }
            OutPaths.Simplify(loop, eps);
            return loop.Segs.Count > 0 ? loop : null;
        }
    }

    private static IEnumerable<Carrier> Candidates(Carrier[]? a, Carrier[]? b)
    {
        if (a is not null) foreach (var c in a) yield return c;
        if (b is not null && !ReferenceEquals(a, b))
            foreach (var c in b)
                if (a is null || Array.IndexOf(a, c) < 0) yield return c;
    }

    /// <summary>
    /// How far from <paramref name="i"/> the points stay on the carrier (monotone along it, sweep at most 180°). A circle
    /// run of only two points must also keep its chord within tolerance of the arc (a bevel's ends lie on the corner's
    /// circle too, but the bevel is no arc).
    /// </summary>
    private static int ExtendCarrier(Vec2[] pts, int i, int limit, Carrier c, double tol, out double sweep)
    {
        sweep = 0;
        if (c.Distance(pts[i]) > c.Tol) return i;
        int j = i;
        int dirSign = 0;
        while (j < limit)
        {
            var q = pts[j + 1];
            if (c.Distance(q) > c.Tol) break;
            double step;
            if (c.IsCircle)
            {
                var u = pts[j] - c.P; var v = q - c.P;
                step = Math.Atan2(Vec2.Cross(u, v), Vec2.Dot(u, v));
                if (Math.Abs(step) > Math.PI / 2) break;
                if (Math.Abs(sweep + step) > MaxSweep) break;
            }
            else step = Vec2.Dot(q - pts[j], c.Dir);
            int sg = Math.Sign(step);
            if (sg != 0)
            {
                if (dirSign != 0 && sg != dirSign) break;
                dirSign = sg;
            }
            if (c.IsCircle) sweep += step;
            j++;
        }
        if (c.IsCircle && j == i + 1)
        {
            double half = Vec2.Distance(pts[i], pts[j]) / 2;
            double sagitta = c.R - Math.Sqrt(Math.Max(c.R * c.R - half * half, 0));
            if (sagitta > 2 * tol) return i;
        }
        return j;
    }

    /// <summary>Largest j in (lo, limit] for which <paramref name="ok"/> holds, by doubling then bisection (lo always ok).</summary>
    private static int Gallop(int lo, int limit, Func<int, bool> ok)
    {
        int good = lo, step = 1;
        int bad = -1;
        while (true)
        {
            int j = Math.Min(good + step, limit);
            if (j <= good) break;
            if (ok(j)) { good = j; if (j == limit) break; step *= 2; }
            else { bad = j; break; }
        }
        if (bad < 0) return good;
        while (bad - good > 1)
        {
            int mid = (good + bad) / 2;
            if (ok(mid)) good = mid; else bad = mid;
        }
        return good;
    }

    private static bool LineFits(Vec2[] pts, int i, int j, double tol)
    {
        var a = pts[i]; var d = pts[j] - a;
        double len = d.Length;
        if (len <= 0) return false;
        var u = d / len;
        double last = 0;
        for (int k = i + 1; k < j; k++)
        {
            var w = pts[k] - a;
            if (Math.Abs(Vec2.Cross(u, w)) > tol) return false;
            double along = Vec2.Dot(u, w);
            if (along < last - tol || along > len + tol) return false;    // no doubling back
            last = along;
        }
        return true;
    }

    /// <summary>Circle through pts[i] and pts[j] fitting the points between within tol (algebraic fit on the bisector).</summary>
    private static bool ArcFits(Vec2[] pts, int i, int j, double tol, out Carrier circle, out double sweep)
    {
        circle = default; sweep = 0;
        var o = pts[i];
        var pe = pts[j] - o;
        double chord = pe.Length;
        if (chord <= 0) return false;
        var mid = pe / 2;
        var nrm = new Vec2(-pe.Y, pe.X) / chord;
        double sab = 0, sbb = 0;
        for (int k = i + 1; k < j; k++)
        {
            var p = pts[k] - o;
            double a = p.LengthSquared - 2 * Vec2.Dot(mid, p);
            double b = 2 * Vec2.Dot(nrm, p);
            sab += a * b; sbb += b * b;
        }
        if (sbb < 1e-300) return false;
        double s = sab / sbb;
        var c = mid + nrm * s;
        double r = c.Length;
        if (r > 1e4 * chord) return false;            // practically straight: a line does it
        for (int k = i + 1; k < j; k++)
            if (Math.Abs(Vec2.Distance(pts[k] - o, c) - r) > tol) return false;
        // Monotone angle, sweep at most 180°.
        int sign = 0;
        for (int k = i; k < j; k++)
        {
            var u = pts[k] - o - c; var v = pts[k + 1] - o - c;
            double step = Math.Atan2(Vec2.Cross(u, v), Vec2.Dot(u, v));
            int sg = Math.Sign(step);
            if (sg != 0 && sign != 0 && sg != sign) return false;
            if (sg != 0) sign = sg;
            sweep += step;
        }
        if (Math.Abs(sweep) > MaxSweep) return false;
        circle = Carrier.Circle(c + o, r, tol);
        return true;
    }

    private static Vec2 Unit(Vec2 v)
    {
        double l = v.Length;
        return l < 1e-300 ? new Vec2(1, 0) : v / l;
    }

    private static Vec2 StartTangent(Vec2[] pts, int i, bool corner) =>
        corner || i == 0 ? Unit(pts[i + 1] - pts[i]) : Unit(pts[i + 1] - pts[i - 1]);

    /// <summary>Direction at j pointing back into the curve, as Schneider's method wants.</summary>
    private static Vec2 EndTangent(Vec2[] pts, int j, bool corner, int m) =>
        corner || j >= m ? Unit(pts[j - 1] - pts[j]) : Unit(pts[j - 1] - pts[j + 1]);

    /// <summary>One cubic through pts[i] and pts[j] with the given end tangents, fitting the points within tol.</summary>
    private static bool CubicFits(Vec2[] pts, int i, int j, Vec2 t1, Vec2 t2, double tol, out Vec2 c1, out Vec2 c2)
    {
        int count = j - i + 1;
        var u = new double[count];
        for (int k = 1; k < count; k++) u[k] = u[k - 1] + Vec2.Distance(pts[i + k], pts[i + k - 1]);
        double total = u[^1];
        c1 = c2 = default;
        if (total <= 0) return false;
        for (int k = 1; k < count; k++) u[k] /= total;

        for (int iter = 0; iter < 8; iter++)
        {
            Generate(pts, i, j, u, t1, t2, out c1, out c2);
            double err = MaxError(pts, i, j, u, c1, c2);
            if (err <= tol) return true;
            if (err > 64 * tol) return false;
            Reparameterize(pts, i, j, u, c1, c2);
        }
        return false;
    }

    private static void Generate(Vec2[] pts, int i, int j, double[] u, Vec2 t1, Vec2 t2, out Vec2 c1, out Vec2 c2)
    {
        var p0 = pts[i]; var p3 = pts[j];
        double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
        for (int k = 0; k < u.Length; k++)
        {
            double t = u[k], mt = 1 - t;
            double b0 = mt * mt * mt, b1 = 3 * t * mt * mt, b2 = 3 * t * t * mt, b3 = t * t * t;
            var a1 = t1 * b1; var a2 = t2 * b2;
            c00 += Vec2.Dot(a1, a1); c01 += Vec2.Dot(a1, a2); c11 += Vec2.Dot(a2, a2);
            var tmp = pts[i + k] - (p0 * (b0 + b1) + p3 * (b2 + b3));
            x0 += Vec2.Dot(a1, tmp); x1 += Vec2.Dot(a2, tmp);
        }
        double det = c00 * c11 - c01 * c01;
        double alpha1 = 0, alpha2 = 0;
        if (Math.Abs(det) > 1e-300)
        {
            alpha1 = (x0 * c11 - x1 * c01) / det;
            alpha2 = (c00 * x1 - c01 * x0) / det;
        }
        double seg = Vec2.Distance(p0, p3);
        double epsA = 1e-6 * seg;
        if (alpha1 < epsA || alpha2 < epsA) alpha1 = alpha2 = seg / 3;
        c1 = p0 + t1 * alpha1;
        c2 = p3 + t2 * alpha2;
    }

    private static double MaxError(Vec2[] pts, int i, int j, double[] u, Vec2 c1, Vec2 c2)
    {
        double max = 0;
        for (int k = 1; k < u.Length - 1; k++)
            max = Math.Max(max, Vec2.Distance(PathGeometry.EvalCubic(pts[i], c1, c2, pts[j], u[k]), pts[i + k]));
        return max;
    }

    private static void Reparameterize(Vec2[] pts, int i, int j, double[] u, Vec2 c1, Vec2 c2)
    {
        var p0 = pts[i]; var p3 = pts[j];
        for (int k = 1; k < u.Length - 1; k++)
        {
            double t = u[k], mt = 1 - t;
            var q = PathGeometry.EvalCubic(p0, c1, c2, p3, t);
            var d1 = 3 * mt * mt * (c1 - p0) + 6 * mt * t * (c2 - c1) + 3 * t * t * (p3 - c2);
            var d2 = 6 * mt * (c2 - 2 * c1 + p0) + 6 * t * (p3 - 2 * c2 + c1);
            var diff = q - pts[i + k];
            double num = Vec2.Dot(diff, d1);
            double den = Vec2.Dot(d1, d1) + Vec2.Dot(diff, d2);
            if (Math.Abs(den) > 1e-300) u[k] = Math.Clamp(t - num / den, 0, 1);
        }
    }

    /// <summary>Distance from <paramref name="q"/> to the two-segment path a → corner → b.</summary>
    private static double DistanceToCorner(Vec2 q, Vec2 a, Vec2 corner, Vec2 b) =>
        Math.Min(DistanceToSegment(q, a, corner), DistanceToSegment(q, corner, b));

    private static double DistanceToSegment(Vec2 q, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        double l2 = ab.LengthSquared;
        if (l2 <= 0) return Vec2.Distance(q, a);
        double t = Math.Clamp(Vec2.Dot(q - a, ab) / l2, 0, 1);
        return Vec2.Distance(q, a + ab * t);
    }

    private static bool SameCarrier(Carrier a, Carrier b)
    {
        if (a.IsCircle != b.IsCircle) return false;
        double eps = 1e-9 * (1 + a.P.Length + a.R);
        if (a.IsCircle) return Vec2.Distance(a.P, b.P) <= eps && Math.Abs(a.R - b.R) <= eps;
        return Math.Abs(Vec2.Cross(a.Dir, b.Dir)) <= 1e-12 && Math.Abs(Vec2.Cross(a.Dir, b.P - a.P)) <= eps;
    }

    /// <summary>Crossing of two carriers nearest to <paramref name="near"/> (tangent ones: their closest point).</summary>
    private static Vec2? Cross(Carrier a, Carrier b, Vec2 near)
    {
        if (!a.IsCircle && !b.IsCircle) return Intersect.LineLine(a.P, a.Dir, b.P, b.Dir);
        if (!a.IsCircle) return Intersect.LineCircle(a.P, a.Dir, b.P, b.R, near);
        if (!b.IsCircle) return Intersect.LineCircle(b.P, b.Dir, a.P, a.R, near);
        return Intersect.CircleCircle(a.P, a.R, b.P, b.R, near);
    }
}
