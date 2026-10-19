using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Tests;

/// <summary>The gradient paint: value equality, normalizing, geometry and the HSV color ramp (Effects ▸ Gradient Blend).</summary>
public class GradientTests
{
    private static readonly DocColor Red = new(255, 0, 0), Blue = new(0, 0, 255), White = new(255, 255, 255), Black = new(0, 0, 0);

    private static GradientPaint Linear(DocColor a, DocColor b, bool alternate = false, bool reflect = false) =>
        new(GradientKind.Linear, GradientPaint.DefaultPoints(GradientKind.Linear), [new GradientNode(0, a), new GradientNode(1, b)], alternate, reflect);

    private static (int R, int G, int B) At(IReadOnlyList<GradientRamp.Entry> ramp, double t)
    {
        var e = ramp.OrderBy(x => Math.Abs(x.T - t)).First();
        return (e.R, e.G, e.B);
    }

    // ---- the paint ----

    [Fact]
    public void Equal_gradients_are_equal_by_value_and_any_difference_shows()
    {
        var a = Linear(Red, Blue);
        Assert.Equal(a, Linear(Red, Blue));
        Assert.Equal(a.GetHashCode(), Linear(Red, Blue).GetHashCode());
        Assert.NotEqual(a, Linear(Red, White));
        Assert.NotEqual(a, Linear(Red, Blue, alternate: true));
        Assert.NotEqual(a, Linear(Red, Blue, reflect: true));
        Assert.NotEqual(a, a.WithPoints([new Vec2(0, 0), new Vec2(1, 0)]));
        Assert.NotEqual(a, a.WithKind(GradientKind.Vertical));
        Assert.NotEqual<Paint>(a, new SolidPaint(Red));
    }

    [Fact]
    public void Stops_are_sorted_clamped_and_run_from_zero_to_one()
    {
        var g = new GradientPaint(GradientKind.Linear, GradientPaint.DefaultPoints(GradientKind.Linear),
            [new GradientNode(0.8, Blue), new GradientNode(-3, Red), new GradientNode(0.3, White), new GradientNode(0.9, Black)]);
        Assert.Equal([0, 0.3, 0.8, 1], g.Stops.Select(s => s.Offset));
        Assert.Equal([Red, White, Blue, Black], g.Stops.Select(s => s.Color));
    }

    [Fact]
    public void A_gradient_needs_two_colors_and_the_right_number_of_points()
    {
        Assert.Throws<ArgumentException>(() => new GradientPaint(GradientKind.Linear, GradientPaint.DefaultPoints(GradientKind.Linear), [new GradientNode(0, Red)]));
        Assert.Throws<ArgumentException>(() => new GradientPaint(GradientKind.Ellipse, GradientPaint.DefaultPoints(GradientKind.Linear), [new GradientNode(0, Red), new GradientNode(1, Blue)]));
    }

    [Fact]
    public void A_new_gradient_goes_from_the_fill_color_to_a_lighter_or_darker_variant()
    {
        var onLight = GradientPaint.Create(GradientKind.Linear, new DocColor(240, 200, 20));
        Assert.Equal(new DocColor(240, 200, 20), onLight.Stops[0].Color);
        Assert.True(onLight.Stops[1].Color.R < 240 && onLight.Stops[1].Color.G < 200);   // light yellow gets darker

        var onDark = GradientPaint.Create(GradientKind.Circle, new DocColor(20, 30, 90));
        Assert.True(onDark.Stops[1].Color.B > 90 && onDark.Stops[1].Color.R > 20);        // navy gets lighter
        Assert.Equal(GradientKind.Circle, onDark.Kind);
    }

    [Fact]
    public void Changing_the_kind_keeps_the_colors_and_restarts_the_control_points()
    {
        var g = Linear(Red, Blue, alternate: true).WithPoints([new Vec2(0.2, 0.2), new Vec2(0.4, 0.9)]);
        var v = g.WithKind(GradientKind.Ellipse);
        Assert.Equal(GradientKind.Ellipse, v.Kind);
        Assert.Equal(3, v.Points.Count);
        Assert.Equal(g.Stops, v.Stops);
        Assert.True(v.AlternateColors);
    }

    [Fact]
    public void The_control_points_are_relative_to_the_objects_bounds()
    {
        var g = Linear(Red, Blue).WithPoints([new Vec2(0, 0.5), new Vec2(1, 0.5)]);
        var small = g.Frame(new Rect2(10, 20, 110, 70));    // 100 x 50 at (10, 20)
        Assert.Equal(new Vec2(10, 45), small.P0);
        Assert.Equal(new Vec2(110, 45), small.P1);
        var big = g.Frame(new Rect2(0, 0, 400, 100));
        Assert.Equal(new Vec2(400, 50), big.P1);                                           // the same gradient follows a resized object
        Assert.Equal(new Vec2(0.25, 0.5), GradientPaint.ToRelative(new Vec2(35, 45), new Rect2(10, 20, 110, 70)));
        Assert.Equal(new Vec2(35, 45), GradientPaint.ToLocal(new Vec2(0.25, 0.5), new Rect2(10, 20, 110, 70)));
    }

    [Fact]
    public void Parameter_is_zero_at_the_start_or_center_and_one_at_the_end_or_radius()
    {
        var bounds = new Rect2(0, 0, 200, 100);
        var lin = Linear(Red, Blue).WithPoints([new Vec2(0, 0), new Vec2(1, 0)]).Frame(bounds);
        Assert.Equal(0, lin.Parameter(new Vec2(0, 77)), 9);
        Assert.Equal(0.5, lin.Parameter(new Vec2(100, 0)), 9);
        Assert.Equal(1.5, lin.Parameter(new Vec2(300, 0)), 9);                              // not clamped

        var circle = new GradientPaint(GradientKind.Circle, GradientPaint.DefaultPoints(GradientKind.Circle), [new GradientNode(0, Red), new GradientNode(1, Blue)]).Frame(bounds);
        Assert.Equal(0, circle.Parameter(new Vec2(100, 50)), 9);
        Assert.Equal(1, circle.Parameter(new Vec2(200, 50)), 9);
        Assert.Equal(1, circle.Parameter(new Vec2(100, 150)), 9);                           // a circle: the same distance up or down

        var ellipse = new GradientPaint(GradientKind.Ellipse, GradientPaint.DefaultPoints(GradientKind.Ellipse), [new GradientNode(0, Red), new GradientNode(1, Blue)]).Frame(bounds);
        Assert.Equal(1, ellipse.Parameter(new Vec2(200, 50)), 9);                           // end of the first axis (wide)
        Assert.Equal(1, ellipse.Parameter(new Vec2(100, 100)), 9);                          // end of the second axis (short)
        Assert.Equal(Math.Sqrt(0.5), ellipse.Parameter(new Vec2(150, 75)), 9);
    }

    [Fact]
    public void A_flat_or_zero_length_control_line_is_degenerate()
    {
        var g = Linear(Red, Blue).WithPoints([new Vec2(0.5, 0.5), new Vec2(0.5, 0.5)]);
        Assert.True(g.Frame(new Rect2(0, 0, 10, 10)).IsDegenerate);
    }

    // ---- the ramp ----

    [Fact]
    public void The_ramp_starts_and_ends_on_the_exact_colors()
    {
        var ramp = GradientRamp.Build(Linear(new DocColor(12, 99, 200), new DocColor(250, 40, 3)));
        Assert.Equal(0, ramp[0].T);
        Assert.Equal((12, 99, 200), (ramp[0].R, ramp[0].G, ramp[0].B));
        Assert.Equal(1, ramp[^1].T);
        Assert.Equal((250, 40, 3), (ramp[^1].R, ramp[^1].G, ramp[^1].B));
        Assert.True(ramp.Zip(ramp.Skip(1)).All(p => p.Second.T >= p.First.T));
    }

    [Fact]
    public void Colors_blend_in_hsv_the_short_way_round_the_wheel()
    {
        // Red (hue 0) to blue (hue 240): the short way is through magenta (hue 300), not through green.
        var (r, g, b) = At(GradientRamp.Build(Linear(Red, Blue)), 0.5);
        Assert.True(r > 200 && b > 200 && g < 40, $"middle was {(r, g, b)}");
    }

    [Fact]
    public void Alternate_colors_go_the_long_way_round_the_wheel()
    {
        // The long way from red to blue passes through yellow, green and cyan (hue 120 in the middle).
        var (r, g, b) = At(GradientRamp.Build(Linear(Red, Blue, alternate: true)), 0.5);
        Assert.True(g > 200 && r < 40 && b < 40, $"middle was {(r, g, b)}");
    }

    [Fact]
    public void A_white_or_black_end_takes_the_hue_of_the_other_end()
    {
        // White to blue must stay on blue's hue (R = G all along), never pick up another color on the way.
        foreach (var e in GradientRamp.Build(Linear(White, Blue)))
            Assert.True(Math.Abs(e.R - e.G) <= 1 && e.B >= e.R, $"{e}");
        // Black to red keeps red's hue: red always leads, and green and blue stay equal (no other color is picked up on the way).
        foreach (var e in GradientRamp.Build(Linear(Black, Red)))
            Assert.True(e.R >= e.G && Math.Abs(e.G - e.B) <= 1, $"{e}");
        // The same holds the other way round.
        foreach (var e in GradientRamp.Build(Linear(Blue, White)))
            Assert.True(Math.Abs(e.R - e.G) <= 1, $"{e}");
    }

    [Fact]
    public void A_black_end_takes_only_the_hue_so_black_to_red_goes_through_a_muted_dark_red()
    {
        // The legacy blend keeps black's zero saturation: halfway between black and red is hue 0, saturation 50 %, value 50 % (128, 64, 64),
        // not the pure dark red (128, 0, 0) a plain RGB mix would give.
        var mid = At2(GradientRamp.Build(Linear(Black, Red)), 0.5);
        Assert.InRange(mid.R, 124, 132);
        Assert.InRange(mid.G, 60, 68);
        Assert.InRange(mid.B, 60, 68);
    }

    [Fact]
    public void Alternate_colors_on_equal_hues_turn_a_full_circle_and_on_opposite_hues_change_nothing()
    {
        // Equal hues (here also a gray end that took the other's hue): the legacy blend goes the long way, which is the whole wheel.
        var dark = new DocColor(120, 0, 0);
        var ramp = GradientRamp.Build(Linear(Red, dark, alternate: true));
        var hues = ramp.Select(e => new DocColor(e.R, e.G, e.B).ToHsv().H).ToList();
        Assert.True(hues.Any(h => Math.Abs(h - 120) < 12) && hues.Any(h => Math.Abs(h - 240) < 12), "every hue shows up");
        Assert.All(GradientRamp.Build(Linear(Red, dark)), e => Assert.True(e.G <= 1 && e.B <= 1));   // without it the hue stays put

        // Exactly opposite hues (red and cyan): both ways are equally long, the legacy rule keeps the plain direction in both cases.
        var cyan = new DocColor(0, 255, 255);
        Assert.Equal(GradientRamp.Build(Linear(Red, cyan)), GradientRamp.Build(Linear(Red, cyan, alternate: true)));
    }

    [Fact]
    public void Alpha_blends_linearly_so_a_color_can_fade_to_nothing()
    {
        var ramp = GradientRamp.Build(Linear(new DocColor(10, 20, 80, 255), new DocColor(10, 20, 80, 0)));
        Assert.Equal(255, ramp[0].A);
        Assert.Equal(0, ramp[^1].A);
        Assert.InRange(At2(ramp, 0.5).A, 120, 135);
        Assert.All(ramp, e => Assert.Equal((10, 20, 80), (e.R, e.G, e.B)));   // the color itself does not drift while it fades
    }

    private static GradientRamp.Entry At2(IReadOnlyList<GradientRamp.Entry> ramp, double t) => ramp.OrderBy(x => Math.Abs(x.T - t)).First();

    [Fact]
    public void Reflect_runs_to_the_middle_of_the_line_and_back()
    {
        var ramp = GradientRamp.Build(Linear(Red, White, reflect: true));
        Assert.Equal((255, 0, 0), (ramp[0].R, ramp[0].G, ramp[0].B));
        Assert.Equal((255, 0, 0), (ramp[^1].R, ramp[^1].G, ramp[^1].B));
        var mid = At2(ramp, 0.5);
        Assert.Equal((255, 255, 255), (mid.R, mid.G, mid.B));
        var q1 = At2(ramp, 0.25); var q3 = At2(ramp, 0.75);
        Assert.InRange(Math.Abs(q1.G - q3.G), 0, 3);                 // symmetric about the middle
        Assert.True(ramp.Zip(ramp.Skip(1)).All(p => p.Second.T >= p.First.T));
    }

    [Fact]
    public void Several_stops_blend_segment_by_segment()
    {
        var g = new GradientPaint(GradientKind.Linear, GradientPaint.DefaultPoints(GradientKind.Linear),
            [new GradientNode(0, Red), new GradientNode(0.25, White), new GradientNode(1, Blue)]);
        var ramp = GradientRamp.Build(g);
        Assert.Equal((255, 255, 255), (At2(ramp, 0.25).R, At2(ramp, 0.25).G, At2(ramp, 0.25).B));
        Assert.Equal(0, At2(ramp, 1).R);
    }

    [Fact]
    public void Color_at_gives_the_blend_for_a_new_stop()
    {
        var g = Linear(Black, White);
        var mid = GradientRamp.ColorAt(g, 0.5);
        Assert.InRange(mid.R, 120, 135);
        Assert.Equal(Black, GradientRamp.ColorAt(g, -1));
        Assert.Equal(White, GradientRamp.ColorAt(g, 5));
    }

    [Fact]
    public void Remove_leaves_a_plain_fill_of_the_first_color()
    {
        var named = new DocColor(1, 2, 3, 255, "Navy", "Oracal 651");
        Assert.Equal(new SolidPaint(named), Linear(named, White).ToSolid());
    }

    [Fact]
    public void Job_colors_count_every_color_of_a_gradient()
    {
        var doc = new Document(PageSetup.FromInches(2, 2));
        doc.Insert(doc.Layers[0], -1, new RectangleObject(10, 10) { Style = new ObjectStyle(Linear(Red, Blue), null) });
        Assert.Equal([Red, Blue], JobColors.Collect(doc));
    }
}
