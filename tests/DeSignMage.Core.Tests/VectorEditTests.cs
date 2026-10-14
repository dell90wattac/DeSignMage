using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Arcs, the editable loop model and its operations.</summary>
public class PathEditingTests
{
    private static Loop Square(double s = 100) =>
        PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(0, 0, s, s)))[0];

    [Fact]
    public void Arc_bounds_and_points_are_on_the_circle()
    {
        var mid = new Vec2(100 * Math.Cos(Math.PI / 4), 100 * Math.Sin(Math.PI / 4));
        var g = new PathBuilder().MoveTo(100, 0).ArcTo(mid, new Vec2(0, 100)).Build();
        var b = g.GetBounds();
        Assert.Equal(0, b.Left, 3); Assert.Equal(100, b.Right, 3);
        Assert.Equal(0, b.Top, 3); Assert.Equal(100, b.Bottom, 3);
        foreach (var p in g.Flatten(0.05)[0].Points) Assert.InRange(p.Length, 99.9, 100.1);
        Assert.Equal(100, Arcs.PointAt(new Vec2(100, 0), mid, new Vec2(0, 100), 0.5).Length, 6);
    }

    [Fact]
    public void Arcs_survive_similarity_transforms_but_become_curves_under_stretch()
    {
        var g = new PathBuilder().MoveTo(100, 0).ArcTo(new Vec2(70.71, 70.71), new Vec2(0, 100)).Build();
        Assert.True(g.Transform(Affine2.Rotation(0.3) * Affine2.Scale(2, 2) * Affine2.Translation(5, 5)).HasArcs);
        Assert.False(g.Transform(Affine2.Scale(2, 1)).HasArcs);
    }

    [Fact]
    public void Loops_round_trip_through_geometry()
    {
        var g = PathGeometry.Ellipse(Rect2.FromXYWH(0, 0, 50, 30));
        var back = PathEditing.FromLoops(PathEditing.ToLoops(g), g.FillRule);
        Assert.Equal(g.GetBounds(), back.GetBounds());
        var l = Square();
        Assert.True(l.Closed);
        Assert.Equal(4, l.NodeCount);
        Assert.Equal(l.Start, l.Segments[^1].End);
    }

    [Fact]
    public void Corners_and_smooth_nodes()
    {
        Assert.All(Enumerable.Range(0, 4), k => Assert.True(Square().IsCorner(k)));
        var ellipse = PathEditing.ToLoops(PathGeometry.Ellipse(Rect2.FromXYWH(0, 0, 50, 30)))[0];
        Assert.All(Enumerable.Range(0, ellipse.NodeCount), k => Assert.False(ellipse.IsCorner(k)));
    }

    [Fact]
    public void Moving_a_node_moves_its_neighbouring_handles_and_keeps_arc_bulge()
    {
        var l = new Loop { Start = new Vec2(0, 0) };
        l.Segments.Add(Segment.Cubic(new Vec2(10, -10), new Vec2(40, -10), new Vec2(50, 0)));
        l.Segments.Add(Segment.Arc(Arcs.ThroughFromBulge(new Vec2(50, 0), new Vec2(100, 0), 0.25), new Vec2(100, 0)));
        PathEditing.MoveNode(l, 1, new Vec2(50, 20));
        Assert.Equal(new Vec2(40, 10), l.Segments[0].C2);           // handle followed the node
        Assert.Equal(new Vec2(10, -10), l.Segments[0].C1);          // far handle untouched
        Assert.Equal(0.25, Arcs.Bulge(new Vec2(50, 20), l.Segments[1].C1, new Vec2(100, 0)), 6);
    }

    [Fact]
    public void Moving_the_start_node_of_a_closed_loop_keeps_it_closed()
    {
        var l = Square();
        PathEditing.MoveNode(l, 0, new Vec2(-10, -10));
        Assert.Equal(new Vec2(-10, -10), l.Start);
        Assert.Equal(l.Start, l.Segments[^1].End);
    }

    [Theory]
    [InlineData(SegmentKind.Line)]
    [InlineData(SegmentKind.Arc)]
    [InlineData(SegmentKind.Cubic)]
    public void Insert_adds_a_node_without_changing_the_shape(SegmentKind kind)
    {
        var l = new Loop { Start = new Vec2(0, 0) };
        l.Segments.Add(kind switch
        {
            SegmentKind.Line => Segment.Line(new Vec2(100, 0)),
            SegmentKind.Arc => Segment.Arc(new Vec2(50, 30), new Vec2(100, 0)),
            _ => Segment.Cubic(new Vec2(20, 60), new Vec2(80, 60), new Vec2(100, 0)),
        });
        var before = l.PointOn(0, 0.3);
        PathEditing.Insert(l, 0, 0.3);
        Assert.Equal(2, l.Segments.Count);
        Assert.Equal(before.X, l.Node(1).X, 6);
        Assert.Equal(before.Y, l.Node(1).Y, 6);
        Assert.Equal(new Vec2(100, 0), l.Segments[1].End);
        if (kind != SegmentKind.Line)   // still the same curve: a point of the second half lies on the original
        {
            var probe = l.PointOn(1, 0.5);
            var original = new Loop { Start = new Vec2(0, 0), Segments = [kind == SegmentKind.Arc ? Segment.Arc(new Vec2(50, 30), new Vec2(100, 0)) : Segment.Cubic(new Vec2(20, 60), new Vec2(80, 60), new Vec2(100, 0))] };
            Assert.True(original.Closest(0, probe).Distance < 0.01);
        }
    }

    [Fact]
    public void Segment_kind_conversions()
    {
        var l = Square();
        PathEditing.ToArc(l, 0);
        Assert.Equal(SegmentKind.Arc, l.Segments[0].Kind);
        Assert.NotEqual(0, Arcs.Bulge(l.Start, l.Segments[0].C1, l.Segments[0].End));
        PathEditing.ToBezier(l, 0);
        Assert.Equal(SegmentKind.Cubic, l.Segments[0].Kind);
        PathEditing.ToLine(l, 0);
        Assert.Equal(SegmentKind.Line, l.Segments[0].Kind);
        Assert.Equal(new Vec2(100, 0), l.Segments[0].End);
    }

    [Fact]
    public void Break_keeps_the_geometry_but_opens_the_loop_and_join_closes_it()
    {
        var loops = new List<Loop> { Square() };
        Assert.True(PathEditing.BreakAt(loops, 0, 2));
        var l = loops[0];
        Assert.False(l.Closed);
        Assert.Equal(new Vec2(100, 100), l.Start);                 // broken at node 2
        Assert.Equal(new Vec2(100, 100), l.Segments[^1].End);      // ends meet: nothing removed
        Assert.Equal(4, l.Segments.Count);
        Assert.True(PathEditing.Join(l));
        Assert.True(l.Closed);
        Assert.Equal(4, l.Segments.Count);
    }

    [Fact]
    public void Set_start_rotates_a_closed_loop()
    {
        var l = Square();
        Assert.True(PathEditing.SetStart(l, 2));
        Assert.Equal(new Vec2(100, 100), l.Start);
        Assert.Equal(new Vec2(100, 100), l.Segments[^1].End);
    }

    [Fact]
    public void Delete_node_merges_vectors_and_delete_segment_opens_the_loop()
    {
        var loops = new List<Loop> { Square() };
        PathEditing.DeleteNode(loops, 0, 1);   // remove (100,0): triangle
        Assert.Equal(3, loops[0].NodeCount);
        Assert.True(loops[0].Closed);

        loops = [Square()];
        PathEditing.DeleteSegment(loops, 0, 0);   // remove the top vector
        Assert.False(loops[0].Closed);
        Assert.Equal(3, loops[0].Segments.Count);
        Assert.Equal(new Vec2(100, 0), loops[0].Start);
        Assert.Equal(new Vec2(0, 0), loops[0].Segments[^1].End);
    }

    [Fact]
    public void Classify_border_interior_and_open_loops()
    {
        var outer = Square(100);
        var inner = PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(25, 25, 50, 50)))[0];
        var open = Square(10);
        open.Closed = false;
        Assert.Equal([LoopKind.Border, LoopKind.Interior, LoopKind.Open], PathEditing.Classify([outer, inner, open]));
    }
}

/// <summary>The vector tool driven like the UI does.</summary>
public class VectorToolTests
{
    private readonly EditorSession _s;
    private VectorEditTool Tool => _s.GetTool<VectorEditTool>();
    private Layer L => _s.Document.ActiveLayer;

    public VectorToolTests()
    {
        _s = new EditorSession(NewDoc());
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
    }

    private RectangleObject AddRect(double x, double y, double w = 100, double h = 100, string color = "#000000")
    {
        var r = Rect(x, y, w, h, ObjectStyle.FillOnly(DocColor.FromHex(color)));
        _s.History.Execute(new AddObjectCommand(L, r));
        return r;
    }

    private PointerInput At(double x, double y, PointerButton b = PointerButton.None, ModifierKeys m = ModifierKeys.None, int clicks = 1) =>
        new(new Vec2(x, y), new Vec2(x, y), b, m, clicks);

    private void DoubleClick(double x, double y)
    {
        _s.PointerDown(At(x, y, PointerButton.Left));
        _s.PointerUp(At(x, y, PointerButton.Left));
        _s.PointerDown(At(x, y, PointerButton.Left, clicks: 2));
        _s.PointerUp(At(x, y, PointerButton.Left, clicks: 2));
    }

    private void Drag(double x0, double y0, double x1, double y1, ModifierKeys m = ModifierKeys.None)
    {
        _s.PointerDown(At(x0, y0, PointerButton.Left, m));
        _s.PointerMove(At((x0 + x1) / 2, (y0 + y1) / 2, PointerButton.None, m));
        _s.PointerUp(At(x1, y1, PointerButton.Left, m));
    }

    private VectorEditOverlay Overlay() => Assert.Single(_s.BuildOverlay().OfType<VectorEditOverlay>());

    [Fact]
    public void Double_click_opens_vectors_with_corner_nodes_in_blue()
    {
        AddRect(100, 100);
        DoubleClick(150, 150);
        Assert.Equal("vector", _s.ActiveTool.Id);
        var o = Overlay();
        Assert.Equal(4, o.Nodes.Count);
        Assert.All(o.Nodes, n => Assert.True(n.Corner));
        Assert.Equal(LoopKind.Border, Assert.Single(o.Loops).Kind);
    }

    [Fact]
    public void Dragging_a_node_reshapes_the_object_and_undo_restores_the_rectangle()
    {
        var r = AddRect(100, 100);
        DoubleClick(150, 150);
        Drag(200, 200, 260, 230);   // bottom-right corner
        var path = Assert.IsType<PathObject>(Assert.Single(L.Children));
        Assert.Equal(new Rect2(100, 100, 260, 230), path.GetWorldBounds());
        Assert.Equal(r.Style, path.Style);
        Assert.Same(path, Tool.Target);
        Assert.Equal("Move Nodes", _s.History.UndoDescription);

        _s.Undo();
        Assert.Same(r, Assert.Single(L.Children));
        Assert.Same(r, Tool.Target);   // the vector view follows the undo
        _s.Redo();
        Assert.Same(path, Tool.Target);
    }

    [Fact]
    public void Objects_do_not_move_in_vector_mode()
    {
        var r = AddRect(100, 100);
        DoubleClick(150, 150);
        Drag(150, 150, 300, 300);   // drag inside the fill: a box, not a move
        Assert.Equal(new Rect2(100, 100, 200, 200), Assert.Single(L.Children).GetWorldBounds());
        Assert.Equal(0, Tool.SelectedNodeCount);   // a box never selects nodes
    }

    [Fact]
    public void Shift_clicked_nodes_move_together_and_a_box_does_not_select_them()
    {
        AddRect(100, 100);
        DoubleClick(150, 150);
        Drag(180, 50, 260, 250);    // a box over the two right-hand (big) nodes: nothing is selected, nothing is deleted
        Assert.Equal(0, Tool.SelectedNodeCount);
        Assert.Equal(new Rect2(100, 100, 200, 200), Assert.Single(L.Children).GetWorldBounds());
        _s.PointerDown(At(200, 100, PointerButton.Left)); _s.PointerUp(At(200, 100, PointerButton.Left));
        _s.PointerDown(At(200, 200, PointerButton.Left, ModifierKeys.Shift)); _s.PointerUp(At(200, 200, PointerButton.Left, ModifierKeys.Shift));
        Assert.Equal(2, Tool.SelectedNodeCount);
        Drag(200, 100, 250, 100);   // drag one of them: both move
        Assert.Equal(new Rect2(100, 100, 250, 200), Assert.Single(L.Children).GetWorldBounds());
    }

    [Fact]
    public void Clicking_another_object_opens_its_vectors()
    {
        AddRect(100, 100);
        var b = AddRect(400, 100);
        DoubleClick(150, 150);
        _s.PointerDown(At(450, 150, PointerButton.Left));
        _s.PointerUp(At(450, 150, PointerButton.Left));
        Assert.Same(b, Tool.Target);
        Assert.Equal("vector", _s.ActiveTool.Id);
    }

    [Fact]
    public void Escape_stays_in_vector_mode_and_the_layout_tool_leaves_it()
    {
        AddRect(100, 100);
        DoubleClick(150, 150);
        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));
        Assert.Equal("vector", _s.ActiveTool.Id);
        _s.SetTool("select");
        Assert.Null(Tool.Target);
        Assert.Empty(_s.BuildOverlay().OfType<VectorEditOverlay>());
    }

    [Fact]
    public void Double_click_on_a_vector_asks_for_the_vector_menu_and_insert_adds_a_node_there()
    {
        AddRect(100, 100);
        DoubleClick(150, 150);
        ContextMenuRequest? req = null;
        _s.ContextMenuRequested += (_, r) => req = r;
        DoubleClick(130, 100);   // on the top vector
        Assert.Equal(MenuKind.Vector, req!.Kind);
        Assert.True(Tool.CanInsert);
        Assert.False(Tool.CanToLine);   // already a line
        Tool.Insert();
        Assert.Equal(5, Overlay().Nodes.Count);
        Assert.Contains(Overlay().Nodes, n => Vec2.Distance(n.Position, new Vec2(130, 100)) < 0.5);
        Assert.Equal("Insert Node", _s.History.UndoDescription);
    }

    [Fact]
    public void Letter_operations_act_on_the_vector_under_the_cursor()
    {
        AddRect(100, 100);
        DoubleClick(150, 150);
        _s.PointerMove(At(150, 100));   // hover the top vector
        Tool.ToArc();
        var path = Assert.IsType<PathObject>(Assert.Single(L.Children));
        Assert.Equal(PathVerb.ArcTo, path.Geometry.Verbs[1]);
        Assert.True(path.GetWorldBounds().Top < 100);   // the arc bulges outward
        _s.Undo();
        Assert.IsType<RectangleObject>(Assert.Single(L.Children));
    }

    [Fact]
    public void Break_loop_turns_the_loop_black_without_changing_the_shape()
    {
        AddRect(100, 100);
        DoubleClick(150, 150);
        _s.PointerMove(At(150, 100));
        Tool.BreakLoop();
        Assert.Equal(LoopKind.Open, Assert.Single(Overlay().Loops).Kind);
        Assert.True(Tool.CanJoinStartEnd);
        var obj = Assert.Single(L.Children);
        Assert.Equal(new Rect2(100, 100, 200, 200), obj.GetWorldBounds());
        Assert.Same(obj, HitTesting.HitTest(_s.Document, new Vec2(150, 150), 1));   // still fills
        Tool.JoinStartEnd();
        Assert.Equal(LoopKind.Border, Assert.Single(Overlay().Loops).Kind);
    }

    [Fact]
    public void Delete_key_removes_selected_nodes_not_the_object()
    {
        AddRect(100, 100);
        DoubleClick(150, 150);
        _s.PointerDown(At(200, 100, PointerButton.Left));   // select the top-right node
        _s.PointerUp(At(200, 100, PointerButton.Left));
        Assert.True(_s.KeyDown(EditorKey.Delete, ModifierKeys.None));
        var obj = Assert.Single(L.Children);
        Assert.Equal(3, Overlay().Nodes.Count);
        Assert.True(obj.GetWorldBounds().Right <= 200);
    }

    [Fact]
    public void Editing_a_combined_object_changes_only_that_part()
    {
        var a = AddRect(100, 100, color: "#FF0000");
        var b = AddRect(300, 100, color: "#0000FF");
        _s.Selection.Set([a, b]);
        var combined = _s.CombineSelection()!;
        DoubleClick(150, 150);
        Assert.Same(combined, Tool.Target);
        Assert.Equal(8, Overlay().Nodes.Count);

        Drag(400, 200, 420, 260);   // bottom-right of the blue part
        Assert.Same(combined, Assert.Single(L.Children));
        Assert.IsType<RectangleObject>(combined.Parts[0]);
        var blue = Assert.IsType<PathObject>(combined.Parts[1]);
        Assert.Equal(DocColor.FromHex("#0000FF"), ((SolidPaint)blue.Style.Fill!).Color);
        Assert.Equal(260, combined.GetWorldBounds().Bottom, 6);
        _s.Undo();
        Assert.IsType<RectangleObject>(combined.Parts[1]);
    }

    [Fact]
    public void Interior_loops_of_a_combined_object_are_red()
    {
        var outer = AddRect(100, 100, 200, 200);
        var inner = AddRect(150, 150, 100, 100);
        _s.Selection.Set([outer, inner]);
        _s.CombineSelection();
        DoubleClick(120, 120);
        Assert.Equal([LoopKind.Border, LoopKind.Interior], Overlay().Loops.Select(l => l.Kind));
    }

    [Fact]
    public void Fill_colors_toggle_is_display_only()
    {
        var r = AddRect(100, 100);
        int changes = 0;
        _s.DisplayChanged += (_, _) => changes++;
        _s.ShowFillColors = false;
        Assert.Equal(1, changes);
        Assert.False(_s.History.CanUndo && _s.History.UndoDescription != "Add Rectangle");
        // Tools keep working: click selects, drag moves.
        _s.PointerDown(At(150, 150, PointerButton.Left));
        _s.PointerMove(At(170, 150));
        _s.PointerUp(At(170, 150, PointerButton.Left));
        Assert.Equal(120, r.GetWorldBounds().Left, 9);
    }

    [Fact]
    public void Text_and_groups_are_not_vector_editable()
    {
        Assert.False(OutlineView.IsEditable(new TextObject("A", "Arial", 10)));
        Assert.False(OutlineView.IsEditable(new GroupObject()));
        Assert.True(OutlineView.IsEditable(new EllipseObject(10, 10)));
    }
}
