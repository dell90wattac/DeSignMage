using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Model;

/// <summary>Stable object identity within a document. Assigned on first insertion; preserved by undo/redo and files.</summary>
public readonly record struct ObjectId(long Value)
{
    public static readonly ObjectId None = new(0);
    public bool IsNone => Value == 0;
    public override string ToString() => $"#{Value}";
}

/// <summary>
/// Something objects belong to: a <see cref="Layer"/> or a <see cref="GroupObject"/>. The two mean different things (requirement R22,
/// "grouping never changes the stacking order"):
/// <list type="bullet">
/// <item>A <see cref="Layer"/>'s <c>Children</c> are <b>every drawn object of the layer, in drawing order</b>, bottom to top, whether they are
/// in a group or not. Groups are never in that list: they have no place in the drawing order.</item>
/// <item>A <see cref="GroupObject"/>'s <c>Children</c> are its direct members (objects and groups). They say what belongs together, nothing
/// about what is drawn on top of what.</item>
/// </list>
/// </summary>
public interface IObjectContainer
{
    IReadOnlyList<DocObject> Children { get; }
    internal List<DocObject> ChildList { get; }
}

public static class ObjectContainerExtensions
{
    public static int IndexOf(this IObjectContainer container, DocObject obj) => container.ChildList.IndexOf(obj);
}

/// <summary>
/// Base of everything placed on the page. Geometry is in local coordinates; <see cref="Transform"/> maps
/// local to parent coordinates. Mutate only through commands (see <c>DeSignMage.Core.Commands</c>) so changes are undoable.
/// </summary>
public abstract class DocObject
{
    private Affine2 _transform = Affine2.Identity;
    private ObjectStyle _style = ObjectStyle.None;
    private string? _name;
    private bool _visible = true;
    private bool _locked;

    public ObjectId Id { get; internal set; }

    /// <summary>
    /// What this object belongs to: its <see cref="GroupObject"/>, or the <see cref="Layer"/> for an object that is not in a group
    /// (null while it is not in a document). It decides the transform (a group's transform applies to its members) and what a click
    /// selects (<see cref="TopLevel"/>); it has nothing to do with the drawing order, which is the order of <see cref="Layer.Children"/>.
    /// </summary>
    public IObjectContainer? Parent { get; internal set; }

    /// <summary>
    /// The layer whose drawing order this object is in (null while it is not in a document). A group has no place in the drawing order;
    /// its layer is just the layer it was made on.
    /// </summary>
    public Layer? Layer { get; internal set; }

    /// <summary>Where the object stood in the drawing order of the document it was copied from (0 = unknown). Only copying reads it (see <c>Stacking.CloneForCopy</c>).</summary>
    internal long CopyRank { get; set; }

    /// <summary>Bumped on every property change. Renderers key caches on (Id, Version).</summary>
    public int Version { get; private set; }

    private int _notShape;   // how many of the Version bumps were not changes of the object's own outline (name, style, placement, flags, link)

    /// <summary>
    /// Changes only when the object's own outline changes (its geometry, size, text, font...), not when it is renamed, recolored, moved,
    /// hidden, locked or linked. Live effects compare it to know whether their source changed shape (placement is compared separately).
    /// </summary>
    public int ShapeVersion => Version - _notShape;

    public string? Name { get => _name; set { _name = value; TouchNotShape(); } }
    public Affine2 Transform { get => _transform; set { _transform = value; TouchNotShape(); } }
    public ObjectStyle Style { get => _style; set { _style = value; TouchNotShape(); } }
    public bool Visible { get => _visible; set { _visible = value; TouchNotShape(); } }
    public bool Locked { get => _locked; set { _locked = value; TouchNotShape(); } }

    private LiveLink? _live;

    /// <summary>
    /// Set on a live effect object (an outline, shadow, soft shadow, glow or bevel crescent that follows its source): what it follows and how
    /// it is made. Null for every other object, and for an effect that was made plain. Change it through commands.
    /// </summary>
    public LiveLink? Live { get => _live; set { _live = value; TouchNotShape(); } }

    /// <summary>What the live-effect updater last saw of this effect and its source (runtime only, never saved or copied).</summary>
    internal object? LiveSync { get; set; }

    private void TouchNotShape()
    {
        _notShape++;
        Touch();
    }

    /// <summary>Human-readable kind, as shown in the layers/objects panel.</summary>
    public abstract string KindName { get; }

    /// <summary>Outline in local coordinates, or null if the object has none of its own (groups).</summary>
    public abstract PathGeometry? GetLocalGeometry();

    /// <summary>Deep copy with no id and no parent (it gets a fresh id when inserted).</summary>
    public DocObject Clone()
    {
        var c = CloneCore();
        c._transform = _transform;
        c._style = _style;
        c._name = _name;
        c._visible = _visible;
        c._locked = _locked;
        c._live = _live;   // still pointing at the original source: copies remap it (LiveEffects.CloneSet), replacements re-link it
        c.CopyRank = CopyRank;
        return c;
    }

    /// <summary>Creates the copy with the subclass's own state; base properties are copied by <see cref="Clone"/>.</summary>
    protected abstract DocObject CloneCore();

    protected void Touch() => Version++;

    public Affine2 GetWorldTransform()
    {
        var m = _transform;
        for (var p = Parent; p is DocObject po; p = po.Parent) m = m * po._transform;
        return m;
    }

    private sealed record BoundsEntry(int Version, long Extra, Affine2 World, Rect2 Bounds);
    private BoundsEntry? _bounds;   // one immutable reference, so a worker thread never sees a half-written entry

    /// <summary>
    /// Tight bounds of the geometry in document coordinates (stroke not included). Cached until this object changes
    /// (<see cref="Version"/>, <see cref="DependencyStamp"/>) or something above it moves it, so asking for the content
    /// bounds of a big document after one edit only recomputes the object that changed.
    /// </summary>
    public virtual Rect2 GetWorldBounds()
    {
        var world = GetWorldTransform();
        long extra = DependencyStamp;
        if (_bounds is { } e && e.Version == Version && e.Extra == extra && e.World == world) return e.Bounds;
        var bounds = ComputeWorldBounds(world);
        _bounds = new BoundsEntry(Version, extra, world, bounds);
        return bounds;
    }

    /// <summary>
    /// Second cache key, next to <see cref="Version"/>, for state this object depends on that <see cref="Version"/> doesn't
    /// cover (text: the font service generation; combined: its parts' versions). Caches compare both values for equality.
    /// </summary>
    public virtual long DependencyStamp => 0;

    protected virtual Rect2 ComputeWorldBounds(Affine2 world)
    {
        var g = GetLocalGeometry();
        return g is null ? Rect2.Empty : g.Transform(world).GetBounds();
    }

    /// <summary>The top-level object (direct child of a layer) that contains this one.</summary>
    public DocObject TopLevel
    {
        get
        {
            var o = this;
            while (o.Parent is DocObject p) o = p;
            return o;
        }
    }

    /// <summary>False when this object or a group around it is hidden.</summary>
    public bool IsShown
    {
        get
        {
            for (DocObject? o = this; o is not null; o = o.Parent as DocObject)
                if (!o._visible) return false;
            return true;
        }
    }

    public override string ToString() => $"{KindName} {Id}";
}
