using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Vector Draw: makes vectors out of thin air, for drawing or tracing (requirement R16). A click places a node, joined to the previous
/// one by a straight line; a click-and-drag instead pulls out a pen-style handle, which makes the segment a smooth curve (the drag
/// sets the handle going forward from the new node and its mirror image coming in). A double-click on the FIRST node closes the shape;
/// a double-click on any other node is just a node placed there. Backspace takes the last node away; Enter or Esc finishes an open
/// line; Ctrl+Z also takes the last node away while a drawing is in progress. In this tool clicks only ever make nodes: they never select
/// other objects. Right-button zoom works anywhere (the session handles it) and nodes snap to ruler guides.
/// Each finished drawing is one new object, with the default style (no color unless one was chosen), and one undo step.
/// </summary>
public sealed class VectorDrawTool : Tool, IModeTool
{
    /// <summary>A node being drawn: its place, and the handle pulled out of it by dragging (null = none, the vector is straight).</summary>
    private sealed class DrawNode(Vec2 position)
    {
        public Vec2 Position = position;
        public Vec2? Out;   // the forward handle; the incoming one is its mirror image
    }

    private readonly List<DrawNode> _nodes = [];
    private bool _pressing, _dragged, _ignoreUp;
    private Vec2 _pressScreen;
    private Vec2? _hover;

    public override string Id => "vectordraw";
    public override string DisplayName => "Vector Draw";
    public override string Hint => "Click: add node · Drag: curve · Double-click first node: close · Backspace: undo node · Enter/Esc: finish";
    public override CursorKind Cursor => CursorKind.VectorDraw;

    /// <summary>True while a drawing has at least one node.</summary>
    public bool IsDrawing => _nodes.Count > 0;

    public int NodeCount => _nodes.Count;

    // ---- Pointer ----

    public override void PointerDown(PointerInput e)
    {
        _ignoreUp = false;
        var p = Session.SnapPoint(e.Doc);
        _hover = p;

        if (e.ClickCount >= 2)
        {
            // The first click of a double-click already placed its node, so a double-click on an ordinary node adds nothing more.
            _ignoreUp = true;
            if (_nodes.Count >= 4 && Near(p, _nodes[0].Position))
            {
                _nodes.RemoveAt(_nodes.Count - 1);   // the duplicate the first click of this double-click put on the first node
                Finish(close: true);
            }
            else if (_nodes.Count >= 2 && Near(p, _nodes[^1].Position) && Near(p, _nodes[0].Position))
            {
                _nodes.RemoveAt(_nodes.Count - 1);   // a shape too small to close: take the duplicate away again
                Redraw();
            }
            return;
        }

        if (_nodes.Count > 0 && Vec2.Distance(_nodes[^1].Position, p) < 1e-9)
        {
            _ignoreUp = true;   // the same spot again: not a new node
            return;
        }
        if (_nodes.Count == 0) Session.MarkMode();   // a new drawing is a new mode: Cancel takes back only this one, not the paths finished before it
        _nodes.Add(new DrawNode(p));
        _pressing = true;
        _dragged = false;
        _pressScreen = e.Screen;
        Redraw();
    }

    public override void PointerMove(PointerInput e)
    {
        var p = Session.SnapPoint(e.Doc);
        _hover = p;
        if (_pressing && _nodes.Count > 0)
        {
            if (!_dragged && Vec2.Distance(_pressScreen, e.Screen) >= Session.ClickThresholdPx) _dragged = true;
            if (_dragged) _nodes[^1].Out = p;   // the drag pulls the handle out of the node just placed
        }
        Redraw();
    }

    public override void PointerUp(PointerInput e)
    {
        if (_ignoreUp) { _ignoreUp = false; return; }
        if (!_pressing) return;
        _pressing = false;
        if (_dragged && _nodes.Count > 0)
        {
            var node = _nodes[^1];
            node.Out = Session.SnapPoint(e.Doc);
            if (node.Out is { } h && Vec2.Distance(h, node.Position) < 1e-9) node.Out = null;
        }
        _dragged = false;
        Redraw();
    }

    public override void PointerExited()
    {
        _hover = null;
        Redraw();
    }

    private bool Near(Vec2 a, Vec2 b) => Vec2.Distance(a, b) <= Session.Viewport.ToDocLength(Session.HitTolerancePixels);

    // ---- Keys ----

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case EditorKey.Back:
                RemoveLastNode();   // always ours in this tool: Backspace never deletes a selected object here
                return true;
            case EditorKey.Enter:
                Finish(close: false);
                return true;
            case EditorKey.Escape when IsDrawing:
                Finish(close: false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Backspace, and Ctrl+Z while drawing: the last node goes (and with it the last vector).</summary>
    public void RemoveLastNode()
    {
        if (_nodes.Count == 0) return;
        _nodes.RemoveAt(_nodes.Count - 1);
        _pressing = false;
        _dragged = false;
        Redraw();
    }

    /// <summary>Leaving the tool keeps the drawing (nothing the user drew is thrown away by clicking another tool).</summary>
    public override void Deactivate()
    {
        Finish(close: false);
        _hover = null;
    }

    /// <summary>Abandons the drawing without making an object.</summary>
    public override void Cancel()
    {
        _nodes.Clear();
        _pressing = _dragged = false;
        Redraw();
    }

    // ---- Done and Cancel (the mode is the path being drawn; nothing is in the history until it is finished) ----

    bool IModeTool.IsInMode => IsDrawing;
    bool IModeTool.CanFinish => true;

    /// <summary>Done: finishes the path where it is (left open, as Enter does), then goes to the Layout tool with the new object selected.</summary>
    Task IModeTool.FinishMode()
    {
        Finish(close: false);
        Session.SetTool("select");
        return Task.CompletedTask;
    }

    void IModeTool.DiscardPending() => Cancel();   // the unfinished path is thrown away

    // ---- Making the object ----

    /// <summary>The loop drawn so far; null with fewer than two nodes.</summary>
    internal Loop? CurrentLoop(bool close)
    {
        if (_nodes.Count < 2) return null;
        var loop = new Loop { Start = _nodes[0].Position, Closed = close };
        for (int i = 1; i < _nodes.Count; i++) loop.Segments.Add(SegmentBetween(_nodes[i - 1], _nodes[i]));
        if (close) loop.Segments.Add(SegmentBetween(_nodes[^1], _nodes[0]));
        return loop;
    }

    private static Segment SegmentBetween(DrawNode from, DrawNode to)
    {
        if (from.Out is null && to.Out is null) return Segment.Line(to.Position);
        var c1 = from.Out ?? from.Position;
        var c2 = to.Out is { } o ? to.Position * 2 - o : to.Position;   // the incoming handle mirrors the outgoing one
        return Segment.Cubic(c1, c2, to.Position);
    }

    private void Finish(bool close)
    {
        var loop = CurrentLoop(close && _nodes.Count >= 3);
        _nodes.Clear();
        _pressing = _dragged = false;
        Redraw();
        if (loop is null) return;
        var layer = Session.Document.ActiveLayer;
        if (layer.Locked) return;

        var obj = new PathObject(PathEditing.FromLoops([loop], FillRule.NonZero)) { Style = Session.DefaultStyle };
        Session.History.Execute(new AddObjectCommand(layer, obj, Stacking.NewItemIndex));   // a new shape goes to the bottom of the layer
        Session.Selection.Set(obj);
    }

    // ---- Feedback ----

    public override void BuildOverlay(List<OverlayItem> items)
    {
        if (_nodes.Count == 0) return;
        var marks = new List<VectorNodeMark>();
        var handles = new List<(Vec2 Node, Vec2 Handle)>();
        for (int i = 0; i < _nodes.Count; i++)
        {
            var n = _nodes[i];
            marks.Add(new VectorNodeMark(n.Position, Corner: n.Out is null, Selected: i == _nodes.Count - 1, Start: i == 0));
            if (n.Out is { } h)
            {
                handles.Add((n.Position, h));
                handles.Add((n.Position, n.Position * 2 - h));
            }
        }
        var loops = new List<OutlineLoop>();
        if (CurrentLoop(close: false) is { } loop) loops.Add(new OutlineLoop(loop.ToGeometry(), LoopKind.Border));
        items.Add(new VectorEditOverlay(loops, marks, handles, null));

        // The live line from the last node to the mouse (a curve when the last node has a handle).
        if (_hover is { } mouse && !_pressing)
        {
            var last = _nodes[^1];
            if (Vec2.Distance(last.Position, mouse) > 1e-9)
            {
                var b = new PathBuilder().MoveTo(last.Position);
                if (last.Out is { } o) b.CubicTo(o, mouse, mouse); else b.LineTo(mouse);
                items.Add(new OutlineOverlay(b.Build()));
            }
        }
    }
}
