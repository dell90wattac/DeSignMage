namespace DeSignMage.Core.Geometry;

/// <summary>
/// 2D affine transform, row-vector convention (same as System.Numerics.Matrix3x2):
/// <c>p' = (x*M11 + y*M21 + OffsetX, x*M12 + y*M22 + OffsetY)</c>.
/// <c>a * b</c> means "apply a, then b".
/// </summary>
public readonly record struct Affine2(double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)
{
    public static readonly Affine2 Identity = new(1, 0, 0, 1, 0, 0);

    public static Affine2 Translation(double dx, double dy) => new(1, 0, 0, 1, dx, dy);
    public static Affine2 Translation(Vec2 d) => new(1, 0, 0, 1, d.X, d.Y);
    public static Affine2 Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);
    public static Affine2 Scale(double sx, double sy, Vec2 center) =>
        Translation(-center) * Scale(sx, sy) * Translation(center);

    public static Affine2 Rotation(double radians)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new(c, s, -s, c, 0, 0);
    }

    public static Affine2 Rotation(double radians, Vec2 center) =>
        Translation(-center) * Rotation(radians) * Translation(center);

    public Vec2 Transform(Vec2 p) => new(p.X * M11 + p.Y * M21 + OffsetX, p.X * M12 + p.Y * M22 + OffsetY);
    public Vec2 TransformVector(Vec2 v) => new(v.X * M11 + v.Y * M21, v.X * M12 + v.Y * M22);

    public static Affine2 operator *(Affine2 a, Affine2 b) => new(
        a.M11 * b.M11 + a.M12 * b.M21,
        a.M11 * b.M12 + a.M12 * b.M22,
        a.M21 * b.M11 + a.M22 * b.M21,
        a.M21 * b.M12 + a.M22 * b.M22,
        a.OffsetX * b.M11 + a.OffsetY * b.M21 + b.OffsetX,
        a.OffsetX * b.M12 + a.OffsetY * b.M22 + b.OffsetY);

    public double Determinant => M11 * M22 - M12 * M21;
    public bool IsInvertible => Math.Abs(Determinant) > 1e-12;
    public bool IsIdentity => this == Identity;
    public Vec2 Offset => new(OffsetX, OffsetY);

    /// <summary>Average linear scale factor; used e.g. to convert a tolerance between spaces.</summary>
    public double AverageScale => Math.Sqrt(Math.Abs(Determinant));

    public Affine2 Invert()
    {
        double det = Determinant;
        if (Math.Abs(det) <= 1e-12) throw new InvalidOperationException("Transform is not invertible.");
        double inv = 1.0 / det;
        double m11 = M22 * inv, m12 = -M12 * inv, m21 = -M21 * inv, m22 = M11 * inv;
        return new(m11, m12, m21, m22,
                   -(OffsetX * m11 + OffsetY * m21),
                   -(OffsetX * m12 + OffsetY * m22));
    }

    public bool ApproximatelyEquals(Affine2 o, double eps = 1e-9) =>
        Math.Abs(M11 - o.M11) < eps && Math.Abs(M12 - o.M12) < eps &&
        Math.Abs(M21 - o.M21) < eps && Math.Abs(M22 - o.M22) < eps &&
        Math.Abs(OffsetX - o.OffsetX) < eps && Math.Abs(OffsetY - o.OffsetY) < eps;
}
