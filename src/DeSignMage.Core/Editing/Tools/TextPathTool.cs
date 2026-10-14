using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Effects ▸ Text on Path (TASKS C.28, <c>notes/effects/text-on-path.md</c>): edits the path a live text follows. The text is flowing along its curve
/// the moment the tool opens; the curve's nodes and Bézier handles are shown and can be dragged, and the text re-flows as they move.
/// <list type="bullet">
/// <item>Drag a node or a handle (the handles of the selected node and vector show); click a vector to select it; click empty canvas to select nothing.</item>
/// <item><b>Add Curve</b> appends a Bézier to the end of the path (as the spec: horizontally by the last vector's length at the first node's height, the first
/// handle continuing the last tangent by a quarter of it). <b>Delete Curve</b> removes the last vector (the path keeps at least one). <b>Bezier</b> / <b>Arc</b> convert the
/// selected vector (the one after the selected node; the first when nothing is selected).</item>
/// <item>The numbers (spacing, offset, side, flip, compensation) are in the Context Bar. <b>Remove</b> takes the path off (the text goes straight again).</item>
/// <item>Every change is one undo step (<see cref="SetTextOnPathCommand"/>): a drag is previewed on the object and recorded when the button is released.
/// <b>Done</b> (Enter, or Esc when nothing is being dragged) leaves the tool; there is nothing to apply, the text is already on its path.</item>
/// </list>
/// </summary>
public sealed class TextPathTool : Tool
{
    private enum Mode { None, Node, Handle }

    private TextObject? _text;
    private int _node = -1, _segment = -1;            // the selected node and vector (-1 = none)
    private Mode _mode;
    private Vec2 _startScreen;
    private bool _moved;
    private int _dragHandleSegment, _dragHandleWhich;
    private TextOnPath? _before;                      // the settings when a drag began (so the command records the whole drag)
    private Loop? _original;

    public override string Id => "textpath";
    public override string DisplayName => "Text on Path";
    public override CursorKind Cursor => CursorKind.Cross;

    public override string Hint => _text is null
        ? "TEXT ON PATH: Select one text, then pick Text on Path in the Effects menu"
        : "TEXT ON PATH: Drag the nodes and handles of the path   Click a vector to select it   Enter: Done";

    /// <summary>Raised when the target, its path or its settings changed (the Context Bar refreshes).</summary>
    public event EventHandler? Changed;

    /// <summary>The text whose path is open, or null.</summary>
    public TextObject? Target => _text;

    public TextOnPath? Settings => _text?.OnPath;

    public int SelectedNode => _node;

    /// <summary>The vector that Bezier / Arc act on: the clicked one, else the one after the selected node (the one before it for the last node), else the first.</summary>
    public int SelectedSegment
    {
        get
        {
            if (PathLoop() is not { } l || l.Segments.Count == 0) return -1;
            if (_segment >= 0 && _segment < l.Segments.Count) return _segment;
            if (_node >= 0) return _node < l.Segments.Count ? _node : l.Segments.Count - 1;
            return 0;
        }
    }

    private Loop? PathLoop() => _text?.OnPath is { } s ? PathEditing.ToLoops(s.Path).FirstOrDefault() : null;

    public bool PathIsClosed => PathLoop()?.Closed == true;
    public int SegmentCount => PathLoop()?.Segments.Count ?? 0;
    public bool CanAddCurve => _text?.OnPath is not null && PathLoop() is { Closed: false };
    public bool CanDeleteCurve => PathLoop() is { Closed: false, Segments.Count: >= 2 };
    public bool CanToBezier => PathLoop() is { } l && SelectedSegment is var i and >= 0 && l.Segments[i].Kind != SegmentKind.Cubic;
    public bool CanToArc => PathLoop() is { } l && SelectedSegment is var i and >= 0 && l.Segments[i].Kind != SegmentKind.Arc;

    // ---- Opening and closing ----

    /// <summary>Opens the path of <paramref name="text"/> (it must have one). False if it has none.</summary>
    public bool Begin(TextObject text)
    {
        if (text.OnPath is null) return false;
        _text = text;
        _node = _segment = -1;
        _mode = Mode.None;
        Session.Selection.Set(text);
        Changed?.Invoke(this, EventArgs.Empty);
        Redraw();
        return true;
    }

    public override void Activate()
    {
        Session.Document.Changed += OnDocumentChanged;
        if (_text is null && Session.Selection.Items is [TextObject { OnPath: not null } only]) Begin(only);
    }

    public override void Deactivate()
    {
        Cancel();
        Session.Document.Changed -= OnDocumentChanged;
        _text = null;
        _node = _segment = -1;
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_mode != Mode.None || _text is null) return;
        if (!Session.Document.Contains(_text) || _text.OnPath is null)
        {
            // Undo took the path away (or the text): there is nothing left to edit.
            _text = null;
            Session.SetTool("select");
            return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        Redraw();
    }

    /// <summary>Leaves the tool; the text stays selected and on its path.</summary>
    public void Done()
    {
        Session.SetTool("select");
    }

    // ---- Changing the settings (each call is one undo step) ----

    /// <summary>Applies a change to the text's settings (spacing, offset, side, flip, compensation, the path itself) as one undo step.</summary>
    public bool Update(Func<TextOnPath, TextOnPath> change, string description = "Text on Path")
    {
        if (_text?.OnPath is not { } now) return false;
        var next = change(now);
        if (next == now) return false;
        Session.History.Execute(new SetTextOnPathCommand(_text, next, description));
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Remove: the text goes straight again (undoable) and the tool closes.</summary>
    public void Remove()
    {
        if (_text is null) return;
        var t = _text;
        Session.History.Execute(new SetTextOnPathCommand(t, null, "Remove Text on Path"));
        _text = null;
        Session.SetTool("select");
        Session.Selection.Set(t);
    }

    private bool EditPath(Action<Loop> edit, string description)
    {
        if (PathLoop() is not { } l) return false;
        edit(l);
        var path = PathEditing.FromLoops([l], FillRule.NonZero);
        return Update(s => s with { Path = path }, description);
    }

    /// <summary>Add Curve: appends a Bézier at the end of the path, as the spec describes.</summary>
    public bool AddCurve()
    {
        if (!CanAddCurve) return false;
        bool ok = EditPath(l =>
        {
            int last = l.Segments.Count - 1;
            var from = l.Segments[last].End;
            double len = Math.Max(l.SegmentLength(last), 1e-6);
            var tangent = l.EndTangent(last);
            var end = new Vec2(from.X + len, l.Start.Y);                       // horizontally by the last vector's length, at the first node's height
            l.Segments.Add(Segment.Cubic(from + tangent * (len / 4), new Vec2(end.X - len / 4, end.Y), end));
        }, "Add Curve");
        if (ok) { _node = SegmentCount; _segment = SegmentCount - 1; }
        return ok;
    }

    /// <summary>Delete Curve: removes the last vector (the path keeps at least one).</summary>
    public bool DeleteCurve()
    {
        if (!CanDeleteCurve) return false;
        bool ok = EditPath(l => l.Segments.RemoveAt(l.Segments.Count - 1), "Delete Curve");
        if (ok) { _node = -1; _segment = -1; }
        return ok;
    }

    public bool ToBezier() => CanToBezier && SelectedSegment is var i && EditPath(l => PathEditing.ToBezier(l, i), "To Bezier");

    public bool ToArc() => CanToArc && SelectedSegment is var i && EditPath(l => PathEditing.ToArc(l, i), "To Arc");

    // ---- Picking ----

    private Affine2 World => _text!.GetWorldTransform();

    private Vec2 ToLocal(Vec2 doc) => World.IsInvertible ? World.Invert().Transform(doc) : doc;

    private Vec2 ScreenOf(Vec2 local) => Session.Viewport.ToScreen(World.Transform(local));

    private int NodeAt(Loop l, Vec2 screen)
    {
        int best = -1;
        double bestD = Session.HandleRadiusPx;
        for (int k = 0; k < l.NodeCount; k++)
        {
            double d = Vec2.Distance(ScreenOf(l.Node(k)), screen);
            if (d <= bestD) { bestD = d; best = k; }
        }
        return best;
    }

    /// <summary>The handles that show: those of the selected node and of the selected vector. (segment, which, position, the node it hangs from)</summary>
    private List<(int Segment, int Which, Vec2 Position, Vec2 Node)> Handles(Loop l)
    {
        var list = new List<(int, int, Vec2, Vec2)>();
        void Add(int seg, int which)
        {
            if (seg < 0 || seg >= l.Segments.Count) return;
            var s = l.Segments[seg];
            if (s.Kind == SegmentKind.Cubic) list.Add((seg, which, which == 1 ? s.C1 : s.C2, which == 1 ? l.SegmentStart(seg) : s.End));
            else if (s.Kind == SegmentKind.Arc && which == 1) list.Add((seg, 1, s.C1, s.C1));
        }
        if (_node >= 0)
        {
            Add(l.Outgoing(_node), 1);
            Add(l.Incoming(_node), 2);
            if (l.Incoming(_node) is var a and >= 0 && l.Segments[a].Kind == SegmentKind.Arc) Add(a, 1);
        }
        int sel = SelectedSegment;
        if (sel >= 0)
        {
            Add(sel, 1);
            Add(sel, 2);
        }
        return list.Distinct().ToList();
    }

    private (int Segment, int Which)? HandleAt(Loop l, Vec2 screen)
    {
        foreach (var (seg, which, pos, _) in Handles(l))
            if (Vec2.Distance(ScreenOf(pos), screen) <= Session.HandleRadiusPx) return (seg, which);
        return null;
    }

    private int SegmentAt(Loop l, Vec2 doc)
    {
        if (!World.IsInvertible) return -1;
        var local = ToLocal(doc);
        double tol = Session.Viewport.ToDocLength(Math.Max(Session.PickTolerancePx, 6)) / Math.Max(World.AverageScale, 1e-12);
        int best = -1;
        double bestD = double.PositiveInfinity;
        for (int i = 0; i < l.Segments.Count; i++)
        {
            var (_, d) = l.Closest(i, local);
            if (d <= tol && d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // ---- Pointer ----

    public override void PointerDown(PointerInput e)
    {
        if (_text is null || PathLoop() is not { } l) return;
        _startScreen = e.Screen;
        _moved = false;
        _before = _text.OnPath;
        _original = l.Clone();

        if (HandleAt(l, e.Screen) is { } h)
        {
            _mode = Mode.Handle;
            (_dragHandleSegment, _dragHandleWhich) = h;
            return;
        }
        if (NodeAt(l, e.Screen) is >= 0 and var n)
        {
            _node = n;
            _segment = -1;
            _mode = Mode.Node;
            Changed?.Invoke(this, EventArgs.Empty);
            Redraw();
            return;
        }
        _segment = SegmentAt(l, e.Doc);
        _node = -1;
        _mode = Mode.None;
        Changed?.Invoke(this, EventArgs.Empty);
        Redraw();
    }

    public override void PointerMove(PointerInput e)
    {
        if (_text is null || _mode == Mode.None || _original is null) return;
        if (!_moved && Vec2.Distance(e.Screen, _startScreen) < Session.ClickThresholdPx) return;
        _moved = true;
        var work = _original.Clone();
        var to = ToLocal(Session.SnapPoint(e.Doc));
        if (_mode == Mode.Node) PathEditing.MoveNode(work, _node, to);
        else PathEditing.MoveHandle(work, _dragHandleSegment, _dragHandleWhich, to);
        // The preview: the object itself follows the pointer; nothing is recorded until the button is released.
        _text.OnPath = _before! with { Path = PathEditing.FromLoops([work], FillRule.NonZero) };
        Session.Document.NotifyChanged();
    }

    public override void PointerUp(PointerInput e)
    {
        if (_mode != Mode.None) PointerMove(e);
        if (_mode != Mode.None && _moved && _text is not null && _before is not null)
        {
            var final = _text.OnPath;
            string what = _mode == Mode.Node ? "Move Path Node" : "Move Path Handle";
            _text.OnPath = _before;   // silently back to the start, so the command records the whole drag
            _mode = Mode.None;
            if (final is not null && final != _before) Session.History.Execute(new SetTextOnPathCommand(_text, final, what));
            else Session.Document.NotifyChanged();
        }
        _mode = Mode.None;
        _original = null;
        Changed?.Invoke(this, EventArgs.Empty);
        Redraw();
    }

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        if (_text is null) return false;
        switch (key)
        {
            case EditorKey.Escape when _mode != Mode.None:
                Cancel();
                return true;
            case EditorKey.Escape:
            case EditorKey.Enter:
                Done();
                return true;
        }
        return false;
    }

    public override void Cancel()
    {
        if (_mode != Mode.None && _text is not null && _before is not null) _text.OnPath = _before;   // abandon a drag, silently
        bool wasDragging = _mode != Mode.None;
        _mode = Mode.None;
        _original = null;
        if (wasDragging && _text is not null) Session.Document.NotifyChanged();
    }

    // ---- Overlay ----

    public override void BuildOverlay(List<OverlayItem> items)
    {
        base.BuildOverlay(items);
        if (_text is null || PathLoop() is not { } l) return;
        var w = World;
        var path = PathEditing.FromLoops([l], FillRule.NonZero).Transform(w);
        var nodes = new List<VectorNodeMark>();
        for (int k = 0; k < l.NodeCount; k++) nodes.Add(new VectorNodeMark(w.Transform(l.Node(k)), l.IsCorner(k), k == _node, Start: k == 0));
        var handles = Handles(l).Select(h => (w.Transform(h.Node), w.Transform(h.Position))).ToList();
        PathGeometry? selected = SelectedSegment is >= 0 and var i && (_segment >= 0 || _node >= 0) ? l.SegmentGeometry(i).Transform(w) : null;
        items.Add(new VectorEditOverlay([new OutlineLoop(path, LoopKind.Open)], nodes, handles, null, selected));
    }
}
