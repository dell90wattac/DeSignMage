using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>The Vector Draw tool in the real window: its Toolbox button, the cursor, the Context Bar and a click sequence.</summary>
public class VectorDrawShellTests
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

    private static Point At(MainWindow w, DesignCanvas c, double x, double y) => c.TranslatePoint(new Point(x, y), w).GetValueOrDefault();

    private static void Click(MainWindow w, Point p)
    {
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
    }

    private static ToggleButton DrawButton(MainWindow w) =>
        w.GetVisualDescendants().OfType<ToggleButton>().Single(b => ToolTip.GetTip(b) is string tip && tip.StartsWith("Vector Draw tool"));

    [AvaloniaFact]
    public void The_Toolbox_has_a_Vector_Draw_button_between_Vector_Edit_and_Text_and_none_for_Loop_Edit()
    {
        var (w, _) = Open();
        Assert.Equal(["select", "vector", "vectordraw", "text", "rectangle", "ellipse"], Toolbox.Buttons.Select(b => b.ToolId));
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<ToggleButton>(), b => ToolTip.GetTip(b) is string tip && tip.StartsWith("Loop Edit tool"));
        Assert.NotNull(w.Session.Tools.FirstOrDefault(t => t.Id == "loop"));          // the tool itself is still there (Tools menu)
        w.Close();
    }

    [AvaloniaFact]
    public void The_Toolbox_button_activates_the_tool_with_its_own_cursor_and_a_hint_in_the_Context_Bar()
    {
        var (w, _) = Open();
        var button = DrawButton(w);
        var p = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), w).GetValueOrDefault();
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("vectordraw", w.Session.ActiveTool.Id);
        Assert.True(button.IsChecked);
        Assert.Equal(CursorKind.VectorDraw, w.Session.CurrentCursor);
        var bar = Assert.IsAssignableFrom<Control>(w.FindControl<ContentControl>("ContextBarHost")!.Content);
        var texts = bar.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Click: add node · Drag: curve · Double-click first node: close · Backspace: undo node · Enter/Esc: finish", texts);
        Assert.Contains("Vector Draw", texts);
        w.Close();
    }

    [AvaloniaFact]
    public void A_click_sequence_makes_one_uncolored_object_and_a_double_click_on_the_first_node_closes_it()
    {
        var (w, c) = Open();
        w.Session.SetTool("vectordraw");
        w.Session.Selection.Clear();

        // an open drawing: three clicks, a drag for a curve, Enter
        Click(w, At(w, c, 200, 200));
        Click(w, At(w, c, 400, 200));
        var a = At(w, c, 400, 400);
        w.MouseDown(a, MouseButton.Left);
        w.MouseMove(a + new Point(30, 0));
        w.MouseMove(a + new Point(60, 0));
        w.MouseUp(a + new Point(60, 0), MouseButton.Left);
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        var open = Assert.IsType<PathObject>(Assert.Single(w.Session.Document.ActiveLayer.Children));
        Assert.Equal(ObjectStyle.None, open.Style);
        var loop = Assert.Single(PathEditing.ToLoops(open.Geometry));
        Assert.False(loop.Closed);
        Assert.Equal(SegmentKind.Line, loop.Segments[0].Kind);
        Assert.Equal(SegmentKind.Cubic, loop.Segments[1].Kind);                 // the drag made the last vector a curve
        Assert.Equal(1, w.Session.History.UndoList.Count(x => x.Description.StartsWith("Add")));

        // a closed drawing: double-click on the first node
        Click(w, At(w, c, 600, 200));
        Click(w, At(w, c, 800, 200));
        Click(w, At(w, c, 700, 400));
        Click(w, At(w, c, 600, 200));
        Click(w, At(w, c, 600, 200));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, w.Session.Document.ActiveLayer.Children.Count);
        var closed = Assert.IsType<PathObject>(w.Session.Document.ActiveLayer.Children[1]);
        Assert.True(Assert.Single(PathEditing.ToLoops(closed.Geometry)).Closed);

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");     // one undo step per drawing
        Assert.Single(w.Session.Document.ActiveLayer.Children);
        w.Close();
    }
}
