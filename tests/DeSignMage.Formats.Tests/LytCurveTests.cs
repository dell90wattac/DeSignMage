using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using SkiaSharp;
using Xunit.Abstractions;
using DeSignMage.TestSupport;

namespace DeSignMage.Formats.Tests;

/// <summary>
/// Regression for "smooth shapes import as straight-segment polygons": the layout stores curves as circular arcs (flag 0x4) AND as cubic
/// Béziers (flag 0x400). The reader once knew only arcs and turned every Bézier into a chord.
/// </summary>
public class LytCurveTests(ITestOutputHelper output)
{
    // ---------------------------------------------------------------- the node encoding

    private static byte[] Node(uint flags, float x, float y, float f3, float f4, float f5, float f6)
    {
        var b = new byte[28];
        BitConverter.TryWriteBytes(b.AsSpan(0), flags);
        BitConverter.TryWriteBytes(b.AsSpan(4), x);
        BitConverter.TryWriteBytes(b.AsSpan(8), y);
        BitConverter.TryWriteBytes(b.AsSpan(12), f3);
        BitConverter.TryWriteBytes(b.AsSpan(16), f4);
        BitConverter.TryWriteBytes(b.AsSpan(20), f5);
        BitConverter.TryWriteBytes(b.AsSpan(24), f6);
        return b;
    }

    [Fact]
    public void A_bezier_node_keeps_its_end_point_and_both_control_points()
    {
        // end (4, 5); second control point where an arc keeps its centre (3, 6); first control point where an arc keeps its sweep (1, 2).
        var n = LytNode.Decode(Node(0x2440, 4, 5, 3, 6, 1, 2));

        Assert.Equal(LytNodeKind.Bezier, n.Kind);
        Assert.Equal((4f, 5f), (n.X, n.Y));
        Assert.Equal((1f, 2f), (n.C1X, n.C1Y));
        Assert.Equal((3f, 6f), (n.C2X, n.C2Y));
    }

    [Fact]
    public void Arcs_markers_moves_and_lines_are_still_what_they_were()
    {
        var arc = BitConverter.GetBytes(1.5).Concat(new byte[0]).ToArray();
        var arcNode = Node(0x2044, 1, 2, 3, 4, 0, 0);
        BitConverter.TryWriteBytes(arcNode.AsSpan(20), 1.5);

        Assert.Equal(LytNodeKind.Arc, LytNode.Decode(arcNode).Kind);
        Assert.Equal(1.5, LytNode.Decode(arcNode).Sweep);
        Assert.Equal(LytNodeKind.Char, LytNode.Decode(Node(0x80, 1, 0, 0, 10, 0, 0)).Kind);
        Assert.Equal(LytNodeKind.Move, LytNode.Decode(Node(0x2041, 1, 2, 0, 0, 0, 0)).Kind);
        Assert.Equal(LytNodeKind.Line, LytNode.Decode(Node(0x2042, 1, 2, 0, 0, 0, 0)).Kind);
        Assert.Equal(0, arc.Length - 8);
    }

    [Fact]
    public void Bezier_nodes_become_cubic_segments_with_y_flipped()
    {
        var path = LytImporter.BuildPath(
        [
            new LytNode(0x41, LytNodeKind.Move, 0, 0, 0, 0, 0),
            new LytNode(0x2440, LytNodeKind.Bezier, 3, 0, 2, 1, 0, 1, 1),     // c1 (1,1), c2 (2,1), end (3,0)
            new LytNode(0x4D0, LytNodeKind.Line, 0, 0, 0, 0, 0),
        ]);

        Assert.Equal([PathVerb.MoveTo, PathVerb.CubicTo, PathVerb.Close], path.Verbs);
        Assert.Equal(new Vec2(1, -1), path.Points[1]);
        Assert.Equal(new Vec2(2, -1), path.Points[2]);
        Assert.Equal(new Vec2(3, 0), path.Points[3]);
    }

    [LocalFact("layouts/sample.lyt")]
    public void A_curved_shape_is_not_taken_for_a_rectangle_image_frame()
    {
        Assert.False(LytImporter.TryGetRectangle(
        [
            new LytNode(0x41, LytNodeKind.Move, 0, 0, 0, 0, 0),
            new LytNode(0x2440, LytNodeKind.Bezier, 4, 0, 3, 0, 0, 1, 0),
            new LytNode(0x2042, LytNodeKind.Line, 4, 4, 0, 0, 0),
            new LytNode(0x2042, LytNodeKind.Line, 0, 4, 0, 0, 0),
        ], out _));
    }

    // ---------------------------------------------------------------- the user's sample

    [LocalFact("layouts/sample.lyt")]
    public void Sample_node_kinds_match_the_reference_reader()
    {
        // Counts from an independent parser on the same file (Bézier nodes were 9,609 of 24,075, all read as lines before).
        var nodes = LytParser.Parse(TestFiles.Sample).Objects.SelectMany(o => o.Nodes).ToList();

        Assert.Equal(9609, nodes.Count(n => n.Kind == LytNodeKind.Bezier));
        Assert.Equal(7900, nodes.Count(n => n.Kind == LytNodeKind.Arc));
        Assert.Equal(5144, nodes.Count(n => n.Kind == LytNodeKind.Line));
        Assert.Equal(1072, nodes.Count(n => n.Kind == LytNodeKind.Move));
        Assert.Equal(350, nodes.Count(n => n.Kind == LytNodeKind.Char));
    }

    private static DocObject PartFromFlatImport(int fileIndex, out LytObject source)
    {
        var result = LytImporter.Open(TestFiles.Sample, new LytImportOptions { PreserveGroups = false });
        source = result.Source.Objects[fileIndex];
        Assert.False(source.IsGroup);
        return result.Document.Layers[0].Children[fileIndex];        // flat import: same order as the file, nothing skipped
    }

    /// <summary>Largest deviation of the outline from a circle through its own centre, as a fraction of the radius.</summary>
    private static double RadialDeviation(PathGeometry geometry)
    {
        var poly = geometry.Flatten(0.01).Single().Points;
        var c = geometry.GetBounds().Center;
        var radii = poly.Select(p => Vec2.Distance(p, c)).ToList();
        return (radii.Max() - radii.Min()) / radii.Average();
    }

    [LocalFact("layouts/sample.lyt")]
    public void The_dot_of_the_i_is_a_round_dot_not_an_octagon()
    {
        // File object 84: "Loop 8", gold, 0.401 in across, eight Bézier segments. Chords between its end points make
        // an octagon, whose radius varies by 7.6 %. A true circle varies by well under 2 %.
        var dot = PartFromFlatImport(84, out var source);
        Assert.Equal("Loop 8", source.Name);
        Assert.Equal(8, source.Nodes.Count(n => n.Kind == LytNodeKind.Bezier));

        var path = Assert.IsType<PathObject>(dot);
        Assert.Equal(8, path.Geometry.Verbs.Count(v => v == PathVerb.CubicTo));
        Assert.DoesNotContain(PathVerb.LineTo, path.Geometry.Verbs);

        double deviation = RadialDeviation(path.Geometry.Transform(path.Transform));
        output.WriteLine($"radial deviation {deviation:P2}");
        Assert.True(deviation < 0.02, $"outline is {deviation:P1} from a circle");
        var b = path.GetWorldBounds();
        Assert.InRange(b.Width / b.Height, 0.97, 1.03);
        Assert.InRange(b.Width / 72, 0.39, 0.41);
    }

    [LocalFact("layouts/sample.lyt")]
    public void The_dot_of_the_i_renders_as_a_filled_circle()
    {
        var dot = PartFromFlatImport(84, out _);
        var doc = dot.Parent is Layer layer ? new Document(PageSetup.FromInches(25.25, 9.31)) : throw new InvalidOperationException();
        var clone = dot.Clone();
        doc.Insert(doc.Layers[0], -1, clone);
        var b = clone.GetWorldBounds();

        using var bmp = LytRenderTests.Render(doc, b.Inflate(2), 10);
        int gold = 0, total = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Red > 200 && p.Green > 150 && p.Blue < 90) gold++;
                total++;
            }
        double ideal = Math.PI * Math.Pow(b.Width / 2 * 10, 2);
        output.WriteLine($"gold pixels {gold}, ideal circle {ideal:0}");
        Assert.InRange(gold / ideal, 0.98, 1.02);        // an octagon would cover about 90 % of it
    }

    [LocalFact("layouts/sample.lyt")]
    public void The_big_paw_is_made_of_curves_with_no_chords()
    {
        // File object 163: "Loop 1", the large paw. One contour of arcs (the pad) and four closed Bézier contours (the toes).
        var paw = PartFromFlatImport(163, out var source);
        Assert.Equal("Loop 1", source.Name);
        Assert.Equal(47, source.Nodes.Length);

        var path = Assert.IsType<PathObject>(paw);
        var verbs = path.Geometry.Verbs;
        Assert.Equal(source.Nodes.Count(n => n.Kind == LytNodeKind.Arc), verbs.Count(v => v == PathVerb.ArcTo));
        Assert.Equal(source.Nodes.Count(n => n.Kind == LytNodeKind.Bezier), verbs.Count(v => v == PathVerb.CubicTo));
        Assert.True(verbs.Count(v => v == PathVerb.CubicTo) >= 24);
        Assert.DoesNotContain(PathVerb.LineTo, verbs);
        Assert.Equal(5, verbs.Count(v => v == PathVerb.MoveTo));
    }

    [LocalFact("layouts/sample.lyt")]
    public void Close_ups_for_looking_at_the_paw_and_the_letters()
    {
        var doc = LytImporter.Open(TestFiles.Sample).Document;

        // The big paw on the pasteboard, and the logo's "pi" with the i-dot on the page, side by side.
        var paw = PartFromFlatImport(163, out _).GetWorldBounds();
        using (var bmp = LytRenderTests.Render(doc, paw.Inflate(8), 4)) LytRenderTests.Save(bmp, "closeup-big-shape.png");
        var dotBounds = PartFromFlatImport(84, out _).GetWorldBounds();
        var area = new Rect2(dotBounds.Left - 160, dotBounds.Top - 20, dotBounds.Left + 150, dotBounds.Top + 240);
        using (var bmp = LytRenderTests.Render(doc, area, 3)) LytRenderTests.Save(bmp, "closeup-pi-and-dot.png");
        using (var bmp = LytRenderTests.Render(doc, doc.Page.Bounds.Inflate(2), 1)) LytRenderTests.Save(bmp, "closeup-page.png");
    }

    [LocalFact("layouts/sample.lyt")]
    public void No_shape_in_the_sample_is_a_bezier_polygon_in_disguise()
    {
        // Every imported Bézier node must produce a cubic, never a line.
        // Text is imported as outlines here (LiveText = false) so that all 9,609 Bézier nodes are shapes, as in the file.
        var result = LytImporter.Open(TestFiles.Sample, new LytImportOptions { PreserveGroups = false, LiveText = false });
        int cubics = 0;
        foreach (var o in result.Document.AllObjects().OfType<PathObject>()) cubics += o.Geometry.Verbs.Count(v => v == PathVerb.CubicTo);
        Assert.Equal(9609, cubics);
    }
}
