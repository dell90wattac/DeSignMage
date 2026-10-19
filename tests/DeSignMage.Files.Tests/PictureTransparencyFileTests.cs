using System.Text;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using DeSignMage.Imaging;
using DeSignMage.Rendering.Export;
using SkiaSharp;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>A cut-out picture (Transparency tab) saved in a .dsm file and exported as PNG, PDF, SVG and EPS.</summary>
public class PictureTransparencyFileTests
{
    private static PixelBuffer WhiteWithBlue(int w, int h)
    {
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool blue = x >= w / 4 && x < w * 3 / 4 && y >= h / 4 && y < h * 3 / 4;
                if (blue) p.Set(x, y, 10, 50, 200); else p.Set(x, y, 255, 255, 255);
            }
        return p;
    }

    private static PictureRecipe CutOutRecipe(int w, int h) => new(w, h)
    {
        Transparency = new PictureTransparency
        {
            Colors = new ValueList<int>([0xFFFFFF]),
            Tolerance = 20,
            EdgeOnly = true,
            Cuts = new ValueList<CutShape>([new CutShape(new ValueList<Vec2>([new Vec2(1.5, 2.25), new Vec2(w - 3, 2), new Vec2(w / 2.0, h - 1)]), Keep: false)]),
            Shrink = 1,
            Softness = 2,
            Opacity = 80,
        },
        Straighten = 4.5,
    };

    /// <summary>A document with one cut-out picture 144 x 108 points (2 x 1.5 inches) in a page of the same size: original, recipe and the finished picture.</summary>
    private static (Document Doc, ImageObject Image) CutOutPicture(PictureRecipe? recipe = null)
    {
        var doc = new Document(PageSetup.FromInches(2, 1.5));
        var original = WhiteWithBlue(80, 60);
        recipe ??= new PictureRecipe(80, 60) { Transparency = new PictureTransparency { Colors = new ValueList<int>([0xFFFFFF]) } };
        var result = PictureProcessor.Render(original, recipe);
        string originalId = doc.Assets.Add(ImageCodec.EncodePng(original), "image/png");
        string resultId = doc.Assets.Add(ImageCodec.EncodePng(result), "image/png");
        var image = new ImageObject(resultId, 144, 108, result.Width, result.Height, "logo.png") { SourceAssetId = originalId, Recipe = recipe };
        doc.Insert(doc.ActiveLayer, -1, image);
        return (doc, image);
    }

    // ---- the file ----

    [Fact]
    public void A_cut_out_picture_comes_back_from_the_file_with_every_transparency_setting()
    {
        var (doc, image) = CutOutPicture(CutOutRecipe(80, 60));
        var loaded = DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(doc)));
        ModelCompare.AssertEqual(doc, loaded.Document);
        var back = loaded.Document.AllObjects().OfType<ImageObject>().Single();
        Assert.Equal(image.Recipe, back.Recipe);
        var t = back.Recipe!.Transparency;
        Assert.Equal([0xFFFFFF], t.Colors);
        Assert.Equal((20, true, 1, 2, 80), (t.Tolerance, t.EdgeOnly, t.Shrink, t.Softness, t.Opacity));
        Assert.Equal(new Vec2(1.5, 2.25), t.Cuts[0].Points[0]);                                   // exact, fractions too
        Assert.False(t.Cuts[0].Keep);
        Assert.Empty(loaded.Warnings);
    }

    [Fact]
    public void A_recipe_without_transparency_writes_nothing_about_it_and_reads_back_without()
    {
        var (doc, _) = CutOutPicture(new PictureRecipe(80, 60) { Crop = new RectI(10, 10, 40, 30) });
        var bytes = DsmWriter.SaveToBytes(doc);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        var text = new StreamReader(zip.Entries.First(e => e.FullName.EndsWith(".json", StringComparison.Ordinal) && !e.FullName.StartsWith("assets/")).Open()).ReadToEnd();
        Assert.DoesNotContain("transparency", text);
        var back = DsmReader.Load(new MemoryStream(bytes)).Document.AllObjects().OfType<ImageObject>().Single();
        Assert.True(back.Recipe!.Transparency.IsIdentity);
    }

    [Fact]
    public void A_damaged_transparency_entry_does_not_lose_the_picture()
    {
        var (doc, _) = CutOutPicture(CutOutRecipe(80, 60));
        var loaded = DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(doc)));
        var back = loaded.Document.AllObjects().OfType<ImageObject>().Single();
        Assert.NotNull(doc.Assets.Get(back.AssetId));
        Assert.NotNull(back.Recipe);
    }

    // ---- exports ----

    private static ExportOptions Options() => new() { PixelWidth = 288 };

    [Fact]
    public void A_png_export_keeps_the_cut_out_see_through()
    {
        var (doc, _) = CutOutPicture();
        using var bmp = SKBitmap.Decode(DocumentExporter.RenderPng(doc, Options()));
        Assert.Equal(0, bmp.GetPixel(5, 5).Alpha);                              // the white corner is gone
        var blue = bmp.GetPixel(144, 108);
        Assert.Equal(255, blue.Alpha);
        Assert.True(blue.Blue > 150 && blue.Red < 60, $"the blue square stays blue: {blue}");
    }

    [Fact]
    public void A_pdf_export_carries_the_picture_with_its_transparency()
    {
        var (doc, _) = CutOutPicture();
        var pdf = Encoding.Latin1.GetString(DocumentExporter.RenderPdf(doc, Options()));
        Assert.Contains("/Subtype /Image", pdf.Replace("/Subtype/Image", "/Subtype /Image"));
        Assert.Contains("/SMask", pdf);                                            // a soft mask: the see-through parts
    }

    [Fact]
    public void An_svg_export_embeds_the_picture_as_a_png_with_alpha()
    {
        var (doc, _) = CutOutPicture();
        var svg = SvgExporter.Render(doc, Options());
        const string marker = "data:image/png;base64,";
        int at = svg.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at > 0, "the picture is embedded");
        int end = svg.IndexOf('"', at);
        var png = Convert.FromBase64String(svg[(at + marker.Length)..end]);
        using var bmp = SKBitmap.Decode(png);
        Assert.Equal(0, bmp.GetPixel(2, 2).Alpha);
        Assert.Equal(255, bmp.GetPixel(40, 30).Alpha);
    }

    [Fact]
    public void An_eps_export_writes_the_picture_and_keys_out_the_see_through_part()
    {
        var (doc, _) = CutOutPicture();
        var eps = DocumentExporter.Render(doc, ExportFormat.Eps, Options());
        var text = Encoding.Latin1.GetString(eps);
        Assert.StartsWith("%!PS-Adobe", text);
        Assert.True(eps.Length > 600, "the picture is in the file");
        Assert.Contains("MaskColor", text);                                        // EPS has no alpha: the see-through part is keyed out by a color
    }

    [Fact]
    public void An_ai_export_works_too_and_a_jpeg_gets_the_solid_background_where_the_picture_is_see_through()
    {
        var (doc, _) = CutOutPicture();
        Assert.True(DocumentExporter.Render(doc, ExportFormat.Ai, Options()).Length > 500);
        using var jpeg = SKBitmap.Decode(DocumentExporter.RenderJpeg(doc, Options()));
        var corner = jpeg.GetPixel(5, 5);
        Assert.True(corner.Red > 240 && corner.Green > 240 && corner.Blue > 240, $"white where nothing is drawn: {corner}");
    }

    [Fact]
    public void Rotated_and_cropped_cut_outs_export_the_finished_picture_not_the_original()
    {
        var recipe = new PictureRecipe(80, 60)
        {
            Crop = new RectI(20, 15, 40, 30),
            Transparency = new PictureTransparency { Colors = new ValueList<int>([0xFFFFFF]), Cuts = new ValueList<CutShape>([new CutShape(new ValueList<Vec2>([new Vec2(20, 15), new Vec2(60, 15), new Vec2(60, 25), new Vec2(20, 25)]), Keep: true)]) },
        };
        var (doc, image) = CutOutPicture(recipe);
        Assert.Equal((40, 30), (image.PixelWidth, image.PixelHeight));
        using var bmp = SKBitmap.Decode(DocumentExporter.RenderPng(doc, Options()));
        Assert.Equal(255, bmp.GetPixel(144, 40).Alpha);                         // the top part of the kept area
        Assert.Equal(0, bmp.GetPixel(144, 200).Alpha);                          // the lower part was cut away
    }
}
