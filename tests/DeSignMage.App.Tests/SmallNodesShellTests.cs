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
    public void Options_has_a_Vector_editing_page_whose_settings_apply_and_are_remembered()
    {
        var oldStore = AppSettings.Store;
        AppSettings.Store = new SettingsStore(null);
        try
        {
            Assert.Contains(OptionsPages.All, p => p.Id == OptionsPages.VectorId && p.Title == "Vector editing");
            var page = (VectorOptionsPage)OptionsPages.All.First(p => p.Id == OptionsPages.VectorId).Create();
            Assert.Equal("0.25", page.ThresholdBox.Text);
            Assert.Equal("2", page.TangencyBox.Text);
            Assert.True(page.MarqueeBox.IsChecked);

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

            page.TangencyBox.Text = "5";
            page.TangencyBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal(5, VectorEditOptions.TangencyDegrees, 9);
            Assert.Equal(5, AppSettings.Store.Get(VectorEditSettings.TangencyKey, 0.0), 9);

            page.MarqueeBox.IsChecked = false;
            Assert.False(VectorEditOptions.MarqueeShiftSelects);
            Assert.False(AppSettings.Store.Get(VectorEditSettings.MarqueeShiftSelectsKey, true));

            SmallVectors.ThresholdPoints = SmallVectors.DefaultPoints;           // a new run reads what was remembered
            VectorEditOptions.TangencyDegrees = VectorEditOptions.DefaultTangencyDegrees;
            VectorEditOptions.MarqueeShiftSelects = true;
            AppSettings.Store.Set(VectorEditSettings.SmallThresholdKey, 0.75);
            VectorEditSettings.Load(AppSettings.Store);
            Assert.Equal(0.75, SmallVectors.ThresholdInches, 9);
            Assert.Equal(5, VectorEditOptions.TangencyDegrees, 9);
            Assert.False(VectorEditOptions.MarqueeShiftSelects);
        }
        finally
        {
            SmallVectors.ThresholdPoints = SmallVectors.DefaultPoints;
            VectorEditOptions.TangencyDegrees = VectorEditOptions.DefaultTangencyDegrees;
            VectorEditOptions.MarqueeShiftSelects = true;
            AppSettings.Store = oldStore;
        }
    }

    [AvaloniaFact]
    public void A_box_drawn_with_the_mouse_deletes_the_nodes_inside_in_one_undo_step_and_selects_the_next()
    {
        var (w, c) = Open();
        var path = AddNoisySquare(w);
        w.Session.EditVectors(path);
        var tool = w.Session.GetTool<VectorEditTool>();
        Assert.Equal(12, Assert.Single(LoopsOf(path)).NodeCount);                // 4 corners and 8 on the top edge

        int steps = w.Session.History.UndoList.Count;
        // The box covers the four-point run at local x 60..72 and nothing else.
        var from = P(w, c, new Vec2(340, 215));
        var to = P(w, c, new Vec2(380, 290));
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(P(w, c, new Vec2(360, 250)));
        w.MouseMove(to);
        var marquee = Assert.Single(w.Session.BuildOverlay().OfType<MarqueeOverlay>());
        Assert.Equal(MarqueeKind.Delete, marquee.Kind);                           // red: this box deletes
        w.CaptureRenderedFrame();
        w.MouseUp(to, MouseButton.Left);

        Assert.Equal(steps + 1, w.Session.History.UndoList.Count);
        Assert.Equal("Delete Vectors", w.Session.History.UndoDescription);
        var loop = Assert.Single(LoopsOf(path));
        Assert.True(loop.Closed);
        Assert.Equal(8, loop.NodeCount);
        Assert.Equal(1, tool.SelectedNodeCount);
        Assert.Equal(new Vec2(440, 250), tool.AnchorPosition());                  // local x 140, the node after the last one deleted
        w.Session.Undo();
        Assert.Equal(12, Assert.Single(LoopsOf(path)).NodeCount);
        w.Close();
    }

    [AvaloniaFact]
    public void A_box_over_a_start_point_beeps_and_changes_nothing_and_Shift_selects_instead()
    {
        var (w, c) = Open();
        int beeps = 0;
        w.Session.Beep = () => beeps++;
        var path = AddNoisySquare(w);
        w.Session.EditVectors(path);
        var tool = w.Session.GetTool<VectorEditTool>();
        int steps = w.Session.History.UndoList.Count;

        var from = P(w, c, new Vec2(280, 230));                                   // around the start at (300,250) and the first nodes after it
        var to = P(w, c, new Vec2(370, 270));
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(P(w, c, new Vec2(330, 250)));
        w.MouseMove(to);
        w.MouseUp(to, MouseButton.Left);
        Assert.Equal(1, beeps);
        Assert.Equal(steps, w.Session.History.UndoList.Count);
        Assert.Equal(12, Assert.Single(LoopsOf(path)).NodeCount);

        w.MouseDown(from, MouseButton.Left, RawInputModifiers.Shift);              // Shift: the same box selects, start point included
        w.MouseMove(P(w, c, new Vec2(330, 250)), RawInputModifiers.Shift);
        w.MouseMove(to, RawInputModifiers.Shift);
        w.MouseUp(to, MouseButton.Left, RawInputModifiers.Shift);
        Assert.Equal(1, beeps);
        Assert.Equal(4, tool.SelectedNodeCount);                                   // the start and the nodes at local x 60, 64, 68
        Assert.Equal(steps, w.Session.History.UndoList.Count);
        w.Close();
    }

    [AvaloniaFact]
    public void View_menu_has_Highlight_Small_Vectors_which_shows_the_marks_and_is_remembered()
    {
        var oldStore = AppSettings.Store;
        AppSettings.Store = new SettingsStore(null);
        try
        {
            var (w, _) = Open();
            AddNoisySquare(w);
            Assert.Empty(w.Session.BuildOverlay().OfType<SmallVectorMarksOverlay>());
            var view = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => ((string)m.Header!).Replace("_", "") == "View");
            var item = view.Items.OfType<MenuItem>().Single(m => ((string)m.Header!).Replace("_", "") == "Highlight Small Vectors");
            var cmd = (AppCommand)item.Tag!;
            Assert.Null(cmd.Gesture);                                                // no shortcut
            cmd.Execute();
            Assert.True(VectorEditOptions.HighlightSmallVectors);
            Assert.True(AppSettings.Store.Get(VectorEditSettings.HighlightSmallKey, false));
            Assert.Equal(5, Assert.Single(w.Session.BuildOverlay().OfType<SmallVectorMarksOverlay>()).Ends.Count);   // the nodes at 64, 68, 72, 144, 148
            cmd.Execute();
            Assert.False(VectorEditOptions.HighlightSmallVectors);
            w.Close();
        }
        finally
        {
            VectorEditOptions.HighlightSmallVectors = false;
            AppSettings.Store = oldStore;
        }
    }

    /// <summary>
    /// Saves vector-small-nodes.png (every marker kind, the small-vector highlight) and vector-delete-box.png (a box being dragged) when
    /// DSW_SCREENSHOTS names a folder (docs only).
    /// </summary>
    [AvaloniaFact]
    public void Screenshot_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSW_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        var (w, c) = Open();
        // Local nodes: 0 (0,0) the start; a run of corners 8 points apart; (140,0) twice (the second one coincident); a smooth join at (220,-40)
        // (tangent, a cross); a corner at (400,0); then down and round to the start.
        var loop = new Loop { Start = new Vec2(0, 0), Closed = true };
        foreach (var p in new[] { (60.0, 0.0), (68, 6), (76, 0), (84, 6), (140, 0), (140, 0) }) loop.Segments.Add(Segment.Line(new Vec2(p.Item1, p.Item2)));
        loop.Segments.Add(Segment.Cubic(new Vec2(170, 0), new Vec2(190, -40), new Vec2(220, -40)));
        loop.Segments.Add(Segment.Cubic(new Vec2(250, -40), new Vec2(370, -40), new Vec2(400, 0)));
        foreach (var p in new[] { (400.0, 400.0), (0.0, 400.0), (0.0, 0.0) }) loop.Segments.Add(Segment.Line(new Vec2(p.Item1, p.Item2)));
        var path = new PathObject(PathEditing.FromLoops([loop], FillRule.NonZero)) { Transform = Affine2.Translation(300, 300), Style = ObjectStyle.FillOnly(DocColor.FromHex("#F2B705")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, path));
        w.Session.EditVectors(path);
        VectorEditOptions.HighlightSmallVectors = true;
        try
        {
            w.Session.Viewport.ZoomToFit(new Rect2(290, 255, 540, 335));            // the left part of the top edge, close up
            Dispatcher.UIThread.RunJobs();
            var node = P(w, c, new Vec2(368, 306));                                  // select one of the corners 8 points apart: the 8 point mark
            w.MouseDown(node, MouseButton.Left);
            w.MouseUp(node, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
            w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "vector-small-nodes.png"));

            // A box over the last two corners of the run, red because it would delete.
            var from = P(w, c, new Vec2(372, 290));
            var to = P(w, c, new Vec2(390, 312));
            w.MouseDown(from, MouseButton.Left);
            w.MouseMove(P(w, c, new Vec2(380, 300)));
            w.MouseMove(to);
            Dispatcher.UIThread.RunJobs();
            w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "vector-delete-box.png"));
#pragma warning restore CS0618
            w.MouseUp(to, MouseButton.Left);
        }
        finally { VectorEditOptions.HighlightSmallVectors = false; }
        w.Close();
    }
}