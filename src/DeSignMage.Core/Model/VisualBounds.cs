using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Model;

/// <summary>
/// The area an object actually paints: its geometry plus half its stroke width, and for a soft shadow or glow the blur's reach. Exports
/// crop to it and Size to Fit grows the page to cover it, so nothing that is drawn is cut off.
/// </summary>
public static class VisualBounds
{
    /// <summary>Bounds of what an object paints: its geometry plus half its stroke width (round/square caps and long miters are not counted).</summary>
    public static Rect2 Of(DocObject o)
    {
        switch (o)
        {
            case GroupObject g:
            {
                var r = Rect2.Empty;
                foreach (var c in g.Children) if (c.Visible) r = r.Union(Of(c));
                return r;
            }
            case CombinedObject c:
            {
                var world = c.GetWorldTransform();
                var r = Rect2.Empty;
                foreach (var p in c.Parts)
                    if (p.Visible && p.GetLocalGeometry() is { } g)
                        r = r.Union(g.Transform(p.Transform * world).GetBounds().Inflate(p.Style.StrokeOutset));
                return r;
            }
            default:
                // a soft shadow or glow reaches about three sigma beyond its outline (a feather only fades inward)
                var bounds = o.GetWorldBounds().Inflate(o.Style.StrokeOutset);
                if (o.Style.Blur is { } blur) bounds = bounds.Inflate(3 * blur.Sigma * Math.Sqrt(Math.Abs(o.GetWorldTransform().Determinant)));
                return bounds;
        }
    }

    /// <summary>Everything the document draws (shown objects on visible layers), including strokes and soft edges. Empty for an empty design. Guides do not count.</summary>
    public static Rect2 OfDocument(Document doc)
    {
        var r = Rect2.Empty;
        foreach (var layer in doc.Layers)
            if (layer.Visible)
                foreach (var o in layer.Children)
                    if (o.IsShown) r = r.Union(Of(o));
        return r;
    }
}
