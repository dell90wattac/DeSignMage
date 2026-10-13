using System.Globalization;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Loop Edit tool (key 3 in some menu builds): works on whole loops (subpaths) of the open graphic.
/// <list type="bullet">
/// <item>Click a vector of a loop to select the whole loop (dotted red); Shift adds. The black square on each loop is its start/end point.</item>
/// <item>Drag to move the loop on its own inside its graphic; Ctrl while dragging drops a copy (red dotted preview) and leaves
/// the original. Del deletes it, Ctrl+X / Ctrl+C put just that loop on the clipboard (paste makes it a graphic).</item>
/// <item>Double-click a loop: the loop menu (Reverse Dir., Open Loop, Close Loop, To Circle, Cut, Copy, Delete, Break Out).</item>
/// <item>Empty canvas: click opens another object, or deselects; a drag selects the loops it fully encloses.</item>
/// </list>
/// Every change is one undo step through <see cref="SetVectorsCommand"/>.
/// </summary>
public sealed class LoopEditTool : PathEditToolBase
{
    private enum Mode { None, Move, Marquee }

    private readonly HashSet<LoopRef> _selected = [];
    private LoopRef? _hover, _menu;
    private Mode _mode;
    private bool _moved, _copying;
    private Vec2 _startDoc, _startScreen;
    private Rect2 _marquee;
    private List<List<Loop>>? _work;
    private int[] _originalCounts = [];

    /// <summary>View ▸ Show Loop Direction: loops drawn blue when clockwise and red when counter-clockwise (black = broken).</summary>
    public bool ShowDirection
    {
        get => _showDirection;
        set { if (_showDirection == value) return; _showDirection = value; Redraw(); OptionsChanged?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>The loop Context Bar's "Show Loop Numbers": each loop's sequence number is drawn at its start point.</summary>
    public bool ShowNumbers
    {
        get => _showNumbers;
        set { if (_showNumbers == value) return; _showNumbers = value; Redraw(); OptionsChanged?.Invoke(this, EventArgs.Empty); }
    }

    private bool _showDirection, _showNumbers;
    public event EventHandler? OptionsChanged;

    public override string Id => "loop";
    public override string DisplayName => "Loop Edit";

    public override string Hint => !HasTarget
        ? "To EDIT LOOPS: Click an object   To LAYOUT: Pick the Layout tool (1)"
        : _mode == Mode.Move
            ? "Move loop: Ctrl = drop a copy"
            : "To SELECT a loop: Click it   Del / Drag = move / Ctrl+Drag = duplicate   Double-click: loop menu";

    public override CursorKind Cursor => _mode == Mode.Move && _moved
        ? (_copying ? CursorKind.Copy : CursorKind.Move)
        : Hovering ? CursorKind.SelectHit : CursorKind.Arrow;

    public int SelectedLoopCount => _selected.Count;
    public bool HasSelection => _selected.Count > 0;

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
        _menu = null;
    }

    protected override void OnRebuilt()
    {
        _selected.RemoveWhere(l => !Valid(l));
        if (_hover is { } h && !Valid(h)) _hover = null;
        if (_menu is { } m && !Valid(m)) _menu = null;
        _work = null;
        if (!HasTarget) { _selected.Clear(); _hover = _menu = null; }
    }

    private bool Valid(LoopRef l) => l.Piece < Pieces.Count && l.Loop < Pieces[l.Piece].Loops.Count;

    // ---- Pointer ----

    public override void PointerDown(PointerInput e)
    {
        _startDoc = e.Doc;
        _startScreen = e.Screen;
        _moved = _copying = false;
        _menu = null;

        if (HasTarget && SegmentAt(e.Doc) is { } seg)
        {
            var loop = new LoopRef(seg.Piece, seg.Loop);
            if (e.Shift) { if (!_selected.Remove(loop)) _selected.Add(loop); }
            else if (!_selected.Contains(loop)) { _selected.Clear(); _selected.Add(loop); }
            if (e.ClickCount >= 2)
            {
                _menu = loop;
                _mode = Mode.None;
                Session.RequestContextMenu(Target!, e.Screen, MenuKind.Loop);
                return;
            }
            _mode = _selected.Contains(loop) ? Mode.Move : Mode.None;
            Redraw();
            return;
        }

        if (TryOpenObjectAt(e))
        {
            _mode = Mode.None;
            return;
        }

        if (!e.Shift) _selected.Clear();
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
        if (_mode == Mode.Move)
        {
            _copying = e.Control;   // Ctrl at any time during the drag: a copy is previewed and dropped at the release point
            _work = Moved(SnappedDelta(e.Doc - _startDoc, e.Control), _copying);
        }
        else _marquee = Rect2.FromPoints(_startDoc, e.Doc);
        Redraw();
    }

    private void UpdateHover(PointerInput e)
    {
        LoopRef? hover = null;
        if (HasTarget && SegmentAt(e.Doc) is { } s) hover = new LoopRef(s.Piece, s.Loop);
        if (hover != _hover)
        {
            _hover = hover;
            Hovering = hover is not null;
            Redraw();
        }
    }

    public override void PointerUp(PointerInput e)
    {
        if (_mode != Mode.None) PointerMove(e);
        if (_mode == Mode.Move && _moved && _work is not null)
        {
            bool copy = _copying;
            var work = _work;
            var counts = _originalCounts;
            var keep = _selected.ToList();
            _mode = Mode.None;
            Apply(work, copy ? "Copy Loop" : "Move Loop");
            if (copy)   // the copies are what the user just dropped: they become the selection
            {
                _selected.Clear();
                for (int pi = 0; pi < Pieces.Count; pi++)
                    for (int li = counts[pi]; li < Pieces[pi].Loops.Count; li++)
                        _selected.Add(new LoopRef(pi, li));
            }
            else
            {
                _selected.Clear();
                foreach (var l in keep) if (Valid(l)) _selected.Add(l);
            }
        }
        else if (_mode == Mode.Marquee && _moved)
        {
            for (int pi = 0; pi < Pieces.Count; pi++)
                for (int li = 0; li < Pieces[pi].Loops.Count; li++)
                    if (_marquee.Contains(Pieces[pi].Loops[li].ToGeometry().Transform(Pieces[pi].World).GetBounds()))
                        _selected.Add(new LoopRef(pi, li));
        }
        _mode = Mode.None;
        _work = null;
        Redraw();
    }

    /// <summary>The drag offset, snapped to guides by the moving loops' bounds unless Ctrl is held (Ctrl also means "copy" here, so only the copy gesture suspends it).</summary>
    private Vec2 SnappedDelta(Vec2 delta, bool control)
    {
        if (control || _selected.Count == 0) return delta;
        var bounds = Rect2.Empty;
        foreach (var l in _selected)
            if (Valid(l)) bounds = bounds.Union(Pieces[l.Piece].Loops[l.Loop].ToGeometry().Transform(Pieces[l.Piece].World).GetBounds());
        return bounds.IsEmpty ? delta : Session.SnapMoveDelta(bounds, delta);
    }

    /// <summary>Selected loops moved by a document-space delta (applied to fresh copies, so no drift), or copied there.</summary>
    private List<List<Loop>> Moved(Vec2 docDelta, bool copy)
    {
        var work = Clones();
        _originalCounts = work.Select(w => w.Count).ToArray();
        foreach (var group in _selected.Where(Valid).GroupBy(l => l.Piece))
        {
            var local = ToLocalVector(Pieces[group.Key], docDelta);
            foreach (var l in group.OrderBy(l => l.Loop))
            {
                var loop = work[group.Key][l.Loop];
                if (copy)
                {
                    var c = loop.Clone();
                    c.Translate(local);
                    work[group.Key].Add(c);
                }
                else loop.Translate(local);
            }
        }
        return work;
    }

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case EditorKey.Escape:
                if (_mode != Mode.None) Cancel();
                else { _selected.Clear(); _menu = null; Redraw(); }
                return true;
            case EditorKey.Delete:
            case EditorKey.Back:
                if (CanDelete) Delete();
                return true;   // never deletes the whole object while editing its loops
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
        var keep = _selected.ToList();
        Apply(Moved(dir.Value * step, copy: false), "Nudge Loops");
        RestoreSelection(keep);
        return true;
    }

    public override void Cancel()
    {
        _mode = Mode.None;
        _work = null;
        Redraw();
    }

    // ---- Commands (Context Bar, loop menu) ----

    /// <summary>The loops commands act on: the selected ones, else the one the menu was opened on / is under the cursor.</summary>
    private List<LoopRef> TargetLoops()
    {
        if (_selected.Count > 0) return _selected.Where(Valid).OrderBy(l => l.Piece).ThenBy(l => l.Loop).ToList();
        if (_menu is { } m && Valid(m)) return [m];
        if (_hover is { } h && Valid(h)) return [h];
        return [];
    }

    private Loop LoopOf(LoopRef l) => Pieces[l.Piece].Loops[l.Loop];

    public bool CanDelete => TargetLoops().Count > 0;
    public bool CanReverse => TargetLoops().Count > 0;
    public bool CanOpen => TargetLoops().Any(l => LoopOf(l).Closed);
    public bool CanClose => TargetLoops().Any(l => !LoopOf(l).Closed);
    public bool CanToCircle => TargetLoops().Count > 0;
    public bool CanCopy => TargetLoops().Count > 0;
    public bool CanBreakOut => TargetLoops().Count > 0;
    public bool CanReorder => HasTarget && Pieces.Any(p => p.Loops.Count > 1);

    /// <summary>Reverse Dir.: flips the loop between clockwise and counter-clockwise.</summary>
    public void ReverseDirection() => EditLoops("Reverse Loop", (loops, l) => PathEditing.Reverse(loops[l]));

    /// <summary>Open Loop: opens each closed loop at its start/end point (nothing is removed).</summary>
    public void OpenLoop() => EditLoops("Open Loop", (loops, l) => { if (loops[l].Closed) PathEditing.BreakAt(loops, l, 0); });

    /// <summary>Close Loop: joins the end of each open loop back to its start.</summary>
    public void CloseLoop() => EditLoops("Close Loop", (loops, l) => { if (!loops[l].Closed) PathEditing.Join(loops[l]); });

    /// <summary>To Circle: each loop is replaced by a circle of about the same size and place.</summary>
    public void ToCircle() => EditLoops("To Circle", (loops, l) => PathEditing.ToCircle(loops[l]));

    /// <summary>Delete Loop: removes the loops (undo brings them back). Deleting the last loop deletes the object.</summary>
    public void Delete()
    {
        var targets = TargetLoops();
        if (targets.Count == 0) return;
        var work = Clones();
        foreach (var l in targets.OrderByDescending(l => l.Loop)) work[l.Piece].RemoveAt(l.Loop);
        Apply(work, targets.Count == 1 ? "Delete Loop" : "Delete Loops");
        _selected.Clear();
    }

    /// <summary>Copy: just these loops go on the clipboard; Paste makes them a graphic (paste in place).</summary>
    public void Copy()
    {
        var objs = BuildLoopObjects(TargetLoops());
        if (objs.Count > 0) Session.SetClipboard(objs);
    }

    /// <summary>Cut: Copy, then Delete Loop.</summary>
    public void Cut()
    {
        var targets = TargetLoops();
        if (targets.Count == 0) return;
        Copy();
        Delete();
    }

    /// <summary>
    /// Break Out: each selected loop becomes a graphic of its own (a separate object just above this one, same color and
    /// place) and leaves this graphic. One undo step.
    /// </summary>
    public void BreakOut()
    {
        var targets = TargetLoops();
        if (targets.Count == 0 || Target is not { Parent: { } container } target) return;
        var objs = BuildLoopObjects(targets);
        var work = Clones();
        foreach (var l in targets.OrderByDescending(l => l.Loop)) work[l.Piece].RemoveAt(l.Loop);
        using (Session.History.BeginTransaction("Break Out Loop"))
        {
            int at = container.IndexOf(target) + 1;
            foreach (var o in objs) Session.History.Execute(new AddObjectCommand(container, o, at++));
            Apply(work, "Break Out Loop");
        }
        _selected.Clear();
    }

    /// <summary>
    /// Reorder Loops (Edit menu): outer loops clockwise, nested ones alternating, inner loops first, for every loop of the
    /// graphic (what routers and cutters want).
    /// </summary>
    public void ReorderLoops()
    {
        if (!HasTarget) return;
        var work = Clones();
        bool changed = false;
        for (int pi = 0; pi < work.Count; pi++)
            changed |= PathEditing.ReorderLoops(work[pi], Pieces[pi].World.Determinant < 0);
        if (changed) Apply(work, "Reorder Loops");
        _selected.Clear();
    }

    private void EditLoops(string description, Action<List<Loop>, int> edit)
    {
        var targets = TargetLoops();
        if (targets.Count == 0) return;
        var work = Clones();
        // Highest loop first: breaking or removing a loop shifts the ones after it.
        foreach (var l in targets.OrderByDescending(l => l.Loop)) edit(work[l.Piece], l.Loop);
        var keep = _selected.ToList();
        Apply(work, description);
        RestoreSelection(keep);
    }

    private void RestoreSelection(List<LoopRef> keep)
    {
        _selected.Clear();
        foreach (var l in keep) if (Valid(l)) _selected.Add(l);
    }

    private void Apply(List<List<Loop>> work, string description)
    {
        _menu = null;
        _hover = null;
        Commit(work, description);
        Redraw();
    }

    private List<DocObject> BuildLoopObjects(List<LoopRef> loops)
    {
        var list = new List<DocObject>();
        foreach (var l in loops)
        {
            var piece = Pieces[l.Piece];
            var source = Target is CombinedObject c && piece.PartIndex >= 0 ? c.Parts[piece.PartIndex] : Target!;
            list.Add(new PathObject(PathEditing.FromLoops([LoopOf(l)], piece.FillRule))
            {
                Name = "Loop",
                Transform = piece.World,
                Style = source.Style,
            });
        }
        return list;
    }

    /// <summary>Called when the loop menu closes.</summary>
    public void MenuClosed() => _menu = null;

    // ---- Readouts for the Context Bar and info line ----

    /// <summary>The first selected loop's size and length in document points, its direction and its place in the loop order.</summary>
    public LoopReadout? Readout()
    {
        var targets = TargetLoops();
        if (targets.Count == 0) return null;
        var first = targets[0];
        var piece = Pieces[first.Piece];
        var loop = LoopOf(first);
        var bounds = loop.ToGeometry().Transform(piece.World).GetBounds();
        int number = Pieces.Take(first.Piece).Sum(p => p.Loops.Count) + first.Loop + 1;
        bool clockwise = loop.IsClockwise ^ (piece.World.Determinant < 0);
        return new LoopReadout(bounds.Width, bounds.Height, loop.Length() * piece.World.AverageScale, loop.Closed, clockwise, number,
            Pieces.Sum(p => p.Loops.Count), targets.Count);
    }

    public override string InfoLine()
    {
        string line = base.InfoLine();
        if (line.Length == 0 || Readout() is not { } r) return line;
        return line + string.Create(CultureInfo.InvariantCulture,
            $"  |  Loop {r.Number} of {r.Total}{(r.Count > 1 ? $" (+{r.Count - 1})" : "")}  {(r.Closed ? (r.Clockwise ? "clockwise" : "counter-clockwise") : "open")}  " +
            $"{Units.ToInches(r.Width):0.0000} x {Units.ToInches(r.Height):0.0000}  length {Units.ToInches(r.Length):0.0000}");
    }

    // ---- Overlay ----

    public override void BuildOverlay(List<OverlayItem> items)
    {
        if (HasTarget)
        {
            var loopsPerPiece = _work ?? Pieces.Select(p => p.Loops).ToList();
            var pieces = Pieces.Select((p, i) => new VectorPiece(p.PartIndex, p.World, loopsPerPiece[i], p.FillRule)).ToList();
            var outline = ShowDirection ? DirectionLoops(pieces) : OutlineView.Loops(pieces);
            var selected = new List<PathGeometry>();
            var preview = new List<PathGeometry>();
            var hover = new List<PathGeometry>();
            var starts = new List<Vec2>();
            var numbers = new List<(Vec2, int)>();
            int n = 0;
            for (int pi = 0; pi < pieces.Count; pi++)
                for (int li = 0; li < pieces[pi].Loops.Count; li++)
                {
                    n++;
                    var loop = pieces[pi].Loops[li];
                    var world = pieces[pi].World;
                    starts.Add(world.Transform(loop.Start));
                    if (ShowNumbers) numbers.Add((world.Transform(loop.Start), n));
                    bool isCopy = _work is not null && _copying && li >= _originalCounts[pi];
                    if (isCopy) preview.Add(loop.ToGeometry().Transform(world));
                    else if (_selected.Contains(new LoopRef(pi, li))) selected.Add(loop.ToGeometry().Transform(world));
                    else if (_hover == new LoopRef(pi, li) && _work is null) hover.Add(loop.ToGeometry().Transform(world));
                }
            items.Add(new LoopEditOverlay(outline, selected, hover, preview, starts, numbers));
        }
        if (_mode == Mode.Marquee && _moved) items.Add(new MarqueeOverlay(_marquee, MarqueeKind.Select));
    }

    /// <summary>Loops colored by direction instead of nesting: clockwise blue, counter-clockwise red, open black.</summary>
    private static List<OutlineLoop> DirectionLoops(IReadOnlyList<VectorPiece> pieces)
    {
        var list = new List<OutlineLoop>();
        foreach (var piece in pieces)
            foreach (var l in piece.Loops)
            {
                var kind = !l.Closed ? LoopKind.Open : (l.IsClockwise ^ (piece.World.Determinant < 0)) ? LoopKind.Border : LoopKind.Interior;
                list.Add(new OutlineLoop(l.ToGeometry().Transform(piece.World), kind));
            }
        return list;
    }
}

/// <summary>The loop Context Bar's readouts (Width, Height, Length). Sizes in document points.</summary>
public readonly record struct LoopReadout(double Width, double Height, double Length, bool Closed, bool Clockwise, int Number, int Total, int Count);
