using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Arrange ▸ Align, Mirror and Locked / Unlock All, driven through the session like the window does.</summary>
public class ArrangeTests
{
    private readonly EditorSession _s;

    public ArrangeTests()
    {
        _s = new EditorSession(NewDoc());   // a 1728 x 864 page
        _s.Viewport.SetViewSize(new Vec2(2000, 1200));
        _s.Viewport.Set(1, Vec2.Zero);       // screen == document coordinates
    }

    private PointerInput At(double x, double y, PointerButton b = PointerButton.None, ModifierKeys m = ModifierKeys.None) =>
        new(new Vec2(x, y), _s.Viewport.ToDoc(new Vec2(x, y)), b, m);

    private void Click(double x, double y, ModifierKeys m = ModifierKeys.None)
    {
        _s.PointerDown(At(x, y, PointerButton.Left, m));
        _s.PointerUp(At(x, y, PointerButton.Left, m));
    }

    private void Drag(double x0, double y0, double x1, double y1)
    {
        _s.PointerDown(At(x0, y0, PointerButton.Left));
        _s.PointerMove(At((x0 + x1) / 2, (y0 + y1) / 2));
        _s.PointerMove(At(x1, y1));
        _s.PointerUp(At(x1, y1, PointerButton.Left));
    }

    private DocObject AddRect(double x, double y, double w, double h)
    {
        var r = Rect(x, y, w, h);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, r));
        return r;
    }

    private static void AssertBounds(Rect2 expected, Rect2 actual)
    {
        Assert.Equal(expected.Left, actual.Left, 6);
        Assert.Equal(expected.Top, actual.Top, 6);
        Assert.Equal(expected.Right, actual.Right, 6);
        Assert.Equal(expected.Bottom, actual.Bottom, 6);
    }

    // ---- Align ----

    [Fact]
    public void Align_left_puts_every_selected_object_on_the_leftmost_edge_in_one_undo_step()
    {
        var a = AddRect(100, 100, 50, 40); var b = AddRect(220, 200, 80, 40); var c = AddRect(400, 300, 30, 40);
        _s.Selection.Set([a, b, c]);
        int steps = _s.History.UndoList.Count;

        Assert.True(_s.CanAlign(AlignMode.Left));
        Assert.True(_s.Align(AlignMode.Left));

        Assert.Equal(100, a.GetWorldBounds().Left, 9);
        Assert.Equal(100, b.GetWorldBounds().Left, 9);
        Assert.Equal(100, c.GetWorldBounds().Left, 9);
        Assert.Equal(200, b.GetWorldBounds().Top, 9);   // only horizontal movement
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Align Left", _s.History.UndoDescription);

        _s.Undo();
        AssertBounds(new Rect2(220, 200, 300, 240), b.GetWorldBounds());
        AssertBounds(new Rect2(400, 300, 430, 340), c.GetWorldBounds());
        _s.Redo();
        Assert.Equal(100, c.GetWorldBounds().Left, 9);
    }

    [Fact]
    public void Each_alignment_uses_the_matching_edge_or_center_of_the_selection_bounds()
    {
        var a = AddRect(100, 100, 50, 40); var b = AddRect(220, 200, 80, 40); var c = AddRect(400, 300, 30, 60);
        _s.Selection.Set([a, b, c]);                    // the selection spans x 100..430, y 100..360

        _s.Align(AlignMode.Right);
        Assert.All(new[] { a, b, c }, o => Assert.Equal(430, o.GetWorldBounds().Right, 9));
        _s.Undo();

        _s.Align(AlignMode.Center);
        Assert.All(new[] { a, b, c }, o => Assert.Equal(265, o.GetWorldBounds().Center.X, 9));
        _s.Undo();

        _s.Align(AlignMode.Top);
        Assert.All(new[] { a, b, c }, o => Assert.Equal(100, o.GetWorldBounds().Top, 9));
        _s.Undo();

        _s.Align(AlignMode.Middle);
        Assert.All(new[] { a, b, c }, o => Assert.Equal(230, o.GetWorldBounds().Center.Y, 9));
        _s.Undo();

        _s.Align(AlignMode.Bottom);
        Assert.All(new[] { a, b, c }, o => Assert.Equal(360, o.GetWorldBounds().Bottom, 9));
        Assert.Equal("Align Bottom", _s.History.UndoDescription);
    }

    [Fact]
    public void A_group_in_a_selection_aligns_as_one_unit()
    {
        var loose = AddRect(100, 400, 50, 50);
        var m1 = AddRect(300, 100, 40, 40); var m2 = AddRect(380, 160, 40, 40);
        var group = _s.GroupObjects([m1, m2])!;
        _s.Selection.Set([loose, group]);

        _s.Align(AlignMode.Left);

        Assert.Equal(100, group.GetWorldBounds().Left, 9);
        AssertBounds(new Rect2(100, 100, 140, 140), m1.GetWorldBounds());   // the members moved together...
        AssertBounds(new Rect2(180, 160, 220, 200), m2.GetWorldBounds());   // ...and kept their distance from each other
        AssertBounds(new Rect2(100, 400, 150, 450), loose.GetWorldBounds()); // the loose object was already on the left edge
    }

    [Fact]
    public void One_selected_group_aligns_its_members_with_each_other()
    {
        // A rubber band makes a group, so aligning "the selection" must line up what is inside the group.
        var a = AddRect(100, 100, 50, 50); var b = AddRect(200, 160, 50, 30); var c = AddRect(300, 120, 50, 90);
        var group = _s.GroupObjects([a, b, c])!;
        Assert.Equal([group], _s.Selection.Items);
        var groupBounds = group.GetWorldBounds();

        Assert.True(_s.CanAlign(AlignMode.Bottom));
        _s.Align(AlignMode.Bottom);

        Assert.All(new[] { a, b, c }, o => Assert.Equal(210, o.GetWorldBounds().Bottom, 9));
        AssertBounds(new Rect2(100, 120, 350, 210), group.GetWorldBounds());   // the group's left, right and bottom did not move
        Assert.Equal(groupBounds.Left, group.GetWorldBounds().Left, 9);
        Assert.Equal(groupBounds.Right, group.GetWorldBounds().Right, 9);
        Assert.Single(_s.History.UndoList.Skip(4));                             // three adds, the group, and ONE align
    }

    [Fact]
    public void There_is_nothing_to_align_for_one_plain_object_nothing_selected_or_already_aligned_objects()
    {
        var a = AddRect(100, 100, 50, 50); var b = AddRect(100, 200, 80, 50);
        _s.Selection.Set(a);
        foreach (var mode in Enum.GetValues<AlignMode>()) Assert.False(_s.CanAlign(mode));
        Assert.False(_s.Align(AlignMode.Left));
        _s.Selection.Clear();
        Assert.False(_s.CanAlign(AlignMode.Left));

        _s.Selection.Set([a, b]);
        int steps = _s.History.UndoList.Count;
        Assert.False(_s.Align(AlignMode.Left));            // already on one left edge: no empty undo step
        Assert.Equal(steps, _s.History.UndoList.Count);
    }

    [Fact]
    public void A_single_text_takes_Align_Left_Center_Right_as_its_alignment_and_stays_where_it_is()
    {
        var t = new TextObject("Hello", "Arial", 100) { Transform = Affine2.Translation(300, 400) };
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, t));
        _s.Selection.Set(t);
        var before = t.GetWorldBounds();
        Assert.Equal(TextAlignment.Left, t.Alignment);
        int steps = _s.History.UndoList.Count;

        Assert.False(_s.CanAlign(AlignMode.Left));        // it is left aligned already
        Assert.False(_s.CanAlign(AlignMode.Top));         // and the vertical modes have nothing to line up with
        Assert.True(_s.CanAlign(AlignMode.Right));
        Assert.True(_s.Align(AlignMode.Right));

        Assert.Equal(TextAlignment.Right, t.Alignment);
        AssertBounds(before, t.GetWorldBounds());          // the text did not jump when its anchor changed
        Assert.Equal(steps + 1, _s.History.UndoList.Count);

        _s.Undo();
        Assert.Equal(TextAlignment.Left, t.Alignment);
        AssertBounds(before, t.GetWorldBounds());
        _s.Redo();
        Assert.Equal(TextAlignment.Right, t.Alignment);
        Assert.True(_s.Align(AlignMode.Center));
        Assert.Equal(TextAlignment.Center, t.Alignment);
        AssertBounds(before, t.GetWorldBounds());
    }

    [Fact]
    public void Several_texts_line_up_like_any_other_objects()
    {
        var t1 = new TextObject("Hello", "Arial", 100) { Transform = Affine2.Translation(300, 400) };
        var t2 = new TextObject("A longer line", "Arial", 100) { Transform = Affine2.Translation(500, 600) };
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, t1));
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, t2));
        _s.Selection.Set([t1, t2]);
        double left = Math.Min(t1.GetWorldBounds().Left, t2.GetWorldBounds().Left);
        _s.Align(AlignMode.Left);
        Assert.Equal(left, t1.GetWorldBounds().Left, 6);
        Assert.Equal(left, t2.GetWorldBounds().Left, 6);
        Assert.Equal(TextAlignment.Left, t2.Alignment);   // the texts themselves were moved, not re-aligned
    }

    [Fact]
    public void Center_of_layout_moves_the_selection_as_one_unit_to_the_page_center()
    {
        var a = AddRect(100, 100, 50, 40); var b = AddRect(300, 200, 50, 40);   // together: x 100..350, y 100..240
        _s.Selection.Set([a, b]);
        var page = _s.Document.Page.Bounds.Center;                                 // (864, 432)

        Assert.True(_s.CenterOnPage(PageCentering.Horizontal));
        Assert.Equal(page.X, _s.Selection.GetBounds().Center.X, 9);
        Assert.Equal(170, _s.Selection.GetBounds().Center.Y, 9);                    // vertical position untouched
        Assert.Equal(200, b.GetWorldBounds().Left - a.GetWorldBounds().Left, 9);    // they moved together
        Assert.Equal("Center Horizontally", _s.History.UndoDescription);
        _s.Undo();
        AssertBounds(new Rect2(100, 100, 350, 240), _s.Selection.GetBounds());

        Assert.True(_s.CenterOnPage(PageCentering.Vertical));
        Assert.Equal(page.Y, _s.Selection.GetBounds().Center.Y, 9);
        Assert.Equal(225, _s.Selection.GetBounds().Center.X, 9);
        _s.Undo();

        Assert.True(_s.CenterOnPage(PageCentering.Both));
        Assert.Equal(page.X, _s.Selection.GetBounds().Center.X, 9);
        Assert.Equal(page.Y, _s.Selection.GetBounds().Center.Y, 9);
        Assert.False(_s.CenterOnPage(PageCentering.Both));                          // already there: no empty step
    }

    [Fact]
    public void Center_of_layout_follows_a_resized_page_and_works_on_one_object_or_group()
    {
        var a = AddRect(10, 10, 100, 40);
        _s.Selection.Set(a);
        _s.History.Execute(new ResizePageCommand(new PageSetup(400, 300, 50, 20)));   // the page is now (50,20)-(450,320)
        _s.CenterOnPage(PageCentering.Both);
        AssertBounds(new Rect2(200, 150, 300, 190), a.GetWorldBounds());
    }

    // ---- Mirror ----

    [Fact]
    public void Horizontal_mirror_flips_the_selection_left_to_right_about_its_center_as_one_unit()
    {
        var a = AddRect(100, 100, 40, 40); var b = AddRect(300, 200, 60, 40);   // together x 100..360, y 100..240
        _s.Selection.Set([a, b]);
        var bounds = _s.Selection.GetBounds();

        Assert.True(_s.Mirror(horizontal: true));

        AssertBounds(bounds, _s.Selection.GetBounds());                          // the whole selection stays in place
        AssertBounds(new Rect2(320, 100, 360, 140), a.GetWorldBounds());         // a was far left, now far right
        AssertBounds(new Rect2(100, 200, 160, 240), b.GetWorldBounds());
        Assert.Equal("Mirror Horizontal", _s.History.UndoDescription);

        _s.Undo();
        AssertBounds(new Rect2(100, 100, 140, 140), a.GetWorldBounds());
        _s.Redo();
        _s.Mirror(horizontal: true);                                             // mirrored twice = as it was
        AssertBounds(new Rect2(100, 100, 140, 140), a.GetWorldBounds());
    }

    [Fact]
    public void Vertical_mirror_flips_top_to_bottom()
    {
        var a = AddRect(100, 100, 40, 40); var b = AddRect(300, 200, 60, 40);
        _s.Selection.Set([a, b]);
        _s.Mirror(horizontal: false);
        AssertBounds(new Rect2(100, 200, 140, 240), a.GetWorldBounds());
        AssertBounds(new Rect2(300, 100, 360, 140), b.GetWorldBounds());
        Assert.Equal("Mirror Vertical", _s.History.UndoDescription);
    }

    [Fact]
    public void A_mirrored_shape_is_really_mirrored_not_just_moved()
    {
        var r = AddRect(100, 100, 40, 20);
        _s.Selection.Set(r);
        // the local top-left corner is at world (100,100) before; after a horizontal mirror about x = 120 it is the top-RIGHT corner
        _s.Mirror(horizontal: true);
        var w = r.GetWorldTransform();
        Assert.Equal(new Vec2(140, 100), w.Transform(new Vec2(0, 0)));
        Assert.True(w.TransformVector(new Vec2(1, 0)).X < 0);
    }

    [Fact]
    public void Mirroring_a_group_mirrors_its_members_and_live_text_stays_live_text()
    {
        var t = new TextObject("Hello", "Arial", 100) { Transform = Affine2.Translation(300, 400) };
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, t));
        var r = AddRect(100, 400, 50, 50);
        var group = _s.GroupObjects([t, r])!;
        var before = group.GetWorldBounds();
        var textBefore = t.GetWorldBounds();

        _s.Mirror(horizontal: true);

        AssertBounds(before, group.GetWorldBounds());
        Assert.IsType<TextObject>(Assert.Single(_s.Document.AllObjects(), o => o is TextObject));   // still live text
        Assert.Equal("Hello", t.Text);
        Assert.True(t.GetWorldTransform().TransformVector(new Vec2(1, 0)).X < 0);                  // its letters now run right to left
        Assert.Equal(before.Left + before.Right - textBefore.Right, t.GetWorldBounds().Left, 6);
        _s.Undo();
        AssertBounds(textBefore, t.GetWorldBounds());
    }

    [Fact]
    public void Nothing_to_mirror_without_a_selection()
    {
        Assert.False(_s.CanMirror);
        Assert.False(_s.Mirror(horizontal: true));
    }

    // ---- Lock ----

    [Fact]
    public void A_locked_object_cannot_be_clicked_selected_or_caught_by_a_rubber_band()
    {
        var r = AddRect(100, 100, 100, 100);
        var other = AddRect(400, 100, 100, 100);
        _s.Selection.Set(r);
        Assert.True(_s.ToggleLockSelection());
        Assert.True(r.Locked);
        Assert.Equal("Lock", _s.History.UndoDescription);

        _s.Selection.Clear();
        Click(150, 150);
        Assert.True(_s.Selection.IsEmpty);                       // the click goes through it
        _s.PointerMove(At(150, 150));
        Assert.Equal(CursorKind.Arrow, _s.CurrentCursor);        // and nothing says "you can pick this"

        Drag(50, 50, 250, 250);                                  // a rubber band around it
        Assert.True(_s.Selection.IsEmpty);
        Drag(50, 50, 600, 250);                                  // and one around it and the other object: only the other
        Assert.Equal([other], _s.Selection.Items);

        _s.Selection.Clear();
        Click(450, 150);                                         // Shift+click does not add it either
        Assert.Equal([other], _s.Selection.Items);
        Click(150, 150, ModifierKeys.Shift);
        Assert.DoesNotContain(r, _s.Selection.Items);

        _s.SelectAll();
        Assert.Equal([other], _s.Selection.Items);
    }

    [Fact]
    public void Dragging_where_a_locked_object_is_does_not_move_it()
    {
        var r = AddRect(100, 100, 100, 100);
        _s.Selection.Set(r);
        _s.ToggleLockSelection();
        _s.Selection.Clear();
        Drag(150, 150, 250, 250);                                // starts a rubber band, since nothing is under the pointer
        AssertBounds(new Rect2(100, 100, 200, 200), r.GetWorldBounds());
    }

    [Fact]
    public void A_locked_object_chosen_in_the_Layers_Panel_cannot_be_moved_resized_nudged_or_deleted()
    {
        var r = AddRect(100, 100, 100, 100);
        _s.Selection.Set(r);
        _s.ToggleLockSelection();
        _s.Selection.Set(r);                                     // what a click on its row in the Layers Panel does
        Assert.True(_s.SelectionLocked);
        int steps = _s.History.UndoList.Count;

        _s.NudgeSelection(new Vec2(20, 0));
        _s.TransformSelection("Move", Affine2.Translation(30, 30));
        _s.SetSelectionBounds(new Rect2(0, 0, 500, 500));
        _s.KeyDown(EditorKey.Right, ModifierKeys.None);
        _s.DeleteSelection();
        _s.Cut();
        Drag(200, 150, 300, 150);                                // the right-middle handle would extend a normal selection
        Drag(150, 100, 250, 100);
        _s.Selection.Set(r);                                     // (those presses started rubber bands, which deselect)

        AssertBounds(new Rect2(100, 100, 200, 200), r.GetWorldBounds());
        Assert.Contains(r, _s.Document.ActiveLayer.Children);
        Assert.Equal(steps, _s.History.UndoList.Count);          // none of it left a history step
        Assert.False(_s.CanAlign(AlignMode.Left));
        Assert.False(_s.CanMirror);
        Assert.False(_s.CanCenterOnPage);
        Assert.False(_s.CanGroup);
        var overlay = _s.BuildOverlay().OfType<SelectionOverlay>().Single();
        Assert.False(overlay.Handles);                           // no handles to grab

        // and it can be unlocked again from there
        Assert.True(_s.CanLock);
        Assert.True(_s.SelectionAllLocked);
        Assert.True(_s.ToggleLockSelection());
        Assert.False(r.Locked);
        Assert.Equal("Unlock", _s.History.UndoDescription);
        _s.NudgeSelection(new Vec2(20, 0));
        Assert.Equal(120, r.GetWorldBounds().Left, 9);
    }

    [Fact]
    public void Locking_a_group_locks_all_its_members_and_one_undo_unlocks()
    {
        var a = AddRect(100, 100, 50, 50); var b = AddRect(200, 100, 50, 50);
        var group = _s.GroupObjects([a, b])!;
        _s.ToggleLockSelection();
        Assert.True(group.Locked);
        _s.Selection.Clear();
        Click(125, 125);                                         // a member of a locked group can't be picked either
        Assert.True(_s.Selection.IsEmpty);
        Drag(50, 50, 300, 200);
        Assert.True(_s.Selection.IsEmpty);

        _s.Undo();
        Assert.False(group.Locked);
        Click(125, 125);
        Assert.Equal([group], _s.Selection.Items);
    }

    [Fact]
    public void Lock_toggles_and_a_mixed_selection_is_locked_first()
    {
        var a = AddRect(100, 100, 50, 50); var b = AddRect(200, 100, 50, 50);
        a.Locked = true;
        _s.Selection.Set([a, b]);
        Assert.False(_s.SelectionAllLocked);
        _s.ToggleLockSelection();                                // some unlocked: lock them all
        Assert.True(a.Locked && b.Locked);
        Assert.True(_s.SelectionAllLocked);
        _s.ToggleLockSelection();                                // all locked: unlock them all
        Assert.False(a.Locked || b.Locked);
        _s.Undo();
        Assert.True(a.Locked && b.Locked);
        _s.Undo();
        Assert.True(a.Locked);                                   // each object got back exactly its own old state
        Assert.False(b.Locked);
    }

    [Fact]
    public void Unlock_All_unlocks_everything_in_the_document_in_one_step()
    {
        var a = AddRect(100, 100, 50, 50); var b = AddRect(200, 100, 50, 50); var c = AddRect(300, 100, 50, 50);
        var group = _s.GroupObjects([b, c])!;
        a.Locked = true; group.Locked = true;
        Assert.True(_s.HasLockedObjects);
        int steps = _s.History.UndoList.Count;

        Assert.True(_s.UnlockAll());

        Assert.False(a.Locked || group.Locked);
        Assert.False(_s.HasLockedObjects);
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Unlock All", _s.History.UndoDescription);
        Assert.False(_s.UnlockAll());                            // nothing left to do
        _s.Undo();
        Assert.True(a.Locked && group.Locked);
    }

    [Fact]
    public void Locking_changes_nothing_about_the_pictures_geometry_and_is_a_normal_history_step()
    {
        var r = AddRect(100, 100, 50, 50);
        _s.History.MarkSaved();
        _s.Selection.Set(r);
        _s.ToggleLockSelection();
        Assert.True(_s.History.IsModified);                      // the title shows the star: the lock is part of the file
        AssertBounds(new Rect2(100, 100, 150, 150), r.GetWorldBounds());
    }

    [Fact]
    public void An_object_stays_selectable_by_the_other_tools_rules_after_unlocking()
    {
        var r = AddRect(100, 100, 100, 100);
        r.Locked = true;
        _s.UnlockAll();
        Click(150, 150);
        Assert.Equal([r], _s.Selection.Items);
        Drag(150, 150, 250, 170);
        AssertBounds(new Rect2(200, 120, 300, 220), r.GetWorldBounds());
    }
}
