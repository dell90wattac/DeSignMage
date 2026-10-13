using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Tests;

public class GeometryTests
{
    private const double Eps = 1e-9;

    [Fact]
    public void Affine_multiply_applies_left_then_right()
    {
        var m = Affine2.Scale(2, 3) * Affine2.Translation(10, 20);
        Assert.Equal(new Vec2(12, 23), m.Transform(new Vec2(1, 1)));
    }

    [Fact]
    public void Affine_invert_round_trips()
    {
        var m = Affine2.Rotation(0.7) * Affine2.Scale(2, -0.5) * Affine2.Translation(5, -3);
        var p = new Vec2(3.5, -8);
        var back = m.Invert().Transform(m.Transform(p));
        Assert.Equal(p.X, back.X, 9);
        Assert.Equal(p.Y, back.Y, 9);
        Assert.True((m * m.Invert()).ApproximatelyEquals(Affine2.Identity));
    }

    [Fact]
    public void Affine_scale_about_center_keeps_center_fixed()
    {
        var c = new Vec2(10, 10);
        Assert.Equal(c, Affine2.Scale(3, 0.5, c).Transform(c));
    }

    [Fact]
    public void Singular_affine_cannot_be_inverted()
    {
        Assert.Throws<InvalidOperationException>(() => Affine2.Scale(0, 1).Invert());
    }

    [Fact]
    public void Empty_rect_is_identity_for_union()
    {
        var r = Rect2.FromXYWH(1, 2, 3, 4);
        Assert.True(Rect2.Empty.IsEmpty);
        Assert.Equal(r, Rect2.Empty.Union(r));
        Assert.False(Rect2.Empty.Intersects(r));
    }

    [Fact]
    public void Rect_from_points_normalizes()
    {
        var r = Rect2.FromPoints(new Vec2(5, 1), new Vec2(2, 7));
        Assert.Equal(new Rect2(2, 1, 5, 7), r);
        Assert.Equal(3, r.Width);
        Assert.Equal(6, r.Height);
    }

    [Fact]
    public void Ellipse_bounds_are_exact()
    {
        var g = PathGeometry.Ellipse(Rect2.FromXYWH(10, 20, 100, 50));
        var b = g.GetBounds();
        Assert.Equal(10, b.Left, 9);
        Assert.Equal(20, b.Top, 9);
        Assert.Equal(110, b.Right, 9);
        Assert.Equal(70, b.Bottom, 9);
    }

    [Fact]
    public void Rotated_circle_bounds_are_tight_not_control_point_hull()
    {
        // A circle of radius 50 rotated 45° still has a 100 x 100 box; the control-point hull would be larger.
        var g = PathGeometry.Ellipse(Rect2.FromCenter(Vec2.Zero, 100, 100)).Transform(Affine2.Rotation(Math.PI / 4));
        var b = g.GetBounds();
        Assert.InRange(b.Width, 99.9, 100.2);
        Assert.InRange(b.Height, 99.9, 100.2);
    }

    [Fact]
    public void Contains_tests_fill_area()
    {
        var rect = PathGeometry.Rectangle(Rect2.FromXYWH(0, 0, 10, 10));
        Assert.True(rect.Contains(new Vec2(5, 5)));
        Assert.False(rect.Contains(new Vec2(11, 5)));

        var ellipse = PathGeometry.Ellipse(Rect2.FromXYWH(0, 0, 10, 10));
        Assert.True(ellipse.Contains(new Vec2(5, 5)));
        Assert.False(ellipse.Contains(new Vec2(0.5, 0.5)));   // inside the box, outside the circle
    }

    [Theory]
    [InlineData(FillRule.EvenOdd, false)]
    [InlineData(FillRule.NonZero, true)]
    public void Fill_rule_decides_whether_inner_same_direction_loop_is_a_hole(FillRule rule, bool centerFilled)
    {
        var b = new PathBuilder { FillRule = rule };
        b.MoveTo(0, 0).LineTo(10, 0).LineTo(10, 10).LineTo(0, 10).Close();
        b.MoveTo(3, 3).LineTo(7, 3).LineTo(7, 7).LineTo(3, 7).Close();
        var g = b.Build();
        Assert.Equal(centerFilled, g.Contains(new Vec2(5, 5)));
        Assert.True(g.Contains(new Vec2(1, 1)));
    }

    [Fact]
    public void Distance_to_outline()
    {
        var rect = PathGeometry.Rectangle(Rect2.FromXYWH(0, 0, 10, 10));
        Assert.Equal(2, rect.DistanceToOutline(new Vec2(5, 2)), 9);
        Assert.Equal(3, rect.DistanceToOutline(new Vec2(13, 5)), 9);
    }

    [Fact]
    public void Open_path_has_no_closing_segment_for_outline_distance()
    {
        var g = new PathBuilder().MoveTo(0, 0).LineTo(10, 0).LineTo(10, 10).Build();
        // Point near the would-be closing diagonal but far from the two real segments.
        Assert.True(g.DistanceToOutline(new Vec2(3, 3)) > 2.9);
    }

    [Fact]
    public void Flatten_stays_within_tolerance_of_circle()
    {
        var g = PathGeometry.Ellipse(Rect2.FromCenter(Vec2.Zero, 200, 200));
        foreach (var poly in g.Flatten(0.1))
            foreach (var p in poly.Points)
                Assert.InRange(p.Length, 99.85, 100.15);
    }

    [Fact]
    public void Rounded_rectangle_radius_is_clamped_to_half_the_short_side()
    {
        var g = PathGeometry.Rectangle(Rect2.FromXYWH(0, 0, 10, 4), cornerRadius: 50);
        var b = g.GetBounds();
        Assert.Equal(10, b.Width, 6);
        Assert.Equal(4, b.Height, 6);
    }

    [Fact]
    public void Quad_is_stored_as_equivalent_cubic()
    {
        var g = new PathBuilder().MoveTo(0, 0).QuadTo(new Vec2(5, 10), new Vec2(10, 0)).Build();
        Assert.Equal(PathVerb.CubicTo, g.Verbs[1]);
        Assert.Equal(5, g.GetBounds().Height, 9);   // quadratic peak is half the control height
    }
}
