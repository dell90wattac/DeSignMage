using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>A paste hangs on the cursor and the next left click puts it down.</summary>
public class PlaceToolTests
{
    private readonly EditorSession _s = new(NewDoc());
    private readonly Layer _layer;
    private readonly RectangleObject _original;

    public PlaceToolTests()
    {
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);   // screen == document coordinates; the view center is (500, 400)
        _layer = _s.Document.ActiveLayer;
        _original = Rect(100, 100, 60, 40);                       // center (130, 120)
        _s.History.Execute(new AddObjectCommand(_layer, _original));
        _s.Selection.Set(_original);
        _s.Copy();
        _s.Selection.Clear();
    }

    private PointerInput At(double x, double y, PointerButton b = PointerButton.None, int clicks = 1) =>
        new(new Vec2(x, y), _s.Viewport.ToDoc(new Vec2(x, y)), b, ModifierKeys.None, clicks);

    private void Move(double x, double y) => _s.PointerMove(At(x, y));

    private void LeftClick(double x, double y)
    {
        _s.PointerDown(At(x, y, PointerButton.Left));
        _s.PointerUp(At(x, y, PointerButton.Left));
    }

    private PlaceTool Tool => _s.GetTool<PlaceTool>();

    // ---- entering ----

    [Fact]
    public void Paste_hangs_the_copy_on_the_cursor_and_adds_nothing_yet()
    {
        int undo = _s.History.UndoList.Count;
        long revision = _s.Document.Revision;

        _s.Paste();

        Assert.True(_s.IsPlacing);
        Assert.IsType<PlaceTool>(_s.ActiveTool);
        Assert.Single(_layer.Children);
        Assert.Equal(undo, _s.History.UndoList.Count);
        Assert.Equal(revision, _s.Document.Revision);
        var preview = Assert.IsType<TransformPreview>(_s.Preview);
        Assert.Single(preview.Objects);
        Assert.True(preview.Copy);                           // the document under it stays drawn
        Assert.False(_s.Document.Contains(preview.Objects[0]));
        Assert.Equal("Click to place · Esc to cancel", _s.Hint);
    }

    [Fact]
    public void Without_the_pointer_over_the_canvas_the_copy_hangs_at_the_view_center()
    {
        _s.Paste();

        Assert.Equal(new Vec2(500, 400), Tool.TargetCenter);
        var d = _s.Preview!.WorldDelta;
        Assert.Equal(new Vec2(500, 400), d.Transform(new Vec2(130, 120)));      // its center, moved to the view center
    }

    [Fact]
    public void With_the_pointer_over_the_canvas_it_hangs_there_and_follows()
    {
        Move(300, 200);
        _s.Paste();
        Assert.Equal(new Vec2(300, 200), Tool.TargetCenter);

        Move(640, 510);

        Assert.Equal(new Vec2(640, 510), Tool.TargetCenter);
        Assert.Equal(new Vec2(640, 510), _s.Preview!.WorldDelta.Transform(new Vec2(130, 120)));
    }

    [Fact]
    public void After_the_pointer_left_the_canvas_a_paste_starts_at_the_view_center_and_follows_once_it_returns()
    {
        Move(300, 200);
        _s.PointerExited();                                   // e.g. on its way to the Edit menu

        _s.Paste();
        Assert.Equal(new Vec2(500, 400), Tool.TargetCenter);

        Move(210, 220);                                       // back over the canvas
        Assert.Equal(new Vec2(210, 220), Tool.TargetCenter);
    }

    // ---- placing ----

    [Fact]
    public void The_next_left_click_places_it_centered_on_the_click_as_one_undo_step()
    {
        _s.Paste();
        Move(400, 300);
        int undo = _s.History.UndoList.Count;

        LeftClick(420, 310);

        Assert.False(_s.IsPlacing);
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Null(_s.Preview);
        Assert.Equal(2, _layer.Children.Count);
        var placed = Assert.Single(_s.Selection.Items);
        Assert.NotSame(_original, placed);
        Assert.Equal(420, placed.GetWorldBounds().Center.X, 6);
        Assert.Equal(310, placed.GetWorldBounds().Center.Y, 6);
        Assert.Equal((60.0, 40.0), (placed.GetWorldBounds().Width, placed.GetWorldBounds().Height));
        Assert.Equal(undo + 1, _s.History.UndoList.Count);
        Assert.Equal("Paste", _s.History.UndoDescription);
        Assert.Equal(new Rect2(100, 100, 160, 140), _original.GetWorldBounds());   // the original did not move

        _s.Undo();
        Assert.Single(_layer.Children);
        _s.Redo();
        Assert.Equal(2, _layer.Children.Count);
        Assert.Equal(420, _layer.Children[1].GetWorldBounds().Center.X, 6);
    }

    [Fact]
    public void Several_objects_keep_their_arrangement_with_their_common_center_on_the_click()
    {
        var other = Rect(300, 100, 20, 20);
        _s.History.Execute(new AddObjectCommand(_layer, other));
        _s.Selection.Set([_original, other]);
        _s.Copy();
        _s.Selection.Clear();
        _s.Paste();

        LeftClick(600, 500);

        var placed = _s.Selection.Items;
        Assert.Equal(2, placed.Count);
        var union = placed.Select(o => o.GetWorldBounds()).Aggregate(Rect2.Empty, (a, b) => a.Union(b));
        Assert.Equal(600, union.Center.X, 6);
        Assert.Equal(500, union.Center.Y, 6);
        Assert.Equal(200.0, placed[1].GetWorldBounds().Left - placed[0].GetWorldBounds().Left, 6);   // still 200 apart, as they were
    }

    [Fact]
    public void A_click_places_even_if_the_pointer_never_moved_over_the_canvas_first()
    {
        _s.Paste();
        LeftClick(250, 250);
        Assert.Equal(250, _layer.Children[1].GetWorldBounds().Center.X, 6);
    }

    [Fact]
    public void A_release_without_a_press_in_the_tool_does_not_place()
    {
        _s.Paste();
        _s.PointerUp(At(250, 250, PointerButton.Left));
        Assert.True(_s.IsPlacing);
        Assert.Single(_layer.Children);
    }

    [Fact]
    public void Placing_snaps_to_guides_like_a_normal_move()
    {
        _s.History.Execute(new AddGuideCommand(Guide.Vertical(400)));
        _s.Paste();
        Move(397, 300);                                       // the pasted item's center is within the snap distance of the guide
        Assert.Equal(400, Tool.TargetCenter!.Value.X, 6);

        LeftClick(397, 300);

        Assert.Equal(400, _layer.Children[1].GetWorldBounds().Center.X, 6);
        Assert.Equal(300, _layer.Children[1].GetWorldBounds().Center.Y, 6);
        Assert.Null(_s.SelectedGuide);                        // the click was not taken as a click on the guide
    }

    [Fact]
    public void Far_from_a_guide_nothing_snaps()
    {
        _s.History.Execute(new AddGuideCommand(Guide.Vertical(400)));
        _s.Paste();
        LeftClick(430, 300);
        Assert.Equal(430, _layer.Children[1].GetWorldBounds().Center.X, 6);
    }

    // ---- cancelling ----

    [Fact]
    public void Escape_cancels_the_paste_without_an_undo_step()
    {
        _s.Paste();
        Move(300, 300);
        int undo = _s.History.UndoList.Count;
        long revision = _s.Document.Revision;

        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));

        Assert.False(_s.IsPlacing);
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Null(_s.Preview);
        Assert.Single(_layer.Children);
        Assert.Equal(undo, _s.History.UndoList.Count);
        Assert.Equal(revision, _s.Document.Revision);
        Assert.True(_s.Selection.IsEmpty);
    }

    [Fact]
    public void A_click_after_cancelling_places_nothing()
    {
        _s.Paste();
        _s.KeyDown(EditorKey.Escape, ModifierKeys.None);
        LeftClick(300, 300);
        Assert.Single(_layer.Children);
    }

    [Fact]
    public void Undo_while_placing_drops_the_paste_and_undoes_the_step_before()
    {
        var extra = Rect(500, 500, 10, 10);
        _s.History.Execute(new AddObjectCommand(_layer, extra));
        _s.Paste();

        _s.Undo();

        Assert.False(_s.IsPlacing);
        Assert.Null(_s.Preview);
        Assert.Single(_layer.Children);                       // the extra rectangle is gone, the original stays, no paste
        Assert.Same(_original, _layer.Children[0]);
    }

    [Fact]
    public void Switching_tools_abandons_the_paste()
    {
        _s.Paste();
        _s.SetTool("rectangle");
        Assert.False(_s.IsPlacing);
        Assert.Null(_s.Preview);
        Assert.Single(_layer.Children);
    }

    [Fact]
    public void Delete_while_placing_does_not_delete_anything()
    {
        _s.Selection.Set(_original);
        _s.Paste();                                           // the old selection is dropped when the paste starts
        _s.KeyDown(EditorKey.Delete, ModifierKeys.None);
        Assert.True(_s.IsPlacing);
        Assert.Single(_layer.Children);
        Assert.Same(_original, _layer.Children[0]);
    }

    [Fact]
    public void Pasting_again_replaces_the_one_hanging_on_the_cursor()
    {
        _s.Paste();
        var first = _s.Preview!.Objects[0];
        _s.Paste();
        Assert.NotSame(first, _s.Preview!.Objects[0]);
        Assert.Single(_s.Preview.Objects);
        LeftClick(300, 300);
        Assert.Equal(2, _layer.Children.Count);               // one paste, not two
    }

    // ---- zooming while placing ----

    [Fact]
    public void Right_drag_zoom_does_not_place_and_the_paste_keeps_following_the_pointer()
    {
        Move(300, 300);
        _s.Paste();

        _s.PointerDown(At(100, 100, PointerButton.Right));
        _s.PointerMove(At(200, 160));
        _s.PointerUp(At(300, 220, PointerButton.Right));      // marquee zoom

        Assert.True(_s.IsPlacing);
        Assert.Single(_layer.Children);
        Assert.True(_s.Viewport.Zoom > 1);
        Assert.Equal(1, _s.Viewport.ZoomStackDepth);
        // The pointer was last at (200, 160) on screen (it moved while the zoom box was drawn); the paste follows it, and is under it in the new view.
        var under = _s.Viewport.ToDoc(new Vec2(200, 160));
        Assert.Equal(under.X, Tool.TargetCenter!.Value.X, 6);
        Assert.Equal(under.Y, Tool.TargetCenter!.Value.Y, 6);
        Assert.Equal(new Vec2(200, 160), _s.Viewport.ToScreen(Tool.TargetCenter!.Value));

        LeftClick(350, 250);                                  // and now a left click places it, in the zoomed view
        Assert.Equal(2, _layer.Children.Count);
        Assert.Equal(_s.Viewport.ToDoc(new Vec2(350, 250)).X, _layer.Children[1].GetWorldBounds().Center.X, 6);
    }

    [Fact]
    public void A_plain_right_click_zooms_out_and_does_not_place()
    {
        _s.Viewport.PushAndZoomTo(Rect2.FromXYWH(0, 0, 200, 150));    // something to go back from
        Move(300, 300);
        _s.Paste();
        double zoomBefore = _s.Viewport.Zoom;

        _s.PointerDown(At(300, 300, PointerButton.Right));
        _s.PointerUp(At(300, 300, PointerButton.Right));

        Assert.True(_s.IsPlacing);
        Assert.Single(_layer.Children);
        Assert.True(_s.Viewport.Zoom < zoomBefore);
        Assert.Equal(new Vec2(300, 300), _s.Viewport.ToScreen(Tool.TargetCenter!.Value));
    }

    [Fact]
    public void The_wheel_zooms_while_placing()
    {
        Move(300, 300);
        _s.Paste();
        _s.Wheel(new Vec2(300, 300), 2);
        Assert.True(_s.IsPlacing);
        Assert.Equal(new Vec2(300, 300), _s.Viewport.ToScreen(Tool.TargetCenter!.Value));
    }

    [Fact]
    public void The_overlay_has_no_selection_frame_while_placing()
    {
        _s.Paste();
        Assert.DoesNotContain(_s.BuildOverlay(), o => o is SelectionOverlay);
    }
}
