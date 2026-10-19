using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using Xunit.Abstractions;
using DeSignMage.TestSupport;

namespace DeSignMage.Formats.Tests;

public class LytImporterTests(ITestOutputHelper output)
{
    // ---------------------------------------------------------------- the real sample

    [LocalFact("layouts/sample.lyt")]
    public void Sample_imports_into_a_document_with_the_same_structure_as_the_file()
    {
        var result = LytImporter.Open(TestFiles.Sample);
        var doc = result.Document;

        Assert.Equal(237, result.PartCount);
        Assert.Equal(46, result.GroupCount);   // every group of the file is kept, however its members are interleaved with other objects (R22)
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(237 + result.GroupCount, doc.ObjectCount);
        Assert.Equal(result.Source.PageWidth * 72, doc.Page.Width, 2);
        Assert.Equal(result.Source.PageHeight * 72, doc.Page.Height, 2);
        Assert.Single(doc.Layers);
        foreach (var w in result.Warnings) output.WriteLine("warning: " + w);
    }

    [LocalFact("layouts/sample.lyt")]
    public void Sample_group_tree_matches_the_file_group_by_group()
    {
        var result = LytImporter.Open(TestFiles.Sample);
        var records = result.Source.Objects.Where(o => o.IsGroup).ToDictionary(o => o.GroupId);

        var imported = result.Document.AllObjects().OfType<GroupObject>().ToList();
        Assert.Equal(records.Count, imported.Count);

        // The groups are the same set: each holds exactly the number of direct children its record declares.
        var expected = records.Values.Select(r => (r.Name, r.NodeCount)).OrderBy(x => x.Name).ThenBy(x => x.NodeCount);
        var actual = imported.Select(g => (Name: g.Name ?? "", g.Children.Count)).OrderBy(x => x.Name).ThenBy(x => x.Count);
        Assert.Equal(expected, actual);
        Assert.Equal(records.Values.Sum(r => r.NodeCount), imported.Sum(g => g.Children.Count));
        Assert.Equal(result.PartCount, result.Document.AllObjects().Count(o => o is not GroupObject));
    }

    [LocalFact("layouts/sample.lyt")]
    public void Sample_page_content_lies_where_the_file_says()
    {
        var doc = LytImporter.Open(TestFiles.Sample).Document;

        // The black background rectangle is the first object. Values from the reference parser:
        // org (24.956, 2.249) in, 0.996 in per unit, 24.7 x 4.775 units, page 25.2549 x 9.3069 in.
        var bg = doc.Layers[0].Children[0];
        var b = bg.GetWorldBounds();
        Assert.InRange(b.Left, 25.0, 26.0);
        Assert.InRange(b.Top, 165.2, 166.2);
        Assert.InRange(b.Right, 1796.4, 1797.4);
        Assert.InRange(b.Bottom, 507.6, 508.6);

        // Test variants sit to the right of the page, as on a pasteboard.
        var content = doc.GetContentBounds();
        Assert.True(content.Right > doc.Page.Width);
    }

    [LocalFact("layouts/art-a.lyt")]
    public void Colors_become_named_document_colors()
    {
        var doc = LytImporter.Open(TestFiles.Layout("art-a.lyt")).Document;

        var named = doc.AllObjects().Select(o => (o.Style.Fill as SolidPaint)?.Color).Where(c => c is { Name: not null }).ToList();
        Assert.Contains(named, c => c!.Value.Name == "ARLON 83 Bright Orange");
        Assert.Equal(new DocColor(255, 188, 17, 255, "ARLON 83 Bright Orange"),
            named.First(c => c!.Value.Name == "ARLON 83 Bright Orange"));
    }

    [LocalFact("layouts/design2.lyt")]
    public void Shapes_without_a_color_are_unfilled_hairlines_and_do_not_raise_warnings()
    {
        // Cut paths and "Outlined" objects have no color; they must stay visible but must not be filled black.
        var result = LytImporter.Open(TestFiles.Layout("design2.lyt"));

        var cut = result.Document.AllObjects().Single(o => o.Name == "Contour Cut Path");
        Assert.Null(cut.Style.Fill);
        Assert.NotNull(cut.Style.Stroke);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("color"));
    }

    [LocalFact("layouts/open.lyt")]
    public void The_second_color_record_is_used_when_the_main_one_is_unset()
    {
        // open.lyt keeps ARLON "73 Poppy" (239, 42, 17) only in the second record.
        var doc = LytImporter.Open(TestFiles.Layout("open.lyt")).Document;

        var fills = doc.AllObjects().Select(o => (o.Style.Fill as SolidPaint)?.Color).ToList();
        Assert.All(fills, c => Assert.NotNull(c));
        Assert.Contains(fills, c => c!.Value is { R: 239, G: 42, B: 17, Name: "ARLON 73 Poppy" });
    }

    [LocalFact("layouts/image2.lyt")]
    public void Embedded_images_become_image_objects_with_their_pixels_in_the_asset_store()
    {
        var result = LytImporter.Open(TestFiles.Layout("image2.lyt"));
        var images = result.Document.AllObjects().OfType<ImageObject>().ToList();

        // Three parts have an image fill; two are plain rectangles (images), the third is the picture clipped
        // to a non-rectangular shape, which is not an ImageObject yet and is reported.
        Assert.Equal(2, images.Count);
        Assert.Contains(result.Warnings, w => w.Contains("image fill that is not a plain rectangle"));
        Assert.All(images, i => Assert.NotNull(result.Document.Assets.Get(i.AssetId)));
        Assert.Equal(2, result.Document.Assets.All.Count());
        Assert.All(result.Document.Assets.All, a => Assert.Equal("image/jpeg", a.MediaType));
        Assert.All(images, i => Assert.True(i.GetWorldBounds().Width > 10));
    }

    [LocalFact("layouts/art-b.lyt")]
    public void Guides_are_imported_in_document_space()
    {
        var result = LytImporter.Open(TestFiles.Layout("art-b.lyt"));
        var guide = Assert.Single(result.Document.Guides);

        // Layout: a vertical guide through x = 29.743 in. Here: points from the page's left edge.
        Assert.Equal(GuideOrientation.Vertical, guide.Orientation);
        Assert.InRange(guide.Origin.X, 29.743 * 72 - 0.1, 29.743 * 72 + 0.1);
    }

    [LocalFact("layouts/sample.lyt")]
    public void Flat_import_keeps_every_part_and_no_groups()
    {
        var result = LytImporter.Open(TestFiles.Sample, new LytImportOptions { PreserveGroups = false });

        Assert.Equal(0, result.GroupCount);
        Assert.Equal(237, result.Document.ObjectCount);
        Assert.Equal(237, result.Document.Layers[0].Children.Count);
    }

    [LocalTheory("layouts/effects.lyt", "layouts/manifest.json", "layouts/text.lyt", "layouts/text2.lyt")]
    [MemberData(nameof(Cases))]
    public void Every_shipped_layout_imports_without_losing_parts(string relativePath, int _)
    {
        var result = LytImporter.Open(TestFiles.Layout(relativePath));
        var parts = result.Source.Objects.Count(o => !o.IsGroup);

        Assert.Equal(parts, result.PartCount + result.SkippedCount);
        if (result.PartCount == 0)
        {
            // Three early first-generation files keep extra data after the outline that is not understood yet: reported, not imported.
            Assert.Contains(relativePath, new[] { "effects.lyt", "text.lyt", "text2.lyt" });
            Assert.Contains(result.Warnings, w => w.Contains("could not be located"));
        }
        // the soft shadows and glows of the layout are separate objects here, each in a group with its source
        Assert.Equal(result.PartCount + result.GroupCount + result.SoftEffectCount + result.SoftEffectGroupCount, result.Document.ObjectCount);
        foreach (var w in result.Warnings) output.WriteLine($"{relativePath}: {w}");
    }

    public static IEnumerable<object[]> Cases() => TestFiles.LayoutCases();

    // ---------------------------------------------------------------- coordinate conversion

    private static LytObject Part(double orgX, double orgY, double scale, double xScale, double rotation, params LytNode[] nodes) =>
        new()
        {
            Name = "t", OrgX = orgX, OrgY = orgY, Scale = scale, XScale = xScale, Rotation = rotation,
            Nodes = nodes, NodeCount = nodes.Length, Style = new LytStyle { Color = new LytColor("", "", 1, 255, 0, 0, 0) },
        };

    [Fact]
    public void Part_transform_puts_node_points_on_the_page_with_y_flipped_and_rotation_counter_clockwise()
    {
        // Page 10 in tall = 720 pt. Part at (1 in, 1 in), 0.5 in per node unit, x stretched x2, turned 90 degrees ccw.
        var xf = LytImporter.PartTransform(Part(1, 1, 0.5, 2, Math.PI / 2), 720);

        // Node point (10, 0) -> layout page (1, 1) + R90 * (10, 0) = (1, 11) in -> (72, 720 - 792) pt. Local y is negated: (10, -0).
        var p = xf.Transform(new Vec2(10, 0));
        Assert.Equal(72, p.X, 6);
        Assert.Equal(-72, p.Y, 6);

        // Node point (0, 10) -> (1, 1) + R90 * (0, 5) = (-4, 1) in -> (-288, 648) pt. Local: (0, -10).
        var q = xf.Transform(new Vec2(0, -10));
        Assert.Equal(-288, q.X, 6);
        Assert.Equal(648, q.Y, 6);

        Assert.True(xf.Determinant > 0);
    }

    [Fact]
    public void Unrotated_part_keeps_up_up()
    {
        var o = Part(2, 3, 1, 1, 0);
        var xf = LytImporter.PartTransform(o, 720);

        // A point 1 node unit above the origin ends up 72 pt higher on screen, i.e. smaller y.
        Assert.Equal(720 - 3 * 72 - 72, xf.Transform(new Vec2(0, -1)).Y, 6);
    }

    // ---------------------------------------------------------------- paths

    private static LytNode Move(float x, float y, uint extra = 0) => new(0x1 | extra, LytNodeKind.Move, x, y, 0, 0, 0);
    private static LytNode Line(float x, float y, uint extra = 0) => new(0x2 | extra, LytNodeKind.Line, x, y, 0, 0, 0);
    private static LytNode Arc(float x, float y, float cx, float cy, double sweep, uint extra = 0) => new(0x4 | extra, LytNodeKind.Arc, x, y, cx, cy, sweep);

    [Fact]
    public void Half_circle_arc_passes_through_the_right_point_after_the_y_flip()
    {
        // From (0,0) around centre (1,0), counter-clockwise (y up) by 180 degrees: it dips below the x axis (to y = -1).
        var path = LytImporter.BuildPath([Move(0, 0), Arc(2, 0, 1, 0, Math.PI, LytNode.FlagClose)]);

        Assert.Equal([PathVerb.MoveTo, PathVerb.ArcTo, PathVerb.Close], path.Verbs);
        var through = path.Points[1];
        Assert.Equal(1, through.X, 9);
        Assert.Equal(1, through.Y, 9);                      // y negated: below in the layout = larger y here
        Assert.Equal(new Vec2(2, 0), path.Points[2]);
        Assert.True(path.Contains(new Vec2(1, 0.5)));
        Assert.False(path.Contains(new Vec2(1, -0.5)));
    }

    [Fact]
    public void Full_circle_is_two_half_arcs()
    {
        var path = LytImporter.BuildPath([Move(2, 0), Arc(2, 0, 1, 0, 2 * Math.PI, LytNode.FlagClose)]);

        Assert.Equal(2, path.Verbs.Count(v => v == PathVerb.ArcTo));
        var b = path.GetBounds();
        Assert.Equal(0, b.Left, 6);
        Assert.Equal(2, b.Right, 6);
        Assert.Equal(-1, b.Top, 6);
        Assert.Equal(1, b.Bottom, 6);
    }

    [Fact]
    public void A_closing_line_back_to_the_start_is_left_to_close_and_markers_are_not_geometry()
    {
        var marker = new LytNode(0x80, LytNodeKind.Char, 5, 0, 0, 10, 3);
        var path = LytImporter.BuildPath([marker, Move(0, 0), Line(4, 0), Line(4, 3), Line(0, 0, LytNode.FlagClose), marker]);

        Assert.Equal([PathVerb.MoveTo, PathVerb.LineTo, PathVerb.LineTo, PathVerb.Close], path.Verbs);
        Assert.Equal(4, path.GetBounds().Right, 9);
    }

    [Fact]
    public void Outer_contour_and_hole_with_opposite_winding_cut_out_with_the_non_zero_rule()
    {
        // Clockwise 10x10 square with a counter-clockwise 4x4 hole (y-up view).
        var path = LytImporter.BuildPath(
        [
            Move(0, 0, LytNode.FlagOuter), Line(0, 10), Line(10, 10), Line(10, 0), Line(0, 0, LytNode.FlagClose | LytNode.FlagOuter),
            Move(3, 3, LytNode.FlagHole), Line(7, 3), Line(7, 7), Line(3, 7), Line(3, 3, LytNode.FlagClose | LytNode.FlagHole),
        ]);

        Assert.Equal(FillRule.NonZero, path.FillRule);
        Assert.True(path.Contains(new Vec2(1, -1)));
        Assert.False(path.Contains(new Vec2(5, -5)));
    }

    [Fact]
    public void Axis_aligned_rectangles_are_recognised_for_image_frames()
    {
        Assert.True(LytImporter.TryGetRectangle([Move(1, 2), Line(1, 6), Line(5, 6), Line(5, 2), Line(1, 2, LytNode.FlagClose)], out var r));
        Assert.Equal(new Rect2(1, -6, 5, -2), r);

        Assert.False(LytImporter.TryGetRectangle([Move(0, 0), Line(2, 1), Line(5, 6), Line(5, 2), Line(0, 0)], out _));      // slanted
        Assert.False(LytImporter.TryGetRectangle([Move(0, 0), Arc(2, 0, 1, 0, Math.PI)], out _));                             // arc
    }

    // ---------------------------------------------------------------- groups and robustness (synthetic files)

    private static LytFile Synthetic(params LytObject[] objects)
    {
        var f = new LytFile { FormatVersion = 0x4A, VersionName = "7.0", PageWidth = 10, PageHeight = 10, DeclaredObjectCount = objects.Length };
        f.Layers.Add(new LytLayer(0, "Layer 1", default, 0));
        f.Objects.AddRange(objects);
        return f;
    }

    private static LytObject Square(string name, int parent = 0) => new()
    {
        Name = name, ParentGroup = parent, Scale = 1, XScale = 1, NodeCount = 5, OrgX = 1, OrgY = 1,
        Nodes = [Move(0, 0), Line(1, 0), Line(1, 1), Line(0, 1), Line(0, 0, LytNode.FlagClose)],
        Style = new LytStyle { Color = new LytColor("", "", 1, 0, 0, 255, 0) },
    };

    private static LytObject Group(int id, int parent, int children) => new() { IsGroup = true, Name = $"G{id}", GroupId = id, ParentGroup = parent, NodeCount = children, Type = 2 };

    [Fact]
    public void Nested_groups_are_rebuilt_and_every_object_keeps_its_place_in_the_drawing_order()
    {
        var f = Synthetic(Square("a"), Square("b", parent: 2), Square("c", parent: 3), Square("d", parent: 2), Group(2, 0, 3), Group(3, 2, 1));
        var result = LytImporter.Import(f);
        var layer = result.Document.Layers[0];

        Assert.Equal(["a", "b", "c", "d"], layer.Children.Select(c => c.Name));   // the groups are not in the drawing order
        var g2 = Assert.Single(result.Document.Groups, g => g.Name == "G2");
        Assert.Same(layer, g2.Parent);
        Assert.Equal(["b", "G3", "d"], g2.Children.Select(c => c.Name));
        var g3 = Assert.IsType<GroupObject>(g2.Children[1]);
        Assert.Equal("c", Assert.Single(g3.Children).Name);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Dangling_cyclic_and_duplicate_groups_do_not_lose_objects_and_are_reported()
    {
        var f = Synthetic(Square("a", parent: 99), Square("b", parent: 5), Square("c", parent: 6), Group(5, 6, 1), Group(6, 5, 1), Group(7, 0, 0));
        var result = LytImporter.Import(f);

        Assert.Equal(3, result.PartCount);
        Assert.Equal(3, result.Document.AllObjects().Count(o => o is not GroupObject));
        Assert.Contains(result.Warnings, w => w.Contains("group 99"));
        Assert.Contains(result.Warnings, w => w.Contains("contains itself"));
        Assert.Equal(0, result.Document.AllObjects().OfType<GroupObject>().Count(g => !g.Children.Any()));   // no empty groups
    }

    [Fact]
    public void Broken_parts_are_skipped_with_a_warning_and_the_rest_still_imports()
    {
        var bad = Square("bad");
        bad = new LytObject { Name = "bad", Scale = 0, XScale = 1, Nodes = bad.Nodes, NodeCount = bad.NodeCount, Style = bad.Style };
        var nan = new LytObject { Name = "nan", Scale = 1, XScale = 1, Nodes = [Move(float.NaN, 0), Line(1, 1)], NodeCount = 2 };
        var result = LytImporter.Import(Synthetic(Square("ok"), bad, nan));

        Assert.Equal(1, result.PartCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void Layers_come_in_bottom_to_top_order_and_objects_follow_their_layer_id()
    {
        var f = Synthetic();
        f.Layers.Clear();
        f.Layers.Add(new LytLayer(4, "Contour Cut", default, 0));      // the header lists the top layer first
        f.Layers.Add(new LytLayer(0, "Layer 1", default, 0));
        var cut = Square("cut");
        cut = new LytObject { Name = "cut", Scale = 1, XScale = 1, Nodes = cut.Nodes, NodeCount = 5, Style = new LytStyle { LayerId = 4, Color = cut.Style!.Color } };
        f.Objects.AddRange([Square("art"), cut]);

        var doc = LytImporter.Import(f).Document;

        Assert.Equal(["Layer 1", "Contour Cut"], doc.Layers.Select(l => l.Name));
        Assert.Equal("art", Assert.Single(doc.Layers[0].Children).Name);
        Assert.Equal("cut", Assert.Single(doc.Layers[1].Children).Name);
    }
}

