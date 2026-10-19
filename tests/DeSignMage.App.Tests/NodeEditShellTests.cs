using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Node editing (C.1) in the real window: dragging and adding nodes, the two Context Bars, the info line, the loop tool.</summary>
public class NodeEditShellTests
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

    private static void DoubleClick(MainWindow w, Point p)
    {
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
    }

    private static PathObject AddBlob(MainWindow w, double x, double y)
    {
        var geometry = PathEditing.FromLoops(
        [
            new Loop
            {
                Start = new Vec2(0, 0),
                Segments =
                [
                    Segment.Cubic(new Vec2(30, -40), new Vec2(70, -40), new Vec2(100, 0)),
                    Segment.Cubic(new Vec2(145, 60), new Vec2(90, 120), new Vec2(50, 80)),
                    Segment.Cubic(new Vec2(10, 40), new Vec2(-30, -40), new Vec2(0, 0)),
                ],
                Closed = true,
            },
        ], FillRule.NonZero);
        var p = new PathObject(geometry) { Transform = Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(DocColor.FromHex("#F2B705")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, p));
        return p;
    }

    private static List<Loop> LoopsOf(DocObject o) => PathEditing.ToLoops(o.GetLocalGeometry()!);

    private static T Bar<T>(MainWindow w) where T : Control => Assert.IsType<T>(w.FindControl<ContentControl>("ContextBarHost")!.Content);

    private static Button ButtonNamed(Control bar, string text) =>
        bar.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == text);

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void Dragging_a_node_with_the_mouse_moves_that_node_and_ctrl_z_puts_it_back()
    {
        var (w, c) = Open();
        var blob = AddBlob(w, 400, 300);
        w.Session.EditVectors(blob);

        var node = P(w, c, new Vec2(500, 300));   // node 1, local (100,0)
        w.MouseDown(node, MouseButton.Left);
        w.MouseMove(node + new Point(10, 10));
        w.MouseMove(node + new Point(30, 20));
        w.MouseUp(node + new Point(30, 20), MouseButton.Left);

        var moved = LoopsOf(blob)[0].Node(1);
        Assert.NotEqual(new Vec2(100, 0), moved);
        Assert.Equal(new Vec2(0, 0), LoopsOf(blob)[0].Node(0));   // the others stayed
        Assert.Equal("Move Nodes", w.Session.History.UndoDescription);

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(new Vec2(100, 0), LoopsOf(blob)[0].Node(1));
        w.Close();
    }

    [AvaloniaFact]
    public void Double_click_a_vector_and_choose_Insert_adds_a_node_there()
    {
        var (w, c) = Open();
        var rect = new RectangleObject(300, 300) { Transform = Affine2.Translation(200, 200), Style = ObjectStyle.FillOnly(DocColor.FromHex("#F2B705")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, rect));
        w.Session.EditVectors(rect);
        Assert.Equal(4, w.Session.GetTool<VectorEditTool>().Stats().Segments);

        DoubleClick(w, P(w, c, new Vec2(320, 200)));   // on the top vector
        var menu = w.LastObjectMenu!;
        Assert.True(menu.IsOpen);
        var insert = menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "_Insert");
        insert.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();

        var path = Assert.IsType<PathObject>(Assert.Single(w.Session.Document.ActiveLayer.Children));
        var loop = LoopsOf(path)[0];
        Assert.Equal(5, loop.Segments.Count);
        Assert.Equal(120, loop.Node(1).X, 0);        // the new node sits where it was clicked (local x = 320 - 200)
        Assert.Equal(0, loop.Node(1).Y, 6);
        Assert.Equal("Insert Node", w.Session.History.UndoDescription);
        w.Close();
    }

    [AvaloniaFact]
    public void The_vector_bar_shows_the_selected_node_and_typing_X_and_Y_moves_it()
    {
        var (w, c) = Open();
        var blob = AddBlob(w, 400, 300);
        w.Session.EditVectors(blob);
        var node = P(w, c, new Vec2(500, 300));
        w.MouseDown(node, MouseButton.Left);
        w.MouseUp(node, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var bar = Bar<VectorEditContextBar>(w);
        var boxes = bar.GetVisualDescendants().OfType<TextBox>().ToList();
        var (ux, uy) = Units.ToUser(new Vec2(500, 300), w.Session.Document.Page);
        Assert.Equal(ux, double.Parse(boxes[0].Text!, System.Globalization.CultureInfo.InvariantCulture), 3);
        Assert.Equal(uy, double.Parse(boxes[1].Text!, System.Globalization.CultureInfo.InvariantCulture), 3);
        Assert.Contains("Smooth", bar.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).First(t => t?.StartsWith("Node:") == true)!);

        boxes[0].Text = (ux + 0.5).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Dispatcher.UIThread.RunJobs();
        boxes[0].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(100 + Units.FromInches(0.5), LoopsOf(blob)[0].Node(1).X, 3);
        Assert.Equal(0, LoopsOf(blob)[0].Node(1).Y, 6);   // only X was typed: Y did not drift
        w.Close();
    }

    [AvaloniaFact]
    public void The_vector_bar_buttons_work_and_follow_the_selection()
    {
        var (w, c) = Open();
        var blob = AddBlob(w, 400, 300);
        w.Session.EditVectors(blob);
        var bar = Bar<VectorEditContextBar>(w);
        Assert.False(ButtonNamed(bar, "Corner").IsEnabled);      // nothing selected yet

        var node = P(w, c, new Vec2(500, 300));
        w.MouseDown(node, MouseButton.Left);
        w.MouseUp(node, MouseButton.Left);
        Assert.True(ButtonNamed(bar, "Corner").IsEnabled);       // a smooth node can become a corner
        Click(ButtonNamed(bar, "Corner"));
        Assert.Equal(NodeKind.Corner, PathEditing.KindOf(LoopsOf(blob)[0], 1));
        Assert.False(ButtonNamed(bar, "Corner").IsEnabled);
        Click(ButtonNamed(bar, "Smooth"));
        Assert.NotEqual(NodeKind.Corner, PathEditing.KindOf(LoopsOf(blob)[0], 1));
        Click(ButtonNamed(bar, "Delete"));                       // deletes the selected node
        Assert.Equal(2, LoopsOf(blob)[0].Segments.Count);
        w.Close();
    }

    [AvaloniaFact]
    public void The_info_line_describes_the_open_graphic()
    {
        var (w, _) = Open();
        var blob = AddBlob(w, 400, 300);
        w.Session.EditVectors(blob);
        Dispatcher.UIThread.RunJobs();
        var info = w.FindControl<TextBlock>("InfoLine")!.Text!;
        Assert.StartsWith("Graphic  4 Vectors", info);          // 3 segments + the start
        Assert.Contains("0 lines  0 arcs  3 beziers", info);
        w.Session.SetTool("select");
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("1 object · 0 groups", w.FindControl<TextBlock>("InfoLine")!.Text!);
        w.Close();
    }

    [AvaloniaFact]
    public void The_loop_tool_is_reached_from_the_Tools_menu_has_a_bar_and_its_buttons_work()
    {
        var (w, c) = Open();
        var outer = PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(0, 0, 400, 400)))[0];
        var inner = PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(100, 100, 200, 200)))[0];
        PathEditing.Reverse(inner);
        var p = new PathObject(PathEditing.FromLoops([outer, inner], FillRule.EvenOdd))
        {
            Transform = Affine2.Translation(300, 200),
            Style = ObjectStyle.FillOnly(DocColor.FromHex("#336699")),
        };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, p));
        w.Session.Selection.Set(p);

        // The Loop Edit tool has no Toolbox button; Tools > Loop Edit Tool selects it.
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>(),
            b => Avalonia.Controls.ToolTip.GetTip(b) is string tip && tip.StartsWith("Loop Edit tool"));
        var tools = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => ((string)m.Header!).Replace("_", "") == "Tools");
        tools.Items.OfType<MenuItem>().Single(m => m.Tag is AppCommand { Id: "tool.loop" }).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("loop", w.Session.ActiveTool.Id);
        Assert.Same(p, w.Session.GetTool<LoopEditTool>().Target);

        var bar = Bar<LoopEditContextBar>(w);
        Assert.False(ButtonNamed(bar, "Reverse").IsEnabled);        // no loop selected

        var edge = P(w, c, new Vec2(400, 350));                     // the inner loop's left vector
        w.MouseDown(edge, MouseButton.Left);
        w.MouseUp(edge, MouseButton.Left);
        Assert.True(ButtonNamed(bar, "Reverse").IsEnabled);
        Assert.False(LoopsOf(p)[1].IsClockwise);
        Click(ButtonNamed(bar, "Reverse"));
        Assert.True(LoopsOf(p)[1].IsClockwise);
        Assert.Equal("Reverse Loop", w.Session.History.UndoDescription);

        // dragging moves just that loop (pressed elsewhere on the loop: a second press on the same spot would be a double-click)
        var grab = P(w, c, new Vec2(400, 450));
        w.MouseDown(grab, MouseButton.Left);
        w.MouseMove(grab + new Point(10, 0));
        w.MouseMove(grab + new Point(40, 0));
        w.MouseUp(grab + new Point(40, 0), MouseButton.Left);
        Assert.Equal(new Rect2(0, 0, 400, 400), LoopsOf(p)[0].Bounds());
        Assert.True(LoopsOf(p)[1].Bounds().Left > 100);
        w.Close();
    }

    [AvaloniaFact]
    public void Right_button_zoom_still_works_inside_the_node_tools()
    {
        var (w, c) = Open();
        var blob = AddBlob(w, 400, 300);
        w.Session.EditVectors(blob);
        double zoom = w.Session.Viewport.Zoom;
        var a = P(w, c, new Vec2(380, 250));
        var b = P(w, c, new Vec2(560, 400));
        w.MouseDown(a, MouseButton.Right);
        w.MouseMove(b);
        w.MouseUp(b, MouseButton.Right);
        Assert.True(w.Session.Viewport.Zoom > zoom);
        Assert.Equal(0, w.Session.GetTool<VectorEditTool>().SelectedNodeCount);
        w.Close();
    }

    /// <summary>Saves a screenshot when DSM_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public void Screenshot_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        var w = new MainWindow(new StartupOptions(Demo: true)) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        var c = w.FindControl<DesignCanvas>("Surface")!;
        var layer = w.Session.Document.ActiveLayer;
        // A letter-like graphic: a smooth blob with a hole, so nodes, handles and loops all show.
        var blob = AddBlob(w, 700, 420);
        w.Session.Viewport.ZoomToFit(blob.GetWorldBounds().Inflate(60));
        w.Session.EditVectors(blob);
        var node = P(w, c, new Vec2(800, 420));
        w.MouseDown(node, MouseButton.Left);
        w.MouseUp(node, MouseButton.Left);
        w.MouseMove(P(w, c, new Vec2(750, 500)));
        Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "node-edit.png"));
#pragma warning restore CS0618
        w.Close();
    }
}
