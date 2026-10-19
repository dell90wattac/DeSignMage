using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>
/// A command that takes objects out and puts others in their place (Convert to Curves, a shape turned into a path by node editing, Break Apart's
/// copies, Cookie Cutter, Knife...). Live effects whose source was replaced by exactly one object follow the replacement; a source replaced by
/// several pieces, or by nothing, leaves its effects as plain objects.
/// </summary>
public interface IReplacesObjects
{
    /// <summary>Each object this command took out, with what it put in its place (empty = nothing), as of the last <c>Do</c>.</summary>
    IEnumerable<(DocObject Old, IReadOnlyList<DocObject> New)> Replacements { get; }
}

/// <summary>
/// The live-effect updater's part of an undo step: new outlines, links and frames for effect objects whose source changed. It is made already done
/// (the updater applies it while it works out the next round, so effects of effects follow too); <see cref="Undo"/> puts every effect back as it was
/// before the step, <see cref="Do"/> (redo) as it was after.
/// </summary>
public sealed class LiveRebuildCommand : IDocumentCommand, IUndoMemory
{
    /// <summary>One effect object's state before and after.</summary>
    internal sealed class Entry(PathObject effect, PathGeometry oldGeometry, ObjectStyle oldStyle, LiveLink? oldLink)
    {
        public PathObject Effect { get; } = effect;
        public PathGeometry OldGeometry { get; } = oldGeometry;
        public ObjectStyle OldStyle { get; } = oldStyle;
        public LiveLink? OldLink { get; } = oldLink;
        public PathGeometry NewGeometry { get; set; } = oldGeometry;
        public ObjectStyle NewStyle { get; set; } = oldStyle;
        public LiveLink? NewLink { get; set; } = oldLink;
    }

    private readonly Dictionary<PathObject, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly List<Entry> _order = [];

    public string Description => "Update Live Effects";
    public bool IsEmpty => _order.Count == 0;

    /// <summary>How many effect objects this step changed (rebuilt, re-linked or made plain).</summary>
    public int Count => _order.Count;

    /// <summary>The effect objects this step changed, in the order they were changed.</summary>
    public IEnumerable<PathObject> Effects => _order.Select(e => e.Effect);

    public long ApproximateBytes => _order.Sum(e => UndoMemory.BytesOf(e.OldGeometry) + (ReferenceEquals(e.OldGeometry, e.NewGeometry) ? 0 : UndoMemory.BytesOf(e.NewGeometry)));
    public IEnumerable<string> HeldAssetIds => [];

    /// <summary>Records and applies a new state of one effect object (the first call for an object remembers what it was).</summary>
    internal void Set(PathObject effect, PathGeometry geometry, ObjectStyle style, LiveLink? link)
    {
        if (!_entries.TryGetValue(effect, out var e))
        {
            e = new Entry(effect, effect.Geometry, effect.Style, effect.Live);
            _entries[effect] = e;
            _order.Add(e);
        }
        e.NewGeometry = geometry;
        e.NewStyle = style;
        e.NewLink = link;
        Apply(e, after: true);
    }

    /// <summary>Takes in what a later round of the same step did (a drag edits the step again): the old states stay the first ones.</summary>
    internal void Merge(LiveRebuildCommand later)
    {
        foreach (var l in later._order)
        {
            if (_entries.TryGetValue(l.Effect, out var e))
            {
                e.NewGeometry = l.NewGeometry;
                e.NewStyle = l.NewStyle;
                e.NewLink = l.NewLink;
            }
            else
            {
                _entries[l.Effect] = l;
                _order.Add(l);
            }
        }
    }

    public void Do(Document doc)
    {
        foreach (var e in _order) Apply(e, after: true);
    }

    public void Undo(Document doc)
    {
        for (int i = _order.Count - 1; i >= 0; i--) Apply(_order[i], after: false);
    }

    private static void Apply(Entry e, bool after)
    {
        var g = after ? e.NewGeometry : e.OldGeometry;
        var s = after ? e.NewStyle : e.OldStyle;
        var l = after ? e.NewLink : e.OldLink;
        if (!ReferenceEquals(e.Effect.Geometry, g)) e.Effect.Geometry = g;
        if (!ReferenceEquals(e.Effect.Style, s)) e.Effect.Style = s;
        if (!ReferenceEquals(e.Effect.Live, l)) e.Effect.Live = l;
    }
}

/// <summary>
/// Gives existing effect objects a new outline, style, name and link in one undo step: reopening an effect's tool on a live effect edits it in place (same
/// object, same place in the drawing order and its group) instead of making a second one.
/// </summary>
public sealed class ReshapeEffectsCommand : IDocumentCommand, IUndoMemory
{
    private readonly (PathObject Obj, PathGeometry OldG, ObjectStyle OldS, string? OldN, LiveLink? OldL, PathGeometry NewG, ObjectStyle NewS, string? NewN, LiveLink? NewL)[] _changes;

    public ReshapeEffectsCommand(string description, IEnumerable<(PathObject Obj, PathGeometry Geometry, ObjectStyle Style, string? Name, LiveLink? Link)> changes)
    {
        Description = description;
        _changes = changes.Select(c => (c.Obj, c.Obj.Geometry, c.Obj.Style, c.Obj.Name, c.Obj.Live, c.Geometry, c.Style, c.Name, c.Link)).ToArray();
    }

    public string Description { get; }
    public IEnumerable<PathObject> Objects => _changes.Select(c => c.Obj);
    public long ApproximateBytes => _changes.Sum(c => UndoMemory.BytesOf(c.OldG) + UndoMemory.BytesOf(c.NewG));
    public IEnumerable<string> HeldAssetIds => [];

    public void Do(Document doc)
    {
        foreach (var c in _changes) { c.Obj.Geometry = c.NewG; c.Obj.Style = c.NewS; c.Obj.Name = c.NewN; c.Obj.Live = c.NewL; }
    }

    public void Undo(Document doc)
    {
        for (int i = _changes.Length - 1; i >= 0; i--)
        {
            var c = _changes[i];
            c.Obj.Geometry = c.OldG; c.Obj.Style = c.OldS; c.Obj.Name = c.OldN; c.Obj.Live = c.OldL;
        }
    }
}

/// <summary>Effects ▸ Make Plain: the effect objects stop following their sources (they stay where and as they are). One undo step.</summary>
public sealed class SetLiveLinkCommand : IDocumentCommand
{
    private readonly (DocObject Obj, LiveLink? Old, LiveLink? New)[] _changes;

    public SetLiveLinkCommand(string description, IEnumerable<(DocObject Obj, LiveLink? New)> changes)
    {
        Description = description;
        _changes = changes.Select(c => (c.Obj, c.Obj.Live, c.New)).ToArray();
    }

    public string Description { get; }
    public int Count => _changes.Length;

    public void Do(Document doc)
    {
        foreach (var (o, _, n) in _changes) o.Live = n;
    }

    public void Undo(Document doc)
    {
        foreach (var (o, old, _) in _changes) o.Live = old;
    }
}
