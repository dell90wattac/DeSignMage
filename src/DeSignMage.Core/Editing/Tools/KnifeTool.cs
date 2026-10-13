using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Effects ▸ Knife: drag a line across objects; on release they are cut along it. The tool stays active
/// for more cuts. Options (Context Bar): Gap (the blade's width) or Overlap (the two sides overlap), Selected Only, Simple Cut.
/// While dragging, Ctrl snaps the line to horizontal or vertical when it is within about 6 degrees of it (its
/// two help texts disagree); Shift makes this one cut Selected Only.
/// </summary>
public sealed class KnifeTool : Tool
{
    private Vec2? _start;
    private Vec2 _end;
    private bool _busy;

    public override string Id => "knife";
    public override string DisplayName => "Knife";
    public override CursorKind Cursor => CursorKind.Cross;

    public override string Hint => _start is null
        ? "KNIFE: Drag to cut (Shift: selected objects only this time, Ctrl: snap to horizontal or vertical)   To ZOOM: Press right button"
        : "KNIFE: Release the button at the end of the line (Ctrl: snap to horizontal or vertical)   To ZOOM: Press right button";

    public KnifeSettings Settings { get; private set; } = new();

    public event EventHandler? Changed;

    public void Update(Func<KnifeSettings, KnifeSettings> change)
    {
        var next = change(Settings);
        if (next == Settings) return;
        Settings = next;
        Changed?.Invoke(this, EventArgs.Empty);
        Redraw();
    }

    /// <summary>Selected Only can be switched on only while something is selected (it is greyed out otherwise).</summary>
    public bool CanSelectedOnly => !Session.Selection.IsEmpty;

    /// <summary>The line being dragged (after snapping), or null.</summary>
    public (Vec2 From, Vec2 To)? Line => _start is { } s ? (s, _end) : null;

    /// <summary>The cut that was made last, awaitable (tests).</summary>
    public Task Idle => Session.EffectIdle;

    public override void Activate() => Changed?.Invoke(this, EventArgs.Empty);

    public override void PointerDown(PointerInput e)
    {
        if (_busy) return;
        _start = _end = Session.SnapPoint(e.Doc);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public override void PointerMove(PointerInput e)
    {
        if (_start is not { } s) return;
        _end = Constrain(s, Session.SnapPoint(e.Doc), e.Control);
        Redraw();
    }

    public override void PointerUp(PointerInput e)
    {
        if (_start is not { } s) return;
        var end = Constrain(s, Session.SnapPoint(e.Doc), e.Control);
        _start = null;
        Changed?.Invoke(this, EventArgs.Empty);
        Redraw();
        if (Vec2.Distance(s, end) * Session.Viewport.Zoom < Session.ClickThresholdPx) return;
        _ = Cut(s, end, e.Shift);
    }

    /// <summary>Ctrl: an end point within 10:1 of an axis moves onto it (the longer direction wins).</summary>
    internal static Vec2 Constrain(Vec2 start, Vec2 end, bool snapAxis)
    {
        if (!snapAxis) return end;
        double dx = Math.Abs(end.X - start.X), dy = Math.Abs(end.Y - start.Y);
        if (dy > 0 && dx / dy > 10) return new Vec2(end.X, start.Y);
        if (dx > 0 && dy / dx > 10) return new Vec2(start.X, end.Y);
        return end;
    }

    /// <summary>Cuts along from → to now. The tool stays active. False when nothing was crossed.</summary>
    public async Task<bool> Cut(Vec2 from, Vec2 to, bool selectedOnlyThisTime = false)
    {
        var plan = KnifeEffect.Plan(Session, from, to, Settings, Settings.SelectedOnly || selectedOnlyThisTime);
        if (plan is null) return false;
        _busy = true;
        try
        {
            var outcome = await Session.RunEffectAsync(plan);
            return outcome.Applied;
        }
        finally
        {
            _busy = false;
        }
    }

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        if (key == EditorKey.Escape && _start is not null) { Cancel(); return true; }
        return false;
    }

    public override void Cancel()
    {
        _start = null;
        Redraw();
    }

    public override void BuildOverlay(List<OverlayItem> items)
    {
        base.BuildOverlay(items);
        if (_start is not { } s || Vec2.Distance(s, _end) < 1e-9) return;
        var (a, b) = KnifeEffect.EffectiveLine(s, _end, Settings, Session.Document.GetContentBounds());
        items.Add(new OutlineOverlay(new PathBuilder().MoveTo(a).LineTo(b).Build()));
    }
}
