using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Node operations on loops (C.1): smooth/corner/symmetric, handle constraints, moving lines, joining, loop helpers.</summary>
public class NodeOperationTests
{
    private static Loop Square(double s = 100) =>
        PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(0, 0, s, s)))[0];

    /// <summary>Three cubics; node 1 at (100,0) is smooth (handles along (0.6,0.8), lengths 50 and 75), node 0 is a corner.</summary>
    private static Loop Blob() => new()
    {
        Start = new Vec2(0, 0),
        Segments =
        [
            Segment.Cubic(new Vec2(30, -40), new Vec2(70, -40), new Vec2(100, 0)),
            Segment.Cubic(new Vec2(145, 60), new Vec2(90, 120), new Vec2(50, 80)),
            Segment.Cubic(new Vec2(10, 40), new Vec2(-30, -40), new Vec2(0, 0)),
        ],
        Closed = true,
    };

    private static double Cross(Vec2 a, Vec2 b) => Vec2.Cross(a, b);

    // ---- Node kinds ----

    [Fact]
    public void Node_kinds_are_derived_from_the_vectors()
    {
        var b = Blob();
        Assert.Equal(NodeKind.Corner, PathEditing.KindOf(b, 0));
        Assert.Equal(NodeKind.Smooth, PathEditing.KindOf(b, 1));
        Assert.Equal(NodeKind.End, PathEditing.KindOf(new Loop { Start = default, Segments = [Segment.Line(new Vec2(5, 5))] }, 0));
        Assert.Equal(NodeKind.Corner, PathEditing.KindOf(Square(), 2));
    }

    [Fact]
    public void Make_smooth_turns_a_line_corner_into_a_tangent_continuous_curve_and_keeps_the_nodes()
    {
        var l = Square();
        Assert.True(PathEditing.MakeSmooth(l, 1));
        Assert.Equal(SegmentKind.Cubic, l.Segments[0].Kind);
        Assert.Equal(SegmentKind.Cubic, l.Segments[1].Kind);
        Assert.False(l.IsCorner(1));
        Assert.Equal(new Vec2(100, 0), l.Node(1));
        Assert.Equal(new Vec2(0, 0), l.Node(0));            // nothing else moved
        Assert.Equal(SegmentKind.Line, l.Segments[2].Kind);
        Assert.Equal(NodeKind.Symmetric, PathEditing.KindOf(l, 1));   // both neighbours were equally long lines
    }

    [Fact]
    public void Make_smooth_on_an_already_smooth_node_keeps_its_direction()
    {
        var l = Blob();
        var before = l.Segments[0].C2;
        PathEditing.MakeSmooth(l, 1);
        Assert.Equal(before.X, l.Segments[0].C2.X, 6);
        Assert.Equal(before.Y, l.Segments[0].C2.Y, 6);
    }

    [Fact]
    public void Make_corner_pulls_the_handles_in_and_make_symmetric_equalizes_them()
    {
        var l = Blob();
        Assert.True(PathEditing.MakeCorner(l, 1));
        Assert.Equal(l.Node(1), l.Segments[0].C2);
        Assert.Equal(l.Node(1), l.Segments[1].C1);
        Assert.Equal(NodeKind.Corner, PathEditing.KindOf(l, 1));
        Assert.False(PathEditing.MakeCorner(l, 1));   // already done

        l = Blob();
        l.Segments[1] = l.Segments[1] with { C1 = new Vec2(100, 0) + new Vec2(60, 80) };   // out handle twice as long
        Assert.True(PathEditing.MakeSymmetric(l, 1));
        double inLen = (l.Node(1) - l.Segments[0].C2).Length, outLen = (l.Segments[1].C1 - l.Node(1)).Length;
        Assert.Equal(inLen, outLen, 6);
        Assert.Equal(0, Cross(l.Node(1) - l.Segments[0].C2, l.Segments[1].C1 - l.Node(1)), 6);
        Assert.Equal(NodeKind.Symmetric, PathEditing.KindOf(l, 1));
    }

    // ---- Handle drags ----

    [Fact]
    public void Dragging_a_handle_of_a_smooth_node_keeps_the_node_smooth_and_the_other_handle_length()
    {
        var l = Blob();
        PathEditing.MoveHandle(l, 1, 1, new Vec2(130, 100), HandleMode.Auto);
        var node = l.Node(1);
        Assert.Equal(new Vec2(130, 100), l.Segments[1].C1);
        var inHandle = l.Segments[0].C2;
        Assert.Equal(0, Cross(node - inHandle, l.Segments[1].C1 - node), 6);   // still one line through the node
        Assert.Equal(50, (node - inHandle).Length, 6);                          // its own length (|(-30,-40)|)
        Assert.False(l.IsCorner(1));
    }

    [Fact]
    public void Dragging_a_handle_of_a_corner_node_moves_only_that_handle()
    {
        var l = Blob();
        var before = l.Segments[2].C2;
        PathEditing.MoveHandle(l, 0, 1, new Vec2(40, -10), HandleMode.Auto);   // node 0 is a corner
        Assert.Equal(before, l.Segments[2].C2);
    }

    [Fact]
    public void Free_and_symmetric_handle_modes()
    {
        var free = Blob();
        PathEditing.MoveHandle(free, 1, 1, new Vec2(130, 100), HandleMode.Free);
        Assert.Equal(new Vec2(70, -40), free.Segments[0].C2);   // untouched

        var sym = Blob();
        PathEditing.MoveHandle(sym, 1, 1, new Vec2(130, 100), HandleMode.Symmetric);
        Assert.Equal(new Vec2(70, -100), sym.Segments[0].C2);   // node - (handle - node)
    }

    [Fact]
    public void Arc_handle_is_its_midpoint()
    {
        var l = new Loop { Start = new Vec2(0, 0), Segments = [Segment.Arc(new Vec2(50, 30), new Vec2(100, 0))] };
        PathEditing.MoveHandle(l, 0, 1, new Vec2(50, 60));
        Assert.Equal(60, Arcs.PointAt(l.Start, l.Segments[0].C1, l.Segments[0].End, 0.5).Y, 6);
    }

    // ---- Lines, arcs, corners ----

    [Fact]
    public void Move_line_translates_both_nodes_and_stretches_the_neighbours()
    {
        var l = Square();
        PathEditing.MoveLine(l, 0, new Vec2(0, -20));   // the top vector
        Assert.Equal(new Vec2(0, -20), l.Node(0));
        Assert.Equal(new Vec2(100, -20), l.Node(1));
        Assert.Equal(new Vec2(100, 100), l.Node(2));
        Assert.Equal(new Vec2(0, 100), l.Node(3));
        Assert.Equal(l.Start, l.Segments[^1].End);
    }

    [Fact]
    public void Bend_line_makes_an_arc_through_the_point()
    {
        var l = Square();
        PathEditing.BendLine(l, 0, new Vec2(50, -20));
        Assert.Equal(SegmentKind.Arc, l.Segments[0].Kind);
        Assert.Equal(-20, Arcs.PointAt(l.Start, l.Segments[0].C1, l.Segments[0].End, 0.5).Y, 6);
    }

    [Fact]
    public void Make_sharp_replaces_a_fillet_arc_with_two_lines_meeting_at_the_corner()
    {
        // A square corner at (100,0) rounded by an arc from (80,0) to (100,20).
        var l = new Loop
        {
            Start = new Vec2(0, 0),
            Segments =
            [
                Segment.Line(new Vec2(80, 0)),
                Segment.Arc(new Vec2(80 + 20 * Math.Sin(Math.PI / 4), 20 - 20 * Math.Cos(Math.PI / 4)), new Vec2(100, 20)),
                Segment.Line(new Vec2(100, 100)),
                Segment.Line(new Vec2(0, 100)),
                Segment.Line(new Vec2(0, 0)),
            ],
            Closed = true,
        };
        Assert.True(PathEditing.MakeSharp(l, 1));
        Assert.Equal(6, l.Segments.Count);
        Assert.Equal(SegmentKind.Line, l.Segments[1].Kind);
        Assert.Equal(100, l.Segments[1].End.X, 6);
        Assert.Equal(0, l.Segments[1].End.Y, 6);
        Assert.Equal(new Vec2(100, 20), l.Segments[2].End);
        Assert.False(PathEditing.MakeSharp(Square(), 0));   // not an arc
    }

    // ---- Join ----

    private static List<Loop> TwoOpen() =>
    [
        new Loop { Start = new Vec2(0, 0), Segments = [Segment.Line(new Vec2(100, 0))] },
        new Loop { Start = new Vec2(110, 0), Segments = [Segment.Line(new Vec2(200, 0))] },
    ];

    [Fact]
    public void Join_merges_two_open_loops_with_a_bridging_vector_when_the_ends_are_apart()
    {
        var loops = TwoOpen();
        int index = PathEditing.JoinLoops(loops, 0, aAtStart: false, 1, bAtStart: true);
        Assert.Equal(0, index);
        var l = Assert.Single(loops);
        Assert.False(l.Closed);
        Assert.Equal(3, l.Segments.Count);
        Assert.Equal(new Vec2(110, 0), l.Segments[1].End);
        Assert.Equal(new Vec2(200, 0), l.Segments[^1].End);
    }

    [Fact]
    public void Join_reverses_a_loop_when_the_wrong_ends_are_joined_and_skips_the_bridge_when_ends_meet()
    {
        var loops = TwoOpen();
        loops[1] = new Loop { Start = new Vec2(200, 0), Segments = [Segment.Line(new Vec2(100, 0))] };   // B runs backwards
        PathEditing.JoinLoops(loops, 0, aAtStart: false, 1, bAtStart: false);
        var l = Assert.Single(loops);
        Assert.Equal(2, l.Segments.Count);   // the ends met at (100,0)
        Assert.Equal(new Vec2(200, 0), l.Segments[^1].End);
    }

    [Fact]
    public void Join_on_one_loop_closes_it_and_closed_loops_cannot_join()
    {
        var loops = new List<Loop> { new() { Start = new Vec2(0, 0), Segments = [Segment.Line(new Vec2(10, 0)), Segment.Line(new Vec2(10, 10))] } };
        Assert.Equal(0, PathEditing.JoinLoops(loops, 0, true, 0, false));
        Assert.True(loops[0].Closed);
        Assert.Equal(-1, PathEditing.JoinLoops([Square(), Square()], 0, true, 1, true));
    }

    // ---- Loop helpers ----

    [Fact]
    public void Direction_length_and_bounds_of_a_loop()
    {
        var l = Square();
        Assert.True(l.IsClockwise);
        Assert.Equal(400, l.Length(), 6);
        PathEditing.Reverse(l);
        Assert.False(l.IsClockwise);
        var half = new Loop { Start = new Vec2(100, 0), Segments = [Segment.Arc(new Vec2(0, 100), new Vec2(-100, 0))], Closed = false };
        Assert.Equal(Math.PI * 100, half.Length(), 3);
    }

    [Fact]
    public void To_circle_keeps_center_and_direction()
    {
        var l = PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(10, 20, 40, 60)))[0];
        bool clockwise = l.IsClockwise;
        PathEditing.ToCircle(l);
        Assert.True(l.Closed);
        Assert.Equal(4, l.Segments.Count);
        Assert.All(l.Segments, s => Assert.Equal(SegmentKind.Arc, s.Kind));
        var b = l.Bounds();
        Assert.Equal(30, b.Center.X, 3);
        Assert.Equal(50, b.Center.Y, 3);
        Assert.Equal(50, b.Width, 1);        // average of 40 and 60 (arc bounds are approximate)
        Assert.Equal(50, b.Height, 1);
        Assert.Equal(clockwise, l.IsClockwise);
    }

    [Fact]
    public void Reorder_makes_outer_loops_clockwise_inner_counter_clockwise_and_inner_first()
    {
        var outer = PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(0, 0, 100, 100)))[0];
        var inner = PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(25, 25, 50, 50)))[0];
        PathEditing.Reverse(outer);   // wrong way round
        var loops = new List<Loop> { outer, inner };
        Assert.True(PathEditing.ReorderLoops(loops));
        Assert.Same(inner, loops[0]);
        Assert.False(inner.IsClockwise);
        Assert.True(outer.IsClockwise);
        Assert.False(PathEditing.ReorderLoops(loops));   // nothing left to do
    }

    [Fact]
    public void Stats_count_kinds_loops_and_small_vectors()
    {
        var loops = new List<Loop>
        {
            Square(100),
            new() { Start = default, Segments = [Segment.Arc(new Vec2(5, 2), new Vec2(10, 0)), Segment.Cubic(new Vec2(12, 2), new Vec2(14, 2), new Vec2(16, 0))] },
        };
        var s = PathEditing.Stats(loops.Select(l => (l, 1.0)), smallLength: 18);
        Assert.Equal(2, s.Loops);
        Assert.Equal(6, s.Segments);
        Assert.Equal(8, s.Vectors);   // each loop's start counts as a vector too
        Assert.Equal((4, 1, 1), (s.Lines, s.Arcs, s.Beziers));
        Assert.Equal(2, s.SmallVectors);
    }
}

/// <summary>The vector tool's node editing, driven like the UI does.</summary>
public class NodeToolTests
{
    private readonly EditorSession _s;
    private VectorEditTool Tool => _s.GetTool<VectorEditTool>();
    private Layer L => _s.Document.ActiveLayer;

    public NodeToolTests()
    {
        _s = new EditorSession(NewDoc());
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
    }

    private PointerInput At(double x, double y, PointerButton b = PointerButton.None, ModifierKeys m = ModifierKeys.None, int clicks = 1) =>
        new(new Vec2(x, y), new Vec2(x, y), b, m, clicks);

    private void Click(double x, double y, ModifierKeys m = ModifierKeys.None)
    {
        _s.PointerDown(At(x, y, PointerButton.Left, m));
        _s.PointerUp(At(x, y, PointerButton.Left, m));
    }

    private void Drag(double x0, double y0, double x1, double y1, ModifierKeys m = ModifierKeys.None, ModifierKeys release = ModifierKeys.None)
    {
        _s.PointerDown(At(x0, y0, PointerButton.Left));
        _s.PointerMove(At((x0 + x1) / 2, (y0 + y1) / 2, PointerButton.None, m));
        _s.PointerMove(At(x1, y1, PointerButton.None, m));
        _s.PointerUp(At(x1, y1, PointerButton.Left, release == ModifierKeys.None ? m : release));
    }

    private PathObject AddPath(PathGeometry g, double x = 200, double y = 200)
    {
        var p = new PathObject(g) { Transform = Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(DocColor.Black) };
        _s.History.Execute(new AddObjectCommand(L, p));
        return p;
    }

    private static PathGeometry Blob() => PathEditing.FromLoops(
    [
        new Loop
        {
            Start = new Vec2(0, 0),
            Segments =
            [
                Segment.Cubic(new Vec2(30, -40), new Vec2(70, -40), new Vec2(100, 0)),
                Segment.Cubic(new Vec2(145, 60), new Vec2(90, 120), new Vec2(50, 80)),
                Segment.Cubic(new Vec2(10, 40), new Vec2(-30, -40), new Vec2(0, 0)),
            ],
            Closed = true,
        },
    ], FillRule.NonZero);

    private static List<Loop> LoopsOf(DocObject o) => PathEditing.ToLoops(o.GetLocalGeometry()!);

    private PathObject Open(PathObject p)
    {
        _s.EditVectors(p);
        Assert.Equal("vector", _s.ActiveTool.Id);
        return p;
    }

    // ---- Selecting nodes, X/Y ----

    [Fact]
    public void Click_selects_a_node_and_the_property_bar_can_move_it_by_typing_X_and_Y()
    {
        var p = Open(AddPath(Blob()));
        Click(300, 200);   // node 1 (100,0) in local space
        Assert.Equal(1, Tool.SelectedNodeCount);
        Assert.Equal(new Vec2(300, 200), Tool.AnchorPosition());
        Assert.Equal(NodeKind.Smooth, Tool.SelectedNodeKind());

        int steps = _s.History.UndoList.Count;
        Tool.SetAnchorPosition(new Vec2(320, 230));
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal(new Vec2(120, 30), LoopsOf(p)[0].Node(1));
        Assert.Equal(new Vec2(320, 230), Tool.AnchorPosition());   // still selected
        _s.Undo();
        Assert.Equal(new Vec2(100, 0), LoopsOf(p)[0].Node(1));
    }

    [Fact]
    public void Shift_click_selects_several_nodes_and_the_anchor_is_the_middle_of_their_box()
    {
        Open(AddPath(Blob()));
        Click(300, 200);
        Click(200, 200, ModifierKeys.Shift);
        Assert.Equal(2, Tool.SelectedNodeCount);
        Assert.Equal(new Vec2(250, 200), Tool.AnchorPosition());
        Click(200, 200, ModifierKeys.Shift);   // Shift on a selected node toggles it off
        Assert.Equal(1, Tool.SelectedNodeCount);
    }

    // ---- Handles ----

    [Fact]
    public void Dragging_a_handle_keeps_a_smooth_node_smooth_and_alt_and_ctrl_change_that()
    {
        var p = Open(AddPath(Blob()));
        Click(300, 200);                       // node 1: shows its handles at world (270,160) and (345,260)
        Drag(345, 260, 330, 300);
        var l = LoopsOf(p)[0];
        Assert.Equal(new Vec2(130, 100), l.Segments[1].C1);
        Assert.Equal(0, Vec2.Cross(l.Node(1) - l.Segments[0].C2, l.Segments[1].C1 - l.Node(1)), 6);
        Assert.Equal("Move Handle", _s.History.UndoDescription);
        _s.Undo();

        Drag(345, 260, 330, 300, ModifierKeys.Alt);   // Alt: only this handle
        Assert.Equal(new Vec2(70, -40), LoopsOf(p)[0].Segments[0].C2);
        _s.Undo();

        Drag(345, 260, 330, 300, ModifierKeys.Control);   // Ctrl: mirror
        Assert.Equal(new Vec2(70, -100), LoopsOf(p)[0].Segments[0].C2);
    }

    [Fact]
    public void A_selected_bezier_shows_both_its_handles_and_an_arc_shows_its_midpoint()
    {
        Open(AddPath(Blob()));
        Click(250, 170);   // the middle of the curve between node 0 and 1, away from the nodes
        Assert.Equal(2, Overlay().Handles.Count);                          // both control points, each with a line to its node
        Assert.All(Overlay().Handles, h => Assert.NotEqual(h.Node, h.Handle));
        Assert.NotNull(Overlay().SelectedVector);
        _s.SetTool("select");

        var arc = AddPath(PathEditing.FromLoops([new Loop { Start = default, Segments = [Segment.Arc(new Vec2(50, -30), new Vec2(100, 0)), Segment.Line(new Vec2(0, 0))], Closed = true }], FillRule.NonZero), 200, 400);
        Open(arc);
        Click(250, 370);   // on the arc
        var handle = Assert.Single(Overlay().Handles);
        Assert.Equal(handle.Node, handle.Handle);   // an arc's handle has no line to a node
        Drag(250, 370, 250, 340);                   // size the arc
        Assert.Equal(60, Math.Abs(LoopsOf(arc)[0].PointOn(0, 0.5).Y), 1);
    }

    // ---- Lines ----

    [Fact]
    public void Dragging_a_line_moves_it_and_ctrl_bends_it_into_an_arc()
    {
        var r = Rect(100, 100, 100, 100);
        _s.History.Execute(new AddObjectCommand(L, r));
        _s.EditVectors(r);
        Drag(150, 100, 150, 80);   // the top edge, 20 up
        var path = Assert.IsType<PathObject>(Assert.Single(L.Children));
        Assert.Equal(new Rect2(100, 80, 200, 200), path.GetWorldBounds());
        Assert.Equal("Move Line", _s.History.UndoDescription);
        _s.Undo();

        Drag(150, 100, 150, 70, ModifierKeys.Control);   // Ctrl: bend into an arc
        var bent = Assert.Single(L.Children);
        Assert.True(bent.GetLocalGeometry()!.HasArcs);
        Assert.Equal("Bend Line", _s.History.UndoDescription);
    }

    [Fact]
    public void Selecting_a_vector_reads_out_its_length_angle_and_tangency()
    {
        var r = Rect(100, 100, 100, 100);
        _s.History.Execute(new AddObjectCommand(L, r));
        _s.EditVectors(r);
        Click(150, 100);
        var read = Tool.Readout();
        Assert.NotNull(read);
        Assert.Equal(SegmentKind.Line, read!.Value.Kind);
        Assert.Equal(100, read.Value.Length, 6);
        Assert.Equal(0, read.Value.Angle!.Value, 6);            // along +x
        Assert.Equal(90, read.Value.Tangency!.Value, 6);        // a square corner to the next vector
        Assert.Null(read.Value.Radius);
    }

    // ---- Node kinds through the tool ----

    [Fact]
    public void Make_smooth_corner_and_symmetric_on_selected_nodes_are_single_undo_steps()
    {
        var r = Rect(100, 100, 100, 100);
        _s.History.Execute(new AddObjectCommand(L, r));
        _s.EditVectors(r);
        Click(200, 100);   // top-right corner
        Assert.Equal(NodeKind.Corner, Tool.SelectedNodeKind());
        Assert.True(Tool.CanMakeSmooth);
        Assert.False(Tool.CanMakeCorner);

        int steps = _s.History.UndoList.Count;
        Tool.MakeSmooth();
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Make Smooth", _s.History.UndoDescription);
        Assert.NotEqual(NodeKind.Corner, Tool.SelectedNodeKind());   // stays selected
        Assert.True(Tool.CanMakeCorner);

        Tool.MakeSymmetric();
        Assert.Equal(NodeKind.Symmetric, Tool.SelectedNodeKind());
        Tool.MakeCorner();
        Assert.Equal(NodeKind.Corner, Tool.SelectedNodeKind());
        _s.Undo(); _s.Undo(); _s.Undo();
        Assert.IsType<RectangleObject>(Assert.Single(L.Children));
    }

    // ---- Join / close / break ----

    private PathObject TwoOpenPaths() => AddPath(PathEditing.FromLoops(
    [
        new Loop { Start = new Vec2(0, 0), Segments = [Segment.Line(new Vec2(100, 0))] },
        new Loop { Start = new Vec2(110, 0), Segments = [Segment.Line(new Vec2(200, 0))] },
    ], FillRule.NonZero));

    [Fact]
    public void Join_start_end_with_two_end_nodes_selected_merges_the_loops()
    {
        var p = Open(TwoOpenPaths());
        Click(300, 200);                          // end of the first loop
        Click(310, 200, ModifierKeys.Shift);      // start of the second
        Assert.True(Tool.CanJoinStartEnd);
        Tool.JoinStartEnd();
        var l = Assert.Single(LoopsOf(Assert.Single(L.Children)));
        Assert.Equal(3, l.Segments.Count);
        Assert.Equal("Join Start/End", _s.History.UndoDescription);
        _s.Undo();
        Assert.Equal(2, LoopsOf(Assert.Single(L.Children)).Count);
    }

    [Fact]
    public void Shift_drag_of_an_end_point_onto_another_end_joins_them()
    {
        Open(TwoOpenPaths());
        _s.PointerDown(At(300, 200, PointerButton.Left));
        _s.PointerMove(At(305, 203));
        _s.PointerMove(At(308, 201, PointerButton.None, ModifierKeys.Shift));
        _s.PointerUp(At(308, 201, PointerButton.Left, ModifierKeys.Shift));   // within reach of (310,200)
        var l = Assert.Single(LoopsOf(Assert.Single(L.Children)));
        Assert.Equal(2, l.Segments.Count);        // ends snapped together, no bridging vector
        Assert.Equal("Join", _s.History.UndoDescription);
    }

    [Fact]
    public void Drag_without_shift_does_not_join()
    {
        Open(TwoOpenPaths());
        Drag(300, 200, 308, 201);
        Assert.Equal(2, LoopsOf(Assert.Single(L.Children)).Count);
    }

    [Fact]
    public void Close_open_loop_and_break_it_again()
    {
        var p = Open(AddPath(PathEditing.FromLoops([new Loop { Start = default, Segments = [Segment.Line(new Vec2(100, 0)), Segment.Line(new Vec2(100, 100))] }], FillRule.NonZero)));
        Click(300, 300);   // the end node
        Tool.JoinStartEnd();   // Close
        Assert.True(LoopsOf(p)[0].Closed);
        Click(300, 200);
        Tool.BreakLoop();
        Assert.False(LoopsOf(p)[0].Closed);
    }

    // ---- Cursor, info line, right-button zoom ----

    [Fact]
    public void Hover_over_a_node_or_a_vector_shows_the_crosshair_arrow_and_nothing_else_the_plain_arrow()
    {
        var r = Rect(100, 100, 100, 100);
        _s.History.Execute(new AddObjectCommand(L, r));
        _s.EditVectors(r);
        _s.PointerMove(At(400, 400));
        Assert.Equal(CursorKind.Arrow, _s.CurrentCursor);
        _s.PointerMove(At(150, 100));   // on the top vector
        Assert.Equal(CursorKind.SelectHit, _s.CurrentCursor);
        _s.PointerMove(At(200, 200));   // on a node
        Assert.Equal(CursorKind.SelectHit, _s.CurrentCursor);
        _s.PointerExited();
        Assert.Equal(CursorKind.Arrow, _s.CurrentCursor);
    }

    [Fact]
    public void Info_line_shows_the_stats_of_the_graphic()
    {
        var r = Rect(100, 100, 100, 100);
        _s.History.Execute(new AddObjectCommand(L, r));
        _s.EditVectors(r);
        var line = Tool.InfoLine();
        Assert.StartsWith("Graphic  5 Vectors  (1.3889, 9.2222)  1.3889 x 1.3889  4 lines  0 arcs  0 beziers  0 small vecs (< 0.25in)", line);
        Tool.ToArc();   // no vector selected or hovered: nothing happens
        Click(150, 100);
        Tool.ToArc();
        Assert.Contains("3 lines  1 arcs  0 beziers", Tool.InfoLine());
    }

    [Fact]
    public void Small_vectors_under_a_quarter_inch_are_counted()
    {
        AddPath(PathEditing.FromLoops([new Loop { Start = default, Segments = [Segment.Line(new Vec2(10, 0)), Segment.Line(new Vec2(100, 0))] }], FillRule.NonZero));
        _s.SetTool("select");
        Open((PathObject)L.Children[0]);
        Assert.Contains("1 small vecs", Tool.InfoLine());   // 10 pt < 18 pt
    }

    [Fact]
    public void Right_button_zoom_keeps_working_inside_the_vector_tool_and_does_not_touch_the_nodes()
    {
        var r = Rect(100, 100, 100, 100);
        _s.History.Execute(new AddObjectCommand(L, r));
        _s.EditVectors(r);
        Click(200, 200);
        double zoom = _s.Viewport.Zoom;
        _s.PointerDown(At(100, 100, PointerButton.Right));
        _s.PointerMove(At(250, 250, PointerButton.None));
        _s.PointerUp(At(250, 250, PointerButton.Right));
        Assert.True(_s.Viewport.Zoom > zoom);
        Assert.Equal(1, Tool.SelectedNodeCount);   // the right-drag was a zoom, not a box select
        Assert.Same(r, Tool.Target);
    }

    // ---- Text ----

    [Fact]
    public void Vector_editing_text_converts_it_to_curves_in_one_step_and_the_first_edit_is_a_second_step()
    {
        var t = new TextObject("Hi", "Arial", 100) { Transform = Affine2.Translation(100, 300) };
        _s.History.Execute(new AddObjectCommand(L, t));
        int before = _s.History.UndoList.Count;

        _s.EditVectors(t);
        var path = Assert.IsType<PathObject>(Assert.Single(L.Children));
        Assert.Equal(before + 1, _s.History.UndoList.Count);
        Assert.Equal("Convert to Curves", _s.History.UndoDescription);
        Assert.Same(path, Tool.Target);

        var pos = LoopsOf(path)[0].Node(0);
        var world = path.GetWorldTransform().Transform(pos);
        Drag(world.X, world.Y, world.X + 20, world.Y);
        Assert.Equal(before + 2, _s.History.UndoList.Count);

        _s.Undo();   // the edit
        Assert.Same(path, Assert.Single(L.Children));
        _s.Undo();   // the conversion: live text again
        Assert.Same(t, Assert.Single(L.Children));
        Assert.Null(Tool.Target);
        _s.Redo();
        Assert.IsType<PathObject>(Assert.Single(L.Children));
    }

    [Fact]
    public void Clicking_text_with_the_vector_tool_opens_it_as_curves()
    {
        var t = new TextObject("H", "Arial", 200) { Transform = Affine2.Translation(100, 400) };
        _s.History.Execute(new AddObjectCommand(L, t));
        _s.SetTool("vector");
        var b = t.GetWorldBounds();
        _s.PointerDown(At(b.Left + 1, b.Center.Y, PointerButton.Left));   // on the left stem of the H
        _s.PointerUp(At(b.Left + 1, b.Center.Y, PointerButton.Left));
        Assert.IsType<PathObject>(Assert.Single(L.Children));
        Assert.NotNull(Tool.Target);
    }

    // ---- Switching tools ----

    [Fact]
    public void Switching_to_the_vector_tool_with_one_object_selected_opens_it_but_never_converts_text()
    {
        var r = Rect(100, 100, 100, 100);
        _s.History.Execute(new AddObjectCommand(L, r));
        _s.Selection.Set(r);
        _s.SetTool("vector");
        Assert.Same(r, Tool.Target);

        _s.SetTool("select");
        var t = new TextObject("Hi", "Arial", 100);
        _s.History.Execute(new AddObjectCommand(L, t));
        _s.Selection.Set(t);
        _s.SetTool("vector");
        Assert.Null(Tool.Target);
        Assert.IsType<TextObject>(L.Children[^1]);
    }

    [Fact]
    public void Opening_another_object_forgets_the_node_selection_of_the_first()
    {
        var a = Rect(100, 100, 100, 100);
        var b = Rect(400, 100, 100, 100);
        _s.History.Execute(new AddObjectCommand(L, a));
        _s.History.Execute(new AddObjectCommand(L, b));
        _s.EditVectors(a);
        Click(200, 200);
        Assert.Equal(1, Tool.SelectedNodeCount);
        Click(450, 150);   // inside the other rectangle
        Assert.Same(b, Tool.Target);
        Assert.Equal(0, Tool.SelectedNodeCount);
        Assert.False(Tool.CanMakeSmooth);
    }

    private VectorEditOverlay Overlay() => Assert.Single(_s.BuildOverlay().OfType<VectorEditOverlay>());
}
