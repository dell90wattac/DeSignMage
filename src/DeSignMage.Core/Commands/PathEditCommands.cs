using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>
/// Replaces the vectors of an object (or of one part of a combined object) with edited geometry, in the object's
/// local space. A rectangle or ellipse becomes a <see cref="PathObject"/> with the same name, transform and style
/// (its live parameters can't describe edited nodes); a <see cref="PathObject"/> just gets the new geometry.
/// </summary>
public sealed class SetVectorsCommand : IDocumentCommand, IUndoMemory, IReplacesObjects
{
    public IEnumerable<(DocObject Old, IReadOnlyList<DocObject> New)> Replacements =>
        _partIndex < 0 && _newPiece is not null ? [(_owner, [_newPiece])] : [];

    public long ApproximateBytes =>
        UndoMemory.BytesOf(_geometry) + (_oldGeometry is null ? 0 : UndoMemory.BytesOf(_oldGeometry)) +
        (_oldPiece is null ? 0 : UndoMemory.BytesOf(_oldPiece)) + (_newPiece is null ? 0 : UndoMemory.BytesOf(_newPiece));
    public IEnumerable<string> HeldAssetIds => [];

    private readonly DocObject _owner;          // top-level object in the document
    private readonly int _partIndex;            // -1 = the owner itself
    private readonly PathGeometry _geometry;
    private DocObject? _oldPiece, _newPiece;
    private PathGeometry? _oldGeometry;
    private Removal? _removal;

    public SetVectorsCommand(string description, DocObject owner, int partIndex, PathGeometry geometry)
    {
        Description = description;
        _owner = owner;
        _partIndex = partIndex;
        _geometry = geometry;
    }

    public string Description { get; }

    /// <summary>The object that now holds the edited vectors at the top level (changes when a shape is converted).</summary>
    public DocObject Result { get; private set; } = null!;

    public void Do(Document doc)
    {
        var piece = _partIndex < 0 ? _owner : ((CombinedObject)_owner).Parts[_partIndex];
        if (piece is PathObject p)
        {
            _oldGeometry ??= p.Geometry;
            p.Geometry = _geometry;
            Result = _owner;
            return;
        }

        _oldPiece ??= piece;
        _newPiece ??= new PathObject(_geometry)
        {
            Name = piece.Name, Transform = piece.Transform, Style = piece.Style, Visible = piece.Visible, Locked = piece.Locked,
        };
        if (_partIndex >= 0)
        {
            ((CombinedObject)_owner).ReplacePart(_partIndex, _newPiece);
            Result = _owner;
        }
        else
        {
            _removal = doc.Remove(_owner);
            doc.Insert(_removal.Slot, _newPiece);
            Result = _newPiece;
        }
    }

    public void Undo(Document doc)
    {
        if (_oldGeometry is not null)
        {
            var piece = _partIndex < 0 ? _owner : ((CombinedObject)_owner).Parts[_partIndex];
            ((PathObject)piece).Geometry = _oldGeometry;
            return;
        }
        if (_partIndex >= 0) ((CombinedObject)_owner).ReplacePart(_partIndex, _oldPiece!);
        else
        {
            doc.Remove(_newPiece!);
            doc.Restore(_removal!);
        }
    }

    /// <summary>The object the user sees before (<c>undone: true</c>) or after this edit.</summary>
    public DocObject ObjectAfter(bool undone) => undone ? _owner : Result;
}
