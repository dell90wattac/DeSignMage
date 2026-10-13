using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Tests;

/// <summary>
/// <c>DistanceToOutline(p, tol, world)</c> and <c>Contains(p, tol, includeOpen, world)</c> are the fast way hit testing asks "is this point near / inside
/// the shape once it is moved to its place": they must say what the plain methods say about <c>Transform(world)</c>.
/// </summary>
public class PickInSpaceTests
{
    private static PathGeometry RandomShape(Random rnd, bool arcs, bool closed)
    {
        var b = new PathBuilder { FillRule = rnd.Next(2) == 0 ? FillRule.NonZero : FillRule.EvenOdd };
        int subpaths = 1 + rnd.Next(3);
        for (int s = 0; s < subpaths; s++)
        {
            Vec2 P() => new(rnd.NextDouble() * 100, rnd.NextDouble() * 60);
            var start = P();
            b.MoveTo(start);
            int n = 3 + rnd.Next(6);
            for (int i = 0; i < n; i++)
            {
                switch (rnd.Next(arcs ? 3 : 2))
                {
                    case 0: b.LineTo(P()); break;
                    case 1: b.CubicTo(P(), P(), P()); break;
                    default:
                    {
                        // a real arc: a point on the way and an end that is not in line with the two
                        var from = b.Current;
                        var to = P();
                        var mid = (from + to) / 2 + new Vec2(-(to - from).Y, (to - from).X) * (0.1 + rnd.NextDouble() * 0.4);
                        b.ArcTo(mid, to);
                        break;
                    }
                }
            }
            if (closed || rnd.Next(2) == 0) b.Close();
            else if (rnd.Next(3) == 0) b.LineTo(start);   // open, but ends where it began
        }
        return b.Build();
    }

    private static readonly Affine2[] Worlds =
    [
        Affine2.Identity,
        Affine2.Translation(30, -20),
        Affine2.Scale(1.7, 1.7) * Affine2.Translation(5, 5),                    // similarity: arcs stay arcs
        Affine2.Rotation(0.7) * Affine2.Translation(40, 10),
        Affine2.Scale(0.55, 1.3) * Affine2.Translation(-8, 12),                 // stretched: arcs become ellipse pieces
        Affine2.Scale(1.9, 0.6) * Affine2.Rotation(-0.4) * Affine2.Translation(10, 90),
    ];

    [Fact]
    public void Distance_in_world_space_matches_moving_the_shape_first()
    {
        var rnd = new Random(11);
        int checkedCount = 0;
        for (int round = 0; round < 60; round++)
        {
            var shape = RandomShape(rnd, arcs: round % 2 == 0, closed: round % 3 == 0);
            foreach (var world in Worlds)
            {
                var moved = shape.Transform(world);
                for (int k = 0; k < 40; k++)
                {
                    var p = new Vec2(rnd.NextDouble() * 220 - 40, rnd.NextDouble() * 160 - 30);
                    double tol = 0.05 + rnd.NextDouble() * 2;
                    double expected = moved.DistanceToOutline(p, tol);
                    double all = shape.DistanceToOutline(p, tol, world);
                    Assert.Equal(expected, all, 6);

                    // With a limit: exact when within it, otherwise anything beyond it.
                    double limit = rnd.NextDouble() * 25;
                    double limited = shape.DistanceToOutline(p, tol, world, limit);
                    if (expected <= limit) Assert.Equal(expected, limited, 6);
                    else Assert.True(limited > limit, $"limit {limit}: expected more than that, got {limited} (true {expected})");
                    checkedCount++;
                }
            }
        }
        Assert.True(checkedCount > 10_000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Containment_in_world_space_matches_moving_the_shape_first(bool includeOpen)
    {
        var rnd = new Random(includeOpen ? 5 : 6);
        int inside = 0, outside = 0;
        for (int round = 0; round < 80; round++)
        {
            var shape = RandomShape(rnd, arcs: round % 2 == 0, closed: round % 4 != 3);
            foreach (var world in Worlds)
            {
                var moved = shape.Transform(world);
                for (int k = 0; k < 40; k++)
                {
                    var p = new Vec2(rnd.NextDouble() * 220 - 40, rnd.NextDouble() * 160 - 30);
                    double tol = 0.05 + rnd.NextDouble();
                    bool expected = moved.Contains(p, tol, includeOpen);
                    Assert.Equal(expected, shape.Contains(p, tol, includeOpen, world));
                    if (expected) inside++; else outside++;
                }
            }
        }
        Assert.True(inside > 500 && outside > 500, $"{inside} inside, {outside} outside: the shapes should give both");
    }

    [Fact]
    public void A_lone_point_and_a_closed_square_behave_like_the_plain_methods()
    {
        var dot = new PathBuilder().MoveTo(3, 4).Build();
        Assert.Equal(5, dot.DistanceToOutline(new Vec2(0, 0), 0.1, Affine2.Identity), 9);
        Assert.Equal(dot.Transform(Affine2.Translation(10, 0)).DistanceToOutline(new Vec2(0, 0), 0.1),
            dot.DistanceToOutline(new Vec2(0, 0), 0.1, Affine2.Translation(10, 0)), 9);

        var square = PathGeometry.Rectangle(new Rect2(0, 0, 10, 10));
        Assert.True(square.Contains(new Vec2(15, 5), 0.1, true, Affine2.Translation(10, 0)));
        Assert.False(square.Contains(new Vec2(5, 5), 0.1, true, Affine2.Translation(10, 0)));
        Assert.Equal(0, square.DistanceToOutline(new Vec2(10, 5), 0.1, Affine2.Translation(10, 0)), 9);
    }

    [Fact]
    public void The_arcs_as_cubics_copy_is_made_once_and_kept()
    {
        var arc = new PathBuilder().MoveTo(0, 0).ArcTo(new Vec2(5, 5), new Vec2(10, 0)).Close().Build();
        Assert.True(arc.HasArcs);
        var first = arc.WithArcsAsCubics();
        Assert.False(first.HasArcs);
        Assert.Same(first, arc.WithArcsAsCubics());
        Assert.Same(first, first.WithArcsAsCubics());

        var plain = PathGeometry.Rectangle(new Rect2(0, 0, 3, 3));
        Assert.Same(plain, plain.WithArcsAsCubics());
    }
}
