using System.Diagnostics;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using DeSignMage.Geometry.Internal;
using static DeSignMage.Geometry.Tests.Shapes;
using DeSignMage.TestSupport;

namespace DeSignMage.Geometry.Tests;

/// <summary>A real layout (<c>local-testdata/layouts/art-a.lyt</c>, read-only, via LytImporter): large shapes, many curves, text outlines.</summary>
public class RealShapeTests
{
    private static readonly IBooleanService B = GeometryEngines.Booleans;
    private static readonly IOffsetService O = GeometryEngines.Offsets;

    private static readonly Lazy<List<DocObject>> Vectors = new(() =>
        LytImporter.Open(Fixtures.Layout("art-a.lyt")).Document.AllObjects().Where(o => o is PathObject).ToList());

    private static PathGeometry World(DocObject o) => o.GetLocalGeometry()!.Transform(o.GetWorldTransform());

    private static DocObject PawObject() =>
        Vectors.Value.Where(o => o.Name == "Loop 1" && Loops(o.GetLocalGeometry()!) == 5).MaxBy(o => o.GetWorldBounds().Width)!;

    /// <summary>
    /// The big paw as placed in the layout (136.9 × 134.8 pt). The placement is stretched a little in x (the record's second scale value), so its pad arcs become
    /// ellipse pieces: in world space the paw is 42 cubics.
    /// </summary>
    private static PathGeometry Paw() => World(PawObject());

    /// <summary>The same paw in its own coordinates, scaled uniformly to the same size: a pad of 18 arcs and four toes of cubics.</summary>
    private static PathGeometry PawArcs() => PawObject().GetLocalGeometry()!.Transform(Affine2.Scale(65, 65));

    private static List<Seg> SegsOf(PathGeometry p) => SourceLoops.From(p, 1e-12).SelectMany(l => l.Segs).ToList();

    /// <summary>Every arc and cubic of <paramref name="result"/> lies on an arc or cubic of the sources (exact restore).</summary>
    private static void CurvesComeFromSources(PathGeometry result, params PathGeometry[] sources)
    {
        var src = sources.SelectMany(SegsOf).Where(s => s.IsCurve).ToList();
        foreach (var piece in SegsOf(result).Where(s => s.IsCurve))
        {
            for (int k = 0; k <= 8; k++)
            {
                var p = piece.Eval(k / 8.0);
                double d = src.Min(s => Vec2.Distance(s.Eval(s.Project(p, 0, 1)), p));
                Assert.True(d < 1e-7, $"{piece.Kind} point {p} is {d:E2} off every source curve");
            }
        }
    }

    [LocalFact("layouts/art-a.lyt")]
    public void The_paw_fixtures_are_the_expected_shapes()
    {
        var arcs = PawArcs();
        Assert.Equal(5, Loops(arcs));
        Assert.Equal(18, Count(arcs, PathVerb.ArcTo));
        Assert.Equal(24, Count(arcs, PathVerb.CubicTo));
        var world = Paw();
        Assert.Equal(5, Loops(world));
        Assert.Equal(0, Count(world, PathVerb.ArcTo));
        Assert.InRange(world.GetBounds().Width, 136.9, 137);
        Assert.InRange(world.GetBounds().Height, 134.7, 134.8);
    }

    [LocalFact("layouts/art-a.lyt")]
    public void Welding_the_paw_keeps_its_five_loops_and_its_exact_curves()
    {
        foreach (var paw in new[] { PawArcs(), Paw() })
        {
            var w = B.Union([paw]);
            Assert.Equal(5, Loops(w));
            Assert.Equal(Area(paw), Area(w), 6);
            Assert.Equal(Count(paw, PathVerb.LineTo), Count(w, PathVerb.LineTo));    // no curve became chords
            Assert.InRange(ArcList(w).Count, Count(paw, PathVerb.ArcTo) / 2, Count(paw, PathVerb.ArcTo));   // neighbours on one circle may merge
            CurvesComeFromSources(w, paw);
            var twice = B.Union([paw, paw]);
            Assert.Equal(Area(paw), Area(twice), 6);
            Assert.True(twice.Verbs.Count <= paw.Verbs.Count + 1);
        }
    }

    [LocalFact("layouts/art-a.lyt")]
    public void Weld_of_the_paw_with_an_overlapping_circle_restores_every_curve_exactly()
    {
        foreach (var paw in new[] { PawArcs(), Paw() })
        {
            var b = paw.GetBounds();
            var circle = Circle(new Vec2(b.Center.X, b.Top + b.Height * 0.45), b.Width * 0.18, arcs: 4, startDeg: 10);
            var w = B.Union([paw, circle]);
            Assert.True(Loops(w) < 6);
            Assert.Equal(Area(paw) + Area(circle) - Area(B.Intersect(paw, circle)), Area(w), 6);
            CurvesComeFromSources(w, paw, circle);
            Assert.Equal(0, Count(w, PathVerb.LineTo));
            Assert.True(w.Verbs.Count < 80, $"{w.Verbs.Count} verbs");
        }
    }

    [LocalFact("layouts/art-a.lyt")]
    public void Cookie_cutting_the_paw_with_a_circle_and_with_an_outlined_circle()
    {
        var paw = PawArcs();
        var b = paw.GetBounds();
        var cutter = Circle(new Vec2(b.Center.X + 10, b.Bottom - b.Height * 0.3), b.Width * 0.15);
        var cut = B.Difference(paw, cutter);
        Assert.Equal(Area(paw) - Area(B.Intersect(paw, cutter)), Area(cut), 6);
        CurvesComeFromSources(cut, paw, cutter);
        // Cookie Cutter with Outline 2 pt: the cutter grows first (an exact circle), then cuts.
        var grown = O.Offset(cutter, 2);
        Assert.All(ArcList(grown), a => Assert.Equal(b.Width * 0.15 + 2, a.Radius, 9));
        var cut2 = B.Difference(paw, grown);
        Assert.True(Area(cut2) < Area(cut));
        CurvesComeFromSources(cut2, paw, grown);
    }

    [LocalFact("layouts/art-a.lyt")]
    public void Knifing_the_paw_through_the_pad()
    {
        var paw = Paw();
        var b = paw.GetBounds();
        var islands = B.SplitIntoIslands(paw);
        Assert.Equal(5, islands.Count);
        var pad = islands[0];                            // the largest
        double y = pad.GetBounds().Center.Y;
        var pieces = B.SplitByLine(paw, new Vec2(b.Left - 10, y), new Vec2(b.Right + 10, y), new SplitOptions { Gap = 0.144 });
        int crossed = islands.Count(i => i.GetBounds().Top < y - 0.072 && i.GetBounds().Bottom > y + 0.072);
        Assert.Equal(5 + crossed, pieces.Count);         // every island the line crosses falls in two
        double blade = Area(B.Intersect(paw, Rect(b.Left - 10, y - 0.072, b.Width + 20, 0.144)));
        Assert.Equal(Area(paw) - blade, pieces.Sum(Area), 6);
        CurvesComeFromSources(B.Union(pieces), paw);
    }

    [LocalFact("layouts/art-a.lyt")]
    public void Outlining_the_paw_gives_a_clean_light_outline_at_the_distance()
    {
        var paw = Paw();
        const double d = 7.2;                            // 0.1 in
        var ol = O.Offset(paw, d);
        Assert.Equal(0, Count(ol, PathVerb.CubicTo));     // lines and arcs
        Assert.True(ol.Verbs.Count < 3 * paw.Verbs.Count, $"{ol.Verbs.Count} verbs");
        Assert.True(Area(ol) > Area(paw));
        Assert.Equal(Area(ol), Area(B.Union([ol])), 6);  // no self-overlaps
        // Every point of the outline is at the distance from the paw (miter tips may be further).
        foreach (var poly in ol.Flatten(0.01))
            foreach (var p in poly.Points)
                Assert.True(paw.DistanceToOutline(p, 0.002) > d - 0.02, $"{p} is closer than {d}");
        double within = ol.Flatten(0.01).SelectMany(x => x.Points).Average(p => Math.Abs(paw.DistanceToOutline(p, 0.002) - d) < 0.02 ? 1.0 : 0.0);
        Assert.True(within > 0.95, $"only {within:P0} of the outline is at the distance");
        // In the paw's own coordinates the pad is made of arcs: they offset to exact concentric arcs.
        var arcPaw = PawArcs();
        var arcOl = O.Offset(arcPaw, d);
        var padCircles = ArcList(arcPaw);
        int concentric = ArcList(arcOl).Count(a => padCircles.Any(c => Vec2.Distance(c.Center, a.Center) < 1e-6 && Math.Abs(Math.Abs(c.Radius - a.Radius) - d) < 1e-6));
        Assert.True(concentric >= 6, $"{concentric} concentric arcs");

        // Inline then outline (round): back to the paw.
        var round = new OffsetOptions { Corners = CornerStyle.Round };
        var back = O.Offset(O.Offset(paw, -1, round), 1, round);
        Assert.True(Hausdorff(back, paw, 0.1) < 0.05, $"{Hausdorff(back, paw, 0.1)}");
    }

    [LocalFact("layouts/art-a.lyt")]
    public void Our_outline_matches_the_outline_stored_in_the_layout_for_the_same_letters()
    {
        // The layout keeps "Loop 7" and "Loop 7 - Outlined 0 in." made from it in place (0.0028 in = 0.2016 pt, default corners).
        var src = World(Vectors.Value.First(o => o.Name == "Loop 7" && Math.Abs(o.GetWorldBounds().Left - 1949.183) < 0.01));
        var reference = World(Vectors.Value.First(o => o.Name == "Loop 7 - Outlined 0 in." && Math.Abs(o.GetWorldBounds().Left - 1948.981) < 0.01));
        var ours = O.Offset(src, 0.2016);
        Assert.True(Hausdorff(ours, reference, 0.01) < 0.025, $"Hausdorff {Hausdorff(ours, reference, 0.01)} pt");
        Assert.InRange(ours.Verbs.Count, reference.Verbs.Count * 0.8, reference.Verbs.Count * 1.2);
        Assert.InRange(Area(ours), Area(reference) * 0.998, Area(reference) * 1.002);
    }

    [LocalFact("layouts/art-a.lyt")]
    public void A_shape_welded_with_its_own_outline_is_the_outline()
    {
        var src = World(Vectors.Value.First(o => o.Name == "Loop 7" && Math.Abs(o.GetWorldBounds().Left - 1949.183) < 0.01));
        var reference = World(Vectors.Value.First(o => o.Name == "Loop 7 - Outlined 0 in." && Math.Abs(o.GetWorldBounds().Left - 1948.981) < 0.01));
        Assert.Equal(Area(B.Union([reference])), Area(B.Union([src, reference])), 6);
    }

    [LocalFact("layouts/art-a.lyt")]
    public void Welding_every_vector_object_in_the_layout_takes_well_under_a_second()
    {
        var all = Vectors.Value.Select(World).ToList();
        Assert.True(all.Sum(p => p.Verbs.Count) > 10_000);
        B.Union(all.Take(5).ToList());                   // warm up (JIT)
        var watch = Stopwatch.StartNew();
        var weld = B.Union(all);
        watch.Stop();
        Assert.False(weld.IsEmpty);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");

        // Outline of the most complex shape (a long line of text, about 600 cubics).
        var text = World(Vectors.Value.MaxBy(o => o.GetLocalGeometry()!.Verbs.Count)!);
        watch.Restart();
        var ol = O.Offset(text, 0.2);
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 1000, $"outline {watch.ElapsedMilliseconds} ms");
        Assert.True(ol.Verbs.Count < 4000, $"{ol.Verbs.Count} verbs");
    }
}
