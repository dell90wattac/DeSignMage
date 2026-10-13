using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Model;

public sealed class DocumentChangedEventArgs(long revision) : EventArgs
{
    public long Revision { get; } = revision;
}

/// <summary>
/// A place in the document where an object can be put: a position in a layer's drawing order and, when the object is to be a member
/// of a group, the group and the position among its members. Taken from <see cref="Removal.Slot"/> (where something stood) and handed
/// to <see cref="Document.Insert(Slot, DocObject)"/> (to put something there), which is how an object is swapped for others.
/// </summary>
public readonly record struct Slot(Layer Layer, int StackIndex, GroupObject? Group = null, int MemberIndex = -1);

/// <summary>
/// What <see cref="Document.Remove"/> took out of the document, and everything <see cref="Document.Restore"/> needs to put it back
/// exactly: every removed object's place in the drawing order, and the removed object's place in its group. Commands keep it for undo.
/// </summary>
public sealed class Removal
{
    internal Removal(DocObject root, Slot slot)
    {
        Root = root;
        Slot = slot;
    }

    /// <summary>The object that was removed (with everything below it, if it is a group).</summary>
    public DocObject Root { get; }

    /// <summary>Where a replacement for <see cref="Root"/> goes: in the layer where its topmost drawn object stood (a group: the topmost member), in its group.</summary>
    public Slot Slot { get; }

    internal GroupObject? OuterGroup { get; set; }
    internal int OuterIndex { get; set; }
    internal Layer? RootLayer { get; set; }

    /// <summary>The drawn objects that left the drawing order, in the order they were taken out (so each index is the one it had before).</summary>
    internal List<(DocObject Leaf, Layer Layer, int Index)> Stack { get; } = [];

    /// <summary>The groups that were unregistered with their layers.</summary>
    internal List<(GroupObject Group, Layer? Layer)> Groups { get; } = [];
}

/// <summary>
/// The design: page setup, layers of objects, and embedded assets. Keeps an id → object index.
/// Structural edits (<see cref="Insert(IObjectContainer,int,DocObject)"/>, <see cref="Remove"/>, <see cref="SetGroup"/>) are meant to be called by commands.
/// <para>
/// Drawing order and grouping are two separate things (requirement R22). Each <see cref="Layer"/> holds all its drawn objects in one flat list,
/// the drawing order. Groups are membership only: <see cref="DocObject.Parent"/> says which group an object is in, and a group's transform
/// applies to its members, but a group has no place in the drawing order, so <see cref="SetGroup"/> (making or breaking a group) never
/// moves anything in it.
/// </para>
/// </summary>
public sealed class Document
{
    private readonly List<Layer> _layers = [];
    private readonly Dictionary<ObjectId, DocObject> _index = [];
    private long _nextId = 1;
    private int _groupCount;
    private Layer _activeLayer;

    public Document(PageSetup page)
    {
        Page = page;
        _activeLayer = new Layer("Layer 1");
        _layers.Add(_activeLayer);
    }

    public PageSetup Page { get; set; }
    public IReadOnlyList<Layer> Layers => _layers;
    public AssetStore Assets { get; } = new();

    /// <summary>Ruler guides (requirement R4): saved with the document, never printed or exported.</summary>
    public GuideCollection Guides { get; } = new();

    public Layer ActiveLayer
    {
        get => _activeLayer;
        set => _activeLayer = _layers.Contains(value) ? value : throw new ArgumentException("Layer is not in this document.");
    }

    /// <summary>Increments on every change notification. Renderers compare it to know when to rebuild.</summary>
    public long Revision { get; private set; }

    public event EventHandler<DocumentChangedEventArgs>? Changed;

    public void NotifyChanged()
    {
        Revision++;
        Changed?.Invoke(this, new DocumentChangedEventArgs(Revision));
    }

    /// <summary>Drawn objects and groups together.</summary>
    public int ObjectCount => _index.Count;

    /// <summary>How many groups the document has.</summary>
    public int GroupCount => _groupCount;

    public DocObject? Find(ObjectId id) => _index.GetValueOrDefault(id);
    public bool Contains(DocObject obj) => !obj.Id.IsNone && _index.TryGetValue(obj.Id, out var o) && ReferenceEquals(o, obj);

    /// <summary>Every group, in the order they were created (by id).</summary>
    public IEnumerable<GroupObject> Groups => _index.Values.OfType<GroupObject>().OrderBy(g => g.Id.Value);

    /// <summary>
    /// Every object: the drawn objects of each layer in drawing order, bottom to top, then the groups (they have no place in the drawing order).
    /// </summary>
    public IEnumerable<DocObject> AllObjects()
    {
        foreach (var layer in _layers)
            foreach (var o in layer.Children)
                yield return o;
        foreach (var g in Groups) yield return g;
    }

    /// <summary>The objects below a container, depth first: for a group its members (groups before what is in them), for a layer its drawn objects.</summary>
    public static IEnumerable<DocObject> Walk(IObjectContainer container)
    {
        foreach (var o in container.Children)
        {
            yield return o;
            if (o is IObjectContainer g && container is not Layer)
                foreach (var c in Walk(g)) yield return c;
        }
    }

    // ---- Layers ----

    public void InsertLayer(int index, Layer layer)
    {
        _layers.Insert(index, layer);
        foreach (var o in layer.Children)
        {
            o.Layer = layer;
            Register(o);
        }
    }

    public int RemoveLayer(Layer layer)
    {
        if (_layers.Count == 1) throw new InvalidOperationException("A document needs at least one layer.");
        int index = _layers.IndexOf(layer);
        if (index < 0) throw new ArgumentException("Layer is not in this document.");
        _layers.RemoveAt(index);
        var groups = new HashSet<GroupObject>();
        foreach (var o in layer.Children)
        {
            Unregister(o);
            for (var g = o.Parent as GroupObject; g is not null; g = g.Parent as GroupObject) groups.Add(g);
        }
        foreach (var g in groups)
            if (!g.Leaves().Any(l => l.Layer is not null && !ReferenceEquals(l.Layer, layer))) Unregister(g);   // a group with nothing left elsewhere goes with the layer
        if (_activeLayer == layer) _activeLayer = _layers[Math.Min(index, _layers.Count - 1)];
        return index;
    }

    // ---- Putting objects in ----

    /// <summary>
    /// Inserts <paramref name="obj"/> (and everything in it, if it is a group) into <paramref name="container"/>; index -1 = on top.
    /// A <see cref="Layer"/>: the object goes at that place in the drawing order. A <see cref="GroupObject"/>: the object becomes its member
    /// number <paramref name="index"/>, and is drawn on top of its layer (a group gets no place in the drawing order; for an exact
    /// place use <see cref="Insert(Slot, DocObject)"/>).
    /// </summary>
    public void Insert(IObjectContainer container, int index, DocObject obj)
    {
        switch (container)
        {
            case Layer layer:
                Insert(new Slot(layer, index < 0 || index > layer.Children.Count ? layer.Children.Count : index), obj);
                break;
            case GroupObject group:
            {
                var layer = group.Layer ?? throw new InvalidOperationException($"{group} is not in the document.");
                Insert(new Slot(layer, layer.Children.Count, group, index), obj);
                break;
            }
            default:
                throw new ArgumentException("A container is a layer or a group.", nameof(container));
        }
    }

    /// <summary>Inserts <paramref name="obj"/> at <paramref name="slot"/>; returns the slot right after it.</summary>
    public Slot Insert(Slot slot, DocObject obj) => InsertMany(slot, [obj]);

    /// <summary>
    /// Inserts several objects one after the other at <paramref name="slot"/>. The drawn objects inside them (the objects themselves, and the
    /// members of groups) enter the drawing order in <paramref name="drawingOrder"/> when given, else object by object, depth first.
    /// Returns the slot right after them.
    /// </summary>
    public Slot InsertMany(Slot slot, IReadOnlyList<DocObject> roots, IReadOnlyList<DocObject>? drawingOrder = null)
    {
        foreach (var r in roots)
            if (r.Parent is not null || r.Layer is not null) throw new InvalidOperationException($"{r} is already in a document or a group.");

        var leaves = drawingOrder ?? CollectLeaves(roots);
        var layer = slot.Layer;
        var outer = slot.Group;
        int member = outer is null ? -1 : (slot.MemberIndex < 0 || slot.MemberIndex > outer.Children.Count ? outer.Children.Count : slot.MemberIndex);
        foreach (var r in roots)
        {
            if (outer is not null)
            {
                outer.MemberList.Insert(member++, r);
                r.Parent = outer;
            }
            else r.Parent = layer;
            RegisterTree(r, layer);
        }
        int at = Math.Clamp(slot.StackIndex, 0, layer.Children.Count);
        layer.InsertRangeAt(at, leaves);
        return new Slot(layer, at + leaves.Count, outer, member);
    }

    private static List<DocObject> CollectLeaves(IReadOnlyList<DocObject> roots)
    {
        var list = new List<DocObject>();
        foreach (var r in roots)
        {
            if (r is GroupObject g) list.AddRange(g.Leaves());
            else list.Add(r);
        }
        return list;
    }

    /// <summary>Registers an object and everything below it, and puts them on <paramref name="layer"/>.</summary>
    private void RegisterTree(DocObject o, Layer layer)
    {
        o.Layer = layer;
        Register(o);
        if (o is GroupObject g)
            foreach (var c in g.Children) RegisterTree(c, layer);
    }

    // ---- Taking objects out ----

    /// <summary>
    /// Removes <paramref name="obj"/> from the document: a drawn object leaves the drawing order and its group; a group takes everything in it along
    /// (the members keep belonging to it). The result holds what <see cref="Restore"/> needs to undo it.
    /// </summary>
    public Removal Remove(DocObject obj)
    {
        if (!Contains(obj)) throw new InvalidOperationException($"{obj} is not in the document.");
        return obj is GroupObject g ? RemoveGroup(g) : RemoveLeaf(obj);
    }

    private Removal RemoveLeaf(DocObject obj)
    {
        var layer = obj.Layer ?? throw new InvalidOperationException($"{obj} is not in a layer.");
        int index = layer.ScanIndexOf(obj);
        layer.RemoveAtIndex(index);
        var outer = obj.Parent as GroupObject;
        int member = -1;
        if (outer is not null)
        {
            member = outer.MemberList.IndexOf(obj);
            outer.MemberList.RemoveAt(member);
        }
        var removal = new Removal(obj, new Slot(layer, index, outer, member)) { OuterGroup = outer, OuterIndex = member, RootLayer = layer };
        removal.Stack.Add((obj, layer, index));
        obj.Parent = null;
        obj.Layer = null;
        Unregister(obj);
        return removal;
    }

    private Removal RemoveGroup(GroupObject root)
    {
        var groups = new List<GroupObject>();
        var leaves = new List<DocObject>();
        void Collect(GroupObject g)
        {
            groups.Add(g);
            foreach (var c in g.Children)
            {
                if (c is GroupObject cg) Collect(cg);
                else leaves.Add(c);
            }
        }
        Collect(root);

        // Positions first (the position map is only valid while nothing changes), then out from the top so each recorded index stays right.
        var entries = leaves.Where(l => l.Layer is not null)
            .Select(l => (Leaf: l, Layer: l.Layer!, Index: l.Layer!.IndexOf(l)))
            .OrderBy(e => _layers.IndexOf(e.Layer)).ThenBy(e => e.Index)
            .ToList();

        var outer = root.Parent as GroupObject;
        int outerIndex = outer?.MemberList.IndexOf(root) ?? -1;
        var homeLayer = root.Layer;

        Slot slot;
        if (entries.Count == 0)
        {
            var layer = homeLayer ?? _layers[0];
            slot = new Slot(layer, layer.Children.Count, outer, outerIndex);
        }
        else
        {
            var top = entries[^1];                                       // highest layer, and in it the highest index
            int below = entries.Count(e => ReferenceEquals(e.Layer, top.Layer) && e.Index < top.Index);
            slot = new Slot(top.Layer, top.Index - below, outer, outerIndex);
        }

        var removal = new Removal(root, slot) { OuterGroup = outer, OuterIndex = outerIndex, RootLayer = homeLayer };
        // Highest layer first and from the top down inside each layer: the index recorded is the one the object had before anything left.
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var (leaf, layer, index) = entries[i];
            layer.RemoveAtIndex(index);
            removal.Stack.Add((leaf, layer, index));
            leaf.Layer = null;
            Unregister(leaf);
        }
        foreach (var g in groups)
        {
            removal.Groups.Add((g, g.Layer));
            g.Layer = null;
            Unregister(g);
        }
        if (outer is not null) outer.MemberList.RemoveAt(outerIndex);
        root.Parent = null;
        return removal;
    }

    /// <summary>Puts back exactly what <see cref="Remove"/> took out. The document must be as it was right after that removal.</summary>
    public void Restore(Removal r)
    {
        if (r.OuterGroup is { } outer)
        {
            outer.MemberList.Insert(r.OuterIndex, r.Root);
            r.Root.Parent = outer;
        }
        else r.Root.Parent = r.RootLayer;

        foreach (var (g, layer) in r.Groups)
        {
            g.Layer = layer;
            Register(g);
        }
        for (int i = r.Stack.Count - 1; i >= 0; i--)
        {
            var (leaf, layer, index) = r.Stack[i];
            layer.InsertAt(index, leaf);
            leaf.Layer = layer;
            Register(leaf);
        }
    }

    // ---- Grouping ----

    /// <summary>
    /// Makes <paramref name="obj"/> (a drawn object or a group, already in the document) a member of <paramref name="group"/> at member position
    /// <paramref name="index"/> (-1 = last), or takes it out of its group when <paramref name="group"/> is null. <b>Nothing moves in the drawing order.</b>
    /// Returns the group it was in and its member position there, for undo.
    /// </summary>
    public (GroupObject? Group, int Index) SetGroup(DocObject obj, GroupObject? group, int index = -1)
    {
        if (!Contains(obj)) throw new InvalidOperationException($"{obj} is not in the document.");
        if (group is not null && !Contains(group)) throw new InvalidOperationException($"{group} is not in the document.");
        for (var a = group; a is not null; a = a.Parent as GroupObject)
            if (ReferenceEquals(a, obj)) throw new InvalidOperationException("A group cannot be a member of itself.");

        var old = obj.Parent as GroupObject;
        int oldIndex = -1;
        if (old is not null)
        {
            oldIndex = old.MemberList.IndexOf(obj);
            old.MemberList.RemoveAt(oldIndex);
        }
        if (group is not null)
        {
            int at = index < 0 || index > group.Children.Count ? group.Children.Count : index;
            group.MemberList.Insert(at, obj);
            obj.Parent = group;
            for (var a = group; a is not null && a.Layer is null; a = a.Parent as GroupObject) a.Layer = obj.Layer;   // a group made empty (a file being read) is on the layer of its first member
        }
        else obj.Parent = obj.Layer;
        return (old, oldIndex);
    }

    // ---- Registry ----

    private void Register(DocObject o)
    {
        if (o.Id.IsNone) o.Id = new ObjectId(_nextId++);
        else if (o.Id.Value >= _nextId) _nextId = o.Id.Value + 1;
        if (_index.TryGetValue(o.Id, out var existing) && !ReferenceEquals(existing, o))
            throw new InvalidOperationException($"Duplicate object id {o.Id}.");
        if (_index.TryAdd(o.Id, o) && o is GroupObject) _groupCount++;
        _index[o.Id] = o;
    }

    private void Unregister(DocObject o)
    {
        if (_index.Remove(o.Id) && o is GroupObject) _groupCount--;
    }

    // ---- Drawing order ----

    /// <summary>Puts the drawn objects of <paramref name="layer"/> in a new order (the same objects). For the stacking commands.</summary>
    public void SetDrawingOrder(Layer layer, IReadOnlyList<DocObject> order)
    {
        if (order.Count != layer.Children.Count) throw new ArgumentException("The new order must hold the same objects.", nameof(order));
        layer.SetOrder(order);
    }

    // ---- Queries ----

    public Rect2 GetContentBounds()
    {
        var r = Rect2.Empty;
        foreach (var layer in _layers)
            if (layer.Visible)
                foreach (var o in layer.Children)
                    if (o.IsShown) r = r.Union(o.GetWorldBounds());
        return r;
    }
}

/// <summary>Embedded binary assets (placed images, later fonts), content-addressed by SHA-256.</summary>
public sealed class AssetStore
{
    // Pictures are processed on worker threads (image commands, import) while the window reads the store, so every access locks.
    private readonly object _gate = new();
    private readonly Dictionary<string, Asset> _assets = [];

    public string Add(byte[] data, string mediaType)
    {
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
        lock (_gate) _assets.TryAdd(id, new Asset(id, mediaType, data));
        return id;
    }

    public Asset? Get(string id)
    {
        lock (_gate) return _assets.GetValueOrDefault(id);
    }

    /// <summary>Stored bytes of an asset (0 if it isn't there).</summary>
    public long SizeOf(string id)
    {
        lock (_gate) return _assets.TryGetValue(id, out var a) ? a.Data.Length : 0;
    }

    /// <summary>Drops an asset's bytes. Only for pictures nothing can show or restore any more (see <see cref="Commands.CommandHistory"/>).</summary>
    public bool Remove(string id)
    {
        lock (_gate) return _assets.Remove(id);
    }

    /// <summary>A snapshot of the stored assets.</summary>
    public IEnumerable<Asset> All
    {
        get
        {
            lock (_gate) return _assets.Values.ToArray();
        }
    }
}

public sealed record Asset(string Id, string MediaType, byte[] Data);
