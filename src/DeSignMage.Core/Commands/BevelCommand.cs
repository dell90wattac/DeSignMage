using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>
/// Makes (or replaces) the Easy Bevel crescents of some objects as one undo step. A crescent goes just above its object in the drawing order and joins the
/// object's group; an object that is not in a group becomes a group with its crescents (R13). Crescents that already sit above an object are taken out first
/// (and put back by undo), so applying a bevel twice does not pile them up. <see cref="Retarget"/> changes what an already executed command makes, so editing a
/// field of the Context Bar stays one undo step; it can add or drop the shadow crescent.
/// </summary>
public sealed class BevelCommand : IDocumentCommand, IUndoMemory
{
    /// <summary>One object and its crescents.</summary>
    public sealed class Target(DocObject source, IReadOnlyList<PathObject> existing, BevelPieces pieces)
    {
        public DocObject Source { get; } = source;
        public PathObject? Highlight { get; internal set; }
        public PathObject? Shadow { get; internal set; }
        internal IReadOnlyList<PathObject> Existing { get; } = existing;
        internal BevelPieces Pieces { get; set; } = pieces;
        internal List<Removal> Removed { get; } = [];
        internal GroupObject? NewGroup { get; set; }
        internal bool HighlightIn { get; set; }
        internal bool ShadowIn { get; set; }
    }

    private readonly Target[] _targets;
    private Document? _doc;

    public BevelCommand(IEnumerable<Target> targets, string description)
    {
        _targets = targets.ToArray();
        if (_targets.Length == 0) throw new ArgumentException("Nothing to do.");
        Description = description;
    }

    public string Description { get; }
    public IReadOnlyList<Target> Targets => _targets;

    /// <summary>What the crescents are made with. Set: they are live (they follow their object and remember these settings). Null: plain crescents.</summary>
    public BevelSettings? Settings { get; set; }

    /// <summary>The objects the crescents belong to, as the top-level objects to select afterwards.</summary>
    public IEnumerable<DocObject> TopLevels => _targets.Select(t => t.Source.TopLevel).Distinct();

    public long ApproximateBytes => _targets.Sum(t => UndoMemory.BytesOf(t.Existing.Cast<DocObject>()) + (t.Highlight is null ? 0 : UndoMemory.BytesOf(t.Highlight)) + (t.Shadow is null ? 0 : UndoMemory.BytesOf(t.Shadow)));
    public IEnumerable<string> HeldAssetIds => [];

    public void Do(Document doc)
    {
        _doc = doc;
        foreach (var t in _targets)
        {
            t.Removed.Clear();
            foreach (var old in t.Existing) t.Removed.Add(doc.Remove(old));   // the bevel that was there: gone, undo brings it back
            t.HighlightIn = t.ShadowIn = false;
            Sync(doc, t);   // (a redo makes new link objects, which the live-effect updater takes as they are)
        }
    }

    public void Undo(Document doc)
    {
        for (int i = _targets.Length - 1; i >= 0; i--)
        {
            var t = _targets[i];
            if (t.ShadowIn) { doc.Remove(t.Shadow!); t.ShadowIn = false; }
            if (t.HighlightIn) { doc.Remove(t.Highlight!); t.HighlightIn = false; }
            if (t.NewGroup is { } g && doc.Contains(g))
            {
                doc.SetGroup(t.Source, null);
                doc.Remove(g);
            }
            Stacking.Restore(doc, t.Removed);
        }
    }

    /// <summary>Changes what every target makes (in the order of <see cref="Targets"/>); applies it at once when the command has run.</summary>
    public void Retarget(IReadOnlyList<BevelPieces> pieces, BevelSettings? settings = null)
    {
        if (settings is not null) Settings = settings.Normalized();
        for (int i = 0; i < _targets.Length && i < pieces.Count; i++)
        {
            _targets[i].Pieces = pieces[i];
            if (_doc is not null) Sync(_doc, _targets[i]);
        }
    }

    // ---- Putting the crescents where the pieces say ----

    private void Sync(Document doc, Target t)
    {
        var leaf = t.Source;
        var layer = leaf.Layer ?? throw new InvalidOperationException($"{leaf} is not in the document.");
        var p = t.Pieces;

        // Highlight: directly above the object.
        if (p.Highlight is null && t.HighlightIn) { doc.Remove(t.Highlight!); t.HighlightIn = false; }
        if (p.Highlight is { } hg)
        {
            t.Highlight ??= New(leaf, BevelEffect.HighlightName);
            t.Highlight.Geometry = EffectGeometry.IntoParentSpace(hg, leaf);
            t.Highlight.Style = p.HighlightStyle;
            if (!t.HighlightIn) { Insert(doc, t, t.Highlight, layer.IndexOf(leaf) + 1); t.HighlightIn = true; }
        }

        // Shadow: above the highlight (or directly above the object when there is none).
        if (p.Shadow is null && t.ShadowIn) { doc.Remove(t.Shadow!); t.ShadowIn = false; }
        if (p.Shadow is { } sg)
        {
            t.Shadow ??= New(leaf, BevelEffect.ShadowName);
            t.Shadow.Geometry = EffectGeometry.IntoParentSpace(sg, leaf);
            t.Shadow.Style = p.ShadowStyle;
            if (!t.ShadowIn) { Insert(doc, t, t.Shadow, layer.IndexOf(leaf) + 1 + (t.HighlightIn ? 1 : 0)); t.ShadowIn = true; }
        }

        // live crescents: they follow the object (computed in document coordinates, stored in its group)
        if (Settings is { } s)
        {
            if (t.HighlightIn) t.Highlight!.Live = LiveLink.BesideSource(leaf, new LiveBevel(s, BevelPart.Highlight));
            if (t.ShadowIn) t.Shadow!.Live = LiveLink.BesideSource(leaf, new LiveBevel(s, BevelPart.Shadow));
        }
    }

    private static PathObject New(DocObject leaf, string name) =>
        new(PathGeometry.EmptyPath) { Name = name, Visible = leaf.Visible, Locked = leaf.Locked };

    /// <summary>Inserts a crescent at a drawing-order index; as a member of the object's group, making a group first when the object has none.</summary>
    private static void Insert(Document doc, Target t, PathObject crescent, int index)
    {
        var leaf = t.Source;
        var layer = leaf.Layer!;
        if (leaf.Parent is not GroupObject group)
        {
            group = t.NewGroup ??= new GroupObject();
            if (!doc.Contains(group))
            {
                doc.Insert(new Slot(layer, layer.Children.Count), group);   // an empty group has no place in the drawing order
                doc.SetGroup(leaf, group);
            }
        }
        doc.Insert(new Slot(layer, index, group, group.Children.Count), crescent);
    }
}
