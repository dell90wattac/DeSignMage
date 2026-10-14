using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>What counts as a "rectangle" for Convert to Rounded Rectangle.</summary>
public static class RectangleShapes
{
    /// <summary>
    /// True for a rectangle object, and for a path that is nothing but a closed, axis-aligned rectangle of four straight vectors in its own
    /// space (what a drawn or imported box is). <paramref name="box"/> is the rectangle in the object's local space.
    /// </summary>
    public static bool TryGet(DocObject o, out Rect2 box)
    {
        box = default;
        switch (o)
        {
            case RectangleObject r:
                box = new Rect2(0, 0, r.Width, r.Height);
                return true;
            case PathObject p:
                var loops = PathEditing.ToLoops(p.Geometry);
                if (loops.Count != 1 || !loops[0].Closed || loops[0].Segments.Count != 4 || loops[0].Segments.Any(s => s.Kind != SegmentKind.Line)) return false;
                var b = loops[0].Bounds();
                double tol = 1e-6 * Math.Max(1, Math.Max(b.Width, b.Height));
                if (b.Width < tol || b.Height < tol) return false;
                var corners = new HashSet<(bool Right, bool Bottom)>();
                for (int k = 0; k < 4; k++)
                {
                    var n = loops[0].Node(k);
                    bool left = Math.Abs(n.X - b.Left) <= tol, right = Math.Abs(n.X - b.Right) <= tol;
                    bool top = Math.Abs(n.Y - b.Top) <= tol, bottom = Math.Abs(n.Y - b.Bottom) <= tol;
                    if (!(left || right) || !(top || bottom)) return false;
                    corners.Add((right, bottom));
                }
                if (corners.Count != 4) return false;
                box = b;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Layout ▸ Shapes ▸ Convert to Rounded Rectangle: each rectangle gets rounded corners of the given radius (clamped to half of its short
/// side by the shape itself). A rectangle object just takes the radius; a path that is a plain box is replaced by a rectangle object in the
/// same place, group and style, so the radius stays editable. One undo step.
/// </summary>
public sealed class ConvertToRoundedRectangleCommand : IDocumentCommand, IUndoMemory
{
    private sealed record Item(DocObject Source, RectangleObject? Replacement, double OldRadius);

    private readonly List<Item> _items = [];
    private readonly double _radius;

    public ConvertToRoundedRectangleCommand(IEnumerable<DocObject> targets, double radius)
    {
        _radius = Math.Max(0, radius);
        foreach (var t in targets)
        {
            if (!RectangleShapes.TryGet(t, out var box)) continue;
            if (t is RectangleObject r) _items.Add(new Item(r, null, r.CornerRadius));
            else
                _items.Add(new Item(t, new RectangleObject(box.Width, box.Height, _radius)
                {
                    Name = t.Name,
                    Transform = Affine2.Translation(box.TopLeft) * t.Transform,
                    Style = t.Style,
                    Visible = t.Visible,
                    Locked = t.Locked,
                }, 0));
        }
    }

    public string Description => "Convert to Rounded Rectangle";
    public bool IsEmpty => _items.Count == 0;

    /// <summary>The rectangle objects that result (the same ones that were given, or their replacements), in target order.</summary>
    public IReadOnlyList<RectangleObject> Results => _items.Select(i => i.Replacement ?? (RectangleObject)i.Source).ToList();

    public long ApproximateBytes => UndoMemory.BytesOf(_items.Select(i => i.Source).ToArray());
    public IEnumerable<string> HeldAssetIds => UndoMemory.AssetsOf(_items.Select(i => i.Source).ToArray());

    private readonly Dictionary<DocObject, Removal> _removed = [];

    public void Do(Document doc)
    {
        _removed.Clear();
        foreach (var item in _items)
        {
            if (item.Replacement is null) ((RectangleObject)item.Source).CornerRadius = _radius;
            else
            {
                var removal = doc.Remove(item.Source);
                _removed[item.Source] = removal;
                doc.Insert(removal.Slot, item.Replacement);
            }
        }
    }

    public void Undo(Document doc)
    {
        foreach (var item in _items.AsEnumerable().Reverse())
        {
            if (item.Replacement is null) ((RectangleObject)item.Source).CornerRadius = item.OldRadius;
            else
            {
                doc.Remove(item.Replacement);
                doc.Restore(_removed[item.Source]);
            }
        }
    }
}
