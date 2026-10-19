using System.Runtime.CompilerServices;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Effects;

/// <summary>
/// Live effects (user decision 2026-10-18: "when you edit a shape or text that has a shadow, outline, bevel or soft shadow, the effect follows the edit").
/// An effect object with a <see cref="LiveLink"/> is rebuilt from its source whenever the source changes shape or moves away from it; the rebuild is part of
/// the undo step of the edit (<see cref="LiveEffectUpdater"/>). This class holds what the rest of the program asks about live effects: which objects are
/// live, which effects an object has, how to copy them, and how one effect is computed from its source.
/// </summary>
public static class LiveEffects
{
    /// <summary>The most rounds of rebuilding in one step (an effect of an effect is rebuilt in the round after its source).</summary>
    internal const int MaxRounds = 8;

    // ---- Questions ----

    /// <summary>Every live effect object in the document, in drawing order.</summary>
    public static IEnumerable<PathObject> All(Document doc)
    {
        foreach (var layer in doc.Layers)
            foreach (var o in layer.Children)
                if (o is PathObject { Live: not null } p) yield return p;
    }

    /// <summary>The live effect objects that follow <paramref name="source"/>, bottom to top.</summary>
    public static List<PathObject> EffectsOf(Document doc, DocObject source) =>
        All(doc).Where(e => ReferenceEquals(e.Live!.Source, source)).ToList();

    /// <summary>The live effects of every object, keyed by source (for code that looks up many).</summary>
    public static Dictionary<DocObject, List<PathObject>> BySource(Document doc)
    {
        var map = new Dictionary<DocObject, List<PathObject>>(ReferenceEqualityComparer.Instance);
        foreach (var e in All(doc))
        {
            if (!map.TryGetValue(e.Live!.Source, out var list)) map[e.Live.Source] = list = [];
            list.Add(e);
        }
        return map;
    }

    /// <summary>The live effect of <paramref name="source"/> with a spec of type <typeparamref name="T"/> (the first one), or null.</summary>
    public static PathObject? Find<T>(Document doc, DocObject source, Func<T, bool>? match = null) where T : LiveEffectSpec =>
        All(doc).FirstOrDefault(e => ReferenceEquals(e.Live!.Source, source) && e.Live.Spec is T t && (match is null || match(t)));

    /// <summary>A short description of a live effect for the Info Line and the Layers Panel: "Live Shadow of "Sale"". Null for a plain object.</summary>
    public static string? Describe(DocObject o)
    {
        if (o.Live is not { } link) return null;
        return $"Live {link.Spec.DisplayName} of {NameOf(link.Source)}";
    }

    private static string NameOf(DocObject source)
    {
        string? n = source is TextObject t ? t.Text : source.Name;
        if (string.IsNullOrWhiteSpace(n)) return source.KindName;
        n = n.ReplaceLineEndings(" ");
        return "“" + (n.Length > 24 ? n[..23] + "…" : n) + "”";
    }

    /// <summary>
    /// What Effects ▸ Make Plain changes for this selection: the live effect objects in it (inside groups too), and the live effects of the objects in it.
    /// </summary>
    public static List<DocObject> PlainTargets(Document doc, IEnumerable<DocObject> selection)
    {
        var picked = new HashSet<DocObject>(ReferenceEqualityComparer.Instance);
        foreach (var root in selection)
        {
            picked.Add(root);
            if (root is GroupObject g) foreach (var d in Document.Walk(g)) picked.Add(d);
        }
        var result = new List<DocObject>();
        foreach (var e in All(doc))
            if (picked.Contains(e) || picked.Contains(e.Live!.Source)) result.Add(e);
        return result;
    }

    /// <summary>
    /// <paramref name="items"/> plus the live effects outside them whose source is in them (at any depth): what a drag of the items should show moving, since
    /// those effects follow on release.
    /// </summary>
    public static DocObject[] WithFollowers(Document doc, IReadOnlyList<DocObject> items)
    {
        var inside = new HashSet<DocObject>(ReferenceEqualityComparer.Instance);
        foreach (var root in items)
        {
            inside.Add(root);
            if (root is GroupObject g) foreach (var d in Document.Walk(g)) inside.Add(d);
        }
        var result = items.ToList();
        foreach (var e in All(doc))
            if (!inside.Contains(e) && inside.Contains(e.Live!.Source) && !inside.Contains(e.TopLevel)) result.Add(e);
        return [.. result];
    }

    // ---- Copies ----

    /// <summary>
    /// Copies of <paramref name="roots"/> (a copy-drag, the clipboard, a paste) in which the live effects follow the copied sources. An effect copied without
    /// its source becomes plain: a copy put somewhere else must not follow the original.
    /// </summary>
    public static List<DocObject> CloneSet(IReadOnlyList<DocObject> roots)
    {
        var copies = roots.Select(r => r.Clone()).ToList();
        RemapClones(roots, copies);
        return copies;
    }

    /// <summary>Points the links inside <paramref name="copies"/> (made by <see cref="DocObject.Clone"/> of <paramref name="originals"/>, same order) at the copies.</summary>
    public static void RemapClones(IReadOnlyList<DocObject> originals, IReadOnlyList<DocObject> copies)
    {
        var map = new Dictionary<DocObject, DocObject>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < originals.Count && i < copies.Count; i++) Pair(originals[i], copies[i], map);
        foreach (var copy in map.Values)
            if (copy.Live is { } link)
                copy.Live = map.TryGetValue(link.Source, out var source) ? link with { Source = source } : null;
    }

    private static void Pair(DocObject original, DocObject copy, Dictionary<DocObject, DocObject> map)
    {
        map[original] = copy;
        if (original is GroupObject og && copy is GroupObject cg)
            for (int i = 0; i < og.Children.Count && i < cg.Children.Count; i++) Pair(og.Children[i], cg.Children[i], map);
        else if (original is CombinedObject oc && copy is CombinedObject cc)
            for (int i = 0; i < oc.Parts.Count && i < cc.Parts.Count; i++) Pair(oc.Parts[i], cc.Parts[i], map);
    }

    // ---- What a source looks like to its effects ----

    /// <summary>A number that changes whenever the outline of <paramref name="source"/> changes (not its colors or placement).</summary>
    internal static long SourceKey(DocObject source)
    {
        var h = new HashCode();
        Add(ref h, source);
        return h.ToHashCode() ^ ((long)source.ShapeVersion << 32);

        static void Add(ref HashCode h, DocObject o)
        {
            h.Add(RuntimeHelpers.GetHashCode(o));
            h.Add(o.ShapeVersion);
            switch (o)
            {
                case TextObject t: h.Add(t.DependencyStamp); break;
                case CombinedObject c:
                    foreach (var p in c.Parts) { Add(ref h, p); h.Add(p.Transform); }
                    break;
                case GroupObject g:
                    foreach (var m in g.Children)
                    {
                        if (m.Live is not null) continue;   // effects inside a group source are not part of its shape
                        Add(ref h, m);
                        h.Add(m.Transform);
                    }
                    break;
            }
        }
    }

    /// <summary>True when moving the source by a plain shift moves the effect by the same shift (so a move needs no rebuild).</summary>
    internal static bool FollowsShifts(LiveEffectSpec spec) =>
        spec is not LiveShadow { Settings: { Form: ShadowForm.Perspective, Pivot: not null } };   // a fixed vanishing point does not move with the object

    // ---- Computing one effect ----

    /// <summary>What a rebuild needs, taken on the UI thread: the source's outline(s) in the space the effect is computed in.</summary>
    internal sealed record Job(PathObject Effect, LiveLink Link, DocObject Source, Affine2 WorldToBuild, Affine2 SourceToBuild, IReadOnlyList<PathGeometry> Shapes,
        SoftSettings? Soft, double TextHeight);

    /// <summary>Takes what <paramref name="effect"/>'s rebuild needs from the document (UI thread). Null when the source has nothing to build from.</summary>
    internal static Job? Prepare(PathObject effect, LiveLink link)
    {
        var source = link.Source;
        var effectWorld = effect.GetWorldTransform();
        if (!effectWorld.IsInvertible) return null;
        var worldToBuild = effectWorld.Invert() * link.SourceFrameMatrix;   // document -> effect space -> build space
        var sourceToBuild = source.GetWorldTransform() * worldToBuild;

        List<PathGeometry> shapes;
        SoftSettings? soft = null;
        switch (link.Spec)
        {
            case LiveOutline:
                shapes = ShapeLeaves(source).Select(EffectGeometry.World).OfType<PathGeometry>().Select(g => g.Transform(worldToBuild)).ToList();
                break;
            case LiveSoft:
                // not united here (no engine on the UI thread): the pieces as one non-zero path cover the same area, which is all a blur needs
                shapes = SoftEffect.Silhouette(null, source) is { } sil ? [sil.Transform(worldToBuild)] : [];
                soft = effect.Style.Blur is null ? null : SoftEffect.Read(effect);
                if (soft is null) return null;
                break;
            default:   // shadow, bevel: the closed outlines, a combined object by its parts
                shapes = EffectGeometry.Operands(ShapeLeaves(source)).Where(x => EffectGeometry.HasClosedArea(x.World)).Select(x => x.World.Transform(worldToBuild)).ToList();
                break;
        }
        if (shapes.Count == 0) return null;
        double textHeight = source is TextObject t ? t.FontSize * 0.72 * Math.Sqrt(Math.Abs(sourceToBuild.Determinant)) : 0;
        return new Job(effect, link, source, worldToBuild, sourceToBuild, shapes, soft, textHeight);
    }

    /// <summary>The drawn objects an effect of <paramref name="source"/> is made from (a group source, which a conversion can leave, by its plain members).</summary>
    private static IEnumerable<DocObject> ShapeLeaves(DocObject source) =>
        source is GroupObject g ? g.Leaves().Where(l => l.Live is null && l.Style.Blur is null) : [source];

    /// <summary>The effect object's new outline (its own coordinates) and style. Safe on any thread. Null when nothing can be made (left as it is).</summary>
    internal static (PathGeometry Geometry, ObjectStyle Style)? Build(Job job, IBooleanService? booleans, IOffsetService? offsets, Dictionary<(DocObject, BevelSettings), BevelPieces>? bevelCache = null)
    {
        var toEffect = job.Link.FrameMatrix;
        var effect = job.Effect;
        PathGeometry? result;
        var style = effect.Style;
        switch (job.Link.Spec)
        {
            case LiveOutline o:
                if (offsets is null) return null;
                var parts = job.Shapes.Select(s => OutlineEffect.ComputeShape(offsets, booleans, s, o.Kind, o.Settings)).Where(r => !r.IsEmpty).ToList();
                result = parts.Count == 1 ? parts[0] : EffectGeometry.Concat(parts, FillRule.NonZero);
                break;

            case LiveShadow sh:
                if (booleans is null || offsets is null) return null;
                var silhouette = job.Shapes.Count == 1 ? job.Shapes[0] : booleans.Union(job.Shapes);
                result = ShadowGeometry.Build(booleans, offsets, silhouette, sh.Settings);
                break;

            case LiveSoft:
                var soft = job.Soft!;
                var sil = job.Shapes[0];
                result = SoftEffect.Shape(offsets, sil, soft);
                if (style.Blur is { } blur)
                {
                    double reference = sil.GetBounds().Height * Math.Sqrt(Math.Abs(toEffect.Determinant));
                    if (Math.Abs(reference - blur.Reference) > 1e-9) style = style with { Blur = blur with { Reference = reference } };
                }
                break;

            case LiveBevel bv:
                if (booleans is null || offsets is null) return null;
                BevelPieces? pieces = null;
                var key = (job.Source, bv.Settings);
                if (bevelCache is not null) lock (bevelCache) bevelCache.TryGetValue(key, out pieces);
                if (pieces is null)
                {
                    var outline = job.Shapes.Count == 1 ? job.Shapes[0] : booleans.Union(job.Shapes);
                    var m = job.SourceToBuild;
                    double h = job.Source is TextObject ? job.TextHeight : outline.GetBounds().Height;
                    pieces = BevelEffect.Compute(booleans, offsets, new BevelSource(job.Source, outline, h, Math.Atan2(m.M12, m.M11)), bv.Settings);
                    if (bevelCache is not null) lock (bevelCache) bevelCache[key] = pieces;
                }
                result = (bv.Part == BevelPart.Highlight ? pieces.Highlight : pieces.Shadow) ?? PathGeometry.EmptyPath;
                break;

            default:
                return null;
        }
        result ??= PathGeometry.EmptyPath;
        if (!toEffect.IsIdentity && !result.IsEmpty) result = result.Transform(toEffect);
        return (result, style);
    }

    /// <summary>The effect computed afresh from its source as it is now (tests and the tools use it; the updater batches the same work).</summary>
    public static PathGeometry? Recompute(PathObject effect, IBooleanService? booleans, IOffsetService? offsets) =>
        effect.Live is { } link && Prepare(effect, link) is { } job ? Build(job, booleans, offsets)?.Geometry : null;
}
