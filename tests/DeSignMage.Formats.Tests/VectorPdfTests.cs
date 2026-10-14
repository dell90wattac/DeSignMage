using System.Text;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using DeSignMage.Formats.Vector;
using SkiaSharp;
using Xunit.Abstractions;

namespace DeSignMage.Formats.Tests;

/// <summary>Reading PDF files: small hand-made files for each feature, plus files our own exporter wrote.</summary>
public class VectorPdfTests(ITestOutputHelper output)
{
    private static VectorImportResult Read(byte[] pdf, VectorImportOptions? options = null, VectorFormat format = VectorFormat.Pdf) =>
        VectorImport.Read(new MemoryStream(pdf), format, options);

    private static VectorImportResult One(string content, Action<MiniPdf, StringBuilder>? resources = null, string mediaBox = "[0 0 200 100]", string pageExtra = "",
        VectorImportOptions? options = null, string catalogExtra = "") =>
        Read(MiniPdf.OnePage(content, resources, mediaBox, pageExtra, catalogExtra), options);

    private static List<DocObject> Drawn(VectorImportResult r) => VectorTestKit.Drawn(r);

    // ---------------------------------------------------------------- page

    [Fact]
    public void The_page_is_the_crop_box_in_points_and_y_runs_down()
    {
        var r = One("1 0 0 rg 20 10 40 30 re f", mediaBox: "[0 0 200 100]");
        Assert.Equal(VectorFormat.Pdf, r.Format);
        Assert.Equal((200.0, 100.0), (r.Document.Page.Width, r.Document.Page.Height));
        var o = Assert.Single(Drawn(r));
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(o), 20, 100 - 40, 60, 100 - 10);
        Assert.Equal(new DocColor(255, 0, 0), VectorTestKit.FillColor(o));
    }

    [Fact]
    public void A_page_box_that_does_not_start_at_the_origin_and_a_turned_page_are_mapped()
    {
        var offset = One("0 g 150 250 20 10 re f", mediaBox: "[100 200 300 300]");
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(Assert.Single(Drawn(offset))), 50, 100 - 60, 70, 100 - 50);

        // the page is 200 x 100 and shown turned 90 degrees clockwise: 100 wide and 200 tall; its bottom-left corner is now the top-left one
        var turned = One("0 g 0 0 20 10 re f", mediaBox: "[0 0 200 100]", pageExtra: "/Rotate 90");
        Assert.Equal((100.0, 200.0), (turned.Document.Page.Width, turned.Document.Page.Height));
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(Assert.Single(Drawn(turned))), 0, 0, 10, 20);
    }

    [Fact]
    public void Pages_are_counted_and_chosen()
    {
        var pdf = new MiniPdf();
        int a = pdf.AddPage("1 0 0 rg 0 0 10 10 re f");
        int b = pdf.AddPage("0 1 0 rg 0 0 30 30 re f", mediaBox: "[0 0 300 300]");
        var bytes = pdf.Build([a, b]);
        var first = Read(bytes);
        Assert.Equal((2, 0), (first.PageCount, first.PageIndex));
        Assert.Equal(200, first.Document.Page.Width);
        var second = Read(bytes, new VectorImportOptions { PageIndex = 1 });
        Assert.Equal(1, second.PageIndex);
        Assert.Equal(300, second.Document.Page.Width);
        Assert.Equal(new DocColor(0, 255, 0), VectorTestKit.FillColor(Assert.Single(Drawn(second))));
        Assert.Equal(1, Read(bytes, new VectorImportOptions { PageIndex = 9 }).PageIndex);   // clamped to the last page

        string path = Path.Combine(Path.GetTempPath(), $"designmage-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        try
        {
            Assert.Equal(2, VectorImport.GetPageCount(path));
            Assert.Equal(VectorFormat.Pdf, VectorImport.Detect(path));
            Assert.Equal(300, VectorImport.Read(path, new VectorImportOptions { PageIndex = 1 }).Document.Page.Width);
        }
        finally { File.Delete(path); }
    }

    // ---------------------------------------------------------------- colors

    [Fact]
    public void Gray_rgb_and_cmyk_colors_keep_their_model()
    {
        var r = One("0.5 g 0 0 10 10 re f  0 0.5 1 rg 20 0 10 10 re f  0.1 0.2 0.3 0.4 k 40 0 10 10 re f  0 0 0 1 k 60 0 10 10 re f");
        var d = Drawn(r);
        Assert.Equal(new DocColor(128, 128, 128), VectorTestKit.FillColor(d[0]));
        Assert.Equal(new DocColor(0, 128, 255), VectorTestKit.FillColor(d[1]));
        var cmyk = VectorTestKit.FillColor(d[2]);
        Assert.Equal(ColorModel.Cmyk, cmyk.Model);
        Assert.Equal(new Cmyk(10, 20, 30, 40), cmyk.Cmyk);
        Assert.Equal(DocColor.Black.R, VectorTestKit.FillColor(d[3]).R);
        Assert.Equal(ColorModel.Cmyk, VectorTestKit.FillColor(d[3]).Model);
    }

    [Fact]
    public void A_spot_color_keeps_its_name_and_its_cmyk_alternate()
    {
        var r = One("/CS1 cs 1 scn 0 0 10 10 re f  /CS1 cs 0.5 scn 20 0 10 10 re f", (pdf, res) =>
        {
            int fn = pdf.AddStream("/FunctionType 4 /Domain [0 1] /Range [0 1 0 1 0 1 0 1]", "{ dup 0.1 mul exch dup 0.9 mul exch dup 0.7 mul exch 0 mul }");
            res.Append($"/ColorSpace << /CS1 [/Separation /PANTONE#20123#20C /DeviceCMYK {fn} 0 R] >>");
        });
        var d = Drawn(r);
        var full = VectorTestKit.FillColor(d[0]);
        Assert.Equal("PANTONE 123 C", full.Name);
        Assert.Equal(ColorModel.Cmyk, full.Model);
        Assert.Equal(new Cmyk(10, 90, 70, 0), full.Cmyk);
        Assert.Null(VectorTestKit.FillColor(d[1]).Name);                    // a half tint is just a color
    }

    [Fact]
    public void Indexed_and_icc_based_colors()
    {
        var r = One("/CS1 cs 1 sc 0 0 10 10 re f  /CS2 cs 0 1 0 sc 20 0 10 10 re f", (pdf, res) =>
        {
            int icc = pdf.AddStream("/N 3 /Alternate /DeviceRGB", "xxxx");
            res.Append($"/ColorSpace << /CS1 [/Indexed /DeviceRGB 1 <ff0000 0000ff>] /CS2 [/ICCBased {icc} 0 R] >>");
        });
        var d = Drawn(r);
        Assert.Equal(new DocColor(0, 0, 255), VectorTestKit.FillColor(d[0]));
        Assert.Equal(new DocColor(0, 255, 0), VectorTestKit.FillColor(d[1]));
    }

    [Fact]
    public void Transparency_of_the_graphics_state_becomes_the_alpha_of_the_color()
    {
        var r = One("/G1 gs 1 0 0 rg 0 0 10 10 re f", (pdf, res) => res.Append("/ExtGState << /G1 << /ca 0.5 /CA 0.25 >> >>"));
        Assert.InRange((int)VectorTestKit.FillColor(Assert.Single(Drawn(r))).A, 127, 129);
    }

    // ---------------------------------------------------------------- paths

    [Fact]
    public void Strokes_have_their_width_cap_join_and_scale_with_the_matrix()
    {
        var r = One("2 w 1 J 2 j 0 0 1 RG 10 10 m 90 10 l S  q 2 0 0 2 0 0 cm 3 w 5 30 m 45 30 l S Q  0 w 5 80 m 50 80 l S", null);
        var d = Drawn(r);
        var s = d[0].Style.Stroke!;
        Assert.Equal((2.0, LineCap.Round, LineJoin.Bevel), (s.Width, s.Cap, s.Join));
        Assert.Equal(new DocColor(0, 0, 255), ((SolidPaint)s.Paint).Color);
        Assert.Null(d[0].Style.Fill);
        Assert.Equal(6.0, d[1].Style.Stroke!.Width, 6);                      // 3 under a 2x matrix
        Assert.True(d[2].Style.Stroke!.Width > 0);                          // a zero-width line is a hairline
    }

    [Fact]
    public void Curves_closing_and_the_even_odd_rule()
    {
        var r = One("0 g 10 10 m 50 10 l 50 50 l h 100 10 m 120 60 160 60 180 10 c f  0 0 1 rg 10 60 80 30 re 30 65 20 20 re f*", null);
        var d = Drawn(r);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(d[0]), 10, 50, 180, 90, 20);
        var ring = (PathObject)d[1];
        Assert.Equal(FillRule.EvenOdd, ring.Geometry.FillRule);
        Assert.False(ring.Geometry.Contains(new Vec2(40, 100 - 75)));
        Assert.True(ring.Geometry.Contains(new Vec2(15, 100 - 75)));
    }

    [Fact]
    public void The_v_and_y_curve_shorthands_and_nesting_of_q_and_cm()
    {
        var r = One("q 1 0 0 1 100 0 cm 0.5 g 0 0 m 20 0 20 20 v 40 40 40 0 y f Q 1 0 0 rg 0 0 10 10 re f", null);
        var d = Drawn(r);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(d[0]), 100, 100 - 30, 140, 100, 12);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(d[1]), 0, 90, 10, 100);
    }

    // ---------------------------------------------------------------- clipping

    private const string ClipContent = "20 0 40 100 re W n 1 0 0 rg 0 0 100 50 re f";

    [Fact]
    public void A_clipping_path_cuts_a_fill_with_the_boolean_service_and_is_reported_without_it()
    {
        var cut = One(ClipContent, null, options: new VectorImportOptions { BooleanService = WriterTestKit.Booleans });
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(Assert.Single(Drawn(cut))), 20, 50, 60, 100);

        var plain = One(ClipContent);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(Assert.Single(Drawn(plain))), 0, 50, 100, 100);
        Assert.Contains(plain.Warnings, w => w.Contains("clipping"));

        var inside = One("0 0 200 100 re W n 1 0 0 rg 10 10 20 20 re f");
        Assert.DoesNotContain(inside.Warnings, w => w.Contains("clipping"));
        var away = One("150 0 40 40 re W n 1 0 0 rg 10 10 20 20 re f");
        Assert.Empty(Drawn(away));
    }

    // ---------------------------------------------------------------- forms, groups, layers

    [Fact]
    public void A_form_is_a_group_and_a_form_with_one_object_is_not()
    {
        var r = One("0 g 0 0 5 5 re f /Fm1 Do /Fm2 Do 1 0 0 rg 90 0 5 5 re f", (pdf, res) =>
        {
            int f1 = pdf.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 100] /Matrix [1 0 0 1 50 10]", "0 1 0 rg 0 0 10 10 re f 0 0 1 rg 20 0 10 10 re f");
            int f2 = pdf.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 100]", "0 1 1 rg 150 50 10 10 re f");
            res.Append($"/XObject << /Fm1 {f1} 0 R /Fm2 {f2} 0 R >>");
        });
        var d = Drawn(r);
        Assert.Equal(5, d.Count);
        Assert.Equal(1, r.Document.GroupCount);
        Assert.Same(d[1].Parent, d[2].Parent);
        Assert.IsType<GroupObject>(d[1].Parent);
        Assert.IsAssignableFrom<Layer>(d[3].Parent);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(d[1]), 50, 80, 60, 90);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(d[2]), 70, 80, 80, 90);
        Assert.Equal(new DocColor(0, 255, 0), VectorTestKit.FillColor(d[1]));
        Assert.Equal(new DocColor(255, 0, 0), VectorTestKit.FillColor(d[4]));          // the order of the page is kept
    }

    [Fact]
    public void Optional_content_layers_become_layers_and_hidden_ones_are_left_out()
    {
        var pdf = new MiniPdf();
        int back = pdf.Add("<< /Type /OCG /Name (Background) >>");
        int art = pdf.Add("<< /Type /OCG /Name (Artwork) >>");
        int secret = pdf.Add("<< /Type /OCG /Name (Guides) >>");
        string content = "/OC /L1 BDC 0 0 1 rg 0 0 50 50 re f EMC /OC /L2 BDC 1 0 0 rg 10 10 20 20 re f 0 1 0 rg 40 10 20 20 re f EMC /OC /L3 BDC 0 g 0 0 200 100 re f EMC";
        int page = pdf.AddPage(content, $"/Properties << /L1 {back} 0 R /L2 {art} 0 R /L3 {secret} 0 R >>");
        var bytes = pdf.Build([page], $"/OCProperties << /OCGs [{back} 0 R {art} 0 R {secret} 0 R] /D << /OFF [{secret} 0 R] >> >>");
        var r = Read(bytes);
        Assert.Equal(["Background", "Artwork"], r.Document.Layers.Select(l => l.Name));
        Assert.Equal([1, 2], r.Document.Layers.Select(l => l.Children.Count));
    }

    // ---------------------------------------------------------------- shadings

    [Fact]
    public void An_axial_shading_painted_into_a_clip_is_a_linear_gradient_with_its_stops()
    {
        var r = One("20 25 100 50 re W n /Sh1 sh", (pdf, res) =>
        {
            int f0 = pdf.Add("<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [1 1 0] /N 1 >>");
            int f1 = pdf.Add("<< /FunctionType 2 /Domain [0 1] /C0 [1 1 0] /C1 [0 0 1] /N 1 >>");
            int fn = pdf.Add($"<< /FunctionType 3 /Domain [0 1] /Functions [{f0} 0 R {f1} 0 R] /Bounds [0.4] /Encode [0 1 0 1] >>");
            res.Append($"/Shading << /Sh1 << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [20 50 120 50] /Function {fn} 0 R /Extend [true true] >> >>");
        });
        var o = Assert.Single(Drawn(r));
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(o), 20, 25, 120, 75);
        var g = Assert.IsType<GradientPaint>(o.Style.Fill);
        Assert.Equal(GradientKind.Linear, g.Kind);
        Assert.Equal(3, g.Stops.Count);
        Assert.Equal([0, 0.4, 1], g.Stops.Select(s => Math.Round(s.Offset, 3)));
        Assert.Equal(new DocColor(255, 0, 0), g.Stops[0].Color);
        Assert.Equal(new DocColor(255, 255, 0), g.Stops[1].Color);
        Assert.Equal(new DocColor(0, 0, 255), g.Stops[2].Color);
        Assert.Equal([new Vec2(0, 0.5), new Vec2(1, 0.5)], g.Points.Select(p => new Vec2(Math.Round(p.X, 6), Math.Round(p.Y, 6))));
    }

    [Fact]
    public void A_radial_shading_pattern_fills_a_shape_as_a_circle_gradient()
    {
        var r = One("/Pattern cs /P1 scn 20 10 80 80 re f", (pdf, res) =>
        {
            int fn = pdf.Add("<< /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 0 0.5] /N 1 >>");
            int pat = pdf.Add($"<< /PatternType 2 /Shading << /ShadingType 3 /ColorSpace /DeviceRGB /Coords [60 50 0 60 50 40] /Function {fn} 0 R >> >>");
            res.Append($"/Pattern << /P1 {pat} 0 R >>");
        });
        var g = Assert.IsType<GradientPaint>(Assert.Single(Drawn(r)).Style.Fill);
        Assert.Equal(GradientKind.Circle, g.Kind);
        Assert.Equal(new Vec2(0.5, 0.5), g.Points[0]);
        Assert.Equal(1.0, g.Points[1].X, 4);
        Assert.Equal(new DocColor(255, 255, 255), g.Stops[0].Color);
        Assert.Equal(new DocColor(0, 0, 128), g.Stops[^1].Color);
    }

    [Fact]
    public void A_tiling_pattern_is_a_flat_color_with_a_warning()
    {
        var r = One("/Pattern cs /P1 scn 0 0 50 50 re f", (pdf, res) =>
        {
            int pat = pdf.AddStream("/PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 5 5] /XStep 5 /YStep 5 /Resources << >>", "0 g 0 0 2 2 re f");
            res.Append($"/Pattern << /P1 {pat} 0 R >>");
        });
        Assert.Single(Drawn(r));
        Assert.Contains(r.Warnings, w => w.Contains("tiling"));
    }

    // ---------------------------------------------------------------- pictures

    [Fact]
    public void A_raw_rgb_picture_becomes_a_png_image_object_at_its_place()
    {
        byte[] pixels = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0];   // 2 x 2: red green / blue yellow
        var r = One("q 80 0 0 40 10 20 cm /Im1 Do Q", (pdf, res) =>
        {
            int im = pdf.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8", pixels, flate: true);
            res.Append($"/XObject << /Im1 {im} 0 R >>");
        });
        var img = Assert.IsType<ImageObject>(Assert.Single(Drawn(r)));
        VectorTestKit.AssertBounds(img.GetWorldBounds(), 10, 40, 90, 80);
        Assert.Equal((2, 2), (img.PixelWidth, img.PixelHeight));
        var asset = r.Document.Assets.Get(img.AssetId)!;
        Assert.Equal("image/png", asset.MediaType);
        using var bmp = SKBitmap.Decode(asset.Data);
        Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(0, 0));          // the first row is the top row
        Assert.Equal(new SKColor(0, 255, 0), bmp.GetPixel(1, 0));
        Assert.Equal(new SKColor(0, 0, 255), bmp.GetPixel(0, 1));
        Assert.Equal(new SKColor(255, 255, 0), bmp.GetPixel(1, 1));
    }

    [Fact]
    public void A_jpeg_picture_is_stored_as_it_is_and_a_soft_mask_gives_alpha()
    {
        var jpeg = WriterTestKit.Jpeg(20, 10);
        byte[] alpha = [0, 255, 255, 0];
        var r = One("q 40 0 0 20 0 0 cm /Im1 Do Q q 40 0 0 20 100 0 cm /Im2 Do Q", (pdf, res) =>
        {
            int jp = pdf.AddStream("/Type /XObject /Subtype /Image /Width 20 /Height 10 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg);
            int mask = pdf.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8", alpha);
            int im = pdf.AddStream($"/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask {mask} 0 R", new byte[12].Select(_ => (byte)200).ToArray());
            res.Append($"/XObject << /Im1 {jp} 0 R /Im2 {im} 0 R >>");
        });
        var d = Drawn(r).OfType<ImageObject>().ToList();
        Assert.Equal(2, d.Count);
        Assert.Equal(jpeg, r.Document.Assets.Get(d[0].AssetId)!.Data);
        Assert.Equal("image/jpeg", r.Document.Assets.Get(d[0].AssetId)!.MediaType);
        using var bmp = SKBitmap.Decode(r.Document.Assets.Get(d[1].AssetId)!.Data);
        Assert.Equal(0, bmp.GetPixel(0, 0).Alpha);
        Assert.Equal(255, bmp.GetPixel(1, 0).Alpha);
    }

    [Fact]
    public void A_stencil_mask_is_painted_in_the_fill_color_and_inline_pictures_work()
    {
        var r = One("0 0 1 rg q 20 0 0 20 0 0 cm /Im1 Do Q q 20 0 0 20 50 0 cm BI /W 2 /H 1 /CS /RGB /BPC 8 ID \xff\x00\x00\x00\xff\x00 EI Q", (pdf, res) =>
        {
            // 8 x 1 stencil: bits 10101010 (1 = not painted)
            int im = pdf.AddStream("/Type /XObject /Subtype /Image /Width 8 /Height 1 /ImageMask true /BitsPerComponent 1", new byte[] { 0b10101010 });
            res.Append($"/XObject << /Im1 {im} 0 R >>");
        });
        var d = Drawn(r).OfType<ImageObject>().ToList();
        Assert.Equal(2, d.Count);
        using var stencil = SKBitmap.Decode(r.Document.Assets.Get(d[0].AssetId)!.Data);
        Assert.Equal(0, stencil.GetPixel(0, 0).Alpha);
        Assert.Equal(new SKColor(0, 0, 255, 255), stencil.GetPixel(1, 0));
        using var inline = SKBitmap.Decode(r.Document.Assets.Get(d[1].AssetId)!.Data);
        Assert.Equal(new SKColor(255, 0, 0), inline.GetPixel(0, 0));
        Assert.Equal(new SKColor(0, 255, 0), inline.GetPixel(1, 0));
    }

    // ---------------------------------------------------------------- text

    [Fact]
    public void Text_becomes_live_text_in_its_place_in_the_drawing_order()
    {
        var r = One("1 0 0 rg 0 0 200 100 re f BT /F1 20 Tf 0 0 1 rg 10 30 Td (Hello) Tj ET 0 1 0 rg 150 10 30 30 re f", (pdf, res) =>
        {
            int font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
            res.Append($"/Font << /F1 {font} 0 R >>");
        });
        var d = Drawn(r);
        Assert.Equal(3, d.Count);
        Assert.IsType<PathObject>(d[0]);
        var t = Assert.IsType<TextObject>(d[1]);
        Assert.IsType<PathObject>(d[2]);
        Assert.Equal("Hello", t.Text);
        Assert.Equal(20.0, t.FontSize, 3);
        Assert.Equal(TextEngine.Fonts.IsInstalled("Arial") ? "Arial" : t.FontFamily, t.FontFamily);
        Assert.Equal(new DocColor(0, 0, 255), VectorTestKit.FillColor(t));
        Assert.Equal(10.0, t.Transform.OffsetX, 3);
        Assert.Equal(70.0, t.Transform.OffsetY, 3);                         // the baseline: 100 - 30
        output.WriteLine(string.Join(", ", r.Warnings));
    }

    [Fact]
    public void Text_in_forms_keeps_its_order_and_text_can_be_switched_to_outlines()
    {
        string content = "BT /F1 12 Tf 10 80 Td (One) Tj ET /Fm1 Do BT /F1 12 Tf 10 20 Td (Three) Tj ET";
        Action<MiniPdf, StringBuilder> res = (pdf, sb) =>
        {
            int font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
            int f1 = pdf.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 200 100] /Resources << /Font << /F1 {font} 0 R >> >>", "BT /F1 12 Tf 10 50 Td (Two) Tj ET");
            sb.Append($"/Font << /F1 {font} 0 R >> /XObject << /Fm1 {f1} 0 R >>");
        };
        var live = One(content, res);
        Assert.Equal(["One", "Two", "Three"], Drawn(live).OfType<TextObject>().Select(t => t.Text));

        // a font that is not in the file (the standard fonts) has no outlines to read: the text stays text, in a replacement font, and the file says so
        var outlined = One(content, res, options: new VectorImportOptions { LiveText = false });
        Assert.Equal(3, Drawn(outlined).Count);
        Assert.Contains(outlined.Warnings, w => w.Contains("font"));
    }

    // ---------------------------------------------------------------- Illustrator files and errors

    [Fact]
    public void A_pdf_compatible_ai_file_is_read_through_the_pdf_reader()
    {
        var bytes = MiniPdf.OnePage("0 0 1 rg 5 5 20 20 re f");
        var r = Read(bytes, format: VectorFormat.Ai);
        Assert.Equal(VectorFormat.Ai, r.Format);
        Assert.Single(Drawn(r));

        string path = Path.Combine(Path.GetTempPath(), $"designmage-{Guid.NewGuid():N}.ai");
        File.WriteAllBytes(path, bytes);
        try { Assert.Equal(VectorFormat.Ai, VectorImport.Read(path).Format); }
        finally { File.Delete(path); }

        var empty = Read(MiniPdf.OnePage(""), format: VectorFormat.Ai);
        Assert.Contains(empty.Warnings, w => w.Contains("PDF"));
    }

    [Fact]
    public void Files_that_are_not_readable_are_refused_with_a_message()
    {
        var notPdf = Assert.Throws<VectorImportException>(() => Read(Encoding.ASCII.GetBytes("hello world"), format: VectorFormat.Pdf));
        Assert.Contains("PDF", notPdf.Message);
        var truncated = MiniPdf.OnePage("0 g 0 0 10 10 re f");
        var cut = Assert.Throws<VectorImportException>(() => Read(truncated[..(truncated.Length / 3)]));
        Assert.Contains("PDF", cut.Message);
    }
    // ---------------------------------------------------------------- our own exporter

    [Fact]
    public void A_pdf_our_own_exporter_wrote_comes_back_with_the_same_shapes_colors_and_order()
    {
        var doc = WriterTestKit.NewDoc(6, 4);
        var a = WriterTestKit.Place(doc, new RectangleObject(100, 60, 6), 20, 30, WriterTestKit.Red, "Box");
        var b = WriterTestKit.Place(doc, new EllipseObject(80, 80), 160, 20, WriterTestKit.Blue, "Dot");
        b.Style = new ObjectStyle(new SolidPaint(WriterTestKit.Blue), StrokeStyle.Solid(WriterTestKit.Green, 6));
        var star = new PathBuilder();
        for (int i = 0; i < 10; i++)
        {
            double rr = i % 2 == 0 ? 50 : 20, ang = -Math.PI / 2 + i * Math.PI / 5;
            var p = new Vec2(rr * Math.Cos(ang), rr * Math.Sin(ang));
            if (i == 0) star.MoveTo(p); else star.LineTo(p);
        }
        var s = new PathObject(star.Close().Build()) { Transform = Affine2.Translation(300, 200), Style = ObjectStyle.FillOnly(WriterTestKit.Green) };
        doc.Insert(doc.ActiveLayer, -1, s);
        var grad = new RectangleObject(120, 40) { Transform = Affine2.Translation(30, 200) };
        grad.Style = new ObjectStyle(new GradientPaint(GradientKind.Linear, GradientPaint.DefaultPoints(GradientKind.Linear), [new GradientNode(0, WriterTestKit.Red), new GradientNode(1, WriterTestKit.Blue)]), null);
        doc.Insert(doc.ActiveLayer, -1, grad);

        var bytes = Rendering(doc);
        var back = Read(bytes);
        var drawn = Drawn(back);
        output.WriteLine($"{drawn.Count} objects: {string.Join(", ", drawn.Select(o => o.KindName))}; warnings: {string.Join(" | ", back.Warnings)}");
        Assert.Equal(doc.Page.Width, back.Document.Page.Width, 2);
        Assert.Equal(doc.Page.Height, back.Document.Page.Height, 2);
        Assert.True(drawn.Count >= 4);

        // every original shows up as an object with the same bounds (the stroke of the dot makes its bounds a little larger)
        foreach (var orig in new DocObject[] { a, b, s, grad })
        {
            var ob = orig.GetWorldBounds();
            double outset = orig.Style.StrokeOutset + 0.6;
            Assert.Contains(drawn, o =>
            {
                var bb = o.GetWorldBounds();
                return Math.Abs(bb.Left - ob.Left) <= outset && Math.Abs(bb.Right - ob.Right) <= outset && Math.Abs(bb.Top - ob.Top) <= outset && Math.Abs(bb.Bottom - ob.Bottom) <= outset;
            });
        }
        // drawing order: the box first, the gradient last
        int iBox = drawn.FindIndex(o => Math.Abs(o.GetWorldBounds().Left - 20) < 1 && Math.Abs(o.GetWorldBounds().Top - 30) < 1);
        int iGrad = drawn.FindIndex(o => o.Style.Fill is GradientPaint);
        Assert.True(iBox >= 0 && iGrad > iBox, $"box {iBox}, gradient {iGrad}");
        Assert.Contains(drawn, o => o.Style.Fill is SolidPaint sp && sp.Color is { R: 200, G: 30, B: 40 });
    }

    private static byte[] Rendering(Document doc) =>
        DeSignMage.Rendering.Export.DocumentExporter.Render(doc, DeSignMage.Rendering.Export.ExportFormat.Pdf, new DeSignMage.Rendering.Export.ExportOptions());
}
