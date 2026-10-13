using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Rendering.Export;
using SkiaSharp;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>File ▸ Export: SVG, PDF and PNG.</summary>
public class ExportTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";

    private static readonly DocColor Red = new(255, 0, 0, 255, "Oracal 651-031 Red");

    private static (Document Doc, RectangleObject Red, EllipseObject Pasteboard, RectangleObject Hidden) Simple()
    {
        var doc = new Document(PageSetup.FromInches(3, 2));    // 216 x 144 pt
        var layer = doc.Layers[0];
        var red = new RectangleObject(72, 36) { Name = "red box", Transform = Affine2.Translation(36, 36), Style = ObjectStyle.FillOnly(Red) };
        doc.Insert(layer, -1, red);
        var pasteboard = new EllipseObject(40, 40) { Transform = Affine2.Translation(400, 400), Style = ObjectStyle.FillOnly(new DocColor(0, 0, 255)) };
        doc.Insert(layer, -1, pasteboard);
        var hiddenLayer = new Layer("Notes") { Printable = false };
        doc.InsertLayer(1, hiddenLayer);
        var hidden = new RectangleObject(30, 30) { Transform = Affine2.Translation(150, 90), Style = ObjectStyle.FillOnly(new DocColor(0, 200, 0)) };
        doc.Insert(hiddenLayer, -1, hidden);
        new AddGuideCommand(Guide.Vertical(20)).Do(doc);
        new AddGuideCommand(Guide.Horizontal(130)).Do(doc);
        return (doc, red, pasteboard, hidden);
    }

    private static XDocument ParseSvg(Document doc, ExportOptions? o = null) => XDocument.Parse(SvgExporter.Render(doc, o ?? new ExportOptions()));

    private static SKBitmap Decode(byte[] png) => SKBitmap.Decode(png) ?? throw new InvalidOperationException("PNG did not decode");

    // ---------------------------------------------------------------- SVG

    [Fact]
    public void Svg_is_valid_xml_sized_in_inches_with_the_page_as_view_box()
    {
        var (doc, _, _, _) = Simple();

        var svg = ParseSvg(doc);

        Assert.Equal(Svg + "svg", svg.Root!.Name);
        Assert.Equal("3in", svg.Root.Attribute("width")!.Value);
        Assert.Equal("2in", svg.Root.Attribute("height")!.Value);
        Assert.Equal("0 0 216 144", svg.Root.Attribute("viewBox")!.Value);
    }

    [Fact]
    public void Svg_colors_are_exact_and_vinyl_names_are_kept()
    {
        var (doc, _, _, _) = Simple();

        var path = Assert.Single(ParseSvg(doc).Descendants(Svg + "path"));

        Assert.Equal("#FF0000", path.Attribute("fill")!.Value);
        Assert.Equal("Oracal 651-031 Red", path.Attribute("data-fill-name")!.Value);
        Assert.Equal("red box", path.Attribute("data-name")!.Value);
        Assert.Equal("M36 36L108 36L108 72L36 72Z", path.Attribute("d")!.Value);
    }

    [Fact]
    public void Svg_alpha_and_stroke_styles_are_written_exactly()
    {
        var doc = new Document(PageSetup.FromInches(1, 1));
        doc.Insert(doc.Layers[0], -1, new RectangleObject(10, 10)
        {
            Style = new ObjectStyle(new SolidPaint(new DocColor(1, 2, 3, 128)),
                new StrokeStyle(new SolidPaint(new DocColor(250, 251, 252, 64, "Edge")), 2.5, LineJoin.Round, LineCap.Square, 7)),
        });

        var p = Assert.Single(ParseSvg(doc).Descendants(Svg + "path"));

        Assert.Equal("#010203", p.Attribute("fill")!.Value);
        Assert.Equal(128, (int)Math.Round(double.Parse(p.Attribute("fill-opacity")!.Value, CultureInfo.InvariantCulture) * 255));
        Assert.Equal("#FAFBFC", p.Attribute("stroke")!.Value);
        Assert.Equal(64, (int)Math.Round(double.Parse(p.Attribute("stroke-opacity")!.Value, CultureInfo.InvariantCulture) * 255));
        Assert.Equal("Edge", p.Attribute("data-stroke-name")!.Value);
        Assert.Equal("2.5", p.Attribute("stroke-width")!.Value);
        Assert.Equal("round", p.Attribute("stroke-linejoin")!.Value);
        Assert.Equal("square", p.Attribute("stroke-linecap")!.Value);
    }

    [Fact]
    public void Pasteboard_objects_and_non_printable_layers_are_left_out_and_guides_never_appear()
    {
        var (doc, _, _, _) = Simple();

        var svg = ParseSvg(doc);

        Assert.Single(svg.Descendants(Svg + "path"));                       // only the red box
        Assert.Single(svg.Descendants(Svg + "g"), g => g.Attribute("data-layer") is not null);
        Assert.DoesNotContain("guide", svg.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(svg.Descendants(Svg + "line"));
    }

    [Fact]
    public void Including_the_pasteboard_adds_those_objects_and_grows_the_view_box()
    {
        var (doc, _, _, _) = Simple();

        var svg = ParseSvg(doc, new ExportOptions { IncludePasteboard = true });

        Assert.Equal(2, svg.Descendants(Svg + "path").Count());
        Assert.Equal("0 0 440 440", svg.Root!.Attribute("viewBox")!.Value);
    }

    [Fact]
    public void Selection_export_covers_exactly_the_selected_objects()
    {
        var (doc, red, pasteboard, _) = Simple();
        red.Style = new ObjectStyle(red.Style.Fill, StrokeStyle.Solid(DocColor.Black, 4));

        var svg = ParseSvg(doc, new ExportOptions { Scope = ExportScope.Selection, Selection = [red] });

        Assert.Single(svg.Descendants(Svg + "path"));
        Assert.Equal("34 34 76 40", svg.Root!.Attribute("viewBox")!.Value);   // box + half the stroke on each side

        var onBoard = ParseSvg(doc, new ExportOptions { Scope = ExportScope.Selection, Selection = [pasteboard] });
        Assert.Equal("400 400 40 40", onBoard.Root!.Attribute("viewBox")!.Value);   // off-page objects export when chosen explicitly

        Assert.Throws<ExportException>(() => SvgExporter.Render(doc, new ExportOptions { Scope = ExportScope.Selection }));
    }

    [Fact]
    public void Svg_keeps_layers_groups_names_arcs_combined_objects_and_bitmaps()
    {
        var doc = SampleDocuments.Everything();

        var svg = ParseSvg(doc);

        Assert.Equal(2, svg.Descendants(Svg + "g").Count(g => g.Attribute("data-layer") is not null));   // the non-printable layer is out
        Assert.Contains(svg.Descendants(Svg + "g"), g => g.Attribute("data-name")?.Value == "outer");
        Assert.Contains(svg.Descendants(Svg + "path"), p => p.Attribute("data-name")?.Value == "Panel \"A\" & <B>");
        Assert.Contains(svg.Descendants(Svg + "path"), p => p.Attribute("d")!.Value.Contains('A'));   // a real arc, not a polyline
        Assert.Single(svg.Descendants(Svg + "clipPath"));                                         // combined: fills clipped to the knocked-out area
        Assert.Equal("evenodd", svg.Descendants(Svg + "clipPath").Single().Element(Svg + "path")!.Attribute("clip-rule")!.Value);
        Assert.Empty(svg.Descendants(Svg + "text"));                                              // text as outlines by default

        var image = Assert.Single(svg.Descendants(Svg + "image"));
        var href = image.Attribute(Xlink + "href")!.Value;
        Assert.StartsWith("data:image/png;base64,", href);
        Assert.Equal(SampleDocuments.TinyPng(), Convert.FromBase64String(href["data:image/png;base64,".Length..]));
    }

    [Fact]
    public void Svg_arcs_cover_the_right_part_of_the_circle()
    {
        // A half circle from (0,0) to (20,0) bulging up, radius 10, must come out as arcs with radius 10 and the sweep the model has.
        var doc = new Document(PageSetup.FromInches(1, 1));
        doc.Insert(doc.Layers[0], -1, new PathObject(new PathBuilder().MoveTo(0, 0).ArcTo(new Vec2(10, -10), new Vec2(20, 0)).Build())
        {
            Style = new ObjectStyle(null, StrokeStyle.Solid(DocColor.Black, 1)),
        });

        var d = Assert.Single(ParseSvg(doc).Descendants(Svg + "path")).Attribute("d")!.Value;

        Assert.Matches(new Regex(@"^M0 0A10 10 0 0 1 \S+ \S+A10 10 0 0 1 20 0$"), d);   // clockwise on screen (y down), in two halves of 90 degrees
    }

    [Fact]
    public void Live_text_can_be_written_as_text_with_every_letter_placed()
    {
        var doc = SampleDocuments.Everything();
        var headline = doc.AllObjects().OfType<TextObject>().Single(t => t.Name == "headline");

        var svg = ParseSvg(doc, new ExportOptions { TextAsOutlines = false });

        var text = svg.Descendants(Svg + "text").Single(t => t.Attribute("data-name")?.Value == "headline");
        Assert.Equal("700", text.Attribute("font-weight")!.Value);
        Assert.Equal("italic", text.Attribute("font-style")!.Value);
        Assert.Contains("Segoe UI", text.Attribute("font-family")!.Value);
        Assert.Equal("#F2B705", text.Attribute("fill")!.Value);
        var spans = text.Elements(Svg + "tspan").ToList();
        Assert.Equal(20, spans.Count);                     // "Kerned Text" + "second line" without spaces
        var letters = headline.Layout.Letters.Where(l => !l.IsBreak && !string.IsNullOrWhiteSpace(headline.Text.Substring(l.Start, l.Length))).ToList();
        for (int i = 0; i < spans.Count; i++)
        {
            Assert.Equal(headline.Text.Substring(letters[i].Start, letters[i].Length), spans[i].Value);
            Assert.Equal(letters[i].X, double.Parse(spans[i].Attribute("x")!.Value, CultureInfo.InvariantCulture), 4);
            Assert.Equal(letters[i].Baseline, double.Parse(spans[i].Attribute("y")!.Value, CultureInfo.InvariantCulture), 4);
        }
        // the missing font names its fallback so other programs can substitute sensibly
        var missing = svg.Descendants(Svg + "text").Single(t => t.Value.Contains("Missing"));
        Assert.Contains("Zzyzx Display Not Installed", missing.Attribute("font-family")!.Value);
    }

    [Fact]
    public void Invisible_objects_and_layers_are_not_exported()
    {
        var doc = SampleDocuments.Everything();
        doc.Layers[2].Visible = false;

        var svg = ParseSvg(doc);

        Assert.Empty(svg.Descendants(Svg + "image"));
        Assert.Single(svg.Descendants(Svg + "g"), g => g.Attribute("data-layer") is not null);
        Assert.DoesNotContain(svg.Descendants(Svg + "path"), p => p.Attribute("fill")?.Value == "#00AA55");
    }

    // ---------------------------------------------------------------- PDF

    private static string PdfText(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    [Fact]
    public void Pdf_is_a_real_vector_document_with_the_page_size()
    {
        var (doc, _, _, _) = Simple();

        var pdf = DocumentExporter.RenderPdf(doc, new ExportOptions());

        var text = PdfText(pdf);
        Assert.StartsWith("%PDF-", text);
        Assert.True(pdf.Length > 500);
        Assert.Contains("%%EOF", text[^64..]);
        Assert.Matches(new Regex(@"/MediaBox\s*\[\s*0\s+0\s+216\s+144\s*\]"), text);
        Assert.Single(Regex.Matches(text, @"/Type\s*/Page\b"));
    }

    [Fact]
    public void Pdf_of_a_busy_document_with_text_images_and_combined_shapes_is_produced()
    {
        var doc = SampleDocuments.Everything();

        var pdf = DocumentExporter.RenderPdf(doc, new ExportOptions { Title = "Everything" });

        Assert.True(pdf.Length > 2000);
        Assert.Contains("Everything", PdfText(pdf));
        Assert.Matches(new Regex(@"/MediaBox\s*\[\s*0\s+0\s+1728\s+864\s*\]"), PdfText(pdf));
    }

    [Fact]
    public void Pdf_of_the_selection_has_the_selections_size()
    {
        var (doc, red, _, _) = Simple();

        var pdf = DocumentExporter.RenderPdf(doc, new ExportOptions { Scope = ExportScope.Selection, Selection = [red] });

        Assert.Matches(new Regex(@"/MediaBox\s*\[\s*0\s+0\s+72\s+36\s*\]"), PdfText(pdf));
    }

    // ---------------------------------------------------------------- PNG

    [Theory]
    [InlineData(72, 216, 144)]
    [InlineData(100, 300, 200)]
    [InlineData(300, 900, 600)]
    [InlineData(600, 1800, 1200)]
    public void Png_size_is_the_page_in_inches_times_the_dpi(double dpi, int width, int height)
    {
        var (doc, _, _, _) = Simple();

        var png = DocumentExporter.RenderPng(doc, new ExportOptions { Dpi = dpi });

        using var bmp = Decode(png);
        Assert.Equal((width, height), (bmp.Width, bmp.Height));
        Assert.Equal((width, height), DocumentExporter.PngSize(doc, new ExportOptions { Dpi = dpi }));
    }

    [Fact]
    public void Png_stores_the_dpi_so_other_programs_open_it_at_the_right_physical_size()
    {
        var (doc, _, _, _) = Simple();

        var png = DocumentExporter.RenderPng(doc, new ExportOptions { Dpi = 150 });

        var pos = 8;
        int? perMeter = null;
        while (pos < png.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "pHYs")
            {
                Assert.Equal(BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8)), BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 12)));
                perMeter = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8));
                Assert.Equal(1, png[pos + 16]);
            }
            pos += 12 + (int)len;
        }
        Assert.Equal(5906, perMeter);   // 150 dpi = 5905.5 dots per metre
        using var bmp = Decode(png);    // and the file is still a valid PNG
        Assert.Equal(450, bmp.Width);
    }

    [Fact]
    public void Png_draws_the_design_in_exact_colors_on_white_or_transparent()
    {
        var (doc, _, _, _) = Simple();

        using var white = Decode(DocumentExporter.RenderPng(doc, new ExportOptions { Dpi = 72 }));
        Assert.Equal(new SKColor(255, 0, 0, 255), white.GetPixel(72, 54));          // inside the red box
        Assert.Equal(new SKColor(255, 255, 255, 255), white.GetPixel(5, 5));         // page background
        Assert.Equal(new SKColor(255, 255, 255, 255), white.GetPixel(20, 100));      // where the vertical guide is: guides never print
        Assert.Equal(new SKColor(255, 255, 255, 255), white.GetPixel(100, 130));     // ...and the horizontal one
        Assert.Equal(new SKColor(255, 255, 255, 255), white.GetPixel(165, 105));     // the non-printable layer's box is not drawn

        using var clear = Decode(DocumentExporter.RenderPng(doc, new ExportOptions { Dpi = 72, TransparentBackground = true }));
        Assert.Equal(0, clear.GetPixel(5, 5).Alpha);
        Assert.Equal(new SKColor(255, 0, 0, 255), clear.GetPixel(72, 54));
    }

    [Fact]
    public void Png_selection_is_cropped_to_the_selected_objects_and_pasteboard_can_be_included()
    {
        var (doc, red, _, _) = Simple();

        using var selection = Decode(DocumentExporter.RenderPng(doc, new ExportOptions { Scope = ExportScope.Selection, Selection = [red], Dpi = 144 }));
        Assert.Equal((144, 72), (selection.Width, selection.Height));                    // 72 x 36 pt at 2 px/pt
        Assert.Equal(new SKColor(255, 0, 0, 255), selection.GetPixel(2, 2));              // fully covered by the box

        using var all = Decode(DocumentExporter.RenderPng(doc, new ExportOptions { IncludePasteboard = true, Dpi = 72 }));
        Assert.Equal((440, 440), (all.Width, all.Height));
        Assert.Equal(new SKColor(0, 0, 255, 255), all.GetPixel(420, 420));                // the pasteboard ellipse is there now
    }

    [Fact]
    public void Png_of_a_page_with_a_fractional_size_rounds_up_and_a_huge_image_is_refused()
    {
        var doc = new Document(PageSetup.FromInches(1.5, 1));
        Assert.Equal((150, 100), DocumentExporter.PngSize(doc, new ExportOptions { Dpi = 100 }));
        Assert.Equal((151, 101), DocumentExporter.PngSize(doc, new ExportOptions { Dpi = 100.5 }));

        var big = new Document(PageSetup.FromInches(100, 100));
        Assert.Throws<ExportException>(() => DocumentExporter.RenderPng(big, new ExportOptions { Dpi = 600 }));
        Assert.Throws<ExportException>(() => DocumentExporter.RenderPng(doc, new ExportOptions { Dpi = 0 }));
        Assert.Throws<ExportException>(() => DocumentExporter.RenderPng(doc, new ExportOptions { Dpi = double.NaN }));
    }

    [Fact]
    public void Png_of_the_busy_document_draws_something_everywhere_it_should()
    {
        var doc = SampleDocuments.Everything();

        using var bmp = Decode(DocumentExporter.RenderPng(doc, new ExportOptions { Dpi = 36 }));

        Assert.Equal((864, 432), (bmp.Width, bmp.Height));
        int dark = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) != SKColors.White) dark++;
        Assert.True(dark > 500, $"only {dark} non-white pixels");
    }

    // ---------------------------------------------------------------- files

    [Fact]
    public void Export_picks_the_format_from_the_extension()
    {
        var (doc, _, _, _) = Simple();
        var dir = Directory.CreateTempSubdirectory("dsm-export-").FullName;
        try
        {
            DocumentExporter.Export(doc, Path.Combine(dir, "a.svg"), new ExportOptions());
            DocumentExporter.Export(doc, Path.Combine(dir, "a.PDF"), new ExportOptions());
            DocumentExporter.Export(doc, Path.Combine(dir, "a.png"), new ExportOptions { Dpi = 50 });

            Assert.StartsWith("<?xml", File.ReadAllText(Path.Combine(dir, "a.svg")));
            Assert.StartsWith("%PDF", File.ReadAllText(Path.Combine(dir, "a.PDF"), Encoding.Latin1));
            using var bmp = Decode(File.ReadAllBytes(Path.Combine(dir, "a.png")));
            Assert.Equal(150, bmp.Width);
            Assert.Throws<ExportException>(() => DocumentExporter.Export(doc, Path.Combine(dir, "a.tiff"), new ExportOptions()));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Exporting_does_not_change_the_document()
    {
        var doc = SampleDocuments.Everything();
        var before = DeSignMage.Formats.Native.DsmWriter.SaveToBytes(doc, new() { SavedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        long revision = doc.Revision;

        SvgExporter.Render(doc, new ExportOptions());
        DocumentExporter.RenderPdf(doc, new ExportOptions());
        DocumentExporter.RenderPng(doc, new ExportOptions { Dpi = 30 });

        Assert.Equal(revision, doc.Revision);
        var after = DeSignMage.Formats.Native.DsmWriter.SaveToBytes(doc, new() { SavedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        Assert.Equal(Zip.DocumentJson(before), Zip.DocumentJson(after));
    }
}

