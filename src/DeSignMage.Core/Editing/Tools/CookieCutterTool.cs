using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Effects ▸ Cookie Cutter: the selected object is the cutter. Place it over the shapes to cut
/// (drag it, or leave it where it is), optionally set the Outline (type it, or drag the cutter's edge in or out), and press Apply:
/// everything below the cutter loses the area under it. With "Remain Active" the cutter is a stamp: it follows the pointer and
/// every click cuts, and the tool stays on.
/// </summary>
public sealed class CookieCutterTool : Tool
{
    private enum Drag { None, Outline, Move }

    private PreviewRunner? _runner;
    private EdgeProbe? _probe;
    private List<PathGeometry> _cutter = [];
    private PathGeometry? _preview;
    private Drag _drag;
    private Vec2 _dragStart;
    private Vec2 _stampDelta;
    private bool _applying;

    public override string Id => "cookie";
    public override string DisplayName => "Cookie Cutter";
    public override CursorKind Cursor => RemainActive ? CursorKind.Cross : CursorKind.Arrow;

    public override string Hint => RemainActive
        ? "COOKIE CUTTER: Move the cutter over a shape and click to cut it. Esc ends   To ZOOM: Press right button"
        : "COOKIE CUTTER: Drag the cutter's edge to set the outline, drag elsewhere to move the cutter, then Apply (Enter)   To ZOOM: Press right button";

    /// <summary>Outline: how far the cutter is grown before it cuts, in points (0 = none; negative = smaller).</summary>
    public double Outline { get; private set; }

    /// <summary>"Remain Active" (default off): stamp mode.</summary>
    public bool RemainActive { get; private set; }

    public PathGeometry? Preview => _preview;
    public Task PreviewSettled => _runner?.Settled ?? Task.CompletedTask;
    public bool CanApply => !_applying && !Session.Selection.IsEmpty && CookieCutEffect.CanRun(Session);
    public event EventHandler? Changed;
    public TimeSpan PreviewDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Starts on the current selection. The outline starts at 0.</summary>
    public void Begin()
    {
        Outline = 0;
        Retarget();
    }

    private void Retarget()
    {
        _cutter.Clear();
        foreach (var leaf in EffectGeometry.Leaves(Session.Selection.Items))
            if (EffectGeometry.World(leaf) is { } g && EffectGeometry.HasClosedArea(g)) _cutter.Add(g);
        _probe = _cutter.Count == 0 ? null : new EdgeProbe(_cutter);
        _stampDelta = default;
        SchedulePreview();
        Changed?.Invoke(this, EventArgs.Empty);
        Redraw();
    }

    public override void Activate()
    {
        _runner = Session.NewPreviewRunner();
        _runner.Debounce = PreviewDelay;
        Session.Selection.Changed += OnSelectionChanged;
        Session.Document.Changed += OnDocumentChanged;
    }

    public override void Deactivate()
    {
        Session.Selection.Changed -= OnSelectionChanged;
        Session.Document.Changed -= OnDocumentChanged;
        _runner?.Cancel();
        Session.Preview = null;
        _preview = null;
        _probe = null;
        _cutter.Clear();
        _drag = Drag.None;
        Redraw();
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e) => OnSelectionChanged(sender, EventArgs.Empty);

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_applying || Session.ActiveTool != this) return;
        if (Session.Selection.IsEmpty || !CookieCutEffect.CanRun(Session)) { Session.SetTool("select"); return; }
        Retarget();
    }

    // ---- Fields ----

    public void SetOutline(double points)
    {
        if (Math.Abs(points - Outline) < 1e-12) return;
        Outline = points;
        SchedulePreview();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetRemainActive(bool on)
    {
        if (RemainActive == on) return;
        RemainActive = on;
        if (!on) { Session.Preview = null; _stampDelta = default; SchedulePreview(); }
        Changed?.Invoke(this, EventArgs.Empty);
        Redraw();
    }

    private void SchedulePreview()
    {
        _runner?.Cancel();
        if (_runner is null) return;
        if (Outline == 0 || _cutter.Count == 0 || Session.BooleanService is not { } booleans || Session.OffsetService is not { } offsets)
        {
            _preview = null;
            Redraw();
            return;
        }
        var cutter = _cutter.ToList();
        double outline = Outline;
        _runner.Debounce = PreviewDelay;
        _runner.Schedule(ct => CookieCutEffect.CutterShape(booleans, offsets, cutter, outline, ct), shape =>
        {
            _preview = shape;
            Changed?.Invoke(this, EventArgs.Empty);
            Redraw();
        }, _ => { _preview = null; Redraw(); });
    }

    // ---- Apply ----

    /// <summary>Cuts. Normally the tool ends afterwards; with Remain Active it stays.</summary>
    public async Task<bool> Apply() => await ApplyAt(default);

    private async Task<bool> ApplyAt(Vec2 moveBy)
    {
        if (_applying) return false;
        var plan = CookieCutEffect.Plan(Session, Outline, moveBy);
        if (plan is null) return false;
        _applying = true;
        _runner?.Cancel();
        try
        {
            var outcome = await Session.RunEffectAsync(plan);
            if (!outcome.Applied) return false;
            Session.Preview = null;
            _stampDelta = default;
            if (RemainActive) Retarget();
            else Session.SetTool("select");
            return true;
        }
        finally
        {
            _applying = false;
        }
    }

    // ---- Pointer ----

    private Vec2 CutterCenter => Session.Selection.GetBounds().Center;

    public override void PointerDown(PointerInput e)
    {
        if (RemainActive)
        {
            _ = ApplyAt(e.Doc - CutterCenter);
            return;
        }
        double tolerance = Session.Viewport.ToDocLength(Session.HitTolerancePixels);
        if (_probe?.Probe(e.Doc) is { } hit && Math.Abs(hit.Signed) <= tolerance) _drag = Drag.Outline;
        else _drag = Drag.Move;
        _dragStart = e.Doc;
        if (_drag == Drag.Outline) SetOutlineFromPointer(e.Doc);
    }

    public override void PointerMove(PointerInput e)
    {
        if (RemainActive)
        {
            _stampDelta = e.Doc - CutterCenter;
            Session.Preview = new TransformPreview(Session.Selection.Items.ToArray(), Affine2.Translation(_stampDelta));
            Redraw();
            return;
        }
        switch (_drag)
        {
            case Drag.Outline: SetOutlineFromPointer(e.Doc); break;
            case Drag.Move:
                Session.Preview = new TransformPreview(Session.Selection.Items.ToArray(), Affine2.Translation(e.Doc - _dragStart));
                Redraw();
                break;
        }
    }

    public override void PointerUp(PointerInput e)
    {
        var drag = _drag;
        _drag = Drag.None;
        if (drag == Drag.Outline) SetOutlineFromPointer(e.Doc);
        else if (drag == Drag.Move)
        {
            Session.Preview = null;
            var delta = e.Doc - _dragStart;
            if (delta.Length * Session.Viewport.Zoom >= Session.ClickThresholdPx)
            {
                Session.TransformSelection("Move", Affine2.Translation(delta));
                Retarget();   // the cutter's outline moved
            }
        }
    }

    private void SetOutlineFromPointer(Vec2 doc)
    {
        if (_probe?.Probe(doc) is { } hit) SetOutline(hit.Signed);   // outside the cutter is positive
    }

    public override void PointerExited()
    {
        if (RemainActive) { Session.Preview = null; Redraw(); }
    }

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        if (key == EditorKey.Enter) { if (!RemainActive) _ = Apply(); return true; }
        return false;
    }

    public override void Cancel()
    {
        _drag = Drag.None;
        Session.Preview = null;
        Redraw();
    }

    public override void BuildOverlay(List<OverlayItem> items)
    {
        base.BuildOverlay(items);
        if (_preview is { } p) items.Add(new OutlineOverlay(RemainActive && _stampDelta != default ? p.Transform(Affine2.Translation(_stampDelta)) : p));
    }
}
