using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using DeSignMage.Formats.Lyt;
using Xunit.Abstractions;

namespace DeSignMage.Formats.Tests;

/// <summary>Text, pictures, gradients, strokes and soft effects through the layout writer and back.</summary>
public class LytWriterContentTests(ITestOutputHelper output)
{
    private static string Family => TextEngine.Fonts.IsInstalled("Arial") ? "Arial" : TextEngine.Fonts.Families[0];

    private static TextObject AddText(Document doc, string text, double size, Affine2 transform, DocColor? color = null, string? family = null)
    {
        var t = new TextObject(text, family ?? Family, size)
        {
            Transform = transform,
            Style = ObjectStyle.FillOnly(color ?? WriterTestKit.Red),
        };
        doc.Insert(doc.ActiveLayer, -1, t);
        return t;
    }

    // ---------------------------------------------------------------- text

    [Fact]
    public void Single_line_text_is_written_as_live_text_that_comes_back_the_same()
    {
        var doc = WriterTestKit.NewDoc(24, 12);
        var t = AddText(doc, "Hello World", 48, Affine2.Translation(100, 200));
        var (bytes, result) = WriterTestKit.Write(doc);
        Assert.Equal(1, result.LiveTextCount);
        Assert.Equal(0, result.TextAsOutlinesCount);
        Assert.Empty(result.Warnings);

        var o = Assert.Single(WriterTestKit.Parse(bytes).Objects);
        Assert.True(o.IsText);
        Assert.Equal("Hello World", o.Name);
        Assert.Equal($"_{Family}.ptt", o.Style!.FontFile);
        Assert.Equal(11, o.Nodes.Count(n => n.Kind == LytNodeKind.Char));
        Assert.Equal(0, o.Nodes[0].X, 6);                                  // the first letter's ink-left edge is the origin
        Assert.Equal(10, o.Nodes[0].Cy, 6);                                // capital height = 10 units
        Assert.Empty(o.Notes);

        var imported = WriterTestKit.Import(bytes);
        Assert.Equal(1, imported.LiveTextCount);
        var back = Assert.IsType<TextObject>(Assert.Single(imported.Document.AllObjects()));
        Assert.Equal("Hello World", back.Text);
        Assert.Equal(Family, back.FontFamily);
        Assert.Equal(t.FontSize, back.FontSize, 1);
        double dev = WriterTestKit.Deviation(WriterTestKit.World(t), WriterTestKit.World(back));
        output.WriteLine($"live text deviation {dev:0.00000} pt");
        Assert.True(dev < 0.05, $"deviation {dev}");
        Assert.Equal(WriterTestKit.Red.R, ((SolidPaint)back.Style.Fill!).Color.R);
    }

    [Fact]
    public void Rotated_and_stretched_text_keeps_its_turn_and_stretch()
    {
        var doc = WriterTestKit.NewDoc(24, 12);
        var t = AddText(doc, "Turned", 40, Affine2.Scale(1.4, 1) * Affine2.Rotation(-0.35) * Affine2.Translation(150, 300));
        var (bytes, result) = WriterTestKit.Write(doc);
        Assert.Equal(1, result.LiveTextCount);

        var o = Assert.Single(WriterTestKit.Parse(bytes).Objects);
        Assert.Equal(0.35, o.Rotation, 5);
        Assert.Equal(1.4, o.XScale, 5);

        var back = Assert.IsType<TextObject>(Assert.Single(WriterTestKit.Import(bytes).Document.AllObjects()));
        double dev = WriterTestKit.Deviation(WriterTestKit.World(t), WriterTestKit.World(back));
        output.WriteLine($"turned text deviation {dev:0.00000} pt");
        Assert.True(dev < 0.05, $"deviation {dev}");
    }

    [Fact]
    public void Centered_text_with_tracking_and_kerning_keeps_every_letter_where_it_is()
    {
        var doc = WriterTestKit.NewDoc(24, 12);
        var t = AddText(doc, "Spaced Out", 36, Affine2.Translation(300, 200));
        t.Alignment = TextAlignment.Center;
        t.Tracking = 0.12;
        t.Kerning = [0, 0.3, 0, 0, -0.1];

        var back = Assert.IsType<TextObject>(Assert.Single(WriterTestKit.Import(WriterTestKit.Write(doc).Bytes).Document.AllObjects()));
        Assert.Equal(t.Layout.Letters.Count, back.Layout.Letters.Count);
        double dev = WriterTestKit.Deviation(WriterTestKit.World(t), WriterTestKit.World(back));
        output.WriteLine($"spaced text deviation {dev:0.00000} pt");
        Assert.True(dev < 0.1, $"deviation {dev}");
    }

    [Fact]
    public void Text_that_live_text_cannot_carry_is_written_as_outlines()
    {
        var doc = WriterTestKit.NewDoc(24, 12);
        var bold = AddText(doc, "Bold", 36, Affine2.Translation(50, 100));
        bold.FontWeight = 700;
        var lines = AddText(doc, "Two\nLines", 36, Affine2.Translation(50, 200));
        var skewed = AddText(doc, "Skewed", 36, new Affine2(1, 0, -0.4, 1, 50, 300));
        var gradient = AddText(doc, "Fade", 36, Affine2.Translation(50, 400));
        gradient.Style = new ObjectStyle(new GradientPaint(GradientKind.Linear, GradientPaint.DefaultPoints(GradientKind.Linear),
            [new GradientNode(0, WriterTestKit.Red), new GradientNode(1, WriterTestKit.Blue)]), null);

        var (bytes, result) = WriterTestKit.Write(doc);
        Assert.Equal(0, result.LiveTextCount);
        Assert.Equal(4, result.TextAsOutlinesCount);
        Assert.Equal(4, result.Warnings.Count(w => w.Contains("written as outlines")));

        var file = WriterTestKit.Parse(bytes);
        Assert.Equal(4, file.Objects.Count);
        Assert.All(file.Objects, o => Assert.False(o.IsText));
        Assert.Equal(["Bold", "Two Lines", "Skewed", "Fade"], file.Objects.Select(o => o.Name));

        var back = WriterTestKit.Drawn(WriterTestKit.Import(bytes).Document);
        Assert.All(back, o => Assert.IsType<PathObject>(o));
        foreach (var (a, b) in new (DocObject, DocObject)[] { (bold, back[0]), (lines, back[1]), (skewed, back[2]), (gradient, back[3]) })
        {
            double dev = WriterTestKit.Deviation(WriterTestKit.World(a), WriterTestKit.World(b));
            Assert.True(dev < 0.05, $"{a} deviation {dev}");
        }
        Assert.IsType<GradientPaint>(back[3].Style.Fill);
    }

    [Fact]
    public void Live_text_can_be_switched_off()
    {
        var doc = WriterTestKit.NewDoc();
        AddText(doc, "Plain", 30, Affine2.Translation(10, 100));
        var (bytes, result) = WriterTestKit.Write(doc, new LytWriteOptions { LiveText = false });
        Assert.Equal(1, result.TextAsOutlinesCount);
        Assert.False(Assert.Single(WriterTestKit.Parse(bytes).Objects).IsText);
    }

    // ---------------------------------------------------------------- pictures

    private static ImageObject AddImage(Document doc, byte[] data, string mediaType, double w, double h, Affine2 transform)
    {
        var img = new ImageObject(doc.Assets.Add(data, mediaType), w, h) { Transform = transform };
        doc.Insert(doc.ActiveLayer, -1, img);
        return img;
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    public void A_picture_is_stored_as_it_is_and_lands_where_it_was(string mediaType)
    {
        var doc = WriterTestKit.NewDoc();
        var data = mediaType == "image/png" ? WriterTestKit.Png() : WriterTestKit.Jpeg();
        var img = AddImage(doc, data, mediaType, 200, 100, Affine2.Rotation(-0.4) * Affine2.Translation(150, 250));

        var (bytes, result) = WriterTestKit.Write(doc);
        Assert.Equal(1, result.PictureCount);
        Assert.Empty(result.Warnings);
        var o = Assert.Single(WriterTestKit.Parse(bytes).Objects);
        Assert.Equal(4u, o.Style!.Fill!.Type);
        Assert.Equal(data, o.Style.Fill.Image);
        Assert.Equal(mediaType, o.Style.Fill.ImageMediaType);

        var back = Assert.IsType<ImageObject>(Assert.Single(WriterTestKit.Import(bytes).Document.AllObjects()));
        Assert.Equal(data, WriterTestKit.Import(bytes).Document.Assets.Get(back.AssetId)!.Data);
        foreach (var (u, v) in new[] { (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0) })
        {
            var a = img.GetWorldTransform().Transform(new Vec2(u * img.Width, v * img.Height));
            var b = back.GetWorldTransform().Transform(new Vec2(u * back.Width, v * back.Height));
            Assert.True(Vec2.Distance(a, b) < 0.02, $"corner ({u}, {v}): {a} vs {b}");
        }
    }

    [Fact]
    public void A_stretched_picture_and_a_picture_in_an_unsupported_format()
    {
        var doc = WriterTestKit.NewDoc();
        var img = AddImage(doc, WriterTestKit.Jpeg(), "image/jpeg", 100, 50, Affine2.Scale(2, 0.5) * Affine2.Translation(30, 60));
        AddImage(doc, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], "image/webp", 50, 50, Affine2.Translation(300, 60));

        var (bytes, result) = WriterTestKit.Write(doc);
        Assert.Equal(1, result.PictureCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Contains(result.Warnings, w => w.Contains("image/webp"));

        var back = Assert.IsType<ImageObject>(Assert.Single(WriterTestKit.Import(bytes).Document.AllObjects()));
        var a = img.GetWorldBounds(); var b = back.GetWorldBounds();
        Assert.True(Math.Abs(a.Left - b.Left) + Math.Abs(a.Top - b.Top) + Math.Abs(a.Right - b.Right) + Math.Abs(a.Bottom - b.Bottom) < 0.05);

        // a re-encoder in the options takes care of other formats
        var (_, again) = WriterTestKit.Write(doc, new LytWriteOptions { ImageEncoder = _ => WriterTestKit.Png() });
        Assert.Equal(2, again.PictureCount);
    }

    // ---------------------------------------------------------------- gradients and fades

    private static RectangleObject AddFilled(Document doc, Paint fill, double x = 50, double y = 50)
    {
        var r = new RectangleObject(200, 100) { Transform = Affine2.Translation(x, y), Style = new ObjectStyle(fill, null) };
        doc.Insert(doc.ActiveLayer, -1, r);
        return r;
    }

    private static GradientPaint Gradient(GradientKind kind, params (double Offset, DocColor Color)[] stops) =>
        new(kind, GradientPaint.DefaultPoints(kind), stops.Select(s => new GradientNode(s.Offset, s.Color)).ToList());

    [Theory]
    [InlineData(GradientKind.Linear)]
    [InlineData(GradientKind.Circle)]
    [InlineData(GradientKind.Ellipse)]
    [InlineData(GradientKind.Horizontal)]
    [InlineData(GradientKind.Vertical)]
    public void Every_gradient_kind_comes_back_with_its_colors_and_control_points(GradientKind kind)
    {
        var doc = WriterTestKit.NewDoc();
        var paint = Gradient(kind, (0, WriterTestKit.Red), (0.4, new DocColor(10, 200, 10)), (1, WriterTestKit.Blue));
        if (kind == GradientKind.Linear) paint = paint.WithPoints([new Vec2(0.1, 0.2), new Vec2(0.8, 0.9)]);
        var r = AddFilled(doc, paint);

        var (bytes, result) = WriterTestKit.Write(doc);
        Assert.Equal(1, result.GradientCount);
        Assert.Empty(result.Warnings);
        var o = Assert.Single(WriterTestKit.Parse(bytes).Objects);
        Assert.True(o.Style!.Fill!.Blend!.GradType is 1 or 2 or 3 or 5 or 6);

        var back = Assert.IsType<GradientPaint>(Assert.Single(WriterTestKit.Import(bytes).Document.AllObjects()).Style.Fill);
        Assert.Equal(kind, back.Kind);
        Assert.Equal(3, back.Stops.Count);
        Assert.Equal([0, 0.4, 1], back.Stops.Select(s => Math.Round(s.Offset, 4)));
        Assert.Equal(new[] { paint.Stops[0].Color, paint.Stops[1].Color, paint.Stops[2].Color }.Select(c => (c.R, c.G, c.B)),
            back.Stops.Select(s => (s.Color.R, s.Color.G, s.Color.B)));
        Assert.Equal(paint.Points.Count, back.Points.Count);
        for (int i = 0; i < paint.Points.Count; i++)
            Assert.True(Vec2.Distance(paint.Points[i], back.Points[i]) < 1e-4, $"point {i}: {paint.Points[i]} vs {back.Points[i]}");
    }

    [Fact]
    public void Many_color_gradients_keep_highlight_nodes_past_the_fourth()
    {
        var doc = WriterTestKit.NewDoc();
        var stops = new List<GradientNode>
        {
            new(0, WriterTestKit.Red), new(0.2, new DocColor(1, 1, 1)), new(0.4, new DocColor(2, 2, 2)), new(0.5, new DocColor(3, 3, 3)),
            new(0.6, new DocColor(4, 4, 4), true), new(0.8, new DocColor(5, 5, 5)), new(1, WriterTestKit.Blue),
        };
        AddFilled(doc, new GradientPaint(GradientKind.Linear, GradientPaint.DefaultPoints(GradientKind.Linear), stops));
        var back = Assert.IsType<GradientPaint>(Assert.Single(WriterTestKit.Import(WriterTestKit.Write(doc).Bytes).Document.AllObjects()).Style.Fill);
        Assert.Equal(7, back.Stops.Count);
        Assert.Equal(stops.Select(s => Math.Round(s.Offset, 4)), back.Stops.Select(s => Math.Round(s.Offset, 4)));
        Assert.Equal([false, false, false, false, true, false, false], back.Stops.Select(s => s.Highlight));
    }

    [Theory]
    [InlineData(GradientKind.Linear, 0, 255)]
    [InlineData(GradientKind.Linear, 255, 0)]
    [InlineData(GradientKind.Linear, 77, 200)]
    [InlineData(GradientKind.Linear, 230, 40)]
    [InlineData(GradientKind.Circle, 255, 0)]
    [InlineData(GradientKind.Circle, 0, 255)]
    [InlineData(GradientKind.Ellipse, 255, 60)]
    [InlineData(GradientKind.Ellipse, 90, 180)]
    [InlineData(GradientKind.Horizontal, 255, 0)]
    [InlineData(GradientKind.Vertical, 0, 255)]
    public void A_color_fading_to_nothing_comes_back_as_the_same_fade(GradientKind kind, byte startAlpha, byte endAlpha)
    {
        var doc = WriterTestKit.NewDoc();
        var paint = Gradient(kind, (0, WriterTestKit.Blue with { A = startAlpha }), (1, WriterTestKit.Blue with { A = endAlpha }));
        AddFilled(doc, paint);

        var (bytes, result) = WriterTestKit.Write(doc);
        Assert.Equal(1, result.FadeCount);
        Assert.Equal(0, result.GradientCount);
        var o = Assert.Single(WriterTestKit.Parse(bytes).Objects);
        Assert.Equal(0u, o.Style!.Fill!.Blend!.GradType);

        var back = Assert.IsType<GradientPaint>(Assert.Single(WriterTestKit.Import(bytes).Document.AllObjects()).Style.Fill);
        Assert.Equal(kind, back.Kind);
        Assert.InRange(back.Stops[0].Color.A, startAlpha - 2, startAlpha + 2);
        Assert.InRange(back.Stops[^1].Color.A, endAlpha - 2, endAlpha + 2);
        Assert.Equal((WriterTestKit.Blue.R, WriterTestKit.Blue.G, WriterTestKit.Blue.B), (back.Stops[0].Color.R, back.Stops[0].Color.G, back.Stops[0].Color.B));
        for (int i = 0; i < paint.Points.Count; i++)
            if (kind is not (GradientKind.Horizontal or GradientKind.Vertical))
                Assert.True(Vec2.Distance(paint.Points[i], back.Points[i]) < 1e-4);
    }

    [Fact]
    public void A_gradient_on_a_moved_and_rotated_object_keeps_its_place_on_the_page()
    {
        var doc = WriterTestKit.NewDoc();
        var paint = Gradient(GradientKind.Linear, (0, WriterTestKit.Red), (1, WriterTestKit.Blue));
        var r = AddFilled(doc, paint);
        r.Transform = Affine2.Rotation(0.3) * Affine2.Translation(200, 100);

        var back = Assert.Single(WriterTestKit.Import(WriterTestKit.Write(doc).Bytes).Document.AllObjects());
        var g = (GradientPaint)back.Style.Fill!;
        var f0 = ((GradientPaint)r.Style.Fill!).Frame(r.GetLocalGeometry()!.GetBounds());
        var f1 = g.Frame(back.GetLocalGeometry()!.GetBounds());
        Assert.True(Vec2.Distance(r.GetWorldTransform().Transform(f0.P0), back.GetWorldTransform().Transform(f1.P0)) < 0.02);
        Assert.True(Vec2.Distance(r.GetWorldTransform().Transform(f0.P1), back.GetWorldTransform().Transform(f1.P1)) < 0.02);
    }

    [Fact]
    public void Gradients_can_be_switched_off()
    {
        var doc = WriterTestKit.NewDoc();
        AddFilled(doc, Gradient(GradientKind.Linear, (0, WriterTestKit.Red), (1, WriterTestKit.Blue)));
        var (bytes, result) = WriterTestKit.Write(doc, new LytWriteOptions { Gradients = false });
        Assert.Equal(0, result.GradientCount);
        var back = Assert.Single(WriterTestKit.Import(bytes).Document.AllObjects());
        Assert.Equal(WriterTestKit.Red.R, ((SolidPaint)back.Style.Fill!).Color.R);
    }

    [Theory]
    [InlineData(true, 1.0, 0.0)]
    [InlineData(true, 0.3, 0.9)]
    [InlineData(false, 1.0, 0.0)]
    [InlineData(false, 0.9, 0.2)]
    [InlineData(false, 0.1, 0.8)]
    [InlineData(false, 0.5, 0.5)]
    public void The_fade_fields_are_the_inverse_of_the_importer_rules(bool linear, double a0, double a1)
    {
        bool radial = !linear;
        Assert.True(LytWriteSession.SolveFade(radial, a0, a1, out double min, out double max, out bool swap));
        var block = new LytBlend
        {
            GradType = 0, TransType = linear ? 2u : 3u, TransMin = (float)min, TransMax = (float)max, TransSwap = swap,
        };
        block.TransPoints[2] = 10;
        var square = new PathBuilder().MoveTo(0, 0).LineTo(10, 0).LineTo(10, -10).LineTo(0, -10).Close().Build();
        var g = LytGradients.TryCreate(block, DocColor.Black, square)!;
        Assert.InRange(g.Stops[0].Color.A, Math.Round(a0 * 255) - 1, Math.Round(a0 * 255) + 1);
        Assert.InRange(g.Stops[1].Color.A, Math.Round(a1 * 255) - 1, Math.Round(a1 * 255) + 1);
    }

    // ---------------------------------------------------------------- strokes and soft effects

    [Fact]
    public void A_stroke_becomes_a_ring_shape_above_the_fill()
    {
        var doc = WriterTestKit.NewDoc();
        var r = WriterTestKit.Place(doc, new RectangleObject(200, 100), 100, 100, WriterTestKit.Red, "Box");
        r.Style = new ObjectStyle(new SolidPaint(WriterTestKit.Red), StrokeStyle.Solid(WriterTestKit.Blue, 10));

        var (bytes, result) = WriterTestKit.Write(doc, WriterTestKit.WithServices);
        Assert.Equal(1, result.StrokeShapeCount);
        Assert.Empty(result.Warnings);
        var file = WriterTestKit.Parse(bytes);
        Assert.Equal(["Box", "Box (stroke)"], file.Objects.Select(o => o.Name));
        Assert.Equal("Blue 8", file.Objects[1].Style!.Color.Name);

        var back = WriterTestKit.Drawn(WriterTestKit.Import(bytes).Document);
        var ring = WriterTestKit.World(back[1]);
        var b = ring.GetBounds();
        Assert.Equal((95.0, 95.0, 305.0, 205.0), (Math.Round(b.Left, 2), Math.Round(b.Top, 2), Math.Round(b.Right, 2), Math.Round(b.Bottom, 2)));
        Assert.False(ring.Contains(new Vec2(200, 150)));            // hollow in the middle
        Assert.True(ring.Contains(new Vec2(100, 150)));             // solid along the edge
    }

    [Fact]
    public void Without_the_geometry_services_a_stroke_is_dropped_and_a_stroke_only_object_becomes_an_outline()
    {
        var doc = WriterTestKit.NewDoc();
        var r = WriterTestKit.Place(doc, new RectangleObject(200, 100), 100, 100, WriterTestKit.Red, "Filled");
        r.Style = new ObjectStyle(new SolidPaint(WriterTestKit.Red), StrokeStyle.Solid(WriterTestKit.Blue, 10));
        var line = WriterTestKit.Place(doc, new RectangleObject(50, 50), 400, 100, null, "Line");
        line.Style = new ObjectStyle(null, StrokeStyle.Solid(WriterTestKit.Blue, 3));

        var (bytes, result) = WriterTestKit.Write(doc);
        Assert.Equal(0, result.StrokeShapeCount);
        Assert.Contains(result.Warnings, w => w.Contains("stroke"));
        var file = WriterTestKit.Parse(bytes);
        Assert.Equal(2, file.Objects.Count);
        Assert.True(file.Objects[0].Style!.Color.IsValid);
        Assert.False(file.Objects[1].Style!.Color.IsValid);        // the unfilled outline
    }

    [Fact]
    public void A_stroke_only_object_and_an_open_line_get_their_ring_with_the_services()
    {
        var doc = WriterTestKit.NewDoc();
        var box = WriterTestKit.Place(doc, new RectangleObject(100, 100), 100, 100, null, "Frame");
        box.Style = new ObjectStyle(null, StrokeStyle.Solid(WriterTestKit.Blue, 8));
        var open = new PathObject(new PathBuilder().MoveTo(0, 0).LineTo(100, 0).Build()) { Name = "Line", Transform = Affine2.Translation(300, 100) };
        open.Style = new ObjectStyle(null, new StrokeStyle(new SolidPaint(WriterTestKit.Red), 10, LineJoin.Miter, LineCap.Butt));
        doc.Insert(doc.ActiveLayer, -1, open);

        var (bytes, result) = WriterTestKit.Write(doc, WriterTestKit.WithServices);
        Assert.Equal(2, result.StrokeShapeCount);
        var back = WriterTestKit.Drawn(WriterTestKit.Import(bytes).Document);
        Assert.Equal(2, back.Count);
        Assert.False(WriterTestKit.World(back[0]).Contains(new Vec2(150, 150)));
        var lb = WriterTestKit.World(back[1]).GetBounds();
        Assert.Equal((300.0, 95.0, 400.0, 105.0), (Math.Round(lb.Left, 2), Math.Round(lb.Top, 2), Math.Round(lb.Right, 2), Math.Round(lb.Bottom, 2)));
    }

    [Fact]
    public void Soft_shadows_and_glows_become_plain_objects_or_are_left_out()
    {
        var doc = WriterTestKit.NewDoc();
        WriterTestKit.Place(doc, new RectangleObject(100, 50), 100, 100, WriterTestKit.Blue, "Sign");
        var shadow = WriterTestKit.Place(doc, new RectangleObject(100, 50), 106, 106, WriterTestKit.Blue, "Shadow");
        shadow.Style = new ObjectStyle(new SolidPaint(DocColor.Black), null, new BlurStyle(SoftKind.SoftShadow, 0.5, 1, 50));

        var (plain, r1) = WriterTestKit.Write(doc);
        Assert.Equal(1, r1.SoftEffectCount);
        Assert.Equal(["Sign", "Shadow"], WriterTestKit.Parse(plain).Objects.Select(o => o.Name));
        Assert.Contains(r1.Warnings, w => w.Contains("plain objects"));

        var (skipped, r2) = WriterTestKit.Write(doc, new LytWriteOptions { SoftEffects = LytSoftEffects.Skip });
        Assert.Equal(["Sign"], WriterTestKit.Parse(skipped).Objects.Select(o => o.Name));
        Assert.Contains(r2.Warnings, w => w.Contains("left out"));
    }

    [Fact]
    public void A_combined_object_is_one_record_with_its_holes()
    {
        var doc = WriterTestKit.NewDoc();
        var a = new RectangleObject(100, 100) { Style = ObjectStyle.FillOnly(WriterTestKit.Red) };
        var b = new RectangleObject(40, 40) { Style = ObjectStyle.FillOnly(WriterTestKit.Blue), Transform = Affine2.Translation(30, 30) };
        var combined = new CombinedObject([a, b]) { Transform = Affine2.Translation(100, 100) };
        doc.Insert(doc.ActiveLayer, -1, combined);

        var (bytes, result) = WriterTestKit.Write(doc);
        Assert.Equal(1, result.PartCount);
        var o = Assert.Single(WriterTestKit.Parse(bytes).Objects);
        Assert.Equal(2, o.Nodes.Count(n => n.Kind == LytNodeKind.Move));
        var back = Assert.Single(WriterTestKit.Import(bytes).Document.AllObjects());
        Assert.False(WriterTestKit.World(back).Contains(new Vec2(150, 150)));
        Assert.True(WriterTestKit.World(back).Contains(new Vec2(110, 110)));
    }
}
