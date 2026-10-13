using Clipper2ZLib;
using DeSignMage.Core.Geometry;

namespace DeSignMage.Geometry.Internal;

/// <summary>
/// The integer grid Clipper works on: document points are moved to the centre of the work and scaled by a power of two so
/// the work spans about ±2^30 grid units (precise and far from Clipper's overflow range at any document size).
/// </summary>
internal sealed class Grid
{
    public readonly Vec2 Origin;
    public readonly double Scale;
    public readonly double Unit;    // one grid step in points

    public Grid(Rect2 bounds)
    {
        if (bounds.IsEmpty) bounds = new Rect2(0, 0, 1, 1);
        Origin = bounds.Center;
        double half = Math.Max(bounds.Width, bounds.Height) / 2;
        if (!(half > 1e-200)) half = Math.Max(Math.Abs(Origin.X) + Math.Abs(Origin.Y), 1) * 1e-6;
        int e = (int)Math.Floor(Math.Log2((double)(1L << 30) / half));
        Scale = Math.Pow(2, e);
        Unit = 1 / Scale;
    }

    public Point64 To(Vec2 p, long z = 0) =>
        new((long)Math.Round((p.X - Origin.X) * Scale), (long)Math.Round((p.Y - Origin.Y) * Scale), z);

    public Vec2 From(Point64 p) => new(p.X * Unit + Origin.X, p.Y * Unit + Origin.Y);
}

/// <summary>
/// Source tags for Clipper's Z field. Every flattened vertex gets Z = index + 1 and remembers its segment and parameter;
/// every intersection Clipper creates gets Z = −(record + 1), a record of the two edges that crossed. From these the engine
/// finds, for each result edge, the source segment it runs along, and restores arcs and cubics exactly.
/// </summary>
internal sealed class TagTable
{
    /// <summary>Z values at or above this mark offset corner tips (see the offset service).</summary>
    public const long TipBase = 1L << 40;

    public readonly struct Record(long preset, long a1, long b1, long a2, long b2, long x, long y)
    {
        public readonly long Preset = preset, A1 = a1, B1 = b1, A2 = a2, B2 = b2, X = x, Y = y;
    }

    public readonly Grid Grid;
    public readonly List<Seg> Segs = [];
    public readonly List<Record> Records = [];

    private readonly List<int> _seg = [];
    private readonly List<double> _t = [];
    private readonly List<int> _next = [];
    private readonly List<int> _prev = [];
    private readonly List<int> _canon = [];
    private readonly Dictionary<(long, long, long, long), int> _edgeKeys = [];
    private readonly Dictionary<long, int[]> _onSetMemo = [];

    public TagTable(Grid grid) => Grid = grid;

    /// <summary>Flattens a loop into a tagged Clipper path (closed loops: no repeated end point).</summary>
    public Path64 AddLoop(SourceLoop loop, double tol)
    {
        var pts = new List<Point64>();
        var segIds = new List<int>();
        var ts = new List<double>();
        var samples = new List<double>();
        foreach (var s in loop.Segs)
        {
            s.Id = Segs.Count;
            Segs.Add(s);
            samples.Clear();
            samples.Add(0);
            s.SampleParams(tol, samples);
            foreach (var t in samples) Push(Grid.To(s.Eval(t)), s.Id, t);
        }
        if (!loop.Closed) Push(Grid.To(loop.End), loop.Segs[^1].Id, 1);
        else if (pts.Count > 1 && Same(pts[^1], pts[0])) { pts.RemoveAt(pts.Count - 1); segIds.RemoveAt(segIds.Count - 1); ts.RemoveAt(ts.Count - 1); }

        int first = _seg.Count, n = pts.Count;
        var path = new Path64(n);
        for (int i = 0; i < n; i++)
        {
            int g = first + i;
            _seg.Add(segIds[i]);
            _t.Add(ts[i]);
            _next.Add(i + 1 < n ? g + 1 : loop.Closed ? first : -1);
            _prev.Add(i > 0 ? g - 1 : loop.Closed ? first + n - 1 : -1);
            path.Add(new Point64(pts[i].X, pts[i].Y, g + 1));
        }
        for (int i = 0; i < n; i++)
        {
            int g = first + i;
            int nx = _next[g];
            if (nx < 0) { _canon.Add(-1); continue; }
            var a = pts[i]; var b = pts[nx - first];
            var key = a.X < b.X || (a.X == b.X && a.Y <= b.Y) ? (a.X, a.Y, b.X, b.Y) : (b.X, b.Y, a.X, a.Y);
            if (!_edgeKeys.TryGetValue(key, out int canon)) _edgeKeys[key] = canon = g;
            _canon.Add(canon);
        }
        return path;

        void Push(Point64 p, int seg, double t)
        {
            if (pts.Count > 0 && Same(pts[^1], p)) return;
            pts.Add(p); segIds.Add(seg); ts.Add(t);
        }
    }

    private static bool Same(Point64 a, Point64 b) => a.X == b.X && a.Y == b.Y;

    /// <summary>Clipper's Z callback: records which edges met at a new vertex.</summary>
    public void OnIntersection(Point64 bot1, Point64 top1, Point64 bot2, Point64 top2, ref Point64 pt)
    {
        Records.Add(new Record(pt.Z, bot1.Z, top1.Z, bot2.Z, top2.Z, pt.X, pt.Y));
        pt.Z = -Records.Count;
    }

    public static bool IsVertex(long z) => z > 0 && z < TipBase;

    public int SegOfVertex(int g) => _seg[g];
    public double TOfVertex(int g) => _t[g];
    public int Prev(int g) => _prev[g];

    /// <summary>Exact source position of a flattened vertex.</summary>
    public Vec2 VertexPos(int g) => Segs[_seg[g]].Eval(_t[g]);

    /// <summary>The source edges (canonical flattened-edge ids) a result vertex with this Z lies on.</summary>
    public int[] OnSet(long z)
    {
        if (z == 0 || z >= TipBase) return [];
        if (z > 0)
        {
            int g = (int)(z - 1);
            int a = _canon[g], p = _prev[g], b = p >= 0 ? _canon[p] : -1;
            if (a >= 0 && b >= 0 && a != b) return [a, b];
            if (a >= 0) return [a];
            return b >= 0 ? [b] : [];
        }
        if (_onSetMemo.TryGetValue(z, out var memo)) return memo;
        _onSetMemo[z] = [];       // recursion guard
        var r = Records[(int)(-z - 1)];
        var set = new HashSet<int>(OnSet(r.Preset));
        set.UnionWith(EdgesOf(r.A1, r.B1));
        set.UnionWith(EdgesOf(r.A2, r.B2));
        var result = set.ToArray();
        _onSetMemo[z] = result;
        return result;
    }

    /// <summary>Source edges an edge between two tagged vertices runs along.</summary>
    public int[] EdgesOf(long za, long zb)
    {
        var a = OnSet(za);
        if (a.Length == 0) return a;
        var b = OnSet(zb);
        if (b.Length == 0) return b;
        return a.Intersect(b).ToArray();
    }

    /// <summary>Segment and parameter range of a canonical source edge.</summary>
    public (Seg Seg, double T0, double T1) EdgeInterval(int e)
    {
        var seg = Segs[_seg[e]];
        double t0 = _t[e];
        int n = _next[e];
        double t1 = n >= 0 && _seg[n] == _seg[e] && _t[n] > t0 ? _t[n] : 1.0;
        return (seg, t0, t1);
    }
}
