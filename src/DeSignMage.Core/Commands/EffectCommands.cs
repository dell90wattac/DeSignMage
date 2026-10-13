using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>
/// Weld / Common Weld: removes the original top-level objects and puts the result objects over the topmost original
/// (<see cref="Stacking.ReplacementSlot"/>). Undo takes the results out and puts the very same original
/// objects back at their old places.
/// </summary>
public sealed class ReplaceObjectsCommand : IDocumentCommand, IUndoMemory
{
    public long ApproximateBytes => UndoMemory.BytesOf(_originals) + UndoMemory.BytesOf(_results);
    public IEnumerable<string> HeldAssetIds => UndoMemory.AssetsOf(_originals).Concat(UndoMemory.AssetsOf(_results));

    private readonly DocObject[] _originals;
    private readonly DocObject[] _results;
    private List<Removal> _removed = [];

    public ReplaceObjectsCommand(Document doc, IEnumerable<DocObject> originals, IEnumerable<DocObject> results, string description)
    {
        _originals = Stacking.BottomToTop(doc, originals).ToArray();
        _results = results.ToArray();
        if (_originals.Length == 0) throw new ArgumentException("Nothing to replace.");
        Description = description;
    }

    public string Description { get; }

    public void Do(Document doc)
    {
        var slot = Stacking.ReplacementSlot(doc, _originals);
        _removed = Stacking.RemoveAll(doc, _originals);
        foreach (var r in _results) slot = doc.Insert(slot, r);
    }

    public void Undo(Document doc)
    {
        for (int i = _results.Length - 1; i >= 0; i--) doc.Remove(_results[i]);
        Stacking.Restore(doc, _removed);
    }
}

/// <summary>
/// Takes one object out of the document (and its group) and puts <paramref name="replacements"/> in its place, in order
/// (none = the object is just removed). Cookie Cutter, Knife and "Leave Original off" use it. Undo puts the same object back.
/// </summary>
public sealed class SwapObjectCommand(DocObject original, IReadOnlyList<DocObject> replacements, string description = "Replace") : IDocumentCommand, IUndoMemory
{
    private Removal? _removal;

    public long ApproximateBytes => UndoMemory.BytesOf(original) + UndoMemory.BytesOf(replacements);
    public IEnumerable<string> HeldAssetIds => UndoMemory.AssetsOf(original).Concat(UndoMemory.AssetsOf(replacements));

    public string Description { get; } = description;

    public void Do(Document doc)
    {
        _removal = doc.Remove(original);
        var slot = _removal.Slot;
        foreach (var r in replacements) slot = doc.Insert(slot, r);
    }

    public void Undo(Document doc)
    {
        for (int i = replacements.Count - 1; i >= 0; i--) doc.Remove(replacements[i]);
        doc.Restore(_removal!);
    }
}
