using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using DeSignMage.Rendering.Export;
using SkiaSharp;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>Gradient fills in the document file and in every export: .dsm round trip, SVG, PNG pixels, PDF.</summary>
public class GradientFileTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly DocColor Red = new(255, 0, 0), Blue = new(0, 0, 255), White = new(255, 255, 255);
    private static readonly DocColor Vinyl = new(0xF2, 0xB7, 0x05, 255, "Oracal 651-020 Golden Yellow", "Oracal 651");

    private static GradientPaint Horizontal(DocColor a, DocColor b) =>
        new(GradientKind.Linear, [new Vec2(0, 0.5), new Vec2(1, 0.5)], [new GradientNode(0, a), new GradientNode(1, b)]);

    private static Document OneObject(DocObject o, double widthIn = 4, double heightIn = 1)
    {
        var doc = new Document(PageSetup.FromInches(widthIn, heightIn));
        doc.Insert(doc.Layers[0], -1, o);
        return doc;
    }

    private static SKBitmap Png(Document doc, double dpi = 72) =>
        SKBitmap.Decode(DocumentExporter.RenderPng(doc, new ExportOptions { Dpi = dpi })) ?? throw new InvalidOperationException("no PNG");

    private static int Close(SKColor c, int r, int g, int b) => Math.Max(Math.Abs(c.Red - r), Math.Max(Math.Abs(c.Green - g), Math.Abs(c.Blue - b)));

    // ---------------------------------------------------------------- the .dsm file

    private static Document Sample()
    {
        var doc = new Document(PageSetup.FromInches(10, 6));
        var l = doc.Layers[0];
        var cmyk = DocColor.FromCmyk(10.5, 20, 30.25, 40, "Warm Grey 4", "Pantone");
        var many = new GradientPaint(GradientKind.Linear, [new Vec2(0.1, 0.2), new Vec2(0.9, 0.7)],
            [new GradientNode(0, Vinyl), new GradientNode(0.3, cmyk), new GradientNode(0.45, DocColor.White, Highlight: true), new GradientNode(0.6, cmyk),
             new GradientNode(1, new DocColor(1, 2, 3, 40))], alternateColors: true, reflect: true);
        doc.Insert(l, -1, new RectangleObject(100, 60) { Name = "many", Style = new ObjectStyle(many, StrokeStyle.Solid(DocColor.Black, 1)) });
        doc.Insert(l, -1, new EllipseObject(80, 40) { Transform = Affine2.Rotation(0.4), Style = new ObjectStyle(
            new GradientPaint(GradientKind.Ellipse, [new Vec2(0.5, 0.5), new Vec2(1, 0.5), new Vec2(0.5, 0.9)], [new GradientNode(0, Red), new GradientNode(1, Blue)]), null) });
        doc.Insert(l, -1, new TextObject("Fade", "Arial", 30) { Style = new ObjectStyle(Horizontal(Vinyl, Vinyl with { A = 0 }), null) });
        doc.Insert(l, -1, new CombinedObject([
            new RectangleObject(30, 30) { Style = new ObjectStyle(new GradientPaint(GradientKind.Circle, GradientPaint.DefaultPoints(GradientKind.Circle), [new GradientNode(0, Red), new GradientNode(1, White)]), null) },
            new RectangleObject(30, 30) { Transform = Affine2.Translation(10, 10), Style = ObjectStyle.FillOnly(Blue) },
        ]));
        return doc;
    }

    [Fact]
    public void A_document_with_gradients_comes_back_exactly_after_save_and_load()
    {
        var doc = Sample();
        var loaded = DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(doc)));

        Assert.Empty(loaded.Warnings);
        ModelCompare.AssertEqual(doc, loaded.Document);
        var many = loaded.Document.AllObjects().Single(o => o.Name == "many");
        var g = Assert.IsType<GradientPaint>(many.Style.Fill);
        Assert.True(g.AlternateColors && g.Reflect);
        Assert.Equal(5, g.Stops.Count);
        Assert.True(g.Stops[2].Highlight);
        Assert.Equal(new DocColor(1, 2, 3, 40), g.Stops[4].Color);                          // a color fading out keeps its alpha
        Assert.Equal(ColorModel.Cmyk, g.Stops[1].Color.Model);                              // and a named CMYK stop keeps its identity
        Assert.Equal("Warm Grey 4", g.Stops[1].Color.Name);
        Assert.Equal(Vinyl, g.Stops[0].Color);
    }

    [Fact]
    public void The_file_stores_the_gradient_as_a_readable_paint()
    {
        var json = Zip.DocumentJson(DsmWriter.SaveToBytes(Sample()));
        Assert.Contains("\"type\": \"gradient\"", json);
        Assert.Contains("\"kind\": \"ellipse\"", json);
        Assert.Contains("\"highlight\": true", json);
        Assert.Contains("\"reflect\": true", json);
    }

    [Fact]
    public void A_damaged_gradient_is_refused_with_a_message_and_a_wrong_point_count_falls_back_to_the_default_line()
    {
        using var one = JsonDocument.Parse("""{ "kind": "linear", "points": [0,0,1,1], "stops": [ { "offset": 0, "color": "#FF0000" } ] }""");
        Assert.Throws<DsmFormatException>(() => GradientJson.Read(one.RootElement));

        using var oddPoints = JsonDocument.Parse("""{ "kind": "ellipse", "points": [0,0,1,1], "stops": [ { "offset": 0, "color": "#FF0000" }, { "offset": 1, "color": "#0000FF" } ] }""");
        var g = GradientJson.Read(oddPoints.RootElement);
        Assert.Equal(GradientPaint.DefaultPoints(GradientKind.Ellipse), g.Points);
    }

    // ---------------------------------------------------------------- pixels (PNG, the same shader the screen uses)

    [Fact]
    public void A_linear_gradient_runs_from_the_first_color_to_the_last_across_the_shape()
    {
        var doc = OneObject(new RectangleObject(288, 72) { Style = new ObjectStyle(Horizontal(Red, Blue), null) });
        using var bmp = Png(doc);

        Assert.True(Close(bmp.GetPixel(1, 36), 255, 0, 0) <= 6, bmp.GetPixel(1, 36).ToString());
        Assert.True(Close(bmp.GetPixel(286, 36), 0, 0, 255) <= 6, bmp.GetPixel(286, 36).ToString());
        var mid = bmp.GetPixel(144, 36);                                                    // red to blue the short way: through magenta
        Assert.True(mid.Red > 220 && mid.Blue > 220 && mid.Green < 40, mid.ToString());
        Assert.Equal(bmp.GetPixel(144, 5), bmp.GetPixel(144, 66));                          // the line is horizontal: nothing changes down the shape
    }

    [Fact]
    public void A_gradient_follows_the_rotation_of_its_object()
    {
        // The same horizontal gradient on a rectangle turned a quarter turn runs top to bottom.
        var r = new RectangleObject(144, 72)
        {
            Transform = Affine2.Rotation(Math.PI / 2, new Vec2(72, 36)) * Affine2.Translation(72, 0),
            Style = new ObjectStyle(Horizontal(Red, Blue), null),
        };
        using var bmp = Png(OneObject(r, 4, 3));
        // The turned rectangle covers x 108..180 and y -36..108 of the page.
        var top = bmp.GetPixel(144, 5);
        var bottom = bmp.GetPixel(144, 100);
        var left = bmp.GetPixel(112, 50); var right = bmp.GetPixel(176, 50);
        Assert.True(top.Red > bottom.Red && bottom.Blue > top.Blue, $"{top} / {bottom}");   // red at the top, blue at the bottom
        Assert.True(Close(left, right.Red, right.Green, right.Blue) <= 8, $"{left} vs {right}");   // no change sideways
    }

    [Fact]
    public void Circle_and_ellipse_gradients_blend_outward_from_the_center()
    {
        var circle = new RectangleObject(144, 144) { Style = new ObjectStyle(new GradientPaint(GradientKind.Circle, GradientPaint.DefaultPoints(GradientKind.Circle), [new GradientNode(0, Red), new GradientNode(1, White)]), null) };
        using var bmp = Png(OneObject(circle, 2, 2));
        Assert.True(Close(bmp.GetPixel(72, 72), 255, 0, 0) <= 6);                           // the center is the first color
        var rim = bmp.GetPixel(142, 72);                                                    // the radius is half the width: nearly white at the rim
        Assert.True(rim.Green > 225 && rim.Blue > 225, rim.ToString());
        Assert.True(Close(bmp.GetPixel(72, 2), bmp.GetPixel(142, 72).Red, bmp.GetPixel(142, 72).Green, bmp.GetPixel(142, 72).Blue) <= 12);   // a circle: same at the top

        var ellipse = new RectangleObject(288, 72) { Style = new ObjectStyle(new GradientPaint(GradientKind.Ellipse, GradientPaint.DefaultPoints(GradientKind.Ellipse), [new GradientNode(0, Red), new GradientNode(1, White)]), null) };
        using var e = Png(OneObject(ellipse));
        Assert.True(Close(e.GetPixel(144, 36), 255, 0, 0) <= 6);
        var side = e.GetPixel(284, 36); var top = e.GetPixel(144, 2);
        Assert.True(side.Green > 215 && top.Green > 215, $"{side} {top}");                  // both axis ends are (nearly) the last color
    }

    [Fact]
    public void A_color_fading_to_transparent_shows_what_is_behind_it()
    {
        var doc = OneObject(new RectangleObject(288, 72) { Style = new ObjectStyle(Horizontal(Red, Red with { A = 0 }), null) });
        using var bmp = Png(doc);
        Assert.True(Close(bmp.GetPixel(2, 36), 255, 0, 0) <= 8);
        var mid = bmp.GetPixel(144, 36);                                                    // half red over white: pink
        Assert.True(mid.Red > 245 && mid.Green is > 105 and < 150 && Math.Abs(mid.Green - mid.Blue) <= 4, mid.ToString());
        Assert.True(bmp.GetPixel(286, 36).Green > 245);                                      // gone at the end

        using var clear = SKBitmap.Decode(DocumentExporter.RenderPng(doc, new ExportOptions { Dpi = 72, TransparentBackground = true }))!;
        Assert.InRange(clear.GetPixel(144, 36).Alpha, 115, 140);                             // a real alpha channel when the background is transparent
    }

    [Fact]
    public void Reflect_and_alternate_colors_show_in_the_picture()
    {
        var g = Horizontal(Red, White);
        using var reflected = Png(OneObject(new RectangleObject(288, 72) { Style = new ObjectStyle(g.WithFlags(false, true), null) }));
        Assert.True(Close(reflected.GetPixel(1, 36), 255, 0, 0) <= 8);
        Assert.True(Close(reflected.GetPixel(286, 36), 255, 0, 0) <= 8);                    // back to the first color at the end
        Assert.True(reflected.GetPixel(144, 36).Green > 235);                               // white in the middle

        using var rainbow = Png(OneObject(new RectangleObject(288, 72) { Style = new ObjectStyle(Horizontal(Red, Blue).WithFlags(true, false), null) }));
        var mid = rainbow.GetPixel(144, 36);
        Assert.True(mid.Green > 200 && mid.Red < 60, mid.ToString());                      // the long way round: green in the middle
    }

    [Fact]
    public void A_gradient_on_live_text_and_on_a_combined_part_paints()
    {
        var text = new TextObject("MMMM", "Arial", 60) { Transform = Affine2.Translation(4, 62), Style = new ObjectStyle(Horizontal(Red, Blue), null) };
        using var bmp = Png(OneObject(text));
        var left = Enumerable.Range(0, 70).SelectMany(x => Enumerable.Range(10, 55).Select(y => bmp.GetPixel(x, y))).Where(c => c.Red < 250 || c.Blue < 250).ToList();
        var right = Enumerable.Range(150, 70).SelectMany(x => Enumerable.Range(10, 55).Select(y => bmp.GetPixel(x, y))).Where(c => c.Red < 250 || c.Blue < 250).ToList();
        Assert.NotEmpty(left);
        Assert.NotEmpty(right);
        Assert.True(left.Average(c => c.Red) > right.Average(c => c.Red) && right.Average(c => c.Blue) > left.Average(c => c.Blue));

        var combined = new CombinedObject([new RectangleObject(100, 50) { Style = new ObjectStyle(Horizontal(Red, Blue), null) }]);
        using var bmp2 = Png(OneObject(combined));
        Assert.True(bmp2.GetPixel(5, 25).Red > bmp2.GetPixel(95, 25).Red);
    }

    // ---------------------------------------------------------------- SVG and PDF

    private static XDocument ParseSvg(Document doc, ExportOptions? o = null) => XDocument.Parse(SvgExporter.Render(doc, o ?? new ExportOptions()));

    [Fact]
    public void Svg_gets_a_real_gradient_with_the_same_colors_the_screen_shows()
    {
        var doc = OneObject(new RectangleObject(288, 72) { Style = new ObjectStyle(Horizontal(Red, Blue), null) });
        var svg = ParseSvg(doc);

        var gradient = Assert.Single(svg.Descendants(Svg + "linearGradient"));
        var id = gradient.Attribute("id")!.Value;
        Assert.Equal($"url(#{id})", Assert.Single(svg.Descendants(Svg + "path")).Attribute("fill")!.Value);
        Assert.Equal("userSpaceOnUse", gradient.Attribute("gradientUnits")!.Value);
        Assert.Equal(("0", "36", "288", "36"), (gradient.Attribute("x1")!.Value, gradient.Attribute("y1")!.Value, gradient.Attribute("x2")!.Value, gradient.Attribute("y2")!.Value));

        var stops = gradient.Elements(Svg + "stop").ToList();
        Assert.True(stops.Count > 8);                                                       // the dense HSV ramp, not just two stops
        Assert.Equal("#FF0000", stops[0].Attribute("stop-color")!.Value);
        Assert.Equal("#0000FF", stops[^1].Attribute("stop-color")!.Value);
        Assert.Equal("0", stops[0].Attribute("offset")!.Value);
        Assert.Equal("1", stops[^1].Attribute("offset")!.Value);
        var mid = stops.OrderBy(s => Math.Abs(double.Parse(s.Attribute("offset")!.Value, CultureInfo.InvariantCulture) - 0.5)).First();
        Assert.Matches("^#(FF|F[0-9A-F]|E[0-9A-F])..(FF|F[0-9A-F]|E[0-9A-F])$", mid.Attribute("stop-color")!.Value.Replace("#", "#") + "");
    }

    [Fact]
    public void Svg_gradients_carry_the_objects_transform_and_alpha()
    {
        var r = new RectangleObject(100, 40)
        {
            Transform = Affine2.Rotation(0.5) * Affine2.Translation(30, 10),
            Style = new ObjectStyle(Horizontal(Red, Red with { A = 0 }), null),
        };
        var svg = ParseSvg(OneObject(r));
        var gradient = Assert.Single(svg.Descendants(Svg + "linearGradient"));
        Assert.Equal(SvgExporter.Matrix(r.GetWorldTransform()), gradient.Attribute("gradientTransform")!.Value);   // exact under rotation
        Assert.Null(gradient.Elements(Svg + "stop").First().Attribute("stop-opacity"));
        Assert.Equal("0", gradient.Elements(Svg + "stop").Last().Attribute("stop-opacity")!.Value);
    }

    [Fact]
    public void Svg_circle_and_ellipse_become_radial_gradients_squeezed_by_their_matrix()
    {
        var g = new GradientPaint(GradientKind.Ellipse, [new Vec2(0.5, 0.5), new Vec2(1, 0.5), new Vec2(0.5, 1)], [new GradientNode(0, Red), new GradientNode(1, White)]);
        var svg = ParseSvg(OneObject(new RectangleObject(200, 100) { Style = new ObjectStyle(g, null) }));
        var radial = Assert.Single(svg.Descendants(Svg + "radialGradient"));
        Assert.Equal("1", radial.Attribute("r")!.Value);
        Assert.Equal("matrix(100 0 0 50 100 50)", radial.Attribute("gradientTransform")!.Value);   // unit circle -> the ellipse, half width and half height
    }

    [Fact]
    public void Svg_live_text_keeps_its_gradient_without_an_extra_matrix()
    {
        var text = new TextObject("Hi", "Arial", 30) { Transform = Affine2.Translation(10, 40), Style = new ObjectStyle(Horizontal(Red, Blue), null) };
        var svg = ParseSvg(OneObject(text), new ExportOptions { TextAsOutlines = false });
        var gradient = Assert.Single(svg.Descendants(Svg + "linearGradient"));
        Assert.Null(gradient.Attribute("gradientTransform"));                                // the text element already has the transform
        Assert.StartsWith("url(#", Assert.Single(svg.Descendants(Svg + "text")).Attribute("fill")!.Value);
    }

    [Fact]
    public void Pdf_exports_with_a_gradient_without_trouble()
    {
        var doc = OneObject(new RectangleObject(288, 72) { Style = new ObjectStyle(Horizontal(Red, Blue), null) });
        var pdf = DocumentExporter.RenderPdf(doc, new ExportOptions());
        Assert.True(pdf.Length > 500);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }
}
