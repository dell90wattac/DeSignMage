using Clipper2ZLib;
using DeSignMage.Core.Geometry;

namespace DeSignMage.Geometry.Internal;

/// <summary>
/// Turns a tagged Clipper result loop back into lines, arcs and cubics: runs of result edges that follow one source
/// segment become one piece of that segment, and cut points between two source segments are moved onto their exact
/// crossing.
/// </summary>
internal static class Reconstruct
{
    private struct Edge
    {
        public int Seg;          // source segment id, or -1 (unknown: drawn as a straight line)
        public double Ta, Tb;    // parameters of its start and end vertex on Seg
    }

    public static OutLoop? Loop(Path64 path, TagTable tags, double tol, double eps, bool keepCurves)
    {
        int n = path.Count;
        if (n < 3) return null;
        var grid = tags.Grid;
        var pos = new Vec2[n];
        var on = new int[n][];
        for (int i = 0; i < n; i++)
        {
            long z = path[i].Z;
            pos[i] = TagTable.IsVertex(z) ? tags.VertexPos((int)(z - 1)) : grid.From(path[i]);
            on[i] = keepCurves ? tags.OnSet(z) : [];
        }

        var loop = new OutLoop { Closed = true };
        if (!keepCurves)
        {
            loop.Start = pos[0];
            for (int i = 1; i <= n; i++) loop.Segs.Add(OutSeg.Line(pos[i % n]));
            OutPaths.Simplify(loop, eps);
            return loop;
        }

        // 1. Which source segment does each result edge follow?
        double maxDev = 2 * tol + 8 * grid.Unit;
        var edges = new Edge[n];
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            edges[i] = new Edge { Seg = -1 };
            Seg? bestSeg = null;
            double bestA = 0, bestB = 0;
            foreach (int e in on[i])
            {
                if (Array.IndexOf(on[j], e) < 0) continue;
                var (seg, t0, t1) = tags.EdgeInterval(e);
                if (bestSeg is not null && (bestSeg.IsCurve || !seg.IsCurve)) continue;    // prefer curves
                double ta = Snap(seg.Project(pos[i], t0, t1), t0, t1);
                double tb = Snap(seg.Project(pos[j], t0, t1), t0, t1);
                // The edge must really lie along the segment (its midpoint near the curve).
                var mid = seg.Eval((ta + tb) / 2);
                if (Vec2.Distance(mid, (pos[i] + pos[j]) / 2) > maxDev) continue;
                if (Vec2.Distance(seg.Eval(ta), pos[i]) > maxDev || Vec2.Distance(seg.Eval(tb), pos[j]) > maxDev) continue;
                bestSeg = seg; bestA = ta; bestB = tb;
            }
            if (bestSeg is not null) edges[i] = new Edge { Seg = bestSeg.Id, Ta = bestA, Tb = bestB };
        }

        // 2. Group consecutive edges into runs along one segment.
        bool SameRun(int a, int b)
        {
            var ea = edges[a]; var eb = edges[b];
            if (ea.Seg < 0 || ea.Seg != eb.Seg) return false;
            // Continuous along the segment (cut points from coincident edges sit a grid step off the curve, so their
            // parameters from two neighbouring edges differ slightly).
            if (Math.Abs(ea.Tb - eb.Ta) > 1e-6) return false;
            int da = Math.Sign(ea.Tb - ea.Ta), db = Math.Sign(eb.Tb - eb.Ta);
            return da == 0 || db == 0 || da == db;
        }

        int start = -1;
        for (int i = 0; i < n; i++)
            if (!SameRun((i + n - 1) % n, i)) { start = i; break; }
        bool forcedSplit = start < 0;       // the whole loop is one segment (a full circle): split it in two
        if (forcedSplit) start = 0;

        var runs = new List<(int First, int Last)>();
        {
            int first = start;
            for (int k = 1; k <= n; k++)
            {
                int i = (start + k) % n;
                int prev = (i + n - 1) % n;
                bool boundary = k == n || !SameRun(prev, i) || (forcedSplit && k == n / 2);
                if (boundary) { runs.Add((first, prev)); first = i; }
            }
        }

        // 3. Cut points between two runs: move them onto the exact crossing of the two segments.
        var tStart = new double[runs.Count];
        var tEnd = new double[runs.Count];
        for (int r = 0; r < runs.Count; r++) { tStart[r] = edges[runs[r].First].Ta; tEnd[r] = edges[runs[r].Last].Tb; }
        for (int r = 0; r < runs.Count; r++)
        {
            int rn = (r + 1) % runs.Count;
            int v = (runs[r].Last + 1) % n;
            if (TagTable.IsVertex(path[v].Z)) continue;        // a source vertex: already exact
            int sa = edges[runs[r].Last].Seg, sb = edges[runs[rn].First].Seg;
            if (sa < 0 || sb < 0 || sa == sb) continue;
            if (Intersect.Refine(tags.Segs[sa], tags.Segs[sb], pos[v], maxDev, out double ta, out double tb, out var p))
            {
                pos[v] = p;
                tEnd[r] = ta;
                tStart[rn] = tb;
            }
        }

        // 4. Emit.
        loop.Start = pos[runs[0].First];
        for (int r = 0; r < runs.Count; r++)
        {
            var (first, last) = runs[r];
            int endV = (last + 1) % n;
            int s = edges[first].Seg;
            if (s < 0)
            {
                // Unknown edges: one straight line per edge.
                for (int k = first; ; k = (k + 1) % n)
                {
                    loop.Segs.Add(OutSeg.Line(pos[(k + 1) % n]));
                    if (k == last) break;
                }
                continue;
            }
            loop.Segs.Add(tags.Segs[s].Piece(tStart[r], tEnd[r], loop.Current, pos[endV]));
        }
        OutPaths.Simplify(loop, eps);
        return loop.Segs.Count > 0 ? loop : null;
    }

    private static double Snap(double t, double t0, double t1)
    {
        if (Math.Abs(t - t0) < 1e-12) return t0;
        if (Math.Abs(t - t1) < 1e-12) return t1;
        return t;
    }
}
