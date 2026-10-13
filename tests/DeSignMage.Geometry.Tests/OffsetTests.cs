using DeSignMage.Core.Geometry;
using static DeSignMage.Geometry.Tests.Shapes;

namespace DeSignMage.Geometry.Tests;

public class OffsetTests
{
    private static readonly IOffsetService O = GeometryEngines.Offsets;
    private static readonly OffsetOptions Round = new() { Corners = CornerStyle.Round };
    private static readonly OffsetOptions Miter = new() { Corners = CornerStyle.Miter };

    private static void Near(double expected, double actual, double tol) =>
        Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected:R}, got {actual:R} (diff {actual - expected:E3})");

    // ---- corners ----

    [Fact]
    public void Square_outline_with_default_corners_is_a_bigger_square_with_sharp_exact_corners()
    {
        var r = O.Offset(Rect(0, 0, 10, 10), 2);       // 90° corners ≥ OAT 70 → miter
        Assert.Equal(1, Loops(r));
        Assert.Equal(196, Area(r), 9);
        Assert.Equal(0, Count(r, PathVerb.ArcTo));
        Assert.Equal(3, Count(r, PathVerb.LineTo));
        foreach (var corner in new Vec2[] { new(-2, -2), new(12, -2), new(12, 12), new(-2, 12) })
            Assert.Contains(r.Points, p => Vec2.Distance(p, corner) < 1e-9);
    }

    [Fact]
    public void Round_corners_are_exact_quarter_arcs_around_the_source_corners()
    {
        var r = O.Offset(Rect(0, 0, 10, 10), 2, Round);
        Assert.Equal(100 + 80 + Math.PI * 4, Area(r), 9);
        var arcs = ArcList(r);
        Assert.Equal(4, arcs.Count);
        foreach (var a in arcs)
        {
            Assert.Equal(2, a.Radius, 9);
            Assert.Equal(Math.PI / 2, Math.Abs(a.Sweep), 9);
            Assert.Contains(new Vec2[] { new(0, 0), new(10, 0), new(10, 10), new(0, 10) }, c => Vec2.Distance(c, a.Center) < 1e-9);
        }
    }

    [Fact]
    public void Bevel_cuts_each_outside_corner_straight()
    {
        var r = O.Offset(Rect(0, 0, 10, 10), 2, new OffsetOptions { Corners = CornerStyle.Bevel });
        Assert.Equal(196 - 4 * 2, Area(r), 6);
        Assert.Equal(0, Count(r, PathVerb.ArcTo));
    }

    [Fact]
    public void Corners_sharper_than_the_OAT_are_rounded_and_the_others_mitered()
    {
        // Isosceles triangle: apex 30° (< OAT 70 → round), base corners 75° (≥ 70 → miter).
        double h = 10 / Math.Tan(15 * Math.PI / 180);              // base 20, apex angle 30°
        var apex = new Vec2(10, 0);
        var tri = Polygon((10, 0), (20, h), (0, h));
        double d = 1.5;
        var r = O.Offset(tri, d);
        var arcs = ArcList(r);
        Assert.Single(arcs);
        Assert.Equal(d, arcs[0].Radius, 9);
        Assert.True(Vec2.Distance(arcs[0].Center, apex) < 1e-9);
        // Base miter tips: the offset lines of the base (y = h + d) and of each side cross there.
        double tipDx = d / Math.Tan(37.5 * Math.PI / 180);       // half of the 75° corner
        Assert.Contains(r.Points, p => Vec2.Distance(p, new Vec2(20 + tipDx, h + d)) < 1e-9);
        Assert.Contains(r.Points, p => Vec2.Distance(p, new Vec2(-tipDx, h + d)) < 1e-9);

        // With OAT 20 the apex (30° ≥ 20) gets a miter as well.
        var sharp = O.Offset(tri, d, new OffsetOptions { OutsideAngleThreshold = 20 });
        Assert.Empty(ArcList(sharp));
        // Round button (OAT 120) rounds all three.
        Assert.Equal(3, ArcList(O.Offset(tri, d, new OffsetOptions { OutsideAngleThreshold = 120 })).Count);
    }

    [Fact]
    public void Very_sharp_miters_are_limited()
    {
        // A 4° spike would miter out to 28 × d; the default limit (20) rounds it instead.
        var spike = Polygon((0, 0), (100, 3.5), (0, 7));
        var r = O.Offset(spike, 1, Miter);
        Assert.True(r.GetBounds().Right < 100 + 20, $"right edge {r.GetBounds().Right}");
        Assert.Single(ArcList(r));
    }

    // ---- arcs, holes, inward ----

    [Fact]
    public void A_circle_grows_into_an_exact_circle()
    {
        var c = new Vec2(5, -3);
        var r = O.Offset(Circle(c, 10, arcs: 3, startDeg: 20), 2.5);
        Assert.Equal(1, Loops(r));
        Assert.Equal(0, Count(r, PathVerb.LineTo));
        Assert.All(ArcList(r), a => { Assert.Equal(12.5, a.Radius, 9); Assert.True(Vec2.Distance(a.Center, c) < 1e-9); });
        Assert.Equal(Math.PI * 12.5 * 12.5, Area(r), 8);
        Assert.InRange(ArcList(r).Count, 2, 4);
    }

    [Fact]
    public void Inline_of_a_circle_shrinks_it_and_vanishes_past_its_radius()
    {
        var r = O.Offset(Circle(new Vec2(0, 0), 10), -4);
        Assert.All(ArcList(r), a => Assert.Equal(6, a.Radius, 9));
        Assert.Equal(Math.PI * 36, Area(r), 8);
        Assert.True(O.Offset(Circle(new Vec2(0, 0), 10), -10.5).IsEmpty);
    }

    [Fact]
    public void Holes_shrink_when_the_outline_grows_and_grow_when_it_shrinks()
    {
        var frame = Shapes.Concat(Rect(0, 0, 30, 30), Rect(10, 10, 10, 10, clockwiseOnScreen: false));
        Assert.Equal(34 * 34 - 6 * 6, Area(O.Offset(frame, 2)), 9);
        Assert.Equal(26 * 26 - 14 * 14, Area(O.Offset(frame, -2)), 9);
        // A hole smaller than twice the distance closes.
        var closed = O.Offset(frame, 6);
        Assert.Equal(1, Loops(closed));
        Assert.Equal(42 * 42, Area(closed), 9);
        // Contour only: the hole is dropped.
        var contour = O.Offset(frame, 2, new OffsetOptions { ContourOnly = true });
        Assert.Equal(1, Loops(contour));
        Assert.Equal(34 * 34, Area(contour), 9);
    }

    [Fact]
    public void A_ring_of_arcs_offsets_both_circles_exactly()
    {
        var ring = Shapes.Concat(Circle(new Vec2(0, 0), 10), Circle(new Vec2(0, 0), 6, clockwiseOnScreen: false));
        var r = O.Offset(ring, 1);
        Assert.Equal(Math.PI * (121 - 25), Area(r), 8);
        Assert.All(ArcList(r), a => Assert.True(Math.Abs(a.Radius - 11) < 1e-9 || Math.Abs(a.Radius - 5) < 1e-9));
    }

    [Fact]
    public void Zero_distance_returns_the_cleaned_shape()
    {
        var a = Shapes.Concat(Rect(0, 0, 10, 10), Rect(5, 5, 10, 10));
        var r = O.Offset(a, 0);
        Assert.Equal(1, Loops(r));
        Assert.Equal(175, Area(r), 9);
    }

    [Fact]
    public void Empty_and_degenerate_sources_give_empty_results()
    {
        Assert.True(O.Offset(PathGeometry.EmptyPath, 3).IsEmpty);
        Assert.True(O.Offset(Polygon((0, 0), (10, 0), (5, 0)), 0).IsEmpty);
        Assert.Throws<ArgumentOutOfRangeException>(() => O.Offset(Rect(0, 0, 1, 1), double.NaN));
    }

    // ---- round trips ----

    [Fact]
    public void Outline_then_inline_returns_the_source()
    {
        // Round corners: growing then shrinking by the same amount gives back any shape without features thinner than 2d.
        var shapes = new[]
        {
            Rect(0, 0, 20, 10),
            Circle(new Vec2(0, 0), 10),
            Shapes.Concat(Rect(0, 0, 40, 30), Circle(new Vec2(20, 15), 8, clockwiseOnScreen: false)),
            Polygon((0, 0), (30, 0), (36, 12), (15, 25), (-4, 12)),   // convex pentagon
        };
        foreach (var s in shapes)
        {
            var back = O.Offset(O.Offset(s, 3, Round), -3, Round);
            Near(Area(s), Area(back), 0.05);
            Assert.True(Hausdorff(s, back) < 0.03, $"round trip moved the outline by {Hausdorff(s, back)}");
        }
        // Miter: exact for a square.
        var sq = Rect(0, 0, 10, 10);
        var sqBack = O.Offset(O.Offset(sq, 2, Miter), -2, Miter);
        Assert.Equal(100, Area(sqBack), 9);
        Assert.True(Hausdorff(sq, sqBack) < 1e-9);
    }

    [Fact]
    public void Several_offsets_are_each_measured_from_the_source()
    {
        var results = O.Offset(Circle(new Vec2(0, 0), 10), [1, 2, -3]);
        Assert.Equal(3, results.Count);
        double[] radii = [11, 12, 7];
        for (int i = 0; i < 3; i++) Assert.All(ArcList(results[i]), a => Assert.Equal(radii[i], a.Radius, 9));
    }

    [Fact]
    public void Offsets_of_cubic_curves_are_light_and_within_tolerance()
    {
        // Rounded rectangle with cubic corners (r = 8): the outline is a rounded rectangle with radius 10.
        var round = PathGeometry.Rectangle(new Rect2(0, 0, 60, 40), 8);
        var r = O.Offset(round, 2);
        var expected = PathGeometry.Rectangle(new Rect2(-2, -2, 62, 42), 10);     // Rect2 is left, top, right, bottom
        // Cubic quarter circles are 0.03 % off a true circle; the offset follows the cubic, so compare loosely.
        Assert.True(Hausdorff(r, expected) < 0.02, $"{Hausdorff(r, expected)}");
        Assert.True(r.Verbs.Count < 30, $"{r.Verbs.Count} verbs");
        Assert.Equal(0, Count(r, PathVerb.CubicTo));          // lines and arcs

        var cubic = O.Offset(round, 2, new OffsetOptions { PreferCubics = true });
        Assert.True(Hausdorff(cubic, expected) < 0.02, $"{Hausdorff(cubic, expected)}");
        Assert.True(cubic.Verbs.Count < 30);
    }

    // ---- open paths ----

    [Fact]
    public void An_open_line_outlines_to_a_stroke_with_the_chosen_caps()
    {
        var line = new PathBuilder().MoveTo(0, 0).LineTo(10, 0).Build();
        Assert.Equal(20 + Math.PI, Area(O.Offset(line, 1)), 8);
        Assert.Equal(20, Area(O.Offset(line, 1, new OffsetOptions { Caps = CapStyle.Butt })), 9);
        Assert.Equal(24, Area(O.Offset(line, 1, new OffsetOptions { Caps = CapStyle.Square })), 9);
        Assert.True(O.Offset(line, 1, new OffsetOptions { OpenPaths = OpenPathMode.Ignore }).IsEmpty);
        var round = O.Offset(line, 1);
        Assert.All(ArcList(round), a => Assert.Equal(1, a.Radius, 9));
    }

    [Fact]
    public void A_bent_stroke_gets_a_mitered_outside_corner()
    {
        var l = new PathBuilder().MoveTo(0, 0).LineTo(10, 0).LineTo(10, 10).Build();
        var r = O.Offset(l, 1, new OffsetOptions { Caps = CapStyle.Butt });
        Assert.Contains(r.Points, p => Vec2.Distance(p, new Vec2(11, -1)) < 1e-9);    // miter tip outside the bend
        Assert.Equal(20 + 20 - 1 + 1, Area(r), 9);     // two 10×2 bars, minus their 1×1 overlap, plus the 1×1 miter corner
    }

    [Fact]
    public void One_sided_parallels_follow_the_path_on_the_chosen_side()
    {
        var l = new PathBuilder().MoveTo(0, 0).LineTo(10, 0).LineTo(10, 10).Build();
        var opt = new OffsetOptions { OpenPaths = OpenPathMode.OneSided };
        // Positive: screen-left of travel (here above the first leg and right of the second), with a sharp corner.
        var left = O.Offset(l, 1, opt);
        Assert.Equal(1, Loops(left));
        Assert.DoesNotContain(PathVerb.Close, left.Verbs);
        Assert.Equal(new Vec2(0, -1), Round6(left.Points[0]));
        Assert.Contains(left.Points, p => Vec2.Distance(p, new Vec2(11, -1)) < 1e-9);
        Assert.Equal(new Vec2(11, 10), Round6(left.Points[^1]));
        // Negative: the inside of the bend, trimmed where the two parallels cross.
        var right = O.Offset(l, -1, opt);
        Assert.Equal(new Vec2(0, 1), Round6(right.Points[0]));
        Assert.Contains(right.Points, p => Vec2.Distance(p, new Vec2(9, 1)) < 1e-9);
        Assert.Equal(new Vec2(9, 10), Round6(right.Points[^1]));
    }

    [Fact]
    public void One_sided_parallel_of_an_arc_is_a_concentric_arc()
    {
        var arc = new PathBuilder().MoveTo(10, 0).ArcTo(new Vec2(0, 10), new Vec2(-10, 0)).Build();   // half circle, r 10
        var outside = O.Offset(arc, 2, new OffsetOptions { OpenPaths = OpenPathMode.OneSided });
        var inside = O.Offset(arc, -2, new OffsetOptions { OpenPaths = OpenPathMode.OneSided });
        var radii = ArcList(outside).Select(a => a.Radius).Concat(ArcList(inside).Select(a => a.Radius)).Distinct(new Close()).ToList();
        Assert.Equal(2, radii.Count);
        Assert.Contains(radii, r => Math.Abs(r - 12) < 1e-9);
        Assert.Contains(radii, r => Math.Abs(r - 8) < 1e-9);
    }

    private sealed class Close : IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) < 1e-6;
        public int GetHashCode(double v) => 0;
    }

    private static Vec2 Round6(Vec2 v) => new(Math.Round(v.X, 6) + 0.0, Math.Round(v.Y, 6) + 0.0);
}
