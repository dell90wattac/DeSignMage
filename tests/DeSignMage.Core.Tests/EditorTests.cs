using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Drives the editor the way the UI does: pointer events in screen coordinates.</summary>
public class EditorTests
{
    private readonly EditorSession _s;

    public EditorTests()
    {
        _s = new EditorSession(NewDoc());
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);   // screen == document coordinates
    }

    private PointerInput At(double x, double y, PointerButton b = PointerButton.None, ModifierKeys m = ModifierKeys.None) =>
        new(new Vec2(x, y), _s.Viewport.ToDoc(new Vec2(x, y)), b, m);

    private void Drag(double x0, double y0, double x1, double y1, PointerButton b = PointerButton.Left, ModifierKeys m = ModifierKeys.None)
    {
        _s.PointerDown(At(x0, y0, b, m));
        _s.PointerMove(At((x0 + x1) / 2, (y0 + y1) / 2, PointerButton.None, m));
        _s.PointerMove(At(x1, y1, PointerButton.None, m));
        _s.PointerUp(At(x1, y1, b, m));
    }

    private void Click(double x, double y, PointerButton b = PointerButton.Left, ModifierKeys m = ModifierKeys.None)
    {
        _s.PointerDown(At(x, y, b, m));
        _s.PointerUp(At(x, y, b, m));
    }

    private DocObject AddRect(double x, double y, double w, double h)
    {
        var r = Rect(x, y, w, h);
        _s.History.Execute(new Commands.AddObjectCommand(_s.Document.ActiveLayer, r));
        return r;
    }

    // ---- Shape tools ----

    [Fact]
    public void Rectangle_tool_drag_creates_selected_rectangle_with_default_style()
    {
        _s.SetTool("rectangle");
        Drag(100, 100, 300, 250);
        var r = Assert.IsType<RectangleObject>(Assert.Single(_s.Document.ActiveLayer.Children));
        Assert.Equal(new Rect2(100, 100, 300, 250), r.GetWorldBounds());
        Assert.Equal(_s.DefaultStyle, r.Style);
        Assert.Equal([r], _s.Selection.Items);
        Assert.True(_s.History.CanUndo);
        Assert.IsType<SelectTool>(_s.ActiveTool);   // the editor returns to the Layout tool after a shape
    }

    [Fact]
    public void Alt_keeps_the_shape_tool_active()
    {
        _s.SetTool("rectangle");
        Drag(100, 100, 300, 250, m: ModifierKeys.Alt);
        Assert.IsType<RectangleTool>(_s.ActiveTool);
    }

    [Fact]
    public void Rectangle_drag_up_and_left_normalizes()
    {
        _s.SetTool("rectangle");
        Drag(300, 250, 100, 100);
        Assert.Equal(new Rect2(100, 100, 300, 250), _s.Document.ActiveLayer.Children[0].GetWorldBounds());
    }

    [Fact]
    public void Shape_tool_click_without_drag_creates_nothing()
    {
        _s.SetTool("ellipse");
        Drag(100, 100, 101, 101);
        Assert.Empty(_s.Document.ActiveLayer.Children);
    }

    [Fact]
    public void Ellipse_tool_ctrl_makes_circle()
    {
        _s.SetTool("ellipse");
        Drag(200, 200, 260, 220, m: ModifierKeys.Control);
        var e = Assert.IsType<EllipseObject>(_s.Document.ActiveLayer.Children[0]);
        Assert.Equal(new Rect2(200, 200, 260, 260), e.GetWorldBounds());
    }

    [Fact]
    public void Rectangle_tool_uses_corner_radius_setting()
    {
        _s.GetTool<RectangleTool>().CornerRadius = 12;
        _s.SetTool("rectangle");
        Drag(0, 0, 100, 100);
        Assert.Equal(12, ((RectangleObject)_s.Document.ActiveLayer.Children[0]).CornerRadius);
    }

    [Fact]
    public void Undo_removes_drawn_shape_and_clears_selection()
    {
        _s.SetTool("rectangle");
        Drag(100, 100, 300, 250);
        _s.Undo();
        Assert.Empty(_s.Document.ActiveLayer.Children);
        Assert.True(_s.Selection.IsEmpty);
        _s.Redo();
        Assert.Single(_s.Document.ActiveLayer.Children);
    }

    // ---- Select tool ----

    [Fact]
    public void Click_selects_and_click_on_empty_clears()
    {
        var r = AddRect(100, 100, 50, 50);
        Click(120, 120);
        Assert.Equal([r], _s.Selection.Items);
        Click(500, 500);
        Assert.True(_s.Selection.IsEmpty);
    }

    [Fact]
    public void Shift_click_with_nothing_selected_just_selects()
    {
        var a = AddRect(0, 0, 50, 50);
        Click(10, 10, m: ModifierKeys.Shift);
        Assert.Equal([a], _s.Selection.Items);
        Assert.Equal([a], _s.Document.ActiveLayer.Children);   // no group was made
    }

    [Fact]
    public void Shift_drag_on_selected_object_moves_constrained_instead_of_deselecting()
    {
        var r = AddRect(100, 100, 50, 50);
        _s.Selection.Set(r);
        Drag(120, 120, 200, 130, m: ModifierKeys.Shift);
        Assert.Equal([r], _s.Selection.Items);
        Assert.Equal(new Rect2(180, 100, 230, 150), r.GetWorldBounds());
    }

    [Fact]
    public void Marquee_selects_enclosed_objects()
    {
        var a = AddRect(10, 10, 20, 20);
        AddRect(200, 200, 20, 20);
        Drag(-20, -20, 100, 100);   // starts outside the page: its border, at 0, would be grabbed instead
        Assert.Equal([a], _s.Selection.Items);
    }

    [Fact]
    public void Drag_moves_selection_as_one_undo_step()
    {
        var r = AddRect(100, 100, 50, 50);
        Drag(120, 120, 170, 140);
        Assert.Equal(new Rect2(150, 120, 200, 170), r.GetWorldBounds());
        Assert.Null(_s.Preview);
        Assert.Equal("Move", _s.History.UndoDescription);
        _s.Undo();
        Assert.Equal(new Rect2(100, 100, 150, 150), r.GetWorldBounds());
    }

    [Fact]
    public void Ctrl_drag_copies()
    {
        var r = AddRect(100, 100, 50, 50);
        Drag(120, 120, 220, 120, m: ModifierKeys.Control);
        Assert.Equal(2, _s.Document.ActiveLayer.Children.Count);
        Assert.Equal(new Rect2(100, 100, 150, 150), r.GetWorldBounds());
        var copy = Assert.Single(_s.Selection.Items);
        Assert.NotSame(r, copy);
        Assert.Equal(new Rect2(200, 100, 250, 150), copy.GetWorldBounds());
        Assert.Equal(r.Style, copy.Style);
        Assert.Equal("Copy", _s.History.UndoDescription);
        _s.Undo();
        Assert.Single(_s.Document.ActiveLayer.Children);
    }

    [Fact]
    public void Preview_is_shown_while_dragging_and_document_is_untouched()
    {
        var r = AddRect(100, 100, 50, 50);
        _s.PointerDown(At(120, 120, PointerButton.Left));
        _s.PointerMove(At(160, 120));
        Assert.NotNull(_s.Preview);
        Assert.Equal(new Rect2(100, 100, 150, 150), r.GetWorldBounds());
        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));   // cancel
        Assert.Null(_s.Preview);
        _s.PointerUp(At(160, 120, PointerButton.Left));
        Assert.Equal(new Rect2(100, 100, 150, 150), r.GetWorldBounds());
    }

    [Fact]
    public void Dragging_side_handle_scales_one_axis()
    {
        var r = AddRect(100, 100, 100, 50);
        Click(150, 120);
        Drag(200, 125, 300, 125);   // right-middle handle → extend: double width, anchored left
        Assert.Equal(new Rect2(100, 100, 300, 150), r.GetWorldBounds());
        Assert.Equal("Extend/Condense", _s.History.UndoDescription);
    }

    [Fact]
    public void Corner_handle_scales_proportionally_from_the_opposite_corner()
    {
        var r = AddRect(100, 100, 100, 50);
        Click(150, 120);
        Drag(200, 100, 300, 80);   // top-right corner, anchored bottom-left (100,150)
        Assert.Equal(new Rect2(100, 50, 300, 150), r.GetWorldBounds());
        Assert.Equal("Scale", _s.History.UndoDescription);
    }

    [Fact]
    public void Corner_handle_with_shift_changes_height_only_and_ctrl_scales_about_center()
    {
        var r = AddRect(100, 100, 100, 50);
        Click(150, 120);
        Drag(100, 100, 130, 75, m: ModifierKeys.Shift);   // top-left, "Heighten"
        Assert.Equal(new Rect2(100, 75, 200, 150), r.GetWorldBounds());

        _s.Undo();
        Drag(100, 100, 50, 75, m: ModifierKeys.Control);  // top-left about center (150,125): factor 2
        Assert.Equal(new Rect2(50, 75, 250, 175), r.GetWorldBounds());
    }

    [Fact]
    public void Bottom_middle_handle_rotates_about_center()
    {
        var r = AddRect(100, 100, 100, 100);   // center (150,150), bottom handle (150,200)
        Click(150, 150);
        Drag(150, 200, 100, 150);   // a quarter turn (Ctrl is for extending downward since R21, no longer a 90° snap)
        Assert.Equal("Rotate", _s.History.UndoDescription);
        var corner = r.GetWorldTransform().Transform(new Vec2(0, 0));   // was top-left (100,100)
        Assert.Equal(200, corner.X, 6);
        Assert.Equal(100, corner.Y, 6);
    }

    [Fact]
    public void Top_middle_handle_slants_keeping_bottom_edge()
    {
        var r = AddRect(100, 100, 100, 50);   // top handle (150,100)
        Click(150, 120);
        Drag(150, 100, 175, 90);
        var m = r.GetWorldTransform();
        Assert.Equal(new Vec2(125, 100), m.Transform(new Vec2(0, 0)));    // top-left shifted by 25
        Assert.Equal(new Vec2(100, 150), m.Transform(new Vec2(0, 50)));   // bottom-left unchanged
        Assert.Equal("Slant", _s.History.UndoDescription);
    }

    [Fact]
    public void Arrow_keys_nudge_with_shift_and_ctrl_multipliers()
    {
        var r = AddRect(100, 100, 10, 10);
        _s.Selection.Set(r);
        double d = _s.NudgeDistance;
        _s.KeyDown(EditorKey.Right, ModifierKeys.None);
        Assert.Equal(100 + d, r.GetWorldBounds().Left, 9);
        _s.KeyDown(EditorKey.Down, ModifierKeys.Shift);
        Assert.Equal(100 + 5 * d, r.GetWorldBounds().Top, 9);
        _s.KeyDown(EditorKey.Left, ModifierKeys.Control);
        Assert.Equal(100 + d - d / 5, r.GetWorldBounds().Left, 9);
    }

    [Fact]
    public void Delete_key_deletes_selection_undoably()
    {
        var r = AddRect(0, 0, 10, 10);
        _s.Selection.Set(r);
        Assert.True(_s.KeyDown(EditorKey.Delete, ModifierKeys.None));
        Assert.Empty(_s.Document.ActiveLayer.Children);
        Assert.True(_s.Selection.IsEmpty);
        _s.Undo();
        Assert.Same(r, _s.Document.ActiveLayer.Children[0]);
    }

    [Fact]
    public void Set_selection_bounds_moves_and_resizes()
    {
        var r = AddRect(0, 0, 10, 20);
        _s.Selection.Set(r);
        _s.SetSelectionBounds(Rect2.FromXYWH(50, 60, 30, 40));
        Assert.Equal(Rect2.FromXYWH(50, 60, 30, 40), r.GetWorldBounds());
    }

    [Fact]
    public void Apply_fill_without_selection_changes_default_style()
    {
        var blue = new SolidPaint(DocColor.FromHex("#0000FF"));
        _s.ApplyFill(blue);
        Assert.Equal(blue, _s.DefaultStyle.Fill);
        Assert.False(_s.History.CanUndo);

        var r = AddRect(0, 0, 10, 10);
        _s.Selection.Set(r);
        _s.ApplyStrokePaint(null);
        Assert.Null(r.Style.Stroke);
    }

    [Fact]
    public void Escape_returns_to_select_tool()
    {
        _s.SetTool("ellipse");
        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));
        Assert.IsType<SelectTool>(_s.ActiveTool);
    }

    // ---- Navigation (right button = zoom, middle / space = pan) ----

    [Fact]
    public void Right_drag_zooms_to_marquee_and_right_click_steps_back()
    {
        AddRect(0, 0, 10, 10);
        var before = _s.Viewport.State;
        Drag(100, 100, 300, 260, PointerButton.Right);

        Assert.Equal(1, _s.Viewport.ZoomStackDepth);
        Assert.True(_s.Viewport.Zoom > 3);
        var center = _s.Viewport.ToScreen(new Vec2(200, 180));
        Assert.Equal(500, center.X, 6);   // marquee center is now the view center
        Assert.Equal(400, center.Y, 6);
        Assert.Single(_s.Document.ActiveLayer.Children);   // the document is not touched
        Assert.True(_s.Selection.IsEmpty);                // and the tool never saw the press

        Drag(450, 350, 550, 450, PointerButton.Right);
        Assert.Equal(2, _s.Viewport.ZoomStackDepth);

        Click(10, 10, PointerButton.Right);
        Click(10, 10, PointerButton.Right);
        Assert.Equal(0, _s.Viewport.ZoomStackDepth);
        Assert.Equal(before, _s.Viewport.State);
    }

    [Fact]
    public void Right_click_with_empty_zoom_stack_shows_everything_including_pasteboard()
    {
        var scrap = AddRect(2000, 100, 100, 100);   // off the 1728-pt-wide page
        Click(10, 10, PointerButton.Right);
        var all = _s.Document.Page.Bounds.Union(scrap.GetWorldBounds());
        var tl = _s.Viewport.ToScreen(all.TopLeft);
        var br = _s.Viewport.ToScreen(all.BottomRight);
        Assert.True(tl.X >= 0 && tl.Y >= 0 && br.X <= 1000 && br.Y <= 800);
        Assert.Equal(500, (tl.X + br.X) / 2, 6);
    }

    [Fact]
    public void Wheel_zoom_is_not_pushed_and_right_click_returns_to_the_last_marquee_step()
    {
        Drag(100, 100, 300, 260, PointerButton.Right);   // marquee step A
        var stepA = _s.Viewport.State;
        _s.Wheel(new Vec2(700, 100), 2);
        _s.Wheel(new Vec2(200, 600), -1);
        Assert.Equal(1, _s.Viewport.ZoomStackDepth);

        Click(10, 10, PointerButton.Right);   // discards the wheel changes
        Assert.Equal(stepA, _s.Viewport.State);
        Assert.Equal(1, _s.Viewport.ZoomStackDepth);
        Click(10, 10, PointerButton.Right);   // then steps back as usual
        Assert.Equal(0, _s.Viewport.ZoomStackDepth);
    }

    [Fact]
    public void Marquee_after_wheel_saves_the_marquee_step_not_the_wheel_view()
    {
        Drag(100, 100, 300, 260, PointerButton.Right);
        var stepA = _s.Viewport.State;
        _s.Wheel(new Vec2(500, 400), 3);
        Drag(400, 300, 600, 500, PointerButton.Right);
        Click(10, 10, PointerButton.Right);
        Assert.Equal(stepA, _s.Viewport.State);
    }

    [Fact]
    public void Shift_right_click_zooms_to_layout_and_alt_right_click_zooms_out_20_percent()
    {
        _s.Viewport.Set(5, new Vec2(-1000, -1000));
        Click(10, 10, PointerButton.Right, ModifierKeys.Shift);
        Assert.Equal(1, _s.Viewport.ZoomStackDepth);
        var tl = _s.Viewport.ToScreen(_s.Document.Page.Bounds.TopLeft);
        Assert.True(tl.X > 0 && tl.Y > 0);

        double z = _s.Viewport.Zoom;
        Click(10, 10, PointerButton.Right, ModifierKeys.Alt);
        Assert.Equal(z * 0.8, _s.Viewport.Zoom, 9);
    }

    [Fact]
    public void Menu_zooms_push_so_right_click_returns()
    {
        var before = _s.Viewport.State;
        _s.ZoomToContent(push: true);
        Click(10, 10, PointerButton.Right);
        Assert.Equal(before, _s.Viewport.State);
    }

    [Fact]
    public void Middle_button_can_be_set_to_zoom()
    {
        _s.MiddleButton = MiddleButtonUsage.Zoom;
        Drag(100, 100, 300, 260, PointerButton.Middle);
        Assert.Equal(1, _s.Viewport.ZoomStackDepth);
        Assert.True(_s.Viewport.Zoom > 3);
    }

    [Fact]
    public void Right_drag_works_while_another_tool_is_active()
    {
        _s.SetTool("rectangle");
        Drag(100, 100, 300, 260, PointerButton.Right);
        Assert.Empty(_s.Document.ActiveLayer.Children);
        Assert.Equal(1, _s.Viewport.ZoomStackDepth);
    }

    [Fact]
    public void Zoom_marquee_overlay_is_shown_while_right_dragging()
    {
        _s.PointerDown(At(100, 100, PointerButton.Right));
        _s.PointerMove(At(200, 200));
        Assert.Contains(_s.BuildOverlay(), o => o is MarqueeOverlay { Kind: MarqueeKind.Zoom });
        _s.PointerUp(At(200, 200, PointerButton.Right));
        Assert.DoesNotContain(_s.BuildOverlay(), o => o is MarqueeOverlay);
    }

    [Fact]
    public void Middle_drag_pans()
    {
        Drag(100, 100, 150, 80, PointerButton.Middle);
        Assert.Equal(new Vec2(50, -20), _s.Viewport.Pan);
    }

    [Fact]
    public void Space_left_drag_pans_instead_of_using_tool()
    {
        _s.SetTool("rectangle");
        _s.SpaceHeld = true;
        Drag(100, 100, 150, 150);
        Assert.Empty(_s.Document.ActiveLayer.Children);
        Assert.Equal(new Vec2(50, 50), _s.Viewport.Pan);
        Assert.Equal(CursorKind.Hand, _s.CurrentCursor);
    }

    [Fact]
    public void Wheel_zoom_keeps_point_under_cursor()
    {
        var anchor = new Vec2(321, 123);
        var docBefore = _s.Viewport.ToDoc(anchor);
        _s.Wheel(anchor, 1);
        Assert.Equal(1.25, _s.Viewport.Zoom, 9);   // 25% per notch
        var docAfter = _s.Viewport.ToDoc(anchor);
        Assert.Equal(docBefore.X, docAfter.X, 9);
        Assert.Equal(docBefore.Y, docAfter.Y, 9);
    }

    [Fact]
    public void Zoom_to_fit_keeps_margin()
    {
        _s.Viewport.FitMargin = 0.1;
        _s.Viewport.ZoomToFit(Rect2.FromXYWH(0, 0, 100, 100));
        Assert.Equal(6.4, _s.Viewport.Zoom, 9);   // 800 * 0.8 / 100
        Assert.Equal(new Vec2(500, 400), _s.Viewport.ToScreen(new Vec2(50, 50)));
    }
}
