using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Effects;

/// <summary>
/// Effects ▸ Cookie Cutter: the selected object is the CUTTER. Every shape below it (lower in
/// the stacking order) that it overlaps loses the area under the cutter, grown by the Outline amount first when that is not 0
/// (negative shrinks it). The cutter stays where it is. A shape the cutter covers completely disappears; a shape in a group is
/// cut in place, inside the group. Live text that gets cut turns into curves. One undo step "Cookie Cut"; each cut shape keeps
/// its own name (user, 2026-10-12), fill and stroke.
/// </summary>
public static class CookieCutEffect
{
    public static string ResultName => "Cookie Cut";

    /// <summary>Something with an area selected, and both engines available.</summary>
    public static bool CanRun(EditorSession s) =>
        s.BooleanService is not null && s.OffsetService is not null && !s.Selection.IsEmpty &&
        EffectGeometry.Leaves(s.Selection.Items).Any(l => EffectGeometry.World(l) is { } g && EffectGeometry.HasClosedArea(g));

    private static List<PathGeometry> CutterGeometries(EditorSession s, Affine2 move)
    {
        var list = new List<PathGeometry>();
        foreach (var leaf in EffectGeometry.Leaves(s.Selection.Items))
            if (EffectGeometry.World(leaf) is { } g && EffectGeometry.HasClosedArea(g)) list.Add(g.Transform(move));
        return list;
    }

    /// <summary>The cutter outline for the preview: the selection's shapes united and grown by <paramref name="outline"/> points, moved by <paramref name="moveBy"/>.</summary>
    public static PathGeometry CutterShape(IBooleanService booleans, IOffsetService offsets, IReadOnlyList<PathGeometry> cutter, double outline,
        CancellationToken ct = default)
    {
        var union = booleans.Union(cutter);
        ct.ThrowIfCancellationRequested();
        return outline == 0 ? union : offsets.Offset(union, outline);
    }

    /// <summary>The shapes under the cutter, the position of the topmost drawn object of the cutter being the cut-off: every visible, unlocked drawn
    /// object (with an outline) that is not part of the selection and is drawn below it.</summary>
    internal static List<(DocObject Leaf, PathGeometry World)> Dough(EditorSession s)
    {
        var doc = s.Document;
        var cutters = Stacking.BottomToTop(doc, s.Selection.Items);
        long limit = new Stacking.Order(doc).TopKey(cutters[^1]);
        var dough = new List<(DocObject, PathGeometry)>();
        for (int li = 0; li < doc.Layers.Count; li++)
        {
            var layer = doc.Layers[li];
            if (!layer.Visible || layer.Locked) continue;
            for (int i = 0; i < layer.Children.Count; i++)
            {
                if ((((long)li << 32) | (uint)i) >= limit) break;   // layers and positions only go up: nothing further is below the cutter
                var leaf = layer.Children[i];
                if (!EffectGeometry.IsEditable(leaf) || s.Selection.Contains(leaf.TopLevel)) continue;
                if (EffectGeometry.World(leaf) is { } g && EffectGeometry.HasClosedArea(g)) dough.Add((leaf, g));
            }
        }
        return dough;
    }

    /// <param name="outline">The Outline field, points (0 = none, negative = smaller).</param>
    /// <param name="moveBy">Stamp mode: the cutter is moved here first, as part of the same undo step.</param>
    public static EffectPlan? Plan(EditorSession s, double outline, Vec2 moveBy = default)
    {
        if (!CanRun(s) || s.BooleanService is not { } booleans || s.OffsetService is not { } offsets) return null;
        var move = Affine2.Translation(moveBy);
        var cutter = CutterGeometries(s, move);
        var dough = Dough(s);
        var cutterItems = s.Selection.Items.ToList();
        return new EffectPlan(ResultName, "Cutting...", ct =>
        {
            var shape = CutterShape(booleans, offsets, cutter, outline, ct);
            if (shape.IsEmpty) return EffectOutcome.Nothing("The cutter has no area.");
            var bounds = shape.GetBounds();
            var cuts = new List<(DocObject Leaf, PathGeometry Remainder)>();
            foreach (var (leaf, world) in dough)
            {
                ct.ThrowIfCancellationRequested();
                if (!world.GetBounds().Intersects(bounds)) continue;
                if (booleans.Intersect(world, shape).IsEmpty) continue;
                cuts.Add((leaf, booleans.Difference(world, shape)));
            }
            if (cuts.Count == 0 && moveBy == default) return EffectOutcome.Nothing("The cutter is not over anything beneath it.");
            return EffectOutcome.Change(session => Apply(session, cutterItems, move, cuts));
        });
    }

    private static void Apply(EditorSession session, List<DocObject> cutterItems, Affine2 move, List<(DocObject Leaf, PathGeometry Remainder)> cuts)
    {
        if (!move.IsIdentity) session.History.Execute(TransformObjectsCommand.ApplyWorld("Move", cutterItems, move));
        var parents = cuts.Select(c => c.Leaf.Parent).OfType<GroupObject>().Distinct().ToList();
        foreach (var (leaf, Remainder) in cuts)
        {
            var replacements = Remainder.IsEmpty
                ? []
                : new List<DocObject> { EffectGeometry.NewResult(EffectGeometry.IntoParentSpace(Remainder, leaf), leaf.Name ?? ResultName, leaf) };
            session.History.Execute(new SwapObjectCommand(leaf, replacements, ResultName));
        }
        EmptyGroups.Remove(session, parents);
    }
}

/// <summary>Removes groups that lost their last member to an effect (an empty group is invisible clutter).</summary>
internal static class EmptyGroups
{
    public static void Remove(EditorSession session, IEnumerable<GroupObject> candidates)
    {
        foreach (var start in candidates)
        {
            GroupObject? g = start;
            while (g is not null && g.Children.Count == 0 && session.Document.Contains(g))
            {
                var parent = g.Parent as GroupObject;
                session.History.Execute(new DeleteObjectsCommand([g]));
                g = parent;
            }
        }
    }
}

