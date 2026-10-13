using DeSignMage.Core.Commands;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Effects ▸ Feather (notes/effects/feather.md): the object's own edge fades out. A property of the object, not a new object (R8). Starting the tool feathers the
/// selection at once (4 % feather, 38 % inset, as the legacy program does) so the change is seen as it is made; the session is one undo step. Drag on the
/// object to set the feather from how far the pointer moved. Done keeps it, Esc takes it back, Remove removes the feather.
/// </summary>
public sealed class FeatherTool : Tool
{
    public const double DefaultAmount = 0.04, DefaultInset = 0.38;

    private List<DocObject> _targets = [];
    private FeatherCommand? _command;
    private bool _busy, _dragging, _existed;
    private Vec2 _dragStart;
    private double _amountAtPress;
    private FeatherStyle _remembered = new(DefaultAmount, DefaultInset);

    public override string Id => "feather";
    public override string DisplayName => "Feather";
    public override CursorKind Cursor => CursorKind.Cross;

    public override string Hint => "FEATHER: Drag on the object to set the feather. Enter or the check mark keeps it, Esc takes it back   To ZOOM: Press right button";

    /// <summary>The fields (0 amount = no feather yet).</summary>
    public FeatherStyle Settings { get; private set; } = new(DefaultAmount, DefaultInset);

    /// <summary>Height of the target in points: what a drag is measured against.</summary>
    public double Height { get; private set; }

    public bool IsActiveOnTarget => _targets.Count > 0;

    public bool HadEffect => _existed;

    public event EventHandler? Changed;

    // ---- Starting and stopping ----

    /// <summary>The objects a feather goes on: the drawn leaves of the selection (a group is not feathered itself), combined objects as one, no Soft Shadow or Glow objects.</summary>
    public static List<DocObject> Targets(EditorSession s)
    {
        var list = new List<DocObject>();
        foreach (var o in Stacking.BottomToTop(s.Document, s.Selection.Items))
            foreach (var leaf in o is GroupObject g ? g.Leaves() : [o])
                if (leaf.Style.Blur is null && leaf.GetLocalGeometry() is { IsEmpty: false } && !list.Contains(leaf)) list.Add(leaf);
        return list;
    }

    public static bool CanRun(EditorSession s) => !s.Selection.IsEmpty && Targets(s).Count > 0;

    /// <summary>Starts on the current selection. False when nothing in it can be feathered.</summary>
    public bool Begin()
    {
        Finish();
        return Retarget();
    }

    private bool Retarget()
    {
        _targets = Targets(Session);
        _command = null;
        if (_targets.Count == 0) { Height = 0; Changed?.Invoke(this, EventArgs.Empty); return false; }
        Height = Session.Selection.GetBounds().Height;
        var existing = _targets.Select(t => t.Style.Feather).FirstOrDefault(f => f is not null);
        _existed = existing is not null;
        Settings = existing ?? (_remembered with { Amount = _remembered.Amount > 0 ? _remembered.Amount : DefaultAmount });
        Changed?.Invoke(this, EventArgs.Empty);
        if (!_existed) Commit();   // a new feather shows at once
        Redraw();
        return true;
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

    private void Finish()
    {
        if (_targets.Count > 0 && Settings.Amount > 0) _remembered = Settings;
        _targets = [];
        _command = null;
        Height = 0;
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_busy || Session.ActiveTool != this) return;
        if (Session.Selection.IsEmpty || !CanRun(Session)) { Session.SetTool("select"); return; }
        Finish();
        Retarget();
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_busy || Session.ActiveTool != this || _command is null) return;
        if (!Session.History.IsTop(_command)) Session.SetTool("select");
    }

    // ---- Fields ----

    /// <summary>Changes the fields; the feather follows at once. An amount of 0 is "no feather".</summary>
    public void Update(Func<FeatherStyle, FeatherStyle> change)
    {
        var next = change(Settings);
        next = new FeatherStyle(Math.Clamp(next.Amount, 0, 1), Math.Clamp(next.Inset, 0, 2));
        if (next == Settings || _targets.Count == 0) return;
        Settings = next;
        Commit();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Commit()
    {
        _busy = true;
        try
        {
            var feather = Settings.Amount > 0 ? Settings : null;
            if (_command is not null && Session.History.IsTop(_command))
            {
                _command.Update(feather);
                Session.History.NotifyTopEdited(_command);
            }
            else
            {
                _command = new FeatherCommand(_existed ? "Feather" : "Add Feather", _targets, feather);
                Session.History.Execute(_command);
            }
        }
        finally { _busy = false; }
        Redraw();
    }

    // ---- Done, Cancel, Remove ----

    public void Done() => Session.SetTool("select");

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

    /// <summary>Remove: the feather is gone (undoable as "Remove Feather").</summary>
    public void Remove()
    {
        _busy = true;
        try
        {
            if (_command is not null && Session.History.IsTop(_command)) Session.History.Undo();
            if (_existed || _targets.Any(t => t.Style.Feather is not null))
                Session.History.Execute(new FeatherCommand("Remove Feather", _targets, null));
        }
        finally { _busy = false; }
        _command = null;
        Session.SetTool("select");
    }

    // ---- Pointer ----

    public override void PointerDown(PointerInput e)
    {
        if (_targets.Count == 0) return;
        _dragging = true;
        _dragStart = e.Doc;
        _amountAtPress = Settings.Amount;
    }

    public override void PointerMove(PointerInput e)
    {
        if (!_dragging || Height < 1e-9) return;
        // legacy program: Feather = distance from where the press was / the object's height
        double distance = Vec2.Distance(_dragStart, e.Doc);
        Update(s => s with { Amount = distance / Height });
    }

    public override void PointerUp(PointerInput e)
    {
        PointerMove(e);
        _dragging = false;
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
        if (!_dragging) return;
        _dragging = false;
        Update(s => s with { Amount = _amountAtPress });
    }
}
