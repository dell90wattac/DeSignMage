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
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using SkiaSharp;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Ruler guides through the real window: rulers, canvas, guide Context Bar, menus and rendering.</summary>
public class GuideShellTests
{
    private static (MainWindow Window, DesignCanvas Canvas) Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return (w, w.FindControl<DesignCanvas>("Surface")!);
    }

    private static Point InWindow(Control c, double x, double y) =>
        c.TranslatePoint(new Point(x, y), TopLevel.GetTopLevel(c)!).GetValueOrDefault();

    private static Ruler TopRuler(MainWindow w) => w.FindControl<Ruler>("TopRuler")!;
    private static Ruler LeftRuler(MainWindow w) => w.FindControl<Ruler>("LeftRuler")!;

    /// <summary>Press on a ruler, pull onto the canvas and release there.</summary>
    private static void PullGuide(MainWindow w, DesignCanvas c, Ruler ruler, Point rulerPoint, Point canvasPoint)
    {
        w.MouseDown(InWindow(ruler, rulerPoint.X, rulerPoint.Y), MouseButton.Left);
        w.MouseMove(InWindow(c, canvasPoint.X, canvasPoint.Y - 40));
        w.MouseMove(InWindow(c, canvasPoint.X, canvasPoint.Y));
        w.MouseUp(InWindow(c, canvasPoint.X, canvasPoint.Y), MouseButton.Left);
    }

    private static void Click(MainWindow w, Control c, double x, double y, MouseButton b = MouseButton.Left)
    {
        w.MouseDown(InWindow(c, x, y), b);
        w.MouseUp(InWindow(c, x, y), b);
    }

    private static Button Named(MainWindow w, string name) =>
        w.GetVisualDescendants().OfType<Button>().First(b => b.Name == name);

    private static void Press(MainWindow w, Button b) =>
        Click(w, b, b.Bounds.Width / 2, b.Bounds.Height / 2);

    // ---- Create ----

    [AvaloniaFact]
    public void Dragging_from_the_top_ruler_onto_the_canvas_creates_a_horizontal_guide()
    {
        var (w, c) = Open();
        var expectedDoc = w.Session.Viewport.ToDoc(new Vec2(300, 220));
        PullGuide(w, c, TopRuler(w), new Point(300, 10), new Point(300, 220));

        var g = Assert.Single(w.Session.Document.Guides);
        Assert.Equal(GuideOrientation.Horizontal, g.Orientation);
        Assert.Equal(expectedDoc.Y, g.Origin.Y, 3);
        Assert.Same(g, w.Session.SelectedGuide);
        Assert.True(w.Session.History.CanUndo);
        w.Close();
    }

    [AvaloniaFact]
    public void Dragging_from_the_left_ruler_creates_a_vertical_guide_and_many_can_be_added()
    {
        var (w, c) = Open();
        for (int i = 0; i < 5; i++)
            PullGuide(w, c, LeftRuler(w), new Point(10, 300), new Point(150 + i * 60, 300));

        Assert.Equal(5, w.Session.Document.Guides.Count);
        Assert.All(w.Session.Document.Guides, g => Assert.Equal(GuideOrientation.Vertical, g.Orientation));
        w.Close();
    }

    [AvaloniaFact]
    public void Releasing_back_on_the_ruler_creates_nothing_and_the_right_button_never_creates_a_guide()
    {
        var (w, c) = Open();
        var top = TopRuler(w);
        w.MouseDown(InWindow(top, 300, 10), MouseButton.Left);
        w.MouseMove(InWindow(c, 300, 100));
        w.MouseMove(InWindow(top, 320, 10));
        w.MouseUp(InWindow(top, 320, 10), MouseButton.Left);
        Assert.Empty(w.Session.Document.Guides);

        w.MouseDown(InWindow(top, 300, 10), MouseButton.Right);
        w.MouseMove(InWindow(c, 300, 200));
        w.MouseUp(InWindow(c, 300, 200), MouseButton.Right);
        Assert.Empty(w.Session.Document.Guides);
        Assert.False(w.Session.IsDraggingGuide);
        w.Close();
    }

    // ---- Select, red, move ----

    private static (SKBitmap Frame, int OffsetX, int OffsetY) Frame(MainWindow w, DesignCanvas c)
    {
        var bmp = w.CaptureRenderedFrame()!;
        using var ms = new MemoryStream();
#pragma warning disable CS0618 // the replacement overload needs encoder options we don't care about for a plain PNG
        bmp.Save(ms);
#pragma warning restore CS0618
        ms.Position = 0;
        var origin = InWindow(c, 0, 0);
        return (SKBitmap.Decode(ms), (int)origin.X, (int)origin.Y);
    }

    private static (int Red, int Blue) CountGuidePixels(SKBitmap frame, int ox, int oy, int canvasY, int x0, int x1)
    {
        int red = 0, blue = 0;
        for (int x = x0; x < x1; x++)
        {
            var p = frame.GetPixel(ox + x, oy + canvasY);
            if (p.Red > 200 && p.Green < 60 && p.Blue < 60) red++;
            else if (p.Blue > 200 && p.Red < 80 && p.Green < 90) blue++;
        }
        return (red, blue);
    }

    [AvaloniaFact]
    public void Clicking_a_guide_selects_it_and_it_draws_red_while_others_stay_blue()
    {
        var (w, c) = Open();
        PullGuide(w, c, TopRuler(w), new Point(300, 10), new Point(300, 150));
        PullGuide(w, c, TopRuler(w), new Point(300, 10), new Point(300, 260));
        var (first, second) = (w.Session.Document.Guides[0], w.Session.Document.Guides[1]);
        Assert.Same(second, w.Session.SelectedGuide);   // the new guide is selected

        double y1 = w.Session.Viewport.ToScreen(first.Origin).Y;
        double y2 = w.Session.Viewport.ToScreen(second.Origin).Y;
        Click(w, c, 500, y1);                            // a single left click selects
        Assert.Same(first, w.Session.SelectedGuide);

        var (frame, ox, oy) = Frame(w, c);
        var selected = CountGuidePixels(frame, ox, oy, (int)Math.Floor(y1), 20, 600);
        var other = CountGuidePixels(frame, ox, oy, (int)Math.Floor(y2), 20, 600);
        Assert.True(selected.Red > 50 && selected.Blue == 0, $"selected guide should be red: {selected}");
        Assert.True(other.Blue > 50 && other.Red == 0, $"unselected guide should be blue: {other}");

        Click(w, c, 500, y2);
        Assert.Same(second, w.Session.SelectedGuide);
        (frame, ox, oy) = Frame(w, c);
        Assert.True(CountGuidePixels(frame, ox, oy, (int)Math.Floor(y2), 20, 600).Red > 50);
        Assert.True(CountGuidePixels(frame, ox, oy, (int)Math.Floor(y1), 20, 600).Blue > 50);
        w.Close();
    }

    [AvaloniaFact]
    public void Dragging_a_guide_with_the_mouse_moves_it_and_undo_puts_it_back()
    {
        var (w, c) = Open();
        PullGuide(w, c, LeftRuler(w), new Point(10, 300), new Point(400, 300));
        var g = Assert.Single(w.Session.Document.Guides);
        double x0 = g.Origin.X;
        double sx = w.Session.Viewport.ToScreen(g.Origin).X;

        w.MouseDown(InWindow(c, sx, 200), MouseButton.Left);
        w.MouseMove(InWindow(c, sx + 40, 200));
        w.MouseMove(InWindow(c, sx + 80, 200));
        w.MouseUp(InWindow(c, sx + 80, 200), MouseButton.Left);
        Assert.Equal(x0 + 80 / w.Session.Viewport.Zoom, g.Origin.X, 3);

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(x0, g.Origin.X, 6);
        w.Close();
    }

    [AvaloniaFact]
    public void A_locked_guide_does_not_move_when_dragged()
    {
        var (w, c) = Open();
        PullGuide(w, c, LeftRuler(w), new Point(10, 300), new Point(400, 300));
        var g = Assert.Single(w.Session.Document.Guides);
        Press(w, Named(w, "GuideLock"));
        Assert.True(g.Locked);

        double sx = w.Session.Viewport.ToScreen(g.Origin).X;
        w.MouseDown(InWindow(c, sx, 200), MouseButton.Left);
        w.MouseMove(InWindow(c, sx + 60, 200));
        w.MouseUp(InWindow(c, sx + 60, 200), MouseButton.Left);
        Assert.Equal(sx, w.Session.Viewport.ToScreen(g.Origin).X, 3);
        Assert.Same(g, w.Session.SelectedGuide);
        w.Close();
    }

    [AvaloniaFact]
    public void Dragging_a_guide_back_onto_its_ruler_removes_it_and_undo_brings_it_back()
    {
        var (w, c) = Open();
        PullGuide(w, c, TopRuler(w), new Point(300, 10), new Point(300, 220));
        var g = Assert.Single(w.Session.Document.Guides);
        double sy = w.Session.Viewport.ToScreen(g.Origin).Y;
        var top = TopRuler(w);

        w.MouseDown(InWindow(c, 500, sy), MouseButton.Left);
        w.MouseMove(InWindow(c, 500, sy - 60));
        w.MouseMove(InWindow(top, 500, 10));
        w.MouseUp(InWindow(top, 500, 10), MouseButton.Left);
        Assert.Empty(w.Session.Document.Guides);
        Assert.Null(w.Session.SelectedGuide);

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Single(w.Session.Document.Guides);
        w.Close();
    }

    /// <summary>Saves a picture of the guide bar (with the snap-to-graphics toggle) when DSM_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public void Screenshot_of_the_guide_bar_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        var (w, c) = Open();
        Directory.CreateDirectory(dir);
        var box = new RectangleObject(300, 140) { Transform = Affine2.Translation(400, 300), Style = ObjectStyle.FillOnly(DocColor.FromHex("#3366CC")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, box));
        var sx = w.Session.Viewport.ToScreen(new Vec2(700, 0)).X + 2;      // 2 px from the right edge: the guide jumps onto it
        PullGuide(w, c, LeftRuler(w), new Point(10, 300), new Point(sx, 300));
        Assert.Equal(700, w.Session.SelectedGuide!.Origin.X, 6);          // it snapped onto the rectangle's right edge
        Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618
        w.CaptureRenderedFrame()!.Save(System.IO.Path.Combine(dir, "guide-bar-snap-graphics.png"));
#pragma warning restore CS0618
        w.Close();
    }

    // ---- Guide Context Bar ----

    [AvaloniaFact]
    public void The_guide_bar_has_a_snap_guides_to_graphics_toggle_next_to_snap_to_guides()
    {
        var (w, c) = Open();
        PullGuide(w, c, TopRuler(w), new Point(300, 10), new Point(300, 220));
        var toggle = Named(w, "GuideSnapGraphics");
        Assert.True(w.Session.SnapGuidesToGraphics);
        Assert.Contains("on", toggle.Classes);                     // pressed while on
        Press(w, toggle);
        Assert.False(w.Session.SnapGuidesToGraphics);
        Assert.DoesNotContain("on", Named(w, "GuideSnapGraphics").Classes);
        Press(w, Named(w, "GuideSnapGraphics"));
        Assert.True(w.Session.SnapGuidesToGraphics);
        w.Close();
    }

    [AvaloniaFact]
    public void A_locked_guide_can_still_be_edited_by_number_it_only_refuses_to_be_dragged()
    {
        var (w, c) = Open();
        PullGuide(w, c, TopRuler(w), new Point(300, 10), new Point(300, 220));
        Press(w, Named(w, "GuideLock"));
        Assert.True(w.Session.SelectedGuide!.Locked);
        var y = w.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "GuideY");
        Assert.True(y.IsEnabled);
        Assert.True(y.IsVisible);
        Assert.True(Named(w, "GuideCenter").IsEnabled);
        Assert.True(Named(w, "GuideVertical").IsEnabled);
        w.Close();
    }

    [AvaloniaFact]
    public void Selecting_a_guide_switches_to_the_guide_bar_with_the_irrelevant_field_greyed()
    {
        var (w, c) = Open();
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<Button>(), b => b.Name == "GuideRemoveOne");

        PullGuide(w, c, TopRuler(w), new Point(300, 10), new Point(300, 200));
        var x = w.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "GuideX");
        var y = w.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "GuideY");
        Assert.False(x.IsEnabled);   // horizontal guide: X doesn't apply
        Assert.True(y.IsEnabled);
        Assert.False(string.IsNullOrEmpty(y.Text));

        // Deselect: the tool's bar is back.
        Click(w, c, 700, 500);
        Assert.Null(w.Session.SelectedGuide);
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<Button>(), b => b.Name == "GuideRemoveOne");
        w.Close();
    }

    [AvaloniaFact]
    public void Blue_X_removes_this_guide_and_red_X_removes_all_and_both_undo()
    {
        var (w, c) = Open();
        for (int i = 0; i < 3; i++)
            PullGuide(w, c, LeftRuler(w), new Point(10, 300), new Point(150 + i * 80, 300));
        var guides = w.Session.Document.Guides.ToArray();
        Assert.Equal(3, guides.Length);
        Assert.Same(guides[2], w.Session.SelectedGuide);

        Press(w, Named(w, "GuideRemoveOne"));
        Assert.Equal([guides[0], guides[1]], w.Session.Document.Guides);
        Assert.Null(w.Session.SelectedGuide);

        // Select another, then remove all.
        double sx = w.Session.Viewport.ToScreen(guides[0].Origin).X;
        Click(w, c, sx, 400);
        Assert.Same(guides[0], w.Session.SelectedGuide);
        Press(w, Named(w, "GuideRemoveAll"));
        Assert.Empty(w.Session.Document.Guides);

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(2, w.Session.Document.Guides.Count);
        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(3, w.Session.Document.Guides.Count);
        w.Close();
    }

    [AvaloniaFact]
    public void Plus_button_centers_a_vertical_guide_on_the_page_and_Delete_removes_the_selected_guide()
    {
        var (w, c) = Open();
        PullGuide(w, c, LeftRuler(w), new Point(10, 300), new Point(200, 300));
        var g = Assert.Single(w.Session.Document.Guides);

        Press(w, Named(w, "GuideCenter"));
        Assert.Equal(w.Session.Document.Page.Width / 2, g.Origin.X, 6);

        w.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Assert.Empty(w.Session.Document.Guides);
        w.Close();
    }

    [AvaloniaFact]
    public void Typing_a_position_in_the_Y_field_moves_a_horizontal_guide()
    {
        var (w, c) = Open();
        PullGuide(w, c, TopRuler(w), new Point(300, 10), new Point(300, 200));
        var g = Assert.Single(w.Session.Document.Guides);
        var y = w.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "GuideY");
        y.Focus();
        y.Text = "3.5";
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(Units.FromUser(0, 3.5, w.Session.Document.Page).Y, g.Origin.Y, 6);
        w.Close();
    }

    // ---- Menus and toggles ----

    private static MenuItem FindMenuItem(IEnumerable<object?> items, string commandId)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            if (item.Tag is AppCommand ac && ac.Id == commandId) return item;
            var nested = FindMenuItem(item.Items.Cast<object?>(), commandId);
            if (nested is not null) return nested;
        }
        return null!;
    }

    [AvaloniaFact]
    public void View_menu_has_Show_Guidelines_and_Show_Rulers_toggles()
    {
        var (w, c) = Open();
        PullGuide(w, c, TopRuler(w), new Point(300, 10), new Point(300, 200));
        var menu = w.FindControl<Menu>("MainMenu")!;
        var guides = FindMenuItem(menu.Items.Cast<object?>(), "view.guides");
        var rulers = FindMenuItem(menu.Items.Cast<object?>(), "view.rulers");
        var snap = FindMenuItem(menu.Items.Cast<object?>(), "view.snapGuides");
        Assert.NotNull(guides);
        Assert.NotNull(rulers);
        Assert.NotNull(snap);
        var guideCmd = (AppCommand)guides.Tag!;
        var rulerCmd = (AppCommand)rulers.Tag!;
        var snapCmd = (AppCommand)snap.Tag!;

        Assert.True(w.Session.ShowGuides);
        guideCmd.Execute();
        Assert.False(w.Session.ShowGuides);
        Assert.Null(w.Session.SelectedGuide);   // hidden guides can't stay selected
        Assert.Empty(w.Session.BuildOverlay().OfType<DeSignMage.Core.Editing.GuideOverlay>());
        guideCmd.Execute();
        Assert.True(w.Session.ShowGuides);

        Assert.True(w.Session.SnapToGuides);
        snapCmd.Execute();
        Assert.False(w.Session.SnapToGuides);

        Assert.True(TopRuler(w).IsVisible);
        rulerCmd.Execute();
        Assert.False(TopRuler(w).IsVisible);
        Assert.False(rulerCmd.IsChecked!());
        rulerCmd.Execute();
        Assert.True(TopRuler(w).IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void Right_button_zoom_still_works_when_it_starts_on_a_guide()
    {
        var (w, c) = Open();
        PullGuide(w, c, TopRuler(w), new Point(300, 10), new Point(300, 200));
        var g = Assert.Single(w.Session.Document.Guides);
        Click(w, c, 600, 450);                           // deselect
        double y = w.Session.Viewport.ToScreen(g.Origin).Y;
        double before = g.Origin.Y;

        w.MouseDown(InWindow(c, 200, y), MouseButton.Right);
        w.MouseMove(InWindow(c, 300, y + 40));
        w.MouseUp(InWindow(c, 400, y + 80), MouseButton.Right);
        Assert.Equal(1, w.Session.Viewport.ZoomStackDepth);
        Assert.Null(w.Session.SelectedGuide);
        Assert.Equal(before, g.Origin.Y, 6);
        w.Close();
    }
}
