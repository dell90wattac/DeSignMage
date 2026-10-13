using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using DeSignMage.Rendering.Export;
using SkiaSharp;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>PNG, JPEG and AI exports, and the typed pixel size (width, height or both, following the aspect ratio of what is exported).</summary>
public class PictureExportTests
{
    private static Document NewDoc(double inchesW = 3, double inchesH = 2) => new(PageSetup.FromInches(inchesW, inchesH));

    private static T Add<T>(Document doc, T o) where T : DocObject
    {
        doc.Insert(doc.Layers[0], -1, o);
        return o;
    }

    private static ObjectStyle Fill(DocColor c) => ObjectStyle.FillOnly(c);

    private static SKBitmap Decode(byte[] bytes) => SKBitmap.Decode(bytes) ?? throw new InvalidOperationException("did not decode");

    // ---------------------------------------------------------------- typed pixel size

    [Fact]
    public void A_typed_width_gives_the_height_that_keeps_the_page_proportions()
    {
        var doc = NewDoc(3, 2);   // 3:2

        Assert.Equal((900, 600), DocumentExporter.PngSize(doc, new ExportOptions { PixelWidth = 900 }));
        Assert.Equal((1000, 667), DocumentExporter.PngSize(doc, new ExportOptions { PixelWidth = 1000 }));
        Assert.Equal((300, 200), DocumentExporter.PngSize(doc, new ExportOptions { PixelHeight = 200 }));
        Assert.Equal((1500, 1000), DocumentExporter.PngSize(doc, new ExportOptions { PixelHeight = 1000 }));
        Assert.Equal((3200, 3200), DocumentExporter.PngSize(doc, new ExportOptions { PixelWidth = 3200, PixelHeight = 3200 }));   // both: as typed
        Assert.Equal((900, 600), DocumentExporter.PngSize(doc, new ExportOptions { Dpi = 300 }));                                  // neither: the dpi
    }

    [Fact]
    public void For_a_selection_the_proportions_are_those_of_the_selection_with_its_stroke_and_its_effects()
    {
        var doc = NewDoc(10, 10);
        var box = Add(doc, new RectangleObject(100, 50) { Transform = Affine2.Translation(10, 10), Style = new ObjectStyle(new SolidPaint(DocColor.Black), StrokeStyle.Solid(DocColor.Black, 10)) });   // 110 x 60 with its stroke
        var other = Add(doc, new EllipseObject(30, 30) { Transform = Affine2.Translation(300, 300), Style = Fill(DocColor.Black) });
        var shadow = Add(doc, new PathObject(PathGeometry.Rectangle(new Rect2(0, 0, 100, 50))) { Transform = Affine2.Translation(100, 200), Style = new ObjectStyle(new SolidPaint(DocColor.Black), null, new BlurStyle(SoftKind.SoftShadow, 1, 1, 50)) });
        var five = new ExportOptions { Scope = ExportScope.Selection, Selection = [box, other] };

        Assert.Equal((1100, 600), DocumentExporter.PngSize(doc, new ExportOptions { Scope = ExportScope.Selection, Selection = [box], PixelWidth = 1100 }));
        // box and ellipse together: from x = 5 to x = 330, y = 5 to y = 330
        Assert.Equal((3200, 3200), DocumentExporter.PngSize(doc, five with { PixelWidth = 3200 }));
        // the shadow reaches 3 sigma (3 x 0.068 x 50 = 10.2) beyond its outline on every side
        var (w, h) = DocumentExporter.PngSize(doc, new ExportOptions { Scope = ExportScope.Selection, Selection = [shadow], PixelWidth = 1000 });
        Assert.Equal(1000, w);
        Assert.Equal((int)Math.Round(1000 * (50 + 20.4) / (100 + 20.4)), h);
    }

    [Fact]
    public void Too_small_or_too_large_a_size_is_refused_with_a_plain_message()
    {
        var doc = NewDoc();

        Assert.Throws<ExportException>(() => DocumentExporter.PngSize(doc, new ExportOptions { PixelWidth = 0 }));
        Assert.Throws<ExportException>(() => DocumentExporter.PngSize(doc, new ExportOptions { PixelWidth = -5, PixelHeight = 10 }));
        var tooWide = Assert.Throws<ExportException>(() => DocumentExporter.PngSize(doc, new ExportOptions { PixelWidth = DocumentExporter.MaxPixelEdge + 1 }));
        Assert.Contains("too large", tooWide.Message);
        Assert.Throws<ExportException>(() => DocumentExporter.PngSize(doc, new ExportOptions { PixelWidth = 20000, PixelHeight = 20000 }));   // 400 million pixels
        Assert.Equal((DocumentExporter.MaxPixelEdge, 8000), DocumentExporter.PngSize(doc, new ExportOptions { PixelWidth = DocumentExporter.MaxPixelEdge, PixelHeight = 8000 }));   // 240 million: allowed
        Assert.Throws<ExportException>(() => DocumentExporter.RenderJpeg(doc, new ExportOptions { PixelWidth = DocumentExporter.MaxPixelEdge + 1 }));
    }

    // ---------------------------------------------------------------- PNG

    [Fact]
    public void A_png_of_a_typed_size_has_that_size_real_alpha_and_the_dpi_of_that_size()
    {
        var doc = NewDoc(3, 2);
        Add(doc, new RectangleObject(72, 36) { Transform = Affine2.Translation(36, 36), Style = Fill(new DocColor(255, 0, 0)) });

        var png = DocumentExporter.RenderPng(doc, new ExportOptions { PixelWidth = 648 });

        using var bmp = Decode(png);
        Assert.Equal((648, 432), (bmp.Width, bmp.Height));
        Assert.Equal(0, bmp.GetPixel(2, 2).Alpha);                          // nothing drawn: no color at all
        Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(200, 200));       // 3 px per point
        // pHYs: 648 px over 3 in = 216 dpi = 8504 per metre
        int at = 8;
        int perMeter = -1;
        while (at < png.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            if (Encoding.ASCII.GetString(png, at + 4, 4) == "pHYs") perMeter = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8));
            at += 12 + (int)len;
        }
        Assert.Equal(8504, perMeter);
    }

    [Fact]
    public void Png_compression_changes_the_file_size_never_the_picture()
    {
        var doc = NewDoc(3, 2);
        var rng = new Random(3);
        for (int i = 0; i < 60; i++)
            Add(doc, new EllipseObject(10 + rng.Next(40), 10 + rng.Next(40)) { Transform = Affine2.Translation(rng.Next(200), rng.Next(120)), Style = Fill(new DocColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256))) });

        var fast = DocumentExporter.RenderPng(doc, new ExportOptions { PixelWidth = 1200, PngCompression = 0 });
        var small = DocumentExporter.RenderPng(doc, new ExportOptions { PixelWidth = 1200, PngCompression = 9 });

        Assert.True(fast.Length > small.Length * 1.5, $"{fast.Length} vs {small.Length}");
        using var a = Decode(fast);
        using var b = Decode(small);
        Assert.Equal(a.Bytes, b.Bytes);
        Assert.Throws<ExportException>(() => DocumentExporter.RenderPng(doc, new ExportOptions { PngCompression = 10 }));
        Assert.Throws<ExportException>(() => DocumentExporter.RenderPng(doc, new ExportOptions { PngCompression = -1 }));
    }

    // ---------------------------------------------------------------- JPEG

    private static Document Colorful()
    {
        var doc = NewDoc(3, 2);
        Add(doc, new RectangleObject(100, 100) { Transform = Affine2.Translation(20, 20), Style = Fill(new DocColor(220, 30, 30)) });
        Add(doc, new EllipseObject(90, 90) { Transform = Affine2.Translation(110, 30), Style = new ObjectStyle(new SolidPaint(new DocColor(30, 90, 220)), StrokeStyle.Solid(new DocColor(250, 220, 0), 6)) });
        Add(doc, new TextObject("JPEG 123", "Arial", 36) { Transform = Affine2.Translation(20, 135), Style = Fill(DocColor.Black) });
        return doc;
    }

    [Fact]
    public void A_jpeg_has_the_typed_size_a_white_page_and_the_right_colors()
    {
        var doc = Colorful();

        var jpeg = DocumentExporter.RenderJpeg(doc, new ExportOptions { PixelWidth = 648, JpegQuality = 95 });

        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, jpeg[..3]);
        using var bmp = Decode(jpeg);
        Assert.Equal((648, 432), (bmp.Width, bmp.Height));
        Assert.Equal(SKAlphaType.Opaque, bmp.AlphaType);
        var corner = bmp.GetPixel(5, 5);                                     // nothing is drawn there: JPEG cannot be transparent, so it is white
        Assert.True(corner.Red >= 253 && corner.Green >= 253 && corner.Blue >= 253, corner.ToString());
        var red = bmp.GetPixel(150, 150);                                    // the red square
        Assert.True(Math.Abs(red.Red - 220) < 6 && Math.Abs(red.Green - 30) < 6 && Math.Abs(red.Blue - 30) < 6, red.ToString());
    }

    [Fact]
    public void A_lower_quality_makes_a_smaller_and_less_exact_file()
    {
        var doc = Colorful();
        var best = DocumentExporter.RenderJpeg(doc, new ExportOptions { PixelWidth = 900, JpegQuality = 100 });
        var middle = DocumentExporter.RenderJpeg(doc, new ExportOptions { PixelWidth = 900, JpegQuality = 70 });
        var worst = DocumentExporter.RenderJpeg(doc, new ExportOptions { PixelWidth = 900, JpegQuality = 5 });
        using var transparent = Decode(DocumentExporter.RenderPng(doc, new ExportOptions { PixelWidth = 900 }));
        using var reference = new SKBitmap(transparent.Width, transparent.Height);   // the same on white, which is what a JPEG shows
        using (var c = new SKCanvas(reference)) { c.Clear(SKColors.White); c.DrawBitmap(transparent, 0, 0); }

        Assert.True(best.Length > middle.Length && middle.Length > worst.Length, $"{best.Length} > {middle.Length} > {worst.Length}");
        double ErrorOf(byte[] jpeg)
        {
            using var b = Decode(jpeg);
            double sum = 0;
            for (int y = 0; y < b.Height; y += 2)
                for (int x = 0; x < b.Width; x += 2)
                {
                    var p = b.GetPixel(x, y); var q = reference.GetPixel(x, y);
                    sum += Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);
                }
            return sum / (b.Width / 2 * (b.Height / 2) * 3.0);
        }
        Assert.True(ErrorOf(best) < ErrorOf(middle));
        Assert.True(ErrorOf(middle) < ErrorOf(worst));
        Assert.True(ErrorOf(best) < 1.5);
    }

    [Fact]
    public void A_jpeg_quality_outside_one_to_a_hundred_is_refused()
    {
        var doc = Colorful();
        Assert.Throws<ExportException>(() => DocumentExporter.RenderJpeg(doc, new ExportOptions { JpegQuality = 0 }));
        Assert.Throws<ExportException>(() => DocumentExporter.RenderJpeg(doc, new ExportOptions { JpegQuality = 101 }));
        Assert.NotEmpty(DocumentExporter.RenderJpeg(doc, new ExportOptions { PixelWidth = 100, JpegQuality = 1 }));
        Assert.NotEmpty(DocumentExporter.RenderJpeg(doc, new ExportOptions { PixelWidth = 100, JpegQuality = 100 }));
    }

    [Fact]
    public void A_jpeg_records_its_density_so_it_opens_at_the_physical_size()
    {
        var doc = NewDoc(3, 2);
        Add(doc, new RectangleObject(30, 30) { Style = Fill(DocColor.Black) });

        var jpeg = DocumentExporter.RenderJpeg(doc, new ExportOptions { PixelWidth = 450 });   // 150 dpi

        Assert.Equal(1, jpeg[13]);                                                              // units: dots per inch
        Assert.Equal(150, BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(14)));
        Assert.Equal(150, BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(16)));
        using var _ = Decode(jpeg);
    }

    [Fact]
    public void A_soft_shadow_in_a_jpeg_is_on_white_and_in_a_png_it_keeps_its_alpha()
    {
        var doc = NewDoc(3, 2);
        Add(doc, new PathObject(PathGeometry.Rectangle(new Rect2(0, 0, 60, 40))) { Transform = Affine2.Translation(80, 50), Style = new ObjectStyle(new SolidPaint(DocColor.Black), null, new BlurStyle(SoftKind.SoftShadow, 1, 0.8, 40)) });

        using var jpeg = Decode(DocumentExporter.RenderJpeg(doc, new ExportOptions { PixelWidth = 432, JpegQuality = 95 }));
        using var png = Decode(DocumentExporter.RenderPng(doc, new ExportOptions { PixelWidth = 432 }));

        Assert.True(jpeg.GetPixel(10, 10).Red > 250);
        Assert.Equal(0, png.GetPixel(10, 10).Alpha);
        var edge = png.GetPixel(80 * 2, 70 * 2);                                                 // inside the shadow's blurred edge region
        Assert.InRange(edge.Alpha, 1, 254);
    }

    [Fact]
    public void A_jpeg_fills_the_corners_a_selection_leaves_with_the_chosen_background_and_soft_edges_blend_into_it()
    {
        var doc = NewDoc(4, 3);
        var shadow = Add(doc, new PathObject(PathGeometry.Ellipse(new Rect2(0, 0, 120, 120))) { Transform = Affine2.Translation(60, 30), Style = new ObjectStyle(new SolidPaint(DocColor.Black), null, new BlurStyle(SoftKind.SoftShadow, 1, 0.6, 120)) });
        var round = Add(doc, new EllipseObject(120, 120) { Transform = Affine2.Translation(60, 30), Style = Fill(new DocColor(255, 255, 255)) });   // curved edges: the selection's box has empty corners
        var options = new ExportOptions { Scope = ExportScope.Selection, Selection = [round, shadow], PixelWidth = 600, JpegQuality = 100, Background = new DocColor(20, 40, 120) };

        using var bmp = Decode(DocumentExporter.RenderJpeg(doc, options));

        var corner = bmp.GetPixel(2, 2);                                             // uncovered: the chosen color
        Assert.True(Math.Abs(corner.Red - 20) < 8 && Math.Abs(corner.Green - 40) < 8 && Math.Abs(corner.Blue - 120) < 8, corner.ToString());
        var far = bmp.GetPixel(bmp.Width - 3, bmp.Height - 3);
        Assert.True(Math.Abs(far.Blue - 120) < 8, far.ToString());
        // the halo of the shadow blends into the dark blue: no pixel along the shadow's soft ring is whitish
        int whitish = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                double d = Math.Sqrt(Math.Pow(x - bmp.Width / 2.0, 2) + Math.Pow(y - bmp.Height / 2.0, 2));
                if (d > bmp.Width * 0.43 && p.Red > 120 && p.Green > 120 && p.Blue > 160) whitish++;   // outside the white disc (its radius is 0.40 of the width): nothing light
            }
        Assert.Equal(0, whitish);
        // the white disc itself is still white
        var middle = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        Assert.True(middle.Red > 235 && middle.Green > 235, middle.ToString());
    }

    [Fact]
    public void The_default_jpeg_background_is_white_and_a_png_ignores_the_option()
    {
        var doc = NewDoc();
        Add(doc, new RectangleObject(30, 30) { Style = Fill(DocColor.Black) });

        using var jpeg = Decode(DocumentExporter.RenderJpeg(doc, new ExportOptions { PixelWidth = 216, JpegQuality = 100 }));
        using var png = Decode(DocumentExporter.RenderPng(doc, new ExportOptions { PixelWidth = 216, Background = new DocColor(255, 0, 0) }));

        Assert.True(jpeg.GetPixel(150, 100).Red >= 250 && jpeg.GetPixel(150, 100).Blue >= 250);
        Assert.Equal(0, png.GetPixel(150, 100).Alpha);                              // a PNG stays transparent whatever the background says
    }

    [Fact]
    public void A_file_with_an_old_opaque_white_thumbnail_still_opens_and_keeps_it()
    {
        using var white = new SKBitmap(8, 4);
        white.Erase(SKColors.White);   // what files saved before the thumbnail became transparent hold
        using var data = SKImage.FromBitmap(white).Encode(SKEncodedImageFormat.Png, 100);
        var oldThumbnail = data.ToArray();
        var doc = new Document(PageSetup.FromInches(2, 1));
        Add(doc, new RectangleObject(10, 10) { Style = Fill(DocColor.Black) });

        var loaded = DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(doc, new DsmSaveOptions { Thumbnail = oldThumbnail })));

        Assert.Equal(1, loaded.Document.ObjectCount);
        Assert.Equal(oldThumbnail, loaded.Thumbnail);
        Assert.Equal(255, SKBitmap.Decode(loaded.Thumbnail!).GetPixel(1, 1).Alpha);
    }
    // ---------------------------------------------------------------- AI

    [Fact]
    public void An_ai_file_is_a_pdf_with_the_export_areas_page_and_its_vectors()
    {
        var doc = Colorful();

        var ai = DocumentExporter.RenderAi(doc, new ExportOptions { Title = "Sign" });
        var text = Encoding.Latin1.GetString(ai);

        Assert.StartsWith("%PDF-", text);
        Assert.Contains("%%EOF", text[^64..]);
        Assert.Matches(new Regex(@"/MediaBox\s*\[\s*0\s+0\s+216\s+144\s*\]"), text);
        Assert.Single(Regex.Matches(text, @"/Type\s*/Page\b"));
        Assert.Contains("Sign", text);
        Assert.Equal(DocumentExporter.RenderPdf(doc, new ExportOptions { Title = "Sign" }).Length, ai.Length);
    }

    // ---------------------------------------------------------------- formats by extension

    [Theory]
    [InlineData("a.png", ExportFormat.Png)]
    [InlineData("a.JPG", ExportFormat.Jpeg)]
    [InlineData("a.jpeg", ExportFormat.Jpeg)]
    [InlineData("a.pdf", ExportFormat.Pdf)]
    [InlineData("a.svg", ExportFormat.Svg)]
    [InlineData("a.eps", ExportFormat.Eps)]
    [InlineData("a.Ai", ExportFormat.Ai)]
    public void Every_format_is_found_by_its_extension_and_written(string name, ExportFormat format)
    {
        Assert.Equal(format, DocumentExporter.FormatOf(name));
        var dir = Directory.CreateTempSubdirectory("dsm-export-").FullName;
        try
        {
            var doc = Colorful();
            var path = Path.Combine(dir, name);
            DocumentExporter.Export(doc, path, new ExportOptions { PixelWidth = 200 });
            Assert.True(new FileInfo(path).Length > 200);
        }
        finally { Directory.Delete(dir, true); }
        Assert.Null(DocumentExporter.FormatOf("a.tiff"));
    }
}
