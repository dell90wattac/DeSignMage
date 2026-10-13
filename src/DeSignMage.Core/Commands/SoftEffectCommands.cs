using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>
/// Adds or changes the Soft Shadow / Glow objects of some sources, as one undo step. A new effect object is put just below the lowest drawn object
/// of its source (R22) and joins the source's group; a source that is not a group becomes a group with it (R13). An effect that exists is changed in
/// place (its outline and style). <see cref="Retarget"/> changes the new state of an already executed command, so that dragging the shadow or typing
/// in the Context Bar keeps editing one step instead of making a step per change.
/// </summary>
public sealed class SoftEffectCommand : IDocumentCommand, IUndoMemory
{
    /// <summary>One source and its effect object. <see cref="IsNew"/>: the object is made by this command (otherwise it exists and is modified).</summary>
    public sealed class Target(DocObject source, PathObject effect, bool isNew, PathGeometry geometry, ObjectStyle style)
    {
        public DocObject Source { get; } = source;
        public PathObject Effect { get; } = effect;
        public bool IsNew { get; } = isNew;
        public PathGeometry NewGeometry { get; internal set; } = geometry;
        public ObjectStyle NewStyle { get; internal set; } = style;
        internal PathGeometry? OldGeometry { get; set; }
        internal ObjectStyle? OldStyle { get; set; }
        internal InsertAtSlotCommand? Insert { get; set; }
        internal GroupObjectsCommand? Group { get; set; }
    }

    private readonly Target[] _targets;
    private bool _done;

    public SoftEffectCommand(IEnumerable<Target> targets, string description)
    {
        _targets = targets.ToArray();
        if (_targets.Length == 0) throw new ArgumentException("Nothing to do.");
        Description = description;
    }

    public string Description { get; }
    public IReadOnlyList<Target> Targets => _targets;

    /// <summary>The effect objects, in the order of the sources.</summary>
    public IEnumerable<PathObject> Effects => _targets.Select(t => t.Effect);

    /// <summary>The groups made for sources that were single objects (null for the others), after the command ran.</summary>
    public GroupObject? GroupOf(int index) => _targets[index].Group?.Group;

    public long ApproximateBytes => _targets.Sum(t =>
        (t.IsNew ? UndoMemory.BytesOf(t.Effect) : 0) + (t.OldGeometry is { } g ? UndoMemory.BytesOf(g) : 0));

    public IEnumerable<string> HeldAssetIds => [];

    public void Do(Document doc)
    {
        foreach (var t in _targets)
        {
            if (t.IsNew)
            {
                if (t.Insert is null) Place(doc, t);
                else
                {
                    t.Insert.Do(doc);   // a redo: the same slot and the same group as the first time
                    t.Group?.Do(doc);
                }
            }
            else
            {
                t.OldGeometry ??= t.Effect.Geometry;
                t.OldStyle ??= t.Effect.Style;
            }
            Apply(t);
        }
        _done = true;
    }

    public void Undo(Document doc)
    {
        for (int i = _targets.Length - 1; i >= 0; i--)
        {
            var t = _targets[i];
            if (t.IsNew)
            {
                t.Group?.Undo(doc);
                t.Insert!.Undo(doc);
            }
            else
            {
                t.Effect.Geometry = t.OldGeometry!;
                t.Effect.Style = t.OldStyle!;
            }
        }
        _done = false;
    }

    /// <summary>Replaces the new outline and style of every target (in the order of <see cref="Targets"/>); applies them at once when the command has run.</summary>
    public void Retarget(IReadOnlyList<(PathGeometry Geometry, ObjectStyle Style)> states)
    {
        for (int i = 0; i < _targets.Length && i < states.Count; i++)
        {
            _targets[i].NewGeometry = states[i].Geometry;
            _targets[i].NewStyle = states[i].Style;
            if (_done) Apply(_targets[i]);
        }
    }

    private static void Apply(Target t)
    {
        t.Effect.Geometry = t.NewGeometry;
        t.Effect.Style = t.NewStyle;
    }

    private static void Place(Document doc, Target t)
    {
        var source = t.Source;
        // just below the member and the effects it already has; in the member's group, or in a new group with it
        var layer = source.Layer ?? throw new InvalidOperationException("The source is not in the document.");
        int index = SoftEffect.LowestIndex(source);
        if (source.Parent is GroupObject group)
        {
            t.Insert = new InsertAtSlotCommand(new Slot(layer, index, group, group.Children.Count), t.Effect, t.Effect.Name ?? "Soft Shadow");
            t.Insert.Do(doc);
        }
        else
        {
            t.Insert = new InsertAtSlotCommand(new Slot(layer, index), t.Effect, t.Effect.Name ?? "Soft Shadow");
            t.Insert.Do(doc);
            t.Group = new GroupObjectsCommand(doc, [source, t.Effect]);
            t.Group.Do(doc);
        }
    }
}

/// <summary>
/// Sets or clears the Feather of objects, as one undo step (<see cref="Update"/> re-targets it while a field is dragged or typed). Feather is part of the
/// object's own style (R8), so there is no new object: groups pass it to their drawn members (a group itself cannot be feathered, as in the legacy program),
/// a combined object is feathered as one.
/// </summary>
public sealed class FeatherCommand : IDocumentCommand
{
    private readonly (DocObject Obj, ObjectStyle Old, FeatherStyle? Previous)[] _targets;
    private FeatherStyle? _feather;

    public FeatherCommand(string description, IEnumerable<DocObject> targets, FeatherStyle? feather)
    {
        Description = description;
        _targets = targets.Select(o => (o, o.Style, o.Style.Feather)).ToArray();
        _feather = feather;
    }

    public string Description { get; }

    public IReadOnlyList<DocObject> Objects => _targets.Select(t => t.Obj).ToArray();

    public void Do(Document doc) => Apply();

    public void Undo(Document doc)
    {
        foreach (var (o, old, _) in _targets) o.Style = old;
    }

    /// <summary>Changes what the objects get (the command must have been executed).</summary>
    public void Update(FeatherStyle? feather)
    {
        _feather = feather;
        Apply();
    }

    private void Apply()
    {
        foreach (var (o, old, _) in _targets) o.Style = old.WithFeather(_feather);
    }
}
