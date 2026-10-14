using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

/// <summary>One editable vector set: an object, or one shape part of a combined object, with its loops in local space.</summary>
public sealed class VectorPiece(int partIndex, Affine2 world, List<Loop> loops, FillRule fillRule)
{
    /// <summary>Index in <see cref="CombinedObject.Parts"/>, or -1 for the object itself.</summary>
    public int PartIndex { get; } = partIndex;
    public Affine2 World { get; } = world;
    public List<Loop> Loops { get; } = loops;
    public FillRule FillRule { get; } = fillRule;
}

/// <summary>Vectors of objects for vector mode and the outline ("no fill colors") view.</summary>
public static class OutlineView
{
    /// <summary>Objects whose vectors can be edited: shapes, paths and combined objects (not groups, text or images).</summary>
    public static bool IsEditable(DocObject o) => o switch
    {
        CombinedObject c => c.Parts.Any(HasShape),
        _ => HasShape(o),
    };

    /// <summary>Objects that are a vector shape of their own: paths, rectangles, ellipses, polygons and arrows.</summary>
    public static bool HasShape(DocObject o) => o is PathObject or RectangleObject or EllipseObject or PolygonObject or ArrowObject;

    /// <summary>Editable pieces of <paramref name="o"/> (one for a simple object, one per shape part of a combined object).</summary>
    public static List<VectorPiece> Pieces(DocObject o, Affine2 outer)
    {
        var world = o.GetWorldTransform() * outer;
        var list = new List<VectorPiece>();
        if (o is CombinedObject c)
        {
            for (int i = 0; i < c.Parts.Count; i++)
                if (HasShape(c.Parts[i]) && c.Parts[i].GetLocalGeometry() is { } g)
                    list.Add(new VectorPiece(i, c.Parts[i].Transform * world, PathEditing.ToLoops(g), g.FillRule));
        }
        else if (HasShape(o) && o.GetLocalGeometry() is { } g)
        {
            list.Add(new VectorPiece(-1, world, PathEditing.ToLoops(g), g.FillRule));
        }
        return list;
    }

    /// <summary>
    /// Every loop of the pieces in document coordinates, classified border / interior / open across all pieces
    /// together (a combined object is one shape).
    /// </summary>
    public static List<OutlineLoop> Loops(IReadOnlyList<VectorPiece> pieces)
    {
        var world = new List<Loop>();
        var paths = new List<PathGeometry>();
        foreach (var piece in pieces)
            foreach (var l in piece.Loops)
            {
                var g = l.ToGeometry().Transform(piece.World);
                var wl = PathEditing.ToLoops(g);
                if (wl.Count == 0) continue;
                world.Add(wl[0]);
                paths.Add(g);
            }
        var kinds = PathEditing.Classify(world);
        return paths.Select((p, i) => new OutlineLoop(p, kinds[i])).ToList();
    }

    public static List<OutlineLoop> Loops(DocObject o, Affine2 outer) => Loops(Pieces(o, outer));
}
