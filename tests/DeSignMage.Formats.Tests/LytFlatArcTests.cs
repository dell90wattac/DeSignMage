using DeSignMage.Core.Geometry;
using DeSignMage.Formats.Lyt;
using DeSignMage.TestSupport;

namespace DeSignMage.Formats.Tests;

/// <summary>
/// A layout stores a curve as arcs of a far centre: a piece 0.2 units long on a circle of radius 195 bulges by 0.00003. The coordinates are
/// 32-bit, so the end point misses the circle by about as much, and the three-point arc built from it used to come out as a circle
/// thousands of units wide.
/// </summary>
public class LytFlatArcTests
{
    private static LytNode Move(float x, float y) => new(0x41, LytNodeKind.Move, x, y, 0, 0, 0);

    private static LytNode Arc(float x, float y, float cx, float cy, double sweep) => new(0x44, LytNodeKind.Arc, x, y, cx, cy, sweep);

    [Fact]
    public void An_arc_of_a_far_centre_that_hardly_bulges_stays_a_short_segment()
    {
        var path = LytImporter.BuildPath([
            Move(0.224f, 1.5266f),
            Arc(0.3227f, 1.7241f, 175.3397f, -85.9256f, -0.00113),
            new LytNode(0x52, LytNodeKind.Line, 0.224f, 1.5266f, 0, 0, 0),
        ]);
        var b = path.GetBounds();
        Assert.True(b.Width < 1 && b.Height < 1, $"bounds {b}");
    }

    [Fact]
    public void A_real_arc_stays_an_arc()
    {
        // a quarter circle of radius 1 around the origin
        var path = LytImporter.BuildPath([
            Move(1, 0),
            Arc(0, 1, 0, 0, Math.PI / 2),
            new LytNode(0x52, LytNodeKind.Line, 1, 0, 0, 0, 0),
        ]);
        Assert.True(path.HasArcs);
    }

    [LocalFact("layouts/art-b.lyt")]
    public void No_shape_of_the_art_b_layout_is_much_larger_than_its_page()
    {
        var result = LytImporter.Open(TestFiles.Layout("art-b.lyt"));
        var page = result.Document.Page.Bounds;
        foreach (var o in result.Document.AllObjects().Where(o => o is not DeSignMage.Core.Model.GroupObject))
        {
            var b = o.GetWorldBounds();
            Assert.True(b.Width < page.Width * 3 && b.Height < page.Height * 3, $"'{o.Name}' is {b.Width:0} x {b.Height:0} pt on a {page.Width:0} x {page.Height:0} pt page");
        }
    }
}
