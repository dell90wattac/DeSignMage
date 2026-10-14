using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeSignMage.App.Canvas;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using SkiaSharp;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>How Vector Edit draws a plain rectangle: black node squares, the outline inverting what is under it, one pixel wide.</summary>
public class VectorEditLookTests
{
    private static readonly SKColor Blue = new(0x00, 0x00, 0xFF);
    private static readonly SKColor Yellow = new(0xFF, 0xFF, 0x00);
    private static readonly SKColor Black = new(0, 0, 0);
    private static readonly SKColor White = new(0xFF, 0xFF, 0xFF);

    private static (MainWindow Window, DesignCanvas Canvas, SKBitmap Pixels, PathObject Path) Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        var c = w.FindControl<DesignCanvas>("Surface")!;
        // A 294 x 152 point rectangle drawn from its lower-left corner, which is the loop's start/end point.
        var loop = new Loop { Start = new Vec2(0, 152), Closed = true };
        foreach (var p in new[] { (0.0, 0.0), (294, 0), (294, 152), (0, 152) }) loop.Segments.Add(Segment.Line(new Vec2(p.Item1, p.Item2)));
        var path = new PathObject(PathEditing.FromLoops([loop], FillRule.NonZero)) { Transform = Affine2.Translation(300, 300), Style = ObjectStyle.FillOnly(DocColor.White) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, path));
        w.Session.EditVectors(path);
        w.Session.Viewport.Set(1, Vec2.Zero);
        Dispatcher.UIThread.RunJobs();
        return (w, c, Capture(w), path);
    }

    private static SKBitmap Capture(MainWindow w)
    {
        Dispatcher.UIThread.RunJobs();
        using var ms = new MemoryStream();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
        w.CaptureRenderedFrame()!.Save(ms);
#pragma warning restore CS0618
        ms.Position = 0;
        return SKBitmap.Decode(ms);
    }

    /// <summary>The frame pixel the document point lands on (the pixel that contains it).</summary>
    private static (int X, int Y) Pix(MainWindow w, DesignCanvas c, double docX, double docY)
    {
        var s = w.Session.Viewport.ToScreen(new Vec2(docX, docY));
        var p = c.TranslatePoint(new Point(s.X, s.Y), w)!.Value;
        return ((int)Math.Floor(p.X * w.RenderScaling), (int)Math.Floor(p.Y * w.RenderScaling));
    }

    [AvaloniaFact]
    public void The_outline_inverts_what_is_under_it_and_the_nodes_are_solid_black_squares_it_runs_across()
    {
        var (w, c, bmp, _) = Open();
        Assert.Equal(1.0, w.RenderScaling);   // the sizes below are at 100 % display scaling
        var (x0, y0) = Pix(w, c, 300, 300);       // top-left corner
        var (x1, _) = Pix(w, c, 594, 300);        // top-right
        var (_, y1) = Pix(w, c, 300, 452);        // bottom-left (the start/end point)

        // The edges: one pixel of blue on the white page, on both sides of nothing else.
        int mx = (x0 + x1) / 2, my = (y0 + y1) / 2;
        Assert.Equal(Blue, bmp.GetPixel(mx, y0));
        Assert.Equal(White, bmp.GetPixel(mx, y0 - 1));
        Assert.Equal(White, bmp.GetPixel(mx, y0 + 1));
        Assert.Equal(Blue, bmp.GetPixel(mx, y1));
        Assert.Equal(Blue, bmp.GetPixel(x0, my));
        Assert.Equal(White, bmp.GetPixel(x0 - 1, my));
        Assert.Equal(White, bmp.GetPixel(x0 + 1, my));
        Assert.Equal(Blue, bmp.GetPixel(x1, my));

        // An ordinary corner: a 6 px black square (x - 3 .. x + 2) with the outline crossing it, which shows yellow on the black.
        for (int dy = -3; dy <= 2; dy++)
            for (int dx = -3; dx <= 2; dx++)
            {
                var px = bmp.GetPixel(x0 + dx, y0 + dy);
                bool onLine = dx == 0 && dy >= 0 || dy == 0 && dx >= 0;   // the outline leaves the corner to the right and downward
                Assert.Equal(onLine ? Yellow : Black, px);
            }
        Assert.Equal(White, bmp.GetPixel(x0 - 4, y0 - 4));
        Assert.Equal(White, bmp.GetPixel(x0 + 3, y0 - 3));   // the square ends at x + 2
        Assert.Equal(White, bmp.GetPixel(x0 - 4, y0));

        // The start/end point: a 10 px black square (x - 5 .. x + 4).
        for (int dy = -5; dy <= 4; dy++)
            for (int dx = -5; dx <= 4; dx++)
            {
                var px = bmp.GetPixel(x0 + dx, y1 + dy);
                bool onLine = dx == 0 && dy <= 0 || dy == 0 && dx >= 0;
                Assert.Equal(onLine ? Yellow : Black, px);
            }
        Assert.Equal(White, bmp.GetPixel(x0 - 6, y1));
        Assert.Equal(White, bmp.GetPixel(x0 + 5, y1 + 3));
        w.Close();
    }

    /// <summary>Saves vector-edit-vs-reference.png (reference screenshot on the left, our render on the right) when DSW_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public void Screenshot_against_the_reference()
    {
        var dir = Environment.GetEnvironmentVariable("DSW_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        var (w, c, bmp, _) = Open();
        var (x0, y0) = Pix(w, c, 300, 300);
        // The reference frames the rectangle with its top-left corner at (71, 54).
        int left = x0 - 71, top = y0 - 54;
        using var ours = new SKBitmap(418, 247);
        using (var cv = new SKCanvas(ours)) cv.DrawBitmap(bmp, SKRect.Create(left, top, 418, 247), SKRect.Create(0, 0, 418, 247));
        using var reference = SKBitmap.Decode(Path.Combine(dir, "sw-vector-edit-rectangle.png"));
        using var both = new SKBitmap(418 * 2 + 10, 247);
        using (var cv = new SKCanvas(both))
        {
            cv.Clear(new SKColor(0xC0, 0xC0, 0xC0));
            cv.DrawBitmap(reference, 0, 0);
            cv.DrawBitmap(ours, 428, 0);
        }
        using var image = SKImage.FromBitmap(both);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(dir, "vector-edit-vs-reference.png"), data.ToArray());
        w.Close();
    }
}
