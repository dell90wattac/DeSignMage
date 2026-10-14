using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>
/// Break Into Loops (Arrange menu): every loop (closed or open subpath) of a graphic becomes its own object, so each can be
/// moved or colored separately. Holes become separate filled shapes of their own (letters lose their counters).
/// <list type="bullet">
/// <item>A <see cref="PathObject"/> with at least two loops is replaced by one <see cref="PathObject"/> per loop.</item>
/// <item>A <see cref="CombinedObject"/> is replaced by the loops of all its parts, each keeping its own part's style.
/// (Break Apart gives the parts back whole; this goes one step further.)</item>
/// </list>
/// The new objects take the original's place in the stacking order, bottom to top in the original's loop order, and look the
/// same as before except where holes now get filled. Undo puts the original back. Pieces are named "Loop 1", "Loop 2", ...
/// </summary>
public sealed class BreakIntoLoopsCommand : IDocumentCommand, IUndoMemory
{
    private readonly DocObject[] _pieces;

    public long ApproximateBytes => UndoMemory.BytesOf(_pieces) + UndoMemory.BytesOf(Source);
    public IEnumerable<string> HeldAssetIds => UndoMemory.AssetsOf(_pieces).Concat(UndoMemory.AssetsOf(Source));
    private Removal? _removal;

    public BreakIntoLoopsCommand(DocObject source)
    {
        if (!CanBreak(source)) throw new ArgumentException("This object has nothing to break into loops.", nameof(source));
        Source = source;
        _pieces = Split(source);
    }

    public DocObject Source { get; }

    /// <summary>The new objects, bottom to top.</summary>
    public IReadOnlyList<DocObject> Pieces => _pieces;

    public string Description => "Break Into Loops";

    /// <summary>True for a path with two or more loops, and for a combined object with at least two loops in total.</summary>
    public static bool CanBreak(DocObject o) => o switch
    {
        PathObject p => PathEditing.ToLoops(p.Geometry).Count >= 2,
        CombinedObject c => c.Parts.Sum(LoopCount) >= 2,
        _ => false,
    };

    /// <summary>Shapes whose outline is real geometry. Text and pictures only have a bounding box, so they are never split.</summary>
    private static bool HasLoops(DocObject o) => Editing.OutlineView.HasShape(o);

    private static int LoopCount(DocObject part) => HasLoops(part) && part.GetLocalGeometry() is { } g ? PathEditing.ToLoops(g).Count : 0;

    private static DocObject[] Split(DocObject source)
    {
        var pieces = new List<DocObject>();
        int loopNumber = 0;
        switch (source)
        {
            case PathObject p:
                AddLoops(pieces, p.Geometry, p.Transform, p.Style, ref loopNumber);
                break;
            case CombinedObject c:
                foreach (var part in c.Parts)
                {
                    if (HasLoops(part))
                    {
                        AddLoops(pieces, part.GetLocalGeometry()!, part.Transform * c.Transform, part.Style, ref loopNumber);
                    }
                    else
                    {
                        var whole = part.Clone();               // text, pictures: kept as they are, with the combined object's transform folded in
                        whole.Transform = part.Transform * c.Transform;
                        pieces.Add(whole);
                    }
                }
                break;
        }
        foreach (var piece in pieces)
        {
            piece.Visible = source.Visible;
            piece.Locked = source.Locked;
        }
        return pieces.ToArray();
    }

    private static void AddLoops(List<DocObject> into, PathGeometry geometry, Affine2 transform, ObjectStyle style, ref int loopNumber)
    {
        foreach (var loop in PathEditing.ToLoops(geometry))
        {
            into.Add(new PathObject(PathEditing.FromLoops([loop], geometry.FillRule))
            {
                Name = $"Loop {++loopNumber}",
                Transform = transform,
                Style = style,
            });
        }
    }

    public void Do(Document doc)
    {
        _removal = doc.Remove(Source);
        var slot = _removal.Slot;
        foreach (var piece in _pieces) slot = doc.Insert(slot, piece);
    }

    public void Undo(Document doc)
    {
        for (int i = _pieces.Length - 1; i >= 0; i--) doc.Remove(_pieces[i]);
        doc.Restore(_removal!);
    }
}
