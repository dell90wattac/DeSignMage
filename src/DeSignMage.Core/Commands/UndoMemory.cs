using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>
/// Optional on a command: tells <see cref="CommandHistory"/> how much memory the undo step keeps alive, so the history can stay
/// under its memory limit. Commands that keep whole objects, geometry or pictures should implement it; the rest count as a
/// few hundred bytes (<see cref="UndoMemory.SmallStepBytes"/>).
/// </summary>
public interface IUndoMemory
{
    /// <summary>Rough size in bytes of what the step holds on to besides the document's live objects (old geometry, removed objects, ...). Not the pictures.</summary>
    long ApproximateBytes { get; }

    /// <summary>
    /// The pictures (asset ids) the step needs to undo or redo. They are counted (once each, even when several steps share one) as undo
    /// memory unless a live object shows them, and they are released from the asset store when the last step needing them is dropped.
    /// </summary>
    IEnumerable<string> HeldAssetIds { get; }
}

/// <summary>Size estimates for undo steps. Approximate on purpose: the point is to notice a photo, not to count bytes.</summary>
public static class UndoMemory
{
    /// <summary>What a step that does not implement <see cref="IUndoMemory"/> counts as (a few changed numbers).</summary>
    public const long SmallStepBytes = 256;

    public static long BytesOf(IDocumentCommand c) => c is IUndoMemory m ? m.ApproximateBytes : SmallStepBytes;

    public static IEnumerable<string> AssetsOf(IDocumentCommand c) => c is IUndoMemory m ? m.HeldAssetIds : [];

    /// <summary>Bytes of a path: 16 per point plus the verbs.</summary>
    public static long BytesOf(PathGeometry g) => 64 + g.Verbs.Count + 16L * g.Points.Count;

    /// <summary>Bytes of an object and everything inside it (group members, combined parts). Pictures count only their frame; see <see cref="AssetsOf(DocObject)"/>.</summary>
    public static long BytesOf(DocObject o)
    {
        long bytes = 192;
        switch (o)
        {
            case GroupObject g:
                foreach (var c in g.Children) bytes += BytesOf(c);
                break;
            case CombinedObject c:
                foreach (var p in c.Parts) bytes += BytesOf(p);
                break;
            case PathObject p:
                bytes += BytesOf(p.Geometry);
                break;
            case TextObject t:
                bytes += 16L * t.Text.Length + 400L * t.Text.Length;   // the string, and the glyph outlines of its laid-out letters
                break;
        }
        return bytes;
    }

    public static long BytesOf(IEnumerable<DocObject> objects)
    {
        long bytes = 0;
        foreach (var o in objects) bytes += BytesOf(o);
        return bytes;
    }

    /// <summary>The pictures shown by <paramref name="o"/> or anything inside it.</summary>
    public static IEnumerable<string> AssetsOf(DocObject o)
    {
        switch (o)
        {
            case ImageObject img:
                yield return img.AssetId;
                break;
            case GroupObject g:
                foreach (var c in g.Children)
                    foreach (var id in AssetsOf(c)) yield return id;
                break;
            case CombinedObject c:
                foreach (var p in c.Parts)
                    foreach (var id in AssetsOf(p)) yield return id;
                break;
        }
    }

    public static IEnumerable<string> AssetsOf(IEnumerable<DocObject> objects) => objects.SelectMany(AssetsOf);
}
