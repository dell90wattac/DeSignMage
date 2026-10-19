using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
using DeSignMage.Formats.Native;
using DeSignMage.TestSupport;
using Xunit;
using Button = Avalonia.Controls.Button;

namespace DeSignMage.App.Tests;

/// <summary>
/// Every button outside the dropdown menus, pressed in a state where it is enabled, with the effect checked: Main Toolbar, Context Bar
/// (every tool), Project Color Bar, Color Mixer, Palette Strip, Toolbox and Layers Panel. Buttons that did nothing were removed instead
/// (see the notes of the UI cleanup); so this list is also the list of what the program's own buttons do.
/// </summary>
public class ButtonAuditTests
{
    private static (MainWindow W, DesignCanvas C) Open(double width = 1400)
    {
        var w = new MainWindow(new StartupOptions()) { Width = width, Height = 860 };
        w.Show();
        Flush();
        w.CaptureRenderedFrame();
        var c = w.FindControl<DesignCanvas>("Surface")!;
        c.Focus();
        return (w, c);
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static void Pump(Func<bool> done, int timeoutMs = 10000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < timeoutMs)
        {
            Flush();
            Thread.Sleep(10);
        }
        Flush();
    }

    /// <summary>A real mouse click on the middle of a control.</summary>
    private static void Press(MainWindow w, Control c)
    {
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w).GetValueOrDefault();
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Flush();
    }

    private static T Bar<T>(MainWindow w) where T : Control => Assert.IsType<T>(w.FindControl<ContentControl>("ContextBarHost")!.Content);

    private static Button TextButton(Control root, string text) =>
        root.GetVisualDescendants().OfType<Button>().Single(b => b.IsVisible && b.Content as string == text);

    private static Button NamedButton(MainWindow w, string name) =>
        w.GetVisualDescendants().OfType<Button>().First(b => b.Name == name);

    private static RectangleObject AddRect(MainWindow w, double x, double y, double width = 120, double height = 80, ObjectStyle? style = null)
    {
        var r = new RectangleObject(width, height) { Transform = Affine2.Translation(x, y), Style = style ?? ObjectStyle.FillOnly(DocColor.FromHex("#336699")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        Flush();
        return r;
    }

    private static int Steps(MainWindow w) => w.Session.History.UndoList.Count;

    // ================================================================ Main Toolbar

    private static List<Button> ToolbarButtons(MainWindow w) => w.FindControl<BarRow>("MainToolbar")!.Children.OfType<Button>().ToList();

    [AvaloniaFact]
    public void The_Main_Toolbar_has_thirteen_buttons_and_no_vector_mode_button()
    {
        var (w, _) = Open();
        var tips = ToolbarButtons(w).Select(b => ToolTip.GetTip(b)?.ToString() ?? "").ToList();
        Assert.Equal(13, tips.Count);
        Assert.DoesNotContain(tips, t => t.Contains("Vector Mode", StringComparison.OrdinalIgnoreCase));
        // the Toolbox button and View > Show Fill Colors are what is left for that setting
        Assert.NotNull(w.GetVisualDescendants().OfType<ToggleButton>().SingleOrDefault(b => b.Name == "ToolboxColorMode"));
        w.Close();
    }

    [AvaloniaFact]
    public void Main_Toolbar_edit_and_zoom_buttons_do_their_job()
    {
        var (w, _) = Open();
        var tb = ToolbarButtons(w);
        Button B(string tip) => tb.Single(b => ToolTip.GetTip(b)?.ToString()!.Contains(tip) == true);

        // Zoom In / Zoom Out
        double z0 = w.Session.Viewport.ZoomPercent;
        Press(w, B("Zoom In"));
        double zIn = w.Session.Viewport.ZoomPercent;
        Assert.True(zIn > z0);
        Press(w, B("Zoom Out"));
        Assert.True(w.Session.Viewport.ZoomPercent < zIn);

        // Zoom Layout (the page), Zoom All
        Press(w, B("Zoom In")); Press(w, B("Zoom In"));
        double zoomed = w.Session.Viewport.ZoomPercent;
        Press(w, B("Zoom Layout"));
        Assert.True(w.Session.Viewport.ZoomPercent < zoomed);
        Press(w, B("Zoom In")); Press(w, B("Zoom In"));
        zoomed = w.Session.Viewport.ZoomPercent;
        Press(w, B("Zoom All"));
        Assert.True(w.Session.Viewport.ZoomPercent < zoomed);

        // Zoom Object: off without a selection, then fits the selection
        Assert.False(B("Zoom Object").IsEnabled);
        var r = AddRect(w, 100, 100, 60, 40);
        w.Session.Selection.Set(r);
        Flush();
        Assert.True(B("Zoom Object").IsEnabled);
        double before = w.Session.Viewport.ZoomPercent;
        Press(w, B("Zoom Object"));
        Assert.True(w.Session.Viewport.ZoomPercent > before);

        // Delete, Undo, Redo
        Assert.True(B("Delete").IsEnabled);
        Press(w, B("Delete"));
        Assert.DoesNotContain(r, w.Session.Document.ActiveLayer.Children);
        Press(w, B("Undo"));
        Assert.Contains(r, w.Session.Document.ActiveLayer.Children);
        Press(w, B("Redo"));
        Assert.DoesNotContain(r, w.Session.Document.ActiveLayer.Children);
        Assert.False(B("Redo").IsEnabled);

        // Show Guidelines, Snap to Guidelines
        bool show = w.Session.ShowGuides, snap = w.Session.SnapToGuides;
        Press(w, B("Show Guidelines"));
        Assert.Equal(!show, w.Session.ShowGuides);
        Press(w, B("Snap to Guidelines"));
        Assert.Equal(!snap, w.Session.SnapToGuides);
        Press(w, B("Show Guidelines"));
        Assert.Equal(show, w.Session.ShowGuides);
        w.Close();
    }

    [AvaloniaFact]
    public void Main_Toolbar_New_Open_and_Save_buttons_do_their_job()
    {
        var (w, _) = Open();
        var tb = ToolbarButtons(w);
        var dir = Directory.CreateTempSubdirectory("dsm-audit-").FullName;
        try
        {
            AddRect(w, 50, 50);
            var path = Path.Combine(dir, "audit.dsm");
            w.SavePathOverride = _ => path;
            Press(w, tb[2]);                                              // Save
            Pump(() => File.Exists(path) && !w.Session.History.IsModified);
            Assert.True(File.Exists(path));
            Assert.Equal(1, DsmReader.Load(path).Document.ObjectCount);

            AddRect(w, 300, 50);                                          // now two objects, unsaved
            w.SaveChangesOverride = (_, _) => Task.FromResult(MainWindow.SaveChoice.Discard);
            var old = w.Session;
            Press(w, tb[0]);                                              // New
            Pump(() => !ReferenceEquals(w.Session, old));
            Assert.NotSame(old, w.Session);
            Assert.Equal(0, w.Session.Document.ObjectCount);

            w.OpenPathOverride = () => Task.FromResult<string?>(path);
            Press(w, tb[1]);                                              // Open
            Pump(() => w.Session.Document.ObjectCount == 1);
            Assert.Equal(1, w.Session.Document.ObjectCount);
        }
        finally { Directory.Delete(dir, true); }
        w.Close();
    }

    // ================================================================ Toolbox

    [AvaloniaFact]
    public void Every_Toolbox_button_selects_its_tool_and_the_color_mode_button_flips_the_view()
    {
        var (w, _) = Open();
        var host = w.FindControl<StackPanel>("ToolboxHost")!;
        var toggles = host.Children.OfType<ToggleButton>().ToList();
        Assert.Equal(Toolbox.Buttons.Length + 1, toggles.Count);
        for (int i = 0; i < Toolbox.Buttons.Length; i++)
        {
            Press(w, toggles[i]);
            Assert.Equal(Toolbox.Buttons[i].ToolId, w.Session.ActiveTool.Id);
        }
        w.Session.SetTool("select");

        var colorMode = toggles[^1];
        Assert.Equal("ToolboxColorMode", colorMode.Name);
        Assert.True(w.Session.ShowFillColors);
        Press(w, colorMode);
        Assert.False(w.Session.ShowFillColors);
        Assert.True(colorMode.IsChecked);
        Press(w, colorMode);
        Assert.True(w.Session.ShowFillColors);
        w.Close();
    }

    // ================================================================ Context Bar: Select tool

    [AvaloniaFact]
    public void Select_bar_group_buttons_group_ungroup_combine_and_break_apart()
    {
        var (w, _) = Open();
        var bar = Bar<SelectionContextBar>(w);
        var a = AddRect(w, 50, 50); var b = AddRect(w, 250, 50);
        w.Session.Selection.Clear();
        Flush();
        Assert.All(new[] { bar.Group, bar.Ungroup, bar.Combine, bar.BreakApart }, x => Assert.False(x.IsEnabled));   // nothing selected

        w.Session.Selection.Set([a, b]);
        Flush();
        Assert.True(bar.Group.IsEnabled);
        Press(w, bar.Group);
        var group = Assert.IsType<GroupObject>(a.Parent);
        Assert.Contains(b, group.Children);

        w.Session.Selection.Set(group);
        Flush();
        Assert.True(bar.Ungroup.IsEnabled);
        Press(w, bar.Ungroup);
        Assert.IsNotType<GroupObject>(a.Parent);

        w.Session.Selection.Set([a, b]);
        Flush();
        Assert.True(bar.Combine.IsEnabled);
        Press(w, bar.Combine);
        var combined = Assert.IsType<CombinedObject>(Assert.Single(w.Session.Document.ActiveLayer.Children));
        w.Session.Selection.Set(combined);
        Flush();
        Assert.True(bar.BreakApart.IsEnabled);
        Press(w, bar.BreakApart);
        Assert.DoesNotContain(combined, w.Session.Document.ActiveLayer.Children);
        Assert.Equal(2, w.Session.Document.ActiveLayer.Children.Count);
        w.Close();
    }

    [AvaloniaFact]
    public void Select_bar_X_Y_W_H_fields_move_and_size_the_selection()
    {
        var (w, _) = Open();
        var bar = Bar<SelectionContextBar>(w);
        var r = AddRect(w, 100, 100, 72, 36);                       // 1 x 0.5 in
        w.Session.Selection.Set(r);
        Flush();
        Assert.Equal("1", bar.W.Text);
        bar.W.Focus();
        bar.W.Text = "2";
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Flush();
        Assert.Equal(144, r.GetWorldBounds().Width, 3);
        bar.X.Focus();
        bar.X.Text = "0.5";
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Flush();
        Assert.Equal(Units.FromInches(0.5), r.GetWorldBounds().Left - w.Session.Document.Page.Bounds.Left, 3);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Select_bar_picture_buttons_rotate_flip_invert_crop_and_one_to_one()
    {
        var (w, _) = Open(1800);   // the picture row is long, and the Context Bar's right end holds the menu tabs (R25): the buttons must be on screen to be pressed
        var dir = Directory.CreateTempSubdirectory("dsm-audit-").FullName;
        try
        {
            var pixels = new DeSignMage.Imaging.PixelBuffer(60, 30);
            for (int y = 0; y < 30; y++)
                for (int x = 0; x < 60; x++) pixels.Set(x, y, (byte)(x * 4), 80, 200, 255);
            var path = Path.Combine(dir, "p.png");
            File.WriteAllBytes(path, DeSignMage.Imaging.ImageCodec.EncodePng(pixels));
            Assert.Equal(1, await w.ImportImagesAsync([path], null));
            Flush();
            var bar = Bar<SelectionContextBar>(w);

            foreach (var (text, description) in new[]
                     {
                         ("Rotate ↻", "Rotate 90 Degrees Clockwise"), ("Rotate ↺", "Rotate 90 Degrees Counter-clockwise"),
                         ("Flip H", "Flip Horizontal"), ("Flip V", "Flip Vertical"), ("Invert", "Invert"),
                     })
            {
                int steps = Steps(w);
                var button = TextButton(bar, text);
                Assert.True(button.IsEnabled, text);
                Press(w, button);
                Pump(() => Steps(w) > steps);
                Assert.Equal(steps + 1, Steps(w));
                Assert.Equal(description, w.Session.History.UndoDescription);
            }

            double zoom = w.Session.Viewport.Zoom;
            Press(w, TextButton(bar, "1:1"));
            Assert.NotEqual(zoom, w.Session.Viewport.Zoom);

            Press(w, TextButton(bar, "Crop"));
            Assert.IsType<CropTool>(w.Session.ActiveTool);
            // the Crop bar's Cancel leaves the tool
            Press(w, TextButton(Bar<CropContextBar>(w), "✕ Cancel"));
            Assert.IsType<SelectTool>(w.Session.ActiveTool);
        }
        finally { Directory.Delete(dir, true); }
        w.Close();
    }

    [AvaloniaFact]
    public void Select_bar_text_buttons_align_and_convert_to_curves()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 2200, Height = 900 };   // wide: the Context Bar ends at the dock, and the text row is long
        w.Show();
        Flush();
        w.CaptureRenderedFrame();
        var t = new TextObject("Hello", "Arial", 40) { Transform = Affine2.Translation(100, 100) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, t));
        w.Session.Selection.Set(t);
        Flush();
        var bar = Bar<SelectionContextBar>(w);
        Assert.True(bar.TextPanel.IsVisible);
        var aligns = bar.TextPanel.GetVisualDescendants().OfType<ToggleButton>().ToList();   // left, center, right
        Assert.Equal(3, aligns.Count);

        Press(w, aligns[1]);                                         // center
        Assert.Equal(DeSignMage.Core.Model.TextAlignment.Center, t.Alignment);
        Press(w, aligns[2]);                                         // right
        Assert.Equal(DeSignMage.Core.Model.TextAlignment.Right, t.Alignment);
        Press(w, aligns[0]);                                         // left
        Assert.Equal(DeSignMage.Core.Model.TextAlignment.Left, t.Alignment);

        var convert = TextButton(bar, "Convert to Curves");
        Assert.True(convert.IsEnabled);
        Press(w, convert);
        Assert.DoesNotContain(t, w.Session.Document.AllObjects());
        w.Close();
    }

    // ================================================================ Context Bar: other tools

    [AvaloniaFact]
    public void Rectangle_bar_corner_radius_field_rounds_the_selected_rectangle()
    {
        var (w, _) = Open();
        var r = AddRect(w, 100, 100);
        w.Session.Selection.Set(r);
        w.Session.SetTool("rectangle");
        Flush();
        var bar = Bar<RectangleContextBar>(w);
        var box = bar.GetVisualDescendants().OfType<TextBox>().Single();
        box.Focus();
        box.Text = "0.25";
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Flush();
        Assert.Equal(Units.FromInches(0.25), r.CornerRadius, 6);
        w.Close();
    }

    private static PathObject AddPath(MainWindow w, bool closed)
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
                    closed
                        ? Segment.Cubic(new Vec2(10, 40), new Vec2(-30, -40), new Vec2(0, 0))
                        : Segment.Cubic(new Vec2(10, 90), new Vec2(-30, 60), new Vec2(-20, 20)),
                ],
                Closed = closed,
            },
        ], FillRule.NonZero);
        var p = new PathObject(geometry) { Transform = Affine2.Translation(400, 300), Style = ObjectStyle.FillOnly(DocColor.FromHex("#F2B705")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, p));
        return p;
    }

    private static Point P(MainWindow w, DesignCanvas c, Vec2 doc)
    {
        var s = w.Session.Viewport.ToScreen(doc);
        return c.TranslatePoint(new Point(s.X, s.Y), w).GetValueOrDefault();
    }

    /// <summary>A path in the Vector Edit tool with node 1 selected (the node at local 100,0).</summary>
    private static (MainWindow W, VectorEditContextBar Bar) VectorSetup(bool closed)
    {
        var (w, c) = Open();
        var p = AddPath(w, closed);
        w.Session.EditVectors(p);
        var node = P(w, c, new Vec2(500, 300));
        w.MouseDown(node, MouseButton.Left);
        w.MouseUp(node, MouseButton.Left);
        Flush();
        return (w, Bar<VectorEditContextBar>(w));
    }

    [AvaloniaTheory]
    [InlineData("Insert", "Insert Node", "")]
    [InlineData("Line", "To Line", "")]
    [InlineData("Arc", "To Arc", "")]
    [InlineData("Bezier", "To Bezier", "Line")]
    [InlineData("Sharp", "Make Sharp Corner", "Arc")]
    [InlineData("Corner", "Make Corner", "")]
    [InlineData("Smooth", "Make Smooth", "Corner")]
    [InlineData("Symmetric", "Make Symmetric", "")]
    [InlineData("Break", "Break Loop", "")]
    [InlineData("Set Start/End", "Set Start/End", "")]
    [InlineData("Delete", "Delete Nodes", "")]
    public void Each_Vector_Edit_bar_button_edits_the_path(string button, string description, string first)
    {
        var (w, bar) = VectorSetup(closed: true);
        if (first.Length > 0) Press(w, TextButton(bar, first));
        var target = TextButton(bar, button);
        Assert.True(target.IsEnabled, button);
        int steps = Steps(w);
        Press(w, target);
        Assert.Equal(steps + 1, Steps(w));
        Assert.Equal(description, w.Session.History.UndoDescription);
        w.Close();
    }

    [AvaloniaFact]
    public void Vector_Edit_bar_Close_Join_closes_an_open_path()
    {
        var (w, bar) = VectorSetup(closed: false);
        var join = TextButton(bar, "Close/Join");
        Assert.True(join.IsEnabled);
        int steps = Steps(w);
        Press(w, join);
        Assert.Equal(steps + 1, Steps(w));
        Assert.Equal("Join Start/End", w.Session.History.UndoDescription);
        var path = w.Session.Document.ActiveLayer.Children.OfType<PathObject>().Single();
        Assert.True(PathEditing.ToLoops(path.GetLocalGeometry()!)[0].Closed);
        w.Close();
    }

    [AvaloniaFact]
    public void Vector_Edit_bar_X_and_Y_fields_exist_and_start_empty_without_a_selected_node()
    {
        var (w, _) = Open();
        w.Session.SetTool("vector");
        Flush();
        var bar = Bar<VectorEditContextBar>(w);
        var boxes = bar.GetVisualDescendants().OfType<TextBox>().ToList();
        Assert.Equal(2, boxes.Count);
        Assert.All(boxes, b => Assert.False(b.IsEnabled));
        w.Close();
    }

    /// <summary>Two loops (an outer one first, a hole second) selected in the Loop Edit tool, the hole's loop picked.</summary>
    private static (MainWindow W, LoopEditContextBar Bar, PathObject Path) LoopSetup(bool holeFirst = false)
    {
        var (w, c) = Open(1700);   // the menu area (R25) keeps its share of the Context Bar, so a wide row needs a wider window for every button to be on screen
        var outer = PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(0, 0, 400, 400)))[0];
        var inner = PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(100, 100, 200, 200)))[0];
        PathEditing.Reverse(inner);
        var p = new PathObject(PathEditing.FromLoops(holeFirst ? [inner, outer] : [outer, inner], FillRule.EvenOdd))
        {
            Transform = Affine2.Translation(300, 200),
            Style = ObjectStyle.FillOnly(DocColor.FromHex("#336699")),
        };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, p));
        w.Session.Selection.Set(p);
        w.Session.SetTool("loop");
        var edge = P(w, c, new Vec2(400, 350));                         // the hole's left vector
        w.MouseDown(edge, MouseButton.Left);
        w.MouseUp(edge, MouseButton.Left);
        Flush();
        return (w, Bar<LoopEditContextBar>(w), p);
    }

    [AvaloniaTheory]
    [InlineData("Reverse", "")]
    [InlineData("Open Loop", "")]
    [InlineData("Close Loop", "Open Loop")]
    [InlineData("To Circle", "")]
    [InlineData("Delete Loop", "")]
    [InlineData("Break Out", "")]
    public void Each_Loop_Edit_bar_button_changes_the_graphic(string button, string first)
    {
        var (w, bar, _) = LoopSetup();
        if (first.Length > 0) Press(w, TextButton(bar, first));
        var target = TextButton(bar, button);
        Assert.True(target.IsEnabled, button);
        int steps = Steps(w);
        Press(w, target);
        Assert.True(Steps(w) > steps, button);
        w.Close();
    }

    [AvaloniaFact]
    public void Loop_Edit_bar_Reorder_and_its_two_check_boxes_work()
    {
        var (w, bar, _) = LoopSetup();   // outer loop first, hole second: Reorder puts inner loops first
        var reorder = TextButton(bar, "Reorder");
        Assert.True(reorder.IsEnabled);
        int steps = Steps(w);
        Press(w, reorder);
        Assert.True(Steps(w) > steps);

        var tool = w.Session.GetTool<LoopEditTool>();
        var numbers = bar.GetVisualDescendants().OfType<CheckBox>().Single(x => (string?)x.Content == "Loop numbers");
        var direction = bar.GetVisualDescendants().OfType<CheckBox>().Single(x => (string?)x.Content == "Direction");
        bool n0 = tool.ShowNumbers, d0 = tool.ShowDirection;
        Press(w, numbers);
        Assert.Equal(!n0, tool.ShowNumbers);
        Press(w, direction);
        Assert.Equal(!d0, tool.ShowDirection);
        w.Close();
    }

    [AvaloniaFact]
    public void Effect_bars_Cancel_and_Done_leave_their_tool_and_their_check_boxes_change_the_settings()
    {
        var (w, _) = Open();
        var r = AddRect(w, 100, 100);
        w.Session.Selection.Set(r);

        // Outline: Cancel
        w.Session.SetTool("outline");
        Flush();
        var outline = Bar<OutlineContextBar>(w);
        var tool = w.Session.GetTool<OutlineTool>();
        var contour = outline.GetVisualDescendants().OfType<CheckBox>().Single(x => (string?)x.Content == "Contour");
        bool c0 = tool.Settings.ContourOnly;
        Press(w, contour);
        Assert.Equal(!c0, tool.Settings.ContourOnly);
        var leave = outline.GetVisualDescendants().OfType<CheckBox>().Single(x => (string?)x.Content == "Leave Original");
        bool l0 = tool.Settings.LeaveOriginal;
        Press(w, leave);
        Assert.Equal(!l0, tool.Settings.LeaveOriginal);
        Press(w, TextButton(outline, "✕ Cancel"));
        Assert.IsType<SelectTool>(w.Session.ActiveTool);

        // Knife: Done, and a mode switch
        w.Session.SetTool("knife");
        Flush();
        var knife = Bar<KnifeContextBar>(w);
        var overlap = knife.GetVisualDescendants().OfType<RadioButton>().Single(x => (string?)x.Content == "Overlap");
        Press(w, overlap);
        Assert.True(w.Session.GetTool<KnifeTool>().Settings.OverlapMode);
        Press(w, TextButton(knife, "✓ Done"));
        Assert.IsType<SelectTool>(w.Session.ActiveTool);

        // Cookie cutter: Remain Active, then Done
        w.Session.Selection.Set(r);
        w.Session.SetTool("cookie");
        Flush();
        var cookie = Bar<CookieCutterContextBar>(w);
        var remain = cookie.GetVisualDescendants().OfType<CheckBox>().Single();
        Press(w, remain);
        Assert.True(w.Session.GetTool<CookieCutterTool>().RemainActive);
        Press(w, TextButton(cookie, "✕ Done"));
        Assert.IsType<SelectTool>(w.Session.ActiveTool);
        w.Close();
    }

    [AvaloniaFact]
    public void Guide_bar_buttons_all_work()
    {
        var (w, _) = Open();
        var g = new Guide(new Vec2(300, 200), 0);
        w.Session.History.Execute(new AddGuideCommand(g));
        w.Session.SelectedGuide = g;
        Flush();

        Press(w, NamedButton(w, "GuideVertical"));
        Assert.Equal(GuideOrientation.Vertical, g.Orientation);
        Press(w, NamedButton(w, "GuideHorizontal"));
        Assert.Equal(GuideOrientation.Horizontal, g.Orientation);
        Press(w, NamedButton(w, "GuideDiagonal"));
        Assert.Equal(GuideOrientation.Angled, g.Orientation);
        Press(w, NamedButton(w, "GuideHorizontal"));

        Press(w, NamedButton(w, "GuideCenter"));
        Assert.Equal(w.Session.Document.Page.Height / 2, g.Origin.Y, 6);

        bool snap = w.Session.SnapToGuides;
        Press(w, NamedButton(w, "GuideSnap"));
        Assert.Equal(!snap, w.Session.SnapToGuides);

        Press(w, NamedButton(w, "GuideLock"));
        Assert.True(g.Locked);
        Assert.True(NamedButton(w, "GuideVertical").IsEnabled);         // locking only stops dragging; the numbers and buttons still work
        Press(w, NamedButton(w, "GuideLock"));
        Assert.False(g.Locked);

        bool snapGraphics = w.Session.SnapGuidesToGraphics;
        Press(w, NamedButton(w, "GuideSnapGraphics"));
        Assert.Equal(!snapGraphics, w.Session.SnapGuidesToGraphics);

        Press(w, NamedButton(w, "GuideRemoveOne"));
        Assert.Empty(w.Session.Document.Guides);
        w.Close();
    }

    // ================================================================ Project Color Bar

    [AvaloniaFact]
    public void Project_Color_Bar_Fill_Stroke_No_Fill_and_No_Stroke_work_with_and_without_a_selection()
    {
        var (w, _) = Open();
        var bar = w.ProjectColorBar;
        var r = AddRect(w, 100, 100, 100, 60, new ObjectStyle(new SolidPaint(DocColor.Black), StrokeStyle.Solid(DocColor.FromHex("#CC0000"), 3)));
        w.Session.Selection.Set(r);
        Flush();

        // Fill / Stroke choose what the Color Mixer edits
        Press(w, bar.StrokeButton);
        Assert.Equal(ColorTarget.Stroke, w.ColorState.Target);
        Assert.True(w.ColorMixer.StrokeRadio.IsChecked);
        Assert.Contains("on", bar.StrokeButton.Classes);
        Assert.DoesNotContain("on", bar.FillButton.Classes);
        Press(w, bar.FillButton);
        Assert.Equal(ColorTarget.Fill, w.ColorState.Target);
        Assert.True(w.ColorMixer.FillRadio.IsChecked);

        // No Fill, undo; No Stroke, undo
        Press(w, bar.NoFill);
        Assert.Null(r.Style.Fill);
        Assert.NotNull(r.Style.Stroke);
        Flush();
        Assert.Null(((Control)bar.FillButton.Content!).Tag);          // the swatch shows the crossed box
        w.Session.Undo();
        Assert.NotNull(r.Style.Fill);
        Press(w, bar.NoStroke);
        Assert.Null(r.Style.Stroke);
        Assert.NotNull(r.Style.Fill);
        w.Session.Undo();
        Assert.NotNull(r.Style.Stroke);

        // With nothing selected they change what new shapes get
        w.Session.Selection.Clear();
        w.Session.DefaultStyle = new ObjectStyle(new SolidPaint(DocColor.Black), StrokeStyle.Solid(DocColor.White, 2));
        Flush();
        Press(w, bar.NoFill);
        Assert.Null(w.Session.DefaultStyle.Fill);
        Assert.NotNull(w.Session.DefaultStyle.Stroke);
        Press(w, bar.NoStroke);
        Assert.Null(w.Session.DefaultStyle.Stroke);
        Assert.Equal(ObjectStyle.None, w.Session.DefaultStyle);
        w.Close();
    }

    [AvaloniaFact]
    public void Project_Color_Bar_job_colors_set_fill_and_stroke()
    {
        var (w, _) = Open();
        var a = AddRect(w, 100, 100, 100, 60, ObjectStyle.FillOnly(new DocColor(10, 20, 30)));
        var b = AddRect(w, 300, 100, 100, 60, ObjectStyle.FillOnly(new DocColor(200, 100, 50)));
        w.Session.Selection.Set(b);
        Flush();
        Grid Cell() => w.ProjectColorBar.JobCells.Children.OfType<Grid>().First(g => g.Tag is SolidPaint { Color: { R: 10 } });
        Press(w, Cell());
        Assert.Equal(new DocColor(10, 20, 30), (b.Style.Fill as SolidPaint)!.Color);
        var cell = Cell();                                              // the row was rebuilt by the click
        var p = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), w).GetValueOrDefault();
        w.MouseDown(p, MouseButton.Right);
        w.MouseUp(p, MouseButton.Right);
        Flush();
        Assert.Equal(new DocColor(10, 20, 30), (b.Style.Stroke!.Paint as SolidPaint)!.Color);
        w.Close();
    }

    // ================================================================ Color Mixer and Palette Strip

    [AvaloniaFact]
    public void Color_Mixer_target_corner_and_end_buttons_work()
    {
        var (w, _) = Open();
        w.ToggleDockPanel("colorMixer");                           // the Dock starts closed
        Flush();
        w.CaptureRenderedFrame();
        var mixer = w.ColorMixer;
        var r = AddRect(w, 100, 100, 100, 60, new ObjectStyle(new SolidPaint(DocColor.Black), StrokeStyle.Solid(DocColor.White, 4)));
        w.Session.Selection.Set(r);
        Flush();

        Press(w, mixer.JoinButtons[2]);
        Assert.Equal(LineJoin.Bevel, r.Style.Stroke!.Join);
        Press(w, mixer.CapButtons[1]);
        Assert.Equal(LineCap.Round, r.Style.Stroke!.Cap);

        Press(w, mixer.StrokeRadio);                                   // the Fill / Stroke target radios
        Assert.Equal(ColorTarget.Stroke, w.ColorState.Target);
        Press(w, mixer.FillRadio);
        Assert.Equal(ColorTarget.Fill, w.ColorState.Target);
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<Button>(), x => x.Name == "NoColorButton");   // the Palette Strip X is the only no-color button
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<Button>(), x => x.Name == "PaletteMenuButton");   // no palette loading any more
        w.Close();
    }

    [AvaloniaFact]
    public void Palette_Strip_red_X_removes_all_color_from_the_selection()
    {
        var (w, _) = Open();
        var r = AddRect(w, 100, 100, 100, 60, new ObjectStyle(new SolidPaint(DocColor.Black), StrokeStyle.Solid(DocColor.White, 4)));
        w.Session.Selection.Set(r);
        Flush();
        var x = NamedButton(w, "PaletteStripNoColor");
        Press(w, x);
        Assert.Equal(ObjectStyle.None, r.Style);
        w.Session.Undo();
        Assert.NotEqual(ObjectStyle.None, r.Style);
        w.Close();
    }

    // ================================================================ Layers Panel

    [AvaloniaFact]
    public void Layers_Panel_rows_select_their_object()
    {
        var (w, _) = Open();
        var a = AddRect(w, 100, 100); var b = AddRect(w, 300, 100);
        w.Session.Selection.Clear();
        Flush();
        var list = w.FindControl<ListBox>("LayersList")!;
        var rows = list.ItemCount;
        Assert.True(rows >= 3);                                        // the layer, then b, then a (top-most first)
        list.SelectedIndex = 1;
        Flush();
        Assert.Same(b, Assert.Single(w.Session.Selection.Items));
        list.SelectedIndex = 2;
        Flush();
        Assert.Same(a, Assert.Single(w.Session.Selection.Items));
        w.Close();
    }

    // ================================================================ History tab

    [AvaloniaFact]
    public void History_rows_take_the_document_back_and_forward_in_one_click()
    {
        var (w, _) = Open();
        var list = w.FindControl<ListBox>("HistoryList")!;
        var a = AddRect(w, 0, 0); var b = AddRect(w, 200, 0); var c = AddRect(w, 400, 0);
        Flush();
        var layer = w.Session.Document.ActiveLayer;
        Assert.Equal(4, list.ItemCount);                        // (start) + three steps
        Assert.Equal(3, list.SelectedIndex);                    // the last step is the current one

        list.SelectedIndex = 1;                                 // click the first step
        Flush();
        Assert.Equal([a], layer.Children);
        Assert.Equal(4, list.ItemCount);                        // the later rows stay, as redo rows
        Assert.Equal(1, list.SelectedIndex);
        Assert.True(w.Session.History.CanRedo);

        list.SelectedIndex = 0;                                 // (start)
        Flush();
        Assert.Empty(layer.Children);
        Assert.Equal(0, list.SelectedIndex);

        list.SelectedIndex = 3;                                 // forward again, past two redo rows
        Flush();
        Assert.Equal([a, b, c], layer.Children);
        Assert.Equal(3, list.SelectedIndex);
        Assert.False(w.Session.History.CanRedo);

        list.SelectedIndex = 2;                                 // back one, then a new edit drops the row after it
        Flush();
        AddRect(w, 600, 0);
        Flush();
        Assert.Equal(4, list.ItemCount);
        Assert.Equal(3, list.SelectedIndex);
        Assert.Equal(3, layer.Children.Count);
        w.Close();
    }
}