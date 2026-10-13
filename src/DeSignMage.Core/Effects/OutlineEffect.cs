using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Effects;

public enum OutlineKind : byte { Outline, Inline }

/// <summary>
/// The Outline / Inline tool's fields. Amounts are distances in points from the original edge
/// in the direction of the command: for Outline a positive value grows the shape, for Inline a positive value shrinks it
/// (both commands take a plain distance; a negative one goes the other way).
/// </summary>
public sealed record OutlineSettings
{
    /// <summary>"1st": distance of the first ring from the original, points.</summary>
    public double First { get; init; }

    /// <summary>"2nd": distance of a second ring from the FIRST one (may be negative). When not 0 the result is the band between the two rings.</summary>
    public double Second { get; init; }

    /// <summary>Corner type: Auto = rule with <see cref="Oat"/>, Miter = sharp, Round = round (Miter / Round buttons).</summary>
    public CornerStyle Corners { get; init; } = CornerStyle.Auto;

    /// <summary>Outside angle threshold in degrees (default 70): corners sharper than this get a round arc.</summary>
    public double Oat { get; init; } = 70;

    /// <summary>"Contour": only the outer loops, no holes.</summary>
    public bool ContourOnly { get; init; }

    /// <summary>"Leave Original" (default on): the source stays. Off, the result takes the source's place.</summary>
    public bool LeaveOriginal { get; init; } = true;

    public bool IsZero => First == 0 && Second == 0;

    internal OffsetOptions ToOffsetOptions() => new()
    {
        Corners = Corners,
        OutsideAngleThreshold = Oat,
        ContourOnly = ContourOnly,
        OpenPaths = OpenPathMode.OneSided,   // R13: an open path gets an open, one-sided parallel copy that follows it
    };
}

/// <summary>What an Outline/Inline works on: the shape-bearing objects of the selection with their outlines, taken when the tool started.</summary>
public sealed class OutlineSnapshot
{
    internal OutlineSnapshot(List<(DocObject Leaf, PathGeometry World)> shapes, double height)
    {
        Shapes = shapes;
        Height = height;
    }

    internal List<(DocObject Leaf, PathGeometry World)> Shapes { get; }

    /// <summary>Height of the selection's bounds in points: the 100 % of the "%" fields ("height of the character").</summary>
    public double Height { get; }

    public IEnumerable<PathGeometry> Geometries => Shapes.Select(s => s.World);
    public bool IsEmpty => Shapes.Count == 0;
}

/// <summary>
/// Effects ▸ Outline and Effects ▸ Inline (outline.md, inline.md; R13 items 6, 7, 9): a new STANDALONE object (not grouped with
/// the source, not linked; live text stays live text). Outline goes just below the source, Inline just above. Closed shapes get
/// an offset area; an open path gets an open parallel line that follows it. One undo step.
/// </summary>
public static class OutlineEffect
{
    public static string NameOf(OutlineKind kind) => kind == OutlineKind.Inline ? "Inline" : "Outline";

    public static bool CanRun(EditorSession s) =>
        s.OffsetService is not null && !s.Selection.IsEmpty && EffectGeometry.Leaves(s.Selection.Items).Any(l => EffectGeometry.World(l) is not null);

    /// <summary>Takes the selection's shapes. Null when there is nothing the offset engine can work on.</summary>
    public static OutlineSnapshot? Snapshot(EditorSession s)
    {
        var items = Stacking.BottomToTop(s.Document, s.Selection.Items);
        var shapes = new List<(DocObject, PathGeometry)>();
        foreach (var leaf in EffectGeometry.Leaves(items))
            if (EffectGeometry.World(leaf) is { } g) shapes.Add((leaf, g));
        return shapes.Count == 0 ? null : new OutlineSnapshot(shapes, s.Selection.GetBounds().Height);
    }

    /// <summary>The result outline of every shape (document coordinates). Shapes that vanish (an inline deeper than the shape) are left out.</summary>
    public static List<(DocObject Leaf, PathGeometry Result)> Compute(IOffsetService offsets, IBooleanService? booleans,
        OutlineSnapshot snapshot, OutlineKind kind, OutlineSettings settings, CancellationToken ct = default)
    {
        double sign = kind == OutlineKind.Inline ? -1 : 1;
        double d1 = sign * settings.First, d2 = sign * settings.Second;
        var options = settings.ToOffsetOptions();
        var results = new List<(DocObject, PathGeometry)>();
        foreach (var (leaf, world) in snapshot.Shapes)
        {
            ct.ThrowIfCancellationRequested();
            PathGeometry r;
            if (d2 == 0)
            {
                r = offsets.Offset(world, d1, options);
            }
            else
            {
                var rings = offsets.Offset(world, [d1, d1 + d2], options);
                bool hasOpen = EffectGeometry.CountLoops(world).Open > 0;
                // Areas: the band between the two rings. Open lines: both parallel lines.
                if (hasOpen || booleans is null) r = EffectGeometry.Concat(rings, FillRule.NonZero);
                else
                {
                    var (outer, inner) = d2 > 0 ? (rings[1], rings[0]) : (rings[0], rings[1]);   // the larger offset is the larger area
                    r = booleans.Difference(outer, inner);
                }
            }
            if (!r.IsEmpty) results.Add((leaf, r));
        }
        return results;
    }

    public static EffectPlan? Plan(EditorSession s, OutlineKind kind, OutlineSettings settings, OutlineSnapshot? snapshot = null)
    {
        if (s.OffsetService is not { } offsets || settings.IsZero) return null;
        snapshot ??= Snapshot(s);
        if (snapshot is null) return null;
        var booleans = s.BooleanService;
        string name = NameOf(kind);
        return new EffectPlan(name, $"{name}...", ct =>
        {
            var results = Compute(offsets, booleans, snapshot, kind, settings, ct);
            if (results.Count == 0) return EffectOutcome.Nothing(kind == OutlineKind.Inline ? "The shape is too small for that inline." : "Nothing to outline.");
            return EffectOutcome.Change(session => Apply(session, results, kind, settings));
        });
    }

    private static void Apply(EditorSession session, List<(DocObject Leaf, PathGeometry Result)> results, OutlineKind kind, OutlineSettings settings)
    {
        string name = NameOf(kind);
        var created = new List<DocObject>();
        foreach (var (leaf, geometry) in results)
        {
            if (settings.LeaveOriginal)
            {
                // Standalone next to the source's top-level object: the outline just below it, the inline just above (R13 item 9).
                // A group has no place of its own: its lowest / topmost drawn object is where the outline goes below / the inline above.
                var top = leaf.TopLevel;
                var layer = (top is GroupObject ? leaf.Layer : top.Layer) ?? session.Document.ActiveLayer;
                var reach = top is GroupObject g ? g.Leaves().Where(l => ReferenceEquals(l.Layer, layer)).Select(layer.IndexOf).ToList() : [layer.IndexOf(top)];
                int index = kind == OutlineKind.Outline ? reach.Min() : reach.Max() + 1;
                var result = EffectGeometry.NewResult(geometry, name, leaf);
                session.History.Execute(new AddObjectCommand(layer, result, index));
                created.Add(result);
            }
            else
            {
                var result = EffectGeometry.NewResult(EffectGeometry.IntoParentSpace(geometry, leaf), name, leaf);
                session.History.Execute(new SwapObjectCommand(leaf, [result], name));
                created.Add(result.TopLevel);
            }
        }
        session.Selection.Set(created);
    }
}
