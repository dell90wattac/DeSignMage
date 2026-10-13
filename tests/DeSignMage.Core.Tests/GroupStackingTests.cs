using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
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
        _s = new EditorSession(NewDoc());
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
        var copies = L.Children.Skip(3).ToList();
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

    // ---- Combine ----

    [Fact]
    public void Combine_of_a_group_with_something_between_its_members_goes_where_the_topmost_member_was_and_undo_restores_the_groups()
    {
        // OPEN (R22): what the reference behavior is here is not known yet; this is what Combine always did (the result takes the topmost member's place).
        var group = GroupBoxAndText();
        _s.Selection.Set(group);
        var combined = _s.CombineSelection()!;
        Assert.Equal([_outline, combined], L.Children);
        Assert.Equal(2, combined.Parts.Count);

        _s.Undo();
        Assert.Equal([_box, _outline, _text], L.Children);
        Assert.Same(group, _text.TopLevel);
        Assert.Equal([_box, _text], group.Children);
    }

    // ---- Order commands ----

    [Fact]
    public void Bring_to_front_of_the_group_moves_its_members_up_in_their_own_order_and_undo_restores()
    {
        // OPEN (R22): what the reference behavior is for a group like this is not known yet; this is the rule written in StackOrder.
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
    public void Forward_and_backward_move_a_single_object_past_one_item_at_a_time()
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
        _s.ChangeOrder(StackMove.Forward);                       // the box and the text now lie together: one item, hopped over as a block
        Assert.Equal([_box, _text, _outline], L.Children);
        Assert.Same(group, _box.TopLevel);                       // grouping was never touched
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
    public void A_group_whose_members_lie_together_hops_over_a_neighbour_as_one_block_in_both_directions()
    {
        var a = Add(300, 0, 10, 10, "#111111");
        var b = Add(320, 0, 10, 10, "#222222");
        var c = Add(340, 0, 10, 10, "#333333");
        _s.Selection.Set([b, c]);
        var bc = _s.GroupSelection()!;
        _s.Selection.Set(a);                                     // [box, outline, text, a, b, c]
        _s.ChangeOrder(StackMove.Forward);                       // a hops over the whole group {b, c}
        Assert.Equal([_box, _outline, _text, b, c, a], L.Children);

        _s.Selection.Set(bc);
        _s.ChangeOrder(StackMove.Forward);                       // the group hops over a
        Assert.Equal([_box, _outline, _text, a, b, c], L.Children);
        _s.ChangeOrder(StackMove.Backward);
        Assert.Equal([_box, _outline, _text, b, c, a], L.Children);   // and back under a (the group is now in front of it)
        _s.Undo();
        Assert.Equal([_box, _outline, _text, a, b, c], L.Children);
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
