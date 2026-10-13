using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Ruler guides (requirement R4): model, commands, snapping math and the editor gestures.</summary>
public class GuideTests
{
    // Page is 24 x 12 in = 1728 x 864 pt.
    private readonly EditorSession _s = new(NewDoc());

    public GuideTests()
    {
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

    private void Click(double x, double y, PointerButton b = PointerButton.Left)
    {
        _s.PointerDown(At(x, y, b));
        _s.PointerUp(At(x, y, b));
    }

    private Guide AddGuide(Guide g)
    {
        _s.History.Execute(new AddGuideCommand(g));
        return g;
    }

    private DocObject AddRect(double x, double y, double w, double h)
    {
        var r = Rect(x, y, w, h);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, r));
        return r;
    }

    // ---- Model ----

    [Theory]
    [InlineData(0, GuideOrientation.Horizontal)]
    [InlineData(90, GuideOrientation.Vertical)]
    [InlineData(180, GuideOrientation.Horizontal)]
    [InlineData(-90, GuideOrientation.Vertical)]
    [InlineData(45, GuideOrientation.Angled)]
    public void Orientation_follows_the_angle(double degrees, GuideOrientation expected) =>
        Assert.Equal(expected, new Guide(Vec2.Zero, degrees).Orientation);

    [Fact]
    public void Angle_is_kept_in_0_to_180()
    {
        Assert.Equal(135, new Guide(Vec2.Zero, -45).AngleDegrees, 9);
        Assert.Equal(0, new Guide(Vec2.Zero, 180).AngleDegrees, 9);
        Assert.Equal(10, new Guide(Vec2.Zero, 370).AngleDegrees, 9);
    }

    [Fact]
    public void Distance_is_perpendicular_to_the_line()
    {
        var h = Guide.Horizontal(100);
        Assert.Equal(7, h.DistanceTo(new Vec2(500, 107)), 9);
        var v = Guide.Vertical(50);
        Assert.Equal(3, v.DistanceTo(new Vec2(47, 900)), 9);
        var d = new Guide(new Vec2(0, 0), 45);   // user-space 45° = up and to the right = (1, -1) in document space
        Assert.Equal(0, d.DistanceTo(new Vec2(10, -10)), 9);
        Assert.Equal(Math.Sqrt(2) * 5, d.DistanceTo(new Vec2(0, 10)), 9);
    }

    [Fact]
    public void Hit_test_picks_the_nearest_guide_within_tolerance()
    {
        var a = Guide.Horizontal(100);
        var b = Guide.Horizontal(103);
        Assert.Same(b, Guide.HitTest([a, b], new Vec2(0, 102), 4));
        Assert.Null(Guide.HitTest([a, b], new Vec2(0, 120), 4));
    }

    [Fact]
    public void Center_on_page_moves_a_vertical_guide_to_the_horizontal_middle()
    {
        var g = Guide.Vertical(10, 77);
        var o = g.CenteredOn(_s.Document.Page);
        Assert.Equal(864, o.X, 9);    // 1728 / 2
        Assert.Equal(77, o.Y, 9);     // other coordinate kept
    }

    [Fact]
    public void Center_on_page_moves_a_horizontal_guide_to_the_vertical_middle()
    {
        var g = Guide.Horizontal(10, 33);
        var o = g.CenteredOn(_s.Document.Page);
        Assert.Equal(432, o.Y, 9);    // 864 / 2
        Assert.Equal(33, o.X, 9);
    }

    [Fact]
    public void Center_on_page_puts_an_angled_guide_through_the_page_center()
    {
        var g = new Guide(new Vec2(100, 100), 30);
        var o = g.CenteredOn(_s.Document.Page);
        var moved = new Guide(o, 30);
        Assert.Equal(0, moved.DistanceTo(_s.Document.Page.Bounds.Center), 6);
        Assert.Equal(30, moved.AngleDegrees, 9);
    }

    // ---- Commands ----

    [Fact]
    public void Add_and_remove_are_undoable_and_keep_the_same_instance()
    {
        var g = AddGuide(Guide.Horizontal(100));
        Assert.Equal([g], _s.Document.Guides);
        _s.Undo();
        Assert.Empty(_s.Document.Guides);
        _s.Redo();
        Assert.Same(g, Assert.Single(_s.Document.Guides));

        _s.RemoveGuide(g);
        Assert.Empty(_s.Document.Guides);
        _s.Undo();
        Assert.Same(g, Assert.Single(_s.Document.Guides));
    }

    [Fact]
    public void Remove_restores_the_guide_at_its_old_list_position()
    {
        var a = AddGuide(Guide.Horizontal(10));
        var b = AddGuide(Guide.Horizontal(20));
        var c = AddGuide(Guide.Horizontal(30));
        _s.RemoveGuide(b);
        _s.Undo();
        Assert.Equal([a, b, c], _s.Document.Guides);
    }

    [Fact]
    public void Remove_all_is_one_undo_step()
    {
        var a = AddGuide(Guide.Horizontal(10));
        var b = AddGuide(Guide.Vertical(20));
        int steps = _s.History.UndoList.Count;
        _s.RemoveAllGuides();
        Assert.Empty(_s.Document.Guides);
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        _s.Undo();
        Assert.Equal([a, b], _s.Document.Guides);
        _s.Redo();
        Assert.Empty(_s.Document.Guides);
    }

    [Fact]
    public void Lock_and_move_and_center_are_undoable()
    {
        var g = AddGuide(Guide.Vertical(100, 5));
        _s.SetGuideLocked(g, true);
        Assert.True(g.Locked);
        _s.Undo();
        Assert.False(g.Locked);

        _s.SetGuide(g, new Vec2(250, 5), 90);
        Assert.Equal(250, g.Origin.X);
        _s.CenterGuideOnPage(g);
        Assert.Equal(864, g.Origin.X, 9);
        _s.Undo();
        Assert.Equal(250, g.Origin.X);
        _s.Undo();
        Assert.Equal(100, g.Origin.X);
    }

    [Fact]
    public void Changing_a_guide_through_commands_notifies_the_document()
    {
        int changes = 0;
        _s.Document.Changed += (_, _) => changes++;
        var g = AddGuide(Guide.Horizontal(10));
        _s.SetGuideLocked(g, true);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Guides_do_not_count_as_objects_or_content()
    {
        AddGuide(Guide.Horizontal(10));
        Assert.Equal(0, _s.Document.ObjectCount);
        Assert.True(_s.Document.GetContentBounds().IsEmpty);
    }

    // ---- Snapping math ----

    [Fact]
    public void Vertical_guide_snaps_left_edge_center_or_right_edge_whichever_is_nearest()
    {
        var guides = new[] { Guide.Vertical(200) };
        // right edge 198 is 2 away
        var r = GuideSnapping.SnapBounds(guides, new Rect2(100, 50, 198, 90), 5);
        Assert.Equal(2, r.Delta.X, 9);
        Assert.Equal(0, r.Delta.Y);
        Assert.Same(guides[0], r.GuideX);
        // center (150 + 1) is 49 away: too far; but a box centered at 203 snaps by -3
        r = GuideSnapping.SnapBounds(guides, new Rect2(153, 0, 253, 10), 5);
        Assert.Equal(-3, r.Delta.X, 9);
        // left edge 197
        r = GuideSnapping.SnapBounds(guides, new Rect2(197, 0, 400, 10), 5);
        Assert.Equal(3, r.Delta.X, 9);
    }

    [Fact]
    public void Horizontal_guide_snaps_top_center_bottom()
    {
        var guides = new[] { Guide.Horizontal(300) };
        Assert.Equal(-2, GuideSnapping.SnapBounds(guides, new Rect2(0, 302, 50, 340), 5).Delta.Y, 9);   // top edge
        Assert.Equal(4, GuideSnapping.SnapBounds(guides, new Rect2(0, 250, 50, 342), 5).Delta.Y, 9);    // center 296
        Assert.Equal(1, GuideSnapping.SnapBounds(guides, new Rect2(0, 200, 50, 299), 5).Delta.Y, 9);    // bottom edge
    }

    [Fact]
    public void Nothing_beyond_the_tolerance_snaps()
    {
        var guides = new[] { Guide.Vertical(200), Guide.Horizontal(300) };
        var r = GuideSnapping.SnapBounds(guides, new Rect2(100, 100, 150, 150), 5);
        Assert.False(r.Snapped);
        Assert.Equal(Vec2.Zero, r.Delta);
    }

    [Fact]
    public void Both_axes_snap_independently()
    {
        var guides = new[] { Guide.Vertical(200), Guide.Horizontal(300) };
        var r = GuideSnapping.SnapBounds(guides, new Rect2(201, 302, 260, 360), 5);
        Assert.Equal(new Vec2(-1, -2), r.Delta);
        Assert.Same(guides[0], r.GuideX);
        Assert.Same(guides[1], r.GuideY);
    }

    [Fact]
    public void A_locked_axis_does_not_snap()
    {
        var guides = new[] { Guide.Vertical(200), Guide.Horizontal(300) };
        var r = GuideSnapping.SnapBounds(guides, new Rect2(201, 302, 260, 360), 5, snapX: false);
        Assert.Equal(new Vec2(0, -2), r.Delta);
    }

    [Fact]
    public void The_closest_of_several_guides_wins()
    {
        var guides = new[] { Guide.Vertical(200), Guide.Vertical(204) };
        var r = GuideSnapping.SnapBounds(guides, new Rect2(0, 0, 203, 10), 5);   // right edge: 1 from 204, 3 from 200
        Assert.Equal(1, r.Delta.X, 9);
        Assert.Same(guides[1], r.GuideX);
    }

    [Fact]
    public void Angled_guide_snaps_a_corner_along_its_normal_only_when_no_axis_guide_did()
    {
        var angled = new Guide(new Vec2(0, 0), 45);   // the line y = -x
        var r = GuideSnapping.SnapBounds([angled], new Rect2(100, -101, 150, -50), 5);   // top-right corner (150,-101): off by ~
        Assert.True(r.Snapped);
        Assert.Same(angled, r.GuideAngled);
        // After applying the delta the snapped point lies on the line.
        var moved = new Rect2(100, -101, 150, -50).Transform(Affine2.Translation(r.Delta));
        double best = new[] { moved.TopLeft, new Vec2(moved.Right, moved.Top), moved.BottomRight, new Vec2(moved.Left, moved.Bottom), moved.Center }
            .Min(p => angled.DistanceTo(p));
        Assert.Equal(0, best, 6);

        var vertical = Guide.Vertical(98);
        var both = GuideSnapping.SnapBounds([angled, vertical], new Rect2(100, -101, 150, -50), 5);
        Assert.Null(both.GuideAngled);
        Assert.Same(vertical, both.GuideX);
    }

    [Fact]
    public void Snap_point_snaps_each_axis()
    {
        var guides = new[] { Guide.Vertical(200), Guide.Horizontal(300) };
        Assert.Equal(new Vec2(200, 300), new Vec2(203, 297) + GuideSnapping.SnapPoint(guides, new Vec2(203, 297), 5).Delta);
        Assert.Equal(new Vec2(203, 300), new Vec2(203, 297) + GuideSnapping.SnapPoint(guides, new Vec2(203, 297), 5, snapX: false).Delta);
    }

    // ---- Creating a guide from a ruler ----

    [Fact]
    public void Dragging_from_the_top_ruler_onto_the_canvas_creates_a_horizontal_guide_that_follows_the_mouse()
    {
        _s.BeginGuideFromRuler(GuideOrientation.Horizontal, new Vec2(400, -5));
        Assert.True(_s.IsDraggingGuide);
        _s.MoveGuideDrag(new Vec2(420, 250));
        // Not in the document yet, but drawn live (red, active) at the pointer.
        Assert.Empty(_s.Document.Guides);
        var live = _s.BuildOverlay().OfType<GuideOverlay>().Single();
        Assert.True(live.Selected);
        Assert.Equal(250, live.Origin.Y);
        Assert.Equal(0, live.AngleDegrees);
        Assert.Equal(GuideOrientation.Horizontal, _s.ActiveGuideMarker!.Value.Orientation);

        _s.EndGuideDrag(new Vec2(420, 260));
        var g = Assert.Single(_s.Document.Guides);
        Assert.Equal(GuideOrientation.Horizontal, g.Orientation);
        Assert.Equal(260, g.Origin.Y, 9);
        Assert.Same(g, _s.SelectedGuide);
        Assert.False(_s.IsDraggingGuide);
        Assert.Equal("Add Guide", _s.History.UndoDescription);
    }

    [Fact]
    public void Dragging_from_the_left_ruler_creates_a_vertical_guide()
    {
        _s.BeginGuideFromRuler(GuideOrientation.Vertical, new Vec2(-5, 300));
        _s.EndGuideDrag(new Vec2(340, 300));
        var g = Assert.Single(_s.Document.Guides);
        Assert.Equal(GuideOrientation.Vertical, g.Orientation);
        Assert.Equal(340, g.Origin.X, 9);
    }

    [Fact]
    public void Releasing_on_the_ruler_discards_the_new_guide()
    {
        _s.BeginGuideFromRuler(GuideOrientation.Horizontal, new Vec2(400, -5));
        _s.MoveGuideDrag(new Vec2(400, 100));
        _s.EndGuideDrag(new Vec2(400, -8));
        Assert.Empty(_s.Document.Guides);
        Assert.False(_s.History.CanUndo);
    }

    [Fact]
    public void Guides_are_unlimited_and_each_creation_is_one_undo_step()
    {
        for (int i = 0; i < 40; i++)
        {
            _s.BeginGuideFromRuler(GuideOrientation.Vertical, new Vec2(-5, 10));
            _s.EndGuideDrag(new Vec2(10 + i * 10, 20));
        }
        Assert.Equal(40, _s.Document.Guides.Count);
        _s.Undo();
        Assert.Equal(39, _s.Document.Guides.Count);
    }

    [Fact]
    public void Creating_a_guide_turns_guide_display_on()
    {
        _s.ShowGuides = false;
        _s.BeginGuideFromRuler(GuideOrientation.Horizontal, new Vec2(0, -1));
        Assert.True(_s.ShowGuides);
        _s.CancelGuideDrag();
    }

    // ---- Selecting, moving, locking ----

    [Fact]
    public void A_click_selects_a_guide_and_it_draws_red_while_others_do_not()
    {
        var a = AddGuide(Guide.Horizontal(200));
        var b = AddGuide(Guide.Vertical(500));
        _s.SelectedGuide = null;
        Click(300, 202);
        Assert.Same(a, _s.SelectedGuide);

        var overlays = _s.BuildOverlay().OfType<GuideOverlay>().ToList();
        Assert.Equal(2, overlays.Count);
        Assert.Single(overlays, o => o.Selected);
        Assert.Equal(200, overlays.Single(o => o.Selected).Origin.Y);
        Assert.False(overlays.Single(o => !o.Selected).Selected);

        Click(500, 400);
        Assert.Same(b, _s.SelectedGuide);
    }

    [Fact]
    public void Clicking_empty_canvas_or_an_object_deselects_the_guide()
    {
        var g = AddGuide(Guide.Horizontal(200));
        _s.SelectedGuide = g;
        Click(600, 600);
        Assert.Null(_s.SelectedGuide);

        var r = AddRect(700, 400, 100, 100);
        _s.SelectedGuide = g;
        Click(750, 450);
        Assert.Null(_s.SelectedGuide);
        Assert.Equal([r], _s.Selection.Items);
    }

    [Fact]
    public void Selecting_a_guide_clears_the_object_selection_and_the_other_way_round()
    {
        var r = AddRect(700, 400, 100, 100);
        var g = AddGuide(Guide.Horizontal(200));
        _s.Selection.Set(r);
        Click(300, 200);
        Assert.Same(g, _s.SelectedGuide);
        Assert.True(_s.Selection.IsEmpty);
        _s.Selection.Set(r);
        Assert.Null(_s.SelectedGuide);
    }

    [Fact]
    public void Dragging_a_guide_moves_it_perpendicular_to_itself_as_one_undo_step()
    {
        var g = AddGuide(Guide.Horizontal(200, 40));
        int steps = _s.History.UndoList.Count;
        Drag(300, 200, 450, 320);                     // the x part of the drag is ignored for a horizontal guide
        Assert.Equal(320, g.Origin.Y, 9);
        Assert.Equal(40, g.Origin.X, 9);
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Same(g, _s.SelectedGuide);
        _s.Undo();
        Assert.Equal(200, g.Origin.Y, 9);
    }

    [Fact]
    public void A_dragged_guide_follows_the_mouse_before_release()
    {
        var g = AddGuide(Guide.Vertical(300));
        _s.PointerDown(At(300, 100, PointerButton.Left));
        _s.PointerMove(At(380, 100));
        Assert.Equal(300, g.Origin.X);                                      // model untouched until release
        Assert.Equal(380, _s.GuideOrigin(g).X, 9);
        Assert.Equal(380, _s.BuildOverlay().OfType<GuideOverlay>().Single().Origin.X, 9);
        Assert.Equal(380, _s.ActiveGuideMarker!.Value.Origin.X, 9);
        _s.PointerUp(At(380, 100, PointerButton.Left));
        Assert.Equal(380, g.Origin.X, 9);
    }

    [Fact]
    public void A_locked_guide_can_be_selected_but_not_dragged()
    {
        var g = AddGuide(Guide.Horizontal(200));
        _s.SetGuideLocked(g, true);
        Drag(300, 200, 300, 330);
        Assert.Equal(200, g.Origin.Y, 9);
        Assert.Same(g, _s.SelectedGuide);
        _s.SetGuideLocked(g, false);
        Drag(300, 200, 300, 330);
        Assert.Equal(330, g.Origin.Y, 9);
    }

    [Fact]
    public void Dragging_a_guide_and_pressing_escape_cancels()
    {
        var g = AddGuide(Guide.Horizontal(200));
        _s.PointerDown(At(300, 200, PointerButton.Left));
        _s.PointerMove(At(300, 300));
        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));
        _s.PointerUp(At(300, 300, PointerButton.Left));
        Assert.Equal(200, g.Origin.Y, 9);
        Assert.False(_s.IsDraggingGuide);
    }

    [Fact]
    public void Escape_deselects_the_guide_and_delete_removes_it()
    {
        var g = AddGuide(Guide.Horizontal(200));
        _s.SelectedGuide = g;
        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));
        Assert.Null(_s.SelectedGuide);

        _s.SelectedGuide = g;
        _s.DeleteSelection();
        Assert.Empty(_s.Document.Guides);
        Assert.Null(_s.SelectedGuide);
        _s.Undo();
        Assert.Same(g, Assert.Single(_s.Document.Guides));
    }

    [Fact]
    public void Removing_the_selected_guide_or_undoing_its_creation_clears_the_selection()
    {
        var g = AddGuide(Guide.Horizontal(200));
        _s.SelectedGuide = g;
        _s.Undo();
        Assert.Null(_s.SelectedGuide);
    }

    [Fact]
    public void Remove_all_leaves_nothing_selected()
    {
        var g = AddGuide(Guide.Horizontal(200));
        AddGuide(Guide.Vertical(200));
        _s.SelectedGuide = g;
        _s.RemoveAllGuides();
        Assert.Empty(_s.Document.Guides);
        Assert.Null(_s.SelectedGuide);
    }

    // ---- Right button stays zoom (R1) ----

    [Fact]
    public void The_right_button_never_selects_or_moves_a_guide_it_zooms()
    {
        var g = AddGuide(Guide.Horizontal(200));
        var start = _s.Viewport.State;
        Drag(300, 200, 500, 320, PointerButton.Right);     // marquee zoom that starts exactly on the guide
        Assert.Null(_s.SelectedGuide);
        Assert.Equal(200, g.Origin.Y, 9);
        Assert.NotEqual(start, _s.Viewport.State);
        Assert.Equal(1, _s.Viewport.ZoomStackDepth);

        Click(300, 200, PointerButton.Right);                // click on the guide = zoom out
        Assert.Null(_s.SelectedGuide);
        Assert.Equal(0, _s.Viewport.ZoomStackDepth);
    }

    [Fact]
    public void A_right_press_during_a_guide_drag_is_ignored()
    {
        var g = AddGuide(Guide.Horizontal(200));
        _s.PointerDown(At(300, 200, PointerButton.Left));
        _s.PointerDown(At(300, 200, PointerButton.Right));
        _s.PointerMove(At(300, 260));
        _s.PointerUp(At(300, 260, PointerButton.Right));
        _s.PointerUp(At(300, 260, PointerButton.Left));
        Assert.Equal(260, g.Origin.Y, 9);
        Assert.Equal(0, _s.Viewport.ZoomStackDepth);
    }

    // ---- Interaction with tools and objects ----

    [Fact]
    public void Guides_are_only_grabbed_by_the_select_tool()
    {
        var g = AddGuide(Guide.Horizontal(200));
        _s.SetTool("rectangle");
        Drag(100, 190, 300, 260);
        Assert.Equal(200, g.Origin.Y);
        Assert.Single(_s.Document.ActiveLayer.Children);
    }

    [Fact]
    public void A_selected_object_under_a_guide_keeps_the_drag()
    {
        var r = AddRect(100, 100, 200, 200);      // guide through its middle
        AddGuide(Guide.Horizontal(200));
        _s.Selection.Set(r);
        Drag(200, 202, 260, 202);
        Assert.Equal(160, r.GetWorldBounds().Left, 9);
        Assert.Null(_s.SelectedGuide);
    }

    [Fact]
    public void Hidden_guides_cannot_be_picked_and_do_not_snap_or_draw()
    {
        var g = AddGuide(Guide.Vertical(500));
        _s.SelectedGuide = g;
        _s.ShowGuides = false;
        Assert.Null(_s.SelectedGuide);
        Assert.Empty(_s.BuildOverlay().OfType<GuideOverlay>());
        Click(500, 300);
        Assert.Null(_s.SelectedGuide);

        var r = AddRect(100, 100, 50, 50);
        Drag(120, 120, 377, 120);   // would put the right edge exactly on x=500 if snapping applied: 150 + 257 = 407 → no
        Assert.Equal(100 + 257, r.GetWorldBounds().Left, 9);
        _s.ShowGuides = true;
        Assert.Single(_s.BuildOverlay().OfType<GuideOverlay>());
    }

    // ---- Snapping in tools ----

    [Fact]
    public void Moving_an_object_snaps_its_edge_to_a_guide_within_a_few_pixels()
    {
        AddGuide(Guide.Vertical(500));
        var r = AddRect(100, 100, 50, 50);
        _s.Selection.Set(r);
        Drag(120, 120, 120 + 347, 120);     // right edge would land on 497: 3 px from the guide
        var b = r.GetWorldBounds();
        Assert.Equal(500, b.Right, 9);
        Assert.Equal(100, b.Top, 9);
    }

    [Fact]
    public void Moving_does_not_snap_beyond_the_tolerance_or_when_snapping_is_off()
    {
        AddGuide(Guide.Vertical(500));
        var r = AddRect(100, 100, 50, 50);
        _s.Selection.Set(r);
        Drag(120, 120, 120 + 340, 120);     // right edge at 490: 10 px away
        Assert.Equal(490, r.GetWorldBounds().Right, 9);

        _s.SnapToGuides = false;
        Drag(460, 120, 460 + 7, 120);       // would snap to 500 (right edge 497); with snapping off it stays at 497
        Assert.Equal(497, r.GetWorldBounds().Right, 9);
    }

    [Fact]
    public void The_center_of_a_moving_object_snaps_too()
    {
        AddGuide(Guide.Horizontal(400));
        var r = AddRect(100, 100, 100, 100);   // center y = 150
        _s.Selection.Set(r);
        Drag(150, 150, 150, 150 + 247);        // center would land on 397
        Assert.Equal(400, r.GetWorldBounds().Center.Y, 9);
    }

    [Fact]
    public void Shift_constrained_moves_do_not_snap_the_locked_axis()
    {
        AddGuide(Guide.Horizontal(103));    // 3 px below the top edge: would snap the y of an unconstrained move
        var r = AddRect(100, 100, 50, 50);
        _s.Selection.Set(r);
        Drag(120, 120, 200, 122, m: ModifierKeys.Shift);   // locked to x; y stays exactly where it was
        Assert.Equal(100, r.GetWorldBounds().Top, 9);
        Assert.Equal(180, r.GetWorldBounds().Left, 9);
    }

    [Fact]
    public void Snap_tolerance_is_in_screen_pixels_so_zooming_in_makes_it_finer()
    {
        AddGuide(Guide.Vertical(500));
        var r = AddRect(100, 100, 50, 50);
        _s.Selection.Set(r);
        _s.Viewport.Set(4, Vec2.Zero);   // 4 screen px per point: 5 px = 1.25 points
        Drag(120 * 4, 120 * 4, (120 + 347) * 4, 120 * 4);   // 3 points = 12 px off
        Assert.Equal(497, r.GetWorldBounds().Right, 9);
    }

    [Fact]
    public void Drawing_a_shape_snaps_its_corners_to_guides()
    {
        AddGuide(Guide.Vertical(200));
        AddGuide(Guide.Horizontal(300));
        _s.SetTool("rectangle");
        Drag(203, 100, 400, 297);
        var b = _s.Document.ActiveLayer.Children.Last().GetWorldBounds();
        Assert.Equal(new Rect2(200, 100, 400, 300), b);
    }

    [Fact]
    public void Scaling_with_a_handle_snaps_the_dragged_edge_to_a_guide()
    {
        var r = AddRect(100, 100, 100, 100);
        AddGuide(Guide.Vertical(400));
        _s.Selection.Set(r);
        // right-middle handle extends; release 3 px short of the guide
        Drag(200, 150, 397, 150);
        Assert.Equal(400, r.GetWorldBounds().Right, 6);
    }

    // ---- Settings ----

    [Fact]
    public void Guide_state_changes_are_announced()
    {
        int n = 0;
        _s.GuideStateChanged += (_, _) => n++;
        var g = AddGuide(Guide.Horizontal(10));
        _s.SelectedGuide = g;
        _s.SnapToGuides = false;
        _s.ShowGuides = false;
        Assert.True(n >= 3);
    }

    [Fact]
    public void Hover_over_a_guide_shows_a_resize_cursor_for_its_direction()
    {
        AddGuide(Guide.Horizontal(200));
        AddGuide(Guide.Vertical(500));
        _s.PointerMove(At(300, 201));
        Assert.Equal(CursorKind.SizeNS, _s.CurrentCursor);
        _s.PointerMove(At(499, 400));
        Assert.Equal(CursorKind.SizeWE, _s.CurrentCursor);
        _s.PointerMove(At(300, 400));
        Assert.Equal(CursorKind.Arrow, _s.CurrentCursor);
    }
}
