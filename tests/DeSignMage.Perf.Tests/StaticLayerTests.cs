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

    private static void AssertSamePixels(SKBitmap a, SKBitmap b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        int differing = 0, maxDiff = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) { differing++; var pa = a.GetPixel(x, y); var pb = b.GetPixel(x, y); maxDiff = Math.Max(maxDiff, Math.Max(Math.Abs(pa.Red - pb.Red), Math.Max(Math.Abs(pa.Green - pb.Green), Math.Max(Math.Abs(pa.Blue - pb.Blue), Math.Abs(pa.Alpha - pb.Alpha))))); }
        // Moving the drawing by whole pixels into a bitmap rounds a few antialiased edge pixels differently (a level or two of 255).
        Assert.True(differing <= a.Width * a.Height / 5000 && maxDiff <= 3, $"{differing} pixels differ, by up to {maxDiff} levels");
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

        Frame();                                                  // nothing is dragged: drawn from the picture
        Assert.Equal(0, frames.StaticLayer.Rebuilds);

        session.Preview = new TransformPreview(dragged, Affine2.Translation(10, 0));
        for (int i = 1; i <= 6; i++)
        {
            session.Preview = new TransformPreview(dragged, Affine2.Translation(10 * i, 4 * i));   // every move is a new preview, the picture under it is the same
            Frame();
        }
        Assert.Equal(1, frames.StaticLayer.Rebuilds);

        session.Viewport.ZoomAt(new Vec2(300, 200), 1.5);         // the view moved: the old bitmap no longer fits
        Frame();
        Assert.Equal(2, frames.StaticLayer.Rebuilds);

        session.Preview = null;
        Frame();
        Assert.Equal(2, frames.StaticLayer.Rebuilds);
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
    }}
