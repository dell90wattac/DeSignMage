using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Vectorize;

/// <summary>
/// Image ▸ Vectorize: puts the traced parts into the document as one <see cref="GroupObject"/> of
/// <see cref="PathObject"/>s (bottom to top; the parts go on top of the drawing order, the group is membership only). One undo step removes the whole group.
/// </summary>
public sealed class InsertVectorGroupCommand : IDocumentCommand, IUndoMemory
{
    private readonly IObjectContainer _container;
    private readonly GroupObject _group = new() { Name = "Vectorized Image" };
    private readonly DocObject[] _parts;

    public long ApproximateBytes => UndoMemory.BytesOf(_parts);
    public IEnumerable<string> HeldAssetIds => [];

    /// <param name="result">Traced parts in source-image pixels.</param>
    /// <param name="placement">Maps source-image pixels to document points (position and size on the page).</param>
    /// <param name="strokeWidthPoints">Outline stroke width in points, for outline results.</param>
    public InsertVectorGroupCommand(IObjectContainer container, VectorizeResult result, Affine2 placement, double strokeWidthPoints = 0.5)
    {
        _container = container;
        _group.Transform = Affine2.Identity;
        int i = 0;
        _parts = result.Shapes.Select(s =>
        {
            var style = new ObjectStyle(
                s.Fill is { } f ? new SolidPaint(f) : null,
                s.Stroke is { } st ? StrokeStyle.Solid(st, strokeWidthPoints) : null);
            var part = new PathObject(s.Path.Transform(placement)) { Style = style, Name = $"Part {++i}" };
            return (DocObject)part;
        }).ToArray();
    }

    public int PartCount => _parts.Length;
    public GroupObject Group => _group;
    public string Description => "Vectorize";

    public void Do(Document doc)
    {
        if (_container is Layer layer)
        {
            // A Vectorize result is a new item: it goes to the bottom of the layer (R22, the user's rule for new items), in one step.
            if (_group.Children.Count == 0)
                foreach (var p in _parts) _group.AddDetached(p);
            doc.Insert(new Slot(layer, Stacking.NewItemIndex), _group);
            return;
        }
        doc.Insert(_container, -1, _group);
        foreach (var p in _parts) doc.Insert(_group, -1, p);
    }

    public void Undo(Document doc) => doc.Remove(_group);   // takes its parts with it
}
