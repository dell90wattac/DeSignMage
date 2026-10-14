using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Geometry;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>
/// Requirement R22: grouping never changes the stacking order. A group is membership only; every object keeps its own place in the drawing
/// order. The running example is a box at the back, an ungrouped outline over it, and a text in front of the outline, with the box and the
/// text grouped.
/// </summary>
public class GroupStackingTests
{
    private readonly EditorSession _s;
    private Document Doc => _s.Document;
    private Layer L => _s.Document.ActiveLayer;

    private readonly RectangleObject _box, _outline, _text;

    public GroupStackingTests()
    {
        _s = new EditorSession(NewDoc()) { BooleanService = GeometryEngines.Booleans, OffsetService = GeometryEngines.Offsets };
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
        _box = Add(0, 0, 100, 100, "#CC0000");           // back
        _outline = Add(30, 30, 40, 40, "#FFFFFF");       // over the box, under the text
        _text = Add(40, 40, 20, 20, "#CC0000");          // front (a rectangle stands in for the text)
    }

    private RectangleObject Add(double x, double y, double w, double h, string color)
    {
        var r = Rect(x, y, w, h, ObjectStyle.FillOnly(DocColor.FromHex(color)));
        _s.History.Execute(new AddObjectCommand(L, r));
        return r;
    }

    private IReadOnlyList<DocObject> Tops => L.Children.Select(o => o.TopLevel).Distinct().ToList();

    private PointerInput At(double x, double y, PointerButton b = PointerButton.None, ModifierKeys m = ModifierKeys.None) =>
        new(new Vec2(x, y), new Vec2(x, y), b, m, 1);

    private void Click(double x, double y)
    {
        _s.PointerDown(At(x, y, PointerButton.Left));
        _s.PointerUp(At(x, y, PointerButton.Left));
    }

    private GroupObject GroupBoxAndText()
    {
        _s.Selection.Set([_box, _text]);
        return _s.GroupSelection()!;
    }

    // ---- The user's example ----

    [Fact]
    public void Grouping_the_box_and_the_text_leaves_the_outline_between_them()
    {
        var group = GroupBoxAndText();

        Assert.Equal([_box, _outline, _text], L.Children);           // the outline is still over the box and under the text
        Assert.Equal([_box, _text], group.Children);
        Assert.Same(group, _box.TopLevel);
        Assert.Same(group, _text.TopLevel);
        Assert.Same(_outline, _outline.TopLevel);
        Assert.Equal("Group", _s.History.UndoDescription);
    }

    [Fact]
    public void Undo_and_redo_of_grouping_and_ungrouping_never_move_anything()
    {
        var group = GroupBoxAndText();
        _s.Undo();
        Assert.Equal([_box, _outline, _text], L.Children);
        Assert.Equal(0, Doc.GroupCount);
        _s.Redo();
        Assert.Equal([_box, _outline, _text], L.Children);
        Assert.Equal(1, Doc.GroupCount);
        Assert.Same(group, _text.TopLevel);

        _s.Selection.Set(group);
        _s.UngroupSelection();
        Assert.Equal([_box, _outline, _text], L.Children);
        Assert.Equal(0, Doc.GroupCount);
        _s.Undo();
        Assert.Equal([_box, _outline, _text], L.Children);
        Assert.Same(group, _box.TopLevel);
        Assert.Equal([_box, _text], group.Children);
        _s.Redo();
        Assert.Equal([_box, _outline, _text], L.Children);
        Assert.Same(_box, _box.TopLevel);
    }

    [Fact]
    public void A_click_on_any_member_selects_the_whole_group_and_a_click_on_the_outline_only_the_outline()
    {
        var group = GroupBoxAndText();
        _s.Selection.Clear();

        Click(10, 10);                        // the box, away from the outline
        Assert.Equal([group], _s.Selection.Items);
        _s.Selection.Clear();
        Click(50, 50);                        // the text, in front of everything
        Assert.Equal([group], _s.Selection.Items);
        _s.Selection.Clear();
        Click(35, 35);                        // the white outline covers the box here
        Assert.Equal([_outline], _s.Selection.Items);
    }

    [Fact]
    public void Moving_the_group_moves_the_box_and_the_text_and_leaves_the_outline_where_it_is_and_undo_restores()
    {
        var group = GroupBoxAndText();
        _s.Selection.Set(group);
        var outlineBefore = _outline.GetWorldBounds();
        _s.TransformSelection("Move", Affine2.Translation(200, 0));

        Assert.Equal(200, _box.GetWorldBounds().Left, 9);
        Assert.Equal(240, _text.GetWorldBounds().Left, 9);
        Assert.Equal(outlineBefore, _outline.GetWorldBounds());
        Assert.Equal([_box, _outline, _text], L.Children);

        _s.Undo();
        Assert.Equal(0, _box.GetWorldBounds().Left, 9);
        Assert.Equal([_box, _outline, _text], L.Children);
    }

    [Fact]
    public void Scaling_and_nudging_work_through_the_group_with_members_far_apart_in_the_drawing_order()
    {
        var group = GroupBoxAndText();
        _s.Selection.Set(group);
        _s.TransformSelection("Scale", Affine2.Scale(2, 2));
        Assert.Equal(200, _box.GetWorldBounds().Width, 9);
        Assert.Equal(40, _text.GetWorldBounds().Width, 9);
        Assert.Equal(40, _outline.GetWorldBounds().Width, 9);
        _s.NudgeSelection(new Vec2(5, 0));
        Assert.Equal(5, _box.GetWorldBounds().Left, 9);
        Assert.Equal([_box, _outline, _text], L.Children);
    }

    [Fact]
    public void Deleting_the_group_leaves_the_outline_and_undo_puts_everything_back_in_place()
    {
        var group = GroupBoxAndText();
        _s.Selection.Set(group);
        _s.DeleteSelection();
        Assert.Equal([_outline], L.Children);
        Assert.False(Doc.Contains(group));
        Assert.Equal(1, Doc.ObjectCount);

        _s.Undo();
        Assert.Equal([_box, _outline, _text], L.Children);
        Assert.Equal([_box, _text], group.Children);
        Assert.Same(group, _text.TopLevel);
        Assert.Equal(4, Doc.ObjectCount);
        _s.Redo();
        Assert.Equal([_outline], L.Children);
    }

    [Fact]
    public void Ungroup_and_Break_Apart_keep_every_stack_position()
    {
        var group = GroupBoxAndText();
        _s.Selection.Set(group);
        _s.UngroupSelection();
        Assert.Equal([_box, _outline, _text], L.Children);

        _s.Undo();
        _s.Selection.Set(group);
        _s.BreakApartSelection();                               // K
        Assert.Equal(3, L.Children.Count);
        Assert.Equal(["#CC0000", "#FFFFFF", "#CC0000"], L.Children.Select(o => ((SolidPaint)o.Style.Fill!).Color.ToHex()));
        Assert.Equal(0, Doc.GroupCount);
        Assert.Equal(L.Children.Select(o => o.GetWorldBounds()), new[] { _box, _outline, _text }.Select(o => o.GetWorldBounds()));
        _s.Undo();
        Assert.Equal([_box, _outline, _text], L.Children);
        Assert.Same(group, _text.TopLevel);
    }

    // ---- Nesting ----

    [Fact]
    public void Nested_groups_with_other_objects_between_their_members_peel_one_level_at_a_time()
    {
        var inner = GroupBoxAndText();                        // {box, text}, outline between them
        var far = Add(200, 200, 20, 20, "#0000FF");
        _s.Selection.Set([inner, far]);
        var outer = _s.GroupSelection()!;
        Assert.Equal([_box, _outline, _text, far], L.Children);
        Assert.Same(outer, _text.TopLevel);
        Assert.Equal([inner, far], outer.Children);

        _s.Selection.Set(outer);
        _s.UngroupSelection();                                 // U: one level
        Assert.Equal([_box, _outline, _text, far], L.Children);
        Assert.Same(inner, _box.TopLevel);
        Assert.Same(far, far.TopLevel);

        _s.Undo();
        Assert.Same(outer, _box.TopLevel);
        Assert.Equal([inner, far], outer.Children);
    }

    // ---- Copy and paste ----

    [Fact]
    public void Copying_the_group_and_the_outline_together_keeps_what_is_between_the_members_of_the_copy()
    {
        var group = GroupBoxAndText();
        _s.Selection.Set([group, _outline]);
        _s.Copy();
        _s.Paste();
        Click(600, 400);                                       // puts the copies down

        Assert.Equal(6, L.Children.Count);
        Assert.Equal([_box, _outline, _text], L.Children.Skip(3));
        var copies = L.Children.Take(3).ToList();                            // a paste is a new item: the copies are the three lowest objects (R32)
        Assert.Equal(["#CC0000", "#FFFFFF", "#CC0000"], copies.Select(o => ((SolidPaint)o.Style.Fill!).Color.ToHex()));   // box, outline, text: as they were
        Assert.Same(copies[0].TopLevel, copies[2].TopLevel);                // the copied box and text are one group
        Assert.IsType<GroupObject>(copies[0].TopLevel);
        Assert.NotSame(group, copies[0].TopLevel);
        Assert.Same(copies[1], copies[1].TopLevel);                         // the copied outline is on its own
        Assert.Equal(2, Doc.GroupCount);
        Assert.Equal(2, _s.Selection.Count);

        _s.Undo();
        Assert.Equal([_box, _outline, _text], L.Children);
        Assert.Equal(1, Doc.GroupCount);
    }

    [Fact]
    public void Copy_dragging_the_group_makes_a_group_with_the_same_gap_between_its_members()
    {
        var group = GroupBoxAndText();
        _s.Selection.Set(group);
        _s.PointerDown(At(10, 10, PointerButton.Left));
        _s.PointerMove(At(310, 10, PointerButton.None, ModifierKeys.Control));
        _s.PointerUp(At(310, 10, PointerButton.Left, ModifierKeys.Control));

        Assert.Equal(5, L.Children.Count);                                  // box, outline, text, and the copy's box and text on top
        var copy = Assert.IsType<GroupObject>(Assert.Single(_s.Selection.Items));
        Assert.NotSame(group, copy);
        Assert.Equal(2, copy.Children.Count);
        Assert.Equal([_box, _outline, _text, copy.Children[0], copy.Children[1]], L.Children);
        Assert.Equal(300, copy.Children[0].GetWorldBounds().Left, 9);
    }

    // ---- Combine, Weld, Common Weld: modified objects whose stacking is readjusted are pulled to the front ----

    [Fact]
    public void Combine_of_members_that_were_apart_goes_to_the_front_and_undo_restores_the_groups()
    {
        var group = GroupBoxAndText();
        var above = Add(300, 300, 20, 20, "#00AA00");           // [box, outline, text, above]: above the topmost member
        _s.Selection.Set(group);
        var combined = _s.CombineSelection()!;
        Assert.Equal([_outline, above, combined], L.Children);   // the front: over everything, also over what was above the group
        Assert.Equal(2, combined.Parts.Count);

        _s.Undo();
        Assert.Equal([_box, _outline, _text, above], L.Children);
        Assert.Same(group, _text.TopLevel);
        Assert.Equal([_box, _text], group.Children);
        _s.Redo();
        Assert.Equal([_outline, above, combined], L.Children);
    }

    [Fact]
    public void Combine_of_members_that_lie_together_stays_where_it_was()
    {
        var a = Add(300, 0, 10, 10, "#111111");
        var b = Add(320, 0, 10, 10, "#222222");
        var c = Add(340, 0, 10, 10, "#333333");                  // [box, outline, text, a, b, c]
        var top = Add(500, 0, 10, 10, "#444444");
        _s.Selection.Set([a, b]);
        var combined = _s.CombineSelection()!;
        Assert.Equal([_box, _outline, _text, combined, c, top], L.Children);   // nothing around it moved
    }

    [Fact]
    public void Weld_of_members_that_were_apart_goes_to_the_front()
    {
        var group = GroupBoxAndText();
        var above = Add(300, 300, 20, 20, "#00AA00");
        _s.Selection.Set(group);
        Assert.True(_s.CanWeld);
        _s.Weld();
        var welded = Assert.IsType<PathObject>(L.Children[^1]);
        Assert.Equal("Weld", welded.Name);
        Assert.Equal([_outline, above, welded], L.Children);
        _s.Undo();
        Assert.Equal([_box, _outline, _text, above], L.Children);
        Assert.Same(group, _box.TopLevel);
    }

    [Fact]
    public void Common_weld_of_members_that_were_apart_goes_to_the_front()
    {
        var group = GroupBoxAndText();
        var above = Add(300, 300, 20, 20, "#00AA00");
        _s.Selection.Set(group);
        Assert.True(_s.CanCommonWeld);
        _s.CommonWeld();
        Assert.Equal([_outline, above], L.Children.Take(2));
        Assert.Equal("Common Weld", L.Children[^1].Name);
        _s.Undo();
        Assert.Equal([_box, _outline, _text, above], L.Children);
    }

    // ---- The user's test of the reference program (2026-10-13): yellow (back), black, green (front); yellow and green grouped ----

    private (RectangleObject Y, RectangleObject B, RectangleObject G, GroupObject Group, EditorSession S) YellowBlackGreen()
    {
        var s = new EditorSession(NewDoc());
        RectangleObject Add(double x, string color)
        {
            var r = Rect(x, 0, 100, 100, ObjectStyle.FillOnly(DocColor.FromHex(color)));
            s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, r));
            return r;
        }
        var y = Add(0, "#FFDD00"); var b = Add(20, "#000000"); var g = Add(40, "#00AA00");   // all overlapping
        s.Selection.Set([y, g]);
        var group = s.GroupSelection()!;
        return (y, b, g, group, s);
    }

    [Fact]
    public void Page_steps_one_to_four_as_the_user_saw_them()
    {
        var (y, b, g, group, s) = YellowBlackGreen();
        var l = s.Document.ActiveLayer;
        Assert.Equal([y, b, g], l.Children);

        s.Selection.Set(group);
        s.ChangeOrder(StackMove.Backward);               // 1. group selected, Page Down: Y B G -> Y G B (green drops below black, stays above yellow)
        Assert.Equal([y, g, b], l.Children);

        s.ChangeOrder(StackMove.Forward);                // 2. group selected, Page Up: Y G B -> Y B G (the sandwich returns; yellow does not move)
        Assert.Equal([y, b, g], l.Children);

        s.Selection.Set(b);
        s.ChangeOrder(StackMove.Backward);               // 3. black selected, Page Down: Y B G -> B Y G (drops below yellow)
        Assert.Equal([b, y, g], l.Children);

        s.ChangeOrder(StackMove.Forward);                // 4. black selected, Page Up: B Y G -> Y G B (jumps above the whole group in one step)
        Assert.Equal([y, g, b], l.Children);

        Assert.Same(group, y.TopLevel);                  // grouping never changed
        Assert.Same(group, g.TopLevel);
        Assert.Equal(4, s.History.UndoList.Count(c => c.Description == "Change Order"));   // each step is one undo step
        for (int i = 0; i < 4; i++) s.Undo();
        Assert.Equal([y, b, g], l.Children);
        for (int i = 0; i < 4; i++) s.Redo();
        Assert.Equal([y, g, b], l.Children);
    }

    [Fact]
    public void A_step_that_changes_nothing_is_no_undo_step()
    {
        var (y, b, g, group, s) = YellowBlackGreen();
        s.Selection.Set(group);
        s.ChangeOrder(StackMove.Forward);                // Y B G: green is on top and stays, yellow hops over black -> B Y G
        Assert.Equal([b, y, g], s.Document.ActiveLayer.Children);
        int steps = s.History.UndoList.Count;
        s.ChangeOrder(StackMove.Forward);                // now yellow has its group mate green above and green is on top: nothing hops
        Assert.Equal(steps, s.History.UndoList.Count);
    }

    // ---- To Front / To Back (menu commands, unchanged) and a single object stepping past a group ----

    [Fact]
    public void Bring_to_front_of_the_group_moves_its_members_up_in_their_own_order_and_undo_restores()
    {
        var group = GroupBoxAndText();
        var front = Add(500, 500, 10, 10, "#00FF00");
        _s.Selection.Set(group);
        _s.ChangeOrder(StackMove.ToFront);
        Assert.Equal([_outline, front, _box, _text], L.Children);
        Assert.Equal([_box, _text], group.Children);
        Assert.Equal("Change Order", _s.History.UndoDescription);
        _s.Undo();
        Assert.Equal([_box, _outline, _text, front], L.Children);
    }

    [Fact]
    public void Send_to_back_and_bring_to_front_of_one_object()
    {
        GroupBoxAndText();
        _s.Selection.Set(_outline);
        _s.ChangeOrder(StackMove.ToBack);
        Assert.Equal([_outline, _box, _text], L.Children);
        _s.ChangeOrder(StackMove.ToFront);
        Assert.Equal([_box, _text, _outline], L.Children);
    }

    [Fact]
    public void A_loose_object_steps_over_the_next_member_or_the_run_of_members_it_meets()
    {
        var group = GroupBoxAndText();
        _s.Selection.Set(_outline);                              // [box, outline, text]: box and text are apart
        _s.ChangeOrder(StackMove.Forward);
        Assert.Equal([_box, _text, _outline], L.Children);
        int steps = _s.History.UndoList.Count;
        _s.ChangeOrder(StackMove.Forward);                       // already on top: nothing to do, and no undo step
        Assert.Equal(steps, _s.History.UndoList.Count);
        _s.Undo();
        Assert.Equal([_box, _outline, _text], L.Children);

        _s.ChangeOrder(StackMove.Backward);
        Assert.Equal([_outline, _box, _text], L.Children);
        _s.ChangeOrder(StackMove.Forward);                       // the box and the text now lie together: one run, hopped over in one step
        Assert.Equal([_box, _text, _outline], L.Children);
        Assert.Same(group, _box.TopLevel);                       // grouping was never touched
    }

    [Fact]
    public void A_group_that_lies_together_is_not_moved_as_a_block_by_a_step()
    {
        var a = Add(300, 0, 10, 10, "#111111");
        var b = Add(320, 0, 10, 10, "#222222");
        var c = Add(340, 0, 10, 10, "#333333");
        _s.Selection.Set([b, c]);
        var bc = _s.GroupSelection()!;
        _s.Selection.Set(a);                                     // [box, outline, text, a, b, c]
        _s.ChangeOrder(StackMove.Forward);                       // a loose object hops over the whole run of the group at once
        Assert.Equal([_box, _outline, _text, b, c, a], L.Children);

        _s.Selection.Set(bc);
        _s.ChangeOrder(StackMove.Forward);                       // b has its group mate above it and stays; c hops over a
        Assert.Equal([_box, _outline, _text, b, a, c], L.Children);
        _s.Undo();
        Assert.Equal([_box, _outline, _text, b, c, a], L.Children);
    }
    // ---- Undo bookkeeping ----

    [Fact]
    public void Object_count_and_group_count_follow_grouping_undo_and_delete()
    {
        Assert.Equal(3, Doc.ObjectCount);
        var group = GroupBoxAndText();
        Assert.Equal(4, Doc.ObjectCount);
        Assert.Equal(1, Doc.GroupCount);
        _s.Selection.Set(group);
        _s.DeleteSelection();
        Assert.Equal(1, Doc.ObjectCount);
        Assert.Equal(0, Doc.GroupCount);
        _s.Undo();
        Assert.Equal(1, Doc.GroupCount);
        _s.Undo();
        Assert.Equal(0, Doc.GroupCount);
        Assert.Equal(3, Doc.ObjectCount);
    }

    [Fact]
    public void Hidden_group_hides_all_its_members_wherever_they_are_drawn()
    {
        var group = GroupBoxAndText();
        group.Visible = false;
        Assert.False(_box.IsShown);
        Assert.False(_text.IsShown);
        Assert.True(_outline.IsShown);
        Assert.Null(HitTesting.HitTest(Doc, new Vec2(10, 10), 1));       // the hidden box cannot be clicked
        Assert.Same(_outline, HitTesting.HitTest(Doc, new Vec2(35, 35), 1));
    }

    [Fact]
    public void Marquee_catches_a_group_once_even_though_its_members_are_apart_in_the_drawing_order()
    {
        var group = GroupBoxAndText();
        var far = Add(600, 600, 10, 10, "#0000FF");
        var found = HitTesting.EnclosedBy(Doc, new Rect2(-10, -10, 200, 200)).ToList();
        Assert.Equal([group, _outline], found);
        Assert.DoesNotContain(far, found);
    }

    [Fact]
    public void Select_all_selects_each_top_level_object_once()
    {
        var group = GroupBoxAndText();
        _s.SelectAll();
        Assert.Equal([group, _outline], _s.Selection.Items);
    }
}
