namespace DeSignMage.Core.Geometry;

/// <summary>Which area a path boolean keeps.</summary>
public enum BooleanOp : byte
{
    /// <summary>Everything covered by any operand (Weld).</summary>
    Union,
    /// <summary>Only what is covered by the subjects and the clips (Common Weld).</summary>
    Intersect,
    /// <summary>Subjects minus clips (Cookie Cutter, Knife).</summary>
    Difference,
    /// <summary>Covered by the subjects or the clips but not both.</summary>
    Xor,
}

/// <summary>Options shared by every boolean. The defaults suit sign work in document units (points).</summary>
public sealed record BooleanOptions
{
    public static readonly BooleanOptions Default = new();

    /// <summary>
    /// Largest distance (points) between a curve and the polyline the engine works on. Curves that survive an operation
    /// are restored exactly, so this only bounds where cut points land on curves before they are refined. Default 0.01 pt.
    /// </summary>
    public double Tolerance { get; init; } = 0.01;

    /// <summary>
    /// How each operand's own loops are filled before the operation. Null (default) = each path's
    /// <see cref="PathGeometry.FillRule"/>. EvenOdd is "Force Loop Direction" (holes by nesting, whatever the directions).
    /// </summary>
    public FillRule? FillRule { get; init; }

    /// <summary>
    /// True (default): where the result follows an original arc or cubic, that curve (or the cut piece of it) is put back.
    /// False: the result is the flattened polyline (lines only).
    /// </summary>
    public bool KeepCurves { get; init; } = true;

    /// <summary>Result loops with a smaller area (square points) are dropped (slivers, specks). Default 0 = keep all.</summary>
    public double MinLoopArea { get; init; }
}

/// <summary>Knife options. Lengths in points.</summary>
public sealed record SplitOptions
{
    public static readonly SplitOptions Default = new();

    /// <summary>Gap mode: width of the blade, the strip removed along the line. default 0.002 in = 0.144 pt.</summary>
    public double Gap { get; init; } = 0.144;

    /// <summary>
    /// Overlap mode when set: no strip is removed; the two sides are cut so they overlap by this amount (half beyond the
    /// line on each side). The line is then always extended across the whole shape. default 0.25 in = 18 pt.
    /// </summary>
    public double? Overlap { get; init; }

    /// <summary>
    /// True: the line is extended across the whole shape (Simple Cut off). False (default, Simple Cut): only
    /// the dragged segment cuts, so a short line just notches the shape.
    /// </summary>
    public bool ExtendLine { get; init; }

    /// <summary>Boolean options used for the cut.</summary>
    public BooleanOptions Boolean { get; init; } = BooleanOptions.Default;
}

/// <summary>
/// Path booleans on closed regions (Weld, Common Weld, Cookie Cutter, Knife, offset cleanup). Works on any
/// <see cref="PathGeometry"/> (lines, cubics, arcs, several loops, holes). Results are new immutable paths:
/// clean (no self-intersections, no overlapping loops), outer loops with positive shoelace area in document space
/// (clockwise on screen, outer direction) and holes negative, inner loops listed before the loops around them,
/// <see cref="FillRule.NonZero"/>. Open subpaths are ignored (an open line has no area) unless they end where they start.
/// Implemented by <c>DeSignMage.Geometry</c> (Clipper2);
/// </summary>
public interface IBooleanService
{
    /// <summary>
    /// The general operation: <paramref name="op"/> between the union of <paramref name="subjects"/> and the union of
    /// <paramref name="clips"/>. Each operand is first filled by its own fill rule, so overlapping loops inside one
    /// operand never cancel out.
    /// </summary>
    PathGeometry Combine(BooleanOp op, IReadOnlyList<PathGeometry> subjects, IReadOnlyList<PathGeometry> clips,
        BooleanOptions? options = null);

    /// <summary>Weld: everything covered by any of the shapes.</summary>
    PathGeometry Union(IReadOnlyList<PathGeometry> shapes, BooleanOptions? options = null) =>
        Combine(BooleanOp.Union, shapes, [], options);

    /// <summary>The area common to <paramref name="a"/> and <paramref name="b"/>.</summary>
    PathGeometry Intersect(PathGeometry a, PathGeometry b, BooleanOptions? options = null) =>
        Combine(BooleanOp.Intersect, [a], [b], options);

    /// <summary>Common Weld with several shapes: the area covered by every one of them (empty when one is empty).</summary>
    PathGeometry IntersectAll(IReadOnlyList<PathGeometry> shapes, BooleanOptions? options = null)
    {
        if (shapes.Count == 0) return PathGeometry.EmptyPath;
        var acc = Combine(BooleanOp.Union, [shapes[0]], [], options);
        for (int i = 1; i < shapes.Count && !acc.IsEmpty; i++) acc = Combine(BooleanOp.Intersect, [acc], [shapes[i]], options);
        return acc;
    }

    /// <summary><paramref name="a"/> minus <paramref name="b"/> (Cookie Cutter: dough minus cutter).</summary>
    PathGeometry Difference(PathGeometry a, PathGeometry b, BooleanOptions? options = null) =>
        Combine(BooleanOp.Difference, [a], [b], options);

    /// <summary>The area covered by exactly one of <paramref name="a"/> and <paramref name="b"/>.</summary>
    PathGeometry Xor(PathGeometry a, PathGeometry b, BooleanOptions? options = null) =>
        Combine(BooleanOp.Xor, [a], [b], options);

    /// <summary>
    /// Splits a filled shape into its top-level pieces: every outer loop with the holes directly inside it (and the islands
    /// inside those holes go to their own pieces). Result order: as the outer loops appear, largest first.
    /// </summary>
    IReadOnlyList<PathGeometry> SplitIntoIslands(PathGeometry shape, BooleanOptions? options = null);

    /// <summary>
    /// Knife: cuts <paramref name="shape"/> along the line <paramref name="from"/> → <paramref name="to"/> and returns the
    /// pieces (top-level islands, see <see cref="SplitIntoIslands"/>), ordered across the line: pieces on the screen-left
    /// of from → to (y is down, so <c>Cross(to - from, p - from) &lt; 0</c>) first. A line that misses the shape returns
    /// it, cleaned, as the only piece.
    /// </summary>
    IReadOnlyList<PathGeometry> SplitByLine(PathGeometry shape, Vec2 from, Vec2 to, SplitOptions? options = null);
}
