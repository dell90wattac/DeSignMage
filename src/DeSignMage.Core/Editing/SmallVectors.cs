using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

/// <summary>
/// What counts as a "small vector": a vector whose two end points are closer than a threshold, measured on the page (so it follows the
/// object's scale). The distance is the straight chord from the previous node to the node, whatever the vector's kind (a long, tight arc or
/// curve with close end points is small). Small vectors are not drawn differently in Vector Edit; they appear only in the Info Line's count
/// and in View ▸ Highlight Small Vectors, which marks the end of each one. The threshold is one setting for the whole program (Tools ▸
/// Options ▸ Vector editing); the shell loads it at startup and writes it when it changes.
/// </summary>
public static class SmallVectors
{
    /// <summary>The default threshold: a quarter inch.</summary>
    public static readonly double DefaultPoints = Units.FromInches(0.25);

    /// <summary>The smallest and largest threshold the setting accepts, in inches.</summary>
    public const double MinInches = 0.01, MaxInches = 5;

    /// <summary>A vector whose chord is shorter than this many points (on the page) is small.</summary>
    public static double ThresholdPoints { get; set; } = DefaultPoints;

    public static double ThresholdInches => Units.ToInches(ThresholdPoints);

    /// <summary>Sets the threshold in inches, kept between <see cref="MinInches"/> and <see cref="MaxInches"/>. Returns the value that was applied.</summary>
    public static double SetInches(double inches)
    {
        double clamped = Math.Clamp(double.IsFinite(inches) ? inches : 0.25, MinInches, MaxInches);
        ThresholdPoints = Units.FromInches(clamped);
        return clamped;
    }

    /// <summary>The straight distance between the two ends of vector <paramref name="segment"/>, in points on the page.</summary>
    public static double Chord(Loop loop, int segment, double scale) =>
        Vec2.Distance(loop.SegmentStart(segment), loop.Segments[segment].End) * scale;

    /// <summary>True if vector <paramref name="segment"/> of <paramref name="loop"/> is small when the loop's space is scaled by <paramref name="scale"/> on the page.</summary>
    public static bool IsSmall(Loop loop, int segment, double scale) => Chord(loop, segment, scale) < ThresholdPoints;

    /// <summary>How many vectors of the loop are small.</summary>
    public static int Count(Loop loop, double scale)
    {
        int n = 0;
        for (int i = 0; i < loop.Segments.Count; i++)
            if (IsSmall(loop, i, scale)) n++;
        return n;
    }

    /// <summary>The end point of every small vector of the loop, in the loop's own space (what the highlight marks).</summary>
    public static IEnumerable<Vec2> SmallEnds(Loop loop, double scale)
    {
        for (int i = 0; i < loop.Segments.Count; i++)
            if (IsSmall(loop, i, scale)) yield return loop.Segments[i].End;
    }
}

/// <summary>
/// Vector Edit's remaining choices (Tools ▸ Options ▸ Vector editing, View menu), one set for the program. The shell loads them at startup and
/// saves them when they change.
/// </summary>
public static class VectorEditOptions
{
    /// <summary>
    /// "Marquee with Shift selects vectors" (on by default). On: a plain box drawn in Vector Edit deletes the nodes inside it and Shift+box
    /// selects them. Off: the two are swapped.
    /// </summary>
    public static bool MarqueeShiftSelects { get; set; } = true;

    /// <summary>The Tangency setting: a node where the two vectors differ in direction by less than this many degrees is drawn as a small cross.</summary>
    public const double DefaultTangencyDegrees = 2, MaxTangencyDegrees = 45;

    public static double TangencyDegrees { get; set; } = DefaultTangencyDegrees;

    public static double SetTangencyDegrees(double degrees)
    {
        TangencyDegrees = Math.Clamp(double.IsFinite(degrees) ? degrees : DefaultTangencyDegrees, 0, MaxTangencyDegrees);
        return TangencyDegrees;
    }

    /// <summary>View ▸ Highlight Small Vectors: a red square at the end of every small vector, in every object. Off by default.</summary>
    public static bool HighlightSmallVectors { get; set; }
}
