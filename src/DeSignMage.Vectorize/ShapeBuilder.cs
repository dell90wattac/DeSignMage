using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Vectorize;

/// <summary>
/// Turns regions into closed paths. Each shape is a set of boundary chains oriented so the shape is on their left;
/// loops are joined at junctions with the rule "turn left if you can, else go straight, else turn right", which keeps
/// regions 4-connected and handles checkerboard corners.
/// <para>
/// <b>Cut-out</b>: a shape is one region.<br/>
/// <b>Stacked</b>: regions are painted largest first. A region's shape is itself plus every smaller region that can be
/// reached from it through regions painted after it (so anything touching it is either below it or inside its shape).
/// That is built with a union-find sweep from smallest to largest region; the boundary of a union is the symmetric
/// difference of its members' chains (chains between two members cancel). Neighbors always end up in an
/// ancestor/descendant relation, which is why stacked output has no seams.
/// </para>
/// </summary>
internal sealed class ShapeBuilder
{
    private readonly RegionMap _map;
    private readonly BoundaryGraph _graph;
    private readonly CurveFitter _fitter;
    private readonly double _scale;
    private readonly ChainCurve?[] _curves;
    private readonly int[][] _chainsByRegion;

    public ShapeBuilder(RegionMap map, BoundaryGraph graph, CurveFitter fitter, double scale)
    {
        _map = map; _graph = graph; _fitter = fitter; _scale = scale;
        _curves = new ChainCurve?[graph.Chains.Count];
        _chainsByRegion = graph.ChainsByRegion(map.Count);
    }

    /// <summary>Returns, for each emitted region (in painting order, bottom first), its path.</summary>
    public List<(int Region, PathGeometry Path)> Build(bool[] emit, VectorizeLayering layering)
    {
        var result = new List<(int, PathGeometry)>();
        // Painting order: outermost first. Regions touching the image border are the "background"; each further ring of
        // neighbors sits on top of the one before, so a plaque on a wall is above the wall, and letters above the plaque.
        var depth = Depths();
        var order = Enumerable.Range(0, _map.Count).Where(r => emit[r])
            .OrderBy(r => depth[r]).ThenByDescending(r => _map.Area[r]).ThenBy(r => r).ToList();

        if (layering == VectorizeLayering.CutOut)
        {
            foreach (int r in order)
            {
                var entries = new List<int>(_chainsByRegion[r].Length);
                foreach (int c in _chainsByRegion[r]) entries.Add(c * 2 + (_graph.Chains[c].Left == r ? 0 : 1));
                result.Add((r, BuildPath(entries)));
            }
            return result;
        }

        // Stacked.
        var rank = new int[_map.Count];
        Array.Fill(rank, -1);
        for (int i = 0; i < order.Count; i++) rank[order[i]] = i;
        var uf = new int[_map.Count];
        for (int i = 0; i < uf.Length; i++) uf[i] = i;
        var sets = new Dictionary<int, sbyte>?[_map.Count];
        int Find(int x)
        {
            while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; }
            return x;
        }

        var shapes = new (int Region, PathGeometry Path)[order.Count];
        for (int i = order.Count - 1; i >= 0; i--)
        {
            int r = order[i];
            var s = new Dictionary<int, sbyte>(_chainsByRegion[r].Length);
            foreach (int c in _chainsByRegion[r]) s[c] = (sbyte)(_graph.Chains[c].Left == r ? 0 : 1);
            foreach (int c in _chainsByRegion[r])
            {
                var ch = _graph.Chains[c];
                int other = ch.Left == r ? ch.Right : ch.Left;
                if (other < 0 || rank[other] <= i) continue;   // outside, not painted, or painted before r
                int root = Find(other);
                if (root == r) continue;
                var b = sets[root]!;
                sets[root] = null;
                if (b.Count > s.Count) (s, b) = (b, s);
                foreach (var (k, v) in b) if (!s.Remove(k)) s[k] = v;
                uf[root] = r;
            }
            sets[r] = s;
            var entries = new List<int>(s.Count);
            foreach (var (c, o) in s) entries.Add(c * 2 + o);
            shapes[i] = (r, BuildPath(entries));
        }
        result.AddRange(shapes);
        return result;
    }

    /// <summary>Number of region-to-region steps from the image border (border-touching regions are 0).</summary>
    private int[] Depths()
    {
        var depth = new int[_map.Count];
        Array.Fill(depth, -1);
        var queue = new Queue<int>();
        foreach (var c in _graph.Chains)
        {
            if (c.Left >= 0 && c.Right < 0 && depth[c.Left] < 0) { depth[c.Left] = 0; queue.Enqueue(c.Left); }
            if (c.Right >= 0 && c.Left < 0 && depth[c.Right] < 0) { depth[c.Right] = 0; queue.Enqueue(c.Right); }
        }
        while (queue.Count > 0)
        {
            int r = queue.Dequeue();
            foreach (int ci in _chainsByRegion[r])
            {
                var c = _graph.Chains[ci];
                int other = c.Left == r ? c.Right : c.Left;
                if (other >= 0 && depth[other] < 0) { depth[other] = depth[r] + 1; queue.Enqueue(other); }
            }
        }
        for (int r = 0; r < depth.Length; r++) if (depth[r] < 0) depth[r] = 0;
        return depth;
    }

    private ChainCurve Curve(int chain)
    {
        var c = _curves[chain];
        if (c is not null) return c;
        var ch = _graph.Chains[chain];
        c = _fitter.Fit(_graph.PointX.AsSpan(ch.PointStart, ch.PointCount), _graph.PointY.AsSpan(ch.PointStart, ch.PointCount), ch.Closed);
        _curves[chain] = c;
        return c;
    }

    private PathGeometry BuildPath(List<int> entries)
    {
        var chains = _graph.Chains;
        // Where each oriented chain starts: (corner, direction of its first edge).
        var startAt = new Dictionary<long, int>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var ch = chains[entries[i] >> 1];
            if (ch.Closed) continue;
            startAt[Key(entries[i])] = i;
        }
        long Key(int entry)
        {
            var ch = chains[entry >> 1];
            return (entry & 1) == 0
                ? (long)ch.StartCorner * 4 + ch.FirstDir
                : (long)ch.EndCorner * 4 + ((ch.LastDir + 2) & 3);
        }

        var used = new bool[entries.Count];
        var b = new PathBuilder { FillRule = FillRule.EvenOdd };
        for (int first = 0; first < entries.Count; first++)
        {
            if (used[first]) continue;
            int cur = first;
            bool started = false;
            while (cur >= 0 && !used[cur])
            {
                used[cur] = true;
                int entry = entries[cur];
                var ch = chains[entry >> 1];
                var curve = Curve(entry >> 1);
                if ((entry & 1) != 0) curve = curve.Reversed();
                if (!started) { b.MoveTo(curve.Start * _scale); started = true; }
                var from = curve.Start;
                foreach (var s in curve.Segs)
                {
                    if (s.IsLine) b.LineTo(s.End * _scale);
                    else b.CubicTo(s.C1 * _scale, s.C2 * _scale, s.End * _scale);
                    from = s.End;
                }
                if (ch.Closed) break;

                int endCorner = (entry & 1) == 0 ? ch.EndCorner : ch.StartCorner;
                int endDir = (entry & 1) == 0 ? ch.LastDir : (ch.FirstDir + 2) & 3;
                int next = -1;
                foreach (int turn in (ReadOnlySpan<int>)[3, 0, 1])   // left, straight, right
                {
                    if (startAt.TryGetValue((long)endCorner * 4 + ((endDir + turn) & 3), out int cand) && !used[cand]) { next = cand; break; }
                }
                cur = next;
            }
            if (started) b.Close();
        }
        return b.Build();
    }
}

