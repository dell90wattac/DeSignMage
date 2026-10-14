namespace DeSignMage.Core.Geometry;

public static partial class PathEditing
{
    /// <summary>
    /// How the closed loops nest, in one coordinate space: <c>Depth[i]</c> is how many other closed loops contain loop i (0 = an outer
    /// loop, 1 = a hole, 2 = an island inside a hole, ...), <c>Parent[i]</c> the innermost loop that contains it (-1 for none).
    /// Open loops have depth -1 and no parent.
    /// </summary>
    public static (int[] Depth, int[] Parent) LoopTree(IReadOnlyList<Loop> loops)
    {
        int n = loops.Count;
        var depth = new int[n];
        var parent = new int[n];
        var geoms = new PathGeometry?[n];
        for (int i = 0; i < n; i++) geoms[i] = loops[i].Closed && loops[i].Segments.Count > 0 ? loops[i].ToGeometry() : null;

        var containers = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            containers[i] = [];
            if (geoms[i] is null) continue;
            var sample = loops[i].PointOn(0, 0.5);
            for (int j = 0; j < n; j++)
                if (j != i && geoms[j] is { } g && g.Contains(sample, 0.01)) containers[i].Add(j);
        }
        for (int i = 0; i < n; i++)
        {
            if (geoms[i] is null) { depth[i] = -1; parent[i] = -1; continue; }
            depth[i] = containers[i].Count;
            parent[i] = -1;
            int best = -1;
            foreach (int j in containers[i])
                if (containers[j].Count > best) { best = containers[j].Count; parent[i] = j; }
        }
        return (depth, parent);
    }

    /// <summary>Close All Loops: every open loop is joined end to start (with a straight vector if its ends are apart). Returns how many were closed.</summary>
    public static int CloseAll(IReadOnlyList<Loop> loops)
    {
        int closed = 0;
        foreach (var l in loops)
            if (!l.Closed && l.Segments.Count > 0 && Join(l)) closed++;
        return closed;
    }

    /// <summary>Loops of another coordinate space brought into this one: <paramref name="map"/> takes a loop's points to the target space.</summary>
    public static Loop Transformed(Loop l, Affine2 map)
    {
        if (map.IsIdentity) return l.Clone();
        var moved = ToLoops(l.ToGeometry().Transform(map));
        if (moved.Count == 0) return l.Clone();
        moved[0].Closed = l.Closed;
        return moved[0];
    }
}
