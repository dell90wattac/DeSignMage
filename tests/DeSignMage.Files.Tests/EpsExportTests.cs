using System.Text;
using System.Text.RegularExpressions;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Rendering.Export;
using SkiaSharp;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>EPS export: the file's structure, what it draws (looked at with <see cref="MiniPostScript"/>), and the pictures that stand in for effects.</summary>
public class EpsExportTests
{
    private static Document NewDoc(double inchesW = 3, double inchesH = 2) => new(PageSetup.FromInches(inchesW, inchesH));

    private static T Add<T>(Document doc, T o) where T : DocObject
    {
        doc.Insert(doc.Layers[0], -1, o);
        return o;
    }

    private static ObjectStyle Fill(DocColor c, double? stroke = null) =>
        new(new SolidPaint(c), stroke is { } w ? StrokeStyle.Solid(DocColor.Black, w) : null);

    private static string Eps(Document doc, ExportOptions? o = null) => EpsExporter.Render(doc, o ?? new ExportOptions());

    private static SKBitmap Draw(string eps, double scale = 1, MiniPostScript? interpreter = null)
    {
        interpreter ??= new MiniPostScript();
        var bmp = interpreter.Render(eps, scale);
        Assert.Empty(interpreter.Unknown);
        return bmp;
    }

    private static SKBitmap Png(Document doc, double scale, ExportOptions? o = null)
    {
        var plan = ExportPlan.Create(doc, o ?? new ExportOptions());
        var options = (o ?? new ExportOptions()) with { PixelWidth = (int)Math.Ceiling(plan.Area.Width * scale), PixelHeight = (int)Math.Ceiling(plan.Area.Height * scale) };
        return SKBitmap.Decode(DocumentExporter.RenderPng(doc, options))!;
    }

    /// <summary>Mean absolute difference per channel (0..255) of two pictures of one size, both composited on white.</summary>
    private static double Difference(SKBitmap a, SKBitmap b, out double worstFraction)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        double sum = 0;
        long bad = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                var (ar, ag, ab) = OnWhite(a.GetPixel(x, y));
                var (br, bg, bb) = OnWhite(b.GetPixel(x, y));
                int d = Math.Abs(ar - br) + Math.Abs(ag - bg) + Math.Abs(ab - bb);
                sum += d / 3.0;
                if (d / 3.0 > 40) bad++;
            }
        worstFraction = bad / (double)(a.Width * a.Height);
        return sum / (a.Width * a.Height);
    }

    private static (int R, int G, int B) OnWhite(SKColor c) => (Blend(c.Red, c.Alpha), Blend(c.Green, c.Alpha), Blend(c.Blue, c.Alpha));
    private static int Blend(byte v, byte a) => (v * a + 255 * (255 - a)) / 255;

    // ---------------------------------------------------------------- structure

    [Fact]
    public void An_eps_file_has_the_dsc_header_the_bounding_box_and_stays_clean_ascii()
    {
        var doc = NewDoc();
        Add(doc, new RectangleObject(72, 36) { Transform = Affine2.Translation(36, 36), Style = Fill(new DocColor(255, 0, 0)) });

        var eps = Eps(doc, new ExportOptions { Title = "My sign — ünïcode" });

        var lines = eps.Split('\n');
        Assert.Equal("%!PS-Adobe-3.0 EPSF-3.0", lines[0]);
        Assert.Contains("%%BoundingBox: 0 0 216 144", lines);
        Assert.Contains("%%HiResBoundingBox: 0 0 216 144", lines);
        Assert.Contains("%%LanguageLevel: 3", lines);
        Assert.Contains("%%Title: My sign ? ?n?code", lines);
        Assert.Contains("%%EndComments", lines);
        Assert.Equal("%%EOF", lines[^2]);
        Assert.All(eps, c => Assert.True(c < 128));
        Assert.True(lines.Max(l => l.Length) <= 255, "a PostScript line is at most 255 characters for DSC readers");
        Assert.DoesNotContain("showpage", eps);
        Assert.DoesNotContain("findfont", eps);

        // balanced: one save/restore around everything, every gsave closed
        var words = Regex.Matches(eps, @"\b(save|restore|gsave|grestore)\b").Select(m => m.Value).ToList();
        Assert.Equal(1, words.Count(w => w == "save"));
        Assert.Equal(1, words.Count(w => w == "restore"));
        Assert.Equal(words.Count(w => w == "gsave"), words.Count(w => w == "grestore"));
    }

    [Fact]
    public void The_bounding_box_is_the_export_area_the_page_the_pasteboard_or_the_selection()
    {
        var doc = NewDoc();
        var box = Add(doc, new RectangleObject(60, 30) { Transform = Affine2.Translation(30.5, 20.25), Style = Fill(DocColor.Black) });
        var away = Add(doc, new EllipseObject(40, 40) { Transform = Affine2.Translation(400, 300), Style = Fill(DocColor.Black) });

        Assert.Contains("%%BoundingBox: 0 0 216 144", Eps(doc));
        Assert.Contains("%%BoundingBox: 0 0 440 340", Eps(doc, new ExportOptions { IncludePasteboard = true }));
        var selection = Eps(doc, new ExportOptions { Scope = ExportScope.Selection, Selection = [box] });
        Assert.Contains("%%BoundingBox: 0 0 60 30", selection);
        Assert.Contains("%%HiResBoundingBox: 0 0 60 30", selection);
        Assert.Contains("%%BoundingBox: 0 0 40 40", Eps(doc, new ExportOptions { Scope = ExportScope.Selection, Selection = [away] }));
    }

    [Fact]
    public void Guides_hidden_things_pasteboard_and_non_printable_layers_are_not_in_the_file()
    {
        var doc = NewDoc();
        Add(doc, new RectangleObject(20, 20) { Style = Fill(DocColor.Black) });                                             // 1: drawn
        Add(doc, new RectangleObject(20, 20) { Style = Fill(DocColor.Black), Visible = false });                            // hidden
        Add(doc, new RectangleObject(20, 20) { Transform = Affine2.Translation(900, 900), Style = Fill(DocColor.Black) });   // pasteboard
        var notes = new Layer("Notes") { Printable = false };
        doc.InsertLayer(1, notes);
        doc.Insert(notes, -1, new RectangleObject(20, 20) { Style = Fill(DocColor.Black) });                                // not printable
        new AddGuideCommand(Guide.Vertical(50)).Do(doc);

        var interpreter = new MiniPostScript();
        Draw(Eps(doc), 1, interpreter);

        Assert.Equal(1, interpreter.Fills);
        Assert.Equal(0, interpreter.Strokes);
    }

    // ---------------------------------------------------------------- vectors

    private static Document VectorSample()
    {
        var doc = NewDoc(4, 3);
        Add(doc, new RectangleObject(80, 50, 10) { Transform = Affine2.Rotation(0.2) * Affine2.Translation(30, 20), Style = Fill(new DocColor(255, 140, 0), 6) });
        Add(doc, new EllipseObject(60, 40) { Transform = Affine2.Translation(150, 30), Style = new ObjectStyle(new SolidPaint(new DocColor(0, 100, 200)), new StrokeStyle(new SolidPaint(new DocColor(200, 0, 50)), 5, LineJoin.Round, LineCap.Round)) });
        var b = new PathBuilder { FillRule = FillRule.EvenOdd };
        b.MoveTo(20, 150).LineTo(120, 150).ArcTo(new Vec2(150, 175), new Vec2(120, 200)).LineTo(20, 200).Close()
         .MoveTo(40, 165).LineTo(70, 165).LineTo(70, 185).LineTo(40, 185).Close();
        Add(doc, new PathObject(b.Build()) { Style = Fill(new DocColor(20, 160, 60)) });
        Add(doc, new PathObject(new PathBuilder().MoveTo(200, 120).CubicTo(230, 90, 250, 160, 270, 120).Build()) { Style = new ObjectStyle(null, new StrokeStyle(new SolidPaint(DocColor.Black), 8, LineJoin.Bevel, LineCap.Square)) });
        Add(doc, new TextObject("Sign 123", "Arial", 40) { Transform = Affine2.Translation(160, 190), Style = Fill(new DocColor(90, 0, 140)) });
        return doc;
    }

    [Fact]
    public void Vectors_come_out_where_the_png_export_has_them()
    {
        var doc = VectorSample();
        var interpreter = new MiniPostScript();

        using var fromEps = Draw(Eps(doc), 3, interpreter);
        using var fromPng = Png(doc, 3);

        double diff = Difference(fromEps, fromPng, out double bad);
        Assert.True(diff < 0.6, $"mean difference {diff:0.###}");
        Assert.True(bad < 0.002, $"{bad:P2} of the pixels differ strongly");
        Assert.Equal(0, interpreter.PicturesDrawn);
        Assert.Equal(4, interpreter.Fills);     // rectangle, ellipse, path, and all the letters as one path: all vectors
        Assert.Equal(3, interpreter.Strokes);   // rectangle, ellipse and the curve
    }

    [Fact]
    public void Text_is_written_as_outlines_never_as_fonts()
    {
        var doc = NewDoc();
        Add(doc, new TextObject("Hello", "Arial", 40) { Transform = Affine2.Translation(20, 70), Style = Fill(DocColor.Black) });

        var eps = Eps(doc);

        Assert.DoesNotContain("show", eps);
        Assert.DoesNotContain("selectfont", eps);
        Assert.Contains("curveto", eps);
        using var bmp = Draw(eps);
        int inked = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Alpha > 128) inked++;
        Assert.True(inked > 300);
    }

    [Fact]
    public void A_combined_object_knocks_out_its_overlap_like_the_screen()
    {
        var doc = NewDoc();
        var a = new RectangleObject(80, 80) { Style = Fill(new DocColor(200, 0, 0)) };
        var b = new RectangleObject(80, 80) { Transform = Affine2.Translation(40, 40), Style = Fill(new DocColor(0, 0, 200)) };
        Add(doc, new CombinedObject([a, b]) { Transform = Affine2.Translation(20, 10) });

        using var bmp = Draw(Eps(doc));

        Assert.Equal(new SKColor(200, 0, 0), bmp.GetPixel(40, 30));         // only in the first
        Assert.Equal(new SKColor(0, 0, 200), bmp.GetPixel(120, 110));       // only in the second
        Assert.Equal(0, bmp.GetPixel(80, 70).Alpha);                         // in both: a hole
        using var png = Png(doc, 1);
        Assert.True(Difference(bmp, png, out _) < 0.5);
    }

    [Fact]
    public void Colors_are_exact_and_a_cmyk_color_keeps_its_four_numbers()
    {
        var doc = NewDoc();
        var cmyk = DocColor.FromCmyk(10, 20.5, 30, 40, "Warm Grey", "Pantone");
        Add(doc, new RectangleObject(50, 50) { Style = Fill(new DocColor(12, 200, 99)) });
        Add(doc, new RectangleObject(50, 50) { Transform = Affine2.Translation(100, 0), Style = Fill(cmyk) });

        var eps = Eps(doc);

        Assert.Contains("0.04706 0.78431 0.38824 setrgbcolor", eps);
        Assert.Contains("0.1 0.205 0.3 0.4 setcmykcolor", eps);
        using var bmp = Draw(eps);
        Assert.Equal(new SKColor(12, 200, 99), bmp.GetPixel(25, 25));
        Assert.Equal(new SKColor(cmyk.R, cmyk.G, cmyk.B), bmp.GetPixel(125, 25));
    }

    // ---------------------------------------------------------------- gradients

    [Theory]
    [InlineData(GradientKind.Linear, 0.0)]
    [InlineData(GradientKind.Horizontal, 0.0)]
    [InlineData(GradientKind.Circle, 0.0)]
    [InlineData(GradientKind.Ellipse, 0.0)]
    [InlineData(GradientKind.Linear, 0.5)]    // turned
    [InlineData(GradientKind.Ellipse, 0.7)]
    public void Gradients_are_shadings_that_follow_the_object_exactly(GradientKind kind, double angle)
    {
        var doc = NewDoc(4, 3);
        var g = GradientPaint.Create(kind, new DocColor(220, 30, 30)).WithStops([new GradientNode(0, new DocColor(220, 30, 30)), new GradientNode(0.5, new DocColor(250, 220, 40)), new GradientNode(1, new DocColor(20, 40, 200))]);
        Add(doc, new RectangleObject(150, 90, 12) { Transform = Affine2.Rotation(angle) * Affine2.Translation(60, 40), Style = new ObjectStyle(g, null) });
        var interpreter = new MiniPostScript();

        var eps = Eps(doc);
        using var fromEps = Draw(eps, 2, interpreter);
        using var fromPng = Png(doc, 2);

        Assert.Contains("shfill", eps);
        Assert.Equal(1, interpreter.ShadingsDrawn);
        Assert.Equal(0, interpreter.PicturesDrawn);
        double diff = Difference(fromEps, fromPng, out double bad);
        Assert.True(diff < 1.2, $"{kind} at {angle}: mean difference {diff:0.###}");
        Assert.True(bad < 0.004, $"{kind} at {angle}: {bad:P2} differ strongly");
    }

    [Fact]
    public void A_gradient_with_transparency_becomes_a_picture_instead_of_losing_it()
    {
        var doc = NewDoc();
        var fade = GradientPaint.Create(GradientKind.Linear, new DocColor(0, 0, 0)).WithStops([new GradientNode(0, new DocColor(0, 0, 0, 255)), new GradientNode(1, new DocColor(0, 0, 0, 0))]);
        Add(doc, new RectangleObject(100, 60) { Transform = Affine2.Translation(40, 40), Style = new ObjectStyle(fade, null) });
        var interpreter = new MiniPostScript();

        var eps = Eps(doc);
        using var bmp = Draw(eps, 1, interpreter);

        Assert.DoesNotContain("shfill", eps);
        Assert.Equal(1, interpreter.PicturesDrawn);
        using var png = Png(doc, 1);
        Assert.True(Difference(bmp, png, out _) < 3);
    }

    // ---------------------------------------------------------------- pictures and effects

    private static byte[] FourColorPicture()
    {
        using var bmp = new SKBitmap(2, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bmp.SetPixel(0, 0, new SKColor(255, 0, 0));        // top left: red
        bmp.SetPixel(1, 0, new SKColor(0, 0, 0, 0));        // top right: nothing
        bmp.SetPixel(0, 1, new SKColor(0, 0, 255));         // bottom left: blue
        bmp.SetPixel(1, 1, new SKColor(0, 255, 0));         // bottom right: green
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void A_picture_lands_the_right_way_up_with_its_transparent_part_left_out()
    {
        var doc = NewDoc();
        var id = doc.Assets.Add(FourColorPicture(), "image/png");
        Add(doc, new ImageObject(id, 80, 80, 2, 2, "four.png") { Transform = Affine2.Translation(20, 10) });
        var interpreter = new MiniPostScript();

        var eps = Eps(doc);
        using var bmp = Draw(eps, 1, interpreter);

        Assert.Equal(1, interpreter.PicturesDrawn);
        Assert.Contains("/MaskColor", eps);                                // the transparent corner
        Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(40, 30));        // top left is red (not mirrored)
        Assert.Equal(0, bmp.GetPixel(80, 30).Alpha);                        // top right: transparent
        Assert.Equal(new SKColor(0, 0, 255), bmp.GetPixel(40, 70));        // bottom left is blue
        Assert.Equal(new SKColor(0, 255, 0), bmp.GetPixel(80, 70));
        Assert.Equal(0, bmp.GetPixel(10, 5).Alpha);                         // beside the picture: nothing
    }

    [Fact]
    public void A_turned_and_flipped_picture_follows_its_matrix()
    {
        var doc = NewDoc();
        var id = doc.Assets.Add(FourColorPicture(), "image/png");
        // 180 degrees about its center: red goes to the bottom right
        Add(doc, new ImageObject(id, 80, 80, 2, 2) { Transform = Affine2.Rotation(Math.PI, new Vec2(40, 40)) * Affine2.Translation(20, 10) });

        using var bmp = Draw(Eps(doc));

        Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(80, 70));
        Assert.Equal(new SKColor(0, 255, 0), bmp.GetPixel(40, 30));
        Assert.Equal(0, bmp.GetPixel(40, 70).Alpha);                        // the transparent corner is now bottom left
    }

    [Fact]
    public void A_big_photo_is_embedded_at_the_raster_dpi_not_at_its_own_size()
    {
        var doc = NewDoc();
        using var bmp = new SKBitmap(3000, 2000);
        bmp.Erase(new SKColor(20, 90, 160));
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        var id = doc.Assets.Add(data.ToArray(), "image/png");
        Add(doc, new ImageObject(id, 144, 96, 3000, 2000) { Transform = Affine2.Translation(10, 10) });   // 2 x 1.33 inches

        var eps = Eps(doc, new ExportOptions { EpsRasterDpi = 150 });

        Assert.Contains("/Width 300 /Height 200 ", eps);   // 2 in x 150 dpi
        Assert.True(eps.Length < 40_000);                   // flat color compresses away
        using var drawn = Draw(eps);
        Assert.Equal(new SKColor(20, 90, 160), drawn.GetPixel(60, 40));
    }

    [Fact]
    public void A_soft_shadow_becomes_a_picture_under_the_vector_art_and_stays_clear_where_nothing_is_painted()
    {
        var doc = NewDoc(4, 3);
        Add(doc, new RectangleObject(288, 216) { Style = Fill(new DocColor(255, 240, 200)) });   // a backdrop the shadow falls on
        var shadow = Add(doc, new PathObject(PathGeometry.Rectangle(new Rect2(0, 0, 90, 50))) { Transform = Affine2.Translation(60, 80), Style = new ObjectStyle(new SolidPaint(DocColor.Black), null, new BlurStyle(SoftKind.SoftShadow, 0.8, 0.6, 50)) });
        Add(doc, new RectangleObject(90, 50) { Transform = Affine2.Translation(50, 70), Style = Fill(new DocColor(0, 120, 200)) });
        var interpreter = new MiniPostScript();

        var eps = Eps(doc);
        using var fromEps = Draw(eps, 2, interpreter);
        using var fromPng = Png(doc, 2);

        Assert.Equal(1, interpreter.PicturesDrawn);                   // the soft shadow, with the backdrop under it
        Assert.Equal(2, interpreter.Fills);                           // the backdrop and the front rectangle stay vectors
        double diff = Difference(fromEps, fromPng, out double bad);
        Assert.True(diff < 2.5, $"mean difference {diff:0.###}");
        Assert.True(bad < 0.01, $"{bad:P2} differ strongly");
    }

    [Fact]
    public void A_soft_shadow_on_an_empty_page_leaves_its_far_corners_transparent()
    {
        var doc = NewDoc(4, 3);
        Add(doc, new PathObject(PathGeometry.Rectangle(new Rect2(0, 0, 60, 40))) { Transform = Affine2.Translation(100, 80), Style = new ObjectStyle(new SolidPaint(DocColor.Black), null, new BlurStyle(SoftKind.SoftShadow, 1, 0.8, 40)) });
        var interpreter = new MiniPostScript();

        using var bmp = Draw(Eps(doc), 1, interpreter);

        Assert.Equal(1, interpreter.PicturesDrawn);
        Assert.Equal(0, bmp.GetPixel(10, 10).Alpha);                  // far from the shadow
        Assert.True(bmp.GetPixel(130, 100).Alpha == 255);             // its dark core
        Assert.Equal(0, bmp.GetPixel(130, 100).Red);
        // the picture is only as big as the shadow with its halo: the page corner is not covered by it
        Assert.Contains("/ImageType 4", Eps(doc));
    }

    [Fact]
    public void A_see_through_fill_and_a_feather_are_pictures_the_rest_stays_vector()
    {
        var doc = NewDoc();
        Add(doc, new RectangleObject(60, 60) { Style = Fill(new DocColor(255, 0, 0)) });
        Add(doc, new RectangleObject(60, 60) { Transform = Affine2.Translation(40, 40), Style = Fill(new DocColor(0, 0, 255, 128)) });
        Add(doc, new EllipseObject(50, 50) { Transform = Affine2.Translation(140, 20), Style = new ObjectStyle(new SolidPaint(new DocColor(0, 150, 0)), null, null, new FeatherStyle(0.25, 0)) });
        Add(doc, new RectangleObject(20, 20) { Transform = Affine2.Translation(10, 100), Style = Fill(DocColor.Black) });
        var interpreter = new MiniPostScript();

        using var bmp = Draw(Eps(doc), 2, interpreter);

        Assert.Equal(2, interpreter.PicturesDrawn);
        Assert.Equal(2, interpreter.Fills);                                 // the first and the last rectangle
        using var png = Png(doc, 2);
        Assert.True(Difference(bmp, png, out _) < 3);
    }

    [Fact]
    public void Selection_export_includes_the_halo_of_a_soft_shadow()
    {
        var doc = NewDoc();
        var shadow = Add(doc, new PathObject(PathGeometry.Rectangle(new Rect2(0, 0, 60, 40))) { Transform = Affine2.Translation(50, 50), Style = new ObjectStyle(new SolidPaint(DocColor.Black), null, new BlurStyle(SoftKind.SoftShadow, 1, 1, 40)) });

        var plan = ExportPlan.Create(doc, new ExportOptions { Scope = ExportScope.Selection, Selection = [shadow] });

        double sigma = 0.068 * 1 * 40;
        Assert.Equal(50 - 3 * sigma, plan.Area.Left, 6);
        Assert.Equal(110 + 3 * sigma, plan.Area.Right, 6);
        Assert.Equal(60 + 6 * sigma, plan.Area.Width, 6);
    }

    [Fact]
    public void The_whole_busy_sample_document_exports_to_a_valid_eps()
    {
        var doc = SampleDocuments.Everything();

        var eps = Eps(doc, new ExportOptions { EpsRasterDpi = 72 });

        var interpreter = new MiniPostScript();
        using var bmp = Draw(eps, 0.5, interpreter);
        Assert.True(interpreter.Fills > 0, $"fills {interpreter.Fills}, pictures {interpreter.PicturesDrawn}");
        Assert.Contains("%%BoundingBox: 0 0 1728 864", eps);
        Assert.Single(Regex.Matches(eps, @"\bsave\b"));
    }
}
