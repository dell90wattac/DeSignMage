using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Rendering;
using SkiaSharp;

namespace DeSignMage.Perf.Tests;

/// <summary>
/// While objects are dragged the still part of the canvas is drawn from a kept bitmap (<see cref="StaticLayerCache"/>). It must look exactly
/// like drawing the picture, at any scale and offset of the control, and it must not be made again for every frame.
/// </summary>
[Collection("perf")]
public class StaticLayerTests
{
    private static (EditorSession Session, CanvasFrameBuilder Frames, List<DocObject> Dragged) Scene(int shapes)
    {
        var doc = new Document(PageSetup.FromInches(24, 12));
        var s = new EditorSession(doc);
        var rnd = new Random(2);
        var all = new List<DocObject>();
        using (s.History.BeginTransaction("shapes"))
            for (int i = 0; i < shapes; i++)
            {
                double w = Units.FromInches(0.2 + rnd.NextDouble() * 1.2), h = Units.FromInches(0.2 + rnd.NextDouble() * 1.2);
                DocObject o = rnd.Next(2) == 0 ? new RectangleObject(w, h) : new EllipseObject(w, h);
                o.Transform = Affine2.Translation(rnd.NextDouble() * doc.Page.Width, rnd.NextDouble() * doc.Page.Height);
                o.Style = new ObjectStyle(new SolidPaint(new DocColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256))), StrokeStyle.Solid(DocColor.Black, 0.5));
                s.History.Execute(new AddObjectCommand(doc.ActiveLayer, o));
                all.Add(o);
            }
        s.Viewport.SetViewSize(new Vec2(900, 500));
        s.ZoomToPage();
        return (s, new CanvasFrameBuilder(s) { Theme = CanvasTheme.Light }, all.Take(3).ToList());
    }

    private static SKBitmap Draw(CanvasFrame frame, SKMatrix device, int w, int h)
    {
        var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(200, 60, 200));   // anything outside the control stays visibly different
        canvas.SetMatrix(device);
        frame.Draw(canvas);
        return bmp;
    }

    private static void AssertSamePixels(SKBitmap a, SKBitmap b, int maxDiffering = -1, int maxLevels = 3)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        int differing = 0, maxDiff = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) { differing++; var pa = a.GetPixel(x, y); var pb = b.GetPixel(x, y); maxDiff = Math.Max(maxDiff, Math.Max(Math.Abs(pa.Red - pb.Red), Math.Max(Math.Abs(pa.Green - pb.Green), Math.Max(Math.Abs(pa.Blue - pb.Blue), Math.Abs(pa.Alpha - pb.Alpha))))); }
        // Moving the drawing by whole pixels into a bitmap rounds a few antialiased edge pixels differently (a level or two of 255).
        Assert.True(differing <= (maxDiffering >= 0 ? maxDiffering : a.Width * a.Height / 5000) && maxDiff <= maxLevels, $"{differing} pixels differ, by up to {maxDiff} levels");
    }

    public static IEnumerable<object[]> Devices() =>
    [
        [1.0f, 0f, 0f],
        [1.5f, 12.4f, 7.6f],     // a display scaled to 150% with the canvas not on a whole pixel
        [1.25f, 3f, 3f],
        [2.0f, 0.5f, 0.25f],
    ];

    [Theory]
    [MemberData(nameof(Devices))]
    public void A_frame_drawn_from_the_kept_bitmap_is_pixel_identical_to_drawing_the_picture(float scale, float dx, float dy)
    {
        var (session, frames, dragged) = Scene(700);
        using var _ = frames;
        session.Preview = new TransformPreview(dragged, Affine2.Translation(40, 25));
        var device = SKMatrix.CreateScale(scale, scale).PostConcat(SKMatrix.CreateTranslation(dx, dy));
        int w = (int)(900 * scale + dx + 20), h = (int)(500 * scale + dy + 20);

        frames.UseStaticLayer = false;
        using var direct = frames.Build(900, 500);
        using var plain = Draw(direct, device, w, h);
        frames.UseStaticLayer = true;
        using var cachedFrame = frames.Build(900, 500);
        using var kept = Draw(cachedFrame, device, w, h);
        using var again = Draw(cachedFrame, device, w, h);   // the second frame is the one that comes from the bitmap

        Assert.Equal(1, frames.StaticLayer.Rebuilds);
        AssertSamePixels(plain, kept);
        AssertSamePixels(plain, again);
    }

    [Fact]
    public void The_bitmap_is_made_once_for_a_drag_and_again_only_when_the_picture_or_the_view_changes()
    {
        var (session, frames, dragged) = Scene(700);
        using var _ = frames;
        var device = SKMatrix.Identity;
        void Frame() { using var f = frames.Build(900, 500); using var bmp = Draw(f, device, 900, 500); }

        Frame();                                                  // the first frame has a view that is new: a bitmap is made at once, for the pan or zoom that may follow
        Assert.Equal(1, frames.StaticLayer.Rebuilds);
        Frame();                                                  // the same view, nothing dragged: nothing new
        Assert.Equal(1, frames.StaticLayer.Rebuilds);

        session.Preview = new TransformPreview(dragged, Affine2.Translation(10, 0));
        for (int i = 1; i <= 6; i++)
        {
            session.Preview = new TransformPreview(dragged, Affine2.Translation(10 * i, 4 * i));   // every move is a new preview, the picture under it is the same
            Frame();
        }
        Assert.Equal(2, frames.StaticLayer.Rebuilds);             // the drag made the second (the picture under it changed: the dragged objects left it)

        Thread.Sleep(150);                                        // a gesture is over after a moment
        session.Viewport.ZoomAt(new Vec2(300, 200), 1.5);         // the view moved: the old bitmap no longer fits
        Frame();
        Assert.Equal(3, frames.StaticLayer.Rebuilds);

        session.Preview = null;
        Frame();                                                  // the picture changed back, the view did not: drawn from the picture until it repeats
        Assert.Equal(3, frames.StaticLayer.Rebuilds);
    }

    [Fact]
    public void A_light_drawing_is_not_worth_a_bitmap()
    {
        var (session, frames, dragged) = Scene(20);
        using var _ = frames;
        session.Preview = new TransformPreview(dragged, Affine2.Translation(10, 0));
        for (int i = 0; i < 3; i++) { using var f = frames.Build(900, 500); using var bmp = Draw(f, SKMatrix.Identity, 900, 500); }
        Assert.Equal(0, frames.StaticLayer.Rebuilds);
    }

    [Fact]
    public void Frames_over_an_unchanged_picture_without_any_drag_come_from_the_bitmap_from_the_second_frame_and_look_the_same()
    {
        var (session, frames, _) = Scene(700);
        using var keep = frames;
        var device = SKMatrix.CreateScale(1.5f, 1.5f).PostConcat(SKMatrix.CreateTranslation(7.3f, 2.9f));
        int w = 900 * 3 / 2 + 40, h = 500 * 3 / 2 + 40;

        frames.UseStaticLayer = false;
        using var directFrame = frames.Build(900, 500);
        using var direct = Draw(directFrame, device, w, h);
        frames.UseStaticLayer = true;
        using var first = frames.Build(900, 500);
        using var firstBmp = Draw(first, device, w, h);
        Assert.Equal(0, frames.StaticLayer.Rebuilds);          // nothing to compare with yet: drawn from the picture
        using var second = frames.Build(900, 500);
        using var secondBmp = Draw(second, device, w, h);
        Assert.Equal(1, frames.StaticLayer.Rebuilds);
        AssertSamePixels(direct, firstBmp);
        AssertSamePixels(direct, secondBmp);
    }

    private static SKBitmap Frame(CanvasFrameBuilder frames, SKMatrix device, int w, int h, bool kept)
    {
        frames.UseStaticLayer = kept;
        using var f = frames.Build(900, 500);
        return Draw(f, device, w, h);
    }

    private sealed class FakeClock { public long Now; public long Tick() => Now; public void Advance(double ms) => Now += (long)(ms * System.Diagnostics.Stopwatch.Frequency / 1000); }

    [Theory]
    [InlineData(1.0f, 0f, 0f, 37.0, -23.0)]
    [InlineData(1.5f, 12.4f, 7.6f, 24.0, -16.0)]     // 150 % display: whole device pixels per step (36 and -24)
    [InlineData(1.0f, 3f, 3f, 300.0, 5.0)]
    public void Panning_moves_the_kept_bitmap_and_the_settled_frame_is_pixel_identical_to_a_full_draw(float scale, float dx, float dy, double stepX, double stepY)
    {
        var (session, frames, _) = Scene(700);
        using var keep = frames;
        var clock = new FakeClock();
        frames.Clock = clock.Tick;
        var device = SKMatrix.CreateScale(scale, scale).PostConcat(SKMatrix.CreateTranslation(dx, dy));
        int w = (int)(900 * scale + dx + 20), h = (int)(500 * scale + dy + 20);

        for (int i = 0; i < 2; i++) { using var idle = Frame(frames, device, w, h, true); clock.Advance(16); }
        Assert.Equal(1, frames.StaticLayer.Rebuilds);

        for (int step = 0; step < 8; step++)
        {
            session.Viewport.PanBy(new Vec2(step % 2 == 0 ? stepX : -Math.Floor(stepX / 2), step % 3 == 0 ? stepY : -stepY));
            clock.Advance(16);
            using var fast = Frame(frames, device, w, h, true);
            Assert.True(frames.SettleFramePending);
            using var plain = Frame(frames, device, w, h, false);
            // While moving, a few edge pixels of shapes that cross the window's edge differ (a path clipped is antialiased a little differently from the same path moved).
            AssertSamePixels(fast, plain, maxDiffering: fast.Width * fast.Height / 500, maxLevels: 60);
        }
        Assert.Equal(1, frames.StaticLayer.Rebuilds);

        // The pan is over: the next frame, a moment later, is the picture drawn as a whole.
        clock.Advance(500);
        using var settled = Frame(frames, device, w, h, true);
        Assert.False(frames.SettleFramePending);
        using var reference = Frame(frames, device, w, h, false);
        Assert.Equal(2, frames.StaticLayer.Rebuilds);
        Assert.Equal(1, frames.StaticLayer.Settles);
        AssertSamePixels(reference, settled);
    }

    [Fact]
    public void A_pan_by_part_of_a_device_pixel_is_drawn_from_the_picture_and_looks_the_same()
    {
        var (session, frames, _) = Scene(700);
        using var keep = frames;
        var device = SKMatrix.CreateScale(1.5f, 1.5f);
        for (int i = 0; i < 2; i++) { using var idle = Frame(frames, device, 1400, 800, true); }
        session.Viewport.PanBy(new Vec2(10.3, 4.1));                 // 15.45 device pixels: not a whole number
        using var fast = Frame(frames, device, 1400, 800, true);
        using var plain = Frame(frames, device, 1400, 800, false);
        Assert.Equal(0, frames.StaticLayer.Shifts);
        AssertSamePixels(plain, fast);
    }

    [Fact]
    public void A_zoom_in_gesture_stretches_the_last_sharp_bitmap_and_the_settled_frame_is_pixel_identical_to_a_full_draw()
    {
        var (session, frames, _) = Scene(700);
        using var keep = frames;
        var clock = new FakeClock();
        frames.Clock = clock.Tick;
        var device = SKMatrix.CreateScale(1.25f, 1.25f).PostConcat(SKMatrix.CreateTranslation(4, 6));
        int w = 1180, h = 680;
        for (int i = 0; i < 3; i++) { using var idle = Frame(frames, device, w, h, true); clock.Advance(16); }
        clock.Advance(500);

        // The first notch is drawn sharp (and kept); the next ones, while the wheel still turns, are the kept bitmap stretched.
        session.Wheel(new Vec2(450, 250), 1);
        clock.Advance(16);
        using (var first = Frame(frames, device, w, h, true)) { }
        Assert.Equal(0, frames.StaticLayer.Magnified);
        for (int notch = 0; notch < 2; notch++)
        {
            session.Wheel(new Vec2(450, 250), 1);
            clock.Advance(30);
            using var stretched = Frame(frames, device, w, h, true);
            Assert.True(frames.SettleFramePending);
        }
        Assert.Equal(2, frames.StaticLayer.Magnified);

        clock.Advance(500);
        using var settled = Frame(frames, device, w, h, true);
        using var reference = Frame(frames, device, w, h, false);
        Assert.False(frames.SettleFramePending);
        AssertSamePixels(reference, settled);
        using var nextIdle = Frame(frames, device, w, h, true);          // and it stays: nothing is drawn again
        AssertSamePixels(reference, nextIdle);
    }

    [Fact]
    public void Zooming_out_a_single_step_or_a_far_zoom_in_is_drawn_sharp_from_the_picture()
    {
        var (session, frames, _) = Scene(700);
        using var keep = frames;
        var clock = new FakeClock();
        frames.Clock = clock.Tick;
        for (int i = 0; i < 3; i++) { using var idle = Frame(frames, SKMatrix.Identity, 900, 500, true); clock.Advance(16); }
        clock.Advance(500);

        session.Wheel(new Vec2(450, 250), -1);                           // out: the bitmap does not hold what comes into view
        clock.Advance(30);
        using (var a = Frame(frames, SKMatrix.Identity, 900, 500, true)) { }
        session.Wheel(new Vec2(450, 250), -1);
        clock.Advance(30);
        using (var b = Frame(frames, SKMatrix.Identity, 900, 500, true)) { }
        session.Viewport.ZoomAt(new Vec2(450, 250), 20);                 // far in: more than a stretch can bear
        clock.Advance(30);
        using (var c = Frame(frames, SKMatrix.Identity, 900, 500, true)) { }
        Assert.Equal(0, frames.StaticLayer.Magnified);
        using var plain = Frame(frames, SKMatrix.Identity, 900, 500, false);
        using var kept = Frame(frames, SKMatrix.Identity, 900, 500, true);
        AssertSamePixels(plain, kept);
    }}
