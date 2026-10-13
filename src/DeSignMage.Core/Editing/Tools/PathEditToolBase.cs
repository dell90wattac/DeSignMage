using System.Globalization;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// What the two path editing tools share (Vector Edit and Loop Edit tools): one open object (a path, a shape or a
/// combined object) whose vectors are shown in <see cref="VectorPiece"/>s, hit testing against them, writing an edit back
/// as one undo step, following the object when undo/redo swaps it, and the info line statistics.
/// </summary>
public abstract class PathEditToolBase : Tool
{
    protected readonly record struct NodeRef(int Piece, int Loop, int Node);
    protected readonly record struct SegRef(int Piece, int Loop, int Seg, double T);
    protected readonly record struct LoopRef(int Piece, int Loop);

    private DocObject? _target;
    private List<VectorPiece> _pieces = [];
    private bool _committing;
    private readonly Dictionary<DocObject, DocObject> _converted = [];   // shape/text <-> path it became (both directions)

    /// <summary>The object whose vectors are open, or null.</summary>
    public DocObject? Target => _target;

    public bool HasTarget => _target is not null;

    protected IReadOnlyList<VectorPiece> Pieces => _pieces;

    /// <summary>True while the pointer is over a node, vector or loop a click would act on: the hover crosshair cursor (R10).</summary>
    protected bool Hovering { get; set; }

    /// <summary>The pointer left the canvas: nothing is under it any more.</summary>
    public override void PointerExited() => ClearHover();

    protected abstract void ClearHover();

    /// <summary>Another object was opened: forget which nodes, vectors or loops were selected on the old one.</summary>
    protected abstract void ResetSelection();

    /// <summary>Called whenever the pieces were rebuilt from the document: drop references that no longer exist.</summary>
    protected abstract void OnRebuilt();

    /// <summary>Remembers that <paramref name="from"/> became <paramref name="to"/> (text → curves) so undo/redo can follow it.</summary>
    internal void NoteConversion(DocObject from, DocObject to)
    {
        _converted[from] = to;
        _converted[to] = from;
    }

    public void SetTarget(DocObject? o)
    {
        var next = o is not null && OutlineView.IsEditable(o) ? o.TopLevel : null;
        if (next != _target) ResetSelection();
        _target = next;
        ClearHover();
        Rebuild();
        if (_target is not null) Session.Selection.Set(_target);
        Redraw();
    }

    public override void Activate()
    {
        Session.Document.Changed += OnDocumentChanged;
        // Coming from the Layout tool (or the other path tool) with one editable object selected: show its vectors at once.
        if (Session.Selection.Items is [{ } only] && OutlineView.IsEditable(only)) SetTarget(only);
    }

    public override void Deactivate()
    {
        Cancel();
        Session.Document.Changed -= OnDocumentChanged;
        _target = null;
        _pieces = [];
        ClearHover();
        OnRebuilt();
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_committing || _target is null) return;
        // Undo/redo of a shape → path conversion swaps the object; follow it.
        if (!Session.Document.Contains(_target))
            _target = _converted.TryGetValue(_target, out var other) && Session.Document.Contains(other) && OutlineView.IsEditable(other) ? other : null;
        Rebuild();
        Redraw();
    }

    protected void Rebuild()
    {
        _pieces = _target is null ? [] : OutlineView.Pieces(_target, Affine2.Identity);
        OnRebuilt();
    }

    // ---- Picking ----

    protected Vec2 Screen(VectorPiece p, Vec2 local) => Session.Viewport.ToScreen(p.World.Transform(local));

    protected Vec2 ToLocal(VectorPiece p, Vec2 doc) => p.World.IsInvertible ? p.World.Invert().Transform(doc) : doc;

    protected Vec2 ToLocalVector(VectorPiece p, Vec2 docDelta) => p.World.IsInvertible ? p.World.Invert().TransformVector(docDelta) : docDelta;

    protected double PickTolerancePixels => Math.Max(Session.PickTolerancePx, 6);

    /// <summary>The nearest vector within the pick distance of a document point (the pick distance is the same on screen at any zoom).</summary>
    protected SegRef? SegmentAt(Vec2 doc)
    {
        SegRef? best = null;
        double bestD = double.PositiveInfinity;
        double docTol = Session.Viewport.ToDocLength(PickTolerancePixels);
        for (int pi = 0; pi < _pieces.Count; pi++)
        {
            var piece = _pieces[pi];
            if (!piece.World.IsInvertible) continue;
            var local = piece.World.Invert().Transform(doc);
            double tol = docTol / Math.Max(piece.World.AverageScale, 1e-12);
            for (int li = 0; li < piece.Loops.Count; li++)
                for (int si = 0; si < piece.Loops[li].Segments.Count; si++)
                {
                    var (t, d) = piece.Loops[li].Closest(si, local);
                    if (d <= tol && d < bestD) { bestD = d; best = new SegRef(pi, li, si, t); }
                }
        }
        return best;
    }

    protected NodeRef? NodeAt(Vec2 screen)
    {
        NodeRef? best = null;
        double bestD = Session.HandleRadiusPx;
        for (int pi = 0; pi < _pieces.Count; pi++)
            for (int li = 0; li < _pieces[pi].Loops.Count; li++)
            {
                var l = _pieces[pi].Loops[li];
                for (int k = 0; k < l.NodeCount; k++)
                {
                    double d = Vec2.Distance(Screen(_pieces[pi], l.Node(k)), screen);
                    if (d <= bestD) { bestD = d; best = new NodeRef(pi, li, k); }
                }
            }
        return best;
    }

    /// <summary>
    /// A click that is not on this object's vectors: text turns into curves and opens (one undo step), another editable
    /// object opens in its place. Returns true if it did either (objects themselves never move in these tools).
    /// </summary>
    protected bool TryOpenObjectAt(PointerInput e)
    {
        var hit = HitTesting.HitPainted(Session.Document, e.Doc, Session.Viewport.ToDocLength(Session.PickTolerancePx));
        if (hit is TextObject text)
        {
            Session.OpenPathEdit(text, Id);   // vector editing text turns it into curves first (it can't change font afterwards)
            return true;
        }
        if (hit is not null && hit != _target && OutlineView.IsEditable(hit))
        {
            SetTarget(hit);
            return true;
        }
        return false;
    }

    // ---- Editing ----

    protected List<List<Loop>> Clones() => _pieces.Select(p => p.Loops.Select(l => l.Clone()).ToList()).ToList();

    private static bool Same(PathGeometry a, PathGeometry b) =>
        a.Verbs.SequenceEqual(b.Verbs) && a.Points.SequenceEqual(b.Points);

    /// <summary>
    /// Writes changed pieces back to the document as one undo step. A rectangle/ellipse (or text) becomes a plain vector
    /// shape on its first edit. If nothing is left of the object it is deleted. Returns false if nothing changed.
    /// </summary>
    protected bool Commit(List<List<Loop>> work, string description)
    {
        if (_target is null) return false;
        var changed = new List<int>();
        for (int pi = 0; pi < _pieces.Count; pi++)
            if (!Same(PathEditing.FromLoops(_pieces[pi].Loops, _pieces[pi].FillRule), PathEditing.FromLoops(work[pi], _pieces[pi].FillRule)))
                changed.Add(pi);
        if (changed.Count == 0) return false;

        var target = _target;
        _committing = true;
        try
        {
            using var tx = Session.History.BeginTransaction(description);
            bool nothingLeft = work.All(loops => loops.Count == 0);
            if (nothingLeft)
            {
                Session.History.Execute(new DeleteObjectsCommand([target]));
                target = null;
            }
            else
            {
                foreach (int pi in changed)
                {
                    var piece = _pieces[pi];
                    var cmd = new SetVectorsCommand(description, target!, piece.PartIndex, PathEditing.FromLoops(work[pi], piece.FillRule));
                    Session.History.Execute(cmd);
                    if (cmd.Result != target)
                    {
                        NoteConversion(target!, cmd.Result);
                        target = cmd.Result;
                    }
                }
            }
        }
        finally
        {
            _committing = false;
        }
        _target = target;
        Rebuild();
        if (_target is not null) Session.Selection.Set(_target); else Session.Selection.Clear();
        Redraw();
        return true;
    }

    // ---- Info line (R8 item 7) ----

    /// <summary>Statistics of the open graphic: vectors, kinds, and short ("small") vectors under a quarter inch.</summary>
    public VectorStats Stats()
    {
        double small = Units.FromInches(0.25);
        return PathEditing.Stats(_pieces.SelectMany(p => p.Loops.Select(l => (l, p.World.AverageScale))), small);
    }

    /// <summary>"Graphic  40 Vectors  (10.1297, 11.1378)  3.5297 x 3.7937  0 lines  0 arcs  35 beziers  4 small vecs (&lt; 0.25in)", or "" with nothing open.</summary>
    public virtual string InfoLine()
    {
        if (_target is null) return "";
        var s = Stats();
        var b = _target.GetWorldBounds();
        var (x, y) = Units.ToUser(new Vec2(b.Left, b.Bottom), Session.Document.Page);
        return string.Create(CultureInfo.InvariantCulture,
            $"Graphic  {s.Vectors} Vectors  ({x:0.0000}, {y:0.0000})  {Units.ToInches(b.Width):0.0000} x {Units.ToInches(b.Height):0.0000}  " +
            $"{s.Lines} lines  {s.Arcs} arcs  {s.Beziers} beziers  {s.SmallVectors} small vecs (< 0.25in)");
    }
}
