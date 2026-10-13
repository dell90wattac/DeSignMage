namespace DeSignMage.Core.Geometry;

/// <summary>How dragging a Bézier handle treats the handle on the other side of the same node.</summary>
public enum HandleMode
{
    /// <summary>Keeps a smooth node smooth (the other handle turns to stay opposite, its length unchanged); a corner node's handles move independently. "corner flag off".</summary>
    Auto,
    /// <summary>Only the dragged handle moves (breaks the tangent).</summary>
    Free,
    /// <summary>The other handle mirrors the dragged one: same length, opposite direction.</summary>
    Symmetric,
}

/// <summary>What kind of node this is, derived from the vectors on both sides (nothing is stored per node).</summary>
public enum NodeKind { Corner, Smooth, Symmetric, End }

/// <summary>Counts shown in the info line ("Graphic 40 Vectors ... 0 lines 0 arcs 35 beziers 4 small vecs").</summary>
public readonly record struct VectorStats(int Loops, int Segments, int Lines, int Arcs, int Beziers, int SmallVectors)
{
    /// <summary>A loop's start point counts as a vector too (35 Béziers in 5 loops show "40 Vectors").</summary>
    public int Vectors => Segments + Loops;
}

public static partial class PathEditing
{
    /// <summary>Index of the node a cubic's handle belongs to: handle 1 sits at the segment's start node, handle 2 at its end node.</summary>
    public static int HandleNode(Loop l, int segment, int which) =>
        which == 1 ? segment : l.Closed && segment + 1 == l.Segments.Count ? 0 : segment + 1;

    public static NodeKind KindOf(Loop l, int k)
    {
        int a = l.Incoming(k), b = l.Outgoing(k);
        if (a < 0 || b < 0) return NodeKind.End;
        if (l.IsCorner(k)) return NodeKind.Corner;
        if (l.Segments[a].Kind == SegmentKind.Cubic && l.Segments[b].Kind == SegmentKind.Cubic)
        {
            double inLen = (l.Segments[a].End - l.Segments[a].C2).Length, outLen = (l.Segments[b].C1 - l.SegmentStart(b)).Length;
            if (inLen > 1e-9 && Math.Abs(inLen - outLen) <= 0.02 * Math.Max(inLen, outLen)) return NodeKind.Symmetric;
        }
        return NodeKind.Smooth;
    }

    /// <summary>
    /// Make Smooth: the vectors on both sides of node <paramref name="k"/> leave it along one line. Lines and arcs next to it
    /// become Béziers first. A node that is already (nearly) smooth keeps its direction; a corner gets the direction from
    /// the previous node to the next, with handles a third of the way there.
    /// </summary>
    public static bool MakeSmooth(Loop l, int k) => Align(l, k, symmetric: false);

    /// <summary>Make Symmetric: smooth, and both handles equally long.</summary>
    public static bool MakeSymmetric(Loop l, int k) => Align(l, k, symmetric: true);

    private static bool Align(Loop l, int k, bool symmetric)
    {
        int a = l.Incoming(k), b = l.Outgoing(k);
        if (a < 0 || b < 0 || a == b) return false;
        var node = l.Node(k);
        var prev = l.SegmentStart(a);
        var next = l.Segments[b].End;
        bool wasSmooth = !l.IsCorner(k) && l.Segments[a].Kind == SegmentKind.Cubic && l.Segments[b].Kind == SegmentKind.Cubic;
        var before = (l.Segments[a], l.Segments[b]);
        ToBezier(l, a);
        ToBezier(l, b);

        double inLen = (node - l.Segments[a].C2).Length, outLen = (l.Segments[b].C1 - node).Length;
        if (inLen < 1e-9) inLen = Vec2.Distance(prev, node) / 3;
        if (outLen < 1e-9) outLen = Vec2.Distance(node, next) / 3;

        var dir = wasSmooth ? Unit(Unit(node - l.Segments[a].C2) + Unit(l.Segments[b].C1 - node)) : Unit(next - prev);
        if (dir == Vec2.Zero) dir = Unit(next - node);
        if (dir == Vec2.Zero) return false;
        if (symmetric) inLen = outLen = (inLen + outLen) / 2;

        l.Segments[a] = l.Segments[a] with { C2 = node - dir * inLen };
        l.Segments[b] = l.Segments[b] with { C1 = node + dir * outLen };
        return (l.Segments[a], l.Segments[b]) != before;
    }

    /// <summary>
    /// Make Corner: the Bézier handles at node <paramref name="k"/> are pulled in to the node, so the vectors leave it
    /// independently (the way a sharp corner reads). Lines and arcs have no handles and are left as they are.
    /// </summary>
    public static bool MakeCorner(Loop l, int k)
    {
        int a = l.Incoming(k), b = l.Outgoing(k);
        var node = l.Node(k);
        bool changed = false;
        if (a >= 0 && l.Segments[a].Kind == SegmentKind.Cubic && l.Segments[a].C2 != node)
        {
            l.Segments[a] = l.Segments[a] with { C2 = node };
            changed = true;
        }
        if (b >= 0 && b != a && l.Segments[b].Kind == SegmentKind.Cubic && l.Segments[b].C1 != node)
        {
            l.Segments[b] = l.Segments[b] with { C1 = node };
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Sharp ("Make Sharp Corner"): an arc between two vectors (a rounded corner) becomes two lines that continue
    /// the neighbours' tangents and meet in a point. False if the tangents don't meet in front of the arc.
    /// </summary>
    public static bool MakeSharp(Loop l, int i)
    {
        if (l.Segments[i].Kind != SegmentKind.Arc) return false;
        int prev = i > 0 ? i - 1 : l.Closed ? l.Segments.Count - 1 : -1;
        int next = i + 1 < l.Segments.Count ? i + 1 : l.Closed ? 0 : -1;
        if (prev < 0 || next < 0 || prev == i || next == i) return false;
        var s = l.SegmentStart(i);
        var e = l.Segments[i].End;
        Vec2 t1 = l.EndTangent(prev), t2 = l.StartTangent(next);
        double cross = Vec2.Cross(t1, t2);
        if (Math.Abs(cross) < 1e-9 * t1.Length * t2.Length) return false;
        var d = e - s;
        double a = Vec2.Cross(d, t2) / cross;   // s + a*t1 = e + c*t2
        double c = Vec2.Cross(d, t1) / cross;
        if (a <= 1e-9 || c >= -1e-9) return false;
        l.Segments[i] = Segment.Line(s + t1 * a);
        l.Segments.Insert(i + 1, Segment.Line(e));
        return true;
    }

    /// <summary>Drags a whole line: both of its nodes move, and the vectors next to them stretch ("move line").</summary>
    public static void MoveLine(Loop l, int i, Vec2 delta)
    {
        int k0 = i, k1 = HandleNode(l, i, 2);
        var p0 = l.Node(k0) + delta;
        var p1 = l.Node(k1) + delta;
        MoveNode(l, k0, p0);
        MoveNode(l, k1, p1);
    }

    /// <summary>A line becomes an arc through <paramref name="through"/> (Ctrl while dragging a line's middle).</summary>
    public static void BendLine(Loop l, int i, Vec2 through) => l.Segments[i] = Segment.Arc(through, l.Segments[i].End);

    /// <summary>
    /// Joins an end of loop <paramref name="la"/> to an end of loop <paramref name="lb"/> (open loops only). The loops
    /// become one, in the first loop's place; a straight vector bridges the gap if the ends are apart. The same loop
    /// with its two ends is Join Start/End (closes it). Returns the merged loop's index, or -1 if nothing could be joined.
    /// </summary>
    public static int JoinLoops(List<Loop> loops, int la, bool aAtStart, int lb, bool bAtStart)
    {
        var a = loops[la];
        if (la == lb)
        {
            if (a.Closed || aAtStart == bAtStart || a.Segments.Count == 0) return -1;
            Join(a);
            return la;
        }
        var b = loops[lb];
        if (a.Closed || b.Closed || a.Segments.Count == 0 || b.Segments.Count == 0) return -1;
        if (aAtStart) Reverse(a);        // a's joining end is now its last node
        if (!bAtStart) Reverse(b);       // b's joining end is now its first node
        if (Vec2.Distance(a.Segments[^1].End, b.Start) > Same) a.Segments.Add(Segment.Line(b.Start));
        else a.Segments[^1] = a.Segments[^1] with { End = b.Start };
        a.Segments.AddRange(b.Segments);
        loops.RemoveAt(lb);
        return lb < la ? la - 1 : la;
    }

    /// <summary>
    /// To Circle: the loop is replaced by a circle with the same center and the average of its width and height as the
    /// diameter (good for cleaning up traced dots and holes). The direction and the start point's side are kept.
    /// </summary>
    public static void ToCircle(Loop l)
    {
        var b = l.Bounds();
        var c = b.Center;
        double r = (b.Width + b.Height) / 4;
        if (r < 1e-9) return;
        bool clockwise = l.SignedArea() >= 0;
        double a0 = Math.Atan2(l.Start.Y - c.Y, l.Start.X - c.X);
        double step = (clockwise ? 1 : -1) * Math.PI / 2;   // increasing angle runs clockwise on screen (y down)
        Vec2 At(double angle) => c + new Vec2(Math.Cos(angle), Math.Sin(angle)) * r;
        var start = At(a0);
        l.Start = start;
        l.Segments = [];
        for (int i = 1; i <= 4; i++)
            l.Segments.Add(Segment.Arc(At(a0 + step * (i - 0.5)), i == 4 ? start : At(a0 + step * i)));
        l.Closed = true;
    }

    /// <summary>How many other closed loops contain each closed loop (0 = outermost); -1 for open loops.</summary>
    public static int[] Depths(IReadOnlyList<Loop> loops)
    {
        var depths = new int[loops.Count];
        var geoms = new PathGeometry?[loops.Count];
        for (int i = 0; i < loops.Count; i++) geoms[i] = loops[i].Closed ? loops[i].ToGeometry() : null;
        for (int i = 0; i < loops.Count; i++)
        {
            if (geoms[i] is null) { depths[i] = -1; continue; }
            var sample = loops[i].PointOn(0, 0.5);
            int depth = 0;
            for (int j = 0; j < loops.Count; j++)
                if (j != i && geoms[j] is { } g && g.Contains(sample, 0.01)) depth++;
            depths[i] = depth;
        }
        return depths;
    }

    /// <summary>
    /// Reorder Loops (Edit menu): outermost loops clockwise, each deeper level alternating, and inner loops come before the
    /// outer ones (what routers and cutters want). <paramref name="mirrored"/> says the object's transform flips direction.
    /// Returns false if nothing had to change.
    /// </summary>
    public static bool ReorderLoops(List<Loop> loops, bool mirrored = false)
    {
        var depths = Depths(loops);
        bool changed = false;
        for (int i = 0; i < loops.Count; i++)
        {
            if (depths[i] < 0) continue;
            bool wantClockwise = depths[i] % 2 == 0;
            if ((loops[i].IsClockwise ^ mirrored) != wantClockwise) { Reverse(loops[i]); changed = true; }
        }
        var order = Enumerable.Range(0, loops.Count).OrderByDescending(i => depths[i]).ToList();   // stable: deepest first, open loops last
        if (!order.SequenceEqual(Enumerable.Range(0, loops.Count)))
        {
            var sorted = order.Select(i => loops[i]).ToList();
            loops.Clear();
            loops.AddRange(sorted);
            changed = true;
        }
        return changed;
    }

    /// <summary>Counts for the info line; <paramref name="scale"/> converts a loop's length to document points (the object's world scale).</summary>
    public static VectorStats Stats(IEnumerable<(Loop Loop, double Scale)> loops, double smallLength)
    {
        int n = 0, segs = 0, lines = 0, arcs = 0, beziers = 0, small = 0;
        foreach (var (l, scale) in loops)
        {
            n++;
            for (int i = 0; i < l.Segments.Count; i++)
            {
                segs++;
                switch (l.Segments[i].Kind)
                {
                    case SegmentKind.Line: lines++; break;
                    case SegmentKind.Arc: arcs++; break;
                    default: beziers++; break;
                }
                if (l.SegmentLength(i) * scale < smallLength) small++;
            }
        }
        return new VectorStats(n, segs, lines, arcs, beziers, small);
    }

    private static Vec2 Unit(Vec2 v) => v.Length < 1e-12 ? Vec2.Zero : v / v.Length;
}
