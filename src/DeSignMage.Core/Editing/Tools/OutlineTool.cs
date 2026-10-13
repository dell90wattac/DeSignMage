using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Effects ▸ Outline and Effects ▸ Inline: one tool, two modes. The selection is the source;
/// the Context Bar's fields set the distance (inches or % of the selection's height), the corner type and Contour; dragging on
/// the canvas sets the distance too (the distance from the pointer to the nearest edge, outside positive). A live preview of the
/// result outline is computed after a short pause on a worker thread. Done (or Enter) makes the new standalone object.
/// </summary>
public sealed class OutlineTool : Tool
{
    private readonly Dictionary<OutlineKind, (double First, double Second)> _remembered = [];
    private OutlineSnapshot? _snapshot;
    private EdgeProbe? _probe;
    private PreviewRunner? _runner;
    private IReadOnlyList<PathGeometry> _previewPaths = [];
    private (Vec2 From, Vec2 To)? _rubber;
    private bool _dragging, _applying;

    public override string Id => "outline";
    public override string DisplayName => Kind == OutlineKind.Inline ? "Inline" : "Outline";
    public override CursorKind Cursor => CursorKind.Cross;

    public override string Hint => Kind == OutlineKind.Inline
        ? "INLINE: Drag in from the edge, or type a distance in the bar. Enter or the check mark applies, Esc cancels   To ZOOM: Press right button"
        : "OUTLINE: Drag out from the edge, or type a distance in the bar. Enter or the check mark applies, Esc cancels   To ZOOM: Press right button";

    public OutlineKind Kind { get; private set; }

    /// <summary>The fields. Change them with <see cref="Update"/>.</summary>
    public OutlineSettings Settings { get; private set; } = new();

    /// <summary>Show the live preview (Preview check box, default on).</summary>
    public bool PreviewEnabled { get; private set; } = true;

    /// <summary>The preview outlines in document coordinates (empty until computed, or when Preview is off or the amount is 0).</summary>
    public IReadOnlyList<PathGeometry> PreviewPaths => _previewPaths;

    /// <summary>Awaitable end of the newest preview computation (tests).</summary>
    public Task PreviewSettled => _runner?.Settled ?? Task.CompletedTask;

    /// <summary>Height of the selection in points: 100 % of the "%" fields.</summary>
    public double Height => _snapshot?.Height ?? 0;

    public bool HasPercent => Height > 1e-9;

    public bool CanApply => !Settings.IsZero && _snapshot is { IsEmpty: false } && !_applying;

    /// <summary>The bar re-reads the tool when this is raised (a field changed, a preview arrived).</summary>
    public event EventHandler? Changed;

    /// <summary>Debounce before a preview starts (about 0.7 s while typing; the work is off the UI thread).</summary>
    public TimeSpan PreviewDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    // ---- Starting and stopping ----

    /// <summary>Starts (or switches mode) on the current selection.</summary>
    public void Begin(OutlineKind kind)
    {
        if (_snapshot is not null) Remember();
        Kind = kind;
        var (first, second) = _remembered.TryGetValue(kind, out var r) ? r : (0, 0);
        Settings = Settings with { First = first, Second = second };
        Retarget();
    }

    private void Retarget()
    {
        _snapshot = OutlineEffect.Snapshot(Session);
        _probe = _snapshot is null ? null : new EdgeProbe(_snapshot.Geometries);
        _previewPaths = [];
        _rubber = null;
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
        _rubber = null;
        _dragging = false;
        Redraw();
    }

    private void Remember() => _remembered[Kind] = (Settings.First, Settings.Second);

    // The object was nudged, undone, edited...: what the tool snapshotted is out of date.
    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e) => OnSelectionChanged(sender, EventArgs.Empty);

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_applying || Session.ActiveTool != this) return;
        if (Session.Selection.IsEmpty || !OutlineEffect.CanRun(Session)) { Session.SetTool("select"); return; }
        Retarget();
    }

    // ---- Fields ----

    /// <summary>Changes the fields; the preview follows.</summary>
    public void Update(Func<OutlineSettings, OutlineSettings> change)
    {
        var next = change(Settings);
        if (next == Settings) return;
        Settings = next;
        SchedulePreview();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetPreviewEnabled(bool on)
    {
        if (PreviewEnabled == on) return;
        PreviewEnabled = on;
        SchedulePreview();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public double FirstPercent => HasPercent ? Settings.First / Height * 100 : 0;
    public double SecondPercent => HasPercent ? Settings.Second / Height * 100 : 0;

    private void SchedulePreview()
    {
        _runner?.Cancel();
        if (_runner is null) return;
        if (!PreviewEnabled || Settings.IsZero || _snapshot is not { IsEmpty: false } snapshot || Session.OffsetService is not { } offsets)
        {
            if (_previewPaths.Count > 0) { _previewPaths = []; Redraw(); }
            return;
        }
        var booleans = Session.BooleanService;
        var kind = Kind;
        var settings = Settings;
        _runner.Debounce = PreviewDelay;
        _runner.Schedule(
            ct => OutlineEffect.Compute(offsets, booleans, snapshot, kind, settings, ct).Select(x => x.Result).ToList(),
            paths =>
            {
                _previewPaths = paths;
                Changed?.Invoke(this, EventArgs.Empty);
                Redraw();
            },
            _ => { _previewPaths = []; Redraw(); });
    }

    // ---- Applying ----

    /// <summary>Done: makes the new object(s) and returns to the Layout tool. False when there is nothing to make.</summary>
    public async Task<bool> Apply()
    {
        if (!CanApply) return false;
        var plan = OutlineEffect.Plan(Session, Kind, Settings, _snapshot);
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
        if (_probe is null || _probe.IsEmpty) return;
        _dragging = true;
        FromPointer(e.Doc);
    }

    public override void PointerMove(PointerInput e)
    {
        if (_dragging) FromPointer(e.Doc);
    }

    public override void PointerUp(PointerInput e)
    {
        if (!_dragging) return;
        FromPointer(e.Doc);
        _dragging = false;
        _rubber = null;
        Redraw();
    }

    private void FromPointer(Vec2 doc)
    {
        if (_probe?.Probe(doc) is not { } hit) return;
        double d = Kind == OutlineKind.Inline ? -hit.Signed : hit.Signed;   // outside is positive for Outline, inside for Inline
        _rubber = (hit.Nearest, doc);
        Update(s => s with { First = d });
        Redraw();
    }

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        if (key == EditorKey.Enter) { _ = Apply(); return true; }
        if (key == EditorKey.Escape && _dragging) { Cancel(); return true; }
        return false;
    }

    public override void Cancel()
    {
        _dragging = false;
        _rubber = null;
        Redraw();
    }

    public override void BuildOverlay(List<OverlayItem> items)
    {
        base.BuildOverlay(items);
        foreach (var p in _previewPaths) items.Add(new OutlineOverlay(p));
        if (_rubber is { } r)
            items.Add(new OutlineOverlay(new PathBuilder().MoveTo(r.From).LineTo(r.To).Build()));
    }
}
