using System.Globalization;
using System.Xml.Linq;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Geometry;
using DeSignMage.Rendering;
using DeSignMage.Rendering.Export;
using SkiaSharp;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>
/// Soft Shadow, Glow and Feather in every output (R8): PNG pixels at the chosen DPI, SVG as filters and masks, PDF, and what the renderer keeps (the masks).
/// The numbers are the legacy program's (soft-shadow.md section 2.3, feather.md section 2): blur sigma 0.068 × softness × height, feather sigma 0.164 × band.
/// </summary>
public class SoftEffectExportTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private static EditorSession NewSession(double widthIn = 4, double heightIn = 3) => new(new Document(PageSetup.FromInches(widthIn, heightIn)))
    {
        BooleanService = GeometryEngines.Booleans,
        OffsetService = GeometryEngines.Offsets,
    };

    private static RectangleObject Add(EditorSession s, double x, double y, double w, double h, DocColor color)
    {
        var r = new RectangleObject(w, h) { Transform = Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(color) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, r));
        s.Selection.Set(r);
        return r;
    }

    /// <summary>The picture laid on white paper: exports are transparent where nothing is drawn (R27), and these checks are about the colors of what is drawn.</summary>
    private static SKBitmap OnWhite(SKBitmap source)
    {
        var paper = new SKBitmap(source.Width, source.Height);
        using (var c = new SKCanvas(paper))
        {
            c.Clear(SKColors.White);
            c.DrawBitmap(source, 0, 0);
        }
        source.Dispose();
        return paper;
    }

    private static SKBitmap Png(Document doc, double dpi) =>
        OnWhite(SKBitmap.Decode(DocumentExporter.RenderPng(doc, new ExportOptions { Dpi = dpi })) ?? throw new InvalidOperationException("no PNG"));

    /// <summary>Gray level at a document point (the page is white, shadows are black), interpolated between the pixel centers so it does not depend on the resolution.</summary>
    private static double Gray(SKBitmap bmp, double dpi, double x, double y)
    {
        double k = dpi / 72.0;
        double fx = x * k - 0.5, fy = y * k - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double tx = fx - x0, ty = fy - y0;
        double G(int px, int py)
        {
            var c = bmp.GetPixel(Math.Clamp(px, 0, bmp.Width - 1), Math.Clamp(py, 0, bmp.Height - 1));
            return (c.Red + c.Green + c.Blue) / 3.0;
        }
        return (G(x0, y0) * (1 - tx) + G(x0 + 1, y0) * tx) * (1 - ty) + (G(x0, y0 + 1) * (1 - tx) + G(x0 + 1, y0 + 1) * tx) * ty;
    }

    // ---------------------------------------------------------------- Soft Shadow: PNG

    private static Document ShadowDoc(double softness = 0.5, double opacity = 1.0)
    {
        var s = NewSession();
        Add(s, 80, 60, 100, 60, DocColor.FromHex("#CC0000"));
        s.ApplySoftEffect(new SoftSettings { Opacity = opacity, Softness = softness, ShiftX = 20, ShiftY = 20, Color = DocColor.Black });
        return s.Document;
    }

    [Fact]
    public void A_soft_shadow_is_dark_inside_half_dark_on_its_edge_and_gone_beyond_it_at_any_resolution()
    {
        var doc = ShadowDoc();   // the shadow covers x 100..200, y 80..140; sigma = 0.068 x 0.5 x 60 = 2.04 points
        foreach (double dpi in new[] { 72.0, 144.0, 300.0 })
        {
            using var bmp = Png(doc, dpi);
            Assert.InRange(Gray(bmp, dpi, 190, 130), 0, 12);                       // well inside the shadow, outside the source: black
            // the blurred edge is point symmetric around the shadow's edge: what is dark inside is as light outside
            if (dpi >= 144) Assert.InRange(Gray(bmp, dpi, 199, 130) + Gray(bmp, dpi, 201, 130), 235, 275);
            Assert.InRange(Gray(bmp, dpi, 200, 130), 105, 150);                    // on its edge: half
            Assert.InRange(Gray(bmp, dpi, 207, 130), 240, 255);                    // 3.4 sigma beyond: white page again
            Assert.InRange(Gray(bmp, dpi, 190, 150), 240, 255);                    // below the shadow
            // the soft edge is smooth: darker at every step in toward the shadow
            double previous = 256;
            for (double x = 210; x >= 192; x -= 2)
            {
                double g = Gray(bmp, dpi, x, 130);
                Assert.True(g <= previous + 3, $"at {dpi} dpi, x={x}: {g} after {previous}");
                previous = g;
            }
        }
    }

    [Fact]
    public void The_blur_is_the_same_width_at_every_resolution()
    {
        var doc = ShadowDoc();
        using var lo = Png(doc, 144);
        using var hi = Png(doc, 400);
        for (double x = 194; x <= 206; x += 2)
            Assert.InRange(Math.Abs(Gray(lo, 144, x, 130) - Gray(hi, 400, x, 130)), 0, 12);
        // and it is the legacy width: at one sigma (2.04 points) inside the edge the shadow is 84 % black
        Assert.InRange(Gray(hi, 400, 200 - 2.04, 130), 255 * (1 - 0.87), 255 * (1 - 0.81));
    }

    [Fact]
    public void The_source_stays_sharp_on_top_of_its_shadow()
    {
        var doc = ShadowDoc();
        using var bmp = Png(doc, 144);
        var inside = bmp.GetPixel(100 * 2, 90 * 2);                          // inside the red rectangle
        Assert.Equal(0xCC, inside.Red);
        Assert.InRange((int)inside.Green, 0, 2);
        // the rectangle's own right edge (x = 180) has no blur: one pixel step from red to the white page (y = 65 is above the shadow, which starts at 80)
        var left = bmp.GetPixel((int)(179.5 * 2), 65 * 2);
        var right = bmp.GetPixel((int)(180.5 * 2), 65 * 2);
        Assert.Equal(0xCC, left.Red);
        Assert.InRange((int)right.Green, 250, 255);
    }

    [Fact]
    public void Opacity_scales_the_shadow_and_zero_softness_gives_a_hard_edge()
    {
        using (var half = Png(ShadowDoc(opacity: 0.5), 144))
            Assert.InRange(Gray(half, 144, 190, 130), 120, 135);              // 50 % black on white
        using var hard = Png(ShadowDoc(softness: 0, opacity: 1), 144);
        Assert.InRange(Gray(hard, 144, 199, 130), 0, 5);
        Assert.InRange(Gray(hard, 144, 201, 130), 250, 255);
    }

    [Fact]
    public void A_glow_is_light_around_a_dark_source_and_a_cast_shadow_follows_its_slant()
    {
        var s = NewSession();
        var dark = new RectangleObject(288, 216) { Style = ObjectStyle.FillOnly(DocColor.Black) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, dark));   // a dark page, so a light halo shows
        var box = Add(s, 100, 80, 80, 60, DocColor.FromHex("#101010"));
        s.ApplySoftEffect(new SoftSettings { Kind = SoftKind.Glow, Opacity = 1, Softness = 0.4, Thickness = 1, Color = DocColor.FromHex("#FFCC00") });
        using var bmp = Png(s.Document, 144);
        // 3 points outside of the box, inside the grown outline (0.04 × 60 = 2.4 pt per side plus blur): yellow-ish and bright
        var near = bmp.GetPixel((int)(97.5 * 2), 110 * 2);
        Assert.True(near.Red > 120 && near.Green > 90 && near.Blue < near.Red, $"a yellow halo next to the box, got {near}");
        var far = bmp.GetPixel((int)(60 * 2), 110 * 2);
        Assert.True(far.Red < 5 && far.Green < 5, $"nothing far away, got {far}");
    }

    // ---------------------------------------------------------------- Feather: PNG

    private static Document FeatherDoc(double amount, double inset)
    {
        var s = NewSession();
        Add(s, 50, 50, 100, 100, DocColor.Black);
        Assert.True(s.SetFeather(amount, inset));
        return s.Document;
    }

    [Fact]
    public void A_feather_fades_the_objects_edge_with_its_50_percent_point_on_the_edge_without_inset()
    {
        var doc = FeatherDoc(0.1, 0);                                       // band 10 points, sigma 0.164 x 10 = 1.64 points; the object lies in x 50..150
        foreach (double dpi in new[] { 144.0, 300.0 })
        {
            using var bmp = Png(doc, dpi);
            Assert.InRange(Gray(bmp, dpi, 100, 100), 0, 5);                  // the middle: untouched
            Assert.InRange(Gray(bmp, dpi, 51.5, 100), 28, 75);               // 1.5 points inside the edge: 82 % (the fade only takes away, it cannot reach beyond the object)
            Assert.InRange(Gray(bmp, dpi, 53, 100), 0, 22);                  // 3 points: 97 %
            Assert.InRange(Gray(bmp, dpi, 48, 100), 250, 255);               // outside: nothing
            Assert.InRange(Gray(bmp, dpi, 148.5, 100), 28, 75);              // the same on the opposite side
            Assert.InRange(Gray(bmp, dpi, 100, 51.5), 28, 75);               // and at the top: the band is the same on all sides
        }
    }

    [Fact]
    public void Inset_pulls_the_fade_in_so_a_hard_edge_loses_its_edge()
    {
        var doc = FeatherDoc(0.1, 0.38);                                    // the erase stroke is 10 x 0.38 x 1.5 = 5.7 points wide: the 50 % point is 2.85 inside
        foreach (double dpi in new[] { 144.0, 300.0 })
        {
            using var bmp = Png(doc, dpi);
            Assert.InRange(Gray(bmp, dpi, 50.8, 100), 205, 250);             // the former edge is nearly white
            Assert.InRange(Gray(bmp, dpi, 52.85, 100), 105, 150);            // the half point
            Assert.InRange(Gray(bmp, dpi, 55, 100), 8, 45);
            Assert.InRange(Gray(bmp, dpi, 58, 100), 0, 8);
        }
    }

    [Fact]
    public void Feather_of_a_text_object_uses_its_height_for_the_band()
    {
        var s = NewSession(6, 2);
        var text = new TextObject("Hi", "Arial", 100) { Transform = Affine2.Translation(20, 20), Style = ObjectStyle.FillOnly(DocColor.Black) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, text));
        s.Selection.Set(text);
        var before = Png(s.Document, 72);
        s.SetFeather(0.05, 0);
        using var after = Png(s.Document, 72);
        using (before)
        {
            long dark(SKBitmap b) { long n = 0; for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++) n += 255 - b.GetPixel(x, y).Red; return n; }
            Assert.True(dark(after) < dark(before), "a feather takes ink off the edge");
            Assert.True(dark(after) > dark(before) * 0.8, "but it is an edge fade, not a deletion");
        }
    }

    // ---------------------------------------------------------------- PDF and SVG

    [Fact]
    public void A_pdf_with_soft_effects_is_written()
    {
        var s = NewSession();
        Add(s, 80, 60, 100, 60, DocColor.FromHex("#CC0000"));
        s.ApplySoftEffect(new SoftSettings { ShiftX = 10, ShiftY = 10 });
        var other = Add(s, 200, 60, 50, 50, DocColor.Black);
        s.SetFeather(0.1, 0.38);
        var pdf = DocumentExporter.RenderPdf(s.Document, new ExportOptions());
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.True(pdf.Length > 1000);
        _ = other;
    }

    [Fact]
    public void Svg_keeps_the_shadow_as_a_vector_path_with_a_blur_filter_and_the_feather_as_a_mask()
    {
        var s = NewSession();
        Add(s, 80, 60, 100, 60, DocColor.FromHex("#CC0000"));
        s.ApplySoftEffect(new SoftSettings { Opacity = 0.6, Softness = 0.5, ShiftX = 20, ShiftY = 20, Color = DocColor.Black });
        Add(s, 200, 60, 50, 50, DocColor.FromHex("#00AA00"));
        s.SetFeather(0.1, 0.38);

        var svg = XDocument.Parse(SvgExporter.Render(s.Document, new ExportOptions()));

        var blur = svg.Descendants(Svg + "filter").Single(f => (string?)f.Attribute("id") is { } id && id.StartsWith("soft-"));
        double std = double.Parse((string)blur.Element(Svg + "feGaussianBlur")!.Attribute("stdDeviation")!, CultureInfo.InvariantCulture);
        Assert.Equal(0.068 * 0.5 * 60, std, 2);
        var shadowPath = svg.Descendants(Svg + "path").Single(p => (string?)p.Attribute("filter") == "url(#" + (string)blur.Attribute("id")! + ")");
        Assert.Equal("#000000", (string?)shadowPath.Attribute("fill"));
        Assert.Equal(0.6, double.Parse((string)shadowPath.Attribute("fill-opacity")!, CultureInfo.InvariantCulture), 3);

        var mask = svg.Descendants(Svg + "mask").Single();
        var users = svg.Descendants(Svg + "g").Where(g => (string?)g.Attribute("mask") == "url(#" + (string)mask.Attribute("id")! + ")").ToList();
        Assert.Single(users);
        Assert.Contains(users[0].Descendants(Svg + "path"), p => (string?)p.Attribute("fill") == "#00aa00" || (string?)p.Attribute("fill") == "#00AA00");
        Assert.Contains(mask.Descendants(Svg + "path"), p => (string?)p.Attribute("stroke") == "#000000");   // the inset's erase stroke
        Assert.DoesNotContain("data:image", SvgExporter.Render(s.Document, new ExportOptions()));              // nothing rasterized
    }

    // ---------------------------------------------------------------- what the renderer keeps

    [Fact]
    public void Masks_are_made_once_kept_through_moves_and_made_again_only_when_the_look_changes()
    {
        var s = NewSession();
        var src = Add(s, 80, 60, 100, 60, DocColor.FromHex("#CC0000"));
        s.ApplySoftEffect(new SoftSettings { ShiftX = 20, ShiftY = 20 });
        var group = (GroupObject)src.Parent!;
        using var r = new SceneRenderer(s.Document) { ViewScale = 1 };

        r.GetDocumentPicture(null).Release();
        Assert.Equal(1, r.SoftMasksMade);
        r.GetDocumentPicture(null).Release();                              // nothing changed: the picture is reused
        Assert.Equal(1, r.SoftMasksMade);

        s.History.Execute(TransformObjectsCommand.ApplyWorld("Move", [group], Affine2.Translation(30, 10)));
        r.GetDocumentPicture(null).Release();
        Assert.Equal(1, r.SoftMasksMade);                                  // moving reuses the mask

        var shadow = SoftEffect.Find(group, SoftKind.SoftShadow)!;
        s.Selection.Set(group);
        s.BeginSoftEffect(SoftKind.SoftShadow);
        s.GetTool<DeSignMage.Core.Editing.Tools.SoftEffectTool>().Update(t => t with { Softness = 0.9 });
        r.GetDocumentPicture(null).Release();
        Assert.Equal(2, r.SoftMasksMade);                                  // another softness: a new mask

        s.GetTool<DeSignMage.Core.Editing.Tools.SoftEffectTool>().Done();
        s.History.Execute(TransformObjectsCommand.ApplyWorld("Scale", [group], Affine2.Scale(2, 2)));
        r.GetDocumentPicture(null).Release();
        Assert.Equal(3, r.SoftMasksMade);                                  // scaled: a new mask

        // zooming in far: the blur is only about 3 pixels wide however big the object is on screen, so the same mask serves
        int made = r.SoftMasksMade;
        r.ViewScale = 4;
        r.GetDocumentPicture(null).Release();
        int atFour = r.SoftMasksMade;
        r.ViewScale = 64;
        r.GetDocumentPicture(null).Release();
        Assert.Equal(atFour, r.SoftMasksMade);
        Assert.True(atFour - made <= 1);
        _ = shadow;
    }

    [Fact]
    public void Masks_of_deleted_objects_are_let_go()
    {
        var s = NewSession();
        var src = Add(s, 80, 60, 100, 60, DocColor.FromHex("#CC0000"));
        s.ApplySoftEffect(new SoftSettings { ShiftX = 20, ShiftY = 20 });
        var group = (GroupObject)src.Parent!;
        using var r = new SceneRenderer(s.Document) { ViewScale = 1 };
        r.GetDocumentPicture(null).Release();
        Assert.Equal(1, r.SoftMasksKept);

        s.History.Execute(new DeleteObjectsCommand([group]));
        r.GetDocumentPicture(null).Release();
        r.GetDocumentPicture(null).Release();
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, new RectangleObject(10, 10)));
        r.GetDocumentPicture(null).Release();
        Assert.Equal(0, r.SoftMasksKept);
    }
}
