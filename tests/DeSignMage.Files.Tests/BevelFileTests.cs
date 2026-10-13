using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using DeSignMage.Geometry;
using DeSignMage.Rendering.Export;
using SkiaSharp;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>Easy Bevel in a saved file and in the PNG (R8): the crescents are ordinary paths with a see-through color and a feather, so they must survive a save and draw the same rims.</summary>
public class BevelFileTests
{
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

    private static Document Reload(Document doc) => DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(doc))).Document;

    [Fact]
    public void A_bevel_survives_a_save_and_load_in_place_grouped_with_its_paint_and_feather()
    {
        var s = NewSession(24, 12);
        var a = Add(s, 100, 100, 200, 100, DocColor.FromHex("#CC2222"));
        s.ApplyBevel(new BevelSettings { Width = 0.03, Softness = 0.07, Highlight = 0.6, HighlightColor = DocColor.FromHex("#FFD400") });
        var text = new TextObject("Bevel", "Arial", 90) { Transform = Affine2.Translation(100, 400), Style = ObjectStyle.FillOnly(DocColor.FromHex("#2060E0")) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, text));
        s.Selection.Set(text);
        s.ApplyBevel(new BevelSettings { Neutral = true });
        var b1 = Add(s, 400, 100, 100, 100, DocColor.FromHex("#22AA55"));
        var b2 = Add(s, 520, 100, 100, 60, DocColor.FromHex("#AA2255"));
        var group = s.GroupObjects([b1, b2])!;
        s.Selection.Set(group);
        s.ApplyBevel(new BevelSettings { IncludeShadow = false });

        var loaded = Reload(s.Document);

        Assert.Equal(s.Document.GroupCount, loaded.GroupCount);
        var before = s.Document.Layers[0].Children;
        var after = loaded.Layers[0].Children;
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].GetType(), after[i].GetType());
            Assert.Equal(before[i].Name, after[i].Name);
            Assert.Equal(before[i].Style, after[i].Style);                          // fill, see-through alpha and feather, exactly
            Assert.Equal(before[i].GetWorldBounds(), after[i].GetWorldBounds());
            Assert.Equal(before[i].Parent is GroupObject, after[i].Parent is GroupObject);
        }
        // the order: owner, then its highlight, then its shadow
        Assert.Equal(["Bevel Highlight", "Bevel Shadow"], after.Skip(1).Take(2).Select(o => o.Name));
        var hl = after.OfType<PathObject>().First(p => p.Name == "Bevel Highlight");
        Assert.Equal(new FeatherStyle(0.07, 0), hl.Style.Feather);
        Assert.Equal(DocColor.FromHex("#FFD400") with { A = 153 }, Assert.IsType<SolidPaint>(hl.Style.Fill).Color);
        Assert.IsType<GroupObject>(hl.Parent);
        Assert.Same(hl.Parent, after[0].Parent);                                      // grouped with its owner
        Assert.Equal(before.OfType<PathObject>().Sum(p => p.Geometry.Points.Count), after.OfType<PathObject>().Sum(p => p.Geometry.Points.Count));
        Assert.IsType<TextObject>(after.First(o => o is TextObject));                 // the text stayed live text
    }

    private static SKBitmap Png(Document doc, double dpi) =>
        SKBitmap.Decode(DocumentExporter.RenderPng(doc, new ExportOptions { Dpi = dpi, WhiteBackground = true })) ?? throw new InvalidOperationException("no PNG");

    /// <summary>Gray level at a document point, interpolated between pixel centers so it does not depend on the resolution.</summary>
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

    private static Document BeveledGray(BevelSettings settings)
    {
        var s = NewSession();
        Add(s, 50, 50, 200, 100, DocColor.FromHex("#808080"));
        s.ApplyBevel(settings);
        return s.Document;
    }

    [Fact]
    public void The_png_has_a_lighter_rim_on_the_upper_left_a_darker_one_on_the_lower_right_and_the_middle_untouched_at_any_dpi()
    {
        var doc = BeveledGray(new BevelSettings { Inset = 0, Width = 0.1 });         // rims 10 points wide on a 128 gray object, 80 % white / black
        foreach (double dpi in new[] { 72.0, 144.0, 300.0 })
        {
            using var bmp = Png(doc, dpi);
            Assert.InRange(Gray(bmp, dpi, 150, 100), 126, 130);                     // the middle: the object's own gray
            Assert.InRange(Gray(bmp, dpi, 55, 100), 190, 240);                      // the left rim: white at 80 % over 128 = about 230 (softened a little)
            Assert.InRange(Gray(bmp, dpi, 150, 55), 190, 240);                      // the top rim
            Assert.InRange(Gray(bmp, dpi, 245, 100), 15, 70);                       // the right rim: black at 80 % over 128 = about 26
            Assert.InRange(Gray(bmp, dpi, 150, 145), 15, 70);                       // the bottom rim
            Assert.InRange(Gray(bmp, dpi, 30, 100), 250, 255);                      // beyond the object: the white page, no halo
            Assert.InRange(Gray(bmp, dpi, 270, 100), 250, 255);
        }
    }

    [Fact]
    public void Highlight_strength_scales_the_rim_and_softness_zero_gives_a_hard_edge_on_the_inside()
    {
        using (var half = Png(BeveledGray(new BevelSettings { Inset = 0, Width = 0.1, Highlight = 0.5, Softness = 0 }), 144))
            Assert.InRange(Gray(half, 144, 55, 100), 185, 195);                     // white at 50 % over 128
        using var hard = Png(BeveledGray(new BevelSettings { Inset = 0, Width = 0.1, Softness = 0 }), 300);
        double inside = Gray(hard, 300, 58, 100), beyond = Gray(hard, 300, 62, 100);   // the rim ends at x = 60
        Assert.True(inside > 220 && beyond is > 125 and < 131, $"inside {inside}, beyond {beyond}");
    }

    [Fact]
    public void A_gradient_or_picture_under_the_bevel_shows_through_the_rim()
    {
        var s = NewSession();
        var r = Add(s, 50, 50, 200, 100, DocColor.FromHex("#C00000"));
        s.ApplyBevel(new BevelSettings { Inset = 0, Width = 0.1 });
        using var red = Png(s.Document, 144);
        var rimOnRed = red.GetPixel((int)(55 * 2), 100 * 2);
        r.Style = ObjectStyle.FillOnly(DocColor.FromHex("#0000C0"));                  // recolor the owner: the same rim, over another color
        using var blue = Png(s.Document, 144);
        var rimOnBlue = blue.GetPixel((int)(55 * 2), 100 * 2);
        Assert.True(rimOnRed.Red > 220 && rimOnRed.Blue < rimOnBlue.Blue + 1 && rimOnRed.Green > 150);   // a lighter red
        Assert.True(rimOnBlue.Blue > 220 && rimOnBlue.Red < rimOnRed.Red);                              // a lighter blue
        Assert.NotEqual(rimOnRed, rimOnBlue);                                                           // the rim carries the color under it
    }
}