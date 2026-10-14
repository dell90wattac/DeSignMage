using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Vector;
using DeSignMage.Rendering.Export;
using Xunit.Abstractions;

namespace DeSignMage.Formats.Tests;

/// <summary>Reading EPS files and the PostScript-based Illustrator files: small hand-written files plus files our own exporter wrote.</summary>
public class VectorEpsTests(ITestOutputHelper output)
{
    private const string Head = "%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 200 100\n%%EndComments\n";
    private const string AiHead = "%!PS-Adobe-3.0\n%%Creator: Adobe Illustrator(R) 8.0\n%%BoundingBox: 0 0 200 100\n%%EndComments\n%%EndProlog\n%%BeginSetup\n%%EndSetup\n";

    private static VectorImportResult Read(string text, VectorFormat format = VectorFormat.Eps, VectorImportOptions? options = null) =>
        VectorImport.Read(new MemoryStream(Encoding.Latin1.GetBytes(text)), format, options);

    private static VectorImportResult Read(byte[] bytes, VectorFormat format = VectorFormat.Eps, VectorImportOptions? options = null) =>
        VectorImport.Read(new MemoryStream(bytes), format, options);

    private static List<DocObject> Drawn(VectorImportResult r) => VectorTestKit.Drawn(r);

    // ---------------------------------------------------------------- PostScript

    [Fact]
    public void The_page_is_the_bounding_box_and_y_runs_down()
    {
        var r = Read(Head + "1 0 0 setrgbcolor 10 10 moveto 110 10 lineto 110 60 lineto 10 60 lineto closepath fill");
        Assert.Equal(VectorFormat.Eps, r.Format);
        Assert.Equal(200, r.Document.Page.Width, 6);
        Assert.Equal(100, r.Document.Page.Height, 6);
        var o = Assert.Single(Drawn(r));
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(o), 10, 40, 110, 90);
        Assert.Equal(new DocColor(255, 0, 0), VectorTestKit.FillColor(o));
    }

    [Fact]
    public void A_bounding_box_that_does_not_start_at_the_origin_moves_the_drawing()
    {
        var r = Read("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 100 200 300 300\n0 setgray 150 220 20 30 rectfill");
        Assert.Equal(200, r.Document.Page.Width, 6);
        var o = Assert.Single(Drawn(r));
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(o), 50, 50, 70, 80);
    }

    [Fact]
    public void Gray_rgb_and_cmyk_colors_keep_their_model()
    {
        var r = Read(Head + "0.5 setgray 0 0 10 10 rectfill 0 1 0 setrgbcolor 20 0 10 10 rectfill 0.1 0.2 0.3 0.4 setcmykcolor 40 0 10 10 rectfill");
        var d = Drawn(r);
        Assert.Equal(3, d.Count);
        Assert.InRange(VectorTestKit.FillColor(d[0]).R, 127, 128);
        Assert.Equal(new DocColor(0, 255, 0), VectorTestKit.FillColor(d[1]));
        var cmyk = VectorTestKit.FillColor(d[2]);
        Assert.Equal(DocColor.FromCmyk(10, 20, 30, 40).R, cmyk.R);
        Assert.NotNull(cmyk.Cmyk);
    }

    [Fact]
    public void Strokes_have_their_width_and_the_matrix_scales_and_moves_the_drawing()
    {
        var r = Read(Head + "gsave 50 20 translate 2 2 scale 3 setlinewidth 1 setlinecap 1 setlinejoin 0 0 0 setrgbcolor 0 0 moveto 20 0 lineto stroke grestore " +
                           "0 0 1 setrgbcolor 150 50 moveto 190 50 lineto stroke");
        var d = Drawn(r);
        Assert.Equal(2, d.Count);
        Assert.Equal(6, d[0].Style.Stroke!.Width, 3);
        Assert.Equal(1, d[1].Style.Stroke!.Width, 3);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(d[0]), 50 - 3, 80, 90 + 3, 80, 3.001);
        Assert.Equal(LineCap.Round, d[0].Style.Stroke!.Cap);
        Assert.Equal(LineJoin.Round, d[0].Style.Stroke!.Join);
    }

    [Fact]
    public void Procedures_loops_and_the_operand_stack_work()
    {
        var r = Read(Head + "/box { /h exch def /w exch def /y exch def /x exch def x y moveto x w add y lineto x w add y h add lineto x y h add lineto closepath fill } def " +
                           "0 setgray 0 20 80 { /i exch def i 10 20 10 box } for 3 { 5 5 10 10 box } repeat " +
                           "10 0 gt { 1 0 0 setrgbcolor 150 10 20 20 box } if");
        var d = Drawn(r);
        Assert.Equal(5 + 3 + 1, d.Count);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(d[0]), 0, 80, 20, 90);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(d[4]), 80, 80, 100, 90);
        Assert.Equal(new DocColor(255, 0, 0), VectorTestKit.FillColor(d[^1]));
    }

    [Fact]
    public void Curves_arcs_and_the_even_odd_fill()
    {
        var r = Read(Head + "0 0 0 setrgbcolor 10 10 moveto 30 50 70 50 90 10 curveto fill 150 50 30 0 360 arc eofill");
        var d = Drawn(r);
        Assert.Equal(2, d.Count);
        var circle = VectorTestKit.Bounds(d[1]);
        VectorTestKit.AssertBounds(circle, 120, 20, 180, 80, 0.5);
        Assert.IsType<PathObject>(d[0]);
    }

    [Fact]
    public void A_clipping_path_cuts_the_drawing_with_the_boolean_service()
    {
        var r = Read(Head + "gsave 0 0 50 50 rectclip 1 0 0 setrgbcolor 25 25 100 100 rectfill grestore", options: new VectorImportOptions { BooleanService = WriterTestKit.Booleans });
        var o = Assert.Single(Drawn(r));
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(o), 25, 50, 50, 75, 0.5);
        var without = Read(Head + "gsave 0 0 50 50 rectclip 1 0 0 setrgbcolor 25 25 100 100 rectfill grestore");
        Assert.Contains(without.Warnings, w => w.Contains("clip", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_axial_shading_filled_with_shfill_is_a_linear_gradient()
    {
        var r = Read(Head + "gsave 0 0 100 100 rectclip << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> /Extend [true true] >> shfill grestore",
            options: new VectorImportOptions { BooleanService = WriterTestKit.Booleans });
        var o = Assert.Single(Drawn(r));
        var g = Assert.IsType<GradientPaint>(o.Style.Fill);
        Assert.Equal(GradientKind.Linear, g.Kind);
        Assert.Equal(new DocColor(255, 0, 0), g.Stops[0].Color);
        Assert.Equal(new DocColor(0, 0, 255), g.Stops[^1].Color);
    }

    [Fact]
    public void A_picture_drawn_with_image_becomes_an_image_object_at_its_place()
    {
        var r = Read(Head + "gsave 20 10 translate 40 40 scale 2 2 8 [2 0 0 2 0 0] {<00 80 c0 ff>} image grestore");
        var o = Assert.Single(Drawn(r));
        var img = Assert.IsType<ImageObject>(o);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(img), 20, 50, 60, 90, 0.5);
    }

    [Fact]
    public void Plain_show_text_becomes_live_text()
    {
        var r = Read(Head + "/Helvetica findfont 20 scalefont setfont 10 40 moveto (Hello) show");
        var t = Assert.Single(Drawn(r).OfType<TextObject>());
        Assert.Contains("Hello", t.Text);
    }

    [Fact]
    public void A_file_that_draws_nothing_it_can_read_says_that_it_needs_a_postscript_interpreter()
    {
        var r = Read(Head + "(hello) pop");
        Assert.Empty(Drawn(r));
        Assert.Contains(r.Warnings, w => w.Contains("PostScript"));
    }

    [Fact]
    public void A_file_with_a_binary_header_is_read_and_its_preview_picture_is_named_in_the_warning()
    {
        string ps = Head + "0 0 1 setrgbcolor 10 10 50 50 rectfill\n";
        var drawing = Encoding.Latin1.GetBytes(ps);
        var tiff = new byte[64];
        var bytes = DosEps(drawing, tiff);
        var r = Read(bytes);
        var o = Assert.Single(Drawn(r));
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(o), 10, 40, 60, 90);

        var empty = DosEps(Encoding.Latin1.GetBytes(Head + "(x) pop\n"), tiff);
        var r2 = Read(empty);
        Assert.Contains(r2.Warnings, w => w.Contains("TIFF"));
    }

    [Fact]
    public void A_pdf_inside_the_eps_is_read_as_a_pdf()
    {
        var pdf = MiniPdf.OnePage("1 0 0 rg 10 10 30 30 re f");
        var bytes = Encoding.Latin1.GetBytes(Head).Concat(pdf).ToArray();
        var r = Read(bytes);
        Assert.Equal(VectorFormat.Eps, r.Format);
        var o = Assert.Single(Drawn(r));
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(o), 10, 60, 40, 90);
    }

    // ---------------------------------------------------------------- Illustrator operators

    private const string TwoLayers =
        "%AI5_BeginLayer\n1 1 1 1 0 0 0 79 128 255 Lb\n(Back) Ln\n" +
        "0 1 1 0 k\n10 10 m\n110 10 L\n110 60 L\n10 60 L\nf\n" +
        "LB\n%AI5_EndLayer--\n" +
        "%AI5_BeginLayer\n1 1 1 1 0 0 0 79 128 255 Lb\n(Front) Ln\n" +
        "u\n0 0 0 1 k\n120 10 m\n140 10 L\n140 30 L\nf\n1 0 0 0 k\n150 10 m\n170 10 L\n170 30 L\nf\nU\n" +
        "LB\n%AI5_EndLayer--\n";

    [Fact]
    public void Illustrator_operators_give_layers_groups_and_cmyk_colors()
    {
        var r = Read(AiHead + TwoLayers, VectorFormat.Ai);
        Assert.Equal(VectorFormat.Ai, r.Format);
        Assert.Equal(2, r.Document.Layers.Count);
        Assert.Equal("Back", r.Document.Layers[0].Name);
        Assert.Equal("Front", r.Document.Layers[1].Name);
        Assert.Equal(1, r.Document.GroupCount);
        var back = Assert.Single(r.Document.Layers[0].Children);
        VectorTestKit.AssertBounds(VectorTestKit.Bounds(back), 10, 40, 110, 90);
        Assert.NotNull(VectorTestKit.FillColor(back).Cmyk);
        var all = Drawn(r);
        Assert.Equal(3, all.Count);
        Assert.Same(all[1].Parent, all[2].Parent);
        Assert.NotSame(all[0].Parent, all[1].Parent);
    }

    [Fact]
    public void A_hidden_layer_is_left_out()
    {
        string text = AiHead + "%AI5_BeginLayer\n0 1 1 1 0 0 0 79 128 255 Lb\n(Off) Ln\n0 0 0 1 k\n0 0 m\n10 0 L\n10 10 L\nf\nLB\n%AI5_EndLayer--\n" +
                      "%AI5_BeginLayer\n1 1 1 1 0 0 0 79 128 255 Lb\n(On) Ln\n0 0 0 1 k\n20 0 m\n30 0 L\n30 10 L\nf\nLB\n%AI5_EndLayer--\n";
        var r = Read(text, VectorFormat.Ai);
        var o = Assert.Single(Drawn(r));
        Assert.True(VectorTestKit.Bounds(o).Left > 15);
    }

    [Fact]
    public void A_compound_path_is_one_object_and_xr_chooses_the_even_odd_rule()
    {
        string text = AiHead + "%AI5_BeginLayer\n1 1 1 1 0 0 0 79 128 255 Lb\n(L) Ln\n0 0 0 1 k\n1 XR\n*u\n" +
                      "10 10 m\n90 10 L\n90 90 L\n10 90 L\nf\n30 30 m\n70 30 L\n70 70 L\n30 70 L\nf\n*U\nLB\n%AI5_EndLayer--\n";
        var r = Read(text, VectorFormat.Ai);
        var o = Assert.IsType<PathObject>(Assert.Single(Drawn(r)));
        Assert.Equal(FillRule.EvenOdd, o.Geometry.FillRule);
        Assert.False(o.Geometry.Contains(new Vec2(50, 100 - 50)));
        Assert.True(o.Geometry.Contains(new Vec2(20, 100 - 50)));
    }

    [Fact]
    public void A_custom_color_keeps_its_name_at_full_strength()
    {
        string text = AiHead + "%AI5_BeginLayer\n1 1 1 1 0 0 0 79 128 255 Lb\n(L) Ln\n0 0.5 1 0 (PMS 123) 0 x\n10 10 m\n50 10 L\n50 50 L\nf\n" +
                      "0 0.5 1 0 (PMS 123) 0.5 x\n60 10 m\n90 10 L\n90 50 L\nf\nLB\n%AI5_EndLayer--\n";
        var d = Drawn(Read(text, VectorFormat.Ai));
        Assert.Equal(2, d.Count);
        Assert.Equal("PMS 123", VectorTestKit.FillColor(d[0]).Name);
        Assert.Null(VectorTestKit.FillColor(d[1]).Name);
    }

    [Fact]
    public void An_illustrator_gradient_fill_is_drawn_in_its_average_color_with_a_warning()
    {
        string text = AiHead + "%AI5_BeginGradient: (Sky)\n(Sky) 0 2 Bd\n[\n0 1\n1 1 1 0 0 1 0 0 0 Bs\n]\nBD\n%AI5_EndGradient\n" +
                      "%AI5_BeginLayer\n1 1 1 1 0 0 0 79 128 255 Lb\n(L) Ln\nBb\n0 0 0 0 Bh\n2 (Sky) 0 0 0 1 1 0 0 1 0 0 1 Bg\n10 10 m\n90 10 L\n90 50 L\nf\n0 BB\nLB\n%AI5_EndLayer--\n";
        var r = Read(text, VectorFormat.Ai);
        Assert.Single(Drawn(r));
        Assert.Contains(r.Warnings, w => w.Contains("gradient", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Illustrator_text_objects_are_reported_not_drawn()
    {
        string text = AiHead + "%AI5_BeginLayer\n1 1 1 1 0 0 0 79 128 255 Lb\n(L) Ln\n0 0 0 1 k\n0 To\n1 0 0 1 10 10 0 Tp\nTP\n0 Tr\n0 O\n(Hi) Tx\nTO\nLB\n%AI5_EndLayer--\n";
        var r = Read(text, VectorFormat.Ai);
        Assert.Contains(r.Warnings, w => w.Contains("text", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- Illustrator private data

    [Fact]
    public void The_document_data_of_an_illustrator_eps_is_read_instead_of_its_postscript()
    {
        string script = "%!PS-Adobe-3.0\n%%BoundingBox: 0 0 200 100\n%AI3_Cropmarks: 0 0 200 100\n" + TwoLayers;
        string eps = Head + "%AI9_PrivateDataBegin\n%AI9_DataStream\n" + Ascii85Lines(Deflate(Encoding.Latin1.GetBytes(script))) + "%AI9_PrivateDataEnd\n" +
                     "%%EndProlog\n% a drawing that needs the Illustrator prolog\n";
        var r = Read(eps, VectorFormat.Eps);
        Assert.Equal(2, r.Document.Layers.Count);
        Assert.Equal(3, Drawn(r).Count);
        Assert.Equal(1, r.Document.GroupCount);
    }

    [Fact]
    public void An_illustrator_pdf_with_an_empty_page_is_read_from_its_private_data()
    {
        string script = "%!PS-Adobe-3.0\n%%BoundingBox: 0 0 200 100\n%AI3_Cropmarks: 0 0 200 100\n" + TwoLayers;
        var pdf = new MiniPdf();
        int b1 = pdf.AddStream("", "%!PS-Adobe-3.0\n%%BoundingBox: 0 0 200 100\n%AI3_Cropmarks: 0 0 200 100\n");
        int b2 = pdf.AddStream("", [.. Encoding.ASCII.GetBytes("%AI12_CompressedData"), .. Deflate(Encoding.Latin1.GetBytes(script[(script.IndexOf("%AI5_BeginLayer", StringComparison.Ordinal))..]))]);
        int priv = pdf.Add($"<< /NumBlock 2 /AIPrivateData1 {b1} 0 R /AIPrivateData2 {b2} 0 R >>");
        int page = pdf.AddPage("", mediaBox: "[0 0 200 100]", extra: $"/PieceInfo << /Illustrator << /Private {priv} 0 R >> >>");
        var r = Read(pdf.Build([page]), VectorFormat.Ai);
        Assert.Equal(VectorFormat.Ai, r.Format);
        Assert.Equal(3, Drawn(r).Count);
        Assert.Equal(2, r.Document.Layers.Count);
        Assert.Contains(r.Warnings, w => w.Contains("empty"));
    }

    // ---------------------------------------------------------------- detection

    [Fact]
    public void Eps_and_postscript_illustrator_files_are_told_apart_from_their_start()
    {
        Assert.Equal(VectorFormat.Eps, VectorImport.Detect("%!PS-Adobe-3.0 EPSF-3.0\n"u8, ".eps"));
        Assert.Equal(VectorFormat.Ai, VectorImport.Detect("%!PS-Adobe-3.0\n%%Creator: Adobe Illustrator"u8, ".ai"));
        Assert.Equal(VectorFormat.Eps, VectorImport.Detect([0xC5, 0xD0, 0xD3, 0xC6, 0, 0, 0, 0], ".eps"));
        Assert.True(VectorImport.CanRead("x.EPS"));
    }

    // ---------------------------------------------------------------- our own exporter

    [Fact]
    public void An_eps_our_own_exporter_wrote_comes_back_with_the_same_shapes_and_colors()
    {
        var doc = WriterTestKit.NewDoc(6, 4);
        var a = WriterTestKit.Place(doc, new RectangleObject(100, 60, 6), 20, 30, WriterTestKit.Red, "Box");
        var b = WriterTestKit.Place(doc, new EllipseObject(80, 80), 160, 20, WriterTestKit.Blue, "Dot");
        b.Style = new ObjectStyle(new SolidPaint(WriterTestKit.Blue), StrokeStyle.Solid(WriterTestKit.Green, 6));
        var tri = new PathObject(new PathBuilder().MoveTo(0, 0).LineTo(60, 0).CubicTo(80, 20, 80, 40, 30, 70).Close().Build()) { Transform = Affine2.Rotation(0.3) * Affine2.Translation(300, 100) };
        tri.Style = ObjectStyle.FillOnly(WriterTestKit.Green);
        doc.Insert(doc.ActiveLayer, -1, tri);

        var bytes = DocumentExporter.Render(doc, ExportFormat.Eps, new ExportOptions());
        output.WriteLine(Encoding.Latin1.GetString(bytes)[..Math.Min(700, bytes.Length)]);
        var back = Read(bytes);
        var drawn = Drawn(back);
        output.WriteLine($"{drawn.Count} objects; warnings: {string.Join(" | ", back.Warnings)}");
        Assert.Equal(doc.Page.Width, back.Document.Page.Width, 0);
        Assert.Equal(doc.Page.Height, back.Document.Page.Height, 0);
        Assert.True(drawn.Count >= 3);
        foreach (var orig in new DocObject[] { a, b, tri })
        {
            var ob = orig.GetWorldBounds();
            double outset = orig.Style.StrokeOutset + 1.5;
            Assert.Contains(drawn, o =>
            {
                var bb = o.GetWorldBounds();
                return Math.Abs(bb.Left - ob.Left) <= outset && Math.Abs(bb.Right - ob.Right) <= outset && Math.Abs(bb.Top - ob.Top) <= outset && Math.Abs(bb.Bottom - ob.Bottom) <= outset;
            });
        }
        Assert.Contains(drawn, o => o.Style.Fill is SolidPaint sp && sp.Color is { R: 200, G: 30, B: 40 });
    }

    [Fact]
    public void An_ai_file_our_own_exporter_wrote_is_read_as_ai()
    {
        var doc = WriterTestKit.NewDoc(4, 2);
        WriterTestKit.Place(doc, new RectangleObject(100, 60, 6), 20, 30, WriterTestKit.Red, "Box");
        WriterTestKit.Place(doc, new EllipseObject(80, 80), 160, 20, WriterTestKit.Blue, "Dot");
        var bytes = DocumentExporter.Render(doc, ExportFormat.Ai, new ExportOptions());
        Assert.Equal(VectorFormat.Ai, VectorImport.Detect(bytes, ".ai"));
        var back = Read(bytes, VectorFormat.Ai);
        Assert.Equal(VectorFormat.Ai, back.Format);
        Assert.Equal(2, Drawn(back).Count);
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>ASCII85 text in comment lines of 64 characters, ending with the terminator.</summary>
    private static string Ascii85Lines(byte[] data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 4)
        {
            int n = Math.Min(4, data.Length - i);
            uint v = 0;
            for (int k = 0; k < 4; k++) v = (v << 8) | (k < n ? data[i + k] : (byte)0);
            if (n == 4 && v == 0) { sb.Append('z'); continue; }
            var c = new char[5];
            for (int k = 4; k >= 0; k--) { c[k] = (char)(v % 85 + 33); v /= 85; }
            sb.Append(c, 0, n + 1);
        }
        sb.Append("~>");
        var all = sb.ToString();
        var lines = new StringBuilder();
        for (int i = 0; i < all.Length; i += 64) lines.Append('%').Append(all, i, Math.Min(64, all.Length - i)).Append('\n');
        return lines.ToString();
    }

    private static byte[] DosEps(byte[] ps, byte[] tiff)
    {
        var header = new byte[30];
        header[0] = 0xC5; header[1] = 0xD0; header[2] = 0xD3; header[3] = 0xC6;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 30);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)ps.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)(30 + ps.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)tiff.Length);
        header[28] = 0xFF; header[29] = 0xFF;
        return [.. header, .. ps, .. tiff];
    }
}
