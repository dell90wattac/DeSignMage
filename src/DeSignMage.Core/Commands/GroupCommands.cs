using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>
/// Wraps top-level objects in a new persistent group (rubber-band auto grouping, Make Group). Grouping nests: an existing group becomes a
/// member of the new one. <b>Nothing moves in the drawing order</b> (R22): a group is only the fact that its members belong together, so
/// whatever lies between the members, or over them, stays exactly where it is.
/// </summary>
public sealed class GroupObjectsCommand : IDocumentCommand
{
    private readonly DocObject[] _members;
    private (GroupObject? Group, int Index)[] _old = [];

    public GroupObjectsCommand(Document doc, IEnumerable<DocObject> objects)
    {
        _members = Stacking.BottomToTop(doc, objects).ToArray();
        if (_members.Length < 2) throw new ArgumentException("Grouping needs at least two objects.");
    }

    public GroupObject Group { get; } = new();
    public string Description => "Group";

    public void Do(Document doc)
    {
        var layer = _members[^1].Layer ?? doc.ActiveLayer;
        doc.Insert(new Slot(layer, layer.Children.Count), Group);   // an empty group: it has no place in the drawing order
        _old = new (GroupObject?, int)[_members.Length];
        for (int i = 0; i < _members.Length; i++) _old[i] = doc.SetGroup(_members[i], Group);   // members keep their transforms (group is identity)
    }

    public void Undo(Document doc)
    {
        for (int i = _members.Length - 1; i >= 0; i--) doc.SetGroup(_members[i], _old[i].Group, _old[i].Index);
        doc.Remove(Group);
    }
}

/// <summary>
/// Ungroup: removes exactly one level. The group's members go to the group's own group (or become top level) and keep their appearance (the
/// group's transform is folded into each of them). <c>{{c1,c2},c3}</c> → <c>{c1,c2}</c> + <c>c3</c>. Nothing moves in the drawing order (R22).
/// </summary>
public sealed class UngroupCommand(GroupObject group) : IDocumentCommand
{
    private DocObject[] _children = [];
    private Affine2[] _oldTransforms = [];
    private Removal? _shell;

    public GroupObject Group { get; } = group;
    public IReadOnlyList<DocObject> Children => _children;
    public string Description => "Ungroup";

    public void Do(Document doc)
    {
        _children = Group.Children.ToArray();
        _oldTransforms = _children.Select(c => c.Transform).ToArray();
        var outer = Group.Parent as GroupObject;
        int at = outer?.Children.ToList().IndexOf(Group) ?? -1;
        var g = Group.Transform;
        for (int i = 0; i < _children.Length; i++)
        {
            _children[i].Transform = _children[i].Transform * g;
            doc.SetGroup(_children[i], outer, outer is null ? -1 : at + i);
        }
        _shell = doc.Remove(Group);   // now empty
    }

    public void Undo(Document doc)
    {
        doc.Restore(_shell!);
        for (int i = 0; i < _children.Length; i++)
        {
            doc.SetGroup(_children[i], Group, i);
            _children[i].Transform = _oldTransforms[i];
        }
    }
}

/// <summary>
/// Combine: replaces top-level objects (typically one group) with a single <see cref="CombinedObject"/> whose parts
/// are copies of every solo piece inside them, in drawing order. Group levels are dropped (the user: combining
/// removes group history). Pieces look the same except where they overlap, which is knocked out
/// (even-odd, see <see cref="CombinedObject.GetLocalGeometry"/>). Undo restores the originals, groups included.
/// <para>
/// The combined object goes over the topmost of the originals (<see cref="Stacking.ReplacementSlot"/>).
/// </para>
/// </summary>
public sealed class CombineCommand : IDocumentCommand, IUndoMemory
{
    private readonly DocObject[] _members;

    public long ApproximateBytes => UndoMemory.BytesOf(Combined);
    public IEnumerable<string> HeldAssetIds => UndoMemory.AssetsOf(Combined);
    private List<Removal> _removed = [];

    public CombineCommand(Document doc, IEnumerable<DocObject> objects)
    {
        _members = Stacking.BottomToTop(doc, objects).ToArray();
        var parts = new List<DocObject>();
        foreach (var leaf in Stacking.LeavesBottomToTop(doc, _members)) CollectParts(leaf, parts);
        if (parts.Count < 2) throw new ArgumentException("Combine needs at least two pieces.");
        // Parts carry their document-space transform; the combined object starts with an identity transform.
        Combined = new CombinedObject(parts);
    }

    public CombinedObject Combined { get; }
    public string Description => "Combine";

    /// <summary>True if the objects contain at least two solo pieces.</summary>
    public static bool CanCombine(IEnumerable<DocObject> objects)
    {
        int count = 0;
        foreach (var o in objects) count += CountPieces(o);
        return count >= 2;
    }

    private static int CountPieces(DocObject o) => o switch
    {
        GroupObject g => g.Children.Sum(CountPieces),
        CombinedObject c => c.Parts.Count,
        _ => 1,
    };

    /// <summary>Appends detached copies of the solo pieces of a drawn object (a combined object gives its parts) with document-space transforms.</summary>
    private static void CollectParts(DocObject o, List<DocObject> parts)
    {
        if (o is CombinedObject c)
        {
            var cw = c.GetWorldTransform();
            foreach (var p in c.Parts)
            {
                var copy = p.Clone();
                copy.Transform = p.Transform * cw;
                parts.Add(copy);
            }
            return;
        }
        var piece = o.Clone();
        piece.Transform = o.GetWorldTransform();
        parts.Add(piece);
    }

    public void Do(Document doc)
    {
        var slot = Stacking.ReplacementSlot(doc, _members);
        _removed = Stacking.RemoveAll(doc, _members);
        doc.Insert(slot, Combined);
    }

    public void Undo(Document doc)
    {
        doc.Remove(Combined);
        Stacking.Restore(doc, _removed);
    }
}

/// <summary>
/// Break Apart (K): a <see cref="CombinedObject"/> becomes its original solo pieces again, ungrouped, in drawing
/// order, at the same place, with all their properties. Undo puts the combined object back.
/// </summary>
public sealed class BreakApartCommand(CombinedObject combined) : IDocumentCommand, IUndoMemory
{
    public long ApproximateBytes => UndoMemory.BytesOf(Combined);   // the pieces are its parts
    public IEnumerable<string> HeldAssetIds => UndoMemory.AssetsOf(Combined);

    private DocObject[] _pieces = [];
    private Affine2[] _partTransforms = [];
    private Removal? _removal;

    public CombinedObject Combined { get; } = combined;
    public IReadOnlyList<DocObject> Pieces => _pieces;
    public string Description => "Break Apart";

    public void Do(Document doc)
    {
        _pieces = Combined.Parts.ToArray();
        _partTransforms = _pieces.Select(p => p.Transform).ToArray();
        _removal = doc.Remove(Combined);
        var slot = _removal.Slot;
        for (int i = 0; i < _pieces.Length; i++)
        {
            _pieces[i].Transform = _partTransforms[i] * Combined.Transform;
            slot = doc.Insert(slot, _pieces[i]);
        }
    }

    public void Undo(Document doc)
    {
        for (int i = _pieces.Length - 1; i >= 0; i--)
        {
            doc.Remove(_pieces[i]);
            _pieces[i].Transform = _partTransforms[i];
        }
        doc.Restore(_removal!);
    }
}

/// <summary>
/// Break Apart (K) on a group: dissolves EVERY group level at once (Ungroup removes one), un-combining any combined object
/// found inside first, so only solo objects remain, each in the place of the object it came from in the drawing order. Multi-loop shapes
/// inside are left whole. The solo objects are copies of the originals (with all their properties), so undo simply puts
/// the untouched group tree back: the exact original structure.
/// </summary>
public sealed class BreakDownGroupCommand : IDocumentCommand, IUndoMemory
{
    private readonly List<(DocObject Leaf, DocObject[] Pieces)> _sets = [];
    private readonly DocObject[] _pieces;

    public long ApproximateBytes => UndoMemory.BytesOf(_pieces);
    public IEnumerable<string> HeldAssetIds => UndoMemory.AssetsOf(_pieces);
    private Removal? _removal;

    public BreakDownGroupCommand(GroupObject group)
    {
        Group = group;
        // Bottom to top, as drawn (the members of a group may not be next to each other in the drawing order).
        foreach (var leaf in group.Leaves().OrderBy(l => l.Layer?.IndexOf(l) ?? -1))
            _sets.Add((leaf, Flatten(leaf, group)));
        _pieces = _sets.SelectMany(s => s.Pieces).ToArray();
    }

    public GroupObject Group { get; }

    /// <summary>The solo objects, bottom to top.</summary>
    public IReadOnlyList<DocObject> Pieces => _pieces;

    public string Description => "Break Apart";

    /// <summary>The solo objects a drawn object becomes, in the space of the group's own parent.</summary>
    private static DocObject[] Flatten(DocObject leaf, GroupObject top)
    {
        // The transform up to and including the group being broken down; whatever is above it keeps applying to the pieces afterwards.
        Affine2 Outer(DocObject o)
        {
            var t = Affine2.Identity;
            for (var p = o.Parent as GroupObject; p is not null; p = p.Parent as GroupObject)
            {
                t = t * p.Transform;
                if (ReferenceEquals(p, top)) break;
            }
            return t;
        }

        var outer = Outer(leaf);
        if (leaf is CombinedObject c)
        {
            var pieces = new List<DocObject>();
            foreach (var part in c.Parts)
            {
                var piece = part.Clone();
                piece.Transform = part.Transform * c.Transform * outer;
                pieces.Add(piece);
            }
            return pieces.ToArray();
        }
        var solo = leaf.Clone();
        solo.Transform = leaf.Transform * outer;
        return [solo];
    }

    public void Do(Document doc)
    {
        _removal = doc.Remove(Group);
        var byLeaf = _sets.ToDictionary(s => s.Leaf, s => s.Pieces);
        var outer = _removal.OuterGroup;
        int member = _removal.OuterIndex;

        // Every original leaf is replaced where it stood: from the bottom up, each at its old index less the leaves still gone below it
        // plus the pieces already put in below it.
        var perLayer = new Dictionary<Layer, (int Gone, int Added)>();
        for (int i = _removal.Stack.Count - 1; i >= 0; i--)
        {
            var (leaf, layer, index) = _removal.Stack[i];
            var (gone, added) = perLayer.GetValueOrDefault(layer);
            var pieces = byLeaf[leaf];
            var next = doc.InsertMany(new Slot(layer, index - gone + added, outer, member), pieces);
            member = next.MemberIndex;
            perLayer[layer] = (gone + 1, added + pieces.Length);
        }
    }

    public void Undo(Document doc)
    {
        for (int i = _pieces.Length - 1; i >= 0; i--) doc.Remove(_pieces[i]);
        doc.Restore(_removal!);
    }
}
