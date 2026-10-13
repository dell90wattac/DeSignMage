using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using DeSignMage.App.Canvas;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Rendering;
using SkiaSharp;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Vector editing and the outline view in the real window.</summary>
public class VectorShellTests
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

    private static RectangleObject AddRect(MainWindow w, double x, double y, double size)
    {
        var r = new RectangleObject(size, size)
        {
            Transform = Affine2.Translation(x, y),
            Style = ObjectStyle.FillOnly(DocColor.FromHex("#F2B705")),
        };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        return r;
    }

    private static void DoubleClick(MainWindow w, Point p)
    {
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
    }

    [AvaloniaFact]
    public void Double_click_opens_vectors_and_dragging_a_node_reshapes()
    {
        var (w, c) = Open();
        AddRect(w, 200, 200, 300);
        DoubleClick(w, P(w, c, new Vec2(350, 350)));
        Assert.Equal("vector", w.Session.ActiveTool.Id);

        var corner = P(w, c, new Vec2(500, 500));
        w.MouseDown(corner, MouseButton.Left);
        w.MouseMove(corner + new Point(20, 0));
        w.MouseUp(corner + new Point(40, 20), MouseButton.Left);
        var obj = Assert.IsType<PathObject>(Assert.Single(w.Session.Document.ActiveLayer.Children));
        Assert.True(obj.GetWorldBounds().Right > 500);
        Assert.Equal(200, obj.GetWorldBounds().Left, 6);   // the object itself did not move

        w.KeyPress(Key.D1, RawInputModifiers.None, PhysicalKey.Digit1, "1");   // Layout tool
        Assert.Equal("select", w.Session.ActiveTool.Id);
        w.KeyPress(Key.D2, RawInputModifiers.None, PhysicalKey.Digit2, "2");   // Vector tool
        Assert.Equal("vector", w.Session.ActiveTool.Id);
        w.Close();
    }

    [AvaloniaFact]
    public void Double_click_on_a_vector_opens_the_vector_menu_and_its_items_work()
    {
        var (w, c) = Open();
        AddRect(w, 200, 200, 300);
        DoubleClick(w, P(w, c, new Vec2(350, 350)));
        DoubleClick(w, P(w, c, new Vec2(320, 200)));   // on the top vector
        var menu = w.LastObjectMenu!;
        Assert.True(menu.IsOpen);
        var headers = menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToList();
        Assert.Equal(["_Insert", "To _Line", "To _Arc", "To _Bezier", "Break L_oop", "Set Start/_End", "_Join Start/End", "_Delete"], headers);
        Assert.False(menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "To _Line").IsEnabled);   // already a line
        Assert.True(menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "_Insert").IsEnabled);
        menu.Close();
        w.Close();
    }

    [AvaloniaFact]
    public void Letter_keys_act_on_the_hovered_vector_only_in_vector_mode()
    {
        var (w, c) = Open();
        AddRect(w, 200, 200, 300);
        var top = P(w, c, new Vec2(320, 200));
        w.MouseMove(top);
        w.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");   // Layout tool: nothing happens
        Assert.IsType<RectangleObject>(Assert.Single(w.Session.Document.ActiveLayer.Children));

        DoubleClick(w, P(w, c, new Vec2(350, 350)));
        w.MouseMove(top);
        w.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");   // To Arc
        var path = Assert.IsType<PathObject>(Assert.Single(w.Session.Document.ActiveLayer.Children));
        Assert.True(path.Geometry.HasArcs);
        w.Close();
    }

    /// <summary>Saves screenshots of vector mode and the outline view when DSM_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public void Screenshots_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        var w = new MainWindow(new StartupOptions(Demo: true)) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        var c = w.FindControl<DesignCanvas>("Surface")!;
        // Combine the paw (pad + toes) so its overlaps show, then open its vectors.
        var paw = w.Session.Document.ActiveLayer.Children.OfType<EllipseObject>().ToList();
        w.Session.Selection.Set(paw);
        w.Session.CombineSelection();
        w.Session.ShowFillColors = false;
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "outline-view.png"));
        w.Session.ShowFillColors = true;
        w.Session.EditVectors(w.Session.Selection.Items[0]);
        var pad = paw.OrderByDescending(e => e.Width).First().GetWorldBounds();
        w.MouseMove(P(w, c, new Vec2(pad.Center.X, pad.Bottom)));
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "vector-mode.png"));
#pragma warning restore CS0618
        w.Close();
    }

    [AvaloniaFact]
    public void F3_toggles_fill_colors()
    {
        var (w, _) = Open();
        Assert.True(w.Session.ShowFillColors);
        w.KeyPress(Key.F3, RawInputModifiers.None, PhysicalKey.F3, null);
        Assert.False(w.Session.ShowFillColors);
        w.CaptureRenderedFrame();   // renders the outline view without error
        w.KeyPress(Key.F3, RawInputModifiers.None, PhysicalKey.F3, null);
        Assert.True(w.Session.ShowFillColors);
        w.Close();
    }

    [Fact]
    public void Outline_view_draws_border_blue_interior_red_and_no_fill()
    {
        var doc = new Document(PageSetup.FromInches(2, 1));
        var s = new EditorSession(doc);
        RectangleObject R(double x, double size) => new(size, size)
        {
            Transform = Affine2.Translation(x + 0.5, x + 0.5),   // hairlines centred on pixels
            Style = ObjectStyle.FillOnly(DocColor.Black),
        };
        var outer = R(10, 80);
        var inner = R(30, 40);
        s.History.Execute(new AddObjectCommand(doc.ActiveLayer, outer));
        s.History.Execute(new AddObjectCommand(doc.ActiveLayer, inner));
        s.Selection.Set([outer, inner]);
        s.CombineSelection();

        using var renderer = new SceneRenderer(doc) { Wireframe = true };
        var pic = renderer.GetDocumentPicture(null);
        using var bmp = new SKBitmap(100, 100);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawPicture(pic.Picture);
        }
        pic.Release();
        Assert.Equal(SKColors.White, bmp.GetPixel(20, 50));                 // no fill
        Assert.Equal(SceneRenderer.BorderColor, bmp.GetPixel(10, 50));      // outer edge
        Assert.Equal(SceneRenderer.InteriorColor, bmp.GetPixel(30, 50));    // inner edge
    }
}
