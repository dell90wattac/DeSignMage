using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Small vectors: two node sizes in Vector Edit, a box that deletes small nodes, and the threshold setting.</summary>
public class SmallNodeTests : IDisposable
{
    private readonly EditorSession _s;
    private Layer L => _s.Document.ActiveLayer;
    private VectorEditTool Tool => _s.GetTool<VectorEditTool>();

    public SmallNodeTests()
    {
        SmallVectors.ThresholdPoints = SmallVectors.DefaultPoints;
        _s = new EditorSession(NewDoc());
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
    }

    public void Dispose() => SmallVectors.ThresholdPoints = SmallVectors.DefaultPoints;

    /// <summary>
    /// A 400 x 400 square whose top edge carries tracing noise: the nodes at x = 100, 104 and 108 are only 4 points apart, so all three touch a
    /// small vector (the default threshold is 18 points). Local nodes: (0,0) (100,0) (104,0) (108,0) (200,0) (400,0) (400,400) (0,400).
    /// </summary>
    private PathObject AddNoisySquare(Affine2? transform = null)
    {
        var loop = new Loop { Start = new Vec2(0, 0), Closed = true };
        foreach (var p in new[] { (100.0, 0.0), (104, 0), (108, 0), (200, 0), (400, 0), (400, 400), (0, 400) })
            loop.Segments.Add(Segment.Line(new Vec2(p.Item1, p.Item2)));
        loop.Segments.Add(Segment.Line(new Vec2(0, 0)));
        var path = new PathObject(PathEditing.FromLoops([loop], FillRule.NonZero)) { Transform = transform ?? Affine2.Translation(200, 200), Style = ObjectStyle.FillOnly(DocColor.Black) };
        _s.History.Execute(new AddObjectCommand(L, path));
        _s.EditVectors(path);
        return path;
    }

    private PointerInput At(double x, double y, PointerButton b = PointerButton.None, ModifierKeys m = ModifierKeys.None) =>
        new(new Vec2(x, y), new Vec2(x, y), b, m);

    private void Box(double x0, double y0, double x1, double y1, ModifierKeys m = ModifierKeys.None)
    {
        _s.PointerDown(At(x0, y0, PointerButton.Left, m));
        _s.PointerMove(At((x0 + x1) / 2, (y0 + y1) / 2, PointerButton.None, m));
        _s.PointerMove(At(x1, y1, PointerButton.None, m));
        _s.PointerUp(At(x1, y1, PointerButton.Left, m));
    }

    private VectorEditOverlay Overlay() => Assert.Single(_s.BuildOverlay().OfType<VectorEditOverlay>());

    private static List<Loop> LoopsOf(DocObject o) => PathEditing.ToLoops(o.GetLocalGeometry()!);

    // ---- Two node sizes ----

    [Fact]
    public void Nodes_that_start_or_end_a_small_vector_are_marked_small_and_the_rest_big()
    {
        AddNoisySquare();
        var marks = Overlay().Nodes;
        Assert.Equal(8, marks.Count);
        // Local x = 100, 104, 108 are small; world = local + 200.
        var small = marks.Where(m => m.Small).Select(m => m.Position.X).OrderBy(x => x).ToList();
        Assert.Equal([300.0, 304.0, 308.0], small);
        Assert.Equal(5, marks.Count(m => !m.Small));
    }

    [Fact]
    public void Small_is_measured_on_the_page_so_a_big_scale_means_nothing_is_small()
    {
        AddNoisySquare(Affine2.Scale(10, 10));                                   // 4 points become 40 on the page
        Assert.DoesNotContain(Overlay().Nodes, m => m.Small);
    }

    [Fact]
    public void A_shrunk_object_has_small_nodes_where_a_full_size_one_did_not()
    {
        AddNoisySquare(Affine2.Scale(0.05, 0.05) * Affine2.Translation(200, 200));   // the whole square is 20 points wide
        Assert.True(Overlay().Nodes.Count(m => m.Small) >= 6);
    }

    [Fact]
    public void The_marks_follow_the_vectors_while_a_node_is_dragged()
    {
        AddNoisySquare();
        // Drag the node at local 200 left next to the noise: the vector 108 -> 200 shrinks below the threshold, so the nodes touching it become small.
        _s.PointerDown(At(400, 200, PointerButton.Left));
        _s.PointerMove(At(380, 200));
        _s.PointerMove(At(318, 200));
        var live = Overlay().Nodes.Where(m => m.Small).Select(m => m.Position.X).OrderBy(x => x).ToList();
        Assert.Contains(318.0, live);
        _s.PointerUp(At(318, 200, PointerButton.Left));
        Assert.Contains(Overlay().Nodes, m => m.Small && m.Position.X == 318);
    }

    // ---- The delete box ----

    [Fact]
    public void A_box_over_small_nodes_deletes_them_as_one_undo_step_and_the_shape_stays_closed()
    {
        var path = AddNoisySquare();
        var before = path.GetWorldBounds();
        int steps = _s.History.UndoList.Count;

        Box(290, 150, 320, 250);                                                  // covers the three small nodes only
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Delete Small Vectors", _s.History.UndoDescription);
        var loop = Assert.Single(LoopsOf(path));
        Assert.True(loop.Closed);
        Assert.Equal(5, loop.NodeCount);                                          // 8 - 3
        Assert.Equal(loop.Start, loop.Segments[^1].End);
        Assert.Equal(before, path.GetWorldBounds());                              // the outline is the same square
        Assert.Equal(new[] { 0.0, 200, 400 }, Enumerable.Range(0, loop.NodeCount).Select(loop.Node).Where(n => n.Y == 0).Select(n => n.X).OrderBy(x => x));
        Assert.Equal(0, Tool.SelectedNodeCount);

        _s.Undo();
        Assert.Equal(8, Assert.Single(LoopsOf(path)).NodeCount);
        _s.Redo();
        Assert.Equal(5, Assert.Single(LoopsOf(path)).NodeCount);
    }

    [Fact]
    public void Big_nodes_in_the_same_box_are_left_alone_neither_deleted_nor_selected()
    {
        var path = AddNoisySquare();
        Box(290, 150, 420, 250);                                                  // the three small nodes and the big one at local x = 200
        var loop = Assert.Single(LoopsOf(path));
        Assert.Equal(5, loop.NodeCount);                                          // only the small ones went
        Assert.Contains(Enumerable.Range(0, loop.NodeCount).Select(loop.Node), n => n == new Vec2(200, 0));   // the big node is still there
        Assert.Equal(0, Tool.SelectedNodeCount);                                  // and a box never selects
    }

    [Fact]
    public void A_box_over_big_nodes_only_does_nothing_at_all()
    {
        var path = AddNoisySquare();
        int steps = _s.History.UndoList.Count;
        Box(580, 180, 620, 220);                                                  // the corner at local (400, 0)
        Assert.Equal(steps, _s.History.UndoList.Count);                           // no undo step
        Assert.Equal(0, Tool.SelectedNodeCount);
        Assert.Equal(8, Assert.Single(LoopsOf(path)).NodeCount);
    }

    [Fact]
    public void Clicking_and_shift_clicking_still_select_nodes()
    {
        AddNoisySquare();
        _s.PointerDown(At(600, 200, PointerButton.Left)); _s.PointerUp(At(600, 200, PointerButton.Left));
        _s.PointerDown(At(600, 600, PointerButton.Left, ModifierKeys.Shift)); _s.PointerUp(At(600, 600, PointerButton.Left, ModifierKeys.Shift));
        Assert.Equal(2, Tool.SelectedNodeCount);
    }

    [Fact]
    public void With_the_delete_switch_off_a_box_does_nothing()
    {
        var path = AddNoisySquare();
        Tool.BoxDeletesSmallNodes = false;
        int steps = _s.History.UndoList.Count;
        Box(290, 150, 320, 250);
        Assert.Equal(steps, _s.History.UndoList.Count);
        Assert.Equal(0, Tool.SelectedNodeCount);
        Assert.Equal(8, Assert.Single(LoopsOf(path)).NodeCount);
    }
    [Fact]
    public void The_box_is_drawn_red_while_it_holds_small_nodes_and_plain_otherwise()
    {
        AddNoisySquare();
        _s.PointerDown(At(290, 150, PointerButton.Left));
        _s.PointerMove(At(310, 200));
        _s.PointerMove(At(320, 250));
        Assert.Equal(MarqueeKind.Delete, Assert.Single(_s.BuildOverlay().OfType<MarqueeOverlay>()).Kind);
        Assert.Equal(3, Tool.SmallNodeCountIn(new Rect2(290, 150, 320, 250)));
        _s.PointerMove(At(295, 160));                                             // shrunk so it holds nothing
        Assert.Equal(MarqueeKind.Select, Assert.Single(_s.BuildOverlay().OfType<MarqueeOverlay>()).Kind);
        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));             // Esc cancels the box
        _s.PointerUp(At(295, 160, PointerButton.Left));
        Assert.Empty(_s.BuildOverlay().OfType<MarqueeOverlay>());
        Assert.Equal(8, Assert.Single(LoopsOf((PathObject)Tool.Target!)).NodeCount);
    }

    [Fact]
    public void Deleting_every_node_of_a_tiny_loop_removes_the_loop_and_leaves_the_rest()
    {
        // A dot made of four tiny vectors beside a normal square: a box over the dot removes it and leaves the square.
        var dot = PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(0, 0, 6, 6)))[0];
        var square = PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(100, 0, 300, 300)))[0];
        var path = new PathObject(PathEditing.FromLoops([dot, square], FillRule.EvenOdd)) { Transform = Affine2.Translation(200, 200), Style = ObjectStyle.FillOnly(DocColor.Black) };
        _s.History.Execute(new AddObjectCommand(L, path));
        _s.EditVectors(path);
        Box(190, 190, 215, 215);
        var loop = Assert.Single(LoopsOf(path));
        Assert.Equal(new Rect2(100, 0, 400, 300), loop.Bounds());
        Assert.Equal("Delete Small Vectors", _s.History.UndoDescription);
    }

    // ---- The threshold ----

    [Fact]
    public void The_Info_Line_counts_the_small_vectors_with_the_threshold_it_uses()
    {
        AddNoisySquare();
        Assert.Contains("2 small vecs (< 0.25in)", Tool.InfoLine());              // the two 4 point vectors (100-104 and 104-108)
    }

    [Fact]
    public void A_smaller_threshold_makes_fewer_nodes_small_and_a_larger_one_more()
    {
        AddNoisySquare();
        Assert.Equal(3, Overlay().Nodes.Count(m => m.Small));

        Assert.Equal(0.02, SmallVectors.SetInches(0.02));                         // 1.44 points: the 4 point vectors are not small any more
        Assert.DoesNotContain(Overlay().Nodes, m => m.Small);
        Assert.Contains("0 small vecs (< 0.02in)", Tool.InfoLine());

        SmallVectors.SetInches(1.5);                                              // 108 points: every vector except the three 400 point sides... and 108-200 (92)
        var small = Overlay().Nodes.Count(m => m.Small);
        Assert.True(small > 3);
        Assert.Contains("(< 1.5in)", Tool.InfoLine());
    }

    [Fact]
    public void The_threshold_is_kept_between_a_hundredth_and_five_inches()
    {
        Assert.Equal(SmallVectors.MinInches, SmallVectors.SetInches(0));
        Assert.Equal(SmallVectors.MaxInches, SmallVectors.SetInches(99));
        Assert.Equal(0.25, SmallVectors.SetInches(double.NaN));
        Assert.Equal(Units.FromInches(0.25), SmallVectors.ThresholdPoints, 9);
    }
}
