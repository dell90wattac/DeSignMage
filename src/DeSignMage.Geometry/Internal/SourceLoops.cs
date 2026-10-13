using DeSignMage.Core.Geometry;

namespace DeSignMage.Geometry.Internal;

/// <summary>One subpath of a source path as segments.</summary>
internal sealed class SourceLoop
{
    public readonly List<Seg> Segs = [];
    public bool Closed;
    public Vec2 Start => Segs[0].P0;
    public Vec2 End => Segs[^1].P1;
}

internal static class SourceLoops
{
    /// <summary>Loose bounds of all the paths (control points and full arc circles included).</summary>
    public static Rect2 Bounds(IEnumerable<PathGeometry> paths)
    {
        var r = Rect2.Empty;
        foreach (var p in paths)
            foreach (var v in p.Points) r = r.Union(v);
        return r;
    }

    /// <summary>
    /// Splits a path into subpaths of segments. Zero-length segments are dropped. A subpath without a Close verb whose end
    /// returns to its start (within <paramref name="eps"/>) counts as closed, as when filling.
    /// </summary>
    public static List<SourceLoop> From(PathGeometry path, double eps)
    {
        var loops = new List<SourceLoop>();
        SourceLoop? cur = null;
        Vec2 start = Vec2.Zero, pos = Vec2.Zero;
        var pts = path.Points;
        int pi = 0;

        void Finish(bool closed)
        {
            if (cur is not null)
            {
                if (closed && Vec2.Distance(pos, start) > eps && Seg.Line(pos, start, eps) is { } closing) cur.Segs.Add(closing);
                if (cur.Segs.Count > 0)
                {
                    cur.Closed = closed || (cur.Segs.Count >= 2 && Vec2.Distance(cur.End, cur.Start) <= eps);
                    loops.Add(cur);
                }
            }
            cur = null;
        }

        void Add(Seg? s)
        {
            if (s is null) return;
            if (cur is null) { cur = new SourceLoop(); start = s.P0; }
            cur.Segs.Add(s);
        }

        foreach (var v in path.Verbs)
        {
            switch (v)
            {
                case PathVerb.MoveTo:
                    Finish(false);
                    start = pos = pts[pi++];
                    break;
                case PathVerb.LineTo:
                {
                    var e = pts[pi++];
                    if (cur is null) start = pos;
                    Add(Seg.Line(pos, e, eps));
                    pos = e;
                    break;
                }
                case PathVerb.CubicTo:
                {
                    var c1 = pts[pi]; var c2 = pts[pi + 1]; var e = pts[pi + 2];
                    pi += 3;
                    if (cur is null) start = pos;
                    Add(Seg.Cubic(pos, c1, c2, e, eps));
                    pos = e;
                    break;
                }
                case PathVerb.ArcTo:
                {
                    var through = pts[pi]; var e = pts[pi + 1];
                    pi += 2;
                    if (cur is null) start = pos;
                    Add(Seg.Arc(pos, through, e, eps));
                    pos = e;
                    break;
                }
                case PathVerb.Close:
                    Finish(true);
                    pos = start;
                    break;
            }
        }
        Finish(false);
        return loops;
    }
}
