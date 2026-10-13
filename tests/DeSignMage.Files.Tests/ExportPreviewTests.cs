using System.Diagnostics;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Rendering.Export;
using SkiaSharp;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>The picture the Export dialog shows: the same area and objects as the export, drawn small, on a checkerboard (or white).</summary>
public class ExportPreviewTests
{
    private static readonly SKColor A = new(0xFF, 0xFF, 0xFF), B = new(0xCC, 0xCC, 0xCC);

    private static Document NewDoc(double inchesW = 3, double inchesH = 2) => new(PageSetup.FromInches(inchesW, inchesH));

    private static T Add<T>(Document doc, T o) where T : DocObject
    {
        doc.Insert(doc.Layers[0], -1, o);
        return o;
    }

    private static SKBitmap Preview(Document doc, ExportOptions options, int box = 300, bool white = false, CancellationToken cancel = default) =>
        ExportPreview.Render(doc, ExportPlan.Create(doc, options), new ExportPreviewOptions(box, box, A, B, white), cancel);

    [Fact]
    public void The_preview_is_the_export_area_fitted_into_the_box_with_its_proportions()
    {
        var doc = NewDoc(3, 2);   // 3:2

        Assert.Equal((300, 200), ExportPreview.FitSize(doc.Page.Bounds, 300, 300));
        Assert.Equal((150, 100), ExportPreview.FitSize(doc.Page.Bounds, 300, 100));   // the height limits it
        Assert.Equal((300, 6), ExportPreview.FitSize(new Rect2(0, 0, 1000, 20), 300, 300));

        using var bmp = Preview(doc, new ExportOptions());
        Assert.Equal((300, 200), (bmp.Width, bmp.Height));
    }

    [Fact]
    public void Where_nothing_is_drawn_there_is_a_checkerboard_and_for_jpeg_white()
    {
        var doc = NewDoc();
        Add(doc, new RectangleObject(72, 36) { Transform = Affine2.Translation(36, 36), Style = ObjectStyle.FillOnly(new DocColor(255, 0, 0)) });

        using var checker = Preview(doc, new ExportOptions());
        using var white = Preview(doc, new ExportOptions(), white: true);

        int cell = 8;
        Assert.Equal(B, checker.GetPixel(2, 2));                  // first square
        Assert.Equal(A, checker.GetPixel(cell + 2, 2));            // next square
        Assert.Equal(A, checker.GetPixel(2, cell + 2));
        Assert.Equal(B, checker.GetPixel(cell + 2, cell + 2));
        Assert.Equal(new SKColor(255, 0, 0), checker.GetPixel(100, 75));      // the drawn rectangle: (36..108, 36..72) pt on a 300 x 200 px picture of the 216 x 144 pt page = px 50..150 x 50..100
        Assert.Equal(SKColors.White, white.GetPixel(2, 2));
        Assert.Equal(SKColors.White, white.GetPixel(cell + 2, 2));
        Assert.Equal(new SKColor(255, 0, 0), white.GetPixel(100, 75));
    }

    [Fact]
    public void A_selection_preview_has_the_bounds_the_export_has_with_its_stroke_and_its_soft_effects()
    {
        var doc = NewDoc(10, 10);
        var box = Add(doc, new RectangleObject(100, 50) { Transform = Affine2.Translation(100, 100), Style = new ObjectStyle(new SolidPaint(new DocColor(0, 0, 200)), StrokeStyle.Solid(DocColor.Black, 10)) });
        var shadow = Add(doc, new PathObject(PathGeometry.Rectangle(new Rect2(0, 0, 100, 50))) { Transform = Affine2.Translation(300, 300), Style = new ObjectStyle(new SolidPaint(DocColor.Black), null, new BlurStyle(SoftKind.SoftShadow, 1, 1, 50)) });

        var boxPlan = ExportPlan.Create(doc, new ExportOptions { Scope = ExportScope.Selection, Selection = [box] });
        Assert.Equal(new Rect2(95, 95, 205, 155), boxPlan.Area);
        using var boxPreview = Preview(doc, new ExportOptions { Scope = ExportScope.Selection, Selection = [box] });
        Assert.Equal(ExportPreview.FitSize(boxPlan.Area, 300, 300), (boxPreview.Width, boxPreview.Height));
        Assert.Equal(new SKColor(0, 0, 0), boxPreview.GetPixel(1, 1));                       // the stroke reaches the very corner of the picture

        var shadowPlan = ExportPlan.Create(doc, new ExportOptions { Scope = ExportScope.Selection, Selection = [shadow] });
        double halo = 3 * 0.068 * 50;
        Assert.Equal(300 - halo, shadowPlan.Area.Left, 6);
        Assert.Equal(400 + halo, shadowPlan.Area.Right, 6);
        using var shadowPreview = Preview(doc, new ExportOptions { Scope = ExportScope.Selection, Selection = [shadow] });
        var core = shadowPreview.GetPixel(shadowPreview.Width / 2, shadowPreview.Height / 2);
        Assert.True(core.Red < 40, "the dark core of the shadow is drawn");
        var edge = shadowPreview.GetPixel(2, shadowPreview.Height / 2);
        Assert.True(edge.Red > 150, "the far edge of the halo is nearly clear");            // the halo is inside the picture, so it is not cut off
    }

    [Fact]
    public void Page_scope_leaves_the_pasteboard_out_unless_asked_and_the_area_follows()
    {
        var doc = NewDoc(3, 2);
        Add(doc, new RectangleObject(40, 40) { Transform = Affine2.Translation(400, 300), Style = ObjectStyle.FillOnly(new DocColor(0, 150, 0)) });

        using var page = Preview(doc, new ExportOptions());
        using var board = Preview(doc, new ExportOptions { IncludePasteboard = true });

        Assert.Equal((300, 200), (page.Width, page.Height));
        Assert.Equal(ExportPreview.FitSize(new Rect2(0, 0, 440, 340), 300, 300), (board.Width, board.Height));
        Assert.Equal(new SKColor(0, 150, 0), board.GetPixel(board.Width - 10, board.Height - 10));
    }

    [Fact]
    public void The_preview_is_what_the_picture_export_draws_only_smaller()
    {
        var doc = SampleDocuments.Everything();
        var options = new ExportOptions();
        using var preview = Preview(doc, options, box: 600);
        var export = DocumentExporterPng(doc, options with { PixelWidth = preview.Width, PixelHeight = preview.Height });

        // the export over the same checkerboard as the preview: the very same drawing code, so the two agree pixel for pixel
        double sum = 0;
        for (int y = 0; y < preview.Height; y++)
            for (int x = 0; x < preview.Width; x++)
            {
                var p = preview.GetPixel(x, y);
                var q = OverChecker(export.GetPixel(x, y), x, y);
                sum += (Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue)) / 3.0;
            }
        Assert.True(sum / (preview.Width * preview.Height) < 0.5, $"mean difference {sum / (preview.Width * preview.Height):0.###}");
        export.Dispose();
    }

    private static SKBitmap DocumentExporterPng(Document doc, ExportOptions options) => SKBitmap.Decode(DocumentExporter.RenderPng(doc, options))!;

    private static SKColor OverChecker(SKColor top, int x, int y)
    {
        var under = ((x / 8) + (y / 8)) % 2 == 0 ? B : A;
        int a = top.Alpha;
        byte Mix(byte t, byte u) => (byte)((t * a + u * (255 - a) + 127) / 255);
        return new SKColor(Mix(top.Red, under.Red), Mix(top.Green, under.Green), Mix(top.Blue, under.Blue));
    }

    [Fact]
    public void A_cancelled_preview_stops_with_an_exception_and_a_big_document_draws_fast()
    {
        var doc = NewDoc(48, 24);
        var rng = new Random(5);
        for (int i = 0; i < 5000; i++)
            Add(doc, new RectangleObject(5 + rng.Next(60), 5 + rng.Next(60), rng.Next(4)) { Transform = Affine2.Translation(rng.Next(3400), rng.Next(1700)), Style = new ObjectStyle(new SolidPaint(new DocColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256))), StrokeStyle.Solid(DocColor.Black, 1)) });

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Preview(doc, new ExportOptions(), cancel: cts.Token));

        var sw = Stopwatch.StartNew();
        using var bmp = Preview(doc, new ExportOptions(), box: 500);
        long ms = sw.ElapsedMilliseconds;
        Assert.True(ms < 2500, $"{ms} ms for 5000 objects");
        Assert.Equal((500, 250), (bmp.Width, bmp.Height));
    }
}
