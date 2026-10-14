using Clipper2ZLib;
using DeSignMage.Core.Geometry;
using CFill = Clipper2ZLib.FillRule;

namespace DeSignMage.Geometry;

/// <summary>
/// <see cref="IOffsetPreviewService"/> on Clipper2 alone (no segment tags, no curve refit): the shape is flattened to the preview accuracy and cleaned by
/// a union once; each offset afterwards is one <c>ClipperOffset</c> pass over those integer polygons. Corners: Miter and Round are exact; Auto (the corner
/// rule with an outside angle threshold) is approximated by a miter whose limit is the longest miter an inside angle of exactly that threshold makes, so
/// corners at least as open as the threshold stay sharp and sharper ones are squared off instead of rounded. Thread-safe: a prepared source is never
/// changed and every offset uses its own state. See <c>notes/effects/step2-commands.md</c> (live preview).
/// </summary>
public sealed class ClipperOffsetPreview : IOffsetPreviewService
{
    /// <summary>Grid units per point: coordinates are rounded to 1/256 point (a document of 100 m stays far below 2^40).</summary>
    private const double Scale = 256;

    public IOffsetPreviewSource? Prepare(PathGeometry shape, double tolerance, bool contourOnly)
    {
        if (shape.IsEmpty) return null;
        var paths = new Paths64();
        foreach (var poly in shape.Flatten(Math.Max(tolerance * 0.5, 0.005)))
        {
            var p = poly.Points;
            bool closed = poly.Closed || (p.Length >= 4 && Vec2.Distance(p[0], p[^1]) < 1e-6);
            if (!closed) return null;   // an open path has no area to grow: the exact offset handles it
            int n = p.Length;
            if (n > 1 && Vec2.Distance(p[0], p[^1]) < 1e-9) n--;
            if (n < 3) continue;
            var path = new Path64(n);
            for (int i = 0; i < n; i++) path.Add(new Point64((long)Math.Round(p[i].X * Scale), (long)Math.Round(p[i].Y * Scale)));
            paths.Add(path);
        }
        if (paths.Count == 0) return null;

        // Cleaned by a union (the shape's own fill rule), then thinned: lettering has far more points than a screen can show, and the
        // time of an offset grows much faster than the number of points. Half the tolerance goes to the flattening, half to the thinning.
        var clipper = new Clipper64();
        clipper.AddSubject(paths);
        var tree = new PolyTree64();
        clipper.Execute(ClipType.Union, shape.FillRule == Core.Geometry.FillRule.EvenOdd ? CFill.EvenOdd : CFill.NonZero, tree);
        var groups = new List<Paths64>();
        Collect(tree, groups, contourOnly, tolerance * 0.5 * Scale);
        return groups.Count == 0 ? null : new Source(groups, tolerance);
    }

    /// <summary>Every outer outline with the holes directly inside it is one group (an offset needs them together to know which way to grow); islands inside holes are groups of their own.</summary>
    private static void Collect(PolyPath64 node, List<Paths64> groups, bool contourOnly, double epsilon)
    {
        if (!node.IsHole && node.Polygon is { Count: >= 3 } outer)
        {
            var group = new Paths64();
            AddThinned(group, outer, epsilon);
            if (!contourOnly)
                for (int i = 0; i < node.Count; i++)
                    if (node[i].IsHole && node[i].Polygon is { Count: >= 3 } hole) AddThinned(group, hole, epsilon);
            if (group.Count > 0 && Clipper.IsPositive(group[0])) groups.Add(group);
            else if (group.Count > 0 && group[0].Count >= 3) groups.Add(group);
        }
        for (int i = 0; i < node.Count; i++) Collect(node[i], groups, contourOnly, epsilon);
    }

    private static void AddThinned(Paths64 into, Path64 path, double epsilon)
    {
        var thin = epsilon > 0 ? Clipper.SimplifyPath(path, epsilon, true) : path;
        if (thin.Count >= 3) into.Add(thin);
    }

    private sealed class Source(List<Paths64> groups, double tolerance) : IOffsetPreviewSource
    {
        public PathGeometry Offset(double distance, double second, CornerStyle corners, double outsideAngleThreshold)
        {
            if (second == 0) return Build(Grow(distance, corners, outsideAngleThreshold));
            var a = Grow(distance, corners, outsideAngleThreshold);
            var b = Grow(distance + second, corners, outsideAngleThreshold);
            var (outer, inner) = second > 0 ? (b, a) : (a, b);   // the larger offset is the larger area
            var clipper = new Clipper64();
            clipper.AddSubject(outer);
            clipper.AddClip(inner);
            var band = new Paths64();
            clipper.Execute(ClipType.Difference, CFill.NonZero, band);
            return Build(band);
        }

        /// <summary>
        /// Each group is offset by itself and the results united once: Clipper's own pass over all the groups at once is far slower when the offsets
        /// overlap a lot (a logo of small letters grown by a few percent of its height).
        /// </summary>
        private Paths64 Grow(double distance, CornerStyle corners, double oat)
        {
            var joined = new Paths64();
            if (distance == 0)
            {
                foreach (var g in groups) joined.AddRange(g);
                return joined;
            }
            var (join, miterLimit) = corners switch
            {
                CornerStyle.Round => (JoinType.Round, 2.0),
                CornerStyle.Bevel => (JoinType.Bevel, 2.0),
                CornerStyle.Miter => (JoinType.Miter, 20.0),
                _ => (JoinType.Miter, Math.Clamp(1 / Math.Sin(Math.Clamp(oat, 1, 179) * Math.PI / 360), 1.0, 20.0)),
            };
            double arcTolerance = Math.Max(tolerance * Scale, 0.25);
            foreach (var g in groups)
            {
                var offsetter = new ClipperOffset(miterLimit, arcTolerance, true, false);
                offsetter.AddPaths(g, join, EndType.Polygon);
                var one = new Paths64();
                offsetter.Execute(distance * Scale, one);
                joined.AddRange(one);
            }
            if (groups.Count == 1) return joined;
            var union = new Clipper64();
            union.AddSubject(joined);
            var result = new Paths64();
            union.Execute(ClipType.Union, CFill.NonZero, result);
            return result;
        }

        private static PathGeometry Build(Paths64 paths)
        {
            var b = new PathBuilder();
            foreach (var path in paths)
            {
                if (path.Count < 3) continue;
                b.MoveTo(path[0].X / Scale, path[0].Y / Scale);
                for (int i = 1; i < path.Count; i++) b.LineTo(path[i].X / Scale, path[i].Y / Scale);
                b.Close();
            }
            return b.Build();
        }
    }
}