using System.Diagnostics;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using DeSignMage.Rendering;
using DeSignMage.TestSupport;
using SkiaSharp;
using Xunit.Abstractions;

namespace DeSignMage.Perf.Tests;

/// <summary>
/// Grabbing and dragging objects in a big real layout (1,400 objects: 1,000 shapes, 14 photos, 70 live texts, and a lot of text outlines made of
/// thousands of nodes). The file is <c>local-testdata/layouts/art-d.lyt</c>; these tests are skipped where it is not available.
/// What they guard (all of it showed up as "the program lags when I pick something up"):
/// <list type="bullet">
/// <item>hit testing runs on every mouse move and on the press; an outline of 2,700 arc nodes was copied, converted and flattened for each
/// call (up to 16 ms at some points);</item>
/// <item>the drag preview re-classified every loop of the dragged objects for every frame (10 ms for a group of a hundred shapes);</item>
/// <item>every frame replayed the whole document picture (5 to 20 ms in software) although only the preview moves.</item>
/// </list>
/// </summary>
[Collection("perf")]
public class GrabPerfTests(ITestOutputHelper output)
{
    private const string File = "layouts/art-d.lyt";

    private static (EditorSession Session, CanvasFrameBuilder Frames, SKSurface Surface) Open()
    {
        DeSignMage.Text.SkiaFontService.Install();
        var doc = LytImporter.Open(LocalData.Layout("art-d.lyt")).Document;
        var session = new EditorSession(doc);
        session.Viewport.SetViewSize(new Vec2(1600, 900));
        session.ZoomToContent();
        return (session, new CanvasFrameBuilder(session) { Theme = CanvasTheme.Light }, SKSurface.Create(new SKImageInfo(1600, 900)));
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    [LocalFact(File)]
    public void Hit_testing_over_the_whole_layout_never_stalls()
    {
        var (session, frames, surface) = Open();
        using var keepFrames = frames; using var keepSurface = surface;
        // The flyers and logos (text outlines of thousands of nodes sit under many of these points), on the pointer positions of a 80 x 50 sweep.
        session.Viewport.ZoomToFit(new Rect2(9300, -5650, 16300, 200));
        var view = session.Viewport.VisibleDocRect;
        var points = new List<Vec2>();
        for (int x = 0; x < 80; x++)
            for (int y = 0; y < 50; y++)
                points.Add(new Vec2(view.Left + view.Width * (x + 0.5) / 80, view.Top + view.Height * (y + 0.5) / 50));
        foreach (var p in points.Take(100)) session.HitTestAt(p);   // warm up

        var times = points.Select(p => Math.Min(Time(() => session.HitTestAt(p)), Time(() => session.HitTestAt(p)))).OrderBy(t => t).ToList();
        double median = times[times.Count / 2], worst = times[^1];
        output.WriteLine($"hit test over {points.Count} pointer positions: median {median:0.000} ms, 99th percentile {times[(int)(times.Count * 0.99)]:0.000} ms, worst {worst:0.000} ms");
        double p99 = times[(int)(times.Count * 0.99)];
        // Debug, dev PC: now median 0.9 / 99th percentile 1.6 / worst 2 ms; before the fix 0.9 / 4.7 / 7.6 (Release: 16 ms worst at one point).
        Assert.True(worst < 4.5, $"one hit test took {worst:0.0} ms");
        Assert.True(p99 < 3, $"1 in 100 hit tests took {p99:0.0} ms or more");
        Assert.True(median < 2, $"the typical hit test took {median:0.00} ms");

        static double Time(Action a) { long t = Stopwatch.GetTimestamp(); a(); return Ms(Stopwatch.GetTimestamp() - t); }
    }

    /// <summary>Press, six drag frames and the release on each kind of object, with the work of a frame split into what the UI thread does and what drawing costs.</summary>
    [LocalFact(File)]
    public void Grabbing_and_dragging_objects_in_the_big_layout_stays_smooth()
    {
        var (session, frames, surface) = Open();
        using var keepFrames = frames; using var keepSurface = surface;
        var doc = session.Document;
        var tops = doc.Layers.SelectMany(l => l.Children).Select(o => o.TopLevel).Distinct().ToList();   // what a click selects: single objects and whole groups
        DocObject Top(DocObject o) => o.TopLevel;
        int Count(DocObject o) => o is GroupObject g ? 1 + g.Children.Sum(Count) : 1;
        var targets = new (string, DocObject)[]
        {
            ("largest photo", Top(doc.AllObjects().OfType<ImageObject>().OrderByDescending(i => i.GetWorldBounds().Width * i.GetWorldBounds().Height).First())),
            ("longest text", Top(doc.AllObjects().OfType<TextObject>().OrderByDescending(t => t.GetWorldBounds().Width).First())),
            ("largest group", tops.OfType<GroupObject>().OrderByDescending(Count).First()),
            ("a shape", tops.First(o => o is PathObject)),
        };

        double worstPress = 0, worstFirst = 0, worstDrag = 0, worstRelease = 0, worstDraw = 0;
        foreach (var (label, target) in targets)
        {
            var b = target.GetWorldBounds();
            session.Viewport.ZoomToFit(b.Inflate(Math.Max(b.Width, b.Height) * 0.5));
            Vec2? hit = null;
            for (int i = 1; i < 40 && hit is null; i++)
                for (int j = 1; j < 40 && hit is null; j++)
                {
                    var p = new Vec2(b.Left + b.Width * i / 40, b.Top + b.Height * j / 40);
                    if (session.HitTestAt(p) == target) hit = p;
                }
            Assert.NotNull(hit);
            session.Selection.Clear();
            Frame(frames, surface, out _, out _);   // the picture of the layout is recorded here: not part of grabbing

            PointerInput At(Vec2 d) => new(session.Viewport.ToScreen(d), d, PointerButton.Left, ModifierKeys.None);
            long t0 = Stopwatch.GetTimestamp();
            session.PointerDown(At(hit.Value));
            Frame(frames, surface, out _, out _);
            double press = Ms(Stopwatch.GetTimestamp() - t0);

            var frameCosts = new List<double>();
            var drawCosts = new List<double>();
            Vec2 pos = hit.Value;
            for (int k = 1; k <= 6; k++)
            {
                pos = hit.Value + new Vec2(10 * k / session.Viewport.Zoom, 4 * k / session.Viewport.Zoom);
                t0 = Stopwatch.GetTimestamp();
                session.PointerMove(At(pos));
                Frame(frames, surface, out double build, out double draw);
                frameCosts.Add(Ms(Stopwatch.GetTimestamp() - t0) - draw);   // what the UI thread spends: input and recording; drawing is the render thread's
                drawCosts.Add(draw);
            }
            t0 = Stopwatch.GetTimestamp();
            session.PointerUp(At(pos));
            Frame(frames, surface, out _, out double releaseDraw);
            double release = Ms(Stopwatch.GetTimestamp() - t0) - releaseDraw;
            session.Undo();

            double first = frameCosts[0], steady = frameCosts.Skip(1).Max(), draw2 = drawCosts.Skip(1).Max();
            output.WriteLine($"{label,-14} press {press,6:0.0} ms | first drag frame {first,6:0.0} | next drag frames (UI) {steady,5:0.0}, drawing {draw2,5:0.0} | release {release,6:0.0}");
            worstPress = Math.Max(worstPress, press); worstFirst = Math.Max(worstFirst, first); worstDrag = Math.Max(worstDrag, steady);
            worstRelease = Math.Max(worstRelease, release); worstDraw = Math.Max(worstDraw, draw2);
        }

        // Measured in Debug on the dev PC (Release is about 2x faster, the old PC about 2.5x slower than the dev PC); budgets leave room for both.
        Assert.True(worstPress < 150, $"pressing on an object took {worstPress:0} ms");
        Assert.True(worstFirst < 150, $"the first drag frame took {worstFirst:0} ms");   // classifies the dragged outlines once: 19-22 ms
        Assert.True(worstDrag < 3, $"a drag frame cost the UI thread {worstDrag:0.0} ms");   // 0.3 ms; 5 and 16 ms (text, group of 107 shapes) when the loops were classified for every frame
        Assert.True(worstDraw < 8, $"drawing a drag frame took {worstDraw:0.0} ms");
        Assert.True(worstRelease < 250, $"letting go took {worstRelease:0} ms");
    }

    /// <summary>
    /// Click on an empty spot and drag a box over the layout (the rubber band): each frame only moves the box, so the picture under it must come from the kept
    /// bitmap. Before, every frame replayed all of it (about 18 ms per frame in software on the dev PC, 45 on the old one).
    /// </summary>
    [LocalFact(File)]
    public void Dragging_a_rubber_band_over_the_big_layout_stays_smooth()
    {
        var (session, frames, surface) = Open();
        using var keepFrames = frames; using var keepSurface = surface;
        double worstUi = 0, worstDraw = 0, worstPress = 0, worstRelease = 0;
        foreach (bool zoomedIn in new[] { false, true })
        {
            if (zoomedIn) session.Viewport.ZoomToFit(new Rect2(11000, -3250, 16300, 200)); else session.ZoomToContent();
            session.Selection.Clear();
            var view = session.Viewport.VisibleDocRect;
            Vec2? start = null;
            for (int i = 1; i < 60 && start is null; i++)
                for (int j = 1; j < 40 && start is null; j++)
                {
                    var p = new Vec2(view.Left + view.Width * i / 60, view.Top + view.Height * j / 40);
                    if (session.HitTestAt(p) is null && !session.Document.Page.Bounds.Inflate(40).Contains(p)) start = p;
                }
            Assert.NotNull(start);
            Frame(frames, surface, out _, out _);   // the picture of the layout is recorded here, and the kept bitmap is made on the next frame
            Frame(frames, surface, out _, out _);

            PointerInput At(Vec2 d) => new(session.Viewport.ToScreen(d), d, PointerButton.Left, ModifierKeys.None);
            long t0 = Stopwatch.GetTimestamp();
            session.PointerDown(At(start!.Value));
            Frame(frames, surface, out _, out double pressDraw);
            double press = Ms(Stopwatch.GetTimestamp() - t0);
            var ui = new List<double>(); var draws = new List<double>();
            Vec2 pos = start.Value;
            for (int k = 1; k <= 8; k++)
            {
                pos = start.Value + new Vec2(view.Width * 0.08 * k, view.Height * 0.06 * k);
                t0 = Stopwatch.GetTimestamp();
                session.PointerMove(At(pos));
                Frame(frames, surface, out _, out double draw);
                ui.Add(Ms(Stopwatch.GetTimestamp() - t0) - draw);
                draws.Add(draw);
            }
            t0 = Stopwatch.GetTimestamp();
            session.PointerUp(At(pos));
            Frame(frames, surface, out _, out double releaseDraw);
            double release = Ms(Stopwatch.GetTimestamp() - t0) - releaseDraw;
            double u = ui.Max(), d2 = draws.Max();
            output.WriteLine($"{(zoomedIn ? "flyers, zoomed in" : "whole layout")}: press {press:0.0} ms (drawing {pressDraw:0.0}) | band frames: UI {u:0.0} ms, drawing {d2:0.0} ms | release {release:0.0} ms, {session.Selection.Count} selected");
            session.Undo();
            worstUi = Math.Max(worstUi, u); worstDraw = Math.Max(worstDraw, Math.Max(d2, pressDraw)); worstPress = Math.Max(worstPress, press); worstRelease = Math.Max(worstRelease, release);
        }
        // Debug, dev PC: UI under 1 ms, drawing about 1 ms from the kept bitmap (18 to 19 ms drawn from the picture); release ends in a new group (3 to 11 ms).
        Assert.True(worstUi < 3, $"a rubber band frame cost the UI thread {worstUi:0.0} ms");
        Assert.True(worstDraw < 8, $"drawing a rubber band frame took {worstDraw:0.0} ms");
        Assert.True(worstPress < 100, $"pressing on empty canvas took {worstPress:0} ms");
        Assert.True(worstRelease < 150, $"letting go of the rubber band took {worstRelease:0} ms");
    }

    [LocalFact(File)]
    public void Frames_over_an_unchanged_picture_are_drawn_from_the_kept_bitmap_but_panning_and_zooming_never_make_one()
    {
        var (session, frames, surface) = Open();
        using var keepFrames = frames; using var keepSurface = surface;
        session.ZoomToContent();
        Frame(frames, surface, out _, out _);
        Assert.Equal(0, frames.StaticLayer.Rebuilds);               // one frame: nothing to compare with yet
        Frame(frames, surface, out _, out double second);
        Assert.Equal(1, frames.StaticLayer.Rebuilds);               // the same picture and view twice: kept
        Frame(frames, surface, out _, out double third);
        Assert.Equal(1, frames.StaticLayer.Rebuilds);
        output.WriteLine($"second frame (makes the bitmap) {second:0.0} ms, then {third:0.0} ms");
        Assert.True(third < 6, $"a frame over an unchanged picture took {third:0.0} ms");

        for (int i = 0; i < 6; i++)
        {
            session.Viewport.PanBy(new Vec2(40, 10));               // every frame is a new view: a bitmap would never be used again
            Frame(frames, surface, out _, out _);
        }
        for (int i = 0; i < 4; i++)
        {
            session.Wheel(new Vec2(800, 450), 1);
            Frame(frames, surface, out _, out _);
        }
        Assert.Equal(1, frames.StaticLayer.Rebuilds);
    }
    private static void Frame(CanvasFrameBuilder frames, SKSurface surface, out double buildMs, out double drawMs)
    {
        long t = Stopwatch.GetTimestamp();
        using var f = frames.Build(1600, 900);
        buildMs = Ms(Stopwatch.GetTimestamp() - t);
        t = Stopwatch.GetTimestamp();
        f.Draw(surface.Canvas);
        surface.Canvas.Flush();
        drawMs = Ms(Stopwatch.GetTimestamp() - t);
    }

    [LocalFact(File)]
    public void The_kept_bitmap_makes_dragging_in_the_big_layout_much_cheaper_to_draw()
    {
        var (session, frames, surface) = Open();
        using var keepFrames = frames; using var keepSurface = surface;
        var doc = session.Document;
        var dragged = doc.Layers.SelectMany(l => l.Children).Take(3).ToList();
        session.Preview = new TransformPreview(dragged, Affine2.Translation(30, 20));

        double Median(bool kept)
        {
            frames.UseStaticLayer = kept;
            var times = new List<double>();
            for (int i = 0; i < 12; i++)
            {
                session.Preview = new TransformPreview(dragged, Affine2.Translation(30 + i, 20 + i));
                Frame(frames, surface, out _, out double draw);
                if (i >= 2) times.Add(draw);
            }
            times.Sort();
            return times[times.Count / 2];
        }
        double direct = Median(false), kept = Median(true);
        output.WriteLine($"a drag frame drawn from the picture: {direct:0.0} ms, from the kept bitmap: {kept:0.0} ms");
        Assert.True(kept < direct * 0.5, $"drawing from the kept bitmap took {kept:0.0} ms against {direct:0.0} ms for the picture");
    }
}
