using System.Text;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using DeSignMage.Formats.Native;
using DeSignMage.Rendering.Export;
using SkiaSharp;

namespace DeSignMage.Formats.Tests;

/// <summary>Text on a path in the native file and in every export (TASKS C.28): the options survive, the letters are written where they flowed.</summary>
public class TextOnPathFormatsTests
{
    private static (Document Doc, TextObject Text) Sample(TextOnPath? settings = null, string text = "Curvy sign text")
    {
        var doc = new Document(PageSetup.FromInches(12, 6));
        var path = new PathBuilder().MoveTo(0, 0).CubicTo(250, -220, 450, 220, 700, 0).Build();
        var t = new TextObject(text, "Arial", 72)
        {
            Transform = Affine2.Translation(60, 250),
            Style = ObjectStyle.FillOnly(DocColor.FromHex("#CC0000")),
            OnPath = settings ?? new TextOnPath(path),
        };
        doc.Insert(doc.ActiveLayer, -1, t);
        return (doc, t);
    }

    [Fact]
    public void The_native_file_keeps_the_path_and_every_option()
    {
        var path = new PathBuilder().MoveTo(0, 0).LineTo(100, 50).ArcTo(new Vec2(200, 150), new Vec2(300, 100)).CubicTo(350, 0, 420, 40, 500, 0).Build();
        var settings = new TextOnPath(path)
        {
            CurveCompensation = 0.31, Spacing = TextPathSpacing.Natural, StartOffset = 14.5, Side = TextPathSide.Below, Flip = true, Raise = -6.25,
        };
        var (doc, text) = Sample(settings);
        text.Alignment = TextAlignment.Center;

        var loaded = DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(doc)));
        var back = Assert.IsType<TextObject>(Assert.Single(loaded.Document.Layers[0].Children));

        Assert.True(back.IsOnPath);
        var s = back.OnPath!;
        Assert.Equal(0.31, s.CurveCompensation, 9);
        Assert.Equal(TextPathSpacing.Natural, s.Spacing);
        Assert.Equal(14.5, s.StartOffset, 9);
        Assert.Equal(TextPathSide.Below, s.Side);
        Assert.True(s.Flip);
        Assert.Equal(-6.25, s.Raise, 9);
        Assert.Equal(path.Verbs, s.Path.Verbs);
        Assert.Equal(path.Points, s.Path.Points);
        // The text is still live and flows to exactly the same place.
        Assert.Equal(text.Text, back.Text);
        var a = text.GetWorldBounds();
        var b = back.GetWorldBounds();
        Assert.Equal(a.Left, b.Left, 3);
        Assert.Equal(a.Right, b.Right, 3);
        Assert.Equal(a.Top, b.Top, 3);
        Assert.Equal(a.Bottom, b.Bottom, 3);
    }

    [Fact]
    public void A_removed_path_does_not_come_back_after_saving_and_loading()
    {
        var (doc, text) = Sample();
        text.OnPath = null;
        var loaded = DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(doc)));
        Assert.False(Assert.IsType<TextObject>(Assert.Single(loaded.Document.Layers[0].Children)).IsOnPath);
    }

    [Fact]
    public void A_loaded_text_on_a_path_re_flows_when_edited()
    {
        var (doc, _) = Sample();
        var loaded = DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(doc)));
        var t = Assert.IsType<TextObject>(Assert.Single(loaded.Document.Layers[0].Children));
        var before = t.Layout.Outline.GetBounds();
        t.Text = "A different, longer line of text";
        Assert.True(t.IsOnPath);
        Assert.NotEqual(before, t.Layout.Outline.GetBounds());
        // Fit spacing: the longer text still spans the same path.
        Assert.InRange(t.GetWorldBounds().Width, before.Width * 0.7, before.Width * 1.4);
    }

    [Fact]
    public void Exports_draw_the_letters_where_they_flowed()
    {
        var (doc, text) = Sample();
        var options = new ExportOptions { Dpi = 72 };

        // PNG: the red letters appear, and in the arch above the baseline of the page where the plain text would have been.
        byte[] png = DocumentExporter.Render(doc, ExportFormat.Png, options);
        using var bitmap = SKBitmap.Decode(png);
        var b = text.GetWorldBounds();
        int red = 0, redInStraightRow = 0;
        double straightY = 250;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Red > 150 && c.Green < 80 && c.Blue < 80) { red++; if (Math.Abs(y - straightY) < 6) redInStraightRow++; }
            }
        Assert.True(red > 500, $"{red} red pixels");
        Assert.True(bitmap.Width >= 800 && b.Top < straightY - 20);   // the letters rise well above where straight text sits
        Assert.True(redInStraightRow < red / 2, "most of the red is off the straight baseline row");

        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(DocumentExporter.Render(doc, ExportFormat.Pdf, options), 0, 4));
        string eps = Encoding.ASCII.GetString(DocumentExporter.Render(doc, ExportFormat.Eps, options));
        Assert.StartsWith("%!PS", eps);
        Assert.Contains("curveto", eps);
    }

    [Fact]
    public void Svg_writes_the_flowed_letters_as_outlines_even_when_live_text_is_asked_for()
    {
        var (doc, _) = Sample();
        string asked = SvgExporter.Render(doc, new ExportOptions { TextAsOutlines = false });
        Assert.DoesNotContain("<text", asked);             // a live <text> could not follow the curve
        Assert.Contains("<path", asked);

        var straight = new Document(PageSetup.FromInches(12, 6));
        straight.Insert(straight.ActiveLayer, -1, new TextObject("Plain", "Arial", 72) { Transform = Affine2.Translation(60, 250) });
        Assert.Contains("<text", SvgExporter.Render(straight, new ExportOptions { TextAsOutlines = false }));   // ordinary text still is live text
    }
}
