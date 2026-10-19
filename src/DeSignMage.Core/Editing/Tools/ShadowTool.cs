using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Effects ▸ Shadow, Extruded Shadow, Perspective Shadow, Cast Shadow: one tool, the form is a field of its Context Bar. The selection is the
/// source. Drag on the canvas to move the shadow; Shift+drag from an edge to set the outline; or type the values. Two forms have a draggable point:
/// Perspective has its vanishing point (where the far end shrinks toward, the center of the object until moved) and Cast has the top edge of the shadow
/// (it sets how far the top is pushed sideways and how tall the shadow is). The live preview of the shadow's outline follows every move on a worker
/// thread (the newest request wins). Apply (or Enter) makes the shadow object, grouped with its source.
/// </summary>
public sealed class ShadowTool : Tool
{
    private ShadowSettings _remembered = new();
    private ShadowSnapshot? _snapshot;
    private Rect2 _bounds;
    private EdgeProbe? _probe;
    private LatestWinsRunner? _live;
    private IReadOnlyList<PathGeometry> _previewPaths = [];
    private bool _applying;

    private enum Drag { None, Shift, Outline, Point }
    private Drag _drag;
    private Vec2 _dragStart;
    private Vec2 _grabOffset;
    private (double X, double Y) _shiftAtPress;
    private Vec2? _pivotAtPress;

    public override string Id => "shadow";
    public override string DisplayName => ShadowEffect.NameOf(Settings.Form);
    public override CursorKind Cursor => CursorKind.Cross;

    public override string Hint => Settings.Form switch
    {
        ShadowForm.Cast => "CAST SHADOW: Drag the round point to set slant and height, or drag to move. Enter applies, Esc cancels   To ZOOM: Press right button",
        ShadowForm.Perspective => "PERSPECTIVE SHADOW: Drag the round point to move the vanishing point, or drag to move. Enter applies, Esc cancels   To ZOOM: Press right button",
        _ => "SHADOW: Drag to move the shadow, Shift+drag from an edge to set the outline. Enter or the check mark applies, Esc cancels   To ZOOM: Press right button",
    };

    /// <summary>The fields. Change them with <see cref="Update"/>.</summary>
    public ShadowSettings Settings { get; private set; } = new();

    /// <summary>Show the live preview (Preview check box, default on).</summary>
    public bool PreviewEnabled { get; private set; } = true;

    /// <summary>The preview outlines in document coordinates (empty until computed, or when Preview is off or there is no shift).</summary>
    public IReadOnlyList<PathGeometry> PreviewPaths => _previewPaths;

    /// <summary>Awaitable end of the newest preview computation (tests).</summary>
    public Task PreviewSettled => _live?.Idle ?? Task.CompletedTask;

    /// <summary>Height of the selection in points: 100 % of the Outline "%" field.</summary>
    public double Height => _snapshot?.Height ?? 0;

    public bool HasPercent => Height > 1e-9;
    public double OutlinePercent => HasPercent ? Settings.Outline / Height * 100 : 0;

    /// <summary>Apply needs a shadow that moves (a shift of 0 would make nothing visible), and for Cast one that has a height.</summary>
    public bool CanApply => !_applying && _snapshot is { IsEmpty: false } && Settings.HasShift;

    public event EventHandler? Changed;

    // ---- The draggable point ----

    /// <summary>True when the current form has a point to drag (Perspective: the vanishing point; Cast: the top edge).</summary>
    public bool HasHandle => _snapshot is { IsEmpty: false } && Settings.Form is ShadowForm.Cast or ShadowForm.Perspective;

    /// <summary>The Perspective vanishing point as the bar shows it: the point that was set, else the center of the selection.</summary>
    public Vec2 VanishingPoint => Settings.Pivot ?? _bounds.Center;

    /// <summary>Where the draggable point is (document coordinates). Cast: top-center of the shadow, which is where the object's top-center goes.</summary>
    public Vec2? HandlePosition => !HasHandle ? null
        : Settings.Form == ShadowForm.Cast ? new Vec2(_bounds.Center.X + Settings.ShiftX, _bounds.Top + Settings.ShiftY)
        : VanishingPoint;

    /// <summary>Puts the draggable point at <paramref name="doc"/> (what dragging it does; the preview follows). Cast: Hs and Vs follow; Perspective: the vanishing point.</summary>
    public void SetHandle(Vec2 doc)
    {
        if (!HasHandle) return;
        if (Settings.Form == ShadowForm.Cast)
            Update(s => s with { ShiftX = doc.X - _bounds.Center.X, ShiftY = doc.Y - _bounds.Top });   // the bottom edge stays; the top edge goes to the point
        else
            Update(s => s with { Pivot = doc });
    }

    /// <summary>Perspective: the vanishing point goes back to the center of each object.</summary>
    public void ResetVanishingPoint() => Update(s => s with { Pivot = null });

    // ---- Starting and stopping ----

    /// <summary>Starts (or switches the form) on the current selection. What was set last time is kept; a first start with no shift puts in 3 % of the height.</summary>
    public void Begin(ShadowForm form)
    {
        if (_snapshot is not null) Remember();
        // reopened on objects with a live shadow: the settings it was made with (the menu item still picks the form)
        Settings = ShadowEffect.ExistingSettings(Session) is { } live ? live with { Form = form } : _remembered with { Form = form, Pivot = null };
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
        _bounds = Session.Selection.GetBounds();
        _probe = _snapshot is null ? null : new EdgeProbe(_snapshot.Geometries);
        _previewPaths = [];
        SchedulePreview();
        Changed?.Invoke(this, EventArgs.Empty);
        Redraw();
    }

    public override void Activate()
    {
        _live = Session.NewLiveRunner();
        Session.Selection.Changed += OnSelectionChanged;
        Session.Document.Changed += OnDocumentChanged;
    }

    public override void Deactivate()
    {
        Session.Selection.Changed -= OnSelectionChanged;
        Session.Document.Changed -= OnDocumentChanged;
        _live?.Cancel();
        if (_snapshot is not null) Remember();
        _snapshot = null;
        _probe = null;
        _previewPaths = [];
        _drag = Drag.None;
        Redraw();
    }

    private void Remember() => _remembered = Settings with { Pivot = null };   // a vanishing point belongs to the objects it was set on

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
        Redraw();
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

    /// <summary>Asks for a new preview of the current fields. It never waits: one preview is worked out at a time on a worker thread and only the newest request waits behind it, so a drag is followed as fast as the worker can go.</summary>
    private void SchedulePreview()
    {
        if (_live is null) return;
        if (!PreviewEnabled || !Settings.HasShift || _snapshot is not { IsEmpty: false } snapshot ||
            Session.BooleanService is not { } booleans || Session.OffsetService is not { } offsets)
        {
            _live.Cancel();
            if (_previewPaths.Count > 0) { _previewPaths = []; Redraw(); }
            return;
        }
        var settings = Settings;
        _live.Submit(
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
        _live?.Cancel();
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
        _pivotAtPress = Settings.Pivot;
        if (HandlePosition is { } handle && Vec2.Distance(Session.Viewport.ToScreen(handle), e.Screen) <= Session.HandleRadiusPx + 2)
        {
            _drag = Drag.Point;
            _grabOffset = handle - e.Doc;   // grabbing the point does not make it jump to the pointer
            Redraw();
            return;
        }
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
            case Drag.Point: SetHandle(e.Doc + _grabOffset); break;
        }
    }

    public override void PointerUp(PointerInput e)
    {
        PointerMove(e);
        _drag = Drag.None;
        Redraw();
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
        if (_drag is Drag.Shift or Drag.Point)
        {
            Settings = Settings with { ShiftX = _shiftAtPress.X, ShiftY = _shiftAtPress.Y, Pivot = _pivotAtPress };   // an abandoned drag puts the shadow back
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
        if (HandlePosition is { } handle)
        {
            // Cast: a line from the fixed bottom edge up to the top edge, which is the point. Perspective: just the vanishing point.
            var lines = Settings.Form == ShadowForm.Cast ? new[] { (new Vec2(_bounds.Center.X, _bounds.Bottom), handle) } : [];
            items.Add(new PointHandlesOverlay(null, lines, [new PointHandleMark(handle, _drag == Drag.Point)]));
        }
    }
}
