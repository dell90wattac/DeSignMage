using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Arrange ▸ Group ▸ Group All and Group by Color. Both follow R22: grouping never changes the stacking order. One undo step each.</summary>
public class GroupAllAndByColorTests
{
    private readonly EditorSession _s = new(NewDoc());
    private Layer L => _s.Document.ActiveLayer;
    private IReadOnlyList<DocObject> Tops => L.Children.Select(o => o.TopLevel).Distinct().ToList();

    private static readonly DocColor Red = DocColor.FromHex("#CC0000"), Blue = DocColor.FromHex("#0000CC"), Green = DocColor.FromHex("#00AA00");

    private RectangleObject Add(DocColor? color, double x = 0, Layer? layer = null)
    {
        var r = Rect(x, 0, 10, 10, color is { } c ? ObjectStyle.FillOnly(c) : ObjectStyle.None);
        _s.History.Execute(new AddObjectCommand(layer ?? L, r));
        return r;
    }

    // ---- Group All ----

    [Fact]
    public void Group_All_groups_every_top_level_object_of_the_layout_and_moves_nothing()
    {
        var a = Add(Red, 0); var b = Add(Blue, 20); var c = Add(Green, 40);
        var other = new Layer("Other");
        _s.Document.InsertLayer(1, other);
        var d = Add(Red, 60, other);
        int steps = _s.History.UndoList.Count;

        Assert.True(_s.CanGroupAll);
        var group = _s.GroupAll()!;

        Assert.Equal([a, b, c, d], group.Children.OrderBy(o => o == d ? 1 : 0));    // one group over both layers
        Assert.All(new DocObject[] { a, b, c, d }, o => Assert.Same(group, o.TopLevel));
        Assert.Equal([a, b, c], L.Children);                                         // the drawing order is untouched
        Assert.Equal([d], other.Children);
        Assert.Equal([group], _s.Selection.Items);
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Group All", _s.History.UndoDescription);

        _s.Undo();
        Assert.Equal(0, _s.Document.GroupCount);
        Assert.All(new DocObject[] { a, b, c, d }, o => Assert.Same(o, o.TopLevel));
        _s.Redo();
        Assert.Equal(1, _s.Document.GroupCount);
    }

    [Fact]
    public void Group_All_leaves_out_hidden_and_locked_layers_and_objects_and_nests_an_existing_group()
    {
        var a = Add(Red, 0); var b = Add(Red, 20); var c = Add(Blue, 40);
        var hidden = Add(Blue, 60); hidden.Visible = false;
        var locked = Add(Blue, 80); locked.Locked = true;
        var lockedLayer = new Layer("Locked") { Locked = true };
        _s.Document.InsertLayer(1, lockedLayer);
        var e = Add(Green, 100, lockedLayer);
        _s.Selection.Set([a, b]);
        var inner = _s.GroupSelection()!;

        var all = _s.GroupAll()!;
        Assert.Equal([inner, c], all.Children);                                      // the existing group became a member; nothing was ungrouped
        Assert.Same(hidden, hidden.TopLevel);
        Assert.Same(locked, locked.TopLevel);
        Assert.Same(e, e.TopLevel);
        Assert.Equal([a, b], inner.Children);
    }

    [Fact]
    public void Group_All_needs_two_objects()
    {
        Assert.False(_s.CanGroupAll);
        Assert.Null(_s.GroupAll());
        Add(Red);
        Assert.False(_s.CanGroupAll);
        Assert.Null(_s.GroupAll());
        Assert.Equal(0, _s.Document.GroupCount);
    }

    // ---- Group by Color ----

    [Fact]
    public void With_nothing_selected_each_color_becomes_one_group_and_the_stacking_stays()
    {
        var a = Add(Red, 0); var b = Add(Blue, 20); var c = Add(Red, 40); var d = Add(Blue, 60); var e = Add(Green, 80);
        _s.Selection.Clear();
        int steps = _s.History.UndoList.Count;

        Assert.True(_s.CanGroupByColor);
        Assert.Equal(2, _s.GroupByColor());

        Assert.Equal([a, b, c, d, e], L.Children);                                   // red and blue each interleave with the other: nothing moved
        var reds = Assert.IsType<GroupObject>(a.TopLevel);
        var blues = Assert.IsType<GroupObject>(b.TopLevel);
        Assert.Equal([a, c], reds.Children);
        Assert.Equal([b, d], blues.Children);
        Assert.Same(e, e.TopLevel);                                                  // the only green object makes no group
        Assert.Equal([reds, blues], _s.Selection.Items);
        Assert.Equal(steps + 1, _s.History.UndoList.Count);                          // one undo step
        Assert.Equal("Group by Color", _s.History.UndoDescription);

        _s.Undo();
        Assert.Equal(0, _s.Document.GroupCount);
        Assert.Equal([a, b, c, d, e], L.Children);
        _s.Redo();
        Assert.Equal(2, _s.Document.GroupCount);
    }

    [Fact]
    public void With_a_selection_only_the_colors_of_the_selected_objects_are_grouped()
    {
        var a = Add(Red, 0); var b = Add(Blue, 20); var c = Add(Red, 40); var d = Add(Blue, 60);
        _s.Selection.Set(a);
        Assert.Equal(1, _s.GroupByColor());                                          // every other red object joins the selected one
        Assert.Same(a.TopLevel, c.TopLevel);
        Assert.Same(b, b.TopLevel);
        Assert.Same(d, d.TopLevel);

        _s.Selection.Set([b, d]);
        Assert.Equal(1, _s.GroupByColor());
        Assert.Same(b.TopLevel, d.TopLevel);
        Assert.Equal(2, _s.Document.GroupCount);
    }

    [Fact]
    public void Colors_match_as_whole_values_so_the_same_rgb_under_two_vinyl_names_is_two_colors()
    {
        var gold1 = new DocColor(0xF2, 0xB7, 0x05, 255, "Oracal 651-020 Golden Yellow", "Oracal 651");
        var gold2 = new DocColor(0xF2, 0xB7, 0x05, 255, "3M 7725 Sunflower", "3M");
        var plain = new DocColor(0xF2, 0xB7, 0x05);
        var a = Add(gold1, 0); var b = Add(gold2, 20); var c = Add(gold1, 40); var d = Add(gold2, 60); var e = Add(plain, 80);
        _s.Selection.Clear();

        Assert.Equal(2, _s.GroupByColor());
        Assert.Same(a.TopLevel, c.TopLevel);
        Assert.Same(b.TopLevel, d.TopLevel);
        Assert.NotSame(a.TopLevel, b.TopLevel);
        Assert.Same(e, e.TopLevel);                                                  // the same RGB with no name is a third color
    }

    [Fact]
    public void Existing_groups_pictures_and_objects_without_a_fill_are_left_alone()
    {
        var a = Add(Red, 0); var b = Add(Red, 20);
        _s.Selection.Set([a, b]);
        var existing = _s.GroupSelection()!;                                         // a red group
        var c = Add(Red, 40); var d = Add(Red, 60);
        var none1 = Add(null, 80); var none2 = Add(null, 100);
        var picture = new ImageObject("none", 10, 10);
        _s.History.Execute(new AddObjectCommand(L, picture));
        _s.Selection.Clear();

        Assert.Equal(1, _s.GroupByColor());
        var made = Assert.IsType<GroupObject>(c.TopLevel);
        Assert.Equal([c, d], made.Children);
        Assert.Same(existing, a.TopLevel);                                           // not pulled apart or into the new one
        Assert.Same(none1, none1.TopLevel);
        Assert.Same(none2, none2.TopLevel);
        Assert.Same(picture, picture.TopLevel);
    }

    [Fact]
    public void Group_by_Color_does_nothing_when_no_color_has_two_objects()
    {
        Add(Red, 0); Add(Blue, 20); Add(Green, 40);
        Assert.False(_s.CanGroupByColor);
        int steps = _s.History.UndoList.Count;
        Assert.Equal(0, _s.GroupByColor());
        Assert.Equal(steps, _s.History.UndoList.Count);
    }

    [Fact]
    public void Grouping_the_red_objects_around_a_blue_one_keeps_the_blue_one_between_them()
    {
        var a = Add(Red, 0); var blue = Add(Blue, 0); var c = Add(Red, 0);          // all overlapping: red, blue, red
        _s.Selection.Set(a);
        _s.GroupByColor();
        Assert.Equal([a, blue, c], L.Children);                                      // the blue object stays between the two red ones (R22)
        Assert.Same(a.TopLevel, c.TopLevel);
    }
}
