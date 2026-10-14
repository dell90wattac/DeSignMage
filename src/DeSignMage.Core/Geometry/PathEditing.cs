namespace DeSignMage.Core.Geometry;

public enum SegmentKind { Line, Arc, Cubic }

/// <summary>How a loop is shown in outline views: on the border of the shape (blue), inside it (red), or broken (black).</summary>
public enum LoopKind { Border, Interior, Open }

/// <summary>
/// One vector of a loop, from the previous node to <see cref="End"/>.
/// Arc: <see cref="C1"/> is the arc's midpoint. Cubic: <see cref="C1"/> and <see cref="C2"/> are the control points.
/// </summary>
public readonly record struct Segment(SegmentKind Kind, Vec2 End, Vec2 C1 = default, Vec2 C2 = default)
{
    public static Segment Line(Vec2 end) => new(SegmentKind.Line, end);
    public static Segment Arc(Vec2 through, Vec2 end) => new(SegmentKind.Arc, end, through);
    public static Segment Cubic(Vec2 c1, Vec2 c2, Vec2 end) => new(SegmentKind.Cubic, end, c1, c2);
}

/// <summary>
/// An editable subpath: a start node and the vectors that follow. Nodes are numbered from 0 (the start).
/// A closed loop's last segment always ends exactly at <see cref="Start"/>, so it has as many nodes as segments;
/// an open loop has one more node than segments.
/// </summary>
public sealed class Loop
{
    public Vec2 Start;
    public List<Segment> Segments = [];
    public bool Closed;

    public Loop Clone() => new() { Start = Start, Segments = [.. Segments], Closed = Closed };

    public int NodeCount => Closed ? Segments.Count : Segments.Count + 1;
    public Vec2 Node(int k) => k == 0 ? Start : Segments[k - 1].End;
    public Vec2 SegmentStart(int i) => i == 0 ? Start : Segments[i - 1].End;

    /// <summary>The segment ending at node <paramref name="k"/>, or -1 (start of an open loop).</summary>
    public int Incoming(int k) => k > 0 ? k - 1 : Closed ? Segments.Count - 1 : -1;

    /// <summary>The segment starting at node <paramref name="k"/>, or -1 (end of an open loop).</summary>
    public int Outgoing(int k) => k < Segments.Count ? k : -1;

    public Vec2 PointOn(int i, double t)
    {
        var s = SegmentStart(i);
        var seg = Segments[i];
        return seg.Kind switch
        {
            SegmentKind.Line => Vec2.Lerp(s, seg.End, t),
            SegmentKind.Arc => Arcs.PointAt(s, seg.C1, seg.End, t),
            _ => PathGeometry.EvalCubic(s, seg.C1, seg.C2, seg.End, t),
        };
    }

    /// <summary>Parameter of the point on segment <paramref name="i"/> closest to <paramref name="p"/>, and its distance.</summary>
    public (double T, double Distance) Closest(int i, Vec2 p)
    {
        double bestT = 0, best = double.PositiveInfinity;
        const int n = 96;
        for (int j = 0; j <= n; j++)
        {
            double t = (double)j / n, d = Vec2.Distance(PointOn(i, t), p);
            if (d < best) { best = d; bestT = t; }
        }
        // Refine around the best sample.
        double lo = Math.Max(0, bestT - 1.0 / n), hi = Math.Min(1, bestT + 1.0 / n);
        for (int it = 0; it < 20; it++)
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if (Vec2.Distance(PointOn(i, m1), p) < Vec2.Distance(PointOn(i, m2), p)) hi = m2; else lo = m1;
        }
        double tt = (lo + hi) / 2, dd = Vec2.Distance(PointOn(i, tt), p);
        return dd < best ? (tt, dd) : (bestT, best);
    }

    public Vec2 StartTangent(int i)
    {
        var s = SegmentStart(i);
        var seg = Segments[i];
        return seg.Kind switch
        {
            SegmentKind.Line => seg.End - s,
            SegmentKind.Arc => Arcs.TangentAt(s, seg.C1, seg.End, 0),
            _ => (seg.C1 - s).Length > 1e-9 ? seg.C1 - s : seg.C2 - s,
        };
    }

    public Vec2 EndTangent(int i)
    {
        var s = SegmentStart(i);
        var seg = Segments[i];
        return seg.Kind switch
        {
            SegmentKind.Line => seg.End - s,
            SegmentKind.Arc => Arcs.TangentAt(s, seg.C1, seg.End, 1),
            _ => (seg.End - seg.C2).Length > 1e-9 ? seg.End - seg.C2 : seg.End - seg.C1,
        };
    }

    /// <summary>A sharp node (tangent changes direction) or the end of an open loop. Smooth nodes are not corners.</summary>
    public bool IsCorner(int k, double toleranceRadians = 0.1)
    {
        int a = Incoming(k), b = Outgoing(k);
        if (a < 0 || b < 0) return true;
        Vec2 t1 = EndTangent(a), t2 = StartTangent(b);
        if (t1.Length < 1e-12 || t2.Length < 1e-12) return true;
        double angle = Math.Abs(Math.Atan2(Vec2.Cross(t1, t2), Vec2.Dot(t1, t2)));
        return angle > toleranceRadians;
    }

    /// <summary>This loop alone as a path.</summary>
    public PathGeometry ToGeometry() => PathEditing.FromLoops([this], FillRule.NonZero);

    /// <summary>
    /// Signed area from the flattened loop. Positive = clockwise as drawn on screen (document space is y-down), the
    /// "blue" loop; negative = counter-clockwise ("red"). In the loop's own space: a mirroring transform flips it.
    /// </summary>
    public double SignedArea()
    {
        double sum = 0;
        var prev = Start;
        const int steps = 12;
        for (int i = 0; i < Segments.Count; i++)
        {
            int n = Segments[i].Kind == SegmentKind.Line ? 1 : steps;
            for (int j = 1; j <= n; j++)
            {
                var p = j == n ? Segments[i].End : PointOn(i, (double)j / n);
                sum += prev.X * p.Y - p.X * prev.Y;
                prev = p;
            }
        }
        sum += prev.X * Start.Y - Start.X * prev.Y;   // an open loop is measured as if closed by a straight line
        return sum / 2;
    }

    public bool IsClockwise => SignedArea() > 0;

    public double SegmentLength(int i)
    {
        var s = SegmentStart(i);
        var seg = Segments[i];
        switch (seg.Kind)
        {
            case SegmentKind.Line:
                return Vec2.Distance(s, seg.End);
            case SegmentKind.Arc:
                if (!Arcs.TryCircle(s, seg.C1, seg.End, out var c, out var r)) return Vec2.Distance(s, seg.End);
                return r * Math.Abs(Arcs.Angles(s, seg.C1, seg.End, c).Sweep);
            default:
                double len = 0;
                var prev = s;
                for (int j = 1; j <= 32; j++)
                {
                    var p = PathGeometry.EvalCubic(s, seg.C1, seg.C2, seg.End, j / 32.0);
                    len += Vec2.Distance(prev, p);
                    prev = p;
                }
                return len;
        }
    }

    /// <summary>The length of the loop (its perimeter when closed).</summary>
    public double Length() => Enumerable.Range(0, Segments.Count).Sum(SegmentLength);

    public Rect2 Bounds() => Segments.Count == 0 ? new Rect2(Start.X, Start.Y, Start.X, Start.Y) : ToGeometry().GetBounds();

    public void Translate(Vec2 d)
    {
        Start += d;
        for (int i = 0; i < Segments.Count; i++)
        {
            var s = Segments[i];
            Segments[i] = s.Kind switch
            {
                SegmentKind.Line => Segment.Line(s.End + d),
                SegmentKind.Arc => Segment.Arc(s.C1 + d, s.End + d),
                _ => Segment.Cubic(s.C1 + d, s.C2 + d, s.End + d),
            };
        }
    }

    /// <summary>Segment <paramref name="i"/> alone as a path (for highlighting).</summary>
    public PathGeometry SegmentGeometry(int i)
    {
        var b = new PathBuilder().MoveTo(SegmentStart(i));
        PathEditing.Append(b, Segments[i]);
        return b.Build();
    }
}

/// <summary>Conversions between <see cref="PathGeometry"/> and editable loops, and the vector-editing operations.</summary>
public static partial class PathEditing
{
    private const double Same = 1e-9;

    public static List<Loop> ToLoops(PathGeometry g)
    {
        var loops = new List<Loop>();
        Loop? cur = null;
        int pi = 0;
        var pts = g.Points;
        Vec2 last = Vec2.Zero;
        foreach (var v in g.Verbs)
        {
            switch (v)
            {
                case PathVerb.MoveTo:
                    cur = new Loop { Start = pts[pi++] };
                    loops.Add(cur);
                    last = cur.Start;
                    break;
                case PathVerb.LineTo:
                    cur = Ensure(cur, last, loops);
                    cur.Segments.Add(Segment.Line(last = pts[pi++]));
                    break;
                case PathVerb.ArcTo:
                    cur = Ensure(cur, last, loops);
                    cur.Segments.Add(Segment.Arc(pts[pi], last = pts[pi + 1]));
                    pi += 2;
                    break;
                case PathVerb.CubicTo:
                    cur = Ensure(cur, last, loops);
                    cur.Segments.Add(Segment.Cubic(pts[pi], pts[pi + 1], last = pts[pi + 2]));
                    pi += 3;
                    break;
                case PathVerb.Close:
                    if (cur is not null && cur.Segments.Count > 0)
                    {
                        var end = cur.Segments[^1].End;
                        if (Vec2.Distance(end, cur.Start) > Same) cur.Segments.Add(Segment.Line(cur.Start));
                        else cur.Segments[^1] = cur.Segments[^1] with { End = cur.Start };
                        cur.Closed = true;
                        last = cur.Start;
                    }
                    cur = null;
                    break;
            }
        }
        loops.RemoveAll(l => l.Segments.Count == 0);
        return loops;
    }

    private static Loop Ensure(Loop? cur, Vec2 at, List<Loop> loops)
    {
        if (cur is not null) return cur;
        var l = new Loop { Start = at };
        loops.Add(l);
        return l;
    }

    public static PathGeometry FromLoops(IEnumerable<Loop> loops, FillRule fillRule)
    {
        var b = new PathBuilder { FillRule = fillRule };
        foreach (var l in loops)
        {
            if (l.Segments.Count == 0) continue;
            b.MoveTo(l.Start);
            foreach (var s in l.Segments) Append(b, s);
            if (l.Closed) b.Close();
        }
        return b.Build();
    }

    internal static void Append(PathBuilder b, Segment s)
    {
        switch (s.Kind)
        {
            case SegmentKind.Line: b.LineTo(s.End); break;
            case SegmentKind.Arc: b.ArcTo(s.C1, s.End); break;
            default: b.CubicTo(s.C1, s.C2, s.End); break;
        }
    }

    // ---- Node moves ----

    /// <summary>
    /// Moves node <paramref name="k"/>. Cubic handles next to it move along; arcs next to it keep their bulge.
    /// </summary>
    public static void MoveNode(Loop l, int k, Vec2 to)
    {
        int a = l.Incoming(k), b = l.Outgoing(k);
        var delta = to - l.Node(k);
        double bulgeA = a >= 0 && l.Segments[a].Kind == SegmentKind.Arc ? Arcs.Bulge(l.SegmentStart(a), l.Segments[a].C1, l.Segments[a].End) : 0;
        double bulgeB = b >= 0 && l.Segments[b].Kind == SegmentKind.Arc ? Arcs.Bulge(l.SegmentStart(b), l.Segments[b].C1, l.Segments[b].End) : 0;

        if (k == 0) l.Start = to;
        if (a >= 0)
        {
            var s = l.Segments[a];
            l.Segments[a] = s.Kind == SegmentKind.Cubic ? s with { End = to, C2 = s.C2 + delta } : s with { End = to };
        }
        if (b >= 0 && l.Segments[b].Kind == SegmentKind.Cubic)
            l.Segments[b] = l.Segments[b] with { C1 = l.Segments[b].C1 + delta };

        if (a >= 0 && l.Segments[a].Kind == SegmentKind.Arc)
            l.Segments[a] = l.Segments[a] with { C1 = Arcs.ThroughFromBulge(l.SegmentStart(a), l.Segments[a].End, bulgeA) };
        if (b >= 0 && l.Segments[b].Kind == SegmentKind.Arc)
            l.Segments[b] = l.Segments[b] with { C1 = Arcs.ThroughFromBulge(l.SegmentStart(b), l.Segments[b].End, bulgeB) };
    }

    /// <summary>Moves a cubic control point (<paramref name="which"/> 1 or 2) or an arc's midpoint (1).</summary>
    public static void MoveHandle(Loop l, int i, int which, Vec2 to, HandleMode mode = HandleMode.Free)
    {
        var s = l.Segments[i];
        if (s.Kind != SegmentKind.Cubic)
        {
            if (s.Kind == SegmentKind.Arc) l.Segments[i] = s with { C1 = Arcs.PointAt(l.SegmentStart(i), to, s.End, 0.5) };
            return;
        }

        // The node this handle belongs to, and the handle on its other side (only another cubic has one).
        int k = HandleNode(l, i, which);
        int other = which == 1 ? l.Incoming(k) : l.Outgoing(k);
        int otherWhich = which == 1 ? 2 : 1;
        bool follow = other >= 0 && other != i && l.Segments[other].Kind == SegmentKind.Cubic &&
                      (mode == HandleMode.Symmetric || (mode == HandleMode.Auto && !l.IsCorner(k)));

        l.Segments[i] = which == 1 ? s with { C1 = to } : s with { C2 = to };
        if (!follow) return;

        var node = l.Node(k);
        var dir = node - to;                                      // the other handle points the opposite way
        if (dir.Length < 1e-9) return;
        var o = l.Segments[other];
        var oldHandle = otherWhich == 1 ? o.C1 : o.C2;
        double length = mode == HandleMode.Symmetric ? (to - node).Length : (oldHandle - node).Length;
        var moved = node + dir / dir.Length * length;
        l.Segments[other] = otherWhich == 1 ? o with { C1 = moved } : o with { C2 = moved };
    }

    // ---- Segment edits (vector menu) ----

    /// <summary>Insert: splits segment <paramref name="i"/> at parameter <paramref name="t"/>, adding a node.</summary>
    public static void Insert(Loop l, int i, double t)
    {
        t = Math.Clamp(t, 1e-4, 1 - 1e-4);
        var s0 = l.SegmentStart(i);
        var seg = l.Segments[i];
        var p = l.PointOn(i, t);
        Segment first, second;
        switch (seg.Kind)
        {
            case SegmentKind.Line:
                first = Segment.Line(p);
                second = Segment.Line(seg.End);
                break;
            case SegmentKind.Arc:
                first = Segment.Arc(Arcs.PointAt(s0, seg.C1, seg.End, t / 2), p);
                second = Segment.Arc(Arcs.PointAt(s0, seg.C1, seg.End, (1 + t) / 2), seg.End);
                break;
            default:
                var ab = Vec2.Lerp(s0, seg.C1, t); var bc = Vec2.Lerp(seg.C1, seg.C2, t); var cd = Vec2.Lerp(seg.C2, seg.End, t);
                var abc = Vec2.Lerp(ab, bc, t); var bcd = Vec2.Lerp(bc, cd, t);
                first = Segment.Cubic(ab, abc, p);
                second = Segment.Cubic(bcd, cd, seg.End);
                break;
        }
        l.Segments[i] = first;
        l.Segments.Insert(i + 1, second);
    }

    public static void ToLine(Loop l, int i) => l.Segments[i] = Segment.Line(l.Segments[i].End);

    /// <summary>To Arc: an arc through the segment's current midpoint (a gentle bulge for a straight line).</summary>
    public static void ToArc(Loop l, int i)
    {
        var s = l.SegmentStart(i);
        var seg = l.Segments[i];
        var mid = l.PointOn(i, 0.5);
        if (!Arcs.TryCircle(s, mid, seg.End, out _, out _))
        {
            // A straight vector gets a gentle bulge, outward from the shape when the loop is closed.
            mid = Arcs.ThroughFromBulge(s, seg.End, 0.2);
            if (l.Closed && l.ToGeometry().Contains(mid, 0.01)) mid = Arcs.ThroughFromBulge(s, seg.End, -0.2);
        }
        l.Segments[i] = Segment.Arc(mid, seg.End);
    }

    /// <summary>To Bezier: a cubic with the same shape (exact for lines, close for arcs).</summary>
    public static void ToBezier(Loop l, int i)
    {
        var s = l.SegmentStart(i);
        var seg = l.Segments[i];
        if (seg.Kind == SegmentKind.Cubic) return;
        if (seg.Kind == SegmentKind.Line || !Arcs.TryCircle(s, seg.C1, seg.End, out var c, out var r))
        {
            l.Segments[i] = Segment.Cubic(Vec2.Lerp(s, seg.End, 1 / 3.0), Vec2.Lerp(s, seg.End, 2 / 3.0), seg.End);
            return;
        }
        var (_, sweep) = Arcs.Angles(s, seg.C1, seg.End, c);
        double k = 4.0 / 3.0 * Math.Tan(Math.Abs(sweep) / 4) * r;
        l.Segments[i] = Segment.Cubic(s + Arcs.TangentAt(s, seg.C1, seg.End, 0) * k,
                                      seg.End - Arcs.TangentAt(s, seg.C1, seg.End, 1) * k, seg.End);
    }

    /// <summary>Re-numbers a closed loop so node <paramref name="k"/> becomes the start.</summary>
    public static void Rotate(Loop l, int k)
    {
        if (!l.Closed || k == 0) return;
        var start = l.Node(k);
        l.Segments = [.. l.Segments.Skip(k), .. l.Segments.Take(k)];
        l.Start = start;
    }

    /// <summary>Reverses the direction of a loop.</summary>
    public static void Reverse(Loop l)
    {
        var nodes = Enumerable.Range(0, l.Segments.Count + 1).Select(l.SegmentStart).ToList();   // includes final end
        var rev = new List<Segment>();
        for (int i = l.Segments.Count - 1; i >= 0; i--)
        {
            var s = l.Segments[i];
            var to = nodes[i];
            rev.Add(s.Kind switch
            {
                SegmentKind.Line => Segment.Line(to),
                SegmentKind.Arc => Segment.Arc(s.C1, to),
                _ => Segment.Cubic(s.C2, s.C1, to),
            });
        }
        l.Start = nodes[^1];
        l.Segments = rev;
    }

    /// <summary>
    /// Break Loop at node <paramref name="k"/>: a closed loop becomes open there (no geometry removed; its ends meet
    /// at that node). An open loop splits in two at an inner node. Returns false if nothing changed.
    /// </summary>
    public static bool BreakAt(List<Loop> loops, int li, int k)
    {
        var l = loops[li];
        if (l.Closed)
        {
            Rotate(l, k);
            l.Closed = false;
            return true;
        }
        if (k <= 0 || k >= l.Segments.Count) return false;
        var tail = new Loop { Start = l.Node(k), Segments = l.Segments.Skip(k).ToList() };
        l.Segments = l.Segments.Take(k).ToList();
        loops.Insert(li + 1, tail);
        return true;
    }

    /// <summary>Set Start/End: node <paramref name="k"/> becomes the loop's start (closed), or an open loop is reversed when k is its end.</summary>
    public static bool SetStart(Loop l, int k)
    {
        if (l.Closed) { if (k == 0) return false; Rotate(l, k); return true; }
        if (k == l.NodeCount - 1) { Reverse(l); return true; }
        return false;
    }

    /// <summary>Join Start/End: closes an open loop (with a straight vector if its ends are apart).</summary>
    public static bool Join(Loop l)
    {
        if (l.Closed) return false;
        if (Vec2.Distance(l.Segments[^1].End, l.Start) > Same) l.Segments.Add(Segment.Line(l.Start));
        else l.Segments[^1] = l.Segments[^1] with { End = l.Start };
        l.Closed = true;
        return true;
    }

    /// <summary>Deletes node <paramref name="k"/>: its two vectors merge into one. Loops left with nothing are removed.</summary>
    public static void DeleteNode(List<Loop> loops, int li, int k)
    {
        var l = loops[li];
        if (l.Closed)
        {
            if (l.Segments.Count <= 2) { loops.RemoveAt(li); return; }
            if (k > 0)
            {
                // An ordinary node: its two vectors merge where they are, so the loop's start/end point does not move.
                var before = l.Segments[k - 1];
                var after = l.Segments[k];
                l.Segments[k - 1] = before.Kind == SegmentKind.Cubic && after.Kind == SegmentKind.Cubic
                    ? Segment.Cubic(before.C1, after.C2, after.End)
                    : Segment.Line(after.End);
                l.Segments.RemoveAt(k);
                return;
            }
            Rotate(l, (k - 1 + l.Segments.Count) % l.Segments.Count);   // now the node is node 1
            var a = l.Segments[0];
            var b = l.Segments[1];
            var merged = a.Kind == SegmentKind.Cubic && b.Kind == SegmentKind.Cubic
                ? Segment.Cubic(a.C1, b.C2, b.End)
                : Segment.Line(b.End);
            l.Segments.RemoveAt(1);
            l.Segments[0] = merged;
            return;
        }
        if (l.Segments.Count <= 1) { loops.RemoveAt(li); return; }
        if (k == 0)
        {
            l.Start = l.Segments[0].End;
            l.Segments.RemoveAt(0);
        }
        else if (k >= l.Segments.Count)
        {
            l.Segments.RemoveAt(l.Segments.Count - 1);
        }
        else
        {
            var a = l.Segments[k - 1];
            var b = l.Segments[k];
            l.Segments[k - 1] = a.Kind == SegmentKind.Cubic && b.Kind == SegmentKind.Cubic
                ? Segment.Cubic(a.C1, b.C2, b.End)
                : Segment.Line(b.End);
            l.Segments.RemoveAt(k);
        }
    }

    /// <summary>
    /// Deletes vector <paramref name="i"/>: a closed loop opens there (its ends are the deleted vector's ends);
    /// an open loop splits in two. Empty loops are removed.
    /// </summary>
    public static void DeleteSegment(List<Loop> loops, int li, int i)
    {
        var l = loops[li];
        if (l.Closed)
        {
            if (l.Segments.Count <= 1) { loops.RemoveAt(li); return; }
            Rotate(l, (i + 1) % l.Segments.Count);   // the deleted vector is now the last one
            l.Segments.RemoveAt(l.Segments.Count - 1);
            l.Closed = false;
            return;
        }
        var head = new Loop { Start = l.Start, Segments = l.Segments.Take(i).ToList() };
        var tail = new Loop { Start = l.Segments[i].End, Segments = l.Segments.Skip(i + 1).ToList() };
        loops.RemoveAt(li);
        if (tail.Segments.Count > 0) loops.Insert(li, tail);
        if (head.Segments.Count > 0) loops.Insert(li, head);
    }

    // ---- Classification ----

    /// <summary>
    /// Border / interior / open for each loop, from how many other closed loops contain it (even = border, odd =
    /// interior), like blue and red vectors. Loops are in one coordinate space (e.g. all parts of a combined object).
    /// </summary>
    public static LoopKind[] Classify(IReadOnlyList<Loop> loops)
    {
        var kinds = new LoopKind[loops.Count];
        var closedGeoms = new (int Index, PathGeometry Geometry)[loops.Count];
        int nClosed = 0;
        for (int i = 0; i < loops.Count; i++)
            if (loops[i].Closed) closedGeoms[nClosed++] = (i, loops[i].ToGeometry());

        for (int i = 0; i < loops.Count; i++)
        {
            if (!loops[i].Closed) { kinds[i] = LoopKind.Open; continue; }
            var sample = loops[i].PointOn(0, 0.5);
            int depth = 0;
            for (int j = 0; j < nClosed; j++)
                if (closedGeoms[j].Index != i && closedGeoms[j].Geometry.Contains(sample, 0.01)) depth++;
            kinds[i] = depth % 2 == 0 ? LoopKind.Border : LoopKind.Interior;
        }
        return kinds;
    }
}

