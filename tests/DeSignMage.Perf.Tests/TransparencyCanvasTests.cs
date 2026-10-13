using System.Diagnostics;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Rendering;
using SkiaSharp;

namespace DeSignMage.Perf.Tests;

/// <summary>
/// Show Transparency (R27): the page is drawn as a checkerboard so that whatever is not covered shows it through. Only the page, never the
/// pasteboard; a constant square on screen at any zoom; part of the still picture that is kept as a bitmap during drags.
/// </summary>
[Collection("perf")]
public class TransparencyCanvasTests
{
    private static (EditorSession Session, CanvasFrameBuilder Frames) Scene(CanvasTheme theme, bool checker, int filler = 0)
    {
        var doc = new Document(PageSetup.FromInches(4, 2));
        var s = new EditorSession(doc);
        var rnd = new Random(7);
        if (filler > 0)
            using (s.History.BeginTransaction("shapes"))
                for (int i = 0; i < filler; i++)
                {
                    var o = new RectangleObject(10 + rnd.NextDouble() * 20, 10 + rnd.NextDouble() * 20)
                    {
                        Transform = Affine2.Translation(rnd.NextDouble() * doc.Page.Width, rnd.NextDouble() * doc.Page.Height),
                        Style = ObjectStyle.FillOnly(new DocColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256))),
                    };
                    s.History.Execute(new AddObjectCommand(doc.ActiveLayer, o));
                }
        s.Viewport.SetViewSize(new Vec2(640, 400));
        s.ZoomToPage();
        return (s, new CanvasFrameBuilder(s) { Theme = theme, ShowTransparency = checker });
    }

    private static SKBitmap Draw(CanvasFrame frame, SKMatrix? device = null, int w = 640, int h = 400)
    {
        var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(200, 60, 200));
        if (device is { } m) canvas.SetMatrix(m);
        frame.Draw(canvas);
        return bmp;
    }

    private static bool Neutral(SKColor c) => c.Red == c.Green && c.Green == c.Blue;

    // ---------------------------------------------------------------- where the checkerboard is

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_checkerboard_covers_the_page_only_with_squares_of_eight_pixels_from_its_corner(bool dark)
    {
        var theme = dark ? CanvasTheme.Dark : CanvasTheme.Light;
        var (_, frames) = Scene(theme, checker: true);
        using var _f = frames;
        using var frame = frames.Build(640, 400);
        using var bmp = Draw(frame);
        var page = frame.PageScreenRect;
        int left = (int)MathF.Round(page.Left), top = (int)MathF.Round(page.Top);

        // the squares: A at the corner, B beside it and below it, A diagonally
        Assert.Equal(theme.CheckerA, bmp.GetPixel(left + 2, top + 2));
        Assert.Equal(theme.CheckerB, bmp.GetPixel(left + 10, top + 2));
        Assert.Equal(theme.CheckerB, bmp.GetPixel(left + 2, top + 10));
        Assert.Equal(theme.CheckerA, bmp.GetPixel(left + 10, top + 10));
        Assert.Equal(theme.CheckerA, bmp.GetPixel(left + 18, top + 2));                    // and so on across the page
        Assert.True(Neutral(theme.CheckerA) && Neutral(theme.CheckerB));                   // neutral greys (R19)
        Assert.NotEqual(theme.CheckerA, theme.CheckerB);

        // the pasteboard around the page looks as it always did: its own color, no squares
        Assert.Equal(theme.Pasteboard, bmp.GetPixel(2, 2));
        Assert.Equal(theme.Pasteboard, bmp.GetPixel(3, 5));
        Assert.Equal(theme.Pasteboard, bmp.GetPixel(Math.Min(639, (int)page.Right + 4), top + 4));
        Assert.Equal(theme.Pasteboard, bmp.GetPixel(Math.Min(639, (int)page.Right + 12), top + 4));
        Assert.Equal(theme.Pasteboard, bmp.GetPixel(left + 4, Math.Min(399, (int)page.Bottom + 4)));

        if (dark)
        {
            // dark squares on the dark canvas, both a little apart from the pasteboard and from the page color
            Assert.True(theme.CheckerA.Red < 0x60 && theme.CheckerB.Red < 0x60);
            Assert.NotEqual(theme.Pasteboard, theme.CheckerB);
        }
    }

    [Fact]
    public void Without_the_toggle_the_page_has_its_plain_color_and_no_squares()
    {
        foreach (var theme in new[] { CanvasTheme.Light, CanvasTheme.Dark })
        {
            var (_, frames) = Scene(theme, checker: false);
            using var _f = frames;
            using var frame = frames.Build(640, 400);
            using var bmp = Draw(frame);
            int left = (int)MathF.Round(frame.PageScreenRect.Left), top = (int)MathF.Round(frame.PageScreenRect.Top);
            var plain = theme.Page ?? theme.Pasteboard;
            foreach (var (dx, dy) in new[] { (2, 2), (10, 2), (2, 10), (10, 10), (50, 70) })
                Assert.Equal(plain, bmp.GetPixel(left + dx, top + dy));
        }
    }

    [Fact]
    public void The_squares_keep_their_size_on_screen_at_any_zoom_and_move_with_the_page()
    {
        var (session, frames) = Scene(CanvasTheme.Light, checker: true);
        using var _f = frames;
        foreach (double zoomBy in new[] { 1.0, 2.0, 0.5, 3.0 })
        {
            if (zoomBy != 1.0) session.Viewport.ZoomAt(new Vec2(320, 200), zoomBy);
            using var frame = frames.Build(640, 400);
            using var bmp = Draw(frame);
            int left = (int)MathF.Round(frame.PageScreenRect.Left), top = (int)MathF.Round(frame.PageScreenRect.Top);
            int y = Math.Max(top, 0) + 2 - (top < 0 ? top % 16 : 0);       // a row inside the page (the page may be bigger than the window)
            int x0 = Math.Max(left, 0);
            int fromCorner = (x0 - left) % 16;                              // where in the two-square period the first visible column is
            var changes = new List<int>();
            var last = bmp.GetPixel(x0, Math.Clamp(y, 0, 399));
            for (int x = x0 + 1; x < Math.Min(639, (int)frame.PageScreenRect.Right - 1); x++)
            {
                var c = bmp.GetPixel(x, Math.Clamp(y, 0, 399));
                if (c != last) { changes.Add(x); last = c; }
            }
            Assert.True(changes.Count >= 3, $"zoom x{zoomBy}: {changes.Count} changes");
            for (int i = 1; i < changes.Count; i++) Assert.Equal(8, changes[i] - changes[i - 1]);    // always 8 pixels, whatever the zoom
            Assert.Equal(0, (changes[0] - left) % 8);                                                 // and they start from the page's corner
            _ = fromCorner;
        }
        // panned by whole pixels: the squares go with the page
        using (var a = frames.Build(640, 400))
        {
            using var before = Draw(a);
            session.Viewport.PanBy(new Vec2(24, 16));
            using var b = frames.Build(640, 400);
            using var after = Draw(b);
            int dx = (int)MathF.Round(b.PageScreenRect.Left) - (int)MathF.Round(a.PageScreenRect.Left), dy = (int)MathF.Round(b.PageScreenRect.Top) - (int)MathF.Round(a.PageScreenRect.Top);
            Assert.Equal(before.GetPixel(100, 100), after.GetPixel(100 + dx, 100 + dy));
        }
    }

    // ---------------------------------------------------------------- what shows through

    [Fact]
    public void A_shape_with_a_hole_shows_the_checkerboard_through_the_hole()
    {
        var (session, frames) = Scene(CanvasTheme.Light, checker: true);
        using var _f = frames;
        var outer = new RectangleObject(120, 90) { Transform = Affine2.Translation(60, 20), Style = ObjectStyle.FillOnly(DocColor.Black) };
        var inner = new RectangleObject(60, 40) { Transform = Affine2.Translation(90, 45), Style = ObjectStyle.FillOnly(DocColor.Black) };
        session.History.Execute(new AddObjectCommand(session.Document.ActiveLayer, outer));
        session.History.Execute(new AddObjectCommand(session.Document.ActiveLayer, inner));
        session.Selection.Set([outer, inner]);
        session.CombineSelection();                                   // the inner one is now a hole
        using var frame = frames.Build(640, 400);
        using var bmp = Draw(frame);
        var theme = CanvasTheme.Light;
        var fill = bmp.GetPixel(frame_x(frame, session, 70), frame_y(frame, session, 30));
        Assert.Equal(SKColors.Black, fill);                           // the shape itself is black
        // the hole is see-through: every pixel in it is one of the two squares, and both of them occur
        var seen = new HashSet<SKColor>();
        for (double dx = 95; dx < 145; dx += 3)
            for (double dy = 50; dy < 80; dy += 3)
                seen.Add(bmp.GetPixel(frame_x(frame, session, dx), frame_y(frame, session, dy)));
        Assert.Equal(new HashSet<SKColor> { theme.CheckerA, theme.CheckerB }, seen);
    }

    private static int frame_x(CanvasFrame f, EditorSession s, double docX) => (int)Math.Round(s.Viewport.ToScreen(new Vec2(docX, 0)).X);
    private static int frame_y(CanvasFrame f, EditorSession s, double docY) => (int)Math.Round(s.Viewport.ToScreen(new Vec2(0, docY)).Y);

    [Fact]
    public void A_partly_transparent_fill_lets_each_square_through_differently()
    {
        var (session, frames) = Scene(CanvasTheme.Light, checker: true);
        using var _f = frames;
        var r = new RectangleObject(200, 100) { Transform = Affine2.Translation(40, 20), Style = ObjectStyle.FillOnly(new DocColor(220, 0, 0, 128)) };
        session.History.Execute(new AddObjectCommand(session.Document.ActiveLayer, r));
        using var frame = frames.Build(640, 400);
        using var bmp = Draw(frame);
        int left = (int)MathF.Round(frame.PageScreenRect.Left), top = (int)MathF.Round(frame.PageScreenRect.Top);
        // find one pixel over a light square and one over a dark square, inside the rectangle
        var seen = new HashSet<SKColor>();
        for (double dx = 60; dx < 200; dx += 5)
            for (double dy = 40; dy < 110; dy += 5)
                seen.Add(bmp.GetPixel(frame_x(frame, session, dx), frame_y(frame, session, dy)));
        Assert.Equal(2, seen.Count);                                  // two colors only: red over the light squares and red over the dark ones
        var (a, b) = (seen.First(), seen.Last());
        Assert.NotEqual(a, b);
        Assert.True(a.Red > 150 && b.Red > 150);                      // both are still clearly red
        _ = (left, top);
    }

    [Fact]
    public void A_soft_shadow_fades_into_the_checkerboard_instead_of_into_white()
    {
        var (session, frames) = Scene(CanvasTheme.Light, checker: true);
        using var _f = frames;
        var r = new RectangleObject(100, 60) { Transform = Affine2.Translation(60, 30), Style = ObjectStyle.FillOnly(DocColor.FromHex("#CC0000")) };
        session.History.Execute(new AddObjectCommand(session.Document.ActiveLayer, r));
        session.Selection.Set(r);
        Assert.NotNull(session.ApplySoftEffect(new SoftSettings { Opacity = 1, Softness = 0.5, ShiftX = 20, ShiftY = 20, Color = DocColor.Black }));
        using var frame = frames.Build(640, 400);
        using var bmp = Draw(frame);
        var theme = CanvasTheme.Light;
        // beyond the shadow's blurred edge nothing is drawn: the squares are exactly what shows
        var beyond = new HashSet<SKColor>();
        for (double dx = 225; dx < 262; dx += 4)
            for (double dy = 40; dy < 100; dy += 4)
                beyond.Add(bmp.GetPixel(frame_x(frame, session, dx), frame_y(frame, session, dy)));
        Assert.Equal(new HashSet<SKColor> { theme.CheckerA, theme.CheckerB }, beyond);
        // inside the shadow (below the source) it is dark: the squares are covered there
        var inside = bmp.GetPixel(frame_x(frame, session, 170), frame_y(frame, session, 100));
        Assert.True(inside.Red < 40, inside.ToString());
    }

    // ---------------------------------------------------------------- the kept bitmap

    [Fact]
    public void Toggling_Show_Transparency_makes_the_kept_bitmap_again_and_the_kept_bitmap_looks_like_drawing_the_picture()
    {
        var (session, frames) = Scene(CanvasTheme.Dark, checker: false, filler: 700);
        using var _f = frames;
        var dragged = session.Document.AllObjects().Take(3).ToList();
        void Frame() { using var f = frames.Build(640, 400); using var b = Draw(f); }

        session.Preview = new TransformPreview(dragged, Affine2.Translation(10, 5));
        Frame();
        Frame();
        int before = frames.StaticLayer.Rebuilds;
        Assert.True(before >= 1);

        frames.ShowTransparency = true;                                // the toggle changed: the old bitmap has the wrong page in it
        session.Preview = new TransformPreview(dragged, Affine2.Translation(14, 5));
        Frame();
        Assert.Equal(before + 1, frames.StaticLayer.Rebuilds);

        // and what comes from the bitmap is what drawing the picture gives, at a scaled device with a fractional offset
        var device = SKMatrix.CreateScale(1.5f, 1.5f).PostConcat(SKMatrix.CreateTranslation(12.4f, 7.6f));
        int w = (int)(640 * 1.5 + 40), h = (int)(400 * 1.5 + 40);
        frames.UseStaticLayer = false;
        using var directFrame = frames.Build(640, 400);
        using var direct = Draw(directFrame, device, w, h);
        frames.UseStaticLayer = true;
        using var cachedFrame = frames.Build(640, 400);
        using var kept = Draw(cachedFrame, device, w, h);
        using var again = Draw(cachedFrame, device, w, h);
        int differing = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (again.GetPixel(x, y) != direct.GetPixel(x, y)) differing++;
        Assert.True(differing <= w * h / 5000, $"{differing} pixels differ between the kept bitmap and the picture");
        Assert.True(kept.GetPixel(5, 5) != default);
    }

    [Fact]
    public void The_checkerboard_costs_a_fraction_of_a_frame()
    {
        var (_, plain) = Scene(CanvasTheme.Light, checker: false, filler: 300);
        var (_, checkered) = Scene(CanvasTheme.Light, checker: true, filler: 300);
        using var _a = plain;
        using var _b = checkered;
        double Average(CanvasFrameBuilder frames)
        {
            for (int i = 0; i < 5; i++) { using var f = frames.Build(900, 500); using var b = Draw(f, null, 900, 500); }   // warm up
            var watch = Stopwatch.StartNew();
            const int frames_ = 40;
            for (int i = 0; i < frames_; i++) { using var f = frames.Build(900, 500); using var b = Draw(f, null, 900, 500); }
            return watch.Elapsed.TotalMilliseconds / frames_;
        }
        double without = Average(plain), with = Average(checkered);
        Assert.True(with - without < 4, $"{without:0.0} ms a frame without, {with:0.0} ms with the checkerboard");
    }
}
