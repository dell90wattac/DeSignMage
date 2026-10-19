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
    internal OutlineSnapshot(List<(DocObject Leaf, PathGeometry World)> shapes, double height, IReadOnlyList<PathObject>? editing = null)
    {
        Shapes = shapes;
        Height = height;
        Editing = editing;
    }

    internal List<(DocObject Leaf, PathGeometry World)> Shapes { get; }

    /// <summary>
    /// When the selection is live outlines or inlines: those effect objects (one per entry of <see cref="Shapes"/>, which then hold their sources). The tool
    /// then edits them instead of outlining them.
    /// </summary>
    public IReadOnlyList<PathObject>? Editing { get; }

    /// <summary>What the edited effects were made with (the first one), when <see cref="Editing"/> is set.</summary>
    public LiveOutline? EditedSpec => Editing is { Count: > 0 } e ? e[0].Live?.Spec as LiveOutline : null;

    /// <summary>Height of the selection's bounds in points: the 100 % of the "%" fields ("height of the character").</summary>
    public double Height { get; }

    public IEnumerable<PathGeometry> Geometries => Shapes.Select(s => s.World);
    public bool IsEmpty => Shapes.Count == 0;
}

/// <summary>
/// Effects ▸ Outline and Effects ▸ Inline (outline.md, inline.md; R13 items 6, 7, 9): a new STANDALONE object (not grouped with
/// the source, but live: it follows edits of the source, see <see cref="LiveEffects"/>; live text stays live text). Outline goes just below the source, Inline just above. Closed shapes get
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
        if (EditTargets(s) is { } editing)
        {
            // reopening the tool on live outlines: their sources are what is outlined, the effects are what changes
            var sources = new List<(DocObject, PathGeometry)>();
            var kept = new List<PathObject>();
            foreach (var e in editing)
                if (EffectGeometry.World(e.Live!.Source) is { } g) { sources.Add((e.Live.Source, g)); kept.Add(e); }
            if (sources.Count > 0)
            {
                var bounds = sources.Aggregate(Rect2.Empty, (r, x) => r.Union(x.Item2.GetBounds()));
                return new OutlineSnapshot(sources, bounds.Height, kept);
            }
        }
        var items = Stacking.BottomToTop(s.Document, s.Selection.Items);
        var shapes = new List<(DocObject, PathGeometry)>();
        foreach (var leaf in EffectGeometry.Leaves(items))
            if (EffectGeometry.World(leaf) is { } g) shapes.Add((leaf, g));
        return shapes.Count == 0 ? null : new OutlineSnapshot(shapes, s.Selection.GetBounds().Height);
    }

    /// <summary>The selected live outlines / inlines when the selection is only those (their sources still in the document); null otherwise.</summary>
    public static List<PathObject>? EditTargets(EditorSession s)
    {
        if (s.Selection.IsEmpty) return null;
        var list = new List<PathObject>();
        foreach (var item in s.Selection.Items)
        {
            if (item is not PathObject { Live: { Spec: LiveOutline } link } p || !s.Document.Contains(link.Source)) return null;
            list.Add(p);
        }
        return list;
    }

    /// <summary>The result outline of every shape (document coordinates). Shapes that vanish (an inline deeper than the shape) are left out.</summary>
    public static List<(DocObject Leaf, PathGeometry Result)> Compute(IOffsetService offsets, IBooleanService? booleans,
        OutlineSnapshot snapshot, OutlineKind kind, OutlineSettings settings, CancellationToken ct = default)
    {
        var results = new List<(DocObject, PathGeometry)>();
        foreach (var (leaf, world) in snapshot.Shapes)
        {
            ct.ThrowIfCancellationRequested();
            var r = ComputeShape(offsets, booleans, world, kind, settings);
            if (!r.IsEmpty) results.Add((leaf, r));
        }
        return results;
    }

    /// <summary>The exact outline of one shape (document coordinates), empty when it vanishes.</summary>
    public static PathGeometry ComputeShape(IOffsetService offsets, IBooleanService? booleans, PathGeometry world, OutlineKind kind, OutlineSettings settings)
    {
        double sign = kind == OutlineKind.Inline ? -1 : 1;
        double d1 = sign * settings.First, d2 = sign * settings.Second;
        var options = settings.ToOffsetOptions();
        if (d2 == 0) return offsets.Offset(world, d1, options);
        var rings = offsets.Offset(world, [d1, d1 + d2], options);
        bool hasOpen = EffectGeometry.CountLoops(world).Open > 0;
        // Areas: the band between the two rings. Open lines: both parallel lines.
        if (hasOpen || booleans is null) return EffectGeometry.Concat(rings, FillRule.NonZero);
        var (outer, inner) = d2 > 0 ? (rings[1], rings[0]) : (rings[0], rings[1]);   // the larger offset is the larger area
        return booleans.Difference(outer, inner);
    }

    // ---- The quick pass for live previews (R31) ----

    /// <summary>
    /// The selection's shapes prepared for the quick offset at one accuracy and one Contour setting. Made once (on the worker thread, the first time a
    /// preview is wanted) and reused for every pointer move and keystroke after it. A shape that cannot be prepared (it has open paths) is null and
    /// gets the exact offset instead.
    /// </summary>
    public sealed class QuickSources
    {
        internal QuickSources(OutlineSnapshot snapshot, double tolerance, bool contourOnly, IReadOnlyList<IOffsetPreviewSource?> sources, IReadOnlyList<PathGeometry?> openParts)
        {
            OpenParts = openParts;
            Snapshot = snapshot;
            Tolerance = tolerance;
            ContourOnly = contourOnly;
            Sources = sources;
        }

        public OutlineSnapshot Snapshot { get; }
        public double Tolerance { get; }
        public bool ContourOnly { get; }
        internal IReadOnlyList<IOffsetPreviewSource?> Sources { get; }

        /// <summary>The open subpaths of a shape that also has closed ones (they get the exact offset, they are few); null when there are none.</summary>
        internal IReadOnlyList<PathGeometry?> OpenParts { get; }

        /// <summary>True when these were made for this snapshot and a preview of at most this much error (a coarser one is not good enough, a much finer one is slower than needed).</summary>
        public bool Fits(OutlineSnapshot snapshot, double tolerance, bool contourOnly) =>
            ReferenceEquals(Snapshot, snapshot) && ContourOnly == contourOnly && Tolerance <= tolerance * 1.01 && Tolerance >= tolerance / 4;
    }

    /// <summary>How accurate (points) a preview needs to be at this zoom: 0.8 of a screen pixel, never finer than 0.02 points or coarser than 2.</summary>
    public static double PreviewTolerance(double zoom) => Math.Clamp(0.8 / Math.Max(zoom, 1e-6), 0.02, 2);

    public static QuickSources PrepareQuick(IOffsetPreviewService service, OutlineSnapshot snapshot, double tolerance, bool contourOnly)
    {
        var sources = new List<IOffsetPreviewSource?>();
        var openParts = new List<PathGeometry?>();
        foreach (var (_, world) in snapshot.Shapes)
        {
            var (closed, open) = EffectGeometry.CountLoops(world);
            if (open == 0) { sources.Add(service.Prepare(world, tolerance, contourOnly)); openParts.Add(null); }
            else if (closed == 0) { sources.Add(null); openParts.Add(null); }   // only lines: the exact offset, as a whole
            else
            {
                var loops = PathEditing.ToLoops(world);
                bool IsClosed(Loop l) => l.Closed || Vec2.Distance(l.Start, l.Segments[^1].End) < 1e-6;
                var closedPart = PathEditing.FromLoops(loops.Where(IsClosed), world.FillRule);
                var openPart = PathEditing.FromLoops(loops.Where(l => !IsClosed(l)), world.FillRule);
                sources.Add(service.Prepare(closedPart, tolerance, contourOnly));
                openParts.Add(openPart);
            }
        }
        return new(snapshot, tolerance, contourOnly, sources, openParts);
    }

    /// <summary>The preview outlines of every shape: the quick offset where a shape was prepared, the exact one for the others.</summary>
    public static List<PathGeometry> ComputeQuick(QuickSources prepared, IOffsetService offsets, IBooleanService? booleans, OutlineKind kind,
        OutlineSettings settings, CancellationToken ct = default)
    {
        double sign = kind == OutlineKind.Inline ? -1 : 1;
        var results = new List<PathGeometry>(prepared.Sources.Count);
        for (int i = 0; i < prepared.Sources.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            PathGeometry r;
            if (prepared.Sources[i] is { } quick)
            {
                r = quick.Offset(sign * settings.First, sign * settings.Second, settings.Corners, settings.Oat);
                if (prepared.OpenParts[i] is { } lines)
                    r = EffectGeometry.Concat([r, ComputeShape(offsets, booleans, lines, kind, settings)], FillRule.NonZero);
            }
            else r = ComputeShape(offsets, booleans, prepared.Snapshot.Shapes[i].World, kind, settings);
            if (!r.IsEmpty) results.Add(r);
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
        if (snapshot.Editing is { } editing)
            return new EffectPlan(name, $"{name}...", ct =>
            {
                var shapes = snapshot.Shapes.Select(x => { ct.ThrowIfCancellationRequested(); return ComputeShape(offsets, booleans, x.World, kind, settings); }).ToList();
                return EffectOutcome.Change(session => ApplyEdit(session, editing, snapshot, shapes, kind, settings));
            });
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
                result.Live = new LiveLink(leaf, new LiveOutline(kind, settings with { LeaveOriginal = true }));   // live: it follows the source (top level, document coordinates)
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

    /// <summary>Reopened on live outlines: each gets its new outline and settings in place (same object, same spot), still following its source.</summary>
    private static void ApplyEdit(EditorSession session, IReadOnlyList<PathObject> editing, OutlineSnapshot snapshot, List<PathGeometry> shapes, OutlineKind kind, OutlineSettings settings)
    {
        var spec = new LiveOutline(kind, settings with { LeaveOriginal = true });
        var changes = new List<(PathObject, PathGeometry, ObjectStyle, string?, LiveLink?)>();
        for (int i = 0; i < editing.Count && i < shapes.Count; i++)
        {
            var e = editing[i];
            var world = e.GetWorldTransform();
            var local = world.IsIdentity || !world.IsInvertible ? shapes[i] : shapes[i].Transform(world.Invert());
            changes.Add((e, local, e.Style, NameOf(kind), LiveLink.Create(snapshot.Shapes[i].Leaf, spec, world)));
        }
        session.History.Execute(new ReshapeEffectsCommand(NameOf(kind), changes));
        session.Selection.Set(editing.Cast<DocObject>().ToList());
    }
}
