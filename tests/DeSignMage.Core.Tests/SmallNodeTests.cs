using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>
/// Vector Edit's node marks (start, ordinary, coincident, tangent, selected), the box that deletes or (with Shift) selects nodes, and the
/// small-vector count and highlight.
/// </summary>
public class SmallNodeTests : IDisposable
{
    private readonly EditorSession _s;
    private int _beeps;
    private Layer L => _s.Document.ActiveLayer;
    private VectorEditTool Tool => _s.GetTool<VectorEditTool>();

    public SmallNodeTests()
    {
        Reset();
        _s = new EditorSession(NewDoc()) { Beep = () => _beeps++ };
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
    }

    public void Dispose() => Reset();

    private static void Reset()
    {
        SmallVectors.ThresholdPoints = SmallVectors.DefaultPoints;
        VectorEditOptions.MarqueeShiftSelects = true;
        VectorEditOptions.TangencyDegrees = VectorEditOptions.DefaultTangencyDegrees;
        VectorEditOptions.HighlightSmallVectors = false;
    }

    /// <summary>A 400 x 400 square whose top edge has more nodes. Local nodes: (0,0) start, (100,0) (104,0) (108,0) (200,0) (400,0) (400,400) (0,400); world = local + 200.</summary>
    private PathObject AddSquare(bool edit = true)
    {
        var loop = new Loop { Start = new Vec2(0, 0), Closed = true };
        foreach (var p in new[] { (100.0, 0.0), (104, 0), (108, 0), (200, 0), (400, 0), (400, 400), (0, 400) })
            loop.Segments.Add(Segment.Line(new Vec2(p.Item1, p.Item2)));
        loop.Segments.Add(Segment.Line(new Vec2(0, 0)));
        var path = new PathObject(PathEditing.FromLoops([loop], FillRule.NonZero)) { Transform = Affine2.Translation(200, 200), Style = ObjectStyle.FillOnly(DocColor.Black) };
        _s.History.Execute(new AddObjectCommand(L, path));
        if (edit) _s.EditVectors(path);
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

    // ---- Node marks ----

    /// <summary>
    /// Local nodes: 0 (0,0) the start; 1 (200,0) an ordinary corner; 2 (200,0) again, at exactly the same place; 3 (300,100) where two curves
    /// meet along one line (tangent, unless a small error is asked for); 4 (300,200) and 5 (0,200) ordinary corners.
    /// </summary>
    private PathObject AddMarkerShape(double tangentErrorDegrees = 0)
    {
        double a = tangentErrorDegrees * Math.PI / 180;
        var loop = new Loop
        {
            Start = new Vec2(0, 0),
            Closed = true,
            Segments =
            [
                Segment.Line(new Vec2(200, 0)),
                Segment.Line(new Vec2(200, 0)),
                Segment.Cubic(new Vec2(240, 0), new Vec2(270, 100), new Vec2(300, 100)),
                Segment.Cubic(new Vec2(300 + 30 * Math.Cos(a), 100 + 30 * Math.Sin(a)), new Vec2(330, 160), new Vec2(300, 200)),
                Segment.Line(new Vec2(0, 200)),
                Segment.Line(new Vec2(0, 0)),
            ],
        };
        var path = new PathObject(PathEditing.FromLoops([loop], FillRule.NonZero)) { Transform = Affine2.Translation(200, 200), Style = ObjectStyle.FillOnly(DocColor.Black) };
        _s.History.Execute(new AddObjectCommand(L, path));
        _s.EditVectors(path);
        return path;
    }

    [Fact]
    public void Marks_tell_apart_the_start_point_ordinary_corners_coincident_nodes_and_tangent_nodes()
    {
        AddMarkerShape();
        var marks = Overlay().Nodes;
        Assert.Equal(6, marks.Count);
        Assert.True(marks[0].Start);                                              // the loop's start/end point: the big square
        Assert.False(marks[1].Start || marks[1].Coincident || !marks[1].Corner);  // an ordinary node
        Assert.True(marks[2].Coincident);                                         // exactly where node 1 was drawn
        Assert.False(marks[3].Corner);                                            // the two curves leave it along one line: a cross, not a square
        Assert.True(marks[4].Corner && !marks[4].Coincident);
        Assert.DoesNotContain(marks.Skip(1), m => m.Start);                       // only node 0 is a start
        Assert.Single(marks, m => m.Coincident);
    }

    [Fact]
    public void A_node_is_tangent_only_within_the_Tangency_setting()
    {
        AddMarkerShape(tangentErrorDegrees: 3);
        Assert.True(Overlay().Nodes[3].Corner);                                   // 3 degrees off, the setting is 2: a square
        VectorEditOptions.SetTangencyDegrees(4);
        Assert.False(Overlay().Nodes[3].Corner);                                  // now within the setting: a cross
        Assert.Equal(VectorEditOptions.MaxTangencyDegrees, VectorEditOptions.SetTangencyDegrees(900));
        Assert.Equal(0, VectorEditOptions.SetTangencyDegrees(-5));
        Assert.Equal(VectorEditOptions.DefaultTangencyDegrees, VectorEditOptions.SetTangencyDegrees(double.NaN));
    }

    [Fact]
    public void A_selected_node_is_marked_selected_and_marks_do_not_depend_on_vector_length_or_zoom()
    {
        AddSquare();                                                              // nodes only 4 points apart: still ordinary nodes
        _s.PointerDown(At(304, 200, PointerButton.Left)); _s.PointerUp(At(304, 200, PointerButton.Left));
        var marks = Overlay().Nodes;
        Assert.Single(marks, m => m.Selected);
        Assert.DoesNotContain(marks, m => m.Coincident);
        Assert.Single(marks, m => m.Start);
        _s.Viewport.Set(4, new Vec2(-500, -500));
        Assert.Equal(marks.Select(m => (m.Start, m.Corner, m.Coincident)), Overlay().Nodes.Select(m => (m.Start, m.Corner, m.Coincident)));
    }

    // ---- The box ----

    [Fact]
    public void A_plain_box_deletes_every_node_inside_as_one_step_keeps_the_loop_closed_and_leaves_the_start_where_it_was()
    {
        var path = AddSquare();
        var before = path.GetWorldBounds();
        int steps = _s.History.UndoList.Count;

        Box(290, 150, 320, 250);                                                  // nodes at local x = 100, 104, 108
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Delete Vectors", _s.History.UndoDescription);
        var loop = Assert.Single(LoopsOf(path));
        Assert.True(loop.Closed);
        Assert.Equal(5, loop.NodeCount);
        Assert.Equal(new Vec2(0, 0), loop.Start);                                 // the big square did not move
        Assert.Equal(loop.Start, loop.Segments[^1].End);
        Assert.Equal(before, path.GetWorldBounds());
        Assert.Equal(0, _beeps);

        _s.Undo();
        Assert.Equal(8, Assert.Single(LoopsOf(path)).NodeCount);
        _s.Redo();
        Assert.Equal(5, Assert.Single(LoopsOf(path)).NodeCount);
    }

    [Fact]
    public void The_node_after_the_last_deleted_one_becomes_selected()
    {
        AddSquare();
        Box(290, 150, 320, 250);
        Assert.Equal(1, Tool.SelectedNodeCount);
        Assert.Equal(new Vec2(400, 200), Tool.AnchorPosition());                  // local (200,0), the next node along

        _s.Undo();
        Box(290, 150, 420, 250);                                                  // now that node goes too: the following one is selected
        Assert.Equal(new Vec2(600, 200), Tool.AnchorPosition());
    }

    [Fact]
    public void Nothing_depends_on_size_every_ordinary_node_in_the_box_goes()
    {
        var path = AddSquare();
        Box(580, 180, 620, 620);                                                  // the corners at local (400,0) and (400,400): long vectors all round
        Assert.Equal(6, Assert.Single(LoopsOf(path)).NodeCount);
    }

    [Fact]
    public void A_box_holding_a_start_point_deletes_nothing_beeps_and_is_no_undo_step()
    {
        var path = AddSquare();
        int steps = _s.History.UndoList.Count;
        Box(150, 150, 320, 250);                                                  // the start at (200,200) and the three nodes beside it
        Assert.Equal(1, _beeps);
        Assert.Equal(steps, _s.History.UndoList.Count);
        Assert.Equal(8, Assert.Single(LoopsOf(path)).NodeCount);
        Assert.Equal(0, Tool.SelectedNodeCount);
    }

    [Fact]
    public void A_box_with_no_nodes_or_no_size_does_nothing()
    {
        var path = AddSquare();
        int steps = _s.History.UndoList.Count;
        Box(700, 700, 750, 750);                                                  // empty canvas: no nodes
        Box(300, 150, 300, 250);                                                  // no width
        Assert.Equal(steps, _s.History.UndoList.Count);
        Assert.Equal(0, _beeps);
        Assert.Equal(8, Assert.Single(LoopsOf(path)).NodeCount);
    }

    [Fact]
    public void Ctrl_and_Alt_make_no_difference_to_a_box()
    {
        var path = AddSquare();
        Box(290, 150, 320, 250, ModifierKeys.Control);
        Assert.Equal(5, Assert.Single(LoopsOf(path)).NodeCount);
        _s.Undo();
        Box(290, 150, 320, 250, ModifierKeys.Alt);
        Assert.Equal(5, Assert.Single(LoopsOf(path)).NodeCount);
    }

    [Fact]
    public void Shift_box_selects_the_nodes_strictly_inside_start_points_too_and_changes_nothing_else()
    {
        var path = AddSquare();
        int steps = _s.History.UndoList.Count;
        Box(190, 190, 210, 210, ModifierKeys.Shift);                              // around the start point
        Assert.Equal(1, Tool.SelectedNodeCount);
        Assert.Equal(new Vec2(200, 200), Tool.AnchorPosition());

        Box(200, 150, 320, 250, ModifierKeys.Shift);                              // the left edge passes exactly through the start: strictly inside excludes it
        Assert.Equal(3, Tool.SelectedNodeCount);
        Assert.Equal(steps, _s.History.UndoList.Count);
        Assert.Equal(0, _beeps);
        Assert.Equal(8, Assert.Single(LoopsOf(path)).NodeCount);
    }

    [Fact]
    public void The_option_swaps_which_box_deletes_and_which_selects()
    {
        var path = AddSquare();
        VectorEditOptions.MarqueeShiftSelects = false;
        Box(290, 150, 320, 250);                                                  // plain now selects
        Assert.Equal(3, Tool.SelectedNodeCount);
        Assert.Equal(8, Assert.Single(LoopsOf(path)).NodeCount);
        Box(290, 150, 320, 250, ModifierKeys.Shift);                              // Shift now deletes
        Assert.Equal(5, Assert.Single(LoopsOf(path)).NodeCount);
        Assert.Equal("Delete Vectors", _s.History.UndoDescription);

        _s.Undo();
        Box(150, 150, 320, 250, ModifierKeys.Shift);                              // and the start-point rule goes with the deleting box
        Assert.Equal(1, _beeps);
        Assert.Equal(8, Assert.Single(LoopsOf(path)).NodeCount);
    }

    [Fact]
    public void Pressing_on_empty_canvas_lets_go_of_the_selection_and_the_box_is_red_only_when_it_deletes()
    {
        AddSquare();
        _s.PointerDown(At(304, 200, PointerButton.Left)); _s.PointerUp(At(304, 200, PointerButton.Left));
        Assert.Equal(1, Tool.SelectedNodeCount);

        _s.PointerDown(At(290, 150, PointerButton.Left));
        Assert.Equal(0, Tool.SelectedNodeCount);
        _s.PointerMove(At(310, 200));
        _s.PointerMove(At(320, 250));
        Assert.Equal(MarqueeKind.Delete, Assert.Single(_s.BuildOverlay().OfType<MarqueeOverlay>()).Kind);
        _s.PointerMove(At(322, 250, PointerButton.None, ModifierKeys.Shift));      // Shift held: this box would select
        Assert.Equal(MarqueeKind.Select, Assert.Single(_s.BuildOverlay().OfType<MarqueeOverlay>()).Kind);
        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));
        _s.PointerUp(At(322, 250, PointerButton.Left));
        Assert.Empty(_s.BuildOverlay().OfType<MarqueeOverlay>());
    }

    // ---- Small vectors: the count and the highlight ----

    [Fact]
    public void A_vector_is_small_by_the_straight_distance_between_its_ends_whatever_its_kind()
    {
        var loop = new Loop
        {
            Start = new Vec2(0, 0),
            Segments =
            [
                Segment.Line(new Vec2(10, 0)),                                               // chord 10: small
                Segment.Arc(new Vec2(15, -30), new Vec2(20, 0)),                             // a tall arc, but its ends are 10 apart: small
                Segment.Cubic(new Vec2(60, 0), new Vec2(-40, 0), new Vec2(25, 0)),           // swings far out and back; chord 5: small
                Segment.Line(new Vec2(125, 0)),                                              // chord 100: not small
            ],
        };
        Assert.Equal(3, SmallVectors.Count(loop, 1));
        Assert.Equal([new Vec2(10, 0), new Vec2(20, 0), new Vec2(25, 0)], SmallVectors.SmallEnds(loop, 1).ToArray());
        Assert.Equal(0, SmallVectors.Count(loop, 10));                                       // on the page everything is ten times as long
    }

    [Fact]
    public void The_object_scale_decides_what_the_count_includes()
    {
        var loops = new List<Loop> { new() { Start = default, Segments = [Segment.Line(new Vec2(10, 0)), Segment.Line(new Vec2(30, 0)), Segment.Line(new Vec2(130, 0))] } };
        int Count(double scale) => PathEditing.Stats(loops.Select(l => (l, scale)), SmallVectors.ThresholdPoints).SmallVectors;
        Assert.Equal(1, Count(1));                                                           // chords 10, 20, 100 against 18 points
        Assert.Equal(2, Count(0.5));                                                         // 5, 10, 50
        Assert.Equal(0, Count(2));                                                           // 20, 40, 200
    }

    [Fact]
    public void Info_Line_counts_small_vectors_with_the_chord_rule_and_names_the_threshold()
    {
        AddSquare();
        Assert.Contains("2 small vecs (< 0.25in)", Tool.InfoLine());                          // 100-104 and 104-108
        SmallVectors.SetInches(0.02);
        Assert.Contains("0 small vecs (< 0.02in)", Tool.InfoLine());
        SmallVectors.SetInches(1.5);                                                         // 108 points
        Assert.Contains("(< 1.5in)", Tool.InfoLine());
        Assert.DoesNotContain("0 small vecs", Tool.InfoLine());
    }

    [Fact]
    public void The_threshold_setting_is_kept_between_a_hundredth_and_five_inches()
    {
        Assert.Equal(SmallVectors.MinInches, SmallVectors.SetInches(0));
        Assert.Equal(SmallVectors.MaxInches, SmallVectors.SetInches(99));
        Assert.Equal(0.25, SmallVectors.SetInches(double.NaN));
        Assert.Equal(Units.FromInches(0.25), SmallVectors.ThresholdPoints, 9);
    }

    [Fact]
    public void Highlight_marks_the_end_of_every_short_vector_in_every_object_and_is_off_by_default()
    {
        AddSquare(edit: false);                                                              // short vectors end at local x = 104 and 108
        var small = new PathObject(PathEditing.FromLoops([new Loop { Start = default, Segments = [Segment.Line(new Vec2(5, 0)), Segment.Line(new Vec2(5, 400))] }], FillRule.NonZero))
        { Transform = Affine2.Translation(100, 500) };
        _s.History.Execute(new AddObjectCommand(L, small));
        Assert.Empty(_s.BuildOverlay().OfType<SmallVectorMarksOverlay>());                   // off

        VectorEditOptions.HighlightSmallVectors = true;
        var marks = Assert.Single(_s.BuildOverlay().OfType<SmallVectorMarksOverlay>()).Ends;
        Assert.Contains(new Vec2(304, 200), marks);
        Assert.Contains(new Vec2(308, 200), marks);
        Assert.Contains(new Vec2(105, 500), marks);                                          // the other object's 5 point vector
        Assert.DoesNotContain(new Vec2(300, 200), marks);                                    // 100 ends a 100 point vector
        Assert.Equal(3, marks.Count);

        _s.Viewport.Set(1, new Vec2(-5000, -5000));                                          // scrolled away: nothing on screen, nothing marked
        Assert.Empty(_s.BuildOverlay().OfType<SmallVectorMarksOverlay>());
        _s.Viewport.Set(1, Vec2.Zero);
        _s.History.Execute(TransformObjectsCommand.ApplyWorld("Move", [small], Affine2.Translation(50, 0)));
        Assert.Contains(new Vec2(155, 500), Assert.Single(_s.BuildOverlay().OfType<SmallVectorMarksOverlay>()).Ends);   // follows the object
        SmallVectors.SetInches(0.05);                                                        // 3.6 points: the 4 point vectors are no longer short
        Assert.Empty(_s.BuildOverlay().OfType<SmallVectorMarksOverlay>());
    }
}
