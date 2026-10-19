using DeSignMage.Core.Commands;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Effects ▸ Easy Bevel (notes/effects/easy-bevel.md). The selection is what gets beveled. Starting the tool makes the bevel with the remembered (or default)
/// settings, so every change in the Context Bar is seen as it will print; the whole session is one undo step (<see cref="BevelCommand"/> is re-targeted, not
/// repeated). The crescents are worked out on a worker thread (the newest request wins, so a drag is followed as fast as the worker can go), so a big
/// selection never freezes the window. Done keeps the bevel, Esc or Cancel takes it back, Remove removes it (a bevel that was there before too). The light is
/// a round point on a dial around the selection: drag it to turn the light (upper left to begin with); the Light field in the bar is the same angle.
/// </summary>
public sealed class BevelTool : Tool, IModeTool
{
    private BevelSettings _remembered = new();
    private List<BevelSource> _sources = [];
    private BevelCommand? _command;
    private LatestWinsRunner? _live;
    private Rect2 _bounds;
    private bool _busy, _existed, _dragging;

    public override string Id => "bevel";
    public override string DisplayName => "Easy Bevel";
    public override string Hint => "EASY BEVEL: Drag the round point to turn the light. Enter or Done keeps the bevel, Esc or Cancel takes it back   To ZOOM: Press right button";

    /// <summary>The fields. Change them with <see cref="Update"/>.</summary>
    public BevelSettings Settings { get; private set; } = new();

    /// <summary>True when the tool has objects to bevel.</summary>
    public bool IsActiveOnSource => _sources.Count > 0;

    /// <summary>True when the objects already had a bevel before this session of the tool (Remove then deletes it instead of taking it back).</summary>
    public bool HadBevel => _existed;

    /// <summary>True when the selection has a bevel to remove: one made in this session or one that was there.</summary>
    public bool CanRemove => _command is not null || _existed;

    /// <summary>Awaitable end of the newest computation (tests).</summary>
    public Task Settled => _live?.Idle ?? Task.CompletedTask;

    public event EventHandler? Changed;

    // ---- The light ----

    /// <summary>The light's point on the dial, in document coordinates: on the ring around the selection, in the direction the light comes from (null with nothing to bevel).</summary>
    public Vec2? LightPoint => _sources.Count == 0 ? null : _bounds.Center + DialRadius * DirectionToLight(Settings.LightAngle);

    private double DialRadius => 0.5 * Math.Max(_bounds.Width, _bounds.Height) + Session.Viewport.ToDocLength(36);

    /// <summary>The unit direction from the center toward the light as drawn on the page: the angle, turned with the first object (the rim turns with its object).</summary>
    private Vec2 DirectionToLight(double angle) =>
        Affine2.Rotation(_sources.Count > 0 ? _sources[0].Angle : 0).TransformVector(-BevelEffect.AwayFromLight(angle));

    /// <summary>Turns the light to point at <paramref name="doc"/> (what dragging the round point does; the bevel follows). Does nothing exactly at the center.</summary>
    public void SetLightToward(Vec2 doc)
    {
        var d = doc - _bounds.Center;
        double length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
        if (_sources.Count == 0 || length < 1e-9) return;
        var local = Affine2.Rotation(-_sources[0].Angle).TransformVector(d * (1 / length));   // back into the object's own frame: (cos a, -sin a)
        double angle = Math.Atan2(-local.Y, local.X) * 180 / Math.PI;
        Update(s => s with { LightAngle = Math.Round(angle, 2) });
    }

    // ---- Starting and stopping ----

    /// <summary>Starts on the current selection. False when nothing in it can be beveled.</summary>
    public bool Begin()
    {
        Finish();
        return Retarget();
    }

    private bool Retarget()
    {
        _sources = BevelEffect.Sources(Session);
        _bounds = Session.Selection.GetBounds();
        _command = null;
        _existed = _sources.Any(s => BevelEffect.Existing(s.Leaf).Count > 0);
        // reopened on a live bevel: the settings it was made with
        Settings = _sources.SelectMany(s => BevelEffect.Existing(s.Leaf)).Select(c => c.Live?.Spec).OfType<LiveBevel>().FirstOrDefault()?.Settings ?? _remembered;
        Changed?.Invoke(this, EventArgs.Empty);
        Redraw();
        if (_sources.Count == 0) return false;
        Schedule();
        return true;
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
        _dragging = false;
        Finish();
        Redraw();
    }

    private void Finish()
    {
        if (_sources.Count > 0) _remembered = Settings;
        _sources = [];
        _command = null;
        _live?.Cancel();
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_busy || Session.ActiveTool != this) return;
        if (Session.Selection.IsEmpty || !BevelEffect.CanRun(Session)) { Session.SetTool("select"); return; }
        Finish();
        Retarget();
    }

    // The bevel was undone or the drawing changed some other way: what the tool holds is out of date.
    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_busy || Session.ActiveTool != this || _command is null) return;
        if (!Session.History.IsTop(_command)) Session.SetTool("select");
    }

    // ---- Fields ----

    /// <summary>Changes the fields; the bevel follows at once (a change that arrives while one is being worked out replaces the one waiting).</summary>
    public void Update(Func<BevelSettings, BevelSettings> change)
    {
        var next = change(Settings).Normalized();
        if (next == Settings || _sources.Count == 0) return;
        Settings = next;
        Changed?.Invoke(this, EventArgs.Empty);
        Schedule();
        Redraw();
    }

    private void Schedule()
    {
        if (_live is null || Session.BooleanService is not { } booleans || Session.OffsetService is not { } offsets) return;
        var sources = _sources;
        var settings = Settings;
        _live.Submit(ct => BevelEffect.ComputeAll(booleans, offsets, sources, settings, ct), pieces => Deliver(sources, pieces, settings), _ => { });
    }

    /// <summary>The crescents arrived: make the command the first time, re-target it after that.</summary>
    private void Deliver(IReadOnlyList<BevelSource> sources, IReadOnlyList<BevelPieces> pieces, BevelSettings settings)
    {
        if (!ReferenceEquals(sources, _sources)) return;   // the selection changed while it was being worked out
        _busy = true;
        try
        {
            if (_command is not null && Session.History.IsTop(_command))
            {
                _command.Retarget(pieces, settings);
                Session.History.NotifyTopEdited(_command);
            }
            else
            {
                var plan = BevelEffect.Plan(sources, pieces, settings);
                if (plan is null) return;
                _command = plan;
                Session.History.Execute(plan);
                Session.Selection.Set(plan.TopLevels.ToList());   // the crescents joined the objects' groups: select those, as the other effects do
            }
        }
        finally
        {
            _busy = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        Redraw();
    }

    // ---- Done, Cancel, Remove ----

    /// <summary>Done: waits for a bevel that is still being worked out, keeps it and returns to the Layout tool.</summary>
    public async Task Done()
    {
        if (_live is { IsBusy: true } live) await live.Idle;
        Session.SetTool("select");
    }

    /// <summary>Esc: takes back what this session of the tool did and returns to the Layout tool.</summary>
    public void Abandon()
    {
        _live?.Cancel();
        _busy = true;
        try
        {
            if (_command is not null && Session.History.IsTop(_command)) Session.History.Undo();
        }
        finally { _busy = false; }
        _command = null;
        Session.SetTool("select");
    }

    /// <summary>Remove: the bevel is gone (undoable). A bevel that was there before is deleted; one made in this session is just taken back.</summary>
    public void Remove()
    {
        _live?.Cancel();
        var leaves = _sources.Select(s => s.Leaf).ToList();
        _busy = true;
        try
        {
            if (_command is not null && Session.History.IsTop(_command)) Session.History.Undo();
            var crescents = leaves.SelectMany(BevelEffect.Existing).ToList();
            if (crescents.Count > 0)
            {
                using var tx = Session.History.BeginTransaction("Remove Bevel");
                Session.History.Execute(new DeleteObjectsCommand(crescents));
            }
        }
        finally { _busy = false; }
        _command = null;
        Session.SetTool("select");
    }

    // ---- Done and Cancel in the Context Bar (the session rolls the history back to how it was when the tool opened) ----

    bool IModeTool.IsInMode => true;
    bool IModeTool.CanFinish => IsActiveOnSource;
    Task IModeTool.FinishMode() => Done();

    void IModeTool.DiscardPending()
    {
        _live?.Cancel();   // a bevel still being worked out never arrives
        _dragging = false;
        _command = null;
    }

    // ---- Pointer: the light ----

    public override void PointerDown(PointerInput e)
    {
        if (LightPoint is not { } point) return;
        // The round point itself, or anywhere on the dial's ring, grabs the light.
        var center = Session.Viewport.ToScreen(_bounds.Center);
        double toPoint = Vec2.Distance(Session.Viewport.ToScreen(point), e.Screen);
        double toRing = Math.Abs(Vec2.Distance(center, e.Screen) - DialRadius * Session.Viewport.Zoom);
        if (toPoint > Session.HandleRadiusPx + 2 && toRing > Session.HandleRadiusPx) return;
        _dragging = true;
        SetLightToward(e.Doc);
        Redraw();
    }

    public override void PointerMove(PointerInput e)
    {
        if (_dragging) SetLightToward(e.Doc);
    }

    public override void PointerUp(PointerInput e)
    {
        if (!_dragging) return;
        SetLightToward(e.Doc);
        _dragging = false;
        Redraw();
    }

    public override void BuildOverlay(List<OverlayItem> items)
    {
        base.BuildOverlay(items);
        if (LightPoint is not { } p) return;
        double r = DialRadius;
        var c = _bounds.Center;
        var ring = new PathBuilder().MoveTo(c.X + r, c.Y).ArcTo(new Vec2(c.X, c.Y - r), new Vec2(c.X - r, c.Y)).ArcTo(new Vec2(c.X, c.Y + r), new Vec2(c.X + r, c.Y)).Close().Build();
        items.Add(new PointHandlesOverlay(ring, [(c, p)], [new PointHandleMark(p, _dragging)]));
    }

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        if (key == EditorKey.Enter) { _ = Done(); return true; }
        if (key == EditorKey.Escape) { Abandon(); return true; }
        return false;
    }
}
