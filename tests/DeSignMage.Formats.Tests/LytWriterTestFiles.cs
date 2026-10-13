using System.Text;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using DeSignMage.Formats.Lyt;
using SkiaSharp;
using Xunit.Abstractions;

namespace DeSignMage.Formats.Tests;

/// <summary>
/// Builds the small set of layouts that somebody with the legacy program opens by hand (the program is the only real judge of the writer).
/// Does nothing unless the environment variable <c>DESIGNMAGE_LYT_TESTFILES</c> names a folder; then it writes the layouts there, together
/// with a picture of what this application shows for each one after reading it back.
/// </summary>
public class LytWriterTestFiles(ITestOutputHelper output)
{
    private static readonly DocColor Orange = new(255, 128, 0, 255, "83 Bright Orange", "ARLON");
    private static readonly DocColor Black = new(0, 0, 0, 255, "12 Black", "3M");
    private static readonly DocColor White = new(255, 255, 255, 255, "White", "Major Print Colors");
    private static readonly DocColor Navy = new(9, 28, 80, 255, "Dark Blue MB97", "Designers Colors");
    private static readonly DocColor Red = new(200, 30, 40, 255, "Red 3", "3M");
    private static readonly DocColor Green = new(20, 140, 60, 255, "Kelly Green", "ARLON");
    private static readonly DocColor Gold = new(236, 190, 40, 255, "Gold Y95", "Designers Colors");

    private static string Pick(params string[] families) =>
        families.FirstOrDefault(TextEngine.Fonts.IsInstalled) ?? TextEngine.Fonts.Families.First();

    [Fact]
    public void Write_the_hand_check_layouts_when_asked_to()
    {
        string? dir = Environment.GetEnvironmentVariable("DESIGNMAGE_LYT_TESTFILES");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);

        var files = new (string Name, Document Doc)[]
        {
            ("01-shapes", Shapes()), ("02-groups", Groups()), ("03-text", Texts()), ("04-pictures", Pictures()),
            ("05-gradients", Gradients()), ("06-strokes-and-effects", StrokesAndEffects()), ("07-two-layers", TwoLayers()),
        };
        var report = new StringBuilder();
        foreach (var (name, doc) in files)
        {
            var thumb = ThumbnailOf(doc);
            var options = new LytWriteOptions
            {
                Title = name, ThumbnailJpeg = thumb, OffsetService = WriterTestKit.Offsets, BooleanService = WriterTestKit.Booleans,
            };
            var bytes = LytWriter.ToBytes(doc, options, out var result);
            File.WriteAllBytes(Path.Combine(dir, name + ".lyt"), bytes);

            var back = LytImporter.Import(LytParser.Parse(bytes));
            using var backBitmap = LytRenderTests.Render(back.Document, back.Document.Page.Bounds, 0.5);
            using var backImage = SKImage.FromBitmap(backBitmap);
            File.WriteAllBytes(Path.Combine(dir, name + ".preview.png"), backImage.Encode(SKEncodedImageFormat.Png, 90).ToArray());

            report.AppendLine($"{name}.lyt: {result.PartCount} objects, {result.GroupCount} groups, {result.LiveTextCount} live texts, {result.TextAsOutlinesCount} texts as outlines, " +
                              $"{result.PictureCount} pictures, {result.GradientCount} gradients, {result.FadeCount} fades, {result.StrokeShapeCount} stroke shapes, {bytes.Length / 1024} KB");
            foreach (var w in result.Warnings) report.AppendLine("    warning: " + w);
        }
        File.WriteAllText(Path.Combine(dir, "written.txt"), report.ToString());
        output.WriteLine(report.ToString());
    }

    private static byte[] ThumbnailOf(Document doc)
    {
        using var bmp = LytRenderTests.Render(doc, doc.Page.Bounds, 400.0 / Math.Max(doc.Page.Width, doc.Page.Height));
        using var image = SKImage.FromBitmap(bmp);
        return image.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }

    // ---------------------------------------------------------------- helpers

    private static T Put<T>(Document doc, T obj, double xIn, double yIn, Paint? fill, string name, Layer? layer = null) where T : DocObject
    {
        obj.Transform = Affine2.Translation(xIn * 72, yIn * 72) ;
        obj.Style = new ObjectStyle(fill, null);
        obj.Name = name;
        doc.Insert(layer ?? doc.ActiveLayer, -1, obj);
        return obj;
    }

    private static Paint Solid(DocColor c) => new SolidPaint(c);

    private static Document Page(double wIn, double hIn, string layerName = "Layer 1")
    {
        var doc = new Document(PageSetup.FromInches(wIn, hIn));
        doc.Layers[0].Name = layerName;
        return doc;
    }

    // ---------------------------------------------------------------- 01 shapes

    private static Document Shapes()
    {
        var doc = Page(12, 6);
        Put(doc, new RectangleObject(144, 72), 0.5, 0.5, Solid(Orange), "Rectangle 2x1 in");
        Put(doc, new EllipseObject(144, 86), 3, 0.5, Solid(Navy), "Ellipse 2 x 1.2 in");
        Put(doc, new RectangleObject(144, 72, 20), 5.5, 0.5, Solid(Red), "Rounded rectangle");

        var triangle = new PathBuilder().MoveTo(72, 0).LineTo(144, 108).LineTo(0, 108).Close().Build();
        Put(doc, new PathObject(triangle), 8, 0.4, Solid(Black), "Triangle");

        var star = new PathBuilder();
        for (int i = 0; i < 10; i++)
        {
            double r = i % 2 == 0 ? 54 : 22, a = -Math.PI / 2 + i * Math.PI / 5;
            var p = new Vec2(54 + r * Math.Cos(a), 54 + r * Math.Sin(a));
            if (i == 0) star.MoveTo(p); else star.LineTo(p);
        }
        Put(doc, new PathObject(star.Close().Build()), 10.2, 0.4, Solid(Gold), "Star");

        var leaf = new PathBuilder().MoveTo(0, 100).CubicTo(10, 20, 80, -10, 150, 0).CubicTo(140, 70, 90, 120, 0, 100).Close().Build();
        Put(doc, new PathObject(leaf), 0.5, 2.5, Solid(Green), "Leaf (Bezier curves)");

        var pie = new PathBuilder().MoveTo(50, 50).LineTo(100, 50).ArcTo(new Vec2(50 + 50 * Math.Cos(-0.8), 50 + 50 * Math.Sin(-0.8)), new Vec2(50, 0)).Close().Build();
        Put(doc, new PathObject(pie), 3.2, 2.5, Solid(Orange), "Pie slice (arc)");

        var ring = new PathBuilder { FillRule = FillRule.EvenOdd }
            .MoveTo(0, 0).LineTo(110, 0).LineTo(110, 110).LineTo(0, 110).Close()
            .MoveTo(30, 30).LineTo(80, 30).LineTo(80, 80).LineTo(30, 80).Close().Build();
        Put(doc, new PathObject(ring), 5.2, 2.5, Solid(Navy), "Square with a square hole");

        var wave = new PathBuilder().MoveTo(0, 30).CubicTo(30, -10, 60, 70, 100, 30).Build();
        var line = Put(doc, new PathObject(wave), 7.3, 2.6, null, "Open curve (no fill)");
        line.Style = new ObjectStyle(null, null);

        Put(doc, new EllipseObject(80, 80), 9, 2.6, Solid(DocColor.FromCmyk(0, 100, 100, 0)), "Circle, unnamed CMYK 0,100,100,0");
        Put(doc, new EllipseObject(80, 80), 10.4, 2.6, Solid(DocColor.FromCmyk(100, 0, 0, 0, "Process Cyan", "Custom")), "Circle, named CMYK");

        var big = new PathBuilder().MoveTo(0, 0).LineTo(60, 0).ArcTo(new Vec2(78, 18), new Vec2(60, 36)).LineTo(0, 36).Close().Build();
        Put(doc, new PathObject(big), 0.5, 4.8, Solid(Black), "D shape (line, arc, line)");
        return doc;
    }

    // ---------------------------------------------------------------- 02 groups

    private static Document Groups()
    {
        var doc = Page(10, 4);
        var colors = new[] { Red, Navy, Green, Orange, Gold };
        var squares = new List<RectangleObject>();
        for (int i = 0; i < 5; i++)
            squares.Add(Put(doc, new RectangleObject(100, 100), 0.5 + i * 1.3, 1.2 - (i % 2) * 0.3, Solid(colors[i]), "ABCDE"[i].ToString()));

        var inner = new GroupObject { Name = "Inner (A + C)" };
        doc.Insert(doc.ActiveLayer, -1, inner);
        doc.SetGroup(squares[0], inner);
        doc.SetGroup(squares[2], inner);
        var outer = new GroupObject { Name = "Outer (Inner + D)" };
        doc.Insert(doc.ActiveLayer, -1, outer);
        doc.SetGroup(inner, outer);
        doc.SetGroup(squares[3], outer);
        return doc;
    }

    // ---------------------------------------------------------------- 03 text

    private static Document Texts()
    {
        var doc = Page(12, 7);
        string sans = Pick("Arial", "Segoe UI"), serif = Pick("Times New Roman", "Georgia"), mono = Pick("Courier New", "Consolas");

        TextObject T(string text, string family, double size, double xIn, double yIn, DocColor c, Affine2? extra = null)
        {
            var t = new TextObject(text, family, size) { Name = null, Style = ObjectStyle.FillOnly(c) };
            t.Transform = (extra ?? Affine2.Identity) * Affine2.Translation(xIn * 72, yIn * 72);
            doc.Insert(doc.ActiveLayer, -1, t);
            return t;
        }

        T("Hello Sign Shop", sans, 54, 0.5, 1.0, Navy);
        T("Serif Heading 123", serif, 48, 0.5, 2.0, Red);
        T("Monospace text", mono, 40, 0.5, 2.9, Black);
        T("Rotated 30", sans, 44, 1.0, 4.9, Green, Affine2.Rotation(-Math.PI / 6));
        T("Stretched wide", sans, 40, 5.0, 3.6, Orange, Affine2.Scale(1.5, 1));
        var spaced = T("S P A C E D", sans, 40, 5.0, 4.5, Navy);
        spaced.Tracking = 0.35;
        var centered = T("Centered", serif, 44, 9.5, 5.4, Black);
        centered.Alignment = TextAlignment.Center;
        var bold = T("Bold falls back to outlines", sans, 36, 0.5, 6.4, Red);
        bold.FontWeight = 700;
        T("Two\nlines", sans, 36, 8.5, 1.0, Gold);
        return doc;
    }

    // ---------------------------------------------------------------- 04 pictures

    private static byte[] Picture(bool png)
    {
        using var bmp = new SKBitmap(240, 120);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(250, 235, 190));
            using var red = new SKPaint { Color = new SKColor(200, 30, 40) };
            using var blue = new SKPaint { Color = new SKColor(20, 60, 200) };
            using var black = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawRect(0, 0, 120, 60, red);                    // top left red
            canvas.DrawRect(120, 60, 120, 60, blue);                // bottom right blue
            using var font = new SKFont { Size = 22 };
            canvas.DrawText(png ? "PNG TOP" : "JPEG TOP", 8, 100, SKTextAlign.Left, font, black);
            canvas.DrawLine(0, 0, 240, 120, black);
        }
        using var image = SKImage.FromBitmap(bmp);
        return image.Encode(png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 90).ToArray();
    }

    private static Document Pictures()
    {
        var doc = Page(12, 5);
        void Image(byte[] data, string type, double w, double h, Affine2 t, string name)
        {
            var img = new ImageObject(doc.Assets.Add(data, type), w, h) { Name = name, Transform = t };
            doc.Insert(doc.ActiveLayer, -1, img);
        }
        Image(Picture(false), "image/jpeg", 288, 144, Affine2.Translation(36, 36), "JPEG picture 4 x 2 in");
        Image(Picture(true), "image/png", 288, 144, Affine2.Translation(360, 36), "PNG picture 4 x 2 in");
        Image(Picture(false), "image/jpeg", 216, 108, Affine2.Rotation(-0.35) * Affine2.Translation(120, 290), "JPEG picture rotated 20 degrees");
        Image(Picture(true), "image/png", 216, 108, Affine2.Scale(1, 1.6) * Affine2.Translation(520, 230), "PNG picture stretched tall");
        return doc;
    }

    // ---------------------------------------------------------------- 05 gradients

    private static Document Gradients()
    {
        var doc = Page(12, 6);
        void Box(GradientPaint p, double xIn, double yIn, string name)
        {
            var r = new RectangleObject(180, 108) { Name = name, Transform = Affine2.Translation(xIn * 72, yIn * 72), Style = new ObjectStyle(p, null) };
            doc.Insert(doc.ActiveLayer, -1, r);
        }
        GradientPaint G(GradientKind k, params (double, DocColor)[] s) => new(k, GradientPaint.DefaultPoints(k), s.Select(x => new GradientNode(x.Item1, x.Item2)).ToList());

        Box(G(GradientKind.Linear, (0, Red), (1, Gold)), 0.4, 0.4, "Linear red to gold");
        Box(G(GradientKind.Horizontal, (0, Navy), (1, White)), 3.2, 0.4, "Horizontal navy to white");
        Box(G(GradientKind.Vertical, (0, Green), (1, Black)), 6.0, 0.4, "Vertical green to black");
        Box(G(GradientKind.Circle, (0, White), (1, Navy)), 8.8, 0.4, "Circle white to navy");
        Box(G(GradientKind.Ellipse, (0, Gold), (1, Red)), 0.4, 2.4, "Ellipse gold to red");
        Box(G(GradientKind.Linear, (0, Red), (0.5, White), (1, Navy)), 3.2, 2.4, "Linear three colors");

        // fades over a dark backdrop, so the transparency shows
        var back = new RectangleObject(5 * 72, 130) { Name = "Backdrop", Transform = Affine2.Translation(6.0 * 72, 2.35 * 72), Style = ObjectStyle.FillOnly(Navy) };
        doc.Insert(doc.ActiveLayer, -1, back);
        Box(G(GradientKind.Linear, (0, Orange with { A = 255 }), (1, Orange with { A = 0 })), 6.1, 2.4, "Linear fade out");
        Box(G(GradientKind.Circle, (0, White with { A = 255 }), (1, White with { A = 0 })), 8.9, 2.4, "Circle fade out");
        return doc;
    }

    // ---------------------------------------------------------------- 06 strokes and effects

    private static Document StrokesAndEffects()
    {
        var doc = Page(12, 5);
        var box = Put(doc, new RectangleObject(160, 100), 0.5, 0.5, Solid(Gold), "Gold box with a navy stroke");
        box.Style = new ObjectStyle(Solid(Gold), StrokeStyle.Solid(Navy, 12));
        var round = Put(doc, new EllipseObject(140, 100), 3.2, 0.5, Solid(White), "Ellipse with a red round stroke");
        round.Style = new ObjectStyle(Solid(White), new StrokeStyle(Solid(Red), 10, LineJoin.Round, LineCap.Round));
        var frame = Put(doc, new RectangleObject(120, 100), 5.6, 0.5, null, "Stroke only (a frame)");
        frame.Style = new ObjectStyle(null, StrokeStyle.Solid(Green, 8));
        var line = new PathObject(new PathBuilder().MoveTo(0, 0).LineTo(120, 40).LineTo(200, 0).Build()) { Name = "Open line with a stroke", Transform = Affine2.Translation(7.9 * 72, 0.8 * 72) };
        line.Style = new ObjectStyle(null, new StrokeStyle(Solid(Black), 9, LineJoin.Miter, LineCap.Butt));
        doc.Insert(doc.ActiveLayer, -1, line);

        var shadow = Put(doc, new RectangleObject(160, 90), 0.62, 3.0, Solid(Black), "Soft shadow (written as a plain object)");
        shadow.Style = new ObjectStyle(Solid(Black), null, new BlurStyle(SoftKind.SoftShadow, 0.5, 1, 90));
        Put(doc, new RectangleObject(160, 90), 0.5, 2.9, Solid(Orange), "Sign in front of the shadow");

        var a = new RectangleObject(100, 100) { Style = ObjectStyle.FillOnly(Red) };
        var b = new EllipseObject(70, 70) { Style = ObjectStyle.FillOnly(Blue), Transform = Affine2.Translation(15, 15) };
        var combined = new CombinedObject([a, b]) { Name = "Combined square and circle (a hole)", Transform = Affine2.Translation(4 * 72, 3 * 72) };
        doc.Insert(doc.ActiveLayer, -1, combined);
        return doc;
    }

    private static readonly DocColor Blue = new(20, 60, 200, 255, "Blue 8", "3M");

    // ---------------------------------------------------------------- 07 layers

    private static Document TwoLayers()
    {
        var doc = Page(10, 5, "Background");
        var top = new Layer("Lettering") { Color = Red };
        doc.InsertLayer(1, top);
        Put(doc, new RectangleObject(10 * 72 - 72, 4 * 72 - 36), 0.5, 0.5, Solid(Navy), "Navy panel", doc.Layers[0]);
        Put(doc, new EllipseObject(200, 200), 6.8, 0.3, Solid(Gold), "Gold disc", doc.Layers[0]);
        var t = new TextObject("OPEN", Pick("Arial", "Segoe UI"), 96) { Name = "OPEN", Style = ObjectStyle.FillOnly(White), Transform = Affine2.Translation(1.0 * 72, 3.2 * 72) };
        doc.Insert(top, -1, t);
        var t2 = new TextObject("Daily 9 - 5", Pick("Times New Roman", "Georgia"), 36) { Name = "Daily", Style = ObjectStyle.FillOnly(Gold), Transform = Affine2.Translation(1.0 * 72, 4.1 * 72) };
        doc.Insert(top, -1, t2);
        var dot = Put(doc, new EllipseObject(30, 30), 7.4, 1.0, Solid(Red), "Red dot", top);
        var grouped = new GroupObject { Name = "Dot group" };
        doc.Insert(top, -1, grouped);
        doc.SetGroup(dot, grouped);
        return doc;
    }
}
