namespace DeSignMage.Core.Model;

/// <summary>
/// A named stacking level. Layers are ordered bottom (index 0) to top. <see cref="Children"/> is the <b>drawing order</b> of the layer:
/// every drawn object, bottom to top, grouped or not. Groups are not in it (see <see cref="IObjectContainer"/>), so making or
/// breaking a group never changes what is drawn on top of what (R22).
/// </summary>
public sealed class Layer : IObjectContainer
{
    private readonly List<DocObject> _children = [];

    // Positions by object, built when somebody asks and dropped by every change. Sorting a selection by drawing order asks for
    // thousands of positions in a row, which one list scan each would make quadratic.
    private sealed class PositionMap(Dictionary<DocObject, int> positions) { public readonly Dictionary<DocObject, int> Positions = positions; }
    private volatile PositionMap? _positions;

    public Layer(string name) => Name = name;

    public string Name { get; set; }
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public bool Printable { get; set; } = true;

    /// <summary>Color chip shown in the layers panel (and used for selection highlight later).</summary>
    public DocColor Color { get; set; } = new(0, 0, 0);

    /// <summary>Every drawn object of the layer in drawing order, bottom to top (members of groups included, groups themselves not).</summary>
    public IReadOnlyList<DocObject> Children => _children;
    List<DocObject> IObjectContainer.ChildList => _children;

    /// <summary>Position of <paramref name="obj"/> in the drawing order, or -1. Fast to ask many times while nothing changes.</summary>
    public int IndexOf(DocObject obj)
    {
        var map = _positions ?? BuildPositions();
        return map.Positions.TryGetValue(obj, out int i) ? i : -1;
    }

    private PositionMap BuildPositions()
    {
        var d = new Dictionary<DocObject, int>(_children.Count);   // DocObject keeps reference equality
        for (int i = 0; i < _children.Count; i++) d[_children[i]] = i;
        return _positions = new PositionMap(d);
    }

    // ---- Changes (called by Document and the stacking commands only) ----

    internal void InsertAt(int index, DocObject obj)
    {
        _children.Insert(index, obj);
        _positions = null;
    }

    internal void InsertRangeAt(int index, IEnumerable<DocObject> objects)
    {
        _children.InsertRange(index, objects);
        _positions = null;
    }

    internal int ScanIndexOf(DocObject obj) => _children.IndexOf(obj);

    internal void RemoveAtIndex(int index)
    {
        _children.RemoveAt(index);
        _positions = null;
    }

    /// <summary>Puts the objects of the layer in a new drawing order (the same objects, in another order).</summary>
    internal void SetOrder(IReadOnlyList<DocObject> order)
    {
        _children.Clear();
        _children.AddRange(order);
        _positions = null;
    }

    public override string ToString() => $"Layer '{Name}'";
}
