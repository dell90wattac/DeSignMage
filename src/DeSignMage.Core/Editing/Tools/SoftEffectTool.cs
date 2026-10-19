using DeSignMage.Core.Commands;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Effects ▸ Soft Shadow and Effects ▸ Glow: one tool, two modes (notes/effects/soft-shadow.md, glow.md). The selection is the source. Starting the tool
/// makes the effect right away with the remembered (or default) settings, so every change in the Context Bar is seen as it will print; the whole session
/// is one undo step (<see cref="SoftEffectCommand"/> is re-targeted, not repeated). Soft Shadow: drag anywhere to move the shadow. Glow: drag outward
/// from the shape to set the thickness. Done keeps the effect, Esc or Cancel takes it back, Remove removes it (an effect that was there before too).
/// </summary>
public sealed class SoftEffectTool : Tool, IModeTool
{
    private readonly Dictionary<SoftKind, SoftSettings> _remembered = [];
    private readonly Dictionary<SoftKind, double> _rememberedShiftFraction = [];
    private List<DocObject> _sources = [];
    private Dictionary<DocObject, PathGeometry> _silhouettes = [];
    private SoftEffectCommand? _command;
    private EdgeProbe? _probe;
    private bool _busy, _dragging, _existed;
    private Vec2 _dragStart;
    private (double X, double Y) _shiftAtPress;
    private double _thicknessAtPress;

    public override string Id => "softeffect";
    public override string DisplayName => SoftEffect.NameOf(Kind);
    public override CursorKind Cursor => CursorKind.Cross;

    public override string Hint => Kind == SoftKind.Glow
        ? "GLOW: Drag out from the shape to set the thickness. Enter or Done keeps it, Esc or Cancel takes it back   To ZOOM: Press right button"
        : "SOFT SHADOW: Drag to move the shadow. Enter or Done keeps it, Esc or Cancel takes it back   To ZOOM: Press right button";

    public SoftKind Kind { get; private set; }

    /// <summary>The fields. Change them with <see cref="Update"/>.</summary>
    public SoftSettings Settings { get; private set; } = new();

    /// <summary>Height of the source in points (100 % of the percentage fields); 0 when nothing is being edited.</summary>
    public double Height { get; private set; }

    /// <summary>True when the tool has a source to work on.</summary>
    public bool IsActiveOnSource => _sources.Count > 0;

    /// <summary>True when the effect was there before this session of the tool (Remove then deletes it instead of taking it back).</summary>
    public bool HadEffect => _existed;

    /// <summary>The effect objects the tool is editing, one per source.</summary>
    public IReadOnlyList<PathObject> Effects => _command?.Effects.ToArray() ?? _sources.Select(s => SoftEffect.Find(s, Kind)).OfType<PathObject>().ToArray();

    public event EventHandler? Changed;

    // ---- Starting and stopping ----

    /// <summary>Starts (or switches mode) on the current selection. False when nothing in it can be a source.</summary>
    public bool Begin(SoftKind kind)
    {
        Finish();
        Kind = kind;
        return Retarget();
    }

    private bool Retarget()
    {
        _sources = SoftEffect.Sources(Session);
        _silhouettes = [];
        _command = null;
        _probe = null;
        if (_sources.Count == 0) { Height = 0; Changed?.Invoke(this, EventArgs.Empty); return false; }
        foreach (var s in _sources)
            if (SoftEffect.Silhouette(Session.BooleanService, s) is { } sil) _silhouettes[s] = sil;
        _sources = _sources.Where(_silhouettes.ContainsKey).ToList();
        if (_sources.Count == 0) { Height = 0; Changed?.Invoke(this, EventArgs.Empty); return false; }

        var union = _silhouettes.Values.Aggregate(Rect2.Empty, (r, g) => r.Union(g.GetBounds()));
        Height = union.Height;
        _probe = new EdgeProbe(_silhouettes.Values);

        var existing = _sources.Select(s => SoftEffect.Find(s, Kind)).FirstOrDefault(e => e is not null);
        _existed = existing is not null;
        if (existing is not null) Settings = SoftEffect.Read(existing);
        else Settings = Fresh();
        Settings = Settings.Normalized();
        Changed?.Invoke(this, EventArgs.Empty);
        Redraw();
        if (!_existed) Commit();   // a new effect shows at once; opening an effect that exists changes nothing until a field does
        return true;
    }

    private SoftSettings Fresh()
    {
        var color = SoftEffect.ColorOf(_sources[0]);
        var s = SoftEffect.Defaults(Kind, Height, color);
        if (_remembered.TryGetValue(Kind, out var last))
        {
            s = s with { Opacity = last.Opacity, Softness = last.Softness };
            if (Kind == SoftKind.SoftShadow)
            {
                double f = _rememberedShiftFraction.GetValueOrDefault(Kind, SoftEffect.DefaultShiftFraction);
                s = s with { ShiftX = Math.Sign(last.ShiftX) * Math.Abs(f) * Height, ShiftY = Math.Sign(last.ShiftY) * Math.Abs(f) * Height };
                if (last.ShiftX == 0 && last.ShiftY == 0) s = s with { ShiftX = 0, ShiftY = 0 };
            }
            if (Kind == SoftKind.Glow) s = s with { Color = last.Color, Thickness = last.Thickness };
            else s = s with { Color = last.Color };
        }
        return s;
    }

    public override void Activate()
    {
        Session.Selection.Changed += OnSelectionChanged;
        Session.Document.Changed += OnDocumentChanged;
    }

    public override void Deactivate()
    {
        Session.Selection.Changed -= OnSelectionChanged;
        Session.Document.Changed -= OnDocumentChanged;
        Finish();
        _dragging = false;
        Redraw();
    }

    /// <summary>Remembers what was set (the legacy program keeps the last shift, opacity and softness for the next new effect) and lets go of the sources.</summary>
    private void Finish()
    {
        if (_sources.Count > 0 && _command is not null)
        {
            _remembered[Kind] = Settings;
            if (Height > 1e-9) _rememberedShiftFraction[Kind] = Math.Max(Math.Abs(Settings.ShiftX), Math.Abs(Settings.ShiftY)) / Height;
        }
        _sources = [];
        _silhouettes = [];
        _command = null;
        _probe = null;
        Height = 0;
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_busy || Session.ActiveTool != this) return;
        // the selection changed because the effect was undone (or the drawing changed some other way): leave, do not make the effect again
        if (_command is not null && !Session.History.IsTop(_command)) { Session.SetTool("select"); return; }
        if (Session.Selection.IsEmpty || !SoftEffect.CanRun(Session)) { Session.SetTool("select"); return; }
        Finish();
        Retarget();
    }

    // The effect was undone or the drawing changed some other way: what the tool holds is out of date.
    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_busy || Session.ActiveTool != this || _command is null) return;
        if (!Session.History.IsTop(_command)) Session.SetTool("select");
    }

    // ---- Fields ----

    /// <summary>Changes the fields; the effect follows at once.</summary>
    public void Update(Func<SoftSettings, SoftSettings> change)
    {
        var next = change(Settings).Normalized();
        if (next == Settings || _sources.Count == 0) return;
        Settings = next;
        Commit();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Makes or re-targets the command for the current settings.</summary>
    private void Commit()
    {
        _busy = true;
        try
        {
            if (_command is not null && Session.History.IsTop(_command))
            {
                var states = new List<(PathGeometry, ObjectStyle)>();
                foreach (var t in _command.Targets)
                {
                    var made = SoftEffect.Make(Session.BooleanService, Session.OffsetService, t.Source, Settings, _silhouettes[t.Source]);
                    states.Add(made ?? (t.NewGeometry, t.NewStyle));
                }
                _command.Retarget(states);
                Session.History.NotifyTopEdited(_command);
            }
            else
            {
                var plan = SoftEffect.Plan(Session, _sources, Settings, _silhouettes);
                if (plan is null) return;
                _command = plan;
                Session.History.Execute(plan);
                // the new effect object joined the source's group: select the groups, as the other effects do
                var groups = new List<DocObject>();
                for (int i = 0; i < plan.Targets.Count; i++)
                    groups.Add(plan.Targets[i].Source.TopLevel);
                Session.Selection.Set(groups);
            }
        }
        finally
        {
            _busy = false;
        }
        Redraw();
    }

    // ---- Done, Cancel, Remove ----

    /// <summary>Done: keeps the effect and returns to the Layout tool.</summary>
    public void Done()
    {
        Session.SetTool("select");
    }

    /// <summary>Esc: takes back what this session of the tool did and returns to the Layout tool.</summary>
    public void Abandon()
    {
        _busy = true;
        try
        {
            if (_command is not null && Session.History.IsTop(_command)) Session.History.Undo();
        }
        finally { _busy = false; }
        _command = null;
        Session.SetTool("select");
    }

    /// <summary>Remove: the effect is gone (undoable). An effect that was there before is deleted; one made in this session is just taken back.</summary>
    public void Remove()
    {
        var effects = Effects.ToList();
        if (effects.Count == 0) { Session.SetTool("select"); return; }
        _busy = true;
        try
        {
            if (_command is not null && Session.History.IsTop(_command)) Session.History.Undo();
            if (_existed)
            {
                using var tx = Session.History.BeginTransaction("Remove " + SoftEffect.NameOf(Kind));
                Session.History.Execute(new DeleteObjectsCommand(effects));
            }
        }
        finally { _busy = false; }
        _command = null;
        Session.SetTool("select");
    }

    // ---- Done and Cancel in the Context Bar (the session rolls the history back to how it was when the tool opened) ----

    bool IModeTool.IsInMode => true;
    bool IModeTool.CanFinish => IsActiveOnSource;
    Task IModeTool.FinishMode() { Done(); return Task.CompletedTask; }

    void IModeTool.DiscardPending()
    {
        _dragging = false;
        _command = null;   // what was set is not remembered for the next effect
    }

    // ---- Pointer ----

    public override void PointerDown(PointerInput e)
    {
        if (_sources.Count == 0) return;
        _dragging = true;
        _dragStart = e.Doc;
        _shiftAtPress = (Settings.ShiftX, Settings.ShiftY);
        _thicknessAtPress = Settings.Thickness;
        if (Kind == SoftKind.Glow) GlowFromPointer(e.Doc);
    }

    public override void PointerMove(PointerInput e)
    {
        if (!_dragging) return;
        if (Kind == SoftKind.Glow) GlowFromPointer(e.Doc);
        else
        {
            var d = e.Doc - _dragStart;
            Update(s => s with { ShiftX = _shiftAtPress.X + d.X, ShiftY = _shiftAtPress.Y + d.Y });
        }
    }

    public override void PointerUp(PointerInput e)
    {
        PointerMove(e);
        _dragging = false;
    }

    private void GlowFromPointer(Vec2 doc)
    {
        if (_probe?.Probe(doc) is not { } hit || Height < 1e-9) return;
        // legacy program: Thickness = (distance from the shape / height) * 12; only outside counts
        double d = hit.Signed;
        if (d <= 0) return;
        Update(s => s with { Thickness = d / Height * 12 });
    }

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        if (key == EditorKey.Enter) { Done(); return true; }
        if (key == EditorKey.Escape)
        {
            if (_dragging) Cancel();
            else Abandon();
            return true;
        }
        return false;
    }

    public override void Cancel()
    {
        if (_dragging)
        {
            _dragging = false;
            Update(s => s with { ShiftX = _shiftAtPress.X, ShiftY = _shiftAtPress.Y, Thickness = _thicknessAtPress });   // an abandoned drag puts the effect back
        }
    }
}
