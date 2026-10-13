using Clipper2ZLib;
using DeSignMage.Core.Geometry;
using DeSignMage.Geometry.Internal;
using CFill = Clipper2ZLib.FillRule;

namespace DeSignMage.Geometry;

/// <summary>
/// <see cref="IBooleanService"/> on Clipper2: paths are flattened within the tolerance with every vertex tagged by its
/// source segment (Clipper's Z field), the boolean runs on a fine integer grid, and the result is rebuilt from the tags:
/// where it follows a source line, arc or cubic, that segment (or the cut piece of it) comes back exactly, with cut
/// points on the exact crossing. Thread-safe: every call uses its own state.
/// </summary>
public sealed class ClipperBooleanService : IBooleanService
{
    public PathGeometry Combine(BooleanOp op, IReadOnlyList<PathGeometry> subjects, IReadOnlyList<PathGeometry> clips,
        BooleanOptions? options = null)
    {
        options ??= BooleanOptions.Default;
        var bounds = SourceLoops.Bounds(subjects.Concat(clips));
        if (bounds.IsEmpty) return PathGeometry.EmptyPath;
        var engine = new Engine(bounds, options.Tolerance);
        var tree = Execute(engine, op, subjects, clips, options);
        return OutPaths.Build(engine.Rebuild(tree, options.KeepCurves, options.MinLoopArea));
    }

    public IReadOnlyList<PathGeometry> SplitIntoIslands(PathGeometry shape, BooleanOptions? options = null)
    {
        options ??= BooleanOptions.Default;
        var bounds = SourceLoops.Bounds([shape]);
        if (bounds.IsEmpty) return [];
        var engine = new Engine(bounds, options.Tolerance);
        var tree = Execute(engine, BooleanOp.Union, [shape], [], options);
        return Islands(engine, tree, options).Select(i => i.Path).ToList();
    }

    public IReadOnlyList<PathGeometry> SplitByLine(PathGeometry shape, Vec2 from, Vec2 to, SplitOptions? options = null)
    {
        options ??= SplitOptions.Default;
        var bo = options.Boolean;
        var shapeBounds = SourceLoops.Bounds([shape]);
        if (shapeBounds.IsEmpty) return [];
        var d = to - from;
        double len = d.Length;
        if (!(len > 0)) return SplitIntoIslands(shape, bo);
        var dir = d / len;
        var normal = new Vec2(-dir.Y, dir.X);     // points to the side where Cross(dir, p - from) > 0

        // Long enough to cross the whole shape when extended.
        var all = shapeBounds.Union(from).Union(to);
        double reach = Math.Max(all.Width, all.Height) * 2 + len;
        bool extend = options.ExtendLine || options.Overlap is not null;
        var a = extend ? from - dir * reach : from;
        var b = extend ? to + dir * reach : to;

        if (options.Overlap is { } overlap)
        {
            // Two half-planes (as big polygons), each reaching overlap/2 past the line: screen-left half first.
            double h = Math.Max(overlap, 0) / 2;
            var left = Engine.Polygon(a + normal * h, b + normal * h, b - normal * reach, a - normal * reach);
            var right = Engine.Polygon(a - normal * h, b - normal * h, b + normal * reach, a + normal * reach);
            var engineO = new Engine(SourceLoops.Bounds([shape, left, right]), bo.Tolerance);
            var result = new List<PathGeometry>();
            foreach (var half in new[] { left, right })
                result.AddRange(Islands(engineO, Execute(engineO, BooleanOp.Intersect, [shape], [half], bo), bo).Select(i => i.Path));
            return result;
        }

        double g = Math.Max(options.Gap, 1e-3) / 2;      // a zero-width blade would not separate anything
        var blade = Engine.Polygon(a + normal * g, b + normal * g, b - normal * g, a - normal * g);
        var engine = new Engine(SourceLoops.Bounds([shape, blade]), bo.Tolerance);
        var pieces = new List<(PathGeometry Path, double Side)>();
        var cut = Execute(engine, BooleanOp.Difference, [shape], [blade], bo);
        foreach (var island in Islands(engine, cut, bo))
        {
            var c = Centroid(island.Path);
            pieces.Add((island.Path, Vec2.Cross(dir, c - from)));
        }
        return pieces.OrderBy(p => p.Side).Select(p => p.Path).ToList();
    }

    // ---- internals ----

    internal static PolyTree64 Execute(Engine engine, BooleanOp op, IReadOnlyList<PathGeometry> subjects,
        IReadOnlyList<PathGeometry> clips, BooleanOptions options)
    {
        var subj = engine.Normalize(subjects, options.FillRule);
        var clip = engine.Normalize(clips, options.FillRule);
        var c = engine.NewClipper();
        c.AddSubject(subj);
        c.AddClip(clip);
        var tree = new PolyTree64();
        c.Execute(ToClipType(op), CFill.NonZero, tree);
        return tree;
    }

    private static ClipType ToClipType(BooleanOp op) => op switch
    {
        BooleanOp.Union => ClipType.Union,
        BooleanOp.Intersect => ClipType.Intersection,
        BooleanOp.Difference => ClipType.Difference,
        _ => ClipType.Xor,
    };

    /// <summary>Top-level islands of a result tree: each outer loop with its holes; islands in holes are their own pieces.</summary>
    private static List<(PathGeometry Path, double Area)> Islands(Engine engine, PolyTree64 tree, BooleanOptions options)
    {
        var result = new List<(PathGeometry, double)>();
        var queue = new Queue<PolyPath64>();
        for (int i = 0; i < tree.Count; i++) queue.Enqueue(tree[i]);
        while (queue.Count > 0)
        {
            var outer = queue.Dequeue();
            if (outer.Polygon is not { Count: >= 3 } outerPoly) continue;
            double area = engine.LoopArea(outerPoly);
            if (area < options.MinLoopArea) continue;
            var loops = new List<OutLoop>();
            for (int h = 0; h < outer.Count; h++)
            {
                var hole = outer[h];
                for (int k = 0; k < hole.Count; k++) queue.Enqueue(hole[k]);
                if (hole.Polygon is { Count: >= 3 } hp && engine.LoopArea(hp) >= options.MinLoopArea &&
                    Reconstruct.Loop(hp, engine.Tags, engine.Tol, engine.Eps, options.KeepCurves) is { } hl)
                    loops.Add(hl);
            }
            if (Reconstruct.Loop(outerPoly, engine.Tags, engine.Tol, engine.Eps, options.KeepCurves) is { } ol) loops.Add(ol);
            result.Add((OutPaths.Build(loops), area));
        }
        result.Sort((x, y) => y.Item2.CompareTo(x.Item2));
        return result;
    }

    /// <summary>Area centroid of a path's flattened outline (holes count negative).</summary>
    internal static Vec2 Centroid(PathGeometry path)
    {
        double a = 0, cx = 0, cy = 0;
        foreach (var poly in path.Flatten(0.05))
        {
            var p = poly.Points;
            for (int i = 0; i < p.Length; i++)
            {
                var q = p[i]; var r = p[(i + 1) % p.Length];
                double cr = Vec2.Cross(q, r);
                a += cr; cx += (q.X + r.X) * cr; cy += (q.Y + r.Y) * cr;
            }
        }
        if (Math.Abs(a) < 1e-300) return path.GetBounds().Center;
        return new Vec2(cx / (3 * a), cy / (3 * a));
    }
}
