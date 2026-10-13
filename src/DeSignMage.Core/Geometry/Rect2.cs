namespace DeSignMage.Core.Geometry;

/// <summary>
/// Axis-aligned rectangle stored as edges. <see cref="Empty"/> has inverted infinite edges so that
/// <see cref="Union(Rect2)"/> with it is a no-op.
/// </summary>
public readonly record struct Rect2(double Left, double Top, double Right, double Bottom)
{
    public static readonly Rect2 Empty = new(double.PositiveInfinity, double.PositiveInfinity,
                                             double.NegativeInfinity, double.NegativeInfinity);

    public static Rect2 FromXYWH(double x, double y, double width, double height) => new(x, y, x + width, y + height);

    public static Rect2 FromPoints(Vec2 a, Vec2 b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public static Rect2 FromCenter(Vec2 center, double width, double height) =>
        new(center.X - width / 2, center.Y - height / 2, center.X + width / 2, center.Y + height / 2);

    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public bool IsEmpty => Right < Left || Bottom < Top;
    public Vec2 TopLeft => new(Left, Top);
    public Vec2 BottomRight => new(Right, Bottom);
    public Vec2 Center => new((Left + Right) / 2, (Top + Bottom) / 2);
    public Vec2 Size => new(Width, Height);

    public bool Contains(Vec2 p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    public bool Contains(Rect2 r) =>
        !IsEmpty && !r.IsEmpty && r.Left >= Left && r.Right <= Right && r.Top >= Top && r.Bottom <= Bottom;

    public bool Intersects(Rect2 r) =>
        !IsEmpty && !r.IsEmpty && r.Left <= Right && r.Right >= Left && r.Top <= Bottom && r.Bottom >= Top;

    public Rect2 Union(Rect2 r) =>
        new(Math.Min(Left, r.Left), Math.Min(Top, r.Top), Math.Max(Right, r.Right), Math.Max(Bottom, r.Bottom));

    public Rect2 Union(Vec2 p) =>
        new(Math.Min(Left, p.X), Math.Min(Top, p.Y), Math.Max(Right, p.X), Math.Max(Bottom, p.Y));

    public Rect2 Inflate(double d) => IsEmpty ? this : new(Left - d, Top - d, Right + d, Bottom + d);

    /// <summary>Bounds of this rectangle after an affine transform (exact for the four corners).</summary>
    public Rect2 Transform(Affine2 m)
    {
        if (IsEmpty) return this;
        return Empty.Union(m.Transform(new Vec2(Left, Top)))
                    .Union(m.Transform(new Vec2(Right, Top)))
                    .Union(m.Transform(new Vec2(Right, Bottom)))
                    .Union(m.Transform(new Vec2(Left, Bottom)));
    }

    public override string ToString() =>
        IsEmpty ? "Empty" : FormattableString.Invariant($"[{Left:0.###}, {Top:0.###}, {Width:0.###} x {Height:0.###}]");
}
