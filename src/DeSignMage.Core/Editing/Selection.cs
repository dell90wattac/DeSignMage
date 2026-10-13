using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

/// <summary>Ordered set of selected top-level objects. Not part of undo history (yet).</summary>
public sealed class Selection
{
    private readonly List<DocObject> _items = [];

    public IReadOnlyList<DocObject> Items => _items;
    public int Count => _items.Count;
    public bool IsEmpty => _items.Count == 0;

    public event EventHandler? Changed;

    public bool Contains(DocObject o) => _items.Contains(o);

    public void Set(IEnumerable<DocObject> objects)
    {
        var list = objects.Distinct().ToList();
        if (list.SequenceEqual(_items)) return;
        _items.Clear();
        _items.AddRange(list);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Set(DocObject o) => Set([o]);
    public void Clear() => Set([]);

    public void Add(IEnumerable<DocObject> objects) => Set(_items.Concat(objects));

    public void Toggle(DocObject o)
    {
        if (!_items.Remove(o)) _items.Add(o);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Rect2 GetBounds()
    {
        var r = Rect2.Empty;
        foreach (var o in _items) r = r.Union(o.GetWorldBounds());
        return r;
    }

    /// <summary>
    /// Drops objects that are no longer in the document and replaces objects that ended up inside a group
    /// (e.g. after undoing an ungroup) by their top-level group, so the selection is always top-level.
    /// </summary>
    public void Prune(Document doc)
    {
        var pruned = _items.Where(doc.Contains).Select(o => o.TopLevel).Distinct().ToList();
        if (pruned.SequenceEqual(_items)) return;
        _items.Clear();
        _items.AddRange(pruned);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
