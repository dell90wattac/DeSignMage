using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Vector Edit tool (key 2): node and vector editing. Behavior from the user's spec (2026-10-11):
/// <list type="bullet">
/// <item>Entered by double-clicking a single or combined object with the Layout tool, from the Toolbox (key 2) with one
/// object selected, or by clicking an object. Left by picking the Layout tool (key 1); Esc only cancels a drag or clears the
/// selection. Objects never move here. Text turns into curves the moment its vectors are opened (one undo step).</item>
/// <item>Nodes: big squares = corners, small crosses = smooth nodes (derived from the vectors, nothing is stored). Vectors on the
/// border are blue, interior ones red, broken (open) loops black. A selected vector is dotted red.</item>
/// <item>Click a node (Shift toggles, arrow keys nudge). Drag nodes (Ctrl = no snap; Shift at release near another
/// end = join). Click a vector to select it: drag a line to move it (Ctrl bends it into an arc), drag an arc's midpoint
/// handle to size it (Ctrl makes it a line), drag Bézier handles (smooth nodes keep their tangent; Alt = independent,
/// Ctrl = symmetric).</item>
/// <item>Double-click a vector: the vector menu (Insert, To Line, To Arc, To Bezier, Break Loop, Set Start/End, Join Start/End,
/// Delete). The letter keys act on the vector under the cursor, else the selected vector or node.</item>
/// </list>
/// Every change is one undoable command; the first edit turns a rectangle/ellipse into a plain vector shape.
/// </summary>
public sealed class VectorEditTool : PathEditToolBase
{
    private readonly record struct HandleRef(int Piece, int Loop, int Seg, int Which, Vec2 Position, Vec2 Node);

    private enum Mode { None, Nodes, Handle, Line, Marquee }

    private readonly HashSet<NodeRef> _selected = [];
    private SegRef? _hover, _selSeg, _menu;
    private Mode _mode;
    private Vec2 _startDoc, _startScreen, _anchorWorld;
    private NodeRef? _anchor;
    private bool _moved;
    private HandleRef _handle;
    private Rect2 _marquee;
    private List<List<Loop>>? _work;   // per piece, during a drag
    private string _dragHint = "";

    public override string Id => "vector";
    public override string DisplayName => "Vector Edit";

    public override string Hint => !HasTarget
        ? "To EDIT VECTORS: Click an object   To LAYOUT: Pick the Layout tool (1)"
        : _mode switch
        {
            Mode.Nodes => "Move node: Ctrl = no snap, Shift = join to an end point nearby",
            Mode.Line => "Move line: Ctrl = bend it into an arc",
            Mode.Handle => _dragHint,
            Mode.Marquee => BoxDeletesSmallNodes ? "Box: small nodes inside are deleted when you let go (the box turns red); big nodes are left alone" : "",
            _ => "To MOVE NODES: Drag (Shift: add)   To EDIT a vector: Double-click it   To LAYOUT: Pick the Layout tool (1)",
        };

    public override CursorKind Cursor => _mode switch
    {
        Mode.Nodes or Mode.Handle or Mode.Line => CursorKind.Move,
        _ => Hovering ? CursorKind.SelectHit : CursorKind.Arrow,
    };

    public int SelectedNodeCount => _selected.Count;

    protected override void ClearHover()
    {
        if (_hover is null && !Hovering) return;
        _hover = null;
        Hovering = false;
        Redraw();
    }

    protected override void ResetSelection()
    {
        _selected.Clear();
        _selSeg = _menu = null;
    }

    protected override void OnRebuilt()
    {
        _selected.RemoveWhere(n => !Valid(n));
        if (_selSeg is { } s && !Valid(s)) _selSeg = null;
        if (_menu is { } m && !Valid(m)) _menu = null;
        if (_hover is { } h && !Valid(h)) _hover = null;
        _work = null;
        _smallCache = null;
        if (!HasTarget) { _selected.Clear(); _selSeg = _menu = _hover = null; }
    }

    private bool Valid(NodeRef n) => n.Piece < Pieces.Count && n.Loop < Pieces[n.Piece].Loops.Count && n.Node < Pieces[n.Piece].Loops[n.Loop].NodeCount;
    private bool Valid(SegRef s) => s.Piece < Pieces.Count && s.Loop < Pieces[s.Piece].Loops.Count && s.Seg < Pieces[s.Piece].Loops[s.Loop].Segments.Count;

    // ---- Handles: Bézier control points and arc midpoints ----

    /// <summary>Handles shown for the selected nodes and the selected vector ("bezier box").</summary>
    private List<HandleRef> VisibleHandles(IReadOnlyList<List<Loop>> loopsPerPiece)
    {
        var list = new List<HandleRef>();
        void AddSegment(int piece, int loop, int seg, int which)
        {
            var l = loopsPerPiece[piece][loop];
            var s = l.Segments[seg];
            if (s.Kind == SegmentKind.Cubic)
            {
                if (which != 2) list.Add(new(piece, loop, seg, 1, s.C1, l.SegmentStart(seg)));
                if (which != 1) list.Add(new(piece, loop, seg, 2, s.C2, s.End));
            }
            else if (s.Kind == SegmentKind.Arc) list.Add(new(piece, loop, seg, 1, s.C1, s.C1));
        }
        foreach (var n in _selected)
        {
            var l = loopsPerPiece[n.Piece][n.Loop];
            int a = l.Incoming(n.Node), b = l.Outgoing(n.Node);
            if (a >= 0) AddSegment(n.Piece, n.Loop, a, 2);
            if (b >= 0) AddSegment(n.Piece, n.Loop, b, 1);
        }
        if (_selSeg is { } s0) AddSegment(s0.Piece, s0.Loop, s0.Seg, 0);
        return list.Distinct().ToList();
    }

    private HandleRef? HandleAt(Vec2 screen)
    {
        foreach (var h in VisibleHandles(Pieces.Select(p => p.Loops).ToList()))
            if (Vec2.Distance(Screen(Pieces[h.Piece], h.Position), screen) <= Session.HandleRadiusPx) return h;
        return null;
    }

    // ---- Pointer ----

    public override void PointerDown(PointerInput e)
    {
        _startDoc = e.Doc;
        _startScreen = e.Screen;
        _moved = false;
        _anchor = null;
        _menu = null;

        if (HasTarget)
        {
            if (HandleAt(e.Screen) is { } h)
            {
                _handle = h;
                _mode = Mode.Handle;
                _dragHint = Pieces[h.Piece].Loops[h.Loop].Segments[h.Seg].Kind == SegmentKind.Arc
                    ? "Size arc: Ctrl = make it a line"
                    : "Move handle: Alt = independent, Ctrl = symmetric";
                return;
            }
            if (NodeAt(e.Screen) is { } n)
            {
                _selSeg = null;
                if (e.Shift) { if (!_selected.Remove(n)) _selected.Add(n); }
                else if (!_selected.Contains(n)) { _selected.Clear(); _selected.Add(n); }
                _mode = _selected.Contains(n) ? Mode.Nodes : Mode.None;
                _anchor = n;
                _anchorWorld = Pieces[n.Piece].World.Transform(Pieces[n.Piece].Loops[n.Loop].Node(n.Node));
                Redraw();
                return;
            }
            if (SegmentAt(e.Doc) is { } seg)
            {
                _selSeg = seg;
                if (!e.Shift) _selected.Clear();
                if (e.ClickCount >= 2)
                {
                    _menu = seg;
                    _mode = Mode.None;
                    Session.RequestContextMenu(Target!, e.Screen, MenuKind.Vector);
                    return;
                }
                // A line can be dragged as a whole; arcs and Béziers are reshaped through their handles.
                _mode = Pieces[seg.Piece].Loops[seg.Loop].Segments[seg.Seg].Kind == SegmentKind.Line ? Mode.Line : Mode.None;
                Redraw();
                return;
            }
        }

        // Clicking another editable object opens its vectors (objects themselves never move here).
        if (TryOpenObjectAt(e))
        {
            _mode = Mode.None;
            return;
        }

        if (!e.Shift) { _selected.Clear(); _selSeg = null; }
        _mode = Mode.Marquee;
        _marquee = Rect2.FromPoints(e.Doc, e.Doc);
        Redraw();
    }

    public override void PointerMove(PointerInput e)
    {
        if (_mode == Mode.None)
        {
            UpdateHover(e);
            return;
        }
        if (!_moved && Vec2.Distance(e.Screen, _startScreen) < Session.ClickThresholdPx) return;
        _moved = true;
        switch (_mode)
        {
            case Mode.Nodes:
                _work = MovedNodes(NodeDragDelta(e));
                break;
            case Mode.Handle:
                _work = Clones();
                var piece = Pieces[_handle.Piece];
                var loop = _work[_handle.Piece][_handle.Loop];
                var local = ToLocal(piece, e.Doc);
                if (loop.Segments[_handle.Seg].Kind == SegmentKind.Arc && e.Control) PathEditing.ToLine(loop, _handle.Seg);   // Ctrl: size arc → line
                else PathEditing.MoveHandle(loop, _handle.Seg, _handle.Which, local, e.Alt ? HandleMode.Free : e.Control ? HandleMode.Symmetric : HandleMode.Auto);
                break;
            case Mode.Line:
                _work = Clones();
                var ls = _selSeg!.Value;
                var lp = Pieces[ls.Piece];
                var ll = _work[ls.Piece][ls.Loop];
                if (e.Control) PathEditing.BendLine(ll, ls.Seg, ToLocal(lp, e.Doc));   // Ctrl: move line → arc
                else PathEditing.MoveLine(ll, ls.Seg, ToLocalVector(lp, e.Doc - _startDoc));
                break;
            case Mode.Marquee:
                _marquee = Rect2.FromPoints(_startDoc, e.Doc);
                break;
        }
        Redraw();
    }

    private void UpdateHover(PointerInput e)
    {
        bool over = false;
        SegRef? hover = null;
        if (HasTarget)
        {
            if (HandleAt(e.Screen) is not null || NodeAt(e.Screen) is not null) over = true;
            else if (SegmentAt(e.Doc) is { } s) { hover = s; over = true; }
        }
        if (hover != _hover || over != Hovering)
        {
            _hover = hover;
            Hovering = over;
            Redraw();
        }
    }

    public override void PointerUp(PointerInput e)
    {
        if (_mode != Mode.None) PointerMove(e);
        switch (_mode)
        {
            case Mode.Nodes when _moved && _work is not null:
                if (e.Shift && TryJoinDraggedEnd()) break;
                Apply(_work, "Move Nodes", keepNodes: true, keepSegment: false);
                break;
            case Mode.Handle when _moved && _work is not null:
                Apply(_work, Pieces[_handle.Piece].Loops[_handle.Loop].Segments[_handle.Seg].Kind == SegmentKind.Arc && e.Control ? "To Line" : "Move Handle",
                    keepNodes: true, keepSegment: true);
                break;
            case Mode.Line when _moved && _work is not null:
                Apply(_work, e.Control ? "Bend Line" : "Move Line", keepNodes: false, keepSegment: true);
                break;
            case Mode.Marquee when _moved:
                EndBox();
                break;
        }
        _mode = Mode.None;
        _work = null;
        Redraw();
    }

    // ---- Box over nodes: deletes the small ones (a box never selects) ----

    /// <summary>
    /// True (the default): a box drawn over <b>small nodes</b> (see <see cref="SmallVectors"/>) deletes them when it is released. Big nodes in
    /// the box are left alone, neither deleted nor selected: a box never selects nodes (click a node, or Shift+click, to select). False: a box
    /// does nothing.
    /// </summary>
    public bool BoxDeletesSmallNodes { get; set; } = true;

    private IEnumerable<NodeRef> NodesIn(Rect2 box)
    {
        for (int pi = 0; pi < Pieces.Count; pi++)
            for (int li = 0; li < Pieces[pi].Loops.Count; li++)
                for (int k = 0; k < Pieces[pi].Loops[li].NodeCount; k++)
                    if (box.Contains(Pieces[pi].World.Transform(Pieces[pi].Loops[li].Node(k))))
                        yield return new NodeRef(pi, li, k);
    }

    /// <summary>The nodes inside <paramref name="box"/> that are small.</summary>
    private List<NodeRef> SmallNodesIn(Rect2 box) => NodesIn(box).Where(IsSmall).ToList();

    /// <summary>How many small nodes a box over <paramref name="box"/> (document coordinates) would delete.</summary>
    public int SmallNodeCountIn(Rect2 box) => SmallNodesIn(box).Count;

    private bool IsSmall(NodeRef n) => SmallNodeSet().Contains(n);

    private HashSet<NodeRef>? _smallCache;
    private double _smallCacheThreshold;

    /// <summary>The small nodes of the open graphic as it is stored (cached until it changes or the threshold does).</summary>
    private HashSet<NodeRef> SmallNodeSet()
    {
        if (_smallCache is null || _smallCacheThreshold != SmallVectors.ThresholdPoints)
        {
            _smallCache = SmallNodesOf(Pieces.Select(p => p.Loops).ToList());
            _smallCacheThreshold = SmallVectors.ThresholdPoints;
        }
        return _smallCache;
    }

    /// <summary>Which nodes are small, by <see cref="SmallVectors"/>'s rule (the only place that decides it).</summary>
    private HashSet<NodeRef> SmallNodesOf(IReadOnlyList<List<Loop>> loopsPerPiece)
    {
        var set = new HashSet<NodeRef>();
        for (int pi = 0; pi < loopsPerPiece.Count; pi++)
        {
            double scale = Pieces[pi].World.AverageScale;
            for (int li = 0; li < loopsPerPiece[pi].Count; li++)
            {
                var small = SmallVectors.SmallNodes(loopsPerPiece[pi][li], scale);
                for (int k = 0; k < small.Length; k++)
                    if (small[k]) set.Add(new NodeRef(pi, li, k));
            }
        }
        return set;
    }

    /// <summary>The box was released: small nodes inside it are deleted as one undo step. Nothing else happens (no node is selected by a box).</summary>
    private void EndBox()
    {
        if (!BoxDeletesSmallNodes) return;
        var small = SmallNodesIn(_marquee);
        if (small.Count == 0) return;
        var work = Clones();
        DeleteNodes(work, small);
        Apply(work, "Delete Small Vectors", keepNodes: false, keepSegment: false);
    }
    /// <summary>Deletes nodes from the working copies: the two vectors at each merge into one, so a closed loop stays closed.</summary>
    private static void DeleteNodes(List<List<Loop>> work, IEnumerable<NodeRef> nodes)
    {
        foreach (var group in nodes.GroupBy(n => (n.Piece, n.Loop)).OrderByDescending(g => g.Key.Loop))
        {
            var loops = work[group.Key.Piece];
            var positions = group.Select(n => loops[group.Key.Loop].Node(n.Node)).ToList();
            foreach (var p in positions)
            {
                if (group.Key.Loop >= loops.Count) break;
                var l = loops[group.Key.Loop];
                int k = Enumerable.Range(0, l.NodeCount).FirstOrDefault(i => l.Node(i) == p, -1);
                if (k >= 0) PathEditing.DeleteNode(loops, group.Key.Loop, k);
            }
        }
    }

    /// <summary>The drag offset in document space; the pressed node snaps to guides unless Ctrl is held.</summary>
    private Vec2 NodeDragDelta(PointerInput e)
    {
        var delta = e.Doc - _startDoc;
        if (!e.Control && _anchor is not null)
        {
            var want = _anchorWorld + delta;
            delta += Session.SnapPoint(want) - want;
        }
        return delta;
    }

    private List<List<Loop>> MovedNodes(Vec2 docDelta)
    {
        var work = Clones();
        foreach (var n in _selected)
        {
            var local = ToLocalVector(Pieces[n.Piece], docDelta);
            var l = work[n.Piece][n.Loop];
            PathEditing.MoveNode(l, n.Node, l.Node(n.Node) + local);
        }
        return work;
    }

    /// <summary>Shift on release: the dragged end point joins another end point close to it ("press Shift near the point").</summary>
    private bool TryJoinDraggedEnd()
    {
        if (_work is null || _selected.Count != 1) return false;
        var n = _selected.First();
        var loops = _work[n.Piece];
        var dragged = loops[n.Loop];
        if (dragged.Closed || (n.Node != 0 && n.Node != dragged.NodeCount - 1)) return false;
        var piece = Pieces[n.Piece];
        var at = Screen(piece, dragged.Node(n.Node));
        for (int li = 0; li < loops.Count; li++)
        {
            var other = loops[li];
            if (other.Closed) continue;
            foreach (int k in new[] { 0, other.NodeCount - 1 }.Distinct())
            {
                if (li == n.Loop && k == n.Node) continue;
                if (li == n.Loop && other.NodeCount < 3) continue;
                if (Vec2.Distance(Screen(piece, other.Node(k)), at) > Session.HandleRadiusPx * 1.5) continue;
                PathEditing.MoveNode(dragged, n.Node, other.Node(k));
                if (PathEditing.JoinLoops(loops, n.Loop, n.Node == 0, li, k == 0) < 0) return false;
                Apply(_work, "Join", keepNodes: false, keepSegment: false);
                return true;
            }
        }
        return false;
    }

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case EditorKey.Escape:
                // Esc never leaves vector mode (the Layout tool does); it cancels a drag or clears the selection.
                if (_mode != Mode.None) Cancel();
                else { _selected.Clear(); _selSeg = null; _menu = null; Redraw(); }
                return true;
            case EditorKey.Delete:
            case EditorKey.Back:
                if (CanDelete) Delete();
                return true;   // never deletes the whole object while editing its vectors
            case EditorKey.Insert:
                if (CanInsert) Insert();
                return true;
        }
        Vec2? dir = key switch
        {
            EditorKey.Left => new Vec2(-1, 0), EditorKey.Right => new Vec2(1, 0),
            EditorKey.Up => new Vec2(0, -1), EditorKey.Down => new Vec2(0, 1),
            _ => null,
        };
        if (dir is null || _selected.Count == 0 || _mode != Mode.None) return false;
        double step = Session.NudgeDistance;
        if ((modifiers & ModifierKeys.Shift) != 0) step *= 5;
        if ((modifiers & ModifierKeys.Control) != 0) step /= 5;
        Apply(MovedNodes(dir.Value * step), "Nudge Nodes", keepNodes: true, keepSegment: false);
        return true;
    }

    public override void Cancel()
    {
        _mode = Mode.None;
        _work = null;
        Redraw();
    }

    // ---- Which vector / node the commands act on ----

    /// <summary>The vector menu was opened on one; else the one under the cursor; else the selected one; else one next to the selected node.</summary>
    private SegRef? CurrentSegment
    {
        get
        {
            if (_menu is { } m) return m;
            if (_hover is { } h) return h;
            if (_selSeg is { } s) return s;
            if (_selected.Count == 1)
            {
                var n = _selected.First();
                var l = Pieces[n.Piece].Loops[n.Loop];
                int seg = l.Outgoing(n.Node) >= 0 ? l.Outgoing(n.Node) : l.Incoming(n.Node);
                if (seg >= 0) return new SegRef(n.Piece, n.Loop, seg, l.Outgoing(n.Node) >= 0 ? 0 : 1);
            }
            return null;
        }
    }

    private Segment? CurrentSegmentData => CurrentSegment is { } s ? Pieces[s.Piece].Loops[s.Loop].Segments[s.Seg] : null;

    /// <summary>The node an operation acts on: the single selected node, or the end of the current vector nearest the click.</summary>
    private NodeRef? CurrentNode
    {
        get
        {
            if (_menu is null && _selected.Count == 1) return _selected.First();
            if (CurrentSegment is not { } s) return null;
            var l = Pieces[s.Piece].Loops[s.Loop];
            int k = s.T < 0.5 ? s.Seg : s.Seg + 1;
            if (l.Closed) k %= l.Segments.Count;
            return new NodeRef(s.Piece, s.Loop, k);
        }
    }

    private Loop? CurrentLoop => CurrentNode is { } n ? Pieces[n.Piece].Loops[n.Loop] : null;

    /// <summary>The nodes node commands act on: every selected node, else the current one.</summary>
    private List<NodeRef> TargetNodes() => _selected.Count > 0 && _menu is null ? _selected.ToList() : CurrentNode is { } n ? [n] : [];

    public bool CanInsert => CurrentSegment is not null;
    public bool CanToLine => CurrentSegmentData is { Kind: not SegmentKind.Line };
    public bool CanToArc => CurrentSegmentData is { Kind: not SegmentKind.Arc };
    public bool CanToBezier => CurrentSegmentData is { Kind: not SegmentKind.Cubic };
    public bool CanBreakLoop => CurrentSegment is not null || CurrentNode is not null;
    public bool CanSetStartEnd => CurrentNode is { } n && CurrentLoop is { } l &&
                                  (l.Closed ? n.Node != 0 : n.Node == l.NodeCount - 1);
    public bool CanJoinStartEnd => JoinPair() is not null || CurrentLoop is { Closed: false };
    public bool CanDelete => (_menu is null && _selected.Count > 0) || CurrentSegment is not null;
    public bool CanMakeSmooth => TargetNodes().Any(n => PathEditing.KindOf(Pieces[n.Piece].Loops[n.Loop], n.Node) != NodeKind.End);
    public bool CanMakeCorner => TargetNodes().Any(n => PathEditing.KindOf(Pieces[n.Piece].Loops[n.Loop], n.Node) is NodeKind.Smooth or NodeKind.Symmetric);
    public bool CanMakeSharp => CurrentSegment is { } s && CurrentSegmentData is { Kind: SegmentKind.Arc } &&
                                Pieces[s.Piece].Loops[s.Loop] is var l && (l.Closed || (s.Seg > 0 && s.Seg < l.Segments.Count - 1));

    // ---- Commands (vector menu, Context Bar, letter keys) ----

    /// <summary>Insert: a new node on the current vector, at the clicked point.</summary>
    public void Insert()
    {
        if (CurrentSegment is not { } s) return;
        Edit(s.Piece, "Insert Node", loops => PathEditing.Insert(loops[s.Loop], s.Seg, s.T));
    }

    public void ToLine() => EditSegment("To Line", PathEditing.ToLine);
    public void ToArc() => EditSegment("To Arc", PathEditing.ToArc);
    public void ToBezier() => EditSegment("To Bezier", PathEditing.ToBezier);

    /// <summary>Break Loop: opens the loop at the selected node, or at the clicked point of the current vector.</summary>
    public void BreakLoop()
    {
        if (_menu is null && _selected.Count == 1)
        {
            var n = _selected.First();
            Edit(n.Piece, "Break Loop", loops => PathEditing.BreakAt(loops, n.Loop, n.Node));
        }
        else if (CurrentSegment is { } s)
        {
            Edit(s.Piece, "Break Loop", loops =>
            {
                PathEditing.Insert(loops[s.Loop], s.Seg, s.T);
                PathEditing.BreakAt(loops, s.Loop, s.Seg + 1);
            });
        }
    }

    public void SetStartEnd()
    {
        if (CurrentNode is not { } n || !CanSetStartEnd) return;
        Edit(n.Piece, "Set Start/End", loops => PathEditing.SetStart(loops[n.Loop], n.Node));
    }

    /// <summary>Two selected end nodes of open loops, if that is what is selected.</summary>
    private (NodeRef A, NodeRef B)? JoinPair()
    {
        if (_selected.Count != 2) return null;
        var pair = _selected.OrderBy(n => n.Loop).ThenBy(n => n.Node).ToArray();
        if (pair[0].Piece != pair[1].Piece) return null;
        bool IsEnd(NodeRef n) { var l = Pieces[n.Piece].Loops[n.Loop]; return !l.Closed && (n.Node == 0 || n.Node == l.NodeCount - 1); }
        if (!IsEnd(pair[0]) || !IsEnd(pair[1])) return null;
        if (pair[0].Loop == pair[1].Loop && Pieces[pair[0].Piece].Loops[pair[0].Loop].NodeCount < 3) return null;
        return (pair[0], pair[1]);
    }

    /// <summary>
    /// Join Start/End: with two end nodes selected they are joined (merging two open loops, or closing one loop on itself);
    /// otherwise the current open loop is closed.
    /// </summary>
    public void JoinStartEnd()
    {
        if (JoinPair() is { } p)
        {
            Edit(p.A.Piece, "Join Start/End", loops => PathEditing.JoinLoops(loops, p.A.Loop, p.A.Node == 0, p.B.Loop, p.B.Node == 0));
            return;
        }
        if (CurrentNode is not { } n || !CanJoinStartEnd) return;
        Edit(n.Piece, "Join Start/End", loops => PathEditing.Join(loops[n.Loop]));
    }

    /// <summary>Delete: the selected nodes (keyboard), or the vector the menu was opened on / under the cursor.</summary>
    public void Delete()
    {
        if (_menu is null && _selected.Count > 0)
        {
            var work = Clones();
            DeleteNodes(work, _selected);
            Apply(work, "Delete Nodes", keepNodes: false, keepSegment: false);
            return;
        }
        if (CurrentSegment is { } s)
            Edit(s.Piece, "Delete Vector", loops => PathEditing.DeleteSegment(loops, s.Loop, s.Seg));
    }

    /// <summary>Make Corner: the handles at the selected nodes are pulled in, so the vectors leave each node independently.</summary>
    public void MakeCorner() => EditNodes("Make Corner", PathEditing.MakeCorner);

    /// <summary>Make Smooth: the vectors on both sides of the selected nodes leave along one line (lines and arcs become Béziers).</summary>
    public void MakeSmooth() => EditNodes("Make Smooth", PathEditing.MakeSmooth);

    /// <summary>Make Symmetric: smooth, with both handles equally long.</summary>
    public void MakeSymmetric() => EditNodes("Make Symmetric", PathEditing.MakeSymmetric);

    /// <summary>Sharp: an arc (a rounded corner) becomes two lines that continue the vectors on either side.</summary>
    public void MakeSharp()
    {
        if (!CanMakeSharp || CurrentSegment is not { } s) return;
        Edit(s.Piece, "Make Sharp Corner", loops => PathEditing.MakeSharp(loops[s.Loop], s.Seg));
    }

    private void EditNodes(string description, Func<Loop, int, bool> op)
    {
        var nodes = TargetNodes();
        if (nodes.Count == 0) return;
        var work = Clones();
        foreach (var n in nodes) op(work[n.Piece][n.Loop], n.Node);
        Apply(work, description, keepNodes: true, keepSegment: true);
    }

    private void EditSegment(string description, Action<Loop, int> edit)
    {
        if (CurrentSegment is not { } s) return;
        var work = Clones();
        edit(work[s.Piece][s.Loop], s.Seg);
        Apply(work, description, keepNodes: false, keepSegment: true, s);
    }

    private void Edit(int piece, string description, Action<List<Loop>> edit)
    {
        var work = Clones();
        edit(work[piece]);
        Apply(work, description, keepNodes: false, keepSegment: false);
    }

    private void Apply(List<List<Loop>> work, string description, bool keepNodes, bool keepSegment, SegRef? segment = null)
    {
        var nodes = keepNodes ? _selected.ToList() : [];
        var seg = keepSegment ? segment ?? _selSeg : null;
        _menu = null;
        Commit(work, description);
        _selected.Clear();
        foreach (var n in nodes) if (Valid(n)) _selected.Add(n);
        _selSeg = seg is { } s && Valid(s) ? s : null;
        Redraw();
    }

    /// <summary>Called when the vector menu closes, so later keys act on the vector under the cursor again.</summary>
    public void MenuClosed() => _menu = null;

    // ---- Readouts for the Context Bar ----

    /// <summary>The nodes that X/Y act on: the selected ones, else none.</summary>
    public IReadOnlyList<Vec2> SelectedNodePositions() =>
        _selected.Select(n => Pieces[n.Piece].World.Transform(Pieces[n.Piece].Loops[n.Loop].Node(n.Node))).ToList();

    /// <summary>The position X/Y show: the node, or the center of the selected nodes' bounding box. Null with no nodes selected.</summary>
    public Vec2? AnchorPosition()
    {
        var pts = SelectedNodePositions();
        if (pts.Count == 0) return null;
        if (pts.Count == 1) return pts[0];
        var r = Rect2.Empty;
        foreach (var p in pts) r = r.Union(p);
        return r.Center;
    }

    /// <summary>Context Bar X/Y: moves the selected nodes so <see cref="AnchorPosition"/> lands on <paramref name="docPoint"/>.</summary>
    public void SetAnchorPosition(Vec2 docPoint)
    {
        if (AnchorPosition() is not { } now || _selected.Count == 0) return;
        Apply(MovedNodes(docPoint - now), "Move Nodes", keepNodes: true, keepSegment: false);
    }

    public NodeKind? SelectedNodeKind() =>
        _selected.Count == 1 ? PathEditing.KindOf(Pieces[_selected.First().Piece].Loops[_selected.First().Loop], _selected.First().Node) : null;

    /// <summary>What the bar shows for the selected vector: length, radius and angle of an arc or line, and the tangency error to the next vector.</summary>
    public SegmentReadout? Readout()
    {
        if ((_selSeg ?? (_selected.Count == 1 ? CurrentSegment : null)) is not { } s) return null;
        var piece = Pieces[s.Piece];
        var l = piece.Loops[s.Loop];
        double scale = piece.World.AverageScale;
        var seg = l.Segments[s.Seg];
        var start = l.SegmentStart(s.Seg);
        double? radius = null, angle = null;
        if (seg.Kind == SegmentKind.Arc && Arcs.TryCircle(start, seg.C1, seg.End, out var c, out var r))
        {
            radius = r * scale;
            angle = Math.Abs(Arcs.Angles(start, seg.C1, seg.End, c).Sweep) * 180 / Math.PI;
        }
        else if (seg.Kind == SegmentKind.Line)
        {
            var d = piece.World.TransformVector(seg.End - start);
            angle = Math.Atan2(-d.Y, d.X) * 180 / Math.PI;   // user space has y up
        }
        double? tangency = null;
        int next = s.Seg + 1 < l.Segments.Count ? s.Seg + 1 : l.Closed ? 0 : -1;
        if (next >= 0 && next != s.Seg)
        {
            Vec2 t1 = l.EndTangent(s.Seg), t2 = l.StartTangent(next);
            if (t1.Length > 1e-12 && t2.Length > 1e-12)
                tangency = Math.Abs(Math.Atan2(Vec2.Cross(t1, t2), Vec2.Dot(t1, t2))) * 180 / Math.PI;
        }
        var kind = seg.Kind;
        var from = piece.World.Transform(start);
        return new SegmentReadout(kind, l.SegmentLength(s.Seg) * scale, radius, angle, tangency, from);
    }

    // ---- Overlay ----

    public override void BuildOverlay(List<OverlayItem> items)
    {
        if (HasTarget)
        {
            var loopsPerPiece = _work ?? Pieces.Select(p => p.Loops).ToList();
            var pieces = Pieces.Select((p, i) => new VectorPiece(p.PartIndex, p.World, loopsPerPiece[i], p.FillRule)).ToList();
            var small = _work is null ? SmallNodeSet() : SmallNodesOf(_work);   // while dragging the lengths change, so measure the copies
            var nodes = new List<VectorNodeMark>();
            for (int pi = 0; pi < pieces.Count; pi++)
                for (int li = 0; li < pieces[pi].Loops.Count; li++)
                {
                    var l = pieces[pi].Loops[li];
                    for (int k = 0; k < l.NodeCount; k++)
                    {
                        var node = new NodeRef(pi, li, k);
                        nodes.Add(new VectorNodeMark(pieces[pi].World.Transform(l.Node(k)), l.IsCorner(k), _selected.Contains(node), k == 0, small.Contains(node)));
                    }
                }
            var handles = VisibleHandles(loopsPerPiece)
                .Select(h => (pieces[h.Piece].World.Transform(h.Node), pieces[h.Piece].World.Transform(h.Position)))
                .ToList();
            PathGeometry? highlight = null, selected = null;
            if (_hover is { } hv && _work is null && Valid(hv))
                highlight = pieces[hv.Piece].Loops[hv.Loop].SegmentGeometry(hv.Seg).Transform(pieces[hv.Piece].World);
            if (_selSeg is { } sv && Valid(sv))
                selected = pieces[sv.Piece].Loops[sv.Loop].SegmentGeometry(sv.Seg).Transform(pieces[sv.Piece].World);
            items.Add(new VectorEditOverlay(OutlineView.Loops(pieces), nodes, handles, highlight, selected));
        }
        if (_mode == Mode.Marquee && _moved)
        {
            // A box that will delete small nodes looks different from one that only selects, so releasing it is never a surprise.
            bool deletes = BoxDeletesSmallNodes && HasTarget && SmallNodesIn(_marquee).Count > 0;
            items.Add(new MarqueeOverlay(_marquee, deletes ? MarqueeKind.Delete : MarqueeKind.Select));
        }
    }
}

/// <summary>The Context Bar's readouts for the selected vector (Radius, Length, Angle, Tangency). Lengths in document points, angles in degrees.</summary>
public readonly record struct SegmentReadout(SegmentKind Kind, double Length, double? Radius, double? Angle, double? Tangency, Vec2 Start);
