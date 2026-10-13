using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Effects;

/// <summary>
/// What the effect commands share: collecting the objects an effect works on, their world-space geometry, and counting loops.
/// A "leaf" is an object that has an outline of its own; groups are looked through, a combined object counts as one leaf
/// (its knocked-out even-odd area is what a weld sees, as in the picture).
/// </summary>
internal static class EffectGeometry
{
    /// <summary>The shape-bearing objects inside <paramref name="roots"/>, bottom to top, groups flattened. Pictures are leaves too; test <see cref="IsShape"/>.</summary>
    public static List<DocObject> Leaves(IEnumerable<DocObject> roots)
    {
        var list = new List<DocObject>();
        foreach (var r in roots) Collect(r, list);
        return list;
    }

    private static void Collect(DocObject o, List<DocObject> list)
    {
        // A group's members are not necessarily next to each other in the drawing order, and the order they were put in is not it: bottom to top means drawn.
        if (o is GroupObject g)
            list.AddRange(g.Leaves().OrderBy(l => l.Layer?.IndexOf(l) ?? -1));
        else
            list.Add(o);
    }

    /// <summary>Shown and not locked, as an effect that works on whatever is on the page (Cookie Cutter's dough, the Knife) needs it: no hidden or locked group around it either.</summary>
    public static bool IsEditable(DocObject leaf)
    {
        for (DocObject? o = leaf; o is not null; o = o.Parent as DocObject)
            if (!o.Visible || o.Locked) return false;
        return true;
    }

    /// <summary>Vector things an effect can cut, weld or outline. Pictures are not (bitmaps are left out of every one of these).</summary>
    public static bool IsShape(DocObject o) => o is not (GroupObject or ImageObject);

    public static bool ContainsImage(IEnumerable<DocObject> roots) =>
        roots.Any(r => r is ImageObject || (r is GroupObject g && Document.Walk(g).Any(c => c is ImageObject)));

    /// <summary>The leaf's outline in document coordinates, or null when it has none.</summary>
    public static PathGeometry? World(DocObject leaf)
    {
        if (!IsShape(leaf) || leaf.GetLocalGeometry() is not { IsEmpty: false } g) return null;
        return g.Transform(leaf.GetWorldTransform());
    }

    /// <summary>Maps document coordinates into the space the leaf's siblings live in (identity for a top-level object).</summary>
    public static Affine2 ParentWorld(DocObject leaf) => leaf.Parent is DocObject p ? p.GetWorldTransform() : Affine2.Identity;

    /// <summary>How many closed and how many open subpaths <paramref name="g"/> has. A subpath that ends where it starts counts as closed.</summary>
    public static (int Closed, int Open) CountLoops(PathGeometry g)
    {
        int closed = 0, open = 0, pi = 0, segs = 0;
        bool inSub = false, closeVerb = false;
        Vec2 start = default, last = default;

        void End()
        {
            if (!inSub) return;
            if (segs >= 2 && (closeVerb || Vec2.Distance(start, last) < 1e-6)) closed++;
            else if (segs >= 1) open++;
            inSub = false;
        }

        var points = g.Points;
        foreach (var v in g.Verbs)
        {
            switch (v)
            {
                case PathVerb.MoveTo: End(); start = last = points[pi++]; inSub = true; closeVerb = false; segs = 0; break;
                case PathVerb.LineTo: last = points[pi++]; segs++; break;
                case PathVerb.CubicTo: last = points[pi + 2]; pi += 3; segs++; break;
                case PathVerb.ArcTo: last = points[pi + 1]; pi += 2; segs++; break;
                case PathVerb.Close: closeVerb = true; break;
            }
        }
        End();
        return (closed, open);
    }

    public static bool HasClosedArea(PathGeometry g) => CountLoops(g).Closed > 0;

    /// <summary>All subpaths of the parts as one path with the given fill rule.</summary>
    public static PathGeometry Concat(IEnumerable<PathGeometry> parts, FillRule rule)
    {
        var verbs = new List<PathVerb>();
        var points = new List<Vec2>();
        foreach (var p in parts)
        {
            verbs.AddRange(p.Verbs);
            points.AddRange(p.Points);
        }
        return verbs.Count == 0 ? PathGeometry.EmptyPath : new PathGeometry(verbs.ToArray(), points.ToArray(), rule);
    }

    /// <summary>
    /// The separate shapes a Weld or Common Weld merges: every leaf, except that a combined object counts as the PARTS it was made of.
    /// Its own outline is one even-odd path that already knocks the overlaps out, so welding that as a single shape would keep the knock-out
    /// and change nothing; the parts, each filled by its own rule (so a letter's hole stays a hole), are what overlap. Outlines in document coordinates.
    /// </summary>
    public static List<(DocObject Source, PathGeometry World)> Operands(IEnumerable<DocObject> roots)
    {
        var list = new List<(DocObject, PathGeometry)>();
        foreach (var leaf in Leaves(roots))
        {
            if (leaf is CombinedObject c)
            {
                var world = c.GetWorldTransform();
                foreach (var part in c.Parts)
                    if (IsShape(part) && part.GetLocalGeometry() is { IsEmpty: false } g)
                        list.Add((c, g.Transform(part.Transform * world)));
            }
            else if (World(leaf) is { } w) list.Add((leaf, w));
        }
        return list;
    }

    /// <summary>
    /// True when a closed loop of <paramref name="g"/> crosses another loop or itself, i.e. a weld would merge something inside this one shape.
    /// A hole inside its outline does not count. Very large outlines are assumed to overlap (the check must stay quick: it runs when the menu opens).
    /// </summary>
    public static bool LoopsCross(PathGeometry g)
    {
        var loops = g.Flatten(0.5).Where(p => p.Closed || (p.Points.Length >= 4 && Vec2.Distance(p.Points[0], p.Points[^1]) < 1e-6)).ToList();
        if (loops.Sum(l => l.Points.Length) > 6000) return true;
        var bounds = loops.Select(l => l.Points.Aggregate(Rect2.Empty, (r, p) => r.Union(p))).ToList();
        for (int i = 0; i < loops.Count; i++)
        {
            if (SelfCrosses(loops[i].Points)) return true;
            for (int j = i + 1; j < loops.Count; j++)
                if (bounds[i].Intersects(bounds[j]) && Crosses(loops[i].Points, loops[j].Points)) return true;
        }
        return false;
    }

    private static bool Crosses(Vec2[] a, Vec2[] b)
    {
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
                if (SegmentsCross(a[i], a[(i + 1) % a.Length], b[j], b[(j + 1) % b.Length])) return true;
        return false;
    }

    private static bool SelfCrosses(Vec2[] p)
    {
        int n = p.Length;
        for (int i = 0; i < n; i++)
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue;   // the closing segment touches the first
                if (SegmentsCross(p[i], p[(i + 1) % n], p[j], p[(j + 1) % n])) return true;
            }
        return false;
    }

    private static bool SegmentsCross(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        double d1 = Vec2.Cross(b - a, c - a), d2 = Vec2.Cross(b - a, d - a);
        double d3 = Vec2.Cross(d - c, a - c), d4 = Vec2.Cross(d - c, b - c);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    /// <summary>Total number of segments: a cheap measure of how long an effect on this geometry takes.</summary>
    public static int Weight(IEnumerable<PathGeometry> parts) => parts.Sum(p => p.Verbs.Count);

    /// <summary>A copy of <paramref name="g"/> in the space of <paramref name="leaf"/>'s parent (undoes the parent's world transform).</summary>
    public static PathGeometry IntoParentSpace(PathGeometry world, DocObject leaf)
    {
        var pw = ParentWorld(leaf);
        return pw.IsIdentity || !pw.IsInvertible ? world : world.Transform(pw.Invert());
    }

    /// <summary>The new path object for a computed result: same fill/stroke/visibility as <paramref name="source"/>, a simple name.</summary>
    public static PathObject NewResult(PathGeometry geometry, string name, DocObject source) =>
        new(geometry) { Name = name, Style = source.Style, Visible = source.Visible, Locked = source.Locked };

    /// <summary>Position in the drawing order of a top-level object (a group counts where its topmost member is): (layer index, index in the layer).</summary>
    public static (int Layer, int Index) StackPosition(Document doc, DocObject topLevel)
    {
        long key = new Stacking.Order(doc).TopKey(topLevel);
        return key < 0 ? (-1, -1) : ((int)(key >> 32), (int)(key & 0xFFFFFFFF));
    }
}
