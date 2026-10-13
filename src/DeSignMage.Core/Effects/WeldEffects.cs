using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Effects;

/// <summary>
/// Effects ▸ Weld: the selection becomes ONE new object, the union of everything
/// in it (merged outer outline, holes kept), and the originals are removed. Live text in the selection turns into curves by
/// being replaced. The result has NO color (no fill, no stroke: "a weld removes all color", user 2026-10-12) so it shows as its
/// plain vector outline, sits where the topmost original was, and is named "Weld". One undo step.
/// </summary>
public static class WeldEffect
{
    /// <summary>The shapes (leaf, outline in document coordinates) a weld or common weld works on, bottom to top.</summary>
    internal static List<(DocObject Leaf, PathGeometry World)> Shapes(EditorSession s, out List<DocObject> items)
    {
        var all = Stacking.BottomToTop(s.Document, s.Selection.Items);
        var shapes = new List<(DocObject, PathGeometry)>();
        foreach (var leaf in EffectGeometry.Leaves(all))
            if (EffectGeometry.World(leaf) is { } g && EffectGeometry.HasClosedArea(g)) shapes.Add((leaf, g));
        // Only objects that took part are replaced: a stray open line in the selection stays where it is.
        items = Stacking.BottomToTop(s.Document, shapes.Select(x => x.Item1.TopLevel));
        return shapes;
    }

    /// <summary>Weld needs two closed loops to merge, anywhere in the selection (two objects, or one script word whose letters overlap), and no pictures.</summary>
    public static bool CanRun(EditorSession s)
    {
        if (s.BooleanService is null || s.Selection.IsEmpty || EffectGeometry.ContainsImage(s.Selection.Items)) return false;
        int loops = 0;
        foreach (var leaf in EffectGeometry.Leaves(s.Selection.Items))
        {
            if (EffectGeometry.World(leaf) is not { } g) continue;
            loops += EffectGeometry.CountLoops(g).Closed;
            if (loops >= 2) return true;
        }
        return false;
    }

    public static EffectPlan? Plan(EditorSession s)
    {
        if (!CanRun(s) || s.BooleanService is not { } booleans) return null;
        var shapes = Shapes(s, out var items);
        var geometries = shapes.Select(x => x.World).ToList();
        var styleSource = shapes[0].Leaf;
        return new EffectPlan("Weld", "Welding...", ct =>
        {
            var merged = booleans.Union(geometries);
            ct.ThrowIfCancellationRequested();
            if (merged.IsEmpty) return EffectOutcome.Nothing("Nothing to weld.");
            return EffectOutcome.Change(session =>
            {
                var result = EffectGeometry.NewResult(merged, "Weld", styleSource);
                result.Style = ObjectStyle.None;   // a weld removes all color
                session.History.Execute(new ReplaceObjectsCommand(session.Document, items, [result], "Weld"));
                session.Selection.Set(result);
            });
        });
    }
}

/// <summary>
/// Effects ▸ Common Weld: the overlap of the shapes, as one new object; the originals
/// are removed. Two shapes give their overlap. Three or more give EVERY pairwise overlap, kept as separate overlapping lens loops in
/// the one result (the loops are not merged), filled non-zero so the middle where three overlap stays solid. Named "Common Weld".
/// </summary>
public static class CommonWeldEffect
{
    /// <summary>At least two separate shapes with an area.</summary>
    public static bool CanRun(EditorSession s)
    {
        if (s.BooleanService is null || s.Selection.IsEmpty || EffectGeometry.ContainsImage(s.Selection.Items)) return false;
        int shapes = 0;
        foreach (var leaf in EffectGeometry.Leaves(s.Selection.Items))
        {
            if (EffectGeometry.World(leaf) is { } g && EffectGeometry.HasClosedArea(g) && ++shapes >= 2) return true;
        }
        return false;
    }

    public static EffectPlan? Plan(EditorSession s)
    {
        if (!CanRun(s) || s.BooleanService is not { } booleans) return null;
        var shapes = WeldEffect.Shapes(s, out var items);
        var geometries = shapes.Select(x => x.World).ToList();
        var styleSource = shapes[0].Leaf;
        return new EffectPlan("Common Weld", "Welding...", ct => Compute(booleans, geometries, styleSource, items, ct));
    }

    /// <summary>The lens loops: Intersect of every pair of shapes whose bounds meet (empty overlaps are left out).</summary>
    internal static PathGeometry Overlaps(IBooleanService booleans, IReadOnlyList<PathGeometry> shapes, CancellationToken ct = default)
    {
        var bounds = shapes.Select(g => g.GetBounds()).ToList();
        var lenses = new List<PathGeometry>();
        for (int i = 0; i < shapes.Count; i++)
            for (int j = i + 1; j < shapes.Count; j++)
            {
                ct.ThrowIfCancellationRequested();
                if (!bounds[i].Intersects(bounds[j])) continue;
                var lens = booleans.Intersect(shapes[i], shapes[j]);
                if (!lens.IsEmpty) lenses.Add(lens);
            }
        return EffectGeometry.Concat(lenses, FillRule.NonZero);
    }

    private static EffectOutcome Compute(IBooleanService booleans, IReadOnlyList<PathGeometry> shapes, DocObject styleSource,
        List<DocObject> items, CancellationToken ct)
    {
        var overlap = Overlaps(booleans, shapes, ct);
        if (overlap.IsEmpty) return EffectOutcome.Nothing("The shapes do not overlap, so there is nothing to common weld.");
        return EffectOutcome.Change(session =>
        {
            var result = EffectGeometry.NewResult(overlap, "Common Weld", styleSource);
            result.Style = ObjectStyle.None;   // like Weld, the result has no color
            session.History.Execute(new ReplaceObjectsCommand(session.Document, items, [result], "Common Weld"));
            session.Selection.Set(result);
        });
    }
}
