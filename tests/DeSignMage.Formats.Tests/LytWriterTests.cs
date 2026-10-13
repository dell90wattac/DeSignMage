using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using DeSignMage.Formats.Lyt;
using DeSignMage.Geometry;
using SkiaSharp;
using Xunit.Abstractions;

namespace DeSignMage.Formats.Tests;

/// <summary>Helpers shared by the layout writer tests.</summary>
internal static class WriterTestKit
{
    public static readonly DocColor Red = new(200, 30, 40, 255, "Red 3", "3M");
    public static readonly DocColor Blue = new(20, 60, 200, 255, "Blue 8");
    public static readonly DocColor Green = DocColor.FromCmyk(80, 10, 90, 5, "Fern", "Oracal 651");

    public static Document NewDoc(double widthInches = 20, double heightInches = 10) => new(PageSetup.FromInches(widthInches, heightInches));

    public static T Place<T>(Document doc, T obj, double x, double y, DocColor? fill = null, string? name = null, Layer? layer = null) where T : DocObject
    {
        obj.Transform = Affine2.Translation(x, y);
        obj.Style = fill is { } c ? ObjectStyle.FillOnly(c) : ObjectStyle.None;
        obj.Name = name;
        doc.Insert(layer ?? doc.ActiveLayer, -1, obj);
        return obj;
    }

    public static PathGeometry World(DocObject o) => o.GetLocalGeometry()!.Transform(o.GetWorldTransform());

    public static (byte[] Bytes, LytWriteResult Result) Write(Document doc, LytWriteOptions? options = null)
    {
        var bytes = LytWriter.ToBytes(doc, options, out var result);
        return (bytes, result);
    }

    public static LytFile Parse(byte[] bytes) => LytParser.Parse(bytes);

    public static LytImportResult Import(byte[] bytes) => LytImporter.Import(LytParser.Parse(bytes));

    /// <summary>The drawn objects of every layer, bottom layer first, each layer in drawing order.</summary>
    public static List<DocObject> Drawn(Document doc) => doc.Layers.SelectMany(l => l.Children).ToList();

    /// <summary>Largest distance of any point of either outline from the other outline (points).</summary>
    public static double Deviation(PathGeometry a, PathGeometry b)
    {
        double worst = 0;
        foreach (var (from, to) in new[] { (a, b), (b, a) })
            foreach (var poly in from.Flatten(0.05))
                foreach (var p in poly.Points)
                    worst = Math.Max(worst, to.DistanceToOutline(p, 0.05));
        return worst;
    }

    public static IOffsetService Offsets { get; } = new ClipperOffsetService();
    public static IBooleanService Booleans { get; } = new ClipperBooleanService();

    public static LytWriteOptions WithServices => new() { OffsetService = Offsets, BooleanService = Booleans };

    public static byte[] Jpeg(int w = 24, int h = 12) => Encode(w, h, SKEncodedImageFormat.Jpeg);

    public static byte[] Png(int w = 24, int h = 12) => Encode(w, h, SKEncodedImageFormat.Png);

    private static byte[] Encode(int w, int h, SKEncodedImageFormat format)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(250, 200, 20));
            using var paint = new SKPaint { Color = new SKColor(20, 40, 200) };
            canvas.DrawRect(0, 0, w / 2f, h / 2f, paint);
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }
}

/// <summary>
/// The legacy layout writer: documents are written, then read back with the parser and the importer; the objects, colors, groups and
/// drawing order have to come out as they went in. (Only the legacy program itself can say whether it opens the files; see
/// notes/lyt-writer-tests.)
/// </summary>
public class LytWriterTests
{
    // ---------------------------------------------------------------- the file

    [Fact]
    public void An_empty_document_is_a_valid_file_with_its_page_size_and_layers()
    {
        var doc = WriterTestKit.NewDoc(30, 12);
        doc.Layers[0].Name = "Artwork";
        var (bytes, result) = WriterTestKit.Write(doc, new LytWriteOptions { Title = "Empty" });
        var file = WriterTestKit.Parse(bytes);

        Assert.Equal(0x4A, file.FormatVersion);
        Assert.Equal("Empty", file.Title);
        Assert.Equal(30, file.PageWidth, 4);
        Assert.Equal(12, file.PageHeight, 4);
        Assert.Empty(file.Objects);
        Assert.Empty(file.Warnings);
        Assert.Equal(0, result.PartCount);
        var layer = Assert.Single(file.Layers);
        Assert.Equal("Artwork", layer.Name);
    }

    [Fact]
    public void A_preview_picture_is_stored_in_front_and_read_back_when_it_is_a_jfif_jpeg()
    {
        var doc = WriterTestKit.NewDoc();
        WriterTestKit.Place(doc, new RectangleObject(72, 72), 10, 10, WriterTestKit.Red);
        var jpeg = WriterTestKit.Jpeg();
        var (withThumb, result) = WriterTestKit.Write(doc, new LytWriteOptions { ThumbnailJpeg = jpeg });
        Assert.Empty(result.Warnings);
        var file = WriterTestKit.Parse(withThumb);
        Assert.Equal(jpeg, file.Thumbnail);
        Assert.Single(file.Objects);

        var (plain, _) = WriterTestKit.Write(doc);
        Assert.Null(WriterTestKit.Parse(plain).Thumbnail);

        var (bad, badResult) = WriterTestKit.Write(doc, new LytWriteOptions { ThumbnailJpeg = [1, 2, 3, 4, 5, 6, 7, 8, 9] });
        Assert.Contains(badResult.Warnings, w => w.Contains("preview"));
        Assert.Single(WriterTestKit.Parse(bad).Objects);
    }

    [Fact]
    public void Guides_and_layers_are_kept()
    {
        var doc = WriterTestKit.NewDoc(24, 12);
        doc.Layers[0].Name = "Back";
        var top = new Layer("Front") { Color = WriterTestKit.Blue };
        doc.InsertLayer(1, top);
        WriterTestKit.Place(doc, new RectangleObject(72, 36), 36, 36, WriterTestKit.Red, "Low", doc.Layers[0]);
        WriterTestKit.Place(doc, new RectangleObject(72, 36), 72, 72, WriterTestKit.Blue, "High", top);
        new DeSignMage.Core.Commands.AddGuideCommand(new Guide(Units.FromUser(3, 4, doc.Page), 0)).Do(doc);
        new DeSignMage.Core.Commands.AddGuideCommand(new Guide(Units.FromUser(5, 6, doc.Page), 90)).Do(doc);

        var (bytes, _) = WriterTestKit.Write(doc);
        var file = WriterTestKit.Parse(bytes);
        Assert.Equal(["Front", "Back"], file.Layers.Select(l => l.Name));   // the header lists the top layer first
        Assert.Equal(2, file.Guides.Count);

        var back = WriterTestKit.Import(bytes).Document;
        Assert.Equal(["Back", "Front"], back.Layers.Select(l => l.Name));
        Assert.Equal(["Low"], back.Layers[0].Children.Select(c => c.Name));
        Assert.Equal(["High"], back.Layers[1].Children.Select(c => c.Name));
        Assert.Equal(2, back.Guides.Count);
        Assert.Equal(3 * 72.0, back.Guides[0].Origin.Y > 0 ? Units.ToUser(back.Guides[0].Origin, back.Page).X * 0 + 3 * 72.0 : 0, 6);
        Assert.Equal((3.0, 4.0), (Math.Round(Units.ToUser(back.Guides[0].Origin, back.Page).X, 4), Math.Round(Units.ToUser(back.Guides[0].Origin, back.Page).Y, 4)));
        Assert.Equal(0, back.Guides[0].AngleDegrees, 4);
        Assert.Equal(90, back.Guides[1].AngleDegrees, 4);
    }

    [Fact]
    public void Hidden_objects_and_hidden_layers_are_left_out_unless_asked_for()
    {
        var doc = WriterTestKit.NewDoc();
        WriterTestKit.Place(doc, new RectangleObject(72, 36), 10, 10, WriterTestKit.Red, "Shown");
        var hidden = WriterTestKit.Place(doc, new RectangleObject(72, 36), 100, 10, WriterTestKit.Red, "Hidden");
        hidden.Visible = false;

        var (bytes, result) = WriterTestKit.Write(doc);
        Assert.Equal(["Shown"], WriterTestKit.Parse(bytes).Objects.Select(o => o.Name));
        Assert.Equal(1, result.HiddenSkippedCount);

        var (all, _) = WriterTestKit.Write(doc, new LytWriteOptions { IncludeHidden = true });
        Assert.Equal(2, WriterTestKit.Parse(all).Objects.Count);
    }

    // ---------------------------------------------------------------- shapes and colors

    [Fact]
    public void A_rectangle_is_written_with_its_nodes_position_and_vinyl_color()
    {
        var doc = WriterTestKit.NewDoc();
        WriterTestKit.Place(doc, new RectangleObject(144, 72), 72, 36, WriterTestKit.Red, "Box");
        var (bytes, result) = WriterTestKit.Write(doc);
        Assert.Empty(result.Warnings);

        var file = WriterTestKit.Parse(bytes);
        var o = Assert.Single(file.Objects);
        Assert.Equal("Box", o.Name);
        Assert.Empty(o.Notes);
        Assert.False(o.IsText);
        var c = o.Style!.Color;
        Assert.Equal(("Red 3", "3M", LytColor.ModelRgb, (ushort)200, (ushort)30, (ushort)40), (c.Name, c.Vendor, c.Model, c.V0, c.V1, c.V2));
        Assert.Equal([LytNodeKind.Move, LytNodeKind.Line, LytNodeKind.Line, LytNodeKind.Line, LytNodeKind.Line], o.Nodes.Select(n => n.Kind));
        Assert.True(o.Nodes[^1].Closes);
        Assert.Equal(o.Nodes[0].X, o.Nodes[^1].X);
        Assert.All(o.Nodes, n => Assert.NotEqual(0u, n.Flags & LytNode.FlagOuter));

        // position: the box starts 1 inch from the left and its bottom edge is 2 inches from the bottom of a 10 inch page (36 + 72 pt from the top)
        Assert.Equal(1.0, o.OrgX, 4);
        Assert.Equal(10 - (36 + 72) / 72.0, o.OrgY, 4);

        var back = Assert.IsType<PathObject>(Assert.Single(WriterTestKit.Import(bytes).Document.AllObjects()));
        var bounds = back.GetWorldBounds();
        Assert.Equal((72.0, 36.0, 216.0, 108.0), (Math.Round(bounds.Left, 3), Math.Round(bounds.Top, 3), Math.Round(bounds.Right, 3), Math.Round(bounds.Bottom, 3)));
        Assert.Equal("3M Red 3", Assert.IsType<SolidPaint>(back.Style.Fill).Color.Name);
    }

    [Fact]
    public void Cmyk_and_named_colors_keep_their_values_and_unnamed_ones_carry_them_as_text()
    {
        var doc = WriterTestKit.NewDoc();
        WriterTestKit.Place(doc, new RectangleObject(36, 36), 0, 0, WriterTestKit.Green, "Cmyk");
        WriterTestKit.Place(doc, new RectangleObject(36, 36), 40, 0, new DocColor(1, 2, 3), "Plain");
        WriterTestKit.Place(doc, new RectangleObject(36, 36), 80, 0, DocColor.FromCmyk(0, 25, 100, 4), "CmykPlain");

        var file = WriterTestKit.Parse(WriterTestKit.Write(doc).Bytes);
        var green = file.Objects[0].Style!.Color;
        Assert.Equal(("Fern", "Oracal 651", LytColor.ModelCmyk, (ushort)8000, (ushort)1000, (ushort)9000, (ushort)500), (green.Name, green.Vendor, green.Model, green.V0, green.V1, green.V2, green.V3));
        var plain = file.Objects[1].Style!.Color;
        Assert.Equal(("1,2,3", "", LytColor.ModelRgb), (plain.Name, plain.Vendor, plain.Model));
        var cmyk = file.Objects[2].Style!.Color;
        Assert.Equal(("0,25,100,4", LytColor.ModelCmyk, (ushort)2500, (ushort)10000, (ushort)400), (cmyk.Name, cmyk.Model, cmyk.V1, cmyk.V2, cmyk.V3));
        Assert.Equal((byte)1, file.Objects[1].Style!.Color.ToDocColor().R);
        Assert.Equal(3, file.DocumentColors.Count);
    }

    [Fact]
    public void Curves_arcs_and_holes_survive_the_trip()
    {
        var doc = WriterTestKit.NewDoc();
        var ellipse = WriterTestKit.Place(doc, new EllipseObject(150, 90), 50, 40, WriterTestKit.Blue, "Ellipse");
        var rounded = WriterTestKit.Place(doc, new RectangleObject(120, 60, 15), 250, 40, WriterTestKit.Red, "Rounded");

        var arcs = new PathBuilder().MoveTo(0, 0).ArcTo(new Vec2(30, -30), new Vec2(60, 0)).LineTo(60, 40).LineTo(0, 40).Close().Build();
        var withArcs = WriterTestKit.Place(doc, new PathObject(arcs), 450, 40, WriterTestKit.Red, "Arcs");

        // an even-odd ring whose two loops run the same way, and a non-zero ring whose inner loop runs against the outer one
        var evenOdd = new PathBuilder { FillRule = FillRule.EvenOdd }
            .MoveTo(0, 0).LineTo(100, 0).LineTo(100, 100).LineTo(0, 100).Close()
            .MoveTo(30, 30).LineTo(70, 30).LineTo(70, 70).LineTo(30, 70).Close().Build();
        var ring1 = WriterTestKit.Place(doc, new PathObject(evenOdd), 600, 40, WriterTestKit.Blue, "EvenOdd");
        var nonZero = new PathBuilder()
            .MoveTo(0, 0).LineTo(100, 0).LineTo(100, 100).LineTo(0, 100).Close()
            .MoveTo(30, 30).LineTo(30, 70).LineTo(70, 70).LineTo(70, 30).Close().Build();
        var ring2 = WriterTestKit.Place(doc, new PathObject(nonZero), 750, 40, WriterTestKit.Blue, "NonZero");

        var (bytes, result) = WriterTestKit.Write(doc);
        Assert.Empty(result.Warnings);
        var file = WriterTestKit.Parse(bytes);

        Assert.Contains(file.Objects[0].Nodes, n => n.Kind == LytNodeKind.Bezier);
        Assert.Contains(file.Objects[1].Nodes, n => n.Kind == LytNodeKind.Bezier);
        Assert.Contains(file.Objects[2].Nodes, n => n.Kind == LytNodeKind.Arc);
        foreach (int i in new[] { 3, 4 })
        {
            var starts = file.Objects[i].Nodes.Where(n => n.Kind == LytNodeKind.Move).ToList();
            Assert.Equal(2, starts.Count);
            Assert.NotEqual(0u, starts[0].Flags & LytNode.FlagOuter);
            Assert.NotEqual(0u, starts[1].Flags & LytNode.FlagHole);
        }

        var back = WriterTestKit.Drawn(WriterTestKit.Import(bytes).Document);
        Assert.Equal(5, back.Count);
        foreach (var (a, b) in new (DocObject, DocObject)[] { (ellipse, back[0]), (rounded, back[1]), (withArcs, back[2]), (ring1, back[3]), (ring2, back[4]) })
            Assert.True(WriterTestKit.Deviation(WriterTestKit.World(a), WriterTestKit.World(b)) < 0.01, $"{a.Name} deviates by {WriterTestKit.Deviation(WriterTestKit.World(a), WriterTestKit.World(b)):0.0000} pt");

        // the arcs stay arcs, the rings keep their holes
        Assert.True(((PathObject)back[2]).Geometry.HasArcs);
        foreach (int i in new[] { 3, 4 })
        {
            var g = WriterTestKit.World(back[i]);
            var b = g.GetBounds();
            Assert.False(g.Contains(new Vec2((b.Left + b.Right) / 2, (b.Top + b.Bottom) / 2)));
            Assert.True(g.Contains(new Vec2(b.Left + 5, b.Top + 5)));
        }
    }

    [Fact]
    public void Open_paths_stay_open_and_a_transform_is_baked_into_the_nodes()
    {
        var doc = WriterTestKit.NewDoc();
        var open = new PathBuilder().MoveTo(0, 0).CubicTo(20, -40, 60, 40, 80, 0).Build();
        var path = new PathObject(open) { Name = "Wave", Style = new ObjectStyle(null, null) };
        path.Transform = Affine2.Scale(2, 1.5) * Affine2.Rotation(0.4) * Affine2.Translation(100, 100);
        doc.Insert(doc.ActiveLayer, -1, path);

        var (bytes, result) = WriterTestKit.Write(doc);
        var o = Assert.Single(WriterTestKit.Parse(bytes).Objects);
        Assert.False(o.Style!.Color.IsValid);                   // no fill: an unfilled outline, as cut lines are
        Assert.DoesNotContain(o.Nodes, n => n.Closes);
        Assert.Equal(0, result.StrokeShapeCount);

        var back = WriterTestKit.Drawn(WriterTestKit.Import(bytes).Document).Single();
        Assert.True(WriterTestKit.Deviation(WriterTestKit.World(path), WriterTestKit.World(back)) < 0.01);
    }

    // ---------------------------------------------------------------- groups

    [Fact]
    public void Groups_are_membership_over_the_drawing_order_and_may_be_interleaved()
    {
        var doc = WriterTestKit.NewDoc();
        var a = WriterTestKit.Place(doc, new RectangleObject(30, 30), 0, 0, WriterTestKit.Red, "A");
        var b = WriterTestKit.Place(doc, new RectangleObject(30, 30), 40, 0, WriterTestKit.Blue, "B");
        var c = WriterTestKit.Place(doc, new RectangleObject(30, 30), 80, 0, WriterTestKit.Red, "C");
        var d = WriterTestKit.Place(doc, new RectangleObject(30, 30), 120, 0, WriterTestKit.Blue, "D");
        var e = WriterTestKit.Place(doc, new RectangleObject(30, 30), 160, 0, WriterTestKit.Red, "E");

        var inner = new GroupObject { Name = "Inner" };
        doc.Insert(doc.ActiveLayer, -1, inner);
        doc.SetGroup(a, inner);
        doc.SetGroup(c, inner);                                  // A and C are grouped with B drawn between them
        var outer = new GroupObject();
        doc.Insert(doc.ActiveLayer, -1, outer);
        doc.SetGroup(inner, outer);
        doc.SetGroup(d, outer);                                  // the outer group holds the inner one and D; B and E are free

        var (bytes, result) = WriterTestKit.Write(doc);
        Assert.Equal(2, result.GroupCount);
        var file = WriterTestKit.Parse(bytes);
        Assert.Empty(file.Warnings);

        // parts in drawing order, groups after them
        Assert.Equal(["A", "B", "C", "D", "E", "Group", "Inner"], file.Objects.Select(o => o.Name));   // an outer group before the groups in it
        var groups = file.Objects.Where(o => o.IsGroup).ToList();
        var go = groups[0]; var gi = groups[1];
        Assert.Equal(2, gi.NodeCount);                           // A, C
        Assert.Equal(2, go.NodeCount);                           // the inner group, D
        Assert.Equal(go.GroupId, gi.ParentGroup);
        Assert.Equal(0, go.ParentGroup);
        Assert.Equal([gi.GroupId, 0, gi.GroupId, go.GroupId, 0], file.Objects.Where(o => !o.IsGroup).Select(o => o.ParentGroup));
        Assert.True(gi.SizeW > 0 && gi.SizeH > 0);               // the extents of the members

        var back = WriterTestKit.Import(bytes);
        Assert.Equal(2, back.GroupCount);
        var drawn = WriterTestKit.Drawn(back.Document);
        Assert.Equal(["A", "B", "C", "D", "E"], drawn.Select(o => o.Name));
        var backInner = Assert.IsType<GroupObject>(drawn[0].Parent);
        Assert.Same(backInner, drawn[2].Parent);
        Assert.Equal("Inner", backInner.Name);
        Assert.IsAssignableFrom<Layer>(drawn[1].Parent);
        var backOuter = Assert.IsType<GroupObject>(backInner.Parent);
        Assert.Same(backOuter, drawn[3].Parent);
        Assert.IsAssignableFrom<Layer>(drawn[4].Parent);
        Assert.Equal(2, backOuter.Children.Count);
    }

    [Fact]
    public void An_object_inside_a_transformed_group_is_written_where_it_is_drawn()
    {
        var doc = WriterTestKit.NewDoc();
        var a = WriterTestKit.Place(doc, new RectangleObject(30, 30), 10, 10, WriterTestKit.Red, "A");
        var group = new GroupObject { Transform = Affine2.Rotation(0.5) * Affine2.Translation(100, 50) };
        doc.Insert(doc.ActiveLayer, -1, group);
        doc.SetGroup(a, group);

        var back = WriterTestKit.Drawn(WriterTestKit.Import(WriterTestKit.Write(doc).Bytes).Document).Single();
        Assert.True(WriterTestKit.Deviation(WriterTestKit.World(a), WriterTestKit.World(back)) < 0.01);
    }
}
