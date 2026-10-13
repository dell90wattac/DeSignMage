using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Model;

/// <summary>
/// How a gradient runs. Vertical and Horizontal are Linear with the control line locked to an axis (the tool does not rotate them);
/// Circle has a center and a radius handle, Ellipse a center and two handles at right angles.
/// (The legacy "Baseline" blend, which follows a paragraph text's arc, is not built.)
/// </summary>
public enum GradientKind { Linear, Vertical, Horizontal, Circle, Ellipse }

/// <summary>
/// One color of a gradient at <see cref="Offset"/> along the control line (0 = start/center, 1 = end/radius). The color's alpha makes the
/// gradient fade: a color fading to transparent is two stops of the same color with alpha 255 and 0 (this is also how an opacity ramp,
/// "transparency", fits the model). A <see cref="Highlight"/> stop is the middle of a triplet [base, highlight, base] that moves together.
/// </summary>
public readonly record struct GradientNode(double Offset, DocColor Color, bool Highlight = false);

/// <summary>
/// A gradient fill (Effects ▸ Gradient Blend). The control points are stored <b>relative to the object's own bounding box</b>
/// (0..1 in x and y, in the object's local space, y down), so the gradient follows the object when it is moved, scaled, edited or resized,
/// and a preset fits any object. <see cref="Points"/> by kind:
/// <list type="bullet">
/// <item>Linear / Vertical / Horizontal: [start, end].</item>
/// <item>Circle: [center, a point on the radius].</item>
/// <item>Ellipse: [center, end of the first axis, end of the second axis] (the axes are at right angles).</item>
/// </list>
/// The <see cref="Stops"/> sit on the line from the first point to the second (stop 0 at offset 0, the last at 1). Colors blend in HSV, taking
/// the short way round the hue wheel; <see cref="AlternateColors"/> takes the long way (a rainbow). <see cref="Reflect"/> mirrors the ramp
/// about the middle of the control line. Value equality: two gradients with the same kind, points, stops and flags are equal.
/// <para>
/// Transparency (planned, not built): a fade is already expressible through stop alpha when the color under it is one solid color.
/// For a fade over another paint (a gradient, a picture) add an optional alpha ramp on the object style, a GradientPaint whose colors are
/// all the same and only the alpha changes, evaluated with this same geometry and ramp code and multiplied in by the renderer.
/// </para>
/// </summary>
public sealed record GradientPaint : Paint
{
    public const int MinStops = 2, MaxStops = 20;

    public GradientPaint(GradientKind kind, IReadOnlyList<Vec2> points, IReadOnlyList<GradientNode> stops, bool alternateColors = false, bool reflect = false)
    {
        Kind = kind;
        Points = points.ToArray();
        Stops = Normalize(stops);
        AlternateColors = alternateColors;
        Reflect = reflect;
        if (Points.Count != PointCount(kind)) throw new ArgumentException($"A {kind} gradient needs {PointCount(kind)} control points.");
    }

    public GradientKind Kind { get; }
    public IReadOnlyList<Vec2> Points { get; }
    public IReadOnlyList<GradientNode> Stops { get; }
    public bool AlternateColors { get; }
    public bool Reflect { get; }

    public static int PointCount(GradientKind kind) => kind == GradientKind.Ellipse ? 3 : 2;

    public bool IsLinear => Kind is GradientKind.Linear or GradientKind.Vertical or GradientKind.Horizontal;

    /// <summary>At least two stops, offsets inside 0..1 and ascending, the first at 0 and the last at 1 (a gradient is always the whole control line).</summary>
    private static GradientNode[] Normalize(IReadOnlyList<GradientNode> stops)
    {
        if (stops.Count < MinStops) throw new ArgumentException("A gradient needs at least two colors.");
        var list = stops.Select(s => s with { Offset = Math.Clamp(double.IsFinite(s.Offset) ? s.Offset : 0, 0, 1) })
            .OrderBy(s => s.Offset).ToArray();   // OrderBy is stable
        list[0] = list[0] with { Offset = 0 };
        list[^1] = list[^1] with { Offset = 1 };
        return list;
    }

    // ---- Building ----

    /// <summary>The control points a gradient of this kind starts with on any object (relative to its bounds).</summary>
    public static Vec2[] DefaultPoints(GradientKind kind) => kind switch
    {
        GradientKind.Vertical => [new(0.5, 0), new(0.5, 1)],
        GradientKind.Horizontal => [new(0, 0.5), new(1, 0.5)],
        GradientKind.Circle => [new(0.5, 0.5), new(1, 0.5)],
        GradientKind.Ellipse => [new(0.5, 0.5), new(1, 0.5), new(0.5, 1)],
        _ => [new(0, 0), new(1, 1)],
    };

    /// <summary>
    /// A new gradient of <paramref name="kind"/> from the object's current fill color to an automatically adjusted variant of it
    /// (lighter when the color is dark, darker when it is light), as the legacy program does.
    /// </summary>
    public static GradientPaint Create(GradientKind kind, DocColor from)
    {
        var (_, _, v) = from.ToHsv();
        var to = v > 50 ? Mix(from, DocColor.Black, 0.45) : Mix(from, DocColor.White, 0.55);
        return new GradientPaint(kind, DefaultPoints(kind), [new GradientNode(0, from), new GradientNode(1, to)]);
    }

    /// <summary>The same gradient reshaped as another kind: new default control points, the same colors.</summary>
    public GradientPaint WithKind(GradientKind kind) =>
        kind == Kind ? this : new GradientPaint(kind, DefaultPoints(kind), Stops, AlternateColors, Reflect);

    public GradientPaint WithPoints(IReadOnlyList<Vec2> points) => new(Kind, points, Stops, AlternateColors, Reflect);
    public GradientPaint WithStops(IReadOnlyList<GradientNode> stops) => new(Kind, Points, stops, AlternateColors, Reflect);
    public GradientPaint WithFlags(bool alternateColors, bool reflect) => new(Kind, Points, Stops, alternateColors, reflect);

    private static DocColor Mix(DocColor a, DocColor b, double t) => new(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t), a.A);

    /// <summary>A flat fill from the first color of this gradient (what "Remove" leaves behind).</summary>
    public SolidPaint ToSolid() => new(Stops[0].Color);

    // ---- Geometry in an object's own space ----

    /// <summary>The control points in the object's local space, given its local bounds (a flat bounds gets a tiny width/height so the frame stays valid).</summary>
    public GradientFrame Frame(Rect2 bounds)
    {
        double w = Math.Max(bounds.Width, 1e-9), h = Math.Max(bounds.Height, 1e-9);
        Vec2 P(int i) => new(bounds.Left + Points[i].X * w, bounds.Top + Points[i].Y * h);
        return new GradientFrame(Kind, P(0), P(1), Points.Count > 2 ? P(2) : P(0));
    }

    /// <summary>The inverse of <see cref="Frame"/> for one point: a local point as relative to the bounds.</summary>
    public static Vec2 ToRelative(Vec2 local, Rect2 bounds) =>
        new((local.X - bounds.Left) / Math.Max(bounds.Width, 1e-9), (local.Y - bounds.Top) / Math.Max(bounds.Height, 1e-9));

    public static Vec2 ToLocal(Vec2 relative, Rect2 bounds) =>
        new(bounds.Left + relative.X * Math.Max(bounds.Width, 1e-9), bounds.Top + relative.Y * Math.Max(bounds.Height, 1e-9));

    // ---- Value equality ----

    public bool Equals(GradientPaint? other) =>
        other is not null && Kind == other.Kind && AlternateColors == other.AlternateColors && Reflect == other.Reflect &&
        Points.SequenceEqual(other.Points) && Stops.SequenceEqual(other.Stops);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Kind); h.Add(AlternateColors); h.Add(Reflect);
        foreach (var p in Points) h.Add(p);
        foreach (var s in Stops) h.Add(s);
        return h.ToHashCode();
    }
}

/// <summary>
/// The control points of a gradient resolved in an object's local space. For Linear kinds <see cref="P0"/> → <see cref="P1"/> is the line;
/// for Circle P0 is the center and P1 a point on the radius; for Ellipse P0 is the center and P1 / P2 the ends of the two axes.
/// </summary>
public readonly record struct GradientFrame(GradientKind Kind, Vec2 P0, Vec2 P1, Vec2 P2)
{
    /// <summary>The control line the stops sit on.</summary>
    public Vec2 Axis => P1 - P0;

    /// <summary>First semi-axis of the circle/ellipse; the second is at right angles for a circle.</summary>
    public Vec2 Axis1 => P1 - P0;

    public Vec2 Axis2 => Kind == GradientKind.Ellipse ? P2 - P0 : new Vec2(-Axis1.Y, Axis1.X);

    /// <summary>True when the control line has no length, so there is nothing to blend along (the last color fills).</summary>
    public bool IsDegenerate => Axis1.LengthSquared < 1e-18;

    /// <summary>Maps the unit circle to the circle/ellipse (the shader of a radial gradient has radius 1).</summary>
    public Affine2 UnitToLocal => new(Axis1.X, Axis1.Y, Axis2.X, Axis2.Y, P0.X, P0.Y);

    /// <summary>Where a local point falls on the ramp: 0 at the start / center, 1 at the end / radius (not clamped).</summary>
    public double Parameter(Vec2 p)
    {
        if (IsDegenerate) return 1;
        if (Kind is GradientKind.Linear or GradientKind.Vertical or GradientKind.Horizontal)
            return Vec2.Dot(p - P0, Axis) / Axis.LengthSquared;
        var a1 = Axis1; var a2 = Axis2;
        var d = p - P0;
        double u = a1.LengthSquared > 0 ? Vec2.Dot(d, a1) / a1.LengthSquared : 0;
        double v = a2.LengthSquared > 0 ? Vec2.Dot(d, a2) / a2.LengthSquared : 0;
        return Math.Sqrt(u * u + v * v);
    }

    /// <summary>The local point at offset <paramref name="t"/> on the control line.</summary>
    public Vec2 PointAt(double t) => Vec2.Lerp(P0, P1, t);
}
