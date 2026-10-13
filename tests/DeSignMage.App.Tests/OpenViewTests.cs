using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeSignMage.App.Canvas;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using DeSignMage.TestSupport;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>A file opens fully zoomed out (R1): the page and every object on the pasteboard in view, as large as that allows.</summary>
public class OpenViewTests
{
    private static MainWindow Show(StartupOptions? options = null)
    {
        var w = new MainWindow(options ?? new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        w.DialogOverride = (_, _) => Task.FromResult(true);
        return w;
    }

    private static void Settle(MainWindow w)
    {
        for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); w.CaptureRenderedFrame(); }
    }

    /// <summary>The view a fresh <c>Zoom All</c> gives at the canvas's current size.</summary>
    private static void AssertFullyZoomedOut(MainWindow w)
    {
        var canvas = w.FindControl<DesignCanvas>("Surface")!;
        var doc = w.Session.Document;
        var content = doc.GetContentBounds().Union(doc.Page.Bounds);
        var expected = new Viewport();
        expected.SetViewSize(new Vec2(canvas.Bounds.Width, canvas.Bounds.Height));
        expected.ZoomToFit(content);

        var vp = w.Session.Viewport;
        Assert.Equal(canvas.Bounds.Width, vp.ViewSize.X, 3);
        Assert.Equal(canvas.Bounds.Height, vp.ViewSize.Y, 3);
        Assert.Equal(expected.Zoom, vp.Zoom, 9);
        Assert.Equal(expected.Pan.X, vp.Pan.X, 3);
        Assert.Equal(expected.Pan.Y, vp.Pan.Y, 3);
        Assert.Equal(0, vp.ZoomStackDepth);                      // right-click step-back-out starts fresh
        var tl = vp.ToScreen(content.TopLeft); var br = vp.ToScreen(content.BottomRight);
        Assert.True(tl.X >= 0 && tl.Y >= 0 && br.X <= canvas.Bounds.Width && br.Y <= canvas.Bounds.Height, $"content {tl} .. {br} in {canvas.Bounds.Size}");
    }

    private static string SaveWithObjectFarAway()
    {
        var doc = new Document(PageSetup.FromInches(8, 5));
        var s = new EditorSession(doc);
        s.History.Execute(new DeSignMage.Core.Commands.AddObjectCommand(doc.ActiveLayer, new RectangleObject(100, 60) { Transform = Affine2.Translation(30_000, -12_000) }));
        s.History.Execute(new DeSignMage.Core.Commands.AddObjectCommand(doc.ActiveLayer, new RectangleObject(50, 50) { Transform = Affine2.Translation(100, 100) }));
        var path = Path.Combine(Path.GetTempPath(), $"dsm-view-{Guid.NewGuid():N}.dsm");
        DsmWriter.Save(doc, path);
        return path;
    }

    [AvaloniaFact]
    public async Task A_document_with_an_object_far_off_the_page_opens_with_everything_in_view()
    {
        var w = Show();
        var path = SaveWithObjectFarAway();
        try
        {
            Assert.True(await w.OpenFileAsync(path));
            Settle(w);
            AssertFullyZoomedOut(w);
            Assert.True(w.Session.Viewport.VisibleDocRect.Contains(new Vec2(30_050, -11_970)));   // the far rectangle is on screen
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Opening_while_zoomed_in_with_a_zoom_history_starts_from_the_fully_zoomed_out_view()
    {
        var w = Show();
        var first = SaveWithObjectFarAway();
        try
        {
            w.Session.Viewport.PushAndZoomTo(new Rect2(0, 0, 40, 40));
            w.Session.Viewport.WheelZoom(new Vec2(300, 300), 3);
            Assert.True(await w.OpenFileAsync(first));
            Settle(w);
            AssertFullyZoomedOut(w);
        }
        finally { File.Delete(first); }
    }

    [AvaloniaFact]
    public async Task A_file_given_on_the_command_line_opens_fully_zoomed_out()
    {
        var path = SaveWithObjectFarAway();
        try
        {
            var w = Show(new StartupOptions(OpenPath: path));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (w.Session.Document.ObjectCount != 2 && watch.ElapsedMilliseconds < 10_000) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
            Assert.Equal(2, w.Session.Document.ObjectCount);
            Settle(w);
            AssertFullyZoomedOut(w);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task A_big_legacy_layout_opens_fully_zoomed_out()
    {
        Assert.SkipUnless(LocalData.Has("layouts/art-d.lyt"), "needs local-testdata/layouts/art-d.lyt (see the README)");
        var w = Show();
        Assert.True(await w.OpenFileAsync(LocalData.Layout("art-d.lyt")));
        Settle(w);
        AssertFullyZoomedOut(w);
        Assert.True(w.Session.Document.GetContentBounds().Width > w.Session.Document.Page.Bounds.Width * 2);   // the layout has pasteboard objects far to the right
    }

    [AvaloniaFact]
    public async Task The_view_is_refitted_when_the_window_changes_size_before_the_user_touches_it_and_left_alone_afterwards()
    {
        var w = Show();
        var path = SaveWithObjectFarAway();
        try
        {
            Assert.True(await w.OpenFileAsync(path));
            Settle(w);
            w.Width = 1000; w.Height = 700;                      // layout changes after the file came in (panels, scroll bars, a resized window)
            Settle(w);
            AssertFullyZoomedOut(w);

            w.Session.Viewport.WheelZoom(new Vec2(300, 300), 2);     // the user took over
            var kept = w.Session.Viewport.State;
            w.Width = 1300; w.Height = 800;
            Settle(w);
            Assert.Equal(kept.Zoom, w.Session.Viewport.Zoom, 9);
        }
        finally { File.Delete(path); }
    }
}
