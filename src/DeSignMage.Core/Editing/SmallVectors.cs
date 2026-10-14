using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

/// <summary>
/// What counts as a "small vector": one shorter than a threshold, measured on the page (so it follows the object's scale). Small vectors are
/// usually noise from tracing or importing; the Vector Edit tool draws the nodes that touch them small, counts them in the Info Line, and
/// lets a box drawn over small nodes delete them. The threshold is one setting for the whole program (Tools ▸ Options ▸ Vector editing);
/// the shell loads it at startup and writes it when it changes.
/// </summary>
public static class SmallVectors
{
    /// <summary>The default threshold: a quarter inch.</summary>
    public static readonly double DefaultPoints = Units.FromInches(0.25);

    /// <summary>The smallest and largest threshold the setting accepts, in inches.</summary>
    public const double MinInches = 0.01, MaxInches = 5;

    /// <summary>A vector shorter than this many points (on the page) is small.</summary>
    public static double ThresholdPoints { get; set; } = DefaultPoints;

    public static double ThresholdInches => Units.ToInches(ThresholdPoints);

    /// <summary>Sets the threshold in inches, kept between <see cref="MinInches"/> and <see cref="MaxInches"/>. Returns the value that was applied.</summary>
    public static double SetInches(double inches)
    {
        double clamped = Math.Clamp(double.IsFinite(inches) ? inches : 0.25, MinInches, MaxInches);
        ThresholdPoints = Units.FromInches(clamped);
        return clamped;
    }

    /// <summary>True if vector <paramref name="segment"/> of <paramref name="loop"/> is small when the loop's space is scaled by <paramref name="scale"/> on the page.</summary>
    public static bool IsSmall(Loop loop, int segment, double scale) => loop.SegmentLength(segment) * scale < ThresholdPoints;

    /// <summary>True if node <paramref name="node"/> is small. <b>Provisional rule:</b> it starts or ends a small vector (either side). This
    /// is the only place that decides it; change it here if the rule turns out to be different.</summary>
    public static bool IsSmallNode(Loop loop, int node, double scale)
    {
        int a = loop.Incoming(node), b = loop.Outgoing(node);
        return Rule(a >= 0 && IsSmall(loop, a, scale), b >= 0 && IsSmall(loop, b, scale));
    }

    /// <summary>The rule itself: what makes a node small, given whether the vector before it and the vector after it are small.</summary>
    private static bool Rule(bool incomingSmall, bool outgoingSmall) => incomingSmall || outgoingSmall;

    /// <summary>Which nodes of <paramref name="loop"/> are small (index = node number).</summary>
    public static bool[] SmallNodes(Loop loop, double scale)
    {
        var small = new bool[loop.NodeCount];
        var vector = new bool?[loop.Segments.Count];   // each vector measured once
        bool V(int i) => vector[i] ??= IsSmall(loop, i, scale);
        for (int k = 0; k < small.Length; k++)
        {
            int a = loop.Incoming(k), b = loop.Outgoing(k);
            small[k] = Rule(a >= 0 && V(a), b >= 0 && V(b));
        }
        return small;
    }
}
