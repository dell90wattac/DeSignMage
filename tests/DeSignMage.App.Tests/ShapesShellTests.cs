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

/// <summary>Layout ▸ Shapes, Convert to Rounded Rectangle and the Arrange loop cleanup commands in the real window (TASKS C.37).</summary>
public class ShapesShellTests
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

    private static MenuItem Top(MainWindow w, string name) =>
        w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().First(m => (m.Header as string)?.Contains(name) == true);

    private static MenuItem Sub(MenuItem menu, string name) =>
        menu.Items.OfType<MenuItem>().First(m => (m.Header as string)?.Replace("_", "").Contains(name) == true);

    private static void Open(MenuItem m) => m.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
    private static void Click(MenuItem m) => m.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static T Bar<T>(MainWindow w) where T : Control => w.GetVisualDescendants().OfType<T>().First(b => b.IsVisible);

    private static void Draw(MainWindow w, DesignCanvas c, Vec2 from, Vec2 to)
    {
        w.MouseDown(P(w, c, from), MouseButton.Left);
        w.MouseMove(P(w, c, (from + to) / 2));
        w.MouseMove(P(w, c, to));
        w.MouseUp(P(w, c, to), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static List<Loop> LoopsOf(DocObject o) => PathEditing.ToLoops(o.GetLocalGeometry()!);

    [AvaloniaFact]
    public void Layout_Shapes_lists_every_shape_and_the_convert_item_follows_the_selection()
    {
        var (w, _) = Open();
        var shapes = Sub(Top(w, "Layout"), "Shapes");
        Open(shapes);
        var texts = shapes.Items.OfType<MenuItem>().Select(i => ((string)i.Header!).Replace("_", "")).ToList();
        Assert.Equal(["Rectangle", "Rounded Rectangle", "Ellipse", "Square", "Circle", "Polygon / Star", "Arrow", "Convert to Rounded Rectangle"], texts);
        Assert.All(shapes.Items.OfType<MenuItem>().Where(i => (string)i.Header! is not ("_Rectangle" or "_Ellipse")), i => Assert.Null(i.InputGesture));   // no new shortcuts (the two old shapes already had theirs)
        Assert.False(Sub(shapes, "Convert to").IsEnabled);                                            // nothing selected

        var r = new RectangleObject(200, 100) { Transform = Affine2.Translation(300, 300), Style = ObjectStyle.FillOnly(DocColor.Black) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        w.Session.Selection.Set(r);
        Open(shapes);
        Assert.True(Sub(shapes, "Convert to").IsEnabled);

        var e = new EllipseObject(50, 50) { Transform = Affine2.Translation(600, 300) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, e));
        w.Session.Selection.Set(e);
        Open(shapes);
        Assert.False(Sub(shapes, "Convert to").IsEnabled);
        Assert.DoesNotContain(KeyMap.Default, k => k.CommandId.StartsWith("shape.") || k.CommandId is "tool.square" or "tool.circle" or "tool.polygon" or "tool.arrow" or "tool.roundedRectangle"
            or "arrange.closeAllLoops" or "arrange.removeInnerLoops" or "arrange.breakIntoOuterLoops");
        w.Close();
    }

    [AvaloniaFact]
    public void Each_shape_item_picks_its_tool_and_a_drag_with_the_mouse_draws_it()
    {
        var (w, c) = Open();
        var shapes = Sub(Top(w, "Layout"), "Shapes");
        foreach (var (item, tool, kind) in new[]
        {
            ("Rounded Rectangle", "roundedRectangle", typeof(RectangleObject)), ("Square", "square", typeof(RectangleObject)), ("Circle", "circle", typeof(EllipseObject)),
            ("Polygon", "polygon", typeof(PolygonObject)), ("Arrow", "arrow", typeof(ArrowObject)),
        })
        {
            Open(shapes);
            Click(Sub(shapes, item));
            Assert.Equal(tool, w.Session.ActiveTool.Id);
            int before = w.Session.Document.ObjectCount;
            Draw(w, c, new Vec2(700, 400), new Vec2(780, 440));
            Assert.Equal(before + 1, w.Session.Document.ObjectCount);
            Assert.IsType(kind, Assert.Single(w.Session.Selection.Items));
            Assert.Equal("select", w.Session.ActiveTool.Id);
            Assert.Equal(ObjectStyle.None, w.Session.Selection.Items[0].Style);                        // uncolored: drawn as an outline
            Assert.Same(w.Session.Selection.Items[0], w.Session.Document.ActiveLayer.Children[0]);    // at the bottom of the stack
        }
        w.Close();
    }

    [AvaloniaFact]
    public void The_polygon_bar_sets_the_sides_and_the_star_and_shows_the_inner_radius_only_for_a_star()
    {
        var (w, c) = Open();
        Click(Sub(Sub(Top(w, "Layout"), "Shapes"), "Polygon"));
        var bar = Bar<PolygonContextBar>(w);
        var fields = bar.Fields;
        Assert.False(fields.Inner.Panel.IsVisible);                                 // R26: nothing to set until it is a star

        fields.Sides.Box.Text = "8";
        fields.Sides.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(8, w.Session.GetTool<PolygonTool>().Sides);

        fields.StarBox.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.Session.GetTool<PolygonTool>().Star);
        Assert.True(fields.Inner.Panel.IsVisible);
        fields.Inner.Box.Text = "30";
        fields.Inner.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(0.3, w.Session.GetTool<PolygonTool>().InnerRatio, 6);

        Draw(w, c, new Vec2(800, 400), new Vec2(880, 400));
        var star = Assert.IsType<PolygonObject>(Assert.Single(w.Session.Selection.Items));
        Assert.Equal((8, 0.3), (star.Sides, star.InnerRatio));
        w.Close();
    }

    [AvaloniaFact]
    public void A_selected_polygon_or_arrow_can_be_reshaped_from_the_Layout_tools_bar_in_one_undo_step_each()
    {
        var (w, _) = Open();
        var poly = new PolygonObject(5, 60, 0, 0) { Transform = Affine2.Translation(400, 300), Style = ObjectStyle.FillOnly(DocColor.Black) };
        var arrow = new ArrowObject(200, 60, 0.4, 0.4) { Transform = Affine2.Translation(600, 300), Style = ObjectStyle.FillOnly(DocColor.Black) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, poly));
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, arrow));

        w.Session.Selection.Set(poly);
        Dispatcher.UIThread.RunJobs();
        var bar = Bar<SelectionContextBar>(w);
        var shape = bar.GetVisualDescendants().OfType<ShapeSelectionFields>().Single();
        Assert.True(shape.IsVisible);
        Assert.True(shape.Polygon.IsVisible);
        Assert.False(shape.Arrow.IsVisible);

        shape.Polygon.Sides.Box.Text = "9";
        shape.Polygon.Sides.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(9, poly.Sides);
        Assert.Equal("Polygon Sides", w.Session.History.UndoDescription);
        w.Session.Undo();
        Assert.Equal(5, poly.Sides);

        w.Session.Selection.Set(arrow);
        Dispatcher.UIThread.RunJobs();
        Assert.False(shape.Polygon.IsVisible);
        Assert.True(shape.Arrow.IsVisible);
        shape.Arrow.Head.Box.Text = "25";
        shape.Arrow.Head.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(0.25, arrow.HeadFraction, 6);
        shape.Arrow.HeadWidth.Box.Text = "50";
        shape.Arrow.HeadWidth.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(100, arrow.HeadWidth, 6);                                       // 50% of the length 200
        w.Session.Undo();
        Assert.Equal(60, arrow.HeadWidth, 6);

        w.Session.Selection.Set([poly, arrow]);                                      // a mix: nothing shape-specific to show
        Dispatcher.UIThread.RunJobs();
        Assert.False(shape.IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void Convert_to_Rounded_Rectangle_opens_the_radius_bar_previews_it_and_Done_applies_it()
    {
        var (w, c) = Open();
        var r = new RectangleObject(300, 150) { Transform = Affine2.Translation(400, 300), Style = ObjectStyle.FillOnly(DocColor.FromHex("#F2B705")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        w.Session.Selection.Set(r);
        var shapes = Sub(Top(w, "Layout"), "Shapes");
        Open(shapes);
        Click(Sub(shapes, "Convert to"));
        Assert.Equal("roundCorners", w.Session.ActiveTool.Id);

        var bar = Bar<RoundCornersContextBar>(w);
        Assert.True(bar.Done.IsVisible);
        bar.Radius.Box.Focus();                                                      // typing happens in the focused field, so Enter belongs to it
        bar.Radius.Box.Text = "0.5";
        bar.Radius.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(Units.FromInches(0.5), w.Session.GetTool<RoundCornersTool>().Radius, 6);
        Assert.Equal(0, r.CornerRadius);                                             // only a preview so far
        Assert.Single(w.Session.BuildOverlay().OfType<OutlineOverlay>());
        w.CaptureRenderedFrame();                                                    // the preview draws without error

        Click(bar.Done);
        Assert.Equal(Units.FromInches(0.5), r.CornerRadius, 6);
        Assert.Equal("select", w.Session.ActiveTool.Id);
        Assert.Equal("Convert to Rounded Rectangle", w.Session.History.UndoDescription);
        w.Session.Undo();
        Assert.Equal(0, r.CornerRadius);
        w.Close();
    }

    [AvaloniaFact]
    public void The_round_corners_bar_shows_nothing_to_set_when_the_selection_has_no_rectangle()
    {
        var (w, _) = Open();
        var e = new EllipseObject(50, 50) { Transform = Affine2.Translation(600, 300) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, e));
        w.Session.Selection.Set(e);
        w.Session.SetTool("roundCorners");                                           // reached by a test only: the menu item is disabled here
        Dispatcher.UIThread.RunJobs();
        var bar = Bar<RoundCornersContextBar>(w);
        Assert.False(bar.Done.IsVisible);
        Assert.False(bar.Radius.Panel.IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void The_Arrange_menu_has_the_three_loop_commands_and_they_work_from_the_menu()
    {
        var (w, _) = Open();
        var arrange = Top(w, "Arrange");
        Open(arrange);
        foreach (var name in new[] { "Break Into Outer Loops", "Close All Loops", "Remove All Inner Loops" })
        {
            var item = Sub(arrange, name);
            Assert.Null(item.InputGesture);
            Assert.False(item.IsEnabled);                                             // nothing selected
        }

        var frame = PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(0, 0, 200, 200)))[0];
        var hole = PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(50, 50, 100, 100)))[0];
        PathEditing.Reverse(hole);
        var open = new Loop { Start = new Vec2(300, 0), Segments = [Segment.Line(new Vec2(400, 0)), Segment.Line(new Vec2(400, 100))] };
        var path = new PathObject(PathEditing.FromLoops([frame, hole, open], FillRule.EvenOdd)) { Transform = Affine2.Translation(300, 300), Style = ObjectStyle.FillOnly(DocColor.Black) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, path));
        w.Session.Selection.Set(path);

        Open(arrange);
        Assert.True(Sub(arrange, "Break Into Outer Loops").IsEnabled);
        Assert.True(Sub(arrange, "Close All Loops").IsEnabled);
        Assert.True(Sub(arrange, "Remove All Inner Loops").IsEnabled);

        Click(Sub(arrange, "Close All Loops"));
        Assert.All(LoopsOf(path), l => Assert.True(l.Closed));
        Open(arrange);
        Assert.False(Sub(arrange, "Close All Loops").IsEnabled);                      // nothing left to close

        Click(Sub(arrange, "Remove All Inner Loops"));
        Assert.Equal(2, LoopsOf(path).Count);
        Open(arrange);
        Assert.False(Sub(arrange, "Remove All Inner Loops").IsEnabled);
        Assert.True(Sub(arrange, "Break Into Outer Loops").IsEnabled);                // two separate loops are left

        Click(Sub(arrange, "Break Into Outer Loops"));
        Assert.Equal(2, w.Session.Document.ObjectCount);
        Assert.DoesNotContain(path, w.Session.Document.ActiveLayer.Children);
        w.Close();
    }

    /// <summary>Saves shapes-*.png screenshots when DSW_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public void Screenshots_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSW_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        var (w, c) = Open();
        var gold = ObjectStyle.FillOnly(DocColor.FromHex("#F2B705"));
        var navy = ObjectStyle.FillOnly(DocColor.FromHex("#1F3A5F"));
        var s = w.Session;
        void Add(DocObject o) => s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, o));
        Add(new RectangleObject(180, 110, 28) { Transform = Affine2.Translation(120, 120), Style = gold });                    // rounded rectangle
        Add(new RectangleObject(110, 110) { Transform = Affine2.Translation(340, 120), Style = navy });                         // square
        Add(new EllipseObject(110, 110) { Transform = Affine2.Translation(500, 120), Style = gold });                           // circle
        Add(new PolygonObject(6, 62, 0, 0) { Transform = Affine2.Translation(720, 175), Style = navy });                        // hexagon
        Add(new PolygonObject(5, 70, 0.42) { Transform = Affine2.Translation(900, 178), Style = gold });                        // star
        Add(new ArrowObject(260, 90, 0.4, 0.4) { Transform = Affine2.Translation(1020, 175), Style = navy });                   // arrow
        Add(new PathObject(PathGeometry.Rectangle(Rect2.FromXYWH(0, 0, 200, 120))) { Transform = Affine2.Translation(160, 300), Style = gold });
        s.ZoomToContent();
        // Round the corners of the plain box path: the preview is the dashed outline.
        s.Selection.Set(s.Document.ActiveLayer.Children.OfType<PathObject>().First());
        s.BeginConvertToRoundedRectangle();
        s.GetTool<RoundCornersTool>().Radius = Units.FromInches(0.4);
        Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "shapes-round-corners.png"));

        s.SetTool("select");
        s.Selection.Set(s.Document.ActiveLayer.Children.OfType<PolygonObject>().First(p => p.IsStar));
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "shapes-gallery.png"));

        // Loop cleanup on a letter: before and after Break Into Outer Loops of "Bo".
        var t = new TextObject("Bo", "Arial", 130) { Transform = Affine2.Translation(500, 480), Style = navy };
        Add(t);
        s.Selection.Set(t);
        Dispatcher.UIThread.RunJobs();
        s.BreakIntoOuterLoopsSelection();
        foreach (var o in s.Document.ActiveLayer.Children.OfType<PathObject>().Where(p => p.Name is "Loop 1" or "Loop 2"))
            s.History.Execute(TransformObjectsCommand.ApplyWorld("Move", [o], Affine2.Translation(o.Name == "Loop 2" ? 40 : 0, 0)));
        s.Selection.Set(s.Document.ActiveLayer.Children.OfType<PathObject>().Where(p => p.Name is "Loop 2").ToList());
        s.ShowFillColors = false;
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "shapes-outer-loops.png"));
#pragma warning restore CS0618
        w.Close();
    }
}
