using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Arrange ▸ Close All Loops, Remove All Inner Loops and Break Into Outer Loops (TASKS C.37).</summary>
public class LoopCleanupTests
{
    private readonly EditorSession _s;
    private Layer L => _s.Document.ActiveLayer;

    private static readonly ObjectStyle Red = ObjectStyle.FillOnly(DocColor.FromHex("#CC0000"));
    private static readonly ObjectStyle Blue = ObjectStyle.FillOnly(DocColor.FromHex("#0033CC"));

    public LoopCleanupTests()
    {
        _s = new EditorSession(NewDoc());
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
    }

    private static Loop Box(double x, double y, double w, double h) => PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(x, y, w, h)))[0];

    private static Loop Reversed(Loop l) { PathEditing.Reverse(l); return l; }

    private static Loop OpenPolyline(params Vec2[] points) =>
        new() { Start = points[0], Segments = points.Skip(1).Select(p => Segment.Line(p)).ToList() };

    private PathObject AddPath(ObjectStyle style, FillRule rule, params Loop[] loops)
    {
        var p = new PathObject(PathEditing.FromLoops(loops, rule)) { Style = style };
        _s.History.Execute(new AddObjectCommand(L, p));
        return p;
    }

    private static List<Loop> LoopsOf(DocObject o) => PathEditing.ToLoops(o.GetLocalGeometry()!);

    // ---- Close All Loops ----

    [Fact]
    public void Close_all_loops_closes_every_open_loop_in_one_undo_step()
    {
        var open = OpenPolyline(new Vec2(0, 0), new Vec2(100, 0), new Vec2(100, 100));          // ends apart: a straight vector bridges them
        var almost = Box(200, 0, 50, 50);
        PathEditing.BreakAt([almost], 0, 0);                                                    // an open loop whose ends already meet
        var path = AddPath(Red, FillRule.EvenOdd, open, almost, Box(400, 0, 50, 50));
        _s.Selection.Set(path);
        Assert.True(_s.CanCloseAllLoops);

        int steps = _s.History.UndoList.Count;
        _s.CloseAllLoops();
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Close All Loops", _s.History.UndoDescription);
        var loops = LoopsOf(path);
        Assert.Equal(3, loops.Count);
        Assert.All(loops, l => Assert.True(l.Closed));
        Assert.Equal(3, loops[0].Segments.Count);        // the two it had plus the bridge back to the start
        Assert.Equal(4, loops[1].Segments.Count);        // nothing added where the ends met
        Assert.False(_s.CanCloseAllLoops);
        Assert.Same(path, Assert.Single(_s.Selection.Items));

        _s.Undo();
        Assert.False(LoopsOf(path)[0].Closed);
        Assert.True(_s.CanCloseAllLoops);
    }

    [Fact]
    public void Close_all_loops_works_inside_groups_and_combined_objects_and_ignores_the_rest()
    {
        var open = AddPath(Red, FillRule.NonZero, OpenPolyline(new Vec2(0, 0), new Vec2(80, 0), new Vec2(80, 80)));
        var box = Rect(300, 0, 50, 50, Red);
        _s.History.Execute(new AddObjectCommand(L, box));
        _s.Selection.Set([open, box]);
        var group = _s.GroupSelection()!;
        Assert.True(_s.CanCloseAllLoops);
        _s.CloseAllLoops();
        Assert.True(LoopsOf(open)[0].Closed);
        Assert.Same(group, Assert.Single(_s.Selection.Items));       // the group stays selected

        var second = AddPath(Blue, FillRule.NonZero, OpenPolyline(new Vec2(0, 300), new Vec2(80, 300), new Vec2(80, 380)));
        var other = Rect(300, 300, 50, 50, Blue);
        _s.History.Execute(new AddObjectCommand(L, other));
        _s.Selection.Set([second, other]);
        var combined = _s.CombineSelection()!;
        Assert.True(_s.CanCloseAllLoops);
        _s.CloseAllLoops();
        var part = combined.Parts.OfType<PathObject>().Single();
        Assert.True(LoopsOf(part)[0].Closed);
        Assert.Equal(Blue, part.Style);                                 // the part kept its own style

        _s.Selection.Set(box);
        Assert.False(_s.CanCloseAllLoops);                              // a rectangle has nothing open
    }

    [Fact]
    public void Close_all_loops_is_not_offered_for_text_or_nothing()
    {
        Assert.False(_s.CanCloseAllLoops);
        var t = new TextObject("Ho", "Arial", 100);
        _s.History.Execute(new AddObjectCommand(L, t));
        _s.Selection.Set(t);
        Assert.False(_s.CanCloseAllLoops);
    }

    // ---- Remove All Inner Loops ----

    [Fact]
    public void Remove_all_inner_loops_keeps_the_outer_loops_and_open_strokes()
    {
        var path = AddPath(Red, FillRule.EvenOdd, Box(0, 0, 300, 300), Reversed(Box(50, 50, 200, 200)), Reversed(Box(100, 100, 100, 100)), OpenPolyline(new Vec2(400, 0), new Vec2(450, 0)));
        _s.Selection.Set(path);
        Assert.True(_s.CanRemoveInnerLoops);

        int steps = _s.History.UndoList.Count;
        _s.RemoveAllInnerLoops();
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Remove All Inner Loops", _s.History.UndoDescription);
        var loops = LoopsOf(path);
        Assert.Equal(2, loops.Count);                                 // the hole and the island inside it are gone
        Assert.Equal(new Rect2(0, 0, 300, 300), loops[0].Bounds());
        Assert.False(loops[1].Closed);
        Assert.False(_s.CanRemoveInnerLoops);

        _s.Undo();
        Assert.Equal(4, LoopsOf(path).Count);
    }

    [Fact]
    public void Remove_all_inner_loops_on_a_combined_object_works_across_its_parts()
    {
        var big = new EllipseObject(200, 200) { Style = Red };
        var small = new EllipseObject(80, 80) { Transform = Affine2.Translation(60, 60), Style = Blue };
        var far = Rect(500, 0, 50, 50, Blue);
        foreach (var o in new DocObject[] { big, small, far }) _s.History.Execute(new AddObjectCommand(L, o));
        _s.Selection.Set([big, small, far]);
        var combined = _s.CombineSelection()!;
        Assert.True(_s.CanRemoveInnerLoops);

        _s.RemoveAllInnerLoops();
        var parts = combined.Parts;
        Assert.Equal(3, parts.Count);
        Assert.Single(LoopsOf(parts[0]));                     // the big circle
        Assert.Empty(LoopsOf(parts[1]));                              // the small one lay inside it: no loop left
        Assert.Single(LoopsOf(parts[2]));
        Assert.Same(combined, Assert.Single(_s.Selection.Items));
        _s.Undo();
        Assert.Single(LoopsOf(combined.Parts[1]));
    }

    [Fact]
    public void Remove_all_inner_loops_turns_live_text_into_curves_only_when_it_has_holes()
    {
        var plain = new TextObject("H", "Arial", 100);
        var holey = new TextObject("B", "Arial", 100) { Transform = Affine2.Translation(300, 0) };
        _s.History.Execute(new AddObjectCommand(L, plain));
        _s.History.Execute(new AddObjectCommand(L, holey));

        _s.Selection.Set(plain);
        Assert.False(_s.CanRemoveInnerLoops);

        _s.Selection.Set(holey);
        Assert.True(_s.CanRemoveInnerLoops);
        _s.RemoveAllInnerLoops();
        var path = Assert.IsType<PathObject>(L.Children.Single(o => o != plain));
        Assert.Single(LoopsOf(path));                         // B has two counters; both are gone
        Assert.Equal(holey.Transform, path.Transform);
        Assert.Same(path, Assert.Single(_s.Selection.Items));         // the converted object is what stays selected

        _s.Undo();                                                    // one step brings the live text back
        Assert.Contains(holey, L.Children);
        Assert.IsType<TextObject>(Assert.Single(L.Children, o => o == holey));
    }

    // ---- Break Into Outer Loops ----

    [Fact]
    public void Break_into_outer_loops_makes_one_object_per_outer_loop_and_each_keeps_its_holes()
    {
        var below = Rect(0, 600, 10, 10, Blue);
        _s.History.Execute(new AddObjectCommand(L, below));
        var path = AddPath(Red, FillRule.EvenOdd, Box(0, 0, 300, 300), Reversed(Box(50, 50, 200, 200)), Reversed(Box(100, 100, 100, 100)), OpenPolyline(new Vec2(400, 0), new Vec2(450, 0)));
        var above = Rect(0, 700, 10, 10, Blue);
        _s.History.Execute(new AddObjectCommand(L, above));
        _s.Selection.Set(path);
        Assert.True(_s.CanBreakIntoOuterLoops);
        Assert.True(_s.CanBreakIntoLoops);                              // the plain command would make four pieces

        int steps = _s.History.UndoList.Count;
        _s.BreakIntoOuterLoopsSelection();
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Break Into Outer Loops", _s.History.UndoDescription);
        Assert.True(_s.Selection.IsEmpty);

        // The three pieces take the place of the original, between the two objects around it.
        Assert.Equal(5, L.Children.Count);
        Assert.Same(below, L.Children[0]);
        Assert.Same(above, L.Children[4]);
        var frame = Assert.IsType<PathObject>(L.Children[1]);
        var island = Assert.IsType<PathObject>(L.Children[2]);
        var stroke = Assert.IsType<PathObject>(L.Children[3]);
        Assert.Equal(2, LoopsOf(frame).Count);                          // the frame keeps its hole
        Assert.Equal(new Rect2(0, 0, 300, 300), LoopsOf(frame)[0].Bounds());
        Assert.Equal(new Rect2(50, 50, 250, 250), LoopsOf(frame)[1].Bounds());
        Assert.Equal(FillRule.EvenOdd, frame.Geometry.FillRule);
        Assert.Single(LoopsOf(island));                         // an island inside the hole is an outer loop of its own
        Assert.Equal(new Rect2(100, 100, 200, 200), LoopsOf(island)[0].Bounds());
        Assert.False(LoopsOf(stroke)[0].Closed);                        // so is an open loop
        Assert.All(new[] { frame, island, stroke }, p => Assert.Equal(Red, p.Style));

        _s.Undo();
        Assert.Same(path, L.Children[1]);
        Assert.Equal(3, L.Children.Count);
        _s.Redo();
        Assert.Equal(5, L.Children.Count);
    }

    [Fact]
    public void An_object_with_one_outer_loop_is_left_alone()
    {
        var ring = AddPath(Red, FillRule.EvenOdd, Box(0, 0, 300, 300), Reversed(Box(50, 50, 200, 200)));
        _s.Selection.Set(ring);
        Assert.False(_s.CanBreakIntoOuterLoops);
        int steps = _s.History.UndoList.Count;
        _s.BreakIntoOuterLoopsSelection();
        Assert.Equal(steps, _s.History.UndoList.Count);
        Assert.Same(ring, Assert.Single(L.Children));
    }

    [Fact]
    public void Break_into_outer_loops_on_a_combined_object_keeps_each_part_s_style_and_takes_holes_from_other_parts()
    {
        var big = new EllipseObject(200, 200) { Style = Red };
        var small = new EllipseObject(80, 80) { Transform = Affine2.Translation(60, 60), Style = Blue };
        var far = Rect(500, 0, 50, 50, Blue);
        foreach (var o in new DocObject[] { big, small, far }) _s.History.Execute(new AddObjectCommand(L, o));
        _s.Selection.Set([big, small, far]);
        var combined = _s.CombineSelection()!;
        Assert.True(_s.CanBreakIntoOuterLoops);

        _s.BreakIntoOuterLoopsSelection();
        Assert.Equal(2, L.Children.Count);
        var ring = Assert.IsType<PathObject>(L.Children[0]);
        var box = Assert.IsType<PathObject>(L.Children[1]);
        Assert.Equal(Red, ring.Style);                                  // the big circle's color, not the small one's
        Assert.Equal(Blue, box.Style);
        var loops = LoopsOf(ring);
        Assert.Equal(2, loops.Count);                                   // its hole is the small circle of the other part
        var hole = loops[1].ToGeometry().Transform(ring.Transform).GetBounds();
        Assert.Equal(60, hole.Left, 1);
        Assert.Equal(140, hole.Right, 1);
        Assert.Equal(60, hole.Top, 1);
        Assert.Equal(FillRule.EvenOdd, ring.Geometry.FillRule);
        Assert.Equal(new Rect2(500, 0, 550, 50), box.GetWorldBounds());
        _s.Undo();
        Assert.Same(combined, Assert.Single(L.Children));
    }

    [Fact]
    public void Break_into_outer_loops_splits_live_text_into_its_letters_without_losing_counters()
    {
        var t = new TextObject("Ho", "Arial", 100) { Transform = Affine2.Translation(100, 300), Style = Red };
        _s.History.Execute(new AddObjectCommand(L, t));
        _s.Selection.Set(t);
        Assert.True(_s.CanBreakIntoOuterLoops);
        _s.BreakIntoOuterLoopsSelection();
        Assert.Equal(2, L.Children.Count);
        var h = Assert.IsType<PathObject>(L.Children[0]);
        var o = Assert.IsType<PathObject>(L.Children[1]);
        Assert.Single(LoopsOf(h));
        Assert.Equal(2, LoopsOf(o).Count);                              // the o keeps its counter (Break Into Loops would lose it)
        Assert.Equal(Red, h.Style);
        Assert.Equal(t.Transform, h.Transform);
        _s.Undo();
        Assert.Same(t, Assert.Single(L.Children));

        var single = new TextObject("H", "Arial", 100);
        _s.History.Execute(new AddObjectCommand(L, single));
        _s.Selection.Set(single);
        Assert.False(_s.CanBreakIntoOuterLoops);
    }

    [Fact]
    public void Break_into_outer_loops_on_converted_text_and_on_group_members()
    {
        var t = new TextObject("Ho", "Arial", 100) { Style = Red };
        _s.History.Execute(new AddObjectCommand(L, t));
        _s.Selection.Set(t);
        _s.ConvertSelectionToCurves(); _s.Selection.Clear();
        var letters = L.Children.ToList();                              // converting makes a group of letter paths
        Assert.Equal(2, letters.Count);
        Assert.IsType<GroupObject>(letters[0].Parent);

        // A multi-loop path inside a group splits inside that group.
        var multi = AddPath(Blue, FillRule.EvenOdd, Box(500, 0, 100, 100), Reversed(Box(520, 20, 40, 40)), Box(700, 0, 100, 100));
        var pal = Rect(0, 500, 20, 20, Blue);
        _s.History.Execute(new AddObjectCommand(L, pal));
        _s.Selection.Set([multi, pal]);
        var group = _s.GroupSelection()!;
        _s.Selection.Set(group);
        Assert.True(_s.CanBreakIntoOuterLoops);
        _s.BreakIntoOuterLoopsSelection();
        Assert.Equal(3, group.Children.Count);                          // two pieces replaced the path, the rectangle stays
        Assert.DoesNotContain(multi, L.Children);
        Assert.All(group.Children, c => Assert.Same(group, c.Parent));
    }

    [Fact]
    public void The_loop_commands_do_nothing_for_an_empty_selection_and_do_not_touch_other_objects()
    {
        var path = AddPath(Red, FillRule.EvenOdd, Box(0, 0, 300, 300), Reversed(Box(50, 50, 200, 200)), Box(400, 0, 50, 50));
        var untouched = AddPath(Red, FillRule.EvenOdd, Box(0, 500, 300, 300), Reversed(Box(50, 550, 200, 200)));
        int steps = _s.History.UndoList.Count;
        _s.CloseAllLoops(); _s.RemoveAllInnerLoops(); _s.BreakIntoOuterLoopsSelection();
        Assert.Equal(steps, _s.History.UndoList.Count);

        _s.Selection.Set(path);
        _s.RemoveAllInnerLoops();
        Assert.Equal(2, LoopsOf(path).Count);
        Assert.Equal(2, LoopsOf(untouched).Count);                      // not selected: still has its hole
    }
}
