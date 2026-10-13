using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>The Vector Draw tool (R16): nodes by clicking, curves by dragging, closing, Backspace, Enter and Esc, undo.</summary>
public class VectorDrawTests
{
    private readonly EditorSession _s = new(NewDoc());

    public VectorDrawTests() => _s.SetTool("vectordraw");

    private Vec2 Screen(Vec2 doc) => _s.Viewport.ToScreen(doc);

    private PointerInput Input(Vec2 doc, int clicks = 1) =>
        new(Screen(doc), doc, PointerButton.Left, ModifierKeys.None, clicks);

    private void Click(double x, double y, int clicks = 1)
    {
        var p = new Vec2(x, y);
        _s.PointerDown(Input(p, clicks));
        _s.PointerUp(Input(p));
    }

    private void Drag(double x, double y, double toX, double toY)
    {
        _s.PointerDown(Input(new Vec2(x, y)));
        _s.PointerMove(Input(new Vec2((x + toX) / 2, (y + toY) / 2)));
        _s.PointerMove(Input(new Vec2(toX, toY)));
        _s.PointerUp(Input(new Vec2(toX, toY)));
    }

    private void Key(EditorKey key) => _s.KeyDown(key, ModifierKeys.None);

    private PathObject Only() => Assert.IsType<PathObject>(Assert.Single(_s.Document.ActiveLayer.Children));

    private Loop DrawnLoop() => Assert.Single(PathEditing.ToLoops(Only().Geometry));

    [Fact]
    public void Clicks_place_nodes_joined_by_straight_lines_and_Enter_finishes_an_open_line()
    {
        Click(100, 100); Click(200, 100); Click(200, 200);
        Assert.Empty(_s.Document.ActiveLayer.Children);          // nothing is made until the drawing is finished
        Key(EditorKey.Enter);

        var loop = DrawnLoop();
        Assert.False(loop.Closed);
        Assert.Equal(new Vec2(100, 100), loop.Start);
        Assert.Equal([Segment.Line(new Vec2(200, 100)), Segment.Line(new Vec2(200, 200))], loop.Segments);
        Assert.Equal(ObjectStyle.None, Only().Style);             // no color: it shows as a plain outline
        Assert.Same(Only(), Assert.Single(_s.Selection.Items));
        Assert.Equal("vectordraw", _s.ActiveTool.Id);             // the tool stays on for the next drawing
    }

    [Fact]
    public void Dragging_pulls_out_a_pen_style_handle_and_makes_the_vector_a_curve()
    {
        Click(0, 0);
        Drag(100, 0, 140, 0);                                    // press at the node, drag right: the handle goes forward
        Click(200, 100);
        Key(EditorKey.Enter);

        var loop = DrawnLoop();
        var first = loop.Segments[0];
        Assert.Equal(SegmentKind.Cubic, first.Kind);
        Assert.Equal(new Vec2(0, 0), first.C1);                  // the first node had no handle
        Assert.Equal(new Vec2(60, 0), first.C2);                 // the mirror image of the dragged handle
        Assert.Equal(new Vec2(100, 0), first.End);
        var second = loop.Segments[1];
        Assert.Equal(SegmentKind.Cubic, second.Kind);
        Assert.Equal(new Vec2(140, 0), second.C1);               // the dragged handle leads out of that node
        Assert.Equal(new Vec2(200, 100), second.C2);
    }

    [Fact]
    public void A_double_click_on_the_first_node_closes_the_shape_without_a_duplicate_node()
    {
        Click(100, 100); Click(300, 100); Click(200, 250);
        Click(100, 100);                                         // the first click of the double-click ...
        Click(100, 100, clicks: 2);                              // ... and the second

        var loop = DrawnLoop();
        Assert.True(loop.Closed);
        Assert.Equal(3, loop.NodeCount);
        Assert.Equal(3, loop.Segments.Count);
        Assert.Equal(loop.Start, loop.Segments[^1].End);
        Assert.False(_s.ActiveTool is VectorDrawTool { IsDrawing: true });
    }

    [Fact]
    public void A_double_click_on_any_other_node_just_places_a_node_there()
    {
        Click(100, 100); Click(300, 100); Click(200, 250);
        Click(300, 100); Click(300, 100, clicks: 2);             // double-click on the second node
        Assert.Empty(_s.Document.ActiveLayer.Children);          // nothing closed, nothing finished
        Key(EditorKey.Enter);

        var loop = DrawnLoop();
        Assert.False(loop.Closed);
        Assert.Equal(4, loop.NodeCount);                          // the path crosses itself at the second node
        Assert.Equal(new Vec2(300, 100), loop.Node(3));
    }

    [Fact]
    public void Backspace_removes_the_last_node_and_never_deletes_a_selected_object()
    {
        var other = Rect(0, 0, 10, 10);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, other));
        _s.Selection.Set(other);

        Click(100, 100); Click(200, 100); Click(200, 200);
        Key(EditorKey.Back);
        Assert.Equal(2, ((VectorDrawTool)_s.ActiveTool).NodeCount);
        Key(EditorKey.Enter);
        Assert.Equal(new Vec2(200, 100), Assert.Single(PathEditing.ToLoops(_s.Document.ActiveLayer.Children.OfType<PathObject>().Single().Geometry)).Segments[0].End);   // the third node is gone

        Key(EditorKey.Back);                                     // nothing being drawn: still no deletion of the selected shape
        Assert.Contains(other, _s.Document.ActiveLayer.Children);
        Assert.Equal(2, _s.Document.ActiveLayer.Children.Count);
    }

    [Fact]
    public void Escape_finishes_an_open_line_and_a_second_Escape_leaves_the_tool()
    {
        Click(100, 100); Click(200, 100);
        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));
        Assert.False(DrawnLoop().Closed);
        Assert.Equal("vectordraw", _s.ActiveTool.Id);

        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));   // nothing being drawn: back to the Layout tool
        Assert.Equal("select", _s.ActiveTool.Id);
    }

    [Fact]
    public void One_finished_drawing_is_one_undo_step_and_Ctrl_Z_while_drawing_removes_the_last_node()
    {
        int steps = _s.History.UndoList.Count;
        Click(100, 100); Click(200, 100); Click(200, 200);
        _s.Undo();                                               // Ctrl+Z mid-drawing
        Assert.Equal(2, ((VectorDrawTool)_s.ActiveTool).NodeCount);
        Assert.Equal(steps, _s.History.UndoList.Count);          // the document history is untouched
        Click(250, 250);
        Key(EditorKey.Enter);

        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal(3, DrawnLoop().NodeCount);
        _s.Undo();
        Assert.Empty(_s.Document.ActiveLayer.Children);
        _s.Redo();
        Assert.Single(_s.Document.ActiveLayer.Children);
    }

    [Fact]
    public void Clicks_never_select_other_objects_and_nothing_is_made_from_a_single_node()
    {
        var other = Rect(50, 50, 100, 100);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, other));
        _s.Selection.Clear();

        Click(100, 100);                                         // on the rectangle: a node, not a selection
        Assert.True(_s.Selection.IsEmpty);
        Assert.Equal(1, ((VectorDrawTool)_s.ActiveTool).NodeCount);
        Key(EditorKey.Enter);
        Assert.Equal([other], _s.Document.ActiveLayer.Children);  // one node is not a drawing
    }

    [Fact]
    public void Nodes_snap_to_guides()
    {
        _s.ShowGuides = true;
        _s.SnapToGuides = true;
        _s.History.Execute(new AddGuideCommand(new Guide(new Vec2(50, 0), 90)));   // vertical guide at x = 50
        _s.SelectedGuide = null;
        Click(52, 100); Click(200, 100);
        Key(EditorKey.Enter);
        Assert.Equal(50, DrawnLoop().Start.X, 6);
    }

    [Fact]
    public void Switching_tools_keeps_the_drawing_and_the_overlay_shows_the_nodes_and_the_live_line()
    {
        Click(100, 100); Click(200, 100);
        _s.PointerMove(Input(new Vec2(250, 150)));
        var items = _s.BuildOverlay();
        Assert.Contains(items, i => i is VectorEditOverlay { Nodes.Count: 2 });
        Assert.Contains(items, i => i is OutlineOverlay);         // the line from the last node to the mouse

        _s.SetTool("select");
        Assert.Single(_s.Document.ActiveLayer.Children);
    }

    [Fact]
    public void A_locked_layer_makes_no_object()
    {
        _s.Document.ActiveLayer.Locked = true;
        Click(100, 100); Click(200, 100);
        Key(EditorKey.Enter);
        Assert.Empty(_s.Document.ActiveLayer.Children);
    }
}
