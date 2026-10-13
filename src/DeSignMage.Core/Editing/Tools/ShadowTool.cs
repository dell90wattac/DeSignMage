using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Effects ▸ Shadow, Extruded Shadow, Perspective Shadow, Cast Shadow: one tool, the form is a field of its Context Bar. The selection is the
/// source. Drag on the canvas to move the shadow; Shift+drag from an edge to set the outline; or type the values. A live preview of the shadow's
/// outline is computed after a short pause on a worker thread. Apply (or Enter) makes the shadow object, grouped with its source.
/// </summary>
public sealed class ShadowTool : Tool
{
    private ShadowSettings _remembered = new();
    private ShadowSnapshot? _snapshot;
    private EdgeProbe? _probe;
    private PreviewRunner? _runner;
    private IReadOnlyList<PathGeometry> _previewPaths = [];
    private bool _applying;

    private enum Drag { None, Shift, Outline }
    private Drag _drag;
    private Vec2 _dragStart;
    private (double X, double Y) _shiftAtPress;

    public override string Id => "shadow";
    public override string DisplayName => ShadowEffect.NameOf(Settings.Form);
    public override CursorKind Cursor => CursorKind.Cross;

    public override string Hint => "SHADOW: Drag to move the shadow, Shift+drag from an edge to set the outline. Enter or the check mark applies, Esc cancels   To ZOOM: Press right button";

    /// <summary>The fields. Change them with <see cref="Update"/>.</summary>
    public ShadowSettings Settings { get; private set; } = new();

    /// <summary>Show the live preview (Preview check box, default on).</summary>
    public bool PreviewEnabled { get; private set; } = true;

    /// <summary>The preview outlines in document coordinates (empty until computed, or when Preview is off or there is no shift).</summary>
    public IReadOnlyList<PathGeometry> PreviewPaths => _previewPaths;

    /// <summary>Awaitable end of the newest preview computation (tests).</summary>
    public Task PreviewSettled => _runner?.Settled ?? Task.CompletedTask;

    /// <summary>Height of the selection in points: 100 % of the Outline "%" field.</summary>
    public double Height => _snapshot?.Height ?? 0;

    public bool HasPercent => Height > 1e-9;
    public double OutlinePercent => HasPercent ? Settings.Outline / Height * 100 : 0;

    /// <summary>Apply needs a shadow that moves (a shift of 0 would make nothing visible), and for Cast one that has a height.</summary>
    public bool CanApply => !_applying && _snapshot is { IsEmpty: false } && Settings.HasShift;

    public event EventHandler? Changed;

    public TimeSpan PreviewDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    // ---- Starting and stopping ----

    /// <summary>Starts (or switches the form) on the current selection. What was set last time is kept; a first start with no shift puts in 3 % of the height.</summary>
    public void Begin(ShadowForm form)
    {
        if (_snapshot is not null) Remember();
        Settings = _remembered with { Form = form };
        Retarget();
        // First start (or a shift that was cleared): 3 % of the height to the lower right.
        if (!Settings.HasShift && HasPercent) Settings = Settings with { ShiftX = 0.03 * Height, ShiftY = 0.03 * Height };
        // An extruded shadow starts with a 1 % outline so the planes overlap and the weld leaves no hairlines.
        if (form is ShadowForm.Extruded or ShadowForm.Perspective && Settings.Outline == 0 && HasPercent) Settings = Settings with { Outline = 0.01 * Height };
        SchedulePreview();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Retarget()
    {
        _snapshot = ShadowEffect.Snapshot(Session);
        _probe = _snapshot is null ? null : new EdgeProbe(_snapshot.Geometries);
        _previewPaths = [];
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
        if (_snapshot is not null) Remember();
        _snapshot = null;
        _probe = null;
        _previewPaths = [];
        _drag = Drag.None;
        Redraw();
    }

    private void Remember() => _remembered = Settings;

    // The object was nudged, undone, edited...: what the tool snapshotted is out of date.
    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e) => OnSelectionChanged(sender, EventArgs.Empty);

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_applying || Session.ActiveTool != this) return;
        if (Session.Selection.IsEmpty || !ShadowEffect.CanRun(Session)) { Session.SetTool("select"); return; }
        Retarget();
    }

    // ---- Fields ----

    /// <summary>Changes the fields; the preview follows.</summary>
    public void Update(Func<ShadowSettings, ShadowSettings> change)
    {
        var next = change(Settings);
        if (next == Settings) return;
        Settings = next;
        SchedulePreview();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Switches the form without leaving the tool (an extruded form gets its 1 % outline when it has none).</summary>
    public void SetForm(ShadowForm form)
    {
        Update(s =>
        {
            var next = s with { Form = form };
            if (form is ShadowForm.Extruded or ShadowForm.Perspective && s.Outline == 0 && HasPercent) next = next with { Outline = 0.01 * Height };
            return next;
        });
    }

    public void SetPreviewEnabled(bool on)
    {
        if (PreviewEnabled == on) return;
        PreviewEnabled = on;
        SchedulePreview();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SchedulePreview()
    {
        _runner?.Cancel();
        if (_runner is null) return;
        if (!PreviewEnabled || !Settings.HasShift || _snapshot is not { IsEmpty: false } snapshot ||
            Session.BooleanService is not { } booleans || Session.OffsetService is not { } offsets)
        {
            if (_previewPaths.Count > 0) { _previewPaths = []; Redraw(); }
            return;
        }
        var settings = Settings;
        _runner.Debounce = PreviewDelay;
        _runner.Schedule(
            ct => ShadowEffect.Compute(booleans, offsets, snapshot, settings, ct).Select(x => x.Shadow).ToList(),
            paths =>
            {
                _previewPaths = paths;
                Changed?.Invoke(this, EventArgs.Empty);
                Redraw();
            },
            _ => { _previewPaths = []; Redraw(); });
    }

    // ---- Applying ----

    /// <summary>Apply: makes the shadow(s) and returns to the Layout tool. False when there is nothing to make.</summary>
    public async Task<bool> Apply()
    {
        if (!CanApply) return false;
        var plan = ShadowEffect.Plan(Session, Settings, _snapshot);
        if (plan is null) return false;
        _applying = true;
        _runner?.Cancel();
        try
        {
            var outcome = await Session.RunEffectAsync(plan);
            if (!outcome.Applied) return false;
            Remember();
            Session.SetTool("select");
            return true;
        }
        finally
        {
            _applying = false;
        }
    }

    // ---- Pointer ----

    public override void PointerDown(PointerInput e)
    {
        _dragStart = e.Doc;
        _shiftAtPress = (Settings.ShiftX, Settings.ShiftY);
        if (e.Shift)
        {
            // Shift+drag only starts on an edge; a miss does nothing.
            double tolerance = Session.Viewport.ToDocLength(Session.HitTolerancePixels);
            if (_probe?.Probe(e.Doc) is { } hit && Math.Abs(hit.Signed) <= tolerance)
            {
                _drag = Drag.Outline;
                OutlineFromPointer(e.Doc);
            }
            return;
        }
        _drag = Drag.Shift;
    }

    public override void PointerMove(PointerInput e)
    {
        switch (_drag)
        {
            case Drag.Shift: ShiftFromPointer(e.Doc); break;
            case Drag.Outline: OutlineFromPointer(e.Doc); break;
        }
    }

    public override void PointerUp(PointerInput e)
    {
        PointerMove(e);
        _drag = Drag.None;
    }

    private void ShiftFromPointer(Vec2 doc)
    {
        var d = doc - _dragStart;
        Update(s => s with { ShiftX = _shiftAtPress.X + d.X, ShiftY = _shiftAtPress.Y + d.Y });
    }

    private void OutlineFromPointer(Vec2 doc)
    {
        if (_probe?.Probe(doc) is not { } hit) return;
        Update(s => s with { Outline = Math.Max(0, hit.Signed) });   // outside is positive; going inside gives 0
    }

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        if (key == EditorKey.Enter) { _ = Apply(); return true; }
        if (key == EditorKey.Escape && _drag != Drag.None) { Cancel(); return true; }
        return false;
    }

    public override void Cancel()
    {
        if (_drag != Drag.None)
        {
            Settings = Settings with { ShiftX = _shiftAtPress.X, ShiftY = _shiftAtPress.Y };   // an abandoned drag puts the shadow back
            SchedulePreview();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        _drag = Drag.None;
        Redraw();
    }

    public override void BuildOverlay(List<OverlayItem> items)
    {
        base.BuildOverlay(items);
        foreach (var p in _previewPaths) items.Add(new OutlineOverlay(p));
    }
}
