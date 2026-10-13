using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Effects;

/// <summary>Drop: the shadow includes the object (a bold, merged silhouette). Relief: the object is cut out of the shadow.</summary>
public enum ShadowType : byte { Drop, Relief }

/// <summary>Flat: the shadow is moved. Extruded: a solid sweep from the object to the moved copy. Perspective: extruded, with the far end scaled by the depth percentage. Cast: a flat copy changed in height and slanted.</summary>
public enum ShadowForm : byte { Flat, Extruded, Perspective, Cast }

/// <summary>
/// Everything that decides how a shadow looks, as one value: the same record feeds the one-shot Effects ▸ Shadow commands now and could feed a
/// live (regenerated) shadow later, because <see cref="ShadowGeometry.Build"/> is a pure function of a shape and these settings.
/// Lengths in points; the shift is in document directions (x right, y DOWN); the Context Bar shows Vs the other way round (up is positive).
/// </summary>
public sealed record ShadowSettings
{
    public ShadowType Type { get; init; } = ShadowType.Drop;
    public ShadowForm Form { get; init; } = ShadowForm.Flat;

    /// <summary>Outline: how far the shadow body is grown (a negative value shrinks it, which helps against vinyl shrinking). 0 = same size.</summary>
    public double Outline { get; init; }

    /// <summary>Hs: how far the shadow moves to the right. For a Cast shadow: how far the top edge is pushed sideways.</summary>
    public double ShiftX { get; init; }

    /// <summary>The shadow's vertical shift, in document direction (positive = down). Vs in the bar is the negative of this. For a Cast shadow Vs is the change of height (-Vs here).</summary>
    public double ShiftY { get; init; }

    /// <summary>Perspective: the size of the far end as a percentage of the near end (100 = same size, 75 default).</summary>
    public double DepthPercent { get; init; } = 75;

    public CornerStyle Corners { get; init; } = CornerStyle.Auto;

    /// <summary>OAT in degrees (corners sharper than this are rounded when <see cref="Corners"/> is Auto).</summary>
    public double Oat { get; init; } = 70;

    /// <summary>The shadow's fill. Null = the object's own fill and stroke (a copy).</summary>
    public DocColor? Color { get; init; }

    public bool HasShift => ShiftX != 0 || ShiftY != 0;

    /// <summary>"Vs" as the Context Bar shows it: positive = up.</summary>
    public double Vs => -ShiftY;

    internal OffsetOptions ToOffsetOptions() => new() { Corners = Corners, OutsideAngleThreshold = Oat };
}

/// <summary>
/// The one geometry function behind every shadow (notes/effects/shadow.md section 4): a closed shape and the settings in, the shadow's
/// outline (clean loops, holes kept) out. World coordinates in, world coordinates out. Safe on any thread.
/// </summary>
public static class ShadowGeometry
{
    /// <summary>How finely curves are flattened to build the extrusion planes (points).</summary>
    private const double SweepTolerance = 0.05;

    public static PathGeometry Build(IBooleanService booleans, IOffsetService offsets, PathGeometry source, ShadowSettings s, CancellationToken ct = default)
    {
        if (source.IsEmpty) return PathGeometry.EmptyPath;
        var options = s.ToOffsetOptions();
        var bounds = source.GetBounds();
        PathGeometry Grow(double amount) => amount == 0 ? source : offsets.Offset(source, amount, options);

        if (s.Form is ShadowForm.Flat or ShadowForm.Cast)
        {
            var body = Grow(s.Outline);
            ct.ThrowIfCancellationRequested();
            body = s.Form == ShadowForm.Cast ? Cast(body, bounds, s) : body.Transform(Affine2.Translation(s.ShiftX, s.ShiftY));
            if (body.IsEmpty) return PathGeometry.EmptyPath;
            return s.Type == ShadowType.Drop ? booleans.Union([body, source]) : booleans.Difference(body, source);
        }

        // Extruded and perspective: A (the object grown by the outline, half of it for a relief), B (A moved, and for perspective first scaled
        // about the center), and a plane between every edge of A and its partner on B; the union of all of it is the sweep.
        var a = Grow(s.Type == ShadowType.Relief ? s.Outline / 2 : s.Outline);
        var m = s.Form == ShadowForm.Perspective
            ? Affine2.Scale(s.DepthPercent / 100, s.DepthPercent / 100, bounds.Center) * Affine2.Translation(s.ShiftX, s.ShiftY)
            : Affine2.Translation(s.ShiftX, s.ShiftY);
        var b = a.Transform(m);
        ct.ThrowIfCancellationRequested();
        var planes = Planes(a, m, ct);
        var sweep = booleans.Union([a, b, planes]);
        if (s.Type == ShadowType.Drop || sweep.IsEmpty) return sweep;
        return booleans.Difference(sweep, Grow(s.Outline));
    }

    /// <summary>
    /// The Cast shadow's shape: the body is squashed or stretched about the bottom edge to the height H + Vs (a height that goes through zero
    /// into the negative flips it under the baseline, which makes a floor shadow), and slanted so the top edge is pushed Hs sideways.
    /// </summary>
    internal static PathGeometry Cast(PathGeometry body, Rect2 bounds, ShadowSettings s)
    {
        double h = bounds.Height;
        if (h < 1e-9) return PathGeometry.EmptyPath;
        double newHeight = h + s.Vs;
        if (Math.Abs(newHeight) < 1e-6) return PathGeometry.EmptyPath;
        double sy = newHeight / h, bottom = bounds.Bottom, slant = s.ShiftX / h;
        // y' = bottom - (bottom - y) * sy ; x' = x + slant * (bottom - y)
        return body.Transform(new Affine2(1, 0, -slant, sy, slant * bottom, bottom * (1 - sy)));
    }

    /// <summary>The planes of a sweep as one path: for every edge of <paramref name="a"/>, the four-sided piece between it and the same edge moved by <paramref name="m"/>.</summary>
    internal static PathGeometry Planes(PathGeometry a, Affine2 m, CancellationToken ct)
    {
        var path = new PathBuilder();
        foreach (var poly in a.Flatten(SweepTolerance))
        {
            ct.ThrowIfCancellationRequested();
            var p = poly.Points;
            if (p.Length < 2) continue;
            int edges = poly.Closed || Vec2.Distance(p[0], p[^1]) < 1e-9 ? p.Length : p.Length - 1;
            for (int i = 0; i < edges; i++)
            {
                var p0 = p[i];
                var p1 = p[(i + 1) % p.Length];
                var q0 = m.Transform(p0);
                var q1 = m.Transform(p1);
                double area = Vec2.Cross(p1 - p0, q1 - p0) + Vec2.Cross(q1 - p0, q0 - p0);   // twice the signed area of p0 p1 q1 q0
                if (Math.Abs(area) < 1e-9) continue;
                // every plane the same way round, so overlapping planes add up instead of cancelling under the non-zero rule
                if (area > 0) path.MoveTo(p0).LineTo(p1).LineTo(q1).LineTo(q0).Close();
                else path.MoveTo(q0).LineTo(q1).LineTo(p1).LineTo(p0).Close();
            }
        }
        return path.Build();
    }
}

/// <summary>What a shadow works on: for each selected top-level object, the outlines of the shapes in it, taken when the tool started.</summary>
public sealed class ShadowSnapshot
{
    internal sealed record Item(DocObject Source, IReadOnlyList<DocObject> Shapes, IReadOnlyList<PathGeometry> Outlines);

    internal ShadowSnapshot(List<Item> items, double height)
    {
        Items = items;
        Height = height;
    }

    internal List<Item> Items { get; }

    /// <summary>Height of the whole selection in points: the 100 % of the "%" field.</summary>
    public double Height { get; }

    public bool IsEmpty => Items.Count == 0;
    public IEnumerable<PathGeometry> Geometries => Items.SelectMany(i => i.Outlines);
}

/// <summary>
/// Effects ▸ Shadow, Extruded Shadow, Perspective Shadow and Cast Shadow (notes/effects/shadow.md; user decisions R7, R13, R22): a one-shot
/// shadow object per selected object, <b>grouped with its source</b> (not linked: editing the source later leaves it as it is) and placed just
/// below the source in the drawing order. A group source gets the shadow as one more member; any other object and the shadow become a new
/// group. Live text stays live text; the shadow is a plain path. One undo step.
/// </summary>
public static class ShadowEffect
{
    public static string NameOf(ShadowForm form) => form switch
    {
        ShadowForm.Extruded => "Extruded Shadow",
        ShadowForm.Perspective => "Perspective Shadow",
        ShadowForm.Cast => "Cast Shadow",
        _ => "Shadow",
    };

    /// <summary>True for the objects a Shadow command made (other effects leave them alone).</summary>
    public static bool IsShadow(DocObject o) =>
        o is PathObject { Name: "Shadow" or "Extruded Shadow" or "Perspective Shadow" or "Cast Shadow" };

    /// <summary>Something with an area is selected and no picture is (bitmaps have no outline to shadow).</summary>
    public static bool CanRun(EditorSession s) =>
        s.BooleanService is not null && s.OffsetService is not null && !s.Selection.IsEmpty && !EffectGeometry.ContainsImage(s.Selection.Items) &&
        EffectGeometry.Operands(s.Selection.Items).Any(x => EffectGeometry.HasClosedArea(x.World));

    /// <summary>Takes the outlines of the selection, one entry per selected object (a combined object contributes its parts, so its overlaps are not holes).</summary>
    public static ShadowSnapshot? Snapshot(EditorSession s)
    {
        var items = new List<ShadowSnapshot.Item>();
        foreach (var source in Stacking.BottomToTop(s.Document, s.Selection.Items))
        {
            var operands = EffectGeometry.Operands([source]).Where(x => EffectGeometry.HasClosedArea(x.World)).ToList();
            if (operands.Count == 0) continue;
            items.Add(new ShadowSnapshot.Item(source, operands.Select(x => x.Source).ToList(), operands.Select(x => x.World).ToList()));
        }
        return items.Count == 0 ? null : new ShadowSnapshot(items, s.Selection.GetBounds().Height);
    }

    /// <summary>The shadow outline of every selected object, in document coordinates.</summary>
    public static List<(DocObject Source, PathGeometry Shadow)> Compute(IBooleanService booleans, IOffsetService offsets, ShadowSnapshot snapshot,
        ShadowSettings settings, CancellationToken ct = default)
    {
        var results = new List<(DocObject, PathGeometry)>();
        foreach (var item in snapshot.Items)
        {
            ct.ThrowIfCancellationRequested();
            var silhouette = item.Outlines.Count == 1 ? item.Outlines[0] : booleans.Union(item.Outlines);
            var shadow = ShadowGeometry.Build(booleans, offsets, silhouette, settings, ct);
            if (!shadow.IsEmpty) results.Add((item.Source, shadow));
        }
        return results;
    }

    public static EffectPlan? Plan(EditorSession s, ShadowSettings settings, ShadowSnapshot? snapshot = null)
    {
        if (s.BooleanService is not { } booleans || s.OffsetService is not { } offsets || !settings.HasShift) return null;
        snapshot ??= Snapshot(s);
        if (snapshot is null) return null;
        string name = NameOf(settings.Form);
        var shapes = snapshot.Items.ToDictionary(i => i.Source, i => i.Shapes[0]);
        return new EffectPlan(name, $"{name}...", ct =>
        {
            var results = Compute(booleans, offsets, snapshot, settings, ct);
            if (results.Count == 0) return EffectOutcome.Nothing("That shadow has no shape (the height is zero).");
            return EffectOutcome.Change(session => Apply(session, results, shapes, settings));
        });
    }

    private static void Apply(EditorSession session, List<(DocObject Source, PathGeometry Shadow)> results, Dictionary<DocObject, DocObject> styleSource, ShadowSettings settings)
    {
        var doc = session.Document;
        string name = NameOf(settings.Form);
        var selected = new List<DocObject>();
        foreach (var (source, geometry) in results)
        {
            // Just below the lowest drawn object of the source (R22: a group has no place of its own).
            var order = new Stacking.Order(doc);
            var lowest = (source is GroupObject g ? g.Leaves() : new[] { source }).Where(l => l.Layer is not null).MinBy(order.KeyOf)!;
            var layer = lowest.Layer!;
            int index = layer.IndexOf(lowest);

            var style = settings.Color is { } c ? ObjectStyle.FillOnly(c) : styleSource[source].Style;
            var group = source as GroupObject;
            var local = group is null || group.GetWorldTransform() is not { IsInvertible: true } w ? geometry : geometry.Transform(w.Invert());
            var shadow = new PathObject(local) { Name = name, Style = style, Visible = source.Visible, Locked = source.Locked };

            if (group is not null)
            {
                session.History.Execute(new InsertAtSlotCommand(new Slot(layer, index, group, group.Children.Count), shadow, name));   // one more member of the group
                selected.Add(group);
            }
            else
            {
                session.History.Execute(new InsertAtSlotCommand(new Slot(layer, index), shadow, name));
                var pair = new GroupObjectsCommand(doc, [source, shadow]);
                session.History.Execute(pair);
                selected.Add(pair.Group);
            }
        }
        session.Selection.Set(selected);
    }
}
