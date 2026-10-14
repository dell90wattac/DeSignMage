using System.Text;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using DeSignMage.Formats.Vector;
using DeSignMage.Rendering.Export;
using Xunit.Abstractions;

namespace DeSignMage.Formats.Tests;

/// <summary>Helpers shared by the vector import tests.</summary>
internal static class VectorTestKit
{
    public static VectorImportResult ReadSvg(string svg, VectorImportOptions? options = null) =>
        VectorImport.Read(new MemoryStream(Encoding.UTF8.GetBytes(svg)), VectorFormat.Svg, options);

    public static List<DocObject> Drawn(VectorImportResult r) => r.Document.Layers.SelectMany(l => l.Children).ToList();

    public static Rect2 Bounds(DocObject o) => o.GetWorldBounds();

    public static void AssertBounds(Rect2 actual, double left, double top, double right, double bottom, double tol = 0.01)
    {
        Assert.True(Math.Abs(actual.Left - left) <= tol && Math.Abs(actual.Top - top) <= tol && Math.Abs(actual.Right - right) <= tol && Math.Abs(actual.Bottom - bottom) <= tol,
            $"expected [{left}, {top}, {right}, {bottom}] but was {actual}");
    }

    public static DocColor FillColor(DocObject o) => Assert.IsType<SolidPaint>(o.Style.Fill).Color;
}

/// <summary>Reading SVG files: hand-written ones for each feature and files our own exporter wrote.</summary>
public class VectorSvgTests(ITestOutputHelper output)
{
    private const string Header = "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\"";

    // ---------------------------------------------------------------- page and units

    [Fact]
    public void The_page_is_the_size_of_the_svg_in_points_and_the_view_box_scales_the_drawing()
    {
        var r = VectorTestKit.ReadSvg($"{Header} width=\"2in\" height=\"1in\" viewBox=\"0 0 200 100\"><rect x=\"10\" y=\"20\" width=\"100\" height=\"50\" fill=\"#ff8000\"/></svg>");
        Assert.Equal(VectorFormat.Svg, r.Format);
        Assert.Equal(144, r.Document.Page.Width, 6);
        Assert.Equal(72, r.Document.Page.Height, 6);
        var o = Assert.Single(VectorTestKit.Drawn(r));
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(o), 7.2, 14.4, 79.2, 50.4);          // 0.72 points per unit
        Assert.Equal(new DocColor(255, 128, 0), VectorTestKit.FillColor(o));
    }

    [Fact]
    public void Without_a_size_user_units_are_css_pixels()
    {
        var r = VectorTestKit.ReadSvg($"{Header} viewBox=\"0 0 400 200\"><rect width=\"400\" height=\"200\"/></svg>");
        Assert.Equal(300, r.Document.Page.Width, 6);
        Assert.Equal(150, r.Document.Page.Height, 6);

        var mm = VectorTestKit.ReadSvg($"{Header} width=\"210mm\" height=\"297mm\"><rect width=\"100\" height=\"100\"/></svg>");
        Assert.Equal(210 / 25.4 * 72, mm.Document.Page.Width, 4);
        Assert.Equal(297 / 25.4 * 72, mm.Document.Page.Height, 4);
    }

    // ---------------------------------------------------------------- shapes and paths

    [Fact]
    public void Basic_shapes_become_path_objects_in_document_order()
    {
        var r = VectorTestKit.ReadSvg($@"{Header} width=""400"" height=""300"">
            <rect x=""10"" y=""10"" width=""100"" height=""50"" rx=""10"" fill=""red""/>
            <circle cx=""200"" cy=""50"" r=""30"" fill=""rgb(0,128,0)""/>
            <ellipse cx=""300"" cy=""50"" rx=""40"" ry=""20"" fill=""#00f""/>
            <line x1=""10"" y1=""100"" x2=""110"" y2=""150"" stroke=""black"" stroke-width=""4""/>
            <polyline points=""10,200 60,250 110,200"" fill=""none"" stroke=""#123456""/>
            <polygon points=""200,200 250,250 150,250"" fill=""yellow""/>
        </svg>");
        var d = VectorTestKit.Drawn(r);
        Assert.Equal(6, d.Count);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(d[0]), 7.5, 7.5, 82.5, 45);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(d[1]), 127.5, 15, 172.5, 60);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(d[2]), 195, 22.5, 255, 52.5);
        Assert.Equal(new DocColor(0, 128, 0), VectorTestKit.FillColor(d[1]));
        Assert.Null(d[3].Style.Fill);                                       // a line has no area
        Assert.Equal(3.0, d[3].Style.Stroke!.Width, 6);
        Assert.Null(d[4].Style.Fill);
        Assert.Equal(new DocColor(0x12, 0x34, 0x56), ((SolidPaint)d[4].Style.Stroke!.Paint).Color);
        Assert.Equal(new DocColor(255, 255, 0), VectorTestKit.FillColor(d[5]));
    }

    [Fact]
    public void Path_data_with_relative_commands_smooth_curves_and_arcs()
    {
        var r = VectorTestKit.ReadSvg($@"{Header} width=""400"" height=""400"">
            <path d=""M 10 10 h 100 v 50 l -100 0 z"" fill=""red""/>
            <path d=""M 200 100 C 200 50 250 50 250 100 S 300 150 300 100"" fill=""none"" stroke=""black""/>
            <path d=""M 50 300 A 50 50 0 0 1 150 300 L 150 350 Z"" fill=""blue""/>
            <path d=""M 300 300 Q 350 250 400 300 T 500 300"" fill=""none"" stroke=""black""/>
            <path d=""m10 10m20 20 5 5"" stroke=""black""/>
        </svg>");
        var d = VectorTestKit.Drawn(r).Select(o => (PathObject)o).ToList();
        VectorTestKit.AssertBounds(d[0].GetWorldBounds(), 7.5, 7.5, 82.5, 45);
        Assert.Contains(PathVerb.CubicTo, d[1].Geometry.Verbs);
        Assert.True(d[2].Geometry.HasArcs);                                   // a circular arc stays an arc
        VectorTestKit.AssertBounds(d[2].GetWorldBounds(), 37.5, 187.5, 112.5, 262.5, 0.02);
        Assert.Contains(PathVerb.CubicTo, d[3].Geometry.Verbs);               // quadratics are cubics inside
        Assert.Equal(2, d[4].Geometry.Verbs.Count(v => v == PathVerb.MoveTo)); // the pair after the second moveto is a lineto
    }

    [Fact]
    public void An_elliptical_arc_is_drawn_with_cubics_and_stays_inside_its_box()
    {
        var r = VectorTestKit.ReadSvg($"{Header} width=\"300\" height=\"300\"><path d=\"M 50 100 A 100 40 0 1 1 250 100\" fill=\"none\" stroke=\"black\"/></svg>");
        var o = Assert.Single(VectorTestKit.Drawn(r));
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(o), 37.5, 75 - 30, 187.5, 75 + 30 - 30 + 30, 40);
        var b = VectorTestKit.Bounds(o);
        Assert.InRange(b.Width, 100, 150.1);
    }

    [Fact]
    public void Fill_rule_even_odd_makes_a_hole()
    {
        var r = VectorTestKit.ReadSvg($"{Header} width=\"200\" height=\"200\"><path fill-rule=\"evenodd\" d=\"M0 0H100V100H0Z M25 25H75V75H25Z\"/></svg>");
        var o = (PathObject)Assert.Single(VectorTestKit.Drawn(r));
        Assert.Equal(FillRule.EvenOdd, o.Geometry.FillRule);
        Assert.False(o.Geometry.Contains(new Vec2(37.5, 37.5)));
        Assert.True(o.Geometry.Contains(new Vec2(10, 10)));
    }

    // ---------------------------------------------------------------- groups, transforms, styles

    [Fact]
    public void Groups_become_groups_and_transforms_stack()
    {
        var r = VectorTestKit.ReadSvg($@"{Header} width=""400"" height=""400"">
            <g id=""outer"" transform=""translate(100 50)"">
              <rect width=""10"" height=""10"" fill=""red""/>
              <g id=""inner"" transform=""scale(2) rotate(90)"">
                <rect width=""10"" height=""20"" fill=""blue""/>
                <rect x=""20"" width=""10"" height=""20"" fill=""green""/>
              </g>
            </g>
            <rect x=""300"" y=""300"" width=""10"" height=""10"" fill=""black""/>
        </svg>");
        var d = VectorTestKit.Drawn(r);
        Assert.Equal(4, d.Count);
        Assert.Equal(2, r.Document.GroupCount);
        var inner = Assert.IsType<GroupObject>(d[1].Parent);
        Assert.Same(inner, d[2].Parent);
        Assert.Equal("inner", inner.Name);
        var outer = Assert.IsType<GroupObject>(inner.Parent);
        Assert.Same(outer, d[0].Parent);
        Assert.IsAssignableFrom<Layer>(d[3].Parent);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(d[0]), 75, 37.5, 82.5, 45);
        // scale(2) rotate(90): the box 10 x 20 turns into 40 wide and 20 tall to the left of x = 0
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(d[1]), 75 - 30, 37.5, 75, 37.5 + 15);
    }

    [Fact]
    public void A_group_with_one_object_is_not_a_group()
    {
        var r = VectorTestKit.ReadSvg($"{Header} width=\"100\" height=\"100\"><g><g><rect width=\"10\" height=\"10\"/></g></g></svg>");
        Assert.Equal(0, r.Document.GroupCount);
        Assert.Single(VectorTestKit.Drawn(r));
    }

    [Fact]
    public void Layers_of_an_editor_become_layers()
    {
        var r = VectorTestKit.ReadSvg($@"{Header} width=""100"" height=""100"">
            <g inkscape:groupmode=""layer"" inkscape:label=""Back""><rect width=""10"" height=""10"" fill=""red""/></g>
            <g inkscape:groupmode=""layer"" inkscape:label=""Front""><rect width=""10"" height=""10"" fill=""blue""/><rect x=""20"" width=""10"" height=""10""/></g>
        </svg>");
        Assert.Equal(["Back", "Front"], r.Document.Layers.Select(l => l.Name));
        Assert.Equal([1, 2], r.Document.Layers.Select(l => l.Children.Count));
        Assert.Equal(0, r.Document.GroupCount);
    }

    [Fact]
    public void Attributes_style_attributes_and_style_sheets_combine_in_that_order()
    {
        var r = VectorTestKit.ReadSvg($@"{Header} width=""100"" height=""100"">
            <style>.a {{ fill: #00ff00; stroke: #000 }} rect {{ fill: #0000ff }} #x {{ stroke-width: 6 }}</style>
            <rect class=""a"" width=""10"" height=""10"" fill=""red""/>
            <rect id=""x"" class=""a"" width=""10"" height=""10"" style=""fill:#ff00ff""/>
            <rect width=""10"" height=""10"" fill=""red"" fill-opacity=""0.5"" opacity=""0.5""/>
        </svg>");
        var d = VectorTestKit.Drawn(r);
        Assert.Equal(new DocColor(0, 255, 0), VectorTestKit.FillColor(d[0]));        // the class beats the attribute
        Assert.Equal(new DocColor(255, 0, 255), VectorTestKit.FillColor(d[1]));      // the style attribute beats the class
        Assert.Equal(6 * 0.75, d[1].Style.Stroke!.Width, 6);
        Assert.InRange((int)VectorTestKit.FillColor(d[2]).A, 63, 65);                         // 0.5 * 0.5 * 255
    }

    [Fact]
    public void Stroke_properties_and_inheritance()
    {
        var r = VectorTestKit.ReadSvg($@"{Header} width=""100"" height=""100"">
            <g fill=""none"" stroke=""#336699"" stroke-width=""4"" stroke-linecap=""round"" stroke-linejoin=""bevel"" stroke-miterlimit=""2"">
              <path d=""M 10 10 L 90 90""/>
              <path d=""M 10 90 L 90 10"" stroke-dasharray=""4 2""/>
            </g></svg>");
        var d = VectorTestKit.Drawn(r);
        var s = d[0].Style.Stroke!;
        Assert.Equal((LineCap.Round, LineJoin.Bevel, 2.0, 3.0), (s.Cap, s.Join, s.MiterLimit, s.Width));
        Assert.Contains(r.Warnings, w => w.Contains("dashed"));
    }

    [Fact]
    public void Hidden_and_invisible_things_are_left_out_and_current_color_works()
    {
        var r = VectorTestKit.ReadSvg($@"{Header} width=""100"" height=""100"" color=""#aa0000"">
            <rect width=""10"" height=""10"" display=""none""/>
            <rect width=""10"" height=""10"" visibility=""hidden""/>
            <rect width=""10"" height=""10"" fill=""currentColor""/>
            <rect width=""10"" height=""10"" fill=""none""/>
        </svg>");
        var o = Assert.Single(VectorTestKit.Drawn(r));
        Assert.Equal(new DocColor(0xAA, 0, 0), VectorTestKit.FillColor(o));
    }

    [Fact]
    public void Use_and_defs_instantiate_the_referenced_element()
    {
        var r = VectorTestKit.ReadSvg($@"{Header} width=""200"" height=""100"">
            <defs><rect id=""r"" width=""10"" height=""10"" fill=""red""/><symbol id=""s"" viewBox=""0 0 10 10""><circle cx=""5"" cy=""5"" r=""5"" fill=""blue""/></symbol></defs>
            <use xlink:href=""#r"" x=""50"" y=""20""/>
            <use href=""#r"" transform=""translate(100 0)""/>
            <use href=""#s"" x=""10"" y=""10"" width=""40"" height=""40""/>
        </svg>");
        var d = VectorTestKit.Drawn(r);
        Assert.Equal(3, d.Count);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(d[0]), 37.5, 15, 45, 22.5);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(d[1]), 75, 0, 82.5, 7.5);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(d[2]), 7.5, 7.5, 37.5, 37.5, 0.05);
    }

    // ---------------------------------------------------------------- gradients

    [Fact]
    public void A_linear_gradient_in_object_bounding_box_units_keeps_its_colors_and_places()
    {
        var r = VectorTestKit.ReadSvg($@"{Header} width=""200"" height=""100"">
            <defs><linearGradient id=""g"" x1=""0"" y1=""0"" x2=""1"" y2=""0"">
              <stop offset=""0"" stop-color=""#ff0000""/><stop offset=""0.25"" stop-color=""#00ff00"" stop-opacity=""0.5""/><stop offset=""100%"" stop-color=""blue""/>
            </linearGradient></defs>
            <rect x=""20"" y=""10"" width=""100"" height=""50"" fill=""url(#g)""/></svg>");
        var o = Assert.Single(VectorTestKit.Drawn(r));
        var g = Assert.IsType<GradientPaint>(o.Style.Fill);
        Assert.Equal(GradientKind.Linear, g.Kind);
        Assert.Equal([0, 0.25, 1], g.Stops.Select(s => s.Offset));
        Assert.Equal(new DocColor(255, 0, 0), g.Stops[0].Color);
        Assert.Equal(128, g.Stops[1].Color.A);
        Assert.Equal(new DocColor(0, 0, 255), g.Stops[2].Color);
        Assert.Equal([new Vec2(0, 0), new Vec2(1, 0)], g.Points);
    }

    [Fact]
    public void A_gradient_in_user_space_with_a_transform_and_inherited_stops()
    {
        var r = VectorTestKit.ReadSvg($@"{Header} width=""400"" height=""200"">
            <defs>
              <linearGradient id=""base""><stop offset=""0"" stop-color=""white""/><stop offset=""1"" stop-color=""black""/></linearGradient>
              <linearGradient id=""g"" xlink:href=""#base"" gradientUnits=""userSpaceOnUse"" x1=""0"" y1=""0"" x2=""200"" y2=""0"" gradientTransform=""translate(100 0)""/>
            </defs>
            <rect x=""100"" y=""0"" width=""200"" height=""100"" fill=""url(#g)""/></svg>");
        var o = Assert.Single(VectorTestKit.Drawn(r));
        var g = Assert.IsType<GradientPaint>(o.Style.Fill);
        var frame = g.Frame(o.GetLocalGeometry()!.GetBounds());
        Assert.Equal(75, frame.P0.X, 4);                                    // user x = 100 at 0.75 points per unit
        Assert.Equal(225, frame.P1.X, 4);
        Assert.Equal(new DocColor(255, 255, 255), g.Stops[0].Color);
    }

    [Fact]
    public void A_radial_gradient_is_a_circle_gradient_and_stretches_to_an_ellipse_on_a_wide_box()
    {
        var square = VectorTestKit.ReadSvg($@"{Header} width=""100"" height=""100"">
            <defs><radialGradient id=""g"" cx=""0.5"" cy=""0.5"" r=""0.5""><stop offset=""0"" stop-color=""white""/><stop offset=""1"" stop-color=""navy""/></radialGradient></defs>
            <rect width=""100"" height=""100"" fill=""url(#g)""/></svg>");
        var g = Assert.IsType<GradientPaint>(Assert.Single(VectorTestKit.Drawn(square)).Style.Fill);
        Assert.Equal(GradientKind.Circle, g.Kind);
        Assert.Equal(new Vec2(0.5, 0.5), g.Points[0]);
        Assert.Equal(1.0, g.Points[1].X, 6);

        var wide = VectorTestKit.ReadSvg($@"{Header} width=""200"" height=""100"">
            <defs><radialGradient id=""g""><stop offset=""0"" stop-color=""white""/><stop offset=""1"" stop-color=""navy""/></radialGradient></defs>
            <rect width=""200"" height=""100"" fill=""url(#g)""/></svg>");
        Assert.Equal(GradientKind.Ellipse, ((GradientPaint)Assert.Single(VectorTestKit.Drawn(wide)).Style.Fill!).Kind);
    }

    // ---------------------------------------------------------------- clip, text, pictures

    [Fact]
    public void A_clip_path_cuts_a_shape_with_the_boolean_service_and_is_reported_without_it()
    {
        string svg = $@"{Header} width=""200"" height=""200"">
            <defs><clipPath id=""c""><rect x=""0"" y=""0"" width=""50"" height=""200""/></clipPath></defs>
            <rect x=""0"" y=""0"" width=""100"" height=""100"" fill=""red"" clip-path=""url(#c)""/></svg>";
        var cut = VectorTestKit.ReadSvg(svg, new VectorImportOptions { BooleanService = WriterTestKit.Booleans });
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(Assert.Single(VectorTestKit.Drawn(cut))), 0, 0, 37.5, 75);

        var plain = VectorTestKit.ReadSvg(svg);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(Assert.Single(VectorTestKit.Drawn(plain))), 0, 0, 75, 75);
        Assert.Contains(plain.Warnings, w => w.Contains("clipping"));

        // a clip that already holds the shape changes nothing and needs no service
        var whole = VectorTestKit.ReadSvg(svg.Replace("width=\"50\" height=\"200\"", "width=\"150\" height=\"200\""));
        Assert.DoesNotContain(whole.Warnings, w => w.Contains("clipping"));
    }

    [Fact]
    public void Text_becomes_live_text_with_its_font_size_anchor_and_color()
    {
        var r = VectorTestKit.ReadSvg($@"{Header} width=""400"" height=""200"">
            <text x=""200"" y=""100"" font-family=""Arial, sans-serif"" font-size=""40"" font-weight=""bold"" text-anchor=""middle"" fill=""#c00"">Hello <tspan fill=""blue"" font-style=""italic"">there</tspan></text></svg>");
        var texts = VectorTestKit.Drawn(r).OfType<TextObject>().ToList();
        Assert.Equal(2, texts.Count);
        Assert.Equal("Hello ", texts[0].Text.TrimEnd() + " ");
        Assert.Equal("Arial", texts[0].FontFamily);
        Assert.Equal(30.0, texts[0].FontSize, 6);                           // 40 units at 0.75
        Assert.Equal(700, texts[0].FontWeight);
        Assert.Equal(TextAlignment.Center, texts[0].Alignment);
        Assert.Equal(new DocColor(0xCC, 0, 0), VectorTestKit.FillColor(texts[0]));
        Assert.True(texts[1].Italic);
        Assert.Equal(new DocColor(0, 0, 255), VectorTestKit.FillColor(texts[1]));
        Assert.Equal(150.0, texts[0].Transform.OffsetX, 6);                  // the anchor, in points
        Assert.Equal(75.0, texts[0].Transform.OffsetY, 6);
    }

    [Fact]
    public void Live_text_can_be_switched_off_for_outlines()
    {
        var r = VectorTestKit.ReadSvg($"{Header} width=\"200\" height=\"100\"><text x=\"10\" y=\"50\" font-size=\"30\">Hi</text></svg>", new VectorImportOptions { LiveText = false });
        var o = Assert.Single(VectorTestKit.Drawn(r));
        Assert.IsType<PathObject>(o);
        Assert.False(o.GetLocalGeometry()!.IsEmpty);
    }

    [Fact]
    public void A_picture_in_a_data_uri_becomes_an_image_object_that_keeps_its_proportions()
    {
        string png = Convert.ToBase64String(WriterTestKit.Png(40, 20));
        var r = VectorTestKit.ReadSvg($@"{Header} width=""400"" height=""200"">
            <image x=""20"" y=""20"" width=""100"" height=""100"" xlink:href=""data:image/png;base64,{png}""/></svg>");
        var img = Assert.IsType<ImageObject>(Assert.Single(VectorTestKit.Drawn(r)));
        Assert.Equal((40, 20), (img.PixelWidth, img.PixelHeight));
        var b = img.GetWorldBounds();                                         // 2:1 in a square box, centered
        Assert.Equal(75.0, b.Width, 4);
        Assert.Equal(37.5, b.Height, 4);
        Assert.Equal(15.0, b.Left, 4);
        Assert.Equal(15 + (75 - 37.5) / 2, b.Top, 4);
        Assert.Equal(WriterTestKit.Png(40, 20), r.Document.Assets.Get(img.AssetId)!.Data);
    }

    // ---------------------------------------------------------------- problems

    [Fact]
    public void Things_that_are_not_read_are_reported()
    {
        var r = VectorTestKit.ReadSvg($@"{Header} width=""100"" height=""100"">
            <defs><filter id=""f""><feGaussianBlur stdDeviation=""2""/></filter><pattern id=""p"" width=""4"" height=""4"" patternUnits=""userSpaceOnUse""><rect width=""2"" height=""2""/></pattern></defs>
            <rect width=""50"" height=""50"" filter=""url(#f)"" fill=""url(#p)""/></svg>");
        Assert.Single(VectorTestKit.Drawn(r));
        Assert.Contains(r.Warnings, w => w.Contains("Filters"));
        Assert.Contains(r.Warnings, w => w.Contains("pattern"));
    }

    [Fact]
    public void A_file_that_is_not_svg_is_refused_with_a_message()
    {
        var notXml = Assert.Throws<VectorImportException>(() => VectorTestKit.ReadSvg("this is not xml"));
        Assert.Contains("XML", notXml.Message);
        var other = Assert.Throws<VectorImportException>(() => VectorTestKit.ReadSvg("<html><body/></html>"));
        Assert.Contains("SVG", other.Message);
    }

    [Fact]
    public void Extensions_and_detection()
    {
        Assert.True(VectorImport.CanRead("a.PDF"));
        Assert.True(VectorImport.CanRead(@"C:\x\y.svg"));
        Assert.True(VectorImport.CanRead("logo.ai") && VectorImport.CanRead("logo.eps"));
        Assert.False(VectorImport.CanRead("pic.png"));
        Assert.False(VectorImport.CanRead("design.lyt"));
        Assert.Equal([".pdf", ".ai", ".eps", ".svg"], VectorImport.Extensions);
        Assert.Equal(VectorFormat.Svg, VectorImport.Detect("<svg "u8, ".svg"));
        Assert.Equal(VectorFormat.Ai, VectorImport.Detect("%PDF-1.5"u8, ".ai"));
        Assert.Equal(VectorFormat.Pdf, VectorImport.Detect("%PDF-1.5"u8, ".pdf"));
        Assert.Equal(VectorFormat.Eps, VectorImport.Detect("%!PS-Adobe-3.0 EPSF-3.0"u8, ".eps"));
        Assert.Equal(VectorFormat.Ai, VectorImport.Detect("%!PS-Adobe-3.0"u8, ".ai"));
    }

    // ---------------------------------------------------------------- our own exporter

    [Fact]
    public void A_file_our_own_exporter_wrote_comes_back_with_the_same_shapes_colors_and_groups()
    {
        var doc = WriterTestKit.NewDoc(8, 4);
        var a = WriterTestKit.Place(doc, new RectangleObject(100, 50, 8), 20, 30, WriterTestKit.Red, "Box");
        var b = WriterTestKit.Place(doc, new EllipseObject(80, 80), 160, 20, WriterTestKit.Blue, "Dot");
        b.Style = new ObjectStyle(new SolidPaint(WriterTestKit.Blue), StrokeStyle.Solid(WriterTestKit.Green, 5));
        var tri = new PathObject(new PathBuilder().MoveTo(0, 0).LineTo(60, 0).CubicTo(80, 20, 80, 40, 30, 70).Close().Build()) { Transform = Affine2.Rotation(0.3) * Affine2.Translation(300, 100) };
        tri.Style = ObjectStyle.FillOnly(WriterTestKit.Green);
        doc.Insert(doc.ActiveLayer, -1, tri);
        var grad = new RectangleObject(100, 40) { Transform = Affine2.Translation(30, 160) };
        grad.Style = new ObjectStyle(new GradientPaint(GradientKind.Linear, GradientPaint.DefaultPoints(GradientKind.Linear), [new GradientNode(0, WriterTestKit.Red), new GradientNode(1, WriterTestKit.Blue)]), null);
        doc.Insert(doc.ActiveLayer, -1, grad);
        var group = new GroupObject();
        doc.Insert(doc.ActiveLayer, -1, group);
        doc.SetGroup(a, group); doc.SetGroup(b, group);

        var bytes = DocumentExporter.Render(doc, ExportFormat.Svg, new ExportOptions());
        output.WriteLine(Encoding.UTF8.GetString(bytes)[..Math.Min(900, bytes.Length)]);
        var back = VectorImport.Read(new MemoryStream(bytes), VectorFormat.Svg);
        var drawn = VectorTestKit.Drawn(back);

        Assert.Equal(doc.Page.Width, back.Document.Page.Width, 3);
        Assert.Equal(doc.Page.Height, back.Document.Page.Height, 3);
        Assert.Equal(1, back.Document.GroupCount);
        // the stroke of the dot is a part of its element; the tests compare the filled shapes
        Assert.Equal(4, drawn.Count);
        foreach (var (orig, got) in new (DocObject, DocObject)[] { (a, drawn[0]), (b, drawn[1]), (tri, drawn[2]), (grad, drawn[3]) })
        {
            var oa = orig.GetWorldBounds(); var ob = got.GetWorldBounds();
            double stroke = orig.Style.StrokeOutset;
            Assert.True(Math.Abs(oa.Left - ob.Left) < 0.5 + stroke && Math.Abs(oa.Right - ob.Right) < 0.5 + stroke && Math.Abs(oa.Top - ob.Top) < 0.5 + stroke && Math.Abs(oa.Bottom - ob.Bottom) < 0.5 + stroke,
                $"{orig.Name}: {oa} vs {ob}");
        }
        Assert.Equal((WriterTestKit.Red.R, WriterTestKit.Red.G, WriterTestKit.Red.B), (VectorTestKit.FillColor(drawn[0]).R, VectorTestKit.FillColor(drawn[0]).G, VectorTestKit.FillColor(drawn[0]).B));
        Assert.Equal(5.0, drawn[1].Style.Stroke!.Width, 3);
        Assert.IsType<GradientPaint>(drawn[3].Style.Fill);
        Assert.Same(drawn[0].Parent, drawn[1].Parent);
    }
}
