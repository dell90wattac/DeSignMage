using Clipper2ZLib;
using DeSignMage.Core.Geometry;
using DeSignMage.Geometry.Internal;
using CFill = Clipper2ZLib.FillRule;

namespace DeSignMage.Geometry;

/// <summary>
/// <see cref="IOffsetService"/> on Clipper2 (offset-engine.md milestone 1, with exact arcs where they are known):
/// <list type="number">
/// <item>the source is flattened with tagged vertices and cleaned (a union by its fill rule);</item>
/// <item>Clipper's <c>ClipperOffset</c> grows or shrinks it with round joins;</item>
/// <item>corner rule: at a real corner (where two source segments meet at an angle) whose inside angle is at least
///   the OAT, a miter "kite" is added (outward) or cut away (inward), so the corner is sharp; sharper corners stay round;</item>
/// <item>the result polylines are refitted to lines and arcs. Their tags say which source segment or corner each point
///   came from, so the offsets of source lines and arcs and the round corners come back as exact lines and arcs, and
///   corners land on the exact crossing.</item>
/// </list>
/// Thread-safe: every call uses its own state.
/// </summary>
public sealed class ClipperOffsetService : IOffsetService
{
    private const double MinTurn = 0.5 * Math.PI / 180;       // smaller turns are not corners

    public PathGeometry Offset(PathGeometry shape, double distance, OffsetOptions? options = null)
    {
        options ??= OffsetOptions.Default;
        if (!double.IsFinite(distance)) throw new ArgumentOutOfRangeException(nameof(distance), "The offset distance must be finite.");
        var srcBounds = SourceLoops.Bounds([shape]);
        if (shape.IsEmpty || srcBounds.IsEmpty) return PathGeometry.EmptyPath;

        double w = Math.Abs(distance);
        double miterLimit = Math.Clamp(double.IsFinite(options.MiterLimit) ? options.MiterLimit : 20, 1, 1000);
        var engine = new Engine(srcBounds.Inflate(w * Math.Max(miterLimit, 2) + 1e-9), options.Tolerance);
        var run = new Run(engine, distance, options, miterLimit);
        return run.Execute(shape);
    }

    /// <summary>The state of one offset call.</summary>
    private sealed class Run(Engine engine, double d, OffsetOptions o, double miterLimit)
    {
        private readonly TagTable _tags = engine.Tags;
        private readonly Grid _grid = engine.Grid;
        private readonly double _w = Math.Abs(d);
        private readonly List<long> _tipSource = [];                  // tip index → Z of the corner it belongs to
        private readonly Dictionary<long, Carrier[]?> _hintMemo = [];
        private int _sourceRecords;                                   // records below this index came from cleaning the source

        public PathGeometry Execute(PathGeometry shape)
        {
            var closed = engine.Normalize([shape], o.FillRule, o.ContourOnly);
            _sourceRecords = _tags.Records.Count;
            var open = new Paths64();
            var openSegs = new List<SourceLoop>();
            if (o.OpenPaths != OpenPathMode.Ignore && d != 0)
                foreach (var loop in engine.Loops(shape))
                    if (!loop.Closed) { open.Add(_tags.AddLoop(loop, engine.Tol)); openSegs.Add(loop); }

            var results = new List<OutLoop>();
            if (d == 0)
            {
                var c0 = engine.NewClipper();
                c0.AddSubject(closed);
                var t0 = new PolyTree64();
                c0.Execute(ClipType.Union, CFill.NonZero, t0);
                return OutPaths.Build(engine.Rebuild(t0, keepCurves: true, minArea: 0));
            }

            double delta = d * _grid.Scale;
            var subjects = new Paths64();
            var kitesOut = new Paths64();
            var kitesIn = new Paths64();

            // Closed loops.
            if (closed.Count > 0)
            {
                var co = NewOffsetter();
                co.AddPaths(closed, o.Corners == CornerStyle.Bevel ? JoinType.Bevel : JoinType.Round, EndType.Polygon);
                var off = new Paths64();
                co.Execute(delta, off);
                subjects.AddRange(off);
                foreach (var path in closed) AddKites(path, closedLoop: true, d, d > 0 ? kitesOut : kitesIn);
            }

            // Open paths: a stroke 2w wide (one-sided: its boundary on one side is the parallel line).
            var strokes = new Paths64();
            if (open.Count > 0)
            {
                var co = NewOffsetter();
                var end = o.OpenPaths == OpenPathMode.OneSided ? EndType.Butt : o.Caps switch
                {
                    CapStyle.Butt => EndType.Butt,
                    CapStyle.Square => EndType.Square,
                    _ => EndType.Round,
                };
                co.AddPaths(open, o.Corners == CornerStyle.Bevel ? JoinType.Bevel : JoinType.Round, end);
                co.Execute(_w * _grid.Scale, strokes);
                foreach (var path in open) AddKites(path, closedLoop: false, _w, strokes);
            }

            // Corners: kites added (outward) or cut away (inward) from the closed region.
            Paths64 region = subjects;
            if (kitesIn.Count > 0 && subjects.Count > 0)
            {
                var ci = engine.NewClipper();
                ci.AddSubject(subjects);
                ci.AddClip(kitesIn);
                region = [];
                ci.Execute(ClipType.Difference, CFill.NonZero, region);
            }

            if (o.OpenPaths == OpenPathMode.OneSided && strokes.Count > 0)
            {
                var cs = engine.NewClipper();
                cs.AddSubject(strokes);
                var strokeRegion = new Paths64();
                cs.Execute(ClipType.Union, CFill.NonZero, strokeRegion);
                foreach (var side in OneSided(strokeRegion, open))
                    if (Refit(side, closedLoop: false) is { } l) results.Add(l);
                strokes.Clear();
            }

            var c = engine.NewClipper();
            c.AddSubject(region);
            c.AddSubject(kitesOut);
            c.AddSubject(strokes);
            var tree = new PolyTree64();
            c.Execute(ClipType.Union, CFill.NonZero, tree);
            var closedLoops = new List<OutLoop>();
            Visit(tree, closedLoops);
            closedLoops.AddRange(results);
            return OutPaths.Build(closedLoops);
        }

        private void Visit(PolyPath64 node, List<OutLoop> into)
        {
            for (int i = 0; i < node.Count; i++) Visit(node[i], into);
            if (node.Polygon is not { Count: >= 3 } poly) return;
            if (o.ContourOnly && node.IsHole) return;
            if (Refit(poly, closedLoop: true) is { } loop) into.Add(loop);
        }

        private ClipperOffset NewOffsetter() =>
            new(2.0, Math.Max(engine.Tol * _grid.Scale * 0.5, 0.25), true, false) { ZCallback = _tags.OnIntersection };

        // ---- corners ----

        /// <summary>
        /// Adds a miter kite (corner, offset point, miter tip, offset point) at every real corner of a grid path where the
        /// corner rule wants a sharp corner. <paramref name="dist"/> is signed: outward of the path's left-hand region when positive.
        /// For open paths both sides are looked at (the outside of each bend gets the kite).
        /// </summary>
        private void AddKites(Path64 path, bool closedLoop, double dist, Paths64 into)
        {
            if (o.Corners is CornerStyle.Round or CornerStyle.Bevel) return;
            int n = path.Count;
            if (n < 3) return;
            double oat = Math.Clamp(o.OutsideAngleThreshold, 0, 180) * Math.PI / 180;
            double scaledW = _w * _grid.Scale;
            for (int i = 0; i < n; i++)
            {
                if (!closedLoop && (i == 0 || i == n - 1)) continue;
                long z = path[i].Z;
                if (!IsCornerCandidate(z, out double trueTurn)) continue;
                var v = P(path[i]);
                var a = P(path[(i + n - 1) % n]);
                var b = P(path[(i + 1) % n]);
                var u1 = v - a; var u2 = b - v;
                double l1 = u1.Length, l2 = u2.Length;
                if (l1 <= 0 || l2 <= 0) continue;
                u1 /= l1; u2 /= l2;
                double cross = Vec2.Cross(u1, u2);
                double turn = double.IsNaN(trueTurn) ? Math.Abs(Math.Atan2(cross, Vec2.Dot(u1, u2))) : trueTurn;
                if (turn < MinTurn) continue;
                // Signed distance towards the gap side of this corner.
                double s;
                if (closedLoop)
                {
                    if (cross * dist <= 0) continue;          // inside corner: no gap to fill
                    s = dist * _grid.Scale;
                }
                else s = cross > 0 ? scaledW : -scaledW;     // open path: the outside of the bend
                double inside = Math.PI - turn;
                bool miter = o.Corners == CornerStyle.Miter || inside >= oat - 1e-12;
                if (!miter) continue;
                double ratio = 1 / Math.Cos(turn / 2);
                if (!(ratio <= miterLimit)) continue;
                var n1 = new Vec2(u1.Y, -u1.X);
                var n2 = new Vec2(u2.Y, -u2.X);
                var p1 = v + n1 * s;
                var p2 = v + n2 * s;
                var tip = v + (n1 + n2) * (s / (1 + Vec2.Dot(n1, n2)));
                long tipZ = TagTable.TipBase + _tipSource.Count;
                _tipSource.Add(z);
                var kite = new Path64(4)
                {
                    new Point64((long)Math.Round(v.X), (long)Math.Round(v.Y), z),
                    new Point64((long)Math.Round(p1.X), (long)Math.Round(p1.Y), z),
                    new Point64((long)Math.Round(tip.X), (long)Math.Round(tip.Y), tipZ),
                    new Point64((long)Math.Round(p2.X), (long)Math.Round(p2.Y), z),
                };
                if (!Clipper.IsPositive(kite)) kite.Reverse();
                into.Add(kite);
            }
        }

        private static Vec2 P(Point64 p) => new(p.X, p.Y);

        /// <summary>
        /// Real corners: where one source segment ends and the next starts (turn from their true tangents), or where the
        /// cleaned source crosses itself (turn from the chords: NaN).
        /// </summary>
        private bool IsCornerCandidate(long z, out double trueTurn)
        {
            trueTurn = double.NaN;
            if (TagTable.IsVertex(z))
            {
                int g = (int)(z - 1);
                if (_tags.TOfVertex(g) != 0) return false;
                int prev = _tags.Prev(g);
                if (prev < 0) return false;
                var tin = _tags.Segs[_tags.SegOfVertex(prev)].Tangent(1);
                var tout = _tags.Segs[_tags.SegOfVertex(g)].Tangent(0);
                trueTurn = Math.Abs(Math.Atan2(Vec2.Cross(tin, tout), Vec2.Dot(tin, tout)));
                return true;
            }
            return z < 0;
        }

        // ---- refit with hints ----

        private OutLoop? Refit(Path64 path, bool closedLoop)
        {
            int n = path.Count;
            var pts = new Vec2[n];
            var hints = new Carrier[]?[n];
            var hard = new bool[n];
            for (int i = 0; i < n; i++)
            {
                long z = path[i].Z;
                pts[i] = _grid.From(path[i]);
                hints[i] = Hints(z);
                hard[i] = z >= TagTable.TipBase || (z < 0 && (int)(-z - 1) >= _sourceRecords);
            }
            return Fitter.Fit(pts, closedLoop, hints, hard, engine.Tol, 30 * Math.PI / 180, o.PreferCubics, engine.Eps);
        }

        /// <summary>Exact lines and circles a result point with this Z may lie on.</summary>
        private Carrier[]? Hints(long z)
        {
            if (z == 0) return null;
            if (_hintMemo.TryGetValue(z, out var memo)) return memo;
            _hintMemo[z] = null;      // recursion guard
            var list = new List<Carrier>();
            double tolBase = Math.Max(8 * _grid.Unit, 0.1 * engine.Tol);
            if (z >= TagTable.TipBase)
            {
                if (Hints(_tipSource[(int)(z - TagTable.TipBase)]) is { } h) list.AddRange(h);
            }
            else if (TagTable.IsVertex(z))
            {
                int g = (int)(z - 1);
                AddSegCarriers(list, _tags.Segs[_tags.SegOfVertex(g)], tolBase);
                int prev = _tags.Prev(g);
                double t = _tags.TOfVertex(g);
                if (t == 0 && prev >= 0) AddSegCarriers(list, _tags.Segs[_tags.SegOfVertex(prev)], tolBase);
                // Round corner around a vertex where segments meet, or a round cap around the end of an open path.
                if ((t == 0 && prev >= 0) || t == 1 || (t == 0 && prev < 0))
                    list.Add(Carrier.Circle(_tags.VertexPos(g), _w, tolBase));
            }
            else
            {
                int r = (int)(-z - 1);
                var rec = _tags.Records[r];
                foreach (var zz in new[] { rec.Preset, rec.A1, rec.B1, rec.A2, rec.B2 })
                    if (Hints(zz) is { } h) list.AddRange(h);
                if (r < _sourceRecords)
                    list.Add(Carrier.Circle(_grid.From(new Point64(rec.X, rec.Y, 0)), _w, tolBase));   // corner where the source crosses itself
            }
            var result = list.Count == 0 ? null : list.Distinct().ToArray();
            _hintMemo[z] = result;
            return result;
        }

        private void AddSegCarriers(List<Carrier> list, Seg s, double tolBase)
        {
            switch (s.Kind)
            {
                case SegKind.Line:
                {
                    var dir = s.Tangent(0);
                    var nrm = new Vec2(dir.Y, -dir.X);
                    list.Add(Carrier.Line(s.P0 + nrm * _w, dir, tolBase));
                    list.Add(Carrier.Line(s.P0 - nrm * _w, dir, tolBase));
                    break;
                }
                case SegKind.Arc:
                {
                    // Offsetting the chords of a flattened arc puts points slightly off the true offset circle.
                    double tol = tolBase + _w * (1 - Math.Cos(s.FlattenStep / 2)) * 1.5 + engine.Tol * 0.05;
                    list.Add(Carrier.Circle(s.Center, s.Radius + _w, tol));
                    if (s.Radius - _w > tolBase) list.Add(Carrier.Circle(s.Center, s.Radius - _w, tol));
                    break;
                }
            }
        }

        // ---- one-sided parallels of open paths ----

        /// <summary>
        /// The parts of the stroke outlines that lie at distance w on the requested side of their source path (positive:
        /// screen-left of travel = Cross(direction, point) &lt; 0), as open polylines in the direction of the source.
        /// </summary>
        private IEnumerable<Path64> OneSided(Paths64 strokeOutlines, Paths64 sources)
        {
            double tolGrid = engine.Tol * _grid.Scale + 4;
            double wGrid = _w * _grid.Scale;
            int wantSign = d > 0 ? -1 : 1;
            foreach (var outline in strokeOutlines)
            {
                int n = outline.Count;
                var keep = new bool[n];
                var param = new double[n];
                var src = new int[n];
                for (int i = 0; i < n; i++)
                {
                    var q = P(outline[i]);
                    var (dist, side, s, along) = Nearest(q, sources);
                    // Miter tips lie further out than w; everything else on the parallel is exactly w away.
                    bool tip = outline[i].Z >= TagTable.TipBase;
                    keep[i] = side == wantSign && (tip || Math.Abs(dist - wGrid) <= tolGrid);
                    param[i] = along;
                    src[i] = s;
                }
                int startAt = Array.IndexOf(keep, false);
                if (startAt < 0) startAt = 0;
                var current = new List<int>();
                for (int k = 1; k <= n; k++)
                {
                    int i = (startAt + k) % n;
                    bool joined = current.Count > 0 && src[i] == src[current[^1]];
                    if (keep[i] && (current.Count == 0 || joined)) current.Add(i);
                    else
                    {
                        if (current.Count >= 2) yield return Oriented(current, param, outline);
                        current = keep[i] ? [i] : [];
                    }
                }
                if (current.Count >= 2) yield return Oriented(current, param, outline);
            }
        }

        /// <summary>The run as a path in the direction of its source (by where its ends project on the source).</summary>
        private static Path64 Oriented(List<int> run, double[] param, Path64 outline)
        {
            if (param[run[0]] > param[run[^1]]) run.Reverse();
            var p = new Path64(run.Count);
            foreach (int i in run) p.Add(outline[i]);
            return p;
        }

        /// <summary>Distance to the nearest source polyline, the side (sign of Cross), which source and the length along it.</summary>
        private static (double Dist, int Side, int Source, double Along) Nearest(Vec2 q, Paths64 sources)
        {
            double best = double.PositiveInfinity, bestAlong = 0;
            int bestSide = 0, bestSrc = -1;
            for (int s = 0; s < sources.Count; s++)
            {
                var path = sources[s];
                double along = 0;
                for (int i = 0; i + 1 < path.Count; i++)
                {
                    var a = P(path[i]); var b = P(path[i + 1]);
                    var ab = b - a;
                    double len2 = ab.LengthSquared;
                    double len = Math.Sqrt(len2);
                    if (len2 <= 0) continue;
                    double t = Math.Clamp(Vec2.Dot(q - a, ab) / len2, 0, 1);
                    var foot = a + ab * t;
                    double dist = Vec2.Distance(q, foot);
                    if (dist < best - 1e-9)
                    {
                        best = dist;
                        bestSrc = s;
                        bestAlong = along + t * len;
                        Vec2 dirAt = ab;
                        if (t >= 1 && i + 2 < path.Count) dirAt = ab / len + Unit(P(path[i + 2]) - b);
                        else if (t <= 0 && i > 0) dirAt = Unit(a - P(path[i - 1])) + ab / len;
                        bestSide = Math.Sign(Vec2.Cross(dirAt, q - foot));
                    }
                    along += len;
                }
            }
            return (best, bestSide, bestSrc, bestAlong);
        }

        private static Vec2 Unit(Vec2 v)
        {
            double l = v.Length;
            return l <= 0 ? v : v / l;
        }
    }
}
