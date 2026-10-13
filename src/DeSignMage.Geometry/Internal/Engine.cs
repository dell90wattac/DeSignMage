using Clipper2ZLib;
using DeSignMage.Core.Geometry;
using CFill = Clipper2ZLib.FillRule;
using DFill = DeSignMage.Core.Geometry.FillRule;

namespace DeSignMage.Geometry.Internal;

/// <summary>One engine run: the grid, the source tags and the helpers shared by booleans and offsets.</summary>
internal sealed class Engine
{
    public readonly Grid Grid;
    public readonly TagTable Tags;
    /// <summary>Flattening tolerance actually used (points).</summary>
    public readonly double Tol;
    /// <summary>Geometric epsilon for merging and degenerate tests (points).</summary>
    public readonly double Eps;

    public Engine(Rect2 bounds, double requestedTol)
    {
        Grid = new Grid(bounds);
        Tags = new TagTable(Grid);
        double extent = Math.Max(bounds.Width, bounds.Height);
        double tol = requestedTol > 0 && double.IsFinite(requestedTol) ? requestedTol : 0.01;
        if (extent > 0) tol = Math.Min(tol, extent * 1e-3);       // tiny artwork still gets real curves
        Tol = Math.Max(tol, 16 * Grid.Unit);
        Eps = 4 * Grid.Unit;
    }

    public Clipper64 NewClipper() => new() { PreserveCollinear = true, ZCallback = Tags.OnIntersection };

    public static CFill ToClipper(DFill rule) => rule == DFill.EvenOdd ? CFill.EvenOdd : CFill.NonZero;

    /// <summary>Source subpaths of a path (degenerate segments dropped).</summary>
    public List<SourceLoop> Loops(PathGeometry path) => SourceLoops.From(path, Eps);

    /// <summary>
    /// Each path filled by its own rule (or <paramref name="ruleOverride"/>) and cleaned: a union per path, so loops of one
    /// path never cancel each other and the result is oriented (outer positive, holes negative). Open subpaths are skipped.
    /// </summary>
    public Paths64 Normalize(IEnumerable<PathGeometry> paths, DFill? ruleOverride, bool contourOnly = false)
    {
        var all = new Paths64();
        foreach (var path in paths)
        {
            var flat = new Paths64();
            foreach (var loop in Loops(path))
                if (loop.Closed) AddIfArea(flat, Tags.AddLoop(loop, Tol));
            if (flat.Count == 0) continue;
            var c = NewClipper();
            c.AddSubject(flat);
            var result = new Paths64();
            c.Execute(ClipType.Union, ToClipper(ruleOverride ?? path.FillRule), result);
            foreach (var p in result)
                if (!contourOnly || Clipper.IsPositive(p)) all.Add(p);
        }
        return all;
    }

    private static void AddIfArea(Paths64 into, Path64 p)
    {
        if (p.Count >= 3) into.Add(p);
    }

    /// <summary>Result loops of a tree, inner loops before the loops around them.</summary>
    public List<OutLoop> Rebuild(PolyPath64 tree, bool keepCurves, double minArea)
    {
        var loops = new List<OutLoop>();
        Visit(tree);
        return loops;

        void Visit(PolyPath64 node)
        {
            for (int i = 0; i < node.Count; i++) Visit(node[i]);
            if (node.Polygon is { Count: >= 3 } poly && LoopArea(poly) >= minArea &&
                Reconstruct.Loop(poly, Tags, Tol, Eps, keepCurves) is { } loop)
                loops.Add(loop);
        }
    }

    /// <summary>Absolute area of a grid loop in square points.</summary>
    public double LoopArea(Path64 p) => Math.Abs(Clipper.Area(p)) * Grid.Unit * Grid.Unit;

    /// <summary>A plain polygon (no tags of interest) as a closed source path.</summary>
    public static PathGeometry Polygon(params Vec2[] pts)
    {
        var b = new PathBuilder().MoveTo(pts[0]);
        for (int i = 1; i < pts.Length; i++) b.LineTo(pts[i]);
        return b.Close().Build();
    }
}
