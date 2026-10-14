namespace DeSignMage.Core.Geometry;

/// <summary>
/// A shape made ready for fast offsets: flattened and cleaned once, so each offset of it afterwards (every pointer move of a drag, every keystroke in
/// the amount box) costs only the offset itself. Immutable and safe to use from any thread.
/// </summary>
public interface IOffsetPreviewSource
{
    /// <summary>
    /// The shape grown by <paramref name="distance"/> (negative: shrunk), as polylines. When <paramref name="second"/> is not 0 the result is the band between
    /// that offset and the one at <c>distance + second</c>.
    /// </summary>
    PathGeometry Offset(double distance, double second, CornerStyle corners, double outsideAngleThreshold);
}

/// <summary>
/// The quick offset for live previews (R31): the same growing and shrinking as <see cref="IOffsetService"/>, but on a flattened copy of the shape
/// at screen accuracy, with the corner rule approximated and no curve refit, so it takes a few milliseconds where the exact offset takes seconds on
/// big lettering. Only for what is drawn while the user is still choosing the amount; what gets made is always the exact offset.
/// </summary>
public interface IOffsetPreviewService
{
    /// <summary>
    /// Prepares <paramref name="shape"/> for offsets accurate to <paramref name="tolerance"/> points. Null when the shape cannot be offset this way
    /// (it has open paths): use the exact service for that one.
    /// </summary>
    IOffsetPreviewSource? Prepare(PathGeometry shape, double tolerance, bool contourOnly);
}
