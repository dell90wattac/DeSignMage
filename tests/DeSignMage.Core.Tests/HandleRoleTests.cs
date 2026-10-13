using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>
/// What the select tool's handles do (R21). Corners scale, left/right extend the width, the top middle slants and the bottom middle rotates;
/// with Ctrl the top middle extends the height upward (bottom edge fixed) and the bottom middle extends it downward (top edge fixed).
/// Every drag is one undoable command.
/// </summary>
public class HandleRoleTests
{
    private readonly EditorSession _s;

    public HandleRoleTests()
    {
        _s = new EditorSession(NewDoc());
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);   // screen == document coordinates
    }

    private PointerInput At(double x, double y, PointerButton b = PointerButton.None, ModifierKeys m = ModifierKeys.None) =>
        new(new Vec2(x, y), _s.Viewport.ToDoc(new Vec2(x, y)), b, m);

    private void Drag(double x0, double y0, double x1, double y1, ModifierKeys m = ModifierKeys.None)
    {
        _s.PointerDown(At(x0, y0, PointerButton.Left, m));
        _s.PointerMove(At((x0 + x1) / 2, (y0 + y1) / 2, PointerButton.None, m));
        _s.PointerMove(At(x1, y1, PointerButton.None, m));
        _s.PointerUp(At(x1, y1, PointerButton.Left, m));
    }

    private DocObject AddRect(double x, double y, double w, double h)
    {
        var r = Rect(x, y, w, h);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, r));
        return r;
    }

    // ---- the table ----

    [Theory]
    [InlineData(Handle.TopLeft, false, HandleRole.Scale)]
    [InlineData(Handle.TopLeft, true, HandleRole.Scale)]
    [InlineData(Handle.TopRight, true, HandleRole.Scale)]
    [InlineData(Handle.BottomRight, true, HandleRole.Scale)]
    [InlineData(Handle.BottomLeft, true, HandleRole.Scale)]
    [InlineData(Handle.Left, false, HandleRole.Extend)]
    [InlineData(Handle.Left, true, HandleRole.Extend)]
    [InlineData(Handle.Right, false, HandleRole.Extend)]
    [InlineData(Handle.Right, true, HandleRole.Extend)]
    [InlineData(Handle.Top, false, HandleRole.Slant)]
    [InlineData(Handle.Top, true, HandleRole.ExtendUp)]
    [InlineData(Handle.Bottom, false, HandleRole.Rotate)]
    [InlineData(Handle.Bottom, true, HandleRole.ExtendDown)]
    public void Each_handle_has_its_role_and_Ctrl_only_changes_the_top_and_bottom_middles(Handle h, bool ctrl, HandleRole expected) =>
        Assert.Equal(expected, SelectionHandles.Role(h, ctrl));

    [Theory]
    [InlineData(Handle.Top, false, CursorKind.Slant)]
    [InlineData(Handle.Top, true, CursorKind.SizeNS)]
    [InlineData(Handle.Bottom, false, CursorKind.Rotate)]
    [InlineData(Handle.Bottom, true, CursorKind.SizeNS)]
    [InlineData(Handle.Left, true, CursorKind.SizeWE)]
    [InlineData(Handle.TopLeft, true, CursorKind.SizeNWSE)]
    public void The_handle_cursor_shows_the_mode(Handle h, bool ctrl, CursorKind expected) =>
        Assert.Equal(expected, SelectionHandles.Cursor(h, ctrl));

    // ---- dragging ----

    [Fact]
    public void Ctrl_on_the_top_middle_extends_upward_with_the_bottom_edge_fixed()
    {
        var r = AddRect(100, 100, 100, 50);   // top handle (150,100), bottom edge y = 150
        _s.Selection.Set(r);
        Drag(150, 100, 150, 50, ModifierKeys.Control);   // up by 50: twice as tall
        Assert.Equal(new Rect2(100, 50, 200, 150), r.GetWorldBounds());
        Assert.Equal("Extend Up", _s.History.UndoDescription);
        Assert.Single(_s.History.UndoList.Skip(1));   // the Add plus ONE command for the drag

        _s.Undo();
        Assert.Equal(new Rect2(100, 100, 200, 150), r.GetWorldBounds());
        Drag(150, 100, 150, 125, ModifierKeys.Control);  // down: condenses, bottom edge still fixed
        Assert.Equal(new Rect2(100, 125, 200, 150), r.GetWorldBounds());
    }

    [Fact]
    public void Ctrl_on_the_top_middle_ignores_sideways_movement()
    {
        var r = AddRect(100, 100, 100, 50);
        _s.Selection.Set(r);
        Drag(150, 100, 400, 50, ModifierKeys.Control);   // only the vertical movement counts: it is a one-axis scale
        Assert.Equal(new Rect2(100, 50, 200, 150), r.GetWorldBounds());
    }

    [Fact]
    public void Ctrl_on_the_bottom_middle_extends_downward_with_the_top_edge_fixed()
    {
        var r = AddRect(100, 100, 100, 50);   // bottom handle (150,150), top edge y = 100
        _s.Selection.Set(r);
        Drag(150, 150, 150, 200, ModifierKeys.Control);
        Assert.Equal(new Rect2(100, 100, 200, 200), r.GetWorldBounds());
        Assert.Equal("Extend Down", _s.History.UndoDescription);
        _s.Undo();
        Assert.Equal(new Rect2(100, 100, 200, 150), r.GetWorldBounds());
    }

    [Fact]
    public void Without_Ctrl_the_top_slants_and_the_bottom_rotates_as_before()
    {
        var r = AddRect(100, 100, 100, 100);
        _s.Selection.Set(r);
        Drag(150, 100, 175, 90);
        Assert.Equal("Slant", _s.History.UndoDescription);
        Assert.Equal(new Vec2(125, 100), r.GetWorldTransform().Transform(new Vec2(0, 0)));
        _s.Undo();
        Drag(150, 200, 100, 150);   // a quarter turn about the center, no modifier needed
        Assert.Equal("Rotate", _s.History.UndoDescription);
        var corner = r.GetWorldTransform().Transform(new Vec2(0, 0));
        Assert.Equal(200, corner.X, 6);
        Assert.Equal(100, corner.Y, 6);
    }

    [Fact]
    public void Left_and_right_middles_extend_the_width_with_or_without_Ctrl()
    {
        var r = AddRect(100, 100, 100, 50);
        _s.Selection.Set(r);
        Drag(200, 125, 300, 125);
        Assert.Equal(new Rect2(100, 100, 300, 150), r.GetWorldBounds());
        _s.Undo();
        Drag(200, 125, 300, 125, ModifierKeys.Control);   // Ctrl makes no difference there
        Assert.Equal(new Rect2(100, 100, 300, 150), r.GetWorldBounds());
        Assert.Equal("Extend/Condense", _s.History.UndoDescription);
        _s.Undo();
        Drag(100, 125, 50, 125, ModifierKeys.Control);   // left middle: anchored at the right edge
        Assert.Equal(new Rect2(50, 100, 200, 150), r.GetWorldBounds());
    }

    [Fact]
    public void Corners_scale_as_before_and_Ctrl_still_means_from_the_center()
    {
        var r = AddRect(100, 100, 100, 50);
        _s.Selection.Set(r);
        Drag(200, 100, 300, 80);   // top-right, anchored bottom-left
        Assert.Equal(new Rect2(100, 50, 300, 150), r.GetWorldBounds());
        Assert.Equal("Scale", _s.History.UndoDescription);
        _s.Undo();
        Drag(100, 100, 50, 75, ModifierKeys.Control);   // top-left about the center (150,125): factor 2
        Assert.Equal(new Rect2(50, 75, 250, 175), r.GetWorldBounds());
        Assert.Equal("Scale", _s.History.UndoDescription);
    }

    [Fact]
    public void Extending_a_selection_scales_every_object_and_a_group_as_a_whole()
    {
        var a = AddRect(100, 100, 50, 40);
        var b = AddRect(200, 140, 50, 60);
        _s.Selection.Set([a, b]);                       // bounds (100,100)-(250,200): top handle at (175,100), bottom handle at (175,200)
        Drag(175, 200, 175, 300, ModifierKeys.Control); // bottom edge from 200 to 300, top fixed: everything twice as tall
        Assert.Equal(new Rect2(100, 100, 150, 180), a.GetWorldBounds());
        Assert.Equal(new Rect2(200, 180, 250, 300), b.GetWorldBounds());

        _s.Undo();
        Assert.Equal(new Rect2(100, 100, 150, 140), a.GetWorldBounds());
        var group = _s.GroupObjects([a, b])!;
        Drag(175, 100, 175, 0, ModifierKeys.Control);   // a group distorts as a whole (the bottom edge stays, y = 200)
        Assert.Equal(new Rect2(100, 0, 250, 200), group.GetWorldBounds());
        Assert.Equal(new Rect2(100, 0, 150, 80), a.GetWorldBounds());
        Assert.Equal(new Rect2(200, 80, 250, 200), b.GetWorldBounds());
    }

    // ---- when Ctrl counts ----

    [Fact]
    public void Ctrl_is_read_at_each_move_so_pressing_or_releasing_it_mid_drag_switches()
    {
        var r = AddRect(100, 100, 100, 50);
        _s.Selection.Set(r);
        _s.PointerDown(At(150, 100, PointerButton.Left));
        _s.PointerMove(At(175, 80));
        Assert.Equal(CursorKind.Slant, _s.CurrentCursor);
        _s.PointerMove(At(175, 50, PointerButton.None, ModifierKeys.Control));     // Ctrl pressed: now extending upward
        Assert.Equal(CursorKind.SizeNS, _s.CurrentCursor);
        Assert.Contains("EXTEND UP", _s.Hint);
        _s.PointerUp(At(175, 50, PointerButton.Left, ModifierKeys.Control));
        Assert.Equal(new Rect2(100, 50, 200, 150), r.GetWorldBounds());
        Assert.Equal("Extend Up", _s.History.UndoDescription);

        _s.Undo();
        _s.PointerDown(At(150, 100, PointerButton.Left, ModifierKeys.Control));
        _s.PointerMove(At(150, 50, PointerButton.None, ModifierKeys.Control));
        _s.PointerMove(At(175, 90));                                                // Ctrl released: back to slanting
        _s.PointerUp(At(175, 90, PointerButton.Left));
        Assert.Equal("Slant", _s.History.UndoDescription);
    }

    [Fact]
    public void A_Ctrl_key_press_without_any_pointer_movement_switches_a_drag_and_the_hover_cursor()
    {
        var r = AddRect(100, 100, 100, 50);
        _s.Selection.Set(r);
        _s.PointerMove(At(150, 100));                         // hovering the top middle handle
        Assert.Equal(CursorKind.Slant, _s.CurrentCursor);
        _s.ModifiersChanged(ModifierKeys.Control);            // Ctrl goes down; the pointer has not moved
        Assert.Equal(CursorKind.SizeNS, _s.CurrentCursor);
        _s.ModifiersChanged(ModifierKeys.None);
        Assert.Equal(CursorKind.Slant, _s.CurrentCursor);

        _s.PointerDown(At(150, 100, PointerButton.Left));
        _s.PointerMove(At(150, 50));
        _s.ModifiersChanged(ModifierKeys.Control);            // mid-drag, pointer still
        Assert.Equal(CursorKind.SizeNS, _s.CurrentCursor);
        Assert.NotNull(_s.Preview);
        var scaled = r.GetWorldBounds().Transform(_s.Preview!.WorldDelta);
        Assert.Equal(new Rect2(100, 50, 200, 150), scaled);   // the preview is the vertical extension
        _s.PointerUp(At(150, 50, PointerButton.Left, ModifierKeys.Control));
        Assert.Equal(new Rect2(100, 50, 200, 150), r.GetWorldBounds());
    }

    [Fact]
    public void Hovering_the_top_and_bottom_handles_shows_the_mode_for_the_Ctrl_state()
    {
        var r = AddRect(100, 100, 100, 50);
        _s.Selection.Set(r);
        _s.PointerMove(At(150, 100));
        Assert.Equal(CursorKind.Slant, _s.CurrentCursor);
        _s.PointerMove(At(150, 100, PointerButton.None, ModifierKeys.Control));
        Assert.Equal(CursorKind.SizeNS, _s.CurrentCursor);
        _s.PointerMove(At(150, 150));
        Assert.Equal(CursorKind.Rotate, _s.CurrentCursor);
        _s.PointerMove(At(150, 150, PointerButton.None, ModifierKeys.Control));
        Assert.Equal(CursorKind.SizeNS, _s.CurrentCursor);
        _s.PointerMove(At(200, 125, PointerButton.None, ModifierKeys.Control));       // left/right middles are unchanged
        Assert.Equal(CursorKind.SizeWE, _s.CurrentCursor);
        _s.PointerMove(At(200, 100, PointerButton.None, ModifierKeys.Control));       // so are the corners
        Assert.Equal(CursorKind.SizeNESW, _s.CurrentCursor);
    }
}
