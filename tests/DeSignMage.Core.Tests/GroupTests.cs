using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Layered grouping, ungroup and combine (requirement R2).</summary>
public class GroupTests
{
    private readonly EditorSession _s;
    private Document Doc => _s.Document;
    private Layer L => _s.Document.ActiveLayer;

    /// <summary>The top-level objects (what a click selects) in drawing order: a group stands where its lowest member is drawn. A layer's own list holds every drawn object, grouped or not (R22).</summary>
    private IReadOnlyList<DocObject> Tops => L.Children.Select(o => o.TopLevel).Distinct().ToList();

    public GroupTests()
    {
        _s = new EditorSession(NewDoc());
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
    }

    private RectangleObject Add(double x, double y, string color = "#000000")
    {
        var r = Rect(x, y, 20, 20, ObjectStyle.FillOnly(DocColor.FromHex(color)));
        _s.History.Execute(new AddObjectCommand(L, r));
        return r;
    }

    private PointerInput At(double x, double y, PointerButton b = PointerButton.None, ModifierKeys m = ModifierKeys.None, int clicks = 1) =>
        new(new Vec2(x, y), new Vec2(x, y), b, m, clicks);

    private void Box(double x0, double y0, double x1, double y1, ModifierKeys m = ModifierKeys.None)
    {
        _s.PointerDown(At(x0, y0, PointerButton.Left, m));
        _s.PointerMove(At(x1, y1, PointerButton.None, m));
        _s.PointerUp(At(x1, y1, PointerButton.Left, m));
    }

    // ---- Commands ----

    [Fact]
    public void Group_ties_members_together_and_moves_nothing_in_the_drawing_order_and_undo_restores()
    {
        var a = Add(0, 0); var b = Add(30, 0); var c = Add(60, 0); var d = Add(90, 0);
        var boundsB = b.GetWorldBounds();
        var cmd = new GroupObjectsCommand(Doc, [d, b]);
        _s.History.Execute(cmd);

        Assert.Equal([a, b, c, d], L.Children);   // R22: c stays between b and d
        Assert.Equal([b, d], cmd.Group.Children);
        Assert.Same(cmd.Group, b.TopLevel);
        Assert.Same(c, c.TopLevel);
        Assert.Equal(boundsB, b.GetWorldBounds());   // members do not move
        Assert.True(Doc.Contains(b));
        Assert.True(Doc.Contains(cmd.Group));

        _s.History.Undo();
        Assert.Equal([a, b, c, d], L.Children);
        Assert.False(Doc.Contains(cmd.Group));
        Assert.Same(b, b.TopLevel);
        _s.History.Redo();
        Assert.Equal([a, b, c, d], L.Children);
        Assert.Same(cmd.Group, d.TopLevel);
    }

    [Fact]
    public void Ungroup_removes_exactly_one_level()
    {
        var c1 = Add(0, 0); var c2 = Add(30, 0); var c3 = Add(60, 0);
        var inner = _s.GroupObjects([c1, c2])!;
        var outer = _s.GroupObjects([inner, c3])!;
        Assert.Equal([outer], Tops);

        _s.Selection.Set(outer);
        _s.UngroupSelection();
        Assert.Equal([inner, c3], Tops);                 // {{c1,c2},c3} → {c1,c2} + c3
        Assert.Equal([c1, c2], inner.Children);
        Assert.Empty(_s.Selection.Items);                // ungroup deselects everything (R2)

        _s.Selection.Set(inner);                         // click {c1,c2} and press U again
        _s.UngroupSelection();
        Assert.Equal([c1, c2, c3], Tops);
        Assert.Equal([c1, c2, c3], L.Children);          // and none of it moved anything in the drawing order

        _s.Undo();
        Assert.Equal([inner, c3], Tops);
        _s.Undo();
        Assert.Equal([outer], Tops);
        Assert.Equal([inner, c3], outer.Children);
        Assert.Equal([c1, c2, c3], L.Children);
    }

    [Fact]
    public void Ungroup_keeps_positions_of_a_moved_group()
    {
        var a = Add(0, 0); var b = Add(30, 0);
        var g = _s.GroupObjects([a, b])!;
        _s.TransformSelection("Move", Affine2.Translation(100, 50));
        var before = b.GetWorldBounds();
        _s.UngroupSelection();
        Assert.Equal(before, b.GetWorldBounds());
        Assert.Same(L, b.Parent);
        _s.Undo();
        Assert.Same(g, b.Parent);
        Assert.Equal(before, b.GetWorldBounds());
    }

    [Fact]
    public void Undoing_an_ungroup_restores_the_group()
    {
        var a = Add(0, 0); var b = Add(30, 0);
        var g = _s.GroupObjects([a, b])!;
        _s.UngroupSelection();
        Assert.Empty(_s.Selection.Items);        // ungroup deselects everything (R2)
        _s.Undo();
        Assert.Equal([g], Tops);
        Assert.Equal([a, b], g.Children);
    }

    [Fact]
    public void Combine_makes_one_object_that_keeps_each_parts_style_and_order()
    {
        var red = Add(0, 0, "#FF0000"); var blue = Add(10, 10, "#0000FF");   // overlapping
        var g = _s.GroupObjects([red, blue])!;
        var bounds = g.GetWorldBounds();

        var combined = _s.CombineSelection()!;
        Assert.Equal([combined], L.Children);
        Assert.Equal(2, combined.Parts.Count);
        Assert.Equal(red.Style, combined.Parts[0].Style);
        Assert.Equal(blue.Style, combined.Parts[1].Style);   // stacking kept: blue on top
        Assert.Equal(bounds, combined.GetWorldBounds());
        Assert.Equal([combined], _s.Selection.Items);
        Assert.Equal("Combine", _s.History.UndoDescription);

        // Overlaps are knocked out (even-odd): the overlap is a gap, single-covered areas are solid.
        Assert.Same(combined, HitTesting.HitTest(Doc, new Vec2(5, 5), 1));
        Assert.Same(combined, HitTesting.HitTest(Doc, new Vec2(25, 25), 1));
        Assert.Null(HitTesting.HitTest(Doc, new Vec2(15, 15), 1));
        Assert.Equal(FillRule.EvenOdd, combined.GetLocalGeometry().FillRule);

        Assert.False(_s.CanUngroup);   // no longer a group: U does nothing
        _s.UngroupSelection();
        Assert.Equal([combined], L.Children);

        _s.Undo();
        Assert.Equal([g], Tops);
        Assert.Equal([red, blue], g.Children);
    }

    [Fact]
    public void Combine_flattens_nested_groups_in_stacking_order()
    {
        var c1 = Add(0, 0, "#110000"); var c2 = Add(30, 0, "#220000"); var c3 = Add(60, 0, "#330000");
        var inner = _s.GroupObjects([c1, c2])!;
        _s.GroupObjects([inner, c3]);
        var combined = _s.CombineSelection()!;
        Assert.Equal([c1.Style, c2.Style, c3.Style], combined.Parts.Select(p => p.Style));
    }

    [Fact]
    public void Combine_of_a_moved_group_keeps_world_positions()
    {
        var a = Add(0, 0); var b = Add(30, 0);
        _s.GroupObjects([a, b]);
        _s.TransformSelection("Move", Affine2.Translation(100, 50));
        var expected = _s.Selection.GetBounds();
        var combined = _s.CombineSelection()!;
        Assert.Equal(expected, combined.GetWorldBounds());
    }

    [Fact]
    public void Text_and_shapes_can_be_combined_and_come_back_unchanged()
    {
        var a = Add(0, 0);
        var t = new TextObject("A", "Arial", 20) { Transform = Affine2.Translation(40, 40), Name = "Letter" };
        _s.History.Execute(new AddObjectCommand(L, t));
        _s.Selection.Set([a, t]);
        Assert.True(_s.CanCombine);
        var combined = _s.CombineSelection()!;
        Assert.IsType<TextObject>(combined.Parts[1]);

        _s.BreakApartSelection();
        var back = Assert.IsType<TextObject>(L.Children[1]);
        Assert.Equal("A", back.Text);
        Assert.Equal("Letter", back.Name);
        Assert.Equal(t.GetWorldBounds(), back.GetWorldBounds());
    }
    [Fact]
    public void Recoloring_a_combined_object_recolors_all_parts_undoably()
    {
        var a = Add(0, 0, "#FF0000"); var b = Add(30, 0, "#0000FF");
        _s.GroupObjects([a, b]);
        var combined = _s.CombineSelection()!;
        var green = new SolidPaint(DocColor.FromHex("#00FF00"));
        _s.ApplyFill(green);
        Assert.All(combined.Parts, p => Assert.Equal(green, p.Style.Fill));
        _s.Undo();
        Assert.Equal(a.Style, combined.Parts[0].Style);
        Assert.Equal(b.Style, combined.Parts[1].Style);
    }

    [Fact]
    public void Combined_object_clones_and_moves_like_any_object()
    {
        var a = Add(0, 0); var b = Add(30, 0);
        _s.GroupObjects([a, b]);
        var combined = _s.CombineSelection()!;
        var copy = (CombinedObject)combined.Clone();
        Assert.Equal(combined.Parts.Select(p => p.Style), copy.Parts.Select(p => p.Style));
        Assert.All(copy.Parts, p => Assert.DoesNotContain(p, combined.Parts));   // deep copy
        _s.NudgeSelection(new Vec2(5, 0));
        Assert.Equal(5, combined.GetWorldBounds().Left, 9);
    }

    [Fact]
    public void Paw_print_combine_drops_group_history_and_K_breaks_into_solo_pieces()
    {
        // Five pieces, some of them in nested groups: {{pad, toe1}, toe2}, toe3, toe4
        var pad = Add(0, 0, "#F2B705"); var toe1 = Add(10, 30, "#F2B705"); var toe2 = Add(25, 35, "#F2B705");
        var toe3 = Add(40, 30, "#F2B705"); var toe4 = Add(50, 10, "#F2B705");
        var bounds = pad.GetWorldBounds();
        var inner = _s.GroupObjects([pad, toe1])!;
        _s.GroupObjects([inner, toe2]);
        _s.Selection.Add([toe3, toe4]);

        var combined = _s.CombineSelection()!;
        Assert.Equal([combined], L.Children);
        Assert.Equal(5, combined.Parts.Count);
        _s.UngroupSelection();                       // U: a combined object is not a group
        Assert.Equal([combined], L.Children);

        _s.BreakApartSelection();                    // K
        Assert.Equal(5, L.Children.Count);
        Assert.All(L.Children, o => Assert.IsType<RectangleObject>(o));   // solo pieces, no groups
        Assert.Equal(bounds, L.Children[0].GetWorldBounds());               // same place, same stacking order
        Assert.Empty(_s.Selection.Items);                                   // K deselects everything, like U
        Assert.Equal("Break Apart", _s.History.UndoDescription);

        _s.Undo();
        Assert.Equal([combined], L.Children);
        _s.Undo();                                   // undo Combine: the groups come back
        Assert.Equal(5, L.Children.Count);
        Assert.Equal(3, Tops.Count);
        Assert.Same(inner, ((GroupObject)Tops[0]).Children[0]);
    }

    [Fact]
    public void Break_apart_keeps_positions_after_the_combined_object_was_moved_and_recolored()
    {
        var a = Add(0, 0); var b = Add(30, 0);
        _s.Selection.Set([a, b]);
        var combined = _s.CombineSelection()!;
        _s.TransformSelection("Move", Affine2.Translation(100, 50));
        var green = new SolidPaint(DocColor.FromHex("#00FF00"));
        _s.ApplyFill(green);
        var expected = combined.GetWorldBounds();

        _s.BreakApartSelection();
        _s.Selection.Set(L.Children.ToList());                // K deselects; select the pieces to measure them
        Assert.Equal(expected, _s.Selection.GetBounds());
        Assert.All(L.Children, o => Assert.Equal(green, o.Style.Fill));
        Assert.Equal(130, L.Children[1].GetWorldBounds().Left, 9);
    }

    [Fact]
    public void K_on_a_nested_group_with_a_combined_object_inside_leaves_only_solo_objects_in_place_and_one_undo_restores_everything()
    {
        // { { {a, b}, combined(c, d) }, e } with the groups moved so their transforms are not identity.
        var a = Add(0, 0, "#FF0000"); var b = Add(30, 0, "#00FF00");
        var c = Add(60, 0, "#0000FF"); var d = Add(90, 0, "#FFFF00");
        var e = Add(120, 0, "#00FFFF");
        _s.Selection.Set([c, d]);
        var combined = _s.CombineSelection()!;
        var g1 = _s.GroupObjects([a, b])!;
        var g2 = _s.GroupObjects([g1, combined])!;
        var g3 = _s.GroupObjects([g2, e])!;
        _s.Selection.Set(g3);
        _s.TransformSelection("Move", Affine2.Translation(100, 50));          // the outer group gets a transform
        g1.Transform = Affine2.Translation(0, 7);                             // and so does an inner one (set directly: only top-level objects can be selected)
        var cw = combined.GetWorldTransform();   // c and d now live on as parts of the combined object
        Rect2 PartBounds(DocObject part) => part.GetLocalGeometry()!.Transform(part.Transform * cw).GetBounds();
        var boundsBefore =new[] { a.GetWorldBounds(), b.GetWorldBounds(), PartBounds(combined.Parts[0]), PartBounds(combined.Parts[1]), e.GetWorldBounds() };
        _s.Selection.Set(g3);
        var undoSteps = _s.History.UndoList.Count;

        Assert.True(_s.CanBreakApart);
        _s.BreakApartSelection();

        Assert.Equal(5, L.Children.Count);
        Assert.All(L.Children, o => Assert.IsType<RectangleObject>(o));       // no groups, no combined objects left
        Assert.Equal(boundsBefore, L.Children.Select(o => o.GetWorldBounds()).ToArray());   // same places, same stacking order
        Assert.Equal(["#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#00FFFF"], L.Children.Select(o => ((SolidPaint)o.Style.Fill!).Color.ToHex()));
        Assert.True(_s.Selection.IsEmpty);
        Assert.Equal(undoSteps + 1, _s.History.UndoList.Count);               // ONE step
        Assert.Equal("Break Apart", _s.History.UndoDescription);

        _s.Undo();
        Assert.Equal([g3], Tops);                                             // the exact original structure
        Assert.Equal([a, b, combined, e], L.Children);
        Assert.Equal([g2, e], g3.Children);
        Assert.Equal([g1, combined], g2.Children);
        Assert.Equal([a, b], g1.Children);
        Assert.Equal(boundsBefore, new[] { a.GetWorldBounds(), b.GetWorldBounds(), PartBounds(combined.Parts[0]), PartBounds(combined.Parts[1]), e.GetWorldBounds() });
        _s.Redo();
        Assert.Equal(5, L.Children.Count);
    }

    [Fact]
    public void K_on_a_group_does_not_split_multi_loop_shapes_inside_but_does_on_a_directly_selected_one()
    {
        var twoLoops = new PathObject(new PathBuilder()
            .MoveTo(0, 0).LineTo(20, 0).LineTo(20, 20).LineTo(0, 20).Close()
            .MoveTo(40, 0).LineTo(60, 0).LineTo(60, 20).LineTo(40, 20).Close().Build())
        { Style = ObjectStyle.FillOnly(DocColor.Black) };
        _s.History.Execute(new AddObjectCommand(L, twoLoops));
        var other = Add(100, 0);
        var group = _s.GroupObjects([twoLoops, other])!;

        _s.Selection.Set(group);
        _s.BreakApartSelection();
        Assert.Equal(2, L.Children.Count);                                    // the paw-like shape stayed one object
        var whole = Assert.IsType<PathObject>(L.Children[0]);
        Assert.Equal(2, PathEditing.ToLoops(whole.Geometry).Count);

        _s.Selection.Set(whole);                                              // selected directly: K splits the loops
        _s.BreakApartSelection();
        Assert.Equal(3, L.Children.Count);
    }

    [Fact]
    public void K_on_a_single_group_is_enabled_and_U_still_removes_only_one_level()
    {
        var a = Add(0, 0); var b = Add(30, 0); var c = Add(60, 0);
        var outer = _s.GroupObjects([_s.GroupObjects([a, b])!, c])!;
        _s.Selection.Set(outer);
        Assert.True(_s.CanBreakApart);
        _s.UngroupSelection();
        Assert.Equal(2, Tops.Count);                                          // {a, b} and c: U peeled one level only
        Assert.IsType<GroupObject>(Tops[0]);
    }

    [Fact]
    public void Break_apart_of_a_non_combined_object_does_nothing()
    {
        var a = Add(0, 0);
        _s.Selection.Set(a);
        Assert.False(_s.CanBreakApart);
        int steps = _s.History.UndoList.Count;
        _s.BreakApartSelection();
        Assert.Equal(steps, _s.History.UndoList.Count);
        Assert.Equal([a], L.Children);
    }

    // ---- Gestures ----

    [Fact]
    public void Rubber_band_over_two_objects_makes_a_persistent_group()
    {
        var a = Add(10, 10); var b = Add(50, 10);
        Box(-20, -20, 100, 100);
        var g = Assert.IsType<GroupObject>(Assert.Single(Tops));
        Assert.Equal([a, b], g.Children);
        Assert.Equal([g], _s.Selection.Items);
        Assert.Equal("Group", _s.History.UndoDescription);
    }

    [Fact]
    public void Clicking_a_member_selects_the_group_and_dragging_moves_all_members()
    {
        var a = Add(10, 10); var b = Add(50, 10);
        Box(-20, -20, 100, 100);
        _s.Selection.Clear();

        _s.PointerDown(At(15, 15, PointerButton.Left));
        Assert.IsType<GroupObject>(Assert.Single(_s.Selection.Items));
        _s.PointerMove(At(35, 25));
        _s.PointerUp(At(35, 25, PointerButton.Left));
        Assert.Equal(30, a.GetWorldBounds().Left, 9);
        Assert.Equal(70, b.GetWorldBounds().Left, 9);
    }

    [Fact]
    public void Rubber_band_over_a_group_and_another_object_nests()
    {
        var c1 = Add(10, 10); var c2 = Add(50, 10); var c3 = Add(150, 10);
        Box(-20, -20, 100, 100);
        var inner = (GroupObject)Tops[0];
        Box(-20, -20, 200, 100);
        var outer = Assert.IsType<GroupObject>(Assert.Single(Tops));
        Assert.Equal([inner, c3], outer.Children);
        Assert.Equal([c1, c2], inner.Children);
    }

    [Fact]
    public void Release_point_counts_even_without_a_move_event_there()
    {
        Add(10, 10); Add(50, 10);
        _s.PointerDown(At(-20, -20, PointerButton.Left));
        _s.PointerMove(At(20, 20));                     // box still too small to catch both
        _s.PointerUp(At(100, 100, PointerButton.Left)); // released far away
        Assert.IsType<GroupObject>(Assert.Single(Tops));
    }

    [Fact]
    public void Rubber_band_catching_one_item_just_selects_it()
    {
        var a = Add(10, 10);
        Add(500, 500);
        Box(-20, -20, 100, 100);
        Assert.Equal([a], _s.Selection.Items);
        Assert.Equal(2, L.Children.Count);
        Assert.IsNotType<GroupObject>(L.Children[0]);
    }

    [Fact]
    public void Without_auto_grouping_the_rubber_band_only_selects_unless_ctrl()
    {
        _s.AutoGrouping = false;
        var a = Add(10, 10); var b = Add(50, 10);
        Box(-20, -20, 100, 100);
        Assert.Equal([a, b], _s.Selection.Items);
        Assert.Equal(2, L.Children.Count);

        _s.Selection.Clear();
        Box(-20, -20, 100, 100, ModifierKeys.Control);   // Ctrl = permanent box
        Assert.IsType<GroupObject>(Assert.Single(Tops));
    }

    private void Click(double x, double y, ModifierKeys m = ModifierKeys.None, int clicks = 1)
    {
        _s.PointerDown(At(x, y, PointerButton.Left, m, clicks));
        _s.PointerUp(At(x, y, PointerButton.Left, m, clicks));
    }

    [Fact]
    public void Shift_click_groups_the_clicked_object_with_the_selection_and_keeps_wrapping()
    {
        var a = Add(0, 0); var b = Add(50, 0); var c = Add(100, 0);
        Click(10, 10);                                        // select a
        int steps = _s.History.UndoList.Count;

        Click(60, 10, ModifierKeys.Shift);                    // + b → {a, b}
        var ab = Assert.Single(Doc.Groups);
        Assert.Equal([a, b], ab.Children);
        Assert.Equal([ab, c], Tops);
        Assert.Equal([ab], _s.Selection.Items);               // the new group is the selection
        Assert.Equal(steps + 1, _s.History.UndoList.Count);

        Click(110, 10, ModifierKeys.Shift);                   // + c → { {a, b}, c }
        var outer = Assert.IsType<GroupObject>(Assert.Single(Tops));
        Assert.Equal([ab, c], outer.Children);
        Assert.Equal([a, b], ab.Children);
        Assert.Equal([outer], _s.Selection.Items);
        Assert.Equal(steps + 2, _s.History.UndoList.Count);

        _s.Undo();                                            // one step per Shift+click
        Assert.Equal([ab, c], Tops);
        _s.Undo();
        Assert.Equal([a, b, c], Tops);
        Assert.Equal([a, b, c], L.Children);
    }

    [Fact]
    public void Shift_click_on_a_member_of_another_group_wraps_that_whole_group()
    {
        var a = Add(0, 0); var b = Add(50, 0); var c = Add(100, 0); var d = Add(150, 0);
        var cd = _s.GroupObjects([c, d])!;
        Click(10, 10);
        Click(160, 10, ModifierKeys.Shift);                   // d is a member of {c, d}
        var outer = Assert.IsType<GroupObject>(_s.Selection.Items[0]);
        Assert.Equal([a, cd], outer.Children);
        Assert.Equal([c, d], cd.Children);
        Assert.Equal([a, b, c, d], L.Children);               // nothing was moved to bring the group together (R22): b stays between a and c
        Assert.Equal([outer, b], Tops);
    }

    [Fact]
    public void Shift_clicking_something_already_selected_does_nothing()
    {
        var a = Add(0, 0); var b = Add(50, 0);
        Box(-20, -20, 100, 40);                               // {a, b}, selected
        var g = Assert.IsType<GroupObject>(_s.Selection.Items[0]);
        int steps = _s.History.UndoList.Count;
        Click(10, 10, ModifierKeys.Shift);
        Assert.Equal([g], _s.Selection.Items);
        Assert.Equal(steps, _s.History.UndoList.Count);
        Assert.Equal([a, b], g.Children);
    }

    [Fact]
    public void Shift_click_on_empty_canvas_deselects_but_a_Shift_drag_still_adds_with_the_rubber_band()
    {
        var a = Add(0, 0); var b = Add(200, 0);
        Click(10, 10);
        Click(500, 500, ModifierKeys.Shift);
        Assert.True(_s.Selection.IsEmpty);
        Assert.Equal(2, L.Children.Count);                    // nothing was grouped

        Click(10, 10);
        Box(190, -25, 230, 30, ModifierKeys.Shift);           // Shift+drag on empty canvas adds b without grouping (starts clear of the page's top border)
        Assert.Equal([a, b], _s.Selection.Items);
        Assert.Equal([a, b], L.Children);
    }

    [Fact]
    public void Shift_drag_on_an_unselected_object_moves_it_with_the_selection_and_does_not_group()
    {
        var a = Add(0, 0); var b = Add(100, 0);
        Click(10, 10);
        _s.PointerDown(At(110, 10, PointerButton.Left, ModifierKeys.Shift));
        _s.PointerMove(At(130, 12, PointerButton.None, ModifierKeys.Shift));
        _s.PointerMove(At(160, 12, PointerButton.None, ModifierKeys.Shift));
        _s.PointerUp(At(160, 12, PointerButton.Left, ModifierKeys.Shift));
        Assert.Equal([a, b], L.Children);                     // still two solo objects
        Assert.Equal([a, b], _s.Selection.Items);
        Assert.Equal(50, a.GetWorldBounds().Left, 9);         // Shift constrains to the dominant axis (x)
        Assert.Equal(0, a.GetWorldBounds().Top, 9);
    }

    [Fact]
    public void Quick_Shift_clicks_that_count_as_double_clicks_still_group()
    {
        var a = Add(0, 0); var b = Add(50, 0); var c = Add(100, 0);
        Click(10, 10);
        Click(60, 10, ModifierKeys.Shift, clicks: 2);
        Click(110, 10, ModifierKeys.Shift, clicks: 3);
        var outer = Assert.IsType<GroupObject>(Assert.Single(Tops));
        Assert.Equal(2, outer.Children.Count);
    }

    [Fact]
    public void Shift_rubber_band_adds_to_selection_without_grouping()
    {
        var a = Add(10, 10); var b = Add(50, 10); var c = Add(300, 10);
        _s.Selection.Set(c);
        Box(-20, -20, 100, 100, ModifierKeys.Shift);
        Assert.Equal([c, a, b], _s.Selection.Items);
        Assert.Equal(3, L.Children.Count);
    }

    [Fact]
    public void Undo_of_rubber_band_group_restores_separate_objects()
    {
        var a = Add(10, 10); var b = Add(50, 10);
        Box(-20, -20, 100, 100);
        _s.Undo();
        Assert.Equal([a, b], L.Children);
        Assert.True(_s.Selection.IsEmpty);
    }

    [Fact]
    public void Left_double_click_on_a_group_requests_its_context_menu()
    {
        var a = Add(10, 10); var b = Add(40, 10);
        var g = _s.GroupObjects([a, b])!;
        ContextMenuRequest? req = null;
        _s.ContextMenuRequested += (_, r) => req = r;
        _s.PointerDown(At(15, 15, PointerButton.Left));
        _s.PointerUp(At(15, 15, PointerButton.Left));
        _s.PointerDown(At(15, 15, PointerButton.Left, clicks: 2));
        _s.PointerUp(At(15, 15, PointerButton.Left, clicks: 2));
        Assert.NotNull(req);
        Assert.Same(g, req!.Target);
        Assert.Equal(MenuKind.Object, req.Kind);
        Assert.Equal([g], _s.Selection.Items);
        Assert.Equal(new Rect2(10, 10, 30, 30), a.GetWorldBounds());   // nothing moved
        Assert.Equal("select", _s.ActiveTool.Id);
    }

    [Fact]
    public void Left_double_click_on_a_single_object_opens_the_vector_tool_instead()
    {
        var a = Add(10, 10);
        ContextMenuRequest? req = null;
        _s.ContextMenuRequested += (_, r) => req = r;
        _s.PointerDown(At(15, 15, PointerButton.Left));
        _s.PointerUp(At(15, 15, PointerButton.Left));
        _s.PointerDown(At(15, 15, PointerButton.Left, clicks: 2));
        _s.PointerUp(At(15, 15, PointerButton.Left, clicks: 2));
        Assert.Null(req);
        Assert.Equal("vector", _s.ActiveTool.Id);
        Assert.Same(a, _s.GetTool<DeSignMage.Core.Editing.Tools.VectorEditTool>().Target);
    }

    [Fact]
    public void Double_click_on_empty_canvas_requests_nothing()
    {
        bool asked = false;
        _s.ContextMenuRequested += (_, _) => asked = true;
        _s.PointerDown(At(500, 500, PointerButton.Left, clicks: 2));
        _s.PointerUp(At(500, 500, PointerButton.Left, clicks: 2));
        Assert.False(asked);
    }

    [Fact]
    public void Copy_paste_and_cut_through_the_in_app_clipboard()
    {
        var a = Add(10, 10);
        _s.Selection.Set(a);
        _s.Copy();
        _s.Paste();
        Assert.Single(L.Children);                       // a paste hangs on the cursor first; nothing is added yet
        var center = a.GetWorldBounds().Center;
        Click(center.X, center.Y);                       // the click puts it down (here: exactly over the original)
        Assert.Equal(2, L.Children.Count);
        var pasted = Assert.Single(_s.Selection.Items);
        Assert.NotSame(a, pasted);
        Assert.Equal(a.GetWorldBounds(), pasted.GetWorldBounds());

        _s.Cut();
        Assert.Single(L.Children);
        _s.Paste();
        Click(center.X, center.Y);
        Assert.Equal(2, L.Children.Count);
        _s.Undo();
        Assert.Single(L.Children);
    }
}
