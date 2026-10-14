using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>The Loop Edit tool driven like the UI does: select, move, copy, delete, reverse, open/close, to circle, break out.</summary>
public class LoopEditToolTests
{
    private readonly EditorSession _s;
    private LoopEditTool Tool => _s.GetTool<LoopEditTool>();
    private Layer L => _s.Document.ActiveLayer;

    public LoopEditToolTests()
    {
        _s = new EditorSession(NewDoc());
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
    }

    private PointerInput At(double x, double y, PointerButton b = PointerButton.None, ModifierKeys m = ModifierKeys.None, int clicks = 1) =>
        new(new Vec2(x, y), new Vec2(x, y), b, m, clicks);

    private void Click(double x, double y, ModifierKeys m = ModifierKeys.None, int clicks = 1)
    {
        _s.PointerDown(At(x, y, PointerButton.Left, m, clicks));
        _s.PointerUp(At(x, y, PointerButton.Left, m, clicks));
    }

    private void Drag(double x0, double y0, double x1, double y1, ModifierKeys m = ModifierKeys.None)
    {
        _s.PointerDown(At(x0, y0, PointerButton.Left));
        _s.PointerMove(At((x0 + x1) / 2, (y0 + y1) / 2, PointerButton.None, m));
        _s.PointerMove(At(x1, y1, PointerButton.None, m));
        _s.PointerUp(At(x1, y1, PointerButton.Left, m));
    }

    private static List<Loop> LoopsOf(DocObject o) => PathEditing.ToLoops(o.GetLocalGeometry()!);

    /// <summary>
    /// A frame (0..200, outer) with a hole (50..150, inner, wound the other way) and a loose open stroke, placed at (100,100).
    /// Loops: 0 = outer, 1 = inner, 2 = stroke (open).
    /// </summary>
    private PathObject AddThreeLoops()
    {
        var outer = PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(0, 0, 200, 200)))[0];
        var inner = PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(50, 50, 100, 100)))[0];
        PathEditing.Reverse(inner);
        var stroke = new Loop { Start = new Vec2(250, 0), Segments = [Segment.Line(new Vec2(300, 0)), Segment.Line(new Vec2(300, 60))] };
        var p = new PathObject(PathEditing.FromLoops([outer, inner, stroke], FillRule.EvenOdd))
        {
            Transform = Affine2.Translation(100, 100),
            Style = ObjectStyle.FillOnly(DocColor.FromHex("#336699")),
        };
        _s.History.Execute(new AddObjectCommand(L, p));
        _s.Selection.Set(p);
        _s.SetTool("loop");   // one object selected: its loops show at once
        return p;
    }

    // inner loop's left edge: x = 150, y 150..250 in the document; outer's left edge: x = 100.

    [Fact]
    public void The_tool_opens_the_selected_object_and_clicking_a_vector_selects_the_whole_loop()
    {
        var p = AddThreeLoops();
        Assert.Same(p, Tool.Target);
        Click(150, 200);   // the inner loop's left vector
        Assert.Equal(1, Tool.SelectedLoopCount);
        var r = Tool.Readout()!.Value;
        Assert.Equal(2, r.Number);
        Assert.Equal(3, r.Total);
        Assert.Equal(100, r.Width, 6);
        Assert.Equal(100, r.Height, 6);
        Assert.Equal(400, r.Length, 6);
        Assert.False(r.Clockwise);     // wound counter-clockwise
        Assert.True(r.Closed);
        Click(100, 200);               // another loop replaces the selection
        Assert.Equal(1, Tool.Readout()!.Value.Number);
        Assert.True(Tool.Readout()!.Value.Clockwise);
        Click(375, 100, ModifierKeys.Shift);   // ... and Shift adds (the open stroke)
        Assert.Equal(2, Tool.SelectedLoopCount);
        Click(900, 700);               // empty canvas deselects
        Assert.Equal(0, Tool.SelectedLoopCount);
    }

    [Fact]
    public void Dragging_a_loop_moves_only_that_loop_as_one_undo_step()
    {
        var p = AddThreeLoops();
        int steps = _s.History.UndoList.Count;
        Drag(150, 200, 180, 230);   // inner loop 30 right, 30 down
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Move Loop", _s.History.UndoDescription);
        var loops = LoopsOf(p);
        Assert.Equal(new Rect2(0, 0, 200, 200), loops[0].Bounds());           // the frame stayed
        Assert.Equal(new Rect2(80, 80, 180, 180), loops[1].Bounds());         // the hole moved
        Assert.Equal(3, loops.Count);
        Assert.Equal(1, Tool.SelectedLoopCount);                               // and is still selected
        _s.Undo();
        Assert.Equal(new Rect2(50, 50, 150, 150), LoopsOf(p)[1].Bounds());
    }

    [Fact]
    public void A_tiny_wobble_is_a_click_not_a_move()
    {
        var p = AddThreeLoops();
        int steps = _s.History.UndoList.Count;
        Drag(150, 200, 151, 201);
        Assert.Equal(steps, _s.History.UndoList.Count);
        Assert.Equal(new Rect2(50, 50, 150, 150), LoopsOf(p)[1].Bounds());
    }

    [Fact]
    public void Ctrl_drag_drops_a_copy_and_leaves_the_original()
    {
        var p = AddThreeLoops();
        Drag(150, 200, 400, 200, ModifierKeys.Control);
        var loops = LoopsOf(p);
        Assert.Equal(4, loops.Count);
        Assert.Equal(new Rect2(50, 50, 150, 150), loops[1].Bounds());
        Assert.Equal(new Rect2(300, 50, 400, 150), loops[3].Bounds());
        Assert.Equal("Copy Loop", _s.History.UndoDescription);
        Assert.Equal(1, Tool.SelectedLoopCount);   // the copy is selected
        Assert.Equal(4, Tool.Readout()!.Value.Number);
    }

    [Fact]
    public void Delete_removes_the_loop_and_deleting_the_last_one_removes_the_object()
    {
        var p = AddThreeLoops();
        Click(150, 200);
        Assert.True(_s.KeyDown(EditorKey.Delete, ModifierKeys.None));
        Assert.Equal(2, LoopsOf(p).Count);
        Assert.Equal("Delete Loop", _s.History.UndoDescription);
        _s.Undo();
        Assert.Equal(3, LoopsOf(p).Count);

        // everything: three deletes, then the object itself goes (and comes back with undo)
        foreach (var x in new[] { 100.0, 150.0 }) { Click(x, 200); _s.KeyDown(EditorKey.Delete, ModifierKeys.None); }
        Assert.Single(LoopsOf(p));
        Click(375, 100);   // the open stroke's first vector
        _s.KeyDown(EditorKey.Delete, ModifierKeys.None);
        Assert.Empty(L.Children);
        Assert.Null(Tool.Target);
        _s.Undo();
        Assert.Same(p, Assert.Single(L.Children));
    }

    [Fact]
    public void Delete_never_deletes_the_object_when_no_loop_is_selected()
    {
        var p = AddThreeLoops();
        _s.KeyDown(EditorKey.Delete, ModifierKeys.None);
        Assert.Same(p, Assert.Single(L.Children));
        Assert.Equal(3, LoopsOf(p).Count);
    }

    [Fact]
    public void Reverse_open_close_and_to_circle_edit_the_selected_loop()
    {
        var p = AddThreeLoops();
        Click(150, 200);
        Tool.ReverseDirection();
        Assert.True(LoopsOf(p)[1].IsClockwise);
        Assert.Equal("Reverse Loop", _s.History.UndoDescription);
        Assert.Equal(1, Tool.SelectedLoopCount);

        Tool.OpenLoop();
        Assert.False(LoopsOf(p)[1].Closed);
        Assert.False(Tool.CanOpen);
        Assert.True(Tool.CanClose);
        Tool.CloseLoop();
        Assert.True(LoopsOf(p)[1].Closed);

        Tool.ToCircle();
        var circle = LoopsOf(p)[1];
        Assert.All(circle.Segments, s => Assert.Equal(SegmentKind.Arc, s.Kind));
        var cb = circle.Bounds();
        Assert.Equal((50.0, 50.0, 150.0, 150.0), (Math.Round(cb.Left, 6), Math.Round(cb.Top, 6), Math.Round(cb.Right, 6), Math.Round(cb.Bottom, 6)));
        Assert.Equal(3, LoopsOf(p).Count);
    }

    [Fact]
    public void Close_loop_on_the_open_stroke()
    {
        var p = AddThreeLoops();
        Click(375, 100);
        Assert.True(Tool.CanClose);
        Tool.CloseLoop();
        Assert.True(LoopsOf(p)[2].Closed);
        Assert.Equal(3, LoopsOf(p)[2].Segments.Count);
    }

    [Fact]
    public void Break_out_makes_the_loop_a_graphic_of_its_own_in_one_undo_step()
    {
        var p = AddThreeLoops();
        Click(150, 200);
        Tool.BreakOut();
        Assert.Equal(2, L.Children.Count);
        Assert.Equal(2, LoopsOf(p).Count);
        var loop = Assert.IsType<PathObject>(L.Children[1]);   // just above the original
        Assert.Equal(new Rect2(150, 150, 250, 250), loop.GetWorldBounds());
        Assert.Equal(p.Style, loop.Style);
        Assert.Equal("Break Out Loop", _s.History.UndoDescription);
        _s.Undo();
        Assert.Same(p, Assert.Single(L.Children));
        Assert.Equal(3, LoopsOf(p).Count);
    }

    [Fact]
    public void Copy_and_cut_put_just_the_loop_on_the_clipboard_and_paste_makes_it_a_graphic()
    {
        var p = AddThreeLoops();
        Click(150, 200);
        Tool.Copy();
        Assert.True(_s.CanPaste);
        Assert.Equal(3, LoopsOf(p).Count);   // copy leaves it

        Tool.Cut();   // (pasting leaves the Loop Edit tool, so the cut comes first)
        Assert.Equal(2, LoopsOf(p).Count);
        Assert.Equal("Delete Loop", _s.History.UndoDescription);

        _s.Paste();
        Click(200, 200);   // the paste hangs on the cursor; a click at the loop's center puts it down in place
        var pasted = Assert.IsType<PathObject>(L.Children[0]);   // a paste is a new item: bottom of the layer
        Assert.Equal(new Rect2(150, 150, 250, 250), pasted.GetWorldBounds());
    }

    [Fact]
    public void Reorder_loops_fixes_directions_and_puts_inner_loops_first()
    {
        var p = AddThreeLoops();
        _s.Selection.Set(p);
        Tool.ReorderLoops();
        var loops = LoopsOf(p);
        Assert.Equal(new Rect2(50, 50, 150, 150), loops[0].Bounds());    // the hole comes first
        Assert.False(loops[0].IsClockwise);
        Assert.True(loops[1].IsClockwise);
        Assert.Equal("Reorder Loops", _s.History.UndoDescription);
    }

    [Fact]
    public void A_box_selects_the_loops_it_fully_encloses()
    {
        var p = AddThreeLoops();
        Drag(120, 120, 280, 280);   // encloses the inner loop (150..250) but not the frame
        Assert.Equal(1, Tool.SelectedLoopCount);
        Assert.Equal(2, Tool.Readout()!.Value.Number);
        Assert.Equal(3, LoopsOf(p).Count);   // nothing moved
    }

    [Fact]
    public void Hovering_a_loop_shows_the_crosshair_cursor_and_double_click_asks_for_the_loop_menu()
    {
        AddThreeLoops();
        _s.PointerMove(At(900, 700));
        Assert.Equal(CursorKind.Arrow, _s.CurrentCursor);
        _s.PointerMove(At(150, 200));
        Assert.Equal(CursorKind.SelectHit, _s.CurrentCursor);

        ContextMenuRequest? request = null;
        _s.ContextMenuRequested += (_, r) => request = r;
        Click(150, 200);
        Click(150, 200, clicks: 2);
        Assert.NotNull(request);
        Assert.Equal(MenuKind.Loop, request!.Kind);
        Assert.Equal(1, Tool.SelectedLoopCount);
    }

    [Fact]
    public void Direction_colors_loops_by_winding_and_numbers_label_each_loop()
    {
        AddThreeLoops();
        Tool.ShowDirection = true;
        Tool.ShowNumbers = true;
        var o = Assert.Single(_s.BuildOverlay().OfType<LoopEditOverlay>());
        Assert.Equal([LoopKind.Border, LoopKind.Interior, LoopKind.Open], o.Loops.Select(l => l.Kind));   // clockwise blue, counter-clockwise red, open black
        Assert.Equal([1, 2, 3], o.Numbers.Select(n => n.Number));
        Assert.Equal(3, o.StartMarks.Count);
        Tool.ShowDirection = false;
        var plain = Assert.Single(_s.BuildOverlay().OfType<LoopEditOverlay>());
        Assert.Equal([LoopKind.Border, LoopKind.Interior, LoopKind.Open], plain.Loops.Select(l => l.Kind));   // the hole is interior here too
    }

    [Fact]
    public void Right_button_zoom_works_in_the_loop_tool_and_does_not_select_or_move_loops()
    {
        var p = AddThreeLoops();
        double zoom = _s.Viewport.Zoom;
        _s.PointerDown(At(120, 120, PointerButton.Right));
        _s.PointerMove(At(280, 280));
        _s.PointerUp(At(280, 280, PointerButton.Right));
        Assert.True(_s.Viewport.Zoom > zoom);
        Assert.Equal(0, Tool.SelectedLoopCount);
        _s.PointerDown(At(5, 5, PointerButton.Right));   // plain right click: back out
        _s.PointerUp(At(5, 5, PointerButton.Right));
        Assert.Equal(zoom, _s.Viewport.Zoom, 6);
        Assert.Equal(3, LoopsOf(p).Count);
    }

    [Fact]
    public void Objects_never_move_in_the_loop_tool_and_clicking_another_object_opens_it()
    {
        var p = AddThreeLoops();
        var other = Rect(600, 400, 100, 100);
        _s.History.Execute(new AddObjectCommand(L, other));
        Click(650, 450);   // inside the other rectangle's fill
        Assert.Same(other, Tool.Target);
        Drag(600, 450, 640, 450);   // on its left edge
        Assert.Equal(new Rect2(640, 400, 740, 500), Assert.Single(L.Children.OfType<PathObject>(), o => o != p).GetWorldBounds());   // the loop moved inside its own graphic
    }

    [Fact]
    public void The_info_line_describes_the_graphic_and_the_selected_loop()
    {
        AddThreeLoops();
        Assert.StartsWith("Graphic  13 Vectors", Tool.InfoLine());   // 4 + 4 + 2 segments + 3 loop starts
        Click(150, 200);
        Assert.Contains("Loop 2 of 3  counter-clockwise", Tool.InfoLine());
    }

    [Fact]
    public void Switching_between_the_vector_and_loop_tools_keeps_the_object_open()
    {
        var p = AddThreeLoops();
        _s.SetTool("vector");
        Assert.Same(p, _s.GetTool<VectorEditTool>().Target);
        _s.SetTool("loop");
        Assert.Same(p, Tool.Target);
        _s.SetTool("select");
        Assert.Null(Tool.Target);
    }

    [Fact]
    public void Loops_of_a_combined_object_belong_to_their_parts()
    {
        var a = Rect(100, 100, 100, 100);
        var b = Rect(300, 100, 100, 100);
        _s.History.Execute(new AddObjectCommand(L, a));
        _s.History.Execute(new AddObjectCommand(L, b));
        _s.Selection.Set([a, b]);
        var combined = _s.CombineSelection()!;
        _s.SetTool("loop");
        Assert.Same(combined, Tool.Target);
        Drag(300, 150, 340, 150);   // the second square's left edge: that part moves 40 right
        var moved = Assert.IsType<CombinedObject>(Assert.Single(L.Children));
        Assert.Equal(new Rect2(100, 100, 440, 200), moved.GetWorldBounds());
        Assert.Equal(2, moved.Parts.Count);
    }
}
