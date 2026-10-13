using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Model;

/// <summary>Geometric picking in document coordinates.</summary>
public static class HitTesting
{
    /// <summary>
    /// Click-to-select (requirement R10): the top-level object a click at <paramref name="p"/> selects, or null.
    /// <list type="number">
    /// <item><b>Outline wins from any depth.</b> The object whose outline (path edges, glyph outlines, image frame, parts of a
    /// combined object; a stroke counts as part of the outline) is nearest, if within <paramref name="tolerance"/>; ties go to the topmost.</item>
    /// <item>Otherwise the topmost object whose enclosed <b>area</b> contains the point, painted or not. Open paths enclose
    /// nothing (unless they are filled, which paints an area); live text and images enclose their whole box.</item>
    /// <item>A hit member resolves to its top-level group, because only top-level objects are returned.</item>
    /// </list>
    /// <paramref name="tolerance"/> is in document units (convert from screen pixels with the zoom).
    /// </summary>
    public static DocObject? HitTest(Document doc, Vec2 p, double tolerance)
    {
        double flat = FlattenTolerance(tolerance);
        DocObject? best = null;
        double bestDist = double.PositiveInfinity;
        foreach (var o in TopDown(doc))
        {
            double d = OutlineDistance(o, p, tolerance, flat, Affine2.Identity);
            if (d <= tolerance && d < bestDist - 1e-9) { best = o; bestDist = d; }   // strictly nearer; ties keep the topmost
        }
        if (best is not null) return best.TopLevel;
        foreach (var o in TopDown(doc))
            if (AreaHit(o, p, flat, Affine2.Identity)) return o.TopLevel;
        return null;
    }

    /// <summary>
    /// The drawn objects that can be clicked, topmost first, in the drawing order of the layers: shown (no hidden group around them) and not
    /// locked (the top-level object they belong to, a group as a whole, is what is locked). Members of a group are drawn wherever the
    /// drawing order puts them, so they are tested one by one and the top-level object of the one that is hit is the answer.
    /// </summary>
    private static IEnumerable<DocObject> TopDown(Document doc)
    {
        for (int li = doc.Layers.Count - 1; li >= 0; li--)
        {
            var layer = doc.Layers[li];
            if (!layer.Visible || layer.Locked) continue;
            for (int i = layer.Children.Count - 1; i >= 0; i--)
            {
                var o = layer.Children[i];
                if (o.Parent is not GroupObject) { if (o.Visible && !o.Locked) yield return o; }   // not in a group: the common case
                else if (o.IsShown && !o.TopLevel.Locked) yield return o;
            }
        }
    }

    private static double FlattenTolerance(double tolerance) => Math.Max(tolerance / 4, 0.01);

    /// <summary>
    /// Distance from <paramref name="p"/> to the nearest outline inside <paramref name="o"/> (0 on a stroke), or infinity when it
    /// is farther than <paramref name="tolerance"/>. Group members and combined parts count as the object.
    /// </summary>
    private static double OutlineDistance(DocObject o, Vec2 p, double tolerance, double flat, Affine2 outer)
    {
        if (!o.Visible) return double.PositiveInfinity;
        double best = double.PositiveInfinity;
        switch (o)
        {
            case GroupObject g:
                foreach (var c in g.Children) best = Math.Min(best, OutlineDistance(c, p, tolerance, flat, outer));
                return best;
            case CombinedObject combined:
            {
                var cw = combined.GetWorldTransform() * outer;
                foreach (var part in combined.Parts) best = Math.Min(best, OutlineDistance(part, p, tolerance, flat, cw));
                return best;
            }
        }

        double outset = o.Style.StrokeOutset;
        double reach = outset + tolerance;
        // First the object's cached bounds: rejects almost everything without building any geometry (a rectangle or an ellipse
        // makes its path anew on every GetLocalGeometry, which dominated hit testing a document with thousands of shapes).
        if (outer.IsIdentity && !o.GetWorldBounds().Inflate(reach).Contains(p)) return best;
        var local = OutlineGeometry(o);
        if (local is null) return best;
        var world = o.GetWorldTransform() * outer;
        // The transformed local box first; then the distance, measured without building a moved copy of the outline (only the curves
        // near the point are looked at, so a text of thousands of nodes costs about as much as a rectangle).
        if (!local.GetBounds().Transform(world).Inflate(reach).Contains(p)) return best;
        double d = Math.Max(0, local.DistanceToOutline(p, flat, world, reach) - outset);
        return d <= tolerance ? d : best;
    }

    /// <summary>The outline that counts for the object (local space): its geometry, or the line box of text that draws nothing.</summary>
    private static PathGeometry? OutlineGeometry(DocObject o)
    {
        var g = o.GetLocalGeometry();
        if (o is TextObject t && (g is null || g.IsEmpty)) return PathGeometry.Rectangle(t.GetPickFrame());
        return g is { IsEmpty: false } ? g : null;
    }

    /// <summary>True when the point lies inside the area the object encloses, whether or not that area is painted.</summary>
    private static bool AreaHit(DocObject o, Vec2 p, double flat, Affine2 outer)
    {
        if (!o.Visible) return false;
        switch (o)
        {
            case GroupObject g:
                for (int i = g.Children.Count - 1; i >= 0; i--)
                    if (AreaHit(g.Children[i], p, flat, outer)) return true;
                return false;
            case CombinedObject combined:
            {
                // The even-odd region of all pieces: overlaps are knocked out, as drawn.
                var cw = combined.GetWorldTransform() * outer;
                bool anyFill = combined.Parts.Any(part => part.Style.Fill is not null);
                return combined.GetLocalGeometry().Contains(p, flat, anyFill, cw);
            }
            case TextObject text:
                // Live text is picked anywhere in its line box, not only on the glyph shapes.
                return PathGeometry.Rectangle(text.GetPickFrame()).Transform(o.GetWorldTransform() * outer).Contains(p, flat);
        }

        if (outer.IsIdentity && !o.GetWorldBounds().Contains(p)) return false;   // cached bounds first, see OutlineDistance
        var local = o.GetLocalGeometry();
        if (local is null) return false;
        var world = o.GetWorldTransform() * outer;
        if (!local.GetBounds().Transform(world).Contains(p)) return false;
        // An open path encloses nothing, unless its fill is painted (images always fill their frame).
        bool openCounts = o.Style.Fill is not null || o is ImageObject;
        return local.Contains(p, flat, openCounts, world);
    }

    /// <summary>
    /// The old "what is painted under the pointer" pick: the top-most object whose fill or stroke is hit; an unfilled shape only
    /// near its stroke. For tools that act on visible things (text editing, vector editing, the eyedropper).
    /// </summary>
    public static DocObject? HitPainted(Document doc, Vec2 p, double tolerance)
    {
        foreach (var o in TopDown(doc))
            if (Hits(o, p, tolerance)) return o.TopLevel;
        return null;
    }

    public static bool Hits(DocObject o, Vec2 p, double tolerance) => Hits(o, p, tolerance, Affine2.Identity);

    /// <summary>
    /// The painted object under <paramref name="p"/> inside the top-level hit <paramref name="top"/>: the member of a group (at any
    /// depth) or the part of a combined object that is hit, else the top-most one. A plain object is its own leaf.
    /// </summary>
    public static DocObject HitLeaf(DocObject top, Vec2 p, double tolerance)
    {
        switch (top)
        {
            case GroupObject g:
            {
                // The members as they are drawn, topmost first (they may be anywhere in the drawing order, not next to each other).
                var drawn = g.Leaves().OrderByDescending(l => l.Layer?.IndexOf(l) ?? -1).ToList();
                foreach (var l in drawn)
                    if (l.IsShown && Hits(l, p, tolerance)) return HitLeaf(l, p, tolerance);
                return drawn.Count > 0 ? HitLeaf(drawn[0], p, tolerance) : top;
            }
            case CombinedObject c:
            {
                var cw = c.GetWorldTransform();
                for (int i = c.Parts.Count - 1; i >= 0; i--)
                    if (Hits(c.Parts[i], p, tolerance, cw)) return c.Parts[i];
                return c.Parts[^1];
            }
            default:
                return top;
        }
    }

    /// <param name="outer">Extra transform after the object's own world transform (parts of a combined object).</param>
    private static bool Hits(DocObject o, Vec2 p, double tolerance, Affine2 outer)
    {
        if (!o.Visible) return false;
        if (o is GroupObject g)
        {
            for (int i = g.Children.Count - 1; i >= 0; i--)
                if (Hits(g.Children[i], p, tolerance, outer)) return true;
            return false;
        }

        if (o is CombinedObject combined)
        {
            // Filled area = the even-odd region of all pieces (overlaps are knocked out); outlines hit as usual.
            var cw = combined.GetWorldTransform() * outer;
            bool anyFill = combined.Parts.Any(part => part.Style.Fill is not null || part is ImageObject or TextObject);
            if (anyFill && combined.GetLocalGeometry().Transform(cw).Contains(p, Math.Max(tolerance / 4, 0.01))) return true;
            foreach (var part in combined.Parts)
            {
                if (part.GetLocalGeometry() is not { } partGeometry) continue;
                var partWorld = partGeometry.Transform(part.Transform * cw);
                double outset = part.Style.StrokeOutset + tolerance;
                if (partWorld.GetBounds().Inflate(outset).Contains(p) &&
                    partWorld.DistanceToOutline(p, Math.Max(tolerance / 4, 0.01)) <= outset) return true;
            }
            return false;
        }

        var world = o.GetWorldTransform() * outer;
        if (o is TextObject text)
        {
            // Live text is picked anywhere in its line box, not only on the glyph shapes (small letters are hard to hit).
            return HitsPath(PathGeometry.Rectangle(text.GetPickFrame()).Transform(world), o.Style, solid: true, p, tolerance);
        }
        var local = o.GetLocalGeometry();
        if (local is null) return false;
        // Cheap rejection before transforming the geometry (HitsPath checks bounds itself for combined parts).
        if (outer.IsIdentity && !o.GetWorldBounds().Inflate(o.Style.StrokeOutset + tolerance).Contains(p)) return false;

        // Images and text are picked anywhere inside their frame; shapes only where painted.
        bool solid = o is ImageObject or TextObject || o.Style.Fill is not null;
        return HitsPath(local.Transform(world), o.Style, solid, p, tolerance);
    }

    private static bool HitsPath(PathGeometry world, ObjectStyle style, bool solid, Vec2 p, double tolerance)
    {
        // Stroke width is in document units and does not scale with the object's transform.
        double outset = style.StrokeOutset + tolerance;
        if (!world.GetBounds().Inflate(outset).Contains(p)) return false;
        double flat = Math.Max(tolerance / 4, 0.01);
        if (solid && world.Contains(p, flat)) return true;
        return world.DistanceToOutline(p, flat) <= outset;
    }

    /// <summary>Top-level objects whose bounds lie entirely inside <paramref name="area"/> (marquee selection). A group is one object, listed once.</summary>
    public static IEnumerable<DocObject> EnclosedBy(Document doc, Rect2 area)
    {
        var seen = new HashSet<DocObject>();
        foreach (var layer in doc.Layers)
        {
            if (!layer.Visible || layer.Locked) continue;
            foreach (var leaf in layer.Children)
            {
                var o = leaf.TopLevel;
                if (!seen.Add(o)) continue;
                if (o.Visible && !o.Locked && area.Contains(o.GetWorldBounds())) yield return o;
            }
        }
    }
}
