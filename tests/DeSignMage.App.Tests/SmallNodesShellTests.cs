using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Small vectors in the real window: the Options page and the delete box in Vector Edit.</summary>
public class SmallNodesShellTests
{
    private static (MainWindow Window, DesignCanvas Canvas) Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        var c = w.FindControl<DesignCanvas>("Surface")!;
        c.Focus();
        return (w, c);
    }

    private static Point P(MainWindow w, DesignCanvas c, Vec2 doc)
    {
        var s = w.Session.Viewport.ToScreen(doc);
        return c.TranslatePoint(new Point(s.X, s.Y), w).GetValueOrDefault();
    }

    /// <summary>A 400 point square (at 300, 250) whose top edge has a run of tracing noise: nodes 4 points apart.</summary>
    private static PathObject AddNoisySquare(MainWindow w, bool zigzag = false)
    {
        var loop = new Loop { Start = new Vec2(0, 0), Closed = true };
        var xs = new List<double> { 60, 64, 68, 72, 140, 144, 148, 220, 400 };
        int n = 0;
        foreach (double x in xs) loop.Segments.Add(Segment.Line(new Vec2(x, zigzag && x < 200 && n++ % 2 == 0 ? 3 : 0)));   // a zigzag makes the noise real corners, which show as squares
        foreach (var p in new[] { (400.0, 400.0), (0.0, 400.0), (0.0, 0.0) }) loop.Segments.Add(Segment.Line(new Vec2(p.Item1, p.Item2)));
        var path = new PathObject(PathEditing.FromLoops([loop], FillRule.NonZero)) { Transform = Affine2.Translation(300, 250), Style = ObjectStyle.FillOnly(DocColor.FromHex("#F2B705")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, path));
        return path;
    }

    private static List<Loop> LoopsOf(DocObject o) => PathEditing.ToLoops(o.GetLocalGeometry()!);

    [AvaloniaFact]
    public void Options_has_a_Vector_editing_page_whose_threshold_applies_and_is_remembered()
    {
        var oldStore = AppSettings.Store;
        AppSettings.Store = new SettingsStore(null);
        try
        {
            Assert.Contains(OptionsPages.All, p => p.Id == OptionsPages.VectorId && p.Title == "Vector editing");
            var page = (VectorOptionsPage)OptionsPages.All.First(p => p.Id == OptionsPages.VectorId).Create();
            Assert.Equal("0.25", page.ThresholdBox.Text);

            page.ThresholdBox.Text = "0.5";
            page.ThresholdBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal(0.5, SmallVectors.ThresholdInches, 9);
            Assert.Equal(0.5, AppSettings.Store.Get(VectorEditSettings.SmallThresholdKey, 0.0), 9);

            page.ThresholdBox.Text = "nonsense";                                 // not a number: the old value comes back
            page.ThresholdBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal("0.5", page.ThresholdBox.Text);

            page.ThresholdBox.Text = "50";                                        // too big: the largest allowed
            page.ThresholdBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal(SmallVectors.MaxInches, SmallVectors.ThresholdInches, 9);

            SmallVectors.ThresholdPoints = SmallVectors.DefaultPoints;           // a new run reads what was remembered
            AppSettings.Store.Set(VectorEditSettings.SmallThresholdKey, 0.75);
            VectorEditSettings.Load(AppSettings.Store);
            Assert.Equal(0.75, SmallVectors.ThresholdInches, 9);
        }
        finally
        {
            SmallVectors.ThresholdPoints = SmallVectors.DefaultPoints;
            AppSettings.Store = oldStore;
        }
    }

    [AvaloniaFact]
    public void Drawing_a_box_over_small_nodes_with_the_mouse_deletes_them_in_one_undo_step()
    {
        var (w, c) = Open();
        var path = AddNoisySquare(w);
        w.Session.EditVectors(path);
        var tool = w.Session.GetTool<VectorEditTool>();
        Assert.Equal(10, Assert.Single(LoopsOf(path)).Segments.Count - 2);       // 12 nodes: 4 corners and 8 on the top edge

        int steps = w.Session.History.UndoList.Count;
        // The box covers the four-point run at local x 60..72 and nothing else: from above the shape to below its top edge.
        var from = P(w, c, new Vec2(340, 215));
        var to = P(w, c, new Vec2(380, 290));
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(P(w, c, new Vec2(360, 250)));
        w.MouseMove(to);
        var marquee = Assert.Single(w.Session.BuildOverlay().OfType<MarqueeOverlay>());
        Assert.Equal(MarqueeKind.Delete, marquee.Kind);                           // red while it holds small nodes
        w.CaptureRenderedFrame();
        w.MouseUp(to, MouseButton.Left);

        Assert.Equal(steps + 1, w.Session.History.UndoList.Count);
        Assert.Equal("Delete Small Vectors", w.Session.History.UndoDescription);
        var loop = Assert.Single(LoopsOf(path));
        Assert.True(loop.Closed);
        Assert.Equal(8, loop.NodeCount);                                          // the run was 60, 64, 68, 72: all small, gone
        Assert.Equal(0, tool.SelectedNodeCount);
        w.Session.Undo();
        Assert.Equal(12, Assert.Single(LoopsOf(path)).NodeCount);
        w.Close();
    }

    /// <summary>Saves vector-small-nodes.png when DSW_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public void Screenshot_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSW_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        var (w, c) = Open();
        var path = AddNoisySquare(w, zigzag: true);
        w.Session.EditVectors(path);
        w.Session.Viewport.ZoomToFit(new Rect2(290, 215, 500, 285));   // the top edge, close up: tiny nodes next to the normal ones
        Dispatcher.UIThread.RunJobs();
        // The box is still being dragged over the second run of noise (red, because small nodes are inside).
        var from = P(w, c, new Vec2(432, 228));
        var to = P(w, c, new Vec2(458, 276));
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(P(w, c, new Vec2(450, 260)));
        w.MouseMove(to);
        Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "vector-small-nodes.png"));
#pragma warning restore CS0618
        w.MouseUp(to, MouseButton.Left);
        w.Close();
    }
}
