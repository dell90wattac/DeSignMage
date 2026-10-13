using DeSignMage.Core.Commands;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Effects ▸ Easy Bevel (notes/effects/easy-bevel.md). The selection is what gets beveled. Starting the tool makes the bevel with the remembered (or default)
/// settings, so every change in the Context Bar is seen as it will print; the whole session is one undo step (<see cref="BevelCommand"/> is re-targeted, not
/// repeated). The crescents are worked out on a worker thread after a short pause, so a big selection never freezes the window. Done keeps the bevel, Esc or
/// Cancel takes it back, Remove removes it (a bevel that was there before too). There is nothing to drag: the light is fixed at the upper left.
/// </summary>
public sealed class BevelTool : Tool
{
    private BevelSettings _remembered = new();
    private List<BevelSource> _sources = [];
    private BevelCommand? _command;
    private PreviewRunner? _runner;
    private bool _busy, _existed;

    public override string Id => "bevel";
    public override string DisplayName => "Easy Bevel";
    public override string Hint => "EASY BEVEL: Set the width, softness and strength in the bar. Enter or the check mark keeps the bevel, Esc takes it back   To ZOOM: Press right button";

    /// <summary>The fields. Change them with <see cref="Update"/>.</summary>
    public BevelSettings Settings { get; private set; } = new();

    /// <summary>True when the tool has objects to bevel.</summary>
    public bool IsActiveOnSource => _sources.Count > 0;

    /// <summary>True when the objects already had a bevel before this session of the tool (Remove then deletes it instead of taking it back).</summary>
    public bool HadBevel => _existed;

    /// <summary>True when the selection has a bevel to remove: one made in this session or one that was there.</summary>
    public bool CanRemove => _command is not null || _existed;

    /// <summary>Awaitable end of the newest computation (tests).</summary>
    public Task Settled => _runner?.Settled ?? Task.CompletedTask;

    /// <summary>The pause before a change in the bar is worked out.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(120);

    public event EventHandler? Changed;

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
        _command = null;
        _existed = _sources.Any(s => BevelEffect.Existing(s.Leaf).Count > 0);
        Settings = _remembered;
        Changed?.Invoke(this, EventArgs.Empty);
        Redraw();
        if (_sources.Count == 0) return false;
        Schedule(immediate: true);
        return true;
    }

    public override void Activate()
    {
        _runner = Session.NewPreviewRunner();
        Session.Selection.Changed += OnSelectionChanged;
        Session.Document.Changed += OnDocumentChanged;
    }

    public override void Deactivate()
    {
        Session.Selection.Changed -= OnSelectionChanged;
        Session.Document.Changed -= OnDocumentChanged;
        _runner?.Cancel();
        Finish();
        Redraw();
    }

    private void Finish()
    {
        if (_sources.Count > 0) _remembered = Settings;
        _sources = [];
        _command = null;
        _runner?.Cancel();
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

    /// <summary>Changes the fields; the bevel follows after a short pause.</summary>
    public void Update(Func<BevelSettings, BevelSettings> change)
    {
        var next = change(Settings).Normalized();
        if (next == Settings || _sources.Count == 0) return;
        Settings = next;
        Changed?.Invoke(this, EventArgs.Empty);
        Schedule(immediate: false);
    }

    private void Schedule(bool immediate)
    {
        if (_runner is null || Session.BooleanService is not { } booleans || Session.OffsetService is not { } offsets) return;
        var sources = _sources;
        var settings = Settings;
        _runner.Debounce = immediate ? TimeSpan.Zero : Delay;
        _runner.Schedule(ct => BevelEffect.ComputeAll(booleans, offsets, sources, settings, ct), pieces => Deliver(sources, pieces), _ => { });
    }

    /// <summary>The crescents arrived: make the command the first time, re-target it after that.</summary>
    private void Deliver(IReadOnlyList<BevelSource> sources, IReadOnlyList<BevelPieces> pieces)
    {
        if (!ReferenceEquals(sources, _sources)) return;   // the selection changed while it was being worked out
        _busy = true;
        try
        {
            if (_command is not null && Session.History.IsTop(_command))
            {
                _command.Retarget(pieces);
                Session.History.NotifyTopEdited(_command);
            }
            else
            {
                var plan = BevelEffect.Plan(sources, pieces);
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
        if (_runner is { IsBusy: true } runner) await runner.Settled;
        Session.SetTool("select");
    }

    /// <summary>Esc: takes back what this session of the tool did and returns to the Layout tool.</summary>
    public void Abandon()
    {
        _runner?.Cancel();
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
        _runner?.Cancel();
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

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        if (key == EditorKey.Enter) { _ = Done(); return true; }
        if (key == EditorKey.Escape) { Abandon(); return true; }
        return false;
    }
}
