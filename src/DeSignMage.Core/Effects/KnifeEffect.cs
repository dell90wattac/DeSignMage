using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Commands;

namespace DeSignMage.Core.Effects;

/// <summary>The Knife tool's options. Lengths in points.</summary>
public sealed record KnifeSettings
{
    /// <summary>smallest blade (0.001 in is raised to 0.002 in).</summary>
    public const double MinGap = 0.002 * Units.PointsPerInch;

    /// <summary>Gap: the width of the blade, the strip that is cut away. default 0.002 in.</summary>
    public double Gap { get; init; } = MinGap;

    /// <summary>Overlap: instead of a gap the two sides overlap by this much. default 0.25 in.</summary>
    public double Overlap { get; init; } = 0.25 * Units.PointsPerInch;

    /// <summary>True = Overlap mode, false = Gap mode (pair of radio buttons).</summary>
    public bool OverlapMode { get; init; }

    /// <summary>Only the selected objects are cut (default on). With nothing selected, everything the line crosses is cut.</summary>
    public bool SelectedOnly { get; init; } = true;

    /// <summary>Simple Cut (default on): the knife cuts exactly along the dragged segment. Off: the line is extended across the whole shape. Overlap mode always extends.</summary>
    public bool SimpleCut { get; init; } = true;

    internal bool Extends => !SimpleCut || OverlapMode;

    internal SplitOptions ToSplitOptions() => new()
    {
        Gap = Math.Max(Gap, MinGap),
        Overlap = OverlapMode ? Overlap : null,
        ExtendLine = Extends,
    };
}

/// <summary>
/// Effects ▸ Knife (knife.md): a dragged line cuts the objects it crosses into pieces, each piece a new object ("Knife piece n")
/// with the source's fill and stroke, in the source's place (in the same group, if it was in one). Objects the line only notches
/// stay one piece. Live text that gets cut turns into curves. One undo step "Knife Cut".
/// </summary>
public static class KnifeEffect
{
    public static bool CanRun(EditorSession s) => s.BooleanService is not null && s.Document.ObjectCount > 0;

    /// <summary>The objects a cut at this moment may touch: the selected ones (when <paramref name="selectedOnly"/> and something is selected) or every visible, unlocked shape.</summary>
    internal static List<(DocObject Leaf, PathGeometry World)> Candidates(EditorSession s, bool selectedOnly)
    {
        var leaves = new List<DocObject>();
        if (selectedOnly && !s.Selection.IsEmpty) leaves.AddRange(EffectGeometry.Leaves(Stacking.BottomToTop(s.Document, s.Selection.Items)));
        else
            foreach (var layer in s.Document.Layers)
                if (layer.Visible && !layer.Locked)
                    foreach (var o in layer.Children)
                        if (EffectGeometry.IsEditable(o)) leaves.Add(o);
        var list = new List<(DocObject, PathGeometry)>();
        foreach (var leaf in leaves)
            if (EffectGeometry.World(leaf) is { } g && EffectGeometry.HasClosedArea(g)) list.Add((leaf, g));
        return list;
    }

    /// <summary>The line the knife actually cuts along, for the preview: the dragged segment, or when it is extended, a line far past both ends.</summary>
    public static (Vec2 From, Vec2 To) EffectiveLine(Vec2 from, Vec2 to, KnifeSettings settings, Rect2 reach)
    {
        if (!settings.Extends || (to - from).Length < 1e-9) return (from, to);
        var dir = (to - from) / (to - from).Length;
        double far = reach.IsEmpty ? 1e5 : Math.Max(reach.Union(from).Union(to).Size.Length, 1);
        return (from - dir * far, to + dir * far);
    }

    public static EffectPlan? Plan(EditorSession s, Vec2 from, Vec2 to, KnifeSettings settings, bool selectedOnly)
    {
        if (!CanRun(s) || s.BooleanService is not { } booleans || Vec2.Distance(from, to) < 1e-6) return null;
        var candidates = Candidates(s, selectedOnly);
        if (candidates.Count == 0) return null;
        var options = settings.ToSplitOptions();
        double halfBlade = settings.OverlapMode ? 0 : options.Gap / 2;
        return new EffectPlan("Knife Cut", "Cutting...", ct =>
        {
            var cuts = new List<(DocObject Leaf, IReadOnlyList<PathGeometry> Pieces)>();
            foreach (var (leaf, world) in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (!Touches(world, from, to, halfBlade, settings.Extends)) continue;
                cuts.Add((leaf, booleans.SplitByLine(world, from, to, options)));
            }
            if (cuts.Count == 0) return EffectOutcome.Nothing("The line did not cross anything to cut.");
            return EffectOutcome.Change(session => Apply(session, cuts));
        });
    }

    private static void Apply(EditorSession session, List<(DocObject Leaf, IReadOnlyList<PathGeometry> Pieces)> cuts)
    {
        var parents = cuts.Select(c => c.Leaf.Parent).OfType<GroupObject>().Distinct().ToList();
        var all = new List<DocObject>();
        int n = 0;
        foreach (var (leaf, pieces) in cuts)
        {
            var objects = new List<DocObject>();
            foreach (var piece in pieces)
                objects.Add(EffectGeometry.NewResult(EffectGeometry.IntoParentSpace(piece, leaf), $"Knife piece {++n}", leaf));
            session.History.Execute(new SwapObjectCommand(leaf, objects, "Knife Cut"));
            all.AddRange(objects);
        }
        EmptyGroups.Remove(session, parents);
        session.Selection.Set(all.Where(session.Document.Contains).Select(o => o.TopLevel));
    }

    /// <summary>Does the blade (the segment, or its infinite extension) reach the shape: a shape edge within half a blade of the line, or an end of the line inside the shape.</summary>
    internal static bool Touches(PathGeometry shape, Vec2 from, Vec2 to, double halfBlade, bool extended)
    {
        var bounds = shape.GetBounds().Inflate(halfBlade + 1e-3);
        var a = from; var b = to;
        if (extended)
        {
            var dir = (to - from) / (to - from).Length;
            double far = Math.Max(bounds.Size.Length, Vec2.Distance(from, to)) * 2 + Vec2.Distance(from, bounds.Center);
            a = from - dir * far; b = to + dir * far;
        }
        if (Rect2.FromPoints(a, b).Inflate(halfBlade).Intersects(bounds) is false) return false;
        if (shape.Contains(a, 0.1, includeOpen: false) || shape.Contains(b, 0.1, includeOpen: false)) return true;
        foreach (var poly in shape.Flatten(0.25))
        {
            var p = poly.Points;
            int count = poly.Closed ? p.Length : p.Length - 1;
            for (int i = 0; i < count; i++)
                if (SegmentDistance(a, b, p[i], p[(i + 1) % p.Length]) <= halfBlade + 1e-6) return true;
        }
        return false;
    }

    /// <summary>Shortest distance between segments a–b and c–d.</summary>
    private static double SegmentDistance(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        if (Crosses(a, b, c, d)) return 0;
        return Math.Min(Math.Min(PointToSegment(a, c, d), PointToSegment(b, c, d)), Math.Min(PointToSegment(c, a, b), PointToSegment(d, a, b)));
    }

    private static bool Crosses(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        double d1 = Vec2.Cross(b - a, c - a), d2 = Vec2.Cross(b - a, d - a);
        double d3 = Vec2.Cross(d - c, a - c), d4 = Vec2.Cross(d - c, b - c);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double PointToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-18) return Vec2.Distance(p, a);
        double t = Math.Clamp(Vec2.Dot(p - a, ab) / len2, 0, 1);
        return Vec2.Distance(p, a + ab * t);
    }
}
