using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using DeSignMage.TestSupport;
using Xunit.Abstractions;

namespace DeSignMage.Formats.Tests;

/// <summary>
/// The fine detail of curves. A layout made by curve fitting (a long outline of thousands of short arcs) stores an arc as an end point, a
/// centre and a sweep, and the sweep is not always the angle the end point makes about the centre: it is a degree short in about a sixth of
/// them, half or double of it in some, and a dummy 4 to 5 degrees on a repeated point. An arc is drawn to its end point, on the circle around
/// its centre; it is read as a line only when it bulges less than 32-bit coordinates can tell apart.
/// </summary>
public class LytCurveDetailTests(ITestOutputHelper output)
{
    private static LytNode Move(double x, double y) => new(0x41, LytNodeKind.Move, (float)x, (float)y, 0, 0, 0);

    private static LytNode Arc(double x, double y, double cx, double cy, double sweep) => new(0x2044, LytNodeKind.Arc, (float)x, (float)y, (float)cx, (float)cy, sweep);

    private static LytNode Line(double x, double y, bool close = false) => new(close ? 0x52u : 0x42u, LytNodeKind.Line, (float)x, (float)y, 0, 0, 0);

    private static (double X, double Y) OnCircle(double radius, double degrees) =>
        (radius * Math.Cos(degrees * Math.PI / 180), radius * Math.Sin(degrees * Math.PI / 180));

    /// <summary>An arc of radius 5 around the origin from 100 to 80 degrees (clockwise, sweep -20), closed with its chord. The top of the circle (y = 5) is on it.</summary>
    private static PathGeometry TopArc(double storedSweepDegrees)
    {
        var (x0, y0) = OnCircle(5, 100);
        var (x1, y1) = OnCircle(5, 80);
        return LytImporter.BuildPath([Move(x0, y0), Arc(x1, y1, 0, 0, storedSweepDegrees * Math.PI / 180), Line(x0, y0, close: true)]);
    }

    [Theory]
    [InlineData(-20)]     // as stored by most arcs: the sweep is the angle of the end point
    [InlineData(-19)]     // a degree short, 1 in 6 arcs of the curve-fitted layout (before: the arc became a straight line)
    [InlineData(-10)]     // half of the angle
    [InlineData(-40)]     // double the angle
    public void An_arc_reaches_its_end_point_and_the_top_of_its_circle_whatever_its_sweep_says(double storedSweepDegrees)
    {
        var path = TopArc(storedSweepDegrees);
        Assert.True(path.HasArcs, "the arc was read as a line");
        var b = path.GetBounds();
        // y is negated on the way in: the top of the circle (file y = 5) is at -5
        Assert.Equal(-5, b.Top, 2);
        Assert.Equal(OnCircle(5, 100).X, b.Left, 3);
        Assert.Equal(OnCircle(5, 80).X, b.Right, 3);
    }

    [Fact]
    public void A_long_arc_stays_the_long_way_round()
    {
        // 270 degrees counter-clockwise from 0: reaches the top, the left and the bottom of the circle
        var (x1, y1) = OnCircle(2, 270);
        var path = LytImporter.BuildPath([Move(2, 0), Arc(x1, y1, 0, 0, 1.5 * Math.PI), Line(2, 0, close: true)]);
        var b = path.GetBounds();
        Assert.Equal(-2, b.Left, 2);
        Assert.Equal(2, b.Right, 2);
        Assert.Equal(-2, b.Top, 2);
        Assert.Equal(2, b.Bottom, 2);
    }

    [Fact]
    public void A_repeated_point_with_a_dummy_sweep_adds_nothing_to_the_outline()
    {
        // the second arc ends where it starts (a repeated point); its stored sweep (0.078 rad, centre 0.3 away) used to draw a little loop
        var path = LytImporter.BuildPath([
            Move(0, 0), Line(1, 0), Arc(1, 0, 1, 0.3, 0.078), Line(1, 1), Line(0, 1, close: true),
        ]);
        var b = path.GetBounds();
        Assert.Equal(0, b.Left, 6);
        Assert.Equal(1, b.Right, 6);
        Assert.Equal(-1, b.Top, 6);
        Assert.Equal(0, b.Bottom, 6);
    }

    // ---------------------------------------------------------------- the layout with the finest detail

    [LocalFact("layouts/art-f.lyt")]
    public void Every_visible_arc_of_the_fine_detail_layout_is_drawn_as_an_arc()
    {
        var file = LytParser.Parse(TestFiles.Layout("art-f.lyt"));
        int visible = 0, asLines = 0, arcNodes = 0;
        foreach (var o in file.Objects.Where(o => o.Nodes.Length > 0))
        {
            LytNode previous = default;
            foreach (var n in o.Nodes)
            {
                if (n.Kind == LytNodeKind.Char) continue;
                if (n.Kind == LytNodeKind.Arc)
                {
                    arcNodes++;
                    double radius = Math.Sqrt((double)(previous.X - n.Cx) * (previous.X - n.Cx) + (double)(previous.Y - n.Cy) * (previous.Y - n.Cy));
                    double angle = Math.Atan2(n.Y - n.Cy, n.X - n.Cx) - Math.Atan2(previous.Y - n.Cy, previous.X - n.Cx);
                    while (angle > Math.PI) angle -= 2 * Math.PI;
                    while (angle <= -Math.PI) angle += 2 * Math.PI;
                    // bulges by more than 0.0001 of a node unit (a tenth of a thousandth of an inch) and more than 32-bit coordinates can tell
                    // apart (an arc of a far centre), and is not a repeated point
                    double magnitude = new[] { 1.0, Math.Abs(previous.X), Math.Abs(previous.Y), Math.Abs(n.X), Math.Abs(n.Y), Math.Abs(n.Cx), Math.Abs(n.Cy) }.Max();
                    double bulge = radius * (1 - Math.Cos(Math.Abs(angle) / 2));
                    if (bulge > 1e-4 && bulge > 2e-6 * magnitude && Math.Abs(angle) < 3)
                    {
                        visible++;
                        var one = LytImporter.BuildPath([new LytNode(0x41, LytNodeKind.Move, previous.X, previous.Y, 0, 0, 0), n]);
                        if (one.Verbs.Last(v => v != PathVerb.Close) != PathVerb.ArcTo) asLines++;
                    }
                }
                previous = n;
            }
        }
        output.WriteLine($"{arcNodes} arc nodes, {visible} bulge visibly, {asLines} of them drawn as lines");
        Assert.Equal(0, asLines);
    }
    [LocalFact("layouts/art-f.lyt")]
    public void The_info_line_numbers_of_the_fine_detail_layout_are_287_objects_19_groups_and_9_67_MB_of_bitmaps()
    {
        var doc = LytImporter.Open(TestFiles.Layout("art-f.lyt")).Document;
        Assert.Equal(19, doc.GroupCount);
        Assert.Equal(287, doc.ObjectCount - doc.GroupCount);    // the file counts its 19 group records among its 306 records
        double megabytes = BitmapStats.TotalBytes(doc) / 1048576.0;
        output.WriteLine($"bitmaps {megabytes:0.00} MB");
        Assert.InRange(megabytes, 9.6, 9.75);                    // one embedded JPEG, counted decoded (width x height x 4 bytes)
    }

    [LocalFact("layouts/art-f.lyt")]
    public void The_loops_that_are_holes_by_their_flags_are_the_loops_inside_another_loop()
    {
        var file = LytParser.Parse(TestFiles.Layout("art-f.lyt"));
        int loops = 0, differ = 0;
        foreach (var o in file.Objects.Where(o => o.Nodes.Length > 0))
        {
            var holeFlags = new List<bool>();
            bool? current = null;
            foreach (var n in o.Nodes.Where(n => n.Kind != LytNodeKind.Char))
            {
                if (n.Kind == LytNodeKind.Move) { if (current is not null) holeFlags.Add(current.Value); current = (n.Flags & LytNode.FlagHole) != 0; }
                else current ??= (n.Flags & LytNode.FlagHole) != 0;
            }
            if (current is not null) holeFlags.Add(current.Value);
            var list = PathEditing.ToLoops(LytImporter.BuildPath(o.Nodes));
            var kinds = PathEditing.Classify(list);
            if (list.Count != holeFlags.Count) { differ += list.Count; continue; }
            for (int i = 0; i < list.Count; i++)
            {
                loops++;
                if ((kinds[i] == LoopKind.Interior) != holeFlags[i]) differ++;
            }
        }
        output.WriteLine($"{loops} loops, {differ} where the flag and the nesting disagree");
        Assert.True(differ <= 2, $"{differ} of {loops} loops are holes by one rule and not by the other");
    }
}
