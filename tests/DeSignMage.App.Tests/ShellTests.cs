using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using DeSignMage.App.Shell;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App;
using DeSignMage.App.Canvas;
using DeSignMage.Core.Model;
using SkiaSharp;
using DeSignMage.Rendering;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Commands;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(DeSignMage.App.Tests.TestAppBuilder))]

namespace DeSignMage.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class ShellTests
{
    private static (MainWindow Window, DesignCanvas Canvas) Open(bool demo = false)
    {
        var w = new MainWindow(new StartupOptions(Demo: demo)) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();   // forces layout + one render
        return (w, w.FindControl<DesignCanvas>("Surface")!);
    }

    /// <summary>Canvas-relative point → window point, as the mouse would deliver it.</summary>
    private static Point P(DesignCanvas c, double x, double y) => c.TranslatePoint(new Point(x, y), TopLevel.GetTopLevel(c)!).GetValueOrDefault();

    private static Point P(DesignCanvas c, DeSignMage.Core.Geometry.Vec2 docPoint, MainWindow w)
    {
        var s = w.Session.Viewport.ToScreen(docPoint);
        return P(c, s.X, s.Y);
    }

    [AvaloniaFact]
    public void Window_opens_and_renders_a_frame()
    {
        var (w, c) = Open(demo: true);
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(c.Bounds.Width > 500 && c.Bounds.Height > 300);
        Assert.Equal(11, w.Session.Document.ObjectCount);
        // The page was fitted into the canvas on first layout.
        var page = w.Session.Document.Page.Bounds;
        var tl = w.Session.Viewport.ToScreen(page.TopLeft);
        Assert.True(tl.X > 0 && tl.Y > 0);
        w.Close();
    }

    [AvaloniaFact]
    public void Palette_color_mode_button_and_strip_no_color_button_work()
    {
        var (w, _) = Open();
        var colorMode = w.FindControl<Avalonia.Controls.Primitives.ToggleButton>("ToolboxColorMode")
            ?? w.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => b.Name == "ToolboxColorMode");
        Assert.True(w.Session.ShowFillColors);                      // starts with colors shown...
        Assert.False(colorMode.IsChecked);                          // ...and the button NOT pressed (pressed = vector mode)
        colorMode.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Primitives.ToggleButton.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.Session.ShowFillColors);                     // vector mode
        Assert.True(colorMode.IsChecked);
        w.Session.ShowFillColors = true;                            // F3 / menu / toolbar keep it in sync
        Dispatcher.UIThread.RunJobs();
        Assert.False(colorMode.IsChecked);

        var r = new RectangleObject(60, 40) { Style = new ObjectStyle(new SolidPaint(DocColor.FromHex("#FF0000")), StrokeStyle.Solid(DocColor.Black, 1)) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        w.Session.Selection.Set(r);
        var noColor = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PaletteStripNoColor");
        noColor.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ObjectStyle.None, r.Style);                    // fill and stroke both gone
        w.Close();
    }

    [AvaloniaFact]
    public void Mouse_draws_rectangle_then_ctrl_z_undoes_it()
    {
        var (w, c) = Open();
        w.Session.SetTool("rectangle");
        w.MouseDown(P(c, 200, 200), MouseButton.Left);
        w.MouseMove(P(c, 260, 240));
        w.MouseMove(P(c, 320, 280));
        w.MouseUp(P(c, 320, 280), MouseButton.Left);

        var r = Assert.IsType<RectangleObject>(Assert.Single(w.Session.Document.ActiveLayer.Children));
        var expectedWidth = 120 / w.Session.Viewport.Zoom;
        Assert.Equal(expectedWidth, r.Width, 3);
        Assert.Single(w.Session.Selection.Items);

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Empty(w.Session.Document.ActiveLayer.Children);
        w.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
        Assert.Single(w.Session.Document.ActiveLayer.Children);
        w.Close();
    }

    [AvaloniaFact]
    public void Mouse_moves_selected_object_and_delete_key_removes_it()
    {
        var (w, c) = Open(demo: true);
        var title = w.Session.Selection.Items.Single();   // demo selects the title text
        var before = title.GetWorldBounds();

        var grab = P(c, before.Center, w);
        w.MouseDown(grab, MouseButton.Left);
        w.MouseMove(grab + new Point(20, 0));
        w.MouseMove(grab + new Point(40, 0));
        w.MouseUp(grab + new Point(40, 0), MouseButton.Left);

        var after = title.GetWorldBounds();
        Assert.Equal(before.Left + 40 / w.Session.Viewport.Zoom, after.Left, 3);
        Assert.Equal(before.Top, after.Top, 3);

        w.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Assert.False(w.Session.Document.Contains(title));
        w.Close();
    }

    [AvaloniaFact]
    public void Right_button_marquee_zooms_in_and_right_click_zooms_back_out()
    {
        var (w, c) = Open(demo: true);
        var start = w.Session.Viewport.State;

        w.MouseDown(P(c, 100, 100), MouseButton.Right);
        w.MouseMove(P(c, 200, 160));
        w.MouseUp(P(c, 300, 220), MouseButton.Right);
        Assert.True(w.Session.Viewport.Zoom > start.Zoom * 2);
        Assert.Equal(1, w.Session.Viewport.ZoomStackDepth);

        w.MouseDown(P(c, 50, 50), MouseButton.Right);
        w.MouseUp(P(c, 50, 50), MouseButton.Right);
        Assert.Equal(start, w.Session.Viewport.State);
        w.Close();
    }

    [AvaloniaFact]
    public void Middle_button_pans()
    {
        var (w, c) = Open();
        var pan = w.Session.Viewport.Pan;
        w.MouseDown(P(c, 300, 300), MouseButton.Middle);
        w.MouseMove(P(c, 350, 280));
        w.MouseUp(P(c, 350, 280), MouseButton.Middle);
        Assert.Equal(pan.X + 50, w.Session.Viewport.Pan.X, 6);
        Assert.Equal(pan.Y - 20, w.Session.Viewport.Pan.Y, 6);
        w.Close();
    }

    /// <summary>Two filled shapes away from the page edge, in an empty document; returns the canvas.</summary>
    private static DesignCanvas TwoShapes(MainWindow w, DesignCanvas c)
    {
        foreach (var x in new[] { 200.0, 300.0 })
        {
            w.Session.SetTool("rectangle");   // shape tools return to Select after each shape
            w.MouseDown(P(c, x, 200), MouseButton.Left);
            w.MouseMove(P(c, x + 60, 260));
            w.MouseUp(P(c, x + 60, 260), MouseButton.Left);
        }
        Assert.Equal(2, w.Session.Document.ActiveLayer.Children.Count);
        return c;
    }

    private static void RubberBand(MainWindow w, DesignCanvas c, double x0, double y0, double x1, double y1)
    {
        w.MouseDown(P(c, x0, y0), MouseButton.Left);
        w.MouseMove(P(c, (x0 + x1) / 2, (y0 + y1) / 2));
        w.MouseUp(P(c, x1, y1), MouseButton.Left);
    }

    [AvaloniaFact]
    public void Rubber_band_groups_and_plain_U_ungroups_when_canvas_has_focus()
    {
        var (w, c) = Open();
        TwoShapes(w, c);
        RubberBand(w, c, 150, 150, 450, 320);
        var g = Assert.IsType<GroupObject>(Assert.Single(w.Session.Document.ActiveLayer.Children.Select(o => o.TopLevel).Distinct()));
        Assert.Equal([g], w.Session.Selection.Items);

        w.KeyPress(Key.U, RawInputModifiers.None, PhysicalKey.U, "u");
        Assert.Equal(2, w.Session.Document.ActiveLayer.Children.Select(o => o.TopLevel).Distinct().Count());
        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.IsType<GroupObject>(Assert.Single(w.Session.Document.ActiveLayer.Children.Select(o => o.TopLevel).Distinct()));
        w.Close();
    }

    [AvaloniaFact]
    public void Plain_U_does_nothing_while_typing_in_a_property_field()
    {
        var (w, c) = Open();
        TwoShapes(w, c);
        RubberBand(w, c, 150, 150, 450, 320);
        var field = w.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEnabled && t.IsVisible);
        field.Focus();
        w.KeyPress(Key.U, RawInputModifiers.None, PhysicalKey.U, "u");
        Assert.IsType<GroupObject>(Assert.Single(w.Session.Document.ActiveLayer.Children.Select(o => o.TopLevel).Distinct()));
        w.Close();
    }

    [AvaloniaFact]
    public void Ctrl_B_combines_the_group_into_one_object()
    {
        var (w, c) = Open();
        TwoShapes(w, c);
        RubberBand(w, c, 150, 150, 450, 320);
        w.KeyPress(Key.B, RawInputModifiers.Control, PhysicalKey.B, "b");
        var combined = Assert.IsType<CombinedObject>(Assert.Single(w.Session.Document.ActiveLayer.Children));
        Assert.Equal(2, combined.Parts.Count);
        w.KeyPress(Key.U, RawInputModifiers.None, PhysicalKey.U, "u");   // not a group: stays combined
        Assert.Single(w.Session.Document.ActiveLayer.Children);
        w.CaptureRenderedFrame();
        w.KeyPress(Key.K, RawInputModifiers.None, PhysicalKey.K, "k");   // Break Apart: solo pieces again
        Assert.All(w.Session.Document.ActiveLayer.Children, o => Assert.IsType<RectangleObject>(o));
        Assert.Equal(2, w.Session.Document.ActiveLayer.Children.Count);
        w.CaptureRenderedFrame();   // renders without error
        w.Close();
    }

    [AvaloniaFact]
    public void Break_Apart_is_plain_K_only_Ctrl_K_is_not_bound()
    {
        var (w, c) = Open();
        TwoShapes(w, c);
        RubberBand(w, c, 150, 150, 450, 320);
        w.KeyPress(Key.B, RawInputModifiers.Control, PhysicalKey.B, "b");
        Assert.IsType<CombinedObject>(Assert.Single(w.Session.Document.ActiveLayer.Children));

        w.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, "k");   // Ctrl+K: nothing happens
        Assert.IsType<CombinedObject>(Assert.Single(w.Session.Document.ActiveLayer.Children));

        w.KeyPress(Key.K, RawInputModifiers.None, PhysicalKey.K, "k");      // K breaks it apart
        Assert.Equal(2, w.Session.Document.ActiveLayer.Children.Count);

        Assert.Equal(["K"], KeyMap.Default.Where(k => k.CommandId == "arrange.breakApart").Select(k => k.Gesture));
        var menuItem = w.GetLogicalDescendants().OfType<MenuItem>().First(m => m.Header as string == "_Break Apart");
        Assert.Equal("K", menuItem.InputGesture?.ToString());   // the shortcut text the menu shows        w.Close();
    }

    [AvaloniaFact]
    public void Ctrl_C_Ctrl_V_pastes_a_copy()
    {
        var (w, c) = Open();
        TwoShapes(w, c);
        w.MouseDown(P(c, 230, 230), MouseButton.Left);
        w.MouseUp(P(c, 230, 230), MouseButton.Left);
        w.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        w.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
        Assert.Equal(2, w.Session.Document.ActiveLayer.Children.Count);   // hanging on the cursor, not added yet
        w.MouseDown(P(c, 600, 400), MouseButton.Left);                    // the click places it
        w.MouseUp(P(c, 600, 400), MouseButton.Left);
        Assert.Equal(3, w.Session.Document.ActiveLayer.Children.Count);
        w.Close();
    }

    [AvaloniaFact]
    public void Left_double_click_on_object_opens_its_context_menu_and_right_click_never_does()
    {
        var (w, c) = Open();
        TwoShapes(w, c);
        w.MouseDown(P(c, 230, 230), MouseButton.Right);
        w.MouseUp(P(c, 230, 230), MouseButton.Right);
        Assert.Null(w.LastObjectMenu);

        RubberBand(w, c, 150, 150, 450, 320);   // a group: double-click opens the object menu
        w.MouseDown(P(c, 230, 230), MouseButton.Left);
        w.MouseUp(P(c, 230, 230), MouseButton.Left);
        w.MouseDown(P(c, 230, 230), MouseButton.Left);
        w.MouseUp(P(c, 230, 230), MouseButton.Left);
        Assert.NotNull(w.LastObjectMenu);
        Assert.True(w.LastObjectMenu!.IsOpen);
        w.LastObjectMenu.Close();
        w.Close();
    }

    [Fact]
    public void Combined_overlaps_render_knocked_out_while_grouped_overlaps_stay_solid()
    {
        var s = new EditorSession(new Document(PageSetup.FromInches(2, 1)));
        static RectangleObject R(double x, string c) => new(40, 40)
        {
            Transform = DeSignMage.Core.Geometry.Affine2.Translation(x, 10),
            Style = ObjectStyle.FillOnly(DocColor.FromHex(c)),
        };
        var red = R(10, "#FF0000"); var blue = R(30, "#0000FF");   // overlap x = 30..50
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, red));
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, blue));

        s.GroupObjects([red, blue]);
        Assert.Equal(SKColors.Blue, PixelAt(s, 40, 30));            // grouped: top piece covers the overlap

        s.CombineSelection();
        Assert.Equal(SKColors.White, PixelAt(s, 40, 30));           // combined: overlap knocked out
        Assert.Equal(SKColors.Red, PixelAt(s, 20, 30));             // pieces keep their own colors
        Assert.Equal(SKColors.Blue, PixelAt(s, 60, 30));
    }

    [Fact]
    public void Dragged_objects_preview_as_outlines_so_what_is_underneath_shows_through()
    {
        var s = new EditorSession(new Document(PageSetup.FromInches(2, 1)));
        var red = new RectangleObject(40, 40)
        {
            Transform = DeSignMage.Core.Geometry.Affine2.Translation(10, 10),
            Style = ObjectStyle.FillOnly(DocColor.FromHex("#FF0000")),
        };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, red));

        using var renderer = new SceneRenderer(s.Document);   // fill colors ON: only the drag preview is see-through
        var pic = renderer.RecordPreview([red], DeSignMage.Core.Geometry.Affine2.Translation(20, 0));
        using var bmp = new SKBitmap(100, 60);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawPicture(pic.Picture);
        }
        pic.Release();

        Assert.Equal(SKColors.White, bmp.GetPixel(50, 30));        // inside the moved rectangle: no fill
        Assert.NotEqual(SKColors.White, bmp.GetPixel(30, 30));     // its left edge (x = 10 + 20) is drawn as a line
    }

    [Fact]
    public void Uncolored_objects_show_as_outlines_on_screen_but_not_in_exports()
    {
        var s = new EditorSession(new Document(PageSetup.FromInches(2, 1)));
        var r = new RectangleObject(60, 40) { Transform = DeSignMage.Core.Geometry.Affine2.Translation(10, 10), Style = ObjectStyle.FillOnly(DocColor.FromHex("#FF0000")) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, r));
        s.Selection.Set(r);
        s.ApplyNoColor();                                            // the strip's red X
        Assert.Equal(ObjectStyle.None, r.Style);

        static int Inked(SceneRenderer renderer)
        {
            var pic = renderer.GetDocumentPicture(null);
            using var bmp = new SKBitmap(100, 60);
            using (var canvas = new SKCanvas(bmp)) { canvas.Clear(SKColors.White); canvas.DrawPicture(pic.Picture); }
            pic.Release();
            int n = 0;
            for (int y = 0; y < 60; y++) for (int x = 0; x < 100; x++) if (bmp.GetPixel(x, y) != SKColors.White) n++;
            return n;
        }
        using (var screen = new SceneRenderer(s.Document) { ViewScale = 1 }) Assert.True(Inked(screen) > 50);   // outline drawn
        using (var export = new SceneRenderer(s.Document)) Assert.Equal(0, Inked(export));                      // nothing to print
    }

    [Fact]
    public void Selection_marks_invert_so_they_show_on_black_and_on_white()
    {
        var s = new EditorSession(new Document(PageSetup.FromInches(4, 2)));
        static RectangleObject R(double x, string c) => new(100, 60)
        {
            Transform = DeSignMage.Core.Geometry.Affine2.Translation(x, 10),
            Style = ObjectStyle.FillOnly(DocColor.FromHex(c)),
        };
        var black = R(10, "#000000"); var white = R(150, "#FFFFFF");
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, black));
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, white));
        s.Viewport.Set(1, new DeSignMage.Core.Geometry.Vec2(0, 0));   // 1 document point = 1 screen pixel

        s.Selection.Set(black);
        Assert.Equal(SKColors.White, FramePixel(s, 60, 40));   // center x over black artwork is white

        s.Selection.Set(white);
        Assert.Equal(SKColors.Black, FramePixel(s, 200, 40));  // center x over white artwork is black
    }

    private static SKColor FramePixel(EditorSession s, int x, int y)
    {
        using var builder = new CanvasFrameBuilder(s);
        using var frame = builder.Build(300, 100);
        using var bmp = new SKBitmap(300, 100);
        using (var canvas = new SKCanvas(bmp)) frame.Draw(canvas);
        return bmp.GetPixel(x, y);
    }

    private static SKColor PixelAt(EditorSession s, int x, int y)
    {
        using var renderer = new SceneRenderer(s.Document);
        var pic = renderer.GetDocumentPicture(null);
        using var bmp = new SKBitmap(100, 60);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawPicture(pic.Picture);
        }
        pic.Release();
        return bmp.GetPixel(x, y);
    }
    [AvaloniaFact]
    public void Tool_shortcuts_match_sign_wizard()
    {
        var (w, _) = Open();
        w.KeyPress(Key.G, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.G, "G");
        Assert.Equal("rectangle", w.Session.ActiveTool.Id);
        w.KeyPress(Key.E, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.E, "E");
        Assert.Equal("ellipse", w.Session.ActiveTool.Id);
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Equal("select", w.Session.ActiveTool.Id);
        w.Close();
    }
}
