using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Model;

/// <summary>
/// A regular polygon, or a star when <see cref="InnerRatio"/> is above 0, drawn around the local origin (so the object's transform is its
/// center). The first corner points along <see cref="StartAngle"/> (radians, in document space: y grows downward, so a positive angle turns
/// clockwise on screen). Vector editing turns it into a plain path on its first edit, like a rectangle.
/// </summary>
public sealed class PolygonObject : DocObject
{
    public const int MinSides = 3, MaxSides = 100;
    public const double MaxInnerRatio = 0.95;

    private int _sides;
    private double _radius, _innerRatio, _startAngle;

    public PolygonObject(int sides, double radius, double innerRatio = 0, double startAngle = -Math.PI / 2)
    {
        _sides = ClampSides(sides);
        _radius = Math.Max(0, radius);
        _innerRatio = ClampInner(innerRatio);
        _startAngle = startAngle;
    }

    public static int ClampSides(int sides) => Math.Clamp(sides, MinSides, MaxSides);
    public static double ClampInner(double ratio) => Math.Clamp(ratio, 0, MaxInnerRatio);

    /// <summary>Number of corners (of the outer circle for a star), 3 to 100.</summary>
    public int Sides { get => _sides; set { _sides = ClampSides(value); Touch(); } }

    /// <summary>Distance from the center to the outer corners, in points.</summary>
    public double Radius { get => _radius; set { _radius = Math.Max(0, value); Touch(); } }

    /// <summary>0 = a plain polygon; above 0 the corners alternate between the outer radius and this fraction of it (a star).</summary>
    public double InnerRatio { get => _innerRatio; set { _innerRatio = ClampInner(value); Touch(); } }

    /// <summary>Direction of the first corner, radians.</summary>
    public double StartAngle { get => _startAngle; set { _startAngle = value; Touch(); } }

    public bool IsStar => _innerRatio > 0;

    public override string KindName => IsStar ? "Star" : "Polygon";
    protected override DocObject CloneCore() => new PolygonObject(_sides, _radius, _innerRatio, _startAngle);
    public override PathGeometry GetLocalGeometry() => Build(_sides, _radius, _innerRatio, _startAngle);

    public static PathGeometry Build(int sides, double radius, double innerRatio, double startAngle)
    {
        var b = new PathBuilder();
        bool star = innerRatio > 0;
        int count = star ? sides * 2 : sides;
        for (int i = 0; i < count; i++)
        {
            double r = star && i % 2 == 1 ? radius * innerRatio : radius;
            double a = startAngle + 2 * Math.PI * i / count;
            var p = new Vec2(r * Math.Cos(a), r * Math.Sin(a));
            if (i == 0) b.MoveTo(p); else b.LineTo(p);
        }
        return b.Close().Build();
    }
}

/// <summary>
/// A basic arrow pointing along local +x: the tail at x = 0, the tip at x = <see cref="Length"/>, centered on y = 0. The head is
/// <see cref="HeadWidth"/> wide and <see cref="HeadFraction"/> of the length long; the shaft is <see cref="ShaftFraction"/> of the head
/// width wide. The object's transform places and turns it. Vector editing turns it into a plain path on its first edit.
/// </summary>
public sealed class ArrowObject : DocObject
{
    public const double MinFraction = 0.05, MaxFraction = 1;

    private double _length, _headWidth, _headFraction, _shaftFraction;

    public ArrowObject(double length, double headWidth, double headFraction = 0.4, double shaftFraction = 0.4)
    {
        _length = Math.Max(0, length);
        _headWidth = Math.Max(0, headWidth);
        _headFraction = ClampFraction(headFraction);
        _shaftFraction = ClampFraction(shaftFraction);
    }

    public static double ClampFraction(double f) => Math.Clamp(f, MinFraction, MaxFraction);

    /// <summary>Tail to tip, in points.</summary>
    public double Length { get => _length; set { _length = Math.Max(0, value); Touch(); } }

    /// <summary>Width of the arrow head across, in points.</summary>
    public double HeadWidth { get => _headWidth; set { _headWidth = Math.Max(0, value); Touch(); } }

    /// <summary>Length of the head as a fraction of <see cref="Length"/> (0.05 to 1).</summary>
    public double HeadFraction { get => _headFraction; set { _headFraction = ClampFraction(value); Touch(); } }

    /// <summary>Width of the shaft as a fraction of <see cref="HeadWidth"/> (0.05 to 1).</summary>
    public double ShaftFraction { get => _shaftFraction; set { _shaftFraction = ClampFraction(value); Touch(); } }

    public override string KindName => "Arrow";
    protected override DocObject CloneCore() => new ArrowObject(_length, _headWidth, _headFraction, _shaftFraction);
    public override PathGeometry GetLocalGeometry() => Build(_length, _headWidth, _headFraction, _shaftFraction);

    public static PathGeometry Build(double length, double headWidth, double headFraction, double shaftFraction)
    {
        double head = length * headFraction, shaft = headWidth * shaftFraction / 2, half = headWidth / 2, neck = length - head;
        return new PathBuilder()
            .MoveTo(0, -shaft).LineTo(neck, -shaft).LineTo(neck, -half).LineTo(length, 0)
            .LineTo(neck, half).LineTo(neck, shaft).LineTo(0, shaft).Close().Build();
    }
}
