using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>Adds an object to a layer or group (index -1 = on top). A group goes in with all its members.</summary>
public sealed class AddObjectCommand(IObjectContainer container, DocObject obj, int index = -1) : IDocumentCommand, IUndoMemory
{
    public DocObject Object { get; } = obj;
    public string Description => $"Add {Object.KindName}";
    public long ApproximateBytes => UndoMemory.BytesOf(Object);
    public IEnumerable<string> HeldAssetIds => UndoMemory.AssetsOf(Object);

    public void Do(Document doc) => doc.Insert(container, index, Object);
    public void Undo(Document doc) => doc.Remove(Object);
}

/// <summary>
/// Adds several new top-level objects (copies, a paste) on top of a layer. The drawn objects inside them go in the order they were drawn in
/// where they were copied from (<see cref="Stacking.CloneForCopy"/>), so what lay between members of a copied group, or between the copied
/// objects, lies between the copies in the same way (R22).
/// </summary>
public sealed class AddObjectsCommand : IDocumentCommand, IUndoMemory
{
    private readonly IObjectContainer _container;
    private readonly DocObject[] _items;
    private readonly DocObject[] _drawingOrder;

    public AddObjectsCommand(IObjectContainer container, IEnumerable<DocObject> items)
    {
        _container = container;
        _items = items.ToArray();
        _drawingOrder = Stacking.DrawingOrderOf(_items).ToArray();
    }

    public string Description => _items.Length == 1 ? $"Add {_items[0].KindName}" : $"Add {_items.Length} Objects";
    public long ApproximateBytes => UndoMemory.BytesOf(_items);
    public IEnumerable<string> HeldAssetIds => UndoMemory.AssetsOf(_items);

    public void Do(Document doc)
    {
        var slot = _container switch
        {
            Layer layer => new Slot(layer, layer.Children.Count),
            GroupObject group => new Slot(group.Layer ?? throw new InvalidOperationException($"{group} is not in the document."),
                group.Layer.Children.Count, group, group.Children.Count),
            _ => throw new ArgumentException("A container is a layer or a group."),
        };
        doc.InsertMany(slot, _items, _drawingOrder);
    }

    public void Undo(Document doc)
    {
        for (int i = _items.Length - 1; i >= 0; i--) doc.Remove(_items[i]);
    }
}

/// <summary>Deletes objects; undo puts each back at its original position in the drawing order and its group.</summary>
public sealed class DeleteObjectsCommand : IDocumentCommand, IUndoMemory
{
    private readonly DocObject[] _objects;
    private readonly List<Removal> _removed = [];

    public DeleteObjectsCommand(IEnumerable<DocObject> objects) => _objects = objects.Distinct().ToArray();

    public long ApproximateBytes => UndoMemory.BytesOf(_objects);
    public IEnumerable<string> HeldAssetIds => UndoMemory.AssetsOf(_objects);

    public string Description => _objects.Length == 1 ? $"Delete {_objects[0].KindName}" : $"Delete {_objects.Length} Objects";

    public void Do(Document doc)
    {
        _removed.Clear();
        foreach (var o in _objects)
        {
            if (!doc.Contains(o)) continue;   // e.g. a member of another deleted group
            _removed.Add(doc.Remove(o));
        }
    }

    public void Undo(Document doc)
    {
        for (int i = _removed.Count - 1; i >= 0; i--) doc.Restore(_removed[i]);
    }
}
/// <summary>Sets new local transforms on objects (move, scale, rotate, skew, mirror).</summary>
public sealed class TransformObjectsCommand : IDocumentCommand
{
    private readonly (DocObject Obj, Affine2 Old, Affine2 New)[] _changes;

    public TransformObjectsCommand(string description, IEnumerable<(DocObject Obj, Affine2 NewTransform)> changes)
    {
        Description = description;
        _changes = changes.Select(c => (c.Obj, c.Obj.Transform, c.NewTransform)).ToArray();
    }

    public string Description { get; }

    /// <summary>
    /// Applies <paramref name="worldDelta"/> (in document coordinates) to each object, accounting for
    /// parent group transforms.
    /// </summary>
    public static TransformObjectsCommand ApplyWorld(string description, IEnumerable<DocObject> objects, Affine2 worldDelta) =>
        new(description, objects.Select(o => (o, ComposeWorld(o, worldDelta))));

    /// <summary>The local transform that moves <paramref name="o"/> by <paramref name="worldDelta"/> in world space.</summary>
    public static Affine2 ComposeWorld(DocObject o, Affine2 worldDelta)
    {
        var parentWorld = o.Parent is DocObject p ? p.GetWorldTransform() : Affine2.Identity;
        return parentWorld.IsIdentity
            ? o.Transform * worldDelta
            : o.Transform * parentWorld * worldDelta * parentWorld.Invert();
    }

    public void Do(Document doc)
    {
        foreach (var (o, _, n) in _changes) o.Transform = n;
    }

    public void Undo(Document doc)
    {
        foreach (var (o, old, _) in _changes) o.Transform = old;
    }
}

/// <summary>
/// Replaces the style (fill and stroke) of objects. Groups pass the change on to every member (at any depth), and a
/// <see cref="CombinedObject"/> to every part, so recoloring a group or a combined graphic recolors all of it.
/// <para>
/// <see cref="Update"/> re-targets an already executed command with a new change computed from the original styles: a slider
/// drag keeps updating one command, so the whole drag is one undo step.
/// </para>
/// </summary>
public sealed class SetStyleCommand : IDocumentCommand
{
    private readonly (DocObject Obj, ObjectStyle Old, ObjectStyle New)[] _changes;

    public SetStyleCommand(string description, IEnumerable<DocObject> objects, Func<ObjectStyle, ObjectStyle> change)
    {
        Description = description;
        _changes = StyleTargets.Expand(objects)
            .Select(o => (o, o.Style, change(o.Style)))
            .ToArray();
    }

    public string Description { get; }

    /// <summary>True when the change leaves every style as it was (nothing worth an undo step).</summary>
    public bool IsNoOp => _changes.All(c => c.Old == c.New);

    public void Do(Document doc) => Apply();

    public void Undo(Document doc)
    {
        foreach (var (o, old, _) in _changes) o.Style = old;
    }

    /// <summary>Recomputes the new styles from the original ones and applies them (the command must have been executed).</summary>
    public void Update(Func<ObjectStyle, ObjectStyle> change)
    {
        for (int i = 0; i < _changes.Length; i++)
            _changes[i] = (_changes[i].Obj, _changes[i].Old, change(_changes[i].Old));
        Apply();
    }

    private void Apply()
    {
        foreach (var (o, _, n) in _changes) o.Style = n;
    }
}
/// <summary>Generic single-property change, e.g. a rectangle's corner radius from a Context Bar field.</summary>
public sealed class SetPropertyCommand<TObj, TValue> : IDocumentCommand where TObj : DocObject
{
    private readonly (TObj Obj, TValue Old)[] _old;
    private readonly Action<TObj, TValue> _set;
    private readonly TValue _value;

    public SetPropertyCommand(string description, IEnumerable<TObj> objects, Func<TObj, TValue> get, Action<TObj, TValue> set, TValue value)
    {
        Description = description;
        _old = objects.Select(o => (o, get(o))).ToArray();
        _set = set;
        _value = value;
    }

    public string Description { get; }

    public void Do(Document doc)
    {
        foreach (var (o, _) in _old) _set(o, _value);
    }

    public void Undo(Document doc)
    {
        foreach (var (o, old) in _old) _set(o, old);
    }
}

/// <summary>
/// Moves one drawn object to a place in the drawing order of its layer (index 0 = bottom). A group cannot be moved this way: it has no place
/// of its own; see <see cref="StackOrderCommand"/> for the commands the user gives (to front, to back, forward, backward).
/// </summary>
public sealed class ReorderObjectCommand(DocObject obj, int newIndex) : IDocumentCommand
{
    private int _oldIndex = -1, _newIndex = -1;

    public string Description => "Change Order";

    public void Do(Document doc)
    {
        if (obj is GroupObject) throw new ArgumentException("A group has no place in the drawing order; move its members.", nameof(obj));
        var layer = obj.Layer ?? throw new InvalidOperationException($"{obj} is not in a layer.");
        var order = layer.Children.ToList();
        _oldIndex = order.IndexOf(obj);
        order.RemoveAt(_oldIndex);
        _newIndex = Math.Clamp(newIndex, 0, order.Count);
        order.Insert(_newIndex, obj);
        doc.SetDrawingOrder(layer, order);
    }

    public void Undo(Document doc)
    {
        var layer = obj.Layer!;
        var order = layer.Children.ToList();
        order.RemoveAt(_newIndex);
        order.Insert(_oldIndex, obj);
        doc.SetDrawingOrder(layer, order);
    }
}
