using DeSignMage.Core.Geometry;
using static DeSignMage.Geometry.Tests.Shapes;

namespace DeSignMage.Geometry.Tests;

public class CurveFitTests
{
    private static List<Vec2> Sample(Func<double, Vec2> f, int n, bool closed)
    {
        var pts = new List<Vec2>();
        int count = closed ? n : n + 1;
        for (int i = 0; i < count; i++) pts.Add(f((double)i / n));
        return pts;
    }

    [Fact]
    public void A_sampled_circle_becomes_two_exact_half_arcs()
    {
        var c = new Vec2(30, -7);
        var pts = Sample(t => c + new Vec2(Math.Cos(2 * Math.PI * t), Math.Sin(2 * Math.PI * t)) * 25, 300, closed: true);
        var r = CurveFit.Fit(pts, closed: true);
        var arcs = ArcList(r);
        Assert.Equal(2, arcs.Count);
        Assert.Equal(0, Count(r, PathVerb.LineTo));
        Assert.All(arcs, a => { Assert.Equal(25, a.Radius, 6); Assert.True(Vec2.Distance(a.Center, c) < 1e-6); });
        Assert.Equal(Math.PI * 625, Math.Abs(Area(r)), 3);
    }

    [Fact]
    public void Straight_runs_become_single_lines_and_corners_stay_sharp()
    {
        // A rectangle sampled every unit along its edges.
        var pts = new List<Vec2>();
        for (int x = 0; x < 20; x++) pts.Add(new Vec2(x, 0));
        for (int y = 0; y < 10; y++) pts.Add(new Vec2(20, y));
        for (int x = 20; x > 0; x--) pts.Add(new Vec2(x, 10));
        for (int y = 10; y > 0; y--) pts.Add(new Vec2(0, y));
        var r = CurveFit.Fit(pts, closed: true);
        Assert.Equal(0, Count(r, PathVerb.ArcTo));
        Assert.Equal(3, Count(r, PathVerb.LineTo));
        Assert.Equal(200, Math.Abs(Area(r)), 9);
        foreach (var corner in new Vec2[] { new(0, 0), new(20, 0), new(20, 10), new(0, 10) }) Assert.Contains(corner, r.Points);
    }

    [Fact]
    public void A_stadium_becomes_two_lines_and_two_half_circles()
    {
        // Two straight sides joined by half circles (tangent joints: no corners to find).
        var pts = new List<Vec2>();
        for (int i = 0; i <= 40; i++) pts.Add(new Vec2(i, 0));
        for (int i = 1; i < 60; i++) { double a = -Math.PI / 2 + Math.PI * i / 60; pts.Add(new Vec2(40 + 10 * Math.Cos(a), 10 + 10 * Math.Sin(a))); }
        for (int i = 40; i >= 0; i--) pts.Add(new Vec2(i, 20));
        for (int i = 1; i < 60; i++) { double a = Math.PI / 2 + Math.PI * i / 60; pts.Add(new Vec2(10 * Math.Cos(a), 10 + 10 * Math.Sin(a))); }
        var r = CurveFit.Fit(pts, closed: true);
        Assert.Equal(2, ArcList(r).Count);
        Assert.InRange(Count(r, PathVerb.LineTo), 1, 2);          // one side may be the closing line
        Assert.Equal(800 + Math.PI * 100, Math.Abs(Area(r)), 6);
    }

    [Fact]
    public void An_ellipse_is_approximated_within_tolerance_by_arcs_or_cubics()
    {
        var ellipse = PathGeometry.Ellipse(new Rect2(0, 0, 80, 40));
        foreach (bool cubics in new[] { false, true })
        {
            var opt = new CurveFitOptions { Tolerance = 0.01, PreferCubics = cubics };
            var r = CurveFit.Refit(ellipse, opt);
            Assert.True(Hausdorff(r, ellipse, 0.02) < 0.012, $"cubics={cubics}: {Hausdorff(r, ellipse, 0.02)}");
            Assert.True(r.Verbs.Count < (cubics ? 16 : 40), $"cubics={cubics}: {r.Verbs.Count} verbs");
            if (cubics) Assert.True(Count(r, PathVerb.CubicTo) >= 4);
            else Assert.Equal(0, Count(r, PathVerb.CubicTo));
        }
    }

    [Fact]
    public void Open_polylines_keep_their_end_points()
    {
        var pts = Sample(t => new Vec2(100 * t, 10 * Math.Sin(Math.PI * t)), 200, closed: false);
        var r = CurveFit.Fit(pts, closed: false);
        Assert.DoesNotContain(PathVerb.Close, r.Verbs);
        Assert.Equal(pts[0], r.Points[0]);
        Assert.Equal(pts[^1], r.Points[^1]);
        Assert.True(r.Verbs.Count < 20);
    }

    [Fact]
    public void Too_few_points_give_an_empty_path()
    {
        Assert.True(CurveFit.Fit([new Vec2(1, 1)], closed: false).IsEmpty);
        Assert.True(CurveFit.Fit([new Vec2(1, 1), new Vec2(1, 1)], closed: true).IsEmpty);
    }
}
