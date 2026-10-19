using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>
/// The loops of one drawn object (a path, shape, text outline, or the shape parts of a combined object) in one place, with how they
/// nest. Used by the Arrange loop cleanup commands (Close All Loops, Remove All Inner Loops, Break Into Outer Loops).
/// <para>
/// Nesting is judged within the object (R14): a combined object's parts count together, a group's members never do.
/// </para>
/// </summary>
public sealed class LoopMap
{
    private LoopMap(List<VectorPiece> pieces, List<Affine2> spaces)
    {
        Pieces = pieces;
        Spaces = spaces;
        foreach (var (p, space) in pieces.Zip(spaces))
            for (int i = 0; i < p.Loops.Count; i++)
            {
                Index.Add((Piece: Pieces.IndexOf(p), Loop: i));
                InSpace.Add(PathEditing.Transformed(p.Loops[i], space));
            }
        (Depth, Parent) = PathEditing.LoopTree(InSpace);
    }

    /// <summary>The editable pieces: one for a simple object, one per shape part of a combined object.</summary>
    public List<VectorPiece> Pieces { get; }

    /// <summary>Per piece, the map from the piece's own space to the object's space (a combined object's part transform; otherwise identity).</summary>
    public List<Affine2> Spaces { get; }

    /// <summary>Every loop of every piece, in order: which piece and which of its loops.</summary>
    public List<(int Piece, int Loop)> Index { get; } = [];

    /// <summary>The same loops, all in the object's space (so loops of different parts can be compared).</summary>
    public List<Loop> InSpace { get; } = [];

    public int[] Depth { get; }
    public int[] Parent { get; }

    /// <summary>Text counts through its glyph outlines. Pictures, groups and objects without loops give null.</summary>
    public static LoopMap? Of(DocObject leaf)
    {
        List<VectorPiece> pieces;
        List<Affine2> spaces;
        if (leaf is TextObject t)
        {
            var g = t.GetLocalGeometry();
            if (g is null || g.IsEmpty) return null;
            pieces = [new VectorPiece(-1, t.GetWorldTransform(), PathEditing.ToLoops(g), g.FillRule)];
            spaces = [Affine2.Identity];
        }
        else if (OutlineView.IsEditable(leaf))
        {
            pieces = OutlineView.Pieces(leaf, Affine2.Identity);
            spaces = pieces.Select(p => p.PartIndex >= 0 && leaf is CombinedObject c ? c.Parts[p.PartIndex].Transform : Affine2.Identity).ToList();
        }
        else return null;
        return pieces.Count == 0 ? null : new LoopMap(pieces, spaces);
    }

    public int LoopCount => Index.Count;
    public bool HasOpenLoops => InSpace.Any(l => !l.Closed && l.Segments.Count > 0);
    public bool HasInnerLoops => Depth.Any(d => d >= 1);

    /// <summary>Outer loops: closed loops at even depth (the blue ones in the vector view) and every open loop.</summary>
    public IEnumerable<int> OuterLoops() => Enumerable.Range(0, LoopCount).Where(i => Depth[i] < 0 || Depth[i] % 2 == 0);
}

/// <summary>What the Arrange loop cleanup commands can do to one drawn object; shared by the session's can-execute checks and the commands.</summary>
public static class LoopCleanup
{
    public static bool HasOpenLoops(DocObject leaf) => leaf is not TextObject && LoopMap.Of(leaf) is { HasOpenLoops: true };

    /// <summary>Remove All Inner Loops applies when some loop lies inside another one (a hole, or an island inside a hole).</summary>
    public static bool HasInnerLoops(DocObject leaf) => LoopMap.Of(leaf) is { HasInnerLoops: true };

    /// <summary>How many separate objects Break Into Outer Loops would make: one per outer loop.</summary>
    public static int OuterLoopCount(DocObject leaf) => LoopMap.Of(leaf) is { } m ? m.OuterLoops().Count() : 0;

    public static bool CanBreakIntoOuterLoops(DocObject leaf) => OuterLoopCount(leaf) >= 2;

    /// <summary>The new geometry of each piece with every loop that lies inside another loop removed; null where a piece does not change.</summary>
    public static List<(int Piece, PathGeometry Geometry)> WithoutInnerLoops(LoopMap map)
    {
        var result = new List<(int, PathGeometry)>();
        for (int pi = 0; pi < map.Pieces.Count; pi++)
        {
            var piece = map.Pieces[pi];
            var keep = new List<Loop>();
            for (int k = 0; k < map.LoopCount; k++)
                if (map.Index[k].Piece == pi && map.Depth[k] < 1) keep.Add(piece.Loops[map.Index[k].Loop]);
            if (keep.Count != piece.Loops.Count) result.Add((pi, PathEditing.FromLoops(keep, piece.FillRule)));
        }
        return result;
    }

    /// <summary>
    /// The combined object <paramref name="combined"/> rebuilt after Remove All Inner Loops: the parts that were left with no loops at all (a
    /// small circle lying inside a big one) are dropped instead of staying behind as empty parts. Null when no part is left. Untouched parts,
    /// text and pictures are copied whole; a changed shape part becomes a plain path in the same place and style.
    /// </summary>
    public static CombinedObject? WithoutEmptyParts(CombinedObject combined, LoopMap map, List<(int Piece, PathGeometry Geometry)> changes)
    {
        var parts = new List<DocObject>();
        for (int i = 0; i < combined.Parts.Count; i++)
        {
            var part = combined.Parts[i];
            int piece = map.Pieces.FindIndex(p => p.PartIndex == i);
            var change = piece < 0 ? default : changes.FirstOrDefault(c => c.Piece == piece);
            if (change.Geometry is null) { parts.Add(part.Clone()); continue; }
            if (change.Geometry.IsEmpty) continue;
            parts.Add(new PathObject(change.Geometry)
            {
                Name = part.Name, Transform = part.Transform, Style = part.Style, Visible = part.Visible, Locked = part.Locked,
            });
        }
        if (parts.Count == 0) return null;
        return new CombinedObject(parts) { Name = combined.Name, Transform = combined.Transform, Visible = combined.Visible, Locked = combined.Locked };
    }

    /// <summary>The new geometry of each piece with every open loop closed; only the pieces that change.</summary>
    public static List<(int Piece, PathGeometry Geometry)> WithClosedLoops(LoopMap map)
    {
        var result = new List<(int, PathGeometry)>();
        for (int pi = 0; pi < map.Pieces.Count; pi++)
        {
            var piece = map.Pieces[pi];
            var loops = piece.Loops.Select(l => l.Clone()).ToList();
            if (PathEditing.CloseAll(loops) > 0) result.Add((pi, PathEditing.FromLoops(loops, piece.FillRule)));
        }
        return result;
    }
}

/// <summary>
/// Puts <paramref name="replacement"/> in the place of <paramref name="source"/> (same spot in the drawing order and the same group), or
/// just removes it when there is no replacement. Undo puts the original back.
/// </summary>
public sealed class ReplaceObjectCommand(string description, DocObject source, DocObject? replacement) : IDocumentCommand, IUndoMemory, IReplacesObjects
{
    private Removal? _removal;

    public IEnumerable<(DocObject Old, IReadOnlyList<DocObject> New)> Replacements => [(source, replacement is null ? [] : [replacement])];

    public string Description => description;
    public DocObject Source => source;
    public DocObject? Replacement => replacement;
    public long ApproximateBytes => UndoMemory.BytesOf(source) + (replacement is null ? 0 : UndoMemory.BytesOf(replacement));
    public IEnumerable<string> HeldAssetIds => UndoMemory.AssetsOf(source).Concat(replacement is null ? [] : UndoMemory.AssetsOf(replacement));

    public void Do(Document doc)
    {
        _removal = doc.Remove(source);
        if (replacement is not null) doc.Insert(_removal.Slot, replacement);
    }

    public void Undo(Document doc)
    {
        if (replacement is not null) doc.Remove(replacement);
        doc.Restore(_removal!);
    }
}

/// <summary>
/// Break Into Outer Loops (Arrange menu): the object is replaced by one object per outer loop, and each keeps the holes that lie
/// directly inside it (so letters keep their counters, unlike Break Into Loops, which splits every loop). An island inside a hole is an
/// outer loop of its own. Open loops are outer loops too.
/// <para>
/// A <see cref="CombinedObject"/> splits across its parts: each outer loop takes the style of the part it came from, and the holes inside
/// it come along even from other parts (nesting is judged across the whole object, like the vector view). Text and pictures that are parts of
/// a combined object are kept whole.
/// </para>
/// The new objects take the original's place in the stacking order, bottom to top in the original's loop order, and stay in its group.
/// Undo puts the original back.
/// </summary>
public sealed class BreakIntoOuterLoopsCommand : IDocumentCommand, IUndoMemory
{
    private readonly DocObject[] _pieces;
    private Removal? _removal;

    public BreakIntoOuterLoopsCommand(DocObject source)
    {
        if (!LoopCleanup.CanBreakIntoOuterLoops(source)) throw new ArgumentException("This object has fewer than two outer loops.", nameof(source));
        Source = source;
        _pieces = Split(source);
    }

    public DocObject Source { get; }

    /// <summary>The new objects, bottom to top.</summary>
    public IReadOnlyList<DocObject> Pieces => _pieces;

    public string Description => "Break Into Outer Loops";
    public long ApproximateBytes => UndoMemory.BytesOf(_pieces) + UndoMemory.BytesOf(Source);
    public IEnumerable<string> HeldAssetIds => UndoMemory.AssetsOf(_pieces).Concat(UndoMemory.AssetsOf(Source));

    private static DocObject[] Split(DocObject source)
    {
        var map = LoopMap.Of(source)!;
        var list = new List<DocObject>();
        int number = 0;
        foreach (int outer in map.OuterLoops())
        {
            var (pi, li) = map.Index[outer];
            var piece = map.Pieces[pi];
            var loops = new List<Loop> { piece.Loops[li].Clone() };
            // Holes: the loops whose innermost container is this loop, at the next depth.
            for (int k = 0; k < map.LoopCount; k++)
            {
                if (map.Parent[k] != outer || map.Depth[k] % 2 != 1) continue;
                var (hp, hl) = map.Index[k];
                loops.Add(hp == pi ? piece.Loops[hl].Clone() : PathEditing.Transformed(map.InSpace[k], map.Spaces[pi].Invert()));
            }
            var style = source is CombinedObject c && piece.PartIndex >= 0 ? c.Parts[piece.PartIndex].Style : source.Style;
            var transform = source is CombinedObject cc && piece.PartIndex >= 0 ? cc.Parts[piece.PartIndex].Transform * cc.Transform : source.Transform;
            list.Add(new PathObject(PathEditing.FromLoops(loops, loops.Count > 1 ? FillRule.EvenOdd : piece.FillRule))
            {
                Name = $"Loop {++number}",
                Transform = transform,
                Style = style,
                Visible = source.Visible,
                Locked = source.Locked,
            });
        }
        if (source is CombinedObject combined)
        {
            // Text and pictures inside a combined object have no loops to split: they are kept whole, like Break Into Loops does.
            for (int i = 0; i < combined.Parts.Count; i++)
            {
                var part = combined.Parts[i];
                if (map.Pieces.Any(p => p.PartIndex == i)) continue;
                var whole = part.Clone();
                whole.Transform = part.Transform * combined.Transform;
                whole.Visible = source.Visible;
                whole.Locked = source.Locked;
                list.Add(whole);
            }
        }
        return list.ToArray();
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
