using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using DeSignMage.App.Canvas;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Resizing the page by its border and the Ctrl roles of the top and bottom middle handles, driven through the real window (R20, R21).</summary>
public class PageResizeShellTests
{
    private static (MainWindow W, DesignCanvas C) Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        var c = w.FindControl<DesignCanvas>("Surface")!;
        c.Focus();
        return (w, c);
    }

    private static Point P(DesignCanvas c, Vec2 screen) => c.TranslatePoint(new Point(screen.X, screen.Y), TopLevel.GetTopLevel(c)!).GetValueOrDefault();

    private static string Info(MainWindow w) => w.FindControl<TextBlock>("InfoLine")!.Text ?? "";

    [AvaloniaFact]
    public void Dragging_the_pages_right_edge_resizes_it_shows_the_size_while_dragging_and_undo_restores_it()
    {
        var (w, c) = Open();
        var s = w.Session;
        var before = s.Document.Page;
        var edge = s.Viewport.ToScreen(new Vec2(before.Bounds.Right, before.Bounds.Center.Y));

        w.MouseMove(P(c, edge));
        Assert.Equal(CursorKind.SizeWE, s.CurrentCursor);

        w.MouseDown(P(c, edge), MouseButton.Left);
        w.MouseMove(P(c, edge + new Vec2(-60, 0)));
        w.MouseMove(P(c, edge + new Vec2(-120, 0)));
        Assert.NotNull(s.PagePreview);
        Assert.StartsWith("Page: ", Info(w));                           // the Info Line shows the live page size
        Assert.Contains("RESIZE", w.FindControl<TextBlock>("HintText")!.Text);
        Assert.Equal(before, s.Document.Page);                          // nothing changed in the document yet
        w.MouseUp(P(c, edge + new Vec2(-120, 0)), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        double shrunk = 120 / s.Viewport.Zoom;
        Assert.Equal(before.Width - shrunk, s.Document.Page.Width, 3);
        Assert.Equal(before.Height, s.Document.Page.Height, 9);
        Assert.Null(s.PagePreview);
        Assert.DoesNotContain("Page: ", Info(w));                       // back to the normal Info Line
        Assert.Equal("Resize Page", s.History.UndoDescription);

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(before, s.Document.Page);
        w.Close();
    }

    [AvaloniaFact]
    public void Dragging_the_top_left_corner_moves_the_page_origin_and_leaves_objects_where_they_are_on_screen()
    {
        var (w, c) = Open();
        var s = w.Session;
        var r = new RectangleObject(100, 60) { Transform = Affine2.Translation(300, 250), Style = ObjectStyle.FillOnly(DocColor.FromHex("#E53935")) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, r));
        var objectScreen = s.Viewport.ToScreen(r.GetWorldBounds().TopLeft);
        var corner = s.Viewport.ToScreen(s.Document.Page.Bounds.TopLeft);

        w.MouseMove(P(c, corner));
        Assert.Equal(CursorKind.SizeNWSE, s.CurrentCursor);
        w.MouseDown(P(c, corner), MouseButton.Left);
        w.MouseMove(P(c, corner + new Vec2(20, 15)));
        w.MouseMove(P(c, corner + new Vec2(40, 30)));
        w.MouseUp(P(c, corner + new Vec2(40, 30)), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var page = s.Document.Page;
        Assert.Equal(40 / s.Viewport.Zoom, page.X, 3);
        Assert.Equal(30 / s.Viewport.Zoom, page.Y, 3);
        var after = s.Viewport.ToScreen(r.GetWorldBounds().TopLeft);
        Assert.Equal(objectScreen.X, after.X, 6);                       // the object did not move on screen
        Assert.Equal(objectScreen.Y, after.Y, 6);
        w.Close();
    }

    [AvaloniaFact]
    public void The_cursor_over_a_selected_objects_handle_or_the_object_itself_beats_the_page_border()
    {
        var (w, c) = Open();
        var s = w.Session;
        var page = s.Document.Page.Bounds;
        // an object whose right edge lies exactly on the page's right edge
        var r = new RectangleObject(100, 60) { Transform = Affine2.Translation(page.Right - 100, 100), Style = ObjectStyle.FillOnly(DocColor.FromHex("#E53935")) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, r));

        var onObject = s.Viewport.ToScreen(new Vec2(page.Right - 1 / s.Viewport.Zoom, 130));
        w.MouseMove(P(c, onObject));
        Assert.Equal(CursorKind.SelectHit, s.CurrentCursor);            // the object, not the border

        s.Selection.Set(r);
        Dispatcher.UIThread.RunJobs();
        var handle = s.Viewport.ToScreen(new Vec2(page.Right, 130));    // the right-middle handle of the selection sits on the border
        w.MouseMove(P(c, handle));
        Assert.Equal(CursorKind.SizeWE, s.CurrentCursor);
        w.MouseDown(P(c, handle), MouseButton.Left);
        w.MouseMove(P(c, handle + new Vec2(30, 0)));
        w.MouseUp(P(c, handle + new Vec2(30, 0)), MouseButton.Left);
        Assert.Equal(page, s.Document.Page.Bounds);                     // the object was extended, the page was not touched
        Assert.True(r.GetWorldBounds().Right > page.Right);
        w.Close();
    }

    [AvaloniaFact]
    public void Pressing_Ctrl_over_the_top_or_bottom_handle_switches_the_cursor_without_moving_the_mouse()
    {
        var (w, c) = Open();
        var s = w.Session;
        var r = new RectangleObject(200, 100) { Transform = Affine2.Translation(300, 250), Style = ObjectStyle.FillOnly(DocColor.FromHex("#E53935")) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, r));
        s.Selection.Set(r);
        Dispatcher.UIThread.RunJobs();

        var top = s.Viewport.ToScreen(new Vec2(400, 250));
        w.MouseMove(P(c, top));
        Assert.Equal(CursorKind.Slant, s.CurrentCursor);
        w.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
        Assert.Equal(CursorKind.SizeNS, s.CurrentCursor);               // Ctrl: extend upward
        w.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
        Assert.Equal(CursorKind.Slant, s.CurrentCursor);

        var bottom = s.Viewport.ToScreen(new Vec2(400, 350));
        w.MouseMove(P(c, bottom));
        Assert.Equal(CursorKind.Rotate, s.CurrentCursor);
        w.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
        Assert.Equal(CursorKind.SizeNS, s.CurrentCursor);               // Ctrl: extend downward
        w.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
        w.Close();
    }
}
