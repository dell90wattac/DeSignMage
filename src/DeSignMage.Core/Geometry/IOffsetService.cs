namespace DeSignMage.Core.Geometry;

/// <summary>How outside corners of an offset are joined.</summary>
public enum CornerStyle : byte
{
    /// <summary>
    /// Rule: a corner sharper than <see cref="OffsetOptions.OutsideAngleThreshold"/> (OAT) gets a round arc, the
    /// others a sharp miter.
    /// </summary>
    Auto,
    /// <summary>Sharp miters everywhere (Miter button, OAT 0), limited by <see cref="OffsetOptions.MiterLimit"/>.</summary>
    Miter,
    /// <summary>Round arcs at every outside corner.</summary>
    Round,
    /// <summary>A straight cut across every outside corner.</summary>
    Bevel,
}

/// <summary>What an offset does with open subpaths (strokes).</summary>
public enum OpenPathMode : byte
{
    /// <summary>A closed outline around the stroke, 2 × |distance| wide, with <see cref="OffsetOptions.Caps"/> at the ends.</summary>
    AroundStroke,
    /// <summary>
    /// One parallel line at the distance, no caps (engine, parallel guides). Positive distances go to the screen-left
    /// of the direction of travel: the side that is outside for an outer (clockwise on screen) loop.
    /// </summary>
    OneSided,
    /// <summary>Open subpaths are left out of the result.</summary>
    Ignore,
}

/// <summary>End caps for <see cref="OpenPathMode.AroundStroke"/>.</summary>
public enum CapStyle : byte { Round, Butt, Square }

/// <summary>Offset options. Lengths in document units (points).</summary>
public sealed record OffsetOptions
{
    public static readonly OffsetOptions Default = new();

    public CornerStyle Corners { get; init; } = CornerStyle.Auto;

    /// <summary>
    /// OAT in degrees, for <see cref="CornerStyle.Auto"/>: a corner whose inside angle is smaller than this is rounded,
    /// the others are mitered. default 70 (= a round-point threshold of 110 = 180 − OAT); the Round button uses 120.
    /// </summary>
    public double OutsideAngleThreshold { get; init; } = 70;

    /// <summary>
    /// Longest allowed miter, as a multiple of the distance (from the corner to the miter tip). Longer miters are rounded
    /// instead. Default 20 (only reached by corners sharper than about 6°).
    /// </summary>
    public double MiterLimit { get; init; } = 20;

    public OpenPathMode OpenPaths { get; init; } = OpenPathMode.AroundStroke;

    public CapStyle Caps { get; init; } = CapStyle.Round;

    /// <summary>True: holes are dropped before and after the offset ("Contour only").</summary>
    public bool ContourOnly { get; init; }

    /// <summary>
    /// Largest deviation (points) of the result from the true offset. Arcs of the source and round corners come back as
    /// exact arcs; other curves are refitted within this tolerance. Default 0.01 pt.
    /// </summary>
    public double Tolerance { get; init; } = 0.01;

    /// <summary>
    /// Where the offset of a cubic is not an exact arc, refit it with cubics (true) or with arcs only (false, default,
    /// Output: lines and arcs only).
    /// </summary>
    public bool PreferCubics { get; init; }

    /// <summary>Fill rule override for the source (see <see cref="BooleanOptions.FillRule"/>). Null = the path's own.</summary>
    public FillRule? FillRule { get; init; }
}

/// <summary>
/// Offsets (Outline, Inline, Multiple Inline, Dynamic Outline, Cookie Cutter outline, shadow outlines). Positive distances
/// grow filled areas (holes shrink), negative ones shrink them (Inline). Results are clean regions in the same form as
/// <see cref="IBooleanService"/> results (open subpaths in <see cref="OpenPathMode.OneSided"/> stay open). Implemented by
/// <c>DeSignMage.Geometry</c> (Clipper2);
/// </summary>
public interface IOffsetService
{
    /// <summary>The shape offset by <paramref name="distance"/> points. Zero returns the shape cleaned (as a union).</summary>
    PathGeometry Offset(PathGeometry shape, double distance, OffsetOptions? options = null);

    /// <summary>
    /// Several offsets of the same source (2nd outline = 1st + 2nd amount, Multiple Inline), each measured from the
    /// original shape, in the order given.
    /// </summary>
    IReadOnlyList<PathGeometry> Offset(PathGeometry shape, IReadOnlyList<double> distances, OffsetOptions? options = null)
    {
        var result = new PathGeometry[distances.Count];
        for (int i = 0; i < result.Length; i++) result[i] = Offset(shape, distances[i], options);
        return result;
    }
}
