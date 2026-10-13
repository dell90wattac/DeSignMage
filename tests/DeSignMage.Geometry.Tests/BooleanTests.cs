using DeSignMage.Core.Geometry;
using static DeSignMage.Geometry.Tests.Shapes;

namespace DeSignMage.Geometry.Tests;

public class BooleanTests
{
    private static readonly IBooleanService B = GeometryEngines.Booleans;

    private static void Near(double expected, double actual, double relTol = 1e-9, double absTol = 1e-9) =>
        Assert.True(Math.Abs(expected - actual) <= Math.Max(absTol, relTol * Math.Abs(expected)),
            $"expected {expected:R}, got {actual:R} (diff {actual - expected:E3})");

    // ---- basics ----

    [Fact]
    public void Union_of_two_overlapping_squares_is_the_exact_octagon_outline()
    {
        var r = B.Union([Rect(0, 0, 10, 10), Rect(5, 5, 10, 10)]);
        Assert.Equal(1, Loops(r));
        Assert.Equal(175, Area(r), 9);
        Assert.Equal(0, Count(r, PathVerb.ArcTo) + Count(r, PathVerb.CubicTo));
        // 8 corners: MoveTo + 7 LineTo (the 8th side is the Close).
        Assert.Equal(7, Count(r, PathVerb.LineTo));
        Assert.Contains(new Vec2(10, 5), r.Points);
        Assert.Contains(new Vec2(5, 10), r.Points);
    }

    [Fact]
    public void Results_are_oriented_outer_positive_and_holes_negative_with_inner_loops_first()
    {
        // Frame = big square minus small square; given clockwise-on-screen and counter-clockwise inputs alike.
        var r = B.Difference(Rect(0, 0, 10, 10, clockwiseOnScreen: false), Rect(3, 3, 4, 4));
        Assert.Equal(2, Loops(r));
        Assert.Equal(84, Area(r), 9);
        var loops = SplitLoops(r);
        Assert.True(SignedArea(loops[0]) < 0, "hole first, negative");
        Assert.True(SignedArea(loops[1]) > 0, "outer last, positive");
        Assert.Equal(FillRule.NonZero, r.FillRule);
    }

    [Fact]
    public void Intersection_difference_and_xor_obey_the_area_identities()
    {
        var a = Circle(new Vec2(0, 0), 10);
        var b = Shapes.Concat(Rect(2, -4, 15, 8), Circle(new Vec2(-6, 7), 3));
        double ua = Area(B.Union([a, b])), ia = Area(B.Intersect(a, b));
        double da = Area(B.Difference(a, b)), db = Area(B.Difference(b, a)), xa = Area(B.Xor(a, b));
        Near(Area(a) + Area(B.Union([b])), ua + ia, 1e-9);
        Near(Area(a) - ia, da, 1e-9);
        Near(ua - ia, xa, 1e-9);
        Near(da + db, xa, 1e-9);
    }

    [Fact]
    public void Empty_and_degenerate_inputs_give_empty_results_without_throwing()
    {
        var empty = PathGeometry.EmptyPath;
        Assert.True(B.Union([]).IsEmpty);
        Assert.True(B.Union([empty]).IsEmpty);
        Assert.True(B.Intersect(Rect(0, 0, 5, 5), empty).IsEmpty);
        Assert.Equal(25, Area(B.Difference(Rect(0, 0, 5, 5), empty)), 9);
        Assert.True(B.Difference(empty, Rect(0, 0, 5, 5)).IsEmpty);
        // Zero area: a flat triangle, a single point, a zero-length line, an open line.
        var flat = Polygon((0, 0), (10, 0), (5, 0));
        var point = new PathBuilder().MoveTo(3, 3).Close().Build();
        var zeroLine = new PathBuilder().MoveTo(3, 3).LineTo(3, 3).Close().Build();
        var openLine = new PathBuilder().MoveTo(0, 0).LineTo(10, 10).Build();
        foreach (var p in new[] { flat, point, zeroLine, openLine })
        {
            Assert.True(B.Union([p]).IsEmpty);
            Assert.Equal(100, Area(B.Union([p, Rect(0, 0, 10, 10)])), 9);
        }
        Assert.Empty(B.SplitIntoIslands(empty));
        Assert.True(B.IntersectAll([]).IsEmpty);
    }

    [Fact]
    public void An_open_subpath_that_ends_at_its_start_counts_as_closed()
    {
        var b = new PathBuilder().MoveTo(0, 0).LineTo(10, 0).LineTo(10, 10).LineTo(0, 10).LineTo(0, 0).Build();
        Assert.Equal(100, Area(B.Union([b])), 9);
    }

    // ---- touching, coincident and tangent ----

    [Fact]
    public void Squares_sharing_an_edge_weld_into_one_rectangle_with_four_corners()
    {
        var r = B.Union([Rect(0, 0, 10, 10), Rect(10, 0, 10, 10)]);
        Assert.Equal(1, Loops(r));
        Assert.Equal(200, Area(r), 9);
        Assert.Equal(3, Count(r, PathVerb.LineTo));      // 4 corners, no point left in the middle of an edge
    }

    [Fact]
    public void Partly_coincident_edges_weld_cleanly()
    {
        // Second square shifted along the shared edge.
        var r = B.Union([Rect(0, 0, 10, 10), Rect(10, 4, 10, 10)]);
        Assert.Equal(1, Loops(r));
        Assert.Equal(200, Area(r), 9);
        Assert.Equal(7, Count(r, PathVerb.LineTo));
    }

    [Fact]
    public void Squares_touching_at_one_corner_stay_two_loops()
    {
        var r = B.Union([Rect(0, 0, 10, 10), Rect(10, 10, 10, 10)]);
        Assert.Equal(200, Area(r), 9);
        Assert.Equal(2, Loops(r));
    }

    [Fact]
    public void Externally_tangent_circles_keep_both_full_circles()
    {
        var a = Circle(new Vec2(0, 0), 10);
        var b = Circle(new Vec2(20, 0), 10);
        var r = B.Union([a, b]);
        Near(2 * Math.PI * 100, Area(r), 1e-9);
        Assert.Equal(0, Count(r, PathVerb.LineTo));
        Assert.All(ArcList(r), arc => Assert.Equal(10, arc.Radius, 9));
    }

    [Fact]
    public void An_internally_tangent_circle_disappears_in_the_union()
    {
        var big = Circle(new Vec2(0, 0), 10);
        var small = Circle(new Vec2(5, 0), 5, arcs: 3, startDeg: 17);
        var r = B.Union([big, small]);
        Near(Math.PI * 100, Area(r), 1e-9);
        Assert.Equal(1, Loops(r));
        Assert.All(ArcList(r), arc => { Assert.Equal(10, arc.Radius, 9); Assert.True(arc.Center.Length < 1e-9); });
        // and as a cutter it leaves a crescent with exactly the two circles' arcs
        var crescent = B.Difference(big, small);
        Near(Math.PI * 75, Area(crescent), 1e-9);
        Assert.Equal(0, Count(crescent, PathVerb.LineTo));
    }

    [Fact]
    public void A_line_tangent_to_a_circle_does_not_cut_it()
    {
        var c = Circle(new Vec2(0, 0), 10);
        var r = B.Union([c, Rect(-5, 10, 10, 5)]);           // square resting on top of the circle (y down: below)
        Near(Math.PI * 100 + 50, Area(r), 1e-9);
    }

    // ---- curves survive ----

    [Fact]
    public void Arcs_survive_a_union_exactly_on_their_original_circles()
    {
        var a = Circle(new Vec2(0, 0), 10);
        var b = Circle(new Vec2(12, 3), 7, arcs: 5, startDeg: 33);
        var r = B.Union([a, b]);
        Assert.Equal(1, Loops(r));
        Assert.Equal(0, Count(r, PathVerb.LineTo));
        Assert.Equal(0, Count(r, PathVerb.CubicTo));
        foreach (var arc in ArcList(r))
        {
            bool onA = Math.Abs(arc.Radius - 10) < 1e-9 && arc.Center.Length < 1e-9;
            bool onB = Math.Abs(arc.Radius - 7) < 1e-9 && Vec2.Distance(arc.Center, new Vec2(12, 3)) < 1e-9;
            Assert.True(onA || onB, $"arc r={arc.Radius} c={arc.Center} is on neither circle");
        }
        Near(TwoCircleUnion(10, 7, Math.Sqrt(153)), Area(r), 1e-9);
        // Light: about one arc per original arc, not hundreds of chords.
        Assert.InRange(r.Verbs.Count, 4, 14);
    }

    [Fact]
    public void Cut_points_land_on_the_exact_crossing_of_the_curves()
    {
        var a = Circle(new Vec2(0, 0), 10);
        var b = Circle(new Vec2(10, 0), 10);
        var r = B.Intersect(a, b);
        // The lens' corners are (5, ±sqrt(75)).
        double y = Math.Sqrt(75);
        Assert.Contains(r.Points, p => Vec2.Distance(p, new Vec2(5, y)) < 1e-12);
        Assert.Contains(r.Points, p => Vec2.Distance(p, new Vec2(5, -y)) < 1e-12);
    }

    [Fact]
    public void Cubics_are_kept_exactly_where_untouched_and_cut_pieces_stay_on_the_original_curve()
    {
        var round = PathGeometry.Rectangle(new Rect2(0, 0, 40, 30), 8);   // lines + 4 cubic corners
        var r = B.Union([round, Rect(15, -5, 10, 10)]);                     // a tab on the top edge, away from the corners
        Assert.Equal(4, Count(r, PathVerb.CubicTo));
        foreach (var (c1, c2, e) in Cubics(round))
            Assert.Contains(Cubics(r), x => x.C1 == c1 && x.C2 == c2 && x.E == e);

        // Cut through a corner: the remaining cubic piece must lie on the original corner curve.
        var cut = B.Difference(round, Rect(30, -5, 20, 10));
        var corners = SegsOf(round).Where(s => s.Kind == Internal.SegKind.Cubic).ToList();
        var pieces = SegsOf(cut).Where(s => s.Kind == Internal.SegKind.Cubic).ToList();
        Assert.Equal(4, pieces.Count);
        foreach (var piece in pieces)
            for (int k = 0; k <= 10; k++)
            {
                var p = piece.Eval(k / 10.0);
                double d = corners.Min(c => Vec2.Distance(c.Eval(c.Project(p, 0, 1)), p));
                Assert.True(d < 1e-9, $"{p} is {d:E2} off the original corner curves");
            }
        Near(Area(round) - Area(B.Intersect(round, Rect(30, -5, 20, 10))), Area(cut), 1e-9);
    }

    [Fact]
    public void KeepCurves_off_gives_lines_only()
    {
        var r = B.Union([Circle(new Vec2(0, 0), 10)], new BooleanOptions { KeepCurves = false });
        Assert.Equal(0, Count(r, PathVerb.ArcTo));
        Assert.True(Count(r, PathVerb.LineTo) > 20);
        Assert.Equal(Math.PI * 100, Area(r), 0);
    }

    // ---- idempotence ----

    [Fact]
    public void Union_with_itself_returns_the_same_shape_and_does_not_grow()
    {
        var shapes = new[]
        {
            Circle(new Vec2(3, 4), 10),
            Shapes.Concat(Rect(0, 0, 30, 20), Circle(new Vec2(15, 10), 5, clockwiseOnScreen: false)),
            PathGeometry.Rectangle(new Rect2(0, 0, 40, 30), 8),
        };
        foreach (var a in shapes)
        {
            var once = B.Union([a]);
            var twice = B.Union([a, a]);
            var again = B.Union([once]);
            Near(Area(once), Area(twice), 1e-12);
            Near(Area(once), Area(again), 1e-12);
            Assert.True(Hausdorff(once, a) < 1e-6);
            Assert.True(Hausdorff(twice, a) < 1e-6);
            Assert.True(twice.Verbs.Count <= a.Verbs.Count + 1, $"{twice.Verbs.Count} verbs from {a.Verbs.Count}");
            Assert.True(again.Verbs.Count <= once.Verbs.Count, "welding a weld adds nothing");
        }
    }

    [Fact]
    public void Welding_a_welded_result_again_changes_nothing()
    {
        var a = Circle(new Vec2(0, 0), 10);
        var b = Rect(5, -3, 12, 6);
        var w1 = B.Union([a, b]);
        var w2 = B.Union([w1, b]);
        Near(Area(w1), Area(w2), 1e-12);
        Assert.True(Hausdorff(w1, w2) < 1e-7);
        Assert.Equal(w1.Verbs.Count, w2.Verbs.Count);
    }

    // ---- holes and fill rules ----

    [Fact]
    public void Holes_are_respected_by_both_fill_rules()
    {
        // A ring: same direction (needs EvenOdd to have a hole) and opposite directions (hole under both rules).
        var sameDir = Shapes.Concat(Circle(new Vec2(0, 0), 10), Circle(new Vec2(0, 0), 5));
        var oppDir = Shapes.Concat(Circle(new Vec2(0, 0), 10), Circle(new Vec2(0, 0), 5, clockwiseOnScreen: false));
        double ring = Math.PI * 75;
        Near(ring, Area(B.Union([WithRule(sameDir, FillRule.EvenOdd)])), 1e-9);
        Near(Math.PI * 100, Area(B.Union([WithRule(sameDir, FillRule.NonZero)])), 1e-9);
        Near(ring, Area(B.Union([WithRule(oppDir, FillRule.NonZero)])), 1e-9);
        Near(ring, Area(B.Union([WithRule(oppDir, FillRule.EvenOdd)])), 1e-9);
        // The override wins over the path's own rule (Force Loop Direction = EvenOdd).
        Near(ring, Area(B.Union([WithRule(sameDir, FillRule.NonZero)], new BooleanOptions { FillRule = FillRule.EvenOdd })), 1e-9);
    }

    [Fact]
    public void A_shape_over_a_hole_fills_only_its_own_area()
    {
        var ring = Shapes.Concat(Rect(0, 0, 30, 30), Rect(10, 10, 10, 10, clockwiseOnScreen: false));
        var plug = Rect(12, 12, 6, 6);
        var r = B.Union([ring, plug]);
        Assert.Equal(800 + 36, Area(r), 9);
        Assert.Equal(3, Loops(r));          // outer, hole, plug island
        // Each operand is filled by itself first: a counter-clockwise plug does not cancel anything.
        var r2 = B.Union([ring, Rect(12, 12, 6, 6, clockwiseOnScreen: false)]);
        Assert.Equal(836, Area(r2), 9);
    }

    [Fact]
    public void Self_intersecting_loops_follow_the_fill_rule()
    {
        // Pentagram: NonZero fills the centre, EvenOdd leaves it empty.
        var star = new (double, double)[5];
        for (int i = 0; i < 5; i++)
        {
            double a = -Math.PI / 2 + i * 4 * Math.PI / 5;
            star[i] = (10 * Math.Cos(a), 10 * Math.Sin(a));
        }
        var p = Polygon(star);
        double nz = Area(B.Union([WithRule(p, FillRule.NonZero)]));
        double eo = Area(B.Union([WithRule(p, FillRule.EvenOdd)]));
        Assert.True(nz > eo + 10);
        // Bow tie: two lobes of opposite direction; NonZero keeps both, each oriented as an outer loop.
        var bowtie = Polygon((0, 0), (10, 10), (10, 0), (0, 10));
        var r = B.Union([bowtie]);
        Assert.Equal(2, Loops(r));
        Assert.Equal(50, Area(r), 9);
        Assert.All(SplitLoops(r), l => Assert.True(SignedArea(l) > 0));
    }

    [Fact]
    public void IntersectAll_keeps_what_every_shape_covers()
    {
        var r = B.IntersectAll([Rect(0, 0, 10, 10), Rect(5, 0, 10, 10), Rect(0, 5, 20, 10)]);
        Assert.Equal(25, Area(r), 9);
        Assert.True(B.IntersectAll([Rect(0, 0, 10, 10), Rect(20, 0, 10, 10)]).IsEmpty);
    }

    // ---- scale ----

    [Theory]
    [InlineData(1e-4, 0)]          // a hair of a point
    [InlineData(1, 0)]
    [InlineData(1e5, 0)]           // a 1.4 km banner
    [InlineData(10, 1e7)]          // small shapes far from the origin
    public void Tiny_huge_and_far_away_shapes_keep_their_accuracy(double size, double offset)
    {
        var o = new Vec2(offset, -offset / 3);
        var a = Circle(o, size);
        var b = Circle(o + new Vec2(size, size / 2), size * 0.7, arcs: 3, startDeg: 10);
        var r = B.Union([a, b]);
        Near(TwoCircleUnion(size, 0.7 * size, size * Math.Sqrt(1.25)), Area(r), 1e-8, 0);
        Assert.Equal(0, Count(r, PathVerb.LineTo));
        var s = B.Difference(Rect(o.X, o.Y, size, size), Rect(o.X + size / 4, o.Y + size / 4, size / 2, size / 2));
        Near(0.75 * size * size, Area(s), 1e-9, 0);
    }

    // ---- knife and islands ----

    [Fact]
    public void Knife_gap_mode_splits_a_rectangle_into_two_pieces_minus_the_blade()
    {
        var rect = Rect(0, 0, 40, 20);
        var pieces = B.SplitByLine(rect, new Vec2(20, -10), new Vec2(20, 30), new SplitOptions { Gap = 2 });
        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, p => Assert.Equal(19 * 20, Area(p), 9));
        // Going downwards (y down), screen-left is +x... Cross(dir, p - from) < 0 comes first.
        var dir = new Vec2(0, 1);
        Assert.True(Vec2.Cross(dir, pieces[0].GetBounds().Center - new Vec2(20, -10)) < 0);
    }

    [Fact]
    public void Knife_short_line_only_notches_unless_extended()
    {
        var rect = Rect(0, 0, 40, 20);
        var notched = B.SplitByLine(rect, new Vec2(20, -5), new Vec2(20, 10), new SplitOptions { Gap = 1 });
        Assert.Single(notched);
        Assert.Equal(800 - 10, Area(notched[0]), 9);
        var cut = B.SplitByLine(rect, new Vec2(20, -5), new Vec2(20, 10), new SplitOptions { Gap = 1, ExtendLine = true });
        Assert.Equal(2, cut.Count);
    }

    [Fact]
    public void Knife_overlap_mode_makes_two_pieces_that_overlap_by_the_amount()
    {
        var rect = Rect(0, 0, 40, 20);
        var pieces = B.SplitByLine(rect, new Vec2(10, 5), new Vec2(10, 6), new SplitOptions { Overlap = 4 });
        Assert.Equal(2, pieces.Count);
        double total = pieces.Sum(Area);
        Assert.Equal(800 + 4 * 20, total, 9);
        Assert.Equal(4 * 20, Area(B.Intersect(pieces[0], pieces[1])), 9);
    }

    [Fact]
    public void Knife_through_a_circle_keeps_exact_arcs_on_each_half()
    {
        var c = Circle(new Vec2(0, 0), 10);
        var pieces = B.SplitByLine(c, new Vec2(-20, 1), new Vec2(20, 1), new SplitOptions { Gap = 0.5 });
        Assert.Equal(2, pieces.Count);
        foreach (var p in pieces)
            Assert.All(ArcList(p), a => { Assert.Equal(10, a.Radius, 9); Assert.True(a.Center.Length < 1e-9); });
        // The blade (0.5 wide along y = 1) removes a strip; the rest is split above and below it.
        Assert.True(pieces.All(p => Count(p, PathVerb.LineTo) <= 1));
    }

    [Fact]
    public void A_cut_through_a_hole_gives_pieces_with_the_hole_parts_and_islands_split_correctly()
    {
        var ring = Shapes.Concat(Circle(new Vec2(0, 0), 10), Circle(new Vec2(0, 0), 5, clockwiseOnScreen: false));
        var pieces = B.SplitByLine(ring, new Vec2(-20, 0), new Vec2(20, 0), new SplitOptions { Gap = 0.2 });
        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, p => Assert.Equal(1, Loops(p)));     // each half is a "C" with no hole left

        var islands = B.SplitIntoIslands(Shapes.Concat(ring, Circle(new Vec2(0, 0), 2), Rect(20, 0, 3, 3)));
        Assert.Equal(3, islands.Count);                         // ring (with its hole), the dot inside, the square
        Assert.Equal(2, Loops(islands[0]));
        Near(Math.PI * 75, Area(islands[0]), 1e-9);
    }

    private static List<PathGeometry> SplitLoops(PathGeometry p)
    {
        var result = new List<PathGeometry>();
        var verbs = new List<PathVerb>();
        var pts = new List<Vec2>();
        int pi = 0;
        foreach (var v in p.Verbs)
        {
            if (v == PathVerb.MoveTo && verbs.Count > 0) { result.Add(new PathGeometry([.. verbs], [.. pts])); verbs.Clear(); pts.Clear(); }
            verbs.Add(v);
            int n = v switch { PathVerb.MoveTo or PathVerb.LineTo => 1, PathVerb.CubicTo => 3, PathVerb.ArcTo => 2, _ => 0 };
            for (int k = 0; k < n; k++) pts.Add(p.Points[pi++]);
        }
        if (verbs.Count > 0) result.Add(new PathGeometry([.. verbs], [.. pts]));
        return result;
    }

    private static List<Internal.Seg> SegsOf(PathGeometry p) =>
        Internal.SourceLoops.From(p, 1e-12).SelectMany(l => l.Segs).ToList();

    private static List<(Vec2 C1, Vec2 C2, Vec2 E)> Cubics(PathGeometry p)
    {
        var list = new List<(Vec2, Vec2, Vec2)>();
        int pi = 0;
        foreach (var v in p.Verbs)
        {
            switch (v)
            {
                case PathVerb.MoveTo: case PathVerb.LineTo: pi++; break;
                case PathVerb.ArcTo: pi += 2; break;
                case PathVerb.CubicTo: list.Add((p.Points[pi], p.Points[pi + 1], p.Points[pi + 2])); pi += 3; break;
            }
        }
        return list;
    }
}
